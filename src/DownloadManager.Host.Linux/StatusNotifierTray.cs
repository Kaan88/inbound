using System.Text;
using Tmds.DBus.Protocol;

namespace DownloadManager.Host;

/// <summary>What the tray offers; the same as the Windows tray's actions, less the updater.</summary>
internal sealed record LinuxTrayActions(
    Action Open,
    Action OpenSettings,
    Action PauseAll,
    Action ResumeAll,
    Action Exit,
    Func<int> ActiveCount);

/// <summary>
/// The tray icon on a Linux desktop: a StatusNotifierItem (<c>org.kde.StatusNotifierItem</c>) with its menu over
/// <c>com.canonical.dbusmenu</c>, registered with the session's <c>org.kde.StatusNotifierWatcher</c>. KDE Plasma shows it
/// natively; GNOME with the AppIndicator extension, and most other panels, do too. Optional: without a watcher the Host
/// runs as before, and the window and the CLI reach it as always. A click opens the window, as on Windows.
/// </summary>
internal sealed class StatusNotifierTray : IAsyncDisposable
{
    private const string ItemPath = "/StatusNotifierItem";
    private const string MenuPath = "/MenuBar";
    private const string ItemInterface = "org.kde.StatusNotifierItem";
    private const string MenuInterface = "com.canonical.dbusmenu";
    private const string PropertiesInterface = "org.freedesktop.DBus.Properties";
    private const string Watcher = "org.kde.StatusNotifierWatcher";

    private readonly LinuxTrayActions _actions;
    private readonly Action<string> _log;
    private readonly Timer _tooltipTimer;
    private DBusConnection? _connection;
    private string _tooltip = string.Empty;
    private int _tooltipPending;

    private StatusNotifierTray(LinuxTrayActions actions, Action<string> log)
    {
        _actions = actions;
        _log = log;
        _tooltipTimer = new Timer(_ => SendTooltip(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>The menu, by item id. Id 0 is the root.</summary>
    private static readonly MenuItem[] Items =
    [
        new(1, "Open Inbound", "folder-download"),
        new(2, "Options", "configure"),
        new(3, null, null),
        new(4, "Pause all", "media-playback-pause"),
        new(5, "Resume all", "media-playback-start"),
        new(6, null, null),
        new(7, "Exit", "application-exit"),
    ];

    private sealed record MenuItem(int Id, string? Label, string? Icon)
    {
        public bool IsSeparator => Label is null;
    }

    /// <summary>Null when the session has no bus or no tray; the reason is logged.</summary>
    public static async Task<StatusNotifierTray?> TryStartAsync(LinuxTrayActions actions, Action<string> log)
    {
        var tray = new StatusNotifierTray(actions, log);
        try
        {
            await tray.StartAsync().ConfigureAwait(false);
            return tray;
        }
        catch (Exception ex) when (ex is DBusErrorReplyException or DBusConnectionException or DBusReadException or IOException or InvalidOperationException)
        {
            log($"No tray icon: {ex.Message}");
            await tray.DisposeAsync().ConfigureAwait(false);
            return null;
        }
    }

    private async Task StartAsync()
    {
        var connection = new DBusConnection(DBusAddress.Session ?? throw new InvalidOperationException("there is no session bus."));
        _connection = connection;
        await connection.ConnectAsync().ConfigureAwait(false);
        connection.AddMethodHandlers([new Handler(ItemPath, HandleItem), new Handler(MenuPath, HandleMenu)]);
        var name = $"org.kde.StatusNotifierItem-{Environment.ProcessId}-1";
        await connection.RequestNameAsync(name, default).ConfigureAwait(false);
        // When the panel restarts (plasmashell, or a GNOME extension reloaded) its new watcher knows no items: register again.
        _watcherObserver = await connection.WatchSignalAsync("org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus",
            "NameOwnerChanged",
            static (message, _) =>
            {
                var reader = message.GetBodyReader();
                return (Name: reader.ReadString(), Old: reader.ReadString(), New: reader.ReadString());
            },
            (exception, change) =>
            {
                if (exception is null && change.Name == Watcher && change.New.Length > 0) _ = RegisterAgainAsync(connection, name);
            }, null, false, ObserverFlags.None).ConfigureAwait(false);
        await connection.CallMethodAsync(BuildRegister(connection, name)).ConfigureAwait(false);
        _log("Tray icon registered.");
    }

    private IDisposable? _watcherObserver;

    private async Task RegisterAgainAsync(DBusConnection connection, string name)
    {
        try
        {
            await connection.CallMethodAsync(BuildRegister(connection, name)).ConfigureAwait(false);
            _log("Tray icon registered again (the panel restarted).");
        }
        catch (Exception ex) when (ex is DBusErrorReplyException or DBusConnectionException or InvalidOperationException)
        {
            _log($"The tray icon could not register again: {ex.GetType().Name}");
        }
    }

    private static MessageBuffer BuildRegister(DBusConnection connection, string name)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(Watcher, "/StatusNotifierWatcher", Watcher, "RegisterStatusNotifierItem", "s");
        writer.WriteString(name);
        return writer.CreateMessage();
    }

    /// <summary>The tooltip follows the active count; sent at most once a second, and only when it changed.</summary>
    public void RequestRefresh()
    {
        if (Interlocked.Exchange(ref _tooltipPending, 1) == 0) _tooltipTimer.Change(1000, Timeout.Infinite);
    }

    private string TooltipText()
    {
        var active = _actions.ActiveCount();
        return active switch
        {
            0 => "No downloads running",
            1 => "1 download running",
            _ => $"{active} downloads running",
        };
    }

    private void SendTooltip()
    {
        Interlocked.Exchange(ref _tooltipPending, 0);
        var connection = _connection;
        if (connection is null) return;
        var text = TooltipText();
        if (text == _tooltip) return;
        _tooltip = text;
        try
        {
            connection.TrySendMessage(BuildSignal(connection, "NewToolTip"));
        }
        catch (Exception ex) when (ex is DBusConnectionException or InvalidOperationException)
        {
            // The panel reads the tooltip again when it shows it.
        }
    }

    private static MessageBuffer BuildSignal(DBusConnection connection, string member)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteSignalHeader(null, ItemPath, ItemInterface, member, null);
        return writer.CreateMessage();
    }

    private void Run(Action action)
    {
        // Off the bus's own thread: an action may take a moment, and the menu must stay responsive.
        _ = Task.Run(() =>
        {
            try { action(); }
            catch (Exception ex) { _log($"Tray action failed: {ex.GetType().Name}"); }
        });
    }

    // ---- org.kde.StatusNotifierItem ----

    private void HandleItem(MethodContext context)
    {
        var request = context.Request;
        if (context.IsDBusIntrospectRequest)
        {
            context.ReplyIntrospectXml([ItemIntrospection], Array.Empty<string>());
            return;
        }

        switch (request.InterfaceAsString, request.MemberAsString)
        {
            case (ItemInterface, "Activate"):
                Run(_actions.Open);
                ReplyEmpty(context);
                break;
            case (ItemInterface, "SecondaryActivate" or "ContextMenu" or "Scroll" or "ProvideXdgActivationToken"):
                ReplyEmpty(context);
                break;
            case (PropertiesInterface, "Get"):
            {
                var reader = request.GetBodyReader();
                reader.ReadString();
                var property = reader.ReadString();
                var writer = context.CreateReplyWriter("v");
                if (!WriteItemProperty(ref writer, property))
                {
                    writer.Dispose();
                    context.ReplyError("org.freedesktop.DBus.Error.UnknownProperty", $"No property {property}.");
                    return;
                }

                context.Reply(writer.CreateMessage());
                writer.Dispose();
                break;
            }
            case (PropertiesInterface, "GetAll"):
            {
                var writer = context.CreateReplyWriter("a{sv}");
                var dict = writer.WriteDictionaryStart();
                foreach (var property in ItemProperties)
                {
                    writer.WriteDictionaryEntryStart();
                    writer.WriteString(property);
                    WriteItemProperty(ref writer, property);
                }

                writer.WriteDictionaryEnd(dict);
                context.Reply(writer.CreateMessage());
                writer.Dispose();
                break;
            }
            default:
                context.ReplyUnknownMethodError();
                break;
        }
    }

    private static readonly string[] ItemProperties =
        ["Category", "Id", "Title", "Status", "WindowId", "IconName", "IconThemePath", "Menu", "ItemIsMenu", "ToolTip"];

    /// <summary>Writes the property as a variant (its signature, then its value). False when there is no such property.</summary>
    private bool WriteItemProperty(ref MessageWriter writer, string property)
    {
        switch (property)
        {
            case "Category": writer.WriteVariantString("ApplicationStatus"); return true;
            case "Id": writer.WriteVariantString(LinuxApp.DesktopId); return true;
            case "Title": writer.WriteVariantString(LinuxApp.Name); return true;
            case "Status": writer.WriteVariantString("Active"); return true;
            case "WindowId": writer.WriteVariantInt32(0); return true;
            case "IconName": writer.WriteVariantString(LinuxApp.IconName); return true;
            case "IconThemePath": writer.WriteVariantString(string.Empty); return true;
            case "Menu":
                writer.WriteSignature("o");
                writer.WriteObjectPath(MenuPath);
                return true;
            case "ItemIsMenu": writer.WriteVariantBool(false); return true;
            case "ToolTip":
                // (icon name, icon pixmaps a(iiay), title, text)
                writer.WriteSignature("(sa(iiay)ss)");
                writer.WriteStructureStart();
                writer.WriteString(LinuxApp.IconName);
                var pixmaps = writer.WriteArrayStart(DBusType.Struct);
                writer.WriteArrayEnd(pixmaps);
                writer.WriteString(LinuxApp.Name);
                writer.WriteString(_tooltip.Length > 0 ? _tooltip : TooltipText());
                return true;
            default:
                return false;
        }
    }

    // ---- com.canonical.dbusmenu ----

    private uint _revision = 1;

    private void HandleMenu(MethodContext context)
    {
        var request = context.Request;
        if (context.IsDBusIntrospectRequest)
        {
            context.ReplyIntrospectXml([MenuIntrospection], Array.Empty<string>());
            return;
        }

        switch (request.InterfaceAsString, request.MemberAsString)
        {
            case (MenuInterface, "GetLayout"):
            {
                var writer = context.CreateReplyWriter("u(ia{sv}av)");
                writer.WriteUInt32(_revision);
                writer.WriteStructureStart();
                writer.WriteInt32(0);
                var rootProperties = writer.WriteDictionaryStart();
                writer.WriteDictionaryEntryStart();
                writer.WriteString("children-display");
                writer.WriteVariantString("submenu");
                writer.WriteDictionaryEnd(rootProperties);
                var children = writer.WriteArrayStart(DBusType.Variant);
                foreach (var item in Items)
                {
                    writer.WriteSignature("(ia{sv}av)");
                    writer.WriteStructureStart();
                    writer.WriteInt32(item.Id);
                    WriteItemProperties(ref writer, item);
                    var none = writer.WriteArrayStart(DBusType.Variant);
                    writer.WriteArrayEnd(none);
                }

                writer.WriteArrayEnd(children);
                context.Reply(writer.CreateMessage());
                writer.Dispose();
                break;
            }
            case (MenuInterface, "GetGroupProperties"):
            {
                var reader = request.GetBodyReader();
                var ids = new List<int>();
                var end = reader.ReadArrayStart(DBusType.Int32);
                while (reader.HasNext(end)) ids.Add(reader.ReadInt32());
                var writer = context.CreateReplyWriter("a(ia{sv})");
                var array = writer.WriteArrayStart(DBusType.Struct);
                foreach (var item in Items.Where(item => ids.Count == 0 || ids.Contains(item.Id)))
                {
                    writer.WriteStructureStart();
                    writer.WriteInt32(item.Id);
                    WriteItemProperties(ref writer, item);
                }

                writer.WriteArrayEnd(array);
                context.Reply(writer.CreateMessage());
                writer.Dispose();
                break;
            }
            case (MenuInterface, "Event"):
            {
                var reader = request.GetBodyReader();
                var id = reader.ReadInt32();
                var eventId = reader.ReadString();
                if (eventId == "clicked") OnClicked(id);
                ReplyEmpty(context);
                break;
            }
            case (MenuInterface, "EventGroup"):
            {
                var reader = request.GetBodyReader();
                var end = reader.ReadArrayStart(DBusType.Struct);
                while (reader.HasNext(end))
                {
                    reader.AlignStruct();
                    var id = reader.ReadInt32();
                    var eventId = reader.ReadString();
                    reader.ReadVariantValue();
                    reader.ReadUInt32();
                    if (eventId == "clicked") OnClicked(id);
                }

                using var writer = context.CreateReplyWriter("ai");
                writer.WriteArray(Array.Empty<int>());
                context.Reply(writer.CreateMessage());
                break;
            }
            case (MenuInterface, "AboutToShow"):
            {
                using var writer = context.CreateReplyWriter("b");
                writer.WriteBool(false);
                context.Reply(writer.CreateMessage());
                break;
            }
            case (MenuInterface, "AboutToShowGroup"):
            {
                using var writer = context.CreateReplyWriter("aiai");
                writer.WriteArray(Array.Empty<int>());
                writer.WriteArray(Array.Empty<int>());
                context.Reply(writer.CreateMessage());
                break;
            }
            case (PropertiesInterface, "Get"):
            {
                var reader = request.GetBodyReader();
                reader.ReadString();
                var property = reader.ReadString();
                var writer = context.CreateReplyWriter("v");
                if (!WriteMenuProperty(ref writer, property))
                {
                    writer.Dispose();
                    context.ReplyError("org.freedesktop.DBus.Error.UnknownProperty", $"No property {property}.");
                    return;
                }

                context.Reply(writer.CreateMessage());
                writer.Dispose();
                break;
            }
            case (PropertiesInterface, "GetAll"):
            {
                var writer = context.CreateReplyWriter("a{sv}");
                var dict = writer.WriteDictionaryStart();
                foreach (var property in new[] { "Version", "TextDirection", "Status", "IconThemePath" })
                {
                    writer.WriteDictionaryEntryStart();
                    writer.WriteString(property);
                    WriteMenuProperty(ref writer, property);
                }

                writer.WriteDictionaryEnd(dict);
                context.Reply(writer.CreateMessage());
                writer.Dispose();
                break;
            }
            default:
                context.ReplyUnknownMethodError();
                break;
        }
    }

    private static bool WriteMenuProperty(ref MessageWriter writer, string property)
    {
        switch (property)
        {
            case "Version": writer.WriteVariantUInt32(3); return true;
            case "TextDirection": writer.WriteVariantString("ltr"); return true;
            case "Status": writer.WriteVariantString("normal"); return true;
            case "IconThemePath":
                writer.WriteSignature("as");
                writer.WriteArray(Array.Empty<string>());
                return true;
            default:
                return false;
        }
    }

    private static void WriteItemProperties(ref MessageWriter writer, MenuItem item)
    {
        var dict = writer.WriteDictionaryStart();
        if (item.IsSeparator)
        {
            writer.WriteDictionaryEntryStart();
            writer.WriteString("type");
            writer.WriteVariantString("separator");
        }
        else
        {
            writer.WriteDictionaryEntryStart();
            writer.WriteString("label");
            writer.WriteVariantString(item.Label!);
            if (item.Icon is { } icon)
            {
                writer.WriteDictionaryEntryStart();
                writer.WriteString("icon-name");
                writer.WriteVariantString(icon);
            }
        }

        writer.WriteDictionaryEnd(dict);
    }

    private void OnClicked(int id)
    {
        switch (id)
        {
            case 1: Run(_actions.Open); break;
            case 2: Run(_actions.OpenSettings); break;
            case 4: Run(_actions.PauseAll); break;
            case 5: Run(_actions.ResumeAll); break;
            case 7: Run(_actions.Exit); break;
        }
    }

    private static void ReplyEmpty(MethodContext context)
    {
        if (context.NoReplyExpected) return;
        using var writer = context.CreateReplyWriter(null);
        context.Reply(writer.CreateMessage());
    }

    private static readonly ReadOnlyMemory<byte> ItemIntrospection = Encoding.UTF8.GetBytes("""
        <interface name="org.kde.StatusNotifierItem">
          <method name="Activate"><arg name="x" type="i" direction="in"/><arg name="y" type="i" direction="in"/></method>
          <method name="SecondaryActivate"><arg name="x" type="i" direction="in"/><arg name="y" type="i" direction="in"/></method>
          <method name="ContextMenu"><arg name="x" type="i" direction="in"/><arg name="y" type="i" direction="in"/></method>
          <method name="Scroll"><arg name="delta" type="i" direction="in"/><arg name="orientation" type="s" direction="in"/></method>
          <signal name="NewToolTip"/>
          <property name="Category" type="s" access="read"/>
          <property name="Id" type="s" access="read"/>
          <property name="Title" type="s" access="read"/>
          <property name="Status" type="s" access="read"/>
          <property name="WindowId" type="i" access="read"/>
          <property name="IconName" type="s" access="read"/>
          <property name="IconThemePath" type="s" access="read"/>
          <property name="Menu" type="o" access="read"/>
          <property name="ItemIsMenu" type="b" access="read"/>
          <property name="ToolTip" type="(sa(iiay)ss)" access="read"/>
        </interface>
        """);

    private static readonly ReadOnlyMemory<byte> MenuIntrospection = Encoding.UTF8.GetBytes("""
        <interface name="com.canonical.dbusmenu">
          <method name="GetLayout"><arg type="i" direction="in"/><arg type="i" direction="in"/><arg type="as" direction="in"/><arg type="u" direction="out"/><arg type="(ia{sv}av)" direction="out"/></method>
          <method name="GetGroupProperties"><arg type="ai" direction="in"/><arg type="as" direction="in"/><arg type="a(ia{sv})" direction="out"/></method>
          <method name="Event"><arg type="i" direction="in"/><arg type="s" direction="in"/><arg type="v" direction="in"/><arg type="u" direction="in"/></method>
          <method name="EventGroup"><arg type="a(isvu)" direction="in"/><arg type="ai" direction="out"/></method>
          <method name="AboutToShow"><arg type="i" direction="in"/><arg type="b" direction="out"/></method>
          <method name="AboutToShowGroup"><arg type="ai" direction="in"/><arg type="ai" direction="out"/><arg type="ai" direction="out"/></method>
          <signal name="LayoutUpdated"><arg type="u"/><arg type="i"/></signal>
          <property name="Version" type="u" access="read"/>
          <property name="TextDirection" type="s" access="read"/>
          <property name="Status" type="s" access="read"/>
          <property name="IconThemePath" type="as" access="read"/>
        </interface>
        """);

    public async ValueTask DisposeAsync()
    {
        await _tooltipTimer.DisposeAsync().ConfigureAwait(false);
        _watcherObserver?.Dispose();
        Interlocked.Exchange(ref _connection, null)?.Dispose();
    }

    private sealed class Handler(string path, Action<MethodContext> handle) : IPathMethodHandler
    {
        public string Path => path;

        public bool HandlesChildPaths => false;

        public ValueTask HandleMethodAsync(MethodContext context)
        {
            handle(context);
            return ValueTask.CompletedTask;
        }
    }
}
