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
    public void A_file_whose_archive_was_removed_offers_nothing_on_the_missing_file_and_says_why()
    {
        var plan = FileMenuRules.Plan(singleFile: true, package: false, sideRows: 0, removableSideRows: 0, fileRemoved: true);

        Assert.Equal("Open (archive removed after extraction)", FileMenuRules.Label("Open", plan.OpenReason));
        Assert.Equal("Open Folder (archive removed after extraction)", FileMenuRules.Label("Open Folder", plan.OpenReason));

        // It is not a broken job: what doesn't need the file stays on.
        Assert.Null(plan.FileReason);
        Assert.Null(plan.LocalReason);
        Assert.Null(plan.RemoveReason);
    }

    [Fact]
    public void Open_keeps_the_ordinary_reason_when_there_is_no_file_row_at_all()
    {
        Assert.Equal("select a file, not a package", FileMenuRules.Plan(false, true, 0, 0).OpenReason);
        Assert.Equal("select a download first", FileMenuRules.Plan(false, false, 0, 0).OpenReason);
        Assert.Null(FileMenuRules.Plan(true, false, 0, 0).OpenReason);
    }

    [Fact]
    public void A_package_row_alone_is_not_offered_as_something_to_group()
    {
        Assert.False(FileMenuRules.CanGroup(files: 3, localRows: 1));
        Assert.False(FileMenuRules.CanGroup(files: 1, localRows: 1));
        Assert.True(FileMenuRules.CanGroup(files: 2, localRows: 2));
        Assert.True(FileMenuRules.CanGroup(files: 4, localRows: 2));
    }

    [Fact]
    public void The_Hosts_note_goes_ahead_of_the_rows_own_description()
    {
        var job = new DownloadManager.Contracts.JobDto { State = "completed", FileRemovedAfterExtraction = true, StatusNote = "Archive removed after extraction" };
        var note = JobNotes.Note(job);

        Assert.Equal("Archive removed after extraction", note);
        Assert.Equal("Archive removed after extraction", JobNotes.Description(note, null));
        Assert.Equal("Archive removed after extraction · my words", JobNotes.Description(note, "my words"));
        Assert.Equal("my words", JobNotes.Description(null, "my words"));
        Assert.Empty(JobNotes.Description(null, null));
        Assert.Equal("Archive removed after extraction, so there is no file to open.", JobNotes.NoFile(note));
        Assert.StartsWith("Its archive", JobNotes.NothingToDelete(1), StringComparison.Ordinal);
        Assert.StartsWith("Their archives", JobNotes.NothingToDelete(2), StringComparison.Ordinal);
    }

    [Fact]
    public void A_job_with_no_note_says_nothing_and_a_note_is_never_shown_with_an_address()
    {
        Assert.Null(JobNotes.Note(new DownloadManager.Contracts.JobDto { State = "completed" }));

        // The flag alone still says why, for a Host that left the words out.
        Assert.Equal("Archive removed after extraction", JobNotes.Note(new DownloadManager.Contracts.JobDto { FileRemovedAfterExtraction = true }));

        var withAddress = JobNotes.Note(new DownloadManager.Contracts.JobDto { StatusNote = "Moved from https://example.invalid/dl/abc?token=invented" });
        Assert.DoesNotContain("invented", withAddress);
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
