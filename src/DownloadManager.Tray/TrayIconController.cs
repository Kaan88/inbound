using System.Collections.Concurrent;
using System.Drawing;
using System.Windows.Forms;

namespace DownloadManager.Tray;

public sealed record TrayActions(
    Action Open,
    Action OpenSettings,
    Action PauseAll,
    Action ResumeAll,
    Action Exit,
    Func<string> IntegrationStatus,
    Func<int> ActiveCount,
    Func<string?> UpdateText,
    Action InstallUpdate,
    Action CheckForUpdates,
    Func<int>? SiteTransferCount = null);

/// <summary>The tray's words for what runs: downloads, and site transfers (SITES.4).</summary>
public static class TrayText
{
    /// <summary>The menu's status line.</summary>
    public static string Status(int downloads, int siteTransfers)
    {
        var parts = new List<string>();
        if (downloads > 0) parts.Add($"{downloads} active download{(downloads == 1 ? "" : "s")}");
        if (siteTransfers > 0) parts.Add($"{siteTransfers} site transfer{(siteTransfers == 1 ? "" : "s")}");
        return parts.Count == 0 ? "Idle" : string.Join(", ", parts);
    }

    /// <summary>The icon's tooltip (Windows keeps at most 127 characters).</summary>
    public static string Tooltip(int downloads, int siteTransfers)
    {
        var parts = new List<string>();
        if (downloads > 0) parts.Add($"{downloads} active");
        if (siteTransfers > 0) parts.Add($"{siteTransfers} site transfer{(siteTransfers == 1 ? "" : "s")}");
        return parts.Count == 0 ? "Inbound Download Manager" : "Inbound — " + string.Join(", ", parts);
    }
}

public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly TrayIconController _tray;

    public TrayApplicationContext(TrayActions actions, Icon? icon = null)
    {
        _tray = new TrayIconController(actions, icon);
    }

    public void Notify(string title, string message) => _tray.Notify(title, message);

    /// <summary>Something the tray shows may have changed. Safe from any thread.</summary>
    public void RequestRefresh() => _tray.RequestRefresh();

    public void ExitNow()
    {
        _tray.Dispose();
        ExitThread();
    }
}

public sealed class TrayIconController : IDisposable
{
    private readonly TrayActions _actions;
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _integrationItem;
    private readonly ToolStripMenuItem _pauseItem;
    private readonly ToolStripMenuItem _resumeItem;
    private readonly ToolStripMenuItem _updateItem;
    /// <summary>
    /// One-shot, started only when something changed, so an idle tray wakes for nothing. Several changes inside
    /// its interval make one refresh, which keeps it to about one a second during downloads.
    /// </summary>
    private readonly System.Windows.Forms.Timer _timer;
    private readonly SynchronizationContext _ui;
    private readonly ConcurrentQueue<(string Title, string Message)> _notifications = new();
    private int _refreshPending;
    private bool _disposed;

    public TrayIconController(TrayActions actions, Icon? icon)
    {
        _actions = actions;
        _menu = new ContextMenuStrip();
        _statusItem = new ToolStripMenuItem("Inbound Download Manager") { Enabled = false };
        _menu.Items.Add(_statusItem);
        _menu.Items.Add(new ToolStripSeparator());

        var open = new ToolStripMenuItem("Open Inbound Download Manager", null, (_, _) => _actions.Open());
        open.Font = new Font(open.Font, FontStyle.Bold);
        _menu.Items.Add(open);

        _pauseItem = new ToolStripMenuItem("Stop All", null, (_, _) => _actions.PauseAll());
        _resumeItem = new ToolStripMenuItem("Resume All", null, (_, _) => _actions.ResumeAll());
        _menu.Items.Add(_pauseItem);
        _menu.Items.Add(_resumeItem);
        _updateItem = new ToolStripMenuItem("Update Available", null,
            (_, _) => _actions.InstallUpdate()) { Visible = false };
        _menu.Items.Add(_updateItem);
        _menu.Items.Add(new ToolStripMenuItem("Check for Updates…", null,
            (_, _) => _actions.CheckForUpdates()));
        _menu.Items.Add(new ToolStripSeparator());

        _integrationItem = new ToolStripMenuItem("Browser integration: checking…") { Enabled = false };
        _menu.Items.Add(_integrationItem);
        _menu.Items.Add(new ToolStripMenuItem("Options…", null, (_, _) => _actions.OpenSettings()));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => _actions.Exit()));

        _icon = new NotifyIcon
        {
            Icon = icon ?? SystemIcons.Application,
            Text = "Inbound Download Manager",
            Visible = true,
            ContextMenuStrip = _menu,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) _actions.Open();
        };
        _icon.BalloonTipClicked += (_, _) => _actions.Open();

        // Created on the tray's own thread, so posts run there.
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            Interlocked.Exchange(ref _refreshPending, 0);
            RefreshState();
        };
        // The integration line reads the registry and a file, so only when someone looks at the menu.
        _menu.Opening += (_, _) =>
        {
            RefreshState();
            RefreshIntegration();
        };
        RefreshState();
    }

    public void Notify(string title, string message)
    {
        _notifications.Enqueue((title, message));
        RequestRefresh();
    }

    public void RequestRefresh()
    {
        if (_disposed || Interlocked.Exchange(ref _refreshPending, 1) == 1) return;
        _ui.Post(_ =>
        {
            if (!_disposed) _timer.Start();
        }, null);
    }

    private void RefreshIntegration()
    {
        try
        {
            _integrationItem.Text = $"Browser integration: {_actions.IntegrationStatus()}";
        }
        catch
        {
            // The tray must survive transient host errors.
        }
    }

    private void RefreshState()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            var active = _actions.ActiveCount();
            var sites = _actions.SiteTransferCount?.Invoke() ?? 0;
            _statusItem.Text = TrayText.Status(active, sites);
            _icon.Text = TrayText.Tooltip(active, sites);
            var updateText = _actions.UpdateText();
            _updateItem.Visible = !string.IsNullOrWhiteSpace(updateText);
            _updateItem.Text = updateText ?? "Update Available";
        }
        catch
        {
            // The tray must survive transient host errors.
        }

        while (_notifications.TryDequeue(out var pending))
        {
            try
            {
                _icon.ShowBalloonTip(4000, pending.Title, pending.Message, ToolTipIcon.Info);
            }
            catch
            {
                // notifications are best effort
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _timer.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }
}
