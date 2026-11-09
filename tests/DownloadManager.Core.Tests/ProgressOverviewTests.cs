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
        Assert.Equal(("2.60", "/ 4.20 GB"), ProgressOverviewText.Of((long)(2.6 * 1024 * 1024 * 1024), (long)(4.2 * 1024 * 1024 * 1024)));
        Assert.Equal(("6.20", "MB"), ProgressOverviewText.Split((long)(6.2 * 1024 * 1024)));
        Assert.Equal("4 min 23 s", ProgressOverviewText.Duration(TimeSpan.FromSeconds(263)));
        Assert.Equal("1 h 05 min", ProgressOverviewText.Duration(TimeSpan.FromMinutes(65)));
        Assert.Equal("—", ProgressOverviewText.Duration(null));
        Assert.Null(ProgressOverviewText.Took(Start, null));
        Assert.Equal("HTTP 403", ProgressOverviewText.ErrorName(403, "http"));
        Assert.Equal("Disk full", ProgressOverviewText.ErrorName(null, "diskFull"));
    }
}
