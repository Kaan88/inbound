using DownloadManager.Contracts;

namespace DownloadManager.Mobile;

/// <summary>The file the ongoing notification names: the oldest active download (stable, so the text does not flip between files).</summary>
public sealed record ActiveFile(Guid Id, string Name, long ReceivedBytes, long? TotalBytes, TimeSpan? TimeLeft);

/// <summary>
/// What the keep-alive and the notifications need, as one immutable value. Compared by value: a new summary equal to the last
/// one raises nothing.
/// </summary>
/// <param name="KeepAlive">Something can transfer now: running, or will start by itself with nothing holding it (see <see cref="ActivityState"/>).</param>
/// <param name="ActiveCount">Downloads starting, connecting, downloading or verifying.</param>
/// <param name="BytesPerSecond">Sum of the active downloads' rates.</param>
/// <param name="First">The file to name in the notification; null when none is active.</param>
/// <param name="ActiveReceivedBytes">Bytes received across the active downloads (for a combined bar).</param>
/// <param name="ActiveTotalBytes">Sizes added up across the active downloads; null when any of them has no known size (no determinate bar).</param>
/// <param name="WaitingForNetwork">The Host holds downloads for a not-metered network and at least one would run without that
/// (waiting is left to the system's network constraint; this does not keep the process alive).</param>
/// <param name="WaitingCount">While <see cref="WaitingForNetwork"/>: the downloads that will resume ("N will resume"); 0 otherwise.</param>
/// <param name="PausedCount">Downloads in the paused state.</param>
/// <param name="NoResumeOnMeteredCount">Active downloads the Host keeps running on metered data because stopping them would start
/// them over (<see cref="JobDto.RunningOnMeteredWithoutResume"/>), for the "stopping starts over" warning.</param>
public sealed record ActivitySummary(
    bool KeepAlive, int ActiveCount, double BytesPerSecond, ActiveFile? First, long ActiveReceivedBytes, long? ActiveTotalBytes,
    bool WaitingForNetwork, int WaitingCount, int PausedCount, int NoResumeOnMeteredCount)
{
    public static readonly ActivitySummary Idle = new(false, 0, 0, null, 0, null, false, 0, 0, 0);
    public bool RemoteFilesActive { get; init; }
}

/// <summary>A download that moved into completed while observed.</summary>
public sealed record FinishedJob(Guid Id, string FileName, string FinalPath, long SizeBytes);

/// <summary>
/// Decides whether the Android keep-alive must hold the process, and what the ongoing notification says. Plain .NET, no Android types.
/// <para>
/// Input: direct method calls (<see cref="ApplySnapshot"/>, <see cref="OnJobChanged"/>, <see cref="SetNetworkWait"/>,
/// <see cref="SetQueues"/>), not
/// <c>IpcMessage</c> frames. The Android side runs in the Host's process and can hand over the service's own <c>JobChanged</c> arguments;
/// serialising a job into a frame only to parse it again would cost CPU and allocations on every progress tick for nothing. A caller that
/// has frames can unpack them in two lines.
/// </para>
/// <para>
/// Threading: every member may be called from any thread (Host threads); a lock guards the state and no lock is held while
/// <see cref="Changed"/> runs. <see cref="Changed"/> runs on the thread that caused it (a Host thread or the timer thread), may run on two
/// threads in quick succession, and carries no data: read <see cref="Summary"/> (always the latest). Events arriving before the first
/// snapshot are ignored (the snapshot already holds them: subscribe first, then take the snapshot).
/// </para>
/// <para>
/// Changed: at most one per interval (1 s) for ordinary changes, sent when the interval after the first unsent change ends.
/// A KeepAlive flip or a completion is raised at once. No timer is armed while nothing changes.
/// </para>
/// <para>
/// KeepAlive is true when something can transfer now: starting, connecting, downloading, verifying, retrywaiting (a download kept
/// running on metered data is one of these); any job with <c>QueueRetryAt</c> that is not paused, completed or cancelled (a failed job
/// its queue will try again); queued jobs unless their start mode is "later" (scheduled for later). A queued or retrying job whose queue
/// is known (<see cref="SetQueues"/>) and stopped does not count; an unknown queue does. The Runtime also looks at quotas, which a
/// <see cref="JobDto"/> does not show; when unsure, queued counts. False for paused, failed (without a retry time), completed, cancelled
/// and scheduled-for-later jobs.
/// </para>
/// <para>
/// While the Host holds downloads for a not-metered network, queued and queue-retry jobs do not keep the process alive: waiting is left
/// to the system's network constraint, and <see cref="ActivitySummary.WaitingForNetwork"/> and <c>WaitingCount</c> say how many would
/// run. Running jobs (including those flagged <see cref="JobDto.RunningOnMeteredWithoutResume"/>) still keep it alive.
/// </para>
/// </summary>
public sealed class ActivityState : IDisposable
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StaleWindow = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private readonly TimeSpan _interval;
    private readonly ITimer _timer;
    private readonly Dictionary<Guid, JobDto> _jobs = new();
    // Ids are never reused: a removed id's later events are stale (the Host announces progress outside its lock).
    private readonly HashSet<Guid> _removed = new();
    // Every id seen completed (in the snapshot or observed), so a completion is reported once.
    private readonly HashSet<Guid> _completed = new();
    private readonly List<FinishedJob> _finished = new();
    // Queue id to whether it runs. A queue not in here is unknown, and its jobs count.
    private readonly Dictionary<int, bool> _queueRuns = new();
    private ActivitySummary _summary = ActivitySummary.Idle;
    private bool _gotSnapshot;
    private bool _waiting;
    private bool _remoteFilesActive;
    private bool _armed;
    private bool _disposed;

    /// <param name="time">Clock and timer source; tests pass a fake.</param>
    /// <param name="interval">The coalescing interval; defaults to one second.</param>
    public ActivityState(TimeProvider? time = null, TimeSpan? interval = null)
    {
        _interval = interval ?? DefaultInterval;
        _timer = (time ?? TimeProvider.System).CreateTimer(_ => OnTimer(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>The latest summary.</summary>
    public ActivitySummary Summary
    {
        get { lock (_gate) return _summary; }
    }

    /// <summary>Something changed in <see cref="Summary"/> or a job finished; see the class remarks for when and on which thread.</summary>
    public event Action? Changed;

    public void SetRemoteFilesActive(bool active)
    {
        Action? raise;
        lock (_gate)
        {
            if (_disposed || _remoteFilesActive == active) return;
            _remoteFilesActive = active;
            raise = Recompute(forceImmediate: true);
        }
        raise?.Invoke();
    }

    /// <summary>
    /// Replaces everything known with the Host's snapshot. Jobs already completed in it are never reported as finished.
    /// <paramref name="queues"/> is <c>SnapshotDto.Settings.Queues</c>; null leaves the queues known so far.
    /// </summary>
    public void ApplySnapshot(IReadOnlyList<JobDto> jobs, HostStatusDto? status = null, IReadOnlyList<QueueDto>? queues = null)
    {
        Action? raise;
        lock (_gate)
        {
            if (_disposed) return;
            _gotSnapshot = true;
            _jobs.Clear();
            foreach (var j in jobs)
            {
                if (_removed.Contains(j.Id)) continue;
                _jobs[j.Id] = j;
                if (IsCompleted(j)) _completed.Add(j.Id);
            }

            _waiting = status?.WaitingForMeteredNetwork ?? false;
            if (queues is not null) StoreQueues(queues);
            raise = Recompute(forceImmediate: false);
        }

        raise?.Invoke();
    }

    /// <summary>One job event; <paramref name="changeType"/> is the event type ("added", "progress", "removed", ...).</summary>
    public void OnJobChanged(JobDto job, string changeType)
    {
        Action? raise;
        lock (_gate)
        {
            if (_disposed || !_gotSnapshot || _removed.Contains(job.Id)) return;
            var removal = changeType.Equals("removed", StringComparison.OrdinalIgnoreCase)
                || changeType.Equals("deleted", StringComparison.OrdinalIgnoreCase);
            var known = _jobs.TryGetValue(job.Id, out var old);
            var progress = changeType.Equals("progress", StringComparison.OrdinalIgnoreCase);
            // A progress frame older than what is known would undo a newer state; one for an unknown job outlived it.
            if (progress && (!known || (job.UpdatedAt < old!.UpdatedAt && old.UpdatedAt - job.UpdatedAt < StaleWindow))) return;

            var completion = false;
            if (removal)
            {
                _jobs.Remove(job.Id);
                _removed.Add(job.Id);
            }
            else
            {
                _jobs[job.Id] = job;
                // A file deleted on purpose after extraction has nothing to open, so it is not reported.
                if (IsCompleted(job) && _completed.Add(job.Id) && !job.FileRemovedAfterExtraction)
                {
                    _finished.Add(new FinishedJob(job.Id, job.FileName, job.FinalFilePath, job.TotalBytes ?? job.ReceivedBytes));
                    completion = true;
                }
            }

            raise = Recompute(completion);
        }

        raise?.Invoke();
    }

    /// <summary>The queues' run states (<c>QueueDto.IsRunning</c>), after a settings change or a queue start or stop. Replaces the known ones.</summary>
    public void SetQueues(IReadOnlyList<QueueDto> queues)
    {
        Action? raise;
        lock (_gate)
        {
            if (_disposed) return;
            StoreQueues(queues);
            raise = Recompute(forceImmediate: false);
        }

        raise?.Invoke();
    }

    private void StoreQueues(IReadOnlyList<QueueDto> queues)
    {
        _queueRuns.Clear();
        foreach (var q in queues) _queueRuns[q.Id] = q.IsRunning && q.ConditionWaitReason is null;
    }

    /// <summary>The <c>network.wait</c> push: downloads started or stopped waiting for a not-metered network.</summary>
    public void SetNetworkWait(bool waiting)
    {
        Action? raise;
        lock (_gate)
        {
            if (_disposed || _waiting == waiting) return;
            _waiting = waiting;
            raise = Recompute(forceImmediate: false);
        }

        raise?.Invoke();
    }

    /// <summary>The jobs that completed since the last call, oldest first, each reported once. Empty when none.</summary>
    public IReadOnlyList<FinishedJob> TakeFinished()
    {
        lock (_gate)
        {
            if (_finished.Count == 0) return Array.Empty<FinishedJob>();
            var list = _finished.ToArray();
            _finished.Clear();
            return list;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _armed = false;
            _timer.Dispose();
        }
    }

    private void OnTimer()
    {
        lock (_gate)
        {
            if (_disposed || !_armed) return;
            _armed = false;
        }

        Changed?.Invoke();
    }

    // Under the gate. Returns the notification to run outside it, or null.
    private Action? Recompute(bool forceImmediate)
    {
        var next = Build();
        var flipped = next.KeepAlive != _summary.KeepAlive;
        var changed = next != _summary;
        _summary = next;
        if (!changed && !forceImmediate) return null;

        if (flipped || forceImmediate)
        {
            // Raised now; a change waiting for the timer is part of this notification.
            if (_armed)
            {
                _armed = false;
                _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }

            return Changed is { } h ? () => h() : null;
        }

        if (!_armed)
        {
            _armed = true;
            _timer.Change(_interval, Timeout.InfiniteTimeSpan);
        }

        return null;
    }

    private static bool Is(JobDto j, string state) => string.Equals(j.State, state, StringComparison.OrdinalIgnoreCase);

    private static bool IsCompleted(JobDto j) => Is(j, "completed");

    private static bool IsActive(JobDto j) => Is(j, "starting") || Is(j, "connecting") || Is(j, "downloading") || Is(j, "verifying");

    private static bool IsLater(JobDto j) => string.Equals(j.StartMode, StartModes.Later, StringComparison.OrdinalIgnoreCase);

    private static bool Transferring(JobDto j) => IsActive(j) || Is(j, "retrywaiting");

    // Would run by itself, the metered rule aside.
    private bool Runnable(JobDto j)
    {
        if (Transferring(j)) return true;
        if (Is(j, "paused") || IsCompleted(j) || Is(j, "cancelled")) return false;
        // The Host starts and retries a queued job only in a running queue; one we know to be stopped holds it. A job
        // outside the queues (IsQueued false) always runs, whatever its QueueId says.
        if (j.IsQueued && _queueRuns.TryGetValue(j.QueueId, out var runs) && !runs) return false;
        if (j.QueueRetryAt is not null) return true;
        // Queued (and any state we do not know): runs by itself unless it was scheduled for later.
        return !Is(j, "failed") && !IsLater(j);
    }

    private ActivitySummary Build()
    {
        var keep = false;
        var active = 0;
        double rate = 0;
        long received = 0, total = 0;
        var totalKnown = true;
        var paused = 0;
        var waiting = 0;
        var noResume = 0;
        JobDto? first = null;
        foreach (var j in _jobs.Values)
        {
            if (Runnable(j))
            {
                // Held for the network: what is not transferring already waits for the system, not for us.
                if (Transferring(j) || !_waiting) keep = true;
                else waiting++;
            }

            if (Is(j, "paused")) paused++;
            // Waiting to retry still uses mobile data when it retries, so the warning stays.
            if (j.RunningOnMeteredWithoutResume && Transferring(j)) noResume++;
            if (!IsActive(j)) continue;
            active++;
            rate += j.BytesPerSecond ?? 0;
            received += j.ReceivedBytes;
            if (j.TotalBytes is > 0) total += j.TotalBytes.Value; else totalKnown = false;
            if (first is null || j.CreatedAt < first.CreatedAt || (j.CreatedAt == first.CreatedAt && j.Id.CompareTo(first.Id) < 0)) first = j;
        }

        ActiveFile? file = null;
        if (first is not null)
        {
            var left = first.EstimatedRemaining;
            if (left is null && first.BytesPerSecond is > 0 && first.TotalBytes is > 0 && first.TotalBytes > first.ReceivedBytes)
                left = TimeSpan.FromSeconds((first.TotalBytes.Value - first.ReceivedBytes) / first.BytesPerSecond.Value);
            file = new ActiveFile(first.Id, string.IsNullOrEmpty(first.FileName) ? first.Url : first.FileName,
                first.ReceivedBytes, first.TotalBytes, left);
        }

        return new ActivitySummary(keep || _remoteFilesActive, active, rate, file, received, active > 0 && totalKnown ? total : null,
            _waiting && waiting > 0, _waiting ? waiting : 0, paused, noResume) { RemoteFilesActive = _remoteFilesActive };
    }
}
