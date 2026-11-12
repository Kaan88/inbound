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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProp(nint window, string name, nint data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetProp(nint window, string name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint RemoveProp(nint window, string name);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint window);

    // Marks the main window, so the app's progress windows find it among its windows, in its process or another (U-WL-4, T11).
    private const string MainWindowMark = "DownloadManager.MainWindow";

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
            if (x != position.X || y != position.Y) appWindow.Move(new PointInt32 { X = x, Y = y });
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Placement is a convenience; never fail a window over it.
        }
    }

    /// <summary>Marks <paramref name="window"/> as the main window for the app's progress, package and completion windows.</summary>
    public static void MarkMain(Window window)
    {
        // The Win32 window calls below are Windows-only; elsewhere this does what it does when nothing is found.
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
            SetProp(handle, MainWindowMark, 1);
            window.Closed += (_, _) => RemoveProp(handle, MainWindowMark);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            // Placement is a convenience; never fail a window over it.
        }
    }

    /// <summary>
    /// Call once the window has its size and its remembered place, before <see cref="Apply"/>: a place on another screen
    /// than the shown main window moves over the main window (<see cref="WindowCascadeRules.OntoMainScreen"/>). A hidden
    /// or minimized main window moves nothing.
    /// </summary>
    public static void OntoMainScreen(Window window)
    {
        try
        {
            var own = WinRT.Interop.WindowNative.GetWindowHandle(window);
            if (MainWindowBounds(own) is not { } main) return;
            var display = DisplayArea.GetFromRect(new RectInt32 { X = main.X, Y = main.Y, Width = main.Width, Height = main.Height }, DisplayAreaFallback.Nearest);
            if (display is null) return;
            var appWindow = window.AppWindow;
            var position = appWindow.Position;
            var size = appWindow.Size;
            var bounds = display.OuterBounds;
            var work = display.WorkArea;
            var (x, y) = WindowCascadeRules.OntoMainScreen(position.X, position.Y, size.Width, size.Height, main,
                new ScreenRect(bounds.X, bounds.Y, bounds.Width, bounds.Height), new ScreenRect(work.X, work.Y, work.Width, work.Height));
            if (x != position.X || y != position.Y) appWindow.Move(new PointInt32 { X = x, Y = y });
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Placement is a convenience; never fail a window over it.
        }
    }

    /// <summary>The shown, not minimized, main window of this app, if there is one: in this process (T11) or another.</summary>
    private static ScreenRect? MainWindowBounds(nint own)
    {
        // The Win32 window calls below are Windows-only; elsewhere this does what it does when nothing is found.
        if (!OperatingSystem.IsWindows()) return null;
        var ids = AppProcessIds();
        ScreenRect? found = null;
        EnumWindows((handle, _) =>
        {
            if (handle != own && IsWindowVisible(handle) && !IsIconic(handle) && GetWindowThreadProcessId(handle, out var pid) != 0 &&
                ids.Contains(pid) && GetProp(handle, MainWindowMark) != 0 && GetWindowRect(handle, out var rect) && rect.Right > rect.Left)
            {
                found = new ScreenRect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
                return false;
            }

            return true;
        }, 0);
        return found;
    }

    /// <summary>
    /// This process and the app's others. T11: the main window opens progress windows in its own process, so its windows
    /// count too; the window being placed is left out by its handle.
    /// </summary>
    private static HashSet<uint> AppProcessIds()
    {
        // The Win32 window calls below are Windows-only; elsewhere this does what it does when nothing is found.
        if (!OperatingSystem.IsWindows()) return [];
        using var current = Process.GetCurrentProcess();
        var ids = new HashSet<uint> { (uint)current.Id };
        foreach (var process in Process.GetProcessesByName(current.ProcessName))
        {
            using (process) ids.Add((uint)process.Id);
        }

        return ids;
    }

    /// <summary>The top-left corners of the app's other visible top-level windows, in this process or another (same program).</summary>
    private static List<(int X, int Y)> OtherWindowCorners(nint own)
    {
        var ids = AppProcessIds();
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
