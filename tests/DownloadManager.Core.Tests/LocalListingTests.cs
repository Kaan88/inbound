using DownloadManager.Contracts;
using DownloadManager.Desktop.WinUI;

namespace DownloadManager.Core.Tests;

/// <summary>SITES.5: the This PC pane of the Two panes view, and the rules its arrows and Delete follow.</summary>
public sealed class LocalListingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "inbound-local-" + Guid.NewGuid().ToString("N"));

    public LocalListingTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public void FoldersComeFirstThenNamesWithoutCase()
    {
        Directory.CreateDirectory(Path.Combine(_root, "beta"));
        Directory.CreateDirectory(Path.Combine(_root, "Alpha"));
        File.WriteAllBytes(Path.Combine(_root, "b.txt"), new byte[3]);
        File.WriteAllBytes(Path.Combine(_root, "A.txt"), new byte[5]);

        var (entries, problem) = LocalListing.Read(_root);

        Assert.Null(problem);
        Assert.Equal(["Alpha", "beta", "A.txt", "b.txt"], entries.Select(e => e.Name));
        Assert.True(entries[0].IsFolder);
        Assert.Equal(-1, entries[0].Size);
        Assert.Equal(5, entries[2].Size);
    }

    [Fact]
    public void AMissingOrRelativeFolderSaysWhy()
    {
        Assert.NotNull(LocalListing.Read(Path.Combine(_root, "gone")).Problem);
        Assert.NotNull(LocalListing.Read("relative").Problem);
        Assert.NotNull(LocalListing.Read("").Problem);
    }

    [Fact]
    public void UpStopsAtTheDrive()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Equal(@"C:\Users", LocalListing.Parent(@"C:\Users\me"));
        Assert.Equal(@"C:\Users", LocalListing.Parent(@"C:\Users\me\"));
        Assert.Equal(@"C:\", LocalListing.Parent(@"C:\Users"));
        Assert.Null(LocalListing.Parent(@"C:\"));
        Assert.Equal("me", LocalListing.Leaf(@"C:\Users\me\"));
    }

    [Fact]
    public void TheLinkedFolderFollowsTheServerFolderInsideTheStartFolder()
    {
        Assert.Equal(_root, LocalListing.LinkedFolder(_root, "/pub", "/pub"));
        Assert.Equal(_root, LocalListing.LinkedFolder(_root, "/pub/", "/pub"));
        Assert.Equal(Path.Combine(_root, "docs", "deep"), LocalListing.LinkedFolder(_root, "/pub", "/pub/docs/deep"));
        Assert.Equal(Path.Combine(_root, "pub"), LocalListing.LinkedFolder(_root, "", "/pub"));
        Assert.Null(LocalListing.LinkedFolder(_root, "/pub", "/public"));
        Assert.Null(LocalListing.LinkedFolder(_root, "/pub", "/"));
        Assert.Null(LocalListing.LinkedFolder(null, "/pub", "/pub"));
        Assert.Null(LocalListing.LinkedFolder("relative", "/pub", "/pub"));
        Assert.Null(LocalListing.LinkedFolder(_root, "/pub", "/pub/a:b"));
        Assert.Null(LocalListing.LinkedFolder(_root, "/pub", "/pub/.."));
    }

    private static SiteTransferDto Batch(string state, string direction, string localFolder, int done = 1) => new()
    {
        Id = Guid.NewGuid().ToString("N"), SiteId = "s1", Direction = direction, RemoteFolder = "/pub", LocalFolder = localFolder,
        State = state, Files = 2, FilesDone = done,
    };

    [Fact]
    public void ADownloadThatLandsInTheShownFolderReadsItAgain()
    {
        var running = Batch("running", "download", @"C:\In");
        var done = new SiteTransferDto { Id = running.Id, SiteId = "s1", Direction = "download", LocalFolder = @"C:\In", State = "done", Files = 2, FilesDone = 2 };
        Assert.True(SiteTransferRules.DownloadLanded([running], [done], @"c:\in\"));
        Assert.False(SiteTransferRules.DownloadLanded([running], [done], @"C:\Other"));
        Assert.False(SiteTransferRules.DownloadLanded([done], [done], @"C:\In"));
        Assert.False(SiteTransferRules.DownloadLanded([running], [running], @"C:\In"));
        var upload = Batch("done", "upload", @"C:\In");
        Assert.False(SiteTransferRules.DownloadLanded([], [upload], @"C:\In"));
        Assert.True(SiteTransferRules.DownloadLandedFrom([running], [done], "s1"));
        Assert.False(SiteTransferRules.DownloadLandedFrom([running], [done], "s2"));
    }

    [Fact]
    public void ADeleteMayNotTakeAwayAFolderATransferUses()
    {
        var transfers = new[] { Batch("running", "download", @"C:\In\Music"), Batch("done", "download", @"C:\Old") };
        Assert.True(SiteTransferRules.LocalInUse(transfers, @"C:\In\Music"));
        Assert.True(SiteTransferRules.LocalInUse(transfers, @"C:\In"));
        Assert.False(SiteTransferRules.LocalInUse(transfers, @"C:\In\Music\song.mp3"));
        Assert.False(SiteTransferRules.LocalInUse(transfers, @"C:\In\Mus"));
        Assert.False(SiteTransferRules.LocalInUse(transfers, @"C:\Old"));
        Assert.True(SiteTransferRules.LocalInUse([Batch("paused", "upload", @"C:\Up")], @"C:\Up"));
    }
}
