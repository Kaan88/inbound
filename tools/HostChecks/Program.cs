using System.IO.Pipes;
using System.Reflection;
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
            if (item.Text is "Open Download Manager" or "Pause All" or "Resume All" or "Exit" ||
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
}
