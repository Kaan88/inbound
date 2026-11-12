using System.Text;
using System.Text.Json;
using DownloadManager.Contracts;
using DownloadManager.Core;
using DownloadManager.Runtime;
using DownloadManager.Runtime.Ipc;
using DownloadManager.Runtime.Remote;
using DownloadManager.Runtime.WebAuth;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// Audit fixes for the IPC and web-session track (2026-11-12): what a web session may do to and learn of downloads
/// outside the app's folders, torrents that name local addresses, the browser's hand-off folder, window pages, a full
/// outgoing queue and exception text.
/// </summary>
public sealed partial class HostCommandDispatcherTests
{
    // ---- report 2, M2: packages, queues and queue moves --------------------------------------------

    [Fact]
    public async Task AWebSessionCantRegroupRequeueOrReprioritiseADownloadOutsideItsFoldersButMayStartAndStopIt()
    {
        var outsideJob = SeededJob(Outside, "outside.bin");
        var insideJob = SeededJob(Inside, "inside.bin");
        var otherInside = SeededJob(Inside, "other.bin");
        var web = await WebAsync(async store =>
        {
            await store.SaveAsync(outsideJob);
            await store.SaveAsync(insideJob);
            await store.SaveAsync(otherInside);
        });

        // A package made on this PC, holding a download from each side.
        var made = (await AskAsync(web.Dispatcher, web.Pipe, MessageTypes.GroupAction,
            new GroupActionRequest { Action = "create", Name = "Mixed", JobIds = [outsideJob.Id, insideJob.Id] })).GetPayload<GroupActionResult>();
        Assert.True(made.Ok, made.Error);
        var package = made.GroupId;
        var queue = web.Service.GetSettings().DefaultQueueId;

        foreach (var request in new[]
                 {
                     new GroupActionRequest { GroupId = package, Action = "priority", Priority = JobPriorities.High },
                     new GroupActionRequest { GroupId = package, Action = "queue", QueueId = queue },
                     new GroupActionRequest { GroupId = package, Action = "unqueue" },
                     new GroupActionRequest { GroupId = package, Action = "rename", Name = "Renamed" },
                     new GroupActionRequest { GroupId = package, Action = "delete" },
                     new GroupActionRequest { GroupId = package, Action = "detach", JobIds = [insideJob.Id] },
                     new GroupActionRequest { Action = "create", Name = "Taken", JobIds = [outsideJob.Id] },
                 })
        {
            var refused = (await AskAsync(web.Dispatcher, web.Session, MessageTypes.GroupAction, request)).GetPayload<GroupActionResult>();
            Assert.False(refused.Ok, request.Action);
            Assert.Equal(CallerScope.OutsideJobRefused, refused.Error);
        }

        Assert.Equal("Mixed", Assert.Single(web.Service.GetDownloadGroups()).Name);
        Assert.Equal(outsideJob.Priority, web.Service.GetJob(outsideJob.Id)!.Priority);

        // Starting and stopping its files is not a change to them, as with job.pause and job.resume.
        var paused = (await AskAsync(web.Dispatcher, web.Session, MessageTypes.GroupAction,
            new GroupActionRequest { GroupId = package, Action = "pause" })).GetPayload<GroupActionResult>();
        Assert.NotEqual(CallerScope.OutsideJobRefused, paused.Error);

        // Inside its folders the same changes go ahead.
        var own = (await AskAsync(web.Dispatcher, web.Session, MessageTypes.GroupAction,
            new GroupActionRequest { Action = "create", Name = "Own", JobIds = [otherInside.Id] })).GetPayload<GroupActionResult>();
        Assert.True(own.Ok, own.Error);

        // job.moveToQueue, into a queue or out of every queue.
        var move = (await AskAsync(web.Dispatcher, web.Session, MessageTypes.JobMoveToQueue,
            new JobQueueRequest { JobId = outsideJob.Id, QueueId = queue })).GetPayload<JobActionResponse>();
        Assert.Equal(CallerScope.OutsideJobRefused, move.Error);
        var detach = (await AskAsync(web.Dispatcher, web.Session, MessageTypes.JobMoveToQueue,
            new JobQueueRequest { JobId = outsideJob.Id, Detach = true })).GetPayload<JobActionResponse>();
        Assert.Equal(CallerScope.OutsideJobRefused, detach.Error);
        var insideMove = (await AskAsync(web.Dispatcher, web.Session, MessageTypes.JobMoveToQueue,
            new JobQueueRequest { JobId = insideJob.Id, QueueId = queue })).GetPayload<JobActionResponse>();
        Assert.True(insideMove.Ok, insideMove.Error);

        // queue.action: deleting a queue moves its downloads, so only when they are all inside.
        var created = (await AskAsync(web.Dispatcher, web.Pipe, MessageTypes.QueueAction,
            new QueueActionRequest { Action = "create", Name = "Second", MaxConcurrentDownloads = 1 })).GetPayload<SettingsResult>();
        Assert.True(created.Ok, created.Error);
        var second = created.Settings!.Queues.Single(q => q.Name == "Second").Id;
        Assert.True((await web.Service.MoveToQueueAsync(outsideJob.Id, second)).Ok);
        var deleted = (await AskAsync(web.Dispatcher, web.Session, MessageTypes.QueueAction,
            new QueueActionRequest { Action = "delete", QueueId = second, MoveContentsToQueueId = queue })).GetPayload<SettingsResult>();
        Assert.Equal(CallerScope.OutsideJobRefused, deleted.Error);
        Assert.Contains(web.Service.GetSettings().Queues, q => q.Id == second);
        Assert.Equal(second, web.Service.GetJob(outsideJob.Id)!.QueueId);
    }

    // ---- report 2, M4: what a web session is told ------------------------------------------------

    [Fact]
    public async Task AWebSessionSeesADownloadOutsideItsFoldersWithoutItsAddressPathsOrProgram()
    {
        var outsideJob = SeededJob(Outside, "outside.bin");
        outsideJob.Url = "http://203.0.113.10/outside.bin?token=invented-token";
        outsideJob.PageUrl = "https://pages.example.invalid/list?sig=invented-sig";
        outsideJob.FinalUrl = "http://cdn.example.invalid/outside.bin?sig=invented-sig";
        outsideJob.CompletionOptions = new JobCompletionOptions { ProgramPath = @"C:\Tools\scan.exe" };
        var insideJob = SeededJob(Inside, "inside.bin");
        var web = await WebAsync(async store =>
        {
            await store.SaveAsync(outsideJob);
            await store.SaveAsync(insideJob);
        });

        void AssertHidden(JobDto job)
        {
            Assert.Equal("http://203.0.113.10/", job.Url);
            Assert.True(job.AddressHidden);
            Assert.Null(job.FinalUrl);
            Assert.Equal("https://pages.example.invalid/", job.PageUrl);
            Assert.Equal(string.Empty, job.DestinationDirectory);
            Assert.Equal(string.Empty, job.FinalFilePath);
            Assert.Equal(string.Empty, job.PartFilePath);
            Assert.Null(job.CompletionOptions.ProgramPath);
            Assert.Equal("outside.bin", job.FileName);
        }

        // The snapshot on connect and on request.
        var snapshot = web.Session.Frames[1].GetPayload<SnapshotDto>();
        AssertHidden(snapshot.Jobs.Single(j => j.Id == outsideJob.Id));
        Assert.Equal(insideJob.FinalFilePath, snapshot.Jobs.Single(j => j.Id == insideJob.Id).FinalFilePath);
        AssertHidden((await AskAsync(web.Dispatcher, web.Session, MessageTypes.JobsQuery, new { })).GetPayload<SnapshotDto>().Jobs.Single(j => j.Id == outsideJob.Id));
        var raw = web.Session.Frames[1].Payload!.Value.GetRawText();
        Assert.DoesNotContain("invented-token", raw);
        Assert.DoesNotContain("invented-sig", raw);
        Assert.DoesNotContain(Outside.Replace(@"\", @"\\"), raw);

        // The pipe is this PC's user: everything.
        var pipeJob = web.Pipe.Frames[1].GetPayload<SnapshotDto>().Jobs.Single(j => j.Id == outsideJob.Id);
        Assert.Equal(outsideJob.Url, pipeJob.Url);
        Assert.Equal(outsideJob.FinalFilePath, pipeJob.FinalFilePath);

        // A change to it, as each session gets it.
        web.Dispatcher.BroadcastJobChanged(new JobChangeEventArgs(web.Service.GetJob(outsideJob.Id)!, "updated"));
        var webEvent = await UntilEventAsync(web.Session, outsideJob.Id);
        AssertHidden(webEvent);
        var pipeEvent = await UntilEventAsync(web.Pipe, outsideJob.Id);
        Assert.Equal(outsideJob.FinalFilePath, pipeEvent.FinalFilePath);
        Assert.Equal(@"C:\Tools\scan.exe", pipeEvent.CompletionOptions.ProgramPath);

        // A reply that carries it: a refused change.
        var refused = (await AskAsync(web.Dispatcher, web.Session, MessageTypes.JobUpdate, new JobUpdateRequest { JobId = outsideJob.Id, Description = "x" })).GetPayload<JobActionResponse>();
        Assert.Equal(CallerScope.OutsideJobRefused, refused.Error);
        AssertHidden(refused.Job!);

        // Its connections and pieces are not shown either.
        var connections = await AskAsync(web.Dispatcher, web.Session, MessageTypes.JobConnections, new JobActionRequest { JobId = outsideJob.Id });
        Assert.Equal("[]", connections.Payload!.Value.GetRawText());
        var segments = await AskAsync(web.Dispatcher, web.Session, MessageTypes.JobSegments, new JobActionRequest { JobId = outsideJob.Id });
        Assert.Equal("[]", segments.Payload!.Value.GetRawText());

        // A set of archives outside its folders leaves its list, and stays in the pipe's.
        var set = new ArchiveSetDto { Id = Guid.NewGuid(), Name = "outside", Directory = Outside };
        web.Dispatcher.BroadcastArchiveChanged(new ArchiveChangedDto { Set = set });
        await web.Session.UntilAsync(frames => frames.Any(f => f.Type == ArchiveMessageTypes.Changed));
        var webArchive = web.Session.Frames.Last(f => f.Type == ArchiveMessageTypes.Changed).GetPayload<ArchiveChangedDto>();
        Assert.Null(webArchive.Set);
        Assert.Equal(set.Id, webArchive.RemovedId);
        await web.Pipe.UntilAsync(frames => frames.Any(f => f.Type == ArchiveMessageTypes.Changed));
        Assert.Equal(Outside, web.Pipe.Frames.Last(f => f.Type == ArchiveMessageTypes.Changed).GetPayload<ArchiveChangedDto>().Set!.Directory);
        var insideSet = new ArchiveSetDto { Id = Guid.NewGuid(), Name = "inside", Directory = Inside };
        web.Dispatcher.BroadcastArchiveChanged(new ArchiveChangedDto { Set = insideSet });
        await web.Session.UntilAsync(frames => frames.Any(f => f.Type == ArchiveMessageTypes.Changed && f.GetPayload<ArchiveChangedDto>().Set?.Id == insideSet.Id));
    }

    [Fact]
    public async Task AWebSessionIsNotToldTheFolderOfAProviderJobOutsideItsFolders()
    {
        var service = await ServiceAsync();
        var registry = new RemoteProviderRegistry();
        var store = new DownloadManager.Persistence.JsonRemoteJobStore(_paths);
        var at = DateTimeOffset.UtcNow;
        DownloadManager.Core.Providers.RemoteJobRecord Record(string folder) => new()
        {
            Id = Guid.NewGuid(),
            Provider = "torbox",
            Kind = DownloadManager.Core.Providers.RemoteItemKind.WebDownload,
            State = DownloadManager.Core.Providers.RemoteJobState.LocalStarted,
            ItemId = "1",
            Destination = folder,
            CreatedAt = at,
            UpdatedAt = at,
        };
        var outsideRecord = Record(Outside);
        var insideRecord = Record(Path.Combine(Inside, "sub"));
        await store.SaveAsync(outsideRecord);
        await store.SaveAsync(insideRecord);
        await using var jobs = new RemoteJobCoordinator(registry, store, service, new RemoteCoordinatorOptions { RunLoops = false });
        await jobs.StartAsync();
        var web = await WebAsync(providers: registry, remoteJobs: jobs, service: service);

        var webList = (await AskAsync(web.Dispatcher, web.Session, MessageTypes.RemoteList, new { })).GetPayload<RemoteListResult>();
        Assert.Null(webList.Jobs.Single(job => job.Id == outsideRecord.Id).Destination);
        Assert.Equal(insideRecord.Destination, webList.Jobs.Single(job => job.Id == insideRecord.Id).Destination);
        // This PC's own commands (as the Host's pipe has them) still list the folder.
        var pipeList = new RemoteCommands(registry, jobs).List();
        Assert.Equal(Outside, pipeList.Jobs.Single(job => job.Id == outsideRecord.Id).Destination);

        // remote.changed: the same, and the copy this PC's sessions get keeps its folder.
        var change = new RemoteChangedDto { Change = "job", Job = pipeList.Jobs.Single(job => job.Id == outsideRecord.Id), JobId = outsideRecord.Id };
        web.Dispatcher.BroadcastRemoteChanged(change);
        await web.Session.UntilAsync(frames => frames.Any(f => f.Type == MessageTypes.RemoteChanged));
        await web.Pipe.UntilAsync(frames => frames.Any(f => f.Type == MessageTypes.RemoteChanged));
        var webChange = web.Session.Frames.Last(f => f.Type == MessageTypes.RemoteChanged).GetPayload<RemoteChangedDto>();
        Assert.Equal(outsideRecord.Id, webChange.Job!.Id);
        Assert.Null(webChange.Job.Destination);
        Assert.Equal(Outside, web.Pipe.Frames.Last(f => f.Type == MessageTypes.RemoteChanged).GetPayload<RemoteChangedDto>().Job!.Destination);
        Assert.Equal(Outside, change.Job!.Destination);
    }

    private static async Task<JobDto> UntilEventAsync(MemorySession session, Guid id)
    {
        await session.UntilAsync(frames => frames.Any(f => f.Type == MessageTypes.JobEvent && f.GetPayload<JobEventDto>().Job.Id == id));
        return session.Frames.Last(f => f.Type == MessageTypes.JobEvent && f.GetPayload<JobEventDto>().Job.Id == id).GetPayload<JobEventDto>().Job;
    }

    // ---- report 2, M3: torrents that name this PC's network ----------------------------------------

    [Theory]
    [InlineData("&tr=http%3A%2F%2F192.168.1.1%2Fannounce")]
    [InlineData("&tr.1=udp%3A%2F%2F127.0.0.1%3A6969")]
    [InlineData("&ws=http%3A%2F%2F10.0.0.5%2Ffile")]
    [InlineData("&x.pe=192.168.1.20%3A6881")]
    [InlineData("&x.pe=%5Bfe80%3A%3A1%5D%3A6881")]
    [InlineData("&tr=udp%3A%2F%2F10.0.0.1%3A70000")]
    [InlineData("&dht=192.168.1.5%3A6881")]
    [InlineData("&tr=http%3A%2F%2Fnas%2Fannounce")]
    [InlineData("&tr=http%3A%2F%2Ffritz.box%2Fannounce")]
    [InlineData("&ws=http%3A%2F%2Fpublic.example.org%2Ffile&tr=udp%3A%2F%2Frouter.example.org%3A6969")]
    public async Task AWebSessionsMagnetMayNotNameATrackerWebSeedOrPeerOnThisPcsNetwork(string field)
    {
        var service = await TorrentServiceAsync();
        service.TorrentHostResolver = InventedDns;
        var web = await WebAsync(service: service);
        var magnet = "magnet:?xt=urn:btih:" + new string('c', 40) + "&dn=Local" + field;

        var inspect = (await AskAsync(web.Dispatcher, web.Session, TorrentMessageTypes.Inspect, new TorrentInspectRequest { Input = magnet })).GetPayload<TorrentInspectResult>();
        Assert.Equal(DownloadManagerService.TorrentLocalAddressText, inspect.Error);
        var add = (await AskAsync(web.Dispatcher, web.Session, TorrentMessageTypes.Add, new TorrentAddRequest { Input = magnet, Destination = Inside })).GetPayload<TorrentAddResult>();
        Assert.False(add.Ok);
        Assert.Equal(DownloadManagerService.TorrentLocalAddressText, add.Error);
        Assert.DoesNotContain(web.Service.GetSnapshot().Jobs, job => job.Torrent is not null);

        // This PC's user may.
        var fromPipe = (await AskAsync(web.Dispatcher, web.Pipe, TorrentMessageTypes.Add, new TorrentAddRequest { Input = magnet, Destination = Inside, StartMode = "later" })).GetPayload<TorrentAddResult>();
        Assert.True(fromPipe.Ok, fromPipe.Error);
    }

    [Fact]
    public async Task AWebSessionsTorrentFileMayNotNameATrackerWebSeedOrNodeOnThisPcsNetworkButAPublicOneIsFine()
    {
        var service = await TorrentServiceAsync();
        service.TorrentHostResolver = InventedDns;
        var web = await WebAsync(service: service);
        var (plain, _) = SingleFileTorrent("film.mkv", 20_000);

        static string S(string text) => $"{Encoding.UTF8.GetByteCount(text)}:{text}";
        byte[] With(string beforeInfo, string afterInfo)
            => [.. "d"u8.ToArray(), .. Encoding.ASCII.GetBytes(beforeInfo), .. plain[1..^1], .. Encoding.ASCII.GetBytes(afterInfo), .. "e"u8.ToArray()];

        var refused = new[]
        {
            With($"{S("announce")}{S("http://192.168.1.1/announce")}", ""),
            With($"{S("announce")}{S("https://tracker.example.org/announce")}{S("announce-list")}ll{S("udp://[::1]:6969")}ee", ""),
            With("", $"{S("nodes")}ll{S("10.0.0.2")}i6881eee"),
            With("", $"{S("url-list")}{S("http://169.254.169.254/latest")}"),
            With("", $"{S("url-list")}l{S("https://seed.example.org/")}{S("http://localhost/seed")}e"),
            // By name: one that resolves to a private address, and one of a single label.
            With($"{S("announce")}{S("udp://router.example.org:6969")}", ""),
            With("", $"{S("nodes")}ll{S("nas")}i6881eee"),
        };
        for (var index = 0; index < refused.Length; index++)
        {
            var file = Path.Combine(Inside, $"local-{index}.torrent");
            await File.WriteAllBytesAsync(file, refused[index]);
            Assert.True(TorrentMetadata.TryParse(refused[index], out _, out var problem), problem);
            var inspect = (await AskAsync(web.Dispatcher, web.Session, TorrentMessageTypes.Inspect, new TorrentInspectRequest { Input = file })).GetPayload<TorrentInspectResult>();
            Assert.Equal(DownloadManagerService.TorrentLocalAddressText, inspect.Error);
            var add = (await AskAsync(web.Dispatcher, web.Session, TorrentMessageTypes.Add, new TorrentAddRequest { Data = Convert.ToBase64String(refused[index]), Destination = Inside })).GetPayload<TorrentAddResult>();
            Assert.Equal(DownloadManagerService.TorrentLocalAddressText, add.Error);
        }

        var publicFile = Path.Combine(Inside, "public.torrent");
        await File.WriteAllBytesAsync(publicFile, With($"{S("announce")}{S("https://tracker.example.org/announce")}", $"{S("url-list")}{S("https://seed.example.org/")}"));
        var fine = (await AskAsync(web.Dispatcher, web.Session, TorrentMessageTypes.Inspect, new TorrentInspectRequest { Input = publicFile })).GetPayload<TorrentInspectResult>();
        Assert.True(fine.Ok, fine.Error);
    }

    [Theory]
    [InlineData("http://192.168.0.1:8080/announce", true)]
    [InlineData("udp://user@127.0.0.1:99999/announce", true)]
    [InlineData("10.1.2.3:6881", true)]
    [InlineData("fd00::1", true)]
    [InlineData("http://printer.local/", true)]
    [InlineData("udp://:6969", true)]
    [InlineData("http://2130706433/", true)]
    [InlineData("http://nas/announce", true)]
    [InlineData("nas:6881", true)]
    [InlineData("http://fritz.box/announce", false)]
    [InlineData("https://tracker.example.org/announce", false)]
    [InlineData("udp://203.0.113.5:6969", false)]
    [InlineData("", false)]
    public void ATorrentAddressIsJudgedByItsHostAlone(string address, bool local)
        => Assert.Equal(local, DownloadManagerService.IsLocalTorrentAddress(address));

    /// <summary>
    /// Invented answers, with no network call: fritz.box and router.example.org lead to the local network, the other
    /// example.org names to public addresses, and broken.example.org fails.
    /// </summary>
    private static Task<System.Net.IPAddress[]> InventedDns(string host, CancellationToken cancellationToken) => host.ToLowerInvariant() switch
    {
        "fritz.box" => Task.FromResult(new[] { System.Net.IPAddress.Parse("192.168.178.1") }),
        "router.example.org" => Task.FromResult(new[] { System.Net.IPAddress.Parse("203.0.113.7"), System.Net.IPAddress.Parse("10.0.0.1") }),
        "broken.example.org" => Task.FromException<System.Net.IPAddress[]>(new System.Net.Sockets.SocketException(11001)),
        _ => Task.FromResult(new[] { System.Net.IPAddress.Parse("203.0.113.9") }),
    };

    [Fact]
    public async Task AWebSessionsMagnetWithPublicNamesOrANameThatDoesNotResolveIsAddedAndTooManyNamesAreRefused()
    {
        var service = await TorrentServiceAsync();
        var asked = new List<string>();
        service.TorrentHostResolver = (host, token) => { lock (asked) asked.Add(host); return InventedDns(host, token); };
        var web = await WebAsync(service: service);

        var magnet = "magnet:?xt=urn:btih:" + new string('d', 40) + "&dn=Public" +
                     "&tr=udp%3A%2F%2Ftracker.example.org%3A6969&tr=http%3A%2F%2FTRACKER.example.org%2Fannounce" +
                     "&ws=https%3A%2F%2Fbroken.example.org%2Ffile&dht=203.0.113.5%3A6881";
        var add = (await AskAsync(web.Dispatcher, web.Session, TorrentMessageTypes.Add, new TorrentAddRequest { Input = magnet, Destination = Inside, StartMode = "later" })).GetPayload<TorrentAddResult>();
        Assert.True(add.Ok, add.Error);
        // Each name once; the address needs no lookup.
        Assert.Equal(new[] { "tracker.example.org", "broken.example.org" }, asked.Order().Reverse().ToArray());

        var many = "magnet:?xt=urn:btih:" + new string('e', 40) + "&dn=Many" +
                   string.Concat(Enumerable.Range(0, 65).Select(index => $"&tr=udp%3A%2F%2Ft{index}.example.org%3A6969"));
        var refused = (await AskAsync(web.Dispatcher, web.Session, TorrentMessageTypes.Add, new TorrentAddRequest { Input = many, Destination = Inside })).GetPayload<TorrentAddResult>();
        Assert.Equal(DownloadManagerService.TorrentTooManyHostsText, refused.Error);
    }

    // ---- report 3, M1: the browser's hand-off folder ----------------------------------------------

    [Fact]
    public async Task TheBrowserMaySaveOnlyIntoTheAppsFoldersOrOneTheFolderDialogReturnedElseTheDefaultFolder()
    {
        var web = await WebAsync();
        var settings = web.Service.GetSettings();
        settings.BrowserIntegrationEnabled = true;
        var favourite = Path.Combine(_root, "favourite");
        settings.FavoriteFolders = [new FavoriteFolderDto { Name = "Fav", Path = favourite }];
        Assert.True((await web.Service.UpdateSettingsAsync(settings)).Ok);
        HandoffRequest Handoff(string? folder, string name) => new()
        {
            RequestId = Guid.NewGuid().ToString("N"),
            ExtensionId = BrowserBridge.StoreExtensionId,
            Url = $"http://203.0.113.10/{name}",
            SuggestedFileName = name,
            DestinationDirectory = folder,
            StartMode = "later",
        };
        async Task<(HandoffAck Ack, JobDto Job)> SendAsync(string? folder, string name)
        {
            var ack = (await AskAsync(web.Dispatcher, web.Pipe, MessageTypes.HandoffRequest, Handoff(folder, name))).GetPayload<HandoffAck>();
            Assert.True(ack.Accepted, ack.Error);
            return (ack, web.Service.GetJob(ack.JobId)!);
        }

        // The app's folders and the folders under them.
        foreach (var folder in new[] { Inside, Path.Combine(Inside, "sub"), favourite, Path.Combine(favourite, "deeper") })
        {
            var (ack, job) = await SendAsync(folder, $"in-{Guid.NewGuid():N}.bin");
            Assert.Null(ack.Note);
            Assert.True(PathNames.Same(folder, job.DestinationDirectory), job.DestinationDirectory);
        }

        // Anywhere else (the Startup folder, say) goes to the default folder, with a note.
        var startup = Path.Combine(_root, "AppData", "Roaming", "Microsoft", "Windows", "Start Menu", "Programs", "Startup");
        foreach (var folder in new[] { Outside, startup, Inside + "x", Path.Combine(Inside, "..", "elsewhere"), "relative\\folder" })
        {
            var (ack, job) = await SendAsync(folder, $"out-{Guid.NewGuid():N}.bat");
            Assert.Equal(HostCommandDispatcher.HandoffFolderReplaced, ack.Note);
            Assert.True(PathNames.Same(Inside, job.DestinationDirectory), job.DestinationDirectory);
        }

        Assert.False(Directory.Exists(startup));

        // EXT.R10: a folder the user picked in this Host's own folder dialog may be used.
        web.Platform.Picks = Outside;
        var picked = (await AskAsync(web.Dispatcher, web.Pipe, MessageTypes.HostPickFolder, new FolderPickRequest())).GetPayload<FolderPickResult>();
        Assert.Equal(Outside, picked.Path);
        var (pickedAck, pickedJob) = await SendAsync(Outside, "picked.bin");
        Assert.Null(pickedAck.Note);
        Assert.True(PathNames.Same(Outside, pickedJob.DestinationDirectory));

        // The extension keeps that folder in its settings and sends it with every automatic download, so the Host
        // keeps it too: a Host started again (the next sign-in) still takes it, and a folder under it.
        var restarted = new HostCommandDispatcher(web.Service, new FakePlatform(PlatformCapabilities.Desktop, _timeline), new ListLog());
        var pipe = Session();
        await restarted.OpenSessionAsync(pipe, new ClientHelloDto(), CancellationToken.None);
        await pipe.UntilAsync(frames => frames.Count >= 2);
        foreach (var (folder, name) in new[] { (Outside, "after-restart.bin"), (Path.Combine(Outside, "sub"), "after-restart-sub.bin") })
        {
            var ack = (await AskAsync(restarted, pipe, MessageTypes.HandoffRequest, Handoff(folder, name))).GetPayload<HandoffAck>();
            Assert.True(ack.Accepted, ack.Error);
            Assert.Null(ack.Note);
            Assert.True(PathNames.Same(folder, web.Service.GetJob(ack.JobId)!.DestinationDirectory));
        }

        // A folder the dialog never returned is still replaced after the restart.
        var other = (await AskAsync(restarted, pipe, MessageTypes.HandoffRequest, Handoff(startup, "late.bat"))).GetPayload<HandoffAck>();
        Assert.Equal(HostCommandDispatcher.HandoffFolderReplaced, other.Note);
    }

    // ---- report 3, L2: window pages ---------------------------------------------------------------

    [Theory]
    [InlineData("settings", "settings")]
    [InlineData(" Settings ", "settings")]
    [InlineData("", null)]
    [InlineData("update-progress", null)]
    [InlineData("x --job 00000000-0000-0000-0000-000000000001 --manual", null)]
    [InlineData(@"x C:\path\file.torrent", null)]
    public async Task OnlyAPageTheWindowKnowsIsOpenedForAClient(string action, string? page)
    {
        var service = await ServiceAsync();
        var platform = new FakePlatform(PlatformCapabilities.Desktop, _timeline);
        var dispatcher = Dispatcher(service, platform);

        var reply = (await AskAsync(dispatcher, Session(), MessageTypes.HostOpenUi, new HostActionRequest { Action = action })).GetPayload<HostActionResult>();
        Assert.True(reply.Ok);
        Assert.Equal([page], platform.Opened);
    }

    // ---- report 2, L16: exception text --------------------------------------------------------------

    [Fact]
    public async Task AWebSessionIsToldOnlyTheKindOfAnExceptionAndThisPcsUserItsWords()
    {
        var web = await WebAsync();
        var broken = IpcMessage.Create(MessageTypes.JobPause, "not an object");

        await web.Dispatcher.DispatchAsync(web.Session, broken, CancellationToken.None);
        var fromWeb = (await web.Session.ReplyToAsync(broken.Id)).GetPayload<HostActionResult>();
        Assert.False(fromWeb.Ok);
        Assert.Matches(@"^That could not be done \(\w+\)\. The app's log has the details\.$", fromWeb.Message);

        var again = IpcMessage.Create(MessageTypes.JobPause, "not an object");
        await web.Dispatcher.DispatchAsync(web.Pipe, again, CancellationToken.None);
        var fromPipe = (await web.Pipe.ReplyToAsync(again.Id)).GetPayload<HostActionResult>();
        Assert.False(fromPipe.Ok);
        Assert.DoesNotContain("That could not be done", fromPipe.Message);
    }

    // ---- report 2, L19 / report 3, L8: a client that falls behind ---------------------------------

    [Fact]
    public void AClientWhoseQueueFillsIsClosedInsteadOfSilentlyLosingAReply()
    {
        var session = Session(runWriter: false);
        for (var i = 0; i < QueuedClientSession.QueueCapacity; i++)
            Assert.True(session.Enqueue(IpcMessage.Create(MessageTypes.JobEvent, new { i })));
        Assert.False(session.Overflowed);

        Assert.False(session.Enqueue(IpcMessage.Create(MessageTypes.HostActionResult, new HostActionResult { Ok = true }, "reply")));
        Assert.True(session.Overflowed);
        // Closed: nothing more is taken, so the client can only connect again and take a new snapshot.
        Assert.False(session.Enqueue(IpcMessage.Create(MessageTypes.JobEvent, new { })));
    }

    // ---- report 2, M1: the post-download program --------------------------------------------------

    [Theory]
    [InlineData(@"C:\Tools\scan.cmd")]
    [InlineData(@"C:\Tools\scan.bat")]
    [InlineData("scan.exe")]
    public async Task APostDownloadCommandThatIsNotAFullExePathIsRefusedWhenSaved(string command)
    {
        if (!OperatingSystem.IsWindows()) return;
        var service = await ServiceAsync();
        var settings = service.GetSettings();
        settings.PostDownloadCommand = command;

        var result = await service.UpdateSettingsAsync(settings);
        Assert.False(result.Ok);
        Assert.Equal(PostDownloadCommandLine.ProgramRefused, result.Error);
        Assert.Null(service.GetSettings().PostDownloadCommand);

        settings.PostDownloadCommand = @"C:\Tools\scan.exe";
        Assert.True((await service.UpdateSettingsAsync(settings)).Ok);
    }
}

/// <summary>Report 2 L17 and L20, report 3 L4: path links, the sign-in file and the update manifest.</summary>
public sealed class AuditEdgeFixTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "test-work", "edges-" + Guid.NewGuid().ToString("N"));

    public AuditEdgeFixTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
    }

    [Fact]
    public void ALinkToARootedButNotFullyQualifiedPathIsReadAsWindowsWould()
    {
        if (!OperatingSystem.IsWindows()) return;
        string? Link(string path, string target, string asked) => string.Equals(asked, path, StringComparison.OrdinalIgnoreCase) ? target : null;

        // "\Windows" from a link on C: is C:\Windows, not a folder under the link's own.
        Assert.Equal(@"C:\Windows\System32", PathRoots.Resolve(@"C:\Downloads\link\System32", asked => Link(@"C:\Downloads\link", @"\Windows", asked)));
        // "D:foo" depends on that drive's current folder, which can't be known: it leads nowhere.
        Assert.Null(PathRoots.Resolve(@"C:\Downloads\link\x", asked => Link(@"C:\Downloads\link", @"D:foo", asked)));
        // A relative target is still read from the link's folder.
        Assert.Equal(@"C:\Downloads\other\x", PathRoots.Resolve(@"C:\Downloads\link\x", asked => Link(@"C:\Downloads\link", @"other", asked)));
    }

    [Fact]
    public void ASignInFileHoldingJsonNullIsDamagedNotFirstTimeSetup()
    {
        File.WriteAllText(Path.Combine(_root, AuthStore.FileName), "null");
        Assert.Throws<AuthFileDamagedException>(() => AuthStore.Open(_root));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(PasswordHash.Iterations - 1)]
    [InlineData(PasswordHash.MaximumIterations + 1)]
    [InlineData(int.MaxValue)]
    public async Task APasswordRecordWithAnIterationCountOutsideTheRangeNeverMatches(int iterations)
    {
        var record = await PasswordHash.CreateAsync("correct horse", DateTimeOffset.UtcNow, "setup");
        Assert.True(await PasswordHash.VerifyAsync("correct horse", record));
        record.Iterations = iterations;
        Assert.False(await PasswordHash.VerifyAsync("correct horse", record));
    }

    [Fact]
    public async Task TheUpdateManifestIsReadNoFurtherThanItsLimitEvenWithoutALength()
    {
        using var small = new StreamContent(new MemoryStream([0xEF, 0xBB, 0xBF, .. "{\"a\":1}"u8.ToArray()]));
        Assert.Equal("{\"a\":1}", await DownloadManagerService.ReadManifestAsync(small, CancellationToken.None));

        var endless = new CountingStream(64L * 1024 * 1024);
        using var large = new StreamContent(endless);
        Assert.Null(large.Headers.ContentLength is null ? null : "has a length");
        Assert.Null(await DownloadManagerService.ReadManifestAsync(large, CancellationToken.None));
        Assert.True(endless.Taken <= 1024 * 1024 + 64 * 1024, $"read {endless.Taken} bytes");
    }

    [Fact]
    public async Task AnUpdateManifestBodyThatNeverFinishesEndsAtItsTimeLimit()
    {
        using var stalled = new StreamContent(new StallingStream());
        var started = System.Diagnostics.Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<TimeoutException>(
            () => DownloadManagerService.ReadManifestAsync(stalled, CancellationToken.None, TimeSpan.FromMilliseconds(200)));
        Assert.Equal("The update manifest took too long to arrive.", error.Message);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(10), started.Elapsed.ToString());

        // The caller's own cancel is still a cancel, not a timeout.
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        using var again = new StreamContent(new StallingStream());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => DownloadManagerService.ReadManifestAsync(again, cancel.Token, TimeSpan.FromMinutes(5)));
    }

    /// <summary>A body that sends one byte and then nothing more until it is cancelled.</summary>
    private sealed class StallingStream : Stream
    {
        private bool _sent;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_sent)
            {
                _sent = true;
                buffer.Span[0] = (byte)'{';
                return 1;
            }

            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }

    /// <summary>A stream of zeros with no length to tell, that counts what was read from it.</summary>
    private sealed class CountingStream(long size) : Stream
    {
        public long Taken { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => Taken; set => throw new NotSupportedException(); }
        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var take = (int)Math.Min(count, size - Taken);
            Array.Clear(buffer, offset, take);
            Taken += take;
            return take;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
