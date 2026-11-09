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

    /// <summary>
    /// Where a new window goes when its remembered place is on another screen than the main window (U-WL-4): over the
    /// middle of the main window, kept inside that screen's work area, so with the main window on a second screen its
    /// windows open there too. On the main window's screen the remembered place stays.
    /// </summary>
    /// <param name="x">The window's place now, left edge.</param>
    /// <param name="y">The window's place now, top edge.</param>
    /// <param name="width">The window's width.</param>
    /// <param name="height">The window's height.</param>
    /// <param name="main">The main window's bounds.</param>
    /// <param name="mainScreen">The bounds of the screen the main window is on.</param>
    /// <param name="mainWork">That screen's work area.</param>
    public static (int X, int Y) OntoMainScreen(int x, int y, int width, int height, ScreenRect main, ScreenRect mainScreen, ScreenRect mainWork)
    {
        // The title bar is what has to be on the screen, as for a remembered place (WindowPlacement).
        var probeX = x + Math.Min(120, Math.Max(1, width / 2));
        var probeY = y + 16;
        if (mainScreen.Contains(probeX, probeY)) return (x, y);

        var centreX = main.X + (main.Width - width) / 2;
        var centreY = main.Y + (main.Height - height) / 2;
        return (Clamp(centreX, mainWork.X, mainWork.X + mainWork.Width - width), Clamp(centreY, mainWork.Y, mainWork.Y + mainWork.Height - height));
    }

    // A window larger than the work area starts at its top-left corner.
    private static int Clamp(int value, int low, int high) => high < low ? low : Math.Clamp(value, low, high);
}

/// <summary>A rectangle on the desktop, in pixels.</summary>
internal readonly record struct ScreenRect(int X, int Y, int Width, int Height)
{
    public bool Contains(int x, int y) => x >= X && x < X + Width && y >= Y && y < Y + Height;
}
