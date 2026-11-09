using DownloadManager.Contracts;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// Which Host this window process shows (design D9, section 5). Set by the window when it switches; read by the parts
/// that would otherwise touch this PC's files on behalf of a download that lives on another machine.
/// </summary>
internal static class ShownServer
{
    private static volatile ServerTarget _target = ServerTarget.Local;
    private static volatile string? _hostKind;
    private static volatile string? _defaultFolder;

    /// <summary>Showing another Host forgets what the one before said about its paths.</summary>
    public static ServerTarget Target
    {
        get => _target;
        set
        {
            _target = value ?? ServerTarget.Local;
            _hostKind = null;
            _defaultFolder = null;
        }
    }

    public static bool IsRemote => !_target.IsLocal;

    /// <summary>
    /// What the shown server said that tells its paths apart: its <c>hostKind</c> (saved when it was paired, then from
    /// its hello) and its default download folder (from its settings). A null or empty value keeps what was known.
    /// </summary>
    public static void Describe(string? hostKind, string? defaultFolder)
    {
        if (!string.IsNullOrWhiteSpace(hostKind)) _hostKind = hostKind;
        if (!string.IsNullOrWhiteSpace(defaultFolder)) _defaultFolder = defaultFolder;
    }

    /// <summary>How a full folder path is written on the Host the window shows: this PC's rules for this PC.</summary>
    public static PathStyle PathStyle => IsRemote ? ServerPaths.StyleFor(_hostKind, _defaultFolder) : PathStyle.ThisPc;
}

/// <summary>How the Host a folder is on writes a full path (<see cref="ServerPaths.IsFull(string, PathStyle)"/>).</summary>
internal enum PathStyle
{
    /// <summary>This PC's Host: Windows' own rules, as <see cref="Path.IsPathFullyQualified(string)"/> has them.</summary>
    ThisPc,

    /// <summary>A server whose folders are <c>/srv/downloads</c>.</summary>
    Linux,

    /// <summary>A server whose folders are <c>D:\Downloads</c>.</summary>
    Windows,

    /// <summary>A server that hasn't said which: either kind is taken.</summary>
    Either,
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

    /// <summary>
    /// Only this PC's Host quitting quits the app. Another server's <c>host.exiting</c> means it went away, which the
    /// window shows as disconnected.
    /// </summary>
    public static bool HostExitQuitsApp(bool remote) => !remote;

    /// <summary>
    /// The completion window. This PC's Host opens it itself; for another server the window opens it from its own job
    /// events while it is open (nothing alerts while it is closed), and whether it does is this PC's setting alone: a
    /// server's per-download choice is that server's, not this PC's. A CLI add on this PC is the one case the window
    /// opens it for this PC's Host, with the download's own choice first, as before. For another server
    /// <see cref="RemoteCompletionGate"/> decides as well.
    /// </summary>
    public static bool WindowOpensCompletion(bool remote, string? source, bool? jobShowsDialog, bool settingShowsDialog) =>
        remote ? settingShowsDialog : source == "cli" && (jobShowsDialog ?? settingShowsDialog);

    /// <summary>
    /// Whether a completion program typed for a download on another server can be sent there. Only the text is looked
    /// at, never a file: that path is on the server, and even asking this PC about a network path would send this
    /// Windows account's credentials there. A full path on the server (<c>/opt/x</c> or <c>C:\x.exe</c>), not a network
    /// or device path; the server checks the rest. Null when it can be sent.
    /// </summary>
    /// <param name="style">The server's path style (<see cref="ShownServer.PathStyle"/>): a Windows server's program is
    /// not <c>/opt/x</c>, and the example is in the server's own style.</param>
    public static string? ServerProgramPathProblem(string? path, PathStyle style = PathStyle.Either)
    {
        var text = path?.Trim() ?? string.Empty;
        if (text.Length == 0) return "Type the full path of the program on the server.";
        if (text.StartsWith(@"\\", StringComparison.Ordinal) || text.StartsWith("//", StringComparison.Ordinal))
            return "Use a program on the server itself, not on a network share.";
        var server = style == PathStyle.ThisPc ? PathStyle.Either : style;
        var example = server == PathStyle.Windows ? @"C:\Tools\done.cmd" : "/opt/tools/done.sh";
        return ServerPaths.IsFull(text, server) ? null : $"Type the full path of the program on the server, such as {example}.";
    }

    /// <summary>A folder is chosen with this PC's folder dialog, or with the server's folder browser.</summary>
    public static bool UsesServerFolderBrowser(bool remote) => remote;

    /// <summary>Why a file on this PC can't be used as-is for another server, or null when it can be sent.</summary>
    /// <param name="uploadsOffered">The server's hello lists <c>uploads</c> (<see cref="RemoteState.UploadsOffered"/>).</param>
    public static string? LocalFileProblem(bool remote, string kind, string serverName, bool uploadsOffered = false) => !remote
        ? null
        : kind switch
        {
            "torrent" => null, // uploaded
            "container" when uploadsOffered => null, // uploaded
            "container" => $"A container file can't be sent to {serverName} from this PC. Open it in this PC's view instead, or in the server's web UI.",
            "archive" => null, // chosen from the server's folders
            _ => $"That file is on this PC, not on {serverName}.",
        };
}

/// <summary>
/// For another server: whether a finished download may open a completion window here (each is a process of its own).
/// Only a download this window saw unfinished while it showed the server, each one once, and at most
/// <see cref="MostWindows"/> within <see cref="Window"/>: a server that sends many completions, or invented ones, can't
/// make this PC start a window for each. Nothing here touches the UI.
/// </summary>
internal sealed class RemoteCompletionGate
{
    public const int MostWindows = 3;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private const int MostRemembered = 20_000;

    private readonly HashSet<Guid> _seenUnfinished = [];
    private readonly HashSet<Guid> _opened = [];
    private readonly Queue<DateTimeOffset> _recent = new();

    /// <summary>A download's state as the server reports it, from a snapshot or an event.</summary>
    public void Saw(Guid jobId, string? state)
    {
        if (state == "completed" || _seenUnfinished.Count >= MostRemembered) return;
        _seenUnfinished.Add(jobId);
    }

    /// <summary>True once for a download seen unfinished, while fewer than <see cref="MostWindows"/> opened in the last minute.</summary>
    public bool MayOpen(Guid jobId, DateTimeOffset now)
    {
        if (!_seenUnfinished.Contains(jobId) || _opened.Contains(jobId)) return false;
        while (_recent.Count > 0 && now - _recent.Peek() >= Window) _recent.Dequeue();
        if (_recent.Count >= MostWindows) return false;
        _recent.Enqueue(now);
        _opened.Add(jobId);
        _seenUnfinished.Remove(jobId);
        return true;
    }

    /// <summary>Another Host is shown: nothing seen before counts.</summary>
    public void Clear()
    {
        _seenUnfinished.Clear();
        _opened.Clear();
        _recent.Clear();
    }
}

/// <summary>
/// Paths on another server, which may use '/' (Linux) or '\' (Windows). This PC's Path methods would put in '\', so a
/// server's path is split and joined with the separator it already uses.
/// </summary>
internal static class ServerPaths
{
    public static string FileName(string path)
    {
        var trimmed = path.TrimEnd('/', '\\');
        var cut = trimmed.LastIndexOfAny(['/', '\\']);
        return cut < 0 ? trimmed : trimmed[(cut + 1)..];
    }

    /// <summary>The folder part; the root stays the root (<c>/</c>, <c>C:\</c>).</summary>
    public static string Folder(string path)
    {
        var cut = path.LastIndexOfAny(['/', '\\']);
        if (cut < 0) return string.Empty;
        var folder = path[..cut];
        return folder.Length == 0 || (folder.Length == 2 && folder[1] == ':') ? path[..(cut + 1)] : folder;
    }

    /// <summary>
    /// A full path on a server: <c>/srv/downloads</c> (Linux) or <c>D:\Downloads</c> (Windows). This PC's
    /// <see cref="Path.IsPathFullyQualified(string)"/> would refuse the first. A network path is no full path here.
    /// </summary>
    public static bool IsFull(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)) return false;
        return path.StartsWith('/') || (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/');
    }

    /// <summary>
    /// A server's path style. Its own default folder settles it; without one, the desktop Host's bridge
    /// (<c>hostKind: windows</c>) is Windows and the headless server (<c>server</c>), which ships for Linux only, is
    /// Linux. A server that said neither takes both.
    /// </summary>
    public static PathStyle StyleFor(string? hostKind, string? defaultFolder)
    {
        var folder = defaultFolder?.Trim() ?? string.Empty;
        if (IsLinuxFull(folder)) return PathStyle.Linux;
        if (IsWindowsFull(folder)) return PathStyle.Windows;
        return hostKind switch
        {
            HostKinds.Windows => PathStyle.Windows,
            HostKinds.Server => PathStyle.Linux,
            _ => PathStyle.Either,
        };
    }

    /// <summary>
    /// A full folder path for a Host of <paramref name="style"/>. This PC keeps Windows' rules; a server's network path
    /// is never one (<see cref="IsFull(string)"/>).
    /// </summary>
    public static bool IsFull(string path, PathStyle style)
    {
        var text = path.Trim();
        switch (style)
        {
            case PathStyle.ThisPc:
                try
                {
                    return Path.IsPathFullyQualified(text);
                }
                catch (ArgumentException)
                {
                    return false;
                }
            case PathStyle.Linux:
                return IsLinuxFull(text);
            case PathStyle.Windows:
                return IsWindowsFull(text);
            default:
                return IsFull(text);
        }
    }

    /// <summary>A folder to name as an example, in the Host's own style.</summary>
    public static string Example(PathStyle style) => style switch
    {
        PathStyle.Linux => "/downloads",
        PathStyle.Either => @"/downloads or D:\Downloads",
        _ => @"D:\Downloads",
    };

    /// <summary>"a full folder path, such as …", with "on the server" for another server.</summary>
    public static string FullFolderWords(PathStyle style) =>
        style == PathStyle.ThisPc ? $"a full folder path, such as {Example(style)}" : $"a full folder path on the server, such as {Example(style)}";

    private static bool IsLinuxFull(string path) => path.StartsWith('/') && !path.StartsWith("//", StringComparison.Ordinal);

    private static bool IsWindowsFull(string path) => path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/';

    public static string Join(string folder, string name)
    {
        var separator = folder.Contains('\\') && !folder.Contains('/') ? '\\' : '/';
        return folder.TrimEnd('/', '\\') + separator + name.TrimStart('/', '\\');
    }
}

/// <summary>
/// What the server folder chooser shows for one <c>folders.browse</c> answer (UI.B16, U-WR-10): the rows with their whole
/// paths for a tooltip, what to say when a folder holds none, and a notice that informs rather than reports a problem.
/// </summary>
internal static class ServerFolderListing
{
    public const string TruncatedNotice = "Only the first 2,000 folders are listed. Type a path to open one further down.";

    public const string EmptyFolder = "This folder has no folders inside it. Choose it, or make a new folder in it.";

    public const string NoRoots = "This server has no folders this app may save to.";

    /// <param name="Rows">Each row's text, and its whole path for the tooltip and for opening it. The contract's own
    /// public type, which the list's binding can read.</param>
    /// <param name="Notice">Information for the line under the list, never an error; null for none.</param>
    /// <param name="Empty">Shown in place of the list when it has no rows; null when it has some.</param>
    public sealed record View(IReadOnlyList<FolderEntryDto> Rows, string? Notice, string? Empty, bool CanGoUp, bool CanCreate);

    /// <summary>The roots are listed with their whole paths, a folder's contents by name.</summary>
    public static View For(FolderBrowseResult result)
    {
        var rows = result.Path is null
            ? result.Roots.Select(root => new FolderEntryDto { Name = root, Path = root }).ToList()
            : result.Folders;
        string? empty = rows.Count > 0 ? null : result.Path is null ? NoRoots : EmptyFolder;
        return new View(rows, result.Truncated ? TruncatedNotice : null, empty, result.Path is not null, result.Path is not null);
    }

    /// <summary>'Choose this folder' needs a folder: one opened, or one typed.</summary>
    public static bool CanChoose(string? typed) => !string.IsNullOrWhiteSpace(typed);
}

/// <summary>
/// The words for a Move/Rename the server refused (U-WR-12): its own reason, then the folders it may use, so the user can
/// put one in the dialog, which stays open with what they typed.
/// </summary>
internal static class MoveRefusal
{
    /// <summary>
    /// The server's refusal of a folder outside its roots ("That folder is outside the folders this server may use."),
    /// the one where listing them helps.
    /// </summary>
    public static bool IsOutsideRoots(string? error) => error?.Contains(" is outside ", StringComparison.OrdinalIgnoreCase) == true;

    public static string Text(string? error, IReadOnlyList<string>? roots, string serverName)
    {
        var reason = string.IsNullOrWhiteSpace(error) ? $"{serverName} refused the move." : error.Trim();
        if (roots is not { Count: > 0 }) return reason;
        var list = string.Join(Environment.NewLine, roots.Select(root => "  " + root));
        return $"{reason}{Environment.NewLine}The folders {serverName} may use:{Environment.NewLine}{list}";
    }
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
        foreach (var name in ThisPcFields)
        {
            var property = typeof(SettingsDto).GetProperty(name) ?? throw new InvalidOperationException($"SettingsDto has no {name}.");
            property.SetValue(edited, property.GetValue(serverAtOpen));
        }

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
