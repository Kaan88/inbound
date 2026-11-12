using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using DownloadManager.Core;

namespace DownloadManager.Torrents;

/// <summary>
/// Runs a torrent job on the shared worker (docs/plans/torrent-t1-design.md, section 3). A run attaches the job, reports
/// progress, and ends when the torrent is complete and its files carry their own names again, when it must wait for the
/// user's choice of files (the job is then paused with <see cref="TorrentJobInfo.AwaitingFileChoice"/>), or when it is
/// cancelled (paused gracefully, its last resume data saved, and removed from the worker).
/// </summary>
public sealed partial class TorrentDownloadEngine : ITorrentDownloadEngine, IAsyncDisposable
{
    private static readonly TimeSpan BlobWait = TimeSpan.FromSeconds(5);
    /// <summary>After a resume blob, how long a later one (requested after it) is still waited for.</summary>
    private static readonly TimeSpan LaterBlobWait = TimeSpan.FromMilliseconds(300);

    private readonly TorrentWorkerSupervisor _supervisor;
    private readonly ITorrentStore _store;
    private readonly Action<string>? _log;
    private readonly ConcurrentDictionary<Guid, TorrentSession> _running = new();
    private readonly ConcurrentDictionary<Guid, byte> _finishing = new();
    private readonly ConcurrentDictionary<string, HashRun> _hashRuns = new(StringComparer.Ordinal);

    /// <summary>How long a run waits for an earlier run of the same torrent to leave the worker.</summary>
    internal static TimeSpan SameTorrentWait { get; set; } = TimeSpan.FromSeconds(30);
    private ITorrentJobSink? _sink;

    public TorrentDownloadEngine(TorrentWorkerSupervisor supervisor, ITorrentStore store, Action<string>? log = null,
        TimeProvider? time = null)
    {
        _supervisor = supervisor;
        _store = store;
        _log = log;
        _time = time ?? TimeProvider.System;
        _supervisor.StateChanged += _ => WorkerRunningChanged?.Invoke();
    }

    public bool WorkerRunning => _supervisor.State != TorrentWorkerState.Off;

    public event Action? WorkerRunningChanged;

    public void SetSink(ITorrentJobSink sink) => _sink = sink;

    /// <summary>The supervisor, for tests and the Host's shutdown.</summary>
    public TorrentWorkerSupervisor Supervisor => _supervisor;

    /// <summary>Closes the worker (it saves its resume data first). Call after the service has stopped its jobs. Seeding
    /// torrents are taken off the worker with their resume data saved, and stay marked seeding for the next start.</summary>
    public async ValueTask DisposeAsync()
    {
        _disposing = true;
        await StopAllSeedingQuietlyAsync().ConfigureAwait(false);
        await _supervisor.DisposeAsync().ConfigureAwait(false);
    }

    public void UpdateProxy(ProxySettings settings)
    {
        // T3: torrents take the proxy through UpdatePeerSettings, and only when Options › Torrents says so.
    }

    public void UpdateSiteLogins(IReadOnlyList<SiteLoginCredential> logins)
    {
    }

    public void UpdateFfmpegPath(string? path)
    {
    }

    public void UpdateSharedSpeedLimit(long? bytesPerSecond) => _ = _supervisor.SetDownloadLimitAsync(bytesPerSecond);

    public void UpdateListen(int port, bool upnp) => _ = _supervisor.SetListenAsync(port, upnp);

    public TorrentNetworkStatus Network => _supervisor.Network;

    public bool IsFinishing(Guid jobId) => _finishing.ContainsKey(jobId);

    public async Task<TorrentFileChoiceOutcome> ApplyFileChoiceAsync(Guid jobId, IReadOnlyList<TorrentFile> files, CancellationToken cancellationToken)
    {
        if (_finishing.ContainsKey(jobId)) return TorrentFileChoiceOutcome.Finishing;
        if (!_running.TryGetValue(jobId, out var session)) return TorrentFileChoiceOutcome.NotRunning;
        var priorities = new JsonArray(TorrentLayout.PrioritiesOf(files).Select(p => (JsonNode)p).ToArray());
        try
        {
            await session.SendAsync("setFilePriorities", new JsonObject { ["priorities"] = priorities }, cancellationToken).ConfigureAwait(false);
            return TorrentFileChoiceOutcome.Applied;
        }
        catch (Exception e) when (e is TorrentEngineExitedException or TorrentCommandException)
        {
            // The stored choice is sent with the next add.
            _log?.Invoke($"Torrent {jobId:N}: the file choice was not sent: {e.Message}");
            return TorrentFileChoiceOutcome.NotRunning;
        }
    }

    /// <summary>Set when the worker answered <c>files</c> with "unknown command" (a worker older than the command).</summary>
    private volatile bool _workerLacksFiles;

    public async Task<IReadOnlyList<long>?> GetFileProgressAsync(Guid jobId, CancellationToken cancellationToken)
    {
        if (_workerLacksFiles || !_running.TryGetValue(jobId, out var session)) return null;
        try
        {
            var reply = await session.SendAsync("files", null, cancellationToken).ConfigureAwait(false);
            return ReadFileProgress(reply);
        }
        catch (TorrentCommandException e) when (e.Reason == "unknown command")
        {
            _workerLacksFiles = true;
            _log?.Invoke("The torrent worker can't report each file's progress (an older worker).");
            return null;
        }
        catch (Exception e) when (e is TorrentEngineExitedException or TorrentCommandException)
        {
            return null;
        }
    }

    /// <summary>The file list with a saved choice applied; null when there is none or a file it names is not in the list.</summary>
    internal static IReadOnlyList<TorrentFile>? ChooseSaved(IReadOnlyList<TorrentFile> files, IReadOnlyCollection<int>? indexes)
    {
        if (indexes is not { Count: > 0 }) return null;
        var chosen = indexes.ToHashSet();
        if (!chosen.IsSubsetOf(files.Select(file => file.Index))) return null;
        return files.Select(file => file with { Wanted = chosen.Contains(file.Index) }).ToList();
    }

    /// <summary>The <c>done</c> array of a <c>files</c> reply, by file index; null when it is missing or malformed.</summary>
    internal static IReadOnlyList<long>? ReadFileProgress(JsonObject reply)
    {
        if (reply["done"] is not JsonArray done) return null;
        var bytes = new long[done.Count];
        for (var index = 0; index < done.Count; index++)
        {
            if (done[index] is not JsonValue value || !value.TryGetValue<long>(out var number) || number < 0) return null;
            bytes[index] = number;
        }

        return bytes;
    }

    public async Task<DownloadResult> ExecuteAsync(DownloadJob job, DownloadCheckpoint? checkpoint,
        IProgress<TransferProgress>? progress, CancellationToken cancellationToken)
    {
        var info = job.Torrent ?? throw new InvalidOperationException("The job is not a torrent.");
        if (info.AwaitingFileChoice) return Held(job);
        // A download run of a torrent that is seeding (this job downloading again, or another job of the same torrent)
        // takes it off the worker first.
        await StopSeedingOfAsync(job.Id, info.InfoHash).ConfigureAwait(false);

        // One run per info hash at a time: the worker holds a torrent once, so a new job of a torrent whose earlier job is
        // still leaving the worker (deleted, then added again at once) waits for it instead of being refused.
        var hashKey = string.IsNullOrEmpty(info.InfoHash) ? null : info.InfoHash.ToLowerInvariant();
        HashRun? claim = null;
        if (hashKey is not null)
        {
            var deadline = DateTime.UtcNow + SameTorrentWait;
            while (claim is null)
            {
                var mine = new HashRun(job.Id);
                if (_hashRuns.TryAdd(hashKey, mine))
                {
                    claim = mine;
                    break;
                }

                if (!_hashRuns.TryGetValue(hashKey, out var other)) continue;
                var left = deadline - DateTime.UtcNow;
                try
                {
                    if (left <= TimeSpan.Zero) throw new TimeoutException();
                    await other.Ended.Task.WaitAsync(left, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return Cancelled(job);
                }
                catch (TimeoutException)
                {
                    return Failed(job, FailureKind.Unknown,
                        "Another download of this torrent is still stopping in the torrent engine. Try again in a moment.", retryable: true);
                }
            }
        }

        try
        {
            return await ExecuteCoreAsync(job, info, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (claim is not null)
            {
                _hashRuns.TryRemove(new KeyValuePair<string, HashRun>(hashKey!, claim));
                claim.Ended.TrySetResult();
            }
        }
    }

    private sealed class HashRun(Guid jobId)
    {
        public Guid JobId { get; } = jobId;
        public TaskCompletionSource Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private async Task<DownloadResult> ExecuteCoreAsync(DownloadJob job, TorrentJobInfo info,
        IProgress<TransferProgress>? progress, CancellationToken cancellationToken)
    {
        var jobId = job.Id;
        var destination = job.DestinationDirectory;
        var partSuffix = string.IsNullOrEmpty(info.PartSuffix) ? TorrentJobInfo.PartSuffixFor(jobId) : info.PartSuffix;
        var rootName = info.RootName;
        var magnet = info.Magnet ?? job.Url;
        var hold = info.ChooseFilesWhenReady && !info.HasMetadata;
        var finalized = info.Finalized;
        // T3: a file check asked for while the torrent was off the worker goes with its first add only.
        var recheck = info.RecheckOnStart;

        IReadOnlyList<TorrentFile>? files = await _store.LoadFilesAsync(jobId, cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(destination);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Failed(job, FailureKind.DestinationUnavailable, $"The folder can't be used: {e.Message}", retryable: true);
        }

        if (!finalized && info.Finalizing && files is { Count: > 0 } list)
        {
            // M16: the last run was stopped while (or just before) the worker renamed the finished files.
            switch (FinishInterruptedFinalize(destination, list, info.FinalizingRootName, partSuffix))
            {
                case { Problem: { } problem }:
                    return Failed(job, FailureKind.Permission, $"The finished files could not be given their names: {problem}", retryable: true);
                case { Files: { } renamed }:
                    // Added under their own names and checked in full, never downloaded again.
                    rootName = info.FinalizingRootName ?? rootName;
                    files = renamed;
                    finalized = true;
                    recheck = true;
                    if (_sink is { } sink) await sink.FinalizedAsync(jobId, rootName, renamed, CancellationToken.None).ConfigureAwait(false);
                    else await _store.SaveFilesAsync(jobId, renamed, CancellationToken.None).ConfigureAwait(false);
                    break;
                default:
                    // Nothing was renamed yet, or a name is taken: the files keep their part names and finish as usual.
                    if (_sink is { } clearing) await clearing.FinalizingAsync(jobId, null, false, CancellationToken.None).ConfigureAwait(false);
                    break;
            }
        }

        async Task<JsonObject> AddArgumentsAsync(CancellationToken token)
        {
            var arguments = new JsonObject
            {
                ["savePath"] = destination,
                ["partSuffix"] = partSuffix,
            };
            if (!string.IsNullOrEmpty(rootName)) arguments["rootName"] = rootName;
            var resume = await _store.LoadResumeAsync(jobId, token).ConfigureAwait(false);
            byte[]? metadata = resume is null ? await _store.LoadMetadataAsync(jobId, token).ConfigureAwait(false) : null;
            if (resume is not null) arguments["resume"] = Convert.ToBase64String(resume);
            else if (metadata is not null) arguments["torrent"] = Convert.ToBase64String(metadata);
            else arguments["magnet"] = magnet;
            // The latest choice, which a restart must keep too.
            var chosen = await _store.LoadFilesAsync(jobId, token).ConfigureAwait(false) ?? files;
            if (chosen is { Count: > 0 })
                arguments["filePriorities"] = new JsonArray(TorrentLayout.PrioritiesOf(chosen).Select(p => (JsonNode)p).ToArray());
            // Resume data saved before the metadata came still needs the hold.
            if (hold) arguments["hold"] = true;
            if (finalized) arguments["finalized"] = true;
            AddOptions(arguments, info);
            if (recheck) arguments["recheck"] = true;
            return arguments;
        }

        TorrentSession session;
        try
        {
            session = await _supervisor.AttachAsync(jobId.ToString("N"), AddArgumentsAsync, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Cancelled(job);
        }
        catch (TorrentCommandException e) when (e.Reason.StartsWith("could not add", StringComparison.Ordinal))
        {
            // libtorrent refused the add itself (a torrent it still holds, for one): worth trying again, and said so.
            _log?.Invoke($"Torrent {jobId:N}: the worker refused the add: {e.Reason}");
            return Failed(job, FailureKind.Unknown, $"The torrent engine did not take this torrent: {e.Reason}", retryable: true);
        }
        catch (TorrentCommandException e)
        {
            return Failed(job, FailureKind.InvalidContent, $"The torrent can't be used: {e.Reason}", retryable: false);
        }
        catch (Exception e) when (e is TorrentEngineExitedException or InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return Failed(job, FailureKind.Unknown, $"The torrent engine could not start: {e.Message}", retryable: true);
        }

        if (recheck)
        {
            // Asked once per start. The job's flag is cleared only when the worker says the check is queued; an older
            // worker ignores the field, and the flag then stays for a worker that can.
            recheck = false;
            if (session.RecheckQueued) _sink?.RecheckStarted(jobId);
            else _log?.Invoke($"Torrent {jobId:N}: the worker did not queue the file check asked for with the add.");
        }

        _running[jobId] = session;
        try
        {
            return await RunAsync(job, session, destination, files, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _running.TryRemove(new KeyValuePair<Guid, TorrentSession>(jobId, session));
            _states.TryRemove(jobId, out _);
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<DownloadResult> RunAsync(DownloadJob job, TorrentSession session, string destination,
        IReadOnlyList<TorrentFile>? files, IProgress<TransferProgress>? progress, CancellationToken cancellationToken)
    {
        var jobId = job.Id;
        long done = job.ReceivedBytes, wanted = job.Torrent!.WantedBytes;
        var finishing = false;
        try
        {
            while (true)
            {
                JsonObject message;
                try
                {
                    if (!await session.Events.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                        return Failed(job, FailureKind.Network, "The torrent engine stopped.", retryable: true, done, wanted);
                    if (!session.Events.TryRead(out message!)) continue;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    await PauseAndRemoveAsync(session, jobId).ConfigureAwait(false);
                    return Cancelled(job, done, wanted);
                }

                switch (message["event"]?.GetValue<string>())
                {
                    case "metadata":
                    {
                        var held = message["held"]?.GetValue<bool>() == true;
                        // L4: the first file list of this job (a magnet's size was unknown when it was added).
                        var firstList = !job.Torrent.HasMetadata;
                        // The stored choice is the latest one (a choice made while the run started is kept).
                        files = MergeFiles(message, await _store.LoadFilesAsync(jobId, CancellationToken.None).ConfigureAwait(false) ?? files);
                        // One file leaves nothing to choose: it is wanted. A choice saved with an imported magnet is used when
                        // every file it names is in the list. Either lifts the worker's hold.
                        var pending = job.Torrent.PendingFileIndexes;
                        IReadOnlyList<TorrentFile>? choice = !held ? null
                            : files.Count == 1 ? [files[0] with { Wanted = true }]
                            : ChooseSaved(files, pending);
                        if (choice is not null)
                        {
                            try
                            {
                                var priorities = new JsonArray(TorrentLayout.PrioritiesOf(choice).Select(p => (JsonNode)p).ToArray());
                                await session.SendAsync("setFilePriorities", new JsonObject { ["priorities"] = priorities }).ConfigureAwait(false);
                                files = choice;
                                held = false;
                            }
                            catch (TorrentCommandException e)
                            {
                                _log?.Invoke($"Torrent {jobId:N}: its files could not be started, so it waits for a choice: {e.Reason}");
                            }
                        }

                        await _store.SaveFilesAsync(jobId, files, CancellationToken.None).ConfigureAwait(false);
                        wanted = files.Where(file => file.Wanted).Sum(file => file.Size);
                        var name = message["name"]?.GetValue<string>() ?? job.Torrent.Name;
                        if (_sink is { } sink)
                            await sink.MetadataArrivedAsync(jobId, name, files, held, CancellationToken.None).ConfigureAwait(false);
                        if (held)
                        {
                            // Keep the metadata (it rides in the resume data), then leave the worker until the user chooses.
                            await WaitForBlobAsync(session, jobId).ConfigureAwait(false);
                            await RemoveAsync(session).ConfigureAwait(false);
                            return Held(job);
                        }

                        // L4: what doesn't fit stops now, not when the disk is full. Resume goes on regardless (the check is once).
                        if (firstList && _sink is { } roomSink
                            && NeededBytes(destination, files, string.IsNullOrEmpty(job.Torrent.PartSuffix) ? TorrentJobInfo.PartSuffixFor(jobId) : job.Torrent.PartSuffix) is > 0 and var needed
                            && await roomSink.RoomProblemAsync(jobId, destination, needed, CancellationToken.None).ConfigureAwait(false) is { } noRoom)
                        {
                            await PauseAndRemoveAsync(session, jobId).ConfigureAwait(false);
                            return DownloadResult.Fail(DownloadState.Paused,
                                new JobFailure(FailureKind.DiskFull, noRoom, false, true, DateTimeOffset.UtcNow), done, wanted);
                        }

                        break;
                    }
                    case "progress":
                    {
                        NoteState(jobId, message);
                        done = message["done"]?.GetValue<long>() ?? done;
                        wanted = message["wanted"]?.GetValue<long>() ?? wanted;
                        var rate = message["downRate"]?.GetValue<double>() ?? 0;
                        var peers = message["peers"]?.GetValue<int>() ?? 0;
                        var seeds = message["seeds"]?.GetValue<int>() ?? 0;
                        var state = message["state"]?.GetValue<string>();
                        _sink?.SwarmChanged(jobId, peers, seeds, message["upRate"]?.GetValue<double>() ?? 0);
                        TimeSpan? eta = rate > 0 && wanted > done ? TimeSpan.FromSeconds((wanted - done) / rate) : null;
                        progress?.Report(new TransferProgress(done, wanted > 0 ? wanted : null, rate, eta, peers, PhaseOf(state)));
                        // A torrent that was already complete when it was added may never say "completed".
                        if (!finishing && state == "seeding" && wanted > 0 && done >= wanted)
                        {
                            finishing = true;
                            return await FinishAsync(job, session, destination, files, wanted).ConfigureAwait(false);
                        }

                        break;
                    }
                    case "completed":
                        if (finishing) break;
                        finishing = true;
                        return await FinishAsync(job, session, destination, files, wanted).ConfigureAwait(false);
                    case "resumeBlob":
                        await SaveBlobAsync(jobId, message).ConfigureAwait(false);
                        break;
                    case "error":
                    {
                        var kind = message["kind"]?.GetValue<string>();
                        var text = message["message"]?.GetValue<string>() ?? "unknown error";
                        _log?.Invoke($"Torrent {jobId:N}: {kind}: {text}");
                        if (TorrentErrors.Classify(kind, message["reason"]?.GetValue<string>(), text) is not { } meaning) break;
                        // The worker refused the file list it fetched and dropped the torrent: nothing more will come.
                        if (kind == "metadata") await RemoveAsync(session).ConfigureAwait(false);
                        else await PauseAndRemoveAsync(session, jobId).ConfigureAwait(false);
                        // A full disk pauses the torrent; Resume adds it back from its resume data once there is room.
                        if (meaning.PausesJob)
                            return DownloadResult.Fail(DownloadState.Paused,
                                new JobFailure(meaning.Kind, meaning.Message, false, true, DateTimeOffset.UtcNow), done, wanted);
                        return Failed(job, meaning.Kind, meaning.Message, meaning.Retryable, done, wanted);
                    }
                    case TorrentWorkerSupervisor.WorkerLostEvent:
                        return Failed(job, FailureKind.Unknown, message["message"]?.GetValue<string>() ?? "The torrent engine stopped.",
                            retryable: true, done, wanted);
                }
            }
        }
        catch (Exception e) when (e is TorrentEngineExitedException)
        {
            return Failed(job, FailureKind.Unknown, "The torrent engine stopped.", retryable: true, done, wanted);
        }
    }

    /// <summary>L4: the wanted files' bytes not yet on disk, under their part names or their own.</summary>
    internal static long NeededBytes(string destination, IReadOnlyList<TorrentFile> files, string partSuffix)
    {
        long needed = 0;
        foreach (var file in files)
        {
            if (!file.Wanted) continue;
            var there = Math.Max(LengthOf(TorrentLayout.DiskPathOf(destination, file, partSuffix)), LengthOf(TorrentLayout.DiskPathOf(destination, file)));
            needed += Math.Max(0, file.Size - there);
        }

        return needed;

        static long LengthOf(string path)
        {
            try
            {
                var info = new FileInfo(path);
                return info.Exists ? info.Length : 0;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return 0;
            }
        }
    }

    /// <summary>What finishing an interrupted finalize came to: the files under their own names, a problem, or neither (leave it).</summary>
    internal readonly record struct InterruptedFinalize(List<TorrentFile>? Files, string? Problem);

    /// <summary>
    /// M16: does by hand what the worker's <c>finalize</c> was doing when the last run was stopped: each wanted file still
    /// under its part name is moved to its own name. Only when the renames had begun (one file is already under its own
    /// name) and no own name is taken by anything else; otherwise nothing is touched and the files keep their part names.
    /// </summary>
    internal static InterruptedFinalize FinishInterruptedFinalize(string destination, IReadOnlyList<TorrentFile> files, string? rootName, string partSuffix)
    {
        var renamed = rootName is null ? files.ToList() : TorrentLayout.WithTopName(files, rootName);
        var moves = new List<(string From, string To)>();
        var begun = false;
        for (var i = 0; i < files.Count; i++)
        {
            if (!files[i].Wanted) continue;
            var part = TorrentLayout.DiskPathOf(destination, files[i], partSuffix);
            var own = TorrentLayout.DiskPathOf(destination, renamed[i]);
            var hasPart = File.Exists(part);
            var hasOwn = File.Exists(own) || Directory.Exists(own);
            if (hasPart && hasOwn) return default; // a name taken by something else: not ours to decide
            if (hasOwn) begun = true;
            else if (hasPart) moves.Add((part, own));
        }

        if (!begun) return default;
        try
        {
            foreach (var (from, to) in moves)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Move(from, to);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new InterruptedFinalize(null, e.Message);
        }

        return new InterruptedFinalize(renamed, null);
    }

    private async Task<DownloadResult> FinishAsync(DownloadJob job, TorrentSession session, string destination,
        IReadOnlyList<TorrentFile>? files, long wanted)
    {
        var jobId = job.Id;
        _finishing[jobId] = 0;
        try
        {
            var list = await _store.LoadFilesAsync(jobId, CancellationToken.None).ConfigureAwait(false) ?? files ?? [];
            var info = job.Torrent!;
            var rootName = info.RootName;
            var finalize = new JsonObject();
            // The top-level name is picked now, when it is known which names are taken; a torrent finalized before keeps its own.
            if (!info.Finalized && list.Count > 0 && _sink is { } chooser)
            {
                var top = TorrentLayout.TopNameOf(list, info.Name);
                var chosen = chooser.ChooseFinalTopName(jobId, destination, top, list);
                if (!string.Equals(chosen, top, StringComparison.Ordinal))
                {
                    finalize["rootName"] = chosen;
                    rootName = chosen;
                    list = TorrentLayout.WithTopName(list, chosen);
                }
            }

            await DrainBlobsAsync(session, jobId).ConfigureAwait(false);

            // M16: the intent goes to disk before the renames, so a kill after them (before FinalizedAsync, or before the
            // resume data that knows the new names) is finished at the next start rather than downloaded again.
            var marked = !info.Finalized && _sink is not null;
            if (marked) await _sink!.FinalizingAsync(jobId, rootName, true, CancellationToken.None).ConfigureAwait(false);
            try
            {
                await session.SendAsync("finalize", finalize).ConfigureAwait(false);
            }
            catch (TorrentCommandException e)
            {
                // "already there" is said before any rename, so the files keep their part names, as the next attempt
                // expects. Any other refusal comes after some renames may have run: the mark stays, and the next start
                // finishes what it can.
                if (marked && e.Reason.StartsWith("already there", StringComparison.Ordinal))
                    await _sink!.FinalizingAsync(jobId, null, false, CancellationToken.None).ConfigureAwait(false);
                await PauseAndRemoveAsync(session, jobId).ConfigureAwait(false);
                return Failed(job, FailureKind.Permission, $"The finished files could not be given their names: {e.Reason}", retryable: true, wanted, wanted);
            }

            if (_sink is { } sink)
                await sink.FinalizedAsync(jobId, rootName, list, CancellationToken.None).ConfigureAwait(false);
            else
                await _store.SaveFilesAsync(jobId, list, CancellationToken.None).ConfigureAwait(false);

            // The resume data after the renames, so a later add finds the files under their own names.
            await PauseAndRemoveAsync(session, jobId).ConfigureAwait(false);
            // libtorrent keeps the pieces shared with files left out in a hidden file; the download is done, so it goes.
            if (!await TorrentLayout.DeletePartsFileAsync(destination, info.InfoHash).ConfigureAwait(false))
                _log?.Invoke($"Torrent {jobId:N}: its hidden parts file is still in use and stays.");
            var finalPath = TorrentLayout.FinalPathOf(destination, list, rootName ?? info.Name);
            return DownloadResult.Ok(wanted, wanted, finalPath, null, null, null);
        }
        finally
        {
            _finishing.TryRemove(jobId, out _);
        }
    }

    private async Task PauseAndRemoveAsync(TorrentSession session, Guid jobId)
    {
        try
        {
            await DrainBlobsAsync(session, jobId).ConfigureAwait(false);
            await session.SendAsync("pause").ConfigureAwait(false);
            await WaitForBlobAsync(session, jobId).ConfigureAwait(false);
        }
        catch (Exception e) when (e is TorrentEngineExitedException or TorrentCommandException)
        {
            _log?.Invoke($"Torrent {jobId:N}: pausing failed: {e.Message}");
        }

        await RemoveAsync(session).ConfigureAwait(false);
    }

    /// <summary>Saves the blobs already queued (they predate the next command) and drops the rest of the queue.</summary>
    private async Task DrainBlobsAsync(TorrentSession session, Guid jobId)
    {
        while (session.Events.TryRead(out var message))
            if (message["event"]?.GetValue<string>() == "resumeBlob") await SaveBlobAsync(jobId, message).ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for the resume blob the last command asked for. A blob requested earlier can still come first, so blobs keep
    /// being saved, in order, until none follows for <see cref="LaterBlobWait"/>: the last one saved is the newest.
    /// </summary>
    private async Task WaitForBlobAsync(TorrentSession session, Guid jobId)
    {
        var saved = false;
        using var timeout = new CancellationTokenSource(BlobWait);
        try
        {
            while (true)
            {
                using var quiet = saved ? CancellationTokenSource.CreateLinkedTokenSource(timeout.Token) : null;
                quiet?.CancelAfter(LaterBlobWait);
                if (!await session.Events.WaitToReadAsync((quiet ?? timeout).Token).ConfigureAwait(false)) return;
                while (session.Events.TryRead(out var message))
                {
                    if (message["event"]?.GetValue<string>() != "resumeBlob") continue;
                    await SaveBlobAsync(jobId, message).ConfigureAwait(false);
                    saved = true;
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (!saved) _log?.Invoke($"Torrent {jobId:N}: no resume data arrived in time; the last saved one stays.");
        }
    }

    private async Task RemoveAsync(TorrentSession session)
    {
        try
        {
            await session.SendAsync("remove").ConfigureAwait(false);
        }
        catch (Exception e) when (e is TorrentEngineExitedException or TorrentCommandException)
        {
            // gone already
        }
    }

    private async Task SaveBlobAsync(Guid jobId, JsonObject message)
    {
        if (message["data"]?.GetValue<string>() is not { } data) return;
        try
        {
            await _store.SaveResumeAsync(jobId, Convert.FromBase64String(data), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e) when (e is FormatException or IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"Torrent {jobId:N}: its resume data was not saved: {e.Message}");
        }
    }

    /// <summary>The worker's file list with each file's earlier choice kept (all wanted when there was none). Pad files are left out.</summary>
    internal static List<TorrentFile> MergeFiles(JsonObject metadata, IReadOnlyList<TorrentFile>? previous)
    {
        var choice = previous?.ToDictionary(file => file.Index, file => file.Wanted);
        // T3: and each wanted file's priority (the file list arrives again whenever the torrent starts).
        var levels = previous?.Where(file => file.Level is not null).ToDictionary(file => file.Index, file => file.Level);
        var files = new List<TorrentFile>();
        if (metadata["files"] is not JsonArray array) return files;
        foreach (var node in array)
        {
            if (node is not JsonObject file || file["pad"]?.GetValue<bool>() == true) continue;
            var index = file["index"]!.GetValue<int>();
            var wanted = choice is null || !choice.TryGetValue(index, out var w) || w;
            // libtorrent joins a path with '\' on Windows; the Host's lists use '/' everywhere.
            files.Add(new TorrentFile(index, TorrentLayout.NormalizePath(file["path"]!.GetValue<string>()), file["size"]!.GetValue<long>(), wanted)
            {
                Level = wanted && levels is not null && levels.TryGetValue(index, out var level) ? level : null,
            });
        }

        return files;
    }

    /// <summary>The worker state last reported per attached torrent, so the sink hears only of changes.</summary>
    private readonly ConcurrentDictionary<Guid, string> _states = new();

    private void NoteState(Guid jobId, JsonObject progress)
    {
        if (progress["state"] is not JsonValue value || !value.TryGetValue<string>(out var state)) return;
        if (_states.TryGetValue(jobId, out var last) && last == state) return;
        _states[jobId] = state;
        _sink?.TorrentStateChanged(jobId, state);
    }

    private static string PhaseOf(string? state) => state switch
    {
        "checking" => "Checking files",
        "metadata" => "Getting file list",
        "held" => "Waiting for file choice",
        "seeding" => "Finishing",
        _ => "Downloading",
    };

    /// <summary>Waiting for the user's choice of files; the service shows the job as paused.</summary>
    public static DownloadResult Held(DownloadJob job)
        => DownloadResult.Fail(DownloadState.Paused,
            new JobFailure(FailureKind.None, "Choose which files to download.", false, true, DateTimeOffset.UtcNow),
            job.ReceivedBytes, job.TotalBytes);

    private static DownloadResult Cancelled(DownloadJob job, long? done = null, long? wanted = null)
        => DownloadResult.Fail(DownloadState.Cancelled,
            new JobFailure(FailureKind.Cancelled, "The torrent was stopped.", false, true, DateTimeOffset.UtcNow),
            done ?? job.ReceivedBytes, wanted ?? job.TotalBytes);

    private static DownloadResult Failed(DownloadJob job, FailureKind kind, string message, bool retryable, long? done = null, long? wanted = null)
        => DownloadResult.Fail(DownloadState.Failed, new JobFailure(kind, message, retryable, true, DateTimeOffset.UtcNow),
            done ?? job.ReceivedBytes, wanted ?? job.TotalBytes);
}
