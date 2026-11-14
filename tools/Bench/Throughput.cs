using System.Diagnostics;
using DownloadManager.Core;
using DownloadManager.Persistence;

namespace Bench;

internal sealed record ThroughputRow(
    long SizeBytes, long BytesWritten, double WallSeconds, double CpuUser, double CpuKernel, long AllocatedBytes,
    int Gc0, int Gc1, int Gc2, long PeakWorkingSet, int ProgressCallbacks, int MaxActiveConnections, int JobSaves,
    int CheckpointSaves, double ServerCpu, bool Verified, string Result)
{
    public double MbPerSecond => WallSeconds > 0 ? BytesWritten / 1e6 / WallSeconds : double.NaN;
    public double CpuTotal => CpuUser + CpuKernel;
    private double Gb => BytesWritten / 1e9;
    public double CpuPerGb => Gb > 0 ? CpuTotal / Gb : double.NaN;
    public double AllocPerGb => Gb > 0 ? AllocatedBytes / Gb : double.NaN;
}

/// <summary>Counts what the engine saves, and forwards to the real store.</summary>
internal sealed class CountingCheckpoints(IDownloadCheckpointStore inner) : IDownloadCheckpointStore
{
    private int _saves;
    public int Saves => Volatile.Read(ref _saves);
    public Task<DownloadCheckpoint?> LoadAsync(Guid jobId, CancellationToken cancellationToken = default) => inner.LoadAsync(jobId, cancellationToken);

    public Task SaveAsync(DownloadCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _saves);
        return inner.SaveAsync(checkpoint, cancellationToken);
    }

    public Task DeleteAsync(Guid jobId, CancellationToken cancellationToken = default) => inner.DeleteAsync(jobId, cancellationToken);
}

internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

internal static class Throughput
{
    /// <summary>
    /// One download through <see cref="HttpDownloadEngine"/> in this process, wired as the Host wires it: the same engine options,
    /// the real <see cref="JsonCheckpointStore"/> (the engine saves every 5 s), a progress callback that updates the job under a
    /// lock, announces a snapshot at most every 300 ms and saves the job with the real <see cref="JsonJobStore"/> every 5 s
    /// (DownloadManagerService.cs, the InlineProgress in StartAsync). Metrics cover engine start to result.
    /// </summary>
    public static async Task<ThroughputRow> RunAsync(string url, long size, int segments, string destRoot, string stateRoot, bool verify, Servers.Running server, int hostLimit)
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var runDir = Path.Combine(destRoot, "run-" + tag);
        var stateDir = Path.Combine(stateRoot, "state-" + tag);
        Directory.CreateDirectory(runDir);
        var paths = new StoragePaths(stateDir);
        paths.EnsureCreated();
        try
        {
            var jobStore = new JsonJobStore(paths);
            var checkpoints = new CountingCheckpoints(new JsonCheckpointStore(paths));
            using var engine = new HttpDownloadEngine(
                new EngineOptions
                {
                    MaxAttempts = 5,
                    InitialRetryDelay = TimeSpan.FromSeconds(2),
                    MaxRetryDelay = TimeSpan.FromSeconds(60),
                    ConnectTimeout = TimeSpan.FromSeconds(20),
                    ReadTimeout = TimeSpan.FromSeconds(90),
                    AllowLocalNetwork = true,
                },
                checkpoints: checkpoints);
            // As DownloadManagerService.cs:200 does from the setting (default 8, which is also EngineOptions' default).
            engine.UpdateHostConnectionLimit(hostLimit);

            var id = Guid.NewGuid();
            var final = Path.Combine(runDir, "bench.bin");
            var job = new DownloadJob
            {
                Id = id,
                Url = url,
                FileName = "bench.bin",
                DestinationDirectory = runDir,
                FinalFilePath = final,
                PartFilePath = $"{final}.{id:N}.part",
                RequestedConnections = segments,
                State = DownloadState.Connecting,
                StartedAt = DateTimeOffset.UtcNow,
                RunStartedAt = DateTimeOffset.UtcNow,
            };
            await jobStore.SaveAsync(job).ConfigureAwait(false);

            var sync = new object();
            int callbacks = 0, maxConnections = 0, jobSaves = 0;
            var lastEvent = DateTimeOffset.MinValue;
            var lastSave = DateTimeOffset.MinValue;
            var progress = new InlineProgress<TransferProgress>(p =>
            {
                lock (sync)
                {
                    var now = DateTimeOffset.UtcNow;
                    job.ReceivedBytes = p.ReceivedBytes;
                    if (p.TotalBytes is not null) job.TotalBytes = p.TotalBytes;
                    job.BytesPerSecond = p.BytesPerSecond;
                    job.EstimatedRemaining = p.EstimatedRemaining;
                    job.ActiveConnections = p.ActiveConnections;
                    job.State = p.Phase.StartsWith("Retrying", StringComparison.Ordinal) ? DownloadState.RetryWaiting
                        : p.Phase == "Connecting" && p.BytesPerSecond <= 0 ? DownloadState.Connecting : DownloadState.Downloading;
                    job.Touch();
                    callbacks++;
                    maxConnections = Math.Max(maxConnections, p.ActiveConnections);
                    if (now - lastEvent >= TimeSpan.FromMilliseconds(300))
                    {
                        lastEvent = now;
                        // Stand-in for Map(job): the Host builds a whole JobDto here. Not the same size, but the same order.
                        GC.KeepAlive(job.Clone());
                    }

                    if (now - lastSave >= TimeSpan.FromSeconds(5))
                    {
                        lastSave = now;
                        jobSaves++;
                        // The Host does this from PersistQuietlyAsync, fire and forget. SaveAsync clones the job in its
                        // synchronous part, so calling it under the lock is safe.
                        _ = jobStore.SaveAsync(job);
                    }
                }
            });

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var alloc0 = GC.GetTotalAllocatedBytes(true);
            int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
            var (user0, kernel0) = Cpu();
            var server0 = server.CpuSeconds();
            long peak = 0;
            using var stopSampler = new CancellationTokenSource();
            var sampler = Task.Run(async () =>
            {
                using var self = Process.GetCurrentProcess();
                while (!stopSampler.IsCancellationRequested)
                {
                    self.Refresh();
                    peak = Math.Max(peak, self.WorkingSet64);
                    try { await Task.Delay(200, stopSampler.Token).ConfigureAwait(false); } catch (OperationCanceledException) { }
                }
            });

            var sw = Stopwatch.StartNew();
            DownloadResult result;
            try
            {
                result = await engine.ExecuteAsync(job, null, progress, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                sw.Stop();
            }

            var (user1, kernel1) = Cpu();
            var alloc1 = GC.GetTotalAllocatedBytes(true);
            var serverCpu = server.CpuSeconds() - server0;
            stopSampler.Cancel();
            await sampler.ConfigureAwait(false);
            using (var self = Process.GetCurrentProcess()) peak = Math.Max(peak, self.WorkingSet64);

            var verified = false;
            if (result.Success && verify && result.FinalFilePath is { } written) verified = Pattern.Verify(written, size);
            var outcome = result.Success ? (verify && !verified ? "bad-content" : "ok") : $"failed:{result.Failure?.Kind}:{result.Failure?.Message}";
            return new ThroughputRow(size, result.BytesWritten, sw.Elapsed.TotalSeconds, (user1 - user0).TotalSeconds, (kernel1 - kernel0).TotalSeconds,
                alloc1 - alloc0, GC.CollectionCount(0) - g0, GC.CollectionCount(1) - g1, GC.CollectionCount(2) - g2, peak, callbacks, maxConnections,
                jobSaves, checkpoints.Saves, serverCpu, verified, outcome);
        }
        finally
        {
            TryDelete(runDir);
            TryDelete(stateDir);
        }
    }

    private static (TimeSpan User, TimeSpan Kernel) Cpu()
    {
        using var p = Process.GetCurrentProcess();
        return (p.UserProcessorTime, p.PrivilegedProcessorTime);
    }

    public static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (Exception) { /* best effort */ }
    }
}
