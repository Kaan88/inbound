// Usage: TorrentWorkerProbe <worker exe> <work dir> <torrents> <file MiB> <minutes>
// Makes <torrents> random files, starts the worker, adds each as a seeding torrent, listens on 127.0.0.1 only (no DHT,
// LSD, UPnP or NAT-PMP, so nothing leaves the machine and no firewall prompt appears), sets the progress rate a
// closed window would use (one report every 5 s), and keeps it running for <minutes>. Prints the worker's process id.
using System.Text.Json.Nodes;
using DownloadManager.Torrents;
using DownloadManager.Torrents.Tests;

// Second mode, gates 4 and 5: TorrentWorkerProbe download <worker exe> <work dir> <magnet> <port> [<dht state file>]
// Listens on every interface at <port> with DHT on, adds the magnet, and prints the time to metadata, to the first
// piece (the first report with bytes done, reports every 100 ms), to completion, and the average rate.
if (args.Length >= 5 && args[0] == "download") return await Download(args);
// Third mode: TorrentWorkerProbe seed <worker exe> <work dir> <app port> <minutes> (see Seed.cs).
if (args.Length >= 5 && args[0] == "seed") return await Seed.RunAsync(args);

if (args.Length != 5)
{
    Console.Error.WriteLine("usage: TorrentWorkerProbe <worker exe> <work dir> <torrents> <file MiB> <minutes>");
    return 2;
}
var (exe, dir, count, mib, minutes) = (Path.GetFullPath(args[0]), Path.GetFullPath(args[1]), int.Parse(args[2]), int.Parse(args[3]), double.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture));
Directory.CreateDirectory(dir);
var torrents = new List<(string Id, byte[] Torrent)>();
for (var i = 0; i < count; i++)
{
    var name = $"probe-{i:000}.bin";
    var path = Path.Combine(dir, name);
    byte[] payload;
    if (File.Exists(path) && new FileInfo(path).Length == mib * 1048576L) payload = File.ReadAllBytes(path);
    else
    {
        payload = new byte[mib * 1048576];
        Random.Shared.NextBytes(payload);
        File.WriteAllBytes(path, payload);
    }
    torrents.Add(($"t{i}", TestTorrent.Create(name, payload, pieceLength: 256 * 1024)));
}

var worker = TorrentWorkerProcess.Start(exe, log: line => Console.Error.WriteLine(line));
Console.WriteLine($"worker pid {worker.ProcessId}");
var seeding = new HashSet<string>();
var drain = Task.Run(async () =>
{
    await foreach (var e in worker.Events.ReadAllAsync())
    {
        if (e["event"]?.GetValue<string>() != "progress") continue;
        foreach (var r in e["torrents"]!.AsArray())
            if (r!["state"]!.GetValue<string>() == "seeding") lock (seeding) seeding.Add(r["id"]!.GetValue<string>());
    }
});
await worker.SendAsync("setListen", new JsonObject { ["port"] = 0, ["interfaces"] = new JsonArray("127.0.0.1"), ["dht"] = false, ["lsd"] = false, ["upnp"] = false, ["natpmp"] = false });
await worker.SendAsync("setProgressInterval", new JsonObject { ["ms"] = 1000 });
foreach (var (id, torrent) in torrents)
    await worker.SendAsync("add", new JsonObject { ["id"] = id, ["savePath"] = dir, ["torrent"] = Convert.ToBase64String(torrent), ["seed"] = true });
var deadline = DateTime.UtcNow.AddMinutes(2);
while (DateTime.UtcNow < deadline)
{
    lock (seeding) if (seeding.Count == count) break;
    await Task.Delay(200);
}
lock (seeding) Console.WriteLine($"{seeding.Count} of {count} torrents seeding at {DateTime.UtcNow:O}");
await worker.SendAsync("setProgressInterval", new JsonObject { ["ms"] = 5000 });
await Task.Delay(TimeSpan.FromMinutes(minutes));
Console.WriteLine($"closing at {DateTime.UtcNow:O}");
await worker.CloseAsync(TimeSpan.FromSeconds(15));
await drain;
Console.WriteLine($"worker exit code {worker.ExitCode}");
return 0;

static async Task<int> Download(string[] args)
{
    var (exe, dir, magnet, port) = (Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), args[3], int.Parse(args[4]));
    var dhtState = args.Length > 5 ? Path.GetFullPath(args[5]) : null;
    Directory.CreateDirectory(dir);
    var clock = System.Diagnostics.Stopwatch.StartNew();
    var worker = TorrentWorkerProcess.Start(exe, dhtState, log: line => Console.Error.WriteLine(line));
    long metadataMs = -1, firstPieceMs = -1, completeMs = -1, total = 0, lastDone = 0;
    int maxPeers = 0;
    var done = new TaskCompletionSource();
    var nextReport = 5000L;
    var drain = Task.Run(async () =>
    {
        await foreach (var e in worker.Events.ReadAllAsync())
        {
            switch (e["event"]?.GetValue<string>())
            {
                case "metadata":
                    metadataMs = clock.ElapsedMilliseconds;
                    total = e["totalSize"]!.GetValue<long>();
                    Console.WriteLine($"{metadataMs,7} ms metadata: {e["name"]}, {total} bytes");
                    break;
                case "completed":
                    completeMs = clock.ElapsedMilliseconds;
                    done.TrySetResult();
                    break;
                case "error":
                    Console.WriteLine($"{clock.ElapsedMilliseconds,7} ms error: {e.ToJsonString()}");
                    break;
                case "progress":
                    foreach (var r in e["torrents"]!.AsArray())
                    {
                        lastDone = r!["done"]!.GetValue<long>();
                        maxPeers = Math.Max(maxPeers, r["peers"]!.GetValue<int>());
                        if (firstPieceMs < 0 && lastDone > 0) firstPieceMs = clock.ElapsedMilliseconds;
                        if (clock.ElapsedMilliseconds >= nextReport)
                        {
                            nextReport += 5000;
                            Console.WriteLine($"{clock.ElapsedMilliseconds,7} ms {r["state"]} {lastDone / 1048576.0:0.0} MiB, {r["downRate"]!.GetValue<long>() / 1048576.0:0.00} MiB/s, {r["peers"]} peers");
                        }
                    }
                    break;
            }
        }
        done.TrySetResult();
    });
    await worker.SendAsync("setListen", new JsonObject { ["port"] = port, ["dht"] = true, ["lsd"] = false, ["upnp"] = false, ["natpmp"] = false });
    await worker.SendAsync("setProgressInterval", new JsonObject { ["ms"] = 100 });
    await worker.SendAsync("add", new JsonObject { ["id"] = "d", ["savePath"] = dir, ["magnet"] = magnet });
    await done.Task.WaitAsync(TimeSpan.FromHours(2));
    await worker.CloseAsync(TimeSpan.FromSeconds(15));
    await drain;
    var seconds = (completeMs - firstPieceMs) / 1000.0;
    Console.WriteLine($"RESULT metadataMs={metadataMs} firstPieceMs={firstPieceMs} completeMs={completeMs} bytes={total} " +
        $"avgMiBps={(completeMs > 0 ? total / 1048576.0 / (completeMs / 1000.0) : 0):0.00} " +
        $"avgFromFirstPieceMiBps={(seconds > 0 ? total / 1048576.0 / seconds : 0):0.00} maxPeers={maxPeers} exit={worker.ExitCode}");
    return completeMs > 0 ? 0 : 1;
}
