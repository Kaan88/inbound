using DownloadManager.Desktop.WinUI;

namespace DownloadManager.Core.Tests;

/// <summary>UI.R29 direction B: the progress window's tile figures.</summary>
public sealed class ProgressOverviewTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Tiles_show_the_number_with_its_unit_apart()
    {
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        try { TileFigures(); }
        finally { System.Globalization.CultureInfo.CurrentCulture = culture; }
    }

    private static void TileFigures()
    {
        Assert.Equal(("2.6", "/ 4.2 GB"), ProgressOverviewText.Of((long)(2.6 * 1024 * 1024 * 1024), (long)(4.2 * 1024 * 1024 * 1024)));
        Assert.Equal(("6.2", "MB"), ProgressOverviewText.Split((long)(6.2 * 1024 * 1024)));
        Assert.Equal("4m 23s", ProgressOverviewText.Duration(TimeSpan.FromSeconds(263)));
        Assert.Equal("1h 5m", ProgressOverviewText.Duration(TimeSpan.FromMinutes(65)));
        Assert.Equal("—", ProgressOverviewText.Duration(null));
        Assert.Equal("under 1s", ProgressOverviewText.Duration(TimeSpan.Zero));
        Assert.Equal("2d 3h", ProgressOverviewText.Duration(TimeSpan.FromHours(51)));
        Assert.Null(ProgressOverviewText.Took(Start, null));
        Assert.Equal("HTTP 403", ProgressOverviewText.ErrorName(403, "http"));
        Assert.Equal("Disk full", ProgressOverviewText.ErrorName(null, "diskFull"));
    }

    /// <summary>UI.R31: one size and one moment format everywhere: the list's.</summary>
    [Fact]
    public void Sizes_and_moments_read_as_the_list_writes_them()
    {
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        try
        {
            Assert.Equal("0 B", UiFormat.Bytes(0));
            Assert.Equal("900 B", UiFormat.Bytes(900));
            Assert.Equal("2.9 MB", UiFormat.Bytes(3_000_000));
            Assert.Equal("858.3 MB", UiFormat.Bytes(900_000_000));
            Assert.Equal("287.6 GB", UiFormat.Bytes((long)(287.56 * 1024 * 1024 * 1024)));
            Assert.Equal("4.0 KB", UiFormat.Bytes(4096));
            var local = new DateTimeOffset(new DateTime(2026, 11, 9, 19, 19, 28, DateTimeKind.Local));
            Assert.Equal("Nov 09 19:19:28 2026", UiFormat.Moment(local));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = culture; }
    }
}
