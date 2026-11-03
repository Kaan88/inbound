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
    Func<int> ActiveCount);

public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly TrayIconController _tray;

    public TrayApplicationContext(TrayActions actions, Icon? icon = null)
    {
        _tray = new TrayIconController(actions, icon);
    }

    public void Notify(string title, string message) => _tray.Notify(title, message);

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
    private readonly System.Windows.Forms.Timer _timer;
    private readonly ConcurrentQueue<(string Title, string Message)> _notifications = new();
    private bool _disposed;

    public TrayIconController(TrayActions actions, Icon? icon)
    {
        _actions = actions;
        _menu = new ContextMenuStrip();
        _statusItem = new ToolStripMenuItem("Download Manager") { Enabled = false };
        _menu.Items.Add(_statusItem);
        _menu.Items.Add(new ToolStripSeparator());

        var open = new ToolStripMenuItem("Open Download Manager", null, (_, _) => _actions.Open());
        open.Font = new Font(open.Font, FontStyle.Bold);
        _menu.Items.Add(open);

        _pauseItem = new ToolStripMenuItem("Pause All", null, (_, _) => _actions.PauseAll());
        _resumeItem = new ToolStripMenuItem("Resume All", null, (_, _) => _actions.ResumeAll());
        _menu.Items.Add(_pauseItem);
        _menu.Items.Add(_resumeItem);
        _menu.Items.Add(new ToolStripSeparator());

        _integrationItem = new ToolStripMenuItem("Browser integration: checking…") { Enabled = false };
        _menu.Items.Add(_integrationItem);
        _menu.Items.Add(new ToolStripMenuItem("Settings…", null, (_, _) => _actions.OpenSettings()));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => _actions.Exit()));

        _icon = new NotifyIcon
        {
            Icon = icon ?? SystemIcons.Application,
            Text = "Download Manager",
            Visible = true,
            ContextMenuStrip = _menu,
        };
        _icon.DoubleClick += (_, _) => _actions.Open();

        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, _) => RefreshState();
        _timer.Start();
        RefreshState();
    }

    public void Notify(string title, string message)
    {
        _notifications.Enqueue((title, message));
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
            _statusItem.Text = active == 0 ? "Idle" : $"{active} active download{(active == 1 ? "" : "s")}";
            _icon.Text = active == 0 ? "Download Manager" : $"Download Manager — {active} active";
            _integrationItem.Text = $"Browser integration: {_actions.IntegrationStatus()}";
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
