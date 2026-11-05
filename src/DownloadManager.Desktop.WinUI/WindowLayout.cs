using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace DownloadManager.Desktop.WinUI;

internal static class WindowLayout
{
    public static void ResizeForDpi(Window window, int width, int height)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var scale = Math.Max(96, GetDpiForWindow(hwnd)) / 96d;
        var area = DisplayArea.GetFromWindowId(window.AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        window.AppWindow.Resize(new SizeInt32(
            Math.Min((int)Math.Ceiling(width * scale), Math.Max(200, area.Width - 24)),
            Math.Min((int)Math.Ceiling(height * scale), Math.Max(200, area.Height - 24))));
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);
}
