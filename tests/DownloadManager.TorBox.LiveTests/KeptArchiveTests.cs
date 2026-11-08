using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using DownloadManager.Core.Providers;

namespace DownloadManager.TorBox.LiveTests;

/// <summary>
/// The archive kept on the account for live extraction checks (owner, 2026-11-08: "yes add one and airlock it").
/// The first three need no key: they hold the rule that no test removes it.
/// </summary>
public sealed class KeptArchiveTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("inbound-live-check-archive.zip", true)]
    [InlineData("Inbound-Live-Check-anything", true)]
    [InlineData("  inbound-live-check", true)]
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
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(folder, "*.cs"))
        {
            if (Path.GetFileName(file) == "KeptArchiveTests.cs") continue;
            var text = File.ReadAllText(file);
            var calls = Regex.Matches(text, @"\.RemoveAsync\(").Count;
            var allowed = Path.GetFileName(file) == "LiveAccount.cs" ? 1 : 0;
            if (calls > allowed) offenders.Add($"{Path.GetFileName(file)}: {calls}");
            if (Regex.IsMatch(text, "\"operation\"\\s*\\]?\\s*=\\s*\"delete\"|controltorrent|controlwebdownload|controlusenetdownload"))
            {
                offenders.Add($"{Path.GetFileName(file)}: a raw delete");
            }
        }

        Assert.Empty(offenders);
    }

    [LiveFact]
    [Trait("Category", LiveAccount.ReadTrait)]
    public async Task TheKeptArchiveIsOnTheAccountReadyAndUnchanged()
    {
        var kept = (await LiveAccount.WebDownloads).Where(item => LiveAccount.IsKept(item.Name)).ToList();
        output.WriteLine($"kept items: {kept.Count}");
        var archive = Assert.Single(kept, item => item.Name == LiveAccount.KeptArchiveName);
        Assert.Equal(RemoteStage.Ready, archive.Stage);
        var file = Assert.Single(archive.Files);
        Assert.EndsWith(".zip", file.Path, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(LiveAccount.KeptArchiveSize, file.Size);
    }

    private static string SourcePath([CallerFilePath] string path = "") => path;
}
