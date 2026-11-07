namespace DownloadManager.Desktop.WinUI;

/// <summary>A run of rows by position, both ends included.</summary>
internal readonly record struct RowRange(int First, int Last)
{
    public bool Contains(int row) => row >= First && row <= Last;
}

/// <summary>Where the selection box is drawn, already kept inside the list.</summary>
internal readonly record struct RegionBox(double Left, double Top, double Width, double Height);

/// <summary>
/// The arithmetic of dragging a box over rows. Rows are all one height, so which ones a box touches is
/// worked out from positions, and rows that are scrolled out and have no container still count.
/// Nothing here touches the UI.
/// </summary>
internal static class RegionSelection
{
    /// <summary>Less movement than this is a click that wobbled, and stays a click.</summary>
    public const double DragThreshold = 6;

    /// <summary>The strip along the top and bottom of the list where the pointer starts it scrolling.</summary>
    public const double EdgeSize = 28;

    /// <summary>The fastest a timer tick scrolls, reached well past the list's edge.</summary>
    public const double MostScrollPerTick = 40;

    /// <summary>Only a mouse with its left button. A finger scrolls, and the row's own controls keep their clicks.</summary>
    public static bool CanStart(bool isMouse, bool leftButton, bool onOwnControl, bool locked)
        => isMouse && leftButton && !onOwnControl && !locked;

    public static bool PastThreshold(double startX, double startY, double x, double y)
    {
        var dx = x - startX;
        var dy = y - startY;
        return dx * dx + dy * dy >= DragThreshold * DragThreshold;
    }

    /// <summary>The rows a box touches, or null when it touches none.</summary>
    /// <param name="top">The box's top, in the same coordinates as <paramref name="rowsTop"/>.</param>
    /// <param name="bottom">The box's bottom.</param>
    /// <param name="rowsTop">Where the first row starts.</param>
    public static RowRange? RowsTouched(double top, double bottom, double rowsTop, double rowHeight, int rowCount)
    {
        if (rowCount <= 0 || rowHeight <= 0) return null;
        if (bottom < top) (top, bottom) = (bottom, top);
        var rowsBottom = rowsTop + (rowCount * rowHeight);
        if (bottom <= rowsTop || top >= rowsBottom) return null;
        var first = (int)Math.Floor((top - rowsTop) / rowHeight);
        var last = (int)Math.Ceiling((bottom - rowsTop) / rowHeight) - 1;
        return new RowRange(Math.Clamp(first, 0, rowCount - 1), Math.Clamp(last, 0, rowCount - 1));
    }

    /// <summary>What to scroll by on this tick: nothing away from the edges, and more the further past them the pointer is.</summary>
    public static double AutoScrollStep(double pointerY, double viewportHeight)
    {
        if (viewportHeight <= 0) return 0;
        var edge = Math.Min(EdgeSize, viewportHeight / 2);
        if (pointerY < edge) return -Speed(edge - pointerY);
        var fromBottom = pointerY - (viewportHeight - edge);
        return fromBottom > 0 ? Speed(fromBottom) : 0;

        static double Speed(double intoEdge) => Math.Min(MostScrollPerTick, 2 + (intoEdge / 3));
    }

    /// <summary>Which rows to add and take away to get from one box's rows to the next, so a long list isn't gone over for a small move.</summary>
    public static (List<int> Add, List<int> Remove) Change(RowRange? before, RowRange? now)
    {
        var add = new List<int>();
        var remove = new List<int>();
        if (now is { } next)
        {
            for (var row = next.First; row <= next.Last; row++)
            {
                if (before is not { } old || !old.Contains(row)) add.Add(row);
            }
        }

        if (before is { } previous)
        {
            for (var row = previous.First; row <= previous.Last; row++)
            {
                if (now is not { } current || !current.Contains(row)) remove.Add(row);
            }
        }

        return (add, remove);
    }

    /// <summary>The box between where the drag began and the pointer, kept inside the list. The start is in content coordinates, so it stays put as the list scrolls.</summary>
    public static RegionBox Box(double startX, double startContentY, double x, double y, double scrollOffset, double width, double height)
    {
        var startY = startContentY - scrollOffset;
        var left = Math.Clamp(Math.Min(startX, x), 0, Math.Max(0, width));
        var right = Math.Clamp(Math.Max(startX, x), 0, Math.Max(0, width));
        var top = Math.Clamp(Math.Min(startY, y), 0, Math.Max(0, height));
        var bottom = Math.Clamp(Math.Max(startY, y), 0, Math.Max(0, height));
        return new RegionBox(left, top, right - left, bottom - top);
    }
}
