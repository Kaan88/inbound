using System.Diagnostics;
using System.Text;
using DownloadManager.Archives;
using DownloadManager.Core.Archives;

namespace DownloadManager.Archives.Tests;

/// <summary>Shared base: runs the real extractor against archives in a throwaway folder.</summary>
public abstract class ExtractionTestBase : IDisposable
{
    protected Workspace Space { get; } = new();

    protected static SevenZipExtractor Extractor(SevenZipExtractorOptions? options = null) =>
        new((options ?? new SevenZipExtractorOptions()) with { ToolPath = RarTools.SevenZipPath });

    protected ExtractionRequest Request(string firstVolume, string? password = null, ExtractionConflictPolicy policy = ExtractionConflictPolicy.Rename, bool preserve = true) => new()
    {
        FirstVolume = firstVolume,
        Destination = Space.Destination,
        StagingRoot = Space.Staging,
        Password = password,
        ConflictPolicy = policy,
        PreserveFolders = preserve,
    };

    protected async Task<ExtractionFailureKind> FailureOf(ExtractionRequest request, SevenZipExtractorOptions? options = null)
    {
        var failure = await Assert.ThrowsAsync<ExtractionException>(() => Extractor(options).ExtractAsync(request, null, CancellationToken.None));
        if (!string.IsNullOrEmpty(request.Password)) Assert.DoesNotContain(request.Password, failure.Message);
        Space.AssertDestinationUntouched();
        Space.AssertNoStagingLeft();
        return failure.Kind;
    }

    public virtual void Dispose()
    {
        Space.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>Real archives from Rar.exe.</summary>
public sealed class Rar5ExtractionTests : ExtractionTestBase
{
    [RequiresRarFact]
    public async Task PlainArchiveExtracts()
    {
        Space.WriteStandardSource();
        var archive = await Space.BuildAsync("plain.rar");

        var result = await Extractor().ExtractAsync(Request(archive), null, CancellationToken.None);

        Space.AssertSameAsSource();
        Space.AssertNoStagingLeft();
        Assert.Equal(3, result.Files.Count);
        Assert.All(result.Files, file => Assert.Equal(ExtractedFileOutcome.Written, file.Outcome));
    }

    [RequiresRarFact]
    public async Task ProbeDescribesAPlainArchive()
    {
        Space.WriteStandardSource();
        var archive = await Space.BuildAsync("plain.rar");

        var probe = await Extractor().ProbeAsync(archive, null, CancellationToken.None);

        Assert.False(probe.IsEncrypted);
        Assert.False(probe.HeadersEncrypted);
        Assert.False(probe.IsVolume);
        Assert.True(probe.AllVolumesPresent);
        Assert.Equal("Rar5", probe.Version);
        Assert.False(probe.ListingHasUnknownLines);
        Assert.Contains(probe.Entries, entry => entry.Path == Workspace.Native(@"sub\big.bin") && entry.Size == 600 * 1024 && !entry.IsLink);
        Assert.Contains(probe.Entries, entry => entry.Path == "sub" && entry.Kind == ArchiveEntryKind.Directory);
    }

    [RequiresRarFact]
    public async Task ListingHasNoUnknownLinesWithEveryExtraFieldRarCanWrite()
    {
        Space.WriteStandardSource();
        var archive = await Space.BuildAsync("rich.rar", "-rr3p", "-ts", "-htb", "-os", "-ow", "-s");

        var probe = await Extractor().ProbeAsync(archive, null, CancellationToken.None);

        Assert.False(probe.ListingHasUnknownLines);
        await Extractor().ExtractAsync(Request(archive), null, CancellationToken.None);
        Space.AssertSameAsSource();
    }

    private async Task<string> CommentedAsync(string comment)
    {
        Space.WriteSource("a.txt", "a");
        var archive = await Space.BuildAsync("commented.rar");
        var commentFile = Path.Combine(Space.Root, "comment.txt");
        File.WriteAllText(commentFile, comment);
        await RarTools.RunRarAsync(Space.Archives, "c", "-idq", "-z" + commentFile, archive);
        return archive;
    }

    /// <summary>
    /// 7-Zip prints a comment verbatim inside the archive section of <c>l -slt</c> ("Comment = ", "{", the text, "}"),
    /// so a comment can be written in its own layout: archive keys, a warnings block, the dashes and entry blocks.
    /// </summary>
    [RequiresRarFact]
    public async Task ArchiveCommentCannotImitateArchiveKeysOrWarnings()
    {
        var archive = await CommentedAsync("hello\r\nType = gzip\r\nVolumes = 9\r\nMultivolume = +\r\nWARNINGS:\r\nMissing volume : desktop.ini\r\n");

        var probe = await Extractor().ProbeAsync(archive, null, CancellationToken.None);

        Assert.Equal("Rar5", probe.Version);
        Assert.False(probe.IsVolume);
        Assert.Empty(probe.MissingVolumes);
        Assert.True(probe.AllVolumesPresent);
        Assert.False(probe.ListingHasUnknownLines);
        Assert.Equal("a.txt", Assert.Single(probe.Entries).Path);
        await Extractor().ExtractAsync(Request(archive), null, CancellationToken.None);
        Assert.Equal("a", File.ReadAllText(Space.Output("a.txt")));
    }

    [RequiresRarFact]
    public async Task ArchiveCommentCannotImitateEntries()
    {
        // The 7-Zip layout: the dashes that start the entry section, then an entry block, under a type that would
        // switch the entry checks off. The real dashes and the closing brace then fall among the entries.
        var archive = await CommentedAsync("hello\r\nType = gzip\r\n\r\n----------\r\nPath = ..\\fake.txt\r\nSize = 1\r\nFolder = -\r\n");

        var probe = await Extractor().ProbeAsync(archive, null, CancellationToken.None);

        Assert.Equal("Rar5", probe.Version);
        Assert.True(probe.ListingHasUnknownLines);
        Assert.Equal(ExtractionFailureKind.UnsafePath, await FailureOf(Request(archive)));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(Space.Destination)!, "fake.txt")));
    }

    [RequiresRarFact]
    public async Task ProgressRisesAndCarriesNoSecret()
    {
        Space.WriteStandardSource();
        var archive = await Space.BuildAsync("pw.rar", "-p" + RarTools.Password);
        var reports = new List<ExtractionProgress>();

        await Extractor().ExtractAsync(Request(archive, RarTools.Password), new ImmediateProgress<ExtractionProgress>(report => { lock (reports) reports.Add(report); }), CancellationToken.None);

        Assert.Contains(reports, report => report.Phase == ExtractionPhase.Probing);
        Assert.Contains(reports, report => report.Phase == ExtractionPhase.Verifying);
        Assert.Contains(reports, report => report.Phase == ExtractionPhase.Finalizing && report.FilesFinalized == 3 && report.FilesTotal == 3);
        var percents = reports.Where(report => report is { Phase: ExtractionPhase.Extracting, Percent: not null }).Select(report => report.Percent!.Value).ToList();
        Assert.Equal(percents.Order(), percents);
        Assert.True(percents[^1] >= 99);
        Assert.All(reports, report => Assert.DoesNotContain(RarTools.Password, report.ToString()));
    }

    [RequiresRarFact]
    public async Task PasswordArchiveExtractsAndIsNotOnTheCommandLine()
    {
        Space.WriteStandardSource();
        var archive = await Space.BuildAsync("pw.rar", "-p" + RarTools.Password);
        var commandLines = new List<string>();
        var options = new SevenZipExtractorOptions { ToolStarted = process => commandLines.Add(string.Join(' ', process.StartInfo.ArgumentList)) };

        await Extractor(options).ExtractAsync(Request(archive, RarTools.Password), null, CancellationToken.None);

        Space.AssertSameAsSource();
        Assert.NotEmpty(commandLines);
        Assert.All(commandLines, line => Assert.DoesNotContain(RarTools.Password, line));
        Assert.Contains(commandLines, line => line.StartsWith("x ", StringComparison.Ordinal));
        Assert.DoesNotContain(commandLines, line => line.Split(' ').Any(argument => argument.StartsWith("-p", StringComparison.Ordinal)));
    }

    [RequiresRarFact]
    public async Task ProbeSaysFileEncryptionWithNamesReadable()
    {
        Space.WriteStandardSource();
        var archive = await Space.BuildAsync("pw.rar", "-p" + RarTools.Password);

        var probe = await Extractor().ProbeAsync(archive, null, CancellationToken.None);

        Assert.True(probe.IsEncrypted);
        Assert.False(probe.HeadersEncrypted);
        Assert.True(probe.AllVolumesPresent);
        Assert.Contains(probe.Entries, entry => entry.Path == "readme.txt" && entry.IsEncrypted);
    }

    [RequiresRarFact]
    public async Task MissingPasswordIsPasswordRequired()
    {
        Space.WriteStandardSource();
        var archive = await Space.BuildAsync("pw.rar", "-p" + RarTools.Password);
        Assert.Equal(ExtractionFailureKind.PasswordRequired, await FailureOf(Request(archive)));
        Assert.Equal(ExtractionFailureKind.PasswordRequired, await FailureOf(Request(archive, string.Empty)));
    }

    [RequiresRarFact]
    public async Task WrongPasswordIsWrongPassword()
    {
        Space.WriteStandardSource();
        var archive = await Space.BuildAsync("pw.rar", "-p" + RarTools.Password);
        Assert.Equal(ExtractionFailureKind.WrongPassword, await FailureOf(Request(archive, "not the password")));
    }

    [RequiresRarFact]
    public async Task HeaderPasswordExtracts()
    {
        Space.WriteStandardSource();
        var archive = await Space.BuildAsync("hp.rar", "-hp" + RarTools.Password);

        await Extractor().ExtractAsync(Request(archive, RarTools.Password), null, CancellationToken.None);

        Space.AssertSameAsSource();
    }

    [RequiresRarFact]
    public async Task HeaderPasswordMissingIsPasswordRequiredNotAMissingVolume()
    {
        Space.WriteStandardSource();
        var archive = await Space.BuildAsync("hp.rar", "-hp" + RarTools.Password);

        Assert.Equal(ExtractionFailureKind.PasswordRequired, await FailureOf(Request(archive)));

        var probe = await Extractor().ProbeAsync(archive, null, CancellationToken.None);
        Assert.True(probe.HeadersEncrypted);
        Assert.True(probe.IsEncrypted);
        Assert.Empty(probe.Entries);
        Assert.Null(probe.AllVolumesPresent);
    }

    [RequiresRarFact]
    public async Task HeaderPasswordWrongIsWrongPasswordNotAMissingVolume()
    {
        Space.WriteStandardSource();
        var archive = await Space.BuildAsync("hp.rar", "-hp" + RarTools.Password);

        Assert.Equal(ExtractionFailureKind.WrongPassword, await FailureOf(Request(archive, "not the password")));

        var failure = await Assert.ThrowsAsync<ExtractionException>(() => Extractor().ProbeAsync(archive, "not the password", CancellationToken.None));
        Assert.Equal(ExtractionFailureKind.WrongPassword, failure.Kind);
    }

    [RequiresRarFact]
    public async Task ProbeWithHeaderPasswordListsEntries()
    {
        Space.WriteStandardSource();
        var archive = await Space.BuildAsync("hp.rar", "-hp" + RarTools.Password);

        var probe = await Extractor().ProbeAsync(archive, RarTools.Password, CancellationToken.None);

        Assert.True(probe.HeadersEncrypted);
        Assert.True(probe.AllVolumesPresent);
        Assert.Contains(probe.Entries, entry => entry.Path == "readme.txt");
    }

    [RequiresRarFact]
    public async Task MixedArchiveNeedsThePassword()
    {
        Space.WriteSource("plain.txt", "plain");
        var archive = await Space.BuildAsync("mixed.rar");
        File.Delete(Path.Combine(Space.Source, "plain.txt"));
        Space.WriteSource("secret.txt", "secret");
        await Space.BuildAsync("mixed.rar", "-p" + RarTools.Password);
        Space.WriteSource("plain.txt", "plain");

        Assert.Equal(ExtractionFailureKind.PasswordRequired, await FailureOf(Request(archive)));
        await Extractor().ExtractAsync(Request(archive, RarTools.Password), null, CancellationToken.None);
        Space.AssertSameAsSource();
    }

    [RequiresRarFact]
    public async Task AWrongPasswordIsTriedOnlyOnce()
    {
        Space.WriteStandardSource();
        var archive = await Space.BuildAsync("pw.rar", "-p" + RarTools.Password);
        var extractions = 0;
        var options = new SevenZipExtractorOptions
        {
            ToolStarted = process => { if (process.StartInfo.ArgumentList[0] == "x") Interlocked.Increment(ref extractions); },
        };

        await FailureOf(Request(archive, "not the password"), options);

        Assert.Equal(1, extractions);
    }

    [RequiresRarFact]
    public async Task PlainArchiveIgnoresAGivenPassword()
    {
        Space.WriteStandardSource();
        var archive = await Space.BuildAsync("plain.rar");

        await Extractor().ExtractAsync(Request(archive, "unneeded"), null, CancellationToken.None);

        Space.AssertSameAsSource();
    }

    [RequiresRarFact]
    public async Task AnAccentedPasswordWorksOverStdin()
    {
        Space.WriteStandardSource();
        const string Password = "pä55ß and space ";
        var archive = await Space.BuildAsync("cp.rar", "-p" + Password);
        var commandLines = new List<string>();
        var options = new SevenZipExtractorOptions { ToolStarted = process => commandLines.Add(string.Join(' ', process.StartInfo.ArgumentList)) };

        await Extractor(options).ExtractAsync(Request(archive, Password), null, CancellationToken.None);

        Space.AssertSameAsSource();

        Assert.All(commandLines, line => Assert.DoesNotContain(Password, line));
    }

    [RequiresRarFact]
    public async Task APasswordInAnyScriptWorksOverStdin()
    {
        Space.WriteStandardSource();
        const string Password = "日本語パスワード";
        var archive = await Space.BuildAsync("jp.rar", "-p" + Password);

        await Extractor().ExtractAsync(Request(archive, Password), null, CancellationToken.None);

        Space.AssertSameAsSource();
    }

    [RequiresRarFact]
    public async Task PasswordOutsideTheCodePageWrongStillSaysWrong()
    {
        Space.WriteStandardSource();
        var archive = await Space.BuildAsync("jp.rar", "-p日本語パスワード");
        Assert.Equal(ExtractionFailureKind.WrongPassword, await FailureOf(Request(archive, "違うパスワード")));
    }

    [RequiresRarTheory]
    [InlineData(127, "")]
    [InlineData(200, "")]
    public async Task ALongPasswordOrOneWithQuotesWorks(int length, string tail)
    {
        Space.WriteStandardSource();
        var password = new string('k', length) + tail;
        var archive = await Space.BuildAsync("long.rar", "-p" + password);

        await Extractor().ExtractAsync(Request(archive, password), null, CancellationToken.None);

        Space.AssertSameAsSource();
    }

    [RequiresRarFact]
    public async Task MultiVolumeSetExtracts()
    {
        Space.WriteStandardSource();
        var first = await Space.BuildAsync("vol.rar", "-v200k");
        first = Space.Archive("vol.part1.rar");

        var probe = await Extractor().ProbeAsync(first, null, CancellationToken.None);
        Assert.True(probe.IsVolume);
        Assert.Equal(1, probe.VolumeNumber);
        Assert.True(probe.AllVolumesPresent);
        Assert.Single(probe.Entries, entry => entry.Path == Workspace.Native(@"sub\big.bin"));

        await Extractor().ExtractAsync(Request(first), null, CancellationToken.None);
        Space.AssertSameAsSource();
    }

    [RequiresRarFact]
    public async Task EncryptedMultiVolumeSetsExtract()
    {
        Space.WriteStandardSource();
        await Space.BuildAsync("pwvol.rar", "-v200k", "-p" + RarTools.Password);

        await Extractor().ExtractAsync(Request(Space.Archive("pwvol.part1.rar"), RarTools.Password), null, CancellationToken.None);

        Space.AssertSameAsSource();
    }

    [RequiresRarFact]
    public async Task HeaderEncryptedMultiVolumeSetExtracts()
    {
        Space.WriteStandardSource();
        await Space.BuildAsync("hpvol.rar", "-v200k", "-hp" + RarTools.Password);

        await Extractor().ExtractAsync(Request(Space.Archive("hpvol.part1.rar"), RarTools.Password), null, CancellationToken.None);

        Space.AssertSameAsSource();
    }

    [RequiresRarFact]
    public async Task SetRenamedToOldStyleExtracts()
    {
        Space.WriteStandardSource();
        await Space.BuildAsync("vol.rar", "-v200k");
        var parts = Directory.GetFiles(Space.Archives, "vol.part*.rar").Order().ToList();
        Assert.True(parts.Count >= 3);
        for (var index = 0; index < parts.Count; index++)
        {
            File.Move(parts[index], Space.Archive(index == 0 ? "old.rar" : $"old.r{index - 1:00}"));
        }

        var probe = await Extractor().ProbeAsync(Space.Archive("old.rar"), null, CancellationToken.None);
        Assert.True(probe.AllVolumesPresent);

        await Extractor().ExtractAsync(Request(Space.Archive("old.rar")), null, CancellationToken.None);
        Space.AssertSameAsSource();
    }

    [RequiresRarFact]
    public async Task SetRenamedToThreeDigitExtracts()
    {
        Space.WriteStandardSource();
        await Space.BuildAsync("vol.rar", "-v200k");
        var parts = Directory.GetFiles(Space.Archives, "vol.part*.rar").Order().ToList();
        for (var index = 0; index < parts.Count; index++)
        {
            File.Move(parts[index], Space.Archive($"num.{index + 1:000}.rar"));
        }

        await Extractor().ExtractAsync(Request(Space.Archive("num.001.rar")), null, CancellationToken.None);

        Space.AssertSameAsSource();
    }

    [RequiresRarFact]
    public async Task ZoneIdIsKeptAndTheSourceUrlIsNot()
    {
        Space.WriteSource("a.txt", "a");
        var archive = await Space.BuildAsync("marked.rar");
        File.WriteAllText(archive + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=https://example.test/a.rar\r\n");

        await Extractor().ExtractAsync(Request(archive), null, CancellationToken.None);

        var mark = File.ReadAllText(Space.Output("a.txt") + ":Zone.Identifier");
        Assert.Contains("ZoneId=3", mark);
        Assert.DoesNotContain("example.test", mark);
    }

    [RequiresRarFact]
    public async Task MissingMiddleVolumeIsNamed()
    {
        Space.WriteStandardSource();
        await Space.BuildAsync("vol.rar", "-v200k");
        File.Delete(Space.Archive("vol.part2.rar"));

        var failure = await Assert.ThrowsAsync<ExtractionException>(() => Extractor().ExtractAsync(Request(Space.Archive("vol.part1.rar")), null, CancellationToken.None));

        Assert.Equal(ExtractionFailureKind.MissingVolume, failure.Kind);
        Assert.Contains("vol.part2.rar", failure.MissingVolumes);
        Space.AssertDestinationUntouched();
    }

    [RequiresRarFact]
    public async Task MissingLastVolumeIsFoundByTheToolAndNotTheNames()
    {
        Space.WriteStandardSource();
        await Space.BuildAsync("vol.rar", "-v200k");
        var last = Directory.GetFiles(Space.Archives, "vol.part*.rar").Order().Last();
        File.Delete(last);

        var probe = await Extractor().ProbeAsync(Space.Archive("vol.part1.rar"), null, CancellationToken.None);

        Assert.False(probe.AllVolumesPresent);
        Assert.Contains(Path.GetFileName(last), probe.MissingVolumes);
        Assert.Equal(ExtractionFailureKind.MissingVolume, await FailureOf(Request(Space.Archive("vol.part1.rar"))));
    }

    [RequiresRarFact]
    public async Task HeaderEncryptedSetWithMissingVolumeIsReportedOnceThePasswordIsRight()
    {
        Space.WriteStandardSource();
        await Space.BuildAsync("hpvol.rar", "-v200k", "-hp" + RarTools.Password);
        var last = Directory.GetFiles(Space.Archives, "hpvol.part*.rar").Order().Last();
        File.Delete(last);
        var first = Space.Archive("hpvol.part1.rar");

        Assert.Equal(ExtractionFailureKind.PasswordRequired, await FailureOf(Request(first)));
        Assert.Equal(ExtractionFailureKind.WrongPassword, await FailureOf(Request(first, "not the password")));
        Assert.Equal(ExtractionFailureKind.MissingVolume, await FailureOf(Request(first, RarTools.Password)));
    }

    [RequiresRarFact]
    public async Task StartingFromALaterVolumeIsRefused()
    {
        Space.WriteStandardSource();
        await Space.BuildAsync("vol.rar", "-v200k");
        Assert.Equal(ExtractionFailureKind.MissingVolume, await FailureOf(Request(Space.Archive("vol.part2.rar"))));
        File.Delete(Space.Archive("vol.part1.rar"));
        Assert.Equal(ExtractionFailureKind.MissingVolume, await FailureOf(Request(Space.Archive("vol.part2.rar"))));
    }

    [RequiresRarFact]
    public async Task SingleArchiveNamedLikeAPartIsNotAVolume()
    {
        Space.WriteStandardSource();
        var archive = await Space.BuildAsync("x.part5.rar");

        var probe = await Extractor().ProbeAsync(archive, null, CancellationToken.None);

        Assert.False(probe.IsVolume);
        Assert.True(probe.AllVolumesPresent);
        await Extractor().ExtractAsync(Request(archive), null, CancellationToken.None);
        Space.AssertSameAsSource();
    }

    [RequiresRarFact]
    public async Task DamagedDataIsCorrupted()
    {
        Space.WriteStandardSource();
        var archive = await Space.BuildAsync("damaged.rar");
        var bytes = File.ReadAllBytes(archive);
        for (var offset = bytes.Length / 2; offset < (bytes.Length / 2) + 64; offset++) bytes[offset] ^= 0xFF;
        File.WriteAllBytes(archive, bytes);

        Assert.Equal(ExtractionFailureKind.Corrupted, await FailureOf(Request(archive)));
    }

    [RequiresRarFact]
    public async Task DamagedEncryptedRar5IsCorruptedNotWrongPassword()
    {
        Space.WriteStandardSource();
        var archive = await Space.BuildAsync("damaged.rar", "-p" + RarTools.Password);
        var bytes = File.ReadAllBytes(archive);
        for (var offset = bytes.Length / 2; offset < (bytes.Length / 2) + 64; offset++) bytes[offset] ^= 0xFF;
        File.WriteAllBytes(archive, bytes);

        Assert.Equal(ExtractionFailureKind.Corrupted, await FailureOf(Request(archive, RarTools.Password)));
    }

    [RequiresRarFact]
    public async Task TruncatedArchiveIsCorrupted()
    {
        Space.WriteStandardSource();
        var archive = await Space.BuildAsync("cut.rar");
        var bytes = File.ReadAllBytes(archive);
        File.WriteAllBytes(archive, bytes[..(bytes.Length / 2)]);

        Assert.Equal(ExtractionFailureKind.Corrupted, await FailureOf(Request(archive)));
    }

    [RequiresRarFact]
    public async Task FailureLeavesEarlierOutputAlone()
    {
        Space.WriteStandardSource();
        var archive = await Space.BuildAsync("pw.rar", "-p" + RarTools.Password);
        Directory.CreateDirectory(Space.Destination);
        File.WriteAllText(Space.Output("readme.txt"), "earlier output");

        var failure = await Assert.ThrowsAsync<ExtractionException>(() => Extractor().ExtractAsync(Request(archive, "not the password", ExtractionConflictPolicy.Overwrite), null, CancellationToken.None));

        Assert.Equal(ExtractionFailureKind.WrongPassword, failure.Kind);
        Assert.Equal("earlier output", File.ReadAllText(Space.Output("readme.txt")));
        Assert.Single(Directory.EnumerateFileSystemEntries(Space.Destination));
        Space.AssertNoStagingLeft();
    }

    [RequiresRarFact]
    public async Task ConflictOverwrite()
    {
        Space.WriteStandardSource();
        var archive = await Space.BuildAsync("a.rar");
        Directory.CreateDirectory(Space.Destination);
        File.WriteAllText(Space.Output("readme.txt"), "old");

        var result = await Extractor().ExtractAsync(Request(archive, policy: ExtractionConflictPolicy.Overwrite), null, CancellationToken.None);

        Space.AssertSameAsSource();
        Assert.Contains(result.Files, file => file.EntryPath == "readme.txt" && file.Outcome == ExtractedFileOutcome.Overwrote);
    }

    [RequiresRarFact]
    public async Task ConflictRename()
    {
        Space.WriteStandardSource();
        var archive = await Space.BuildAsync("a.rar");
        Directory.CreateDirectory(Space.Destination);
        File.WriteAllText(Space.Output("readme.txt"), "old");

        var result = await Extractor().ExtractAsync(Request(archive, policy: ExtractionConflictPolicy.Rename), null, CancellationToken.None);

        Assert.Equal("old", File.ReadAllText(Space.Output("readme.txt")));
        Assert.Equal("hello from the archive\n", File.ReadAllText(Space.Output("readme (1).txt")));
        Assert.Contains(result.Files, file => file.EntryPath == "readme.txt" && file.Outcome == ExtractedFileOutcome.Renamed);
    }

    [RequiresRarFact]
    public async Task ConflictSkip()
    {
        Space.WriteStandardSource();
        var archive = await Space.BuildAsync("a.rar");
        Directory.CreateDirectory(Space.Destination);
        File.WriteAllText(Space.Output("readme.txt"), "old");

        var result = await Extractor().ExtractAsync(Request(archive, policy: ExtractionConflictPolicy.Skip), null, CancellationToken.None);

        Assert.Equal("old", File.ReadAllText(Space.Output("readme.txt")));
        Assert.Contains(result.Files, file => file.EntryPath == "readme.txt" && file.Outcome == ExtractedFileOutcome.Skipped);
        Assert.True(File.Exists(Space.Output(Workspace.Native(@"sub\deep\note.txt"))));
    }

    [RequiresRarFact]
    public async Task FlattensWhenFoldersAreNotPreserved()
    {
        Space.WriteStandardSource();
        var archive = await Space.BuildAsync("a.rar");

        await Extractor().ExtractAsync(Request(archive, preserve: false), null, CancellationToken.None);

        Assert.Equal(["big.bin", "note.txt", "readme.txt"], Directory.GetFiles(Space.Destination).Select(file => Path.GetFileName(file)).Order());
        Assert.Empty(Directory.GetDirectories(Space.Destination));
    }

    [RequiresRarFact]
    public async Task ReadOnlyFileDoesNotBlockCleanup()
    {
        var file = Space.WriteSource("locked.txt", "ro");
        File.SetAttributes(file, FileAttributes.ReadOnly);
        var archive = await Space.BuildAsync("ro.rar");
        var extractor = Extractor(new SevenZipExtractorOptions { AvailableBytes = _ => 0 });

        await Assert.ThrowsAsync<ExtractionException>(() => extractor.ExtractAsync(Request(archive), null, CancellationToken.None));
        await Extractor().ExtractAsync(Request(archive), null, CancellationToken.None);

        Assert.True(File.Exists(Space.Output("locked.txt")));
        Space.AssertNoStagingLeft();
    }

    [RequiresRarFact]
    public async Task NotEnoughFreeSpaceIsRefusedBeforeWriting()
    {
        Space.WriteStandardSource();
        var archive = await Space.BuildAsync("a.rar");
        var options = new SevenZipExtractorOptions { AvailableBytes = _ => 1000 };
        Assert.Equal(ExtractionFailureKind.DiskSpace, await FailureOf(Request(archive), options));
    }

    [RequiresRarFact]
    public async Task TotalSizeCapIsEnforced()
    {
        Space.WriteStandardSource();
        var archive = await Space.BuildAsync("a.rar");
        var options = new SevenZipExtractorOptions { Limits = new ExtractionLimits { MaxTotalBytes = 1000 } };
        Assert.Equal(ExtractionFailureKind.DiskSpace, await FailureOf(Request(archive), options));
    }

    [RequiresRarFact]
    public async Task EntryCapIsEnforced()
    {
        Space.WriteStandardSource();
        var archive = await Space.BuildAsync("a.rar");
        var options = new SevenZipExtractorOptions { Limits = new ExtractionLimits { MaxEntries = 2 } };
        Assert.Equal(ExtractionFailureKind.UnsafePath, await FailureOf(Request(archive), options));
    }

    [RequiresRarFact]
    public async Task ArchiveWithALinkIsRefusedWhenBuiltByRar()
    {
        Space.WriteSource("a.txt", "a");
        var target = Directory.CreateDirectory(Path.Combine(Space.Root, "linked")).FullName;
        StagingWalkTests.MakeJunction(Path.Combine(Space.Source, "junction"), target);
        var archive = await Space.BuildAsync("linked.rar", "-ol");

        Assert.Equal(ExtractionFailureKind.UnsafePath, await FailureOf(Request(archive)));
    }

    [RequiresSevenZipFact]
    public async Task NotAnArchiveIsCorrupted()
    {
        var archive = Space.WriteArchive("fake.rar", Encoding.ASCII.GetBytes("this is no archive"));

        Assert.Equal(ExtractionFailureKind.Corrupted, await FailureOf(Request(archive)));
    }

    [RequiresSevenZipFact]
    public async Task EmptyFileIsCorrupted()
    {
        var archive = Space.WriteArchive("empty.rar", []);
        Assert.Equal(ExtractionFailureKind.Corrupted, await FailureOf(Request(archive)));
    }

    [RequiresSevenZipFact]
    public async Task ArchiveThatIsNotThereIsMissingVolume()
    {
        Assert.Equal(ExtractionFailureKind.MissingVolume, await FailureOf(Request(Space.Archive("nope.rar"))));
    }

    [RequiresSevenZipFact]
    public async Task AlreadyCancelledNeverStartsTheTool()
    {
        var archive = Space.WriteArchive("a.rar", RarBuilder.Rar5(("a.txt", [1])));
        var starts = 0;
        var extractor = Extractor(new SevenZipExtractorOptions { ToolStarted = _ => Interlocked.Increment(ref starts) });

        var failure = await Assert.ThrowsAsync<ExtractionException>(() => extractor.ExtractAsync(Request(archive), null, new CancellationToken(canceled: true)));

        Assert.Equal(ExtractionFailureKind.Cancelled, failure.Kind);
        Assert.Equal(0, starts);
    }
}

/// <summary>
/// An archive slow enough to catch 7-Zip mid-write, built once for the tests that interrupt it. 7-Zip reports progress
/// about every 200 ms, and a stored file would be out in a fraction of that, so this one is compressed text.
/// </summary>
public sealed class BigArchiveFixture : IDisposable
{
    public BigArchiveFixture()
    {
        Space = new Workspace();
        if (RarTools.RarMissing is not null) return;
        Space.WriteSource("big.bin", System.Text.Encoding.ASCII.GetBytes(Convert.ToBase64String(Workspace.RandomBytes(192 * 1024 * 1024, 7))));
        Space.WriteSource("small.txt", "small");
        Archive = Space.BuildAsync("big.rar", "-m3").GetAwaiter().GetResult();
    }

    public Workspace Space { get; }

    public string Archive { get; } = string.Empty;

    public void Dispose() => Space.Dispose();
}

public sealed class InterruptedExtractionTests(BigArchiveFixture fixture) : ExtractionTestBase, IClassFixture<BigArchiveFixture>
{
    [RequiresRarFact]
    public async Task CancellingMidWayKillsTheToolAndWipesStaging()
    {
        using var cancellation = new CancellationTokenSource();
        var toolId = 0;
        var extractor = Extractor(new SevenZipExtractorOptions { ToolStarted = process => toolId = process.Id });
        var cancelledAt = -1;
        var progress = new ImmediateProgress<ExtractionProgress>(report =>
        {
            if (report is { Phase: ExtractionPhase.Extracting, Percent: > 0 } && cancelledAt < 0)
            {
                cancelledAt = report.Percent.Value;
                cancellation.Cancel();
            }
        });

        var failure = await Assert.ThrowsAsync<ExtractionException>(() => extractor.ExtractAsync(Request(fixture.Archive), progress, cancellation.Token));

        Assert.Equal(ExtractionFailureKind.Cancelled, failure.Kind);
        Assert.InRange(cancelledAt, 1, 99);
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(toolId));
        Space.AssertDestinationUntouched();
        Space.AssertNoStagingLeft();
    }

    [RequiresRarFact]
    public async Task ToolKilledMidWayIsAFailureAndARetrySucceeds()
    {
        var killed = 0;
        Process? tool = null;
        var options = new SevenZipExtractorOptions { ToolStarted = process => tool = process };
        var progress = new ImmediateProgress<ExtractionProgress>(report =>
        {
            if (report is { Phase: ExtractionPhase.Extracting, Percent: > 0 } && Interlocked.Exchange(ref killed, 1) == 0)
            {
                tool!.Kill();
            }
        });

        var failure = await Assert.ThrowsAsync<ExtractionException>(() => Extractor(options).ExtractAsync(Request(fixture.Archive), progress, CancellationToken.None));

        Assert.Equal(ExtractionFailureKind.Unknown, failure.Kind);
        Space.AssertDestinationUntouched();
        Space.AssertNoStagingLeft();

        await Extractor().ExtractAsync(Request(fixture.Archive), null, CancellationToken.None);
        Assert.Equal(new FileInfo(Path.Combine(fixture.Space.Source, "big.bin")).Length, new FileInfo(Space.Output("big.bin")).Length);
    }

    /// <summary>
    /// What a Host crash looks like afterwards: the tool died with no one to clean up. The notes
    /// found a full-size file with a zero tail here, which is why size proves nothing.
    /// </summary>
    [RequiresRarFact]
    public async Task CrashLeavesOnlyDetectableStagingAndTheNextAttemptStartsClean()
    {
        var staging = await StagingArea.PrepareAsync(Space.Staging, fixture.Archive, Space.Destination, CancellationToken.None);
        var info = new ProcessStartInfo(RarTools.SevenZipPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "x", "-y", "-aoa", "-bsp1", "-o" + staging.PayloadFolder, fixture.Archive }) info.ArgumentList.Add(argument);
        using (var process = Process.Start(info)!)
        {
            process.StandardInput.Close();
            var seen = new char[64];
            await process.StandardOutput.ReadAsync(seen);
            for (var wait = 0; wait < 200 && !Directory.EnumerateFiles(staging.PayloadFolder).Any(); wait++) await Task.Delay(10);
            process.Kill();
            await process.WaitForExitAsync();
        }

        var leftover = Assert.Single(StagingArea.FindLeftovers(Space.Staging));
        Assert.Equal(staging.Folder, leftover);
        Assert.NotEmpty(Directory.EnumerateFiles(staging.PayloadFolder));
        Space.AssertDestinationUntouched();

        await Extractor().ExtractAsync(Request(fixture.Archive), null, CancellationToken.None);

        Space.AssertNoStagingLeft();
        Assert.Equal("small", File.ReadAllText(Space.Output("small.txt")));
        Assert.Equal(256 * 1024 * 1024, new FileInfo(Space.Output("big.bin")).Length);
    }
}

/// <summary>Hand-built archives, needing only 7-Zip.</summary>
public sealed class HandBuiltExtractionTests : ExtractionTestBase
{
    private static readonly byte[] Text = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("hello a\n", 100)));

    [RequiresSevenZipFact]
    public async Task StoredRar4Extracts()
    {
        var archive = Space.WriteArchive("r4.rar", RarBuilder.Rar4(("a.txt", Text), (@"sub\b.txt", Text)));

        var probe = await Extractor().ProbeAsync(archive, null, CancellationToken.None);
        Assert.False(probe.IsEncrypted);
        Assert.Equal("Rar", probe.Version);

        await Extractor().ExtractAsync(Request(archive), null, CancellationToken.None);

        Assert.Equal(Text, File.ReadAllBytes(Space.Output("a.txt")));
        Assert.Equal(Text, File.ReadAllBytes(Space.Output(Workspace.Native(@"sub\b.txt"))));
    }

    [RequiresSevenZipFact]
    public async Task Rar4OldStyleVolumesExtract()
    {
        var data = Workspace.RandomBytes(300 * 1024, 3);
        var volumes = RarBuilder.Rar4Volumes("big4.bin", data, 3, newNumbering: false);
        var first = Space.WriteArchive("v.rar", volumes[0]);
        Space.WriteArchive("v.r00", volumes[1]);
        Space.WriteArchive("v.r01", volumes[2]);

        var probe = await Extractor().ProbeAsync(first, null, CancellationToken.None);
        Assert.True(probe.IsVolume);
        Assert.Null(probe.VolumeNumber);
        Assert.True(probe.AllVolumesPresent);

        await Extractor().ExtractAsync(Request(first), null, CancellationToken.None);
        Assert.Equal(data, File.ReadAllBytes(Space.Output("big4.bin")));
    }

    [RequiresSevenZipFact]
    public async Task Rar4PartVolumesExtract()
    {
        var data = Workspace.RandomBytes(300 * 1024, 4);
        var volumes = RarBuilder.Rar4Volumes("big4.bin", data, 3, newNumbering: true);
        var first = Space.WriteArchive("v.part1.rar", volumes[0]);
        Space.WriteArchive("v.part2.rar", volumes[1]);
        Space.WriteArchive("v.part3.rar", volumes[2]);

        await Extractor().ExtractAsync(Request(first), null, CancellationToken.None);

        Assert.Equal(data, File.ReadAllBytes(Space.Output("big4.bin")));
    }

    [RequiresSevenZipFact]
    public async Task Rar4MissingOldStyleVolumeIsNamed()
    {
        var volumes = RarBuilder.Rar4Volumes("big4.bin", Workspace.RandomBytes(300 * 1024, 5), 3, newNumbering: false);
        var first = Space.WriteArchive("v.rar", volumes[0]);
        Space.WriteArchive("v.r01", volumes[2]);

        var failure = await Assert.ThrowsAsync<ExtractionException>(() => Extractor().ExtractAsync(Request(first), null, CancellationToken.None));

        Assert.Equal(ExtractionFailureKind.MissingVolume, failure.Kind);
        Assert.Contains("v.r00", failure.MissingVolumes);
    }

    [RequiresSevenZipFact]
    public async Task Rar4LaterVolumeIsRefusedThoughItHasNoNumber()
    {
        var volumes = RarBuilder.Rar4Volumes("big4.bin", Workspace.RandomBytes(300 * 1024, 6), 3, newNumbering: true);
        Space.WriteArchive("v.part1.rar", volumes[0]);
        var second = Space.WriteArchive("v.part2.rar", volumes[1]);
        Space.WriteArchive("v.part3.rar", volumes[2]);

        Assert.Equal(ExtractionFailureKind.MissingVolume, await FailureOf(Request(second)));
    }

    [RequiresSevenZipFact]
    public async Task Rar4BadChecksumIsCorrupted()
    {
        var archive = Space.WriteArchive("crc.rar", RarBuilder.Rar4WithBadCrc("a.txt", Text));
        Assert.Equal(ExtractionFailureKind.Corrupted, await FailureOf(Request(archive)));
    }

    [RequiresSevenZipFact]
    public async Task Rar4PasswordRightWrongAndMissing()
    {
        var archive = Space.WriteArchive("pw.rar", RarBuilder.Rar4Encrypted("a.txt", Text, RarTools.Password));

        Assert.Equal(ExtractionFailureKind.PasswordRequired, await FailureOf(Request(archive)));
        Assert.Equal(ExtractionFailureKind.WrongPassword, await FailureOf(Request(archive, "not the password")));

        await Extractor().ExtractAsync(Request(archive, RarTools.Password), null, CancellationToken.None);
        Assert.Equal(Text, File.ReadAllBytes(Space.Output("a.txt")));
    }

    [RequiresSevenZipFact]
    public async Task Rar4HeaderPasswordRightWrongAndMissing()
    {
        var archive = Space.WriteArchive("hp.rar", RarBuilder.Rar4HeaderEncrypted("a.txt", Text, RarTools.Password));

        Assert.Equal(ExtractionFailureKind.PasswordRequired, await FailureOf(Request(archive)));
        Assert.Equal(ExtractionFailureKind.WrongPassword, await FailureOf(Request(archive, "not the password")));

        var probe = await Extractor().ProbeAsync(archive, null, CancellationToken.None);
        Assert.True(probe.HeadersEncrypted);

        await Extractor().ExtractAsync(Request(archive, RarTools.Password), null, CancellationToken.None);
        Assert.Equal(Text, File.ReadAllBytes(Space.Output("a.txt")));
    }

    [RequiresSevenZipTheory]
    [InlineData("../up.txt")]
    [InlineData("../../up2.txt")]
    [InlineData("sub/../../../up3.txt")]
    [InlineData("/rooted.txt")]
    [InlineData("C:/drive-relative.txt")]
    [InlineData("CON.txt")]
    [InlineData("trail. ")]
    public async Task HostileRar5NameIsRefusedBeforeAnythingIsWritten(string name)
    {
        var archive = Space.WriteArchive("hostile.rar", RarBuilder.Rar5(("ok.txt", Text), (name, Text)));
        var starts = new List<string>();
        var options = new SevenZipExtractorOptions { ToolStarted = process => starts.Add(process.StartInfo.ArgumentList[0]) };

        Assert.Equal(ExtractionFailureKind.UnsafePath, await FailureOf(Request(archive), options));

        Assert.DoesNotContain("x", starts);
        Assert.Empty(Directory.EnumerateFiles(Space.Root, "up*.txt", SearchOption.AllDirectories));
    }

    [RequiresSevenZipFact]
    public async Task HostileRar4TraversalIsRefused()
    {
        var archive = Space.WriteArchive("hostile.rar", RarBuilder.Rar4((@"..\..\evil.txt", Text), ("ok.txt", Text)));

        Assert.Equal(ExtractionFailureKind.UnsafePath, await FailureOf(Request(archive)));
        Assert.Empty(Directory.EnumerateFiles(Space.Root, "evil.txt", SearchOption.AllDirectories));
    }

    [RequiresSevenZipFact]
    public async Task NameWithALineBreakCannotHideFromTheChecks()
    {
        var spoof = "a\r\n        Name: spoof.txt\r\n        Type: File\r\n.txt";
        var archive = Space.WriteArchive("spoof.rar", RarBuilder.Rar5((spoof, Text), ("ok.txt", Text)));

        Assert.Equal(ExtractionFailureKind.UnsafePath, await FailureOf(Request(archive)));
    }

    [RequiresSevenZipFact]
    public async Task NameWithATabIsRefused()
    {
        var archive = Space.WriteArchive("tab.rar", RarBuilder.Rar5(("tab\there.txt", Text)));

        Assert.Equal(ExtractionFailureKind.UnsafePath, await FailureOf(Request(archive)));
    }

    [RequiresSevenZipFact]
    public async Task ANameWithAColonIsRefused()
    {
        var archive = Space.WriteArchive("ads.rar", RarBuilder.Rar5(("a.txt:evil", Text)));

        Assert.Equal(ExtractionFailureKind.UnsafePath, await FailureOf(Request(archive)));
        Space.AssertDestinationUntouched();
    }

    [RequiresSevenZipTheory]
    [InlineData(2, @"..\..\..\outside")]
    [InlineData(2, "ok.txt")]
    [InlineData(3, @"\??\C:\Windows")]
    [InlineData(4, @"..\..\outside.txt")]
    [InlineData(4, "ok.txt")]
    public async Task LinksAreRefused(int kind, string target)
    {
        var archive = Space.WriteArchive("link.rar", RarBuilder.Rar5Raw(
            RarBuilder.Rar5File("ok.txt", Text),
            RarBuilder.Rar5Link("link", kind, target)));

        var probe = await Extractor().ProbeAsync(archive, null, CancellationToken.None);
        Assert.True(probe.Entries.Single(entry => entry.Path == "link").IsLink);

        Assert.Equal(ExtractionFailureKind.UnsafePath, await FailureOf(Request(archive)));
    }

    [RequiresSevenZipFact]
    public async Task RightToLeftNameExtractsButIsNeutralisedInMessages()
    {
        var plain = Space.WriteArchive("rtl.rar", RarBuilder.Rar5(("cod\u202Etxt.exe", Text)));
        await Extractor().ExtractAsync(Request(plain), null, CancellationToken.None);
        Assert.True(File.Exists(Space.Output("cod\u202Etxt.exe")));

        var hostile = Space.WriteArchive("rtl2.rar", RarBuilder.Rar5(("../cod\u202Etxt.exe", Text)));
        var failure = await Assert.ThrowsAsync<ExtractionException>(() => Extractor().ExtractAsync(Request(hostile), null, CancellationToken.None));

        Assert.Equal(ExtractionFailureKind.UnsafePath, failure.Kind);
        Assert.Contains("cod_txt.exe", failure.Message);
        Assert.False(failure.Message.Contains('\u202E'));
    }

    [RequiresSevenZipFact]
    public async Task UnknownCompressionIsUnsupportedVersion()
    {
        const ulong FutureAlgorithm = 62 | (3 << 7);
        var archive = Space.WriteArchive("future.rar", RarBuilder.Rar5Raw(RarBuilder.Rar5File("a.txt", Text, compressionInfo: FutureAlgorithm)));

        Assert.Equal(ExtractionFailureKind.UnsupportedVersion, await FailureOf(Request(archive)));
    }
}
