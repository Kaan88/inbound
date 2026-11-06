using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// Session download-rate history: a slim bar with the current figures, and a plot that opens upward
/// from it. A missing sample creates a gap rather than invented zero traffic.
/// </summary>
public sealed partial class DownloadSpeedGraph : UserControl
{
    private const double OpenHeight = 85;
    private static readonly (int Seconds, string Name, string Short)[] Periods =
    [
        (60, "1 minute", "1 min"),
        (300, "5 minutes", "5 min"),
        (1800, "30 minutes", "30 min"),
        (3600, "1 hour", "1 hour"),
    ];

    private readonly Queue<(DateTimeOffset Time, double? Rate)> _samples = new();
    private int _seconds = 1800;
    private bool _expanded = true;
    private Storyboard? _transition;

    public DownloadSpeedGraph()
    {
        InitializeComponent();
        foreach (var period in Periods)
        {
            var item = new RadioMenuFlyoutItem
            {
                Text = period.Name,
                GroupName = "DownloadGraphPeriod",
                IsChecked = period.Seconds == _seconds,
            };
            item.Click += (_, _) =>
            {
                _seconds = period.Seconds;
                ShowPeriod(period.Short, period.Name);
                Draw();
            };
            PeriodMenu.Items.Add(item);
            if (item.IsChecked) ShowPeriod(period.Short, period.Name);
        }

        DescribeToggle();
        Plot.SizeChanged += (_, _) => Draw();
        ActualThemeChanged += (_, _) => Draw();
    }

    /// <summary>Raised when the user opens or closes the plot.</summary>
    public event EventHandler? ExpandedChanged;

    /// <summary>Setting this applies at once; only the user's own click animates.</summary>
    public bool IsExpanded
    {
        get => _expanded;
        set => SetExpanded(value, animate: false);
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

    private void ToggleButton_Click(object sender, RoutedEventArgs e)
    {
        SetExpanded(!_expanded, animate: true);
        ExpandedChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ShowPeriod(string shortName, string name)
    {
        PeriodText.Text = shortName;
        AutomationProperties.SetName(PeriodButton, $"Download graph period: {name}");
    }

    private void SetExpanded(bool expanded, bool animate)
    {
        if (_expanded == expanded) return;
        _expanded = expanded;
        DescribeToggle();
        // Drawn before it shows, since the plot is not kept up to date while closed.
        if (expanded) Draw();
        var height = expanded ? OpenHeight : 0;
        var angle = expanded ? 0 : 180;

        if (!animate || !new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
        {
            _transition?.Stop();
            _transition = null;
            PlotPanel.Height = height;
            ChevronRotation.Angle = angle;
            return;
        }

        // No "From": a click during the animation carries on from wherever it has got to.
        var duration = new Duration(TimeSpan.FromMilliseconds(180));
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var heightAnimation = new DoubleAnimation
        {
            To = height,
            Duration = duration,
            EasingFunction = easing,
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(heightAnimation, PlotPanel);
        Storyboard.SetTargetProperty(heightAnimation, nameof(Height));
        var chevronAnimation = new DoubleAnimation { To = angle, Duration = duration, EasingFunction = easing };
        Storyboard.SetTarget(chevronAnimation, ChevronRotation);
        Storyboard.SetTargetProperty(chevronAnimation, nameof(RotateTransform.Angle));
        _transition = new Storyboard();
        _transition.Children.Add(heightAnimation);
        _transition.Children.Add(chevronAnimation);
        _transition.Begin();
    }

    private void DescribeToggle()
    {
        var text = _expanded ? "Hide download speed graph" : "Show download speed graph";
        AutomationProperties.SetName(ToggleButton, text);
        ToolTipService.SetToolTip(ToggleButton, text);
    }

    private static string RateText(double rate)
    {
        string[] units = ["B/s", "KB/s", "MB/s", "GB/s", "TB/s"];
        var unit = 0;
        while (rate >= 1024 && unit < units.Length - 1) { rate /= 1024; unit++; }
        return $"{rate:0.#} {units[unit]}";
    }

    private void Draw()
    {
        var now = _samples.LastOrDefault().Time;
        if (now == default) now = DateTimeOffset.Now;
        var start = now.AddSeconds(-_seconds);
        var visible = _samples.Where(sample => sample.Time >= start).ToArray();
        var known = visible.Where(sample => sample.Rate.HasValue).Select(sample => sample.Rate!.Value).ToArray();
        var peak = known.Length == 0 ? 0 : known.Max();
        var average = known.Length == 0 ? 0 : known.Average();
        var latest = visible.LastOrDefault().Rate;
        Summary.Text = $"{(latest is { } current ? RateText(current) : "—")}   ·   Average {RateText(average)}   ·   Peak {RateText(peak)}";

        if (!_expanded) return;
        Plot.Children.Clear();
        var width = Plot.ActualWidth;
        var height = Plot.ActualHeight;
        if (width < 150 || height < 60) return;
        var top = peak <= 0 ? 1024 : Math.Pow(10, Math.Floor(Math.Log10(peak)));
        top *= Math.Ceiling(Math.Max(peak, top) / top);
        if (!double.IsFinite(top) || top <= 0) top = Math.Max(peak, 1024);
        const double left = 62, upper = 8, lower = 16;
        const int rows = 2, columns = 4;
        var chartWidth = width - left - 8;
        var chartHeight = height - upper - lower;
        var foreground = InboundTheme.Brush("InboundSecondaryTextBrush", this);
        var gridBrush = InboundTheme.Brush("InboundDividerBrush", this);
        void Label(string text, double x, double y)
        {
            var label = new TextBlock { Text = text, FontSize = 10, Foreground = foreground };
            Canvas.SetLeft(label, x); Canvas.SetTop(label, y); Plot.Children.Add(label);
        }
        for (var row = 0; row <= rows; row++)
        {
            var y = upper + row * chartHeight / rows;
            Plot.Children.Add(new Line { X1 = left, X2 = left + chartWidth, Y1 = y, Y2 = y,
                Stroke = gridBrush, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 3 } });
            Label(RateText(top * (rows - row) / rows), 0, y - 7);
        }
        for (var column = 0; column <= columns; column++)
        {
            var x = left + column * chartWidth / columns;
            Plot.Children.Add(new Line { X1 = x, X2 = x, Y1 = upper, Y2 = upper + chartHeight,
                Stroke = gridBrush, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 3 } });
            var minutes = _seconds * (columns - column) / (60d * columns);
            Label(column == columns ? "Now" : $"−{minutes:0.#} min", Math.Clamp(x - 18, left, width - 40), upper + chartHeight + 1);
        }
        var stroke = InboundTheme.Brush("InboundProgressFillBrush", this);
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
                Plot.Children.Add(line);
            }
            line.Points.Add(new Point(left + Math.Clamp((sample.Time - start).TotalSeconds / _seconds, 0, 1) * chartWidth,
                upper + chartHeight * (1 - Math.Clamp(sample.Rate.Value / top, 0, 1))));
        }
    }
}
