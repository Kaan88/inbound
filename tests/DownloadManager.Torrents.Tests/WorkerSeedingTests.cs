using System.Text.Json.Nodes;

namespace DownloadManager.Torrents.Tests;

/// <summary>T2 worker commands against the native worker on loopback: <c>seed</c>, <c>peers</c>, <c>trackers</c>, <c>pieces</c>.</summary>
public sealed class WorkerSeedingTests(ITestOutputHelper output) : IDisposable
{
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(60);
    private readonly string _root = Directory.CreateTempSubdirectory("inbound-seed-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [WorkerFact]
    public async Task SeedTurnsSeedingOnAndOffAndTheDetailsDescribeTheTorrentAndItsPeer()
    {
        var payload = new byte[3 * 1024 * 1024 + 5];
        Random.Shared.NextBytes(payload);
        var seedDir = Path.Combine(_root, "seed");
        Directory.CreateDirectory(seedDir);
        File.WriteAllBytes(Path.Combine(seedDir, "data.bin"), payload);
        var torrent = TestTorrent.Create("data.bin", payload);
        var hash = TestTorrent.LastInfoHash; // thread-static: read before the first await
        var pieceCount = (payload.Length + 64 * 1024 - 1) / (64 * 1024);

        await using var seeder = Worker.Start(output, "seeder");
        var port = await seeder.ListenOnLoopbackAsync();
        await seeder.Process.SendAsync("setProgressInterval", new JsonObject { ["ms"] = 200 });
        // Added without seeding: complete on disk, so it is paused at once.
        await seeder.Process.SendAsync("add", new JsonObject
        {
            ["id"] = "t", ["savePath"] = seedDir, ["torrent"] = Convert.ToBase64String(torrent),
        });
        await seeder.WaitForAsync(e => e["event"]?.GetValue<string>() == "state" && e["state"]?.GetValue<string>() == "paused", Long, "pause (seeding off)");

        // Unknown torrents are refused like every other command.
        var unknown = await Assert.ThrowsAsync<TorrentCommandException>(() => seeder.Process.SendAsync("peers", new JsonObject { ["id"] = "nobody" }));
        Assert.Equal("unknown torrent", unknown.Reason);

        var pieces = await seeder.Process.SendAsync("pieces", new JsonObject { ["id"] = "t" });
        Assert.Equal(pieceCount, pieces["count"]!.GetValue<int>());
        Assert.Equal(pieceCount, pieces["have"]!.GetValue<int>());
        Assert.Equal(64 * 1024, pieces["pieceLength"]!.GetValue<int>());
        var range = Assert.Single(pieces["ranges"]!.AsArray())!.AsArray();
        Assert.Equal((0, pieceCount - 1), (range[0]!.GetValue<int>(), range[1]!.GetValue<int>()));
        Assert.Empty((await seeder.Process.SendAsync("trackers", new JsonObject { ["id"] = "t" }))["trackers"]!.AsArray());

        var mark = seeder.Mark();
        await seeder.Process.SendAsync("seed", new JsonObject { ["id"] = "t", ["on"] = true });
        await seeder.WaitForProgressAsync("t", r => r["state"]!.GetValue<string>() == "seeding", Long, "seeding", mark);

        await using var leecher = Worker.Start(output, "leecher");
        await leecher.ListenOnLoopbackAsync();
        await leecher.Process.SendAsync("setProgressInterval", new JsonObject { ["ms"] = 200 });
        // A tracker that answers nothing: listed, never working.
        await leecher.Process.SendAsync("add", new JsonObject
        {
            ["id"] = "t", ["savePath"] = Path.Combine(_root, "get"),
            ["magnet"] = $"magnet:?xt=urn:btih:{hash}&tr=http%3A%2F%2F127.0.0.1%3A9%2Fannounce",
        });
        await leecher.Process.SendAsync("addPeer", new JsonObject { ["id"] = "t", ["address"] = "127.0.0.1", ["port"] = port });
        await leecher.WaitForEventAsync("metadata", "t", Long);
        var tracker = Assert.Single((await leecher.Process.SendAsync("trackers", new JsonObject { ["id"] = "t" }))["trackers"]!.AsArray())!.AsObject();
        Assert.Equal("http://127.0.0.1:9/announce", tracker["url"]!.GetValue<string>());
        Assert.NotEqual("working", tracker["status"]!.GetValue<string>());

        // While connected, each side lists the other.
        JsonObject? peer = null;
        var deadline = DateTime.UtcNow + Long;
        while (peer is null && DateTime.UtcNow < deadline)
        {
            var list = (await seeder.Process.SendAsync("peers", new JsonObject { ["id"] = "t" }))["peers"]!.AsArray();
            peer = list.FirstOrDefault()?.AsObject();
            if (peer is null) await Task.Delay(100);
        }

        Assert.NotNull(peer);
        Assert.Equal("127.0.0.1", peer["address"]!.GetValue<string>());
        Assert.Contains("incoming", peer["flags"]!.AsArray().Select(f => f!.GetValue<string>()));
        await leecher.WaitForEventAsync("completed", "t", Long);
        var seeding = await seeder.WaitForProgressAsync("t", r => r["uploaded"]!.GetValue<long>() >= payload.Length, Long, "everything uploaded");
        Assert.True(seeding["ratio"]!.GetValue<double>() >= 1.0);
        Assert.True(seeding["seedingSeconds"]!.GetValue<long>() >= 0);

        mark = seeder.Mark();
        await seeder.Process.SendAsync("seed", new JsonObject { ["id"] = "t", ["on"] = false });
        await seeder.WaitForProgressAsync("t", r => r["state"]!.GetValue<string>() == "paused", Long, "seeding stopped", mark);
        await seeder.WaitForEventAsync("resumeBlob", "t", Long);
    }
}
