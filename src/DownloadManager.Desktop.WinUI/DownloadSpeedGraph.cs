using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace DownloadManager.Desktop.WinUI;

/// <summary>Session download-rate history. A missing sample creates a gap rather than invented zero traffic.</summary>
public sealed class DownloadSpeedGraph : Grid
{
    private readonly Queue<(DateTimeOffset Time, double? Rate)> _samples = new();
    private readonly Canvas _plot = new() { MinHeight = 100, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _rate = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _period = new() { MinWidth = 115 };
    private readonly int[] _periods = [60, 300, 1800, 3600];
    private int _seconds = 1800;

    public DownloadSpeedGraph()
    {
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Height = 190;
        Padding = new Thickness(12, 4, 12, 4);
        var header = new Grid { ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.Children.Add(_period);
        SetColumn(_rate, 1);
        header.Children.Add(_rate);
        foreach (var name in new[] { "1 minute", "5 minutes", "30 minutes", "1 hour" }) _period.Items.Add(name);
        _period.SelectedIndex = 2;
        _period.SelectionChanged += (_, _) =>
        {
            if (_period.SelectedIndex >= 0) _seconds = _periods[_period.SelectedIndex];
            Draw();
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_period, "Download graph period");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_plot, "Download speed history for this session");
        Children.Add(header);
        SetRow(_plot, 1);
        Children.Add(_plot);
        _plot.SizeChanged += (_, _) => Draw();
        ActualThemeChanged += (_, _) => Draw();
    }

    public void AddSample(DateTimeOffset time, double? bytesPerSecond)
    {
        var value = bytesPerSecond is { } rate && double.IsFinite(rate) && rate >= 0 ? rate : (double?)null;
        if (_samples.Count > 0)
        {
            var previous = _samples.Last().Time;
            if (time <= previous) _samples.Clear();
            else if (time - previous > TimeSpan.FromSeconds(5)) _samples.Enqueue((previous.AddSeconds(1), null));
        }
        _samples.Enqueue((time, value));
        while (_samples.Count > 3601 || _samples.TryPeek(out var first) && first.Time < time.AddHours(-1)) _samples.Dequeue();
        Draw();
    }

    private static string RateText(double rate)
    {
        string[] units = ["B/s", "KiB/s", "MiB/s", "GiB/s", "TiB/s"];
        var unit = 0;
        while (rate >= 1024 && unit < units.Length - 1) { rate /= 1024; unit++; }
        return $"{rate:0.#} {units[unit]}";
    }

    private void Draw()
    {
        _plot.Children.Clear();
        var width = _plot.ActualWidth;
        var height = _plot.ActualHeight;
        if (width < 150 || height < 60) return;
        var now = _samples.LastOrDefault().Time;
        if (now == default) now = DateTimeOffset.Now;
        var start = now.AddSeconds(-_seconds);
        var visible = _samples.Where(sample => sample.Time >= start).ToArray();
        var known = visible.Where(sample => sample.Rate.HasValue).Select(sample => sample.Rate!.Value).ToArray();
        var peak = known.Length == 0 ? 0 : known.Max();
        var average = known.Length == 0 ? 0 : known.Average();
        var latest = visible.LastOrDefault().Rate;
        _rate.Text = $"Download: {(latest is { } current ? RateText(current) : "unavailable")}   ·   Average {RateText(average)}   ·   Peak {RateText(peak)}";
        var top = peak <= 0 ? 1024 : Math.Pow(10, Math.Floor(Math.Log10(peak)));
        top *= Math.Ceiling(Math.Max(peak, top) / top);
        if (!double.IsFinite(top) || top <= 0) top = Math.Max(peak, 1024);
        const double left = 77, upper = 8, lower = 22;
        var chartWidth = width - left - 8;
        var chartHeight = height - upper - lower;
        var dark = ActualTheme == ElementTheme.Dark;
        var foreground = new SolidColorBrush(dark ? Windows.UI.Color.FromArgb(255, 192, 192, 192) : Windows.UI.Color.FromArgb(255, 85, 85, 85));
        var gridBrush = new SolidColorBrush(dark ? Windows.UI.Color.FromArgb(255, 70, 70, 70) : Windows.UI.Color.FromArgb(255, 205, 205, 205));
        void Label(string text, double x, double y)
        {
            var label = new TextBlock { Text = text, FontSize = 10, Foreground = foreground };
            Canvas.SetLeft(label, x); Canvas.SetTop(label, y); _plot.Children.Add(label);
        }
        for (var row = 0; row <= 4; row++)
        {
            var y = upper + row * chartHeight / 4;
            _plot.Children.Add(new Line { X1 = left, X2 = left + chartWidth, Y1 = y, Y2 = y,
                Stroke = gridBrush, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 3 } });
            Label(RateText(top * (4 - row) / 4), 0, y - 7);
        }
        for (var column = 0; column <= 4; column++)
        {
            var x = left + column * chartWidth / 4;
            _plot.Children.Add(new Line { X1 = x, X2 = x, Y1 = upper, Y2 = upper + chartHeight,
                Stroke = gridBrush, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 3 } });
            var minutes = _seconds * (4 - column) / 240d;
            Label(column == 4 ? "Now" : $"−{minutes:0.#} min", Math.Clamp(x - 18, left, width - 40), upper + chartHeight + 3);
        }
        var color = ActualTheme == ElementTheme.Dark ? Windows.UI.Color.FromArgb(255, 143, 211, 72) : Windows.UI.Color.FromArgb(255, 62, 133, 18);
        var stroke = new SolidColorBrush(color);
        Polyline? line = null;
        // Keep peaks/troughs when a period contains more samples than horizontal pixels.
        var bucketWidth = Math.Max(1, (int)chartWidth);
        var compact = visible.GroupBy(sample => (int)((sample.Time - start).TotalSeconds / _seconds * bucketWidth))
            .SelectMany(bucket => bucket.Any(sample => sample.Rate is null)
                ? (IEnumerable<(DateTimeOffset Time, double? Rate)>)bucket
                : bucket.OrderBy(sample => sample.Rate).Take(1).Concat(bucket.OrderByDescending(sample => sample.Rate).Take(1))
                    .Distinct().OrderBy(sample => sample.Time));
        foreach (var sample in compact)
        {
            if (sample.Rate is null) { line = null; continue; }
            if (line is null)
            {
                line = new Polyline { Stroke = stroke, StrokeThickness = 1.5, Points = new PointCollection() };
                _plot.Children.Add(line);
            }
            line.Points.Add(new Point(left + Math.Clamp((sample.Time - start).TotalSeconds / _seconds, 0, 1) * chartWidth,
                upper + chartHeight * (1 - Math.Clamp(sample.Rate.Value / top, 0, 1))));
        }
    }
}
