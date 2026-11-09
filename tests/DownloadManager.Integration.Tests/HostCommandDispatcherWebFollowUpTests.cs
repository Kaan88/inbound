using DownloadManager.Containers.Tests;
using DownloadManager.Contracts;
using DownloadManager.Core;
using DownloadManager.Persistence;
using DownloadManager.Runtime;
using DownloadManager.Runtime.Archives;
using DownloadManager.Runtime.Ipc;
using DownloadManager.Runtime.Remote;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// What the web UI asked of the Host after D2c, for web sessions on the desktop Host (the Docker server answers through
/// the same dispatcher): the speed history, the archive capability, web adds that open no window, refreshing a finished
/// download's address, archive files in the folder browser, uploaded containers, and hidden addresses redacted by the Host.
/// Nothing here makes a network call.
/// </summary>
public sealed partial class HostCommandDispatcherTests
{
    [Fact]
    public async Task AWebSessionGetsTheSpeedHistoryThePipeGets()
    {
        var web = await WebAsync();

        var fromWeb = await AskAsync(web.Dispatcher, web.Session, MessageTypes.SpeedHistoryGet, new { });
        Assert.Equal(MessageTypes.SpeedHistoryResult, fromWeb.Type);
        var history = fromWeb.GetPayload<SpeedHistoryDto>();
        Assert.Equal(1, history.IntervalSeconds);
        Assert.NotEmpty(history.BytesPerSecond);
        var fromPipe = (await AskAsync(web.Dispatcher, web.Pipe, MessageTypes.SpeedHistoryGet, new { })).GetPayload<SpeedHistoryDto>();
        Assert.Equal(fromPipe.Start, history.Start);
    }

    [Fact]
    public async Task TheHelloNamesArchivesOnlyWhereTheArchiveCommandsAreAnsweredAndAnExtractorIsThere()
    {
        var service = await ServiceAsync();
        await using var archives = new ArchiveCoordinator(service, new JsonArchiveStore(_paths),
            options: new ArchiveCoordinatorOptions { ExtractorFactory = _ => new FakeExtractor() });
        await archives.InitializeAsync();
        var web = await WebAsync(archives: archives, service: service);

        Assert.Equal(["jobs", "settings", "handoff", "tray", "status", "folders", "archives"], web.Session.Frames[0].GetPayload<HostHelloDto>().Capabilities);
        // This test's dispatcher has no archive commands for the pipe; the Host's own has them.
        Assert.DoesNotContain("archives", web.Pipe.Frames[0].GetPayload<HostHelloDto>().Capabilities);
        var withArchives = new HostCommandDispatcher(service, new FakePlatform(PlatformCapabilities.Desktop, _timeline), new ListLog())
        {
            Archives = new ArchiveIpcDispatcher(archives),
        };
        Assert.Equal("archives", withArchives.Capabilities[^1]);

        // Without extraction neither has it.
        var without = await WebAsync(service: service);
        Assert.DoesNotContain("archives", without.Session.Frames[0].GetPayload<HostHelloDto>().Capabilities);

        // Extraction started but no 7-Zip there: the commands are answered, the archive boxes aren't offered.
        await using var noTool = new ArchiveCoordinator(service, new JsonArchiveStore(new StoragePaths(Path.Combine(_root, "no-tool"))),
            options: new ArchiveCoordinatorOptions { SevenZipPath = Path.Combine(_root, "missing", "7z.exe") });
        await noTool.InitializeAsync();
        Assert.False(noTool.CanExtract);
        var toolless = new HostCommandDispatcher(service, new FakePlatform(PlatformCapabilities.Desktop, _timeline), new ListLog())
        {
            Archives = new ArchiveIpcDispatcher(noTool),
        };
        Assert.DoesNotContain("archives", toolless.Capabilities);
    }

    [Fact]
    public async Task TheHelloNamesUploadsOnlyWhereUploadedFilesAreReadByTheirReference()
    {
        var service = await ServiceAsync();
        var registry = new RemoteProviderRegistry();
        await using var jobs = new RemoteJobCoordinator(registry, new JsonRemoteJobStore(_paths), service, new RemoteCoordinatorOptions { RunLoops = false });
        var without = await WebAsync(providers: registry, remoteJobs: jobs, service: service);
        var names = without.Session.Frames[0].GetPayload<HostHelloDto>().Capabilities;
        Assert.Contains("remote", names);
        Assert.DoesNotContain("uploads", names);

        jobs.ResolveReference = UploadFolder.Prepare(_paths.Root).Resolve;
        var with = await WebAsync(providers: registry, remoteJobs: jobs, service: service);
        Assert.Equal("uploads", with.Session.Frames[0].GetPayload<HostHelloDto>().Capabilities[^1]);
    }

    [Fact]
    public async Task WhatAWebSessionAddsIsMarkedAsFromAnotherDeviceAndOpensNoWindowOnThisPc()
    {
        var web = await WebAsync();

        foreach (var (source, kept) in new (string?, string)[] { (null, "cli"), ("api", "api"), ("import", "import"), ("chromeAuto", "chromeAuto"), ("web", "api") })
        {
            var request = Later("https://203.0.113.10/" + (source ?? "none") + ".bin", Inside, (source ?? "none") + ".bin");
            request.Source = source!; // null on the wire: no source named
            var added = (await AskAsync(web.Dispatcher, web.Session, MessageTypes.JobAdd, request)).GetPayload<AddJobResponse>();
            Assert.True(added.Accepted, added.Error);
            // Its source stays the one it named; the mark beside it keeps the windows away.
            Assert.Equal(kept, added.Job!.Source);
            Assert.True(added.Job.AddedFromAnotherDevice);
            Assert.False(DownloadWindows.OpensWindows(added.Job));
            Assert.True((await StoredAsync(added.Job.Id)).AddedFromAnotherDevice);
        }

        // A hand-off through the bridge too.
        var handoff = (await AskAsync(web.Dispatcher, web.Session, MessageTypes.HandoffRequest, new HandoffRequest
        {
            RequestId = "web-handoff-1",
            Url = "https://203.0.113.10/handed.bin",
            DestinationDirectory = Inside,
            StartMode = "later",
            IsAutoIntercept = true,
        })).GetPayload<HandoffAck>();
        Assert.True(handoff.Accepted, handoff.Error);
        Assert.Equal("chromeAuto", handoff.Job!.Source);
        Assert.True(handoff.Job.AddedFromAnotherDevice);
        Assert.False(DownloadWindows.OpensWindows(handoff.Job));

        // The user at this PC: windows as before. A client there naming "web" gets an app add, not the mark.
        var localRequest = Later("https://203.0.113.10/local.bin", Inside, "local.bin");
        localRequest.Source = "api";
        var local = (await AskAsync(web.Dispatcher, web.Pipe, MessageTypes.JobAdd, localRequest)).GetPayload<AddJobResponse>();
        Assert.Equal("api", local.Job!.Source);
        Assert.False(local.Job.AddedFromAnotherDevice);
        Assert.True(DownloadWindows.OpensWindows(local.Job));
        var named = Later("https://203.0.113.10/named.bin", Inside, "named.bin");
        named.Source = "web";
        var namedJob = (await AskAsync(web.Dispatcher, web.Pipe, MessageTypes.JobAdd, named)).GetPayload<AddJobResponse>().Job!;
        Assert.Equal("api", namedJob.Source);
        Assert.False(namedJob.AddedFromAnotherDevice);
        Assert.True(DownloadWindows.OpensWindows(namedJob));
    }

    [Fact]
    public async Task AJobAddedFromAnotherDeviceIsSavedSoAnOlderBuildStillLoadsIt()
    {
        var web = await WebAsync();
        var request = Later("https://203.0.113.10/kept.bin", Inside, "kept.bin");
        request.Source = "web";
        var added = (await AskAsync(web.Dispatcher, web.Session, MessageTypes.JobAdd, request)).GetPayload<AddJobResponse>();
        Assert.True(added.Accepted, added.Error);

        // The file names a source every build reads, and the mark as a property of its own.
        var file = Path.Combine(_paths.JobsDirectory, $"{added.Job!.Id:N}.json");
        var json = File.ReadAllText(file);
        using (var saved = System.Text.Json.JsonDocument.Parse(json))
        {
            Assert.Equal("api", saved.RootElement.GetProperty("source").GetString());
            Assert.True(saved.RootElement.GetProperty("addedFromAnotherDevice").GetBoolean());
        }

        // As an older build reads it: a property it doesn't know is skipped, and the job loads...
        var older = new StoragePaths(Path.Combine(_root, "older-build"));
        var olderStore = new JsonJobStore(older);
        await olderStore.InitializeAsync();
        File.WriteAllText(Path.Combine(older.JobsDirectory, $"{added.Job.Id:N}.json"),
            json.Replace("\"addedFromAnotherDevice\"", "\"aPropertyFromALaterBuild\"", StringComparison.Ordinal));
        Assert.Equal([added.Job.Id], (await olderStore.LoadAllAsync()).Select(job => job.Id));
        // ...while a source it doesn't know sets the job aside, which is why the mark isn't a source.
        var unknown = Guid.NewGuid();
        var withUnknownSource = json.Replace(added.Job.Id.ToString(), unknown.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("\"source\": \"api\"", "\"source\": \"aSourceFromALaterBuild\"", StringComparison.Ordinal);
        Assert.Contains("aSourceFromALaterBuild", withUnknownSource);
        File.WriteAllText(Path.Combine(older.JobsDirectory, $"{unknown:N}.json"), withUnknownSource);
        Assert.DoesNotContain(unknown, (await olderStore.LoadAllAsync()).Select(job => job.Id));
        Assert.Single(Directory.GetFiles(older.CorruptDirectory));
    }

    private DownloadJob FinishedJob(string folder, string name, JobCompletionOptions? options = null)
    {
        var job = SeededJob(folder, name);
        job.State = DownloadState.Completed;
        job.CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        job.TotalBytes = 4;
        job.ReceivedBytes = 4;
        job.PartFilePath = string.Empty;
        if (options is not null) job.CompletionOptions = options;
        Directory.CreateDirectory(folder);
        File.WriteAllText(job.FinalFilePath, "done");
        return job;
    }

    [Fact]
    public async Task AFinishedDownloadsAddressCanBeRefreshedAndItStaysFinishedWithItsFile()
    {
        var finished = FinishedJob(Inside, "finished.bin");
        var outside = FinishedJob(Outside, "outside.bin");
        var opens = FinishedJob(Inside, "opens.bin", new JobCompletionOptions { OpenFile = true });
        var web = await WebAsync(async store =>
        {
            await store.SaveAsync(finished);
            await store.SaveAsync(outside);
            await store.SaveAsync(opens);
        });
        var completedAt = web.Service.GetJob(finished.Id)!.CompletedAt;
        var changes = new List<(Guid Id, string Change)>();
        web.Service.JobChanged += (_, e) => { lock (changes) changes.Add((e.Job.Id, e.ChangeType)); };

        var fromPipe = (await AskAsync(web.Dispatcher, web.Pipe, MessageTypes.JobRefreshUrl,
            new JobRefreshUrlRequest { JobId = finished.Id, Url = "https://203.0.113.20/finished.bin" })).GetPayload<JobActionResponse>();
        Assert.True(fromPipe.Ok, fromPipe.Error);
        Assert.Equal("completed", fromPipe.Job!.State);
        Assert.Equal("https://203.0.113.20/finished.bin", fromPipe.Job.Url);
        Assert.Equal(completedAt, fromPipe.Job.CompletedAt);
        var stored = await StoredAsync(finished.Id);
        Assert.Equal(DownloadState.Completed, stored.State);
        Assert.Equal("https://203.0.113.20/finished.bin", stored.Url);
        Assert.False(stored.PublicNetworkOnly);
        Assert.Equal("done", File.ReadAllText(finished.FinalFilePath));
        // Told as a change, not a restart.
        lock (changes) Assert.Equal([(finished.Id, "updated")], changes);

        // A web session may too, for one inside the app's folders; the download then stays off this PC's network.
        var fromWeb = (await AskAsync(web.Dispatcher, web.Session, MessageTypes.JobRefreshUrl,
            new JobRefreshUrlRequest { JobId = finished.Id, Url = "https://203.0.113.30/finished.bin" })).GetPayload<JobActionResponse>();
        Assert.True(fromWeb.Ok, fromWeb.Error);
        Assert.Equal("completed", fromWeb.Job!.State);
        Assert.True((await StoredAsync(finished.Id)).PublicNetworkOnly);

        // Every stage-A rule still holds for it.
        var local = (await AskAsync(web.Dispatcher, web.Session, MessageTypes.JobRefreshUrl,
            new JobRefreshUrlRequest { JobId = finished.Id, Url = "http://192.168.1.10/finished.bin" })).GetPayload<JobActionResponse>();
        Assert.Equal(CallerScope.LocalAddressRefused, local.Error);
        var outsideRefused = (await AskAsync(web.Dispatcher, web.Session, MessageTypes.JobRefreshUrl,
            new JobRefreshUrlRequest { JobId = outside.Id, Url = "https://203.0.113.30/outside.bin" })).GetPayload<JobActionResponse>();
        Assert.Equal(CallerScope.OutsideJobRefused, outsideRefused.Error);
        var opensRefused = (await AskAsync(web.Dispatcher, web.Session, MessageTypes.JobRefreshUrl,
            new JobRefreshUrlRequest { JobId = opens.Id, Url = "https://203.0.113.30/opens.bin" })).GetPayload<JobActionResponse>();
        Assert.Equal(CallerScope.CompletionActionsKeepAddress, opensRefused.Error);
        Assert.Equal(outside.Url, web.Service.GetJob(outside.Id)!.Url);
        Assert.Equal(opens.Url, web.Service.GetJob(opens.Id)!.Url);
    }

    /// <summary>Answers for the provider's rules without asking anyone, and counts what it was asked.</summary>
    private sealed class RecordingAddressPolicy(string refused) : IDirectAddressPolicy
    {
        public List<string> Asked { get; } = [];

        public Task<string?> WhyNotDirectAsync(string address, bool explicitDirect, CancellationToken cancellationToken = default)
        {
            lock (Asked) Asked.Add(address);
            return Task.FromResult(address == refused ? "A provider handles this site." : null);
        }
    }

    [Fact]
    public async Task AFinishedDownloadsNewAddressIsCheckedAsAnyOtherSoAProviderLinkCantBecomeItsAddress()
    {
        var finished = FinishedJob(Inside, "checked.bin");
        var web = await WebAsync(async store => await store.SaveAsync(finished));
        var policy = new RecordingAddressPolicy("https://hoster.example/file/abc");
        web.Service.DirectAddresses = policy;

        var refused = (await AskAsync(web.Dispatcher, web.Pipe, MessageTypes.JobRefreshUrl,
            new JobRefreshUrlRequest { JobId = finished.Id, Url = "https://hoster.example/file/abc" })).GetPayload<JobActionResponse>();
        Assert.False(refused.Ok);
        Assert.Equal("A provider handles this site.", refused.Error);
        Assert.Equal(finished.Url, web.Service.GetJob(finished.Id)!.Url);
        Assert.Equal("completed", web.Service.GetJob(finished.Id)!.State);

        var allowed = (await AskAsync(web.Dispatcher, web.Pipe, MessageTypes.JobRefreshUrl,
            new JobRefreshUrlRequest { JobId = finished.Id, Url = "https://203.0.113.40/checked.bin" })).GetPayload<JobActionResponse>();
        Assert.True(allowed.Ok, allowed.Error);
        Assert.Equal("completed", allowed.Job!.State);
        Assert.Equal("https://203.0.113.40/checked.bin", allowed.Job.Url);
        lock (policy.Asked) Assert.Equal(["https://hoster.example/file/abc", "https://203.0.113.40/checked.bin"], policy.Asked);

        // The address it already has isn't asked about again.
        var same = (await AskAsync(web.Dispatcher, web.Pipe, MessageTypes.JobRefreshUrl,
            new JobRefreshUrlRequest { JobId = finished.Id, Url = "https://203.0.113.40/checked.bin" })).GetPayload<JobActionResponse>();
        Assert.True(same.Ok, same.Error);
        lock (policy.Asked) Assert.Equal(2, policy.Asked.Count);
    }

    [Fact]
    public async Task TheFolderBrowserListsArchiveFilesOnlyWhenAskedAndOnlyInsideTheFolders()
    {
        var web = await WebAsync();
        Directory.CreateDirectory(Path.Combine(Inside, "sub"));
        foreach (var name in new[] { "b.part1.rar", "a.zip", "c.tar.gz", "d.7z.001", "notes.txt", "movie.mkv",
                     "backup.sql.gz", "movie.mkv.001", "d.7z.002", "e.r00", ".zip" })
            File.WriteAllText(Path.Combine(Inside, name), "x");
        File.WriteAllText(Path.Combine(Outside, "outside.rar"), "x");

        var listed = (await AskAsync(web.Dispatcher, web.Session, MessageTypes.FoldersBrowse,
            new FolderBrowseRequest { Path = Inside, Files = FolderBrowseFiles.Archives })).GetPayload<FolderBrowseResult>();
        Assert.True(listed.Ok, listed.Error);
        Assert.Equal(["a.zip", "b.part1.rar", "c.tar.gz", "d.7z.001"], listed.Files.Select(f => f.Name));
        Assert.Equal(Path.Combine(listed.Path!, "a.zip"), listed.Files[0].Path);
        Assert.Equal(1, listed.Files[0].Size);
        Assert.Equal(["sub"], listed.Folders.Select(f => f.Name));

        var folders = (await AskAsync(web.Dispatcher, web.Session, MessageTypes.FoldersBrowse, new FolderBrowseRequest { Path = Inside })).GetPayload<FolderBrowseResult>();
        Assert.Empty(folders.Files);
        var roots = (await AskAsync(web.Dispatcher, web.Session, MessageTypes.FoldersBrowse, new FolderBrowseRequest { Files = FolderBrowseFiles.Archives })).GetPayload<FolderBrowseResult>();
        Assert.Empty(roots.Files);

        var outside = (await AskAsync(web.Dispatcher, web.Session, MessageTypes.FoldersBrowse,
            new FolderBrowseRequest { Path = Outside, Files = FolderBrowseFiles.Archives })).GetPayload<FolderBrowseResult>();
        Assert.False(outside.Ok);
        Assert.Empty(outside.Files);
        var unc = (await AskAsync(web.Dispatcher, web.Session, MessageTypes.FoldersBrowse,
            new FolderBrowseRequest { Path = @"\\attacker.invalid\share", Files = FolderBrowseFiles.Archives })).GetPayload<FolderBrowseResult>();
        Assert.False(unc.Ok);
        var other = (await AskAsync(web.Dispatcher, web.Session, MessageTypes.FoldersBrowse,
            new FolderBrowseRequest { Path = Inside, Files = "all" })).GetPayload<FolderBrowseResult>();
        Assert.False(other.Ok);
        Assert.Equal("Only archive files can be listed.", other.Error);
    }

    [Fact]
    public async Task AWebSessionPreviewsAnUploadedContainerByItsReferenceButCantTypeTheUploadFoldersPath()
    {
        var service = await ServiceAsync();
        var registry = new RemoteProviderRegistry();
        await using var jobs = new RemoteJobCoordinator(registry, new JsonRemoteJobStore(_paths), service, new RemoteCoordinatorOptions { RunLoops = false });
        var uploads = UploadFolder.Prepare(_paths.Root);
        jobs.ResolveReference = uploads.Resolve;
        var web = await WebAsync(providers: registry, remoteJobs: jobs, service: service);

        using var body = new MemoryStream(ContainerFixtures.Rsdf("https://203.0.113.10/a.bin", "https://203.0.113.10/b.bin"));
        var reference = await uploads.SaveAsync(body, ".rsdf", 16 * 1024 * 1024, CancellationToken.None);
        Assert.Matches("^upload:[0-9a-f]{32}$", reference);

        var preview = (await AskAsync(web.Dispatcher, web.Session, MessageTypes.RemoteContainerPreview,
            new RemoteContainerPreviewRequest { Path = reference, FileName = "My links.rsdf" })).GetPayload<RemoteContainerPreviewResult>();
        Assert.Null(preview.Error);
        Assert.Equal("rsdf", preview.Format);
        Assert.Equal(2, preview.Entries.Count);
        Assert.False(preview.CanSendToJd);

        // Its path, typed, is outside the app's folders; so is anything else there.
        var typed = (await AskAsync(web.Dispatcher, web.Session, MessageTypes.RemoteContainerPreview,
            new RemoteContainerPreviewRequest { Path = uploads.Resolve(reference)! })).GetPayload<RemoteContainerPreviewResult>();
        Assert.Equal("That file is outside the app's folders.", typed.Error);
        // A reference that names no upload (or no longer does) says so; a torrent's reads nothing.
        var unknown = (await AskAsync(web.Dispatcher, web.Session, MessageTypes.RemoteContainerPreview,
            new RemoteContainerPreviewRequest { Path = "upload:" + new string('0', 32) })).GetPayload<RemoteContainerPreviewResult>();
        Assert.Equal(UploadFolder.GoneText, unknown.Error);
        File.Delete(uploads.Resolve(reference)!);
        var gone = (await AskAsync(web.Dispatcher, web.Session, MessageTypes.RemoteContainerPreview,
            new RemoteContainerPreviewRequest { Path = reference })).GetPayload<RemoteContainerPreviewResult>();
        Assert.Equal(UploadFolder.GoneText, gone.Error);
        using var torrentBody = new MemoryStream("d4:infod4:name1:aee"u8.ToArray());
        var torrent = await uploads.SaveAsync(torrentBody, UploadFolder.TorrentExtension, 1024, CancellationToken.None);
        var notContainer = (await AskAsync(web.Dispatcher, web.Session, MessageTypes.RemoteContainerPreview,
            new RemoteContainerPreviewRequest { Path = torrent })).GetPayload<RemoteContainerPreviewResult>();
        Assert.Equal("Only .rsdf, .ccf and .dlc files can be imported.", notContainer.Error);
    }

    [Fact]
    public async Task TheUploadFolderKeepsWholeFilesUnderReferencesOnly()
    {
        var uploads = UploadFolder.Prepare(Path.Combine(_root, "upload-data"));
        Assert.Null(uploads.Resolve("C:\\file.torrent"));
        Assert.Null(uploads.Resolve("upload:../../x"));
        Assert.Null(uploads.Resolve("upload:" + new string('A', 32)));
        // Well formed, but nothing there.
        Assert.Null(uploads.Resolve("upload:" + new string('0', 32)));

        using (var big = new MemoryStream(new byte[2048]))
            await Assert.ThrowsAsync<UploadTooLargeException>(() => uploads.SaveAsync(big, ".ccf", 1024, CancellationToken.None));
        using (var empty = new MemoryStream())
            await Assert.ThrowsAsync<InvalidDataException>(() => uploads.SaveAsync(empty, ".ccf", 1024, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => uploads.SaveAsync(new MemoryStream([1]), ".exe", 1024, CancellationToken.None));
        // Nothing half-written is left behind, and nothing at all for the refused ones.
        Assert.Empty(Directory.GetFiles(uploads.Directory));

        using var good = new MemoryStream([1, 2, 3]);
        var reference = await uploads.SaveAsync(good, ".ccf", 1024, CancellationToken.None);
        var path = uploads.Resolve(reference)!;
        Assert.Equal(uploads.Directory, Path.GetDirectoryName(path));
        Assert.Equal(".ccf", Path.GetExtension(path));
        Assert.Equal([1, 2, 3], File.ReadAllBytes(path));
        Assert.Single(Directory.GetFiles(uploads.Directory));

        // A day old, it goes with the next clean-up.
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-2));
        uploads.Clean();
        Assert.False(File.Exists(path));

        // Each kind has its own cap: a burst of containers leaves the torrent an open dialog still names.
        using var torrentBody = new MemoryStream([1]);
        var torrent = uploads.Resolve(await uploads.SaveAsync(torrentBody, UploadFolder.TorrentExtension, 1024, CancellationToken.None))!;
        File.SetLastWriteTimeUtc(torrent, DateTime.UtcNow.AddHours(-1));
        for (var i = 0; i <= UploadFolder.MaxFiles; i++)
        {
            using var container = new MemoryStream([1]);
            await uploads.SaveAsync(container, ".dlc", 1024, CancellationToken.None);
        }

        uploads.Clean();
        Assert.True(File.Exists(torrent));
        Assert.Equal(UploadFolder.MaxFiles, Directory.GetFiles(uploads.Directory, "*.dlc").Length);

        // A folder removed since it was prepared is made again rather than failing the upload.
        Directory.Delete(uploads.Directory, recursive: true);
        using var again = new MemoryStream([1]);
        Assert.NotNull(uploads.Resolve(await uploads.SaveAsync(again, ".rsdf", 1024, CancellationToken.None)));
    }

    [Fact]
    public void TheHostRedactsAHiddenDownloadsPageAndFinalAddressForEveryClient()
    {
        var hidden = new DownloadJob
        {
            Url = "https://hoster.example/download/***",
            ProtectedUrl = "protected",
            FinalUrl = "https://cdn.hoster.example/abcdef/a.bin?token=1",
            PageUrl = "https://user:secret@hoster.example:8443/folder/abcdef?key=1",
            FileName = "a.bin",
            DestinationDirectory = Inside,
            FinalFilePath = Path.Combine(Inside, "a.bin"),
            State = DownloadState.Downloading,
            // The Host sets a host name here, never an address; this one only shows that the note's safeguard holds.
            WaitingForHost = "https://hoster.example/download/abcdef",
        };
        var shown = DownloadManagerService.Map(hidden);
        Assert.True(shown.AddressHidden);
        Assert.Equal("https://hoster.example:8443/", shown.PageUrl);
        Assert.Null(shown.FinalUrl);
        Assert.DoesNotContain("abcdef", shown.StatusNote);
        Assert.StartsWith("Waiting for a connection to ", shown.StatusNote);

        var provider = new DownloadJob
        {
            Url = "remote://provider/item/1",
            RemoteSource = new RemoteSource("provider", "torrent", "1", "2"),
            PageUrl = "https://hoster.example/file/abcdef",
            FileName = "b.bin",
        };
        Assert.Equal("https://hoster.example/", DownloadManagerService.Map(provider).PageUrl);

        // The user's own link shows as it is.
        var plain = new DownloadJob { Url = "https://example.com/c.bin", FinalUrl = "https://cdn.example.com/c.bin", PageUrl = "https://example.com/page?id=7", FileName = "c.bin" };
        Assert.Equal("https://example.com/page?id=7", DownloadManagerService.Map(plain).PageUrl);
        Assert.Equal("https://cdn.example.com/c.bin", DownloadManagerService.Map(plain).FinalUrl);
        Assert.Null(DownloadManagerService.Map(new DownloadJob { Url = "x", ProtectedUrl = "p", PageUrl = "not an address" }).PageUrl);
    }
}
