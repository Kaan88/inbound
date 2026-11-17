using System.Diagnostics;
using DownloadManager.Core;
using DownloadManager.Persistence;
using TestServer;

namespace DownloadManager.Engine.Tests;

/// <summary>
/// CONN.2: a download that began on one connection (one stream, its checkpoint a single range) must split when its
/// count is raised: at once while it runs, and on resume after a stop. Before the fix it stayed on one range
/// "0 B – total" for good, while the window said "Connections (4)".
/// </summary>
[Collection("http-server")]
public sealed class ConnectionRaiseTests(TestServerFixture fixture) : IDisposable
{
    private const int Megabyte = 1024 * 1024;
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    private readonly TestServerHost _server = fixture.Server;
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "test-work", Guid.NewGuid().ToString("N"));
    private readonly List<HttpDownloadEngine> _engines = [];

    private string Destination => Path.Combine(_root, "downloads");
    private StoragePaths Paths => new(Path.Combine(_root, "state"));
    private JsonCheckpointStore Checkpoints => new(Paths);

    public void Dispose()
    {
        foreach (var engine in _engines) engine.Dispose();
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    private HttpDownloadEngine Engine()
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
                MaxConnectionsPerHost = 8,
                CheckpointInterval = TimeSpan.FromMilliseconds(200),
            },
            checkpoints: Checkpoints);
        _engines.Add(engine);
        return engine;
    }

    private DownloadJob Job(string name, int size, int seed, int bps, bool provider)
    {
        var final = Path.Combine(Destination, name);
        var address = $"{_server.BaseAddress}/file/{name}?size={size}&seed={seed}&bps={bps}";
        var job = new DownloadJob
        {
            Url = address,
            FileName = name,
            DestinationDirectory = Destination,
            FinalFilePath = final,
            PartFilePath = final + $".{Guid.NewGuid():N}.part",
            RequestedConnections = 1,
        };
        if (provider)
        {
            // As the runtime runs a provider job: a reference address, the run's link, and the size the provider gave.
            job.ResolvedUrl = address;
            job.Url = $"remote://torbox/torrents/77/{seed}";
            job.ExpectedTotalBytes = size;
        }

        return job;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARaisedCountSplitsASingleStreamThatIsRunning(bool provider)
    {
        const int size = 8 * Megabyte;
        var engine = Engine();
        var job = Job($"raise-live-{provider}.bin", size, provider ? 901 : 902, 256 * 1024, provider);
        var connections = 0;
        var bytes = 0L;
        var least = long.MaxValue;
        var raised = false;
        var run = engine.ExecuteAsync(job, null, new SyncProgress(p =>
        {
            InterlockedMax(ref connections, p.ActiveConnections);
            Interlocked.Exchange(ref bytes, p.ReceivedBytes);
            if (Volatile.Read(ref raised)) InterlockedMin(ref least, p.ReceivedBytes);
        }), CancellationToken.None);
        await UntilAsync(() => Interlocked.Read(ref bytes) > Megabyte / 2, "the single stream to be under way");
        Assert.True(Volatile.Read(ref connections) <= 1);

        var atRaise = Interlocked.Read(ref bytes);
        Volatile.Write(ref raised, true);
        job.RequestedConnections = 4;
        engine.ConnectionsChanged(job.Id);
        await UntilAsync(() => Volatile.Read(ref connections) >= 3 || run.IsCompleted, "the raised count to reach the transfer");
        Assert.True(Volatile.Read(ref connections) >= 3, $"the single stream never split (at most {connections} connections)");

        // Lowering again still works on the split transfer.
        job.RequestedConnections = 1;
        engine.ConnectionsChanged(job.Id);

        await AssertFileAsync(await run.WaitAsync(Patience), provider ? 901 : 902, size);
        Assert.True(Interlocked.Read(ref least) >= atRaise,
            $"the transfer went back to {Interlocked.Read(ref least)} bytes from {atRaise}: it started over");
        Assert.Equal(0, engine.HostConnectionsActive);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AResumedSingleStreamCheckpointSplitsToTheRaisedCount(bool provider)
    {
        const int size = 8 * Megabyte;
        var seed = provider ? 911 : 912;
        var engine = Engine();
        var job = Job($"raise-resume-{provider}.bin", size, seed, 512 * 1024, provider);
        var bytes = 0L;
        using (var stop = new CancellationTokenSource())
        {
            var first = engine.ExecuteAsync(job, null, new SyncProgress(p => Interlocked.Exchange(ref bytes, p.ReceivedBytes)), stop.Token);
            await UntilAsync(() => Interlocked.Read(ref bytes) > Megabyte, "the single stream to be under way");
            stop.Cancel();
            var stopped = await first.WaitAsync(Patience);
            Assert.Equal(DownloadState.Cancelled, stopped.FinalState);
        }

        var checkpoint = await Checkpoints.LoadAsync(job.Id);
        Assert.NotNull(checkpoint);
        Assert.Single(checkpoint!.Segments);
        var saved = checkpoint.CompletedBytes;
        Assert.True(saved > 0);

        job.RequestedConnections = 4;
        var connections = 0;
        var least = long.MaxValue;
        var resumed = await engine.ExecuteAsync(job, checkpoint, new SyncProgress(p =>
        {
            InterlockedMax(ref connections, p.ActiveConnections);
            InterlockedMin(ref least, p.ReceivedBytes);
        }), CancellationToken.None).WaitAsync(Patience);
        await AssertFileAsync(resumed, seed, size);
        Assert.True(Volatile.Read(ref connections) >= 3, $"the resume never split (at most {connections} connections)");
        Assert.True(Interlocked.Read(ref least) >= saved,
            $"the resume went back to {Interlocked.Read(ref least)} bytes from the {saved} saved: it started over");
    }

    /// <summary>A kill right after the resume split: the checkpoint on disk then has the split ranges, and resumes.</summary>
    [Fact]
    public async Task TheSplitCheckpointOfAResumedSingleStreamResumesToTheRightFile()
    {
        const int size = 8 * Megabyte;
        const int seed = 921;
        var engine = Engine();
        var job = Job("raise-kill.bin", size, seed, 512 * 1024, provider: true);
        var bytes = 0L;
        using (var stop = new CancellationTokenSource())
        {
            var first = engine.ExecuteAsync(job, null, new SyncProgress(p => Interlocked.Exchange(ref bytes, p.ReceivedBytes)), stop.Token);
            await UntilAsync(() => Interlocked.Read(ref bytes) > Megabyte, "the single stream to be under way");
            stop.Cancel();
            await first.WaitAsync(Patience);
        }

        job.RequestedConnections = 4;
        var single = await Checkpoints.LoadAsync(job.Id);
        bytes = 0;
        using (var stop = new CancellationTokenSource())
        {
            var second = engine.ExecuteAsync(job, single, new SyncProgress(p => Interlocked.Exchange(ref bytes, p.ReceivedBytes)), stop.Token);
            await UntilAsync(() => Interlocked.Read(ref bytes) > single!.CompletedBytes + Megabyte / 2, "the split resume to be under way");
            stop.Cancel();
            await second.WaitAsync(Patience);
        }

        var split = await Checkpoints.LoadAsync(job.Id);
        Assert.NotNull(split);
        Assert.True(split!.Segments.Count >= 3, $"{split.Segments.Count} ranges after the split resume");
        Assert.True(split.CompletedBytes >= single!.CompletedBytes);

        job.ResolvedUrl = $"{_server.BaseAddress}/file/raise-kill.bin?size={size}&seed={seed}&bps=0";
        job.RequestedConnections = 2;
        var least = long.MaxValue;
        var result = await Engine().ExecuteAsync(job, split, new SyncProgress(p => InterlockedMin(ref least, p.ReceivedBytes)),
            CancellationToken.None).WaitAsync(Patience);
        await AssertFileAsync(result, seed, size);
        Assert.True(Interlocked.Read(ref least) >= split.CompletedBytes);
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

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }

    private static void InterlockedMin(ref long target, long value)
    {
        long current;
        while (value < (current = Interlocked.Read(ref target)) && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }

    private sealed class SyncProgress(Action<TransferProgress> report) : IProgress<TransferProgress>
    {
        public void Report(TransferProgress value) => report(value);
    }
}
