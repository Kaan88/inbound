using System.Security.Cryptography;
using System.Text;
using DownloadManager.Contracts;
using DownloadManager.Core;
using DownloadManager.Persistence;
using DownloadManager.Runtime;
using DownloadManager.Runtime.Ipc;
using DownloadManager.Runtime.Remote;
using DownloadManager.Torrents;

namespace DownloadManager.Integration.Tests;

/// <summary>T1: <c>torrent.*</c> through the dispatcher, with the service running torrents on the in-memory engine.</summary>
public sealed partial class HostCommandDispatcherTests
{
    private readonly List<FakeTorrentEngine> _torrentWorkers = [];

    private TorrentWorkerSupervisor? _torrentSupervisor;

    private async Task<DownloadManagerService> TorrentServiceAsync(bool enabled = true, int filesPerTorrent = 1, Task? holdSeedAdds = null, string? singleFileName = null,
        IReadOnlySet<string>? lacks = null, Action<AppSettings>? settings = null, bool ignoresAddRecheck = false, TimeProvider? timeProvider = null)
    {
        var saved = new AppSettings { TorrentsEnabled = enabled };
        settings?.Invoke(saved);
        await new JsonSettingsStore(_paths).SaveAsync(saved, CancellationToken.None);
        var store = new JsonJobStore(_paths);
        var checkpoints = new JsonCheckpointStore(_paths);
        _engine = new HttpDownloadEngine(new EngineOptions { MaxAttempts = 1, AllowLocalNetwork = true }, checkpoints: checkpoints);
        var supervisor = new TorrentWorkerSupervisor(() =>
        {
            var worker = new FakeTorrentEngine { FilesPerTorrent = filesPerTorrent, HoldSeedAdds = holdSeedAdds, SingleFileName = singleFileName, Lacks = lacks ?? new HashSet<string>(), IgnoresAddRecheck = ignoresAddRecheck };
            lock (_torrentWorkers) _torrentWorkers.Add(worker);
            return worker;
        }, new TorrentSupervisorOptions { DrainDelay = TimeSpan.FromMilliseconds(100) });
        _torrentSupervisor = supervisor;
        var torrents = new TorrentDownloadEngine(supervisor, new TorrentStore(_paths));
        _service = new DownloadManagerService(_paths, store, checkpoints, new JsonSettingsStore(_paths), _engine,
            enableSchedulerTimer: false, torrentEngine: torrents, timeProvider: timeProvider);
        // A web session's torrent has its host names looked up (report 2, M3); here none resolves, with no network call.
        _service.TorrentHostResolver = (_, _) => Task.FromResult(Array.Empty<System.Net.IPAddress>());
        await _service.InitializeAsync();
        return _service;
    }

    [Fact]
    public async Task TorrentsAreOfferedOnlyWhereTheHostHasAWorker()
    {
        var plain = await ServiceAsync();
        Assert.DoesNotContain("torrents", Dispatcher(plain).Capabilities);
        var request = IpcMessage.Create(TorrentMessageTypes.Add, new TorrentAddRequest { Input = "magnet:?xt=urn:btih:" + new string('a', 40) });
        var session = Session();
        await Dispatcher(plain).DispatchAsync(session, request, CancellationToken.None);
        Assert.NotEqual(TorrentMessageTypes.AddResult, (await session.ReplyToAsync(request.Id)).Type);
    }

    [Fact]
    public async Task WithTorrentsTurnedOffTheCommandsSaySoAndNothingIsAdded()
    {
        var service = await TorrentServiceAsync(enabled: false);
        var dispatcher = Dispatcher(service);
        Assert.Contains("torrents", dispatcher.Capabilities);
        var reply = await AskAsync(dispatcher, Session(), TorrentMessageTypes.Add, new TorrentAddRequest { Input = "magnet:?xt=urn:btih:" + new string('a', 40) });
        var result = reply.GetPayload<TorrentAddResult>();
        Assert.False(result.Ok);
        Assert.Contains("turned off", result.Error);
        Assert.Empty(service.GetSnapshot().Jobs);
        Assert.Empty(_torrentWorkers);
    }

    [Fact]
    public async Task ATorrentFileIsInspectedAddedOnceRunAndCompleted()
    {
        var service = await TorrentServiceAsync();
        var dispatcher = Dispatcher(service);
        var file = Path.Combine(_root, "film.torrent");
        var (bytes, hash) = SingleFileTorrent("film.mkv", 100_000);
        await File.WriteAllBytesAsync(file, bytes);
        var destination = Path.Combine(_root, "down");

        var inspect = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Inspect, new TorrentInspectRequest { Input = file })).GetPayload<TorrentInspectResult>();
        Assert.True(inspect.Ok, inspect.Error);
        Assert.Equal(("torrentFile", hash, "film.mkv", 100_000L), (inspect.Kind, inspect.InfoHash, inspect.Name, inspect.TotalBytes));
        Assert.Equal(0, Assert.Single(inspect.Files!).Index);
        Assert.Null(inspect.ExistingJobId);

        var added = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Add, new TorrentAddRequest
        {
            Input = file, Destination = destination, StartMode = "later",
        })).GetPayload<TorrentAddResult>();
        Assert.True(added.Ok, added.Error);
        Assert.False(added.Existing);
        var job = added.Job!;
        Assert.Equal(hash, job.Torrent!.InfoHash);
        Assert.True(job.Torrent.HasMetadata);
        Assert.Equal((1, 1, 100_000L), (job.Torrent.FileCount, job.Torrent.WantedCount, job.Torrent.WantedBytes));
        Assert.StartsWith("magnet:?xt=urn:btih:" + hash, job.Url);

        // The same torrent again is the job already there, however it is given.
        var again = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Add, new TorrentAddRequest { Input = "magnet:?xt=urn:btih:" + hash.ToUpperInvariant() }))
            .GetPayload<TorrentAddResult>();
        Assert.True(again is { Ok: true, Existing: true });
        Assert.Equal(job.Id, again.Job!.Id);
        Assert.Equal(job.Id, (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Inspect, new TorrentInspectRequest { Input = file }))
            .GetPayload<TorrentInspectResult>().ExistingJobId);
        Assert.Single(service.GetSnapshot().Jobs);

        var files = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Files, new TorrentFilesRequest { JobId = job.Id })).GetPayload<TorrentFilesResult>();
        Assert.Equal("film.mkv", Assert.Single(files.Files).Path);

        // Started, it runs on the worker and completes under the name the worker reports.
        await service.ResumeAsync(job.Id);
        var worker = await UntilAsync(() => _torrentWorkers.FirstOrDefault(w => w.Torrents.Count == 1));
        for (var i = 0; i < 4; i++) worker.Advance();
        var done = await UntilAsync(() => service.GetSnapshot().Jobs.FirstOrDefault(j => j.Id == job.Id && j.State == "completed"));
        Assert.Equal(Path.Combine(destination, job.Id.ToString("N") + ".bin"), done.FinalFilePath);
        // T2: by default it seeds once it is done; stopping that leaves nothing on the worker, which closes.
        await UntilAsync(() => service.GetSnapshot().Jobs.FirstOrDefault(j => j.Id == job.Id && j.Torrent!.Seeding));
        var stopped = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Seed, new TorrentSeedRequest { JobId = job.Id, On = false })).GetPayload<TorrentActionResult>();
        Assert.True(stopped.Ok, stopped.Error);
        Assert.False(service.GetSnapshot().Jobs.Single(j => j.Id == job.Id).Torrent!.Seeding);
        await UntilAsync(() => worker.Exited.IsCompleted ? worker : null); // the worker closes once nothing runs
    }

    [Fact]
    public async Task AMagnetThatChoosesFilesWaitsPausedThenGoesOnWithTheChoice()
    {
        var service = await TorrentServiceAsync(filesPerTorrent: 3);
        var dispatcher = Dispatcher(service);
        var hash = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(20));
        var destination = Path.Combine(_root, "down");
        var added = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Add, new TorrentAddRequest
        {
            Input = $"magnet:?xt=urn:btih:{hash}&dn=Album", Destination = destination, Files = "askWhenReady",
        })).GetPayload<TorrentAddResult>();
        Assert.True(added.Ok, added.Error);
        Assert.Equal("Album", added.Job!.Torrent!.Name);
        Assert.False(added.Job.Torrent.HasMetadata);

        var waiting = await UntilAsync(() => service.GetSnapshot().Jobs.FirstOrDefault(j => j.Torrent?.AwaitingFileChoice == true && j.State == "paused"));
        Assert.True(waiting.Torrent!.HasMetadata);
        Assert.All(_torrentWorkers, w => Assert.Empty(w.Torrents)); // it left the worker while it waits
        // The in-memory worker names the torrent (and its folder) after the job, and joins paths with '\' as libtorrent
        // does on Windows: the row is named after the torrent, not after its first file, and the list uses '/'.
        var torrentName = waiting.Id.ToString("N");
        Assert.Equal((torrentName, Path.Combine(destination, torrentName)), (waiting.FileName, waiting.FinalFilePath));
        var files = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Files, new TorrentFilesRequest { JobId = waiting.Id })).GetPayload<TorrentFilesResult>();
        Assert.Equal([$"{torrentName}/file-0.bin", $"{torrentName}/file-1.bin", $"{torrentName}/file-2.bin"], files.Files.Select(file => file.Path));

        var chosen = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.SetFiles, new TorrentSetFilesRequest { JobId = waiting.Id, FileIndexes = [0, 2] }))
            .GetPayload<TorrentActionResult>();
        Assert.True(chosen.Ok, chosen.Error);
        var worker = await UntilAsync(() => _torrentWorkers.LastOrDefault(w => w.Torrents.Count == 1));
        for (var i = 0; i < 4; i++) worker.Advance();
        var done = await UntilAsync(() => service.GetSnapshot().Jobs.FirstOrDefault(j => j.Id == waiting.Id && j.State == "completed"));
        Assert.Equal((torrentName, Path.Combine(destination, torrentName)), (done.FileName, done.FinalFilePath));
    }

    [Fact]
    public async Task AChoiceForAWaitingMagnetIsRefusedWhenItsFilesDoNotFit()
    {
        // Review of audit L4: a magnet held for a choice stopped at its first file list before the room check, and the
        // run after the choice no longer counted as the first list, so it was never checked.
        var service = await TorrentServiceAsync(filesPerTorrent: 3);
        var dispatcher = Dispatcher(service);
        var hash = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(20));
        var added = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Add, new TorrentAddRequest
        {
            Input = $"magnet:?xt=urn:btih:{hash}&dn=Album", Destination = Path.Combine(_root, "down"), Files = "askWhenReady",
        })).GetPayload<TorrentAddResult>();
        Assert.True(added.Ok, added.Error);
        var waiting = await UntilAsync(() => service.GetSnapshot().Jobs.FirstOrDefault(j => j.Torrent?.AwaitingFileChoice == true && j.State == "paused"));
        var files = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Files, new TorrentFilesRequest { JobId = waiting.Id })).GetPayload<TorrentFilesResult>();
        var both = files.Files[0].Size + files.Files[2].Size;

        service.MeasureFreeBytes = (_, _) => Task.FromResult<long?>(both - 1);
        var refused = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.SetFiles, new TorrentSetFilesRequest { JobId = waiting.Id, FileIndexes = [0, 2] }))
            .GetPayload<TorrentActionResult>();
        Assert.False(refused.Ok);
        Assert.StartsWith("Not enough free space", refused.Error);
        var still = service.GetSnapshot().Jobs.Single(j => j.Id == waiting.Id);
        Assert.True(still.Torrent!.AwaitingFileChoice);
        Assert.Equal("paused", still.State);
        Assert.All(_torrentWorkers, w => Assert.Empty(w.Torrents));

        // Fewer files fit, and go on.
        var fewer = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.SetFiles, new TorrentSetFilesRequest { JobId = waiting.Id, FileIndexes = [0] }))
            .GetPayload<TorrentActionResult>();
        Assert.True(fewer.Ok, fewer.Error);
        await UntilAsync(() => _torrentWorkers.LastOrDefault(w => w.Torrents.Count == 1));

        // Once it runs, a change of choice is not checked here (the files may be partly on disk).
        var more = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.SetFiles, new TorrentSetFilesRequest { JobId = waiting.Id, FileIndexes = [0, 2] }))
            .GetPayload<TorrentActionResult>();
        Assert.True(more.Ok, more.Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AMultiFileTorrentKeepsItsFolderUnlessOneOfItsFilesIsAlreadyThere(bool clash)
    {
        var service = await TorrentServiceAsync(filesPerTorrent: 2);
        var dispatcher = Dispatcher(service);
        var hash = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(20));
        var destination = Path.Combine(_root, "down");
        var job = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Add, new TorrentAddRequest
        {
            Input = $"magnet:?xt=urn:btih:{hash}&dn=Pack", Destination = destination,
        })).GetPayload<TorrentAddResult>().Job!;
        var worker = await UntilAsync(() => _torrentWorkers.FirstOrDefault(w => w.Torrents.Count == 1));
        var name = job.Id.ToString("N");
        // Its folder exists while it downloads (its part files are in it); a user's file there of another name changes nothing.
        var folder = Path.Combine(destination, name);
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, clash ? "file-0.bin" : "notes.txt"), "the user's");
        for (var i = 0; i < 4; i++) worker.Advance();
        var done = await UntilAsync(() => service.GetSnapshot().Jobs.FirstOrDefault(j => j.Id == job.Id && j.State == "completed"));
        var expected = clash ? name + " (2)" : name;
        Assert.Equal((expected, Path.Combine(destination, expected)), (done.FileName, done.FinalFilePath));
    }

    [Fact]
    public async Task ASingleFileMagnetThatChoosesFilesFirstStartsAtOnceWithItsOneFile()
    {
        var service = await TorrentServiceAsync();
        var dispatcher = Dispatcher(service);
        var hash = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(20));
        var added = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Add, new TorrentAddRequest
        {
            Input = $"magnet:?xt=urn:btih:{hash}&dn=one.bin", Destination = Path.Combine(_root, "down"), Files = "askWhenReady",
        })).GetPayload<TorrentAddResult>();
        Assert.True(added.Ok, added.Error);

        // The worker holds it for a choice; with one file there is none, so it goes on in the same run.
        var worker = await UntilAsync(() => _torrentWorkers.FirstOrDefault(w => w.Torrents.Count == 1));
        Assert.True(worker.Adds.Single()["hold"]?.GetValue<bool>());
        var running = await UntilAsync(() => service.GetSnapshot().Jobs.FirstOrDefault(j => j.Id == added.Job!.Id && j.Torrent!.HasMetadata));
        Assert.False(running.Torrent!.AwaitingFileChoice);
        for (var i = 0; i < 4; i++) worker.Advance();
        var done = await UntilAsync(() => service.GetSnapshot().Jobs.FirstOrDefault(j => j.Id == added.Job.Id && j.State == "completed"));
        Assert.False(done.Torrent!.AwaitingFileChoice);
        // never left the worker to wait (the add that seeds it once finished is not counted)
        Assert.Single(worker.Adds, add => add["seed"]?.GetValue<bool>() != true);
    }

    [Fact]
    public async Task TheHiddenPartsFileGoesWhenATorrentFinishesAndNothingElseDoes()
    {
        var service = await TorrentServiceAsync(filesPerTorrent: 2);
        var dispatcher = Dispatcher(service);
        var hash = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(20));
        var destination = Path.Combine(_root, "down");
        Directory.CreateDirectory(destination);
        var parts = Path.Combine(destination, $".{hash}.parts");
        var otherParts = Path.Combine(destination, $".{new string('c', 40)}.parts");
        var lookAlike = Path.Combine(destination, $"{hash}.parts");
        foreach (var path in new[] { parts, otherParts, lookAlike }) await File.WriteAllTextAsync(path, "x");

        var job = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Add, new TorrentAddRequest
        {
            Input = $"magnet:?xt=urn:btih:{hash}&dn=Pack", Destination = destination,
        })).GetPayload<TorrentAddResult>().Job!;
        var worker = await UntilAsync(() => _torrentWorkers.FirstOrDefault(w => w.Torrents.Count == 1));
        for (var i = 0; i < 4; i++) worker.Advance();
        await UntilAsync(() => service.GetSnapshot().Jobs.FirstOrDefault(j => j.Id == job.Id && j.State == "completed"));
        Assert.False(File.Exists(parts));
        Assert.True(File.Exists(otherParts));
        Assert.True(File.Exists(lookAlike));
    }

    [Fact]
    public async Task DeletingATorrentWithItsFilesTakesItsHiddenPartsFileToo()
    {
        var service = await TorrentServiceAsync();
        var dispatcher = Dispatcher(service);
        var hash = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(20));
        var destination = Path.Combine(_root, "down");
        var job = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Add, new TorrentAddRequest
        {
            Input = $"magnet:?xt=urn:btih:{hash}&dn=Show", Destination = destination,
        })).GetPayload<TorrentAddResult>().Job!;
        var worker = await UntilAsync(() => _torrentWorkers.FirstOrDefault(w => w.Torrents.Count == 1));
        worker.Advance();
        var parts = Path.Combine(destination, $".{hash}.parts");
        var other = Path.Combine(destination, "keep.txt");
        await File.WriteAllTextAsync(parts, "x");
        await File.WriteAllTextAsync(other, "mine");

        Assert.True((await service.RemoveAsync(job.Id, deleteFile: true)).Ok);
        Assert.False(File.Exists(parts));
        Assert.Equal("mine", await File.ReadAllTextAsync(other));
    }

    [Fact]
    public async Task ATorrentDeletedAndAddedAgainRunsAgainOnTheWorkerStillRunning()
    {
        var service = await TorrentServiceAsync();
        var dispatcher = Dispatcher(service);
        var hash = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(20));
        var destination = Path.Combine(_root, "down");
        var magnet = $"magnet:?xt=urn:btih:{hash}&dn=Again";
        var first = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Add, new TorrentAddRequest { Input = magnet, Destination = destination }))
            .GetPayload<TorrentAddResult>().Job!;
        var worker = await UntilAsync(() => _torrentWorkers.FirstOrDefault(w => w.Torrents.Count == 1));
        worker.Advance();
        Assert.True((await service.RemoveAsync(first.Id, deleteFile: true)).Ok);
        Assert.Empty(worker.Torrents); // the delete took it off the worker
        var second = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Add, new TorrentAddRequest { Input = magnet, Destination = destination }))
            .GetPayload<TorrentAddResult>();
        Assert.True(second.Ok, second.Error);
        Assert.False(second.Existing);
        Assert.NotEqual(first.Id, second.Job!.Id);
        var running = await UntilAsync(() => _torrentWorkers.LastOrDefault(w => w.Torrents.Contains(second.Job.Id.ToString("N"))));
        for (var i = 0; i < 4; i++) running.Advance();
        await UntilAsync(() => service.GetSnapshot().Jobs.FirstOrDefault(j => j.Id == second.Job.Id && j.State == "completed"));
    }

    [Fact]
    public async Task BadTorrentInputIsRefusedWithAReason()
    {
        var service = await TorrentServiceAsync();
        var dispatcher = Dispatcher(service);
        async Task<string?> Add(TorrentAddRequest request) =>
            (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Add, request)).GetPayload<TorrentAddResult>().Error;

        Assert.NotNull(await Add(new TorrentAddRequest { Input = "" }));
        Assert.NotNull(await Add(new TorrentAddRequest { Input = "magnet:?dn=nohash" }));
        Assert.NotNull(await Add(new TorrentAddRequest { Input = "relative.torrent" }));
        Assert.NotNull(await Add(new TorrentAddRequest { Input = Path.Combine(_root, "missing.torrent") }));
        var junk = Path.Combine(_root, "junk.torrent");
        await File.WriteAllTextAsync(junk, "not bencode");
        Assert.NotNull(await Add(new TorrentAddRequest { Input = junk }));
        // A magnet's files are not known yet, so they can't be chosen now.
        Assert.NotNull(await Add(new TorrentAddRequest { Input = "magnet:?xt=urn:btih:" + new string('b', 40), Files = "chosen", FileIndexes = [0] }));
        var (bytes, _) = SingleFileTorrent("one.bin", 1000);
        var file = Path.Combine(_root, "one.torrent");
        await File.WriteAllBytesAsync(file, bytes);
        Assert.NotNull(await Add(new TorrentAddRequest { Input = file, Files = "chosen", FileIndexes = [5] }));
        Assert.Empty(service.GetSnapshot().Jobs);
    }

    [Fact]
    public async Task ATorrentWhoseNameIsTakenWhenItFinishesGetsAFreeOneAndItsFilesStayFixed()
    {
        var service = await TorrentServiceAsync();
        var dispatcher = Dispatcher(service);
        var file = Path.Combine(_root, "clip.torrent");
        var (bytes, _) = SingleFileTorrent("clip.mkv", 50_000);
        await File.WriteAllBytesAsync(file, bytes);
        var destination = Path.Combine(_root, "down");
        var job = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Add, new TorrentAddRequest { Input = file, Destination = destination }))
            .GetPayload<TorrentAddResult>().Job!;
        // The in-memory worker names the file after the job; something else takes that name before it finishes.
        var worker = await UntilAsync(() => _torrentWorkers.FirstOrDefault(w => w.Torrents.Count == 1));
        var taken = Path.Combine(destination, job.Id.ToString("N") + ".bin");
        worker.Advance();
        await UntilAsync(() => service.GetSnapshot().Jobs.FirstOrDefault(j => j.Id == job.Id && j.FinalFilePath == taken));
        await File.WriteAllTextAsync(taken, "someone else's");
        for (var i = 0; i < 4; i++) worker.Advance();
        var done = await UntilAsync(() => service.GetSnapshot().Jobs.FirstOrDefault(j => j.Id == job.Id && j.State == "completed"));
        Assert.Equal(Path.Combine(destination, job.Id.ToString("N") + " (2).bin"), done.FinalFilePath);
        Assert.Equal("someone else's", await File.ReadAllTextAsync(taken));

        // Finished, its files and place stay as they are.
        var setFiles = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.SetFiles, new TorrentSetFilesRequest { JobId = job.Id, FileIndexes = [0] }))
            .GetPayload<TorrentActionResult>();
        Assert.False(setFiles.Ok);
        var move = await service.UpdateJobAsync(new JobUpdateRequest { JobId = job.Id, NewFilePath = Path.Combine(destination, "moved.bin") });
        Assert.False(move.Ok);
        Assert.Contains("torrent", move.Error);
    }

    [Fact]
    public async Task TurningTorrentsOffStopsARunningOneWithoutMarkingItPausedByTheUser()
    {
        var service = await TorrentServiceAsync();
        var dispatcher = Dispatcher(service);
        var hash = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(20));
        var job = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Add, new TorrentAddRequest
        {
            Input = $"magnet:?xt=urn:btih:{hash}&dn=Show", Destination = Path.Combine(_root, "down"),
        })).GetPayload<TorrentAddResult>().Job!;
        var worker = await UntilAsync(() => _torrentWorkers.FirstOrDefault(w => w.Torrents.Count == 1));
        worker.Advance();

        var settings = service.GetSettings();
        settings.TorrentsEnabled = false;
        Assert.True((await service.UpdateSettingsAsync(settings)).Ok);
        await UntilAsync(() => worker.Torrents.Count == 0 ? worker : null);
        var stopped = await UntilAsync(() => service.GetSnapshot().Jobs.FirstOrDefault(j => j.Id == job.Id && j.State == "queued"));
        Assert.False(stopped.PausedByUser);
        await Task.Delay(300);
        Assert.Equal("queued", service.GetSnapshot().Jobs.Single(j => j.Id == job.Id).State);
    }

    [Fact]
    public async Task AnUploadReferenceWhoseFileIsGoneSaysSo()
    {
        var service = await TorrentServiceAsync();
        service.ResolveTorrentUpload = _ => null;
        var reply = (await AskAsync(Dispatcher(service), Session(), TorrentMessageTypes.Add, new TorrentAddRequest { Input = "upload:" + new string('a', 32) }))
            .GetPayload<TorrentAddResult>();
        Assert.Equal(UploadFolder.GoneText, reply.Error);
    }

    private static async Task<T> UntilAsync<T>(Func<T?> probe) where T : class
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (true)
        {
            if (probe() is { } value) return value;
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The expected state was not reached.");
            await Task.Delay(20);
        }
    }

    /// <summary>A valid single-file v1 .torrent (BEP 3) and its info hash.</summary>
    internal static (byte[] Bytes, string Hash) SingleFileTorrent(string name, int size)
    {
        const int pieceLength = 16384;
        var payload = new byte[size];
        using var pieces = new MemoryStream();
        for (var offset = 0; offset < size; offset += pieceLength)
            pieces.Write(SHA1.HashData(payload.AsSpan(offset, Math.Min(pieceLength, size - offset))));
        using var info = new MemoryStream();
        void W(Stream s, string text) => s.Write(Encoding.ASCII.GetBytes(text));
        void S(Stream s, byte[] data) { W(s, $"{data.Length}:"); s.Write(data); }
        W(info, "d");
        S(info, "length"u8.ToArray()); W(info, $"i{size}e");
        S(info, "name"u8.ToArray()); S(info, Encoding.UTF8.GetBytes(name));
        S(info, "piece length"u8.ToArray()); W(info, $"i{pieceLength}e");
        S(info, "pieces"u8.ToArray()); S(info, pieces.ToArray());
        W(info, "e");
        using var torrent = new MemoryStream();
        W(torrent, "d");
        S(torrent, "info"u8.ToArray());
        torrent.Write(info.ToArray());
        W(torrent, "e");
        return (torrent.ToArray(), Convert.ToHexStringLower(SHA1.HashData(info.ToArray())));
    }
}
