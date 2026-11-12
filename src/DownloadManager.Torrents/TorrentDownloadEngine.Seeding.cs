using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using DownloadManager.Core;

namespace DownloadManager.Torrents;

/// <summary>
/// Seeding (T2) and the details queries. A completed torrent is added back to the worker from its last resume data with
/// <c>seed</c> and <c>finalized</c>, and stays there until the seeding policy's ratio or time limit, a stop, an error or
/// the worker's loss. While nothing downloads or seeds, the supervisor closes the worker as before.
/// </summary>
public sealed partial class TorrentDownloadEngine
{
    private static readonly TimeSpan SeedStopWait = TimeSpan.FromSeconds(15);
    /// <summary>A timer longer than this is cut and the limits checked again then (a CancellationTokenSource's limit).</summary>
    private static readonly TimeSpan LongestSeedTimer = TimeSpan.FromDays(1);

    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<Guid, SeedRun> _seeding = new();
    private volatile TorrentSeedPolicy _seedPolicy = TorrentSeedPolicy.Default;
    private CancellationTokenSource _seedPolicyChanged = new();
    private volatile bool _disposing;
    private volatile bool _workerLacksPeers;
    private volatile bool _workerLacksTrackers;
    private volatile bool _workerLacksPieces;

    /// <summary>How many times an add that libtorrent refused is tried again (the same torrent may still be leaving).</summary>
    internal static int SeedAddAttempts { get; set; } = 5;

    private sealed class SeedRun(Guid jobId, string hashKey, TorrentSession session)
    {
        public Guid JobId { get; } = jobId;
        public string HashKey { get; } = hashKey;
        public TorrentSession Session { get; } = session;
        public CancellationTokenSource Stop { get; } = new();
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public bool IsSeeding(Guid jobId) => _seeding.ContainsKey(jobId);

    public void UpdateSeedPolicy(TorrentSeedPolicy policy)
    {
        _seedPolicy = policy;
        // Every seeding torrent checks its limits again.
        var previous = Interlocked.Exchange(ref _seedPolicyChanged, new CancellationTokenSource());
        previous.Cancel();
    }

    public void UpdateSpeedLimits(long? download, long? upload) => _ = _supervisor.SetLimitsAsync(download, upload);

    public async Task<string?> StartSeedingAsync(DownloadJob job, CancellationToken cancellationToken)
    {
        if (_disposing) return "The torrent engine is closing.";
        if (job.Torrent is not { } info) return "The job is not a torrent.";
        if (!info.Finalized) return "Only a finished torrent can be seeded.";
        var jobId = job.Id;
        if (_seeding.ContainsKey(jobId)) return null;
        if (_running.ContainsKey(jobId)) return "The torrent is still downloading.";
        var hashKey = info.InfoHash.ToLowerInvariant();
        if (hashKey.Length > 0 && _hashRuns.ContainsKey(hashKey)) return "Another download of this torrent is running.";
        if (_seeding.Values.Any(run => run.HashKey.Length > 0 && run.HashKey == hashKey)) return "This torrent is already seeding from another download.";

        var since = info.SeedingSince ?? _time.GetUtcNow();
        var wanted = info.WantedBytes;
        NoteSeedGoal(jobId, info);
        if (SeedLimitReached(SeedPolicyOf(jobId), info.Uploaded, wanted, since) is { } reached)
        {
            if (_sink is { } sink) await sink.SeedingStoppedAsync(jobId, TorrentSeedingEnd.LimitReached, reached).ConfigureAwait(false);
            return null;
        }

        var destination = job.DestinationDirectory;
        var partSuffix = string.IsNullOrEmpty(info.PartSuffix) ? TorrentJobInfo.PartSuffixFor(jobId) : info.PartSuffix;
        var rootName = info.RootName;
        var magnet = info.Magnet ?? job.Url;

        async Task<JsonObject> AddArgumentsAsync(CancellationToken token)
        {
            var arguments = new JsonObject
            {
                ["savePath"] = destination,
                ["partSuffix"] = partSuffix,
                ["finalized"] = true,
                ["seed"] = true,
            };
            if (!string.IsNullOrEmpty(rootName)) arguments["rootName"] = rootName;
            var resume = await _store.LoadResumeAsync(jobId, token).ConfigureAwait(false);
            var metadata = resume is null ? await _store.LoadMetadataAsync(jobId, token).ConfigureAwait(false) : null;
            if (resume is not null) arguments["resume"] = Convert.ToBase64String(resume);
            else if (metadata is not null) arguments["torrent"] = Convert.ToBase64String(metadata);
            else arguments["magnet"] = magnet;
            if (await _store.LoadFilesAsync(jobId, token).ConfigureAwait(false) is { Count: > 0 } files)
                arguments["filePriorities"] = new JsonArray(TorrentLayout.PrioritiesOf(files).Select(p => (JsonNode)p).ToArray());
            AddOptions(arguments, info);
            return arguments;
        }

        TorrentSession? session = null;
        for (var attempt = 1; session is null; attempt++)
        {
            try
            {
                session = await _supervisor.AttachAsync(jobId.ToString("N"), AddArgumentsAsync, cancellationToken).ConfigureAwait(false);
            }
            catch (TorrentCommandException e) when (e.Reason.StartsWith("could not add", StringComparison.Ordinal) && attempt < SeedAddAttempts)
            {
                // The download run's torrent may still be leaving libtorrent.
                await Task.Delay(TimeSpan.FromMilliseconds(300 * attempt), cancellationToken).ConfigureAwait(false);
            }
            catch (TorrentCommandException e)
            {
                return $"The torrent engine did not take this torrent: {e.Reason}";
            }
            catch (Exception e) when (e is TorrentEngineExitedException or InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
            {
                return $"The torrent engine could not start: {e.Message}";
            }
        }

        var run = new SeedRun(jobId, hashKey, session);
        if (!_seeding.TryAdd(jobId, run))
        {
            await session.DisposeAsync().ConfigureAwait(false);
            return null;
        }

        _ = Task.Run(() => SeedLoopAsync(run, since, info.Uploaded, wanted));
        return null;
    }

    public async Task StopSeedingAsync(Guid jobId)
    {
        if (!_seeding.TryGetValue(jobId, out var run)) return;
        run.Stop.Cancel();
        try
        {
            await run.Done.Task.WaitAsync(SeedStopWait).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _log?.Invoke($"Torrent {jobId:N}: seeding is still stopping.");
        }
    }

    /// <summary>Stops the seeding of this job and of any other job of the same torrent.</summary>
    private async Task StopSeedingOfAsync(Guid jobId, string infoHash)
    {
        var hashKey = infoHash.ToLowerInvariant();
        foreach (var run in _seeding.Values.Where(run => run.JobId == jobId || hashKey.Length > 0 && run.HashKey == hashKey).ToList())
            await StopSeedingAsync(run.JobId).ConfigureAwait(false);
    }

    /// <summary>
    /// All at once: each stop waits a moment for the torrent's last resume data, so one after another a hundred seeds
    /// would add half a minute to an exit, and a logoff that ends the Host first would lose the newest resume data (L1).
    /// </summary>
    private async Task StopAllSeedingQuietlyAsync()
    {
        var runs = _seeding.Values.ToList();
        foreach (var run in runs) run.Stop.Cancel();
        await Task.WhenAll(runs.Select(run => StopSeedingAsync(run.JobId))).ConfigureAwait(false);
    }

    /// <summary>Why the policy ends seeding now, or null.</summary>
    private string? SeedLimitReached(TorrentSeedPolicy policy, long uploaded, long wanted, DateTimeOffset since)
    {
        if (policy.RatioReached(uploaded, wanted)) return $"it reached the ratio {policy.Ratio:0.##}.";
        if (policy.TimeLeft(since, _time.GetUtcNow()) is { } left && left <= TimeSpan.Zero)
            return $"it seeded for {policy.Duration.TotalHours:0.##} hours.";
        return null;
    }

    private async Task SeedLoopAsync(SeedRun run, DateTimeOffset since, long uploaded, long wanted)
    {
        var jobId = run.JobId;
        var session = run.Session;
        var end = TorrentSeedingEnd.Failed;
        var message = "The torrent engine stopped.";
        var leave = true;
        // One try/finally over all of it: the run is fire-and-forget, so whatever throws in the loop (a JobChanged
        // subscriber reached through the sink, say) must still take it off the worker and out of _seeding (M17).
        try
        {
            try
            {
                while (true)
                {
                    var policy = SeedPolicyOf(jobId);
                    var policyChanged = _seedPolicyChanged.Token;
                    if (SeedLimitReached(policy, uploaded, wanted, since) is { } reached)
                    {
                        end = TorrentSeedingEnd.LimitReached;
                        message = "Seeding stopped: " + reached;
                        break;
                    }

                    var left = policy.TimeLeft(since, _time.GetUtcNow());
                    using var timer = left is { } wait
                        ? new CancellationTokenSource(wait < LongestSeedTimer ? wait : LongestSeedTimer, _time)
                        : new CancellationTokenSource();
                    using var wake = CancellationTokenSource.CreateLinkedTokenSource(run.Stop.Token, policyChanged, timer.Token);
                    try
                    {
                        if (!await session.Events.WaitToReadAsync(wake.Token).ConfigureAwait(false))
                        {
                            leave = false;
                            break;
                        }
                    }
                    catch (OperationCanceledException) when (run.Stop.IsCancellationRequested)
                    {
                        end = TorrentSeedingEnd.Stopped;
                        message = "Seeding was stopped.";
                        break;
                    }
                    catch (OperationCanceledException)
                    {
                        continue; // the time limit or the policy changed: checked at the top
                    }

                    var stop = false;
                    while (!stop && session.Events.TryRead(out var m))
                    {
                        switch (m["event"]?.GetValue<string>())
                        {
                            case "progress":
                                NoteState(jobId, m);
                                uploaded = Math.Max(uploaded, m["uploaded"]?.GetValue<long>() ?? 0);
                                _sink?.SeedingProgress(jobId, uploaded, m["upRate"]?.GetValue<double>() ?? 0,
                                    m["peers"]?.GetValue<int>() ?? 0, m["seeds"]?.GetValue<int>() ?? 0);
                                break;
                            case "resumeBlob":
                                await SaveBlobAsync(jobId, m).ConfigureAwait(false);
                                break;
                            case "error" when m["kind"]?.GetValue<string>() is "torrent" or "file":
                                message = "Seeding stopped: " + (m["message"]?.GetValue<string>() ?? "unknown error");
                                stop = true;
                                break;
                            case TorrentWorkerSupervisor.WorkerLostEvent:
                                message = m["message"]?.GetValue<string>() ?? message;
                                leave = false;
                                stop = true;
                                break;
                        }
                    }

                    if (stop) break;
                }
            }
            catch (Exception e) when (e is TorrentEngineExitedException or InvalidOperationException)
            {
                message = e.Message;
            }
            catch (Exception e)
            {
                message = "Seeding stopped after a fault: " + e.Message;
                _log?.Invoke($"Torrent {jobId:N}: the seeding loop failed: {e.GetType().Name}: {e.Message}");
            }

            try
            {
                if (leave) await PauseAndRemoveAsync(session, jobId).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _log?.Invoke($"Torrent {jobId:N}: taking the seeding torrent off the worker failed: {e.Message}");
            }

            await session.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _log?.Invoke($"Torrent {jobId:N}: closing the seeding run failed: {e.Message}");
        }
        finally
        {
            _seeding.TryRemove(new KeyValuePair<Guid, SeedRun>(jobId, run));
            _states.TryRemove(jobId, out _);
            // A closing engine keeps the job marked seeding, so it goes on after the next start.
            if (!_disposing && _sink is { } sink)
            {
                try { await sink.SeedingStoppedAsync(jobId, end, message).ConfigureAwait(false); }
                catch (Exception e) { _log?.Invoke($"Torrent {jobId:N}: recording the end of seeding failed: {e.Message}"); }
            }

            run.Done.TrySetResult();
        }
    }

    // ---- details queries ----

    private TorrentSession? AttachedSession(Guid jobId)
        => _running.TryGetValue(jobId, out var running) ? running
            : _seeding.TryGetValue(jobId, out var seeding) ? seeding.Session
            : null;

    /// <summary>Sends a details command; null when the torrent is not attached, the worker is gone, or the worker is older
    /// than the command (then <paramref name="lacks"/> is set and it is not asked again).</summary>
    private async Task<JsonObject?> QueryAsync(Guid jobId, string command, Func<bool> lacking, Action lacks, CancellationToken cancellationToken)
    {
        if (lacking() || AttachedSession(jobId) is not { } session) return null;
        try
        {
            return await session.SendAsync(command, null, cancellationToken).ConfigureAwait(false);
        }
        catch (TorrentCommandException e) when (e.Reason == "unknown command")
        {
            lacks();
            _log?.Invoke($"The torrent worker can't answer {command} (an older worker).");
            return null;
        }
        catch (Exception e) when (e is TorrentEngineExitedException or TorrentCommandException)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<TorrentPeerInfo>?> GetPeersAsync(Guid jobId, CancellationToken cancellationToken)
        => await QueryAsync(jobId, "peers", () => _workerLacksPeers, () => _workerLacksPeers = true, cancellationToken).ConfigureAwait(false) is { } reply
            ? ReadPeers(reply) : null;

    public async Task<IReadOnlyList<TorrentTrackerInfo>?> GetTrackersAsync(Guid jobId, CancellationToken cancellationToken)
        => await QueryAsync(jobId, "trackers", () => _workerLacksTrackers, () => _workerLacksTrackers = true, cancellationToken).ConfigureAwait(false) is { } reply
            ? ReadTrackers(reply) : null;

    public async Task<TorrentPieceSummary?> GetPiecesAsync(Guid jobId, CancellationToken cancellationToken)
        => await QueryAsync(jobId, "pieces", () => _workerLacksPieces, () => _workerLacksPieces = true, cancellationToken).ConfigureAwait(false) is { } reply
            ? ReadPieces(reply) : null;

    internal static List<TorrentPeerInfo> ReadPeers(JsonObject reply)
    {
        var peers = new List<TorrentPeerInfo>();
        if (reply["peers"] is not JsonArray array) return peers;
        foreach (var node in array)
        {
            if (node is not JsonObject p) continue;
            peers.Add(new TorrentPeerInfo(
                Text(p, "address"), Int(p, "port"), Text(p, "client"),
                p["flags"] is JsonArray flags ? flags.Select(f => f?.GetValue<string>() ?? string.Empty).Where(f => f.Length > 0).ToList() : [],
                Long(p, "downRate"), Long(p, "upRate"), Long(p, "downloaded"), Long(p, "uploaded"),
                Math.Clamp(Double(p, "progress"), 0, 1)));
        }

        return peers;
    }

    internal static List<TorrentTrackerInfo> ReadTrackers(JsonObject reply)
    {
        var trackers = new List<TorrentTrackerInfo>();
        if (reply["trackers"] is not JsonArray array) return trackers;
        foreach (var node in array)
        {
            if (node is not JsonObject t) continue;
            trackers.Add(new TorrentTrackerInfo(Text(t, "url"), Int(t, "tier"), Text(t, "status"), Text(t, "message"),
                t["seeds"] is null ? -1 : Int(t, "seeds"), t["peers"] is null ? -1 : Int(t, "peers"))
            {
                NextAnnounce = t["nextAnnounce"] is null ? -1 : Math.Max(-1, Long(t, "nextAnnounce")),
            });
        }

        return trackers;
    }

    internal static TorrentPieceSummary ReadPieces(JsonObject reply)
    {
        List<int[]>? ranges = null;
        if (reply["ranges"] is JsonArray array)
        {
            ranges = [];
            foreach (var node in array)
            {
                if (node is JsonArray { Count: 2 } pair && pair[0] is JsonValue first && first.TryGetValue<int>(out var a) &&
                    pair[1] is JsonValue last && last.TryGetValue<int>(out var b) && a >= 0 && b >= a)
                    ranges.Add([a, b]);
            }
        }

        var bitmap = reply["bitmap"]?.GetValue<string>();
        return new TorrentPieceSummary(Int(reply, "count"), Long(reply, "pieceLength"), Int(reply, "have"), ranges,
            string.IsNullOrEmpty(bitmap) ? null : bitmap);
    }

    private static string Text(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : string.Empty;
    private static int Int(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<int>(out var n) ? n : 0;
    private static long Long(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<long>(out var n) ? n : 0;
    private static double Double(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<double>(out var n) ? n : 0;
}
