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

/// <summary>
/// UI.B26: what one drawing of the speed graph shows, and the scale that fits it. A drawing lasts until the next sample:
/// its line slides left meanwhile, so its right edge moves from "now" to "now" plus <see cref="Reveal"/> (a second, the
/// sampling step) and, should the next sample be late, on to where the slide stops. The scale is fitted to the line up to
/// the reveal. Past it the line follows its real points, so a late sample doesn't make the end jump, but never higher
/// than the figure the scale was fitted to, so nothing shows above it or is clipped to the top. Before, the scale was
/// fitted to every sample, including ones the line would show only seconds later (each point is drawn late, UI.B23), so
/// the top grew while the line still lay at zero.
/// </summary>
internal static class SpeedGraphDrawing
{
    /// <summary>How far a drawing's right edge moves before the next sample is due: samples come once a second.</summary>
    public static readonly TimeSpan Reveal = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The line a drawing shows (a null rate breaks it): <paramref name="points"/> up to <paramref name="end"/> (where the
    /// slide stops), with every figure past <paramref name="reveal"/> held to at most <paramref name="cap"/> (the figure
    /// the scale was fitted to). Where the points stop short of the end (samples came late), the last figure carries on
    /// flat while <paramref name="newestKnown"/>.
    /// </summary>
    public static List<SpeedGraphPoints.Point> Line(IReadOnlyList<SpeedGraphPoints.Point> points, DateTimeOffset reveal, DateTimeOffset end, double cap, bool newestKnown)
    {
        // The real line up to the end.
        var raw = new List<SpeedGraphPoints.Point>();
        foreach (var point in points)
        {
            if (point.At <= end)
            {
                raw.Add(point);
                continue;
            }
            if (point.Rate is { } next && raw.Count > 0 && raw[^1].Rate is { } before)
                raw.Add(new SpeedGraphPoints.Point(end, Between(raw[^1].At, before, point.At, next, end)));
            break;
        }
        if (newestKnown && raw.Count > 0 && raw[^1].Rate is { } last && raw[^1].At < end && (points.Count == 0 || points[^1].At <= end))
            raw.Add(new SpeedGraphPoints.Point(end, last));

        // Past the reveal, held to the cap: where a segment crosses it, a point on it.
        var line = new List<SpeedGraphPoints.Point>();
        for (var i = 0; i < raw.Count; i++)
        {
            var point = raw[i];
            if (point.At <= reveal || point.Rate is not { } rate)
            {
                line.Add(point);
                continue;
            }
            if (i > 0 && raw[i - 1].Rate is { } previous && raw[i - 1].At < point.At)
            {
                var from = raw[i - 1].At;
                var startAt = from < reveal ? reveal : from;
                var startValue = Between(from, previous, point.At, rate, startAt);
                // At the reveal only when the line is already held there (a crossing point lies on the real segment).
                if (startAt > from && startValue >= cap && Math.Max(startValue, rate) > cap) line.Add(new SpeedGraphPoints.Point(startAt, Math.Min(startValue, cap)));
                if ((startValue - cap) * (rate - cap) < 0)
                {
                    var share = (cap - startValue) / (rate - startValue);
                    line.Add(new SpeedGraphPoints.Point(startAt + (point.At - startAt) * share, cap));
                }
            }
            line.Add(point with { Rate = Math.Min(rate, cap) });
        }
        return line;
    }

    private static double Between(DateTimeOffset fromAt, double from, DateTimeOffset toAt, double to, DateTimeOffset at)
    {
        var span = (toAt - fromAt).TotalSeconds;
        var share = span <= 0 ? 1 : Math.Clamp((at - fromAt).TotalSeconds / span, 0, 1);
        return from + (to - from) * share;
    }

    /// <summary>One series for <see cref="Draw"/>: its samples (oldest first) and whether its newest figure is known.</summary>
    public readonly record struct Series(IEnumerable<(DateTimeOffset Time, double? Rate)> Samples, bool NewestKnown);

    /// <summary>
    /// The lines a drawing shows when its newest sample is at <paramref name="now"/> (one per series, in order) and the
    /// figures its scale works from. One scale for all the lines (T8), fitted to the highest each shows from
    /// <paramref name="start"/> up to the reveal.
    /// </summary>
    public static (List<List<SpeedGraphPoints.Point>> Lines, Peaks Peaks) Draw(IReadOnlyList<Series> series, DateTimeOffset start, DateTimeOffset now, int bucketSeconds)
    {
        var reveal = now + Reveal;
        var built = series.Select(item => SpeedGraphPoints.Build(item.Samples, now, bucketSeconds)).ToList();
        var fitted = built.Select(points => Peak(points, start, reveal)).DefaultIfEmpty(0).Max();
        var lines = built.Select((points, i) => Line(points, reveal, now + SpeedGraphPoints.MaxSlide, fitted, series[i].NewestKnown)).ToList();
        return (lines, PeaksOf(lines, start, now));
    }

    /// <summary>
    /// True when the hover can pick the sample at <paramref name="time"/>: its point (all of its bucket's points) is drawn
    /// by the reveal of the drawing whose newest sample is <paramref name="latest"/>, so its figure is within the scale.
    /// </summary>
    public static bool IsInScale(DateTimeOffset time, DateTimeOffset latest, int bucketSeconds)
    {
        if (!SpeedGraphPoints.IsFinished(time, latest, bucketSeconds)) return false;
        var lastPoint = SpeedGraphPoints.ShownAt(time, bucketSeconds) + (bucketSeconds <= 1 ? TimeSpan.Zero : TimeSpan.FromSeconds(bucketSeconds / 4d));
        return lastPoint <= latest + Reveal;
    }

    /// <summary>The line's figure at <paramref name="at"/>: null outside it or in a gap.</summary>
    public static double? ValueAt(IReadOnlyList<SpeedGraphPoints.Point> line, DateTimeOffset at)
    {
        for (var i = 0; i < line.Count; i++)
        {
            if (line[i].At < at) continue;
            if (line[i].Rate is not { } rate) return null;
            if (line[i].At == at) return rate;
            if (i == 0 || line[i - 1].Rate is not { } previous) return null;
            var span = (line[i].At - line[i - 1].At).TotalSeconds;
            return previous + (rate - previous) * ((at - line[i - 1].At).TotalSeconds / span);
        }
        return null;
    }

    /// <summary>The highest figure the line shows between <paramref name="from"/> and <paramref name="to"/>; 0 for none.</summary>
    public static double Peak(IReadOnlyList<SpeedGraphPoints.Point> line, DateTimeOffset from, DateTimeOffset to)
    {
        var peak = Math.Max(ValueAt(line, from) ?? 0, ValueAt(line, to) ?? 0);
        foreach (var point in line)
            if (point.At > from && point.At < to && point.Rate is { } rate && rate > peak) peak = rate;
        return peak;
    }

    /// <summary>
    /// The figures a drawing's scale works from, over all its lines: <see cref="Shown"/>, the highest it shows (left edge
    /// to the reveal; past it the line is held to that figure); <see cref="Before"/>, the highest in view as it begins (what the last drawing ended on);
    /// <see cref="WhileEasing"/>, the highest in view while the line eases to a new scale.
    /// </summary>
    public readonly record struct Peaks(double Shown, double Before, double WhileEasing);

    public static Peaks PeaksOf(IEnumerable<IReadOnlyList<SpeedGraphPoints.Point>> lines, DateTimeOffset start, DateTimeOffset now)
    {
        double shown = 0, before = 0, easing = 0;
        foreach (var line in lines)
        {
            shown = Math.Max(shown, Peak(line, start, now + Reveal));
            before = Math.Max(before, Peak(line, start, now));
            easing = Math.Max(easing, Peak(line, start, now + SpeedGraphEase.Duration));
        }
        return new Peaks(shown, before, easing);
    }
}

/// <summary>
/// UI.B26: how the line eases to a new scale. It is redrawn against the new top and stretched (a transform) to look as
/// it did, then eased back over <see cref="Duration"/>. The transform stretches the stroke too, so the stretch is kept
/// small (a flat zero line stretched 4,096 times was the full-height bar of the recording), it is skipped when the line
/// showed nothing above zero (there is nothing to carry over), and it never takes what comes into view past the top.
/// </summary>
internal static class SpeedGraphEase
{
    public static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(250);

    /// <summary>The most the line (and its stroke) is stretched or squeezed. Neighbouring steps are at most 2.5 apart.</summary>
    public const double MaxStretch = 2.5;

    /// <summary>
    /// The stretch the ease starts from (1: none). <paramref name="before"/> is the highest figure in view as the new
    /// drawing begins, <paramref name="whileEasing"/> the highest in view while it eases.
    /// </summary>
    public static double From(double previousTop, double top, double before, double whileEasing)
    {
        if (previousTop <= 0 || top <= 0 || previousTop == top || before <= 0) return 1;
        var stretch = top / previousTop;
        // Shrinking: the line is squeezed to its old height and rises into place, never past the top.
        if (stretch < 1) return Math.Max(stretch, 1 / MaxStretch);
        // Growing: at its start the ease draws figures as high as the old scale did; nothing in view may go past the top.
        stretch = Math.Min(stretch, MaxStretch);
        if (whileEasing > 0) stretch = Math.Min(stretch, top / whileEasing);
        return Math.Max(1, stretch);
    }

    /// <summary>The stretch <paramref name="elapsed"/> into an ease from <paramref name="from"/> (the cubic ease-out the control runs).</summary>
    public static double At(double from, TimeSpan elapsed)
    {
        var t = Math.Clamp(elapsed / Duration, 0, 1);
        return from + (1 - from) * (1 - Math.Pow(1 - t, 3));
    }
}

/// <summary>
/// T8: the speed graph's upload rules: its names while the upload line shows (the heading, the plot's and the toggle's
/// spoken names say "and upload" then), and when the Host's upload hour can be placed.
/// </summary>
internal static class SpeedGraphUpload
{
    /// <summary>The heading the main window's graph has in its XAML.</summary>
    public const string DownloadHeading = "Download speed";

    /// <summary>The words on the bar: "Download speed" becomes "Download and upload speed" while upload shows.</summary>
    public static string Heading(string heading, bool showUpload)
        => showUpload && heading == DownloadHeading ? "Download and upload speed" : heading;

    /// <summary>The plot's spoken name.</summary>
    public static string PlotName(string heading, bool showUpload) => $"{Heading(heading, showUpload)} history";

    /// <summary>
    /// Whether the Host's upload hour can be placed beside its download hour: the same number of seconds from the same
    /// start. A Host before T8 sends none; then the graph keeps its own upload samples, which carry their own times.
    /// </summary>
    public static bool UploadLinesUp(IReadOnlyList<long> download, IReadOnlyList<long>? upload)
        => upload is { Count: > 0 } && upload.Count == download.Count;

    /// <summary>The toggle's spoken name and tooltip.</summary>
    public static string Toggle(bool expanded, bool showUpload)
        => $"{(expanded ? "Hide" : "Show")} {(showUpload ? "download and upload" : "download")} speed graph";
}
