using DownloadManager.Contracts;
using DownloadManager.Sites;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// SITES.5 end to end: <c>sites.mkdir</c>, <c>sites.rename</c>, <c>sites.count</c> and <c>sites.delete</c> against
/// <c>rclone serve sftp</c>, each checked on the server's own folder.
/// </summary>
public sealed partial class SiteTransfersEndToEndTests
{
    [SiteTransfersFact]
    public async Task ChronologicalBrowsePagesGloballyAndCountsOnlyDirectChildren()
    {
        var dir = Path.Combine(Served, "pub", "counts");
        Directory.CreateDirectory(Path.Combine(dir, "nested"));
        await File.WriteAllTextAsync(Path.Combine(dir, "direct.txt"), "direct");
        await File.WriteAllTextAsync(Path.Combine(dir, "nested", "not-direct.txt"), "not direct");
        var root = Path.Combine(Served, "pub");
        var early = Path.Combine(root, "z-old.txt");
        var late = Path.Combine(root, "a-new.txt");
        await File.WriteAllTextAsync(early, "old");
        await File.WriteAllTextAsync(late, "new");
        File.SetLastWriteTimeUtc(early, new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(late, new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var (commands, siteId) = await OpenSiteAsync(StartServer(slow: false));
        var request = new SiteBrowseRequest
        {
            Id = siteId, Path = "/pub", Limit = 2, ChronologicalFiles = true, IncludeFolderCounts = true
        };
        var first = await Call<SiteBrowseResultDto>(commands, SitesMessageTypes.Browse, request);
        Assert.True(first.Ok, first.Message);
        Assert.Equal(2, Assert.Single(first.Items, item => item.Name == "counts").ChildCount);
        Assert.All(first.Items, item => Assert.True(item.IsFolder));
        request.Offset = first.Items.Count;
        request.Limit = 500;
        request.Listing = first.Listing;
        var rest = await Call<SiteBrowseResultDto>(commands, SitesMessageTypes.Browse, request);
        Assert.True(rest.Ok, rest.Message);
        Assert.Equal("z-old.txt", rest.Items.First(item => !item.IsFolder).Name);
        Assert.Equal("a-new.txt", rest.Items.Last().Name);
        request.ChronologicalFiles = false;
        var mismatched = await Call<SiteBrowseResultDto>(commands, SitesMessageTypes.Browse, request);
        Assert.Equal(SiteProblems.ListingChanged, mismatched.Problem);
    }

    [SiteTransfersFact]
    public async Task ALastUsedStampAfterTheFirstPageKeepsItsListingUsable()
    {
        // Fable, merge review: the first browse stamps the remote's last-used time; that write must not make the listing
        // just returned stale, or the next page, a delete or an upload is refused as changed.
        var (commands, siteId) = await OpenSiteAsync(StartServer(slow: false));
        var first = await Call<SiteBrowseResultDto>(commands, SitesMessageTypes.Browse, new SiteBrowseRequest { Id = siteId, Path = "/pub", Limit = 1 });
        Assert.True(first.Ok, first.Message);
        for (var i = 0; i < 100; i++)
        {
            var listed = await Call<SitesListDto>(commands, SitesMessageTypes.ListRequest, new { });
            if (listed.Sites.Single(site => site.Id == siteId).LastUsedAt is not null) break;
            await Task.Delay(50);
        }

        Assert.NotNull((await Call<SitesListDto>(commands, SitesMessageTypes.ListRequest, new { })).Sites.Single(site => site.Id == siteId).LastUsedAt);
        var more = await Call<SiteBrowseResultDto>(commands, SitesMessageTypes.Browse,
            new SiteBrowseRequest { Id = siteId, Path = "/pub", Offset = first.Items.Count, Limit = 500, Listing = first.Listing });
        Assert.True(more.Ok, more.Message);
        var deleted = await Call<SiteChangeResultDto>(commands, SitesMessageTypes.DeleteFiles, new SiteChangeRequest
        {
            Id = siteId, Path = "/pub", Listing = first.Listing, Items = [new() { Name = "readme.txt" }]
        });
        Assert.True(deleted.Ok, deleted.Message);
        Assert.False(File.Exists(Path.Combine(Served, "pub", "readme.txt")));
    }

    [SiteTransfersFact]
    public async Task FileOnlyDeleteLeavesAFolderAndItsContentsAlone()
    {
        var (commands, siteId) = await OpenSiteAsync(StartServer(slow: false));
        string docs = Path.Combine(Served, "pub", "docs");
        string moved = Path.Combine(Served, "saved-docs");
        Directory.Move(docs, moved);
        await File.WriteAllTextAsync(docs, "initially a file");
        var listed = await Call<SiteBrowseResultDto>(commands, SitesMessageTypes.Browse,
            new SiteBrowseRequest { Id = siteId, Path = "/pub" });
        Assert.True(listed.Ok, listed.Message);
        Assert.False(Assert.Single(listed.Items, item => item.Name == "docs").IsFolder);
        File.Delete(docs);
        Directory.Move(moved, docs);
        var result = await Call<SiteChangeResultDto>(commands, SitesMessageTypes.DeleteFiles, new SiteChangeRequest
        {
            Id = siteId, Path = "/pub", Listing = listed.Listing,
            Items = [new() { Name = "docs" }, new() { Name = "readme.txt" }]
        });
        Assert.False(result.Ok);
        Assert.False(Assert.Single(result.Items, item => item.Name == "docs").Ok);
        Assert.True(Assert.Single(result.Items, item => item.Name == "readme.txt").Ok);
        Assert.True(File.Exists(Path.Combine(Served, "pub", "docs", "deep", "b.txt")));
        Assert.False(File.Exists(Path.Combine(Served, "pub", "readme.txt")));
    }

    [SiteTransfersFact]
    public async Task AFirstPageAskedAgainWithItsListingComesFromTheHeldListing()
    {
        var (commands, siteId) = await OpenSiteAsync(StartServer(slow: false));
        var first = await Call<SiteBrowseResultDto>(commands, SitesMessageTypes.Browse, new SiteBrowseRequest { Id = siteId, Path = "/pub" });
        Assert.True(first.Ok, first.Message);
        // The counts follow-up (review P1): the same listing, not a new one read from the server.
        var again = await Call<SiteBrowseResultDto>(commands, SitesMessageTypes.Browse,
            new SiteBrowseRequest { Id = siteId, Path = "/pub", Listing = first.Listing, IncludeFolderCounts = true });
        Assert.True(again.Ok, again.Message);
        Assert.Equal(first.Listing, again.Listing);
        Assert.Equal(first.Items.Select(i => i.Name), again.Items.Select(i => i.Name));
        // Refresh, or a listing the Host no longer holds, reads the server again.
        var refreshed = await Call<SiteBrowseResultDto>(commands, SitesMessageTypes.Browse,
            new SiteBrowseRequest { Id = siteId, Path = "/pub", Listing = first.Listing, Refresh = true });
        Assert.NotEqual(first.Listing, refreshed.Listing);
        var unknown = await Call<SiteBrowseResultDto>(commands, SitesMessageTypes.Browse,
            new SiteBrowseRequest { Id = siteId, Path = "/pub", Listing = "0123456789abcdef0123456789abcdef" });
        Assert.True(unknown.Ok, unknown.Message);
        Assert.NotEqual("0123456789abcdef0123456789abcdef", unknown.Listing);
    }

    [SiteTransfersFact]
    public async Task BackToAFolderShownBeforeUsesItsHeldListingWithItsCounts()
    {
        Directory.CreateDirectory(Path.Combine(Served, "pub", "inner", "deeper"));
        var (commands, siteId) = await OpenSiteAsync(StartServer(slow: false));
        var top = await Call<SiteBrowseResultDto>(commands, SitesMessageTypes.Browse,
            new SiteBrowseRequest { Id = siteId, Path = "/pub", IncludeFolderCounts = true });
        Assert.True(top.Ok, top.Message);
        Assert.Equal(1, Assert.Single(top.Items, item => item.Name == "inner").ChildCount);
        var inner = await Call<SiteBrowseResultDto>(commands, SitesMessageTypes.Browse, new SiteBrowseRequest { Id = siteId, Path = "/pub/inner" });
        Assert.True(inner.Ok, inner.Message);

        // Back (review P5): the top folder is still held after the inner one was read, counts included.
        File.WriteAllText(Path.Combine(Served, "pub", "added-later.txt"), "x");
        var back = await Call<SiteBrowseResultDto>(commands, SitesMessageTypes.Browse,
            new SiteBrowseRequest { Id = siteId, Path = "/pub", Listing = top.Listing });
        Assert.True(back.Ok, back.Message);
        Assert.Equal(top.Listing, back.Listing);
        Assert.DoesNotContain(back.Items, item => item.Name == "added-later.txt");
        Assert.Equal(1, Assert.Single(back.Items, item => item.Name == "inner").ChildCount);

        // A change through the Host forgets what it held: the next look reads the server.
        var made = await Call<SiteResultDto>(commands, SitesMessageTypes.MakeFolder, new SiteMakeFolderRequest { Id = siteId, Path = "/pub", Name = "made" });
        Assert.True(made.Ok, made.Message);
        var after = await Call<SiteBrowseResultDto>(commands, SitesMessageTypes.Browse,
            new SiteBrowseRequest { Id = siteId, Path = "/pub", Listing = top.Listing });
        Assert.NotEqual(top.Listing, after.Listing);
        Assert.Contains(after.Items, item => item.Name == "added-later.txt");
    }

    [SiteTransfersFact]
    public async Task FileOnlyDeleteRejectsAStaleListingAfterAnEndpointEdit()
    {
        int port = StartServer(slow: false);
        var (commands, siteId) = await OpenSiteAsync(port);
        var listed = await Call<SiteBrowseResultDto>(commands, SitesMessageTypes.Browse,
            new SiteBrowseRequest { Id = siteId, Path = "/pub" });
        Assert.True(listed.Ok, listed.Message);
        var edited = await Call<SiteResultDto>(commands, SitesMessageTypes.Save, new SiteSaveRequest
        {
            Id = siteId, Name = "Edited location", Kind = SiteKinds.Sftp, Host = "localhost", Port = port,
            // Another endpoint takes the password again: a saved one is only sent to the server it was saved for.
            User = User, Password = Password, RemoteFolder = "/pub"
        });
        Assert.True(edited.Ok, edited.Message);
        var result = await Call<SiteChangeResultDto>(commands, SitesMessageTypes.DeleteFiles, new SiteChangeRequest
        {
            Id = siteId, Path = "/pub", Listing = listed.Listing,
            Items = [new() { Name = "readme.txt" }]
        });
        Assert.False(result.Ok);
        Assert.Equal(SiteProblems.ListingChanged, result.Problem);
        Assert.True(File.Exists(Path.Combine(Served, "pub", "readme.txt")));
    }

    [SiteTransfersFact]
    public async Task NewFolderRenameCountAndDeleteChangeTheServer()
    {
        var port = StartServer(slow: false);
        var (commands, siteId) = await OpenSiteAsync(port);

        // A new folder; a second one of the same name is refused; bad names are refused before anything is sent.
        var made = await Call<SiteResultDto>(commands, SitesMessageTypes.MakeFolder, new SiteMakeFolderRequest { Id = siteId, Path = "/pub", Name = "made" });
        Assert.True(made.Ok, made.Message);
        Assert.True(Directory.Exists(Path.Combine(Served, "pub", "made")));
        var again = await Call<SiteResultDto>(commands, SitesMessageTypes.MakeFolder, new SiteMakeFolderRequest { Id = siteId, Path = "/pub", Name = "made" });
        Assert.False(again.Ok);
        Assert.Contains("already there", again.Message);
        foreach (var bad in new[] { "", "  ", "a/b", "..", "x\ny" })
        {
            var refused = await Call<SiteResultDto>(commands, SitesMessageTypes.MakeFolder, new SiteMakeFolderRequest { Id = siteId, Path = "/pub", Name = bad });
            Assert.False(refused.Ok, bad);
        }

        // A file renamed; onto an existing name refused; a folder with files inside renamed whole.
        var renamed = await Call<SiteResultDto>(commands, SitesMessageTypes.Rename,
            new SiteRenameRequest { Id = siteId, Path = "/pub", Name = "readme.txt", NewName = "read me.txt" });
        Assert.True(renamed.Ok, renamed.Message);
        Assert.False(File.Exists(Path.Combine(Served, "pub", "readme.txt")));
        Assert.Equal("read me", await File.ReadAllTextAsync(Path.Combine(Served, "pub", "read me.txt")));
        var onto = await Call<SiteResultDto>(commands, SitesMessageTypes.Rename,
            new SiteRenameRequest { Id = siteId, Path = "/pub", Name = "read me.txt", NewName = "one-mb.bin" });
        Assert.False(onto.Ok);
        Assert.Contains("already there", onto.Message);
        Assert.True(File.Exists(Path.Combine(Served, "pub", "read me.txt")));
        // A change of case only on a server that ignores case (this one serves a Windows folder): the item itself is not "already there".
        var cased = await Call<SiteResultDto>(commands, SitesMessageTypes.Rename,
            new SiteRenameRequest { Id = siteId, Path = "/pub", Name = "read me.txt", NewName = "Read me.txt" });
        Assert.True(cased.Ok, cased.Message);
        Assert.Contains("Read me.txt", Directory.GetFiles(Path.Combine(Served, "pub")).Select(Path.GetFileName));
        Assert.True((await Call<SiteResultDto>(commands, SitesMessageTypes.Rename,
            new SiteRenameRequest { Id = siteId, Path = "/pub", Name = "Read me.txt", NewName = "read me.txt" })).Ok);
        var folder = await Call<SiteResultDto>(commands, SitesMessageTypes.Rename,
            new SiteRenameRequest { Id = siteId, Path = "/pub", Name = "docs", NewName = "papers" });
        Assert.True(folder.Ok, folder.Message);
        Assert.False(Directory.Exists(Path.Combine(Served, "pub", "docs")));
        Assert.True(File.Exists(Path.Combine(Served, "pub", "papers", "a.txt")));
        Assert.True(File.Exists(Path.Combine(Served, "pub", "papers", "deep", "b.txt")));
        var gone = await Call<SiteResultDto>(commands, SitesMessageTypes.Rename,
            new SiteRenameRequest { Id = siteId, Path = "/pub", Name = "docs", NewName = "x" });
        Assert.False(gone.Ok);

        // The count for the delete dialog: the files inside a folder count, and the bytes; a name no longer there is said.
        var items = new List<SiteChangeItemDto>
        {
            new() { Name = "papers", IsFolder = true },
            new() { Name = "one-mb.bin" },
            new() { Name = "made", IsFolder = true },
            new() { Name = "not-there" },
        };
        var count = await Call<SiteCountResultDto>(commands, SitesMessageTypes.Count, new SiteChangeRequest { Id = siteId, Path = "/pub", Items = items });
        Assert.True(count.Ok, count.Message);
        Assert.Equal(2, count.Folders);
        Assert.Equal(3, count.Files); // papers/a.txt, papers/deep/b.txt, one-mb.bin
        var expectedBytes = new FileInfo(Path.Combine(Served, "pub", "papers", "a.txt")).Length
            + new FileInfo(Path.Combine(Served, "pub", "papers", "deep", "b.txt")).Length + 1024 * 1024;
        Assert.Equal(expectedBytes, count.Bytes);
        Assert.Equal(1, count.Missing);

        // Delete: the folder with all inside, the file, the empty folder; the missing name is fine.
        var deleted = await Call<SiteChangeResultDto>(commands, SitesMessageTypes.Delete, new SiteChangeRequest { Id = siteId, Path = "/pub", Items = items });
        Assert.True(deleted.Ok, deleted.Message);
        Assert.Equal(4, deleted.Items.Count);
        Assert.All(deleted.Items, i => Assert.True(i.Ok, i.Message));
        Assert.False(Directory.Exists(Path.Combine(Served, "pub", "papers")));
        Assert.False(File.Exists(Path.Combine(Served, "pub", "one-mb.bin")));
        Assert.False(Directory.Exists(Path.Combine(Served, "pub", "made")));
        Assert.True(File.Exists(Path.Combine(Served, "pub", "read me.txt")));

        // A listing read before the change is not paged after it.
        var browse = await Call<SiteBrowseResultDto>(commands, SitesMessageTypes.Browse, new SiteBrowseRequest { Id = siteId, Path = "/pub" });
        Assert.True(browse.Ok, browse.Message);
        Assert.Equal(["read me.txt", "slow.bin"], browse.Items.Select(i => i.Name).Order(StringComparer.Ordinal));
    }

    [SiteTransfersFact]
    public async Task NothingARunningTransferUsesIsRenamedOrDeleted()
    {
        var port = StartServer(slow: true);
        var (commands, siteId) = await OpenSiteAsync(port);
        await File.WriteAllBytesAsync(Path.Combine(Served, "pub", "docs", "slow.bin"),
            System.Security.Cryptography.RandomNumberGenerator.GetBytes((int)(6 * SlowRate)));
        var started = await StartAsync(commands, siteId, Path.Combine(Local, "busy"), SiteTransferConflicts.Skip, "docs");
        await WaitForAsync(commands, started.Id, t => t.FilesRunning > 0);

        foreach (var (path, name) in new[] { ("/pub", "docs"), ("/pub/docs", "slow.bin"), ("/", "pub") })
        {
            var rename = await Call<SiteResultDto>(commands, SitesMessageTypes.Rename,
                new SiteRenameRequest { Id = siteId, Path = path, Name = name, NewName = name + "-x" });
            Assert.False(rename.Ok, $"{path}/{name}");
            Assert.Contains("transfer", rename.Message);
            var delete = await Call<SiteChangeResultDto>(commands, SitesMessageTypes.Delete,
                new SiteChangeRequest { Id = siteId, Path = path, Items = [new SiteChangeItemDto { Name = name }] });
            Assert.False(delete.Ok, $"{path}/{name}");
            Assert.Empty(delete.Items);
        }

        // Something the batch does not touch can go.
        var other = await Call<SiteChangeResultDto>(commands, SitesMessageTypes.Delete,
            new SiteChangeRequest { Id = siteId, Path = "/pub", Items = [new SiteChangeItemDto { Name = "readme.txt" }] });
        Assert.True(other.Ok, other.Message);
        Assert.True(File.Exists(Path.Combine(Served, "pub", "docs", "slow.bin")));

        // Once canceled, the folder can be renamed.
        var canceled = await Call<SiteTransferResultDto>(commands, SitesMessageTypes.TransferCancel, new SiteTransferIdRequest { Id = started.Id });
        Assert.True(canceled.Ok, canceled.Message);
        var after = await Call<SiteResultDto>(commands, SitesMessageTypes.Rename,
            new SiteRenameRequest { Id = siteId, Path = "/pub", Name = "docs", NewName = "docs-later" });
        Assert.True(after.Ok, after.Message);
    }
}
