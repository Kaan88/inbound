using DownloadManager.Core;

namespace DownloadManager.Runtime;

/// <summary>Live platform conditions gate queue work without changing the user's run or pause decisions.</summary>
public sealed partial class DownloadManagerService
{
    private bool _platformTransfersAllowed = true;
    private bool _queuePowerKnown;
    private bool _queueCharging;
    private bool _queueBatterySaver;

    // Unknown power is deliberately held until the platform reports it; persisted phone settings must never run unchecked.
    private string? QueueConditionWaitLocked(DownloadQueue queue)
        => !_platformTransfersAllowed ? "Waiting for Android to allow background work"
        : queue.RequireUnmeteredNetwork && _networkMetered ? "Waiting for Wi-Fi"
        : queue.RequireCharging && (!_queuePowerKnown || !_queueCharging) ? "Waiting for charging"
        : queue.RequireBatterySaverOff && (!_queuePowerKnown || _queueBatterySaver) ? "Waiting for battery saver to turn off"
        : null;

    private void RefreshQueueConditionsLocked()
    {
        foreach (var queue in _settings.Queues) queue.ConditionWaitReason = QueueConditionWaitLocked(queue);
    }

    /// <summary>Platform execution lease. Revoking it checkpoints running work without changing user pause/run intent.</summary>
    public async Task SetPlatformTransfersAllowedAsync(bool allowed, CancellationToken cancellationToken = default)
    {
        List<JobRuntime> stopped;
        lock (_sync)
        {
            if (_platformTransfersAllowed == allowed) return;
            _platformTransfersAllowed = allowed;
            if (!_initialized) return;
            RefreshQueueConditionsLocked();
            stopped = !allowed ? _jobs.Values.Where(r => (r.Starting || r.Runner is { IsCompleted: false }) &&
                !r.PauseRequested && !r.CancelRequested && !r.Job.IsTerminal && !IsFinishing(r)).ToList() : [];
            foreach (var runtime in stopped) PreemptLocked(runtime);
        }
        // The lease is already revoked and runners canceled. Persistence completes even if the platform's callback ends.
        foreach (var runtime in stopped)
        {
            await PersistAsync(runtime, CancellationToken.None).ConfigureAwait(false);
            Raise(runtime, "queued");
        }
        SettingsChanged?.Invoke(this, _settings);
        Schedule();
    }

    /// <summary>Report before initialization, then on power changes. No platform state is written to disk.</summary>
    public async Task SetQueuePowerStateAsync(bool charging, bool batterySaver, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            _queuePowerKnown = true;
            _queueCharging = charging;
            _queueBatterySaver = batterySaver;
            if (!_initialized) return;
        }
        await RequeueExcessAsync(cancellationToken).ConfigureAwait(false);
        // Subscribers re-read the latest state, so a late report cannot publish an older condition snapshot.
        SettingsChanged?.Invoke(this, _settings);
        Schedule();
    }
}
