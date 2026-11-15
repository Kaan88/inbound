using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace DownloadManager.Host;

/// <summary>What the Mac's menu-bar helper can ask of the Host besides the tray's own actions (MAC.6).</summary>
internal sealed class MacMenuRequests
{
    public Action<string>? Copied { get; set; }
    public Action<string>? AddText { get; set; }
    public Action<string>? AddFile { get; set; }
    public Action? OpenFolder { get; set; }
    public Action<string, Guid>? Job { get; set; }
    public Action? Wake { get; set; }
    public Action<string>? Network { get; set; }
    public Action<string>? Power { get; set; }
}

/// <summary>
/// The menu-bar icon on macOS, the tray's counterpart: a small native helper (native/macos-menu, built as
/// Inbound.app/Contents/Helpers/Inbound Menu.app) shows the icon and its menu. The Host is its only peer, over the
/// helper's stdin and stdout, one line per message (the protocol is described in main.swift). Optional, as the tray is
/// on Linux: without the helper the Host runs as before. Disposing it closes the helper's stdin, which ends it.
/// </summary>
internal sealed class MacMenuBar : IAsyncDisposable
{
    public const string HelperName = "inbound-menu";
    public const string HelperApp = "Inbound Menu.app";

    private readonly LinuxTrayActions _actions;
    private readonly Action<string> _log;
    private readonly Process _process;
    private readonly Timer _tooltipTimer;
    private readonly Task _reader;
    private readonly Lock _write = new();
    private string _tooltip = string.Empty;
    private string _status = string.Empty;
    private int _tooltipPending;
    private bool? _watching;
    private volatile bool _watchOn;

    private MacMenuBar(Process process, LinuxTrayActions actions, Action<string> log)
    {
        _process = process;
        _actions = actions;
        _log = log;
        _tooltipTimer = new Timer(_ => SendTooltip(), null, Timeout.Infinite, Timeout.Infinite);
        _reader = Task.Run(ReadChoicesAsync);
        SendTooltip();
    }

    public MacMenuRequests Requests { get; } = new();

    /// <summary>The menu's activity: the active count, total speed and the newest active downloads, as JSON.</summary>
    public Func<string>? Status { get; set; }

    /// <summary>Whether the helper may show notifications (the user allowed them); until it says, no.</summary>
    public bool NotificationsOn { get; private set; }

    /// <summary>The helper couldn't use the notification center at all (an unsigned or moved helper): use another way.</summary>
    public bool NotificationsBroken { get; private set; }

    /// <summary>The helper's program: in its own bundle under Contents/Helpers, else beside the Host (older layout).</summary>
    public static string? FindHelper(string baseDirectory)
    {
        var contents = Path.GetFullPath(Path.Combine(baseDirectory, "..", ".."));
        var bundled = Path.Combine(contents, "Helpers", HelperApp, "Contents", "MacOS", HelperName);
        if (File.Exists(bundled)) return bundled;
        var beside = Path.Combine(baseDirectory, HelperName);
        return File.Exists(beside) ? beside : null;
    }

    public static MacMenuBar? TryStart(LinuxTrayActions actions, Action<string> log)
    {
        if (FindHelper(AppContext.BaseDirectory) is not { } helper)
        {
            log($"No menu-bar icon: {HelperName} is not in the bundle.");
            return null;
        }

        RegisterServices(helper, log);
        try
        {
            var start = new ProcessStartInfo(helper)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = false,
            };
            start.ArgumentList.Add("--host");
            var process = Process.Start(start);
            if (process is null) return null;
            log("Menu-bar icon started.");
            return new MacMenuBar(process, actions, log);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            log($"No menu-bar icon: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Services > Download with Inbound comes from the helper's Info.plist, which macOS reads once LaunchServices knows
    /// the bundle. A nested bundle started as a program isn't registered by itself, so it is here (quick, no prompt).
    /// </summary>
    private static void RegisterServices(string helper, Action<string> log)
    {
        const string lsregister = "/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister";
        var bundle = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(helper)!, "..", ".."));
        if (!bundle.EndsWith(".app", StringComparison.Ordinal) || !File.Exists(lsregister)) return;
        try
        {
            var start = new ProcessStartInfo(lsregister) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("-f");
            start.ArgumentList.Add(bundle);
            using var process = Process.Start(start);
            process?.WaitForExit(10_000);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            log($"The Services entry was not registered: {ex.GetType().Name}");
        }
    }

    /// <summary>The tooltip and menu follow the downloads; sent at most once a second, and only when they changed.</summary>
    public void RequestRefresh()
    {
        if (Interlocked.Exchange(ref _tooltipPending, 1) == 0) _tooltipTimer.Change(1000, Timeout.Infinite);
    }

    /// <summary>MAC.5: the helper watches the clipboard only while Options asks for it.</summary>
    public void WatchClipboard(bool on)
    {
        lock (_write)
        {
            if (_watching == on) return;
            _watching = on;
            _watchOn = on;
            Send(on ? "clipboard on" : "clipboard off");
        }
    }

    /// <summary>MAC.6: a notification as Inbound's own, with Open and Show in Finder (finished) or Retry (failed).</summary>
    public void Notify(string title, string body, Guid? job, string? kind)
    {
        var json = JsonSerializer.Serialize(new NoticeLine(title, body, job?.ToString("D"), kind), MacMenuJson.Default.NoticeLine);
        lock (_write) Send("notify " + Convert.ToBase64String(Encoding.UTF8.GetBytes(json)));
    }

    private void Send(string line)
    {
        try
        {
            _process.StandardInput.WriteLine(line);
            _process.StandardInput.Flush();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The helper has gone; the Host runs on without its icon.
        }
    }

    private async Task ReadChoicesAsync()
    {
        try
        {
            while (await _process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } raw)
            {
                var line = raw.Trim();
                try
                {
                    Handle(line);
                }
                catch (Exception ex)
                {
                    // Never the line itself: it may carry what the user copied or dropped.
                    _log($"Menu-bar request '{line.Split(' ')[0]}' failed: {ex.GetType().Name}");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The helper has gone.
        }
    }

    private void Handle(string line)
    {
        var space = line.IndexOf(' ');
        var verb = space < 0 ? line : line[..space];
        var rest = space < 0 ? string.Empty : line[(space + 1)..];
        var requests = Requests;
        switch (verb)
        {
            case "open": _actions.Open(); break;
            case "settings": _actions.OpenSettings(); break;
            case "pause": _actions.PauseAll(); break;
            case "resume": _actions.ResumeAll(); break;
            case "exit": _actions.Exit(); break;
            case "folder": requests.OpenFolder?.Invoke(); break;
            case "clipboard" when _watchOn: requests.Copied?.Invoke(Decode(rest)); break;
            case "add": requests.AddText?.Invoke(Decode(rest)); break;
            case "addfile": requests.AddFile?.Invoke(Decode(rest)); break;
            case "wake" or "clock": requests.Wake?.Invoke(); break;
            case "network": requests.Network?.Invoke(rest); break;
            case "power": requests.Power?.Invoke(rest); break;
            case "notifications":
                NotificationsOn = rest == "on";
                NotificationsBroken = rest == "error";
                _log($"Notifications through the menu-bar helper: {rest}.");
                break;
            case "job":
                var parts = rest.Split(' ', 2);
                if (parts.Length == 2 && Guid.TryParse(parts[1], out var id)) requests.Job?.Invoke(parts[0], id);
                break;
        }
    }

    private static string Decode(string base64) => Encoding.UTF8.GetString(Convert.FromBase64String(base64));

    private void SendTooltip()
    {
        Interlocked.Exchange(ref _tooltipPending, 0);
        var active = _actions.ActiveCount();
        var text = active switch
        {
            0 => "No downloads running",
            1 => "1 download running",
            _ => $"{active} downloads running",
        };
        string? status = null;
        try
        {
            status = Status?.Invoke();
        }
        catch (Exception ex)
        {
            _log($"Menu-bar status failed: {ex.GetType().Name}");
        }

        lock (_write)
        {
            if (text != _tooltip)
            {
                _tooltip = text;
                Send("tooltip " + text);
            }

            if (status is not null && status != _status)
            {
                _status = status;
                Send("status " + Convert.ToBase64String(Encoding.UTF8.GetBytes(status)));
            }
        }

        // Speeds change while the count stays: keep the menu current while anything runs.
        if (active > 0) RequestRefresh();
    }

    public async ValueTask DisposeAsync()
    {
        await _tooltipTimer.DisposeAsync().ConfigureAwait(false);
        try
        {
            lock (_write) _process.StandardInput.Close(); // the helper ends when its input does
            if (!_process.WaitForExit(TimeSpan.FromSeconds(2))) _process.Kill();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }

        try { await _reader.ConfigureAwait(false); }
        catch (Exception) { /* the reader only ends */ }
        _process.Dispose();
    }

    /// <summary>The menu's status line: what <see cref="Status"/> returns.</summary>
    public static string StatusJson(IEnumerable<DownloadManager.Contracts.JobDto> jobs)
    {
        var active = jobs.Where(job => job.State is "connecting" or "downloading" or "retryWaiting" or "verifying").ToList();
        var speed = active.Sum(job => job.BytesPerSecond ?? 0);
        var items = active.OrderByDescending(job => job.BytesPerSecond ?? 0).Take(5)
            .Select(job => new StatusItem(job.Id.ToString("D"), job.FileName,
                job.Progress is { } progress ? (int)Math.Clamp(Math.Floor(progress * 100), 0, 100) : null, job.State))
            .ToList();
        return JsonSerializer.Serialize(new StatusLine(active.Count, speed > 0 ? Speed(speed) : null, items), MacMenuJson.Default.StatusLine);
    }

    /// <summary>The http and https addresses in dropped or sent text, one per word, at most 100.</summary>
    public static IReadOnlyList<string> WebLinks(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(word => word.Trim('<', '>', '"', '\'', '(', ')', ','))
            .Where(word => Uri.TryCreate(word, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.Host.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Take(100)
            .ToList();

    /// <summary>A link added from the menu bar wasn't copied: its notice says so without "Clipboard".</summary>
    public static string AddedTitle(string title) => title switch
    {
        "Clipboard download added" => "Download added",
        "Copied link not added" => "Link not added",
        "Copied link not started" => "Link not started",
        "Copied link is waiting" => "Link is waiting",
        _ => title,
    };

    public static string Speed(double bytesPerSecond) => bytesPerSecond switch
    {
        >= 1024 * 1024 * 1024 => $"{bytesPerSecond / (1024 * 1024 * 1024):0.0} GB/s",
        >= 1024 * 1024 => $"{bytesPerSecond / (1024 * 1024):0.0} MB/s",
        >= 1024 => $"{bytesPerSecond / 1024:0} KB/s",
        _ => $"{bytesPerSecond:0} B/s",
    };
}

internal sealed record NoticeLine(
    [property: System.Text.Json.Serialization.JsonPropertyName("title")] string Title,
    [property: System.Text.Json.Serialization.JsonPropertyName("body")] string Body,
    [property: System.Text.Json.Serialization.JsonPropertyName("job")] string? Job,
    [property: System.Text.Json.Serialization.JsonPropertyName("kind")] string? Kind);

internal sealed record StatusItem(
    [property: System.Text.Json.Serialization.JsonPropertyName("id")] string Id,
    [property: System.Text.Json.Serialization.JsonPropertyName("name")] string Name,
    [property: System.Text.Json.Serialization.JsonPropertyName("percent")] int? Percent,
    [property: System.Text.Json.Serialization.JsonPropertyName("state")] string State);

internal sealed record StatusLine(
    [property: System.Text.Json.Serialization.JsonPropertyName("active")] int Active,
    [property: System.Text.Json.Serialization.JsonPropertyName("speed")] string? Speed,
    [property: System.Text.Json.Serialization.JsonPropertyName("items")] IReadOnlyList<StatusItem> Items);

[System.Text.Json.Serialization.JsonSerializable(typeof(NoticeLine))]
[System.Text.Json.Serialization.JsonSerializable(typeof(StatusLine))]
internal sealed partial class MacMenuJson : System.Text.Json.Serialization.JsonSerializerContext;
