using DownloadManager.Contracts;
using DownloadManager.Desktop.WinUI;

namespace DownloadManager.Core.Tests;

/// <summary>UI.B25 (menu separators), UI.R36 (arrow keys between a dialog's buttons) and UI.B24 (Explorer in front).</summary>
public sealed class MenuAndDialogKeyRulesTests
{
    // A menu written as text: 'i' a shown item, 'h' a hidden item, '-' a separator its rule shows, '_' one its rule hides.
    // The result is the shown entries: 'i' and '-'.
    private static string Collapse(string menu)
    {
        var isSeparator = menu.Select(c => c is '-' or '_').ToList();
        var wanted = menu.Select(c => c is 'i' or '-').ToList();
        var shown = MenuSeparatorRules.Visible(isSeparator, wanted);
        return string.Concat(menu.Select((c, i) => shown[i] ? (isSeparator[i] ? "-" : "i") : string.Empty));
    }

    [Theory]
    [InlineData("i-i", "i-i")]
    [InlineData("i--i", "i-i")]
    [InlineData("i-h-i", "i-i")]
    [InlineData("-i-i-", "i-i")]
    [InlineData("h-i", "i")]
    [InlineData("i-h", "i")]
    [InlineData("i_i", "ii")]
    [InlineData("i_-i", "i-i")]
    [InlineData("hh-hh", "")]
    [InlineData("", "")]
    [InlineData("i-hh-hh-i-i", "i-i-i")]
    public void Separators_never_double_lead_or_trail(string menu, string expected)
    {
        Assert.Equal(expected, Collapse(menu));
    }

    [Fact]
    public void A_torrent_row_menu_has_one_line_under_Open_Folder()
    {
        // The row menu as built, for a torrent: Open, Open With (hidden), Open Folder, Show Download (hidden),
        // Move/Rename (hidden), the Open group's line, the archive commands (all hidden for a torrent) and their line,
        // then Resume, Stop, Copy Magnet Link.
        var menu = "i" + "h" + "i" + "h" + "h" + "-" + "hhhhhhhh" + "-" + "iii";
        Assert.Equal("ii-iii", Collapse(menu));
    }

    [Fact]
    public void Items_keep_their_own_visibility()
    {
        var shown = MenuSeparatorRules.Visible([false, true, false], [false, true, true]);
        Assert.Equal([false, false, true], shown);
    }

    [Fact]
    public void Mismatched_lists_are_refused()
    {
        Assert.Throws<ArgumentException>(() => MenuSeparatorRules.Visible([true], []));
    }

    [Theory]
    // Cancel is the default and stands last; Left goes to Delete.
    [InlineData(2, 1, "Left", false, 0)]
    [InlineData(2, 0, "Right", false, 1)]
    [InlineData(2, 1, "Up", false, 0)]
    [InlineData(2, 0, "Down", false, 1)]
    // The delete-files box, then Delete, then Cancel.
    [InlineData(3, 2, "Left", false, 1)]
    [InlineData(3, 1, "Up", false, 0)]
    [InlineData(3, 0, "Down", false, 1)]
    // Right-to-left: the buttons stand the other way round.
    [InlineData(2, 1, "Right", true, 0)]
    [InlineData(2, 0, "Left", true, 1)]
    public void Arrows_move_between_buttons(int count, int current, string arrow, bool rightToLeft, int expected)
    {
        Assert.Equal(expected, DialogArrowRules.Target(count, current, Enum.Parse<DialogArrow>(arrow), rightToLeft));
    }

    [Theory]
    [InlineData(2, 0, "Left")]
    [InlineData(2, 1, "Right")]
    [InlineData(2, 0, "Up")]
    [InlineData(2, 1, "Down")]
    [InlineData(1, 0, "Right")]
    [InlineData(2, -1, "Right")]
    [InlineData(2, 2, "Left")]
    public void At_an_end_or_off_the_buttons_the_key_is_left_alone(int count, int current, string arrow)
    {
        Assert.Null(DialogArrowRules.Target(count, current, Enum.Parse<DialogArrow>(arrow)));
    }

    [Theory]
    [InlineData(2, 1, "Left")]
    [InlineData(2, 0, "Right")]
    [InlineData(3, 1, "Up")]
    [InlineData(3, 1, "Down")]
    public void An_arrow_with_a_modifier_is_left_alone(int count, int current, string arrow)
    {
        // Shift, Ctrl, Alt or Windows with the arrow: the focus stays, as in the web UI's dialogs.
        Assert.Null(DialogArrowRules.Target(count, current, Enum.Parse<DialogArrow>(arrow), modified: true));
        Assert.NotNull(DialogArrowRules.Target(count, current, Enum.Parse<DialogArrow>(arrow), modified: false));
    }

    [Fact]
    public void Footer_buttons_are_named_in_the_order_they_stand()
    {
        Assert.Equal(["PrimaryButton", "SecondaryButton", "CloseButton"], DialogArrowRules.FooterButtonNames);
    }

    [Fact]
    public void Every_dialog_gets_the_arrow_keys()
    {
        // Through DialogGuard, or right before a ShowAsync of its own.
        var winUi = FindWinUi();
        var guard = File.ReadAllText(Path.Combine(winUi, "DialogGuard.cs"));
        Assert.Contains("DialogArrowKeys.Add(dialog);", guard, StringComparison.Ordinal);

        var missing = new List<string>();
        var count = 0;
        foreach (var path in Directory.EnumerateFiles(winUi, "*.cs", SearchOption.AllDirectories))
        {
            if (path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") || path.EndsWith("DialogGuard.cs", StringComparison.Ordinal)) continue;
            var lines = File.ReadAllLines(path);
            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Contains("dialog.ShowAsync()", StringComparison.Ordinal)) continue;
                count++;
                if (i == 0 || !lines[i - 1].Contains("DialogArrowKeys.Add(dialog);", StringComparison.Ordinal)) missing.Add($"{Path.GetFileName(path)}:{i + 1}");
            }
        }

        Assert.True(count > 0);
        Assert.Empty(missing);
    }

    [Fact]
    public void The_row_menu_collapses_its_separators_after_its_rules()
    {
        var main = File.ReadAllText(Path.Combine(FindWinUi(), "MainWindow.xaml.cs"));
        var opening = main.IndexOf("private void JobsContextMenu_Opening", StringComparison.Ordinal);
        var collapse = main.IndexOf("MenuSeparators.Collapse(menu.Items);", opening, StringComparison.Ordinal);
        Assert.True(opening >= 0 && collapse > opening);
        // After the last rule that shows or hides items.
        Assert.True(collapse > main.IndexOf("ShowProviderContextItem();", opening, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(MessageTypes.JobReveal, true)]
    [InlineData(MessageTypes.HostPickFolder, true)]
    [InlineData(MessageTypes.HostOpenUi, true)]
    [InlineData(MessageTypes.JobsQuery, false)]
    [InlineData(MessageTypes.HandoffRequest, false)]
    [InlineData(null, false)]
    public void The_native_host_passes_the_foreground_on_only_for_a_window(string? type, bool shows)
    {
        Assert.Equal(shows, NativeBridgePolicy.ShowsWindow(type));
    }

    [Fact]
    public void Allowing_the_foreground_never_throws()
    {
        // In a test run nothing is in front for this process; the call then does nothing.
        ShellActions.AllowShellForeground();
    }

    [Fact]
    public void Explorer_and_files_are_started_so_they_may_come_to_the_front()
    {
        // Every place that opens Explorer or a file on Windows goes through ShellActions.Start (UI.B24).
        var src = Path.GetDirectoryName(FindWinUi())!;
        var offenders = new List<string>();
        foreach (var project in new[] { "DownloadManager.Desktop.WinUI", "DownloadManager.Host", "DownloadManager.Cli" })
        {
            foreach (var path in Directory.EnumerateFiles(Path.Combine(src, project), "*.cs", SearchOption.AllDirectories))
            {
                if (path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
                var text = File.ReadAllText(path);
                if (text.Contains("Process.Start(ShellActions.", StringComparison.Ordinal) ||
                    System.Text.RegularExpressions.Regex.IsMatch(text, @"FileName = (folder|directory), UseShellExecute = true"))
                    offenders.Add($"{project}/{Path.GetFileName(path)}");
            }
        }

        Assert.Empty(offenders);
        var native = File.ReadAllText(Path.Combine(src, "DownloadManager.NativeHost", "Program.cs"));
        Assert.Contains("if (NativeBridgePolicy.ShowsWindow(message.Type)) ShellActions.AllowShellForeground();", native, StringComparison.Ordinal);
        var host = File.ReadAllText(Path.Combine(src, "DownloadManager.Host", "HostController.cs"));
        Assert.Contains("picker.ShowDialog(owner)", host, StringComparison.Ordinal);
        Assert.Contains("TopMost = true", host, StringComparison.Ordinal);
    }

    private static string FindWinUi()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DownloadManager.slnx"))) directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, "src", "DownloadManager.Desktop.WinUI");
    }
}
