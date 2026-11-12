using System.Text.Json.Nodes;
using DownloadManager.Core;

namespace DownloadManager.Torrents.Tests;

/// <summary>T2: seeding after the download, its ratio and time limits on a manual clock, stops, and the details queries.</summary>
public sealed partial class EngineTests
{
    // ---- in-memory engine ----

    [Fact]
    public async Task ACompletedTorrentSeedsUntilItsRatioAndThenLeavesTheWorker()
    {
        var (engine, supervisor, sink, workers, _) = SeedingEngine();
        await using var owned = supervisor;
        var job = FinishedJob("ratio");
        var id = job.Id.ToString("N");

        Assert.Null(await engine.StartSeedingAsync(job, CancellationToken.None));
        Assert.True(engine.IsSeeding(job.Id));
        var fake = Assert.Single(workers);
        var add = Assert.Single(fake.Adds);
        Assert.True(add["seed"]!.GetValue<bool>());
        Assert.True(add["finalized"]!.GetValue<bool>());
        Assert.True(fake.IsActive(id));

        fake.Upload(id, FakeTorrentEngine.FakeFileSize / 2);
        await WaitForAsync(() => sink.Uploaded.TryGetValue(job.Id, out var up) && up == FakeTorrentEngine.FakeFileSize / 2 ? "" : null, "half uploaded");
        Assert.True(engine.IsSeeding(job.Id));

        fake.Upload(id, FakeTorrentEngine.FakeFileSize);
        await WaitForAsync(() => sink.SeedingEnded.ContainsKey(job.Id) ? "" : null, "the end of seeding");
        Assert.Equal(TorrentSeedingEnd.LimitReached, sink.SeedingEnded[job.Id].Reason);
        Assert.Contains("ratio", sink.SeedingEnded[job.Id].Message);
        Assert.False(engine.IsSeeding(job.Id));
        Assert.Empty(fake.Torrents);
        // Nothing downloads or seeds: the worker closes.
        await WaitForAsync(() => supervisor.State == TorrentWorkerState.Off ? "" : null, "the worker closed");
    }

    [Fact]
    public async Task AFaultInTheSeedingLoopStillTakesTheTorrentOffAndEndsItsSeeding()
    {
        // Audit M17: only two exception types were caught, and the cleanup came after the catch, so anything else left
        // the torrent on the worker and in the seeding list for good.
        var (engine, supervisor, sink, workers, _) = SeedingEngine();
        await using var owned = supervisor;
        var job = FinishedJob("fault");
        var id = job.Id.ToString("N");
        sink.SeedingProgressFault = new ArgumentException("a subscriber failed");

        Assert.Null(await engine.StartSeedingAsync(job, CancellationToken.None));
        var fake = Assert.Single(workers);
        fake.Upload(id, FakeTorrentEngine.FakeFileSize / 4);

        await WaitForAsync(() => sink.SeedingEnded.ContainsKey(job.Id) ? "" : null, "the end of seeding");
        Assert.Equal(TorrentSeedingEnd.Failed, sink.SeedingEnded[job.Id].Reason);
        Assert.Contains("a subscriber failed", sink.SeedingEnded[job.Id].Message);
        Assert.False(engine.IsSeeding(job.Id));
        Assert.Empty(fake.Torrents);
        await engine.StopSeedingAsync(job.Id); // nothing left to wait for
    }

    [Fact]
    public async Task SeedingStopsWhenItsTimeIsUpOnTheClockNotBefore()
    {
        var (engine, supervisor, sink, workers, time) = SeedingEngine();
        await using var owned = supervisor;
        engine.UpdateSeedPolicy(new TorrentSeedPolicy(0, TimeSpan.FromHours(2)));
        var job = FinishedJob("time");
        job.Torrent!.SeedingSince = time.GetUtcNow();

        Assert.Null(await engine.StartSeedingAsync(job, CancellationToken.None));
        time.Advance(TimeSpan.FromMinutes(119));
        await Task.Delay(200);
        Assert.True(engine.IsSeeding(job.Id));
        Assert.False(sink.SeedingEnded.ContainsKey(job.Id));

        time.Advance(TimeSpan.FromMinutes(1));
        await WaitForAsync(() => sink.SeedingEnded.ContainsKey(job.Id) ? "" : null, "the end of seeding");
        Assert.Equal(TorrentSeedingEnd.LimitReached, sink.SeedingEnded[job.Id].Reason);
        Assert.Contains("hours", sink.SeedingEnded[job.Id].Message);
        Assert.Empty(Assert.Single(workers).Torrents);
    }

    [Fact]
    public async Task ATorrentPastItsLimitIsNotAddedAndALoweredLimitStopsOneThatSeeds()
    {
        var (engine, supervisor, sink, workers, time) = SeedingEngine();
        await using var owned = supervisor;
        var late = FinishedJob("late");
        late.Torrent!.SeedingSince = time.GetUtcNow() - TimeSpan.FromHours(49);
        Assert.Null(await engine.StartSeedingAsync(late, CancellationToken.None));
        Assert.Equal(TorrentSeedingEnd.LimitReached, sink.SeedingEnded[late.Id].Reason);
        Assert.Empty(workers);

        var job = FinishedJob("lowered");
        job.Torrent!.Uploaded = FakeTorrentEngine.FakeFileSize / 2;
        Assert.Null(await engine.StartSeedingAsync(job, CancellationToken.None));
        Assert.True(engine.IsSeeding(job.Id));
        engine.UpdateSeedPolicy(new TorrentSeedPolicy(0.25, TimeSpan.Zero));
        await WaitForAsync(() => sink.SeedingEnded.ContainsKey(job.Id) ? "" : null, "the end of seeding");
        Assert.Equal(TorrentSeedingEnd.LimitReached, sink.SeedingEnded[job.Id].Reason);
    }

    [Fact]
    public async Task AStopSavesResumeDataTakesTheTorrentOffAndSaysStopped()
    {
        var (engine, supervisor, sink, workers, _) = SeedingEngine();
        await using var owned = supervisor;
        var job = FinishedJob("stop");
        _seedStore.Resume.TryRemove(job.Id, out _);
        Assert.Null(await engine.StartSeedingAsync(job, CancellationToken.None));

        await engine.StopSeedingAsync(job.Id);
        Assert.Equal(TorrentSeedingEnd.Stopped, sink.SeedingEnded[job.Id].Reason);
        Assert.False(engine.IsSeeding(job.Id));
        Assert.Empty(Assert.Single(workers).Torrents);
        Assert.True(_seedStore.Resume.ContainsKey(job.Id)); // the pause handed over resume data
    }

    [Fact]
    public async Task ADownloadOfTheSameTorrentTakesItOffSeedingFirst()
    {
        var (engine, supervisor, sink, workers, _) = SeedingEngine();
        await using var owned = supervisor;
        var job = FinishedJob("again");
        Assert.Null(await engine.StartSeedingAsync(job, CancellationToken.None));

        // The same job downloading again (the user asked to download it again): seeding ends, then the run adds it.
        job.Torrent!.Finalized = false;
        var run = engine.ExecuteAsync(job, null, null, CancellationToken.None);
        await WaitForAsync(() => sink.SeedingEnded.ContainsKey(job.Id) ? "" : null, "the end of seeding");
        var fake = Assert.Single(workers);
        await WaitForAsync(() => fake.Adds.Count == 2 && fake.Torrents.Count == 1 ? "" : null, "the download's add");
        Assert.Null(fake.Adds[1]["seed"]);
        for (var i = 0; i < 4; i++) fake.Advance();
        Assert.True((await run.WaitAsync(Long)).Success);
    }

    [Fact]
    public async Task ClosingTheEngineTakesSeedingTorrentsOffWithoutEndingTheirSeeding()
    {
        var (engine, supervisor, sink, workers, _) = SeedingEngine();
        var job = FinishedJob("close");
        Assert.Null(await engine.StartSeedingAsync(job, CancellationToken.None));
        await engine.DisposeAsync();
        Assert.False(engine.IsSeeding(job.Id));
        Assert.False(sink.SeedingEnded.ContainsKey(job.Id)); // still marked seeding for the next start
        Assert.True(Assert.Single(workers).Exited.IsCompleted);
    }

    [Fact]
    public async Task ClosingTheEngineStopsAllSeedsAtOnceNotOneAfterAnother()
    {
        // Audit L1: each stop waits 300 ms after the pause's resume data for a later one. Eight in turn take 2.4 s or more.
        var (engine, _, sink, workers, _) = SeedingEngine();
        var jobs = Enumerable.Range(0, 8).Select(i => FinishedJob($"many{i}")).ToList();
        foreach (var job in jobs) Assert.Null(await engine.StartSeedingAsync(job, CancellationToken.None));
        Assert.All(jobs, job => Assert.True(engine.IsSeeding(job.Id)));

        var clock = System.Diagnostics.Stopwatch.StartNew();
        await engine.DisposeAsync();
        clock.Stop();

        Assert.All(jobs, job => Assert.False(engine.IsSeeding(job.Id)));
        Assert.Empty(sink.SeedingEnded);
        Assert.Empty(Assert.Single(workers).Torrents);
        Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(1800), $"closing took {clock.Elapsed.TotalMilliseconds:0} ms");
    }

    [Fact]
    public async Task PeersTrackersAndPiecesAreReadWhileTheTorrentIsOnTheWorkerOnly()
    {
        var (engine, supervisor, _, _, _) = SeedingEngine();
        await using var owned = supervisor;
        var job = FinishedJob("details");
        Assert.Null(await engine.GetPeersAsync(job.Id, CancellationToken.None));
        Assert.Null(await engine.GetPiecesAsync(job.Id, CancellationToken.None));

        Assert.Null(await engine.StartSeedingAsync(job, CancellationToken.None));
        var peer = Assert.Single((await engine.GetPeersAsync(job.Id, CancellationToken.None))!);
        Assert.Equal(("127.0.0.2", 6881, "Fake 1.0"), (peer.Address, peer.Port, peer.Client));
        Assert.Equal(new[] { "seed", "outgoing" }, peer.Flags);
        Assert.Equal(1.0, peer.Progress);
        var tracker = Assert.Single((await engine.GetTrackersAsync(job.Id, CancellationToken.None))!);
        Assert.Equal(("working", 3, 4), (tracker.Status, tracker.Seeds, tracker.Peers));
        var pieces = (await engine.GetPiecesAsync(job.Id, CancellationToken.None))!;
        Assert.Equal((4, 4), (pieces.Count, pieces.Have));
        Assert.Equal(new[] { 0, 3 }, Assert.Single(pieces.Ranges!));
    }

    [Fact]
    public async Task AWorkerOlderThanTheDetailsCommandsIsAskedOnceAndThenNotAgain()
    {
        var asked = 0;
        var store = _seedStore;
        await using var supervisor = new TorrentWorkerSupervisor(() => new OlderWorker(new FakeTorrentEngine(), () => asked++),
            new TorrentSupervisorOptions { DrainDelay = TimeSpan.FromMilliseconds(100) });
        var engine = new TorrentDownloadEngine(supervisor, store, Log);
        engine.SetSink(new Sink());
        var job = FinishedJob("older");
        Assert.Null(await engine.StartSeedingAsync(job, CancellationToken.None));
        Assert.Null(await engine.GetPeersAsync(job.Id, CancellationToken.None));
        Assert.Null(await engine.GetPeersAsync(job.Id, CancellationToken.None));
        Assert.Equal(1, asked);
    }

    [Fact]
    public void PieceRepliesAreReadAsRangesOrABitmap()
    {
        var ranges = TorrentDownloadEngine.ReadPieces(JsonNode.Parse("""{"count":10,"pieceLength":16384,"have":4,"ranges":[[0,2],[7,7],[5,3]]}""")!.AsObject());
        Assert.Equal((10, 16384L, 4), (ranges.Count, ranges.PieceLength, ranges.Have));
        Assert.Equal(2, ranges.Ranges!.Count); // the backwards pair is dropped
        Assert.Null(ranges.Bitmap);
        var bitmap = TorrentDownloadEngine.ReadPieces(JsonNode.Parse("""{"count":8,"pieceLength":1,"have":4,"bitmap":"qg=="}""")!.AsObject());
        Assert.Null(bitmap.Ranges);
        Assert.Equal("qg==", bitmap.Bitmap);
    }

    [Fact]
    public void TheDefaultPolicyIsRatioOneOrFortyEightHours()
    {
        var policy = TorrentSeedPolicy.Default;
        Assert.Equal((1.0, TimeSpan.FromHours(48)), (policy.Ratio, policy.Duration));
        Assert.False(policy.RatioReached(99, 100));
        Assert.True(policy.RatioReached(100, 100));
        var since = DateTimeOffset.UnixEpoch;
        Assert.Equal(TimeSpan.FromHours(1), policy.TimeLeft(since, since + TimeSpan.FromHours(47)));
        Assert.Null(TorrentSeedPolicy.From(2, 0).TimeLeft(since, since));
        Assert.False(TorrentSeedPolicy.From(0, 1).RatioReached(long.MaxValue / 2, 1));
    }

    // ---- native worker on loopback ----

    [WorkerFact]
    public async Task ADownloadedTorrentSeedsToAnotherPeerAndStopsAtItsRatio()
    {
        var size = 2 * 1024 * 1024;
        var (_, torrent) = Payload("shared.bin", size);
        var hash = TestTorrent.LastInfoHash; // thread-static: read before the first await
        await using var seeder = await StartSeederAsync(torrent);
        var port = await seeder.ListenOnLoopbackAsync();
        var store = new MemoryStore();
        await using var supervisor = LoopbackSupervisor();
        var engine = new TorrentDownloadEngine(supervisor, store, Log);
        var sink = new Sink();
        engine.SetSink(sink);
        var job = Job("shared", magnet: $"magnet:?xt=urn:btih:{hash}");
        store.Metadata[job.Id] = torrent;
        var run = engine.ExecuteAsync(job, null, null, CancellationToken.None);
        await AddPeerAsync(supervisor, job, port, run);
        Assert.True((await run.WaitAsync(Long)).Success);

        // The service marks the finished job so (FinalizedAsync), then seeds it.
        job.Torrent!.Finalized = true;
        job.Torrent.WantedBytes = size;
        job.Torrent.HasMetadata = true;
        Assert.Null(await engine.StartSeedingAsync(job, CancellationToken.None));
        Assert.True(engine.IsSeeding(job.Id));
        var listen = await WaitForAsync(() => supervisor.ListenPort is { } p and > 0 ? (object)p : null, "a listen port");

        // Seeding needs the whole torrent: every piece, from the worker's own summary.
        var pieces = (await engine.GetPiecesAsync(job.Id, CancellationToken.None))!;
        Assert.True(pieces.Count > 0);
        Assert.Equal(pieces.Count, pieces.Have);
        Assert.Equal(new[] { 0, pieces.Count - 1 }, Assert.Single(pieces.Ranges!));
        Assert.NotNull(await engine.GetTrackersAsync(job.Id, CancellationToken.None));

        await using var other = Worker.Start(output, "other");
        await other.ListenOnLoopbackAsync();
        await other.Process.SendAsync("setProgressInterval", new JsonObject { ["ms"] = 200 });
        await other.Process.SendAsync("add", new JsonObject { ["id"] = "o", ["savePath"] = Path.Combine(_root, "other"), ["torrent"] = Convert.ToBase64String(torrent) });
        await other.Process.SendAsync("addPeer", new JsonObject { ["id"] = "o", ["address"] = "127.0.0.1", ["port"] = (int)listen });
        // "completed" means every piece arrived from this engine and passed its hash check.
        await other.WaitForEventAsync("completed", "o", Long);

        // Everything it downloaded went out again: ratio 1.0, the default limit, ends the seeding.
        await WaitForAsync(() => sink.SeedingEnded.ContainsKey(job.Id) ? "" : null, "the end of seeding");
        Assert.Equal(TorrentSeedingEnd.LimitReached, sink.SeedingEnded[job.Id].Reason);
        Assert.True(sink.Uploaded[job.Id] >= size);
        Assert.False(engine.IsSeeding(job.Id));
        await WaitForAsync(() => supervisor.State == TorrentWorkerState.Off ? "" : null, "the worker closed");
    }

    // ---- helpers ----

    private readonly MemoryStore _seedStore = new();

    private (TorrentDownloadEngine Engine, TorrentWorkerSupervisor Supervisor, Sink Sink, List<FakeTorrentEngine> Workers, ManualTime Time) SeedingEngine()
    {
        var workers = new List<FakeTorrentEngine>();
        var supervisor = new TorrentWorkerSupervisor(() =>
        {
            var fake = new FakeTorrentEngine();
            lock (workers) workers.Add(fake);
            return fake;
        }, new TorrentSupervisorOptions { DrainDelay = TimeSpan.FromMilliseconds(100) });
        var time = new ManualTime();
        var engine = new TorrentDownloadEngine(supervisor, _seedStore, Log, time);
        var sink = new Sink();
        engine.SetSink(sink);
        return (engine, supervisor, sink, workers, time);
    }

    /// <summary>A torrent job whose download is done and whose files carry their own names.</summary>
    private DownloadJob FinishedJob(string name)
    {
        var job = Job(name);
        job.State = DownloadState.Completed;
        job.Torrent!.InfoHash = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(20));
        job.Torrent.HasMetadata = true;
        job.Torrent.Finalized = true;
        job.Torrent.WantedBytes = FakeTorrentEngine.FakeFileSize;
        _seedStore.Resume[job.Id] = "fake"u8.ToArray();
        _seedStore.Files[job.Id] = [new TorrentFile(0, name + ".bin", FakeTorrentEngine.FakeFileSize, true)];
        return job;
    }

    /// <summary>The in-memory engine, minus the details commands (a worker from before T2).</summary>
    private sealed class OlderWorker(FakeTorrentEngine inner, Action asked) : ITorrentEngine
    {
        public System.Threading.Channels.ChannelReader<JsonObject> Events => inner.Events;
        public Task Exited => inner.Exited;

        public Task<JsonObject> SendAsync(string command, JsonObject? arguments = null, CancellationToken cancellationToken = default)
        {
            if (command is "peers" or "trackers" or "pieces" or "seed")
            {
                asked();
                throw new TorrentCommandException(command, "unknown command");
            }

            return inner.SendAsync(command, arguments, cancellationToken);
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class ManualTime : TimeProvider
    {
        private readonly Lock _sync = new();
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow()
        {
            lock (_sync) return _now;
        }

        public void Advance(TimeSpan by)
        {
            List<ManualTimer> due;
            lock (_sync)
            {
                _now += by;
                due = _timers.Where(timer => timer.Due is { } at && at <= _now).ToList();
                foreach (var timer in due) timer.Due = null;
            }

            foreach (var timer in due) timer.Fire();
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            lock (_sync) _timers.Add(timer);
            return timer;
        }

        private sealed class ManualTimer(ManualTime owner, TimerCallback callback, object? state) : ITimer
        {
            public DateTimeOffset? Due { get; set; }

            public void Fire() => callback(state);

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner._sync) Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
                return true;
            }

            public void Dispose()
            {
                lock (owner._sync)
                {
                    Due = null;
                    owner._timers.Remove(this);
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
