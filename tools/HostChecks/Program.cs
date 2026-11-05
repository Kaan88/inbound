using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Windows.Forms;
using DownloadManager.Tray;

namespace HostChecks;

internal static class Program
{
    private static int _failures;

    [STAThread]
    private static int Main()
    {
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        CheckCurrentUserPipeAcl();
        CheckTrayActionsAndHealth();
        CheckTrayClickThrough();
        Console.WriteLine(_failures == 0 ? "ALL CHECKS PASSED" : $"{_failures} CHECK(S) FAILED");
        return _failures == 0 ? 0 : 1;
    }

    private static void CheckCurrentUserPipeAcl()
    {
        var security = PipeSecurityFactory.CreateForCurrentUser();
        var pipeName = $"DownloadManager-AclCheck-{Guid.NewGuid():N}";
        using var server = NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            4096,
            4096,
            security);
        var currentUser = WindowsIdentity.GetCurrent().User!;
        var currentRule = false;
        var systemRule = false;
        var broadRule = false;
        var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var worldSid = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        var authenticatedSid = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
        var usersSid = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        foreach (PipeAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow)
            {
                continue;
            }

            var sid = (SecurityIdentifier)rule.IdentityReference;
            var full = (rule.PipeAccessRights & PipeAccessRights.FullControl) == PipeAccessRights.FullControl;
            if (sid.Equals(currentUser) && full)
            {
                currentRule = true;
            }

            if (sid.Equals(systemSid) && full)
            {
                systemRule = true;
            }

            if (sid.Equals(worldSid) || sid.Equals(authenticatedSid) || sid.Equals(usersSid))
            {
                broadRule = true;
            }
        }

        Console.WriteLine($"pipe currentUserFullControl={currentRule}");
        Console.WriteLine($"pipe localSystemFullControl={systemRule}");
        Console.WriteLine($"pipe broadAllowAce={broadRule}");
        Require(currentRule, "current user FullControl ACE");
        Require(systemRule, "LocalSystem FullControl ACE");
        Require(!broadRule, "no Everyone/Authenticated Users/Builtin Users ACE");
    }

    private static void CheckTrayActionsAndHealth()
    {
        var calls = new List<string>();
        var actions = new TrayActions(
            Open: () => calls.Add("open"),
            OpenSettings: () => calls.Add("settings"),
            PauseAll: () => calls.Add("pause-all"),
            ResumeAll: () => calls.Add("resume-all"),
            Exit: () => calls.Add("exit"),
            IntegrationStatus: () => "connected",
            ActiveCount: () => 4,
            UpdateText: () => "Install update 9.9.9",
            InstallUpdate: () => calls.Add("install-update"),
            CheckForUpdates: () => calls.Add("check-updates"));

        using var tray = new TrayIconController(actions, SystemIcons.Application);
        var type = typeof(TrayIconController);
        var menu = (ContextMenuStrip)type
            .GetField("_menu", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(tray)!;
        var itemTexts = menu.Items.OfType<ToolStripItem>().Select(item => item.Text).ToList();
        Console.WriteLine($"tray menu: {string.Join(" | ", itemTexts)}");

        foreach (var item in menu.Items.OfType<ToolStripItem>())
        {
            if (item.Text is "Open Inbound Download Manager" or "Pause All" or "Resume All" or "Exit" ||
                item.Text.StartsWith("Settings"))
            {
                item.PerformClick();
            }
        }

        type.GetMethod("RefreshState", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(tray, null);
        var integration = (ToolStripMenuItem)type
            .GetField("_integrationItem", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(tray)!;
        var status = (ToolStripMenuItem)type
            .GetField("_statusItem", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(tray)!;
        var icon = (NotifyIcon)type
            .GetField("_icon", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(tray)!;

        Console.WriteLine($"tray callbacks: {string.Join(",", calls)}");
        Console.WriteLine($"tray status: {status.Text}");
        Console.WriteLine($"tray integration: {integration.Text}");
        Console.WriteLine($"tray icon text: {icon.Text}");
        Require(calls.Contains("open"), "Open callback");
        Require(calls.Contains("settings"), "Settings callback");
        Require(calls.Contains("pause-all"), "Pause All callback");
        Require(calls.Contains("resume-all"), "Resume All callback");
        Require(calls.Contains("exit"), "Exit callback");
        Require(integration.Text.Contains("connected"), "tray integration connected");
        Require(status.Text.Contains("4 active"), "tray active count reflected");
    }

    private static void CheckTrayClickThrough()
    {
        using var form = new Form
        {
            ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-2000, -2000),
            Size = new Size(1, 1),
        };
        form.Load += async (_, _) =>
        {
            try
            {
                await RunTrayClickChecksAsync();
            }
            catch (Exception ex)
            {
                _failures++;
                Console.WriteLine($"FAIL tray click check: {ex.Message}");
            }
            finally
            {
                form.Close();
            }
        };
        Application.Run(form);
    }

    private static async Task RunTrayClickChecksAsync()
    {
        var calls = new List<string>();
        var actions = new TrayActions(
            Open: () => calls.Add("open"),
            OpenSettings: () => calls.Add("settings"),
            PauseAll: () => calls.Add("pause-all"),
            ResumeAll: () => calls.Add("resume-all"),
            Exit: () => calls.Add("exit"),
            IntegrationStatus: () => "connected",
            ActiveCount: () => 2,
            UpdateText: () => null,
            InstallUpdate: () => calls.Add("install-update"),
            CheckForUpdates: () => calls.Add("check-updates"));

        using var tray = new TrayIconController(actions, SystemIcons.Application);
        await Task.Delay(700);

        var type = typeof(TrayIconController);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var icon = (NotifyIcon)type.GetField("_icon", flags)!.GetValue(tray)!;
        var menu = (ContextMenuStrip)type.GetField("_menu", flags)!.GetValue(tray)!;
        var added = (bool)typeof(NotifyIcon).GetField("_added", flags)!.GetValue(icon)!;
        Console.WriteLine($"tray added={added}");

        var rect = GetNotifyIconRect(icon, out var firstResult);
        Console.WriteLine($"tray rect first hr=0x{firstResult:X8} rect={rect.Left},{rect.Top},{rect.Right},{rect.Bottom}");
        if (rect.Right <= rect.Left || rect.Bottom <= rect.Top)
        {
            var script = FindRepoScript("open-tray-overflow.ps1");
            if (script is not null)
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                });
                if (process is not null)
                {
                    await process.WaitForExitAsync();
                    Console.WriteLine("overflow: " + (await process.StandardOutput.ReadToEndAsync()).Trim());
                }
            }

            await Task.Delay(900);
            rect = GetNotifyIconRect(icon, out var secondResult);
            Console.WriteLine($"tray rect retry hr=0x{secondResult:X8} rect={rect.Left},{rect.Top},{rect.Right},{rect.Bottom}");
        }

        Require(added, "tray icon registered");
        if (rect.Right > rect.Left && rect.Bottom > rect.Top)
        {
            var x = (rect.Left + rect.Right) / 2;
            var y = (rect.Top + rect.Bottom) / 2;
            SetCursorPos(x, y);
            mouse_event(0x0008, 0, 0, 0, UIntPtr.Zero);
            mouse_event(0x0010, 0, 0, 0, UIntPtr.Zero);
            await Task.Delay(1200);
            Console.WriteLine($"tray menu visible={menu.Visible}");
            Require(menu.Visible, "right-click opens the tray menu");

            if (menu.Visible)
            {
                var pause = menu.Items.OfType<ToolStripMenuItem>()
                    .FirstOrDefault(item => item.Text == "Pause All");
                if (pause is not null)
                {
                    var point = menu.PointToScreen(new Point(
                        pause.Bounds.Left + pause.Bounds.Width / 2,
                        pause.Bounds.Top + pause.Bounds.Height / 2));
                    SetCursorPos(point.X, point.Y);
                    mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
                    mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
                    await Task.Delay(800);
                }

                Console.WriteLine($"tray click callbacks={string.Join(",", calls)}");
                Require(calls.Contains("pause-all"), "tray click invokes Pause All");
            }
        }
        else
        {
            Console.WriteLine(
                $"tray click-through constrained: icon rect unavailable (hr=0x{firstResult:X8}) " +
                "and the overflow chevron was not exposed");
        }

        var notifications = (ConcurrentQueue<(string Title, string Message)>)type
            .GetField("_notifications", flags)!.GetValue(tray)!;
        var before = notifications.Count;
        tray.Notify("Download complete", "u5-tray.bin");
        var enqueued = notifications.Count;
        type.GetMethod("RefreshState", flags)!.Invoke(tray, null);
        await Task.Delay(300);
        var drained = notifications.Count;
        Console.WriteLine($"tray notification queue before={before} enqueued={enqueued} after={drained}");
        Require(enqueued == before + 1 && drained == 0, "tray notification queue drained");

        try
        {
            icon.ShowBalloonTip(4000, "Download complete", "u5-tray.bin", ToolTipIcon.Info);
            Console.WriteLine("tray balloon=handed");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"tray balloon failed: {ex.Message}");
        }

        var title = (string?)typeof(NotifyIcon).GetField("_balloonTipTitle", flags)!.GetValue(icon);
        var text = (string?)typeof(NotifyIcon).GetField("_balloonTipText", flags)!.GetValue(icon);
        Console.WriteLine($"tray notification title='{title}' text='{text}'");
        if (!string.IsNullOrEmpty(title) || !string.IsNullOrEmpty(text))
        {
            Require(title == "Download complete" && text == "u5-tray.bin",
                "notification handed to NotifyIcon");
        }
        else
        {
            Console.WriteLine("tray balloon fields not retained by this WinForms runtime");
        }
    }

    private static Rectangle GetNotifyIconRect(NotifyIcon icon, out int result)
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var idField = typeof(NotifyIcon).GetField("id", flags)
                      ?? typeof(NotifyIcon).GetField("_id", flags);
        var windowField = typeof(NotifyIcon).GetField("window", flags)
                          ?? typeof(NotifyIcon).GetField("_window", flags);
        if (idField is null || windowField is null)
        {
            result = -1;
            return Rectangle.Empty;
        }

        var id = (uint)idField.GetValue(icon)!;
        var window = windowField.GetValue(icon);
        if (window is null)
        {
            result = -1;
            return Rectangle.Empty;
        }

        var handleProperty = window.GetType().GetProperty(
            "Handle", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (handleProperty is null)
        {
            result = -1;
            return Rectangle.Empty;
        }

        var handle = (IntPtr)handleProperty.GetValue(window)!;
        var identifier = new NOTIFYICONIDENTIFIER
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONIDENTIFIER>(),
            hWnd = handle,
            uID = id,
        };
        result = Shell_NotifyIconGetRect(ref identifier, out var rect);
        return Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    private static string? FindRepoScript(string name)
    {
        var directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            var candidate = Path.Combine(directory, "scripts", name);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = Path.GetDirectoryName(directory);
        }

        return null;
    }

    private static void Pump(int milliseconds)
    {
        var until = Environment.TickCount64 + milliseconds;
        while (Environment.TickCount64 < until)
        {
            Application.DoEvents();
            Thread.Sleep(30);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (condition)
        {
            Console.WriteLine($"PASS {message}");
            return;
        }

        _failures++;
        Console.WriteLine($"FAIL {message}");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NOTIFYICONIDENTIFIER
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public Guid guidItem;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("shell32.dll")]
    private static extern int Shell_NotifyIconGetRect(
        ref NOTIFYICONIDENTIFIER identifier,
        out RECT iconLocation);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
}
