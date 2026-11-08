using System.Collections.Concurrent;
using System.Diagnostics;
using DownloadManager.Core;
using DownloadManager.Persistence;
using TestServer;
using Xunit.Abstractions;

namespace DownloadManager.Engine.Tests;

/// <summary>UI.B7: jobs on one host share its connections, and a job holding none says so.</summary>
[Collection("http-server")]
public sealed class HostFairnessTests(TestServerFixture fixture, ITestOutputHelper output) : IDisposable
{
    private const int Megabyte = 1024 * 1024;
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    private readonly TestServerHost _server = fixture.Server;
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "test-work", Guid.NewGuid().ToString("N"));
    private readonly List<HttpDownloadEngine> _engines = [];

    private string Destination => Path.Combine(_root, "downloads");
    private StoragePaths Paths => new(Path.Combine(_root, "state"));
    private JsonCheckpointStore Checkpoints => new(Paths);
    private string Host => new Uri(_server.BaseAddress).Host;

    public void Dispose()
    {
        foreach (var engine in _engines) engine.Dispose();
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    private HttpDownloadEngine Engine(int hostLimit, TimeSpan? checkpointInterval = null)
    {
        Directory.CreateDirectory(Destination);
        Paths.EnsureCreated();
        var engine = new HttpDownloadEngine(
            new EngineOptions
            {
                MaxAttempts = 3,
                InitialRetryDelay = TimeSpan.FromMilliseconds(10),
                MaxRetryDelay = TimeSpan.FromMilliseconds(50),
                ReadTimeout = TimeSpan.FromSeconds(20),
                ConnectTimeout = TimeSpan.FromSeconds(5),
                AllowLocalNetwork = true,
                MaxConnectionsPerHost = hostLimit,
                CheckpointInterval = checkpointInterval ?? TimeSpan.FromSeconds(5),
            },
            checkpoints: Checkpoints);
        _engines.Add(engine);
        return engine;
    }

    private DownloadJob Job(string name, int size, int seed, int bps, int connections)
    {
        var final = Path.Combine(Destination, name);
        return new DownloadJob
        {
            Url = $"{_server.BaseAddress}/file/{name}?size={size}&seed={seed}&bps={bps}",
            FileName = name,
            DestinationDirectory = Destination,
            FinalFilePath = final,
            PartFilePath = final + $".{Guid.NewGuid():N}.part",
            RequestedConnections = connections,
        };
    }

    private static string Sha256Of(int seed, int size)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(TestContent.Generate(seed, size))).ToLowerInvariant();

    private static async Task AssertFileAsync(DownloadResult result, int seed, int size)
    {
        Assert.True(result.Success, result.Failure?.Message);
        Assert.Equal(Sha256Of(seed, size), await Hashing.Sha256FileAsync(result.FinalFilePath!));
    }

    private static async Task UntilAsync(Func<bool> condition, string what)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(clock.Elapsed < Patience, $"Timed out waiting for {what}.");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task ThirdJobOnAHostGetsAConnectionWhileTwoSegmentedJobsHoldTheRest()
    {
        const int big = 8 * Megabyte;
        const int small = 2 * Megabyte;
        var engine = Engine(hostLimit: 8);
        var a = Job("fair-a.bin", big, 801, 256 * 1024, 4);
        var b = Job("fair-b.bin", big, 802, 256 * 1024, 4);
        var c = Job("fair-c.bin", small, 803, 256 * 1024, 4);

        var aRun = engine.ExecuteAsync(a, null, null, CancellationToken.None);
        var bRun = engine.ExecuteAsync(b, null, null, CancellationToken.None);
        await UntilAsync(() => engine.HostConnectionsActive == 8, "the first two jobs to hold every connection");

        // Before the fix C waited here on "Connecting" until A or B finished.
        var cFirstByte = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cRun = engine.ExecuteAsync(c, null, new SyncProgress(p =>
        {
            if (p.ReceivedBytes > 0) cFirstByte.TrySetResult();
        }), CancellationToken.None);
        await cFirstByte.Task.WaitAsync(Patience);
        Assert.False(aRun.IsCompleted, "A finished before C got a byte, so this proves nothing.");
        Assert.False(bRun.IsCompleted, "B finished before C got a byte, so this proves nothing.");

        await AssertFileAsync(await cRun.WaitAsync(Patience), 803, small);
        await AssertFileAsync(await aRun.WaitAsync(Patience), 801, big);
        await AssertFileAsync(await bRun.WaitAsync(Patience), 802, big);
        Assert.True(engine.HostConnectionPeak <= 8, $"peak {engine.HostConnectionPeak}");
        Assert.Equal(0, engine.HostConnectionsActive);
    }

    /// <summary>
    /// A connection given up for another job leaves the range from its last written byte for the job's other
    /// workers, and the checkpoint saved at that moment is enough to finish the file from, as after a kill.
    /// </summary>
    [Fact]
    public async Task YieldedSegmentIsResplitAndTheFileMatches()
    {
        const int size = 8 * Megabyte;
        // No checkpoint on the timer after the first write's: a later one while A runs was saved by the yield.
        var engine = Engine(hostLimit: 4, checkpointInterval: TimeSpan.FromHours(1));
        var a = Job("yield-a.bin", size, 811, 256 * 1024, 4);
        var b = Job("yield-b.bin", 512 * 1024, 812, 256 * 1024, 1);
        var aBytes = 0L;
        var aConnections = new ConcurrentQueue<int>();
        using var stopA = new CancellationTokenSource();
        var aRun = engine.ExecuteAsync(a, null, new SyncProgress(p =>
        {
            Volatile.Write(ref aBytes, p.ReceivedBytes);
            aConnections.Enqueue(p.ActiveConnections);
        }), stopA.Token);
        await UntilAsync(() => engine.HostConnectionsActive == 4 && Volatile.Read(ref aBytes) > Megabyte / 2, "A to hold every connection");
        var early = (await Checkpoints.LoadAsync(a.Id))?.CompletedBytes ?? 0;

        var bFirstByte = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bRun = engine.ExecuteAsync(b, null, new SyncProgress(p =>
        {
            if (p.ReceivedBytes > 0) bFirstByte.TrySetResult();
        }), CancellationToken.None);
        await bFirstByte.Task.WaitAsync(Patience);
        Assert.False(aRun.IsCompleted);

        DownloadCheckpoint? atYield = null;
        await UntilAsync(() => (atYield = Checkpoints.LoadAsync(a.Id).GetAwaiter().GetResult()) is { } saved && saved.CompletedBytes > early,
            "the yield's checkpoint");
        Assert.True(atYield!.CompletedBytes > 0);
        AssertCoversTheFile(atYield, size);
        output.WriteLine($"At the yield: {atYield.Segments.Count} segments, {atYield.CompletedBytes} bytes recorded.");

        // Stop A as a kill right after that save would, and go on from exactly that checkpoint.
        stopA.Cancel();
        var stopped = await aRun.WaitAsync(Patience);
        Assert.Equal(DownloadState.Cancelled, stopped.FinalState);
        await AssertFileAsync(await bRun.WaitAsync(Patience), 812, 512 * 1024);
        await Checkpoints.SaveAsync(atYield);

        var resumed = await Engine(hostLimit: 4).ExecuteAsync(a, await Checkpoints.LoadAsync(a.Id), null, CancellationToken.None);
        await AssertFileAsync(resumed, 811, size);
        Assert.Contains(aConnections, count => count == 4);
        Assert.Contains(aConnections, count => count < 4);
    }

    /// <summary>The checkpoint's ranges tile the file exactly, each with no more done than it holds.</summary>
    private static void AssertCoversTheFile(DownloadCheckpoint checkpoint, long size)
    {
        Assert.Equal(size, checkpoint.TotalBytes);
        var next = 0L;
        foreach (var segment in checkpoint.Segments.OrderBy(segment => segment.Start))
        {
            Assert.Equal(next, segment.Start);
            Assert.InRange(segment.Completed, 0, segment.End - segment.Start + 1);
            next = segment.End + 1;
        }

        Assert.Equal(size, next);
    }

    [Fact]
    public async Task WaitingForHostIsReportedOnlyWhileTheJobHoldsNoSlot()
    {
        const int size = Megabyte;
        var engine = Engine(hostLimit: 2);
        var notices = new ConcurrentQueue<(Guid Job, string? Host)>();
        engine.HostWaitChanged += job => notices.Enqueue((job, engine.WaitingForHost(job)));

        // One stream can't yield, so a third one waits for real.
        var x = Job("wait-x.bin", size, 821, 512 * 1024, 1);
        var y = Job("wait-y.bin", size, 822, 512 * 1024, 1);
        var z = Job("wait-z.bin", size, 823, 0, 1);
        var xRun = engine.ExecuteAsync(x, null, null, CancellationToken.None);
        var yRun = engine.ExecuteAsync(y, null, null, CancellationToken.None);
        await UntilAsync(() => engine.HostConnectionsActive == 2, "both streams to hold a connection");
        var zRun = engine.ExecuteAsync(z, null, null, CancellationToken.None);
        await UntilAsync(() => engine.WaitingForHost(z.Id) == Host, "Z to be waiting");
        Assert.Null(engine.WaitingForHost(x.Id));
        Assert.Null(engine.WaitingForHost(y.Id));
        Assert.False(zRun.IsCompleted);

        await AssertFileAsync(await zRun.WaitAsync(Patience), 823, size);
        Assert.Null(engine.WaitingForHost(z.Id));
        await AssertFileAsync(await xRun.WaitAsync(Patience), 821, size);
        await AssertFileAsync(await yRun.WaitAsync(Patience), 822, size);
        Assert.Contains((z.Id, Host), notices);
        Assert.Contains((z.Id, (string?)null), notices);
        Assert.DoesNotContain(notices, notice => notice.Job != z.Id && notice.Host is not null);

        // A segmented job with more workers than connections holds some, so it is never "waiting".
        var w = Job("wait-w.bin", 3 * Megabyte, 824, 512 * 1024, 4);
        var seenWaiting = false;
        var wRun = engine.ExecuteAsync(w, null, null, CancellationToken.None);
        while (!wRun.IsCompleted)
        {
            if (engine.WaitingForHost(w.Id) is not null) seenWaiting = true;
            await Task.Delay(5);
        }

        await AssertFileAsync(await wRun, 824, 3 * Megabyte);
        Assert.Equal(2, engine.HostConnectionPeak);
        Assert.False(seenWaiting, "W was reported waiting, though it always held a connection while its other workers waited.");
        Assert.DoesNotContain(notices, notice => notice.Job == w.Id && notice.Host is not null);
    }

    /// <summary>UI.R21: a running segmented job takes a raised connection count at once, and a lowered one adds none.</summary>
    [Fact]
    public async Task ConnectionCountChangesApplyToARunningSegmentedJob()
    {
        const int size = 8 * Megabyte;
        var engine = Engine(hostLimit: 8);
        var job = Job("live-connections.bin", size, 831, 256 * 1024, 2);
        var connections = 0;
        var bytes = 0L;
        var afterLowering = new ConcurrentQueue<int>();
        var lowered = false;
        var run = engine.ExecuteAsync(job, null, new SyncProgress(p =>
        {
            Volatile.Write(ref connections, p.ActiveConnections);
            Volatile.Write(ref bytes, p.ReceivedBytes);
            if (Volatile.Read(ref lowered)) afterLowering.Enqueue(p.ActiveConnections);
        }), CancellationToken.None);
        await UntilAsync(() => Volatile.Read(ref bytes) > Megabyte / 2, "the job to be under way");
        Assert.True(Volatile.Read(ref connections) <= 2);

        job.RequestedConnections = 6;
        engine.ConnectionsChanged(job.Id);
        await UntilAsync(() => Volatile.Read(ref connections) >= 5, "the new workers to connect");

        job.RequestedConnections = 1;
        engine.ConnectionsChanged(job.Id);
        var atLowering = Volatile.Read(ref connections);
        Volatile.Write(ref lowered, true);
        await AssertFileAsync(await run.WaitAsync(Patience), 831, size);
        Assert.All(afterLowering, count => Assert.True(count <= atLowering, $"{count} connections after lowering from {atLowering}"));
        Assert.Equal(0, engine.HostConnectionsActive);
    }

    /// <summary>
    /// UI.R21 "Apply to all": a provider job's transfer goes to the link of its run (<see cref="DownloadJob.ResolvedUrl"/>)
    /// while its own address is a reference. A raised count reaches it the same way, and the new workers still queue
    /// for the host's connections: with a host limit of 3, a count of 8 never has more than 3 out.
    /// </summary>
    [Fact]
    public async Task ARaisedCountOnAProviderLinkStaysWithinTheHostLimit()
    {
        const int size = 6 * Megabyte;
        var engine = Engine(hostLimit: 3);
        var job = ProviderJob("live-host.bin", size, 861, 256 * 1024, 2);
        var bytes = 0L;
        var most = 0;
        var run = engine.ExecuteAsync(job, null, new SyncProgress(p =>
        {
            Volatile.Write(ref bytes, p.ReceivedBytes);
            InterlockedMax(ref most, p.ActiveConnections);
        }), CancellationToken.None);
        await UntilAsync(() => Volatile.Read(ref bytes) > Megabyte / 2, "the job to be under way");

        job.RequestedConnections = 8;
        engine.ConnectionsChanged(job.Id);
        await UntilAsync(() => Volatile.Read(ref most) >= 3 || run.IsCompleted, "a third connection");
        // Two connections need about 11 more seconds, so a run that ends first never took the raised count.
        Assert.True(Volatile.Read(ref most) >= 3, $"the raised count never reached the transfer (at most {most} connections)");

        await AssertFileAsync(await run.WaitAsync(Patience), 861, size);
        Assert.True(engine.HostConnectionPeak <= 3, $"host peak {engine.HostConnectionPeak}");
        Assert.Equal(3, Volatile.Read(ref most));
        Assert.Equal(0, engine.HostConnectionsActive);
    }

    /// <summary>
    /// Kill at any point while a connection change splits a provider job's ranges: the checkpoint on disk and the
    /// part file, taken as a killed process leaves them (checkpoint first, then the file, which only gains bytes),
    /// must resume to exactly the right file, here with yet another count. Snapshots are taken while the new
    /// workers connect and split, and after the count is lowered again.
    /// </summary>
    [Fact]
    public async Task EveryCheckpointDuringAConnectionChangeResumesToTheRightFile()
    {
        const int size = 6 * Megabyte;
        var engine = Engine(hostLimit: 8, checkpointInterval: TimeSpan.FromMilliseconds(100));
        var job = ProviderJob("live-kill.bin", size, 871, 256 * 1024, 2);
        var bytes = 0L;
        var run = engine.ExecuteAsync(job, null, new SyncProgress(p => Volatile.Write(ref bytes, p.ReceivedBytes)), CancellationToken.None);
        await UntilAsync(() => Volatile.Read(ref bytes) > Megabyte / 2, "the job to be under way");

        var snapshots = new List<(DownloadCheckpoint Checkpoint, string Part)>();
        async Task SnapshotAsync()
        {
            var checkpoint = await ReadCheckpointAsync(job.Id);
            if (checkpoint is null || run.IsCompleted) return;
            var copy = Path.Combine(_root, $"snapshot-{snapshots.Count}.part");
            await using (var source = new FileStream(job.PartFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            await using (var target = File.Create(copy))
            {
                await source.CopyToAsync(target);
            }

            snapshots.Add((checkpoint, copy));
        }

        job.RequestedConnections = 8;
        engine.ConnectionsChanged(job.Id);
        for (var i = 0; i < 6; i++)
        {
            await SnapshotAsync();
            await Task.Delay(40 * (i + 1));
        }

        job.RequestedConnections = 1;
        engine.ConnectionsChanged(job.Id);
        for (var i = 0; i < 3; i++)
        {
            await SnapshotAsync();
            await Task.Delay(150);
        }

        await AssertFileAsync(await run.WaitAsync(Patience), 871, size);
        Assert.True(snapshots.Count >= 3, $"only {snapshots.Count} snapshots before the job finished");
        Assert.Contains(snapshots, snapshot => snapshot.Checkpoint.Segments.Count > 2);

        var resumer = Engine(hostLimit: 8);
        for (var i = 0; i < snapshots.Count; i++)
        {
            var (checkpoint, part) = snapshots[i];
            var resumed = ProviderJob($"live-kill-{i}.bin", size, 871, 0, 3);
            resumed.Id = Guid.NewGuid();
            File.Move(part, resumed.PartFilePath);
            checkpoint.JobId = resumed.Id;
            await AssertFileAsync(await resumer.ExecuteAsync(resumed, checkpoint, null, CancellationToken.None).WaitAsync(Patience), 871, size);
        }
    }

    private async Task<DownloadCheckpoint?> ReadCheckpointAsync(Guid id)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await Checkpoints.LoadAsync(id);
            }
            catch (IOException) when (attempt < 20)
            {
                // Replaced under us; a killed process leaves either version whole.
                await Task.Delay(5);
            }
        }
    }

    /// <summary>A provider job as the runtime runs it: a reference address, and the run's link to transfer from.</summary>
    private DownloadJob ProviderJob(string name, int size, int seed, int bps, int connections)
    {
        var job = Job(name, size, seed, bps, connections);
        job.ResolvedUrl = job.Url;
        job.Url = $"remote://torbox/torrents/77/{seed}";
        return job;
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }

    /// <summary>
    /// A segmented job whose worker still waits for a connection when its last range is nearly done must not
    /// wait for it once nothing is left to take: the free connection can go to another job first, and before the
    /// fix the finished job then sat in its run until some other download let a connection go.
    /// </summary>
    [Fact]
    public async Task AWorkerWithNothingLeftStopsWaitingForAConnection()
    {
        const int small = 2 * Megabyte;
        const int large = 3 * Megabyte;
        var engine = Engine(hostLimit: 2);
        using var stopOthers = new CancellationTokenSource();
        // Y holds one connection for a long time and never yields.
        var y = Job("idle-y.bin", large, 851, 256 * 1024, 1);
        var yRun = engine.ExecuteAsync(y, null, null, stopOthers.Token);
        await UntilAsync(() => engine.HostConnectionsActive == 1, "Y to hold a connection");

        // X gets the other connection; its second worker waits, holding no range.
        var x = Job("idle-x.bin", small, 852, 1024 * 1024, 2);
        var xBytes = 0L;
        var xRun = engine.ExecuteAsync(x, null, new SyncProgress(p => Volatile.Write(ref xBytes, p.ReceivedBytes)), CancellationToken.None);
        // Bytes, not just a connection: the probe's connection is not yet a worker's.
        await UntilAsync(() => Volatile.Read(ref xBytes) > 0, "X to be under way on the other connection");

        // Z waits too, from before X's first range ends: when X's last range ends, the free connection goes to
        // Z (holding none, waiting longer) rather than to X's worker that asked again after its first range.
        var z = Job("idle-z.bin", large, 853, 256 * 1024, 1);
        var zRun = engine.ExecuteAsync(z, null, null, stopOthers.Token);
        await UntilAsync(() => engine.WaitingForHost(z.Id) == Host, "Z to be waiting");
        Assert.True(Volatile.Read(ref xBytes) < small / 2, "X's first range ended before Z waited, so this proves nothing.");

        // X's bytes take about two seconds; Y needs about twelve. X must not wait for it.
        await AssertFileAsync(await xRun.WaitAsync(TimeSpan.FromSeconds(8)), 852, small);
        Assert.False(yRun.IsCompleted, "Y finished first, so this proves nothing.");
        Assert.False(zRun.IsCompleted, "Z finished first, so this proves nothing.");
        Assert.Null(engine.WaitingForHost(x.Id));

        stopOthers.Cancel();
        await Task.WhenAll(yRun, zRun).WaitAsync(Patience);
        await UntilAsync(() => engine.HostConnectionsActive == 0, "every connection to come back");
    }

    private sealed class SyncProgress(Action<TransferProgress> report) : IProgress<TransferProgress>
    {
        public void Report(TransferProgress value) => report(value);
    }
}
