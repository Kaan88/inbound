namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// UI.B23: the speed graph's vertical scale. It used to be worked out afresh from the visible peak on every sample, so the
/// whole line rescaled (and the right end jumped) whenever the peak crossed a step or left the period. Now it grows at
/// once when a figure needs more room, but shrinks only after the figures have stayed well below the next lower step for
/// a while, and then one step at a time. Steps are round figures in binary units (1, 2, 4, 6, 8, 10, 20 … 800 per unit),
/// so the half-way line is round too.
/// </summary>
internal sealed class SpeedGraphScale
{
    /// <summary>The smallest top: 1 KB/s, so an idle graph is not drawn against a scale of a few bytes.</summary>
    public const double Minimum = 1024;

    /// <summary>How long the figures must stay below the next lower step before the scale takes it.</summary>
    public static readonly TimeSpan ShrinkAfter = TimeSpan.FromSeconds(5);

    /// <summary>How long between one step down and the next.</summary>
    public static readonly TimeSpan StepEvery = TimeSpan.FromSeconds(3);

    /// <summary>The figures must be at most this share of the lower step before shrinking counts, so a peak near a step doesn't flap.</summary>
    public const double ShrinkMargin = 0.85;

    private static readonly double[] Mantissas = [1, 2, 4, 6, 8, 10, 20, 40, 60, 80, 100, 200, 400, 600, 800];

    private DateTimeOffset? _belowSince;

    public double Top { get; private set; } = Minimum;

    /// <summary>The smallest step at or above <paramref name="value"/>.</summary>
    public static double NiceAtOrAbove(double value)
    {
        if (!double.IsFinite(value) || value <= Minimum) return Minimum;
        var unit = 1d;
        while (true)
        {
            foreach (var mantissa in Mantissas)
            {
                var step = mantissa * unit;
                if (step >= value && step >= Minimum) return step;
            }
            unit *= 1024;
        }
    }

    /// <summary>The step below <paramref name="top"/>, or <see cref="Minimum"/>.</summary>
    public static double NiceBelow(double top)
    {
        var below = Minimum;
        for (var step = Minimum; step < top; step = NiceAtOrAbove(step * 1.0001)) below = step;
        return below;
    }

    /// <summary>Fits the scale to <paramref name="peak"/> at once (a new period, the graph shown again).</summary>
    public double Reset(double peak)
    {
        _belowSince = null;
        return Top = NiceAtOrAbove(peak);
    }

    /// <summary>The top for a sample at <paramref name="time"/> whose visible figures reach <paramref name="peak"/>.</summary>
    public double Update(double peak, DateTimeOffset time)
    {
        var needed = NiceAtOrAbove(peak);
        if (needed >= Top)
        {
            // Grows at once: nothing is ever drawn above the top.
            _belowSince = null;
            return Top = needed;
        }

        var lower = NiceBelow(Top);
        if (peak > lower * ShrinkMargin)
        {
            _belowSince = null;
            return Top;
        }

        if (_belowSince is not { } since || time < since)
        {
            _belowSince = time;
            return Top;
        }

        if (time - since < ShrinkAfter) return Top;
        Top = lower;
        // The next step waits StepEvery, not the whole ShrinkAfter again.
        _belowSince = time - ShrinkAfter + StepEvery;
        return Top;
    }
}

/// <summary>
/// UI.B23: which points the speed graph draws, and where in time. Every point is placed by its own time (not by where the
/// right edge is), so once drawn it never moves. Where a period has more seconds than the chart has pixels, a point stands
/// for a whole bucket of time lined up with the clock; a bucket's points are chosen once it has ended, and the bucket under
/// way is not drawn. Each point is drawn <see cref="Lag"/> late, which keeps the newest point past the right edge, so the
/// line's visible end slides along a segment that is already there instead of jumping when a sample arrives.
/// </summary>
internal static class SpeedGraphPoints
{
    /// <summary>A drawn point: <see cref="Rate"/> null is a gap. <see cref="At"/> is the place on the time axis.</summary>
    public readonly record struct Point(DateTimeOffset At, double? Rate);

    /// <summary>
    /// The bucket length in whole seconds, so a period never has more points than pixels. 1 means every sample is a point
    /// of its own: the window's ticks don't line up with the clock's seconds, so one-second buckets would sometimes be empty.
    /// </summary>
    public static int BucketSeconds(int periodSeconds, double chartWidth) =>
        chartWidth < 1 || periodSeconds <= 0 ? 1 : Math.Max(1, (int)Math.Ceiling(periodSeconds / Math.Floor(chartWidth)));

    /// <summary>
    /// The longest the line slides after a sample. A sample later than this leaves the line where it got to rather than
    /// sliding on into empty space.
    /// </summary>
    public static readonly TimeSpan MaxSlide = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How late a point is drawn. A sample of its own is finished when it comes, so <see cref="MaxSlide"/> keeps it past
    /// the edge until the line stops. A bucket's points are at least half way through the bucket before the one under way,
    /// whose last sample may be near its end, so they need another bucket and a half. (Ticks come about a second apart, so
    /// a bucket of two seconds or more always holds one.)
    /// </summary>
    public static TimeSpan Lag(int bucketSeconds) =>
        bucketSeconds <= 1 ? MaxSlide : TimeSpan.FromSeconds(bucketSeconds * 1.5) + MaxSlide;

    private static long BucketOf(DateTimeOffset time, int bucketSeconds) =>
        (long)Math.Floor(time.ToUnixTimeMilliseconds() / (bucketSeconds * 1000d));

    private static DateTimeOffset BucketStart(long bucket, int bucketSeconds) =>
        DateTimeOffset.FromUnixTimeMilliseconds(bucket * bucketSeconds * 1000L);

    /// <summary>True when the sample at <paramref name="time"/> is drawn by the time of the newest, <paramref name="latest"/>.</summary>
    public static bool IsFinished(DateTimeOffset time, DateTimeOffset latest, int bucketSeconds) =>
        bucketSeconds <= 1 ? time <= latest : BucketOf(time, bucketSeconds) < BucketOf(latest, bucketSeconds);

    /// <summary>Where a sample is drawn: its own time, or its bucket's middle, plus the lag.</summary>
    public static DateTimeOffset ShownAt(DateTimeOffset time, int bucketSeconds) => bucketSeconds <= 1
        ? time + Lag(1)
        : BucketStart(BucketOf(time, bucketSeconds), bucketSeconds) + TimeSpan.FromSeconds(bucketSeconds / 2d) + Lag(bucketSeconds);

    /// <summary>
    /// The points for <paramref name="samples"/> (oldest first) whose bucket has ended. A bucket gives its lowest and
    /// highest figure in the order they came, at a quarter and three quarters of the way through it, so peaks and troughs
    /// survive at any period; a missing figure breaks the line. <paramref name="latest"/> is the newest sample's time;
    /// the bucket it is in is still under way.
    /// </summary>
    public static List<Point> Build(IEnumerable<(DateTimeOffset Time, double? Rate)> samples, DateTimeOffset latest, int bucketSeconds)
    {
        var points = new List<Point>();
        void Gap(DateTimeOffset at)
        {
            // One break is enough however many figures are missing.
            if (points.Count > 0 && points[^1].Rate is not null) points.Add(new Point(at, null));
        }

        if (bucketSeconds <= 1)
        {
            foreach (var (time, rate) in samples)
            {
                if (time > latest) break;
                if (rate is null) Gap(time + MaxSlide);
                else points.Add(new Point(time + MaxSlide, rate));
            }
            return points;
        }

        var open = BucketOf(latest, bucketSeconds);
        var span = TimeSpan.FromSeconds(bucketSeconds);
        var lag = Lag(bucketSeconds);
        long current = long.MinValue;
        var run = new List<(DateTimeOffset Time, double Rate)>();

        void EndRun()
        {
            if (run.Count == 0) return;
            var start = BucketStart(current, bucketSeconds) + lag;
            var low = run[0];
            var high = run[0];
            foreach (var sample in run)
            {
                if (sample.Rate < low.Rate) low = sample;
                if (sample.Rate > high.Rate) high = sample;
            }
            if (low.Time == high.Time) points.Add(new Point(start + span / 2, low.Rate));
            else
            {
                var (first, second) = low.Time < high.Time ? (low, high) : (high, low);
                points.Add(new Point(start + span / 4, first.Rate));
                points.Add(new Point(start + span * 3 / 4, second.Rate));
            }
            run.Clear();
        }

        foreach (var (time, rate) in samples)
        {
            var bucket = BucketOf(time, bucketSeconds);
            if (bucket >= open) break;
            if (bucket != current)
            {
                EndRun();
                current = bucket;
            }
            if (rate is { } value) run.Add((time, value));
            else
            {
                EndRun();
                Gap(BucketStart(current, bucketSeconds) + lag + span / 2);
            }
        }
        EndRun();
        return points;
    }
}
