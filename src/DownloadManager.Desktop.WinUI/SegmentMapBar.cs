using DownloadManager.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// reference app-style file map: one track for the whole file, with each saved segment's completed bytes drawn at
/// its real byte offset and a divider at every segment start. Falls back to a single fill when only an
/// overall fraction is known.
/// </summary>
internal sealed class SegmentMapBar : Grid
{
    private readonly Canvas _canvas = new();
    private IReadOnlyList<SegmentDto> _segments = [];
    private long _total;
    private double _fallbackFraction;

    public SegmentMapBar()
    {
        Height = 14;
        CornerRadius = new CornerRadius(2);
        Children.Add(_canvas);
        SizeChanged += (_, _) => Draw();
        ActualThemeChanged += (_, _) => Draw();
    }

    public void SetSegments(IReadOnlyList<SegmentDto> segments, long totalBytes)
    {
        _segments = segments;
        _total = totalBytes > 0 ? totalBytes : segments.Count > 0 ? segments.Max(s => s.End) + 1 : 0;
        Draw();
    }

    public void SetFraction(double fraction)
    {
        _segments = [];
        _fallbackFraction = Math.Clamp(fraction, 0, 1);
        Draw();
    }

    private void Draw()
    {
        _canvas.Children.Clear();
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0) return;
        Background = InboundTheme.Brush("InboundProgressTrackBrush", this);
        var fill = InboundTheme.Brush("InboundProgressFillBrush", this);
        var divider = InboundTheme.Brush("InboundSurfaceBrush", this);

        if (_segments.Count == 0 || _total <= 0)
        {
            AddRect(0, width * _fallbackFraction, height, fill);
            return;
        }

        foreach (var segment in _segments)
        {
            var x = segment.Start / (double)_total * width;
            var done = Math.Max(0, segment.Completed) / (double)_total * width;
            AddRect(x, Math.Max(done, segment.Completed > 0 ? 1 : 0), height, fill);
        }
        // Dividers last so segment boundaries stay visible over filled areas.
        foreach (var segment in _segments.Skip(1))
        {
            AddRect(segment.Start / (double)_total * width - 0.5, 1.5, height, divider);
        }
    }

    private void AddRect(double x, double w, double h, Microsoft.UI.Xaml.Media.Brush brush)
    {
        if (w <= 0) return;
        var rect = new Rectangle { Width = w, Height = h, Fill = brush };
        Canvas.SetLeft(rect, x);
        _canvas.Children.Add(rect);
    }
}
