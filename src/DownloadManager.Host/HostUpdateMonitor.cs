using DownloadManager.Contracts;
using DownloadManager.Core;
using DownloadManager.Runtime;

namespace DownloadManager.Host;

internal sealed class HostUpdateMonitor : IDisposable
{
    private readonly DownloadManagerService _service;
    private readonly Action<string> _log;
    private readonly System.Threading.Timer _timer;
    private readonly System.Threading.Timer _retryTimer;
    private readonly object _sync = new();
    private readonly UpdatePostponement _postponement = new();
    private volatile bool _waitingForIdle;
    private Action<string, string>? _notify;
    private UpdateCheckResult? _available;
    private bool _checking;
    private volatile bool _autoCheck;
    private int _intervalHours = 24;
    private bool _disposed;

    public HostUpdateMonitor(DownloadManagerService service, Action<string> log)
    {
        _service = service;
        _log = log;
        _timer = new System.Threading.Timer(
            _ => _ = CheckAsync(), null, Timeout.Infinite, Timeout.Infinite);
        // M2: the retry installs the version already found; only the scheduled check fetches the manifest.
        _retryTimer = new System.Threading.Timer(
            _ => _ = RetryInstallAsync(), null, Timeout.Infinite, Timeout.Infinite);
        _service.UpdateDeferred += OnUpdateDeferred;
        _service.JobChanged += OnJobChanged;
    }

    private void OnUpdateDeferred(object? sender, string e)
    {
        lock (_sync)
        {
            _available = null;
            _waitingForIdle = false;
            _postponement.Reset();
            if (!_disposed) _retryTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    /// <summary>
    /// While an automatic install waits for downloads, the last one to stop brings the retry forward, at most once a
    /// minute. Progress events arrive often, so this returns at once unless an install is waiting.
    /// </summary>
    private void OnJobChanged(object? sender, JobChangeEventArgs e)
    {
        if (!_waitingForIdle) return;
        if (e.Job.State is { } state && (
                state.Equals("connecting", StringComparison.OrdinalIgnoreCase) ||
                state.Equals("downloading", StringComparison.OrdinalIgnoreCase) ||
                state.Equals("verifying", StringComparison.OrdinalIgnoreCase) ||
                state.Equals("retryWaiting", StringComparison.OrdinalIgnoreCase)))
            return;
        if (_service.ActiveDownloadCount() > 0) return;
        lock (_sync)
        {
            if (_disposed || !_waitingForIdle) return;
            if (!_postponement.TryEarlyRetry(DateTimeOffset.UtcNow)) return;
            _waitingForIdle = false;
            _retryTimer.Change(TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan);
        }
    }

    private async Task RetryInstallAsync()
    {
        lock (_sync)
        {
            if (_disposed || !_autoCheck || _available is null) return;
        }

        try
        {
            await InstallAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log($"Update install retry failed: {ex.Message}");
        }
    }

    public string? UpdateText
    {
        get
        {
            lock (_sync)
            {
                return _available is null ? null : $"Install update {_available.AvailableVersion}";
            }
        }
    }

    public void Attach(Action<string, string> notify) => _notify = notify;

    public void ApplySettings(SettingsDto settings)
    {
        bool startCheck;
        lock (_sync)
        {
            if (_disposed) return;
            var wasEnabled = _autoCheck;
            var oldHours = _intervalHours;
            _autoCheck = settings.AutoCheckUpdates && InstalledBuild.TryLoad() is not null;
            _intervalHours = Math.Clamp(settings.UpdateCheckIntervalHours, 1, 168);
            // Settings change for many reasons, such as adding a download; only re-arm when the schedule did.
            if (_autoCheck != wasEnabled || _intervalHours != oldHours)
            {
                var interval = _autoCheck ? TimeSpan.FromHours(_intervalHours) : Timeout.InfiniteTimeSpan;
                _timer.Change(interval, interval);
            }

            if (!_autoCheck)
            {
                _retryTimer.Change(Timeout.Infinite, Timeout.Infinite);
                _waitingForIdle = false;
            }
            startCheck = _autoCheck && !wasEnabled;
        }
        if (startCheck)
        {
            _ = CheckAsync();
        }
    }

    public void Start()
    {
        ReportLastInstall();
        _ = ReportLastInstallLaterAsync();
        ApplySettings(_service.GetSettings());
        if (_autoCheck)
        {
            _ = CheckAsync();
        }
    }

    /// <summary>
    /// The helper records success only once this Host has survived starting, which is after the
    /// first look. A rollback restarts the old Host with its result already written.
    /// </summary>
    private async Task ReportLastInstallLaterAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(12)).ConfigureAwait(false);
        lock (_sync)
        {
            if (_disposed) return;
        }

        ReportLastInstall();
    }

    private void ReportLastInstall()
    {
        var outcome = _service.TakeUpdateInstallOutcome();
        if (outcome is null) return;
        if (outcome.Succeeded)
        {
            _log($"Updated to {outcome.Version}.");
            _notify?.Invoke("Update installed", $"Inbound Download Manager {outcome.Version} is ready.");
            return;
        }

        _log($"Update to {outcome.Version} failed: {outcome.Error}");
        _notify?.Invoke("Update failed",
            $"Version {outcome.Version} could not be installed. You can try again from the tray or Help menu.");
    }

    private async Task InstallAsync()
    {
        UpdateCheckResult? check;
        lock (_sync)
        {
            check = _available;
        }

        if (check is null)
        {
            return;
        }

        var result = await _service.InstallUpdateAsync(new UpdateInstallRequest()).ConfigureAwait(false);
        if (result.Status == "busy")
        {
            bool retry;
            bool notify;
            lock (_sync)
            {
                retry = !_disposed && _autoCheck;
                // M2: one balloon per version.
                notify = _postponement.ShouldNotify(check.AvailableVersion);
                if (retry)
                {
                    _retryTimer.Change(_postponement.NextDelay(), Timeout.InfiniteTimeSpan);
                    // Only the downloads ending can clear a downloads refusal; other reasons wait for the delay.
                    _waitingForIdle = UpdatePostponement.WaitsForDownloads(result.Error);
                }
            }

            if (notify) _notify?.Invoke("Update postponed", UpdatePostponement.Message(result.Error, retry));
            _log($"Update install postponed: {result.Error}");
            return;
        }

        if (!result.Ok)
        {
            _notify?.Invoke("Update failed", result.Error ?? "The update could not be installed.");
            return;
        }

        lock (_sync)
        {
            _available = null;
            _waitingForIdle = false;
            _postponement.Reset();
        }

        _notify?.Invoke("Installing update", $"Version {result.Version} will be installed; Inbound's background program is restarting.");
        _service.RequestUpdateShutdown();
    }

    /// <summary>
    /// The automatic check, on the timer and at start. The tray's Check for Updates and Install update open the window's
    /// update dialog (UI.R40), which asks the Host itself, so nothing here is manual.
    /// </summary>
    private async Task CheckAsync()
    {
        if (!_autoCheck) return;
        lock (_sync)
        {
            if (_checking)
            {
                return;
            }

            _checking = true;
        }

        try
        {
            var check = await _service.CheckForUpdatesAsync().ConfigureAwait(false);
            if (!check.Ok || check.Status != "updateAvailable")
            {
                lock (_sync)
                {
                    _available = null;
                }

                return;
            }

            var skipped = _service.GetSettings().SkippedUpdateVersion;
            if (string.Equals(check.AvailableVersion, skipped, StringComparison.OrdinalIgnoreCase))
            {
                lock (_sync)
                {
                    _available = null;
                }

                return;
            }

            if (!_autoCheck) return;
            lock (_sync) _available = check;
            if (_service.UpdateBeingInstalled()) return;
            // The tray's Install update still offers it (it opens the window's update dialog); only the automatic retry waits.
            if (_service.UpdateFailedRecently(check.AvailableVersion ?? string.Empty))
            {
                _log($"Not retrying update {check.AvailableVersion} automatically yet; it failed recently.");
                return;
            }

            await InstallAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log($"Update check failed: {ex.Message}");
        }
        finally
        {
            lock (_sync)
            {
                _checking = false;
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _autoCheck = false;
            _waitingForIdle = false;
            _service.JobChanged -= OnJobChanged;
            _service.UpdateDeferred -= OnUpdateDeferred;
            _retryTimer.Dispose();
            _timer.Dispose();
        }
    }
}
