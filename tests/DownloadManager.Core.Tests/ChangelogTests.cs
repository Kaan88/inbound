using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DownloadManager.Contracts;
using DownloadManager.Desktop.WinUI;

namespace DownloadManager.Core.Tests;

/// <summary>UPD.N1: the changelog file, the per-app filtering, the update manifest's notes and when What's new opens.</summary>
public sealed class ChangelogTests
{
    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DownloadManager.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }

    private static readonly string ChangelogPath = Path.Combine("src", "DownloadManager.Contracts", "Changelog", "changelog.json");

    /// <summary>The tags changelog.json may use (docs\release-builds.md); a provider's id is the provider tag.</summary>
    public static readonly string[] AllowedTags = ["all", "windows", "webui", "server", "docker", "linux", "extension", "torrents", "torbox"];

    [Fact]
    public void EveryAllowedTagIsAPlaceTorrentsOrAProvider()
    {
        Assert.All(AllowedTags, tag => Assert.True(ChangelogTags.IsKnown(tag)));
        Assert.True(ChangelogTags.IsProvider("torbox"));
        Assert.False(ChangelogTags.IsProvider("windows"));
        Assert.False(ChangelogTags.IsProvider("torrents"));
        Assert.False(ChangelogTags.IsKnown("Bad Tag"));
    }

    [Fact]
    public void TheChangelogFileParsesNewestFirstWithKnownTagsOnly()
    {
        var versions = Changelog.Parse(File.ReadAllText(Path.Combine(RepoRoot(), ChangelogPath)));
        Assert.NotEmpty(versions);
        for (var i = 1; i < versions.Count; i++)
            Assert.True(Changelog.Compare(versions[i - 1].Version, versions[i].Version) > 0,
                $"{versions[i - 1].Version} must come before {versions[i].Version}, newest first, each once.");
        foreach (var version in versions)
        {
            Assert.True(version.Date is null || Changelog.DisplayDate(version.Date) is not null, $"{version.Version}: the date is yyyy-MM-dd.");
            Assert.NotEmpty(version.Sections);
            foreach (var section in version.Sections)
            {
                Assert.False(string.IsNullOrWhiteSpace(section.Title));
                Assert.NotEmpty(section.Items);
                foreach (var item in section.Items)
                {
                    Assert.False(string.IsNullOrWhiteSpace(item.Text));
                    Assert.NotEmpty(item.Tags);
                    Assert.All(item.Tags, tag => Assert.True(AllowedTags.Contains(tag), $"Unknown tag '{tag}' in {version.Version}: {item.Text}"));
                    Assert.True(item.Text.Length <= UpdateNotes.MaxTextLength, $"Too long for the update manifest: {item.Text}");
                    Assert.DoesNotContain('<', item.Text);
                }
            }

            // A released version's lines go into its update manifest, which carries at most MaxLines.
            Assert.Null(UpdateNotes.Problem(UpdateNotes.From(version)));
        }
    }

    [Fact]
    public void TheBuildCarriesTheSameChangelog()
    {
        var file = Changelog.Parse(File.ReadAllText(Path.Combine(RepoRoot(), ChangelogPath)));
        Assert.Equal(file.Select(version => version.Version), Changelog.Embedded.Select(version => version.Version));
        Assert.NotNull(Changelog.Find(Changelog.Embedded, Protocol.HostVersion.Split('+')[0]));
    }

    private static ChangelogVersion Sample() => new()
    {
        Version = "9.1.0",
        Date = "2026-12-01",
        Sections =
        [
            new ChangelogSection
            {
                Title = "New",
                Items =
                [
                    new ChangelogItem { Text = "Everywhere", Tags = ["all"] },
                    new ChangelogItem { Text = "Window only", Tags = ["windows"] },
                    new ChangelogItem { Text = "Web only", Tags = ["webui"] },
                    new ChangelogItem { Text = "Docker only", Tags = ["docker"] },
                    new ChangelogItem { Text = "Extension only", Tags = ["extension"] },
                    new ChangelogItem { Text = "Linux only", Tags = ["linux"] },
                    new ChangelogItem { Text = "Window torrents", Tags = ["windows", "torrents"] },
                    new ChangelogItem { Text = "Window TorBox", Tags = ["windows", "torbox"] },
                    new ChangelogItem { Text = "No place tag", Tags = ["torrents"] },
                ],
            },
            new ChangelogSection { Title = "Fixes", Items = [new ChangelogItem { Text = "Extension fix", Tags = ["extension"] }] },
        ],
    };

    [Fact]
    public void TheWindowsAppShowsItsOwnLinesAndFoldsTheRest()
    {
        var view = ChangelogFilter.For([Sample()], ChangelogSurface.Windows, new ChangelogFeatures(false, []));
        var shown = Assert.Single(view.Versions);
        var section = Assert.Single(shown.Sections);
        Assert.Equal("New", section.Title);
        Assert.Equal(["Everywhere", "Window only"], section.Lines);
        Assert.Equal(["Web UI · Web only", "Server · Docker only", "Extension · Extension only", "Extension · Extension fix"],
            view.Elsewhere.Select(line => $"{line.Place} · {line.Text}"));
        Assert.Equal("4 changes in the web UI, the server and the browser extension", view.ElsewhereText);
    }

    [Fact]
    public void TorrentAndTorBoxLinesShowOnlyWhenTurnedOn()
    {
        var view = ChangelogFilter.For([Sample()], ChangelogSurface.Windows, new ChangelogFeatures(true, ["torbox"]));
        Assert.Equal(["Everywhere", "Window only", "Window torrents", "Window TorBox", "No place tag"], view.Versions[0].Sections[0].Lines);
    }

    [Fact]
    public void TheWebUiShowsWebServerAndDockerLines()
    {
        var view = ChangelogFilter.For([Sample()], ChangelogSurface.WebUi, new ChangelogFeatures(false, []));
        Assert.Equal(["Everywhere", "Web only", "Docker only"], view.Versions[0].Sections[0].Lines);
        Assert.DoesNotContain(view.Elsewhere, line => line.Text.Contains("Linux"));
        Assert.Contains(view.Elsewhere, line => line.Place == "Windows" && line.Text == "Window only");
    }

    [Fact]
    public void NoLineForThisAppMeansNothingOpens()
    {
        var only = new ChangelogVersion
        {
            Version = "9.2.0",
            Sections = [new ChangelogSection { Title = "New", Items = [new ChangelogItem { Text = "x", Tags = ["extension"] }, new ChangelogItem { Text = "y", Tags = ["linux"] }] }],
        };
        Assert.True(ChangelogFilter.For([only], ChangelogSurface.Windows, new ChangelogFeatures(true, ["torbox"])).IsEmpty);
    }

    [Fact]
    public void BetweenJoinsSkippedVersionsNewestFirst()
    {
        var versions = new[] { "0.8.0", "0.7.3", "0.7.2", "0.7.1" }.Select(v => new ChangelogVersion { Version = v }).ToList();
        Assert.Equal(["0.7.3", "0.7.2"], Changelog.Between(versions, "0.7.1", "0.7.3").Select(v => v.Version));
        Assert.Equal(["0.7.2"], Changelog.Between(versions, "0.7.1", "0.7.2.0").Select(v => v.Version));
        Assert.Empty(Changelog.Between(versions, "0.7.3", "0.7.3"));
    }

    [Theory]
    [InlineData(null, null, true, false, null, null)]                // a development build: never, nothing kept
    [InlineData("not a version", null, true, false, null, null)]
    [InlineData("0.7.2", null, false, false, null, "0.7.2")]          // a fresh install: nothing shown, the version kept
    [InlineData("0.7.2", null, true, true, "0.7.1", "0.7.2")]         // from 0.7.1, which kept no marker
    [InlineData("0.7.4", null, true, true, "0.7.1", "0.7.4")]
    [InlineData("0.7.4", "0.7.2", true, true, "0.7.2", "0.7.4")]      // skipped versions are joined
    [InlineData("0.7.4", "0.7.2", false, true, "0.7.2", "0.7.4")]
    [InlineData("0.7.2", "0.7.2", true, false, null, null)]           // seen already
    [InlineData("0.7.2", "0.7.3", true, false, null, null)]           // a rollback keeps the newer mark
    [InlineData("0.7.2", "garbage", true, true, "0.7.1", "0.7.2")]
    public void WhatsNewOpensOnceAfterAnUpgradeOnly(string? installed, string? lastSeen, bool hadState, bool open, string? after, string? record)
    {
        var decision = WhatsNewRules.Decide(installed, lastSeen, hadState);
        Assert.Equal(open, decision.Open);
        Assert.Equal(after, decision.After);
        Assert.Equal(record, decision.Record);
    }

    [Fact]
    public void TheSubtitleSaysWhereItCameFromOnlyWhenKnown()
    {
        Assert.Equal("Updated from 0.7.1 · 12 Nov 2026", WhatsNewRules.Subtitle("0.7.1", true, "2026-11-12"));
        // No mark was kept (an upgrade from 0.7.1 or older): the approved wording, honest about the guess.
        Assert.Equal("Updated from 0.7.1 or earlier · 12 Nov 2026", WhatsNewRules.Subtitle("0.7.1", false, "2026-11-12"));
        Assert.Equal("Released 12 Nov 2026", WhatsNewRules.Subtitle(null, false, "2026-11-12"));
        Assert.Equal("Updated from 0.7.1", WhatsNewRules.Subtitle("0.7.1", true, null));
        Assert.Equal(string.Empty, WhatsNewRules.Subtitle(null, false, null));
    }

    [Fact]
    public void TheUpdateQuestionListsThisAppsLinesAndCountsTheOthers()
    {
        var notes = UpdateNotes.From(Sample());
        var words = WhatsNewRules.UpdateNotes(notes, "9.1.0", new ChangelogFeatures(false, []));
        Assert.Equal("What 9.1.0 changes in this app", words.Header);
        Assert.Equal(["Everywhere", "Window only"], words.Lines);
        Assert.Equal("Also 4 changes in the web UI, the server and the browser extension.", words.Also);

        var none = WhatsNewRules.UpdateNotes(null, "9.1.0", new ChangelogFeatures(true, ["torbox"]));
        Assert.Null(none.Header);
        Assert.Empty(none.Lines);

        // The Linux window (same code) keeps the question as it was: these are the Windows app's lines, not its own.
        var linux = WhatsNewRules.UpdateNotes(notes, "9.1.0", new ChangelogFeatures(false, []), windowsApp: false);
        Assert.Null(linux.Header);
        Assert.Empty(linux.Lines);
        Assert.Null(linux.Also);
    }

    private static (UpdateManifest Manifest, string PublicKey) SignedManifest(string notesJson)
    {
        using var rsa = RSA.Create(2048);
        var manifest = new UpdateManifest
        {
            Version = "0.7.2",
            Channel = "stable",
            InstallerUrl = "https://example.com/download-manager-0.7.2.zip",
            Size = 1234,
            Sha256 = new string('b', 64),
        };
        // Signed as UpdateSigner signs: the five fields only, as every client from before the notes checks them.
        var canonical = $"{manifest.Version}\n{manifest.Channel}\n{manifest.InstallerUrl}\n{manifest.Size}\n{manifest.Sha256}";
        Assert.Equal(canonical, UpdateManifestVerifier.Canonical(manifest));
        manifest.Signature = Convert.ToBase64String(rsa.SignData(Encoding.UTF8.GetBytes(canonical), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        var json = JsonSerializer.Serialize(manifest, IpcJson.Options);
        json = json[..^1] + $",\"notes\":{notesJson}}}";
        var parsed = UpdateManifestVerifier.Parse(json)!;
        // The notes' own signature, as UpdateSigner writes it (review of 0.7.2, security-2).
        if (UpdateManifestVerifier.NotesCanonical(canonical, parsed.Notes) is { } notesText)
            parsed.NotesSignature = Convert.ToBase64String(rsa.SignData(Encoding.UTF8.GetBytes(notesText), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        return (parsed, rsa.ExportSubjectPublicKeyInfoPem());
    }

    /// <summary>
    /// Review of 0.7.2, security-2: someone who can change the manifest but hasn't the key can't put words in the update
    /// question. Notes show only when their own signature verifies; the manifest itself still verifies either way.
    /// </summary>
    [Fact]
    public void NotesShowOnlyWhenTheirOwnSignatureVerifies()
    {
        var (manifest, key) = SignedManifest("[{\"text\":\"Faster lists.\",\"tags\":[\"windows\"]}]");
        Assert.Equal("Faster lists.", Assert.Single(UpdateManifestVerifier.VerifiedNotes(manifest, key)!).Text);

        var signature = manifest.NotesSignature;
        manifest.NotesSignature = null;
        Assert.True(UpdateManifestVerifier.Verify(manifest, key));
        Assert.Null(UpdateManifestVerifier.VerifiedNotes(manifest, key));

        manifest.NotesSignature = signature;
        manifest.Notes = JsonDocument.Parse("[{\"text\":\"Install the fix from https://evil.example/fix.exe first.\",\"tags\":[\"windows\"]}]").RootElement;
        Assert.True(UpdateManifestVerifier.Verify(manifest, key));
        Assert.Null(UpdateManifestVerifier.VerifiedNotes(manifest, key));

        // A tag added, or the notes moved to another version's manifest, breaks it too.
        manifest.Notes = JsonDocument.Parse("[{\"text\":\"Faster lists.\",\"tags\":[\"windows\",\"all\"]}]").RootElement;
        Assert.Null(UpdateManifestVerifier.VerifiedNotes(manifest, key));
        manifest.Notes = JsonDocument.Parse("[{\"text\":\"Faster lists.\",\"tags\":[\"windows\"]}]").RootElement;
        Assert.NotNull(UpdateManifestVerifier.VerifiedNotes(manifest, key));
        manifest.Version = "0.7.3";
        Assert.Null(UpdateManifestVerifier.VerifiedNotes(manifest, key));

        using var other = RSA.Create(2048);
        manifest.Version = "0.7.2";
        Assert.Null(UpdateManifestVerifier.VerifiedNotes(manifest, other.ExportSubjectPublicKeyInfoPem()));
    }

    [Fact]
    public void FormatCharactersAreDroppedFromNotes()
    {
        var (manifest, _) = SignedManifest("[{\"text\":\"Safe\\u202Eexe.txt\\u200B done\\uFEFF\",\"tags\":[\"all\"]}]");
        Assert.Equal("Safeexe.txt done", Assert.Single(UpdateNotes.Read(manifest.Notes)!).Text);
    }

    [Fact]
    public void AManifestWithNotesStillVerifiesWithTheOldSignedText()
    {
        var (manifest, key) = SignedManifest("[{\"text\":\"Faster lists.\",\"tags\":[\"windows\"]}]");
        Assert.True(UpdateManifestVerifier.Verify(manifest, key));
        var notes = UpdateNotes.Read(manifest.Notes);
        Assert.Equal("Faster lists.", Assert.Single(notes!).Text);

        // A client from before the notes reads the same manifest into its old shape, which has no notes, and verifies it.
        var old = JsonSerializer.Deserialize<OldManifest>(JsonSerializer.Serialize(manifest, IpcJson.Options), IpcJson.Options)!;
        var oldCanonical = $"{old.Version}\n{old.Channel}\n{old.InstallerUrl}\n{old.Size}\n{old.Sha256.ToLowerInvariant()}";
        using var rsa = RSA.Create();
        rsa.ImportFromPem(key);
        Assert.True(rsa.VerifyData(Encoding.UTF8.GetBytes(oldCanonical), Convert.FromBase64String(old.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));

        // Changing the notes doesn't touch the manifest's signature, which older clients check; only the notes' own
        // signature fails, so the notes are no longer shown.
        manifest.Notes = JsonDocument.Parse("[{\"text\":\"Other words.\",\"tags\":[\"all\"]}]").RootElement;
        Assert.True(UpdateManifestVerifier.Verify(manifest, key));
        Assert.Null(UpdateManifestVerifier.VerifiedNotes(manifest, key));
    }

    private sealed class OldManifest
    {
        public string Version { get; set; } = string.Empty;
        public string Channel { get; set; } = string.Empty;
        public string InstallerUrl { get; set; } = string.Empty;
        public long Size { get; set; }
        public string Sha256 { get; set; } = string.Empty;
        public string Signature { get; set; } = string.Empty;
    }

    [Theory]
    [InlineData("\"not a list\"")]
    [InlineData("{\"text\":\"an object\"}")]
    [InlineData("42")]
    [InlineData("null")]
    [InlineData("[1, \"x\", null, {\"text\": 5}, {\"tags\":[\"all\"]}, {\"text\":\"   \"}]")]
    public void BadNotesAreIgnoredAndTheManifestStillVerifies(string notesJson)
    {
        var (manifest, key) = SignedManifest(notesJson);
        Assert.True(UpdateManifestVerifier.Verify(manifest, key));
        Assert.Null(UpdateNotes.Read(manifest.Notes));
    }

    [Fact]
    public void OversizedNotesAreCutAndShownAsPlainText()
    {
        var lines = Enumerable.Range(0, 60).Select(i => $"{{\"text\":\"Line {i}\",\"tags\":[\"windows\",\"Evil <b>\",\"windows\"]}}");
        var tooLong = $"{{\"text\":\"{new string('x', UpdateNotes.MaxTextLength + 1)}\",\"tags\":[\"all\"]}}";
        var markup = "{\"text\":\"<b>bold</b>\\n[link](https://example.com)\\u0007\",\"tags\":[\"all\"]}";
        var (manifest, key) = SignedManifest($"[{tooLong},{markup},{string.Join(',', lines)}]");
        Assert.True(UpdateManifestVerifier.Verify(manifest, key));
        var notes = UpdateNotes.Read(manifest.Notes)!;
        Assert.Equal(UpdateNotes.MaxLines, notes.Count);
        Assert.DoesNotContain(notes, line => line.Text.Length > UpdateNotes.MaxTextLength);
        // Kept as the very text it is, with its control characters made spaces; the dialog shows it in a plain TextBlock.
        Assert.Equal("<b>bold</b> [link](https://example.com)", notes[0].Text);
        Assert.Equal(["windows"], notes[1].Tags);
    }

    [Fact]
    public void ALineWhoseTagsAreAllUnknownIsDroppedNotShownEverywhere()
    {
        var (manifest, _) = SignedManifest("[{\"text\":\"Web only\",\"tags\":[\"WebUI\"]},{\"text\":\"Untagged\"},{\"text\":\"Empty tags\",\"tags\":[]},{\"text\":\"Mixed\",\"tags\":[\"Bad\",\"webui\"]}]");
        var notes = UpdateNotes.Read(manifest.Notes)!;
        Assert.Equal(["Untagged", "Empty tags", "Mixed"], notes.Select(line => line.Text));
        Assert.Equal(["webui"], notes[2].Tags);
        Assert.DoesNotContain("Web only", WhatsNewRules.UpdateNotes(notes, "9.1.0", new ChangelogFeatures(false, [])).Lines);
    }

    [Fact]
    public void TheUpdateCheckResultCarriesNotesOnlyWhenThereAreSome()
    {
        var json = JsonSerializer.Serialize(new UpdateCheckResult { Ok = true, Status = "upToDate" }, IpcJson.Options);
        Assert.DoesNotContain("notes", json);
    }
}
