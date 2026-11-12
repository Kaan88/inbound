using DownloadManager.Desktop.WinUI;

namespace DownloadManager.Core.Tests;

/// <summary>
/// UI.B26: the owner's recording of a torrent being added (30 min graph): at the first sample the right edge showed a
/// full-height bar for a frame, then the line lay at zero while the top of the scale went 1 KB/s, 4, 10, 20 MB/s. These
/// replay it drawing by drawing and frame by frame, the way the control draws, slides and eases.
/// </summary>
public sealed class SpeedGraphStartTests
{
    private const double MB = 1024 * 1024;
    private const int Period = 1800;
    private static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(50);
    // A whole number of seconds that is even, so two-second buckets line up with the samples.
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    private sealed record Drawing(DateTimeOffset Now, DateTimeOffset Start, List<SpeedGraphPoints.Point> Line, double Top, double EaseFrom);

    private sealed record Shown(int Drawing, TimeSpan At, double Top, double EaseStretch, double MaxData, double MaxHeight, double? EdgeHeight, bool FullHeightNotInData);

    /// <summary>A minute of zeros, then the recording's figures a second apart (and the last one held).</summary>
    private static List<(DateTimeOffset Time, double? Rate)> Recording(int heldSeconds) =>
        Samples(0, (0, 2.9), (1, 2.9), (2, 9.5), (3, 11.8))
            .Concat(Enumerable.Range(4, heldSeconds).Select(i => (T0.AddSeconds(i), (double?)(11.8 * MB))))
            .ToList();

    /// <summary>A minute of <paramref name="history"/> MB/s, then the given figures (MB/s) at the given seconds.</summary>
    private static List<(DateTimeOffset Time, double? Rate)> Samples(double history, params (double Second, double Rate)[] figures)
    {
        var samples = new List<(DateTimeOffset, double?)>();
        for (var i = -60; i < 0; i++) samples.Add((T0.AddSeconds(i), history * MB));
        foreach (var (second, rate) in figures) samples.Add((T0.AddSeconds(second), rate * MB));
        return samples;
    }

    /// <summary>The drawings the control makes, one per sample from the first non-zero one, as Draw works them out.</summary>
    private static List<Drawing> Draw(List<(DateTimeOffset Time, double? Rate)> samples, int bucket)
    {
        var scale = new SpeedGraphScale();
        var drawings = new List<Drawing>();
        double drawnTop = 0;
        var first = true;
        foreach (var (now, _) in samples.Where(sample => sample.Time >= T0.AddSeconds(-3)))
        {
            var start = now.AddSeconds(-Period);
            var lineStart = start - SpeedGraphPoints.Lag(bucket) - TimeSpan.FromSeconds(bucket);
            var (lines, peaks) = SpeedGraphDrawing.Draw(
                [new(samples.Where(sample => sample.Time >= lineStart && sample.Time <= now).ToArray(), true)], start, now, bucket);
            var line = lines[0];
            var top = first ? scale.Reset(peaks.Shown) : scale.Update(peaks.Shown, now);
            first = false;
            drawings.Add(new Drawing(now, start, line, top, SpeedGraphEase.From(drawnTop, top, peaks.Before, peaks.WhileEasing)));
            drawnTop = top;
        }
        return drawings;
    }

    /// <summary>Every frame of every drawing until the next sample: what is in view, against which top, stretched how far.</summary>
    private static List<Shown> Frames(List<Drawing> drawings)
    {
        var frames = new List<Shown>();
        for (var d = 0; d < drawings.Count; d++)
        {
            var drawing = drawings[d];
            var until = d + 1 < drawings.Count ? drawings[d + 1].Now - drawing.Now : SpeedGraphPoints.MaxSlide;
            // Every frame, and the moment the next sample comes (the last thing this drawing shows).
            for (var elapsed = TimeSpan.Zero; elapsed <= until; elapsed = elapsed + Frame > until && elapsed < until ? until : elapsed + Frame)
            {
                // The line has slid this far left: what is in view is the period that much later.
                var slid = elapsed < SpeedGraphPoints.MaxSlide ? elapsed : SpeedGraphPoints.MaxSlide;
                var from = drawing.Start + slid;
                var to = drawing.Now + slid;
                var stretch = SpeedGraphEase.At(drawing.EaseFrom, elapsed);
                var inView = drawing.Line.Where(point => point.At >= from && point.At <= to && point.Rate is not null)
                    .Select(point => point.Rate!.Value)
                    .Concat(new[] { SpeedGraphDrawing.ValueAt(drawing.Line, from), SpeedGraphDrawing.ValueAt(drawing.Line, to) }
                        .Where(value => value is not null).Select(value => value!.Value))
                    .ToList();
                var maxData = inView.DefaultIfEmpty(0).Max();
                // Heights as drawn: the figure against the top, times the ease's stretch (1 is the chart's full height).
                var heights = inView.Select(value => value / drawing.Top * stretch).ToList();
                var maxHeight = heights.DefaultIfEmpty(0).Max();
                var edge = SpeedGraphDrawing.ValueAt(drawing.Line, to) is { } edgeValue ? Math.Min(1, edgeValue / drawing.Top * stretch) : (double?)null;
                // A point at (or clipped to) the top whose figure doesn't reach the top: the bar of the recording.
                var fullNotInData = inView.Any(value => value / drawing.Top * stretch >= 0.999 && value < drawing.Top * 0.999);
                frames.Add(new Shown(d, drawing.Now - T0 + elapsed, drawing.Top, stretch, maxData, maxHeight, edge, fullNotInData));
            }
        }
        return frames;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Recording_never_shows_a_bar_or_a_top_above_what_is_drawn(int bucket)
    {
        var drawings = Draw(Recording(heldSeconds: 8), bucket);
        var frames = Frames(drawings);

        foreach (var frame in frames)
        {
            // Nothing is drawn past the chart's top, and nothing reaches it that isn't a figure at the top.
            Assert.True(frame.MaxHeight <= 1 + 1e-9, $"{frame.At}: drawn at {frame.MaxHeight:0.###} of the height");
            Assert.False(frame.FullHeightNotInData, $"{frame.At}: a full-height point that isn't in the data");
            // The stroke is stretched with the line: never so far it shows as a bar.
            Assert.InRange(frame.EaseStretch, 1 / SpeedGraphEase.MaxStretch, SpeedGraphEase.MaxStretch);
        }

        // The scale only reflects what the drawing shows: while the line in view is all zero, the top stays at its smallest.
        for (var d = 0; d < drawings.Count; d++)
        {
            var drawing = drawings[d];
            var drawingFrames = frames.Where(frame => frame.Drawing == d).ToList();
            if (drawingFrames.All(frame => frame.MaxData == 0))
            {
                Assert.Equal(SpeedGraphScale.Minimum, drawing.Top);
                // A flat zero line is not stretched (that was the full-height bar).
                Assert.Equal(1, drawing.EaseFrom);
            }
            // The top is the round figure for the highest the drawing shows before the next sample: no more.
            var shown = drawingFrames.Select(frame => frame.MaxData).DefaultIfEmpty(0).Max();
            var reached = SpeedGraphDrawing.Peak(drawing.Line, drawing.Start, drawing.Now + SpeedGraphDrawing.Reveal);
            Assert.True(shown <= reached + 1e-6);
            Assert.True(drawing.Top <= SpeedGraphScale.NiceAtOrAbove(reached), $"{drawing.Now - T0}: top {drawing.Top} over a line reaching {reached}");
        }

        // The line grows smoothly. When a sample comes, the new drawing starts about where the last one ended (the ease
        // carries the old height over); while one is shown, its right end moves on by a part of the height per frame.
        for (var i = 1; i < frames.Count; i++)
        {
            if (frames[i].EdgeHeight is not { } now || frames[i - 1].EdgeHeight is not { } before) continue;
            var limit = frames[i].Drawing != frames[i - 1].Drawing ? 0.1 : 0.3;
            Assert.True(Math.Abs(now - before) <= limit, $"{frames[i].At}: the end went from {before:0.###} to {now:0.###}");
        }

        // And it does grow: the line reaches the figures.
        Assert.Equal(11.8 * MB, frames[^1].MaxData, 3);
    }

    [Fact]
    public void Recording_scale_steps_as_the_line_reaches_them()
    {
        var drawings = Draw(Recording(heldSeconds: 3), bucket: 1);
        var tops = drawings.Where(drawing => drawing.Now >= T0).Select(drawing => drawing.Top / MB).ToList();
        // At the first 2.9 MB/s sample the line in view is still zero, so the top is still 1 KB/s (it went to 4 MB/s
        // at once before); it grows a sample later, as the line starts to rise, and so on.
        Assert.Equal(SpeedGraphScale.Minimum / MB, tops[0]);
        Assert.Equal([4d, 4d, 10d, 20d], tops.Skip(1).Take(4));
    }

    [Fact]
    public void Line_past_the_reveal_follows_its_points_but_never_above_the_fit()
    {
        SpeedGraphPoints.Point P(double seconds, double? rate) => new(T0.AddSeconds(seconds), rate);
        // Fitted to 60 (the figure at the reveal): past it the rise is held to 60.
        var line = SpeedGraphDrawing.Line([P(0, 0), P(1, 0), P(2, 120), P(3, 300)], T0.AddSeconds(1.5), T0.AddSeconds(2.5), cap: 60, newestKnown: true);
        Assert.Equal([P(0, 0), P(1, 0), P(1.5, 60), P(2, 60), P(2.5, 60)], line);
        Assert.Equal(60, SpeedGraphDrawing.Peak(line, T0, T0.AddSeconds(3)));
        Assert.Equal(30, SpeedGraphDrawing.ValueAt(line, T0.AddSeconds(1.25)));

        // Under the fit it is the real line, so a late sample's end slides on along it.
        var under = SpeedGraphDrawing.Line([P(0, 100), P(1, 20), P(2, 60)], T0.AddSeconds(1), T0.AddSeconds(2), cap: 100, newestKnown: true);
        Assert.Equal([P(0, 100), P(1, 20), P(2, 60)], under);
        // Where a rise past the reveal crosses the fit, a point on it.
        var crossing = SpeedGraphDrawing.Line([P(0, 50), P(1, 50), P(3, 150)], T0.AddSeconds(1), T0.AddSeconds(3), cap: 100, newestKnown: true);
        Assert.Equal([P(0, 50), P(1, 50), P(2, 100), P(3, 100)], crossing);

        // A gap past the end stops the line; nothing is carried on.
        var broken = SpeedGraphDrawing.Line([P(0, 10), P(2, null), P(3, 300)], T0.AddSeconds(1), T0.AddSeconds(1.5), cap: 300, newestKnown: true);
        Assert.Equal([P(0, 10)], broken);

        // Points that stop short (samples came late) carry their last figure to the end while the newest is known.
        var late = SpeedGraphDrawing.Line([P(0, 10), P(1, 20)], T0.AddSeconds(2), T0.AddSeconds(3), cap: 20, newestKnown: true);
        Assert.Equal([P(0, 10), P(1, 20), P(3, 20)], late);
        Assert.Equal([P(0, 10), P(1, 20)], SpeedGraphDrawing.Line([P(0, 10), P(1, 20)], T0.AddSeconds(2), T0.AddSeconds(3), cap: 20, newestKnown: false));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    public void Late_sample_keeps_the_end_sliding_without_a_jump_or_a_bar(double history)
    {
        // The UI thread is busy while a torrent is added, so the fourth tick comes 0.4 s late. From zero (the scale
        // grows) and after a steady 15 MB/s (the scale holds at 20 MB/s).
        var samples = Samples(history, (0, 2.9), (1, 2.9), (2, 9.5), (3.4, 11.8), (4.4, 11.8), (5.4, 11.8), (6.4, 11.8));
        var drawings = Draw(samples, bucket: 1);
        var frames = Frames(drawings);
        foreach (var frame in frames)
        {
            Assert.True(frame.MaxHeight <= 1 + 1e-9, $"{frame.At}: drawn at {frame.MaxHeight:0.###} of the height");
            Assert.False(frame.FullHeightNotInData, $"{frame.At}: a full-height point that isn't in the data");
        }
        for (var i = 1; i < frames.Count; i++)
        {
            if (frames[i].EdgeHeight is not { } now || frames[i - 1].EdgeHeight is not { } before) continue;
            var limit = frames[i].Drawing != frames[i - 1].Drawing ? 0.1 : 0.3;
            Assert.True(Math.Abs(now - before) <= limit, $"{frames[i].At}: the end went from {before:0.###} to {now:0.###}");
        }
        if (history > 0)
        {
            // The scale holds, so the late stretch is the real rise: 2.9 to 9.5 MB/s, 0.4 of the way at 3.4 s.
            var late = drawings.FindIndex(drawing => drawing.Now == T0.AddSeconds(2));
            Assert.Equal(20 * MB, drawings[late].Top);
            Assert.Equal((2.9 + 6.6 * 0.4) / 20, frames.Last(frame => frame.Drawing == late).EdgeHeight!.Value, 3);
        }
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 0)]
    [InlineData(2, 1)]
    public void Hover_picks_only_samples_within_the_scale(int bucket, int offsetSeconds)
    {
        // The review's case: two-second buckets, the newest sample at the start of its bucket. The bucket two back holds
        // 0 then 2.9 MB/s, but its points are past the reveal, so the top is still 1 KB/s; the hover must not pick it.
        var samples = Samples(0, (0, 0), (1, 2.9), (2, 2.9), (3, 2.9), (4, 2.9));
        var latest = T0.AddSeconds(4 + offsetSeconds);
        if (offsetSeconds > 0) samples.Add((latest, 2.9 * MB));
        var start = latest.AddSeconds(-Period);
        var (_, peaks) = SpeedGraphDrawing.Draw([new(samples, true)], start, latest, bucket);
        var top = new SpeedGraphScale().Reset(peaks.Shown);
        var picked = 0;
        foreach (var (time, rate) in samples.Where(sample => sample.Time >= start))
        {
            if (!SpeedGraphDrawing.IsInScale(time, latest, bucket)) continue;
            picked++;
            Assert.True(rate <= top, $"{time - T0}: {rate} over the top {top}");
        }
        Assert.True(picked > 0);
        Assert.False(SpeedGraphDrawing.IsInScale(latest, latest, bucket));
        if (bucket == 2 && offsetSeconds == 0)
        {
            Assert.Equal(SpeedGraphScale.Minimum, top);
            Assert.False(SpeedGraphDrawing.IsInScale(T0.AddSeconds(1), latest, bucket));
        }
    }

    [Fact]
    public void Ease_is_skipped_for_a_flat_zero_line_and_kept_small()
    {
        // 1 KB/s to 4 MB/s with nothing in view above zero: no stretch (it was 4,096, a full-height bar).
        Assert.Equal(1, SpeedGraphEase.From(1024, 4 * MB, before: 0, whileEasing: 0.7 * MB));
        // A big jump is stretched at most MaxStretch, and never so far that what comes into view passes the top.
        Assert.Equal(SpeedGraphEase.MaxStretch, SpeedGraphEase.From(1 * MB, 20 * MB, before: 0.5 * MB, whileEasing: 0.6 * MB));
        Assert.Equal(10 / 4.55, SpeedGraphEase.From(4 * MB, 10 * MB, before: 2.9 * MB, whileEasing: 4.55 * MB), 6);
        // Shrinking: squeezed, at most MaxStretch.
        Assert.Equal(0.5, SpeedGraphEase.From(4 * MB, 2 * MB, before: MB, whileEasing: MB));
        Assert.Equal(1 / SpeedGraphEase.MaxStretch, SpeedGraphEase.From(40 * MB, 2 * MB, before: MB, whileEasing: MB));
        Assert.Equal(1, SpeedGraphEase.From(4 * MB, 4 * MB, before: MB, whileEasing: MB));
        // The ease runs from its stretch to 1.
        Assert.Equal(2, SpeedGraphEase.At(2, TimeSpan.Zero));
        Assert.Equal(1, SpeedGraphEase.At(2, SpeedGraphEase.Duration));
        Assert.InRange(SpeedGraphEase.At(2, SpeedGraphEase.Duration / 2), 1, 1.2);
    }

    [Fact]
    public void Upload_line_follows_the_same_rules()
    {
        // T8: the scale is fitted to both lines as they are drawn, so an upload that starts later doesn't move it early.
        var down = Recording(heldSeconds: 0).Select(sample => (sample.Time, (double?)0)).ToList();
        var up = Recording(heldSeconds: 0);
        var now = up[^1].Time;
        var start = now.AddSeconds(-Period);
        var (lines, peaks) = SpeedGraphDrawing.Draw([new(down, true), new(up, true)], start, now, 1);
        // Up to now + 1 s the upload line reaches 9.5 MB/s, not the newest 11.8.
        Assert.Equal(9.5 * MB, peaks.Shown, 3);
        Assert.Equal(SpeedGraphDrawing.Draw([new(up, true)], start, now, 1).Peaks, peaks);
        // Neither line is drawn above the fitted figure, past the reveal included.
        Assert.All(lines.SelectMany(line => line), point => Assert.True((point.Rate ?? 0) <= peaks.Shown + 1e-6));
    }
}
