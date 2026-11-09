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
        var icon = Path.Combine(AppContext.BaseDirectory, "app.ico");
        if (File.Exists(icon)) window.AppWindow.SetIcon(icon);
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
        var titleBar = window.AppWindow.TitleBar;
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

    /// <summary>
    /// Puts a window in the middle of <paramref name="owner"/>, a window of this process, kept on that window's screen.
    /// Falls back to <see cref="CenterOnMainWindow"/> without one.
    /// </summary>
    public static void CenterOn(Window window, nint owner)
    {
        if (owner == 0 || !GetWindowRect(owner, out var rect))
        {
            CenterOnMainWindow(window);
            return;
        }

        var size = window.AppWindow.Size;
        var bounds = new RectInt32(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        var screen = DisplayArea.GetFromRect(bounds, DisplayAreaFallback.Primary).WorkArea;
        var x = Math.Clamp(bounds.X + (bounds.Width - size.Width) / 2, screen.X, Math.Max(screen.X, screen.X + screen.Width - size.Width));
        var y = Math.Clamp(bounds.Y + (bounds.Height - size.Height) / 2, screen.Y, Math.Max(screen.Y, screen.Y + screen.Height - size.Height));
        window.AppWindow.Move(new PointInt32(x, y));
    }

    /// <summary>
    /// Makes <paramref name="window"/> a modal window of <paramref name="owner"/>: owned, so it stays above it and
    /// never falls behind, and the owner takes no input until it closes, when the owner gets it back and comes forward.
    /// </summary>
    public static void OwnModally(Window window, nint owner)
    {
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

    private const int GwlpHwndParent = -8;

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);

    [DllImport("user32.dll")]
    private static extern bool EnableWindow(nint hwnd, bool enable);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hwnd);
}
