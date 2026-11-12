using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace DownloadManager.Desktop.WinUI;

internal static class WindowLayout
{
    /// <summary>The window icon: the .ico on Windows; on Linux the window manager takes it from the window itself, from a PNG.</summary>
    public static string AppIconPath => Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "app.ico" : "app.png");

    /// <summary>What SetIcon takes: Uno looks the file up in the app's own folder by name.</summary>
    public static string IconArgument(string path) => OperatingSystem.IsWindows() ? path : Path.GetFileName(path);

    public static void ResizeForDpi(Window window, double width, double height) => window.AppWindow.Resize(ScaledToFit(window, width, height));

    /// <summary>
    /// Gives a window a size it had before, in pixels: on Windows exactly that, as before; on Uno no bigger than the
    /// screen it opens on, as the size may come from a bigger one.
    /// </summary>
    public static void ResizeRemembered(Window window, int width, int height)
    {
#if HAS_UNO
        var fit = WindowSizeRules.FitOnScreen(width, height, UnoScreens.WorkArea());
        (width, height) = (fit.Width, fit.Height);
#endif
        window.AppWindow.Resize(new SizeInt32 { Width = width, Height = height });
    }

    /// <summary>How small the user can drag the window, and how small a remembered size can come back. Set it before restoring one.</summary>
    public static void SetMinimumSize(Window window, double width, double height)
    {
        if (window.AppWindow.Presenter is not OverlappedPresenter presenter) return;
        var size = ScaledToFit(window, width, height);
        presenter.PreferredMinimumWidth = size.Width;
        presenter.PreferredMinimumHeight = size.Height;
#if HAS_UNO
        // Uno's X11 head ignores the preferred minimum (the toolbar clipped under 1000 px, UI check F-10): a window made
        // smaller than it grows back to it, as Windows would have stopped the drag there.
        window.AppWindow.Changed += (sender, change) =>
        {
            if (!change.DidSizeChange || sender.Presenter is OverlappedPresenter { State: not OverlappedPresenterState.Restored }) return;
            var now = sender.Size;
            if (now.Width >= size.Width && now.Height >= size.Height) return;
            sender.Resize(new SizeInt32 { Width = Math.Max(now.Width, size.Width), Height = Math.Max(now.Height, size.Height) });
        };
#endif
    }

    private static SizeInt32 ScaledToFit(Window window, double width, double height)
    {
#if HAS_UNO
        // Uno has no DisplayArea.GetFromWindowId. Its AppWindow sizes and places are screen pixels, as on Windows (checked in
        // the Uno 6.7 source: X11WindowWrapper.Resize/Move hand them to XResizeWindow/XMoveWindow unchanged, and the
        // content is laid out at pixels over RawPixelsPerViewPixel), so DIPs are scaled here. The screen comes from
        // UnoScreens, in pixels; an unknown one keeps the window to a size most screens hold (the Linux window opened
        // bigger than the screen under Xvfb).
        var fit = WindowSizeRules.FitOnScreen(width, height, UnoScreens.Scale(), UnoScreens.WorkArea());
        return new SizeInt32 { Width = fit.Width, Height = fit.Height };
#endif
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var scale = Math.Max(96, GetDpiForWindow(hwnd)) / 96d;
        var area = DisplayArea.GetFromWindowId(window.AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var size = WindowSizeRules.Fit(width, height, scale, area.Width, area.Height);
        return new SizeInt32 { Width = size.Width, Height = size.Height };
    }

    /// <summary>
    /// T13: makes a window that is already showing at least <paramref name="width"/> by <paramref name="height"/> DIPs
    /// (never smaller than it is, never bigger than its screen's work area) and keeps it inside that work area, moving it
    /// left or up when it would now reach past the edge.
    /// </summary>
    public static void GrowForDpi(Window window, double width, double height)
    {
        var appWindow = window.AppWindow;
        if (appWindow.Presenter is OverlappedPresenter { State: not OverlappedPresenterState.Restored }) return;
        var wanted = ScaledToFit(window, width, height);
        var now = appWindow.Size;
        var grown = WindowSizeRules.Grown(new PixelSize(now.Width, now.Height), new PixelSize(wanted.Width, wanted.Height));
        if (grown.Width == now.Width && grown.Height == now.Height) return;
        appWindow.Resize(new SizeInt32 { Width = grown.Width, Height = grown.Height });
        KeepInside(window);
    }

    /// <summary>T13: moves a window left or up as far as needed for all of it to be inside its screen's work area.</summary>
    public static void KeepInside(Window window)
    {
#if HAS_UNO
        // Uno has no DisplayArea lookups (they throw); UnoScreens asks the X server (or Win32) instead.
        var unoWindow = window.AppWindow;
        var now = unoWindow.Position;
        if (UnoScreens.WorkArea(now.X, now.Y) is not { } work) return;
        var (left, top) = WindowSizeRules.KeptInside(now.X, now.Y, new PixelSize(unoWindow.Size.Width, unoWindow.Size.Height),
            work.X, work.Y, work.Width, work.Height);
        if (left != now.X || top != now.Y) unoWindow.Move(new PointInt32 { X = left, Y = top });
#else
        var appWindow = window.AppWindow;
        var area = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var position = appWindow.Position;
        var size = new PixelSize(appWindow.Size.Width, appWindow.Size.Height);
        var (x, y) = WindowSizeRules.KeptInside(position.X, position.Y, size, area.X, area.Y, area.Width, area.Height);
        if (x != position.X || y != position.Y) appWindow.Move(new PointInt32 { X = x, Y = y });
#endif
    }

    /// <summary>
    /// Gives a secondary window the same chrome as the main window: themed title bar (so a dark window
    /// never sits under a white caption) and the app icon. Re-applies when the root's theme changes.
    /// </summary>
    /// <summary>Whether windows get Mica: Windows 11. Elsewhere the window brush is the solid color beneath Mica.</summary>
    public static bool MicaSupported { get; } = Microsoft.UI.Composition.SystemBackdrops.MicaController.IsSupported();

    /// <summary>
    /// Without Mica a transparent window would show nothing behind it, so the window brush becomes the solid color
    /// Mica falls back to. Runs once, before any window resolves the brush.
    /// </summary>
    public static void UseSolidWindowsWithoutMica(ResourceDictionary resources)
    {
        if (MicaSupported) return;
        foreach (var key in new[] { "Dark", "Light" })
        {
            if (resources.ThemeDictionaries.TryGetValue(key, out var value) && value is ResourceDictionary dictionary &&
                dictionary.TryGetValue("InboundWindowFallbackBrush", out var solid))
            {
                dictionary["InboundWindowBrush"] = solid;
            }
        }
    }

    /// <summary>Every window is frosted: Mica behind a transparent root (InboundWindowBrush).</summary>
    public static void UseBackdrop(Window window)
    {
        if (MicaSupported && window.SystemBackdrop is null) window.SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
    }

    public static void ApplyChrome(Window window, FrameworkElement root)
    {
        var icon = WindowLayout.AppIconPath;
        if (File.Exists(icon)) window.AppWindow.SetIcon(WindowLayout.IconArgument(icon));
        UseBackdrop(window);
        if (root is Microsoft.UI.Xaml.Controls.Panel { Background: null } panel)
        {
            void Fill() => panel.Background = InboundTheme.Brush("InboundWindowBrush", panel);
            Fill();
            panel.ActualThemeChanged += (_, _) => Fill();
        }
        bool IsDark() => root.RequestedTheme switch
        {
            ElementTheme.Dark => true,
            ElementTheme.Light => false,
            _ => Application.Current.RequestedTheme == ApplicationTheme.Dark,
        };
        ApplyTitleBar(window, IsDark());
        root.ActualThemeChanged += (_, _) => ApplyTitleBar(window, root.ActualTheme == ElementTheme.Dark);
    }

    public static void ApplyTitleBar(Window window, bool dark)
    {
        if (!AppWindowTitleBar.IsCustomizationSupported()) return;
        // A closed window's root can still hear a theme change (OK in Options closes it as the theme changes); its
        // AppWindow is gone by then, and touching it ended the whole app.
        if (window.AppWindow?.TitleBar is not { } titleBar) return;
        // The caption is drawn by Windows, outside the content: the solid color under Mica (#202020 / #F3F3F3), so it meets the frosted body.
        var background = dark ? Windows.UI.Color.FromArgb(255, 32, 32, 32) : Windows.UI.Color.FromArgb(255, 243, 243, 243);
        var foreground = dark ? Windows.UI.Color.FromArgb(255, 235, 235, 235) : Windows.UI.Color.FromArgb(255, 30, 30, 30);
        var inactive = dark ? Windows.UI.Color.FromArgb(255, 150, 150, 150) : Windows.UI.Color.FromArgb(255, 120, 120, 120);
        var hover = dark ? Windows.UI.Color.FromArgb(255, 45, 45, 48) : Windows.UI.Color.FromArgb(255, 225, 225, 225);
        var pressed = dark ? Windows.UI.Color.FromArgb(255, 60, 60, 63) : Windows.UI.Color.FromArgb(255, 210, 210, 210);
        titleBar.BackgroundColor = background;
        titleBar.ForegroundColor = foreground;
        titleBar.InactiveBackgroundColor = background;
        titleBar.InactiveForegroundColor = inactive;
        titleBar.ButtonBackgroundColor = background;
        titleBar.ButtonForegroundColor = foreground;
        titleBar.ButtonHoverBackgroundColor = hover;
        titleBar.ButtonHoverForegroundColor = foreground;
        titleBar.ButtonPressedBackgroundColor = pressed;
        titleBar.ButtonPressedForegroundColor = foreground;
        titleBar.ButtonInactiveBackgroundColor = background;
        titleBar.ButtonInactiveForegroundColor = inactive;
    }

    /// <summary>
    /// Puts a window in the middle of the visible main Inbound window, which may belong to another process of this
    /// app (the update window runs on its own), or in the middle of its screen's work area when none is showing.
    /// </summary>
    public static void CenterOnMainWindow(Window window)
    {
#if HAS_UNO
        // Uno has no DisplayArea lookups (they throw); the window manager places a new window itself.
        return;
#endif
        var own = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var size = window.AppWindow.Size;
        var target = MainWindowBounds(own);
        if (target is null)
        {
            var area = DisplayArea.GetFromWindowId(window.AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
            target = new RectInt32 { X = area.X, Y = area.Y, Width = area.Width, Height = area.Height };
        }
        var bounds = target.Value;
        var x = bounds.X + (bounds.Width - size.Width) / 2;
        var y = bounds.Y + (bounds.Height - size.Height) / 2;
        // Never off the screen the main window is on.
        var screen = DisplayArea.GetFromRect(bounds, DisplayAreaFallback.Primary).WorkArea;
        x = Math.Clamp(x, screen.X, Math.Max(screen.X, screen.X + screen.Width - size.Width));
        y = Math.Clamp(y, screen.Y, Math.Max(screen.Y, screen.Y + screen.Height - size.Height));
        window.AppWindow.Move(new PointInt32 { X = x, Y = y });
    }

    /// <summary>
    /// Puts a window in the middle of <paramref name="owner"/>, a window of this process, kept on that window's screen.
    /// Falls back to <see cref="CenterOnMainWindow"/> without one.
    /// </summary>
    public static void CenterOn(Window window, nint owner)
    {
        // The Win32 window calls below are Windows-only; elsewhere this does what it does when nothing is found.
        if (!OperatingSystem.IsWindows())
        {
            CenterOnMainWindow(window);
            return;
        }

        if (owner == 0 || !GetWindowRect(owner, out var rect))
        {
            CenterOnMainWindow(window);
            return;
        }

        var size = window.AppWindow.Size;
        var bounds = new RectInt32 { X = rect.Left, Y = rect.Top, Width = rect.Right - rect.Left, Height = rect.Bottom - rect.Top };
        var screen = DisplayArea.GetFromRect(bounds, DisplayAreaFallback.Primary).WorkArea;
        var x = Math.Clamp(bounds.X + (bounds.Width - size.Width) / 2, screen.X, Math.Max(screen.X, screen.X + screen.Width - size.Width));
        var y = Math.Clamp(bounds.Y + (bounds.Height - size.Height) / 2, screen.Y, Math.Max(screen.Y, screen.Y + screen.Height - size.Height));
        window.AppWindow.Move(new PointInt32 { X = x, Y = y });
    }

    /// <summary>
    /// Makes <paramref name="window"/> a modal window of <paramref name="owner"/>: owned, so it stays above it and
    /// never falls behind, and the owner takes no input until it closes, when the owner gets it back and comes forward.
    /// </summary>
    public static void OwnModally(Window window, nint owner)
    {
        // A Win32 owner window; Windows only.
        if (!OperatingSystem.IsWindows()) return;
        if (owner == 0) return;
        var own = WinRT.Interop.WindowNative.GetWindowHandle(window);
        SetWindowLongPtr(own, GwlpHwndParent, owner);
        if (window.AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMinimizable = false;
            presenter.IsMaximizable = false;
        }

        EnableWindow(owner, false);
        // Before the window goes, so Windows hands the focus to the owner rather than to some other app.
        window.AppWindow.Closing += (_, _) => EnableWindow(owner, true);
        window.Closed += (_, _) =>
        {
            EnableWindow(owner, true);
            SetForegroundWindow(owner);
        };
    }

    /// <summary>The largest visible, not minimised top-level window of another process running this same program.</summary>
    private static RectInt32? MainWindowBounds(nint own)
    {
        // The Win32 window calls below are Windows-only; elsewhere this does what it does when nothing is found.
        if (!OperatingSystem.IsWindows()) return null;
        var self = Environment.ProcessId;
        var exe = Environment.ProcessPath;
        RectInt32? best = null;
        EnumWindows((hwnd, _) =>
        {
            if (hwnd == own || !IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == self) return true;
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById((int)pid);
                if (!string.Equals(process.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return true;
            }
            if (!GetWindowRect(hwnd, out var rect)) return true;
            var candidate = new RectInt32 { X = rect.Left, Y = rect.Top, Width = rect.Right - rect.Left, Height = rect.Bottom - rect.Top };
            if (best is not { } current || candidate.Width * candidate.Height > current.Width * current.Height) best = candidate;
            return true;
        }, 0);
        return best;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    private delegate bool EnumWindowsProc(nint hwnd, nint parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    private const int GwlpHwndParent = -8;

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);

    [DllImport("user32.dll")]
    private static extern bool EnableWindow(nint hwnd, bool enable);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hwnd);
}
