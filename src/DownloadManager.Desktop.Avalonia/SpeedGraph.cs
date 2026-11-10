using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace DownloadManager.AvaloniaUi;

/// <summary>
/// The slim "Download speed" bar with the plot that opens upward from it, as the WinUI DownloadSpeedGraph.
/// The plot is one control that draws itself in <see cref="Plot.Render"/>: no child shapes are made per sample.
/// A missing sample is a gap, not invented zero traffic.
/// </summary>
internal sealed class SpeedGraph : Grid
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
    private readonly Plot _plot;
    private readonly Border _plotPanel;
    private readonly TextBlock _current = Summary();
    private readonly TextBlock _average = Summary();
    private readonly TextBlock _peak = Summary();
    private readonly TextBlock _periodText = new() { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _chevron;
    private int _seconds = 1800;
    private bool _expanded = true;
    private bool _seen = true;
    private bool _stale;

    public SpeedGraph()
    {
        RowDefinitions = new RowDefinitions("Auto,23");
        _plot = new Plot(this) { Height = OpenHeight - 1, Margin = new Thickness(8, 0) };
        _plotPanel = new Border
        {
            Height = OpenHeight,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = _plot,
            ClipToBounds = true,
        };
        _plotPanel.Bind(Border.BackgroundProperty, this.GetResourceObservable("InboundSurfaceBrush"));
        _plotPanel.Bind(Border.BorderBrushProperty, this.GetResourceObservable("InboundDividerBrush"));
        Children.Add(_plotPanel);

        var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var barBorder = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Child = bar };
        barBorder.Bind(Border.BackgroundProperty, this.GetResourceObservable("InboundChromeBrush"));
        barBorder.Bind(Border.BorderBrushProperty, this.GetResourceObservable("InboundDividerBrush"));
        SetRow(barBorder, 1);
        Children.Add(barBorder);

        _chevron = new TextBlock { Text = "", FontSize = 9, Classes = { "icon" }, RenderTransformOrigin = RelativePoint.Center };
        _chevron.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("InboundSecondaryTextBrush"));
        var toggleContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        toggleContent.Children.Add(_chevron);
        toggleContent.Children.Add(new TextBlock { Text = "Download speed", FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        var summary = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        summary.Children.Add(_current);
        summary.Children.Add(Dim("   ·   Average "));
        summary.Children.Add(_average);
        summary.Children.Add(Dim("   ·   Peak "));
        summary.Children.Add(_peak);
        toggleContent.Children.Add(summary);
        var toggle = new Button
        {
            Classes = { "flat" },
            Content = toggleContent,
            Padding = new Thickness(8, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
        };
        toggle.Click += (_, _) => IsExpanded = !IsExpanded;
        bar.Children.Add(toggle);

        var periodMenu = new MenuFlyout { Placement = PlacementMode.TopEdgeAlignedRight };
        foreach (var period in Periods)
        {
            var item = new MenuItem { Header = period.Name, ToggleType = MenuItemToggleType.Radio, GroupName = "graphPeriod", IsChecked = period.Seconds == _seconds };
            item.Click += (_, _) => { _seconds = period.Seconds; _periodText.Text = period.Short; Draw(); };
            periodMenu.Items.Add(item);
            if (period.Seconds == _seconds) _periodText.Text = period.Short;
        }
        var periodContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        periodContent.Children.Add(_periodText);
        periodContent.Children.Add(new TextBlock { Text = "", FontSize = 8, Classes = { "icon" } });
        var periodButton = new Button { Classes = { "flat" }, Content = periodContent, Padding = new Thickness(8, 0), VerticalAlignment = VerticalAlignment.Stretch, Flyout = periodMenu };
        ToolTip.SetTip(periodButton, "How much history the graph, average and peak cover");
        SetColumn(periodButton, 1);
        bar.Children.Add(periodButton);
        ShowChevron();
    }

    public bool IsExpanded
    {
        get => _expanded;
        set
        {
            if (_expanded == value) return;
            _expanded = value;
            _plotPanel.IsVisible = value;
            ShowChevron();
            Draw();
        }
    }

    /// <summary>False while nobody can see the graph: samples are kept, nothing is drawn.</summary>
    public bool IsSeen
    {
        get => _seen;
        set
        {
            if (_seen == value) return;
            _seen = value;
            if (value && _stale) Draw();
        }
    }

    public void AddSample(DateTimeOffset time, double? bytesPerSecond)
    {
        var value = bytesPerSecond is { } rate && double.IsFinite(rate) && rate >= 0 ? rate : (double?)null;
        if (_samples.Count > 0)
        {
            var previous = _samples.Last().Time;
            if (time <= previous) _samples.Clear();
        }
        _samples.Enqueue((time, value));
        Trim(time);
        Draw();
    }

    public void LoadHistory(DateTimeOffset start, int intervalSeconds, IReadOnlyList<long> bytesPerSecond)
    {
        if (bytesPerSecond.Count == 0 || intervalSeconds <= 0) return;
        var end = start.AddSeconds((bytesPerSecond.Count - 1) * (double)intervalSeconds);
        var before = _samples.Where(sample => sample.Time < start).ToList();
        var after = _samples.Where(sample => sample.Time > end).ToList();
        _samples.Clear();
        foreach (var sample in before) _samples.Enqueue(sample);
        for (var i = 0; i < bytesPerSecond.Count; i++)
            _samples.Enqueue((start.AddSeconds(i * (double)intervalSeconds), bytesPerSecond[i] >= 0 ? bytesPerSecond[i] : null));
        foreach (var sample in after) _samples.Enqueue(sample);
        Trim(_samples.Last().Time);
        Draw();
    }

    private void Trim(DateTimeOffset latest)
    {
        while (_samples.Count > 3601 || _samples.TryPeek(out var first) && first.Time < latest.AddHours(-1)) _samples.Dequeue();
    }

    private void ShowChevron() => _chevron.RenderTransform = new RotateTransform(_expanded ? 0 : 180);

    private void Draw()
    {
        if (!_seen) { _stale = true; return; }
        _stale = false;
        var (visible, start, _) = Window();
        var known = visible.Where(sample => sample.Rate.HasValue).Select(sample => sample.Rate!.Value).ToArray();
        var latest = visible.LastOrDefault().Rate;
        SetText(_current, latest is { } current ? RateText(current) : "—");
        SetText(_average, RateText(known.Length == 0 ? 0 : known.Average()));
        SetText(_peak, RateText(known.Length == 0 ? 0 : known.Max()));
        if (_expanded) _plot.InvalidateVisual();
    }

    private ((DateTimeOffset Time, double? Rate)[] Visible, DateTimeOffset Start, double Peak) Window()
    {
        var now = _samples.Count > 0 ? _samples.Last().Time : DateTimeOffset.UtcNow;
        var start = now.AddSeconds(-_seconds);
        var visible = _samples.Where(sample => sample.Time >= start).ToArray();
        var peak = visible.Where(sample => sample.Rate.HasValue).Select(sample => sample.Rate!.Value).DefaultIfEmpty(0).Max();
        return (visible, start, peak);
    }

    private static void SetText(TextBlock block, string text)
    {
        if (block.Text != text) block.Text = text;
    }

    private static TextBlock Summary() => new() { FontSize = 11, MinWidth = 64, TextAlignment = TextAlignment.Right, Classes = { "dim" } };

    private TextBlock Dim(string text)
    {
        var block = new TextBlock { Text = text, FontSize = 11 };
        block.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("InboundSecondaryTextBrush"));
        return block;
    }

    internal static string RateText(double rate)
    {
        string[] units = ["B/s", "KB/s", "MB/s", "GB/s", "TB/s"];
        var unit = 0;
        while (rate >= 1024 && unit < units.Length - 1) { rate /= 1024; unit++; }
        return $"{rate:0.#} {units[unit]}";
    }

    private sealed class Plot(SpeedGraph owner) : Control
    {
        private static readonly Typeface LabelFace = new(FontFamily.Default);

        public override void Render(DrawingContext context)
        {
            var width = Bounds.Width;
            var height = Bounds.Height;
            if (width < 150 || height < 60) return;
            var (visible, start, peak) = owner.Window();
            var seconds = owner._seconds;
            var top = peak <= 0 ? 1024 : Math.Pow(10, Math.Floor(Math.Log10(peak)));
            top *= Math.Ceiling(Math.Max(peak, top) / top);
            if (!double.IsFinite(top) || top <= 0) top = Math.Max(peak, 1024);
            const double left = 62, upper = 8, lower = 16;
            const int rows = 2, columns = 4;
            var chartWidth = width - left - 8;
            var chartHeight = height - upper - lower;
            var text = Brush("InboundSecondaryTextBrush");
            var grid = new Pen(Brush("InboundDividerBrush"), 1, new DashStyle([3, 3], 0));
            void Label(string words, double x, double y)
                => context.DrawText(new FormattedText(words, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, LabelFace, 10, text), new Point(x, y));
            for (var row = 0; row <= rows; row++)
            {
                var y = upper + row * chartHeight / rows;
                context.DrawLine(grid, new Point(left, y), new Point(left + chartWidth, y));
                Label(RateText(top * (rows - row) / rows), 0, y - 7);
            }
            for (var column = 0; column <= columns; column++)
            {
                var x = left + column * chartWidth / columns;
                context.DrawLine(grid, new Point(x, upper), new Point(x, upper + chartHeight));
                var minutes = seconds * (columns - column) / (60d * columns);
                Label(column == columns ? "Now" : $"−{minutes:0.#} min", Math.Clamp(x - 18, left, width - 40), upper + chartHeight + 1);
            }

            var stroke = new Pen(Brush("InboundProgressFillBrush"), 1.5);
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                var open = false;
                foreach (var sample in visible)
                {
                    if (sample.Rate is null)
                    {
                        if (open) g.EndFigure(false);
                        open = false;
                        continue;
                    }
                    var point = new Point(left + Math.Clamp((sample.Time - start).TotalSeconds / seconds, 0, 1) * chartWidth,
                        upper + chartHeight * (1 - Math.Clamp(sample.Rate.Value / top, 0, 1)));
                    if (!open) { g.BeginFigure(point, false); open = true; }
                    else g.LineTo(point);
                }
                if (open) g.EndFigure(false);
            }
            context.DrawGeometry(null, stroke, geometry);
        }

        private IBrush Brush(string key)
            => this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : Brushes.Gray;
    }
}
