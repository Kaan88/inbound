namespace DownloadManager.Desktop.WinUI;

/// <summary>A window's size in physical pixels.</summary>
internal readonly record struct PixelSize(int Width, int Height);

/// <summary>
/// T13: how big windows and dialogs open so their bottom buttons show without resizing, at any display scaling. Sizes are
/// asked for in device-independent pixels (DIPs, 1/96 inch) and become pixels at the window's scale; nothing is ever
/// bigger than the screen's work area, where the window's content scrolls instead (never its buttons). Nothing here
/// touches the UI, so it is tested (tests/DownloadManager.Core.Tests links this file).
/// </summary>
internal static class WindowSizeRules
{
    /// <summary>What a window keeps clear of the work area's edges.</summary>
    public const int ScreenMargin = 24;

    /// <summary>The smallest a window is ever made, in pixels.</summary>
    public const int Smallest = 200;

    /// <summary>
    /// The pixel size for <paramref name="widthDip"/> by <paramref name="heightDip"/> at <paramref name="scale"/> (1 at
    /// 96 DPI, 1.25 at 125 %…), no bigger than the work area less <see cref="ScreenMargin"/>.
    /// </summary>
    public static PixelSize Fit(double widthDip, double heightDip, double scale, int workWidth, int workHeight)
    {
        var factor = double.IsFinite(scale) && scale >= 1 ? scale : 1;
        return new PixelSize(
            Math.Min((int)Math.Ceiling(widthDip * factor), Math.Max(Smallest, workWidth - ScreenMargin)),
            Math.Min((int)Math.Ceiling(heightDip * factor), Math.Max(Smallest, workHeight - ScreenMargin)));
    }

    /// <summary>A size that only grows: each side is the larger of the current one and the one asked for.</summary>
    public static PixelSize Grown(PixelSize current, PixelSize wanted)
        => new(Math.Max(current.Width, wanted.Width), Math.Max(current.Height, wanted.Height));

    /// <summary>
    /// Where a window of <paramref name="size"/> at <paramref name="x"/>, <paramref name="y"/> goes so it stays inside the
    /// work area: moved left and up as far as needed, never past the area's left and top.
    /// </summary>
    public static (int X, int Y) KeptInside(int x, int y, PixelSize size, int workX, int workY, int workWidth, int workHeight)
        => (Math.Clamp(x, workX, Math.Max(workX, workX + workWidth - size.Width)),
            Math.Clamp(y, workY, Math.Max(workY, workY + workHeight - size.Height)));

    // ---- Screens the window toolkit can't measure (Uno on Linux) ----

    /// <summary>
    /// The most a window is made when its screen's size is unknown: inside a 1280 x 800 screen, and still in a
    /// 1366 x 768 one with a panel and a title bar.
    /// </summary>
    public const int UnknownScreenWidth = 1100, UnknownScreenHeight = 700;

    /// <summary>
    /// The work area of the monitor a window opens on: the one holding <paramref name="x"/>, <paramref name="y"/> (a
    /// remembered place or the mouse), else the primary one, else the first. The desktop's own work area (X11's
    /// <c>_NET_WORKAREA</c>, one rectangle over every monitor, less the panels) cuts it down where they meet. Null when
    /// nothing is known. All in the same screen pixels.
    /// </summary>
    public static ScreenRect? WorkAreaFor(IReadOnlyList<ScreenRect> monitors, int primary, ScreenRect? desktopWorkArea, int? x, int? y)
    {
        ScreenRect? monitor = null;
        if (x is { } px && y is { } py)
        {
            foreach (var candidate in monitors)
            {
                if (Usable(candidate) && candidate.Contains(px, py)) { monitor = candidate; break; }
            }
        }

        if (monitor is null && primary >= 0 && primary < monitors.Count && Usable(monitors[primary])) monitor = monitors[primary];
        monitor ??= monitors.Where(Usable).Select(m => (ScreenRect?)m).FirstOrDefault();
        var desktop = desktopWorkArea is { } d && Usable(d) ? d : (ScreenRect?)null;
        if (monitor is not { } screen) return desktop;
        if (desktop is not { } work) return screen;

        var left = Math.Max(screen.X, work.X);
        var top = Math.Max(screen.Y, work.Y);
        var right = Math.Min(screen.X + screen.Width, work.X + work.Width);
        var bottom = Math.Min(screen.Y + screen.Height, work.Y + work.Height);
        // A work area that misses this monitor says nothing about it.
        return right - left > 0 && bottom - top > 0 ? new ScreenRect(left, top, right - left, bottom - top) : screen;
    }

    /// <summary>
    /// A size asked for in DIPs, as screen pixels at <paramref name="scale"/> pixels for each DIP (1 when the scale is
    /// unknown or below 1), then fitted by <see cref="FitOnScreen(int, int, ScreenRect?)"/>. For Uno, whose
    /// <c>AppWindow</c> sizes and places are screen pixels, as on Windows.
    /// </summary>
    public static PixelSize FitOnScreen(double widthDip, double heightDip, double scale, ScreenRect? workArea)
    {
        var factor = double.IsFinite(scale) && scale >= 1 ? scale : 1;
        return FitOnScreen((int)Math.Ceiling(widthDip * factor), (int)Math.Ceiling(heightDip * factor), workArea);
    }

    /// <summary>
    /// A size no bigger than the work area less <see cref="ScreenMargin"/> (or <see cref="UnknownScreenWidth"/> by
    /// <see cref="UnknownScreenHeight"/> when the screen is unknown); never made bigger.
    /// </summary>
    public static PixelSize FitOnScreen(int width, int height, ScreenRect? workArea)
    {
        var (mostWidth, mostHeight) = workArea is { } area && Usable(area)
            ? (Math.Max(Smallest, area.Width - ScreenMargin), Math.Max(Smallest, area.Height - ScreenMargin))
            : (UnknownScreenWidth, UnknownScreenHeight);
        return new PixelSize(Math.Max(Smallest, Math.Min(width, mostWidth)), Math.Max(Smallest, Math.Min(height, mostHeight)));
    }

    /// <summary>
    /// A remembered place and size (perhaps from a bigger screen) made to fit <paramref name="workArea"/>: the size cut down
    /// by <see cref="FitOnScreen"/>, then the window moved left and up as far as needed to be all inside. An unknown
    /// work area keeps the place, which the window manager keeps on a screen.
    /// </summary>
    public static (int X, int Y, PixelSize Size) OnScreen(int x, int y, int width, int height, ScreenRect? workArea)
    {
        var size = FitOnScreen(width, height, workArea);
        if (workArea is not { } area || !Usable(area)) return (x, y, size);
        var (left, top) = KeptInside(x, y, size, area.X, area.Y, area.Width, area.Height);
        return (left, top, size);
    }

    private static bool Usable(ScreenRect rect) => rect.Width > 0 && rect.Height > 0;

    // ---- Progress Details ----

    /// <summary>Progress Details for an ordinary download (DIPs), as it opened before T13.</summary>
    public const double ProgressWidth = 640, ProgressHeight = 640;

    /// <summary>
    /// Progress Details for a torrent: wide enough for the row of buttons a running torrent has on its Trackers tab
    /// (Delete, Force recheck, Announce now, Stop, Resume, Cancel download, Hide: about 865 DIPs with the window's
    /// padding), and still inside a 1366 x 768 screen at 100 % (its work area is about 1366 x 728). Should the labels come
    /// out wider than that, the row wraps (<see cref="LineStarts"/>) rather than cut a button off.
    /// </summary>
    public const double TorrentProgressWidth = 880, TorrentProgressHeight = 660;

    /// <summary>A finished download's Progress Details: Open, Open Folder and the rest.</summary>
    public const double CompletedWidth = 600, CompletedHeight = 500;

    /// <summary>A finished torrent keeps its tabs and Stop seeding, Force recheck and Announce now beside Delete and Close.</summary>
    public const double CompletedTorrentWidth = 720, CompletedTorrentHeight = 560;

    /// <summary>
    /// How small Progress Details can be dragged: the header, the tabs, a little of the page and two lines of buttons
    /// (the row wraps when it is narrower than its buttons).
    /// </summary>
    public const double ProgressMinimumWidth = 480, ProgressMinimumHeight = 420;

    public static (double Width, double Height) ProgressSize(bool torrent, bool completed) => (torrent, completed) switch
    {
        (true, true) => (CompletedTorrentWidth, CompletedTorrentHeight),
        (true, false) => (TorrentProgressWidth, TorrentProgressHeight),
        (false, true) => (CompletedWidth, CompletedHeight),
        _ => (ProgressWidth, ProgressHeight),
    };

    // ---- Dialogs ----

    /// <summary>
    /// What a dialog's title, its row of buttons and its margins take from the window's height (DIPs): the title (about
    /// 44), the buttons and their padding (about 80), the dialog's padding and the room WinUI keeps around a dialog.
    /// </summary>
    public const double DialogChrome = 200;

    /// <summary>The tallest a dialog's content is made, so a big window doesn't get a page-high dialog.</summary>
    public const double DialogMostContent = 620;

    /// <summary>The least a dialog's content gets: below this the window is too short for anything useful, and the content scrolls.</summary>
    public const double DialogLeastContent = 120;

    /// <summary>
    /// The most height a dialog's scrolling content may take in a window <paramref name="windowHeight"/> DIPs tall, so the
    /// title and the buttons stay in the window and the content scrolls.
    /// </summary>
    public static double DialogContentMaxHeight(double windowHeight, double most = DialogMostContent)
    {
        if (!double.IsFinite(windowHeight) || windowHeight <= 0) return most;
        return Math.Clamp(windowHeight - DialogChrome, DialogLeastContent, Math.Max(DialogLeastContent, most));
    }

    /// <summary>
    /// How tall a list inside a dialog is made: what the content may take less the rows above and below it
    /// (<paramref name="besides"/>), between <paramref name="least"/> and <paramref name="most"/>.
    /// </summary>
    public static double DialogListHeight(double contentMaxHeight, double besides, double least, double most)
        => Math.Clamp(contentMaxHeight - besides, Math.Min(least, most), most);

    // ---- A row of buttons that wraps ----

    /// <summary>
    /// Which line each button of a right-aligned row goes on when the row is <paramref name="available"/> wide: as many
    /// as fit on the last line, the rest on lines above it, keeping their order. A button wider than the row gets a line
    /// of its own. Returns the first index of each line.
    /// </summary>
    public static List<int> LineStarts(IReadOnlyList<double> widths, double available, double spacing)
    {
        var starts = new List<int>();
        if (widths.Count == 0) return starts;

        // Filled from the end, so the buttons that matter most (the window's own action is last) stay on one line together.
        var used = 0d;
        var count = 0;
        for (var at = widths.Count - 1; at >= 0; at--)
        {
            var width = widths[at];
            if (width <= 0) continue;
            var needed = count == 0 ? width : used + spacing + width;
            if (count > 0 && needed > available)
            {
                starts.Insert(0, at + 1);
                used = width;
                count = 1;
                continue;
            }

            used = needed;
            count++;
        }

        starts.Insert(0, 0);
        return starts;
    }

    /// <summary>The width a row of <paramref name="widths"/> takes on one line (hidden buttons, 0 wide, take no spacing).</summary>
    public static double OneLineWidth(IReadOnlyList<double> widths, double spacing)
    {
        var shown = widths.Where(width => width > 0).ToList();
        return shown.Count == 0 ? 0 : shown.Sum() + spacing * (shown.Count - 1);
    }
}
