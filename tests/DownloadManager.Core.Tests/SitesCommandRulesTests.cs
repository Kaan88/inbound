using DownloadManager.Contracts;
using DownloadManager.Desktop.WinUI;

namespace DownloadManager.Core.Tests;

/// <summary>SITES.11 R2b: the menus, Enter and double click, the conflict question, Copy address and the Open folder.</summary>
public sealed class SitesCommandRulesTests
{
    private static SitesBrowseRules.PaneLine File(string name) => new(name, false, 10);
    private static SitesBrowseRules.PaneLine Folder(string name) => new(name, true, -1);
    private static readonly SitesBrowseRules.PaneLine Up = new("..", true, -1, IsParent: true);

    private static readonly string[] All = [SiteCapabilities.NewFolder, SiteCapabilities.EmptyFolders];

    [Fact]
    public void OneFileGetsTheWholeServerMenu()
    {
        var groups = SitesCommandRules.ServerMenu(All, [File("a.iso")]);

        Assert.Equal(3, groups.Count);
        Assert.Equal([SiteCommand.Download, SiteCommand.DownloadTo, SiteCommand.Open], groups[0]);
        Assert.Equal([SiteCommand.Rename, SiteCommand.Delete, SiteCommand.NewFolder], groups[1]);
        Assert.Equal([SiteCommand.CopyAddress], groups[2]);
    }

    [Fact]
    public void AFolderRowAddsStarAndSeveralRowsHaveNoRenameOrOpen()
    {
        Assert.Contains(SiteCommand.StarFolder, SitesCommandRules.ServerMenu(All, [Folder("x")]).SelectMany(g => g));
        var many = SitesCommandRules.ServerMenu(All, [File("a"), Folder("b")]).SelectMany(g => g).ToList();
        Assert.DoesNotContain(SiteCommand.Rename, many);
        Assert.DoesNotContain(SiteCommand.Open, many);
        Assert.DoesNotContain(SiteCommand.StarFolder, many);
        Assert.Contains(SiteCommand.Delete, many);
    }

    [Fact]
    public void ASiteWithoutNewFolderHidesIt()
    {
        var rows = SitesCommandRules.ServerMenu([], [File("a")]).SelectMany(g => g);
        var empty = SitesCommandRules.ServerMenu([], []).SelectMany(g => g).ToList();

        Assert.DoesNotContain(SiteCommand.NewFolder, rows);
        Assert.DoesNotContain(SiteCommand.NewFolder, empty);
        Assert.Contains(SiteCommand.Upload, empty);
    }

    [Fact]
    public void EmptySpaceAndTheParentLineShowTheEmptyMenu()
    {
        foreach (var menu in new[] { SitesCommandRules.ServerMenu(All, []), SitesCommandRules.ServerMenu(All, [Up]) })
        {
            Assert.Equal(2, menu.Count);
            Assert.Equal([SiteCommand.NewFolder, SiteCommand.Upload], menu[0]);
            Assert.Equal([SiteCommand.Refresh, SiteCommand.PasteAddress], menu[1]);
        }
    }

    [Fact]
    public void ThisPcMenuFollowsTheSelection()
    {
        var one = SitesCommandRules.LocalMenu([File("a")], canUpload: true);
        Assert.Equal([LocalCommand.Upload, LocalCommand.Open, LocalCommand.ShowInExplorer], one[0]);
        Assert.Equal([LocalCommand.Rename, LocalCommand.Delete], one[1]);
        Assert.Equal([LocalCommand.NewFolder], one[2]);

        var two = SitesCommandRules.LocalMenu([File("a"), File("b")], canUpload: false);
        Assert.Equal(2, two.Count);
        Assert.Equal([LocalCommand.Delete], two[0]);

        var empty = SitesCommandRules.LocalMenu([Up], canUpload: true);
        Assert.Single(empty);
        Assert.Equal([LocalCommand.NewFolder, LocalCommand.Refresh], empty[0]);
    }

    [Fact]
    public void EnterAndDoubleClick()
    {
        Assert.Equal(ActivateAction.Download, SitesCommandRules.Activate([File("a")]));
        Assert.Equal(ActivateAction.OpenFolder, SitesCommandRules.Activate([Folder("a")]));
        Assert.Equal(ActivateAction.GoUp, SitesCommandRules.Activate([Up]));
        Assert.Equal(ActivateAction.Download, SitesCommandRules.Activate([File("a"), Folder("b")]));
        Assert.Equal(ActivateAction.Download, SitesCommandRules.Activate([Up, File("a")]));
        Assert.Equal(ActivateAction.None, SitesCommandRules.Activate([]));
    }

    [Fact]
    public void ConflictsAreFilesThatAreThere()
    {
        var picked = new List<SiteTransferNameDto>
        {
            new() { Name = "a.iso" }, new() { Name = "B.txt" }, new() { Name = "docs", IsFolder = true }, new() { Name = "new.bin" },
        };
        string[] there = ["A.ISO", "b.txt", "docs"];

        // A folder that is there counts too: the files inside it follow the batch's choice.
        Assert.Equal(["a.iso", "B.txt", "docs"], SitesCommandRules.AlreadyThere(picked, there, ignoreCase: true));
        Assert.Equal(["docs"], SitesCommandRules.AlreadyThere(picked, there, ignoreCase: false));
        Assert.Equal("skip", SitesCommandRules.ConflictChoices[0].Value);
    }

    [Fact]
    public void TheQuestionNamesOneFileAndThePlace()
    {
        var one = SitesCommandRules.ConflictQuestion(["debian-13.2-amd64.iso"], @"D:\Downloads\ISOs", hasFolders: false, targetComplete: true);
        Assert.Equal(@"debian-13.2-amd64.iso is already in D:\Downloads\ISOs", one?.Title);
        Assert.Empty(one!.Value.Notes);
        var three = SitesCommandRules.ConflictQuestion(["a", "b", "c"], @"D:\x", hasFolders: false, targetComplete: true);
        Assert.Equal(@"3 of these are already in D:\x", three?.Title);
        Assert.Null(SitesCommandRules.ConflictQuestion([], @"D:\x", hasFolders: true, targetComplete: true));
    }

    [Fact]
    public void ABatchWithFoldersSaysTheFilesInsideFollow()
    {
        var ask = SitesCommandRules.ConflictQuestion(["docs"], "/media on nas", hasFolders: true, targetComplete: true);
        Assert.Contains(SitesCommandRules.FoldersFollowNote, ask!.Value.Notes);
        Assert.Equal("Files inside the folders follow the same choice.", SitesCommandRules.FoldersFollowNote);
    }

    [Fact]
    public void UploadsAskWhenTheListingIsPartAndCompareWithoutCase()
    {
        List<SiteTransferNameDto> picked = [new() { Name = "A.ISO" }];
        Assert.Equal(["A.ISO"], SitesCommandRules.AlreadyThere(picked, ["a.iso"], ignoreCase: true));
        var partial = SitesCommandRules.ConflictQuestion([], "/media on nas", hasFolders: false, targetComplete: false);
        Assert.Equal("Some of these may already be there", partial?.Title);
        var some = SitesCommandRules.ConflictQuestion(["a.iso"], "/media on nas", hasFolders: false, targetComplete: false);
        Assert.Contains(some!.Value.Notes, note => note.Contains("others may be there too", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("setup.exe", true)]
    [InlineData("SETUP.EXE", true)]
    [InlineData("run.bat", true)]
    [InlineData("x.cmd", true)]
    [InlineData("a.ps1", true)]
    [InlineData("link.lnk", true)]
    [InlineData("site.url", true)]
    [InlineData("a.js", true)]
    [InlineData("a.vbs", true)]
    [InlineData("a.hta", true)]
    [InlineData("pkg.msi", true)]
    [InlineData("s.scr", true)]
    [InlineData("trick.exe. ", true)]
    [InlineData("notes.txt", false)]
    [InlineData("photo.jpg", false)]
    [InlineData("disk.iso", false)]
    [InlineData("exe", false)]
    [InlineData("archive.exe.zip", false)]
    public void ProgramsAndScriptsAreAskedAboutBeforeRunning(string name, bool runnable)
        => Assert.Equal(runnable, SitesCommandRules.IsRunnable(name));

    [Fact]
    public void RunQuestionAndMarkOfTheWeb()
    {
        Assert.Equal("setup.exe is a program or script from nas.home.lan. Run it?", SitesCommandRules.RunQuestion("setup.exe", "nas.home.lan"));
        Assert.Contains("ZoneId=3", SitesCommandRules.ZoneIdentifierText);
        Assert.StartsWith("[ZoneTransfer]", SitesCommandRules.ZoneIdentifierText);
    }

    [Fact]
    public void EachOpenGetsAFolderOfItsOwn()
    {
        var first = SitesCommandRules.OpenFolder(@"C:\Temp", "s1", Guid.NewGuid());
        var second = SitesCommandRules.OpenFolder(@"C:\Temp", "s1", Guid.NewGuid());
        Assert.NotEqual(first, second);
        Assert.Equal(SitesCommandRules.OpenFolder(@"C:\Temp", "s1"), Path.GetDirectoryName(first));
        Assert.Equal(Path.Combine(@"C:\Temp", "Inbound", "open"), SitesCommandRules.OpenRoot(@"C:\Temp"));
        Assert.Equal(SitesCommandRules.OpenKey("s1", "/media", "a.iso"), SitesCommandRules.OpenKey("s1", "/media/", "a.iso"));
        Assert.NotEqual(SitesCommandRules.OpenKey("s1", "/media", "a.iso"), SitesCommandRules.OpenKey("s2", "/media", "a.iso"));
        var now = new DateTime(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(SitesCommandRules.IsStaleOpenFolder(now.AddDays(-1).AddMinutes(-1), now));
        Assert.False(SitesCommandRules.IsStaleOpenFolder(now.AddHours(-23), now));
    }

    [Fact]
    public void AnotherServerHasNoOpenOrDownloadTo()
    {
        Assert.True(SitesCommandRules.OffForOtherServer(SiteCommand.Open));
        Assert.True(SitesCommandRules.OffForOtherServer(SiteCommand.DownloadTo));
        Assert.False(SitesCommandRules.OffForOtherServer(SiteCommand.Download));
        var menu = SitesCommandRules.ServerMenu(All, [File("a.iso")], otherServer: true).SelectMany(g => g).ToList();
        Assert.DoesNotContain(SiteCommand.Open, menu);
        Assert.DoesNotContain(SiteCommand.DownloadTo, menu);
        Assert.Contains(SiteCommand.Download, menu);
        Assert.Contains(SiteCommand.Open, SitesCommandRules.ServerMenu(All, [File("a.iso")]).SelectMany(g => g));
    }

    [Theory]
    [InlineData("  new name  ", "new name", true)]
    [InlineData("", "", false)]
    [InlineData("   ", "", false)]
    [InlineData(".", ".", false)]
    [InlineData("..", "..", false)]
    [InlineData("name.", "name.", false)]
    [InlineData("CON", "CON", false)]
    [InlineData("nul.txt", "nul.txt", false)]
    [InlineData("a:b", "a:b", false)]
    [InlineData("a?b", "a?b", false)]
    public void LocalNamesAreTrimmedAndChecked(string typed, string name, bool ok)
    {
        var (got, problem) = SitesCommandRules.LocalNewName(typed, windows: true);
        Assert.Equal(name, got);
        Assert.Equal(ok, problem is null);
    }

    [Fact]
    public void ARunningBatchKeepsItsItemsFromRename()
    {
        var running = new SiteTransferDto { Id = "t", State = "running", LocalFolder = @"D:\ISOs", Picked = ["debian.iso", "docs/a.txt"] };
        var done = new SiteTransferDto { Id = "d", State = "done", LocalFolder = @"D:\ISOs", Picked = ["old.iso"] };
        Assert.True(SitesCommandRules.LocalItemBusy([running], @"D:\ISOs", "DEBIAN.ISO"));
        Assert.True(SitesCommandRules.LocalItemBusy([running], @"D:\ISOs\", "docs"));
        Assert.False(SitesCommandRules.LocalItemBusy([running], @"D:\ISOs", "other.iso"));
        Assert.False(SitesCommandRules.LocalItemBusy([done], @"D:\ISOs", "old.iso"));
        // The batch's folder itself, renamed from the folder above it.
        Assert.True(SitesCommandRules.LocalItemBusy([running], @"D:\", "ISOs"));
    }

    [Fact]
    public void APastedAddressMustUseTheSitesPort()
    {
        var odd = Sftp(port: 2222);
        Assert.Null(SitesCommandRules.PathFromText(odd, "sftp://nas.home.lan/x"));
        Assert.Null(SitesCommandRules.PathFromText(odd, "sftp://nas.home.lan:22/x"));
        Assert.Equal("/x", SitesCommandRules.PathFromText(odd, "sftp://nas.home.lan:2222/x"));
        // A path from "/" has no port to compare.
        Assert.Equal("/x", SitesCommandRules.PathFromText(odd, "/x"));
        Assert.Equal("/x", SitesCommandRules.PathFromText(Sftp(), "sftp://nas.home.lan/x"));
        var ftp = new SiteDto { Kind = SiteKindCatalog.Ftp, Host = "f.example", Port = 2121, Tls = SiteTlsModes.None };
        Assert.Null(SitesCommandRules.PathFromText(ftp, "ftp://f.example/pub"));
        Assert.Equal("/pub", SitesCommandRules.PathFromText(ftp, "ftp://f.example:2121/pub"));
        var dav = new SiteDto { Kind = SiteKindCatalog.WebDav, Url = "https://cloud.example.net:8443/dav" };
        Assert.Null(SitesCommandRules.PathFromText(dav, "https://cloud.example.net/dav/a"));
        Assert.Equal("/a", SitesCommandRules.PathFromText(dav, "https://cloud.example.net:8443/dav/a"));
    }

    private static SiteDto Sftp(string host = "nas.home.lan", int port = 22, string user = "kaan") =>
        new() { Kind = SiteKindCatalog.Sftp, Host = host, Port = port, User = user, HasPassword = true };

    [Fact]
    public void AddressHasNoPasswordAndEscapesTheParts()
    {
        var site = Sftp();

        Assert.Equal("sftp://kaan@nas.home.lan/media/my%20files/a%23b.iso", SitesCommandRules.Address(site, "/media/my files/a#b.iso"));
        Assert.Equal("sftp://kaan@nas.home.lan/", SitesCommandRules.Address(site, "/"));
        Assert.DoesNotContain(":@", SitesCommandRules.Address(site, "/x"));
    }

    [Fact]
    public void AddressShowsAnOddPortAndBracketsAnIpv6Host()
    {
        Assert.Equal("sftp://kaan@nas.home.lan:2222/x", SitesCommandRules.Address(Sftp(port: 2222), "/x"));
        Assert.Equal("sftp://kaan@[fe80::1]/x", SitesCommandRules.Address(Sftp(host: "fe80::1"), "/x"));
        Assert.Equal("sftp://kaan@[fe80::1]/x", SitesCommandRules.Address(Sftp(host: "[fe80::1]"), "/x"));
        Assert.Equal("sftp://nas.home.lan/x", SitesCommandRules.Address(Sftp(user: ""), "/x"));
    }

    [Fact]
    public void AddressOfFtpAndWebDav()
    {
        var plain = new SiteDto { Kind = SiteKindCatalog.Ftp, Host = "files.example.org", Port = 21, User = "anonymous", Tls = SiteTlsModes.None };
        var secure = new SiteDto { Kind = SiteKindCatalog.Ftp, Host = "files.example.org", Port = 990, Tls = SiteTlsModes.Implicit };
        var dav = new SiteDto { Kind = SiteKindCatalog.WebDav, Url = "https://cloud.example.net/remote.php/dav/files/me", User = "me" };

        Assert.Equal("ftp://anonymous@files.example.org/pub", SitesCommandRules.Address(plain, "/pub"));
        Assert.Equal("ftps://files.example.org/pub", SitesCommandRules.Address(secure, "/pub"));
        Assert.Equal("https://me@cloud.example.net/remote.php/dav/files/me/docs", SitesCommandRules.Address(dav, "/docs"));
    }

    [Fact]
    public void AddressesListTheSelectionOrTheFolder()
    {
        var site = Sftp();

        Assert.Equal("sftp://kaan@nas.home.lan/media", SitesCommandRules.Addresses(site, "/media", []));
        Assert.Equal(["sftp://kaan@nas.home.lan/media/a", "sftp://kaan@nas.home.lan/media/b"],
            SitesCommandRules.Addresses(site, "/media", ["a", "b"]).Split(Environment.NewLine));
    }

    [Fact]
    public void PasteTakesAPathOrAnAddressOfThisSite()
    {
        var site = Sftp();

        Assert.Equal("/media/isos", SitesCommandRules.PathFromText(site, "  /media/isos/ \r\n"));
        Assert.Equal("/media/my files", SitesCommandRules.PathFromText(site, "sftp://kaan@nas.home.lan/media/my%20files"));
        Assert.Equal("/x", SitesCommandRules.PathFromText(site, "SFTP://NAS.HOME.LAN:22/x"));
        Assert.Null(SitesCommandRules.PathFromText(site, "sftp://other.host/x"));
        Assert.Null(SitesCommandRules.PathFromText(site, "sftp://nas.home.lan:2222/x"));
        Assert.Null(SitesCommandRules.PathFromText(site, "https://nas.home.lan/x"));
        Assert.Null(SitesCommandRules.PathFromText(site, "hello"));
        Assert.Null(SitesCommandRules.PathFromText(site, ""));
        Assert.Equal("/a", SitesCommandRules.PathFromText(Sftp(host: "fe80::1"), "sftp://[fe80::1]/a"));
    }

    [Fact]
    public void OpenFolderIsPerSiteUnderTemp()
    {
        var folder = SitesCommandRules.OpenFolder(@"C:\Temp", "ab12-_cd");

        Assert.Equal(Path.Combine(@"C:\Temp", "Inbound", "open", "ab12-_cd"), folder);
        Assert.Equal(Path.Combine(@"C:\Temp", "Inbound", "open", "a_b_c"), SitesCommandRules.OpenFolder(@"C:\Temp", "a/b\\c"));
        Assert.EndsWith("site", SitesCommandRules.OpenFolder(@"C:\Temp", ""));
    }

    [Fact]
    public void TheAskedForFolderShowsInTrouble()
    {
        Assert.Equal("/media/isos", SitesCommandRules.AskedPath(atStart: true, "/media/isos/", "/"));
        Assert.Equal("/", SitesCommandRules.AskedPath(atStart: true, "", "/"));
        Assert.Equal("/a/b", SitesCommandRules.AskedPath(atStart: false, "/media", "/a/b"));
    }

    [Fact]
    public void EveryCommandHasWordsAndTheKeysAreTheDrawnOnes()
    {
        foreach (var command in Enum.GetValues<SiteCommand>()) Assert.False(string.IsNullOrWhiteSpace(SitesCommandRules.Label(command)));
        Assert.Equal("Ctrl+Shift+S", SitesCommandRules.KeyText(SiteCommand.DownloadTo));
        Assert.Equal("Ctrl+Enter", SitesCommandRules.KeyText(SiteCommand.Open));
        Assert.Equal("", SitesCommandRules.KeyText(SiteCommand.StarFolder));
    }
}
