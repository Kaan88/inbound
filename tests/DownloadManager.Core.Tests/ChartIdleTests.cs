using DownloadManager.Desktop.WinUI;

namespace DownloadManager.Core.Tests;

/// <summary>
/// Audit M11: Progress Details and the package window take no samples while their download waits; the wait's seconds
/// come back as zeros once it moves again, so nothing ticks during a pause and the line still has no gap.
/// </summary>
public sealed class ChartIdleTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_wait_begins_once_and_gives_its_seconds_as_zeros_when_the_download_moves_again()
    {
        var idle = new ChartIdle();
        var (zeros, begins) = idle.Update(waiting: true, connected: true, Start);
        Assert.True(begins);
        Assert.Empty(zeros);

        // Further changes during the wait (another update, the window shown again) neither restart it nor add anything.
        (zeros, begins) = idle.Update(waiting: true, connected: true, Start.AddSeconds(30));
        Assert.False(begins);
        Assert.Empty(zeros);
        Assert.True(idle.Waiting);

        (zeros, begins) = idle.Update(waiting: false, connected: true, Start.AddSeconds(90.5));
        Assert.False(begins);
        Assert.False(idle.Waiting);
        // One a second after the wait's first sample, all before the moment it moved; consecutive, so the graph sees no gap.
        Assert.Equal(90, zeros.Count);
        Assert.Equal(Start.AddSeconds(1), zeros[0]);
        Assert.Equal(Start.AddSeconds(90), zeros[^1]);
        Assert.All(zeros.Zip(zeros.Skip(1)), pair => Assert.Equal(TimeSpan.FromSeconds(1), pair.Second - pair.First));
    }

    [Fact]
    public void A_dropped_connection_ends_the_wait_and_no_wait_begins_until_it_is_back()
    {
        var idle = new ChartIdle();
        idle.Update(waiting: true, connected: true, Start);
        var (zeros, begins) = idle.Update(waiting: true, connected: false, Start.AddSeconds(10));
        Assert.False(begins);
        Assert.Equal(9, zeros.Count);
        Assert.Equal(Start.AddSeconds(9), zeros[^1]);

        // While away nothing is known: no zeros, no wait.
        (zeros, begins) = idle.Update(waiting: true, connected: false, Start.AddSeconds(60));
        Assert.False(begins);
        Assert.Empty(zeros);
        Assert.False(idle.Waiting);

        (_, begins) = idle.Update(waiting: true, connected: true, Start.AddSeconds(70));
        Assert.True(begins);
    }

    [Fact]
    public void Nothing_is_added_when_the_download_never_waited_or_a_wait_lasts_under_a_second()
    {
        var idle = new ChartIdle();
        Assert.Equal((0, false), Count(idle.Update(waiting: false, connected: true, Start)));
        idle.Update(waiting: true, connected: true, Start);
        Assert.Equal((0, false), Count(idle.Update(waiting: false, connected: true, Start.AddMilliseconds(900))));
    }

    [Fact]
    public void A_wait_longer_than_the_graphs_hour_gives_only_the_last_hour()
    {
        var idle = new ChartIdle();
        idle.Update(waiting: true, connected: true, Start);
        var (zeros, _) = idle.Update(waiting: false, connected: true, Start.AddHours(5));
        Assert.Equal(3600, zeros.Count);
        Assert.Equal(Start.AddHours(4), zeros[0]);
        Assert.Equal(Start.AddHours(5).AddSeconds(-1), zeros[^1]);
    }

    private static (int, bool) Count((IReadOnlyList<DateTimeOffset> Zeros, bool Begins) result) => (result.Zeros.Count, result.Begins);
}
