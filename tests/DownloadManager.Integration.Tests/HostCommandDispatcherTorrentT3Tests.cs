using System.Security.Cryptography;
using System.Text.Json.Nodes;
using DownloadManager.Contracts;
using DownloadManager.Core;
using DownloadManager.Runtime;
using DownloadManager.Runtime.Ipc;
using DownloadManager.Torrents;

namespace DownloadManager.Integration.Tests;

/// <summary>T3: file priorities, a torrent's own options, recheck, announce, details, Options › Torrents and watching,
/// through the dispatcher with the service running torrents on the in-memory engine.</summary>
public sealed partial class HostCommandDispatcherTests
{
    private async Task<(DownloadManagerService Service, HostCommandDispatcher Dispatcher, JobDto Job, FakeTorrentEngine Worker)> RunningMagnetAsync(
        int filesPerTorrent = 1, IReadOnlySet<string>? lacks = null, Action<AppSettings>? settings = null)
    {
        var service = await TorrentServiceAsync(filesPerTorrent: filesPerTorrent, lacks: lacks, settings: settings);
        var dispatcher = Dispatcher(service);
        var hash = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(20));
        var added = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Add, new TorrentAddRequest
        {
            Input = $"magnet:?xt=urn:btih:{hash}&dn=Pack", Destination = Path.Combine(_root, "down"),
        })).GetPayload<TorrentAddResult>();
        Assert.True(added.Ok, added.Error);
        var worker = await UntilAsync(() => _torrentWorkers.FirstOrDefault(w => w.Torrents.Count == 1));
        var job = await UntilAsync(() => service.GetSnapshot().Jobs.FirstOrDefault(j => j.Id == added.Job!.Id && j.Torrent!.HasMetadata));
        return (service, dispatcher, job, worker);
    }

    /// <summary>Puts a part file of the job's one file on disk (the in-memory worker writes nothing).</summary>
    private async Task<string> PartOnDiskAsync(HostCommandDispatcher dispatcher, JobDto job)
    {
        var files = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Files, new TorrentFilesRequest { JobId = job.Id })).GetPayload<TorrentFilesResult>();
        var part = Path.Combine(job.DestinationDirectory, Assert.Single(files.Files).Path) + TorrentJobInfo.PartSuffixFor(job.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(part)!);
        await File.WriteAllBytesAsync(part, new byte[16]);
        return part;
    }

    [Fact]
    public async Task FilePrioritiesGoToTheWorkerAndShowInTheFileList()
    {
        var (_, dispatcher, job, worker) = await RunningMagnetAsync(filesPerTorrent: 3);
        var set = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.SetFiles, new TorrentSetFilesRequest
        {
            JobId = job.Id,
            Priorities = [new() { Index = 0, Priority = "high" }, new() { Index = 1, Priority = "low" }, new() { Index = 2, Priority = "skip" }],
        })).GetPayload<TorrentActionResult>();
        Assert.True(set.Ok, set.Error);
        var sent = await UntilAsync(() => worker.Sent.LastOrDefault(s => s.Command == "setFilePriorities").Arguments);
        Assert.Equal([7, 1, 0], sent["priorities"]!.AsArray().Select(p => p!.GetValue<int>()));

        var files = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Files, new TorrentFilesRequest { JobId = job.Id })).GetPayload<TorrentFilesResult>();
        Assert.Equal(["high", "low", "skip"], files.Files.Select(f => f.Priority));
        Assert.Equal([true, true, false], files.Files.Select(f => f.Wanted));

        // Only the listed files change; an unknown word is refused.
        var one = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.SetFiles, new TorrentSetFilesRequest
        {
            JobId = job.Id, Priorities = [new() { Index = 2, Priority = "normal" }],
        })).GetPayload<TorrentActionResult>();
        Assert.True(one.Ok, one.Error);
        files = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Files, new TorrentFilesRequest { JobId = job.Id })).GetPayload<TorrentFilesResult>();
        Assert.Equal(["high", "low", "normal"], files.Files.Select(f => f.Priority));
        var bad = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.SetFiles, new TorrentSetFilesRequest
        {
            JobId = job.Id, Priorities = [new() { Index = 0, Priority = "urgent" }],
        })).GetPayload<TorrentActionResult>();
        Assert.False(bad.Ok);
        Assert.False(string.IsNullOrEmpty(bad.Error));
    }

    [Fact]
    public async Task ATorrentsOwnOptionsAreKeptSentAndCheckedAndTheGenericSpeedLimitIsRefused()
    {
        var service = await TorrentServiceAsync();
        var dispatcher = Dispatcher(service);
        var file = Path.Combine(_root, "film.torrent");
        var (bytes, _) = SingleFileTorrent("film.mkv", 100_000);
        await File.WriteAllBytesAsync(file, bytes);
        var added = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Add, new TorrentAddRequest
        {
            Input = file, Destination = Path.Combine(_root, "down"),
            Options = new TorrentOptionsDto { DownloadLimit = 1000, Sequential = true },
        })).GetPayload<TorrentAddResult>();
        Assert.True(added.Ok, added.Error);
        Assert.Equal((1000L, true), (added.Job!.Torrent!.DownloadLimit, added.Job.Torrent.Sequential));
        var worker = await UntilAsync(() => _torrentWorkers.FirstOrDefault(w => w.Adds.Count == 1));
        Assert.Equal(1000, worker.Adds[0]["downloadLimit"]!.GetValue<long>());
        Assert.True(worker.Adds[0]["sequential"]!.GetValue<bool>());

        var set = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.SetOptions, new TorrentOptionsDto
        {
            JobId = added.Job.Id, UploadLimit = 2000, SeedGoal = "ratio", SeedGoalValue = 1.5,
        })).GetPayload<TorrentOptionsResult>();
        Assert.True(set.Ok, set.Error);
        Assert.True(set.Running);
        var limits = await UntilAsync(() => worker.Sent.LastOrDefault(s => s.Command == "setTorrentLimits").Arguments);
        Assert.Equal((1000L, 2000L), (limits["download"]!.GetValue<long>(), limits["upload"]!.GetValue<long>()));

        var got = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Options, new TorrentJobRequest { JobId = added.Job.Id })).GetPayload<TorrentOptionsResult>();
        Assert.Equal((1000L, 2000L, "ratio", 1.5, true),
            (got.Options!.DownloadLimit, got.Options.UploadLimit, got.Options.SeedGoal, got.Options.SeedGoalValue, got.Options.Sequential));

        foreach (var wrong in new[]
                 {
                     new TorrentOptionsDto { JobId = added.Job.Id, SeedGoal = "forever" },
                     new TorrentOptionsDto { JobId = added.Job.Id, SeedGoal = "ratio" },
                     new TorrentOptionsDto { JobId = added.Job.Id, DownloadLimit = -1 },
                 })
        {
            var refused = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.SetOptions, wrong)).GetPayload<TorrentOptionsResult>();
            Assert.False(refused.Ok);
            Assert.False(string.IsNullOrEmpty(refused.Error));
        }

        var update = await service.UpdateJobAsync(new JobUpdateRequest { JobId = added.Job.Id, SpeedLimitBytesPerSecond = 5000 });
        Assert.False(update.Ok);
        Assert.Equal(DownloadManagerService.TorrentSpeedLimitRefused, update.Error);
    }

    [Fact]
    public async Task RecheckGoesToTheWorkerOrWaitsForTheStartAndNeedsDataOnDisk()
    {
        var (service, dispatcher, job, worker) = await RunningMagnetAsync();
        var part = await PartOnDiskAsync(dispatcher, job);
        var running = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Recheck, new TorrentJobRequest { JobId = job.Id })).GetPayload<TorrentActionResult>();
        Assert.True(running.Ok, running.Error);
        Assert.Contains(worker.Sent, s => s.Command == "recheck");

        // Paused, it stays on the worker, which checks it too.
        await service.PauseAsync(job.Id);
        await UntilAsync(() => service.GetSnapshot().Jobs.FirstOrDefault(j => j.Id == job.Id && j.State == "paused"));
        Assert.True((await AskAsync(dispatcher, Session(), TorrentMessageTypes.Recheck, new TorrentJobRequest { JobId = job.Id })).GetPayload<TorrentActionResult>().Ok);
        Assert.Equal(2, worker.Sent.Count(s => s.Command == "recheck"));
        File.Delete(part);

        // Never started (not on the worker): it is checked when it starts, if something of it is on disk.
        var file = Path.Combine(_root, "later.torrent");
        var (bytes, _) = SingleFileTorrent("later.mkv", 100_000);
        await File.WriteAllBytesAsync(file, bytes);
        var waiting = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Add, new TorrentAddRequest
        {
            Input = file, Destination = Path.Combine(_root, "down"), StartMode = "later",
        })).GetPayload<TorrentAddResult>().Job!;
        var nothing = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Recheck, new TorrentJobRequest { JobId = waiting.Id })).GetPayload<TorrentActionResult>();
        Assert.False(nothing.Ok);
        Assert.Contains("Nothing of this torrent is on disk", nothing.Error);

        await PartOnDiskAsync(dispatcher, waiting);
        var later = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Recheck, new TorrentJobRequest { JobId = waiting.Id })).GetPayload<TorrentActionResult>();
        Assert.True(later.Ok, later.Error);
        Assert.True(service.GetSnapshot().Jobs.Single(j => j.Id == waiting.Id).Torrent!.RecheckPending);

        await service.ResumeAsync(waiting.Id);
        var id = waiting.Id.ToString("N");
        var add = await UntilAsync(() => _torrentWorkers.SelectMany(w => w.Adds).FirstOrDefault(a => a["id"]?.GetValue<string>() == id));
        Assert.True(add["recheck"]?.GetValue<bool>());
        await UntilAsync(() => service.GetSnapshot().Jobs.FirstOrDefault(j => j.Id == waiting.Id && !j.Torrent!.RecheckPending));
    }

    [Fact]
    public async Task ACheckAskedForTheStartStaysPendingWhileTheWorkerDoesNotQueueIt()
    {
        var service = await TorrentServiceAsync(ignoresAddRecheck: true);
        var dispatcher = Dispatcher(service);
        var file = Path.Combine(_root, "older.torrent");
        var (bytes, _) = SingleFileTorrent("older.mkv", 100_000);
        await File.WriteAllBytesAsync(file, bytes);
        var waiting = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Add, new TorrentAddRequest
        {
            Input = file, Destination = Path.Combine(_root, "down"), StartMode = "later",
        })).GetPayload<TorrentAddResult>().Job!;
        await PartOnDiskAsync(dispatcher, waiting);
        Assert.True((await AskAsync(dispatcher, Session(), TorrentMessageTypes.Recheck, new TorrentJobRequest { JobId = waiting.Id })).GetPayload<TorrentActionResult>().Ok);

        await service.ResumeAsync(waiting.Id);
        var id = waiting.Id.ToString("N");
        var add = await UntilAsync(() => _torrentWorkers.SelectMany(w => w.Adds).FirstOrDefault(a => a["id"]?.GetValue<string>() == id));
        Assert.True(add["recheck"]?.GetValue<bool>());
        // The worker took the add without queuing the check (as a 0.3 worker does): the check is still owed.
        var worker = await UntilAsync(() => _torrentWorkers.FirstOrDefault(w => w.Torrents.Contains(id)));
        worker.Advance();
        await UntilAsync(() => service.GetSnapshot().Jobs.FirstOrDefault(j => j.Id == waiting.Id && j.ReceivedBytes > 0));
        Assert.True(service.GetSnapshot().Jobs.Single(j => j.Id == waiting.Id).Torrent!.RecheckPending);
    }

    [Fact]
    public async Task AnnounceAndDetailsAskTheWorkerAndAnOlderWorkerIsNamed()
    {
        var (service, dispatcher, job, worker) = await RunningMagnetAsync();
        var announced = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Announce, new TorrentJobRequest { JobId = job.Id })).GetPayload<TorrentActionResult>();
        Assert.True(announced.Ok, announced.Error);
        Assert.Contains(worker.Sent, s => s.Command == "announce");

        var details = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Details, new TorrentJobRequest { JobId = job.Id })).GetPayload<TorrentDetailsResult>();
        Assert.True(details is { Ok: true, Running: true, Source: "magnet" }, details.Error);
        Assert.Equal(new string('b', 64), details.InfoHashV2);
        Assert.Equal((1L, 42L, 1800L), (details.HashFailures, details.DhtNodes, details.NextAnnounce));
        var network = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Network, new { })).GetPayload<TorrentNetworkResult>();
        Assert.Equal(42L, network.DhtNodes);

        await service.PauseAsync(job.Id);
        await UntilAsync(() => service.GetSnapshot().Jobs.FirstOrDefault(j => j.Id == job.Id && j.State == "paused"));
        var stopped = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Announce, new TorrentJobRequest { JobId = job.Id })).GetPayload<TorrentActionResult>();
        Assert.Equal("The torrent isn't running, so it can't ask for peers now.", stopped.Error);
        var kept = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Details, new TorrentJobRequest { JobId = job.Id })).GetPayload<TorrentDetailsResult>();
        Assert.True(kept.Ok, kept.Error);
    }

    [Fact]
    public async Task AnOlderWorkerWithoutAnnounceOrRecheckIsNamedInWords()
    {
        var (_, dispatcher, job, _) = await RunningMagnetAsync(lacks: new HashSet<string> { "announce", "recheck" });
        await PartOnDiskAsync(dispatcher, job);
        var announced = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Announce, new TorrentJobRequest { JobId = job.Id })).GetPayload<TorrentActionResult>();
        Assert.Equal(DownloadManagerService.TorrentOlderWorkerText, announced.Error);
        var recheck = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Recheck, new TorrentJobRequest { JobId = job.Id })).GetPayload<TorrentActionResult>();
        Assert.Equal(DownloadManagerService.TorrentOlderWorkerText, recheck.Error);
    }

    [Fact]
    public async Task TorrentSettingsAreCheckedAndTheWorkerIsToldAtStart()
    {
        var service = await TorrentServiceAsync();
        var settings = service.GetSettings();
        settings.TorrentEncryption = "always";
        Assert.False((await service.UpdateSettingsAsync(settings)).Ok);

        settings = service.GetSettings();
        settings.TorrentConnectionsPerTorrent = 1;
        Assert.False((await service.UpdateSettingsAsync(settings)).Ok);

        settings = service.GetSettings();
        settings.TorrentUseProxy = true;
        var noProxy = await service.UpdateSettingsAsync(settings);
        Assert.False(noProxy.Ok);
        Assert.Contains("Options > Proxy", noProxy.Error);

        settings = service.GetSettings();
        settings.TorrentUseProxy = true;
        settings.Proxy = new ProxyDto { Mode = "manual", Address = "socks4a://127.0.0.1:1080" };
        var socks4a = await service.UpdateSettingsAsync(settings);
        Assert.False(socks4a.Ok);
        Assert.Contains("SOCKS4a", socks4a.Error);

        // SOCKS4 too: the torrent engine would look tracker names up on this PC.
        settings = service.GetSettings();
        settings.TorrentUseProxy = true;
        settings.Proxy = new ProxyDto { Mode = "manual", Address = "socks4://127.0.0.1:1080" };
        var socks4 = await service.UpdateSettingsAsync(settings);
        Assert.False(socks4.Ok);
        Assert.Contains("can't use a SOCKS4 proxy. Choose SOCKS5 or HTTP", socks4.Error);

        settings = service.GetSettings();
        settings.TorrentUseProxy = true;
        settings.Proxy = new ProxyDto { Mode = "manual", Address = "socks5://127.0.0.1:1080" };
        settings.TorrentEncryption = "require";
        settings.TorrentPex = false;
        settings.TorrentSeedsAtOnce = 3;
        settings.TorrentConnectionsPerTorrent = 40;
        settings.TorrentMaxConnections = 300;
        var saved = await service.UpdateSettingsAsync(settings);
        Assert.True(saved.Ok, saved.Error);
        Assert.Equal(("require", false, 3, 40, 300, true), (saved.Settings.TorrentEncryption, saved.Settings.TorrentPex, saved.Settings.TorrentSeedsAtOnce,
            saved.Settings.TorrentConnectionsPerTorrent, saved.Settings.TorrentMaxConnections, saved.Settings.TorrentUseProxy));

        var dispatcher = Dispatcher(service);
        var hash = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(20));
        Assert.True((await AskAsync(dispatcher, Session(), TorrentMessageTypes.Add, new TorrentAddRequest
        {
            Input = $"magnet:?xt=urn:btih:{hash}", Destination = Path.Combine(_root, "down"),
        })).GetPayload<TorrentAddResult>().Ok);
        var worker = await UntilAsync(() => _torrentWorkers.FirstOrDefault(w => w.Torrents.Count == 1));
        var proxy = worker.Sent.First(s => s.Command == "setProxy").Arguments;
        Assert.Equal(("socks5", "127.0.0.1", 1080), (proxy["type"]!.GetValue<string>(), proxy["host"]!.GetValue<string>(), proxy["port"]!.GetValue<int>()));
        var order = worker.Sent.Select(s => s.Command).ToList();
        Assert.True(order.IndexOf("setProxy") < order.IndexOf("setListen"), string.Join(", ", order));
        var listen = worker.Sent.First(s => s.Command == "setListen").Arguments;
        Assert.Equal(("forced", false), (listen["encryption"]!.GetValue<string>(), listen["pex"]!.GetValue<bool>()));
        var peers = worker.Sent.First(s => s.Command == "setPeerLimits").Arguments;
        Assert.Equal((300, 40, 3), (peers["connections"]!.GetValue<int>(), peers["connectionsPerTorrent"]!.GetValue<int>(), peers["activeSeeds"]!.GetValue<int>()));
    }

    [Fact]
    public void TheWebUiMayChangeTheTorrentLimitsButNotHowThisPcMeetsPeers()
    {
        foreach (var allowed in new[] { nameof(SettingsDto.TorrentSeedsAtOnce), nameof(SettingsDto.TorrentConnectionsPerTorrent), nameof(SettingsDto.TorrentMaxConnections) })
            Assert.Contains(allowed, WebSettingsRules.Allowed);
        foreach (var pipeOnly in new[]
                 {
                     nameof(SettingsDto.TorrentDht), nameof(SettingsDto.TorrentPex), nameof(SettingsDto.TorrentLsd),
                     nameof(SettingsDto.TorrentEncryption), nameof(SettingsDto.TorrentUseProxy),
                 })
        {
            Assert.Contains(pipeOnly, WebSettingsRules.PipeOnly);
            Assert.DoesNotContain(pipeOnly, WebSettingsRules.Allowed);
        }
    }

    [Fact]
    public async Task ProgressComesFasterOnlyWhileAClientWatches()
    {
        var service = await TorrentServiceAsync();
        var supervisor = _torrentSupervisor!;
        var client = Guid.NewGuid();
        Assert.True(service.WatchTorrents(client, new TorrentWatchRequest { On = true }).Ok);
        Assert.Equal(1, service.TorrentWatcherCount);
        await UntilAsync(() => supervisor.ProgressIntervalMs == 1000 ? supervisor : null);
        Assert.True(service.WatchTorrents(client, new TorrentWatchRequest { On = false }).Ok);
        await UntilAsync(() => supervisor.ProgressIntervalMs == 5000 ? supervisor : null);

        service.WatchTorrents(client, new TorrentWatchRequest { On = true, LeaseSeconds = 30 });
        await UntilAsync(() => supervisor.ProgressIntervalMs == 1000 ? supervisor : null);
        service.ForgetTorrentWatcher(client); // the client disconnected
        Assert.Equal(0, service.TorrentWatcherCount);
        await UntilAsync(() => supervisor.ProgressIntervalMs == 5000 ? supervisor : null);
    }

    [Fact]
    public async Task ATorrentThatDoesNotFitIsSaidSoOnInspectAndRefusedOnAdd()
    {
        var service = await TorrentServiceAsync();
        service.MeasureFreeBytes = (_, _) => Task.FromResult<long?>(1000);
        var dispatcher = Dispatcher(service);
        var file = Path.Combine(_root, "big.torrent");
        var (bytes, _) = SingleFileTorrent("big.mkv", 100_000);
        await File.WriteAllBytesAsync(file, bytes);
        var destination = Path.Combine(_root, "down");

        var inspect = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Inspect, new TorrentInspectRequest { Input = file, Destination = destination }))
            .GetPayload<TorrentInspectResult>();
        Assert.True(inspect.Ok, inspect.Error);
        Assert.Equal(1000L, inspect.FreeBytes);
        Assert.False(string.IsNullOrEmpty(inspect.SpaceProblem));

        var added = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Add, new TorrentAddRequest { Input = file, Destination = destination }))
            .GetPayload<TorrentAddResult>();
        Assert.False(added.Ok);
        Assert.Equal(inspect.SpaceProblem, added.Error);
        Assert.Empty(service.GetSnapshot().Jobs);

        service.MeasureFreeBytes = (_, _) => Task.FromResult<long?>(10_000_000);
        Assert.Null((await AskAsync(dispatcher, Session(), TorrentMessageTypes.Inspect, new TorrentInspectRequest { Input = file, Destination = destination }))
            .GetPayload<TorrentInspectResult>().SpaceProblem);
    }

    [Fact]
    public async Task AWebSessionIsRefusedTheT3CommandsForATorrentOutsideTheAppsFolders()
    {
        var service = await TorrentServiceAsync();
        var web = await WebAsync(service: service);

        async Task<JobDto> AddAsync(string folder)
        {
            var hash = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(20));
            var added = (await AskAsync(web.Dispatcher, web.Pipe, TorrentMessageTypes.Add, new TorrentAddRequest
            {
                Input = $"magnet:?xt=urn:btih:{hash}&dn=Pack", Destination = folder, StartMode = "later",
            })).GetPayload<TorrentAddResult>();
            Assert.True(added.Ok, added.Error);
            return added.Job!;
        }

        // The pipe (this PC) may put a torrent anywhere; the web session only sees the app's folders.
        var outside = await AddAsync(Outside);
        var inside = await AddAsync(Inside);
        var before = service.GetSnapshot().Jobs.Single(j => j.Id == outside.Id).Torrent!;

        foreach (var type in new[] { TorrentMessageTypes.Options, TorrentMessageTypes.Recheck, TorrentMessageTypes.Announce, TorrentMessageTypes.Details })
        {
            var refused = (await AskAsync(web.Dispatcher, web.Session, type, new TorrentJobRequest { JobId = outside.Id })).GetPayload<TorrentActionResult>();
            Assert.False(refused.Ok, type);
            Assert.Equal(CallerScope.OutsideJobRefused, refused.Error);
        }

        var set = (await AskAsync(web.Dispatcher, web.Session, TorrentMessageTypes.SetOptions,
            new TorrentOptionsDto { JobId = outside.Id, DownloadLimit = 1024, SeedGoal = "never", Sequential = true })).GetPayload<TorrentOptionsResult>();
        Assert.False(set.Ok);
        Assert.Equal(CallerScope.OutsideJobRefused, set.Error);
        var after = service.GetSnapshot().Jobs.Single(j => j.Id == outside.Id).Torrent!;
        Assert.Equal((before.DownloadLimit, before.SeedGoal, before.Sequential), (after.DownloadLimit, after.SeedGoal, after.Sequential));

        // Inside the folders the same session goes ahead, and the pipe may change the one outside.
        var options = (await AskAsync(web.Dispatcher, web.Session, TorrentMessageTypes.Options, new TorrentJobRequest { JobId = inside.Id })).GetPayload<TorrentOptionsResult>();
        Assert.True(options.Ok, options.Error);
        var details = (await AskAsync(web.Dispatcher, web.Session, TorrentMessageTypes.Details, new TorrentJobRequest { JobId = inside.Id })).GetPayload<TorrentDetailsResult>();
        Assert.True(details.Ok, details.Error);
        var pipe = (await AskAsync(web.Dispatcher, web.Pipe, TorrentMessageTypes.Options, new TorrentJobRequest { JobId = outside.Id })).GetPayload<TorrentOptionsResult>();
        Assert.True(pipe.Ok, pipe.Error);
    }
}
