using DownloadManager.Contracts;
using DownloadManager.Core;

namespace DownloadManager.Runtime;

/// <summary>
/// UI.B7 and UI.R21: limits that apply while downloads run. A lower simultaneous-downloads limit (overall or a
/// queue's) sends the excess back to the queue, newest first, keeping their data; the per-host connection share
/// is the engine's, and this side only shows which jobs are waiting for a connection.
/// </summary>
public sealed partial class DownloadManagerService
{
    public static string WaitingForConnectionText(string host) => $"Waiting for a connection to {host}";

    /// <summary>The waiting host is only worth showing while the job runs; a stale one must not outlive it.</summary>
    private static string? WaitingHostShown(DownloadJob job)
        => job.State is DownloadState.Connecting or DownloadState.Downloading or DownloadState.RetryWaiting
            ? job.WaitingForHost
            : null;

    /// <summary>
    /// Raised by the engine from inside its limiter's dispatch, perhaps while a cancel holds <c>_sync</c> on
    /// another thread, so the work moves to the pool. It reads the limiter's current answer, which makes notices
    /// that arrive late or out of order harmless: the last one to run sees the latest state.
    /// </summary>
    private void OnHostWaitChanged(Guid jobId)
        => ThreadPool.UnsafeQueueUserWorkItem(static state => state.service.RefreshWaitingForHost(state.jobId),
            (service: this, jobId), preferLocal: false);

    private void RefreshWaitingForHost(Guid jobId)
    {
        JobDto? dto;
        lock (_sync)
        {
            if (!_jobs.TryGetValue(jobId, out var runtime))
            {
                return;
            }

            var running = runtime.Starting || runtime.Runner is { IsCompleted: false };
            var host = running ? _engine.WaitingForHost(jobId) : null;
            if (string.Equals(host, runtime.Job.WaitingForHost, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            runtime.Job.WaitingForHost = host;
            runtime.Job.Touch();
            dto = Map(runtime.Job);
        }

        AnnounceJobChange(dto, "progress");
    }

    /// <summary>Requeues what the current limits leave over, then saves and announces each one.</summary>
    private async Task RequeueExcessAsync(CancellationToken cancellationToken)
    {
        List<JobRuntime> requeued;
        List<JobRuntime> flagged;
        lock (_sync)
        {
            RefreshQueueConditionsLocked();
            foreach (var runtime in _jobs.Values.Where(r => r.Job.ResumeSupported is null)) runtime.ResumeKnownChecked = false;
            requeued = EnforceConcurrencyLimitsLocked();
            flagged = RefreshNoResumeFlagsLocked();
        }

        foreach (var runtime in requeued)
        {
            await PersistAsync(runtime, cancellationToken).ConfigureAwait(false);
            Raise(runtime, "queued");
        }

        foreach (var runtime in flagged) Raise(runtime, "updated");
        AnnounceMeteredWait();
        ApplyTorrentConditions();
    }

    /// <summary>
    /// Picks the running downloads above the overall limit or their queue's, in the reverse of the order
    /// <see cref="Schedule"/> starts them (lowest priority, then latest in the queue's file order, then newest),
    /// and stops each as a pause would, except that it ends in Queued with its data and no completion actions.
    /// A job that can't stop without losing work is left to finish above the limit, and counts as part of the
    /// excess it is: the owner chose "newest first", so an older one is not stopped in its place.
    /// </summary>
    private List<JobRuntime> EnforceConcurrencyLimitsLocked()
    {
        var requeued = new List<JobRuntime>();
        if (!_initialized || _shuttingDown)
        {
            return requeued;
        }

        if (MeteredRuleHoldsLocked())
        {
            // Not metered only, on a metered network: everything that can stop without losing work goes back to the
            // queue (not paused by the user), and Schedule starts nothing until the rule lets go.
            foreach (var runtime in _jobs.Values.Where(r => (r.Starting || r.Runner is { IsCompleted: false }) && !r.PauseRequested && !r.CancelRequested &&
                         !r.Job.IsTerminal).ToList())
            {
                if (WhyNotPreemptable(runtime) is { } why)
                {
                    _log?.Invoke($"Job {runtime.Job.Id} keeps running on a metered network: {why}");
                    continue;
                }

                PreemptLocked(runtime);
                requeued.Add(runtime);
            }

            return requeued;
        }

        var running = _jobs.Values
            // A runner in its completion tail (state already Completed, runner not yet done) is never sent back to the queue.
            .Where(r => (r.Starting || r.Runner is { IsCompleted: false }) && !r.PauseRequested && !r.CancelRequested && !r.Job.IsTerminal)
            .ToList();
        if (TorrentNetworkHoldsLocked())
        {
            // ANDROID.9: torrents only on Wi-Fi: a running torrent goes back to its queue (it resumes from its pieces).
            foreach (var runtime in running.Where(r => r.Job.Torrent is not null).ToList())
            {
                if (WhyNotPreemptable(runtime) is not null) continue;
                PreemptLocked(runtime);
                requeued.Add(runtime);
                running.Remove(runtime);
            }
        }

        var queueDefaults = _settings.Queues.FirstOrDefault(q => q.Id == _settings.DefaultQueueId) ?? _settings.Queues[0];
        int QueueKey(JobRuntime r) => r.Job.IsQueued ? (_settings.Queues.FirstOrDefault(q => q.Id == r.Job.QueueId) ?? queueDefaults).Id : -1;
        int QueueMax(JobRuntime r) => r.Job.IsQueued
            ? (_settings.Queues.FirstOrDefault(q => q.Id == r.Job.QueueId) ?? queueDefaults).MaxConcurrentDownloads
            : _settings.MaxConcurrentDownloads;

        foreach (var runtime in running.ToList())
        {
            var queue = runtime.Job.IsQueued ? _settings.Queues.FirstOrDefault(q => q.Id == runtime.Job.QueueId) ?? queueDefaults : null;
            if (queue is null || QueueConditionWaitLocked(queue) is null || WhyNotPreemptable(runtime) is not null) continue;
            PreemptLocked(runtime);
            requeued.Add(runtime);
            running.Remove(runtime);
        }
        var overall = running.Count - _settings.MaxConcurrentDownloads;
        var perQueue = running.GroupBy(QueueKey).ToDictionary(group => group.Key, group => group.Count());
        var fileOrder = FileOrderLocked();
        foreach (var runtime in running
                     .OrderBy(r => r.Job.Priority)
                     .ThenByDescending(r => fileOrder.GetValueOrDefault((r.Job.QueueId, r.Job.Id), int.MaxValue))
                     .ThenByDescending(r => r.Job.CreatedAt))
        {
            var key = QueueKey(runtime);
            if (overall <= 0 && perQueue[key] <= QueueMax(runtime))
            {
                continue;
            }

            overall--;
            perQueue[key]--;
            if (WhyNotPreemptable(runtime) is { } reason)
            {
                _log?.Invoke($"Job {runtime.Job.Id} stays above the lowered limit: {reason}");
                continue;
            }

            PreemptLocked(runtime);
            requeued.Add(runtime);
        }

        return requeued;
    }

    /// <summary>Stops a running job as a pause would, except that it ends Queued with its data (no user pause mark).</summary>
    private static void PreemptLocked(JobRuntime runtime)
    {
        runtime.Preempted = true;
        runtime.PauseRequested = true;
        runtime.ResumeWhenStopped = true;
        runtime.Job.State = DownloadState.Queued;
        runtime.Job.BytesPerSecond = null;
        runtime.Job.EstimatedRemaining = null;
        runtime.Job.ActiveConnections = 0;
        runtime.Job.WaitingForHost = null;
        runtime.Job.Touch();
        runtime.Cts?.Cancel();
    }

    // Not metered only (phone): the network the platform last reported, and whether "Use data" lifted the rule until
    // the network is next reported not metered. A platform that can't tell never reports, so desktop is never held.
    private bool _networkMetered;
    private bool _meteredLifted;
    private bool _meteredWaitAnnounced;
    // Decide and deliver under one gate (taken before _sync, never inside it), so two callers can't deliver out of order.
    private readonly object _meteredAnnounceGate = new();

    /// <summary>The previous run was stopped by the user; see <see cref="InitializeAsync(bool, CancellationToken)"/>.</summary>
    private bool _lastRunStoppedByUser;

    /// <summary>
    /// Sets <see cref="DownloadJob.RunningOnMeteredWithoutResume"/> on running jobs the held rule leaves running because they
    /// can't stop without losing work, and clears it everywhere else. Returns the jobs whose flag changed. Re-evaluated
    /// whenever limits are re-enforced (network report, "Use data", settings), not on every progress tick.
    /// </summary>
    private List<JobRuntime> RefreshNoResumeFlagsLocked()
    {
        var changed = new List<JobRuntime>();
        var holds = MeteredRuleHoldsLocked();
        foreach (var runtime in _jobs.Values)
        {
            var want = holds && !runtime.Job.IsTerminal && (runtime.Starting || runtime.Runner is { IsCompleted: false }) &&
                       !runtime.PauseRequested && !runtime.CancelRequested && !IsFinishing(runtime) && WhyNotResumable(runtime.Job) is not null;
            if (runtime.Job.RunningOnMeteredWithoutResume == want) continue;
            runtime.Job.RunningOnMeteredWithoutResume = want;
            runtime.ResumeKnownChecked = false;
            runtime.Job.Touch();
            changed.Add(runtime);
        }

        return changed;
    }

    /// <summary>
    /// A running job's server has just said whether it can resume while the held rule keeps the job running (flagged): applies the
    /// rule again, so a job that can resume goes back to the queue like any other, and one that can't stays flagged.
    /// </summary>
    private async Task RecheckMeteredAfterResumeKnownAsync()
    {
        try
        {
            await RequeueExcessAsync(CancellationToken.None).ConfigureAwait(false);
            Schedule();
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Re-applying the metered rule failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Raised, outside the lock, when <see cref="WaitingForMeteredNetwork"/> changes.</summary>
    public event EventHandler<bool>? MeteredWaitChanged;

    private bool MeteredRuleHoldsLocked() => _settings.DownloadOnlyOnUnmeteredNetwork && _networkMetered && !_meteredLifted;

    /// <summary>True while downloads are held for a not-metered network (setting on, network metered, "Use data" not tapped).</summary>
    public bool WaitingForMeteredNetwork
    {
        get { lock (_sync) return MeteredRuleHoldsLocked(); }
    }

    private void AnnounceMeteredWait()
    {
        lock (_meteredAnnounceGate)
        {
            bool holds;
            lock (_sync)
            {
                holds = MeteredRuleHoldsLocked();
                if (holds == _meteredWaitAnnounced) return;
                _meteredWaitAnnounced = holds;
            }

            try
            {
                MeteredWaitChanged?.Invoke(this, holds);
            }
            catch (Exception ex)
            {
                // A listener's failure must not reach the settings save or the network report that caused it.
                _log?.Invoke($"A metered-wait listener failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// The platform reports the active network: metered or not. Metered with the setting on sends running downloads back to
    /// the queue (data kept) and holds the queue; not metered lets go, ends a "Use data" lift, and starts what waits.
    /// <para>
    /// Order: a platform should report the network <b>before</b> <see cref="InitializeAsync(CancellationToken)"/>, so the first
    /// <c>Schedule()</c> after reconcile is already held. A report before initialize only stores the state (nothing is saved,
    /// no job is touched, nothing is announced); initialize then announces it against the loaded settings.
    /// </para>
    /// </summary>
    public async Task SetNetworkMeteredAsync(bool metered, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            _networkMetered = metered;
            if (!metered) _meteredLifted = false;
            if (!_initialized) return;
        }
        await RequeueExcessAsync(cancellationToken).ConfigureAwait(false);
        SettingsChanged?.Invoke(this, _settings);
        Schedule();
    }

    /// <summary>
    /// Only while the metered rule holds: stops the running downloads flagged <see cref="DownloadJob.RunningOnMeteredWithoutResume"/>
    /// (the phone's Stop on the warning, or the platform when the phone is locked as the rule starts to hold) and puts them back to
    /// Queued, never paused by the user. Their partial data can't be resumed, so they start over from zero on a network that isn't
    /// metered, as the engine restarts any download whose server can't resume. A job allowed to go on stays allowed until it ends
    /// or the rule lets go. One verifying or finishing its file is left alone. Returns how many were stopped; 0 when the rule
    /// doesn't hold.
    /// </summary>
    public async Task<int> StopNonResumableOnMeteredAsync(CancellationToken cancellationToken = default)
    {
        List<JobRuntime> stopped = new();
        List<JobRuntime> flagged;
        lock (_sync)
        {
            if (!_initialized || _shuttingDown || !MeteredRuleHoldsLocked()) return 0;
            foreach (var runtime in _jobs.Values.Where(r => r.Job.RunningOnMeteredWithoutResume && !r.Job.IsTerminal &&
                         (r.Starting || r.Runner is { IsCompleted: false }) && !r.PauseRequested && !r.CancelRequested).ToList())
            {
                if (IsFinishing(runtime)) continue;
                PreemptLocked(runtime);
                stopped.Add(runtime);
            }

            flagged = RefreshNoResumeFlagsLocked();
        }

        foreach (var runtime in stopped)
        {
            await PersistAsync(runtime, cancellationToken).ConfigureAwait(false);
            Raise(runtime, "queued");
        }

        foreach (var runtime in flagged.Except(stopped)) Raise(runtime, "updated");
        return stopped.Count;
    }

    /// <summary>"Use data": the rule stays off until the network is next reported not metered.</summary>
    public void AllowMeteredUntilUnmetered()
    {
        // Only a metered network can be lifted: a tap that lands after the network turned not metered must not carry over
        // to the next metered one.
        // A rule that doesn't hold (setting off, or not metered) has nothing to lift, and the lift must not outlive it.
        List<JobRuntime> flagged;
        lock (_sync)
        {
            if (MeteredRuleHoldsLocked()) _meteredLifted = true;
            flagged = RefreshNoResumeFlagsLocked();
        }

        foreach (var runtime in flagged) Raise(runtime, "updated");
        AnnounceMeteredWait();
        Schedule();
    }

    /// <summary>Null when stopping now keeps everything downloaded so far; otherwise why not.</summary>
    private static string? WhyNotPreemptable(JobRuntime runtime) =>
        IsFinishing(runtime) ? "it is verifying or finishing its file." : WhyNotResumable(runtime.Job);

    /// <summary>Verifying or finishing its file: left alone, and never flagged as "can't resume" (it is about to end).</summary>
    private static bool IsFinishing(JobRuntime runtime)
    {
        var phase = runtime.LastPhase ?? string.Empty;
        return runtime.Job.State == DownloadState.Verifying || phase.StartsWith("Verifying", StringComparison.Ordinal) ||
               phase.StartsWith("Finaliz", StringComparison.Ordinal);
    }

    /// <summary>Null when the job's data so far survives a stop; otherwise why not.</summary>
    private static string? WhyNotResumable(DownloadJob job)
    {
        if (job.ReceivedBytes == 0)
        {
            return null;
        }

        return job.ResumeSupported switch
        {
            true => null,
            false => "its server can't resume, so stopping would lose what it has.",
            null => "its server hasn't said whether it can resume.",
        };
    }

    private Dictionary<(int QueueId, Guid JobId), int> FileOrderLocked()
        => _settings.Queues
            .SelectMany(queue => (queue.JobOrder ?? new()).Select((id, index) => (QueueId: queue.Id, JobId: id, Index: index)))
            .GroupBy(item => (item.QueueId, item.JobId))
            .ToDictionary(group => group.Key, group => group.First().Index);
}
