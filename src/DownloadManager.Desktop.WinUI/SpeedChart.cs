using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// The progress window's speed for the last minute: one line and the area under it, scaled to the peak,
/// drawn only when asked. It has no timer of its own; the window redraws it when a job update arrives
/// and the window can be seen.
/// </summary>
internal sealed class SpeedChart : Grid
{
    private readonly Canvas _canvas = new();
    private readonly Polyline _line = new() { StrokeThickness = 1.6, StrokeLineJoin = PenLineJoin.Round };
    private readonly Polygon _area = new() { Opacity = 0.14 };
    private readonly Line _middle = new() { StrokeThickness = 1 };
    private readonly Line _base = new() { StrokeThickness = 1 };
    private IReadOnlyList<(double X, double Rate)> _points = [];
    private double _peak;

    public SpeedChart()
    {
        Height = 64;
        _canvas.Children.Add(_middle);
        _canvas.Children.Add(_base);
        _canvas.Children.Add(_area);
        _canvas.Children.Add(_line);
        Children.Add(_canvas);
        SizeChanged += (_, _) => Draw();
        ActualThemeChanged += (_, _) => Draw();
    }

    public void Show(IReadOnlyList<(double X, double Rate)> points, double peak)
    {
        _points = points;
        _peak = peak;
        Draw();
    }

    private void Draw()
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0) return;
        var fill = InboundTheme.Brush("InboundProgressFillBrush", this);
        var grid = InboundTheme.Brush("InboundCardStrokeBrush", this);
        _line.Stroke = fill;
        _area.Fill = fill;
        foreach (var (rule, y) in new[] { (_middle, Math.Round(height / 2) + 0.5), (_base, height - 0.5) })
        {
            rule.Stroke = grid;
            rule.X1 = 0;
            rule.X2 = width;
            rule.Y1 = rule.Y2 = y;
        }

        var line = new PointCollection();
        var area = new PointCollection();
        if (_points.Count > 0)
        {
            // Headroom over the peak, so the top of the line isn't cut by the edge.
            var top = _peak > 0 ? _peak * 1.1 : 1;
            foreach (var (x, rate) in _points)
                line.Add(new Point(x * width, height - 1 - rate / top * (height - 2)));
            if (line.Count == 1) line.Insert(0, new Point(Math.Max(0, line[0].X - 2), line[0].Y));
            area.Add(new Point(line[0].X, height));
            foreach (var point in line) area.Add(point);
            area.Add(new Point(line[^1].X, height));
        }

        _line.Points = line;
        _area.Points = area;
    }
}
