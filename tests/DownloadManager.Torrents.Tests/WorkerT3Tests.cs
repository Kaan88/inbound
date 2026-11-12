using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace DownloadManager.Torrents.Tests;

/// <summary>
/// T3 worker commands against the native worker on loopback: <c>recheck</c>, <c>announce</c>, <c>sequential</c>,
/// <c>details</c>, <c>setPeerLimits</c>, <c>setProxy</c>, per-torrent limits on add, tracker <c>nextAnnounce</c>, and a
/// swarm found only through a loopback HTTP tracker (G14).
/// </summary>
public sealed class WorkerT3Tests(ITestOutputHelper output) : IDisposable
{
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(60);
    private readonly string _root = Directory.CreateTempSubdirectory("inbound-t3-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private (string Dir, byte[] Torrent, string Hash, byte[] Payload) SeedFiles(string name, int size)
    {
        var payload = new byte[size];
        Random.Shared.NextBytes(payload);
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "data.bin"), payload);
        var torrent = TestTorrent.Create("data.bin", payload);
        return (dir, torrent, TestTorrent.LastInfoHash!, payload);
    }

    [WorkerFact]
    public async Task RecheckFindsADamagedPieceAndDetailsDescribeTheTorrent()
    {
        var (dir, torrent, hash, payload) = SeedFiles("seed", 1024 * 1024 + 7);
        await using var worker = Worker.Start(output, "recheck");
        await worker.ListenOnLoopbackAsync();
        await worker.Process.SendAsync("setProgressInterval", new JsonObject { ["ms"] = 200 });
        await worker.Process.SendAsync("add", new JsonObject
        {
            ["id"] = "t", ["savePath"] = dir, ["torrent"] = Convert.ToBase64String(torrent), ["seed"] = true, ["finalized"] = true,
            ["downloadLimit"] = 50_000, ["uploadLimit"] = 60_000,
        });
        await worker.WaitForProgressAsync("t", r => r["state"]!.GetValue<string>() == "seeding", Long, "seeding");

        var details = await worker.Process.SendAsync("details", new JsonObject { ["id"] = "t" });
        Assert.Equal(hash, details["infoHashV1"]!.GetValue<string>());
        Assert.Equal("", details["infoHashV2"]!.GetValue<string>());
        Assert.False(details["private"]!.GetValue<bool>());
        Assert.Equal((payload.Length + 64 * 1024 - 1) / (64 * 1024), details["pieces"]!.GetValue<int>());
        Assert.Equal((50_000, 60_000), (details["downloadLimit"]!.GetValue<int>(), details["uploadLimit"]!.GetValue<int>()));
        Assert.Equal(0, details["hashFailures"]!.GetValue<long>());
        Assert.True(details["pex"]!.GetValue<bool>());
        Assert.False(details["sequential"]!.GetValue<bool>());
        // DHT is off on loopback: no node count.
        Assert.Equal(-1, details["dhtNodes"]!.GetValue<long>());
        Assert.Equal(-1, (await worker.Process.SendAsync("stats"))["dhtNodes"]!.GetValue<long>());

        // Download order and limits change while it runs; PEX follows setListen.
        await worker.Process.SendAsync("sequential", new JsonObject { ["id"] = "t", ["on"] = true });
        await worker.Process.SendAsync("setTorrentLimits", new JsonObject { ["id"] = "t", ["download"] = 0, ["upload"] = 0 });
        await worker.Process.SendAsync("setListen", new JsonObject
        {
            ["port"] = 0, ["interfaces"] = new JsonArray("127.0.0.1"), ["dht"] = false, ["pex"] = false,
        });
        details = await worker.Process.SendAsync("details", new JsonObject { ["id"] = "t" });
        Assert.True(details["sequential"]!.GetValue<bool>());
        Assert.False(details["pex"]!.GetValue<bool>());
        Assert.Equal((0, 0), (details["downloadLimit"]!.GetValue<int>(), details["uploadLimit"]!.GetValue<int>()));

        // A seed has no piece picker, so libtorrent knows no distributed copies.
        Assert.Equal(-1, details["distributedCopies"]!.GetValue<double>());

        // A damaged piece in a finished torrent: the check finds it, and the torrent stops (paused, with an error) rather
        // than downloading the piece into the finished file.
        await using (var file = new FileStream(Path.Combine(dir, "data.bin"), FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            file.Position = 100_000;
            file.Write(new byte[4096]);
        }

        var mark = worker.Mark();
        await worker.Process.SendAsync("recheck", new JsonObject { ["id"] = "t" });
        var damaged = await worker.WaitForAsync(e => e["event"]?.GetValue<string>() == "error" && e["id"]?.GetValue<string>() == "t",
            Long, "damage error", mark);
        Assert.Equal("torrent", damaged["kind"]!.GetValue<string>());
        Assert.Equal("the file check found damaged or missing pieces", damaged["message"]!.GetValue<string>());
        var after = await worker.WaitForProgressAsync("t",
            r => r["state"]!.GetValue<string>() == "paused" && r["done"]!.GetValue<long>() < payload.Length, Long, "paused after the check", mark);
        Assert.Equal(payload.Length - 64 * 1024, after["done"]!.GetValue<long>());

        // Now it has a piece picker: distributed copies are known (no peers: 0).
        details = await worker.Process.SendAsync("details", new JsonObject { ["id"] = "t" });
        Assert.True(details["distributedCopies"]!.GetValue<double>() >= 0);
        // Paused: no announce is due.
        Assert.Equal(-1, details["nextAnnounce"]!.GetValue<long>());

        var unknown = await Assert.ThrowsAsync<TorrentCommandException>(() => worker.Process.SendAsync("recheck", new JsonObject { ["id"] = "nobody" }));
        Assert.Equal("unknown torrent", unknown.Reason);
    }

    [WorkerFact]
    public async Task ARecheckSentWithTheAddChecksTheFilesAfterTheResumeData()
    {
        var (dir, torrent, _, payload) = SeedFiles("again", 512 * 1024 + 11);
        await using var worker = Worker.Start(output, "add-recheck");
        await worker.ListenOnLoopbackAsync();
        await worker.Process.SendAsync("setProgressInterval", new JsonObject { ["ms"] = 200 });
        var first = await worker.Process.SendAsync("add", new JsonObject
        {
            ["id"] = "t", ["savePath"] = dir, ["torrent"] = Convert.ToBase64String(torrent), ["seed"] = true, ["finalized"] = true,
        });
        Assert.Null(first["recheckQueued"]);
        await worker.WaitForProgressAsync("t", r => r["state"]!.GetValue<string>() == "seeding", Long, "seeding");
        // Resume data that says every piece is there.
        var mark = worker.Mark();
        await worker.Process.SendAsync("pause", new JsonObject { ["id"] = "t" });
        await worker.WaitForAsync(e => e["event"]?.GetValue<string>() == "resumeBlob" && e["id"]?.GetValue<string>() == "t", Long, "resume blob", mark);
        var blob = worker.Blob("t");
        Assert.NotNull(blob);
        await worker.Process.SendAsync("remove", new JsonObject { ["id"] = "t" });

        // Damaged while off the worker; the add asks for a check. Without it, libtorrent would trust the resume data.
        await using (var file = new FileStream(Path.Combine(dir, "data.bin"), FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            file.Position = 200_000;
            file.Write(new byte[4096]);
        }

        mark = worker.Mark();
        var added = await worker.Process.SendAsync("add", new JsonObject
        {
            ["id"] = "t", ["savePath"] = dir, ["resume"] = blob, ["recheck"] = true,
        });
        Assert.True(added["recheckQueued"]!.GetValue<bool>());
        var after = await worker.WaitForProgressAsync("t",
            r => r["state"]!.GetValue<string>() is not "checking" && r["done"]!.GetValue<long>() is > 0 and < 512 * 1024 + 11, Long,
            "missing piece after the check", mark);
        Assert.Equal(payload.Length - 64 * 1024, after["done"]!.GetValue<long>());
        Assert.Equal("downloading", after["state"]!.GetValue<string>());
        // The resume data's "finished" was not taken as the torrent completing.
        Assert.DoesNotContain(worker.Seen.Skip(mark), e => e["event"]?.GetValue<string>() == "completed");
    }

    [WorkerFact]
    public async Task AFinishedTorrentAddedOverADamagedFileStopsInsteadOfDownloadingIntoIt()
    {
        var (dir, torrent, _, payload) = SeedFiles("damaged", 256 * 1024 + 5);
        var path = Path.Combine(dir, "data.bin");
        await using (var file = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            file.Position = 70_000;
            file.Write(new byte[4096]);
        }

        var onDisk = await File.ReadAllBytesAsync(path);
        await using var worker = Worker.Start(output, "finished-damaged");
        await worker.ListenOnLoopbackAsync();
        await worker.Process.SendAsync("setProgressInterval", new JsonObject { ["ms"] = 200 });
        await worker.Process.SendAsync("add", new JsonObject
        {
            ["id"] = "t", ["savePath"] = dir, ["torrent"] = Convert.ToBase64String(torrent), ["seed"] = true, ["finalized"] = true,
        });
        var damaged = await worker.WaitForAsync(e => e["event"]?.GetValue<string>() == "error" && e["id"]?.GetValue<string>() == "t", Long, "damage error");
        Assert.Equal("the file check found damaged or missing pieces", damaged["message"]!.GetValue<string>());
        var row = await worker.WaitForProgressAsync("t", r => r["state"]!.GetValue<string>() == "paused", Long, "paused");
        Assert.Equal(payload.Length - 64 * 1024, row["done"]!.GetValue<long>());
        Assert.DoesNotContain(worker.Seen, e => e["event"]?.GetValue<string>() == "completed");
        Assert.Equal(onDisk, await File.ReadAllBytesAsync(path));
    }

    [WorkerFact]
    public async Task PeerLimitsAndProxyAreTakenOrRefusedInWords()
    {
        await using var worker = Worker.Start(output, "limits");
        var defaultConnections = (await worker.Process.SendAsync("stats"))["connectionsLimit"]!.GetValue<int>();
        await worker.Process.SendAsync("setPeerLimits", new JsonObject { ["connections"] = 100, ["connectionsPerTorrent"] = 20, ["activeSeeds"] = 2 });
        Assert.Equal(100, (await worker.Process.SendAsync("stats"))["connectionsLimit"]!.GetValue<int>());
        // 0 puts libtorrent's default back on the running worker.
        await worker.Process.SendAsync("setPeerLimits", new JsonObject { ["connections"] = 0 });
        Assert.Equal(defaultConnections, (await worker.Process.SendAsync("stats"))["connectionsLimit"]!.GetValue<int>());
        await worker.Process.SendAsync("setPeerLimits", new JsonObject { ["activeSeeds"] = 0 });
        var tooMany = await Assert.ThrowsAsync<TorrentCommandException>(() => worker.Process.SendAsync("setPeerLimits", new JsonObject { ["activeSeeds"] = 20000 }));
        Assert.Equal("limit out of range", tooMany.Reason);

        // Audit L5: a number past int64 was cast (undefined for a double), and setLimits' connections wrapped in int.
        var huge = await Assert.ThrowsAsync<TorrentCommandException>(() => worker.Process.SendAsync("setPeerLimits", new JsonObject { ["connections"] = 1e300 }));
        Assert.Equal("connections is out of range", huge.Reason);
        var unsigned = await Assert.ThrowsAsync<TorrentCommandException>(() => worker.Process.SendAsync("setLimits", new JsonObject { ["download"] = ulong.MaxValue }));
        Assert.Equal("download is out of range", unsigned.Reason);
        var wraps = await Assert.ThrowsAsync<TorrentCommandException>(() => worker.Process.SendAsync("setLimits", new JsonObject { ["connections"] = 4_294_967_396L }));
        Assert.Equal("limit out of range", wraps.Reason);
        await worker.Process.SendAsync("setLimits", new JsonObject { ["download"] = 0, ["upload"] = 0, ["connections"] = 150.7 });
        Assert.Equal(150, (await worker.Process.SendAsync("stats"))["connectionsLimit"]!.GetValue<int>());

        await worker.Process.SendAsync("setProxy", new JsonObject { ["type"] = "none" });
        // Nothing listens on port 9 of loopback: the settings are only stored, nothing connects yet.
        await worker.Process.SendAsync("setProxy", new JsonObject
        {
            ["type"] = "socks5", ["host"] = "127.0.0.1", ["port"] = 9, ["username"] = "u", ["password"] = "secret-not-logged",
        });
        await worker.Process.SendAsync("setProxy", new JsonObject { ["type"] = "http", ["host"] = "127.0.0.1", ["port"] = 9 });
        var badType = await Assert.ThrowsAsync<TorrentCommandException>(() => worker.Process.SendAsync("setProxy", new JsonObject { ["type"] = "socks4a", ["host"] = "h", ["port"] = 1 }));
        Assert.Equal("type must be none, http, socks4 or socks5", badType.Reason);
        var badPort = await Assert.ThrowsAsync<TorrentCommandException>(() => worker.Process.SendAsync("setProxy", new JsonObject { ["type"] = "socks5", ["host"] = "h", ["port"] = 0 }));
        Assert.Equal("port must be 1 to 65535", badPort.Reason);
        await worker.Process.SendAsync("setProxy", new JsonObject { ["type"] = "none" });
    }

    [WorkerFact]
    public async Task ALeecherFindsTheSeederThroughALoopbackTrackerAndCanAnnounceAgain()
    {
        var (dir, torrent, hash, payload) = SeedFiles("seed", 512 * 1024 + 3);
        await using var seeder = Worker.Start(output, "seeder");
        var seederPort = await seeder.ListenOnLoopbackAsync();
        await seeder.Process.SendAsync("setProgressInterval", new JsonObject { ["ms"] = 200 });
        await seeder.Process.SendAsync("add", new JsonObject
        {
            ["id"] = "t", ["savePath"] = dir, ["torrent"] = Convert.ToBase64String(torrent), ["seed"] = true, ["finalized"] = true,
        });
        await seeder.WaitForProgressAsync("t", r => r["state"]!.GetValue<string>() == "seeding", Long, "seeding");

        await using var tracker = LoopbackTracker.Start(seederPort);
        await using var leecher = Worker.Start(output, "leecher");
        await leecher.ListenOnLoopbackAsync();
        await leecher.Process.SendAsync("setProgressInterval", new JsonObject { ["ms"] = 200 });
        // No addPeer: the only way to the seeder is the tracker.
        await leecher.Process.SendAsync("add", new JsonObject
        {
            ["id"] = "t", ["savePath"] = Path.Combine(_root, "get"),
            ["magnet"] = $"magnet:?xt=urn:btih:{hash}&tr={Uri.EscapeDataString(tracker.Url)}",
        });
        await leecher.WaitForEventAsync("completed", "t", Long);
        Assert.True(tracker.Announces >= 1);
        // Every piece passed its hash check (the bytes are checked against the payload in WorkerTests).
        var pieces = await leecher.Process.SendAsync("pieces", new JsonObject { ["id"] = "t" });
        Assert.Equal((payload.Length + 64 * 1024 - 1) / (64 * 1024), pieces["have"]!.GetValue<int>());

        // The leecher pauses when done (seeding off); a paused torrent does not announce, so seed it.
        var mark = leecher.Mark();
        await leecher.Process.SendAsync("seed", new JsonObject { ["id"] = "t", ["on"] = true });
        await leecher.WaitForProgressAsync("t", r => r["state"]!.GetValue<string>() == "seeding", Long, "seeding", mark);

        // Seeding announces again ("updating" for a moment); wait for the answer.
        JsonObject row;
        var until = DateTime.UtcNow + Long;
        while (true)
        {
            row = Assert.Single((await leecher.Process.SendAsync("trackers", new JsonObject { ["id"] = "t" }))["trackers"]!.AsArray())!.AsObject();
            if (row["status"]!.GetValue<string>() == "working" || DateTime.UtcNow > until) break;
            await Task.Delay(50);
        }

        Assert.Equal("working", row["status"]!.GetValue<string>());
        // The tracker asked for 1800 s between announces.
        var next = row["nextAnnounce"]!.GetValue<long>();
        Assert.InRange(next, 1, 1800);

        var before = tracker.Announces;
        await leecher.Process.SendAsync("announce", new JsonObject { ["id"] = "t" });
        var deadline = DateTime.UtcNow + Long;
        while (tracker.Announces == before && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.True(tracker.Announces > before, "announce did not reach the tracker");
    }

    /// <summary>A minimal HTTP tracker on 127.0.0.1 that answers every announce with one peer: the seeder.</summary>
    private sealed class LoopbackTracker : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly int _peerPort;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        private int _announces;

        private LoopbackTracker(int peerPort)
        {
            _peerPort = peerPort;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _loop = Task.Run(LoopAsync);
        }

        public static LoopbackTracker Start(int peerPort) => new(peerPort);

        public string Url => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/announce";

        public int Announces => Volatile.Read(ref _announces);

        private async Task LoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
                catch (Exception) { return; }
                _ = Task.Run(() => AnswerAsync(client));
            }
        }

        private async Task AnswerAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var buffer = new byte[8192];
                    var read = 0;
                    while (read < buffer.Length)
                    {
                        var n = await stream.ReadAsync(buffer.AsMemory(read), _stop.Token);
                        if (n == 0) break;
                        read += n;
                        if (Encoding.ASCII.GetString(buffer, 0, read).Contains("\r\n\r\n", StringComparison.Ordinal)) break;
                    }

                    if (!Encoding.ASCII.GetString(buffer, 0, read).StartsWith("GET /announce", StringComparison.Ordinal)) return;
                    Interlocked.Increment(ref _announces);
                    // Compact peers: 127.0.0.1 and the seeder's port, big-endian.
                    var peer = new byte[] { 127, 0, 0, 1, (byte)(_peerPort >> 8), (byte)_peerPort };
                    using var body = new MemoryStream();
                    body.Write("d8:intervali1800e5:peers6:"u8);
                    body.Write(peer);
                    body.Write("e"u8);
                    var head = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(head, _stop.Token);
                    await stream.WriteAsync(body.ToArray(), _stop.Token);
                }
                catch (Exception) when (_stop.IsCancellationRequested)
                {
                }
                catch (IOException)
                {
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            try { await _loop; }
            catch (Exception) { }
        }
    }
}
