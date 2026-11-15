using System.Runtime.InteropServices;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// The work area a window opens on, for Uno, whose <c>DisplayArea</c> is not implemented (it throws). On X11 it asks the
/// X server for the monitors (XRandR) and the desktop's work area (<c>_NET_WORKAREA</c>, the screen less the panels); on
/// Windows, Win32 for the monitor's work area. The result is in screen pixels, which is what Uno's <c>AppWindow</c>
/// takes and reports (Uno 6.7: X11WindowWrapper.Move/Resize pass the numbers to XMoveWindow/XResizeWindow as they are,
/// and Position/Size are read back from the X server; Win32WindowWrapper uses SetWindowPos). Null when nothing can be told,
/// and the caller then keeps to <see cref="WindowSizeRules.UnknownScreenWidth"/> by
/// <see cref="WindowSizeRules.UnknownScreenHeight"/>. Asked only when a window opens; it keeps no connection open.
/// </summary>
internal static class UnoScreens
{
    /// <summary>
    /// The work area of the monitor holding <paramref name="x"/>, <paramref name="y"/> (screen pixels), or of the one with
    /// the mouse, or the primary one.
    /// </summary>
    public static ScreenRect? WorkArea(int? x = null, int? y = null)
    {
        try
        {
            ScreenRect? pixels = null;
            if (OperatingSystem.IsLinux()) pixels = X11WorkArea(x, y);
            else if (OperatingSystem.IsMacOS()) pixels = MacWorkArea(x, y);
            else if (OperatingSystem.IsWindows()) pixels = Win32WorkArea(x, y);
            if (pixels is null) UiTrace.Write("screen size unknown");
            else if (OperatingSystem.IsMacOS()) UiTrace.Write($"work area {pixels}, scale {Scale()}");
            return pixels;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException
                                       or NotImplementedException or NotSupportedException or COMException)
        {
            UiTrace.Write("screen size unknown: " + ex.GetType().Name);
            return null;
        }
    }

    /// <summary>Screen pixels for each DIP of the window's content (Uno lays the content out at pixels over this); 1 when Uno can't tell.</summary>
    public static double Scale()
    {
        try
        {
            var scale = Windows.Graphics.Display.DisplayInformation.GetForCurrentView().RawPixelsPerViewPixel;
            return double.IsFinite(scale) && scale >= 1 ? scale : 1;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotImplementedException or NullReferenceException or COMException)
        {
            return 1;
        }
    }

    // ---- macOS ----
    //
    // Uno's macOS AppWindow takes and reports pixels: MacOSWindowWrapper.Move/Resize divide by the window's
    // RawPixelsPerViewPixel and hand Cocoa points, with y measured down from the top of the first screen
    // (uno_desktop_top: NSMaxY(NSScreen.screens[0].frame)). Without this the window opened at the unknown-screen size in
    // pixels, which a Retina screen halves (550 by 350 points), and grew to its minimum at the first resize.

    /// <summary>A standard macOS title bar, in points.</summary>
    private const double MacTitleBarPoints = 28;

    private static ScreenRect? MacWorkArea(int? x, int? y)
    {
        var screens = ObjC.Send(ObjC.Class("NSScreen"), ObjC.Selector("screens"));
        var count = (int)ObjC.SendUInt(screens, ObjC.Selector("count"));
        if (count == 0) return null;
        var first = ObjC.SendRect(ObjC.SendIndex(screens, ObjC.Selector("objectAtIndex:"), 0), ObjC.Selector("frame"));
        var desktopTop = first.Y + first.Height;

        // The scale Uno divides by (RawPixelsPerViewPixel), not each screen's backingScaleFactor: sizes here must come out
        // the same once Uno turns them back into points.
        var scale = Scale();
        var monitors = new List<ScreenRect>(count);
        for (var i = 0; i < count; i++)
        {
            var screen = ObjC.SendIndex(screens, ObjC.Selector("objectAtIndex:"), (nuint)i);
            var visible = ObjC.SendRect(screen, ObjC.Selector("visibleFrame")); // less the menu bar and the Dock
            // Uno's macOS AppWindow sizes are the content's, below the title bar (uno_window_resize adds it): a window
            // as tall as the visible frame ran a title bar's height under the Dock. The work area leaves room for it.
            monitors.Add(new ScreenRect(
                (int)Math.Round(visible.X * scale),
                (int)Math.Round((desktopTop - (visible.Y + visible.Height)) * scale),
                (int)Math.Round(visible.Width * scale),
                (int)Math.Round(Math.Max(0, visible.Height - MacTitleBarPoints) * scale)));
        }

        if (x is null || y is null)
        {
            // A new window opens on the screen with the mouse. NSEvent.mouseLocation is in points, from the bottom.
            var mouse = ObjC.SendPoint(ObjC.Class("NSEvent"), ObjC.Selector("mouseLocation"));
            x = (int)Math.Round(mouse.X * scale);
            y = (int)Math.Round((desktopTop - mouse.Y) * scale);
        }

        return WindowSizeRules.WorkAreaFor(monitors, 0, null, x, y);
    }

    /// <summary>
    /// macOS: the window titled <paramref name="title"/> as Cocoa has it, in global points with a top-left origin: the
    /// frame's top-left corner and the title bar's height. Uno's AppWindow.Position can differ (it keeps what it was asked
    /// for when macOS moved the window below the menu bar). Null when no such window, or off macOS.
    /// </summary>
    public static (double Left, double Top, double TitleBar)? MacWindowFrame(string title)
    {
        if (!OperatingSystem.IsMacOS()) return null;
        var screens = ObjC.Send(ObjC.Class("NSScreen"), ObjC.Selector("screens"));
        var first = ObjC.SendRect(ObjC.SendIndex(screens, ObjC.Selector("objectAtIndex:"), 0), ObjC.Selector("frame"));
        var desktopTop = first.Y + first.Height;
        var app = ObjC.Send(ObjC.Class("NSApplication"), ObjC.Selector("sharedApplication"));
        var windows = ObjC.Send(app, ObjC.Selector("windows"));
        var count = (int)ObjC.SendUInt(windows, ObjC.Selector("count"));
        for (var i = 0; i < count; i++)
        {
            var window = ObjC.SendIndex(windows, ObjC.Selector("objectAtIndex:"), (nuint)i);
            var name = Marshal.PtrToStringUTF8(ObjC.Send(ObjC.Send(window, ObjC.Selector("title")), ObjC.Selector("UTF8String")));
            if (name != title) continue;
            var frame = ObjC.SendRect(window, ObjC.Selector("frame"));
            var content = ObjC.SendRect(ObjC.Send(window, ObjC.Selector("contentView")), ObjC.Selector("frame"));
            return (frame.X, desktopTop - (frame.Y + frame.Height), frame.Height - content.Height);
        }

        return null;
    }

    private static class ObjC
    {
        private const string Lib = "/usr/lib/libobjc.A.dylib";

        [StructLayout(LayoutKind.Sequential)]
        public struct CGPoint { public double X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct CGRect { public double X, Y, Width, Height; }

        [DllImport(Lib, EntryPoint = "objc_getClass")]
        public static extern IntPtr Class(string name);

        [DllImport(Lib, EntryPoint = "sel_registerName")]
        public static extern IntPtr Selector(string name);

        [DllImport(Lib, EntryPoint = "objc_msgSend")]
        public static extern IntPtr Send(IntPtr receiver, IntPtr selector);

        [DllImport(Lib, EntryPoint = "objc_msgSend")]
        public static extern nuint SendUInt(IntPtr receiver, IntPtr selector);

        [DllImport(Lib, EntryPoint = "objc_msgSend")]
        public static extern IntPtr SendIndex(IntPtr receiver, IntPtr selector, nuint index);

        [DllImport(Lib, EntryPoint = "objc_msgSend")]
        public static extern CGPoint SendPoint(IntPtr receiver, IntPtr selector);

        // A 32-byte struct comes back in registers on arm64 and through a hidden pointer on x86_64 (objc_msgSend_stret).
        [DllImport(Lib, EntryPoint = "objc_msgSend")]
        private static extern CGRect SendRectArm64(IntPtr receiver, IntPtr selector);

        [DllImport(Lib, EntryPoint = "objc_msgSend_stret")]
        private static extern void SendRectX64(out CGRect result, IntPtr receiver, IntPtr selector);

        public static CGRect SendRect(IntPtr receiver, IntPtr selector)
        {
            if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64) return SendRectArm64(receiver, selector);
            SendRectX64(out var rect, receiver, selector);
            return rect;
        }
    }

    // ---- X11 ----

    private const string Lib = "libX11.so.6";

    private static ScreenRect? X11WorkArea(int? x, int? y)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))) return null;
        var display = XOpenDisplay(IntPtr.Zero);
        if (display == IntPtr.Zero) return null;
        try
        {
            var root = XDefaultRootWindow(display);
            var (monitors, primary) = Monitors(display, root);
            if (x is null || y is null)
            {
                // A new window opens where the window manager puts it, which is mostly the monitor with the mouse.
                if (XQueryPointer(display, root, out _, out _, out var mouseX, out var mouseY, out _, out _, out _) != 0)
                {
                    x = mouseX;
                    y = mouseY;
                }
            }

            return WindowSizeRules.WorkAreaFor(monitors, primary, DesktopWorkArea(display, root), x, y);
        }
        finally
        {
            XCloseDisplay(display);
        }
    }

    private static (List<ScreenRect> Monitors, int Primary) Monitors(IntPtr display, IntPtr root)
    {
        var list = new List<ScreenRect>();
        var primary = -1;
        try
        {
            var infos = XRRGetMonitors(display, root, true, out var count);
            if (infos != IntPtr.Zero)
            {
                try
                {
                    var size = Marshal.SizeOf<XRRMonitorInfo>();
                    for (var i = 0; i < count; i++)
                    {
                        var info = Marshal.PtrToStructure<XRRMonitorInfo>(infos + i * size);
                        if (info.Primary != 0 && primary < 0) primary = list.Count;
                        list.Add(new ScreenRect(info.X, info.Y, info.Width, info.Height));
                    }
                }
                finally
                {
                    XRRFreeMonitors(infos);
                }
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // No XRandR: the whole X screen below.
        }

        if (list.Count == 0)
        {
            var screen = XDefaultScreen(display);
            list.Add(new ScreenRect(0, 0, XDisplayWidth(display, screen), XDisplayHeight(display, screen)));
            primary = 0;
        }

        return (list, primary);
    }

    /// <summary><c>_NET_WORKAREA</c> for the current desktop: x, y, width, height for each desktop. Null without one.</summary>
    private static ScreenRect? DesktopWorkArea(IntPtr display, IntPtr root)
    {
        var areas = Cardinals(display, root, "_NET_WORKAREA");
        if (areas.Count < 4) return null;
        var desktop = Cardinals(display, root, "_NET_CURRENT_DESKTOP") is [var current, ..] ? (int)current : 0;
        var at = desktop >= 0 && (desktop + 1) * 4 <= areas.Count ? desktop * 4 : 0;
        return new ScreenRect((int)areas[at], (int)areas[at + 1], (int)areas[at + 2], (int)areas[at + 3]);
    }

    private static List<long> Cardinals(IntPtr display, IntPtr window, string name)
    {
        var values = new List<long>();
        var atom = XInternAtom(display, name, true);
        if (atom == IntPtr.Zero) return values;
        if (XGetWindowProperty(display, window, atom, 0, 1024, false, IntPtr.Zero, out _, out var format, out var items, out _, out var data) != 0
            || data == IntPtr.Zero) return values;
        try
        {
            // Format 32 items are C longs in the client's memory, 8 bytes each on 64-bit.
            if (format != 32) return values;
            for (var i = 0; i < (int)Math.Min(items, 1024UL); i++)
                values.Add(IntPtr.Size == 8 ? Marshal.ReadInt64(data, i * 8) : Marshal.ReadInt32(data, i * 4));
            return values;
        }
        finally
        {
            XFree(data);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XRRMonitorInfo
    {
        public IntPtr Name;
        public int Primary, Automatic, OutputCount, X, Y, Width, Height, WidthMillimetres, HeightMillimetres;
        public IntPtr Outputs;
    }

    [DllImport(Lib)] private static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport(Lib)] private static extern int XCloseDisplay(IntPtr display);
    [DllImport(Lib)] private static extern IntPtr XDefaultRootWindow(IntPtr display);
    [DllImport(Lib)] private static extern int XDefaultScreen(IntPtr display);
    [DllImport(Lib)] private static extern int XDisplayWidth(IntPtr display, int screen);
    [DllImport(Lib)] private static extern int XDisplayHeight(IntPtr display, int screen);
    [DllImport(Lib)] private static extern IntPtr XInternAtom(IntPtr display, string name, bool onlyIfExists);
    [DllImport(Lib)] private static extern int XFree(IntPtr data);
    [DllImport(Lib)]
    private static extern int XQueryPointer(IntPtr display, IntPtr window, out IntPtr root, out IntPtr child,
        out int rootX, out int rootY, out int windowX, out int windowY, out uint mask);
    [DllImport(Lib)]
    private static extern int XGetWindowProperty(IntPtr display, IntPtr window, IntPtr property, long offset, long length, bool delete,
        IntPtr requestedType, out IntPtr actualType, out int actualFormat, out ulong items, out ulong bytesAfter, out IntPtr data);
    [DllImport("libXrandr.so.2")] private static extern IntPtr XRRGetMonitors(IntPtr display, IntPtr window, bool getActive, out int count);
    [DllImport("libXrandr.so.2")] private static extern void XRRFreeMonitors(IntPtr monitors);

    // ---- Windows (the Uno build run there) ----

    private static ScreenRect? Win32WorkArea(int? x, int? y)
    {
        if (x is null || y is null)
        {
            if (!GetCursorPos(out var cursor)) return null;
            x = cursor.X;
            y = cursor.Y;
        }

        var monitor = MonitorFromPoint(new Win32Point { X = x.Value, Y = y.Value }, 2 /* MONITOR_DEFAULTTONEAREST */);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return null;
        var work = info.Work;
        return new ScreenRect(work.Left, work.Top, work.Right - work.Left, work.Bottom - work.Top);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Win32Point { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Win32Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public Win32Rect Monitor, Work; public uint Flags; }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Win32Point point);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(Win32Point point, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
