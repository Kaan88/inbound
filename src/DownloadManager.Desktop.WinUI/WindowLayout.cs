using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace DownloadManager.Desktop.WinUI;

internal static class WindowLayout
{
    public static void ResizeForDpi(Window window, int width, int height) => window.AppWindow.Resize(ScaledToFit(window, width, height));

    /// <summary>How small the user can drag the window, and how small a remembered size can come back. Set it before restoring one.</summary>
    public static void SetMinimumSize(Window window, int width, int height)
    {
        if (window.AppWindow.Presenter is not OverlappedPresenter presenter) return;
        var size = ScaledToFit(window, width, height);
        presenter.PreferredMinimumWidth = size.Width;
        presenter.PreferredMinimumHeight = size.Height;
    }

    private static SizeInt32 ScaledToFit(Window window, int width, int height)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var scale = Math.Max(96, GetDpiForWindow(hwnd)) / 96d;
        var area = DisplayArea.GetFromWindowId(window.AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        return new SizeInt32(
            Math.Min((int)Math.Ceiling(width * scale), Math.Max(200, area.Width - 24)),
            Math.Min((int)Math.Ceiling(height * scale), Math.Max(200, area.Height - 24)));
    }

    /// <summary>
    /// Gives a secondary window the same chrome as the main window: themed title bar (so a dark window
    /// never sits under a white caption) and the app icon. Re-applies when the root's theme changes.
    /// </summary>
    public static void ApplyChrome(Window window, FrameworkElement root)
    {
        var icon = Path.Combine(AppContext.BaseDirectory, "app.ico");
        if (File.Exists(icon)) window.AppWindow.SetIcon(icon);
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
        var titleBar = window.AppWindow.TitleBar;
        // Matches InboundChromeBrush (#202020 / #F3F3F3).
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
        var own = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var size = window.AppWindow.Size;
        var target = MainWindowBounds(own);
        if (target is null)
        {
            var area = DisplayArea.GetFromWindowId(window.AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
            target = new RectInt32(area.X, area.Y, area.Width, area.Height);
        }
        var bounds = target.Value;
        var x = bounds.X + (bounds.Width - size.Width) / 2;
        var y = bounds.Y + (bounds.Height - size.Height) / 2;
        // Never off the screen the main window is on.
        var screen = DisplayArea.GetFromRect(bounds, DisplayAreaFallback.Primary).WorkArea;
        x = Math.Clamp(x, screen.X, Math.Max(screen.X, screen.X + screen.Width - size.Width));
        y = Math.Clamp(y, screen.Y, Math.Max(screen.Y, screen.Y + screen.Height - size.Height));
        window.AppWindow.Move(new PointInt32(x, y));
    }

    /// <summary>The largest visible, not minimised top-level window of another process running this same program.</summary>
    private static RectInt32? MainWindowBounds(nint own)
    {
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
            var candidate = new RectInt32(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
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
}
