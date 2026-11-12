using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using DownloadManager.Core;

namespace DownloadManager.Torrents.Tests;

/// <summary>
/// T1 step 3: <see cref="TorrentWorkerSupervisor"/> and <see cref="TorrentDownloadEngine"/>. The first group uses the
/// in-memory engine (lifecycle, restart, failure); the second the native worker against a loopback seeder.
/// </summary>
public sealed partial class EngineTests(ITestOutputHelper output) : IDisposable
{
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(60);
    private readonly string _root = Directory.CreateTempSubdirectory("inbound-engine-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ---- in-memory engine ----

    [Fact]
    public async Task TheWorkerStartsForTheFirstTorrentAndClosesAfterTheLast()
    {
        var started = new List<FakeTorrentEngine>();
        await using var supervisor = new TorrentWorkerSupervisor(() => { var f = new FakeTorrentEngine(); started.Add(f); return f; },
            new TorrentSupervisorOptions { DrainDelay = TimeSpan.FromMilliseconds(200) });
        Assert.Equal(TorrentWorkerState.Off, supervisor.State);
        var engine = new TorrentDownloadEngine(supervisor, new MemoryStore(), Log);
        var job = Job("a");

        var run = engine.ExecuteAsync(job, null, null, CancellationToken.None);
        var fake = await WaitForAsync(() => started.FirstOrDefault(f => f.Torrents.Count == 1), "the torrent added");
        Assert.Equal(TorrentWorkerState.Active, supervisor.State);
        for (var i = 0; i < 4; i++) fake.Advance();
        var result = await run.WaitAsync(Long);

        Assert.True(result.Success, result.Failure?.Message);
        Assert.Equal(Path.Combine(job.DestinationDirectory, job.Id.ToString("N") + ".bin"), result.FinalFilePath);
        Assert.Empty(fake.Torrents); // removed from the worker after the final renames
        await WaitForAsync(() => supervisor.State == TorrentWorkerState.Off ? "" : null, "the worker closed");
        Assert.True(fake.Exited.IsCompleted);
        Assert.Single(started);
    }

    [Fact]
    public async Task TheIntentToFinalizeIsSavedBeforeTheWorkerIsToldAndClearedWithTheFinalizedSave()
    {
        // Audit M16: nothing was saved before the renames, so a kill between them and the Host's save lost them.
        FakeTorrentEngine? fake = null;
        await using var supervisor = new TorrentWorkerSupervisor(() => fake = new FakeTorrentEngine());
        var engine = new TorrentDownloadEngine(supervisor, new MemoryStore(), Log);
        var sink = new Sink { TopName = name => name + ".renamed" };
        var finalizeSentFirst = (bool?)null;
        sink.OnFinalizing = () => finalizeSentFirst ??= fake!.Sent.Any(sent => sent.Command == "finalize");
        engine.SetSink(sink);
        var job = Job("intent");

        var run = engine.ExecuteAsync(job, null, null, CancellationToken.None);
        await WaitForAsync(() => fake?.Torrents.Count == 1 ? fake : null, "the torrent added");
        for (var i = 0; i < 4; i++) fake!.Advance();
        Assert.True((await run.WaitAsync(Long)).Success);

        Assert.False(finalizeSentFirst);
        var call = Assert.Single(sink.FinalizingCalls);
        Assert.Equal((job.Id, job.Id.ToString("N") + ".bin.renamed", true), call);
        Assert.Equal(job.Id.ToString("N") + ".bin.renamed", sink.Finalized[job.Id].Root);
    }

    [Fact]
    public async Task AFinalizeMarkThatCannotBeSavedSendsNoFinalizeAndFailsForARetry()
    {
        // Review 2 L6: the mark must be on disk before the worker renames anything; a failed save stops the renames.
        FakeTorrentEngine? fake = null;
        await using var supervisor = new TorrentWorkerSupervisor(() => fake = new FakeTorrentEngine());
        var engine = new TorrentDownloadEngine(supervisor, new MemoryStore(), Log);
        var sink = new Sink { OnFinalizing = () => throw new IOException("disk full") };
        engine.SetSink(sink);
        var job = Job("unsaved");

        var run = engine.ExecuteAsync(job, null, null, CancellationToken.None);
        await WaitForAsync(() => fake?.Torrents.Count == 1 ? fake : null, "the torrent added");
        for (var i = 0; i < 4; i++) fake!.Advance();
        var result = await run.WaitAsync(Long);

        Assert.False(result.Success);
        Assert.True(result.Failure!.IsRetryable);
        Assert.DoesNotContain(fake!.Sent, sent => sent.Command == "finalize");
        Assert.False(sink.Finalized.ContainsKey(job.Id));
    }

    [Fact]
    public async Task AFinalizeAKillInterruptedIsFinishedAtTheNextStartAndCheckedNotDownloaded()
    {
        // The worker renamed one of two files when the Host was killed: Finalizing is on disk, Finalized is not.
        var store = new MemoryStore();
        FakeTorrentEngine? fake = null;
        await using var supervisor = new TorrentWorkerSupervisor(() => fake = new FakeTorrentEngine());
        var engine = new TorrentDownloadEngine(supervisor, store, Log);
        var sink = new Sink();
        engine.SetSink(sink);
        var job = Job("killed");
        var info = job.Torrent!;
        info.HasMetadata = true;
        info.Finalizing = true;
        info.FinalizingRootName = "pack (2)";
        store.Files[job.Id] = [new TorrentFile(0, "pack/a.bin", 3, true), new TorrentFile(1, "pack/b.bin", 3, true), new TorrentFile(2, "pack/c.bin", 3, false)];
        var dest = job.DestinationDirectory;
        Directory.CreateDirectory(Path.Combine(dest, "pack (2)"));
        Directory.CreateDirectory(Path.Combine(dest, "pack"));
        File.WriteAllText(Path.Combine(dest, "pack (2)", "a.bin"), "aaa");
        File.WriteAllText(Path.Combine(dest, "pack", "b.bin" + info.PartSuffix), "bbb");
        File.WriteAllText(Path.Combine(dest, "pack", "c.bin" + info.PartSuffix), "ccc");

        var run = engine.ExecuteAsync(job, null, null, CancellationToken.None);
        await WaitForAsync(() => fake?.Torrents.Count == 1 ? fake : null, "the torrent added");
        var add = Assert.Single(fake!.Adds);
        Assert.True(add["finalized"]!.GetValue<bool>());
        Assert.True(add["recheck"]!.GetValue<bool>());
        Assert.Equal("pack (2)", add["rootName"]!.GetValue<string>());
        Assert.Equal("bbb", File.ReadAllText(Path.Combine(dest, "pack (2)", "b.bin")));
        Assert.False(File.Exists(Path.Combine(dest, "pack", "b.bin" + info.PartSuffix)));
        Assert.True(File.Exists(Path.Combine(dest, "pack", "c.bin" + info.PartSuffix))); // not wanted: not ours to move
        Assert.Equal("pack (2)", sink.Finalized[job.Id].Root);
        Assert.Equal(["pack (2)/a.bin", "pack (2)/b.bin", "pack (2)/c.bin"], sink.Finalized[job.Id].Files.Select(file => file.Path));

        // Added finalized, it has all its data (the check found it): the worker says so, and nothing is downloaded.
        fake.State(job.Id.ToString("N"), "seeding");
        Assert.True((await run.WaitAsync(Long)).Success);
    }

    [Theory]
    [InlineData(false, false)] // nothing renamed yet: the files keep their part names
    [InlineData(true, true)] // a name taken by something else
    public void AnInterruptedFinalizeIsLeftAloneWhenItHadNotBegunOrANameIsTaken(bool someRenamed, bool conflict)
    {
        var dest = Path.Combine(_root, "left-" + Guid.NewGuid().ToString("N"));
        const string suffix = ".x.part";
        Directory.CreateDirectory(Path.Combine(dest, "pack"));
        Directory.CreateDirectory(Path.Combine(dest, "pack (2)"));
        File.WriteAllText(Path.Combine(dest, "pack", "a.bin" + suffix), "a");
        File.WriteAllText(Path.Combine(dest, "pack", "b.bin" + suffix), "b");
        if (someRenamed) File.WriteAllText(Path.Combine(dest, "pack (2)", "c.bin"), "c");
        if (conflict) File.WriteAllText(Path.Combine(dest, "pack (2)", "a.bin"), "the user's");
        IReadOnlyList<TorrentFile> files = [new(0, "pack/a.bin", 1, true), new(1, "pack/b.bin", 1, true), new(2, "pack/c.bin", 1, true)];

        var outcome = TorrentDownloadEngine.FinishInterruptedFinalize(dest, files, "pack (2)", suffix);

        Assert.Null(outcome.Files);
        Assert.Null(outcome.Problem);
        Assert.True(File.Exists(Path.Combine(dest, "pack", "a.bin" + suffix)));
        Assert.True(File.Exists(Path.Combine(dest, "pack", "b.bin" + suffix)));
        if (conflict) Assert.Equal("the user's", File.ReadAllText(Path.Combine(dest, "pack (2)", "a.bin")));
    }

    [Fact]
    public async Task AFinalizeThatNeverBeganIsClearedAndTheTorrentFinishesAsUsual()
    {
        var store = new MemoryStore();
        FakeTorrentEngine? fake = null;
        await using var supervisor = new TorrentWorkerSupervisor(() => fake = new FakeTorrentEngine());
        var engine = new TorrentDownloadEngine(supervisor, store, Log);
        var sink = new Sink();
        engine.SetSink(sink);
        var job = Job("never");
        job.Torrent!.Finalizing = true;
        store.Files[job.Id] = [new TorrentFile(0, job.Id.ToString("N") + ".bin", FakeTorrentEngine.FakeFileSize, true)];

        var run = engine.ExecuteAsync(job, null, null, CancellationToken.None);
        await WaitForAsync(() => fake?.Torrents.Count == 1 ? fake : null, "the torrent added");
        Assert.Null(Assert.Single(fake!.Adds)["finalized"]);
        Assert.Equal((job.Id, (string?)null, false), sink.FinalizingCalls.First());
        for (var i = 0; i < 4; i++) fake.Advance();
        Assert.True((await run.WaitAsync(Long)).Success);
    }

    [Fact]
    public async Task AStopPausesSavesResumeDataAndLeavesTheWorker()
    {
        var store = new MemoryStore();
        FakeTorrentEngine? fake = null;
        await using var supervisor = new TorrentWorkerSupervisor(() => fake = new FakeTorrentEngine(),
            new TorrentSupervisorOptions { DrainDelay = TimeSpan.FromSeconds(30) });
        var engine = new TorrentDownloadEngine(supervisor, store, Log);
        var job = Job("b");
        using var cts = new CancellationTokenSource();

        var run = engine.ExecuteAsync(job, null, null, cts.Token);
        await WaitForAsync(() => fake?.Torrents.Count == 1 ? fake : null, "the torrent added");
        fake!.Advance();
        cts.Cancel();
        var result = await run.WaitAsync(Long);

        Assert.Equal(DownloadState.Cancelled, result.FinalState);
        Assert.Equal("fake:1024", System.Text.Encoding.ASCII.GetString(store.Resume[job.Id]));
        Assert.Empty(fake.Torrents);
        Assert.Equal(TorrentWorkerState.Draining, supervisor.State);
    }

    [Fact]
    public async Task AWorkerThatDiesIsStartedAgainOnceAndTheTorrentIsAddedBackFromItsResumeData()
    {
        var store = new MemoryStore();
        var started = new List<FakeTorrentEngine>();
        var adds = new ConcurrentQueue<JsonObject>();
        await using var supervisor = new TorrentWorkerSupervisor(() => { var f = new FakeTorrentEngine(); lock (started) started.Add(f); return f; });
        var engine = new TorrentDownloadEngine(supervisor, store, Log);
        var job = Job("c");
        store.Resume[job.Id] = "saved"u8.ToArray();

        var run = engine.ExecuteAsync(job, null, null, CancellationToken.None);
        var first = await WaitForAsync(() => started.FirstOrDefault(f => f.Torrents.Count == 1), "the first add");
        first.Crash();
        var second = await WaitForAsync(() => started.Skip(1).FirstOrDefault(f => f.Torrents.Count == 1), "the add after the restart");
        for (var i = 0; i < 4; i++) second.Advance();
        var result = await run.WaitAsync(Long);
        Assert.True(result.Success, result.Failure?.Message);

        // A second death within two minutes fails the job (retryable) instead of starting a third worker.
        var again = engine.ExecuteAsync(Job("d"), null, null, CancellationToken.None);
        var current = await WaitForAsync(() => started.LastOrDefault(f => f.Torrents.Count == 1), "the next torrent");
        current.Crash();
        var failed = await again.WaitAsync(Long);
        Assert.Equal(DownloadState.Failed, failed.FinalState);
        Assert.True(failed.Failure!.IsRetryable);
        Assert.Equal(2, started.Count);
        Assert.Equal(TorrentWorkerState.Off, supervisor.State);
    }

    [Fact]
    public async Task AnEventWithAFieldOfTheWrongTypeIsSkippedNotTakenForTheWorkersExit()
    {
        // Audit L2: GetValue<string>() on a number threw out of the event pump, which then restarted the worker as if
        // it had exited, with the first one still running on the same files.
        var started = new List<FakeTorrentEngine>();
        await using var supervisor = new TorrentWorkerSupervisor(() => { var f = new FakeTorrentEngine(); lock (started) started.Add(f); return f; });
        var engine = new TorrentDownloadEngine(supervisor, new MemoryStore(), Log);
        var job = Job("malformed");

        var run = engine.ExecuteAsync(job, null, null, CancellationToken.None);
        var fake = await WaitForAsync(() => started.FirstOrDefault(f => f.Torrents.Count == 1), "the add");
        fake.Inject(new JsonObject { ["event"] = 5 });
        fake.Inject(new JsonObject { ["event"] = "progress", ["torrents"] = new JsonArray(new JsonObject { ["id"] = 7 }) });
        fake.Inject(new JsonObject { ["event"] = "error", ["id"] = null, ["kind"] = true });
        for (var i = 0; i < 4; i++) fake.Advance();

        var result = await run.WaitAsync(Long);
        Assert.True(result.Success, result.Failure?.Message);
        Assert.Single(started);
    }

    [Fact]
    public async Task AJobWaitingForItsFileChoiceDoesNotStartTheWorker()
    {
        var started = 0;
        await using var supervisor = new TorrentWorkerSupervisor(() => { started++; return new FakeTorrentEngine(); });
        var engine = new TorrentDownloadEngine(supervisor, new MemoryStore(), Log);
        var job = Job("e");
        job.Torrent!.AwaitingFileChoice = true;
        var result = await engine.ExecuteAsync(job, null, null, CancellationToken.None);
        Assert.Equal(DownloadState.Paused, result.FinalState);
        Assert.Equal(FailureKind.None, result.Failure!.Kind);
        Assert.Equal(0, started);
    }

    [Fact]
    public async Task AWorkerWithoutTheFilesCommandGivesNoFileProgressAndIsNotAskedAgain()
    {
        // The in-memory engine answers "files" as an older worker does: unknown command.
        FakeTorrentEngine? fake = null;
        await using var supervisor = new TorrentWorkerSupervisor(() => fake = new FakeTorrentEngine(),
            new TorrentSupervisorOptions { DrainDelay = TimeSpan.FromSeconds(30) });
        var lines = new List<string>();
        var engine = new TorrentDownloadEngine(supervisor, new MemoryStore(), line => { lock (lines) lines.Add(line); });
        var job = Job("f");
        Assert.Null(await engine.GetFileProgressAsync(job.Id, CancellationToken.None)); // not on the worker

        using var cts = new CancellationTokenSource();
        var run = engine.ExecuteAsync(job, null, null, cts.Token);
        await WaitForAsync(() => fake?.Torrents.Count == 1 ? fake : null, "the torrent added");
        Assert.Null(await engine.GetFileProgressAsync(job.Id, CancellationToken.None));
        Assert.Null(await engine.GetFileProgressAsync(job.Id, CancellationToken.None));
        lock (lines) Assert.Single(lines, line => line.Contains("each file's progress", StringComparison.Ordinal));
        cts.Cancel();
        await run.WaitAsync(Long);
    }

    [Fact]
    public void AFilesReplyIsReadByIndexAndAMalformedOneIsDropped()
    {
        // As the worker's reply arrives: parsed from its JSON.
        static IReadOnlyList<long>? Read(string json) => TorrentDownloadEngine.ReadFileProgress(JsonNode.Parse(json)!.AsObject());
        Assert.Equal([0L, 5L, 5_000_000_000L], Read("""{"done":[0,5,5000000000]}"""));
        Assert.Empty(Read("""{"done":[]}""")!);
        Assert.Null(Read("{}"));
        Assert.Null(Read("""{"done":[1,-2]}"""));
        Assert.Null(Read("""{"done":["x"]}"""));
        Assert.Null(Read("""{"done":[null]}"""));
    }

    // ---- native worker on loopback ----

    [WorkerFact]
    public async Task ATorrentJobDownloadsUnderItsPartNameAndEndsUnderItsOwnName()
    {
        var (payload, torrent) = Payload("film.bin", 4 * 1024 * 1024);
        var hash = TestTorrent.LastInfoHash; // thread-static: read before the first await
        await using var seeder = await StartSeederAsync(torrent);
        var port = await seeder.ListenOnLoopbackAsync();
        var store = new MemoryStore();
        await using var supervisor = LoopbackSupervisor();
        var engine = new TorrentDownloadEngine(supervisor, store, Log);
        var sink = new Sink();
        engine.SetSink(sink);
        var job = Job("film", magnet: $"magnet:?xt=urn:btih:{hash}");
        store.Metadata[job.Id] = torrent;

        var seenPart = false;
        var progress = new Progress<TransferProgress>(_ =>
        {
            seenPart |= File.Exists(Path.Combine(job.DestinationDirectory, "film.bin" + job.Torrent!.PartSuffix));
        });
        var run = engine.ExecuteAsync(job, null, progress, CancellationToken.None);
        await AddPeerAsync(supervisor, job, port, run);
        var result = await run.WaitAsync(Long);

        Assert.True(result.Success, result.Failure?.Message);
        var final = Path.Combine(job.DestinationDirectory, "film.bin");
        Assert.Equal(final, result.FinalFilePath);
        Assert.Equal(payload, await File.ReadAllBytesAsync(final));
        Assert.False(File.Exists(final + job.Torrent!.PartSuffix));
        Assert.True(sink.Metadata.ContainsKey(job.Id));
        Assert.Single(store.Files[job.Id]);
        Assert.True(store.Resume.ContainsKey(job.Id));
        output.WriteLine($"part file seen during the transfer: {seenPart}");
    }

    [WorkerFact]
    public async Task ASingleFileMagnetThatChoosesFilesFirstDownloadsWithoutWaitingForAChoice()
    {
        // The held path itself (several files, then the choice, then a run from the resume data) is walked with the native
        // worker in the Integration tests (AMultiFileTorrentKeepsItsOwnNameAndLeavesNoPartsFileAfterSkippedFiles).
        var (payload, torrent) = Payload("held.bin", 2 * 1024 * 1024);
        var hash = TestTorrent.LastInfoHash; // thread-static: read before the first await
        await using var seeder = await StartSeederAsync(torrent);
        var port = await seeder.ListenOnLoopbackAsync();
        var store = new MemoryStore();
        await using var supervisor = LoopbackSupervisor();
        var engine = new TorrentDownloadEngine(supervisor, store, Log);
        var sink = new Sink();
        engine.SetSink(sink);
        var job = Job("held", magnet: $"magnet:?xt=urn:btih:{hash}");
        job.Torrent!.ChooseFilesWhenReady = true;

        var run = engine.ExecuteAsync(job, null, null, CancellationToken.None);
        await AddPeerAsync(supervisor, job, port, run);
        var result = await run.WaitAsync(Long);
        Assert.True(result.Success, result.Failure?.Message);
        Assert.False(sink.Metadata[job.Id]); // the worker held it; with one file there was nothing to ask
        Assert.True(Assert.Single(store.Files[job.Id]).Wanted);
        Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(job.DestinationDirectory, "held.bin")));
    }

    [WorkerFact]
    public async Task AFinishedTorrentWhoseNameIsTakenGetsTheFreeNameItIsGivenAndTheUsersFileStays()
    {
        var (payload, torrent) = Payload("taken.bin", 1024 * 1024);
        var hash = TestTorrent.LastInfoHash; // thread-static: read before the first await
        await using var seeder = await StartSeederAsync(torrent);
        var port = await seeder.ListenOnLoopbackAsync();
        var store = new MemoryStore();
        await using var supervisor = LoopbackSupervisor();
        var engine = new TorrentDownloadEngine(supervisor, store, Log);
        var sink = new Sink { TopName = name => name == "taken.bin" ? "taken (2).bin" : name };
        engine.SetSink(sink);
        var job = Job("taken", magnet: $"magnet:?xt=urn:btih:{hash}");
        store.Metadata[job.Id] = torrent;
        Directory.CreateDirectory(job.DestinationDirectory);
        var mine = Path.Combine(job.DestinationDirectory, "taken.bin");
        await File.WriteAllTextAsync(mine, "the user's own file");

        var run = engine.ExecuteAsync(job, null, null, CancellationToken.None);
        await AddPeerAsync(supervisor, job, port, run);
        var result = await run.WaitAsync(Long);

        Assert.True(result.Success, result.Failure?.Message);
        var final = Path.Combine(job.DestinationDirectory, "taken (2).bin");
        Assert.Equal(final, result.FinalFilePath);
        Assert.Equal(payload, await File.ReadAllBytesAsync(final));
        Assert.Equal("the user's own file", await File.ReadAllTextAsync(mine));
        Assert.Equal(("taken (2).bin", "taken (2).bin"), (sink.Finalized[job.Id].Root, Assert.Single(sink.Finalized[job.Id].Files).Path));
    }

    [WorkerFact]
    public async Task FinishingRefusesToRenameOverAFileThatIsAlreadyThere()
    {
        var (_, torrent) = Payload("clash.bin", 1024 * 1024);
        var hash = TestTorrent.LastInfoHash; // thread-static: read before the first await
        await using var seeder = await StartSeederAsync(torrent);
        var port = await seeder.ListenOnLoopbackAsync();
        var store = new MemoryStore();
        await using var supervisor = LoopbackSupervisor();
        var engine = new TorrentDownloadEngine(supervisor, store, Log);
        var sink = new Sink(); // keeps the name, as if the file appeared after it was chosen
        engine.SetSink(sink);
        var job = Job("clash", magnet: $"magnet:?xt=urn:btih:{hash}");
        store.Metadata[job.Id] = torrent;
        Directory.CreateDirectory(job.DestinationDirectory);
        var mine = Path.Combine(job.DestinationDirectory, "clash.bin");
        await File.WriteAllTextAsync(mine, "the user's own file");

        var run = engine.ExecuteAsync(job, null, null, CancellationToken.None);
        await AddPeerAsync(supervisor, job, port, run);
        var result = await run.WaitAsync(Long);

        Assert.Equal(DownloadState.Failed, result.FinalState);
        Assert.True(result.Failure!.IsRetryable);
        Assert.Contains("already there", result.Failure.Message);
        Assert.Equal("the user's own file", await File.ReadAllTextAsync(mine));
        Assert.True(File.Exists(mine + job.Torrent!.PartSuffix)); // the download stays under its part name
        Assert.False(sink.Finalized.ContainsKey(job.Id));
    }

    [WorkerFact]
    public async Task AFinalizedTorrentAddedAgainFindsItsFilesUnderTheirOwnNames()
    {
        var (payload, torrent) = Payload("again.bin", 1024 * 1024);
        var hash = TestTorrent.LastInfoHash; // thread-static: read before the first await
        await using var seeder = await StartSeederAsync(torrent);
        var port = await seeder.ListenOnLoopbackAsync();
        var store = new MemoryStore();
        await using var supervisor = LoopbackSupervisor();
        var engine = new TorrentDownloadEngine(supervisor, store, Log);
        var sink = new Sink();
        engine.SetSink(sink);
        var job = Job("again", magnet: $"magnet:?xt=urn:btih:{hash}");
        store.Metadata[job.Id] = torrent;
        var run = engine.ExecuteAsync(job, null, null, CancellationToken.None);
        await AddPeerAsync(supervisor, job, port, run);
        Assert.True((await run.WaitAsync(Long)).Success);

        // Without its resume data (lost, or saved before the renames) and with no peer added, only the finalized flag lets the
        // worker find the finished file: part names would leave it nothing to check.
        store.Resume.TryRemove(job.Id, out _);
        job.Torrent!.Finalized = true;
        var again = await engine.ExecuteAsync(job, null, null, CancellationToken.None).WaitAsync(Long);
        Assert.True(again.Success, again.Failure?.Message);
        var final = Path.Combine(job.DestinationDirectory, "again.bin");
        Assert.Equal(payload, await File.ReadAllBytesAsync(final));
        Assert.False(File.Exists(final + job.Torrent.PartSuffix));
    }

    [Fact]
    public async Task ARunOfATorrentWhoseEarlierJobIsStillLeavingTheWorkerWaitsForIt()
    {
        FakeTorrentEngine? fake = null;
        await using var supervisor = new TorrentWorkerSupervisor(() => fake = new FakeTorrentEngine());
        var engine = new TorrentDownloadEngine(supervisor, new MemoryStore(), Log);
        var hash = new string('a', 40);
        var old = Job("old");
        old.Torrent!.InfoHash = hash;
        var again = Job("again");
        again.Torrent!.InfoHash = hash.ToUpperInvariant();
        using var stopOld = new CancellationTokenSource();

        var oldRun = engine.ExecuteAsync(old, null, null, stopOld.Token);
        await WaitForAsync(() => fake?.Torrents.Count == 1 ? fake : null, "the first torrent added");
        var newRun = engine.ExecuteAsync(again, null, null, CancellationToken.None);
        await Task.Delay(200);
        // The worker holds one torrent per info hash: the new job is not added while the old one is there.
        Assert.Equal([old.Id.ToString("N")], fake!.Torrents);
        Assert.False(newRun.IsCompleted);

        stopOld.Cancel();
        Assert.Equal(DownloadState.Cancelled, (await oldRun.WaitAsync(Long)).FinalState);
        await WaitForAsync(() => fake.Torrents.Contains(again.Id.ToString("N")) ? fake : null, "the new torrent added");
        for (var i = 0; i < 4; i++) fake.Advance();
        var result = await newRun.WaitAsync(Long);
        Assert.True(result.Success, result.Failure?.Message);
    }

    [Fact]
    public async Task AnAddTheWorkerRefusesFailsTheJobVisibly()
    {
        await using var supervisor = new TorrentWorkerSupervisor(() => new RefusingEngine("could not add: duplicate torrent"));
        var engine = new TorrentDownloadEngine(supervisor, new MemoryStore(), Log);
        var result = await engine.ExecuteAsync(Job("refused"), null, null, CancellationToken.None).WaitAsync(Long);
        Assert.Equal(DownloadState.Failed, result.FinalState);
        Assert.True(result.Failure!.IsRetryable);
        Assert.Contains("duplicate torrent", result.Failure.Message);

        await using var supervisor2 = new TorrentWorkerSupervisor(() => new RefusingEngine("magnet not usable: invalid"));
        var engine2 = new TorrentDownloadEngine(supervisor2, new MemoryStore(), Log);
        var bad = await engine2.ExecuteAsync(Job("bad"), null, null, CancellationToken.None).WaitAsync(Long);
        Assert.Equal(DownloadState.Failed, bad.FinalState);
        Assert.False(bad.Failure!.IsRetryable);
        Assert.Contains("magnet not usable", bad.Failure.Message);
    }

    [Fact]
    public async Task AFileListTheWorkerRefusesFailsTheJobInsteadOfWaitingForever()
    {
        FakeTorrentEngine? fake = null;
        await using var supervisor = new TorrentWorkerSupervisor(() => fake = new FakeTorrentEngine());
        var engine = new TorrentDownloadEngine(supervisor, new MemoryStore(), Log);
        var job = Job("unsafe");
        var run = engine.ExecuteAsync(job, null, null, CancellationToken.None);
        await WaitForAsync(() => fake?.Torrents.Count == 1 ? fake : null, "the torrent added");
        fake!.Inject(new JsonObject { ["event"] = "error", ["id"] = job.Id.ToString("N"), ["kind"] = "metadata", ["message"] = "unsafe file path in the torrent" });
        var result = await run.WaitAsync(Long);
        Assert.Equal(DownloadState.Failed, result.FinalState);
        Assert.Contains("unsafe file path", result.Failure!.Message);
    }

    [Fact]
    public async Task AFullDiskPausesTheTorrentWithThatReasonAndLeavesTheWorker()
    {
        var store = new MemoryStore();
        FakeTorrentEngine? fake = null;
        await using var supervisor = new TorrentWorkerSupervisor(() => fake = new FakeTorrentEngine());
        var engine = new TorrentDownloadEngine(supervisor, store, Log);
        var job = Job("full");
        var run = engine.ExecuteAsync(job, null, null, CancellationToken.None);
        await WaitForAsync(() => fake?.Torrents.Count == 1 ? fake : null, "the torrent added");
        fake!.Inject(new JsonObject
        {
            ["event"] = "error", ["id"] = job.Id.ToString("N"), ["kind"] = "file", ["reason"] = "diskFull",
            ["message"] = "Il n'y a pas assez d'espace sur le disque.",
        });
        var result = await run.WaitAsync(Long);

        Assert.Equal(DownloadState.Paused, result.FinalState);
        Assert.Equal(FailureKind.DiskFull, result.Failure!.Kind);
        Assert.Equal("The disk is full", result.Failure.Message);
        Assert.Empty(fake.Torrents);
        Assert.NotNull(await store.LoadResumeAsync(job.Id, CancellationToken.None)); // Resume adds it back from here
    }

    [Fact]
    public async Task AMagnetWhoseFilesDoNotFitPausesWhenItsFileListComes()
    {
        var store = new MemoryStore();
        FakeTorrentEngine? fake = null;
        await using var supervisor = new TorrentWorkerSupervisor(() => fake = new FakeTorrentEngine());
        var engine = new TorrentDownloadEngine(supervisor, store, Log);
        var sink = new Sink { Room = (_, needed) => $"needs {needed}" };
        engine.SetSink(sink);
        var job = Job("big");

        var result = await engine.ExecuteAsync(job, null, null, CancellationToken.None).WaitAsync(Long);

        Assert.Equal(DownloadState.Paused, result.FinalState);
        Assert.Equal(FailureKind.DiskFull, result.Failure!.Kind);
        Assert.Equal($"needs {FakeTorrentEngine.FakeFileSize}", result.Failure.Message);
        Assert.Empty(fake!.Torrents);
        Assert.NotNull(await store.LoadResumeAsync(job.Id, CancellationToken.None)); // Resume adds it back from here
    }

    [Fact]
    public async Task OnlyTheFirstFileListIsMeasuredAndWhatIsOnDiskIsNotCountedAgain()
    {
        FakeTorrentEngine? fake = null;
        await using var supervisor = new TorrentWorkerSupervisor(() => fake = new FakeTorrentEngine());
        var engine = new TorrentDownloadEngine(supervisor, new MemoryStore(), Log);
        var sink = new Sink();
        engine.SetSink(sink);
        using var stop = new CancellationTokenSource();

        // Half the file is already there under its part name: only the rest is asked for.
        var job = Job("half");
        var part = TorrentLayout.DiskPathOf(job.DestinationDirectory, new TorrentFile(0, job.Id.ToString("N") + ".bin", 1, true), job.Torrent!.PartSuffix);
        Directory.CreateDirectory(job.DestinationDirectory);
        await File.WriteAllBytesAsync(part, new byte[FakeTorrentEngine.FakeFileSize / 2]);
        var run = engine.ExecuteAsync(job, null, null, stop.Token);
        await WaitForAsync(() => sink.RoomAsked.IsEmpty ? null : sink, "the room asked about");
        Assert.Equal((job.Id, FakeTorrentEngine.FakeFileSize / 2), Assert.Single(sink.RoomAsked));

        // A job whose file list came before (it was resumed after a pause for room) is not asked again.
        var known = Job("known");
        known.Torrent!.HasMetadata = true;
        var again = engine.ExecuteAsync(known, null, null, stop.Token);
        await WaitForAsync(() => fake?.Torrents.Count == 2 ? fake : null, "the second torrent added");
        await Task.Delay(200); // its file list has come by now (the fake sends it with the add)
        await stop.CancelAsync();
        await Task.WhenAll(run, again).WaitAsync(Long);
        Assert.DoesNotContain(sink.RoomAsked, asked => asked.JobId == known.Id);
    }

    [Fact]
    public async Task APermissionErrorFailsTheTorrentWithoutRetryingIt()
    {
        FakeTorrentEngine? fake = null;
        await using var supervisor = new TorrentWorkerSupervisor(() => fake = new FakeTorrentEngine());
        var engine = new TorrentDownloadEngine(supervisor, new MemoryStore(), Log);
        var job = Job("denied");
        var run = engine.ExecuteAsync(job, null, null, CancellationToken.None);
        await WaitForAsync(() => fake?.Torrents.Count == 1 ? fake : null, "the torrent added");
        // An older worker sends no reason: its English message says the same.
        fake!.Inject(new JsonObject { ["event"] = "error", ["id"] = job.Id.ToString("N"), ["kind"] = "file", ["message"] = "Access is denied." });
        var result = await run.WaitAsync(Long);

        Assert.Equal(DownloadState.Failed, result.FinalState);
        Assert.Equal(FailureKind.Permission, result.Failure!.Kind);
        Assert.False(result.Failure.IsRetryable);
        Assert.Contains("permission denied", result.Failure.Message);
    }

    [Fact]
    public async Task AListenErrorDoesNotStopTheTorrent()
    {
        FakeTorrentEngine? fake = null;
        await using var supervisor = new TorrentWorkerSupervisor(() => fake = new FakeTorrentEngine());
        var engine = new TorrentDownloadEngine(supervisor, new MemoryStore(), Log);
        var job = Job("listen");
        var run = engine.ExecuteAsync(job, null, null, CancellationToken.None);
        await WaitForAsync(() => fake?.Torrents.Count == 1 ? fake : null, "the torrent added");
        fake!.Inject(new JsonObject { ["event"] = "error", ["kind"] = "listen", ["message"] = "address in use" });
        for (var i = 0; i < 4; i++) fake.Advance();
        var result = await run.WaitAsync(Long);
        Assert.True(result.Success, result.Failure?.Message);
    }

    [Fact]
    public async Task AMultiFileTorrentIsNamedAfterItselfAndItsPathsUseSlashes()
    {
        FakeTorrentEngine? fake = null;
        await using var supervisor = new TorrentWorkerSupervisor(() => fake = new FakeTorrentEngine { FilesPerTorrent = 3 });
        var store = new MemoryStore();
        var engine = new TorrentDownloadEngine(supervisor, store, Log);
        var sink = new Sink();
        engine.SetSink(sink);
        var job = Job("pack");
        var run = engine.ExecuteAsync(job, null, null, CancellationToken.None);
        await WaitForAsync(() => fake?.Torrents.Count == 1 ? fake : null, "the torrent added");
        for (var i = 0; i < 4; i++) fake!.Advance();
        var result = await run.WaitAsync(Long);

        Assert.True(result.Success, result.Failure?.Message);
        var name = job.Id.ToString("N"); // the in-memory worker names the torrent after its id and joins paths with '\'
        Assert.Equal(Path.Combine(job.DestinationDirectory, name), result.FinalFilePath);
        Assert.All(store.Files[job.Id], file => Assert.Equal($"{name}/file-{file.Index}.bin", file.Path));
        Assert.Equal(Path.Combine(job.DestinationDirectory, name), TorrentLayout.FinalPathOf(job.DestinationDirectory, store.Files[job.Id], "x"));
    }

    [Fact]
    public async Task ASingleFileTorrentHeldForAChoiceStartsWithoutOne()
    {
        FakeTorrentEngine? fake = null;
        await using var supervisor = new TorrentWorkerSupervisor(() => fake = new FakeTorrentEngine());
        var store = new MemoryStore();
        var engine = new TorrentDownloadEngine(supervisor, store, Log);
        var sink = new Sink();
        engine.SetSink(sink);
        var job = Job("one");
        job.Torrent!.ChooseFilesWhenReady = true;
        var run = engine.ExecuteAsync(job, null, null, CancellationToken.None);
        await WaitForAsync(() => sink.Metadata.ContainsKey(job.Id) ? fake : null, "the file list");
        Assert.True(fake!.Adds.Single()["hold"]!.GetValue<bool>());
        Assert.False(sink.Metadata[job.Id]); // not waiting for a choice
        for (var i = 0; i < 4; i++) fake.Advance();
        var result = await run.WaitAsync(Long);
        Assert.True(result.Success, result.Failure?.Message);
        Assert.True(Assert.Single(store.Files[job.Id]).Wanted);
    }

    [Fact]
    public async Task ATorrentWithSeveralFilesHeldForAChoiceStillWaits()
    {
        await using var supervisor = new TorrentWorkerSupervisor(() => new FakeTorrentEngine { FilesPerTorrent = 2 });
        var engine = new TorrentDownloadEngine(supervisor, new MemoryStore(), Log);
        var sink = new Sink();
        engine.SetSink(sink);
        var job = Job("two");
        job.Torrent!.ChooseFilesWhenReady = true;
        var result = await engine.ExecuteAsync(job, null, null, CancellationToken.None).WaitAsync(Long);
        Assert.Equal(DownloadState.Paused, result.FinalState);
        Assert.True(sink.Metadata[job.Id]);
    }

    [Fact]
    public async Task AChoiceSavedWithAnImportedMagnetIsUsedWhenTheFileListComes()
    {
        FakeTorrentEngine? fake = null;
        await using var supervisor = new TorrentWorkerSupervisor(() => fake = new FakeTorrentEngine { FilesPerTorrent = 3 });
        var store = new MemoryStore();
        var engine = new TorrentDownloadEngine(supervisor, store, Log);
        var sink = new Sink();
        engine.SetSink(sink);
        var job = Job("saved");
        job.Torrent!.ChooseFilesWhenReady = true;
        job.Torrent.PendingFileIndexes = [0, 2];
        var run = engine.ExecuteAsync(job, null, null, CancellationToken.None);
        await WaitForAsync(() => sink.Metadata.ContainsKey(job.Id) ? fake : null, "the file list");
        Assert.False(sink.Metadata[job.Id]); // not waiting for a choice
        for (var i = 0; i < 4; i++) fake!.Advance();
        var result = await run.WaitAsync(Long);
        Assert.True(result.Success, result.Failure?.Message);
        Assert.Equal([true, false, true], store.Files[job.Id].Select(file => file.Wanted));
    }

    [Fact]
    public async Task ASavedChoiceNamingAFileTheTorrentLacksAsksInstead()
    {
        await using var supervisor = new TorrentWorkerSupervisor(() => new FakeTorrentEngine { FilesPerTorrent = 2 });
        var store = new MemoryStore();
        var engine = new TorrentDownloadEngine(supervisor, store, Log);
        var sink = new Sink();
        engine.SetSink(sink);
        var job = Job("misfit");
        job.Torrent!.ChooseFilesWhenReady = true;
        job.Torrent.PendingFileIndexes = [1, 5];
        var result = await engine.ExecuteAsync(job, null, null, CancellationToken.None).WaitAsync(Long);
        Assert.Equal(DownloadState.Paused, result.FinalState);
        Assert.True(sink.Metadata[job.Id]);
        Assert.All(store.Files[job.Id], file => Assert.True(file.Wanted));
    }

    [Fact]
    public async Task AFinishedTorrentTakesItsHiddenPartsFileAndNothingElse()
    {
        FakeTorrentEngine? fake = null;
        await using var supervisor = new TorrentWorkerSupervisor(() => fake = new FakeTorrentEngine { FilesPerTorrent = 2 });
        var engine = new TorrentDownloadEngine(supervisor, new MemoryStore(), Log);
        var job = Job("parts");
        job.Torrent!.InfoHash = "0123456789abcdef0123456789abcdef01234567";
        Directory.CreateDirectory(job.DestinationDirectory);
        var parts = Path.Combine(job.DestinationDirectory, ".0123456789abcdef0123456789abcdef01234567.parts");
        var other = Path.Combine(job.DestinationDirectory, ".ffffffffffffffffffffffffffffffffffffffff.parts");
        var sub = Path.Combine(job.DestinationDirectory, "sub");
        Directory.CreateDirectory(sub);
        var nested = Path.Combine(sub, ".0123456789abcdef0123456789abcdef01234567.parts");
        foreach (var path in new[] { parts, other, nested }) await File.WriteAllTextAsync(path, "x");

        var run = engine.ExecuteAsync(job, null, null, CancellationToken.None);
        await WaitForAsync(() => fake?.Torrents.Count == 1 ? fake : null, "the torrent added");
        for (var i = 0; i < 4; i++) fake!.Advance();
        Assert.True((await run.WaitAsync(Long)).Success);
        Assert.False(File.Exists(parts));
        Assert.True(File.Exists(other));
        Assert.True(File.Exists(nested));
    }

    [Fact]
    public void ThePartsFileIsNamedOnlyForARealInfoHash()
    {
        Assert.Equal(Path.Combine("d", ".0123456789abcdef0123456789abcdef01234567.parts"),
            TorrentLayout.PartsFileOf("d", "0123456789ABCDEF0123456789ABCDEF01234567"));
        Assert.Null(TorrentLayout.PartsFileOf("d", ""));
        Assert.Null(TorrentLayout.PartsFileOf("d", null));
        Assert.Null(TorrentLayout.PartsFileOf("d", "../../x"));
        Assert.Null(TorrentLayout.PartsFileOf("d", new string('g', 40)));
        Assert.Null(TorrentLayout.PartsFileOf("", new string('a', 40)));
    }

    [Fact]
    public async Task ThePartsFileStaysWhileAnotherJobOfTheHashUsesTheFolder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "inbound-parts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            const string hash = "0123456789abcdef0123456789abcdef01234567";
            var parts = Path.Combine(dir, "." + hash + ".parts");
            await File.WriteAllTextAsync(parts, "x");
            Assert.True(await TorrentLayout.DeletePartsFileAsync(dir, hash, CancellationToken.None, () => false));
            Assert.True(File.Exists(parts));
            Assert.True(await TorrentLayout.DeletePartsFileAsync(dir, hash, CancellationToken.None, () => true));
            Assert.False(File.Exists(parts));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void LayoutReadsBackslashesAsSeparators()
    {
        TorrentFile[] old = [new(0, "Pack\\video\\film.mkv", 10, true), new(1, "Pack\\readme.txt", 1, true)];
        Assert.Equal("Pack", TorrentLayout.TopNameOf(old, "x"));
        Assert.Equal(Path.Combine("d", "Pack"), TorrentLayout.FinalPathOf("d", old, "x"));
        Assert.Equal(["Pack (2)/video/film.mkv", "Pack (2)/readme.txt"], TorrentLayout.WithTopName(old, "Pack (2)").Select(file => file.Path));
        Assert.Equal(Path.Combine("d", "film.mkv"), TorrentLayout.FinalPathOf("d", [new(0, "film.mkv", 1, true)], "x"));
        Assert.Equal(Path.Combine("d", "Solo"), TorrentLayout.FinalPathOf("d", [new(0, "Solo/one.bin", 1, true)], "x"));
    }

    // ---- native worker on loopback, again ----

    [WorkerFact]
    public async Task ATorrentStoppedThenAddedAgainAsANewJobOnTheSameWorkerDownloads()
    {
        var (payload, torrent) = Payload("readd.bin", 16 * 1024 * 1024);
        var hash = TestTorrent.LastInfoHash; // thread-static: read before the first await
        await using var seeder = await StartSeederAsync(torrent);
        var port = await seeder.ListenOnLoopbackAsync();
        await seeder.Process.SendAsync("setTorrentLimits", new JsonObject { ["id"] = "t", ["upload"] = 3_000_000 });
        await using var supervisor = LoopbackSupervisor(TimeSpan.FromSeconds(30));
        var engine = new TorrentDownloadEngine(supervisor, new MemoryStore(), Log);
        engine.SetSink(new Sink());
        var magnet = $"magnet:?xt=urn:btih:{hash}&x.pe=127.0.0.1:{port}";
        var first = Job("readd1", magnet: magnet);
        first.Torrent!.InfoHash = hash!;
        using var stop = new CancellationTokenSource();
        long seen = 0;
        var run = engine.ExecuteAsync(first, null, new Progress<TransferProgress>(p => Interlocked.Exchange(ref seen, p.ReceivedBytes)), stop.Token);
        await WaitForAsync(() => Interlocked.Read(ref seen) > 1_000_000 ? "" : null, "some bytes");
        stop.Cancel();
        Assert.Equal(DownloadState.Cancelled, (await run.WaitAsync(Long)).FinalState);
        Assert.Equal(TorrentWorkerState.Draining, supervisor.State);

        await seeder.Process.SendAsync("setTorrentLimits", new JsonObject { ["id"] = "t", ["upload"] = 0 });
        var second = Job("readd2", magnet: magnet);
        second.Torrent!.InfoHash = hash!;
        var result = await engine.ExecuteAsync(second, null, null, CancellationToken.None).WaitAsync(Long);
        Assert.True(result.Success, result.Failure?.Message);
        Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(second.DestinationDirectory, "readd.bin")));
    }

    // ---- helpers ----

    /// <summary>The in-memory worker, except that every <c>add</c> is refused with <paramref name="reason"/>.</summary>
    private sealed class RefusingEngine(string reason) : ITorrentEngine
    {
        private readonly FakeTorrentEngine _inner = new();

        public System.Threading.Channels.ChannelReader<JsonObject> Events => _inner.Events;

        public Task Exited => _inner.Exited;

        public Task<JsonObject> SendAsync(string command, JsonObject? arguments = null, CancellationToken cancellationToken = default)
            => command == "add" ? throw new TorrentCommandException(command, reason) : _inner.SendAsync(command, arguments, cancellationToken);

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    private TorrentWorkerSupervisor LoopbackSupervisor(TimeSpan? drain = null) => new(
        () => TorrentWorkerProcess.Start(WorkerBinary.Path!, null, null, line => Log("leecher: " + line)),
        new TorrentSupervisorOptions
        {
            DrainDelay = drain ?? TimeSpan.FromSeconds(1),
            ProgressIntervalMs = 200,
            Listen = () => new JsonObject
            {
                ["port"] = 0, ["interfaces"] = new JsonArray("127.0.0.1"), ["dht"] = false, ["lsd"] = false, ["upnp"] = false, ["natpmp"] = false,
            },
        }, Log);

    /// <summary>No tracker or DHT on loopback: the seeder is given by address once the torrent is in the worker.</summary>
    private static async Task AddPeerAsync(TorrentWorkerSupervisor supervisor, DownloadJob job, int port, Task<DownloadResult> run)
    {
        var deadline = DateTime.UtcNow + Long;
        while (true)
        {
            try
            {
                if (supervisor.Engine is { } engine)
                {
                    await engine.SendAsync("addPeer", new JsonObject { ["id"] = job.Id.ToString("N"), ["address"] = "127.0.0.1", ["port"] = port });
                    return;
                }
            }
            catch (TorrentCommandException) when (DateTime.UtcNow < deadline)
            {
                // not added yet
            }

            if (run.IsCompleted) throw new InvalidOperationException($"The run ended before its peer was added: {run.Result.FinalState} {run.Result.Failure?.Message}");
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The torrent never reached the worker.");
            await Task.Delay(50);
        }
    }

    private async Task<Worker> StartSeederAsync(byte[] torrent)
    {
        var seeder = Worker.Start(output, "seeder");
        await seeder.Process.SendAsync("setProgressInterval", new JsonObject { ["ms"] = 200 });
        await seeder.Process.SendAsync("add", new JsonObject
        {
            ["id"] = "t", ["savePath"] = Path.Combine(_root, "seed"), ["torrent"] = Convert.ToBase64String(torrent), ["seed"] = true,
        });
        await seeder.WaitForProgressAsync("t", r => r["state"]!.GetValue<string>() == "seeding", Long, "seeding");
        return seeder;
    }

    private (byte[] Payload, byte[] Torrent) Payload(string name, int size)
    {
        var payload = new byte[size];
        Random.Shared.NextBytes(payload);
        var dir = Path.Combine(_root, "seed");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, name), payload);
        return (payload, TestTorrent.Create(name, payload));
    }

    private DownloadJob Job(string name, string? magnet = null)
    {
        var id = Guid.NewGuid();
        var destination = Path.Combine(_root, "down-" + name);
        return new DownloadJob
        {
            Id = id,
            Url = magnet ?? $"magnet:?xt=urn:btih:{new string('0', 40)}",
            DestinationDirectory = destination,
            Torrent = new TorrentJobInfo { Name = name, Magnet = magnet, PartSuffix = TorrentJobInfo.PartSuffixFor(id) },
        };
    }

    private static async Task<T> WaitForAsync<T>(Func<T?> probe, string what) where T : class
    {
        var deadline = DateTime.UtcNow + Long;
        while (true)
        {
            if (probe() is { } value) return value;
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"No {what} within {Long.TotalSeconds:0} s.");
            await Task.Delay(20);
        }
    }

    private void Log(string line)
    {
        try { output.WriteLine(line); }
        catch (InvalidOperationException) { }
    }

    private sealed class MemoryStore : ITorrentStore
    {
        public ConcurrentDictionary<Guid, byte[]> Metadata { get; } = new();
        public ConcurrentDictionary<Guid, byte[]> Resume { get; } = new();
        public ConcurrentDictionary<Guid, IReadOnlyList<TorrentFile>> Files { get; } = new();

        public Task SaveMetadataAsync(Guid jobId, byte[] torrent, CancellationToken cancellationToken) { Metadata[jobId] = torrent; return Task.CompletedTask; }
        public Task<byte[]?> LoadMetadataAsync(Guid jobId, CancellationToken cancellationToken) => Task.FromResult(Metadata.GetValueOrDefault(jobId));
        public Task SaveResumeAsync(Guid jobId, byte[] resume, CancellationToken cancellationToken) { Resume[jobId] = resume; return Task.CompletedTask; }
        public Task<byte[]?> LoadResumeAsync(Guid jobId, CancellationToken cancellationToken) => Task.FromResult(Resume.GetValueOrDefault(jobId));
        public Task SaveFilesAsync(Guid jobId, IReadOnlyList<TorrentFile> files, CancellationToken cancellationToken) { Files[jobId] = files; return Task.CompletedTask; }
        public Task<IReadOnlyList<TorrentFile>?> LoadFilesAsync(Guid jobId, CancellationToken cancellationToken) => Task.FromResult(Files.GetValueOrDefault(jobId));
        public Task DeleteAsync(Guid jobId, CancellationToken cancellationToken) { Metadata.TryRemove(jobId, out _); Resume.TryRemove(jobId, out _); Files.TryRemove(jobId, out _); return Task.CompletedTask; }
    }

    private sealed class Sink : ITorrentJobSink
    {
        /// <summary>Job → whether it was told to wait for a file choice.</summary>
        public ConcurrentDictionary<Guid, bool> Metadata { get; } = new();

        public Task MetadataArrivedAsync(Guid jobId, string name, IReadOnlyList<TorrentFile> files, bool awaitingFileChoice, CancellationToken cancellationToken)
        {
            Metadata[jobId] = awaitingFileChoice;
            return Task.CompletedTask;
        }

        public void SwarmChanged(Guid jobId, int peers, int seeds, double uploadRate)
        {
        }

        /// <summary>Names the finished torrent gets: the given one unless the test set another.</summary>
        public Func<string, string> TopName { get; set; } = name => name;

        /// <summary>Job → the root name and files it was finalized with.</summary>
        public ConcurrentDictionary<Guid, (string? Root, IReadOnlyList<TorrentFile> Files)> Finalized { get; } = new();

        public string ChooseFinalTopName(Guid jobId, string destination, string topName, IReadOnlyList<TorrentFile> files) => TopName(topName);

        public Task FinalizedAsync(Guid jobId, string? rootName, IReadOnlyList<TorrentFile> files, CancellationToken cancellationToken)
        {
            Finalized[jobId] = (rootName, files);
            return Task.CompletedTask;
        }

        /// <summary>Every FinalizingAsync call in order: (job, root name, set or cleared).</summary>
        public ConcurrentQueue<(Guid JobId, string? Root, bool Finalizing)> FinalizingCalls { get; } = new();

        /// <summary>Called inside FinalizingAsync, before it returns.</summary>
        public Action? OnFinalizing { get; set; }

        /// <summary>L4: the room problem to give for (job, needed bytes); null by default.</summary>
        public Func<Guid, long, string?>? Room { get; set; }

        /// <summary>Every RoomProblemAsync call: (job, needed bytes).</summary>
        public ConcurrentQueue<(Guid JobId, long Needed)> RoomAsked { get; } = new();

        public Task<string?> RoomProblemAsync(Guid jobId, string destination, long neededBytes, CancellationToken cancellationToken)
        {
            RoomAsked.Enqueue((jobId, neededBytes));
            return Task.FromResult(Room?.Invoke(jobId, neededBytes));
        }

        public Task FinalizingAsync(Guid jobId, string? rootName, bool finalizing, CancellationToken cancellationToken)
        {
            FinalizingCalls.Enqueue((jobId, rootName, finalizing));
            OnFinalizing?.Invoke();
            return Task.CompletedTask;
        }

        /// <summary>Job → the last uploaded total it reported while seeding.</summary>
        public ConcurrentDictionary<Guid, long> Uploaded { get; } = new();

        /// <summary>Job → how its seeding ended.</summary>
        public ConcurrentDictionary<Guid, (TorrentSeedingEnd Reason, string Message)> SeedingEnded { get; } = new();

        /// <summary>Thrown from <see cref="SeedingProgress"/> when set, as a faulty JobChanged subscriber behind the real sink would.</summary>
        public Exception? SeedingProgressFault { get; set; }

        public void SeedingProgress(Guid jobId, long uploaded, double uploadRate, int peers, int seeds)
        {
            Uploaded[jobId] = uploaded;
            if (SeedingProgressFault is { } fault) throw fault;
        }

        public Task SeedingStoppedAsync(Guid jobId, TorrentSeedingEnd reason, string message)
        {
            SeedingEnded[jobId] = (reason, message);
            return Task.CompletedTask;
        }
    }
}
