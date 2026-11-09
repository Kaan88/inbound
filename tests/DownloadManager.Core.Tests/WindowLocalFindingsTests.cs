using System.Xml.Linq;
using DownloadManager.Contracts;
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

    [Fact]
    public void U_WL_10_before_the_firewall_step_the_page_shows_the_addresses_that_will_work()
    {
        var running = new WebUiStatusDto { State = WebUiStates.Running, LocalOnly = true, Port = 8080, AlsoReachableAs = ["nas", "NAS-PC"] };
        Assert.Equal(["http://nas-pc:8080/", "http://nas:8080/"], WebUiForm.AddressesOnceAllowed(running, "NAS-PC"));
        Assert.Empty(WebUiForm.AddressesOnceAllowed(new WebUiStatusDto { State = WebUiStates.Running, LocalOnly = false, Port = 8080 }, "PC"));
        Assert.Empty(WebUiForm.AddressesOnceAllowed(new WebUiStatusDto { State = WebUiStates.Off, LocalOnly = true, Port = 8080 }, "PC"));
    }

    [Fact]
    public void U_WL_10_the_password_comes_before_the_box_that_needs_it()
    {
        var source = File.ReadAllText(WinUiFile("WebUiOptionsPage.cs"));
        var controls = source[source.IndexOf("Controls =", StringComparison.Ordinal)..];
        Assert.True(controls.IndexOf("_newPassword,", StringComparison.Ordinal) < controls.IndexOf("_enable,", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("http://h/part[1-3].bin?size=1", new[] { "http://h/part1.bin?size=1", "http://h/part2.bin?size=1", "http://h/part3.bin?size=1" })]
    [InlineData("http://h/f[08-10].rar", new[] { "http://h/f08.rar", "http://h/f09.rar", "http://h/f10.rar" })]
    [InlineData("http://h/[a,b]/x[1-2]", new[] { "http://h/a/x1", "http://h/a/x2", "http://h/b/x1", "http://h/b/x2" })]
    [InlineData("http://h/[draft]/x[1-2]", new[] { "http://h/[draft]/x1", "http://h/[draft]/x2" })]
    [InlineData("http://[::1]:8080/file.bin", new[] { "http://[::1]:8080/file.bin" })]
    [InlineData("http://h/file[1-x].bin", new[] { "http://h/file[1-x].bin" })]
    [InlineData("http://h/plain.bin", new[] { "http://h/plain.bin" })]
    public void U_WL_12_a_numbered_address_stands_for_each_of_its_numbers(string link, string[] expected)
        => Assert.Equal(expected, LinkExtractor.ExpandNumbered(link));

    [Theory]
    [InlineData("http://h/x[final-v2]/p[1-2].zip", new[] { "http://h/x[final-v2]/p1.zip", "http://h/x[final-v2]/p2.zip" })]
    [InlineData("http://h/2024/x[2024-01]/p[1-2].zip", new[] { "http://h/2024/x[2024-01]/p1.zip", "http://h/2024/x[2024-01]/p2.zip" })]
    [InlineData("http://h/x[2024-01].zip", new[] { "http://h/x[2024-01].zip" })]
    [InlineData("http://h/x[final-v2].zip", new[] { "http://h/x[final-v2].zip" })]
    [InlineData("http://h/x[1-1000000].zip", new[] { "http://h/x[1-1000000].zip" })]
    [InlineData("http://h/r[2024-2025].pdf", new[] { "http://h/r2024.pdf", "http://h/r2025.pdf" })]
    [InlineData("http://h/f[a-c]", new[] { "http://h/fa", "http://h/fb", "http://h/fc" })]
    public void U_WL_12_only_a_plain_range_or_list_is_expanded_and_names_with_brackets_stay(string link, string[] expected)
        => Assert.Equal(expected, LinkExtractor.ExpandNumbered(link));

    [Theory]
    [InlineData("1-3", true)]
    [InlineData("08-10", true)]
    [InlineData("001-120", true)]
    [InlineData("2024-2025", true)]
    [InlineData("a-f", true)]
    [InlineData("A-F", true)]
    [InlineData("x,y", true)]
    [InlineData("2024-01", false)]
    [InlineData("01-2024", false)]
    [InlineData("final-v2", false)]
    [InlineData("a-F", false)]
    [InlineData("1-1500", false)]
    [InlineData("1234567-1234568", false)]
    [InlineData("a-b-c", false)]
    [InlineData("v1,final-v2", false)]
    [InlineData("draft", false)]
    public void U_WL_12_a_numbered_group_follows_the_cli_rules_with_a_sane_width(string token, bool numbered)
        => Assert.Equal(numbered, LinkExtractor.IsNumberedGroup(token));


    [Fact]
    public void U_WL_12_pasted_text_expands_numbered_links_in_both_add_dialogs()
    {
        const string text = "see http://127.0.0.1:47315/file/part[1-3].bin?size=100000 and http://h/end[1-2].";
        Assert.Equal(
            ["http://127.0.0.1:47315/file/part1.bin?size=100000", "http://127.0.0.1:47315/file/part2.bin?size=100000", "http://127.0.0.1:47315/file/part3.bin?size=100000", "http://h/end1", "http://h/end2"],
            LinkExtractor.HttpLinks(text));
        Assert.Equal(5, LinkExtractor.Inputs(text).Count);
    }

    [Theory]
    [InlineData("ftp://bad", true)]
    [InlineData("https://example.com/", true)]
    [InlineData("https://example.com/?id=4", false)]
    [InlineData("https://example.com/file.zip", false)]
    public void U_WL_23_an_address_that_names_no_file_waits_to_be_ticked(string link, bool noPath)
        => Assert.Equal(noPath, LinkExtractor.HasNoPath(link));

    [Fact]
    public void U_WL_23_an_ordinary_line_says_its_host_and_one_cut_short_says_so()
    {
        var good = new AddLine("https://files.example.com/a.zip") { Result = new RemoteClassifyResult { Ok = true, Kind = "ordinaryLink" } };
        Assert.Contains("From files.example.com.", RemoteText.AddLineWords(good, "TorBox").Detail);
        var cut = new AddLine("ftp://bad") { Result = new RemoteClassifyResult { Ok = true, Kind = "ordinaryLink" } };
        Assert.Contains(LinkExtractor.NoPathWords, RemoteText.AddLineWords(cut, "TorBox").Detail);
    }

    [Fact]
    public void U_WL_14_a_package_folder_is_the_one_its_files_share()
    {
        Assert.Equal(@"D:\dl\Sample", ShellWords.CommonFolder([@"D:\dl\Sample\other", @"D:\dl\Sample\sub", @"D:\dl\Sample"]));
        Assert.Equal(@"D:\dl\Sample", ShellWords.CommonFolder([@"D:\dl\Sample\", null, ""]));
        Assert.Equal(@"D:\", ShellWords.CommonFolder([@"D:\a", @"D:\b"]));
        Assert.Null(ShellWords.CommonFolder([@"D:\a", @"E:\b"]));
        Assert.Null(ShellWords.CommonFolder([]));
    }

    [Fact]
    public void U_WL_24_files_of_one_name_show_the_folder_they_are_in()
    {
        var names = ShellWords.PackageFileNames([(@"D:\dl\S\other", "video.bin"), (@"D:\dl\S\sub", "video.bin"), (@"D:\dl\S", "readme.txt")]);
        Assert.Equal([@"other\video.bin", @"sub\video.bin", "readme.txt"], names);
        Assert.Equal(["a.bin", "b.bin"], ShellWords.PackageFileNames([(@"D:\x", "a.bin"), (@"D:\y", "b.bin")]));
    }

    [Fact]
    public void U_WL_24_the_package_window_fits_a_768_pixel_screen()
        => Assert.Contains("WindowLayout.ResizeForDpi(this, 720, 680)", File.ReadAllText(WinUiFile("PackageWindow.cs")));

    [Theory]
    [InlineData("Network type", "Network type: Ethernet is Private.", "Network type: Ethernet is Private.")]
    [InlineData("Web UI state", "Off.", "Web UI state: Off.")]
    [InlineData("Web UI state", "", "Web UI state")]
    public void U_WL_18_a_status_line_says_its_name_once(string name, string text, string spoken)
        => Assert.Equal(spoken, ShellWords.SpokenStatus(name, text));

    [Fact]
    public void U_WL_22_the_container_import_says_what_it_takes_and_what_it_needs()
    {
        Assert.Equal("Import Link Container…", RemoteText.ContainerImportItem("TorBox"));
        Assert.Contains("needs a provider key", RemoteText.ContainerImportItem(null));
        Assert.Contains(".rsdf, .ccf or .dlc", RemoteText.ContainerImportHelp("TorBox"));
        Assert.DoesNotContain("JD Container", File.ReadAllText(WinUiFile("MainWindow.Remote.cs")));
    }

    [Fact]
    public void U_WL_11_a_my_provider_row_has_the_web_UIs_quick_actions()
    {
        var none = RemoteRules.ItemMenu(canDownload: true, job: null, "TorBox", acting: false, loading: false);
        Assert.Equal(["open", "downloadAll", null, "copyName", "refresh"], none.Select(entry => entry.Action));
        Assert.Equal("Download all", none[1].Label);
        Assert.False(none[1].Disabled);

        var job = new RemoteJobDto { Id = Guid.NewGuid(), ItemId = "42", State = "waiting", Provider = "torbox" };
        var known = RemoteRules.ItemMenu(canDownload: true, job, "TorBox", acting: true, loading: true);
        Assert.Equal("Download all its files", known.First(entry => entry.Action == "downloadAll").Label);
        Assert.True(known.First(entry => entry.Action == "downloadAll").Disabled);
        Assert.True(known.First(entry => entry.Action == "refresh").Disabled);
        Assert.Equal("Delete from TorBox…", known[^1].Label);
        Assert.True(RemoteRules.ItemMenu(canDownload: false, null, "TorBox", false, false)[1].Disabled);
    }

    [Fact]
    public void U_WL_11_the_row_menu_selects_its_row_and_download_says_the_window_closes()
    {
        var source = File.ReadAllText(WinUiFile("ProviderPanel.cs"));
        Assert.Contains("_list.ContextRequested += List_ContextRequested;", source);
        Assert.Contains("_list.SelectedItem = row;", source);
        Assert.Contains("then closes this window", source);
    }

    [Theory]
    [InlineData("failed", false, 0.0, "Failed — unreachable-file.bin")]
    [InlineData("paused", false, 42.0, "Paused — unreachable-file.bin")]
    [InlineData("downloading", false, 42.5, "42.5% — unreachable-file.bin")]
    [InlineData("downloading", false, null, "Downloading — unreachable-file.bin")]
    [InlineData("completed", true, 100.0, "Download complete — unreachable-file.bin")]
    public void U_WL_16_the_progress_title_says_the_state_unless_it_is_moving(string state, bool complete, double? percent, string title)
    {
        var words = state switch { "failed" => "Failed", "paused" => "Paused", _ => "Downloading" };
        Assert.Equal(title, ShellWords.ProgressTitle(state, complete, percent, "unreachable-file.bin", words));
    }

    [Fact]
    public void U_WL_15_one_verb_and_one_time_format_across_windows()
    {
        // UI.R29 direction B: the progress window's Stop is an icon and a label.
        var progress = File.ReadAllText(WinUiFile("DownloadWindow.xaml"));
        Assert.Contains("AutomationProperties.Name=\"Stop\"", progress);
        Assert.Contains("<TextBlock Text=\"Stop\" />", progress);
        Assert.Contains("\"Stop all\", \"InboundIconPauseBrush\"", File.ReadAllText(WinUiFile("PackageWindow.cs")));
        Assert.Contains("DurationText.TimeLeft(duration)", File.ReadAllText(WinUiFile("JobDetailsPanel.xaml.cs")));
        Assert.DoesNotContain("Automatic (from file type)", File.ReadAllText(WinUiFile("MainWindow.xaml.cs")));
    }

    [Fact]
    public void U_WL_25_saving_a_key_checks_it_in_the_same_press()
    {
        Assert.Equal("Key saved. TorBox accepted this key.", RemoteText.KeySavedAndChecked("TorBox accepted this key."));
        Assert.StartsWith("Key saved, but", RemoteText.KeySavedButNotChecked("TorBox refused this key."));
        var source = File.ReadAllText(WinUiFile("ProviderAccountPage.cs"));
        var save = source[source.IndexOf("private async Task SaveAsync()", StringComparison.Ordinal)..source.IndexOf("private Task ValidateAsync()", StringComparison.Ordinal)];
        Assert.Contains("_client.ValidateAsync(", save);
        Assert.DoesNotContain("Select Validate", source);
    }

    [Fact]
    public void U_WL_20_a_set_not_yet_extracted_says_so_in_the_extraction_column()
        => Assert.DoesNotContain("(\"Downloaded\"", File.ReadAllText(WinUiFile(Path.Combine("Remote", "ArchiveRows.cs"))));

    internal static string WinUiFile(string name) => Path.Combine(RepoRoot(), "src", "DownloadManager.Desktop.WinUI", name);

    internal static string RepoRoot()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "DownloadManager.slnx"))) folder = folder.Parent;
        return folder?.FullName ?? throw new InvalidOperationException("The repository wasn't found.");
    }
}
