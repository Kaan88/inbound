using System.Diagnostics;
using DownloadManager.Core;
using TestServer;

namespace DownloadManager.Engine.Tests;

[Collection("http-server")]
public sealed class QueueSpeedLimitTests(TestServerFixture fixture) : IDisposable
{
    private readonly TestServerHost _server = fixture.Server;
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "test-work", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task TwoQueuedDownloadsShareOneBudgetIncludingSegmentWorkers()
    {
        const int size = 2 * 1024 * 1024;
        Directory.CreateDirectory(_root);
        var single = Job("single.bin", size, 701, queue: 9, connections: 1);
        var segmented = Job("segments.bin", size, 702, queue: 9, connections: 2);
        using var engine = Engine();
        engine.UpdateQueueSpeedLimits(new Dictionary<int, long> { [9] = 1024 * 1024 });
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        int sawMultipleConnections = 0;
        var progress = new InlineProgress<TransferProgress>(p =>
        {
            if (p.ActiveConnections >= 2) Interlocked.Exchange(ref sawMultipleConnections, 1);
        });
        // 4 MiB at 1 MiB/s takes about four seconds together, versus two with a per-job cap.
        var watch = Stopwatch.StartNew();
        var first = engine.ExecuteAsync(single, null, null, cancel.Token);
        var second = engine.ExecuteAsync(segmented, null, progress, cancel.Token);
        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(24));
        Assert.All(results, r => Assert.True(r.Success, r.Failure?.Message));
        Assert.True(Volatile.Read(ref sawMultipleConnections) != 0, "The second job must exercise multiple segment workers.");
        Assert.True(watch.Elapsed >= TimeSpan.FromSeconds(3.1), $"Combined transfers finished too quickly: {watch.Elapsed}.");
        Assert.Equal(Expected(701, size), await Hashing.Sha256FileAsync(results[0].FinalFilePath!));
        Assert.Equal(Expected(702, size), await Hashing.Sha256FileAsync(results[1].FinalFilePath!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemovingLimitUnblocksActiveTransferAndOtherTrafficBypassesIt(bool omitQueue)
    {
        const int size = 512 * 1024;
        Directory.CreateDirectory(_root);
        var capped = Job("capped.bin", size, 703, queue: 4, connections: 2);
        var other = Job("other.bin", 64 * 1024, 704, queue: 5);
        var detached = Job("detached.bin", 64 * 1024, 705, queue: 4);
        detached.IsQueued = false;
        using var engine = Engine();
        engine.UpdateQueueSpeedLimits(new Dictionary<int, long> { [4] = 16 * 1024 });
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var progressStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new InlineProgress<TransferProgress>(p =>
        {
            if (p.ReceivedBytes > 0) progressStarted.TrySetResult();
        });
        var cappedRun = engine.ExecuteAsync(capped, null, progress, cancel.Token);
        try
        {
            await progressStarted.Task.WaitAsync(TimeSpan.FromSeconds(12));
            var bypasses = await Task.WhenAll(
                engine.ExecuteAsync(other, null, null, cancel.Token),
                engine.ExecuteAsync(detached, null, null, cancel.Token)).WaitAsync(TimeSpan.FromSeconds(12));
            Assert.All(bypasses, r => Assert.True(r.Success, r.Failure?.Message));
            Assert.False(cappedRun.IsCompleted, "The limited queue should still be transferring when the other traffic completes.");
            Assert.Equal(Expected(704, 64 * 1024), await Hashing.Sha256FileAsync(bypasses[0].FinalFilePath!));
            Assert.Equal(Expected(705, 64 * 1024), await Hashing.Sha256FileAsync(bypasses[1].FinalFilePath!));
            engine.UpdateQueueSpeedLimits(omitQueue ? new Dictionary<int, long>() : new Dictionary<int, long> { [4] = 0 });
            var result = await cappedRun.WaitAsync(TimeSpan.FromSeconds(12));
            Assert.True(result.Success, result.Failure?.Message);
            Assert.Equal(Expected(703, size), await Hashing.Sha256FileAsync(result.FinalFilePath!));
        }
        finally
        {
            cancel.Cancel();
            try { await cappedRun; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task MovingAnActiveDownloadOutOfItsQueueReleasesItsCurrentBudgetWait()
    {
        Directory.CreateDirectory(_root);
        var job = Job("moved.bin", 256 * 1024, 706, queue: 4);
        using var engine = Engine(); engine.UpdateQueueSpeedLimits(new Dictionary<int, long> { [4] = 16 * 1024 });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var begun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = engine.ExecuteAsync(job, null, new InlineProgress<TransferProgress>(p => { if (p.ReceivedBytes > 0) begun.TrySetResult(); }), cancellation.Token);
        await begun.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(200); // The next read is now held in the original queue's budget.
        job.IsQueued = false;
        var finished = await run.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(finished.Success, finished.Failure?.Message);
        Assert.Equal(Expected(706, 256 * 1024), await Hashing.Sha256FileAsync(finished.FinalFilePath!));
    }

    private static HttpDownloadEngine Engine() => new(new EngineOptions
    {
        AllowLocalNetwork = true, MaxAttempts = 1, ReadTimeout = TimeSpan.FromSeconds(15), MaxConnectionsPerHost = 8,
    });
    private DownloadJob Job(string name, int size, int seed, int queue, int connections = 1)
    {
        var final = Path.Combine(_root, name);
        return new DownloadJob
        {
            Url = $"{_server.BaseAddress}/file/{name}?size={size}&seed={seed}", FileName = name,
            DestinationDirectory = _root, FinalFilePath = final, PartFilePath = final + ".part",
            QueueId = queue, IsQueued = true, RequestedConnections = connections, State = DownloadState.Queued,
        };
    }
    private static string Expected(int seed, int size) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(TestContent.Generate(seed, size))).ToLowerInvariant();
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }
    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }
}
