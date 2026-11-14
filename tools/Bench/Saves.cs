using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DownloadManager.Core;
using DownloadManager.Persistence;

namespace Bench;

/// <summary>
/// Save costs. "product" rows call the real JsonJobStore / JsonCheckpointStore. Write-through and fsync are hard-coded in the
/// product's AtomicFile (internal; FileOptions.WriteThrough plus Flush(flushToDisk: true)) and cannot be switched without a code
/// change, so the "replica" rows write the same bytes with a copy of that routine (here, not in src) with the durability pieces
/// switched, to show what they cost. "replica-fsync" is the product's setting and should match "product".
/// </summary>
internal static class Saves
{
    public static readonly string[] Columns =
        ["record", "segments", "dir_kind", "filesystem", "variant", "count", "bytes_per_save", "mean_ms", "p50_ms", "p95_ms", "max_ms", "writes_per_s", "note"];

    private static readonly JsonSerializerOptions Indented = Make(true);
    private static readonly JsonSerializerOptions Compact = Make(false);

    // The product's PersistenceJson.Options (internal), copied.
    private static JsonSerializerOptions Make(bool indented)
    {
        var o = new JsonSerializerOptions
        {
            WriteIndented = indented,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return o;
    }

    public static DownloadJob SampleJob(bool browser)
    {
        var id = Guid.NewGuid();
        var dir = OperatingSystem.IsWindows() ? @"C:\Users\someone\Downloads\Compressed" : "/home/someone/Downloads/Compressed";
        var name = "Some.Large.Archive.Name-2026.11.part1.rar";
        var job = new DownloadJob
        {
            Id = id,
            Url = "https://downloads.example.com/releases/2026/11/Some.Large.Archive.Name-2026.11.part1.rar?token=abcdef0123456789abcdef0123456789&expires=1795000000",
            FileName = name,
            DestinationDirectory = dir,
            FinalFilePath = Path.Combine(dir, name),
            PartFilePath = Path.Combine(dir, name) + "." + id.ToString("N") + ".part",
            RequestedConnections = 8,
            State = DownloadState.Downloading,
            TotalBytes = 4_294_967_296,
            ReceivedBytes = 1_234_567_890,
            BytesPerSecond = 62_500_000,
            ActiveConnections = 8,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            RunStartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        };
        if (browser)
        {
            job.PageUrl = "https://downloads.example.com/releases/2026/11/";
            job.Referrer = "https://downloads.example.com/releases/2026/11/";
            job.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/156.0.0.0 Safari/537.36";
            job.RequestHeaders["Cookie"] = string.Join("; ", Enumerable.Range(0, 12).Select(i => $"cookie{i}={new string((char)('a' + i), 40)}"));
        }

        return job;
    }

    public static DownloadCheckpoint SampleCheckpoint(int segments)
    {
        const long total = 4_294_967_296;
        var cp = new DownloadCheckpoint
        {
            JobId = Guid.NewGuid(),
            Url = "https://downloads.example.com/releases/2026/11/Some.Large.Archive.Name-2026.11.part1.rar?token=abcdef0123456789abcdef0123456789&expires=1795000000",
            ETag = "\"5f2c1a7e-ffffffff\"",
            LastModified = "Wed, 11 Nov 2026 10:00:00 GMT",
            TotalBytes = total,
            ContentType = "application/octet-stream",
        };
        var each = total / segments;
        for (var i = 0; i < segments; i++)
            cp.Segments.Add(new SegmentCheckpoint { Start = i * each, End = i == segments - 1 ? total - 1 : (i + 1) * each - 1, Completed = each / 3 + i * 4096 });
        return cp;
    }

    public static async Task RunAsync(Csv csv, IEnumerable<(string Kind, string Dir)> dirs, int count)
    {
        var first = true;
        foreach (var (kind, dir) in dirs)
        {
            var fsType = Fs.Describe(dir);
            var root = Path.Combine(dir, "saves-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                var paths = new StoragePaths(root);
                paths.EnsureCreated();
                var jobs = new JsonJobStore(paths);
                var cps = new JsonCheckpointStore(paths);

                foreach (var browser in new[] { false, true })
                {
                    var job = SampleJob(browser);
                    var record = browser ? "job-browser" : "job-plain";
                    var file = Path.Combine(paths.JobsDirectory, $"{job.Id:N}.json");
                    await Measure(csv, record, 0, kind, fsType, "product", count, file, () => jobs.SaveAsync(job)).ConfigureAwait(false);
                    await Replicas(csv, record, 0, kind, fsType, count, file, Path.Combine(root, "replica-" + record + ".json")).ConfigureAwait(false);
                    if (first) SerializeRows(csv, record, 0, count, File.ReadAllText(file), job);
                }

                foreach (var segments in new[] { 1, 8, 16 })
                {
                    var cp = SampleCheckpoint(segments);
                    var file = Path.Combine(paths.CheckpointsDirectory, $"{cp.JobId:N}.json");
                    await Measure(csv, "checkpoint", segments, kind, fsType, "product", count, file, () => cps.SaveAsync(cp)).ConfigureAwait(false);
                    await Replicas(csv, "checkpoint", segments, kind, fsType, count, file, Path.Combine(root, $"replica-checkpoint-{segments}.json")).ConfigureAwait(false);
                    if (first) SerializeRows(csv, "checkpoint", segments, count, File.ReadAllText(file), cp);
                }
            }
            finally
            {
                Throughput.TryDelete(root);
            }

            first = false;
        }
    }

    private static async Task Measure(Csv csv, string record, int segments, string kind, string fs, string variant, int count, string file, Func<Task> save, string note = "")
    {
        for (var i = 0; i < Math.Min(5, Math.Max(1, count / 5)); i++) await save().ConfigureAwait(false);
        var ms = new List<double>(count);
        var total = Stopwatch.StartNew();
        for (var i = 0; i < count; i++)
        {
            var t = Stopwatch.GetTimestamp();
            await save().ConfigureAwait(false);
            ms.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds);
        }

        total.Stop();
        var size = File.Exists(file) ? new FileInfo(file).Length : 0;
        var row = new object?[]
        {
            record, segments, kind, fs, variant, count, size, Stats.Mean(ms), Stats.Median(ms), Stats.Percentile(ms, 95), ms.Max(), count / total.Elapsed.TotalSeconds, note,
        };
        csv.Row(row);
        Console.WriteLine($"[saves] {record,-12} seg={segments,-2} {kind,-4} {variant,-22} n={count} {size,6} B  mean {Stats.Mean(ms):0.000} ms  p95 {Stats.Percentile(ms, 95):0.000} ms  {count / total.Elapsed.TotalSeconds:0} writes/s");
    }

    private static async Task Replicas(Csv csv, string record, int segments, string kind, string fs, int count, string productFile, string target)
    {
        var bytes = await File.ReadAllBytesAsync(productFile).ConfigureAwait(false);
        foreach (var (variant, writeThrough, fsync) in new[] { ("replica-fsync", true, true), ("replica-writethrough-only", true, false), ("replica-no-durability", false, false) })
            await Measure(csv, record, segments, kind, fs, variant, count, target, () => ReplicaWriteAsync(target, bytes, writeThrough, fsync), "copy of AtomicFile, not product code").ConfigureAwait(false);
    }

    /// <summary>The product's AtomicFile.WriteBytesAsync, with the two durability steps optional.</summary>
    private static async Task ReplicaWriteAsync(string path, ReadOnlyMemory<byte> bytes, bool writeThrough, bool fsync)
    {
        var directory = Path.GetDirectoryName(path)!;
        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        var options = FileOptions.Asynchronous | (writeThrough ? FileOptions.WriteThrough : FileOptions.None);
        await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, options))
        {
            await stream.WriteAsync(bytes).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
            if (fsync) stream.Flush(flushToDisk: true);
        }

        File.Move(temp, path, overwrite: true);
    }

    /// <summary>In-memory serialisation cost and size, indented (the product's setting) against compact. Compact size is taken from the real file's JSON.</summary>
    private static void SerializeRows(Csv csv, string record, int segments, int count, string productJson, object model)
    {
        using var document = JsonDocument.Parse(productJson);
        var compactBytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(document.RootElement, Compact));
        foreach (var (variant, options, bytes) in new[] { ("serialize-indented", Indented, Encoding.UTF8.GetByteCount(productJson)), ("serialize-compact", Compact, compactBytes) })
        {
            for (var i = 0; i < 20; i++) JsonSerializer.Serialize(model, options);
            var ms = new List<double>(count);
            for (var i = 0; i < count; i++)
            {
                var t = Stopwatch.GetTimestamp();
                JsonSerializer.Serialize(model, options);
                ms.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds);
            }

            csv.Row(record, segments, "memory", "", variant, count, bytes, Stats.Mean(ms), Stats.Median(ms), Stats.Percentile(ms, 95), ms.Max(), 1000 / Stats.Mean(ms),
                "serialise only, no file; the job is serialised without the store's header protection");
            Console.WriteLine($"[saves] {record,-12} seg={segments,-2} mem  {variant,-22} n={count} {bytes,6} B  mean {Stats.Mean(ms):0.0000} ms");
        }
    }
}
