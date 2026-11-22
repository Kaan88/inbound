using DownloadManager.Core;

namespace DownloadManager.Runtime;

/// <summary>
/// ANDROID.9 (owner, 2026-11-22): the phone's two torrent rules. <see cref="AppSettings.TorrentOnlyOnUnmeteredNetwork"/>:
/// on a metered network torrents wait in their queue ("Waiting for Wi-Fi") and a running one goes back to it; there is no
/// "Use data" lift for them. <see cref="AppSettings.TorrentSeedOnlyOnUnmeteredAndCharging"/>: a seeding torrent is taken
/// off the worker while the network is metered or the phone isn't charging, stays marked seeding with the reason, and is
/// put back by itself. Only a platform that reports the network and power (Android) is ever held.
/// </summary>
public sealed partial class DownloadManagerService
{
    public const string TorrentWaitingForWifi = "Waiting for Wi-Fi";
    public const string TorrentWaitingForCharging = "Waiting for charging";

    /// <summary>Seeding torrents held off the worker by the seeding rule (under <c>_sync</c>); still marked seeding.</summary>
    private readonly HashSet<Guid> _seedHeld = [];
    // Held torrents whose worker is being stopped right now (HoldSeedingAsync); guarded by _sync.
    private readonly HashSet<Guid> _seedHolding = [];

    /// <summary>Whether a torrent shows a WaitingFor note (under <c>_sync</c>), so the pass that clears it still runs.</summary>
    private bool _torrentWaitingShown;

    private bool TorrentNetworkHoldsLocked() => _settings.TorrentOnlyOnUnmeteredNetwork && _networkMetered;

    /// <summary>Why seeding waits now, or null. Unknown power holds it, as a queue that needs charging is held.</summary>
    private string? SeedHoldReasonLocked() =>
        !_settings.TorrentSeedOnlyOnUnmeteredAndCharging ? null
        : _networkMetered ? TorrentWaitingForWifi
        : _platformReportsQueuePower && (!_queuePowerKnown || !_queueCharging) ? TorrentWaitingForCharging
        : null;

    /// <summary>Whether the phone's conditions can matter now; elsewhere nothing is looked at.</summary>
    private bool TorrentConditionsMayApplyLocked() => _networkMetered || _platformReportsQueuePower || _seedHeld.Count > 0 || _torrentWaitingShown;

    /// <summary>
    /// Brings every torrent in line with the rules as they are now: says why a queued one waits, holds seeding that may not
    /// go on, and puts back what was held when the rule lets go. Called when the network, the power or the settings change,
    /// at start, and after each scheduling pass (a torrent queued since). Only a changed torrent is announced.
    /// </summary>
    private void ApplyTorrentConditions()
    {
        var changed = new List<JobRuntime>();
        var hold = new List<JobRuntime>();
        var resume = new List<JobRuntime>();
        lock (_sync)
        {
            if (!_initialized || _shuttingDown || !TorrentConditionsMayApplyLocked()) return;
            var networkHolds = TorrentNetworkHoldsLocked();
            var seedReason = SeedHoldReasonLocked();
            var shown = false;
            foreach (var runtime in _jobs.Values)
            {
                if (runtime.Job.Torrent is not { } info) continue;
                string? waiting = null;
                if (info.Seeding && runtime.Job.State == DownloadState.Completed)
                {
                    if (seedReason is not null)
                    {
                        waiting = seedReason;
                        if (_seedHeld.Add(runtime.Job.Id)) hold.Add(runtime);
                    }
                    else if (_seedHeld.Remove(runtime.Job.Id))
                    {
                        _seedPending.Add(runtime.Job.Id);
                        resume.Add(runtime);
                    }
                }
                else if (networkHolds && runtime.Job.State == DownloadState.Queued)
                {
                    waiting = TorrentWaitingForWifi;
                }

                shown |= waiting is not null;
                if (info.WaitingFor == waiting) continue;
                info.WaitingFor = waiting;
                runtime.Job.Touch();
                changed.Add(runtime);
            }

            _torrentWaitingShown = shown;
        }

        foreach (var runtime in changed) Raise(runtime, "updated");
        foreach (var runtime in hold) _ = HoldSeedingAsync(runtime);
        foreach (var runtime in resume) _ = BeginSeedingAsync(runtime, CancellationToken.None);
    }

    /// <summary>Takes a held torrent off the worker; it stays marked seeding. A stop or a resume since then wins.</summary>
    private async Task HoldSeedingAsync(JobRuntime runtime)
    {
        await _seedingGate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                if (!_seedHeld.Contains(runtime.Job.Id)) return;
                if (runtime.Job.Torrent is { } info)
                {
                    info.UploadRate = 0;
                    info.Peers = 0;
                    info.Seeds = 0;
                }

                _speedHistory.UploadStopped(runtime.Job.Id, DateTimeOffset.UtcNow);
            }

            if (_torrentEngine is { } engine && engine.IsSeeding(runtime.Job.Id))
            {
                // The hold may be lifted while the worker stops (charger in): its "stopped" must still not clear the
                // seeding mark, or the resume restarts the seeding-hours limit (Fable F6).
                lock (_sync) _seedHolding.Add(runtime.Job.Id);
                try
                {
                    await engine.StopSeedingAsync(runtime.Job.Id).ConfigureAwait(false);
                    _log?.Invoke($"Torrent job {runtime.Job.Id}: seeding waits for Wi-Fi and charging.");
                }
                catch (Exception ex)
                {
                    _log?.Invoke($"Torrent job {runtime.Job.Id}: holding its seeding failed: {ex.Message}");
                }
                finally
                {
                    lock (_sync) _seedHolding.Remove(runtime.Job.Id);
                }
            }
        }
        finally { _seedingGate.Release(); }

        Raise(runtime, "updated");
    }
}
