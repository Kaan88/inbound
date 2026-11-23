using DownloadManager.Mobile.Updates;

namespace DownloadManager.Mobile.Tests;

public class ReleaseNotesTextTests
{
    [Fact]
    public void A_bullet_and_its_space_come_off()
    {
        Assert.Equal("item", ReleaseNotesText.Item("- item"));
        Assert.Equal("x", ReleaseNotesText.Item("• x"));
        Assert.Equal("x", ReleaseNotesText.Item("*\tx"));
        Assert.Equal("x", ReleaseNotesText.Item("  •   x  "));
    }

    [Fact]
    public void A_minus_in_front_of_a_number_stays()
    {
        Assert.Equal("-5 % memory", ReleaseNotesText.Item("-5 % memory"));
        Assert.Equal("-5 % memory", ReleaseNotesText.Lines("-5 % memory").Single());
    }

    [Fact]
    public void A_line_of_nothing_but_markers_is_skipped()
    {
        Assert.Equal("", ReleaseNotesText.Item("---"));
        Assert.Empty(ReleaseNotesText.Lines("---"));
        Assert.Equal(new[] { "First", "Second" }, ReleaseNotesText.Lines("• First\n---\n\n• Second\n•   "));
    }
}
