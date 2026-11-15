using System.Diagnostics;

namespace DownloadManager.Host;

/// <summary>
/// The menu-bar icon on macOS, the tray's counterpart: a small native helper (<c>inbound-menu</c>, from
/// native/macos-menu, built into Inbound.app beside the Host) shows the icon and its menu. The Host is its only peer, over
/// the helper's stdin and stdout: a menu choice comes back as one line, and the tooltip (the active count) goes out as
/// "tooltip &lt;text&gt;". Optional, as the tray is on Linux: without the helper the Host runs as before. Disposing it
/// closes the helper's stdin, which ends it.
/// </summary>
internal sealed class MacMenuBar : IAsyncDisposable
{
    public const string HelperName = "inbound-menu";

    private readonly LinuxTrayActions _actions;
    private readonly Action<string> _log;
    private readonly Process _process;
    private readonly Timer _tooltipTimer;
    private readonly Task _reader;
    private readonly Lock _write = new();
    private string _tooltip = string.Empty;
    private int _tooltipPending;

    private MacMenuBar(Process process, LinuxTrayActions actions, Action<string> log)
    {
        _process = process;
        _actions = actions;
        _log = log;
        _tooltipTimer = new Timer(_ => SendTooltip(), null, Timeout.Infinite, Timeout.Infinite);
        _reader = Task.Run(ReadChoicesAsync);
        SendTooltip();
    }

    public static MacMenuBar? TryStart(LinuxTrayActions actions, Action<string> log)
    {
        var helper = Path.Combine(AppContext.BaseDirectory, HelperName);
        if (!File.Exists(helper))
        {
            log($"No menu-bar icon: {HelperName} is not beside the Host.");
            return null;
        }

        try
        {
            var start = new ProcessStartInfo(helper)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = false,
            };
            var process = Process.Start(start);
            if (process is null) return null;
            log("Menu-bar icon started.");
            return new MacMenuBar(process, actions, log);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            log($"No menu-bar icon: {ex.Message}");
            return null;
        }
    }

    /// <summary>The tooltip follows the active count; sent at most once a second, and only when it changed.</summary>
    public void RequestRefresh()
    {
        if (Interlocked.Exchange(ref _tooltipPending, 1) == 0) _tooltipTimer.Change(1000, Timeout.Infinite);
    }

    private async Task ReadChoicesAsync()
    {
        try
        {
            while (await _process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                Action? action = line.Trim() switch
                {
                    "open" => _actions.Open,
                    "settings" => _actions.OpenSettings,
                    "pause" => _actions.PauseAll,
                    "resume" => _actions.ResumeAll,
                    "exit" => _actions.Exit,
                    _ => null,
                };
                try
                {
                    action?.Invoke();
                }
                catch (Exception ex)
                {
                    _log($"Menu-bar choice '{line.Trim()}' failed: {ex.GetType().Name}");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The helper has gone.
        }
    }

    private void SendTooltip()
    {
        Interlocked.Exchange(ref _tooltipPending, 0);
        var active = _actions.ActiveCount();
        var text = active switch
        {
            0 => "No downloads running",
            1 => "1 download running",
            _ => $"{active} downloads running",
        };
        lock (_write)
        {
            if (text == _tooltip) return;
            _tooltip = text;
            try
            {
                _process.StandardInput.WriteLine("tooltip " + text);
                _process.StandardInput.Flush();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // The helper has gone; the Host runs on without its icon.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _tooltipTimer.DisposeAsync().ConfigureAwait(false);
        try
        {
            lock (_write) _process.StandardInput.Close(); // the helper ends when its input does
            if (!_process.WaitForExit(TimeSpan.FromSeconds(2))) _process.Kill();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }

        try { await _reader.ConfigureAwait(false); }
        catch (Exception) { /* the reader only ends */ }
        _process.Dispose();
    }
}
