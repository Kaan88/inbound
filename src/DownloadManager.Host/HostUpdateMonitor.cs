using DownloadManager.Contracts;
using DownloadManager.Core;
using DownloadManager.Runtime;

namespace DownloadManager.Host;

internal sealed class HostUpdateMonitor : IDisposable
{
    private readonly DownloadManagerService _service;
    private readonly Action<string> _log;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly object _sync = new();
    private Action<string, string>? _notify;
    private UpdateCheckResult? _available;
    private string? _notifiedVersion;
    private bool _checking;
    private bool _autoCheck;
    private int _intervalHours = 24;

    public HostUpdateMonitor(DownloadManagerService service, Action<string> log)
    {
        _service = service;
        _log = log;
        _timer = new System.Windows.Forms.Timer();
        _timer.Tick += (_, _) => _ = CheckAsync(manual: false);
        _service.UpdateDeferred += (_, _) =>
        {
            lock (_sync)
            {
                _available = null;
            }
        };
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
        var wasEnabled = _autoCheck;
        _autoCheck = settings.AutoCheckUpdates;
        _intervalHours = Math.Clamp(settings.UpdateCheckIntervalHours, 1, 168);
        _timer.Interval = _intervalHours * 60 * 60 * 1000;
        if (_autoCheck && !_timer.Enabled)
        {
            _timer.Start();
        }
        else if (!_autoCheck && _timer.Enabled)
        {
            _timer.Stop();
        }

        if (_autoCheck && !wasEnabled)
        {
            _ = CheckAsync(manual: false);
        }
    }

    public void Start()
    {
        ApplySettings(_service.GetSnapshot().Settings);
        if (_autoCheck)
        {
            _ = CheckAsync(manual: false);
        }
    }

    public void CheckNow() => _ = CheckAsync(manual: true);

    public async Task InstallAsync()
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
            _notify?.Invoke("Update postponed", "Downloads are active; the update will not restart the Host yet.");
            _log("Automatic update install blocked by active downloads.");
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
        }

        _notify?.Invoke("Installing update", $"Version {result.Version} will be installed; the Host is restarting.");
    }

    public async Task CheckAsync(bool manual)
    {
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

            var skipped = _service.GetSnapshot().Settings.SkippedUpdateVersion;
            if (!manual &&
                string.Equals(check.AvailableVersion, skipped, StringComparison.OrdinalIgnoreCase))
            {
                lock (_sync)
                {
                    _available = null;
                }

                return;
            }

            lock (_sync)
            {
                _available = check;
                if (!manual &&
                    string.Equals(_notifiedVersion, check.AvailableVersion, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                _notifiedVersion = check.AvailableVersion;
            }

            _log($"Update {check.AvailableVersion} available.");
            _notify?.Invoke(
                "Update available",
                $"Download Manager {check.AvailableVersion} is ready. Use the tray or Help menu to install it.");
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
        _timer.Stop();
        _timer.Dispose();
    }
}
