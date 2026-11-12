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
/// <para>T8: the main window also gives it the torrents' upload, drawn as a second line in its own color with a legend
/// and its figure on the bar, while <see cref="ShowUpload"/> is set (a torrent downloads or seeds).</para>
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
    /// <summary>T8: the upload figures, taken at the same times as <see cref="_samples"/>.</summary>
    private readonly Queue<(DateTimeOffset Time, double? Rate)> _upSamples = new();
    private bool _showUpload;
    private string _heading = SpeedGraphUpload.DownloadHeading;
    private double? _liveUpload;
    private int _seconds = 1800;
    private bool _expanded = true;
    private bool _seen = true;
    private bool _stale;
    /// <summary>UI.B23: the scale moves with hysteresis; this makes the next drawing fit it at once instead.</summary>
    private bool _refit = true;
    private readonly SpeedGraphScale _scale = new();
    private Storyboard? _rescale;
    private int _bucketSeconds = 1;
    /// <summary>The top of the last drawing with a line, so a change of scale can ease the line into place.</summary>
    private double _drawnTop;
    private Storyboard? _transition;
    private Storyboard? _slide;
    private double _pixelsPerSecond;
    /// <summary>When the newest sample came in (<see cref="Environment.TickCount64"/>), so a redraw between samples resumes the slide where it is.</summary>
    private long _sampledAt = Environment.TickCount64;

    // Where the last drawing put things, for the hover layer.
    private double _left, _upper, _chartWidth, _chartHeight, _top;
    private DateTimeOffset _start;
    private bool _drawn;
    private double? _pointerX;

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
                _refit = true;
                ShowPeriod(period.Short, period.Name);
                Draw();
            };
            PeriodMenu.Items.Add(item);
            if (item.IsChecked) ShowPeriod(period.Short, period.Name);
        }

        DescribeToggle();
        Plot.SizeChanged += (_, _) => Draw();
        ActualThemeChanged += (_, _) => Draw();
        Plot.PointerMoved += (_, e) => { _pointerX = e.GetCurrentPoint(Plot).Position.X; DrawHover(); };
        Plot.PointerExited += (_, _) => { _pointerX = null; DrawHover(); };
        Plot.PointerCanceled += (_, _) => { _pointerX = null; DrawHover(); };
    }

    /// <summary>Raised when the user opens or closes the plot.</summary>
    public event EventHandler? ExpandedChanged;

    /// <summary>Setting this applies at once; only the user's own click animates.</summary>
    public bool IsExpanded
    {
        get => _expanded;
        set => SetExpanded(value, animate: false);
    }

    /// <summary>
    /// False while nobody can see the graph (window hidden or minimised, graph turned off): samples are still kept,
    /// but nothing is drawn until it can be seen again, so a hidden window does no layout work every second.
    /// </summary>
    public bool IsSeen
    {
        get => _seen;
        set
        {
            if (_seen == value) return;
            _seen = value;
            // Nothing moves while nobody can see it.
            if (!value) StopSlide();
            // The scale wasn't kept up while hidden; it fits what is there at once rather than stepping down in view.
            if (value && _stale) { _refit = true; Draw(); }
        }
    }

    /// <summary>The period shown, in seconds: one of the menu's (1 min, 5 min, 30 min, 1 hour).</summary>
    public int PeriodSeconds
    {
        get => _seconds;
        set
        {
            var period = Periods.FirstOrDefault(candidate => candidate.Seconds == value);
            if (period.Seconds == 0 || period.Seconds == _seconds) return;
            _seconds = period.Seconds;
            _refit = true;
            foreach (var item in PeriodMenu.Items.OfType<RadioMenuFlyoutItem>()) item.IsChecked = item.Text == period.Name;
            ShowPeriod(period.Short, period.Name);
            Draw();
        }
    }

    /// <summary>The words on the bar, and the name screen readers give the plot.</summary>
    public string Heading
    {
        set
        {
            _heading = value;
            DescribeHeading();
        }
    }

    private void DescribeHeading()
    {
        HeadingText.Text = SpeedGraphUpload.Heading(_heading, _showUpload);
        AutomationProperties.SetName(Plot, SpeedGraphUpload.PlotName(_heading, _showUpload));
    }

    /// <summary>
    /// T8: the torrents' upload now, worked out from the jobs (null while the Host can't be reached). The bar shows it
    /// while the graph has no upload sample (<see cref="SpeedSample.UploadFigure"/>). Set as jobs change, not on a timer.
    /// </summary>
    public double? LiveUpload
    {
        set
        {
            if (_liveUpload == value) return;
            _liveUpload = value;
            if (_showUpload) ShowUploadFigure();
        }
    }

    private void ShowUploadFigure()
    {
        var upText = SpeedSample.UploadFigure(_upSamples.LastOrDefault().Rate, _liveUpload) is { } up ? RateText(up) : "—";
        if (UploadCurrent.Text != upText) UploadCurrent.Text = upText;
        AutomationProperties.SetName(UploadSummary, $"Upload {upText}");
    }

    /// <summary>
    /// T8: shows the upload line, its legend and its figure (while a torrent downloads or seeds). Its samples are kept
    /// while it is hidden, so it comes back with its history.
    /// </summary>
    public bool ShowUpload
    {
        get => _showUpload;
        set
        {
            if (_showUpload == value) return;
            _showUpload = value;
            var visibility = value ? Visibility.Visible : Visibility.Collapsed;
            UploadSummary.Visibility = visibility;
            Legend.Visibility = visibility;
            DescribeHeading();
            DescribeToggle();
            _refit = true;
            Draw();
        }
    }

    /// <summary>A sample; <paramref name="uploadBytesPerSecond"/> is the torrents' upload (null: none known, a gap).</summary>
    public void AddSample(DateTimeOffset time, double? bytesPerSecond, double? uploadBytesPerSecond = null)
    {
        Append(time, bytesPerSecond, uploadBytesPerSecond);
        Draw();
    }

    /// <summary>Audit M11: several samples (oldest first), drawn once, such as the zeros of a pause that took no samples.</summary>
    public void AddSamples(IEnumerable<(DateTimeOffset Time, double? BytesPerSecond)> samples)
    {
        var any = false;
        foreach (var (time, rate) in samples)
        {
            Append(time, rate, null);
            any = true;
        }
        if (any) Draw();
    }

    private void Append(DateTimeOffset time, double? bytesPerSecond, double? uploadBytesPerSecond)
    {
        Append(_samples, time, bytesPerSecond);
        Append(_upSamples, time, uploadBytesPerSecond);
        _sampledAt = Environment.TickCount64;
    }

    private static void Append(Queue<(DateTimeOffset Time, double? Rate)> samples, DateTimeOffset time, double? bytesPerSecond)
    {
        var value = bytesPerSecond is { } rate && double.IsFinite(rate) && rate >= 0 ? rate : (double?)null;
        if (samples.Count > 0)
        {
            var previous = samples.Last().Time;
            if (time <= previous) samples.Clear();
            else if (time - previous > TimeSpan.FromSeconds(5)) samples.Enqueue((previous.AddSeconds(1), null));
        }
        samples.Enqueue((time, value));
        while (samples.Count > 3601 || samples.TryPeek(out var first) && first.Time < time.AddHours(-1)) samples.Dequeue();
    }

    /// <summary>
    /// UI.R24: the Host's figures, one per <paramref name="intervalSeconds"/> from <paramref name="start"/>, replace this
    /// window's own samples for the time they cover. Older samples stay, with a gap before the Host's start if there was
    /// one; newer ones (taken while the request was out) stay after them.
    /// </summary>
    /// <remarks>
    /// T8: <paramref name="uploadBytesPerSecond"/> is the Host's upload for the same seconds. A Host before T8 sends
    /// none, and one with no torrent upload since it started sends none either: the upload line keeps this window's own
    /// samples then.
    /// </remarks>
    public void LoadHistory(DateTimeOffset start, int intervalSeconds, IReadOnlyList<long> bytesPerSecond, IReadOnlyList<long>? uploadBytesPerSecond = null)
    {
        if (bytesPerSecond.Count == 0 || intervalSeconds <= 0) return;
        var after = Load(_samples, start, intervalSeconds, bytesPerSecond);
        if (SpeedGraphUpload.UploadLinesUp(bytesPerSecond, uploadBytesPerSecond))
            Load(_upSamples, start, intervalSeconds, uploadBytesPerSecond!);
        if (after == 0) _sampledAt = Environment.TickCount64;
        _refit = true;
        Draw();
    }

    /// <summary>Puts the Host's figures in place of <paramref name="samples"/>' own for the time they cover; returns how many newer ones stayed.</summary>
    private static int Load(Queue<(DateTimeOffset Time, double? Rate)> samples, DateTimeOffset start, int intervalSeconds, IReadOnlyList<long> values)
    {
        var end = start.AddSeconds((values.Count - 1) * (double)intervalSeconds);
        var before = samples.Where(sample => sample.Time < start).ToList();
        var after = samples.Where(sample => sample.Time > end).ToList();
        samples.Clear();
        foreach (var sample in before) samples.Enqueue(sample);
        if (before.Count > 0 && start - before[^1].Time > TimeSpan.FromSeconds(5)) samples.Enqueue((before[^1].Time.AddSeconds(1), null));
        for (var i = 0; i < values.Count; i++)
        {
            var rate = values[i];
            samples.Enqueue((start.AddSeconds(i * (double)intervalSeconds), rate >= 0 ? (double?)rate : null));
        }
        foreach (var sample in after) samples.Enqueue(sample);
        var latest = samples.Last().Time;
        while (samples.Count > 3601 || samples.TryPeek(out var first) && first.Time < latest.AddHours(-1)) samples.Dequeue();
        return after.Count;
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
        if (expanded) { _refit = true; Draw(); }
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
        var text = SpeedGraphUpload.Toggle(_expanded, _showUpload);
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
        if (!_seen) { _stale = true; return; }
        _stale = false;
        var now = _samples.LastOrDefault().Time;
        if (now == default) now = DateTimeOffset.Now;
        var start = now.AddSeconds(-_seconds);
        var visible = _samples.Where(sample => sample.Time >= start).ToArray();
        var known = visible.Where(sample => sample.Rate.HasValue).Select(sample => sample.Rate!.Value).ToArray();
        var peak = known.Length == 0 ? 0 : known.Max();
        var average = known.Length == 0 ? 0 : known.Average();
        var latest = visible.LastOrDefault().Rate;
        var currentText = latest is { } current ? RateText(current) : "—";
        var averageText = RateText(average);
        var peakText = RateText(peak);
        if (SummaryCurrent.Text != currentText) SummaryCurrent.Text = currentText;
        if (SummaryAverage.Text != averageText) SummaryAverage.Text = averageText;
        if (SummaryPeak.Text != peakText) SummaryPeak.Text = peakText;
        AutomationProperties.SetName(Summary, $"{currentText}, average {averageText}, peak {peakText}");
        if (_showUpload) ShowUploadFigure();

        if (!_expanded) { StopSlide(); return; }
        Plot.Children.Clear();
        Lines.Children.Clear();
        StopSlide();
        _drawn = false;
        var width = Plot.ActualWidth;
        var height = Plot.ActualHeight;
        if (width < 150 || height < 60) { DrawHover(); return; }
        const double left = 62, upper = 8, lower = 16;
        const int rows = 2, columns = 4;
        var chartWidth = width - left - 8;
        var chartHeight = height - upper - lower;

        // UI.B23: points stand for clock-aligned buckets and are drawn a little late (SpeedGraphPoints), so the samples
        // that reach the left edge start a lag earlier than the period.
        var bucket = SpeedGraphPoints.BucketSeconds(_seconds, chartWidth);
        if (bucket != _bucketSeconds) { _bucketSeconds = bucket; _refit = true; }
        var lineStart = start - SpeedGraphPoints.Lag(bucket) - TimeSpan.FromSeconds(bucket);
        var drawnSamples = _samples.Where(sample => sample.Time >= lineStart).ToArray();
        // UI.B26: the lines this drawing shows and one scale for both (T8), fitted to what is drawn up to where the slide
        // goes before the next sample, not to samples the line shows only later, so the top doesn't grow while the line
        // still lies at zero. Past that the line is held to the fitted figure.
        SpeedGraphDrawing.Series[] series = _showUpload
            ? [new(drawnSamples, _samples.LastOrDefault().Rate is not null),
               new(_upSamples.Where(sample => sample.Time >= lineStart).ToArray(), _upSamples.LastOrDefault().Rate is not null)]
            : [new(drawnSamples, _samples.LastOrDefault().Rate is not null)];
        var (lines, peaks) = SpeedGraphDrawing.Draw(series, start, now, bucket);
        var downLine = lines[0];
        var upLine = _showUpload ? lines[1] : [];
        var previousTop = _drawnTop;
        // UI.B23: grows at once, shrinks only after a hold and a step at a time, always to a round figure.
        var top = _refit ? _scale.Reset(peaks.Shown) : _scale.Update(peaks.Shown, now);
        _refit = false;
        LineClip.Clip = new RectangleGeometry { Rect = new Rect(left, 0, chartWidth, height) };
        (_left, _upper, _chartWidth, _chartHeight, _top, _start, _drawn) = (left, upper, chartWidth, chartHeight, top, start, true);
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
        // UI.B23: every point sits at a fixed place on the time axis and is never chosen again, so a new sample adds
        // to the right and moves nothing already drawn. Points past either edge are clipped, not squeezed onto it.
        double X(DateTimeOffset at) => left + (at - start).TotalSeconds / _seconds * chartWidth;
        // The line ends where the slide stops (SpeedGraphDrawing.Line): held to the fitted figure past the next sample's
        // due time, flat past the newest point should samples come late, rather than leaving a blank strip.
        void DrawLine(List<SpeedGraphPoints.Point> points, Brush stroke)
        {
            Polyline? line = null;
            foreach (var point in points)
            {
                if (point.Rate is null) { line = null; continue; }
                if (line is null)
                {
                    line = new Polyline { Stroke = stroke, StrokeThickness = 1.5, Points = new PointCollection() };
                    Lines.Children.Add(line);
                }
                line.Points.Add(new Point(X(point.At), upper + chartHeight * (1 - Math.Clamp(point.Rate.Value / top, 0, 1))));
            }
        }

        // T8: the upload line first, so the download line is on top where they meet.
        if (_showUpload) DrawLine(upLine, InboundTheme.Brush("InboundUploadBrush", this));
        DrawLine(downLine, InboundTheme.Brush("InboundProgressFillBrush", this));

        // A new sample moves the line under a pointer that is still there.
        DrawHover();
        Slide(chartWidth / _seconds);
        EaseScale(SpeedGraphEase.From(previousTop, top, peaks.Before, peaks.WhileEasing), upper + chartHeight);
        _drawnTop = top;
    }

    /// <summary>
    /// UI.B23: when the scale changes, the line is redrawn against the new top at once, then eased from its old height to
    /// its new one with a short one-off animation of a transform, so it doesn't leap. The labels change at once.
    /// UI.B26: the stretch comes from <see cref="SpeedGraphEase.From"/>, which keeps the stroke from being stretched into a
    /// bar and anything coming into view from going past the top.
    /// </summary>
    private void EaseScale(double from, double baseline)
    {
        _rescale?.Stop();
        _rescale = null;
        LineScale.ScaleY = 1;
        if (from == 1 || !new Windows.UI.ViewManagement.UISettings().AnimationsEnabled) return;
        LineScale.CenterY = baseline;
        var animation = new DoubleAnimation
        {
            From = from,
            To = 1,
            Duration = new Duration(SpeedGraphEase.Duration),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(animation, LineScale);
        Storyboard.SetTargetProperty(animation, nameof(ScaleTransform.ScaleY));
        _rescale = new Storyboard();
        _rescale.Children.Add(animation);
        _rescale.Begin();
    }

    /// <summary>
    /// Between samples the line slides left at the speed time passes, so the graph scrolls smoothly instead of
    /// stepping once a second. The next sample redraws it from where the slide has got to, so nothing jumps.
    /// It is one composition animation of a transform, with no timer, and it stops while the graph can't be seen.
    /// </summary>
    private static readonly double SlideSeconds = SpeedGraphPoints.MaxSlide.TotalSeconds;

    private void Slide(double pixelsPerSecond)
    {
        _pixelsPerSecond = pixelsPerSecond;
        if (!_seen || !_expanded || pixelsPerSecond <= 0 || !new Windows.UI.ViewManagement.UISettings().AnimationsEnabled) return;
        // Two seconds at most: a sample that is late leaves the line where it got to, not sliding away.
        var longest = SlideSeconds;
        // UI.B22: a redraw that is not a new sample (the plot resized as the window's text changed, the theme, the
        // period) starts from where the slide had got to by now, not from 0. Restarting at 0 jumped the line back to
        // the right each time, so it moved in steps with blank stretches instead of sliding in from the right.
        var elapsed = Math.Clamp((Environment.TickCount64 - _sampledAt) / 1000d, 0, longest);
        if (longest - elapsed < 0.05)
        {
            SlideTransform.X = -pixelsPerSecond * longest;
            return;
        }
        var animation = new DoubleAnimation
        {
            From = -pixelsPerSecond * elapsed,
            To = -pixelsPerSecond * longest,
            Duration = new Duration(TimeSpan.FromSeconds(longest - elapsed)),
            FillBehavior = FillBehavior.HoldEnd,
        };
        Storyboard.SetTarget(animation, SlideTransform);
        Storyboard.SetTargetProperty(animation, nameof(TranslateTransform.X));
        _slide = new Storyboard();
        _slide.Children.Add(animation);
        _slide.Begin();
    }

    private void StopSlide()
    {
        _slide?.Stop();
        _slide = null;
        SlideTransform.X = 0;
    }

    /// <summary>How far the line has slid, in seconds, so the pointer finds the sample that is under it.</summary>
    private double SlidSeconds => _pixelsPerSecond > 0 ? -SlideTransform.X / _pixelsPerSecond : 0;

    /// <summary>
    /// The point under the pointer: a guide line, a dot on the line and its speed and time. A gap says there was no
    /// figure then, rather than showing a nearby one as if it were.
    /// </summary>
    private void DrawHover()
    {
        Hover.Children.Clear();
        if (_pointerX is not { } x || !_drawn || !_expanded || x < _left || x > _left + _chartWidth) return;
        var shift = TimeSpan.FromSeconds(SlidSeconds);
        var time = _start + shift + TimeSpan.FromSeconds((x - _left) / _chartWidth * _seconds);
        // UI.B23: a sample is where the line draws it (a little late, in its bucket), and only once it is drawn. In a
        // bucket, the highest figure speaks for it. UI.B26: only one the scale was fitted to, so the dot is never above
        // the line or clipped to the top.
        var latest = _samples.LastOrDefault().Time;
        var nearest = _samples
            .Where(sample => sample.Time >= _start - SpeedGraphPoints.Lag(_bucketSeconds) && SpeedGraphDrawing.IsInScale(sample.Time, latest, _bucketSeconds))
            .OrderBy(sample => Math.Abs((SpeedGraphPoints.ShownAt(sample.Time, _bucketSeconds) - time).TotalSeconds))
            .ThenByDescending(sample => sample.Rate ?? -1)
            .FirstOrDefault();
        var shownAt = SpeedGraphPoints.ShownAt(nearest.Time, _bucketSeconds);
        var close = nearest.Time != default && Math.Abs((shownAt - time).TotalSeconds) <= Math.Max(2, _seconds / _chartWidth * 4);
        var guide = InboundTheme.Brush("InboundSecondaryTextBrush", this);
        var pointX = close ? _left + Math.Clamp((shownAt - _start - shift).TotalSeconds / _seconds, 0, 1) * _chartWidth : x;
        Hover.Children.Add(new Line { X1 = pointX, X2 = pointX, Y1 = _upper, Y2 = _upper + _chartHeight, Stroke = guide, StrokeThickness = 1, Opacity = 0.6 });

        void Dot(double rate, string brush)
        {
            var y = _upper + _chartHeight * (1 - Math.Clamp(rate / _top, 0, 1));
            var dot = new Ellipse { Width = 7, Height = 7, Fill = InboundTheme.Brush(brush, this) };
            Canvas.SetLeft(dot, pointX - 3.5);
            Canvas.SetTop(dot, y - 3.5);
            Hover.Children.Add(dot);
        }

        string text;
        // T8: the upload figure taken with it (the two are sampled together).
        var upload = close && _showUpload ? _upSamples.FirstOrDefault(sample => sample.Time == nearest.Time).Rate : null;
        if (close && nearest.Rate is { } rate)
        {
            if (upload is { } up) Dot(up, "InboundUploadBrush");
            Dot(rate, "InboundProgressFillBrush");
            var figures = upload is { } sent ? $"Down {RateText(rate)}  ·  Up {RateText(sent)}" : RateText(rate);
            text = $"{figures}  ·  {nearest.Time.ToLocalTime():HH:mm:ss}  ·  {Ago(_samples.Last().Time - nearest.Time)}";
        }
        else
        {
            var at = close ? nearest.Time : time;
            text = $"No figure  ·  {at.ToLocalTime():HH:mm:ss}";
        }

        var label = new Border
        {
            Background = InboundTheme.Brush("InboundSurfaceBrush", this),
            BorderBrush = InboundTheme.Brush("InboundDividerBrush", this),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6, 2, 6, 2),
            Child = new TextBlock { Text = text, FontSize = 11 },
        };
        label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var labelWidth = label.DesiredSize.Width;
        // Beside the guide, on whichever side has room.
        var labelX = pointX + 8 + labelWidth <= _left + _chartWidth ? pointX + 8 : Math.Max(_left, pointX - 8 - labelWidth);
        Canvas.SetLeft(label, labelX);
        Canvas.SetTop(label, _upper);
        Hover.Children.Add(label);
    }

    private static string Ago(TimeSpan span) => span.TotalSeconds < 1.5 ? "now"
        : span.TotalMinutes < 1 ? $"{span.TotalSeconds:0} s ago"
        : span.TotalHours < 1 ? $"{(int)span.TotalMinutes} min {span.Seconds:00} s ago"
        : $"{(int)span.TotalHours} h {span.Minutes:00} min ago";
}
