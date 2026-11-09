using DownloadManager.Desktop.WinUI;

namespace DownloadManager.Core.Tests;

/// <summary>UI.R29 direction B: the progress window's speed minute and its tile figures.</summary>
public sealed class ProgressOverviewTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void One_sample_is_kept_per_second_and_the_last_in_it_wins()
    {
        var history = new SpeedHistory();
        history.Add(Start, 100);
        history.Add(Start.AddMilliseconds(400), 300);
        history.Add(Start.AddSeconds(1), 200);
        Assert.Equal([(Start.ToUnixTimeSeconds(), 300d), (Start.ToUnixTimeSeconds() + 1, 200d)], history.Samples);
        Assert.Equal(300, history.Peak);
    }

    [Fact]
    public void Only_the_last_minute_is_kept_and_drawn_across_it()
    {
        var history = new SpeedHistory();
        for (var second = 0; second <= 90; second += 10) history.Add(Start.AddSeconds(second), second);
        Assert.Equal(30, history.Samples[0].Rate);
        var points = history.Points();
        Assert.Equal(0, points[0].X, 6);
        Assert.Equal(1, points[^1].X, 6);
        Assert.Equal(90, history.Peak);
    }

    [Fact]
    public void A_clock_that_goes_back_or_a_bad_rate_draws_nothing_wrong()
    {
        var history = new SpeedHistory();
        history.Add(Start.AddSeconds(5), 10);
        history.Add(Start, 99);
        history.Add(Start.AddSeconds(6), double.NaN);
        history.Add(Start.AddSeconds(7), -4);
        Assert.Equal([10d, 0d, 0d], history.Samples.Select(sample => sample.Rate));
    }

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
