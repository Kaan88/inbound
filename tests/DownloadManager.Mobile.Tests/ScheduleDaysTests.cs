using DownloadManager.Mobile;

namespace DownloadManager.Mobile.Tests;

public sealed class ScheduleDaysTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    public void Empty_host_value_means_all_seven_days(string? value)
        => Assert.Equal("Mon,Tue,Wed,Thu,Fri,Sat,Sun", ScheduleDays.Serialize(ScheduleDays.FromHost(value)));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    public void Empty_draft_means_no_days(string? value)
        => Assert.Empty(ScheduleDays.FromDraft(value));

    [Fact]
    public void Full_and_short_names_accept_semicolons_and_mixed_case()
    {
        var days = ScheduleDays.FromHost("mOnDay;TUESDAY;wed;THURSDAY;Fri;SATURDAY;sunDAY");

        Assert.Equal("Mon,Tue,Wed,Thu,Fri,Sat,Sun", ScheduleDays.Serialize(days));
    }

    [Fact]
    public void Serialization_is_canonical_monday_to_sunday_and_deduplicates()
    {
        var serialized = ScheduleDays.Serialize(["Sun", "Monday", "FRI", "Tuesday", "wed", "Saturday", "THU", "MON"]);

        Assert.Equal("Mon,Tue,Wed,Thu,Fri,Sat,Sun", serialized);
    }

    [Fact]
    public void Empty_selection_serializes_to_an_empty_value()
        => Assert.Equal(string.Empty, ScheduleDays.Serialize([]));

    [Theory]
    [InlineData("Funday")]
    [InlineData("Mon,Funday")]
    public void Unknown_nonempty_host_days_are_invalid_instead_of_becoming_daily(string value)
        => Assert.Throws<FormatException>(() => ScheduleDays.FromHost(value));

    [Theory]
    [InlineData("Funday")]
    [InlineData("Monday,Someday")]
    public void Unknown_nonempty_draft_days_are_invalid_instead_of_becoming_daily(string value)
        => Assert.Throws<FormatException>(() => ScheduleDays.FromDraft(value));

    [Fact]
    public void Serialization_rejects_unknown_tokens()
        => Assert.Throws<FormatException>(() => ScheduleDays.Serialize(["Mon", "Funday"]));
}
