using DownloadManager.Desktop.WinUI;

namespace DownloadManager.Core.Tests;

/// <summary>UI.B20: Progress Details and the main window sample a download's speed by the same rule.</summary>
public sealed class SpeedSampleTests
{
    private static readonly DateTimeOffset Updated = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_last_rate_is_held_between_updates_until_it_goes_stale()
    {
        // Updates arrive irregularly; every one-second sample until the next one carries the same figure.
        for (var second = 0; second <= 10; second++)
            Assert.Equal(800_000, SpeedSample.JobRate("downloading", 800_000, Updated, Updated.AddSeconds(second)));
        Assert.Equal(0, SpeedSample.JobRate("downloading", 800_000, Updated, Updated.AddSeconds(11)));
    }

    [Theory]
    [InlineData("connecting", 5_000d, 5_000d)]
    [InlineData("retryWaiting", null, 0d)]
    [InlineData("paused", 5_000d, 0d)]
    [InlineData("completed", 5_000d, 0d)]
    [InlineData("downloading", -1d, 0d)]
    [InlineData("downloading", double.NaN, 0d)]
    public void Quiet_or_stopped_downloads_count_as_zero_not_a_gap(string state, double? rate, double expected) =>
        Assert.Equal(expected, SpeedSample.JobRate(state, rate, Updated, Updated));

    [Fact]
    public void Only_a_lost_connection_is_a_gap()
    {
        Assert.Null(SpeedSample.Total(false, [1, 2]));
        Assert.Equal(0d, SpeedSample.Total(true, []));
        Assert.Equal(3d, SpeedSample.Total(true, [1, 2]));
    }
}
