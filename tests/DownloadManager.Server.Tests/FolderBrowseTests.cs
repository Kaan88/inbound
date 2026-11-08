using DownloadManager.Contracts;
using DownloadManager.Runtime.Ipc;

namespace DownloadManager.Server.Tests;

public sealed class FolderBrowseTests : IDisposable
{
    private readonly string _work = ServerHarness.NewWork();

    private string Root => Path.Combine(_work, "downloads");
    private string Outside => Directory.CreateDirectory(Path.Combine(_work, "outside")).FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_work, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // left for the next clean
        }
    }

    [Fact]
    public void AnEmptyPathListsTheRootsAndARootListsItsFoldersOnlyWithItsFreeSpace()
    {
        Directory.CreateDirectory(Path.Combine(Root, "Movies"));
        Directory.CreateDirectory(Path.Combine(Root, "audio"));
        File.WriteAllText(Path.Combine(Root, "not-a-folder.txt"), "x");
        var browser = new FolderBrowser([Root]);

        var roots = browser.Browse(null);
        Assert.True(roots.Ok);
        Assert.Equal([Path.GetFullPath(Root)], roots.Roots);
        Assert.Null(roots.Path);

        var listing = browser.Browse(Root);
        Assert.True(listing.Ok, listing.Error);
        Assert.Equal(["audio", "Movies"], listing.Folders.Select(folder => folder.Name));
        Assert.Null(listing.Parent);
        Assert.NotNull(listing.FreeBytes);

        var inner = browser.Browse(Path.Combine(Root, "Movies"));
        Assert.Equal(Path.GetFullPath(Root), inner.Parent);
    }

    [Fact]
    public void ArchiveFilesAreListedOnlyWhenAskedForAndHiddenOnesAreLeftOut()
    {
        Directory.CreateDirectory(Path.Combine(Root, "Movies"));
        File.WriteAllText(Path.Combine(Root, "set.part1.rar"), "x");
        File.WriteAllText(Path.Combine(Root, "Backup.ZIP"), "xy");
        File.WriteAllText(Path.Combine(Root, "readme.txt"), "x");
        var hidden = Path.Combine(Root, OperatingSystem.IsWindows() ? "hidden.7z" : ".hidden.7z");
        File.WriteAllText(hidden, "x");
        if (OperatingSystem.IsWindows()) File.SetAttributes(hidden, FileAttributes.Hidden);
        var browser = new FolderBrowser([Root]);

        Assert.Empty(browser.Browse(Root).Files);
        var listing = browser.Browse(Root, FolderBrowseFiles.Archives);
        Assert.True(listing.Ok, listing.Error);
        Assert.Equal(["Backup.ZIP", "set.part1.rar"], listing.Files.Select(file => file.Name));
        Assert.Equal(2, listing.Files[0].Size);
        Assert.Equal(["Movies"], listing.Folders.Select(folder => folder.Name));
        Assert.False(browser.Browse(Outside, FolderBrowseFiles.Archives).Ok);
        Assert.False(browser.Browse(Root, "everything").Ok);
    }

    [Fact]
    public void DotDotOutOfARootIsRefusedAndSoIsAnyPathOutsideIt()
    {
        var browser = new FolderBrowser([Root]);
        var escape = Path.Combine(Root, "..", "outside");
        Directory.CreateDirectory(Outside);

        Assert.False(browser.Browse(escape).Ok);
        Assert.False(browser.Browse(Outside).Ok);
        Assert.False(browser.Browse(Path.GetPathRoot(Root)).Ok);
        Assert.False(browser.Browse("relative/path").Ok);
        // A sibling whose name starts like the root is not inside it.
        var sibling = Directory.CreateDirectory(Root + "-other").FullName;
        Assert.False(browser.Browse(sibling).Ok);
        Assert.Null(browser.Allowed(sibling));
    }

    [Fact]
    public void ALinkInsideARootThatPointsOutsideIsNeitherListedNorOpened()
    {
        var link = Path.Combine(Root, "escape");
        if (!TryLink(link, Outside)) return;
        Directory.CreateDirectory(Path.Combine(Outside, "secret"));
        var browser = new FolderBrowser([Root]);

        Assert.DoesNotContain(browser.Browse(Root).Folders, folder => folder.Name == "escape");
        Assert.False(browser.Browse(link).Ok);
        Assert.False(browser.Browse(Path.Combine(link, "secret")).Ok);
        Assert.False(browser.Create(link, "planted").Ok);
        Assert.False(Directory.Exists(Path.Combine(Outside, "planted")));
        // ".." after the link is the outside folder's parent, as the file system sees it, not the root the text suggests.
        Assert.Null(browser.Allowed(Path.Combine(link, "..")));
    }

    [Fact]
    public void ALinkThatStaysInsideTheRootIsListedAndResolved()
    {
        var real = Directory.CreateDirectory(Path.Combine(Root, "real")).FullName;
        var link = Path.Combine(Root, "alias");
        if (!TryLink(link, real)) return;
        var browser = new FolderBrowser([Root]);

        Assert.Contains(browser.Browse(Root).Folders, folder => folder.Name == "alias");
        var through = browser.Browse(link);
        Assert.True(through.Ok, through.Error);
        Assert.Equal(real, through.Path);
    }

    [Fact]
    public void ARootThatIsItselfALinkIsResolvedFirst()
    {
        var real = Directory.CreateDirectory(Path.Combine(_work, "real-root")).FullName;
        var link = Path.Combine(_work, "linked-root");
        if (!TryLink(link, real)) return;
        var browser = new FolderBrowser([link]);

        Assert.Equal([real], browser.Roots);
        Assert.True(browser.Browse(link).Ok);
    }

    [Fact]
    public void HiddenFoldersAreLeftOutOfAListing()
    {
        Directory.CreateDirectory(Path.Combine(Root, "Visible"));
        var hidden = Directory.CreateDirectory(Path.Combine(Root, OperatingSystem.IsWindows() ? "Hidden" : ".cache"));
        if (OperatingSystem.IsWindows()) hidden.Attributes |= FileAttributes.Hidden;
        var browser = new FolderBrowser([Root]);

        Assert.Equal(["Visible"], browser.Browse(Root).Folders.Select(folder => folder.Name));
        // Left out of the list, not out of reach: a path typed in still opens.
        Assert.True(browser.Browse(hidden.FullName).Ok);
    }

    [Fact]
    public void ATruncatedListingHoldsTheFirstFoldersByNameWhateverOrderTheDiskGivesThem()
    {
        var names = Enumerable.Range(0, FolderBrowser.MaxFolders + 25).Select(index => $"f{index:D5}").ToList();
        foreach (var name in names.OrderBy(_ => Random.Shared.Next())) Directory.CreateDirectory(Path.Combine(Root, name));
        var browser = new FolderBrowser([Root]);

        var listing = browser.Browse(Root);
        Assert.True(listing.Truncated);
        Assert.Equal(names.Take(FolderBrowser.MaxFolders), listing.Folders.Select(folder => folder.Name));
    }

    [Fact]
    public void CreateMakesOneFolderInsideTheRootAndRefusesBadNames()
    {
        var browser = new FolderBrowser([Root]);

        var made = browser.Create(Root, "New folder");
        Assert.True(made.Ok, made.Error);
        Assert.True(Directory.Exists(Path.Combine(Root, "New folder")));
        Assert.Equal(Path.Combine(Path.GetFullPath(Root), "New folder"), made.Path);

        Assert.False(browser.Create(Root, "New folder").Ok);
        foreach (var bad in new[] { "", " ", "..", ".", "a/b", "a\\b", " lead", "trail.", "CON", "x\u0001" })
            Assert.False(browser.Create(Root, bad).Ok, bad);
        Assert.False(browser.Create(Outside, "x").Ok);
        Assert.False(Directory.Exists(Path.Combine(Outside, "x")));
        Assert.False(browser.Create(Path.Combine(Root, ".."), "x").Ok);
    }

    [Fact]
    public void RootsAreSplitBySemicolonEverywhereAndByColonOffWindows()
    {
        Assert.Equal(["/downloads"], FolderBrowser.ParseRoots(null, "/downloads"));
        Assert.Equal(["/a", "/b"], FolderBrowser.ParseRoots("/a;/b", "/downloads"));
        if (!OperatingSystem.IsWindows()) Assert.Equal(["/a", "/b", "/c"], FolderBrowser.ParseRoots("/a:/b;/c", "/downloads"));
        else Assert.Equal([@"C:\a", @"D:\b"], FolderBrowser.ParseRoots(@"C:\a;D:\b", "/downloads"));
    }

    [Fact]
    public async Task OverTheServerBrowseAndCreateWorkUnderTheRootOnly()
    {
        await using var server = await ServerHarness.StartAsync();
        var listing = await server.AskAsync<FolderBrowseResult>(MessageTypes.FoldersBrowse, new FolderBrowseRequest { Path = server.Root });
        Assert.True(listing.Ok, listing.Error);

        var made = await server.AskAsync<FolderBrowseResult>(MessageTypes.FoldersCreate, new FolderCreateRequest { Parent = server.Root, Name = "Series" });
        Assert.True(made.Ok, made.Error);
        Assert.True(Directory.Exists(Path.Combine(server.Root, "Series")));

        var outside = await server.AskAsync<FolderBrowseResult>(MessageTypes.FoldersBrowse, new FolderBrowseRequest { Path = server.Data });
        Assert.False(outside.Ok);
        Assert.Contains("outside", outside.Error);
        var escape = await server.AskAsync<FolderBrowseResult>(MessageTypes.FoldersCreate, new FolderCreateRequest { Parent = Path.Combine(server.Root, ".."), Name = "planted" });
        Assert.False(escape.Ok);
        Assert.False(Directory.Exists(Path.Combine(server.Work, "planted")));
    }

    [Fact]
    public void AnArchiveLinkIsListedOnlyWhenItLeadsInsideTheRoots()
    {
        Directory.CreateDirectory(Root);
        var outsideFile = Path.Combine(Outside, "secret.rar");
        File.WriteAllText(outsideFile, "x");
        var insideFile = Path.Combine(Root, "real.zip");
        File.WriteAllText(insideFile, "x");
        try
        {
            File.CreateSymbolicLink(Path.Combine(Root, "out.rar"), outsideFile);
            File.CreateSymbolicLink(Path.Combine(Root, "in.zip"), insideFile);
        }
        catch (Exception ex) when (OperatingSystem.IsWindows() && ex is IOException or UnauthorizedAccessException)
        {
            return; // no symbolic links without Developer Mode or elevation
        }

        var listing = new FolderBrowser([Root]).Browse(Root, FolderBrowseFiles.Archives);
        Assert.Equal(["in.zip", "real.zip"], listing.Files.Select(file => file.Name));
    }

    [Fact]
    public async Task OverTheServerTheArchiveFilesAndTheSpeedHistoryAreAnswered()
    {
        await using var server = await ServerHarness.StartAsync();
        File.WriteAllText(Path.Combine(server.Root, "film.7z"), "x");
        var listing = await server.AskAsync<FolderBrowseResult>(MessageTypes.FoldersBrowse,
            new FolderBrowseRequest { Path = server.Root, Files = FolderBrowseFiles.Archives });
        Assert.True(listing.Ok, listing.Error);
        Assert.Equal("film.7z", Assert.Single(listing.Files).Name);

        var history = await server.AskAsync<SpeedHistoryDto>(MessageTypes.SpeedHistoryGet, new { });
        Assert.Equal(1, history.IntervalSeconds);
        Assert.NotEmpty(history.BytesPerSecond);
    }

    /// <summary>Windows needs Developer Mode or elevation for a symbolic link; without either the case is skipped there.</summary>
    private static bool TryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (OperatingSystem.IsWindows() && ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
