using DownloadManager.Desktop.WinUI;

namespace DownloadManager.Core.Tests;

/// <summary>
/// UI.B40: with the visible line at zero the graph must not rescale, ease or flash when a download starts (UI.B26), and the
/// collapsed graph must not draw its plot outside its slim bar. The scale and ease are checked on the model the control
/// draws with; the layout is checked in the control's source, since a WinUI layout can't be run here.
/// </summary>
public sealed class SpeedGraphZeroLineTests
{
    private const double MB = 1024 * 1024;
    private const int Period = 1800;
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    /// <summary>The drawings of the control as each sample comes in: top, ease stretch and the highest figure shown.</summary>
    private static List<(double Top, double Ease, double Shown)> Drawings(List<(DateTimeOffset Time, double? Rate)> samples, int bucket)
    {
        var scale = new SpeedGraphScale();
        var result = new List<(double, double, double)>();
        double drawnTop = 0;
        var first = true;
        foreach (var (now, _) in samples)
        {
            var start = now.AddSeconds(-Period);
            var lineStart = start - SpeedGraphPoints.Lag(bucket) - TimeSpan.FromSeconds(bucket);
            var (_, peaks) = SpeedGraphDrawing.Draw(
                [new(samples.Where(s => s.Time >= lineStart && s.Time <= now).ToArray(), true)], start, now, bucket);
            var top = first ? scale.Reset(peaks.Shown) : scale.Update(peaks.Shown, now);
            first = false;
            result.Add((top, SpeedGraphEase.From(drawnTop, top, peaks.Before, peaks.WhileEasing), peaks.Shown));
            drawnTop = top;
        }
        return result;
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(1, true)]
    public void Zero_line_keeps_its_scale_and_does_not_ease_until_a_figure_is_in_view(int bucket, bool afterIdleGap)
    {
        var samples = new List<(DateTimeOffset Time, double? Rate)>();
        for (var i = -120; i <= -10; i++) samples.Add((T0.AddSeconds(i), 0d));
        // After an idle stretch the timer had stopped: a gap, then the download's first samples.
        if (afterIdleGap) samples.Add((T0.AddSeconds(-9), null));
        for (var i = afterIdleGap ? -8 : -9; i < 0; i++) samples.Add((T0.AddSeconds(i), 0d));
        samples.Add((T0, 0d));
        samples.Add((T0.AddSeconds(1), 4 * MB));
        samples.Add((T0.AddSeconds(2), 6 * MB));
        samples.Add((T0.AddSeconds(3), 6 * MB));
        samples.Add((T0.AddSeconds(4), 6 * MB));

        var drawings = Drawings(samples, bucket);
        // Every drawing whose visible line is still zero keeps the smallest scale and is not stretched.
        var zero = drawings.TakeWhile(d => d.Shown == 0).ToList();
        Assert.True(zero.Count >= samples.Count - 5 - (bucket > 1 ? 4 : 0), "the zero stretch is drawn before the figures come into view");
        Assert.All(zero, d =>
        {
            Assert.Equal(SpeedGraphScale.Minimum, d.Top);
            Assert.Equal(1, d.Ease);
        });
        // Once the figures show, the scale grows with them.
        if (bucket == 1) Assert.True(drawings[^1].Top >= 6 * MB);
        Assert.All(drawings, d => Assert.InRange(d.Ease, 1 / SpeedGraphEase.MaxStretch, SpeedGraphEase.MaxStretch));
    }

    [Fact]
    public void Scale_holds_while_every_visible_point_is_zero()
    {
        var scale = new SpeedGraphScale();
        Assert.Equal(SpeedGraphScale.Minimum, scale.Reset(0));
        for (var i = 0; i < 600; i++) Assert.Equal(SpeedGraphScale.Minimum, scale.Update(0, T0.AddSeconds(i)));
    }

    private static string WinUiFile(string name)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "DownloadManager.slnx"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!, "src", "DownloadManager.Desktop.WinUI", name));
    }

    [Fact]
    public void Collapsed_graph_clips_its_plot_to_the_panel_and_keeps_it_out_of_the_status_bar()
    {
        var code = WinUiFile("DownloadSpeedGraph.xaml.cs");
        // The panel is clipped to its own (animated) size, so the plot, its labels and the legend never draw below it.
        Assert.Contains("PlotPanel.SizeChanged", code);
        Assert.Contains("PlotPanel.Clip", code);
        // Nothing is drawn into the plot while it is closed.
        Assert.Contains("if (!_expanded) { StopSlide(); return; }", code);
        // The graph keeps its own row in the main window, under the list and above the status bar's row.
        var xaml = WinUiFile("MainWindow.xaml");
        Assert.Contains("<local:DownloadSpeedGraph x:Name=\"SpeedGraph\" Grid.Row=\"4\" />", xaml);
        Assert.Contains("<Border Grid.Row=\"5\"", xaml);
    }
}
