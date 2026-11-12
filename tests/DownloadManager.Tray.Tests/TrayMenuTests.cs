using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using DownloadManager.Tray;

namespace DownloadManager.Tray.Tests;

/// <summary>
/// The tray menu calls the right action for each item and shows the active count and the integration line (the
/// in-process part of tools/HostChecks, which is gone). The right-click on the real tray icon is not covered here.
/// </summary>
[Trait("Platform", "Windows")]
public sealed class TrayMenuTests
{
    [Fact]
    public void EachMenuItemCallsItsActionAndTheMenuShowsTheState()
    {
        RunOnSta(() =>
        {
            var calls = new List<string>();
            var active = 4;
            var actions = new TrayActions(
                Open: () => calls.Add("open"),
                OpenSettings: () => calls.Add("settings"),
                PauseAll: () => calls.Add("pause-all"),
                ResumeAll: () => calls.Add("resume-all"),
                Exit: () => calls.Add("exit"),
                IntegrationStatus: () => "connected",
                ActiveCount: () => active,
                UpdateText: () => "Install update 9.9.9",
                InstallUpdate: () => calls.Add("install-update"),
                CheckForUpdates: () => calls.Add("check-updates"));

            using var tray = new TrayIconController(actions, SystemIcons.Application);
            var menu = Field<ContextMenuStrip>(tray, "_menu");
            var items = menu.Items.OfType<ToolStripMenuItem>().ToDictionary(item => item.Text ?? "");

            var expected = new Dictionary<string, string>
            {
                ["Open Inbound Download Manager"] = "open",
                ["Stop All"] = "pause-all",
                ["Resume All"] = "resume-all",
                ["Install update 9.9.9"] = "install-update",
                ["Check for Updates…"] = "check-updates",
                ["Options…"] = "settings",
                ["Exit"] = "exit",
            };
            foreach (var (text, call) in expected)
            {
                Assert.True(items.TryGetValue(text, out var item), $"no tray menu item \"{text}\"; items: {string.Join(" | ", items.Keys)}");
                calls.Clear();
                item!.PerformClick();
                Assert.Equal([call], calls);
            }

            Assert.Equal("4 active downloads", Field<ToolStripMenuItem>(tray, "_statusItem").Text);
            Assert.Equal("Inbound — 4 active", Field<NotifyIcon>(tray, "_icon").Text);
            Assert.True(Field<ToolStripMenuItem>(tray, "_updateItem").Available, "the update item is hidden although an update is offered");

            active = 1;
            Invoke(tray, "RefreshState");
            Assert.Equal("1 active download", Field<ToolStripMenuItem>(tray, "_statusItem").Text);
            active = 0;
            Invoke(tray, "RefreshState");
            Assert.Equal("Idle", Field<ToolStripMenuItem>(tray, "_statusItem").Text);
            Assert.Equal("Inbound Download Manager", Field<NotifyIcon>(tray, "_icon").Text);

            Invoke(tray, "RefreshIntegration");
            Assert.Equal("Browser integration: connected", Field<ToolStripMenuItem>(tray, "_integrationItem").Text);
        });
    }

    [Fact]
    public void AHostErrorLeavesTheMenuWorking()
    {
        RunOnSta(() =>
        {
            var opened = 0;
            var actions = new TrayActions(
                Open: () => opened++,
                OpenSettings: () => { },
                PauseAll: () => { },
                ResumeAll: () => { },
                Exit: () => { },
                IntegrationStatus: () => throw new IOException("pipe gone"),
                ActiveCount: () => throw new IOException("pipe gone"),
                UpdateText: () => null,
                InstallUpdate: () => { },
                CheckForUpdates: () => { });

            using var tray = new TrayIconController(actions, SystemIcons.Application);
            Invoke(tray, "RefreshState");
            Invoke(tray, "RefreshIntegration");
            var menu = Field<ContextMenuStrip>(tray, "_menu");
            menu.Items.OfType<ToolStripMenuItem>().Single(item => item.Text == "Open Inbound Download Manager").PerformClick();
            Assert.Equal(1, opened);
            Assert.Equal("Browser integration: checking…", Field<ToolStripMenuItem>(tray, "_integrationItem").Text);
        });
    }

    private static T Field<T>(TrayIconController tray, string name) =>
        (T)typeof(TrayIconController).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tray)!;

    private static void Invoke(TrayIconController tray, string name) =>
        typeof(TrayIconController).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(tray, null);

    /// <summary>WinForms controls want a single-threaded apartment; xUnit's threads are not.</summary>
    private static void RunOnSta(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                body();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "the tray check did not finish in 30 seconds");
        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
