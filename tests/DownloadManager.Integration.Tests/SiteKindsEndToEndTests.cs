using System.Security.Cryptography;
using DownloadManager.Contracts;
using DownloadManager.Runtime.Sites;
using DownloadManager.Sites;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// SITES.6: FTP, WebDAV and S3 sites end to end, each against <c>rclone serve ftp|webdav|s3</c> on loopback with a real
/// rclone (INBOUND_RCLONE): test, browse, new folder where the kind has one, rename, upload, download (SHA-256 equal) and
/// delete. Skipped without rclone.
/// </summary>
public sealed partial class SiteTransfersEndToEndTests
{
    private const string AccessKey = "INBOUNDTESTKEY";
    private const string SecretKey = "inbound-test-secret-8813";

    [SiteTransfersFact]
    public async Task APlainFtpSiteWorksAndSaysItIsNotEncrypted()
    {
        var port = StartServer(slow: false, "ftp", ["--user", User, "--pass", Password]);
        var (commands, site) = await SaveKindAsync(new SiteSaveRequest
        {
            Name = "FTP", Kind = SiteKinds.Ftp, Host = "127.0.0.1", Port = port, Tls = SiteTlsModes.None, User = User, Password = Password,
        });
        Assert.False(site.Encrypted);
        Assert.Contains(SiteCapabilities.NewFolder, site.Capabilities);
        Assert.DoesNotContain(SiteCapabilities.HostKey, site.Capabilities);
        // There is no SSH key to trust on an FTP site.
        Assert.False((await Call<SiteResultDto>(commands, SitesMessageTypes.Trust,
            new SiteTrustRequest { Id = site.Id, Entries = ["ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIA"] })).Ok);
        await ExerciseAsync(commands, site.Id, makesFolders: true);
    }

    [SiteTransfersFact]
    public async Task AWebDavSiteWorks()
    {
        var port = StartServer(slow: false, "webdav", ["--user", User, "--pass", Password]);
        var (commands, site) = await SaveKindAsync(new SiteSaveRequest
        {
            Name = "DAV", Kind = SiteKinds.WebDav, Url = $"http://127.0.0.1:{port}/", Vendor = "other", User = User, Password = Password,
        });
        Assert.False(site.Encrypted);
        Assert.Equal("127.0.0.1", site.Host);
        Assert.Equal(port, site.Port);
        await ExerciseAsync(commands, site.Id, makesFolders: true);

        // A user name or password in the address is refused: they have boxes of their own.
        var refused = await Call<SiteResultDto>(commands, SitesMessageTypes.Save, new SiteSaveRequest
        {
            Name = "Bad", Kind = SiteKinds.WebDav, Url = $"http://me:secret@127.0.0.1:{port}/", Vendor = "other",
        });
        Assert.False(refused.Ok);
        Assert.DoesNotContain("secret", refused.Message);
    }

    [SiteTransfersFact]
    public async Task ConditionalDeviceCreateFailsClosedOnProductionHost()
    {
        var commands = Open();
        var status = await Call<SitesStatusDto>(commands, SitesMessageTypes.SetEnabled,
            new SitesSetEnabledRequest { Enabled = true, OwnRclonePath = Rclone });
        Assert.True(status.Enabled);
        var result = await Call<SiteDeviceStreamDto>(commands, SitesMessageTypes.DeviceCreateOpen,
            new SiteDeviceOpenRequest { Listing = "confirmed", Size = 3 });
        Assert.False(result.Ok);
        Assert.Null(result.StreamId);
        Assert.Contains("does not support", result.Message);
    }

    [SiteTransfersFact]
    public async Task ConditionalDeviceCreatePreservesCompetingFileAndRejectsEditedSite()
    {
        int port = StartServer(slow: false, "webdav", []);
        string url = $"http://127.0.0.1:{port}/";
        var (commands, site) = await SaveKindAsync(new SiteSaveRequest { Name = "DAV", Kind = SiteKinds.WebDav, Url = url }, uploadProbe: true);
        var browse = await Call<SiteBrowseResultDto>(commands, SitesMessageTypes.Browse, new SiteBrowseRequest { Id = site.Id, Path = "/pub" });
        Assert.True(browse.Ok, browse.Message);

        Task<SiteDeviceStreamDto> OpenUpload(string name, string? listing) =>
            Call<SiteDeviceStreamDto>(commands, SitesMessageTypes.DeviceCreateOpen, new SiteDeviceOpenRequest
            { Id = site.Id, Folder = "/pub", Name = name, Size = 3, Listing = listing });
        Task<SiteDeviceChunkDto> Publish(string? id) =>
            Call<SiteDeviceChunkDto>(commands, SitesMessageTypes.DeviceWrite,
                new SiteDeviceChunkRequest { StreamId = id!, Last = true, Data = [97, 98, 99] });
        string FolderFile(string name) => Path.Combine(Served, "pub", name);

        var refused = await OpenUpload("no-confirmation.txt", "unissued");
        Assert.Equal(SiteProblems.ListingChanged, refused.Problem);
        var fresh = await OpenUpload("fresh.txt", browse.Listing);
        Assert.True(fresh.Ok, fresh.Message);
        var published = await Publish(fresh.StreamId);
        Assert.True(published.Done, published.Message);
        Assert.Equal("abc", await File.ReadAllTextAsync(FolderFile("fresh.txt")));

        browse = await Call<SiteBrowseResultDto>(commands, SitesMessageTypes.Browse, new SiteBrowseRequest { Id = site.Id, Path = "/pub" });
        var racing = await OpenUpload("competing.txt", browse.Listing);
        Assert.True(racing.Ok, racing.Message);
        await File.WriteAllTextAsync(FolderFile("competing.txt"), "someone else");
        var conflict = await Publish(racing.StreamId);
        Assert.False(conflict.Ok);
        Assert.Contains("not replaced", conflict.Message);
        Assert.Equal("someone else", await File.ReadAllTextAsync(FolderFile("competing.txt")));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(Served, "pub"), ".inbound-upload-*"));

        browse = await Call<SiteBrowseResultDto>(commands, SitesMessageTypes.Browse, new SiteBrowseRequest { Id = site.Id, Path = "/pub" });
        var stale = await OpenUpload("stale.txt", browse.Listing);
        Assert.True(stale.Ok, stale.Message);
        var edited = await Call<SiteResultDto>(commands, SitesMessageTypes.Save,
            new SiteSaveRequest { Id = site.Id, Name = "Edited DAV", Kind = SiteKinds.WebDav, Url = url + "pub/" });
        Assert.True(edited.Ok, edited.Message);
        var blocked = await Publish(stale.StreamId);
        Assert.False(blocked.Ok);
        Assert.Contains("destination changed", blocked.Message);
        Assert.False(File.Exists(FolderFile("stale.txt")));
        Assert.False(File.Exists(Path.Combine(Served, "pub", "pub", "stale.txt")));

        browse = await Call<SiteBrowseResultDto>(commands, SitesMessageTypes.Browse, new SiteBrowseRequest { Id = site.Id, Path = "/" });
        Assert.True(browse.Ok, browse.Message);
        var oldSession = await Call<SiteDeviceStreamDto>(commands, SitesMessageTypes.DeviceCreateOpen, new SiteDeviceOpenRequest
        { Id = site.Id, Folder = "/", Name = "old-session.txt", Size = 3, Listing = browse.Listing });
        Assert.True(oldSession.Ok, oldSession.Message);
        await Call<SitesStatusDto>(commands, SitesMessageTypes.SetEnabled, new SitesSetEnabledRequest { Enabled = false });
        await Call<SitesStatusDto>(commands, SitesMessageTypes.SetEnabled, new SitesSetEnabledRequest { Enabled = true });
        Assert.False((await Publish(oldSession.StreamId)).Ok);
        Assert.False(File.Exists(FolderFile("old-session.txt")));
    }

    [SiteTransfersFact]
    public async Task AnS3SiteWorksWithoutEmptyFolders()
    {
        var port = StartServer(slow: false, "s3", ["--auth-key", $"{AccessKey},{SecretKey}"]);
        var (commands, site) = await SaveKindAsync(new SiteSaveRequest
        {
            Name = "S3", Kind = SiteKinds.S3, Provider = "Other", Endpoint = $"http://127.0.0.1:{port}", User = AccessKey, Password = SecretKey,
        });
        Assert.DoesNotContain(SiteCapabilities.NewFolder, site.Capabilities);
        Assert.DoesNotContain(SiteCapabilities.EmptyFolders, site.Capabilities);
        // The buckets are the top folders.
        var top = await Call<SiteBrowseResultDto>(commands, SitesMessageTypes.Browse, new SiteBrowseRequest { Id = site.Id, Path = "/" });
        Assert.True(top.Ok, top.Message);
        Assert.Contains(top.Items, item => item.Name == "pub" && item.IsFolder);
        await ExerciseAsync(commands, site.Id, makesFolders: false);

        // A wrong secret is refused by the service, and the words do not carry it.
        var wrong = await Call<SiteResultDto>(commands, SitesMessageTypes.Save, new SiteSaveRequest
        {
            Id = site.Id, Name = "S3", Kind = SiteKinds.S3, Provider = "Other", Endpoint = $"http://127.0.0.1:{port}", User = AccessKey,
            Password = "not-the-secret-4471",
        });
        Assert.True(wrong.Ok, wrong.Message);
        var test = await Call<SiteResultDto>(commands, SitesMessageTypes.Test, new SiteIdRequest { Id = site.Id });
        Assert.False(test.Ok);
        Assert.DoesNotContain("not-the-secret-4471", test.Message);
    }

    [SiteTransfersFact]
    public async Task AKindsFieldsAreCheckedOnSave()
    {
        var commands = Open(null);
        await Call<SitesStatusDto>(commands, SitesMessageTypes.SetEnabled, new SitesSetEnabledRequest { Enabled = true, OwnRclonePath = Rclone });
        var cases = new[]
        {
            new SiteSaveRequest { Name = "a", Kind = "gopher", Host = "h" },
            new SiteSaveRequest { Name = "a", Kind = SiteKinds.Ftp, Host = "h", Port = 21, User = "u", Password = "p" }, // no TLS choice
            new SiteSaveRequest { Name = "a", Kind = SiteKinds.Ftp, Host = "h", Port = 21, User = "u", AuthMethod = "key", PrivateKey = "x", Tls = SiteTlsModes.Explicit },
            new SiteSaveRequest { Name = "a", Kind = SiteKinds.WebDav, Url = "files.example.com", Vendor = "other" },
            new SiteSaveRequest { Name = "a", Kind = SiteKinds.WebDav, Url = "https://files.example.com/?x=1", Vendor = "other" },
            new SiteSaveRequest { Name = "a", Kind = SiteKinds.WebDav, Url = "https://files.example.com/", Vendor = "sharepoint" },
            new SiteSaveRequest { Name = "a", Kind = SiteKinds.S3, Provider = "Other", User = "k", Password = "s" }, // no endpoint
            new SiteSaveRequest { Name = "a", Kind = SiteKinds.S3, Provider = "AWS", Password = "s" }, // no access key
            new SiteSaveRequest { Name = "a", Kind = SiteKinds.S3, Provider = "AWS", User = "k", Password = "s", Region = "eu central" },
        };
        foreach (var request in cases)
        {
            var result = await Call<SiteResultDto>(commands, SitesMessageTypes.Save, request);
            Assert.False(result.Ok, $"{request.Kind} {request.Url}{request.Region} was saved");
        }

        // Amazon S3 needs no endpoint, and its address is s3.amazonaws.com.
        var aws = await Call<SiteResultDto>(commands, SitesMessageTypes.Save,
            new SiteSaveRequest { Name = "a", Kind = SiteKinds.S3, Provider = "AWS", User = "k", Password = "s", Region = "eu-central-1" });
        Assert.True(aws.Ok, aws.Message);
        Assert.Equal("s3.amazonaws.com", aws.Site!.Host);
        Assert.True(aws.Site.Encrypted);
        // A site's kind stays.
        var changed = await Call<SiteResultDto>(commands, SitesMessageTypes.Save,
            new SiteSaveRequest { Id = aws.Site.Id, Name = "a", Kind = SiteKinds.Ftp, Host = "h", Port = 21, Tls = SiteTlsModes.Explicit, User = "u" });
        Assert.False(changed.Ok);
    }

    private async Task<(SitesCommands Commands, SiteDto Site)> SaveKindAsync(SiteSaveRequest request, bool uploadProbe = false)
    {
        var commands = Open(uploadProbe: uploadProbe);
        var status = await Call<SitesStatusDto>(commands, SitesMessageTypes.SetEnabled, new SitesSetEnabledRequest { Enabled = true, OwnRclonePath = Rclone });
        Assert.True(status.Enabled);
        var saved = await Call<SiteResultDto>(commands, SitesMessageTypes.Save, request);
        Assert.True(saved.Ok, saved.Message);
        var test = await Call<SiteResultDto>(commands, SitesMessageTypes.Test, new SiteIdRequest { Id = saved.Site!.Id });
        Assert.True(test.Ok, test.Message);
        return (commands, saved.Site);
    }

    /// <summary>Browse, new folder (where the kind has one), rename, upload, download and delete, each checked on the served folder.</summary>
    private async Task ExerciseAsync(SitesCommands commands, string siteId, bool makesFolders)
    {
        var listed = await Call<SiteBrowseResultDto>(commands, SitesMessageTypes.Browse, new SiteBrowseRequest { Id = siteId, Path = "/pub" });
        Assert.True(listed.Ok, listed.Message);
        Assert.Contains(listed.Items, item => item.Name == "readme.txt" && item.Size == 7);
        Assert.Contains(listed.Items, item => item.Name == "docs" && item.IsFolder);

        if (makesFolders)
        {
            var made = await Call<SiteResultDto>(commands, SitesMessageTypes.MakeFolder, new SiteMakeFolderRequest { Id = siteId, Path = "/pub", Name = "made" });
            Assert.True(made.Ok, made.Message);
            Assert.True(Directory.Exists(Path.Combine(Served, "pub", "made")));
        }

        var renamed = await Call<SiteResultDto>(commands, SitesMessageTypes.Rename,
            new SiteRenameRequest { Id = siteId, Path = "/pub", Name = "readme.txt", NewName = "read me.txt" });
        Assert.True(renamed.Ok, renamed.Message);
        Assert.Equal("read me", await File.ReadAllTextAsync(Path.Combine(Served, "pub", "read me.txt")));
        Assert.False(File.Exists(Path.Combine(Served, "pub", "readme.txt")));

        var source = Path.Combine(Local, "up-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(Path.Combine(source, "photos", "empty"));
        await File.WriteAllBytesAsync(Path.Combine(source, "photos", "p1.jpg"), RandomNumberGenerator.GetBytes(200_000));
        var up = await Call<SiteTransferResultDto>(commands, SitesMessageTypes.TransferStart, new SiteTransferStartRequest
        {
            SiteId = siteId, Direction = SiteTransferDirections.Upload, RemoteFolder = "/pub/incoming", LocalFolder = source,
            Names = [new SiteTransferNameDto { Name = "photos", IsFolder = true }],
        });
        Assert.True(up.Ok, up.Message);
        var upDone = await WaitForAsync(commands, up.Transfer!.Id, t => SiteTransferStates.IsFinished(t.State));
        Assert.Equal(SiteTransferStates.Done, upDone.State);
        AssertSame(Path.Combine(source, "photos", "p1.jpg"), Path.Combine(Served, "pub", "incoming", "photos", "p1.jpg"));
        // An object store has no empty folders, so the empty one is not an item there.
        Assert.Equal(makesFolders ? 2 : 1, upDone.Files);

        var target = Path.Combine(Local, "dl-" + Guid.NewGuid().ToString("N")[..6]);
        var down = await Call<SiteTransferResultDto>(commands, SitesMessageTypes.TransferStart, new SiteTransferStartRequest
        {
            SiteId = siteId, Direction = SiteTransferDirections.Download, RemoteFolder = "/pub", LocalFolder = target,
            Names = [new SiteTransferNameDto { Name = "docs", IsFolder = true }, new SiteTransferNameDto { Name = "one-mb.bin" }],
        });
        Assert.True(down.Ok, down.Message);
        var downDone = await WaitForAsync(commands, down.Transfer!.Id, t => SiteTransferStates.IsFinished(t.State));
        Assert.Equal(SiteTransferStates.Done, downDone.State);
        AssertSame(Path.Combine(Served, "pub", "one-mb.bin"), Path.Combine(target, "one-mb.bin"));
        AssertSame(Path.Combine(Served, "pub", "docs", "deep", "b.txt"), Path.Combine(target, "docs", "deep", "b.txt"));

        var deleted = await Call<SiteChangeResultDto>(commands, SitesMessageTypes.Delete, new SiteChangeRequest
        {
            Id = siteId, Path = "/pub", Items = [new SiteChangeItemDto { Name = "read me.txt" }, new SiteChangeItemDto { Name = "incoming", IsFolder = true }],
        });
        Assert.True(deleted.Ok, deleted.Message);
        Assert.False(File.Exists(Path.Combine(Served, "pub", "read me.txt")));
        Assert.False(File.Exists(Path.Combine(Served, "pub", "incoming", "photos", "p1.jpg")));
    }
}
