using System.Security.Cryptography;
using DownloadManager.Contracts;
using DownloadManager.Core;
using DownloadManager.Persistence;
using DownloadManager.Runtime;
using DownloadManager.Runtime.Ipc;
using DownloadManager.Torrents;

namespace DownloadManager.Integration.Tests;

/// <summary>T2: seeding and the details commands through the dispatcher, on the in-memory torrent engine.</summary>
public sealed partial class HostCommandDispatcherTests
{
    private async Task<(DownloadManagerService Service, HostCommandDispatcher Dispatcher, JobDto Job)> CompletedTorrentAsync(bool seedAfterDownload = true)
    {
        var service = await TorrentServiceAsync();
        if (!seedAfterDownload)
        {
            var settings = service.GetSettings();
            settings.TorrentSeedAfterDownload = false;
            Assert.True((await service.UpdateSettingsAsync(settings)).Ok);
        }

        var dispatcher = Dispatcher(service);
        var hash = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(20));
        var job = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Add, new TorrentAddRequest
        {
            Input = $"magnet:?xt=urn:btih:{hash}&dn=Seeded", Destination = Path.Combine(_root, "down"),
        })).GetPayload<TorrentAddResult>().Job!;
        var worker = await UntilAsync(() => _torrentWorkers.FirstOrDefault(w => w.Torrents.Count == 1));
        for (var i = 0; i < 4; i++) worker.Advance();
        var done = await UntilAsync(() => service.GetSnapshot().Jobs.FirstOrDefault(j => j.Id == job.Id && j.State == "completed"));
        return (service, dispatcher, done);
    }

    [Fact]
    public async Task ACompletedTorrentSeedsWithoutADownloadSlotAndAnswersPeersTrackersAndPieces()
    {
        var (service, dispatcher, job) = await CompletedTorrentAsync();
        var seeding = await UntilAsync(() => service.GetSnapshot().Jobs.FirstOrDefault(j => j.Id == job.Id && j.Torrent!.Seeding));
        Assert.Equal("completed", seeding.State);
        Assert.NotNull(seeding.Torrent!.SeedingSince);
        Assert.False(seeding.CanPause); // a finished download: nothing for Stop or the queue to count
        // The flag is set before the torrent reaches the worker; the details answer once it is there.
        TorrentPeersResult peers;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        do
        {
            peers = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Peers, new TorrentJobRequest { JobId = job.Id })).GetPayload<TorrentPeersResult>();
            if (!peers.Running) await Task.Delay(20);
        } while (!peers.Running && DateTime.UtcNow < deadline);
        Assert.True(peers is { Ok: true, Running: true }, peers.Error);
        Assert.Equal("Fake 1.0", Assert.Single(peers.Peers).Client);
        var trackers = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Trackers, new TorrentJobRequest { JobId = job.Id })).GetPayload<TorrentTrackersResult>();
        Assert.Equal("working", Assert.Single(trackers.Trackers).Status);
        var pieces = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Pieces, new TorrentJobRequest { JobId = job.Id })).GetPayload<TorrentPiecesResult>();
        Assert.Equal((true, 4, 4), (pieces.Running, pieces.Count, pieces.Have));

        // Uploads show on the job, and the default ratio 1.0 ends the seeding.
        var worker = _torrentWorkers.Single(w => w.Torrents.Count == 1);
        var id = job.Id.ToString("N");
        worker.Upload(id, FakeTorrentEngine.FakeFileSize / 2);
        var half = await UntilAsync(() => service.GetSnapshot().Jobs.FirstOrDefault(j => j.Id == job.Id && j.Torrent!.Uploaded > 0));
        Assert.Equal(0.5, half.Torrent!.Ratio);
        worker.Upload(id, FakeTorrentEngine.FakeFileSize);
        var ended = await UntilAsync(() => service.GetSnapshot().Jobs.FirstOrDefault(j => j.Id == job.Id && !j.Torrent!.Seeding));
        Assert.Equal(1.0, ended.Torrent!.Ratio);
        Assert.Equal("completed", ended.State);
        await UntilAsync(() => worker.Exited.IsCompleted ? worker : null);

        // Off the worker, the details say it is not running.
        var after = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Peers, new TorrentJobRequest { JobId = job.Id })).GetPayload<TorrentPeersResult>();
        Assert.True(after is { Ok: true, Running: false });
        Assert.Empty(after.Peers);
        var unknown = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Pieces, new TorrentJobRequest { JobId = Guid.NewGuid() })).GetPayload<TorrentPiecesResult>();
        Assert.False(unknown.Ok);
    }

    [Fact]
    public async Task ASeedingFaultIsSaidOnTheRowUntilItSeedsAgain()
    {
        // Walk 2026-11-12: a recheck that found damage stopped seeding, and the row only said "Completed".
        var (service, dispatcher, job) = await CompletedTorrentAsync();
        await UntilAsync(() => service.GetSnapshot().Jobs.FirstOrDefault(j => j.Id == job.Id && j.Torrent!.Seeding));
        var worker = await UntilAsync(() => _torrentWorkers.FirstOrDefault(w => w.Torrents.Count == 1));
        worker.Inject(new System.Text.Json.Nodes.JsonObject
        {
            ["event"] = "error", ["kind"] = "torrent", ["id"] = job.Id.ToString("N"), ["message"] = "the file check found damaged pieces",
        });
        var stopped = await UntilAsync(() => service.GetSnapshot().Jobs.FirstOrDefault(j => j.Id == job.Id && !j.Torrent!.Seeding));
        Assert.Equal("completed", stopped.State);
        Assert.Contains("damaged pieces", stopped.StatusNote);
        var saved = await UntilAsync(() => new JsonJobStore(_paths).LoadAllAsync().GetAwaiter().GetResult().FirstOrDefault(j => j.Id == job.Id
            && j.Torrent is { Seeding: false, SeedingStoppedNote: not null }));
        Assert.Contains("damaged pieces", saved.Torrent!.SeedingStoppedNote);

        var on = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Seed, new TorrentSeedRequest { JobId = job.Id, On = true })).GetPayload<TorrentActionResult>();
        Assert.True(on.Ok, on.Error);
        Assert.Null(service.GetSnapshot().Jobs.Single(j => j.Id == job.Id).StatusNote);
    }

    [Fact]
    public async Task AFaultThatComesAfterSeedingWasOffIsStillSaidAndSaved()
    {
        // Review 2026-11-12: a fault reported after seeding was already off was kept only in memory.
        var (service, _, job) = await CompletedTorrentAsync(seedAfterDownload: false);
        Assert.False(job.Torrent!.Seeding);
        await ((ITorrentJobSink)service).SeedingStoppedAsync(job.Id, TorrentSeedingEnd.Failed, "the worker stopped");
        Assert.Equal("the worker stopped", service.GetSnapshot().Jobs.Single(j => j.Id == job.Id).StatusNote);
        var saved = await UntilAsync(() => new JsonJobStore(_paths).LoadAllAsync().GetAwaiter().GetResult().FirstOrDefault(j => j.Id == job.Id
            && j.Torrent is { SeedingStoppedNote: not null }));
        Assert.Equal("the worker stopped", saved.Torrent!.SeedingStoppedNote);
    }

    [Fact]
    public async Task StopAllStopsSeedingAndSeedOnStartsItAgain()
    {
        var (service, dispatcher, job) = await CompletedTorrentAsync();
        await UntilAsync(() => service.GetSnapshot().Jobs.FirstOrDefault(j => j.Id == job.Id && j.Torrent!.Seeding));

        await service.PauseAllAsync();
        await UntilAsync(() => service.GetSnapshot().Jobs.FirstOrDefault(j => j.Id == job.Id && !j.Torrent!.Seeding));
        await UntilAsync(() => _torrentWorkers.All(w => w.Torrents.Count == 0) ? "" : null);
        Assert.Equal("completed", service.GetSnapshot().Jobs.Single(j => j.Id == job.Id).State);

        var on = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Seed, new TorrentSeedRequest { JobId = job.Id, On = true })).GetPayload<TorrentActionResult>();
        Assert.True(on.Ok, on.Error);
        Assert.True(service.GetSnapshot().Jobs.Single(j => j.Id == job.Id).Torrent!.Seeding);
        await UntilAsync(() => _torrentWorkers.FirstOrDefault(w => w.Torrents.Count == 1));

        // Turning torrents off stops it too.
        var settings = service.GetSettings();
        settings.TorrentsEnabled = false;
        Assert.True((await service.UpdateSettingsAsync(settings)).Ok);
        await UntilAsync(() => service.GetSnapshot().Jobs.FirstOrDefault(j => j.Id == job.Id && !j.Torrent!.Seeding));
    }

    [Fact]
    public async Task WithSeedingAfterDownloadOffAFinishedTorrentLeavesTheWorkerUntilAskedToSeed()
    {
        var (service, dispatcher, job) = await CompletedTorrentAsync(seedAfterDownload: false);
        Assert.False(job.Torrent!.Seeding);
        var worker = _torrentWorkers.Single();
        await UntilAsync(() => worker.Exited.IsCompleted ? worker : null);

        var on = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Seed, new TorrentSeedRequest { JobId = job.Id, On = true })).GetPayload<TorrentActionResult>();
        Assert.True(on.Ok, on.Error);
        var seeder = await UntilAsync(() => _torrentWorkers.FirstOrDefault(w => w.Torrents.Count == 1));
        Assert.True(seeder.Adds.Last()["seed"]!.GetValue<bool>());
    }

    [Fact]
    public async Task OnlyAFinishedTorrentCanBeSeeded()
    {
        var service = await TorrentServiceAsync();
        var dispatcher = Dispatcher(service);
        var hash = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(20));
        var job = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Add, new TorrentAddRequest
        {
            Input = $"magnet:?xt=urn:btih:{hash}", Destination = Path.Combine(_root, "down"), StartMode = "later",
        })).GetPayload<TorrentAddResult>().Job!;
        var on = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Seed, new TorrentSeedRequest { JobId = job.Id, On = true })).GetPayload<TorrentActionResult>();
        Assert.False(on.Ok);
        Assert.Contains("finished", on.Error);
        Assert.Empty(_torrentWorkers);
    }

    /// <summary>Two finished torrents with seeding after download off, on a Host whose seeding adds wait for
    /// <paramref name="hold"/>. A's seeding start is asked for and holds the seeding gate on its held add; B's start is
    /// asked for after it and waits for the gate. Returns B's waiting start.</summary>
    private async Task<(DownloadManagerService Service, Guid A, Guid B, Task<TorrentActionResult> BStart)> TwoSeedStartsAsync(TaskCompletionSource hold)
    {
        // Let go in any case, so a failing test cannot hang the Host shutdown on the held add.
        new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token.Register(() => hold.TrySetResult());
        var service = await TorrentServiceAsync(holdSeedAdds: hold.Task);
        var settings = service.GetSettings();
        settings.TorrentSeedAfterDownload = false;
        Assert.True((await service.UpdateSettingsAsync(settings)).Ok);
        var dispatcher = Dispatcher(service);
        var ids = new List<Guid>();
        for (var i = 0; i < 2; i++)
        {
            var hash = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(20));
            ids.Add((await AskAsync(dispatcher, Session(), TorrentMessageTypes.Add, new TorrentAddRequest
            {
                Input = $"magnet:?xt=urn:btih:{hash}&dn=Pair{i}", Destination = Path.Combine(_root, "down"),
            })).GetPayload<TorrentAddResult>().Job!.Id);
        }

        await UntilAsync(() =>
        {
            foreach (var worker in _torrentWorkers.ToArray()) worker.Advance();
            return service.GetSnapshot().Jobs.Count(j => ids.Contains(j.Id) && j.State == "completed") == 2 ? "" : null;
        });

        var (a, b) = (ids[0], ids[1]);
        _ = service.SetTorrentSeedingAsync(new TorrentSeedRequest { JobId = a, On = true });
        await UntilAsync(() => service.GetSnapshot().Jobs.FirstOrDefault(j => j.Id == a && j.Torrent!.Seeding));
        // B's start is recorded before it waits for the gate, which A's held add keeps.
        var bStart = service.SetTorrentSeedingAsync(new TorrentSeedRequest { JobId = b, On = true });
        await Task.Delay(50);
        Assert.False(bStart.IsCompleted);
        Assert.False(service.GetSnapshot().Jobs.Single(j => j.Id == b).Torrent!.Seeding);
        return (service, a, b, bStart);
    }

    private bool SeedAddSent(Guid jobId)
    {
        var id = jobId.ToString("N");
        return _torrentWorkers.SelectMany(w => w.Adds).Any(add => add["id"]!.GetValue<string>() == id && add["seed"]?.GetValue<bool>() == true);
    }

    /// <summary>A Stop All while one seeding start holds the gate and another waits for it stops both: the waiting one
    /// never reaches the worker (review finding: it used to start after the stop).</summary>
    [Fact]
    public async Task StopAllStopsASeedingStartThatIsStillWaiting()
    {
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (service, a, b, bStart) = await TwoSeedStartsAsync(hold);

        await service.PauseAllAsync();
        hold.TrySetResult();

        var refused = await bStart;
        Assert.False(refused.Ok);
        Assert.Equal("Seeding was stopped.", refused.Error);
        await UntilAsync(() => service.GetSnapshot().Jobs.Where(j => j.Id == a || j.Id == b).All(j => !j.Torrent!.Seeding) ? "" : null);
        await UntilAsync(() => _torrentWorkers.All(w => w.Torrents.Count == 0) ? "" : null);
        Assert.False(SeedAddSent(b));
        Assert.All(service.GetSnapshot().Jobs, j => Assert.Equal("completed", j.State));
    }

    /// <summary>Removing a job whose seeding start waits for the gate: the start does nothing once it gets its turn.</summary>
    [Fact]
    public async Task RemovingAJobWhoseSeedingStartWaitsKeepsItOffTheWorker()
    {
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (service, _, b, bStart) = await TwoSeedStartsAsync(hold);

        // The removal also waits for the gate; let the held add go once the removal is on its way.
        var removal = service.RemoveAsync(b, deleteFile: false);
        await Task.Delay(100);
        hold.TrySetResult();
        var removed = await removal;
        Assert.True(removed.Ok, removed.Error);
        Assert.False((await bStart).Ok);

        Assert.DoesNotContain(service.GetSnapshot().Jobs, j => j.Id == b);
        Assert.False(SeedAddSent(b));
        Assert.DoesNotContain(_torrentWorkers.SelectMany(w => w.Torrents), id => id == b.ToString("N"));
    }

    /// <summary>A job saved as seeding whose Host starts with torrents off does not stay marked seeding.</summary>
    [Fact]
    public async Task ASeedingFlagIsClearedWhenTorrentsAreOffAtTheNextStart()
    {
        var (service, _, job) = await CompletedTorrentAsync();
        await UntilAsync(() => service.GetSnapshot().Jobs.FirstOrDefault(j => j.Id == job.Id && j.Torrent!.Seeding));
        await service.ShutdownAsync();
        _service = null;
        // The in-memory engine writes no files; the finished file must be there, or the start marks the job failed.
        var final = job.FinalFilePath;
        if (!File.Exists(final) && !Directory.Exists(final)) await File.WriteAllBytesAsync(final, new byte[16]);

        var next = await TorrentServiceAsync(enabled: false);
        var cleared = await UntilAsync(() => next.GetSnapshot().Jobs.FirstOrDefault(j => j.Id == job.Id && !j.Torrent!.Seeding));
        Assert.Null(cleared.Torrent!.SeedingSince);
        Assert.Equal("completed", cleared.State);
    }

    [Fact]
    public async Task SeedingLimitsMustBeSensible()
    {
        var service = await TorrentServiceAsync();
        var settings = service.GetSettings();
        Assert.Equal((true, 1.0, 48.0, 0L), (settings.TorrentSeedAfterDownload, settings.TorrentSeedRatio, settings.TorrentSeedHours, settings.TorrentUploadLimit));

        settings.TorrentSeedRatio = 0;
        settings.TorrentSeedHours = 0;
        var both = await service.UpdateSettingsAsync(settings);
        Assert.False(both.Ok);
        Assert.Contains("both", both.Error);
        settings.TorrentSeedHours = 2;
        settings.TorrentUploadLimit = -1;
        Assert.False((await service.UpdateSettingsAsync(settings)).Ok);
        settings.TorrentUploadLimit = 50_000;
        var saved = await service.UpdateSettingsAsync(settings);
        Assert.True(saved.Ok, saved.Error);
        Assert.Equal((0.0, 2.0, 50_000L), (saved.Settings!.TorrentSeedRatio, saved.Settings.TorrentSeedHours, saved.Settings.TorrentUploadLimit));
    }
}
