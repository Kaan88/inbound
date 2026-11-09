using System.Xml.Linq;
using DownloadManager.Desktop.WinUI;

namespace DownloadManager.Core.Tests;

/// <summary>The desktop window findings of the 2026-11-09 usability test (U-WL-n) whose logic can run without WinUI.</summary>
public sealed class WindowLocalFindingsTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void U_WL_6_the_package_button_comes_after_every_other_toolbar_button()
    {
        var toolbar = XDocument.Load(WinUiFile("MainWindow.xaml")).Descendants(Xaml + "StackPanel")
            .First(panel => (string?)panel.Attribute(X + "Name") == "ToolbarPanel");
        var buttons = toolbar.Descendants(Xaml + "Button").Select(button => (string?)button.Attribute(X + "Name")).ToList();
        Assert.Equal("CancelPackageButton", buttons[^1]);
        Assert.Equal("StopQueueButton", buttons[^2]);

        // What is shown and hidden is the group, its separator included, so nothing else moves.
        var group = toolbar.Descendants(Xaml + "StackPanel").First(panel => (string?)panel.Attribute(X + "Name") == "CancelPackageGroup");
        Assert.Equal("Collapsed", (string?)group.Attribute("Visibility"));
        Assert.Null(toolbar.Descendants(Xaml + "Button").First(button => (string?)button.Attribute(X + "Name") == "CancelPackageButton").Attribute("Visibility"));
    }

    [Fact]
    public void U_WL_8_a_filtered_list_says_what_it_shows()
    {
        Assert.Null(ShellWords.FindFilter(null, 3));
        Assert.Null(ShellWords.FindFilter("  ", 3));
        Assert.Equal("Showing 2 downloads that match \u201cvideo\u201d.", ShellWords.FindFilter(" video ", 2));
        Assert.Equal("Showing 1 download that match \u201cvideo\u201d.", ShellWords.FindFilter("video", 1));
        Assert.Equal("No downloads match \u201cvideo\u201d.", ShellWords.FindFilter("video", 0));
        Assert.Equal("2 shown by Find", ShellWords.FindCount("video", 2));
        Assert.Null(ShellWords.FindCount(string.Empty, 2));
    }

    [Theory]
    [InlineData("on", "browser integration on", false)]
    [InlineData("on; Chrome registration is broken", "browser integration on; Chrome registration is broken", true)]
    [InlineData("on; not registered for Chrome", "browser integration on; not registered for Chrome", true)]
    [InlineData("native host not registered", "browser integration not registered", true)]
    [InlineData("disabled", "browser integration disabled", false)]
    public void U_WL_9_the_status_bar_says_integration_and_offers_repair_only_for_a_registration(string status, string words, bool repair)
    {
        Assert.Equal(words, ShellWords.BrowserIntegration(status));
        Assert.Equal(repair, ShellWords.IntegrationNeedsRepair(status));
    }

    [Fact]
    public void U_WL_9_an_unknown_status_says_nothing()
    {
        Assert.Null(ShellWords.BrowserIntegration("unknown"));
        Assert.Null(ShellWords.BrowserIntegration(null));
        Assert.False(ShellWords.IntegrationNeedsRepair(null));
    }

    [Fact]
    public void U_WL_9_repair_never_runs_for_a_test_or_portable_instance()
    {
        var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\Inbound\register-native-host.ps1",
            @"C:\Inbound\app\DownloadManager.NativeHost.exe",
        };

        Assert.NotNull(ShellWords.RepairPlan(@"C:\Inbound\app\", @"D:\test-data", null, installed.Contains).Refusal);
        Assert.NotNull(ShellWords.RepairPlan(@"C:\Inbound\app\", null, "uiqa", installed.Contains).Refusal);

        var plan = ShellWords.RepairPlan(@"C:\Inbound\app\", null, null, installed.Contains);
        Assert.Null(plan.Refusal);
        Assert.Equal(@"C:\Inbound\register-native-host.ps1", plan.Script);
        Assert.Equal(@"C:\Inbound\app", plan.AppDirectory);
    }

    [Fact]
    public void U_WL_9_a_build_that_was_not_installed_is_told_to_run_setup()
    {
        var plan = ShellWords.RepairPlan(@"C:\src\artifacts\app\", null, null, _ => false);
        Assert.Null(plan.Script);
        Assert.Contains("Setup", plan.Refusal);
    }

    internal static string WinUiFile(string name) => Path.Combine(RepoRoot(), "src", "DownloadManager.Desktop.WinUI", name);

    internal static string RepoRoot()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "DownloadManager.slnx"))) folder = folder.Parent;
        return folder?.FullName ?? throw new InvalidOperationException("The repository wasn't found.");
    }
}
