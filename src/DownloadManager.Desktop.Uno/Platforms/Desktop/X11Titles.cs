using System.Runtime.InteropServices;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// Uno's X11 head sets a window's title only with <c>XStoreName</c> (WM_NAME), which window managers read as Latin-1, so
/// the UTF-8 it writes shows as mojibake ("5.4% â big.bin" for an em dash). This copies each of this process's
/// windows' WM_NAME into <c>_NET_WM_NAME</c> as UTF8_STRING, which EWMH window managers (KWin, Mutter) prefer.
/// It has a connection and a thread of its own that sleep in <c>XNextEvent</c>: nothing runs until a window of ours
/// appears or its title changes, so it costs nothing while idle. Without X (Wayland only, no XWayland) it does nothing.
/// </summary>
internal static class X11Titles
{
    private const string Lib = "libX11.so.6";
    private const long SubstructureNotifyMask = 1L << 19;
    private const long PropertyChangeMask = 1L << 22;
    private const int MapNotify = 19, PropertyNotify = 28, CreateNotify = 16, ReparentNotify = 21;
    private const int PropModeReplace = 0;

    public static void Start()
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))) return;
        try
        {
            if (XInitThreads() == 0) return;
            var display = XOpenDisplay(IntPtr.Zero);
            if (display == IntPtr.Zero) return;
            var thread = new Thread(() => Run(display)) { IsBackground = true, Name = "X11 titles" };
            thread.Start();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            UiTrace.Write("X11 titles off: " + ex.GetType().Name);
        }
    }

    private static void Run(IntPtr display)
    {
        try
        {
            var root = XDefaultRootWindow(display);
            var wmName = XInternAtom(display, "WM_NAME", false);
            var netWmName = XInternAtom(display, "_NET_WM_NAME", false);
            var utf8 = XInternAtom(display, "UTF8_STRING", false);
            var pid = Environment.ProcessId;
            var watched = new HashSet<IntPtr>();

            void Watch(IntPtr window)
            {
                if (window == IntPtr.Zero || watched.Contains(window) || Pid(display, window) != pid) return;
                watched.Add(window);
                XSelectInput(display, window, PropertyChangeMask);
                Copy(display, window, wmName, netWmName, utf8);
            }

            // New top-level windows are created as children of the root.
            XSelectInput(display, root, SubstructureNotifyMask);
            foreach (var child in Children(display, root)) Watch(child);
            XFlush(display);
            var ev = new byte[192];
            while (true)
            {
                XNextEvent(display, ev);
                var type = BitConverter.ToInt32(ev, 0);
                // XAnyEvent: type, serial, send_event, display, window; window is the 5th field (offset 32 on 64-bit).
                var window = (IntPtr)BitConverter.ToInt64(ev, 32);
                switch (type)
                {
                    case CreateNotify:
                        // XCreateWindowEvent: ..., parent (32), window (40).
                        Watch((IntPtr)BitConverter.ToInt64(ev, 40));
                        break;
                    case MapNotify or ReparentNotify:
                        // XMapEvent/XReparentEvent: event (32), window (40).
                        Watch((IntPtr)BitConverter.ToInt64(ev, 40));
                        break;
                    case PropertyNotify:
                        // XPropertyEvent: window (32), atom (40).
                        if ((IntPtr)BitConverter.ToInt64(ev, 40) == wmName && watched.Contains(window))
                            Copy(display, window, wmName, netWmName, utf8);
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            UiTrace.Write("X11 titles stopped: " + ex.GetType().Name);
        }
    }

    private static void Copy(IntPtr display, IntPtr window, IntPtr wmName, IntPtr netWmName, IntPtr utf8)
    {
        if (XGetWindowProperty(display, window, wmName, 0, 4096, false, IntPtr.Zero, out _, out var format, out var count, out _, out var data) != 0
            || data == IntPtr.Zero) return;
        try
        {
            if (format != 8) return;
            XChangeProperty(display, window, netWmName, utf8, 8, PropModeReplace, data, (int)count);
            XFlush(display);
        }
        finally { XFree(data); }
    }

    /// <summary>
    /// The process that made a window, from the X-Resource extension (Uno doesn't set <c>_NET_WM_PID</c>); -1 when it
    /// can't be told. The X server knows each client's process, so another program's window never counts as ours.
    /// </summary>
    private static int Pid(IntPtr display, IntPtr window)
    {
        var spec = new XResClientIdSpec { Client = window, Mask = 2 /* XRES_CLIENT_ID_PID_MASK */ };
        if (XResQueryClientIds(display, 1, ref spec, out var count, out var ids) != 0 || ids == IntPtr.Zero) return -1;
        try
        {
            for (var i = 0; i < count; i++)
            {
                var pid = XResGetClientPid(ids + i * Marshal.SizeOf<XResClientIdValue>());
                if (pid > 0) return pid;
            }
            return -1;
        }
        finally { XResClientIdsDestroy(count, ids); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XResClientIdSpec { public IntPtr Client; public uint Mask; }

    [StructLayout(LayoutKind.Sequential)]
    private struct XResClientIdValue { public XResClientIdSpec Spec; public long Length; public IntPtr Value; }

    [DllImport("libXRes.so.1")] private static extern int XResQueryClientIds(IntPtr display, long numSpecs, ref XResClientIdSpec specs, out long numIds, out IntPtr ids);
    [DllImport("libXRes.so.1")] private static extern int XResGetClientPid(IntPtr value);
    [DllImport("libXRes.so.1")] private static extern void XResClientIdsDestroy(long numIds, IntPtr ids);

    private static IEnumerable<IntPtr> Children(IntPtr display, IntPtr root)
    {
        if (XQueryTree(display, root, out _, out _, out var children, out var count) == 0 || children == IntPtr.Zero) return [];
        var list = new List<IntPtr>();
        for (var i = 0; i < count; i++) list.Add(Marshal.ReadIntPtr(children, i * IntPtr.Size));
        XFree(children);
        return list;
    }

    [DllImport(Lib)] private static extern int XInitThreads();
    [DllImport(Lib)] private static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport(Lib)] private static extern IntPtr XDefaultRootWindow(IntPtr display);
    [DllImport(Lib)] private static extern IntPtr XInternAtom(IntPtr display, string name, bool onlyIfExists);
    [DllImport(Lib)] private static extern int XSelectInput(IntPtr display, IntPtr window, long mask);
    [DllImport(Lib)] private static extern int XNextEvent(IntPtr display, byte[] ev);
    [DllImport(Lib)] private static extern int XFlush(IntPtr display);
    [DllImport(Lib)] private static extern int XFree(IntPtr data);
    [DllImport(Lib)] private static extern int XQueryTree(IntPtr display, IntPtr window, out IntPtr root, out IntPtr parent, out IntPtr children, out uint count);
    [DllImport(Lib)] private static extern int XChangeProperty(IntPtr display, IntPtr window, IntPtr property, IntPtr type, int format, int mode, IntPtr data, int elements);
    [DllImport(Lib)]
    private static extern int XGetWindowProperty(IntPtr display, IntPtr window, IntPtr property, long offset, long length, bool delete,
        IntPtr requestedType, out IntPtr actualType, out int actualFormat, out ulong items, out ulong bytesAfter, out IntPtr data);
}
