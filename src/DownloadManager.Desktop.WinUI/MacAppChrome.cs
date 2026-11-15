#if HAS_UNO
using System.Runtime.InteropServices;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// MAC.6: what a Mac app has outside its window, through the Objective-C runtime (Uno's macOS window is AppKit
/// underneath, and this runs on its main thread):
/// <list type="bullet">
/// <item>The menu bar's own menus in place of Uno's two-item default: Inbound (About, Settings… ⌘,, Services, Hide ⌘H,
/// Hide Others ⌥⌘H, Show All, Quit ⌘Q, which is File > Exit) and Window (Minimize ⌘M, Zoom, Close ⌘W, Bring All to
/// Front). The window's own menu bar stays: it has everything else.</item>
/// <item>The Dock icon: the number of running downloads as its badge, and their overall progress as a bar.</item>
/// </list>
/// Nothing happens off a Mac. Not drawn by the CI's self-test; the owner's Mac is where it is looked at.
/// </summary>
internal static class MacAppChrome
{
    private const string Lib = "/usr/lib/libobjc.A.dylib";

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ActionImp(IntPtr self, IntPtr command, IntPtr sender);

    // Kept for as long as the process runs: AppKit holds only the function pointers.
    private static readonly List<ActionImp> Imps = [];
    private static readonly Dictionary<string, Action> Actions = new(StringComparer.Ordinal);
    private static IntPtr _target;
    private static string? _badge;
    private static int _progressStep = -1;
    private static IntPtr _progressBar;

    /// <summary>Puts the app menu in place. <paramref name="about"/>, <paramref name="settings"/> and <paramref name="quit"/> run on the UI thread.</summary>
    public static void InstallMenu(Action about, Action settings, Action quit)
    {
        if (!OperatingSystem.IsMacOS() || _target != IntPtr.Zero) return;
        try
        {
            _target = Target(new() { ["inboundAbout:"] = about, ["inboundSettings:"] = settings, ["inboundQuit:"] = quit });
            var app = Send(Class("NSApplication"), Sel("sharedApplication"));
            var main = New("NSMenu");

            var appMenu = Submenu(main, "Inbound");
            Add(appMenu, Item("About Inbound", "inboundAbout:", string.Empty, _target));
            Add(appMenu, Separator());
            Add(appMenu, Item("Settings…", "inboundSettings:", ",", _target));
            Add(appMenu, Separator());
            var services = Submenu(appMenu, "Services");
            Send(app, Sel("setServicesMenu:"), services);
            Add(appMenu, Separator());
            Add(appMenu, Item("Hide Inbound", "hide:", "h", IntPtr.Zero));
            var others = Item("Hide Others", "hideOtherApplications:", "h", IntPtr.Zero);
            SendUInt(others, Sel("setKeyEquivalentModifierMask:"), CommandMask | OptionMask);
            Add(appMenu, others);
            Add(appMenu, Item("Show All", "unhideAllApplications:", string.Empty, IntPtr.Zero));
            Add(appMenu, Separator());
            Add(appMenu, Item("Quit Inbound", "inboundQuit:", "q", _target));

            var window = Submenu(main, "Window");
            Add(window, Item("Minimize", "performMiniaturize:", "m", IntPtr.Zero));
            Add(window, Item("Zoom", "performZoom:", string.Empty, IntPtr.Zero));
            Add(window, Item("Close Window", "performClose:", "w", IntPtr.Zero));
            Add(window, Separator());
            Add(window, Item("Bring All to Front", "arrangeInFront:", string.Empty, IntPtr.Zero));
            Send(app, Sel("setWindowsMenu:"), window);

            Send(app, Sel("setMainMenu:"), main);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            UiTrace.Write($"Mac app menu not installed: {ex.GetType().Name}");
        }
    }

    /// <summary>
    /// The Dock icon: <paramref name="active"/> as its badge (none at 0), and <paramref name="progress"/> (0 to 1, null
    /// for none or unknown sizes) as a bar along its foot. Redrawn only when either visibly changes.
    /// </summary>
    public static void ShowActivity(int active, double? progress)
    {
        if (!OperatingSystem.IsMacOS()) return;
        try
        {
            var dock = Send(Send(Class("NSApplication"), Sel("sharedApplication")), Sel("dockTile"));
            var badge = active > 0 ? active.ToString(System.Globalization.CultureInfo.CurrentCulture) : string.Empty;
            if (badge != _badge)
            {
                _badge = badge;
                Send(dock, Sel("setBadgeLabel:"), String(badge));
            }

            var step = active > 0 && progress is { } value ? (int)Math.Round(Math.Clamp(value, 0, 1) * 100) : -1;
            if (step == _progressStep) return;
            _progressStep = step;
            if (step < 0)
            {
                Send(dock, Sel("setContentView:"), IntPtr.Zero); // the plain icon again
                _progressBar = IntPtr.Zero;
            }
            else
            {
                if (_progressBar == IntPtr.Zero) _progressBar = ProgressView(dock);
                SendDouble(_progressBar, Sel("setDoubleValue:"), step);
            }

            Send(dock, Sel("display"));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            UiTrace.Write($"Dock activity not shown: {ex.GetType().Name}");
        }
    }

    /// <summary>The overall progress of what runs: bytes received over total, for downloads whose size is known.</summary>
    public static double? OverallProgress(IEnumerable<DownloadManager.Contracts.JobDto> running)
    {
        long received = 0, total = 0;
        foreach (var job in running)
        {
            if (job.TotalBytes is not > 0) return null; // one unknown size makes any bar a guess
            received += Math.Min(job.ReceivedBytes, job.TotalBytes.Value);
            total += job.TotalBytes.Value;
        }

        return total > 0 ? (double)received / total : null;
    }

    /// <summary>The Dock tile's view: the app's icon with a bar over its foot. Returns the bar.</summary>
    private static IntPtr ProgressView(IntPtr dock)
    {
        var size = SendSize(dock, Sel("size"));
        var view = Send(Send(Class("NSImageView"), Sel("alloc")), Sel("initWithFrame:"), new CGRect(0, 0, size.Width, size.Height));
        var icon = Send(Send(Class("NSApplication"), Sel("sharedApplication")), Sel("applicationIconImage"));
        Send(view, Sel("setImage:"), icon);
        var bar = Send(Send(Class("NSProgressIndicator"), Sel("alloc")), Sel("initWithFrame:"),
            new CGRect(size.Width * 0.1, size.Height * 0.06, size.Width * 0.8, size.Height * 0.14));
        SendUInt(bar, Sel("setStyle:"), 0); // NSProgressIndicatorStyleBar
        SendBool(bar, Sel("setIndeterminate:"), false);
        SendDouble(bar, Sel("setMinValue:"), 0);
        SendDouble(bar, Sel("setMaxValue:"), 100);
        Send(view, Sel("addSubview:"), bar);
        Send(dock, Sel("setContentView:"), view);
        return bar;
    }

    private const nuint CommandMask = 1 << 20, OptionMask = 1 << 19;

    private static IntPtr Target(Dictionary<string, Action> actions)
    {
        var type = AllocateClassPair(Class("NSObject"), "InboundMenuTarget", 0);
        if (type == IntPtr.Zero) type = Class("InboundMenuTarget"); // a second window in one process
        else
        {
            foreach (var selector in actions.Keys)
            {
                ActionImp imp = Invoke;
                Imps.Add(imp);
                AddMethod(type, Sel(selector), Marshal.GetFunctionPointerForDelegate(imp), "v@:@");
            }

            RegisterClassPair(type);
        }

        foreach (var (selector, action) in actions) Actions[selector] = action;
        return Send(Send(type, Sel("alloc")), Sel("init"));
    }

    private static void Invoke(IntPtr self, IntPtr command, IntPtr sender)
    {
        try
        {
            var name = Marshal.PtrToStringUTF8(SelName(command));
            if (name is not null && Actions.TryGetValue(name, out var action)) action();
        }
        catch (Exception ex)
        {
            // Never into AppKit: an exception through a native frame ends the process.
            UiTrace.Write($"Mac menu action failed: {ex.GetType().Name}");
        }
    }

    private static IntPtr New(string type) => Send(Send(Class(type), Sel("alloc")), Sel("init"));

    private static IntPtr Submenu(IntPtr parent, string title)
    {
        var menu = Send(Send(Class("NSMenu"), Sel("alloc")), Sel("initWithTitle:"), String(title));
        var holder = Item(title, null, string.Empty, IntPtr.Zero);
        Send(holder, Sel("setSubmenu:"), menu);
        Add(parent, holder);
        return menu;
    }

    private static IntPtr Item(string title, string? action, string key, IntPtr target)
    {
        var item = Send(Send(Class("NSMenuItem"), Sel("alloc")), Sel("initWithTitle:action:keyEquivalent:"),
            String(title), action is null ? IntPtr.Zero : Sel(action), String(key));
        if (target != IntPtr.Zero) Send(item, Sel("setTarget:"), target);
        return item;
    }

    private static IntPtr Separator() => Send(Class("NSMenuItem"), Sel("separatorItem"));

    private static void Add(IntPtr menu, IntPtr item) => Send(menu, Sel("addItem:"), item);

    private static IntPtr String(string text) => SendText(Class("NSString"), Sel("stringWithUTF8String:"), text);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct CGRect(double X, double Y, double Width, double Height);

    [StructLayout(LayoutKind.Sequential)]
    private struct CGSize { public double Width, Height; }

    [DllImport(Lib, EntryPoint = "objc_getClass")] private static extern IntPtr Class(string name);
    [DllImport(Lib, EntryPoint = "sel_registerName")] private static extern IntPtr Sel(string name);
    [DllImport(Lib, EntryPoint = "sel_getName")] private static extern IntPtr SelName(IntPtr selector);
    [DllImport(Lib, EntryPoint = "objc_allocateClassPair")] private static extern IntPtr AllocateClassPair(IntPtr superclass, string name, nuint extraBytes);
    [DllImport(Lib, EntryPoint = "objc_registerClassPair")] private static extern void RegisterClassPair(IntPtr type);
    [DllImport(Lib, EntryPoint = "class_addMethod")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool AddMethod(IntPtr type, IntPtr selector, IntPtr imp, string types);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr argument);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr a, IntPtr b, IntPtr c);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector, CGRect frame);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] private static extern IntPtr SendUInt(IntPtr receiver, IntPtr selector, nuint value);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] private static extern void SendDouble(IntPtr receiver, IntPtr selector, double value);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] private static extern void SendBool(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.I1)] bool value);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] private static extern CGSize SendSize(IntPtr receiver, IntPtr selector);
    [DllImport(Lib, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendText(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string text);
}
#endif
