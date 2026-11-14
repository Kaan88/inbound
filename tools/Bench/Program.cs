using System.Globalization;
using System.Text;

namespace Bench;

/// <summary>
/// PERF.8 baseline: engine throughput, store saves and start times. See README.md.
///   Bench all --out DIR [--quick] ...        everything
///   Bench throughput|saves|start --out DIR   one part
///   Bench serve --kind static|testserver     the server process the throughput part starts for itself
/// </summary>
internal static class Program
{
    private const string Usage = "usage: Bench <all|throughput|saves|start|serve> --out DIR [options]; see tools/Bench/README.md";

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help") { Console.WriteLine(Usage); return args.Length == 0 ? 2 : 0; }
        try
        {
            var command = args[0];
            var options = new Options(args.Skip(1));
            if (command == "serve") return await Servers.ServeAsync(options).ConfigureAwait(false);
            if (command is not ("all" or "throughput" or "saves" or "start")) { Console.Error.WriteLine(Usage); return 2; }
            return await RunAsync(command, options).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> RunAsync(string command, Options o)
    {
        var quick = o.Has("quick");
        var outDir = Path.GetFullPath(o.Get("out") ?? throw new ArgumentException("--out DIR is required."));
        Directory.CreateDirectory(outDir);
        var diskDir = Path.GetFullPath(o.Get("disk-dir", Path.Combine(outDir, "work-disk")));
        var ramDir = o.Get("ram-dir") ?? (OperatingSystem.IsLinux() && Directory.Exists("/dev/shm") ? "/dev/shm" : null);
        Directory.CreateDirectory(diskDir);
        diskDir = Path.Combine(diskDir, "bench-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(diskDir);
        if (ramDir is not null)
        {
            ramDir = Path.Combine(Path.GetFullPath(ramDir), "inbound-bench-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(ramDir);
        }

        var parts = command == "all" ? new[] { "throughput", "saves", "start" } : [command];
        var warnings = new List<string>();
        if (Fs.IsMemoryBacked(diskDir)) warnings.Add($"The disk folder {diskDir} is on {Fs.Describe(diskDir)}, which is memory, not a disk. Pass --disk-dir on a real disk.");
        var load0 = Env.LoadAverage();
        if (!OperatingSystem.IsWindows())
        {
            // The job store seals cookie and authorization headers; off Windows a protector must be chosen first, as the Host does
            // (a key file in a data folder). A throw-away key in the disk folder.
            var keyDir = Path.Combine(diskDir, "key");
            Directory.CreateDirectory(keyDir);
            DownloadManager.Persistence.SecretProtector.Use(DownloadManager.Persistence.AesGcmSecretProtector.OpenForDataFolder(
                Path.Combine(keyDir, DownloadManager.Persistence.AesGcmSecretProtector.DefaultFileName), keyDir, discardUnreadable: false));
        }


        var summary = new StringBuilder();
        summary.AppendLine(quick ? "# Bench summary (QUICK smoke run: not a baseline)" : "# Bench summary");
        summary.AppendLine();
        summary.AppendLine("Written by `tools/Bench`; every number is explained in `tools/Bench/README.md`. CSV files are next to this file.");
        summary.AppendLine();

        try
        {
            var env = Env.Describe([("disk_folder", diskDir), ("ram_folder", ramDir)]);
            env.Add(("quick_mode", quick.ToString()));
            env.Add(("note", o.Get("note", "")));
            using (var csv = new Csv(Path.Combine(outDir, "environment.csv"), ["key", "value"]))
                foreach (var (k, v) in env) csv.Row(k, v);
            summary.AppendLine("## Environment").AppendLine().AppendLine("| key | value |").AppendLine("| --- | --- |");
            foreach (var (k, v) in env) summary.AppendLine($"| {k} | {v} |");
            summary.AppendLine();

            var dirs = new List<(string Kind, string Dir)> { ("disk", diskDir) };
            if (ramDir is not null) dirs.Add(("ram", ramDir));
            else Console.WriteLine("[bench] no RAM folder (not Linux, and no --ram-dir): RAM-backed rows are skipped.");

            if (parts.Contains("throughput")) await ThroughputPartAsync(o, quick, outDir, dirs, diskDir, summary, warnings).ConfigureAwait(false);

            if (parts.Contains("saves"))
            {
                var count = o.Int("save-count", quick ? 10 : 300);
                Console.WriteLine($"[saves] measuring {count} saves per record and folder; milliseconds per save, writes per second");
                using var csv = new Csv(Path.Combine(outDir, "saves.csv"), Saves.Columns);
                await Saves.RunAsync(csv, dirs, count).ConfigureAwait(false);
                SavesSummary(csv, summary);
            }

            if (parts.Contains("start"))
            {
                var runs = o.Int("start-runs", quick ? 3 : 10);
                var cli = o.Get("cli");
                var native = o.Get("native-host");
                var host = o.Get("host");
                if (cli is null || native is null || host is null)
                {
                    warnings.Add("Start times skipped: pass --cli, --native-host and --host (published, self-contained Release builds).");
                }
                else
                {
                    Console.WriteLine($"[start] {runs} measured runs per scenario (plus one warm-up), milliseconds from process start");
                    using var csv = new Csv(Path.Combine(outDir, "starts.csv"), StartTimes.Columns);
                    await StartTimes.RunAsync(csv, Path.GetFullPath(cli), Path.GetFullPath(native), Path.GetFullPath(host), Path.Combine(diskDir, "start"), runs).ConfigureAwait(false);
                    StartSummary(csv, summary, runs);
                }
            }
        }
        finally
        {
            Throughput.TryDelete(diskDir);
            if (ramDir is not null) Throughput.TryDelete(ramDir);
        }

        var load1 = Env.LoadAverage();
        summary.AppendLine("## Noise").AppendLine();
        summary.AppendLine($"- load average (1, 5, 15 min; running/total; last pid) before: `{load0 ?? "n/a"}`, after: `{load1 ?? "n/a"}`");
        foreach (var w in warnings) summary.AppendLine($"- WARNING: {w}");
        if (quick) summary.AppendLine("- QUICK mode: small files, few runs. These numbers only show that the tool works.");
        await File.WriteAllTextAsync(Path.Combine(outDir, "summary.md"), summary.ToString()).ConfigureAwait(false);
        Console.WriteLine($"[bench] results in {outDir}");
        foreach (var w in warnings) Console.WriteLine($"[bench] WARNING: {w}");
        return 0;
    }

    // ---- throughput ---------------------------------------------------------------------------------------------------

    private static readonly string[] ThroughputColumns =
    [
        "run_id", "utc", "server", "dest_kind", "filesystem", "segments", "host_connection_limit", "repeat", "size_bytes", "bytes_written", "wall_s", "mb_per_s",
        "cpu_user_s", "cpu_kernel_s", "cpu_total_s", "cpu_s_per_gb", "alloc_bytes", "alloc_bytes_per_gb", "gc0", "gc1", "gc2",
        "peak_working_set_bytes", "server_cpu_s", "progress_callbacks", "max_active_connections", "job_saves", "checkpoint_saves", "verified",
        "reached_target_seconds", "result",
    ];

    private static async Task ThroughputPartAsync(Options o, bool quick, string outDir, List<(string Kind, string Dir)> dirs, string diskDir, StringBuilder summary, List<string> warnings)
    {
        var segments = o.IntList("segments", [1, 8, 16]);
        var repeat = o.Int("repeat", quick ? 1 : 3);
        var targetSeconds = o.Int("target-seconds", 30);
        var sizeText = o.Get("size", quick ? "64M" : "auto");
        var auto = sizeText == "auto";
        var fixedSize = auto ? 0 : Options.ParseBytes(sizeText);
        var maxSize = o.Bytes("max-size", 16L << 30);
        var ramMax = o.Bytes("ram-max-size", 4L << 30);
        var ceilingSize = o.Bytes("ceiling-size", quick ? 64L << 20 : 4L << 30);
        var limits = o.IntList("host-limits", [8, 16]);
        var serverKind = o.Get("server", "auto");
        if (serverKind == "auto") serverKind = "static";

        // 1. The servers' own ceilings: raw read-and-discard from a separate process, no engine, no disk.
        Console.WriteLine("[ceiling] raw HttpClient read-and-discard from each server, megabytes (10^6 bytes) per second");
        double staticBest = 0, testBest = 0;
        using (var csv = new Csv(Path.Combine(outDir, "ceilings.csv"), ["server", "method", "connections", "size_bytes", "seconds", "mb_per_s", "client_cpu_s"]))
        {
            foreach (var kind in new[] { "static", "testserver" })
            {
                using var server = await Servers.StartAsync(kind).ConfigureAwait(false);
                var size = kind == "static" ? ceilingSize : Math.Min(ceilingSize, quick ? 64L << 20 : 512L << 20);
                foreach (var connections in segments)
                {
                    var r = await Servers.RawReadAsync(server.Url(size), size, connections).ConfigureAwait(false);
                    csv.Row(kind, "httpclient", connections, size, r.Seconds, r.MbPerSecond, r.ClientCpuSeconds);
                    Console.WriteLine($"[ceiling] {kind,-10} httpclient {connections,2} conn  {r.MbPerSecond,9:0.0} MB/s  ({size >> 20} MiB in {r.Seconds:0.00} s, client cpu {r.ClientCpuSeconds:0.00} s)");
                    if (kind == "static") staticBest = Math.Max(staticBest, r.MbPerSecond); else testBest = Math.Max(testBest, r.MbPerSecond);
                }

                if (await Servers.CurlAsync(server.Url(size), size).ConfigureAwait(false) is { } curl)
                {
                    csv.Row(kind, "curl", 1, curl.Bytes, curl.Seconds, curl.MbPerSecond, null);
                    Console.WriteLine($"[ceiling] {kind,-10} curl       1 conn  {curl.MbPerSecond,9:0.0} MB/s");
                }
            }

            summary.AppendLine("## Server ceilings").AppendLine();
            summary.AppendLine("Raw read-and-discard (128 KiB reads, no disk, no engine) from a client process on the same machine. MB = 10^6 bytes.").AppendLine();
            summary.AppendLine("| server | method | connections | MB/s |").AppendLine("| --- | --- | ---: | ---: |");
            foreach (var row in csv.Rows) summary.AppendLine($"| {row[0]} | {row[1]} | {row[2]} | {((double)row[5]!):0} |");
            summary.AppendLine();
        }

        // 2. Engine runs.
        var state = diskDir;
        var rows = new List<(string Dest, int Segments, int Limit, ThroughputRow Row)>();
        using var tcsv = new Csv(Path.Combine(outDir, "throughput.csv"), ThroughputColumns);
        using var main = await Servers.StartAsync(serverKind).ConfigureAwait(false);
        Console.WriteLine($"[throughput] engine runs against the {serverKind} server: MB/s, wall seconds, process CPU seconds and per GB (10^9 bytes), allocated bytes per GB, GC counts");
        if (serverKind == "testserver") warnings.Add("The TestServer cannot serve more than 2 GiB and holds it in memory; runs are capped at 1 GiB, so they are short.");

        // One config per destination, segment count and host connection limit. The limit (Options > connections per host, default 8)
        // caps how many of the segments run at once, so 16 segments at the default limit run 8 connections; a limit that gives the
        // same number of connections as an earlier one for that segment count is skipped.
        var configs = new List<(string Kind, string Dir, int Seg, int Limit)>();
        foreach (var (kind, dir) in dirs)
        foreach (var seg in segments)
        foreach (var group in limits.GroupBy(l => Math.Min(seg, l)))
            configs.Add((kind, dir, seg, group.Min()));

        var sizes = new Dictionary<(string, int, int), long>();
        foreach (var (kind, dir, seg, limit) in configs)
        {
            var cap = kind == "ram" ? ramMax : maxSize;
            cap = Math.Min(cap, (long)(Fs.FreeBytes(dir) * (kind == "ram" ? 0.4 : 0.6)));
            if (serverKind == "testserver") cap = Math.Min(cap, 1L << 30);
            long size;
            if (!auto) size = Math.Min(fixedSize, cap);
            else
            {
                const long probe = 256L << 20;
                var p = await Throughput.RunAsync(main.Url(probe), probe, seg, dir, state, false, main, limit).ConfigureAwait(false);
                size = (long)Math.Clamp(p.MbPerSecond * 1e6 * targetSeconds * 1.15, probe, cap);
                Console.WriteLine($"[throughput] probe {kind} segments={seg} limit={limit}: {p.MbPerSecond:0.0} MB/s -> file of {size >> 20} MiB for about {targetSeconds} s");
            }

            var aligned = size & ~((1L << 20) - 1);
            sizes[(kind, seg, limit)] = aligned > 0 ? aligned : size;
        }

        for (var r = 1; r <= repeat; r++)
        foreach (var (kind, dir, seg, limit) in configs)
        {
            var size = sizes[(kind, seg, limit)];
            var verify = serverKind == "static";
            var row = await Throughput.RunAsync(main.Url(size), size, seg, dir, state, verify, main, limit).ConfigureAwait(false);
            rows.Add((kind, seg, limit, row));
            var reached = row.WallSeconds >= targetSeconds;
            tcsv.Row(Guid.NewGuid().ToString("N")[..8], DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture), serverKind, kind, Fs.Describe(dir), seg, limit, r, row.SizeBytes,
                row.BytesWritten, row.WallSeconds, row.MbPerSecond, row.CpuUser, row.CpuKernel, row.CpuTotal, row.CpuPerGb, row.AllocatedBytes, row.AllocPerGb,
                row.Gc0, row.Gc1, row.Gc2, row.PeakWorkingSet, row.ServerCpu, row.ProgressCallbacks, row.MaxActiveConnections, row.JobSaves, row.CheckpointSaves,
                verify ? row.Verified : null, reached, row.Result);
            Console.WriteLine($"[throughput] {kind,-4} segments={seg,-2} limit={limit,-2} run {r}/{repeat}: {row.MbPerSecond,8:0.0} MB/s  {row.WallSeconds,6:0.0} s  cpu {row.CpuTotal:0.0} s ({row.CpuPerGb:0.00} s/GB)  " +
                              $"alloc {row.AllocPerGb / 1e6:0.0} MB/GB  gc {row.Gc0}/{row.Gc1}/{row.Gc2}  peak ws {row.PeakWorkingSet >> 20} MiB  conns {row.MaxActiveConnections}  " +
                              $"saves job {row.JobSaves} cp {row.CheckpointSaves}  {row.Result}");
            if (row.Result != "ok") warnings.Add($"A {kind} run with {seg} segment(s) ended with: {row.Result}");
            if (!reached && !quick) warnings.Add($"A {kind} run with {seg} segment(s), limit {limit}, took {row.WallSeconds:0.0} s, less than the {targetSeconds} s target (file size {size >> 20} MiB; raise --max-size / --ram-max-size or free space). Memory-backed folders usually cannot hold enough.");
        }

        summary.AppendLine("## Engine throughput").AppendLine();
        summary.AppendLine($"Server: `{serverKind}` (separate process). Median over {repeat} run(s). GB = 10^9 bytes; CPU = this process, user + kernel.").AppendLine();
        summary.AppendLine("| destination | segments | host limit | MB/s | wall s | CPU s/GB | alloc MB/GB | GC 0/1/2 | peak WS MiB | max conns | size MiB |").AppendLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: | --- | ---: | ---: | ---: |");
        foreach (var group in rows.GroupBy(x => (x.Dest, x.Segments, x.Limit)))
        {
            var g = group.Select(x => x.Row).ToList();
            summary.AppendLine($"| {group.Key.Dest} | {group.Key.Segments} | {group.Key.Limit} | {Stats.Median(g.Select(x => x.MbPerSecond).ToList()):0} | {Stats.Median(g.Select(x => x.WallSeconds).ToList()):0.0} | " +
                               $"{Stats.Median(g.Select(x => x.CpuPerGb).ToList()):0.00} | {Stats.Median(g.Select(x => x.AllocPerGb / 1e6).ToList()):0.0} | " +
                               $"{Stats.Median(g.Select(x => (double)x.Gc0).ToList()):0}/{Stats.Median(g.Select(x => (double)x.Gc1).ToList()):0}/{Stats.Median(g.Select(x => (double)x.Gc2).ToList()):0} | " +
                               $"{Stats.Median(g.Select(x => x.PeakWorkingSet / 1048576.0).ToList()):0} | {g.Max(x => x.MaxActiveConnections)} | {g[0].SizeBytes >> 20} |");
        }

        summary.AppendLine();
        var bestEngine = rows.Count == 0 ? 0 : rows.Max(x => x.Row.MbPerSecond);
        summary.AppendLine($"Server check: best raw ceiling of the {serverKind} server {(serverKind == "static" ? staticBest : testBest):0} MB/s against the best engine run {bestEngine:0} MB/s " +
                           $"(ratio {((serverKind == "static" ? staticBest : testBest) / Math.Max(bestEngine, 1)):0.0}x). The other server's ceiling: {(serverKind == "static" ? testBest : staticBest):0} MB/s.");
        summary.AppendLine();
    }

    // ---- summaries ----------------------------------------------------------------------------------------------------

    private static void SavesSummary(Csv csv, StringBuilder summary)
    {
        summary.AppendLine("## Saves").AppendLine();
        summary.AppendLine("`product` = the real stores (write-through and fsync, hard-coded). `replica-*` = a copy of the product's atomic write with durability steps removed (not product code). `serialize-*` = in memory only.").AppendLine();
        summary.AppendLine("| record | segments | folder | variant | bytes/save | mean ms | p95 ms | writes/s |").AppendLine("| --- | ---: | --- | --- | ---: | ---: | ---: | ---: |");
        foreach (var r in csv.Rows)
            summary.AppendLine($"| {r[0]} | {r[1]} | {r[2]} ({r[3]}) | {r[4]} | {r[6]} | {((double)r[7]!):0.000} | {((double)r[9]!):0.000} | {((double)r[11]!):0} |");
        summary.AppendLine();
    }

    private static void StartSummary(Csv csv, StringBuilder summary, int runs)
    {
        summary.AppendLine("## Start times").AppendLine();
        summary.AppendLine($"Milliseconds from process start, {runs} measured runs after one warm-up, isolated instance. first output = first byte on stdout/stderr; ready = native host: reply to host.ping, Host: IPC endpoint accepts a connection; exit = process end (Host: from the shutdown command).").AppendLine();
        summary.AppendLine("| target | scenario | metric | median | min | max |").AppendLine("| --- | --- | --- | ---: | ---: | ---: |");
        foreach (var g in csv.Rows.Where(r => (string)r[2]! == "measured").GroupBy(r => ((string)r[0]!, (string)r[1]!)))
        {
            foreach (var (name, index) in new[] { ("first output", 4), ("ready", 5), ("exit", 6) })
            {
                var v = g.Select(r => r[index]).OfType<double>().Where(d => !double.IsNaN(d)).ToList();
                if (v.Count == 0) continue;
                summary.AppendLine($"| {g.Key.Item1} | {g.Key.Item2} | {name} | {Stats.Median(v):0.0} | {v.Min():0.0} | {v.Max():0.0} |");
            }
        }

        summary.AppendLine();
    }
}
