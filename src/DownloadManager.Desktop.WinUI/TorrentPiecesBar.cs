using DownloadManager.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// T2: a torrent's pieces as one compact bar. Each cell of about four pixels stands for a run of pieces and is filled as
/// far as the torrent has them (<see cref="TorrentDetailsRules.Cells"/>); full neighbours are drawn as one rectangle.
/// </summary>
internal sealed class TorrentPiecesBar : Grid
{
    private const double CellWidth = 4;

    private readonly Canvas _canvas = new();
    private TorrentPiecesResult? _pieces;

    public TorrentPiecesBar()
    {
        Height = 24;
        CornerRadius = new CornerRadius(3);
        Children.Add(_canvas);
        SizeChanged += (_, _) => Draw();
        ActualThemeChanged += (_, _) => Draw();
    }

    /// <summary>The pieces to draw; null draws an empty track.</summary>
    public void SetPieces(TorrentPiecesResult? pieces)
    {
        _pieces = pieces;
        Draw();
    }

    private void Draw()
    {
        _canvas.Children.Clear();
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0) return;
        Background = InboundTheme.Brush("InboundProgressTrackBrush", this);
        if (_pieces is not { Count: > 0 } pieces) return;
        var fill = InboundTheme.Brush("InboundProgressFillBrush", this);
        var cells = TorrentDetailsRules.Cells(pieces, Math.Max(1, (int)(width / CellWidth)));
        if (cells.Length == 0) return;
        var step = width / cells.Length;
        var runStart = -1;
        for (var cell = 0; cell <= cells.Length; cell++)
        {
            var full = cell < cells.Length && cells[cell] >= 0.999;
            if (full)
            {
                if (runStart < 0) runStart = cell;
                continue;
            }

            if (runStart >= 0)
            {
                Add(runStart * step, (cell - runStart) * step, height, fill, 1);
                runStart = -1;
            }

            // A cell partly had is drawn fainter, by how much of it is there.
            if (cell < cells.Length && cells[cell] > 0) Add(cell * step, step, height, fill, 0.25 + 0.6 * cells[cell]);
        }
    }

    private void Add(double left, double width, double height, Brush brush, double opacity)
    {
        var rect = new Rectangle { Width = Math.Max(1, width), Height = height, Fill = brush, Opacity = opacity };
        Canvas.SetLeft(rect, left);
        _canvas.Children.Add(rect);
    }
}
