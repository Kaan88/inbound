using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using DownloadManager.Contracts;
using DownloadManager.Core;
using DownloadManager.Persistence;
using DownloadManager.Runtime;
using DownloadManager.Torrents;

namespace DownloadManager.Integration.Tests;

/// <summary>T1 walk fixes: the service with the native worker against a loopback seeder (skipped without the worker).</summary>
public sealed partial class HostCommandDispatcherTests
{
    private static readonly TimeSpan WorkerWait = TimeSpan.FromSeconds(60);

    [TorrentWorkerTheory]
    [InlineData("completed")]
    [InlineData("downloading")]
    [InlineData("metadata")]
    [InlineData("offered")]
    public async Task ATorrentDeletedAndAddedAgainWhileTheWorkerStillRunsDownloadsAgain(string stage)
    {
        var size = (stage == "downloading" ? 24 : 3) * 1024 * 1024 + 123;
        await using var loop = await LoopbackAsync("again.bin", null, size, uploadLimit: stage == "downloading" ? 4_000_000 : null, offer: stage == "offered");
        var (service, dispatcher) = (loop.Service, Dispatcher(loop.Service));
        var destination = Path.Combine(_root, "down");

        // A magnet without the seeder's address stays at "getting the file list".
        var firstInput = stage == "metadata" ? $"magnet:?xt=urn:btih:{loop.Hash}&dn=again.bin" : loop.Magnet;
        var first = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Add, new TorrentAddRequest { Input = firstInput, Destination = destination }))
            .GetPayload<TorrentAddResult>();
        Assert.True(first.Ok, first.Error);
        var firstId = first.Job!.Id;
        switch (stage)
        {
            case "metadata":
                await UntilAsync(() => service.GetSnapshot().Jobs.FirstOrDefault(j => j.Id == firstId && j.State is "connecting" or "downloading"), WorkerWait);
                await Task.Delay(1000);
                break;
            case "downloading":
                await UntilAsync(() => service.GetSnapshot().Jobs.FirstOrDefault(j => j.Id == firstId && j.ReceivedBytes > 2_000_000), WorkerWait);
                Assert.NotEmpty(Directory.EnumerateFiles(destination, $"again.bin.*.part"));
                break;
            default:
                await UntilAsync(() => service.GetSnapshot().Jobs.FirstOrDefault(j => j.Id == firstId && j.State == "completed"), WorkerWait);
                if (stage == "offered") await Task.Delay(7000);
                break;
        }

        Assert.True((await service.RemoveAsync(firstId, deleteFile: true)).Ok);
        // Before completion only the part file (again.bin.<id>.part) is there, so look for anything of it.
        Assert.Empty(Directory.Exists(destination) ? Directory.EnumerateFiles(destination, "again.bin*") : []);
        Assert.NotEqual(TorrentWorkerState.Off, loop.Supervisor.State); // the worker is still in its wait

        var second = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Add, new TorrentAddRequest { Input = loop.Magnet, Destination = destination }))
            .GetPayload<TorrentAddResult>();
        Assert.True(second.Ok, second.Error);
        Assert.False(second.Existing);
        var done = await UntilAsync(() => service.GetSnapshot().Jobs.FirstOrDefault(j => j.Id == second.Job!.Id && j.State is "completed" or "failed"), WorkerWait);
        Assert.True(done.State == "completed", done.Failure?.Message);
        Assert.Equal(loop.Payloads["again.bin"], await File.ReadAllBytesAsync(Path.Combine(destination, "again.bin")));
    }

    [TorrentWorkerFact]
    public async Task AMultiFileTorrentKeepsItsOwnNameAndLeavesNoPartsFileAfterSkippedFiles()
    {
        // Pieces of 16 KiB, so a skipped file shares pieces with its neighbours and libtorrent keeps them in its parts file.
        await using var loop = await LoopbackAsync("Inbound pack", [("video/film.mkv", 300_001), ("subs/en.srt", 20_003), ("cover.jpg", 30_005), ("readme.txt", 1_007)], 0);
        var (service, dispatcher) = (loop.Service, Dispatcher(loop.Service));
        var destination = Path.Combine(_root, "down");

        var added = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Add, new TorrentAddRequest
        {
            Input = loop.Magnet, Destination = destination, Files = "askWhenReady",
        })).GetPayload<TorrentAddResult>();
        Assert.True(added.Ok, added.Error);
        var id = added.Job!.Id;
        var waiting = await UntilAsync(() => service.GetSnapshot().Jobs.FirstOrDefault(j => j.Id == id && j.Torrent!.AwaitingFileChoice), WorkerWait);
        // Named after the torrent, not after its first file, as soon as the list is in.
        Assert.Equal("Inbound pack", waiting.FileName);
        Assert.Equal(Path.Combine(destination, "Inbound pack"), waiting.FinalFilePath);
        var files = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Files, new TorrentFilesRequest { JobId = id })).GetPayload<TorrentFilesResult>().Files;
        Assert.Contains(files, file => file.Path == "Inbound pack/video/film.mkv");

        var keep = files.Where(file => !file.Path.EndsWith("en.srt", StringComparison.Ordinal)).Select(file => file.Index).ToList();
        var chosen = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.SetFiles, new TorrentSetFilesRequest { JobId = id, FileIndexes = keep }))
            .GetPayload<TorrentActionResult>();
        Assert.True(chosen.Ok, chosen.Error);
        var done = await UntilAsync(() => service.GetSnapshot().Jobs.FirstOrDefault(j => j.Id == id && j.State is "completed" or "failed"), WorkerWait);
        Assert.True(done.State == "completed", done.Failure?.Message);
        Assert.Equal("Inbound pack", done.FileName);
        Assert.Equal(Path.Combine(destination, "Inbound pack"), done.FinalFilePath);
        Assert.Equal(loop.Payloads["video/film.mkv"], await File.ReadAllBytesAsync(Path.Combine(destination, "Inbound pack", "video", "film.mkv")));
        Assert.False(File.Exists(Path.Combine(destination, "Inbound pack", "subs", "en.srt")));
        Assert.False(File.Exists(Path.Combine(destination, $".{loop.Hash}.parts")));
        Assert.Empty(Directory.EnumerateFiles(destination, "*.parts", SearchOption.AllDirectories));
    }

    [TorrentWorkerFact]
    public async Task TorrentFilesSaysHowFarEachFileIsWhileItDownloadsAndWhenItHasFinished()
    {
        // The seeder sends about 1 MB/s, so the files are read while the torrent is still on the worker.
        await using var loop = await LoopbackAsync("Progress pack", [("a.bin", 3_000_011), ("b.bin", 2_000_003), ("skip.bin", 40_009)], 0, uploadLimit: 1_000_000);
        var (service, dispatcher) = (loop.Service, Dispatcher(loop.Service));
        var destination = Path.Combine(_root, "down");
        var added = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Add, new TorrentAddRequest
        {
            Input = loop.Magnet, Destination = destination, Files = "askWhenReady",
        })).GetPayload<TorrentAddResult>();
        Assert.True(added.Ok, added.Error);
        var id = added.Job!.Id;
        await UntilAsync(() => service.GetSnapshot().Jobs.FirstOrDefault(j => j.Id == id && j.Torrent!.AwaitingFileChoice), WorkerWait);
        async Task<List<TorrentFileDto>> FilesAsync()
        {
            var result = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Files, new TorrentFilesRequest { JobId = id })).GetPayload<TorrentFilesResult>();
            Assert.True(result.Ok, result.Error);
            return result.Files;
        }

        var keep = (await FilesAsync()).Where(file => !file.Path.EndsWith("skip.bin", StringComparison.Ordinal)).Select(file => file.Index).ToList();
        Assert.True((await AskAsync(dispatcher, Session(), TorrentMessageTypes.SetFiles, new TorrentSetFilesRequest { JobId = id, FileIndexes = keep }))
            .GetPayload<TorrentActionResult>().Ok);

        // Part of the way: the worker counts whole checked pieces, never more than a file's size.
        await UntilAsync(() => service.GetSnapshot().Jobs.FirstOrDefault(j => j.Id == id && j.State == "downloading" && j.ReceivedBytes > 500_000), WorkerWait);
        var during = await FilesAsync();
        Assert.All(during, file => Assert.NotNull(file.Done));
        Assert.All(during, file => Assert.InRange(file.Done!.Value, 0, file.Size));
        Assert.Contains(during, file => file.Wanted && file.Done > 0);

        var done = await UntilAsync(() => service.GetSnapshot().Jobs.FirstOrDefault(j => j.Id == id && j.State is "completed" or "failed"), WorkerWait);
        Assert.True(done.State == "completed", done.Failure?.Message);
        // Off the worker now: the saved list says a wanted file is whole and a skipped one has nothing.
        var after = await FilesAsync();
        Assert.All(after, file => Assert.Equal(file.Wanted ? file.Size : 0, file.Done));
    }

    [TorrentWorkerFact]
    public async Task ASingleFileMagnetAddedToChooseFilesFirstStartsWithoutAsking()
    {
        await using var loop = await LoopbackAsync("solo.bin", null, 200_000);
        var (service, dispatcher) = (loop.Service, Dispatcher(loop.Service));
        var destination = Path.Combine(_root, "down");
        var added = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Add, new TorrentAddRequest
        {
            Input = loop.Magnet, Destination = destination, Files = "askWhenReady",
        })).GetPayload<TorrentAddResult>();
        Assert.True(added.Ok, added.Error);
        var done = await UntilAsync(() => service.GetSnapshot().Jobs.FirstOrDefault(j => j.Id == added.Job!.Id && (j.State is "completed" or "failed" || j.Torrent!.AwaitingFileChoice)), WorkerWait);
        Assert.True(done.State == "completed", done.Failure?.Message ?? done.State);
        Assert.Equal("solo.bin", done.FileName);
        Assert.Equal(loop.Payloads["solo.bin"], await File.ReadAllBytesAsync(Path.Combine(destination, "solo.bin")));
    }

    /// <summary>A seeding worker on 127.0.0.1 and the service with its own worker; the magnet carries the seeder's address.</summary>
    private async Task<Loopback> LoopbackAsync(string name, (string Path, int Size)[]? files, int singleSize, long? uploadLimit = null, bool offer = false)
    {
        var seedDir = Path.Combine(_root, "seed");
        var list = files ?? [(name, singleSize)];
        var payloads = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (path, size) in list)
        {
            var bytes = RandomNumberGenerator.GetBytes(size);
            payloads[path] = bytes;
            var full = files is null ? Path.Combine(seedDir, name) : Path.Combine(seedDir, name, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllBytesAsync(full, bytes);
        }

        var (torrent, hash) = TorrentOf(name, files is null ? null : list.Select(file => (file.Path, payloads[file.Path])).ToArray(), payloads.Values.First());
        var seeder = TorrentWorkerProcess.Start(TorrentWorkerBinary.Path!, null, null, _ => { });
        var drain = Task.Run(async () => { await foreach (var _ in seeder.Events.ReadAllAsync()) { } });
        await seeder.SendAsync("setListen", LoopbackListen(0));
        await seeder.SendAsync("add", new JsonObject { ["id"] = "s", ["savePath"] = seedDir, ["torrent"] = Convert.ToBase64String(torrent), ["seed"] = true });
        if (uploadLimit is { } limit) await seeder.SendAsync("setTorrentLimits", new JsonObject { ["id"] = "s", ["upload"] = limit });
        var port = 0;
        for (var i = 0; i < 200 && port == 0; i++)
        {
            port = (await seeder.SendAsync("stats"))["listenPort"]?.GetValue<int>() ?? 0;
            if (port == 0) await Task.Delay(50);
        }

        Assert.NotEqual(0, port);

        await new JsonSettingsStore(_paths).SaveAsync(new AppSettings { TorrentsEnabled = true }, CancellationToken.None);
        var store = new JsonJobStore(_paths);
        var checkpoints = new JsonCheckpointStore(_paths);
        _engine = new HttpDownloadEngine(new EngineOptions { MaxAttempts = 1, AllowLocalNetwork = true }, checkpoints: checkpoints);
        var appPort = 0;
        if (offer)
        {
            var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            probe.Start();
            appPort = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    try { await seeder.SendAsync("addPeer", new JsonObject { ["id"] = "s", ["address"] = "127.0.0.1", ["port"] = appPort }); }
                    catch (Exception) { return; }
                    await Task.Delay(3000);
                }
            });
        }

        var supervisor = new TorrentWorkerSupervisor(() => TorrentWorkerProcess.Start(TorrentWorkerBinary.Path!, null, null, _ => { }),
            new TorrentSupervisorOptions { DrainDelay = TimeSpan.FromSeconds(30), ProgressIntervalMs = 1000, Listen = () => LoopbackListen(appPort) });
        var torrents = new TorrentDownloadEngine(supervisor, new TorrentStore(_paths));
        _service = new DownloadManagerService(_paths, store, checkpoints, new JsonSettingsStore(_paths), _engine,
            enableSchedulerTimer: false, torrentEngine: torrents);
        await _service.InitializeAsync();
        var magnet = $"magnet:?xt=urn:btih:{hash}&dn={Uri.EscapeDataString(name)}&x.pe=127.0.0.1:{port}";
        return new Loopback(this, _service, supervisor, torrents, seeder, drain, hash, magnet, payloads);
    }

    private static JsonObject LoopbackListen(int port) => new()
    {
        ["port"] = port, ["interfaces"] = new JsonArray("127.0.0.1"), ["dht"] = false, ["lsd"] = false, ["upnp"] = false, ["natpmp"] = false,
    };

    private sealed record Loopback(HostCommandDispatcherTests Owner, DownloadManagerService Service, TorrentWorkerSupervisor Supervisor,
        TorrentDownloadEngine Torrents, TorrentWorkerProcess Seeder, Task SeederEvents, string Hash, string Magnet,
        Dictionary<string, byte[]> Payloads) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Service.ShutdownAsync();
            Owner._service = null;
            await Torrents.DisposeAsync();
            await Seeder.CloseAsync(TimeSpan.FromSeconds(10));
            await Seeder.DisposeAsync();
            await SeederEvents;
        }
    }

    private static async Task<T> UntilAsync<T>(Func<T?> probe, TimeSpan timeout) where T : class
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (probe() is { } value) return value;
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The expected state was not reached.");
            await Task.Delay(50);
        }
    }

    /// <summary>A v1 .torrent (BEP 3) with 16 KiB pieces: a single file, or <paramref name="files"/> under the folder <paramref name="name"/>.</summary>
    private static (byte[] Bytes, string Hash) TorrentOf(string name, (string Path, byte[] Bytes)[]? files, byte[] single)
    {
        const int pieceLength = 16 * 1024;
        var all = files is null ? single : files.SelectMany(file => file.Bytes).ToArray();
        using var pieces = new MemoryStream();
        for (var offset = 0; offset < all.Length; offset += pieceLength)
            pieces.Write(SHA1.HashData(all.AsSpan(offset, Math.Min(pieceLength, all.Length - offset))));
        using var info = new MemoryStream();
        void W(Stream s, string text) => s.Write(Encoding.ASCII.GetBytes(text));
        void S(Stream s, string text) { var data = Encoding.UTF8.GetBytes(text); W(s, $"{data.Length}:"); s.Write(data); }
        // Keys in sorted order, as bencoding requires.
        W(info, "d");
        if (files is null)
        {
            S(info, "length"); W(info, $"i{single.Length}e");
        }
        else
        {
            S(info, "files");
            W(info, "l");
            foreach (var (path, bytes) in files)
            {
                W(info, "d");
                S(info, "length"); W(info, $"i{bytes.Length}e");
                S(info, "path");
                W(info, "l");
                foreach (var part in path.Split('/')) S(info, part);
                W(info, "e");
                W(info, "e");
            }

            W(info, "e");
        }

        S(info, "name"); S(info, name);
        S(info, "piece length"); W(info, $"i{pieceLength}e");
        var hashes = pieces.ToArray();
        W(info, $"6:pieces{hashes.Length}:");
        info.Write(hashes);
        W(info, "e");
        using var torrent = new MemoryStream();
        W(torrent, "d");
        S(torrent, "info");
        torrent.Write(info.ToArray());
        W(torrent, "e");
        return (torrent.ToArray(), Convert.ToHexStringLower(SHA1.HashData(info.ToArray())));
    }
}

/// <summary>The built native worker: INBOUND_TORRENT_WORKER, else artifacts\torrent-worker\&lt;rid&gt;\ in the repository.</summary>
internal static class TorrentWorkerBinary
{
    public static readonly string? Path = Find();

    public const string Missing = "The native torrent worker is not built (scripts\\build-torrent-worker.ps1), and INBOUND_TORRENT_WORKER is not set.";

    private static string? Find()
    {
        var fromEnv = Environment.GetEnvironmentVariable("INBOUND_TORRENT_WORKER");
        if (!string.IsNullOrEmpty(fromEnv)) return File.Exists(fromEnv) ? fromEnv : null;
        var rid = OperatingSystem.IsWindows() ? "win-x64"
            : System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "linux-arm64" : "linux-x64";
        var name = OperatingSystem.IsWindows() ? "inbound-torrent.exe" : "inbound-torrent";
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (!File.Exists(System.IO.Path.Combine(dir.FullName, "DownloadManager.slnx"))) continue;
            var candidate = System.IO.Path.Combine(dir.FullName, "artifacts", "torrent-worker", rid, name);
            return File.Exists(candidate) ? candidate : null;
        }

        return null;
    }
}

/// <summary>A test that needs the native worker; skipped, saying why, when it is not built.</summary>
public sealed class TorrentWorkerFactAttribute : FactAttribute
{
    public TorrentWorkerFactAttribute()
    {
        if (TorrentWorkerBinary.Path is null) Skip = TorrentWorkerBinary.Missing;
    }
}

/// <summary>A theory that needs the native worker; skipped, saying why, when it is not built.</summary>
public sealed class TorrentWorkerTheoryAttribute : TheoryAttribute
{
    public TorrentWorkerTheoryAttribute()
    {
        if (TorrentWorkerBinary.Path is null) Skip = TorrentWorkerBinary.Missing;
    }
}
