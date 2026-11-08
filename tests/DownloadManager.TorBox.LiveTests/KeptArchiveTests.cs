using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using DownloadManager.Core.Providers;

namespace DownloadManager.TorBox.LiveTests;

/// <summary>
/// The archive kept on the account for live extraction checks (owner, 2026-11-08: "yes add one and airlock it").
/// The tests without a key hold the rule that no live test removes it. This covers live test cleanup only: the
/// app's own "delete from provider" and the CLI have no such check.
/// </summary>
public sealed class KeptArchiveTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("inbound-live-check-archive.zip", true)]
    [InlineData("Inbound-Live-Check-anything", true)]
    [InlineData("  inbound-live-check", true)]
    [InlineData("inbound-live-check.zip", true)]
    [InlineData("inbound-live-check_2", true)]
    [InlineData("inbound-live-checkpoint-x", false)]
    [InlineData("inbound-live-check2", false)]
    [InlineData("14838-h.zip", false)]
    [InlineData("an inbound-live-check copy", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ANameWithThePrefixIsKept(string? name, bool kept) => Assert.Equal(kept, LiveAccount.IsKept(name));

    [Fact]
    public void TheKeptArchiveHasThePrefix() => Assert.True(LiveAccount.IsKept(LiveAccount.KeptArchiveName));

    /// <summary>A removal written straight against the client would skip the check, so only the helper may call it.</summary>
    [Fact]
    public void NoLiveTestRemovesAnItemExceptThroughTheKeptCheck()
    {
        var folder = Path.GetDirectoryName(SourcePath())!;
        var files = Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories)
            .Where(file => !InBuildOutput(folder, file) && Path.GetFileName(file) != "KeptArchiveTests.cs")
            .ToList();
        Assert.Contains(files, file => Path.GetFileName(file) == "LiveAccount.cs");

        var offenders = files.SelectMany(file => RemovalOffences(Path.GetFileName(file), File.ReadAllText(file))).ToList();

        Assert.Empty(offenders);
    }

    /// <summary>What the guard above catches, shown on sample text so a weaker scan fails here.</summary>
    [Theory]
    [InlineData("Other.cs", "await client.RemoveAsync(item);")]
    [InlineData("Other.cs", "await client.RemoveAsync (item);")]
    [InlineData("Other.cs", "Func<RemoteItemRef, CancellationToken, Task> remove = client.RemoveAsync;")]
    [InlineData("Other.cs", "await remote.RemoveAsync(request, token);")]
    [InlineData("Other.cs", "await client.ControlQueuedAsync(id, RemoteQueueAction.Remove);")]
    [InlineData("Other.cs", "var action = RemoteQueueAction.Remove;")]
    [InlineData("Other.cs", "await LiveAccount.RawAsync(\"queued/controlqueued\");")]
    [InlineData("Other.cs", "await LiveAccount.RawAsync(\"torrents/controltorrent\");")]
    [InlineData("Other.cs", "var body = new { operation = \"Delete\" };")]
    [InlineData("LiveAccount.cs", "public static Task Wipe(TorBoxClient client, RemoteItemRef item) => client.RemoveAsync(item);")]
    public void TheGuardCatchesARemovalWrittenAnyOtherWay(string fileName, string text) =>
        Assert.NotEmpty(RemovalOffences(fileName, text));

    [Fact]
    public void TheGuardAllowsTheOneCallInsideTheHelper()
    {
        const string helper = """
            public static async Task<bool> RemoveUnlessKeptAsync(TorBoxClient client, RemoteItemRef created)
            {
                var item = await client.GetAsync(created, fresh: true).ConfigureAwait(false);
                if (item is null || IsKept(item.Name)) return false;
                await client.RemoveAsync(created).ConfigureAwait(false);
                return true;
            }
            """;
        Assert.Empty(RemovalOffences("LiveAccount.cs", helper));
        Assert.NotEmpty(RemovalOffences("LiveAccount.cs", helper + "\nstatic Task Other(TorBoxClient c, RemoteItemRef r) => c.RemoveAsync(r);"));
        Assert.NotEmpty(RemovalOffences("Other.cs", helper));
    }

    /// <summary>
    /// Any mention of <c>RemoveAsync</c> (a call with or without a space, a generic call or a method group), outside
    /// the body of <see cref="LiveAccount.RemoveUnlessKeptAsync"/> in LiveAccount.cs; any removal of a queued item;
    /// any raw control endpoint; any "delete" string.
    /// </summary>
    private static List<string> RemovalOffences(string fileName, string text)
    {
        var offences = new List<string>();
        var scanned = text;
        if (fileName == "LiveAccount.cs" && HelperBody(text) is { } body) scanned = text.Remove(body.Start, body.Length);

        var removals = Regex.Matches(scanned, @"\bRemoveAsync\b").Count;
        if (removals > 0) offences.Add($"{fileName}: {removals} RemoveAsync outside RemoveUnlessKeptAsync");
        if (Regex.IsMatch(text, @"\bRemoteQueueAction\s*\.\s*Remove\b")) offences.Add($"{fileName}: a queued item removed");
        if (Regex.IsMatch(text, "controltorrent|controlwebdownload|controlusenetdownload|controlqueued", RegexOptions.IgnoreCase))
        {
            offences.Add($"{fileName}: a raw control call");
        }

        if (Regex.IsMatch(text, "\"delete\"", RegexOptions.IgnoreCase)) offences.Add($"{fileName}: a raw delete");
        return offences;
    }

    /// <summary>The helper's braces, from its declaration to the brace that closes it.</summary>
    private static (int Start, int Length)? HelperBody(string text)
    {
        var declaration = Regex.Match(text, @"Task<bool>\s+RemoveUnlessKeptAsync\s*\(");
        if (!declaration.Success) return null;
        var open = text.IndexOf('{', declaration.Index);
        if (open < 0) return null;
        var depth = 0;
        for (var index = open; index < text.Length; index++)
        {
            if (text[index] == '{') depth++;
            else if (text[index] == '}' && --depth == 0) return (open, index - open + 1);
        }

        return null;
    }

    private static bool InBuildOutput(string root, string file)
    {
        var relative = Path.GetRelativePath(root, file).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return relative.Any(part => part is "bin" or "obj");
    }

    [LiveFact]
    [Trait("Category", LiveAccount.ReadTrait)]
    public async Task TheKeptArchiveIsOnTheAccountReady()
    {
        var kept = (await LiveAccount.WebDownloads).Where(item => LiveAccount.IsKept(item.Name)).ToList();
        output.WriteLine($"kept items: {kept.Count}");
        var archive = Assert.Single(kept, item => item.Name == LiveAccount.KeptArchiveName);
        Assert.Equal(RemoteStage.Ready, archive.Stage);
        var file = Assert.Single(archive.Files);
        Assert.EndsWith(".zip", file.Path, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(LiveAccount.KeptArchiveSize, file.Size);
        if (!string.IsNullOrEmpty(file.Md5)) Assert.Equal(LiveAccount.KeptArchiveMd5, file.Md5, ignoreCase: true);
        output.WriteLine($"provider md5 given: {!string.IsNullOrEmpty(file.Md5)}");
    }

    /// <summary>Downloads the 1.29 MB file through a fresh link and checks its content, not only its size.</summary>
    [LiveFact]
    [Trait("Category", LiveAccount.ReadTrait)]
    public async Task TheKeptArchiveDownloadsWithItsPublishedMd5()
    {
        var archive = Assert.Single(await LiveAccount.WebDownloads, item => item.Name == LiveAccount.KeptArchiveName);
        var file = Assert.Single(archive.Files);
        var link = (await LiveAccount.Client().ResolveLinkAsync(archive.Ref, file.FileId)).Url;

        byte[] bytes;
        try
        {
            bytes = await LiveAccount.Http.GetByteArrayAsync(link);
        }
        catch (HttpRequestException ex)
        {
            // The link holds the key: say only the status.
            throw new InvalidOperationException($"The kept archive's download failed: {ex.StatusCode?.ToString() ?? "no status"}.");
        }

        Assert.Equal(LiveAccount.KeptArchiveSize, bytes.LongLength);
        Assert.Equal(LiveAccount.KeptArchiveMd5, Convert.ToHexStringLower(MD5.HashData(bytes)));
    }

    private static string SourcePath([CallerFilePath] string path = "") => path;
}
