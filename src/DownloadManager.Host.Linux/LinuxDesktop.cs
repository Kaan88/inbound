using System.Diagnostics;
using System.Text;
using DownloadManager.Core;
using Microsoft.Win32.SafeHandles;
using Tmds.DBus.Protocol;

namespace DownloadManager.Host;

/// <summary>
/// The desktop's notifications (<c>org.freedesktop.Notifications</c> on the session bus): Plasma, GNOME and the others
/// all answer it. The Windows Host's tray balloons, here. Nothing is shown when the session has no notification service.
/// </summary>
internal sealed class DesktopNotifications(Action<string> warn) : IDisposable
{
    private const string Service = "org.freedesktop.Notifications";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DBusConnection? _connection;
    private bool _warned;

    /// <summary>Shows a notice; never throws, and never waits for the desktop.</summary>
    public void Notify(string title, string message) => _ = NotifyAsync(title, message);

    private async Task NotifyAsync(string title, string message)
    {
        try
        {
            var connection = await ConnectAsync().ConfigureAwait(false);
            await connection.CallMethodAsync(BuildNotify(connection, title, message)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DBusErrorReplyException or DBusConnectionException or DBusReadException or IOException or InvalidOperationException)
        {
            if (!_warned) warn($"Desktop notifications are unavailable: {ex.GetType().Name}");
            _warned = true;
            Reset();
        }
    }

    private static MessageBuffer BuildNotify(DBusConnection connection, string title, string message)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(Service, "/org/freedesktop/Notifications", Service, "Notify", "susssasa{sv}i");
        writer.WriteString(LinuxApp.Name);
        writer.WriteUInt32(0);
        writer.WriteString(LinuxApp.IconName);
        writer.WriteString(title);
        // The body may hold markup on some desktops; a file name is shown as it is.
        writer.WriteString(EscapeMarkup(message));
        writer.WriteArray(Array.Empty<string>());
        writer.WriteDictionary(new Dictionary<string, VariantValue> { ["desktop-entry"] = VariantValue.String(LinuxApp.DesktopId) });
        writer.WriteInt32(-1);
        return writer.CreateMessage();
    }

    private async Task<DBusConnection> ConnectAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_connection is { } existing) return existing;
            var connection = new DBusConnection(DBusAddress.Session ?? throw new InvalidOperationException("No session bus."));
            await connection.ConnectAsync().ConfigureAwait(false);
            _connection = connection;
            return connection;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Reset() => Interlocked.Exchange(ref _connection, null)?.Dispose();

    internal static string EscapeMarkup(string text) =>
        text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);

    public void Dispose()
    {
        Reset();
        _gate.Dispose();
    }
}

/// <summary>
/// Keeps the machine awake while downloads run, as the Windows Host's power request does: a logind sleep inhibitor
/// (<c>org.freedesktop.login1.Manager.Inhibit</c>, mode <c>block</c>), held as the file descriptor it returns and
/// released by closing it. Idle sleep and a sleep from the menu both wait; the screen may still lock and blank.
/// </summary>
internal sealed class LogindSleepPrevention(Action<string> log) : ISleepPrevention
{
    private readonly object _gate = new();
    private SafeFileHandle? _inhibitor;
    private bool _wanted;
    private int _generation;
    private bool _warned;

    public void SetActive(bool active)
    {
        int generation;
        lock (_gate)
        {
            if (_wanted == active) return;
            _wanted = active;
            generation = ++_generation;
            if (!active)
            {
                _inhibitor?.Dispose();
                _inhibitor = null;
                log("Sleep prevention released.");
                return;
            }
        }

        _ = AcquireAsync(generation);
    }

    private async Task AcquireAsync(int generation)
    {
        try
        {
            using var connection = new DBusConnection(DBusAddress.System ?? throw new InvalidOperationException("No system bus."));
            await connection.ConnectAsync().ConfigureAwait(false);
            var handle = await connection.CallMethodAsync(BuildInhibit(connection),
                static (message, _) => message.GetBodyReader().ReadHandle<SafeFileHandle>()!, null).ConfigureAwait(false);
            lock (_gate)
            {
                // Released, or wanted again, while this was on its way: keep only the newest.
                if (generation != _generation || !_wanted)
                {
                    handle.Dispose();
                    return;
                }

                _inhibitor?.Dispose();
                _inhibitor = handle;
            }

            log("Sleep prevention held.");
        }
        catch (Exception ex) when (ex is DBusErrorReplyException or DBusConnectionException or DBusReadException or IOException or InvalidOperationException)
        {
            if (!_warned) log($"Sleep prevention is unavailable: {ex.GetType().Name}");
            _warned = true;
        }
    }

    private static MessageBuffer BuildInhibit(DBusConnection connection)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader("org.freedesktop.login1", "/org/freedesktop/login1", "org.freedesktop.login1.Manager", "Inhibit", "ssss");
        writer.WriteString("sleep");
        writer.WriteString(LinuxApp.Name);
        writer.WriteString("Downloads are running");
        writer.WriteString("block");
        return writer.CreateMessage();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _wanted = false;
            _generation++;
            _inhibitor?.Dispose();
            _inhibitor = null;
        }
    }
}

/// <summary>
/// Start at sign-in: an XDG autostart entry (<c>~/.config/autostart</c>), which KDE, GNOME and the others read. It
/// starts the AppImage when the Host runs from one, so the entry survives the AppImage's mount point changing.
/// </summary>
internal static class AutostartEntry
{
    public static string PathOf()
    {
        var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(config) || !Path.IsPathRooted(config))
            config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return Path.Combine(config, "autostart", LinuxApp.DesktopId + "-host.desktop");
    }

    public static void Apply(bool enabled)
    {
        var path = PathOf();
        if (!enabled)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }

        var program = LinuxApp.LaunchPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var text = new StringBuilder()
            .Append("[Desktop Entry]\n")
            .Append("Type=Application\n")
            .Append("Name=").Append(LinuxApp.Name).Append('\n')
            .Append("Comment=Starts Inbound's background service at sign-in\n")
            .Append("Exec=").Append(QuoteExec(program)).Append(" --background\n")
            .Append("Icon=").Append(LinuxApp.IconName).Append('\n')
            .Append("Terminal=false\n")
            .Append("NoDisplay=true\n")
            .Append("X-GNOME-Autostart-enabled=true\n")
            .ToString();
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, text);
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>The desktop entry spec's quoting for one Exec argument.</summary>
    internal static string QuoteExec(string argument)
    {
        var escaped = new StringBuilder();
        foreach (var c in argument)
        {
            if (c is '"' or '`' or '$' or '\\') escaped.Append('\\');
            escaped.Append(c);
        }

        // In a desktop file the backslash itself is escaped once more.
        return "\"" + escaped.ToString().Replace("\\", "\\\\", StringComparison.Ordinal) + "\"";
    }
}

/// <summary>
/// A finished download's or schedule's actions on this machine: open the file with its default program, exit the Host,
/// or end the session. Power off and restart go through systemd (the active session may do that without a password);
/// as on Windows, they wait 30 seconds first.
/// </summary>
internal sealed class LinuxCompletionActionExecutor(Action<string> log) : ICompletionActionExecutor
{
    public static readonly TimeSpan ShutdownDelay = TimeSpan.FromSeconds(30);

    public Action? ExitHost { get; set; }

    /// <summary>Shown before a power-off, restart or sign-out, as Windows' shutdown.exe warns.</summary>
    public Action<string, string>? Notice { get; set; }

    public Task OpenFileAsync(string path, CancellationToken cancellationToken)
    {
        LinuxApp.Open(path);
        return Task.CompletedTask;
    }

    public Task RequestExitAsync(CancellationToken cancellationToken)
    {
        var exit = ExitHost ?? throw new InvalidOperationException("Host exit handler is not ready.");
        log("Completion action: exiting the host.");
        exit();
        return Task.CompletedTask;
    }

    public async Task RequestShutdownAsync(string mode, bool force, CancellationToken cancellationToken)
    {
        var (program, arguments, what) = mode?.Trim().ToLowerInvariant() switch
        {
            "restart" => ("systemctl", new List<string> { "reboot" }, "restart"),
            "logoff" => ("loginctl", ["terminate-session", Environment.GetEnvironmentVariable("XDG_SESSION_ID") ?? string.Empty], "sign out"),
            _ => ("systemctl", new List<string> { "poweroff" }, "shut down"),
        };
        // force: systemctl has no "close programs without asking"; logind's own inhibitors are ignored with -i.
        if (force && program == "systemctl") arguments.Add("--check-inhibitors=no");
        log($"Completion action: {what} in {ShutdownDelay.TotalSeconds:0} s ({program} {string.Join(' ', arguments)}).");
        Notice?.Invoke("Downloads finished", $"This computer will {what} in {ShutdownDelay.TotalSeconds:0} seconds.");
        await Task.Delay(ShutdownDelay, cancellationToken).ConfigureAwait(false);
        var start = new ProcessStartInfo(program) { UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var _ = Process.Start(start);
    }
}

/// <summary>Names and paths the Linux build shares.</summary>
internal static class LinuxApp
{
    public const string Name = "Inbound Download Manager";

    /// <summary>The desktop file's id (<c>inbound.desktop</c>) the window installs; notifications name it.</summary>
    public const string DesktopId = "inbound";

    /// <summary>A themed icon every desktop has, until the AppImage installs the app's own.</summary>
    public const string IconName = "folder-download";

    /// <summary>The AppImage when the Host runs from one (its mount point changes every run), else this program.</summary>
    public static string LaunchPath()
    {
        var appImage = Environment.GetEnvironmentVariable("APPIMAGE");
        if (!string.IsNullOrWhiteSpace(appImage) && File.Exists(appImage)) return appImage;
        return Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "inbound-host");
    }

    /// <summary>A file or folder with the desktop's default program (<c>xdg-open</c>).</summary>
    public static void Open(string path)
    {
        var start = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
        start.ArgumentList.Add(path);
        using var _ = Process.Start(start);
    }
}
