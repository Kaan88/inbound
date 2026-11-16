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

    // ---- SITES.11 R2a ----

    [Theory]
    [InlineData("big.bin.63642737.partial", true)]
    [InlineData("c1.bin.b1dc66bb.partial", true)]
    [InlineData("report.old.partial", false)]
    [InlineData("x.6364273.partial", false)]
    [InlineData("x.636427377.partial", false)]
    [InlineData("x.6364273G.partial", false)]
    [InlineData("x.63642737.partial.txt", false)]
    [InlineData(".63642737.partial", true)]
    [InlineData("a.partial", false)]
    public void OnlyARunningTransfersPartialFileIsHidden(string name, bool hidden)
        => Assert.Equal(hidden, LocalListing.IsTransferPartial(name));

    [Fact]
    public void ReadHidesAPartialFileButKeepsAFolderWithThatName()
    {
        File.WriteAllText(Path.Combine(_root, "keep.iso"), "x");
        File.WriteAllText(Path.Combine(_root, "keep.iso.63642737.partial"), "x");
        File.WriteAllText(Path.Combine(_root, "mine.old.partial"), "x");
        Directory.CreateDirectory(Path.Combine(_root, "dir.63642737.partial"));
        var (entries, problem) = LocalListing.Read(_root);
        Assert.Null(problem);
        Assert.Equal(["dir.63642737.partial", "keep.iso", "mine.old.partial"], entries.Select(entry => entry.Name));
    }

    [Fact]
    public void ColumnsSortWithFoldersFirst()
    {
        var folder = new LocalEntry("zz", true, -1, null);
        var small = new LocalEntry("b", false, 1, new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero));
        var big = new LocalEntry("a", false, 9, new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
        List<string> Order(string key, bool ascending)
        {
            var list = new List<LocalEntry> { big, small, folder };
            list.Sort((x, y) => LocalListing.Compare(x, y, key, ascending));
            return list.Select(entry => entry.Name).ToList();
        }

        Assert.Equal(["zz", "b", "a"], Order(SitesBrowseRules.SortSize, true));
        Assert.Equal(["zz", "a", "b"], Order(SitesBrowseRules.SortSize, false));
        Assert.Equal(["zz", "a", "b"], Order(SitesBrowseRules.SortModified, true));
        Assert.Equal(["zz", "a", "b"], Order(SitesBrowseRules.SortName, true));
    }

    [Fact]
    public void CrumbsSplitAPathIntoItsFolders()
    {
        if (!OperatingSystem.IsWindows()) return;
        var crumbs = LocalListing.Crumbs(@"D:\Downloads\ISOs");
        Assert.Equal(["D:", "Downloads", "ISOs"], crumbs.Select(crumb => crumb.Name));
        Assert.Equal([@"D:\", @"D:\Downloads", @"D:\Downloads\ISOs"], crumbs.Select(crumb => crumb.Path));
        Assert.Equal(["D:"], LocalListing.Crumbs(@"D:\").Select(crumb => crumb.Name));
        Assert.Equal([@"\\nas\share", "x"], LocalListing.Crumbs(@"\\nas\share\x").Select(crumb => crumb.Name));
    }

    [Fact]
    public void StarTogglesAFolderAndKeepsTwentyAtMost()
    {
        if (!OperatingSystem.IsWindows()) return;
        var stars = LocalListing.ToggleStar([], @"D:\One");
        Assert.Equal([@"D:\One"], stars);
        Assert.True(LocalListing.IsStarred(stars, @"d:\one\"));
        stars = LocalListing.ToggleStar(stars, @"D:\Two");
        Assert.Equal([@"D:\Two", @"D:\One"], stars);
        Assert.Equal([@"D:\Two"], LocalListing.ToggleStar(stars, @"d:\ONE"));
        var many = Enumerable.Range(0, 20).Select(i => $@"D:\F{i}").ToList();
        var added = LocalListing.ToggleStar(many, @"D:\New");
        Assert.Equal(20, added.Count);
        Assert.Equal(@"D:\New", added[0]);
        Assert.DoesNotContain(@"D:\F19", added);
    }
}
