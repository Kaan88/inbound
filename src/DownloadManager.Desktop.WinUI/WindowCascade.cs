using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace DownloadManager.Desktop.WinUI;

/// <summary>Moves a new window off the app's other windows when it would open exactly on one of them (<see cref="WindowCascadeRules"/>).</summary>
internal static class WindowCascade
{
    private delegate bool EnumWindowsProc(nint window, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out Rect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    /// <summary>Call once the window has its size and its remembered place.</summary>
    public static void Apply(Window window)
    {
        try
        {
            var appWindow = window.AppWindow;
            var own = WinRT.Interop.WindowNative.GetWindowHandle(window);
            var taken = OtherWindowCorners(own);
            if (taken.Count == 0) return;
            var area = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
            var scale = window.Content?.XamlRoot?.RasterizationScale ?? 1.0;
            var position = appWindow.Position;
            var size = appWindow.Size;
            var (x, y) = WindowCascadeRules.Place(position.X, position.Y, taken, area.X, area.Y, area.Width, area.Height,
                size.Width, size.Height, (int)Math.Round(WindowCascadeRules.Step * scale));
            if (x != position.X || y != position.Y) appWindow.Move(new PointInt32(x, y));
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Placement is a convenience; never fail a window over it.
        }
    }

    /// <summary>The top-left corners of the visible top-level windows of this app's other processes (same program).</summary>
    private static List<(int X, int Y)> OtherWindowCorners(nint own)
    {
        var current = Process.GetCurrentProcess();
        var ids = new HashSet<uint>();
        foreach (var process in Process.GetProcessesByName(current.ProcessName))
        {
            using (process)
            {
                if (process.Id != current.Id) ids.Add((uint)process.Id);
            }
        }

        var corners = new List<(int X, int Y)>();
        if (ids.Count == 0) return corners;
        EnumWindows((handle, _) =>
        {
            if (handle != own && IsWindowVisible(handle) && GetWindowThreadProcessId(handle, out var pid) != 0 && ids.Contains(pid) &&
                GetWindowRect(handle, out var rect) && rect.Right > rect.Left)
                corners.Add((rect.Left, rect.Top));
            return true;
        }, 0);
        return corners;
    }
}
