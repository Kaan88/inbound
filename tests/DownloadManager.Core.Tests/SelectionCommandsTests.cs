using System.Globalization;
using System.Xml.Linq;
using DownloadManager.Desktop.WinUI;

namespace DownloadManager.Core.Tests;

public sealed class SelectionCommandsTests
{
    [Fact]
    public void One_file_turns_every_command_on()
    {
        var plan = FileMenuRules.Plan(singleFile: true, package: false, sideRows: 0, removableSideRows: 0);
        Assert.Equal(new FileMenuPlan(null, null, null), plan);
    }

    [Fact]
    public void A_package_has_no_single_file_to_open_but_can_be_stopped_and_removed()
    {
        var plan = FileMenuRules.Plan(singleFile: false, package: true, sideRows: 0, removableSideRows: 0);
        Assert.NotNull(plan.FileReason);
        Assert.Null(plan.LocalReason);
        Assert.Null(plan.RemoveReason);
    }

    [Fact]
    public void Side_rows_alone_can_only_be_removed()
    {
        var plan = FileMenuRules.Plan(singleFile: false, package: false, sideRows: 2, removableSideRows: 2);
        Assert.Equal("no local file selected", plan.FileReason);
        Assert.Equal("no local file selected", plan.LocalReason);
        Assert.Null(plan.RemoveReason);
    }

    [Fact]
    public void Archives_still_extracting_alone_say_what_to_do_first()
    {
        var plan = FileMenuRules.Plan(singleFile: false, package: false, sideRows: 1, removableSideRows: 0);
        Assert.Equal("cancel the extraction first", plan.RemoveReason);
    }

    [Fact]
    public void Nothing_selected_asks_for_a_selection()
    {
        var plan = FileMenuRules.Plan(singleFile: false, package: false, sideRows: 0, removableSideRows: 0);
        Assert.Equal("select a download first", plan.FileReason);
        Assert.Equal("select a download first", plan.LocalReason);
        Assert.Equal("select a download first", plan.RemoveReason);
    }

    [Fact]
    public void A_label_carries_its_reason_the_way_Extract_does()
    {
        Assert.Equal("Open", FileMenuRules.Label("Open", null));
        Assert.Equal("Open (no local file selected)", FileMenuRules.Label("Open", "no local file selected"));
    }

    [Fact]
    public void Records_left_behind_are_named_only_when_there_are_any()
    {
        Assert.Null(ArchiveText.LeftExtracting(0));
        Assert.Contains("Cancel the extraction first", ArchiveText.LeftExtracting(1));
        Assert.StartsWith("2 archives", ArchiveText.LeftExtracting(2));
    }

    [Theory]
    [InlineData("connecting", null, true)]
    [InlineData("downloading", null, true)]
    [InlineData("downloading", 0L, true)]
    [InlineData("verifying", null, true)]
    [InlineData("downloading", 100L, false)]
    [InlineData("failed", null, false)]
    [InlineData("paused", null, false)]
    [InlineData("queued", null, false)]
    [InlineData("retryWaiting", null, false)]
    [InlineData("cancelled", null, false)]
    [InlineData("completed", null, false)]
    public void The_moving_bar_only_runs_while_bytes_of_unknown_total_are_being_worked_on(string state, long? total, bool expected)
        => Assert.Equal(expected, ProgressBarRules.ShowsMovingBar(state, total));

    /// <summary>Status text is 11px, so it needs 4.5:1. The selected fills are WinUI's own subtle fill over each surface, worked out by hand.</summary>
    [Theory]
    [InlineData("Dark", "#262626")]
    [InlineData("Light", "#F6F6F6")]
    public void Status_colors_stay_readable_on_the_list_in_each_theme(string theme, string selectedFill)
    {
        var tokens = ThemeTokens(theme);
        foreach (var key in new[] { "InboundStatusActiveBrush", "InboundStatusDoneBrush", "InboundStatusFailedBrush", "InboundStatusWaitingBrush", "InboundStatusNeutralBrush" })
        {
            Assert.True(Contrast(tokens[key], tokens["InboundSurfaceBrush"]) >= 4.5, $"{theme} {key} on the surface");
            Assert.True(Contrast(tokens[key], selectedFill) >= 4.5, $"{theme} {key} on a selected row");
        }
    }

    private static Dictionary<string, string> ThemeTokens(string theme)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !System.IO.File.Exists(Path.Combine(directory.FullName, "DownloadManager.slnx"))) directory = directory.Parent;
        Assert.NotNull(directory);
        var app = XDocument.Load(Path.Combine(directory.FullName, "src", "DownloadManager.Desktop.WinUI", "App.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var dictionary = app.Descendants().Single(element => element.Name.LocalName == "ResourceDictionary" && (string?)element.Attribute(x + "Key") == theme);
        return dictionary.Elements()
            .Where(element => element.Name.LocalName == "SolidColorBrush")
            .ToDictionary(element => (string)element.Attribute(x + "Key")!, element => (string)element.Attribute("Color")!);
    }

    private static double Contrast(string first, string second)
    {
        var (a, b) = (Luminance(first), Luminance(second));
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static double Luminance(string color)
    {
        static double Channel(string hex)
        {
            var value = int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255d;
            return value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(color[1..3])) + (0.7152 * Channel(color[3..5])) + (0.0722 * Channel(color[5..7]));
    }
}
