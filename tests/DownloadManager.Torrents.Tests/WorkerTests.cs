using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace DownloadManager.Torrents.Tests;

/// <summary>
/// The native worker against itself on loopback: one worker seeds, another downloads. No DHT, LSD, UPnP or tracker;
/// peers are connected by address. These cover roadmap gates 9 (resume), 11 (fit) and 14 (crash containment) on the
/// machine that runs them.
/// </summary>
public sealed class WorkerTests(ITestOutputHelper output) : IDisposable
{
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(60);
    private readonly string _root = Directory.CreateTempSubdirectory("inbound-torrent-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [WorkerFact]
    public async Task HelloNamesTheProtocolAndLibtorrent()
    {
        await using var w = Worker.Start(output, "w");
        var hello = await w.WaitForEventAsync("hello", null, TimeSpan.FromSeconds(10));
        Assert.Equal(1, hello["protocol"]!.GetValue<int>());
        Assert.StartsWith("2.1.", hello["libtorrent"]!.GetValue<string>());
    }

    [WorkerFact]
    public async Task BadCommandsAreRefusedAndTheWorkerCarriesOn()
    {
        await using var w = Worker.Start(output, "w");
        var e = await Assert.ThrowsAsync<TorrentCommandException>(() => w.Process.SendAsync("nonsense"));
        Assert.Equal("unknown command", e.Reason);
        await Assert.ThrowsAsync<TorrentCommandException>(() => w.Process.SendAsync("add", new JsonObject { ["id"] = "a", ["savePath"] = _root }));
        await Assert.ThrowsAsync<TorrentCommandException>(() => w.Process.SendAsync("add", new JsonObject { ["id"] = "a", ["savePath"] = _root, ["torrent"] = "not base64!" }));
        await Assert.ThrowsAsync<TorrentCommandException>(() => w.Process.SendAsync("add", new JsonObject { ["id"] = "a", ["savePath"] = _root, ["torrent"] = Convert.ToBase64String("d4:spam4:eggse"u8.ToArray()) }));
        await Assert.ThrowsAsync<TorrentCommandException>(() => w.Process.SendAsync("pause", new JsonObject { ["id"] = "nobody" }));
        await Assert.ThrowsAsync<TorrentCommandException>(() => w.Process.SendAsync("debugAbort")); // no test hooks: unknown
        var stats = await w.Process.SendAsync("stats");
        Assert.Equal(0, stats["torrents"]!.GetValue<int>());
    }

    [WorkerFact]
    public async Task AnUnsafePathInTheMetadataIsRefused()
    {
        await using var w = Worker.Start(output, "w");
        var torrent = TestTorrent.Create("..", new byte[1000]);
        await Assert.ThrowsAsync<TorrentCommandException>(() => w.Process.SendAsync("add", new JsonObject { ["id"] = "a", ["savePath"] = _root, ["torrent"] = Convert.ToBase64String(torrent) }));
    }

    [WorkerFact]
    public async Task OddNamesAreRewrittenNotRefused()
    {
        // Common in torrents made on Linux. libtorrent rewrites what Windows can't store, and the worker renames
        // Windows device names; neither refuses the torrent.
        await using var w = Worker.Start(output, "w");
        var cases = new (string Id, string Name, string Windows)[]
        {
            ("colon", "Title: Part 1.bin", "Title_ Part 1.bin"), ("device", "nul.txt", "_nul.txt"), ("dots", "trailing.", "trailing"),
            ("spaces", "trailing ", "trailing"), ("super", "COM¹.txt", "_COM¹.txt"), ("dotdot", "..x", "..x"),
        };
        foreach (var (id, name, windows) in cases)
        {
            var torrent = TestTorrent.Create(name, new byte[1000]);
            await w.Process.SendAsync("add", new JsonObject { ["id"] = id, ["savePath"] = _root, ["torrent"] = Convert.ToBase64String(torrent), ["paused"] = true });
            var path = (await w.WaitForEventAsync("metadata", id, Long))["files"]![0]!["path"]!.GetValue<string>();
            output.WriteLine($"{name} -> {path}");
            if (OperatingSystem.IsWindows()) Assert.Equal(windows, path);
        }
    }

    [WorkerFact]
    public async Task AMagnetGetsItsMetadataFromAPeerAndThenDownloads()
    {
        var (payload, torrent) = Payload("magnet.bin", 2 * 1024 * 1024, seedDir: "seed");
        var hash = TestTorrent.LastInfoHash!;
        await using var seeder = await StartSeederAsync(torrent);
        var port = await seeder.ListenOnLoopbackAsync();
        await using var leecher = Worker.Start(output, "leecher");
        await leecher.ListenOnLoopbackAsync();
        await leecher.Process.SendAsync("add", new JsonObject { ["id"] = "m", ["savePath"] = Path.Combine(_root, "get"), ["magnet"] = $"magnet:?xt=urn:btih:{hash}" });
        await leecher.Process.SendAsync("addPeer", new JsonObject { ["id"] = "m", ["address"] = "127.0.0.1", ["port"] = port });
        var m = await leecher.WaitForEventAsync("metadata", "m", Long);
        Assert.Equal("magnet.bin", m["name"]!.GetValue<string>());
        await leecher.WaitForEventAsync("completed", "m", Long);
        await leecher.Process.CloseAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(SHA256.HashData(payload), SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(_root, "get", "magnet.bin"))));
        // The last blob carries the metadata, so a restart needs no swarm.
        await using var again = Worker.Start(output, "again");
        await again.Process.SendAsync("add", new JsonObject { ["id"] = "m", ["savePath"] = Path.Combine(_root, "get"), ["resume"] = leecher.Blob("m"), ["paused"] = true });
        await again.WaitForEventAsync("metadata", "m", TimeSpan.FromSeconds(10));
    }

    [WorkerFact]
    public async Task ClosingStdinSavesResumeDataAndExits()
    {
        var (_, torrent) = Payload("close.bin", 256 * 1024, seedDir: "seed");
        await using var w = Worker.Start(output, "w");
        await w.Process.SendAsync("add", Add("t", torrent, Path.Combine(_root, "seed"), seed: true));
        await w.WaitForEventAsync("metadata", "t", Long);
        var stopwatch = Stopwatch.StartNew();
        Assert.True(await w.Process.CloseAsync(TimeSpan.FromSeconds(15)), "the worker did not exit after stdin closed");
        output.WriteLine($"exit after stdin closed: {stopwatch.ElapsedMilliseconds} ms, code {w.Process.ExitCode}");
        Assert.Equal(0, w.Process.ExitCode);
        Assert.NotNull(w.Blob("t"));
        Assert.Contains(w.Seen, e => e["event"]?.GetValue<string>() == "bye");
    }

    [WorkerFact]
    public async Task DownloadsFromASeederOnLoopbackAndTheFileMatches()
    {
        var (payload, torrent) = Payload("movie.bin", 24 * 1024 * 1024, seedDir: "seed");
        await using var seeder = await StartSeederAsync(torrent);
        var port = await seeder.ListenOnLoopbackAsync();

        await using var leecher = Worker.Start(output, "leecher");
        await leecher.ListenOnLoopbackAsync();
        await leecher.Process.SendAsync("setProgressInterval", new JsonObject { ["ms"] = 200 });
        var stopwatch = Stopwatch.StartNew();
        await leecher.Process.SendAsync("add", Add("t", torrent, Path.Combine(_root, "get")));
        await leecher.Process.SendAsync("addPeer", new JsonObject { ["id"] = "t", ["address"] = "127.0.0.1", ["port"] = port });
        await leecher.WaitForEventAsync("completed", "t", Long);
        output.WriteLine($"24 MiB over loopback in {stopwatch.ElapsedMilliseconds} ms");
        await leecher.WaitForAsync(e => e["event"]?.GetValue<string>() == "state" && e["state"]?.GetValue<string>() == "paused", Long, "pause after completion (seeding off)");

        await leecher.Process.CloseAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(SHA256.HashData(payload), SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(_root, "get", "movie.bin"))));
    }

    [WorkerFact]
    public async Task FilesDownloadUnderTheirPartNameAndFinalizeRenamesThemBack()
    {
        // T1's part-file rule: every file is written as "<name><suffix>" in the save folder, and finalize renames it to
        // its own name. rootName replaces the torrent's top-level name (here a single file's name).
        var (payload, torrent) = Payload("film.bin", 8 * 1024 * 1024, seedDir: "seed");
        await using var seeder = await StartSeederAsync(torrent);
        var port = await seeder.ListenOnLoopbackAsync();
        await using var leecher = Worker.Start(output, "leecher");
        await leecher.ListenOnLoopbackAsync();
        var get = Path.Combine(_root, "get");
        var add = Add("t", torrent, get);
        add["partSuffix"] = ".0f8fad5bd9cb469fa165707ef7d3e1c1.part";
        add["rootName"] = "film (2).bin";
        await leecher.Process.SendAsync("add", add);
        var meta = await leecher.WaitForEventAsync("metadata", "t", Long);
        Assert.Equal("film (2).bin", meta["files"]![0]!["path"]!.GetValue<string>());
        await leecher.Process.SendAsync("addPeer", new JsonObject { ["id"] = "t", ["address"] = "127.0.0.1", ["port"] = port });
        await leecher.WaitForEventAsync("completed", "t", Long);
        var part = Path.Combine(get, "film (2).bin.0f8fad5bd9cb469fa165707ef7d3e1c1.part");
        Assert.True(File.Exists(part), "the file was not written under its part name");
        Assert.False(File.Exists(Path.Combine(get, "film.bin")));

        await leecher.Process.SendAsync("finalize", new JsonObject { ["id"] = "t" });
        Assert.False(File.Exists(part));
        Assert.Equal(SHA256.HashData(payload), SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(get, "film (2).bin"))));
        // A second finalize has nothing left to rename and says so at once.
        await leecher.Process.SendAsync("finalize", new JsonObject { ["id"] = "t" });

        // The last blob knows the final name: a restart finds the finished file, not a part file.
        await leecher.Process.CloseAsync(TimeSpan.FromSeconds(15));
        await using var again = Worker.Start(output, "again");
        // The Host sends the same partSuffix and rootName with every add of the torrent, a resume included.
        await again.Process.SendAsync("add", new JsonObject
        {
            ["id"] = "t", ["savePath"] = get, ["resume"] = leecher.Blob("t"), ["paused"] = true,
            ["partSuffix"] = ".0f8fad5bd9cb469fa165707ef7d3e1c1.part", ["rootName"] = "film (2).bin",
        });
        var again_meta = await again.WaitForEventAsync("metadata", "t", TimeSpan.FromSeconds(10));
        Assert.Equal("film (2).bin", again_meta["files"]![0]!["path"]!.GetValue<string>());
        // Checking against the finished file, not a part file: the restarted torrent is complete.
        var row = await again.WaitForProgressAsync("t", _ => true, Long, "progress after restart");
        Assert.Equal(row["wanted"]!.GetValue<long>(), row["done"]!.GetValue<long>());
    }

    [WorkerFact]
    public async Task AHeldMagnetWaitsForItsFileChoiceAfterTheMetadata()
    {
        var (payload, torrent) = Payload("held.bin", 2 * 1024 * 1024, seedDir: "seed");
        var hash = TestTorrent.LastInfoHash!;
        await using var seeder = await StartSeederAsync(torrent);
        var port = await seeder.ListenOnLoopbackAsync();
        await using var leecher = Worker.Start(output, "leecher");
        await leecher.ListenOnLoopbackAsync();
        await leecher.Process.SendAsync("setProgressInterval", new JsonObject { ["ms"] = 200 });
        var get = Path.Combine(_root, "get");
        await leecher.Process.SendAsync("add", new JsonObject
        {
            ["id"] = "m", ["savePath"] = get, ["magnet"] = $"magnet:?xt=urn:btih:{hash}", ["hold"] = true,
            ["partSuffix"] = ".held.part",
        });
        await leecher.Process.SendAsync("addPeer", new JsonObject { ["id"] = "m", ["address"] = "127.0.0.1", ["port"] = port });
        var m = await leecher.WaitForEventAsync("metadata", "m", Long);
        Assert.True(m["held"]?.GetValue<bool>());
        Assert.Equal("held.bin", m["files"]![0]!["path"]!.GetValue<string>());
        var mark = leecher.Mark();
        await leecher.WaitForProgressAsync("m", r => r["state"]!.GetValue<string>() == "held", Long, "held state", mark);
        await Task.Delay(TimeSpan.FromSeconds(2));
        var row = await leecher.WaitForProgressAsync("m", _ => true, Long, "progress while held", leecher.Mark());
        Assert.Equal(0, row["done"]!.GetValue<long>());

        await leecher.Process.SendAsync("setFilePriorities", new JsonObject { ["id"] = "m", ["priorities"] = new JsonArray(4) });
        await leecher.WaitForEventAsync("completed", "m", Long);
        await leecher.Process.SendAsync("finalize", new JsonObject { ["id"] = "m" });
        Assert.Equal(SHA256.HashData(payload), SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(get, "held.bin"))));
        Assert.False(File.Exists(Path.Combine(get, "held.bin.held.part")));
    }

    [WorkerFact]
    public async Task ABadPartSuffixOrRootNameIsRefused()
    {
        await using var w = Worker.Start(output, "w");
        var torrent = TestTorrent.Create("x.bin", new byte[1000]);
        foreach (var (key, value) in new[] { ("partSuffix", "part"), ("partSuffix", "./x"), ("rootName", "a/b"), ("rootName", "..") })
        {
            var add = Add("a", torrent, _root);
            add[key] = value;
            await Assert.ThrowsAsync<TorrentCommandException>(() => w.Process.SendAsync("add", add));
        }
    }

    [WorkerFact]
    public async Task UnselectedFilesAreNotDownloaded()
    {
        // With every file at priority 0 nothing is wanted: no byte is fetched and no file is made.
        var (_, torrent) = Payload("skip.bin", 1024 * 1024, seedDir: "seed");
        await using var seeder = await StartSeederAsync(torrent);
        var port = await seeder.ListenOnLoopbackAsync();
        await using var leecher = Worker.Start(output, "leecher");
        await leecher.ListenOnLoopbackAsync();
        await leecher.Process.SendAsync("setProgressInterval", new JsonObject { ["ms"] = 200 });
        var add = Add("t", torrent, Path.Combine(_root, "get"));
        add["filePriorities"] = new JsonArray(0);
        await leecher.Process.SendAsync("add", add);
        await leecher.Process.SendAsync("addPeer", new JsonObject { ["id"] = "t", ["address"] = "127.0.0.1", ["port"] = port });
        await Task.Delay(TimeSpan.FromSeconds(3));
        var row = await leecher.WaitForProgressAsync("t", _ => true, Long, "progress");
        Assert.Equal(0, row["wanted"]!.GetValue<long>());
        Assert.Equal(0, row["done"]!.GetValue<long>());
        Assert.False(File.Exists(Path.Combine(_root, "get", "skip.bin")) && new FileInfo(Path.Combine(_root, "get", "skip.bin")).Length > 0);
    }

    [WorkerFact]
    public async Task FilesSaysHowFarEachFileIs()
    {
        var (_, torrent) = Payload("each.bin", 2 * 1024 * 1024 + 77, seedDir: "seed");
        await using var seeder = await StartSeederAsync(torrent);
        var port = await seeder.ListenOnLoopbackAsync();
        await using var leecher = Worker.Start(output, "leecher");
        await leecher.ListenOnLoopbackAsync();
        var otherHash = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(20));

        // Unknown torrent: refused. A magnet without its metadata: no files yet.
        var unknown = await Assert.ThrowsAsync<TorrentCommandException>(() => leecher.Process.SendAsync("files", new JsonObject { ["id"] = "nobody" }));
        Assert.Equal("unknown torrent", unknown.Reason);
        await leecher.Process.SendAsync("add", new JsonObject { ["id"] = "m", ["savePath"] = Path.Combine(_root, "m"), ["magnet"] = $"magnet:?xt=urn:btih:{otherHash}", ["paused"] = true });
        Assert.Empty((JsonArray)(await leecher.Process.SendAsync("files", new JsonObject { ["id"] = "m" }))["done"]!);

        // A paused torrent with its metadata: one entry per file, nothing done.
        var add = Add("t", torrent, Path.Combine(_root, "get"));
        add["paused"] = true;
        await leecher.Process.SendAsync("add", add);
        await leecher.WaitForEventAsync("metadata", "t", Long);
        var before = (JsonArray)(await leecher.Process.SendAsync("files", new JsonObject { ["id"] = "t" }))["done"]!;
        Assert.Equal(0L, Assert.Single(before)!.GetValue<long>());

        await leecher.Process.SendAsync("resume", new JsonObject { ["id"] = "t" });
        await leecher.Process.SendAsync("addPeer", new JsonObject { ["id"] = "t", ["address"] = "127.0.0.1", ["port"] = port });
        await leecher.WaitForEventAsync("completed", "t", Long);
        var after = (JsonArray)(await leecher.Process.SendAsync("files", new JsonObject { ["id"] = "t" }))["done"]!;
        Assert.Equal(2L * 1024 * 1024 + 77, Assert.Single(after)!.GetValue<long>());
    }

    [WorkerFact]
    public Task AKilledDownloadResumesFromItsLastBlob() => InterruptAndResumeAsync(crash: false);

    [WorkerFact]
    public Task AnAbortInsideTheWorkerIsContainedAndTheDownloadResumes() => InterruptAndResumeAsync(crash: true);

    [WorkerFact]
    public async Task ASeederThatCrashesComesBackFromItsBlobWithoutARecheckOfTheWholeFile()
    {
        var (payload, torrent) = Payload("seed.bin", 4 * 1024 * 1024, seedDir: "seed");
        string blob;
        await using (var first = Worker.Start(output, "seeder-1", testHooks: true))
        {
            await first.Process.SendAsync("add", Add("t", torrent, Path.Combine(_root, "seed"), seed: true));
            await first.WaitForProgressAsync("t", r => r["state"]!.GetValue<string>() == "seeding", Long, "seeding");
            var mark = first.Mark();
            await first.Process.SendAsync("saveResume");
            blob = (await first.WaitForAsync(e => e["event"]?.GetValue<string>() == "resumeBlob", Long, "resume blob", mark))["data"]!.GetValue<string>();
            try { await first.Process.SendAsync("debugAbort"); }
            catch (TorrentEngineExitedException) { }
            await first.Process.Exited.WaitAsync(Long);
            Assert.NotEqual(0, first.Process.ExitCode);
        }
        await using var second = Worker.Start(output, "seeder-2");
        await second.Process.SendAsync("setProgressInterval", new JsonObject { ["ms"] = 200 });
        await second.Process.SendAsync("add", new JsonObject { ["id"] = "t", ["savePath"] = Path.Combine(_root, "seed"), ["resume"] = blob, ["seed"] = true });
        await second.WaitForProgressAsync("t", r => r["state"]!.GetValue<string>() == "seeding", TimeSpan.FromSeconds(10), "seeding again");
        var port = await second.ListenOnLoopbackAsync();
        await using var leecher = Worker.Start(output, "leecher");
        await leecher.ListenOnLoopbackAsync();
        await leecher.Process.SendAsync("add", Add("t", torrent, Path.Combine(_root, "get")));
        await leecher.Process.SendAsync("addPeer", new JsonObject { ["id"] = "t", ["address"] = "127.0.0.1", ["port"] = port });
        await leecher.WaitForEventAsync("completed", "t", Long);
        await leecher.Process.CloseAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(SHA256.HashData(payload), SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(_root, "get", "seed.bin"))));
    }

    private async Task InterruptAndResumeAsync(bool crash)
    {
        const int size = 16 * 1024 * 1024;
        var (payload, torrent) = Payload("big.bin", size, seedDir: "seed");
        await using var seeder = await StartSeederAsync(torrent);
        // A per-torrent limit: libtorrent exempts local peers from the session-wide one, and these peers are on loopback.
        await seeder.Process.SendAsync("setTorrentLimits", new JsonObject { ["id"] = "t", ["upload"] = 2 * 1024 * 1024 });
        var port = await seeder.ListenOnLoopbackAsync();
        var save = Path.Combine(_root, "get");

        long doneBefore;
        string? blob;
        int seederMark;
        await using (var first = Worker.Start(output, "leecher-1", testHooks: crash))
        {
            await first.ListenOnLoopbackAsync();
            await first.Process.SendAsync("setProgressInterval", new JsonObject { ["ms"] = 200 });
            await first.Process.SendAsync("add", Add("t", torrent, save));
            await first.Process.SendAsync("addPeer", new JsonObject { ["id"] = "t", ["address"] = "127.0.0.1", ["port"] = port });
            var row = await first.WaitForProgressAsync("t", r => r["done"]!.GetValue<long>() >= size / 4, Long, "a quarter done");
            doneBefore = row["done"]!.GetValue<long>();
            // The Host's checkpoint: ask for a blob now, as the periodic timer would.
            var mark = first.Mark();
            await first.Process.SendAsync("saveResume");
            blob = (await first.WaitForAsync(e => e["event"]?.GetValue<string>() == "resumeBlob", Long, "resume blob", mark))["data"]!.GetValue<string>();
            seederMark = seeder.Mark();
            if (crash)
            {
                try { await first.Process.SendAsync("debugAbort"); }
                catch (TorrentEngineExitedException) { }
            }
            else
            {
                first.Process.Kill();
            }
            await first.Process.Exited.WaitAsync(Long);
            output.WriteLine($"interrupted at {doneBefore} bytes, exit code {first.Process.ExitCode}");
        }
        Assert.NotNull(blob);
        // The seeder must notice the old connection is gone: libtorrent takes one connection per IP address, and
        // both leechers are 127.0.0.1.
        var gone = Stopwatch.StartNew();
        await seeder.WaitForProgressAsync("t", r => r["peers"]!.GetValue<int>() == 0, Long, "the seeder dropping the dead peer", seederMark);
        output.WriteLine($"seeder dropped the dead peer after {gone.ElapsedMilliseconds} ms");

        var restart = Stopwatch.StartNew();
        await using var second = Worker.Start(output, "leecher-2");
        await second.ListenOnLoopbackAsync();
        await second.Process.SendAsync("setProgressInterval", new JsonObject { ["ms"] = 200 });
        await second.Process.SendAsync("add", new JsonObject { ["id"] = "t", ["savePath"] = save, ["resume"] = blob });
        await second.Process.SendAsync("addPeer", new JsonObject { ["id"] = "t", ["address"] = "127.0.0.1", ["port"] = port });
        var resumed = await second.WaitForProgressAsync("t", r => r["state"]!.GetValue<string>() == "downloading" && r["done"]!.GetValue<long>() > 0, Long, "downloading again");
        output.WriteLine($"downloading again after {restart.ElapsedMilliseconds} ms with {resumed["done"]} bytes kept");
        // Gate 9: no more lost than one checkpoint interval. The blob was asked for at doneBefore, but libtorrent
        // records only pieces already written to disk, which trail the live counter by up to a second or two of
        // transfer (here at most 2 MB/s): so allow 4 MiB, and require that most of it was kept, with no full recheck.
        var kept = resumed["done"]!.GetValue<long>();
        Assert.True(kept >= doneBefore - 4 * 1024 * 1024 && kept >= size / 8, $"kept {kept} of {doneBefore}");
        Assert.True(restart.Elapsed < TimeSpan.FromSeconds(10), $"took {restart.Elapsed}");
        await seeder.Process.SendAsync("setTorrentLimits", new JsonObject { ["id"] = "t", ["upload"] = 0 });
        await second.WaitForEventAsync("completed", "t", Long);
        await second.Process.CloseAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(SHA256.HashData(payload), SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(save, "big.bin"))));
    }

    private async Task<Worker> StartSeederAsync(byte[] torrent)
    {
        var seeder = Worker.Start(output, "seeder");
        await seeder.Process.SendAsync("setProgressInterval", new JsonObject { ["ms"] = 200 });
        await seeder.Process.SendAsync("add", Add("t", torrent, Path.Combine(_root, "seed"), seed: true));
        await seeder.WaitForProgressAsync("t", r => r["state"]!.GetValue<string>() == "seeding", Long, "seeding");
        return seeder;
    }

    private (byte[] Payload, byte[] Torrent) Payload(string name, int size, string seedDir)
    {
        var payload = new byte[size];
        Random.Shared.NextBytes(payload);
        var dir = Path.Combine(_root, seedDir);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, name), payload);
        return (payload, TestTorrent.Create(name, payload));
    }

    private static JsonObject Add(string id, byte[] torrent, string savePath, bool seed = false) => new()
    {
        ["id"] = id, ["savePath"] = savePath, ["torrent"] = Convert.ToBase64String(torrent), ["seed"] = seed,
    };
}
