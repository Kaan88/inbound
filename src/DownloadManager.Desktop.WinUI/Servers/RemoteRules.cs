using DownloadManager.Contracts;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// Which Host this window process shows (design D9, section 5). Set by the window when it switches; read by the parts
/// that would otherwise touch this PC's files on behalf of a download that lives on another machine.
/// </summary>
internal static class ShownServer
{
    private static volatile ServerTarget _target = ServerTarget.Local;

    public static ServerTarget Target
    {
        get => _target;
        set => _target = value ?? ServerTarget.Local;
    }

    public static bool IsRemote => !_target.IsLocal;
}

/// <summary>
/// What the window may do with data from a Host, by where that Host is. A path from another server names a file on
/// that machine: this PC never checks it, opens it, reveals it, reads its icon or plays it. Nothing here touches the UI.
/// </summary>
internal static class RemoteDataRules
{
    /// <summary>The reason Open, Open With, Open Folder and the like are off for a download on another server.</summary>
    public const string OnAnotherServer = "on another server";

    /// <summary>File.Exists, Directory.Exists, shell open, reveal, Open With, Open Folder, the Diagnostics folder.</summary>
    public static bool MayUseFiles(bool remote) => !remote;

    /// <summary>An icon read from the file itself; a remote download gets its type's icon only.</summary>
    public static string? IconPath(bool remote, string? completedPath) => remote ? null : completedPath;

    /// <summary>A sound file named in settings is a path on the Host's machine.</summary>
    public static bool MayPlaySoundFiles(bool remote) => !remote;

    /// <summary>
    /// Only this PC's Host quitting quits the app. Another server's <c>host.exiting</c> means it went away, which the
    /// window shows as disconnected.
    /// </summary>
    public static bool HostExitQuitsApp(bool remote) => !remote;

    /// <summary>
    /// The completion window and notification. This PC's Host opens them itself; for another server the window opens
    /// them from its own job events while it is open (nothing alerts while it is closed). A CLI add on this PC is the
    /// one case the window opens itself, as before.
    /// </summary>
    public static bool WindowOpensCompletion(bool remote, string? source, bool? jobShowsDialog, bool settingShowsDialog) =>
        (remote || source == "cli") && (jobShowsDialog ?? settingShowsDialog);

    /// <summary>A folder is chosen with this PC's folder dialog, or with the server's folder browser.</summary>
    public static bool UsesServerFolderBrowser(bool remote) => remote;

    /// <summary>Why a file on this PC can't be used as-is for another server, or null when it can be sent.</summary>
    public static string? LocalFileProblem(bool remote, string kind, string serverName) => !remote
        ? null
        : kind switch
        {
            "torrent" => null, // uploaded
            "container" => $"A container file can't be sent to {serverName} from this PC. Open it in this PC's view instead, or in the server's web UI.",
            "archive" => null, // chosen from the server's folders
            _ => $"That file is on this PC, not on {serverName}.",
        };
}

/// <summary>
/// Options when the window shows another server (design D9, section 5): this PC's own choices always go to this PC's
/// Host, and the server's pages show only what its hello allows. Nothing here touches the UI.
/// </summary>
internal static class RemoteSettingsRules
{
    /// <summary>The settings that are this PC's, whichever server the window shows; saved to the local Host only.</summary>
    public static readonly string[] ThisPcFields =
    [
        nameof(SettingsDto.Theme),
        nameof(SettingsDto.LaunchAtStartup),
        nameof(SettingsDto.CloseWindowCompletely),
        nameof(SettingsDto.ClipboardMonitoring),
        nameof(SettingsDto.BrowserIntegrationEnabled),
        nameof(SettingsDto.AutoCheckUpdates),
        nameof(SettingsDto.UpdateCheckIntervalHours),
        nameof(SettingsDto.EventSoundFiles),
        nameof(SettingsDto.PlayCompletionSound),
        nameof(SettingsDto.NotifyOnCompletion),
        nameof(SettingsDto.ShowCompletionDialog),
    ];

    /// <summary>A server's pages, and the fields on them, by what its hello says.</summary>
    public sealed record ServerPages(
        bool Folders,
        bool Connection,
        bool Proxy,
        bool SiteLogins,
        bool ProgramSettings,
        bool AutoResume,
        bool Categories);

    /// <summary>
    /// The Docker server (<c>hostKind: server</c>) lets a paired app change what a signed-in browser may: everything but
    /// programs it would run (<c>programSettings</c>). The Windows app's web bridge (<c>windows</c>) allows its short list
    /// only (queues, schedules, connections, speed, categories' names), so folders, proxy and logins stay off there.
    /// A Host that doesn't say what it is gets the short list too.
    /// </summary>
    public static ServerPages PagesFor(IReadOnlyCollection<string>? capabilities, string? hostKind)
    {
        var caps = capabilities ?? [];
        var server = hostKind == HostKinds.Server;
        return new ServerPages(
            Folders: server,
            Connection: true,
            Proxy: server,
            SiteLogins: server,
            ProgramSettings: caps.Contains("programSettings"),
            AutoResume: server,
            Categories: true);
    }

    /// <summary>
    /// Puts the server's own values of <see cref="ThisPcFields"/> back into what is about to be sent to it, so a save
    /// on the server's Options never changes them there: those choices belong to each PC. Returns <paramref name="edited"/>.
    /// </summary>
    public static SettingsDto KeepServerValues(SettingsDto edited, SettingsDto serverAtOpen)
    {
        edited.Theme = serverAtOpen.Theme;
        edited.LaunchAtStartup = serverAtOpen.LaunchAtStartup;
        edited.CloseWindowCompletely = serverAtOpen.CloseWindowCompletely;
        edited.ClipboardMonitoring = serverAtOpen.ClipboardMonitoring;
        edited.BrowserIntegrationEnabled = serverAtOpen.BrowserIntegrationEnabled;
        edited.AutoCheckUpdates = serverAtOpen.AutoCheckUpdates;
        edited.UpdateCheckIntervalHours = serverAtOpen.UpdateCheckIntervalHours;
        edited.EventSoundFiles = serverAtOpen.EventSoundFiles;
        edited.PlayCompletionSound = serverAtOpen.PlayCompletionSound;
        edited.NotifyOnCompletion = serverAtOpen.NotifyOnCompletion;
        edited.ShowCompletionDialog = serverAtOpen.ShowCompletionDialog;
        return edited;
    }
}

/// <summary>Words for a connection to another server that failed. Never an exception's own text, which could carry an address's secrets.</summary>
internal static class RemoteConnectionText
{
    public static string Failure(ServerTarget target, Exception ex) => ex switch
    {
        RemoteServerException => ex.Message,
        InvalidDataException => $"{target.Name} didn't answer like an Inbound server. Trying again shortly.",
        OperationCanceledException or TimeoutException => $"{target.Name} didn't answer in time. Trying again shortly.",
        _ => $"Couldn't reach {target.Name}. Trying again shortly.",
    };
}

/// <summary>The command line and single-instance names of the progress and package windows (<c>--server</c>).</summary>
internal static class WindowInstance
{
    /// <summary>The <c>--server</c> id: 32 hex digits, or null for this PC (missing or not one).</summary>
    public static string? ServerId(IReadOnlyList<string> args)
    {
        var index = -1;
        for (var i = 0; i < args.Count; i++)
            if (string.Equals(args[i], "--server", StringComparison.OrdinalIgnoreCase)) index = i;
        if (index < 0 || index + 1 >= args.Count) return null;
        var id = args[index + 1];
        return id.Length == 32 && id.All(Uri.IsHexDigit) ? id.ToLowerInvariant() : null;
    }

    /// <summary>The arguments that open a download's or a package's window for this server.</summary>
    public static string Arguments(string kind, Guid id, ServerTarget target, bool manual = false) =>
        $"--{kind} {id:D}" + (manual ? " --manual" : string.Empty) + (target.IsLocal ? string.Empty : $" --server {target.Id}");

    /// <summary>
    /// The window's activation event: the same download's window for another server is another window, so the server's
    /// id is part of the name; this PC's names are as before.
    /// </summary>
    public static string EventName(string userKey, bool update, Guid? job, Guid? package, string? serverId)
    {
        var name = $"Local\\DownloadManager.UI.Show.{userKey}" +
            (update ? ".Update" : job is { } id ? $".Job.{id:D}" : package is { } pid ? $".Package.{pid:D}" : string.Empty);
        return serverId is null || update || (job is null && package is null) ? name : name + ".Server." + serverId;
    }
}
