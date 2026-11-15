using System.Diagnostics;
using Process = System.Diagnostics.Process;
using SystemClock = Android.OS.SystemClock;
using OperationCanceledException = System.OperationCanceledException;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Android.Content;
using Android.OS;
using DownloadManager.Core;
using DownloadManager.Persistence;

namespace AndroidTrial;

public sealed record TrialRequest(string Url, int Segments, string Dest, string? ExpectedSha256, string RunId, long OnCreateMs);

internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

/// <summary>
/// One download through <see cref="HttpDownloadEngine"/>, wired as tools/Bench/Throughput.cs wires it (which copies the Host):
/// real JsonJobStore + JsonCheckpointStore, the Host's engine options, a progress callback that updates the job and saves it
/// every 5 s. State (jobs, checkpoints, secret key) is in the app's private files dir; the payload in the chosen destination.
/// </summary>
internal static class TrialRunner
{
    private static int _secretProtectorSet;

    public static async Task RunAsync(Context context, TrialRequest request, Action<string> say, CancellationToken cancellationToken)
    {
        var json = new JsonObject
        {
            ["runId"] = request.RunId,
            ["url"] = request.Url,
            ["segments"] = request.Segments,
            ["dest"] = request.Dest,
            ["device"] = $"{Build.Manufacturer} {Build.Model} API{(int)Build.VERSION.SdkInt} {string.Join(",", Build.SupportedAbis ?? [])}",
            ["runtime"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            ["processArch"] = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            ["onCreateMs"] = request.OnCreateMs,
        };
        var extDir = context.GetExternalFilesDir(null)?.AbsolutePath ?? throw new InvalidOperationException("No external files dir.");
        var resultFile = Path.Combine(extDir, $"result-{request.RunId}.json");
        try
        {
            if (request.Dest == "raw") await RunRawAsync(request, json, say, cancellationToken).ConfigureAwait(false);
            else await RunCoreAsync(context, request, extDir, json, say, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            json["fatal"] = ex.ToString();
            say("FATAL " + ex);
        }

        var text = json.ToJsonString();
        try { File.WriteAllText(resultFile, text); } catch (Exception ex) { say("could not write result file: " + ex.Message); }
        say("RESULT " + text);
    }

    private static async Task RunCoreAsync(Context context, TrialRequest request, string extDir, JsonObject json, Action<string> say, CancellationToken ct)
    {
        var filesDir = context.FilesDir?.AbsolutePath ?? throw new InvalidOperationException("No files dir.");
        var stateRoot = Path.Combine(filesDir, "state");
        string destDir = request.Dest switch
        {
            "app" => extDir,
            "internal" => Path.Combine(filesDir, "payload"),
            "downloads" => Android.OS.Environment.GetExternalStoragePublicDirectory(Android.OS.Environment.DirectoryDownloads)!.AbsolutePath,
            var other => throw new ArgumentException($"Unknown dest '{other}'."),
        };
        json["destDir"] = destDir;
        json["targetSdk"] = (int)(context.ApplicationInfo?.TargetSdkVersion ?? 0);
        json["isExternalStorageManager"] = Android.OS.Environment.IsExternalStorageManager;

        // Step-by-step probe of what the engine does to the destination, so a failure names the call and the exception.
        json["probe"] = Probe(destDir, request.RunId);
        json["probeDoneMs"] = SystemClock.ElapsedRealtime() - Android.OS.Process.StartElapsedRealtime;

        var paths = new StoragePaths(stateRoot);
        paths.EnsureCreated();
        if (Interlocked.Exchange(ref _secretProtectorSet, 1) == 0)
        {
            // Off Windows a protector must be chosen before the job store is used (as the Host and Bench do).
            var keyDir = Path.Combine(stateRoot, "key");
            Directory.CreateDirectory(keyDir);
            SecretProtector.Use(AesGcmSecretProtector.OpenForDataFolder(
                Path.Combine(keyDir, AesGcmSecretProtector.DefaultFileName), keyDir, discardUnreadable: false));
        }

        json["protectorDoneMs"] = SystemClock.ElapsedRealtime() - Android.OS.Process.StartElapsedRealtime;
        var jobStore = new JsonJobStore(paths);
        var checkpoints = new JsonCheckpointStore(paths);
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
        engine.UpdateHostConnectionLimit(8);
        json["engineCreatedMs"] = SystemClock.ElapsedRealtime() - Android.OS.Process.StartElapsedRealtime;

        // Resume: the same runId with a saved job and checkpoint continues that job.
        var idFile = Path.Combine(stateRoot, $"run-{request.RunId}.id");
        DownloadJob? job = null;
        DownloadCheckpoint? checkpoint = null;
        long bytesAtResume = 0;
        var resumed = false;
        if (File.Exists(idFile) && Guid.TryParse(File.ReadAllText(idFile).Trim(), out var knownId))
        {
            job = await jobStore.LoadAsync(knownId, ct).ConfigureAwait(false);
            checkpoint = await checkpoints.LoadAsync(knownId, ct).ConfigureAwait(false);
            if (job is not null && checkpoint is not null)
            {
                resumed = true;
                bytesAtResume = checkpoint.CompletedBytes;
                json["bytesAtResumeJob"] = job.ReceivedBytes;
                json["partFileSizeAtResume"] = File.Exists(job.PartFilePath) ? new FileInfo(job.PartFilePath).Length : -1;
                job.State = DownloadState.Connecting;
                job.RunStartedAt = DateTimeOffset.UtcNow;
            }
            else
            {
                job = null;
                checkpoint = null;
            }
        }

        if (job is null)
        {
            var id = Guid.NewGuid();
            var name = $"inbound-trial-{request.RunId}.bin";
            var final = Path.Combine(destDir, name);
            job = new DownloadJob
            {
                Id = id,
                Url = request.Url,
                FileName = name,
                DestinationDirectory = destDir,
                FinalFilePath = final,
                PartFilePath = $"{final}.{id:N}.part",
                RequestedConnections = request.Segments,
                State = DownloadState.Connecting,
                StartedAt = DateTimeOffset.UtcNow,
                RunStartedAt = DateTimeOffset.UtcNow,
                ConflictPolicy = ConflictPolicy.Overwrite,
            };
            await jobStore.SaveAsync(job, ct).ConfigureAwait(false);
            File.WriteAllText(idFile, id.ToString());
        }

        json["resumed"] = resumed;
        json["bytesAtResume"] = bytesAtResume;
        json["jobId"] = job.Id.ToString();
        var engineReadyMs = SystemClock.ElapsedRealtime() - Android.OS.Process.StartElapsedRealtime;
        json["engineReadyMs"] = engineReadyMs;
        say($"engine ready at {engineReadyMs} ms after process start; resumed={resumed} bytesAtResume={bytesAtResume}");

        var sync = new object();
        int callbacks = 0, maxConnections = 0;
        var lastSave = DateTimeOffset.MinValue;
        var lastSay = DateTimeOffset.MinValue;
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
                if (now - lastSave >= TimeSpan.FromSeconds(5))
                {
                    lastSave = now;
                    _ = jobStore.SaveAsync(job);
                }

                if (now - lastSay >= TimeSpan.FromSeconds(2))
                {
                    lastSay = now;
                    say($"progress {p.ReceivedBytes} / {p.TotalBytes} bytes, {p.BytesPerSecond / 1e6:F1} MB/s, {p.ActiveConnections} conn");
                }
            }
        });

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var alloc0 = GC.GetTotalAllocatedBytes(true);
        int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
        var (user0, kernel0) = Cpu();
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
            result = await engine.ExecuteAsync(job, checkpoint, progress, ct).ConfigureAwait(false);
        }
        finally
        {
            sw.Stop();
        }

        var (user1, kernel1) = Cpu();
        var alloc1 = GC.GetTotalAllocatedBytes(true);
        stopSampler.Cancel();
        await sampler.ConfigureAwait(false);
        using (var self = Process.GetCurrentProcess())
        {
            peak = Math.Max(peak, self.WorkingSet64);
            json["peakWorkingSetKernelHwm"] = self.PeakWorkingSet64;
        }

        var wall = sw.Elapsed.TotalSeconds;
        var cpu = (user1 - user0 + kernel1 - kernel0).TotalSeconds;
        json["success"] = result.Success;
        json["engineState"] = result.FinalState.ToString();
        json["failure"] = result.Failure is null ? null : $"{result.Failure.Kind}: {result.Failure.Message}";
        json["bytes"] = result.BytesWritten;
        json["totalBytes"] = result.TotalBytes;
        json["wallSeconds"] = wall;
        json["mbPerSecond"] = wall > 0 ? result.BytesWritten / 1e6 / wall : 0;
        json["cpuUserSeconds"] = (user1 - user0).TotalSeconds;
        json["cpuKernelSeconds"] = (kernel1 - kernel0).TotalSeconds;
        json["cpuSeconds"] = cpu;
        json["cpuSecondsPerGB"] = result.BytesWritten > 0 ? cpu / (result.BytesWritten / 1e9) : 0;
        json["allocatedBytes"] = alloc1 - alloc0;
        json["gc0"] = GC.CollectionCount(0) - g0;
        json["gc1"] = GC.CollectionCount(1) - g1;
        json["gc2"] = GC.CollectionCount(2) - g2;
        json["peakWorkingSetSampled"] = peak;
        json["progressCallbacks"] = callbacks;
        json["maxActiveConnections"] = maxConnections;
        json["finalPath"] = result.FinalFilePath;
        var finalPath = result.FinalFilePath ?? job.FinalFilePath;
        json["finalExistsAtFinalName"] = File.Exists(finalPath);
        json["partFileGone"] = !File.Exists(job.PartFilePath);
        json["finalSize"] = File.Exists(finalPath) ? new FileInfo(finalPath).Length : -1;

        if (result.Success && File.Exists(finalPath))
        {
            var sha = await Sha256Async(finalPath, ct).ConfigureAwait(false);
            json["sha256"] = sha;
            json["expectedSha256"] = request.ExpectedSha256;
            json["hashMatched"] = request.ExpectedSha256 is null ? null : string.Equals(sha, request.ExpectedSha256, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// dest=raw: the loopback ceiling. Reads (and discards) the file with <c>segments</c> parallel range requests through a plain
    /// HttpClient, 128 KiB reads as the engine does, writing nothing. The total size comes from a HEAD request.
    /// </summary>
    private static async Task RunRawAsync(TrialRequest request, JsonObject json, Action<string> say, CancellationToken ct)
    {
        using var handler = new SocketsHttpHandler { MaxConnectionsPerServer = request.Segments, AutomaticDecompression = System.Net.DecompressionMethods.None };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        long size;
        using (var head = new HttpRequestMessage(HttpMethod.Head, request.Url))
        using (var response = await client.SendAsync(head, ct).ConfigureAwait(false))
            size = response.Content.Headers.ContentLength ?? throw new InvalidOperationException("No Content-Length.");
        var connections = request.Segments;
        var (user0, kernel0) = Cpu();
        var sw = Stopwatch.StartNew();
        var chunk = size / connections;
        var tasks = Enumerable.Range(0, connections).Select(async i =>
        {
            var from = i * chunk;
            var to = i == connections - 1 ? size - 1 : from + chunk - 1;
            using var message = new HttpRequestMessage(HttpMethod.Get, request.Url);
            if (connections > 1) message.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(from, to);
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var buffer = new byte[128 * 1024];
            long total = 0;
            int n;
            while ((n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0) total += n;
            return total;
        }).ToArray();
        var counts = await Task.WhenAll(tasks).ConfigureAwait(false);
        sw.Stop();
        var (user1, kernel1) = Cpu();
        var bytes = counts.Sum();
        var cpu = (user1 - user0 + kernel1 - kernel0).TotalSeconds;
        json["success"] = bytes == size;
        json["bytes"] = bytes;
        json["wallSeconds"] = sw.Elapsed.TotalSeconds;
        json["mbPerSecond"] = bytes / 1e6 / sw.Elapsed.TotalSeconds;
        json["cpuSeconds"] = cpu;
        json["cpuSecondsPerGB"] = cpu / (bytes / 1e9);
        using var self = Process.GetCurrentProcess();
        json["peakWorkingSetKernelHwm"] = self.PeakWorkingSet64;
        say($"raw read {bytes} bytes");
    }

    private static (TimeSpan User, TimeSpan Kernel) Cpu()
    {
        using var p = Process.GetCurrentProcess();
        return (p.UserProcessorTime, p.PrivilegedProcessorTime);
    }

    private static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 20];
        int n;
        while ((n = await f.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0) hash.AppendData(buffer, 0, n);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>The storage calls the engine makes, one by one, each reporting "ok" or the exception.</summary>
    private static JsonObject Probe(string dir, string runId)
    {
        var o = new JsonObject();
        var part = Path.Combine(dir, $"probe-{runId}.part");
        var final = Path.Combine(dir, $"probe-{runId}.bin");
        void Step(string name, Action a)
        {
            try { a(); o[name] = "ok"; }
            catch (Exception ex) { o[name] = $"{ex.GetType().FullName}: {ex.Message}"; }
        }

        Step("createDirectory", () => Directory.CreateDirectory(dir));
        FileStream? fs = null;
        Step("fileStreamCreate", () => fs = new FileStream(part, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite, 1, FileOptions.RandomAccess));
        if (fs is not null)
        {
            Step("setLength", () => fs.SetLength(8 << 20));
            Step("randomAccessWriteAtOffset", () => RandomAccess.Write(fs.SafeFileHandle, new byte[4096], 4 << 20));
            Step("flushToDisk", () => RandomAccess.FlushToDisk(fs.SafeFileHandle));
            Step("close", () => fs.Dispose());
            Step("fileMoveRename", () => File.Move(part, final, overwrite: true));
            Step("finalSizeCheck", () => { if (new FileInfo(final).Length != 8 << 20) throw new IOException("wrong size"); });
            Step("delete", () => File.Delete(final));
        }

        try { if (File.Exists(part)) File.Delete(part); } catch (Exception) { }
        return o;
    }
}
