using DownloadManager.Contracts;
using DownloadManager.Mobile;

namespace DownloadManager.Mobile.Tests;

public sealed class RemotePanesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    private static SiteItemDto File(string name, long size = 1, DateTimeOffset? modified = null) =>
        new() { Name = name, Size = size, Modified = modified };

    private static SiteItemDto Folder(string name, int? count = null) => new() { Name = name, IsFolder = true, ChildCount = count };

    [Fact]
    public void NameSortPutsFoldersFirstThenFilesByNameIgnoringCase()
    {
        var sorted = RemoteFileList.Sort([File("b.txt"), Folder("zeta"), File("A.txt"), Folder("Alpha")], RemoteSort.Name);
        Assert.Equal(["Alpha", "zeta", "A.txt", "b.txt"], sorted.Select(item => item.Name));
    }

    [Fact]
    public void NewestSortOrdersFilesByDateWithUnknownDatesLastAndFoldersStayFirst()
    {
        var sorted = RemoteFileList.Sort([
            File("old", modified: Now.AddDays(-30)), File("none"), File("new", modified: Now.AddDays(-1)), Folder("dir")], RemoteSort.Newest);
        Assert.Equal(["dir", "new", "old", "none"], sorted.Select(item => item.Name));
    }

    [Fact]
    public void LargestSortOrdersFilesBySizeWithUnknownSizesLast()
    {
        var sorted = RemoteFileList.Sort([File("small", 10), File("unknown", -1), File("big", 1000), Folder("dir")], RemoteSort.Largest);
        Assert.Equal(["dir", "big", "small", "unknown"], sorted.Select(item => item.Name));
    }

    [Fact]
    public void DatesShowTheYearOnlyWhenItIsNotThisYear()
    {
        Assert.Equal("17 May", RemoteFileList.Date(new DateTimeOffset(2026, 5, 17, 9, 0, 0, TimeSpan.Zero), Now, TimeZoneInfo.Utc));
        Assert.Equal("21 Dec 2025", RemoteFileList.Date(new DateTimeOffset(2025, 12, 21, 9, 0, 0, TimeSpan.Zero), Now, TimeZoneInfo.Utc));
    }

    [Fact]
    public void RowDetailsFollowTheMockups()
    {
        Assert.Equal("Folder · 24 items", RemoteFileList.Detail(Folder("Music", 24), Now, TimeZoneInfo.Utc));
        Assert.Equal("Folder · 1 item", RemoteFileList.Detail(Folder("One", 1), Now, TimeZoneInfo.Utc));
        Assert.Equal("Folder", RemoteFileList.Detail(Folder("Unknown"), Now, TimeZoneInfo.Utc));
        Assert.Equal("24 MB · 17 May", RemoteFileList.Detail(File("a.m4a", 24L * 1000 * 1000, new DateTimeOffset(2026, 5, 17, 9, 0, 0, TimeSpan.Zero)), Now, TimeZoneInfo.Utc));
        Assert.Equal("Size unknown", RemoteFileList.Detail(File("a", -1), Now, TimeZoneInfo.Utc));
    }

    [Fact]
    public void SelectionSummaryCountsFoldersAndFiles()
    {
        Assert.Equal("1 folder", RemoteFileList.Summary([Folder("Wedding")]));
        Assert.Equal("2 files · 800 B", RemoteFileList.Summary([File("a", 400), File("b", 400)]));
        Assert.Equal("1 folder · 1 file · 5 B", RemoteFileList.Summary([Folder("d"), File("a", 5)]));
        Assert.Equal("1 file · size unknown", RemoteFileList.Summary([File("a", -1)]));
        Assert.Equal("Nothing selected", RemoteFileList.Summary([]));
    }

    [Fact]
    public void DestinationNamesTheLocationAndItsFolder()
    {
        Assert.Equal("Home NAS/media/recordings/2026", RemoteFileList.Destination("Home NAS", "/media/recordings/2026"));
        Assert.Equal("Home NAS", RemoteFileList.Destination("Home NAS", "/"));
    }

    [Fact]
    public void BreadcrumbsStartAtTheRootAndNeverGoAboveIt()
    {
        var crumbs = RemoteCrumbs.Build("media", "/media", "/media/recordings/2026");
        Assert.Equal(["media", "recordings", "2026"], crumbs.Select(c => c.Label));
        Assert.Equal(["/media", "/media/recordings", "/media/recordings/2026"], crumbs.Select(c => c.Path));
        // R3: a path above the configured start folder is shown from the root, not as its own segments.
        Assert.Equal(["media"], RemoteCrumbs.Build("media", "/media", "/etc/passwd.d").Select(c => c.Label));
        Assert.Equal(["My Drive"], RemoteCrumbs.Build("My Drive", "/", "/").Select(c => c.Label));
        Assert.Equal(["My Drive", "Photos 2025"], RemoteCrumbs.Build("My Drive", "/", "/Photos 2025").Select(c => c.Label));
    }

    [Fact]
    public void ParentStopsAtTheRoot()
    {
        Assert.Equal("/media/recordings", RemoteCrumbs.Parent("/media", "/media/recordings/2026"));
        Assert.Null(RemoteCrumbs.Parent("/media", "/media"));
        Assert.Null(RemoteCrumbs.Parent("/media", "/other"));
        Assert.Equal("/", RemoteCrumbs.Parent("/", "/a"));
        Assert.Null(RemoteCrumbs.Parent("/", "/"));
    }

    [Fact]
    public void PhoneCrumbsStartAtPhone()
    {
        Assert.Equal(["Phone", "Download", "Inbound"], RemoteCrumbs.BuildPhone("Download/Inbound").Select(c => c.Label));
        Assert.Equal(["", "Download", "Download/Inbound"], RemoteCrumbs.BuildPhone("Download/Inbound").Select(c => c.Path));
        Assert.Single(RemoteCrumbs.BuildPhone(""));
    }

    [Fact]
    public void RootLabelUsesTheStartFolderOrTheService()
    {
        Assert.Equal("recordings", RemoteCrumbs.RootLabel(new SiteDto { Kind = "sftp", Name = "Home NAS", RemoteFolder = "/media/recordings" }));
        Assert.Equal("My Drive", RemoteCrumbs.RootLabel(new SiteDto { Kind = "drive", Name = "drive" }));
        Assert.Equal("Home NAS", RemoteCrumbs.RootLabel(new SiteDto { Kind = "sftp", Name = "Home NAS" }));
    }

    [Fact]
    public void CloudLocationsKeepTheirOwnNameButShowTheServiceForTheBareRcloneName()
    {
        Assert.Equal("Google Drive", RemoteLocationText.DisplayName(new SiteDto { Kind = "drive", Name = "drive" }));
        Assert.Equal("OneDrive", RemoteLocationText.DisplayName(new SiteDto { Kind = "onedrive", Name = "onedrive" }));
        Assert.Equal("Work Drive", RemoteLocationText.DisplayName(new SiteDto { Kind = "drive", Name = "Work Drive" }));
        Assert.Equal("Home NAS", RemoteLocationText.DisplayName(new SiteDto { Kind = "sftp", Name = "Home NAS" }));
        Assert.Equal("drive", RemoteLocationText.DisplayName(new SiteDto { Kind = "webdav", Name = "drive" }));
    }

    [Fact]
    public void TransferModeFollowsThePanes()
    {
        Assert.Equal(RemoteTransferMode.Download, RemoteTransfers.Mode(false, true));
        Assert.Equal(RemoteTransferMode.Upload, RemoteTransfers.Mode(true, false));
        Assert.Equal(RemoteTransferMode.LocationToLocation, RemoteTransfers.Mode(false, false));
        Assert.Equal(RemoteTransferMode.None, RemoteTransfers.Mode(true, true));
    }

    [Fact]
    public void ALocationToLocationCopyNamesBothEndsAndSkipsWhatIsThere()
    {
        var request = RemoteTransfers.Copy("drive", "Photos 2025/", [new SiteItemDto { Name = "Wedding", IsFolder = true }, File("IMG_2041.jpg")],
            "nas", "/photos");
        Assert.Equal(("drive", "copy", "/Photos 2025"), (request.SiteId, request.Direction, request.RemoteFolder));
        Assert.Equal(("nas", "/photos", "skip"), (request.DestinationSiteId, request.DestinationFolder, request.Conflict));
        Assert.Equal(["Wedding", "IMG_2041.jpg"], request.Names.Select(n => n.Name));
        Assert.Equal([true, false], request.Names.Select(n => n.IsFolder));
        Assert.Equal("", request.LocalFolder);
        Assert.False(request.SafeSingleDownload);
        // r6's caption under the selection.
        Assert.Equal("To Home NAS/photos", "To " + RemoteFileList.Destination("Home NAS", RemoteCrumbs.Normalize(request.DestinationFolder)));
        Assert.Equal("sitesRemoteCopy", SiteTransferStartRequest.RemoteCopyCapability);
    }

    [Fact]
    public void ATransferRowNamesBothLocationsOfACopy()
    {
        Assert.Equal("copy · Google Drive → Home NAS",
            RemoteTransfers.Title(new SiteTransferDto { Direction = "copy", SiteName = "Google Drive", DestinationSiteName = "Home NAS" }));
        Assert.Equal("download · Home NAS", RemoteTransfers.Title(new SiteTransferDto { Direction = "download", SiteName = "Home NAS" }));
    }

    [Fact]
    public void PhonePathsMapToTheDownloadFolder()
    {
        Assert.Null(RemoteTransfers.DownloadRelative(""));
        Assert.Equal("", RemoteTransfers.DownloadRelative("Download"));
        Assert.Equal("Inbound/Music", RemoteTransfers.DownloadRelative("Download/Inbound/Music"));
        Assert.Null(RemoteTransfers.DownloadRelative("DCIM/Camera"));
        Assert.Equal("Downloads/Inbound/Music", RemoteTransfers.PhoneLabel("Download/Inbound/Music"));
        Assert.Equal("Downloads", RemoteTransfers.PhoneLabel("Download"));
    }

    private static SiteHostKeyDto Key(string type, string fingerprint) => new() { Type = type, Fingerprint = fingerprint, Entry = type + " " + fingerprint };

    [Fact]
    public void FirstTrustOffersEveryKeyTheServerHas()
    {
        var review = SftpKeyReview.Compare([], [Key("ssh-ed25519", "SHA256:a"), Key("ssh-rsa", "SHA256:b")]);
        Assert.Equal(SftpKeyCase.First, review.Case);
        Assert.Equal(["ssh-ed25519 SHA256:a", "ssh-rsa SHA256:b"], review.Entries);
    }

    [Fact]
    public void ATrustedKeyThatIsStillOfferedWithNewKeyTypesIsNotAChange()
    {
        // R1: the phone saved only the first key, so the next browse looked like a changed key.
        var review = SftpKeyReview.Compare([Key("ssh-ed25519", "SHA256:a")], [Key("ssh-ed25519", "SHA256:a"), Key("ssh-rsa", "SHA256:b")]);
        Assert.Equal(SftpKeyCase.MoreKeys, review.Case);
        Assert.Equal(2, review.Entries.Count);
    }

    [Fact]
    public void AllOfferedKeysAlreadyTrustedIsUnchanged() =>
        Assert.Equal(SftpKeyCase.Unchanged,
            SftpKeyReview.Compare([Key("ssh-ed25519", "SHA256:a"), Key("ssh-rsa", "SHA256:b")], [Key("ssh-ed25519", "SHA256:a")]).Case);

    [Fact]
    public void ADifferentFingerprintForATrustedKeyTypeIsAChange()
    {
        Assert.Equal(SftpKeyCase.Changed, SftpKeyReview.Compare([Key("ssh-ed25519", "SHA256:a")], [Key("ssh-ed25519", "SHA256:x")]).Case);
        Assert.Equal(SftpKeyCase.Changed,
            SftpKeyReview.Compare([Key("ssh-ed25519", "SHA256:a")], [Key("ssh-ed25519", "SHA256:x"), Key("ssh-rsa", "SHA256:b")]).Case);
        Assert.Equal(SftpKeyCase.Changed, SftpKeyReview.Compare([Key("ssh-ed25519", "SHA256:a")], [Key("ssh-rsa", "SHA256:b")]).Case);
    }

    [Fact]
    public void FingerprintsBreakIntoTwoEvenLines()
    {
        var fingerprint = "SHA256:" + new string('x', 43);
        var lines = SftpKeyReview.FingerprintLines(fingerprint).Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.True(Math.Abs(lines[0].Length - lines[1].Length) <= 1);
        Assert.Equal(fingerprint, string.Concat(lines));
        Assert.Equal("short", SftpKeyReview.FingerprintLines("short"));
    }

    [Fact]
    public async Task TrustSendsEveryKeyOnceAndTheChangeRequestsUseTheirMessages()
    {
        var host = new FakeHostConnection
        {
            Reply = request => request.Type switch
            {
                SitesMessageTypes.CountResult or SitesMessageTypes.Count => IpcMessage.Create(SitesMessageTypes.CountResult, new SiteCountResultDto { Ok = true, Files = 3 }, request.Id),
                SitesMessageTypes.Delete => IpcMessage.Create(SitesMessageTypes.ChangeResult, new SiteChangeResultDto { Ok = true }, request.Id),
                _ => IpcMessage.Create(SitesMessageTypes.Result, new SiteResultDto { Ok = true }, request.Id)
            }
        };
        var client = new RemoteFilesClient(host);
        Assert.True((await client.TrustSftpEndpointAsync("s", "nas", 22, ["a", "b", "a"])).Ok);
        Assert.True((await client.MakeFolderAsync(new SiteMakeFolderRequest { Id = "s", Path = "/x", Name = "new" })).Ok);
        Assert.True((await client.RenameAsync(new SiteRenameRequest { Id = "s", Path = "/x", Name = "a", NewName = "b" })).Ok);
        Assert.Equal(3, (await client.CountAsync(new SiteChangeRequest { Id = "s", Path = "/x" })).Files);
        Assert.True((await client.DeleteAsync(new SiteChangeRequest { Id = "s", Path = "/x" })).Ok);

        Assert.Equal(["a", "b"], host.Sent[0].GetPayload<SiteTrustRequest>().Entries);
        Assert.Equal(SitesMessageTypes.TrustSftpEndpoint, host.Sent[0].Type);
        Assert.Equal(["mkdir", "rename"], [host.Sent[1].Type[6..], host.Sent[2].Type[6..]]);
        Assert.Equal(SitesMessageTypes.Count, host.Sent[3].Type);
        Assert.Equal(SitesMessageTypes.Delete, host.Sent[4].Type);
    }
}
