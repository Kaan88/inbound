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

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);
}
