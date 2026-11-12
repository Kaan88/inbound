using DownloadManager.Contracts;
using DownloadManager.Core;

namespace DownloadManager.Runtime;

/// <summary>
/// Torrent seeding (T2): a completed torrent stays on the worker, shared with other peers, until the seeding policy's
/// ratio or time limit, the user, Stop All, its queue's stop or quota, or turning torrents off ends it. The job stays
/// completed throughout and takes no download slot.
/// </summary>
public sealed partial class DownloadManagerService
{
    /// <summary>One seeding start or stop at a time, so a stop that comes while a start is on its way finds it.</summary>
    private readonly SemaphoreSlim _seedingGate = new(1, 1);

    /// <summary>Jobs whose seeding start is asked for but has not taken <see cref="_seedingGate"/> yet (under
    /// <c>_sync</c>). A stop or a removal takes the job out, and the start then does nothing.</summary>
    private readonly HashSet<Guid> _seedPending = [];

    /// <summary>Why the seeding settings can't be saved, or null.</summary>
    private static string? TorrentSeedSettingsError(SettingsDto settings)
    {
        if (!double.IsFinite(settings.TorrentSeedRatio) || settings.TorrentSeedRatio is < 0 or > 100)
            return "Enter a seeding ratio from 0 to 100 (0 is no ratio limit).";
        if (!double.IsFinite(settings.TorrentSeedHours) || settings.TorrentSeedHours is < 0 or > 8760)
            return "Enter seeding hours from 0 to 8760 (0 is no time limit).";
        if (settings.TorrentSeedRatio == 0 && settings.TorrentSeedHours == 0)
            return "Seeding needs a ratio or a time limit; both can't be 0.";
        if (settings.TorrentUploadLimit < 0)
            return "The torrent upload limit can't be below 0.";
        return null;
    }

    private void ApplyTorrentSeedSettings(AppSettings settings)
    {
        // The upload limit goes with the overall speed limit's split (TorrentSpeedBudget.cs).
        _torrentEngine?.UpdateSeedPolicy(TorrentSeedPolicy.From(settings.TorrentSeedRatio, settings.TorrentSeedHours));
    }

    /// <summary>After a torrent's download completed: seeds when the default policy says so.</summary>
    private void StartSeedingAfterDownload(JobRuntime runtime)
    {
        lock (_sync)
        {
            // Marked when the job turned completed; a stop since then took it out.
            if (!_seedPending.Contains(runtime.Job.Id)) return;
            if (_shuttingDown)
            {
                _seedPending.Remove(runtime.Job.Id);
                return;
            }
        }

        _ = BeginSeedingAsync(runtime, CancellationToken.None);
    }

    /// <summary>Under <c>_sync</c>, as a torrent job turns completed: its seeding start counts as asked for from that
    /// moment, so a Stop All that sees the job completed also stops the seeding that is about to start. A torrent that
    /// finished just as it was stopped (Stop, Stop All, its queue's stop or quota), or whose queue has used up its quota,
    /// does not start seeding.</summary>
    private void MarkSeedingPendingLocked(JobRuntime runtime)
    {
        // T3: the torrent's own seeding goal wins over Options' "seed after download".
        if (runtime.Job.Torrent is not { } torrent || !TorrentSeedGoals.Seeds(torrent, _settings.TorrentSeedAfterDownload) || _shuttingDown ||
            runtime.PauseRequested && !runtime.ResumeWhenStopped ||
            runtime.CancelRequested) return;
        if (QuotaUsedUpLocked(runtime.Job)) return;
        _seedPending.Add(runtime.Job.Id);
    }

    private bool QuotaUsedUpLocked(DownloadJob job)
        => job.IsQueued && _settings.Queues.FirstOrDefault(queue => queue.Id == job.QueueId) is { QuotaEnabled: true } queue &&
           queue.QuotaBytesUsed >= queue.QuotaBytes;

    /// <summary>Under <c>_sync</c>: whether a torrent of this queue has seeded since before <paramref name="stopAt"/>, so
    /// the queue's scheduled stop stops it even when the queue itself is not running. Seeding started after the stop
    /// time (by hand, or a torrent started by hand that finished) is left alone.</summary>
    private bool QueueSeededBeforeLocked(int queueId, DateTimeOffset stopAt)
        => _jobs.Values.Any(runtime => runtime.Job.IsQueued && runtime.Job.QueueId == queueId &&
            runtime.Job.Torrent is { Seeding: true } torrent && (torrent.SeedingSince is not { } since || since <= stopAt));

    /// <summary>Completed torrents that were seeding when the Host stopped go on seeding (their time limit still counts
    /// from when they began).</summary>
    private void ResumeSeedingAfterStart()
    {
        var on = TorrentsOn;
        List<JobRuntime> seeding;
        lock (_sync)
        {
            seeding = _jobs.Values.Where(runtime => runtime.Job.Torrent is { Seeding: true }).ToList();
            if (on)
                foreach (var runtime in seeding.Where(runtime => runtime.Job.State == DownloadState.Completed))
                    _seedPending.Add(runtime.Job.Id);
        }

        foreach (var runtime in seeding)
        {
            // Torrents are off or missing now (Options, the kill switch, no worker), or the job is no longer
            // completed: nothing seeds it, so it doesn't stay marked seeding.
            if (on && runtime.Job.State == DownloadState.Completed) _ = BeginSeedingAsync(runtime, CancellationToken.None);
            else _ = ClearSeedingAsync(runtime.Job.Id);
        }
    }

    /// <summary>Marks the job seeding and puts it on the worker. Returns why it could not, or null.</summary>
    private async Task<string?> BeginSeedingAsync(JobRuntime runtime, CancellationToken cancellationToken)
    {
        await _seedingGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await BeginSeedingCoreAsync(runtime, cancellationToken).ConfigureAwait(false); }
        finally { _seedingGate.Release(); }
    }

    private async Task<string?> BeginSeedingCoreAsync(JobRuntime runtime, CancellationToken cancellationToken)
    {
        string? refusal = null;
        var engine = _torrentEngine;
        var on = TorrentsOn;
        var onEngine = engine?.IsSeeding(runtime.Job.Id) == true;
        bool clear;
        lock (_sync)
        {
            // A stop or a removal that came while this start waited for the gate took it out of the pending set.
            if (!_seedPending.Remove(runtime.Job.Id) || runtime.CancelledForRemoval || !_jobs.ContainsKey(runtime.Job.Id))
                refusal = "Seeding was stopped.";
            else if (engine is null || !on)
                refusal = "Torrents are turned off.";
            else if (runtime.Job.Torrent is not { } info || runtime.Job.State != DownloadState.Completed || !info.Finalized)
                refusal = "Only a finished torrent can be seeded.";
            else if (runtime.Job.FileRemovedAfterExtraction)
                refusal = RemovedAfterExtractionRefusal;
            else if (!info.Seeding)
            {
                info.Seeding = true;
                info.SeedingStoppedNote = null;
                info.SeedingSince = _timeProvider.GetUtcNow();
                runtime.Job.Touch();
            }

            clear = refusal is not null && runtime.Job.Torrent is { Seeding: true } && !onEngine;
        }

        if (refusal is not null)
        {
            // A flag left from before (seeding at the last start) is cleared, so nothing shows seeding that isn't.
            if (clear) await ClearSeedingAsync(runtime.Job.Id).ConfigureAwait(false);
            return refusal;
        }

        await PersistAsync(runtime, CancellationToken.None).ConfigureAwait(false);
        Raise(runtime, "updated");
        string? error;
        try
        {
            error = await engine!.StartSeedingAsync(runtime.Job, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            error = ex.Message;
        }

        if (error is not null)
        {
            _log?.Invoke($"Torrent job {runtime.Job.Id}: seeding did not start: {error}");
            await ((ITorrentJobSink)this).SeedingStoppedAsync(runtime.Job.Id, TorrentSeedingEnd.Failed, error).ConfigureAwait(false);
        }

        return error;
    }

    /// <summary>Stops seeding for the jobs that match; callable under the lock (the engine is told afterwards).</summary>
    private void StopSeedingWhere(Func<JobRuntime, bool> match)
    {
        List<JobRuntime> stopping;
        lock (_sync)
        {
            // A start still waiting for the gate counts too: it is taken out here, before the gate, so it does nothing.
            stopping = _jobs.Values.Where(runtime =>
                (runtime.Job.Torrent is { Seeding: true } || _seedPending.Contains(runtime.Job.Id)) && match(runtime)).ToList();
            foreach (var runtime in stopping) _seedPending.Remove(runtime.Job.Id);
        }

        foreach (var runtime in stopping) _ = Task.Run(() => StopTorrentSeedingAsync(runtime));
    }

    private async Task StopTorrentSeedingAsync(JobRuntime runtime)
    {
        lock (_sync) _seedPending.Remove(runtime.Job.Id);
        await _seedingGate.WaitAsync().ConfigureAwait(false);
        try { await StopTorrentSeedingCoreAsync(runtime).ConfigureAwait(false); }
        finally { _seedingGate.Release(); }
    }

    private async Task StopTorrentSeedingCoreAsync(JobRuntime runtime)
    {
        bool seeding;
        lock (_sync) seeding = runtime.Job.Torrent is { Seeding: true };
        if (_torrentEngine is { } engine && (seeding || engine.IsSeeding(runtime.Job.Id)))
        {
            try
            {
                await engine.StopSeedingAsync(runtime.Job.Id).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log?.Invoke($"Torrent job {runtime.Job.Id}: stopping its seeding failed: {ex.Message}");
            }
        }

        // The engine reports the stop through the sink; a job that was not on the worker is cleared here.
        if (seeding) await ClearSeedingAsync(runtime.Job.Id).ConfigureAwait(false);
    }

    private async Task ClearSeedingAsync(Guid jobId, string? failure = null)
    {
        JobRuntime? runtime;
        lock (_sync)
        {
            // T8: it sends nothing more, also when its job has gone.
            _speedHistory.UploadStopped(jobId, DateTimeOffset.UtcNow);
            if (!_jobs.TryGetValue(jobId, out runtime) || runtime.Job.Torrent is not { } info) return;
            var noted = failure is { Length: > 0 } && info.SeedingStoppedNote != failure;
            if (!info.Seeding && !noted) return;
            if (noted) info.SeedingStoppedNote = failure;
            info.Seeding = false;
            info.SeedingSince = null;
            info.UploadRate = 0;
            info.Peers = 0;
            info.Seeds = 0;
            runtime.Job.Touch();
        }

        await PersistAsync(runtime, CancellationToken.None).ConfigureAwait(false);
        Raise(runtime, "updated");
    }

    void ITorrentJobSink.SeedingProgress(Guid jobId, long uploaded, double uploadRate, int peers, int seeds)
    {
        JobRuntime? runtime;
        lock (_sync)
        {
            if (!_jobs.TryGetValue(jobId, out runtime) || runtime.Job.Torrent is not { Seeding: true } info) return;
            info.Uploaded = Math.Max(info.Uploaded, uploaded);
            info.UploadRate = uploadRate;
            info.Peers = peers;
            info.Seeds = seeds;
            _speedHistory.ReportUpload(jobId, uploadRate, DateTimeOffset.UtcNow);
        }

        Raise(runtime, "progress");
        SplitSpeedBudgetSoon();
    }

    async Task ITorrentJobSink.SeedingStoppedAsync(Guid jobId, TorrentSeedingEnd reason, string message)
    {
        // A Host that is closing keeps the flag, so seeding goes on after the next start.
        if (_shuttingDown) return;
        if (reason != TorrentSeedingEnd.Stopped) _log?.Invoke($"Torrent job {jobId}: seeding ended ({reason}): {message}");
        // A fault is said on the row (walk 2026-11-12: a recheck that found damage left only "Completed"); it is saved
        // even when seeding was already off, so a late fault isn't lost.
        await ClearSeedingAsync(jobId, reason == TorrentSeedingEnd.Failed ? message : null).ConfigureAwait(false);
    }

    /// <summary>Extraction deleted the archive: there is nothing left to share.</summary>
    private const string RemovedAfterExtractionRefusal = "The file was removed after extraction.";

    /// <summary><c>torrent.seed</c>: starts or stops seeding a completed torrent.</summary>
    public async Task<TorrentActionResult> SetTorrentSeedingAsync(TorrentSeedRequest request, CancellationToken cancellationToken = default, CallerScope? caller = null)
    {
        JobRuntime? runtime;
        lock (_sync) _jobs.TryGetValue(request.JobId, out runtime);
        if (runtime?.Job.Torrent is null) return new TorrentActionResult { Error = "That torrent is not in the list." };
        if (caller is not null && HeldBy(request.JobId, caller) == false) return new TorrentActionResult { Error = CallerScope.OutsideJobRefused };
        if (!request.On)
        {
            await StopTorrentSeedingAsync(runtime).ConfigureAwait(false);
            return new TorrentActionResult { Ok = true };
        }

        lock (_sync)
        {
            if (runtime.Job.State != DownloadState.Completed || !runtime.Job.Torrent!.Finalized)
                return new TorrentActionResult { Error = "Only a finished torrent can be seeded." };
            if (runtime.Job.Torrent.SeedGoal == TorrentSeedGoals.Never)
                return new TorrentActionResult { Error = "This torrent's seeding goal is Never. Change it in its Options tab to seed it." };
            if (runtime.Job.FileRemovedAfterExtraction) return new TorrentActionResult { Error = RemovedAfterExtractionRefusal };
            // Already seeding, or its start is on its way.
            if (runtime.Job.Torrent.Seeding || !_seedPending.Add(runtime.Job.Id)) return new TorrentActionResult { Ok = true };
        }

        var error = await BeginSeedingAsync(runtime, cancellationToken).ConfigureAwait(false);
        return new TorrentActionResult { Ok = error is null, Error = error };
    }

    public async Task<TorrentPeersResult> GetTorrentPeersAsync(TorrentJobRequest request, CancellationToken cancellationToken = default, CallerScope? caller = null)
    {
        if (TorrentQueryRefusal(request.JobId, caller) is { } refusal) return new TorrentPeersResult { Error = refusal };
        var peers = await _torrentEngine!.GetPeersAsync(request.JobId, cancellationToken).ConfigureAwait(false);
        return new TorrentPeersResult
        {
            Ok = true,
            Running = peers is not null,
            Peers = (peers ?? []).Select(peer => new TorrentPeerDto
            {
                Address = peer.Address, Port = peer.Port, Client = peer.Client, Flags = peer.Flags.ToList(),
                DownloadRate = peer.DownloadRate, UploadRate = peer.UploadRate, Downloaded = peer.Downloaded,
                Uploaded = peer.Uploaded, Progress = peer.Progress,
            }).ToList(),
        };
    }

    public async Task<TorrentTrackersResult> GetTorrentTrackersAsync(TorrentJobRequest request, CancellationToken cancellationToken = default, CallerScope? caller = null)
    {
        if (TorrentQueryRefusal(request.JobId, caller) is { } refusal) return new TorrentTrackersResult { Error = refusal };
        var trackers = await _torrentEngine!.GetTrackersAsync(request.JobId, cancellationToken).ConfigureAwait(false);
        return new TorrentTrackersResult
        {
            Ok = true,
            Running = trackers is not null,
            Trackers = (trackers ?? []).Select(tracker => new TorrentTrackerDto
            {
                Url = tracker.Url, Tier = tracker.Tier, Status = tracker.Status, Message = tracker.Message,
                Seeds = tracker.Seeds, Peers = tracker.Peers,
            }).ToList(),
        };
    }

    public async Task<TorrentPiecesResult> GetTorrentPiecesAsync(TorrentJobRequest request, CancellationToken cancellationToken = default, CallerScope? caller = null)
    {
        if (TorrentQueryRefusal(request.JobId, caller) is { } refusal) return new TorrentPiecesResult { Error = refusal };
        var pieces = await _torrentEngine!.GetPiecesAsync(request.JobId, cancellationToken).ConfigureAwait(false);
        return pieces is null
            ? new TorrentPiecesResult { Ok = true }
            : new TorrentPiecesResult
            {
                Ok = true, Running = true, Count = pieces.Count, PieceLength = pieces.PieceLength, Have = pieces.Have,
                Ranges = pieces.Ranges?.ToList(), Bitmap = pieces.Bitmap,
            };
    }

    private string? TorrentQueryRefusal(Guid jobId, CallerScope? caller)
    {
        bool known;
        lock (_sync) known = _jobs.TryGetValue(jobId, out var runtime) && runtime.Job.Torrent is not null;
        if (!known || _torrentEngine is null) return "That torrent is not in the list.";
        if (caller is not null && HeldBy(jobId, caller) == false) return CallerScope.OutsideJobRefused;
        return null;
    }
}
