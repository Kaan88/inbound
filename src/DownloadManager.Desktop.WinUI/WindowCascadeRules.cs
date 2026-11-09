namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// Where a progress, package or completion window goes when another of the app's windows already sits at its place
/// (U-WL-4, U-WR-16). Each runs in its own process and they share one remembered place, so several finishing together
/// would open exactly on top of each other and look like one. Each is moved down and right by a step from the last,
/// starting again at the top of the screen when one would leave it. Nothing here touches a window.
/// </summary>
internal static class WindowCascadeRules
{
    /// <summary>The offset from one window to the next, in pixels at 100%.</summary>
    public const int Step = 32;

    /// <summary>How many places are tried before the last one is used anyway.</summary>
    public const int MostTries = 24;

    /// <param name="x">The window's place now, left edge.</param>
    /// <param name="y">The window's place now, top edge.</param>
    /// <param name="taken">The top-left corners of the app's other windows.</param>
    /// <param name="workX">The screen's work area, left edge.</param>
    /// <param name="workY">The screen's work area, top edge.</param>
    /// <param name="workWidth">The screen's work area, width.</param>
    /// <param name="workHeight">The screen's work area, height.</param>
    /// <param name="width">The window's width.</param>
    /// <param name="height">The window's height.</param>
    /// <param name="step">The offset, scaled for the screen.</param>
    public static (int X, int Y) Place(int x, int y, IReadOnlyCollection<(int X, int Y)> taken,
        int workX, int workY, int workWidth, int workHeight, int width, int height, int step = Step)
    {
        if (step <= 0) step = Step;
        var near = Math.Max(1, step / 2);
        var column = 0;
        for (var tries = 0; tries < MostTries; tries++)
        {
            if (!taken.Any(corner => Math.Abs(corner.X - x) < near && Math.Abs(corner.Y - y) < near)) return (x, y);
            x += step;
            y += step;
            if (y + Math.Min(height, workHeight) > workY + workHeight || x + Math.Min(width, workWidth) > workX + workWidth)
            {
                // Off the bottom or the right: a new column, from the top, a step further right than the last one began.
                column++;
                x = workX + column * step;
                y = workY;
                if (x + Math.Min(width, workWidth) > workX + workWidth) x = workX;
            }
        }

        return (x, y);
    }
}
