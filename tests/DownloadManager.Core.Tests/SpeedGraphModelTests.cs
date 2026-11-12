using DownloadManager.Desktop.WinUI;

namespace DownloadManager.Core.Tests;

public sealed class SpeedGraphModelTests
{
    private const double KB = 1024;
    private const double MB = 1024 * 1024;
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    [Theory]
    [InlineData(0, 1024)]
    [InlineData(500, 1024)]
    [InlineData(1025, 2048)]
    [InlineData(5 * 1024, 6 * 1024)]
    [InlineData(9 * 1024, 10 * 1024)]
    [InlineData(700 * 1024, 800 * 1024)]
    [InlineData(801 * 1024, 1024 * 1024)]
    [InlineData(1.9 * 1024 * 1024, 2 * 1024 * 1024)]
    [InlineData(2.1 * 1024 * 1024, 4 * 1024 * 1024)]
    public void Steps_are_round_figures_in_binary_units(double peak, double expected) =>
        Assert.Equal(expected, SpeedGraphScale.NiceAtOrAbove(peak));

    [Fact]
    public void Step_below_is_the_previous_round_figure()
    {
        Assert.Equal(8 * KB, SpeedGraphScale.NiceBelow(10 * KB));
        Assert.Equal(800 * KB, SpeedGraphScale.NiceBelow(MB));
        Assert.Equal(SpeedGraphScale.Minimum, SpeedGraphScale.NiceBelow(SpeedGraphScale.Minimum));
    }

    [Fact]
    public void Scale_grows_at_once_when_a_figure_needs_room()
    {
        var scale = new SpeedGraphScale();
        Assert.Equal(2 * MB, scale.Update(1.5 * MB, T0));
        Assert.Equal(4 * MB, scale.Update(3 * MB, T0.AddSeconds(1)));
    }

    [Fact]
    public void Scale_does_not_change_while_the_peak_moves_within_a_step()
    {
        var scale = new SpeedGraphScale();
        scale.Update(3.5 * MB, T0);
        // Up and down below the top, but above the lower step's margin: the scale stays put.
        for (var i = 1; i <= 60; i++)
            Assert.Equal(4 * MB, scale.Update(i % 2 == 0 ? 3.9 * MB : 3.6 * MB, T0.AddSeconds(i)));
    }

    [Fact]
    public void Scale_shrinks_slowly_one_step_at_a_time_after_a_hold()
    {
        var scale = new SpeedGraphScale();
        scale.Update(7 * MB, T0);
        Assert.Equal(8 * MB, scale.Top);
        var tops = new List<double>();
        for (var i = 1; i <= 20; i++) tops.Add(scale.Update(100 * KB, T0.AddSeconds(i)));
        // Nothing for the first seconds below.
        Assert.All(tops.Take(5), top => Assert.Equal(8 * MB, top));
        // Then one step at a time, never skipping one.
        for (var i = 1; i < tops.Count; i++)
            Assert.True(tops[i] == tops[i - 1] || tops[i] == SpeedGraphScale.NiceBelow(tops[i - 1]));
        Assert.Equal(6 * MB, tops[5]);
        Assert.True(tops[^1] < 8 * MB);
    }

    [Fact]
    public void A_figure_near_the_lower_step_holds_the_scale()
    {
        var scale = new SpeedGraphScale();
        scale.Update(7 * MB, T0);
        // 5.5 MB is under 6 MB but within the margin, so the scale doesn't flap between 6 and 8.
        for (var i = 1; i <= 30; i++) Assert.Equal(8 * MB, scale.Update(5.5 * MB, T0.AddSeconds(i)));
    }

    [Fact]
    public void Reset_fits_at_once()
    {
        var scale = new SpeedGraphScale();
        scale.Update(7 * MB, T0);
        Assert.Equal(200 * KB, scale.Reset(150 * KB));
    }

    [Theory]
    [InlineData(60, 900, 1)]
    [InlineData(300, 900, 1)]
    [InlineData(1800, 900, 2)]
    [InlineData(3600, 900, 4)]
    [InlineData(3600, 150, 24)]
    [InlineData(60, 0, 1)]
    public void Buckets_never_outnumber_pixels(int period, double width, int expected) =>
        Assert.Equal(expected, SpeedGraphPoints.BucketSeconds(period, width));

    private static List<(DateTimeOffset, double?)> Samples(int count, Func<int, double?> rate, double offset = 0.37) =>
        Enumerable.Range(0, count).Select(i => (T0.AddSeconds(i + offset), rate(i))).ToList();

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(24)]
    public void Points_already_drawn_never_move_when_samples_arrive(int bucket)
    {
        var random = new Random(7);
        var all = Samples(400, i => i % 97 == 50 ? null : random.Next(0, 10_000_000));
        List<SpeedGraphPoints.Point>? previous = null;
        for (var n = 1; n <= all.Count; n++)
        {
            var points = SpeedGraphPoints.Build(all.Take(n), all[n - 1].Item1, bucket);
            if (previous is not null)
            {
                // Everything drawn before is still drawn, the same, at the same place; new points only add on the right.
                Assert.True(points.Count >= previous.Count);
                Assert.Equal(previous, points.Take(previous.Count));
            }
            previous = points;
        }
    }

    [Theory]
    [InlineData(1, 0.37)]
    [InlineData(1, 0.98)]
    [InlineData(2, 0.5)]
    [InlineData(4, 0.1)]
    [InlineData(24, 0.9)]
    public void The_line_always_reaches_past_the_right_edge_until_the_next_sample(int bucket, double offset)
    {
        // Samples about a second apart (a little jitter); the edge is at the newest sample's time plus however long the
        // line has slid since, up to the next sample. The newest drawn point must be past it, so the visible end of the
        // line lies on a segment whose ends are both fixed and never on a newly added one.
        var jitter = new Random(3);
        var times = new List<DateTimeOffset>();
        var t = T0.AddSeconds(offset);
        // The timer's ticks wander a little either way. (A tick far later than MaxSlide is a stall: the line stops, its last
        // value carries on flat to the edge, and that end can step when samples come again.)
        for (var i = 0; i < 300; i++) { times.Add(t); t = t.AddSeconds(0.6 + jitter.NextDouble() * 0.8); }
        var samples = times.Select((time, i) => (time, (double?)(i * 1000))).ToList();
        for (var n = 60; n < samples.Count - 1; n++)
        {
            var points = SpeedGraphPoints.Build(samples.Take(n), samples[n - 1].time, bucket);
            var edge = samples[n].time < samples[n - 1].time + SpeedGraphPoints.MaxSlide ? samples[n].time : samples[n - 1].time + SpeedGraphPoints.MaxSlide;
            Assert.True(points[^1].At >= edge, $"bucket {bucket}: line ends at {points[^1].At:ss.fff}, edge stops at {edge:ss.fff}");
            // And it is a bounded lag, not a line that runs far ahead.
            Assert.True(points[^1].At - samples[n - 1].time <= SpeedGraphPoints.Lag(bucket) + TimeSpan.FromSeconds(bucket));
        }
    }

    [Fact]
    public void A_bucket_keeps_its_lowest_and_highest_figure_in_order()
    {
        var samples = new List<(DateTimeOffset, double?)>
        {
            (T0.AddSeconds(0.2), 5), (T0.AddSeconds(1.2), 9), (T0.AddSeconds(2.2), 1), (T0.AddSeconds(3.2), 4),
            (T0.AddSeconds(4.2), 7),
        };
        var points = SpeedGraphPoints.Build(samples, T0.AddSeconds(4.2), 4);
        Assert.Equal([9d, 1d], points.Select(point => point.Rate!.Value));
        Assert.True(points[0].At < points[1].At);
    }

    [Fact]
    public void The_bucket_under_way_is_not_drawn()
    {
        // Two-second buckets: 0-1, 2-3 are finished; 4 (the newest) is under way.
        var samples = Samples(5, i => 1000 * (i + 1), offset: 0);
        var points = SpeedGraphPoints.Build(samples, samples[^1].Item1, 2);
        Assert.Equal([1000d, 2000d, 3000d, 4000d], points.Select(point => point.Rate!.Value));
        Assert.True(SpeedGraphPoints.IsFinished(samples[3].Item1, samples[^1].Item1, 2));
        Assert.False(SpeedGraphPoints.IsFinished(samples[^1].Item1, samples[^1].Item1, 2));
    }

    [Fact]
    public void A_missing_figure_breaks_the_line_once()
    {
        var samples = Samples(8, i => i is 3 or 4 ? null : 1000);
        var points = SpeedGraphPoints.Build(samples, samples[^1].Item1, 1);
        Assert.Equal(1, points.Count(point => point.Rate is null));
        Assert.Null(points[3].Rate);
        // 0, 1, 2, the gap, 5, 6, 7: a sample of its own is finished when it comes.
        Assert.Equal(7, points.Count);
    }

    [Fact]
    public void Shown_at_matches_the_drawn_point_of_a_single_sample()
    {
        var samples = Samples(5, i => 1000);
        var points = SpeedGraphPoints.Build(samples, samples[^1].Item1, 1);
        Assert.Equal(points[2].At, SpeedGraphPoints.ShownAt(samples[2].Item1, 1));
        Assert.True(SpeedGraphPoints.IsFinished(samples[2].Item1, samples[^1].Item1, 1));
        Assert.Equal(samples[^1].Item1 + SpeedGraphPoints.MaxSlide, points[^1].At);
    }
}
