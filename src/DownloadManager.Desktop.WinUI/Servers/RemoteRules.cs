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
        if (!_seenUnfinished.Contains(jobId) || _opened.Contains(jobId) || !HasRoom(now)) return false;
        Finished(jobId);
        MayOpenWindow(now);
        return true;
    }

    /// <summary>
    /// True once for a download seen unfinished here: its finish is news. Whether a window opens for it is decided with
    /// the rest of its burst (<see cref="CompletionBurst"/>) and <see cref="MayOpenWindow"/>.
    /// </summary>
    public bool Finished(Guid jobId)
    {
        if (!_seenUnfinished.Contains(jobId) || _opened.Contains(jobId)) return false;
        _opened.Add(jobId);
        _seenUnfinished.Remove(jobId);
        return true;
    }

    /// <summary>True, and counted, while fewer than <see cref="MostWindows"/> windows opened in the last minute.</summary>
    public bool MayOpenWindow(DateTimeOffset now)
    {
        if (!HasRoom(now)) return false;
        _recent.Enqueue(now);
        return true;
    }

    private bool HasRoom(DateTimeOffset now)
    {
        while (_recent.Count > 0 && now - _recent.Peek() >= Window) _recent.Dequeue();
        return _recent.Count < MostWindows;
    }

    /// <summary>Another Host is shown: nothing seen before counts.</summary>
    public void Clear()
    {
        _seenUnfinished.Clear();
        _opened.Clear();
        _recent.Clear();
    }
}

/// <summary>One completion window to open: a package's, or a single download's.</summary>
internal readonly record struct CompletionWindow(bool IsPackage, Guid Id);

/// <summary>
/// Downloads that finished close together, gathered for a moment and then given their windows (U-WL-4, U-WR-16): a
/// package gets one window, once all of its files have settled (some may have failed), however many of them finished in the burst, and a file that
/// is still waiting for its package's other files gets none. Waiting also lets a package that was only just made
/// (its list is announced a moment after its first files) be known before its files are judged one by one.
/// </summary>
internal static class CompletionBurst
{
    /// <summary>How long finishes are gathered before their windows open.</summary>
    public static readonly TimeSpan Gather = TimeSpan.FromSeconds(1.2);

    /// <param name="finished">The downloads that finished, in the order they did.</param>
    /// <param name="packageOf">A download's package and whether every file of it has settled (<see cref="Settled"/>); null when it is in none.</param>
    public static IReadOnlyList<CompletionWindow> Plan(IEnumerable<Guid> finished, Func<Guid, (Guid Package, bool Settled)?> packageOf)
    {
        var windows = new List<CompletionWindow>();
        var seen = new HashSet<Guid>();
        foreach (var job in finished)
        {
            if (!seen.Add(job)) continue;
            if (packageOf(job) is not { } package)
            {
                windows.Add(new CompletionWindow(false, job));
                continue;
            }

            var window = new CompletionWindow(true, package.Package);
            if (package.Settled && !windows.Contains(window)) windows.Add(window);
        }

        return windows;
    }

    /// <summary>
    /// Every file of a package has finished one way or another and at least one was saved, so its completion window is
    /// due (U-WR-16): a package with a failed or cancelled file still gets one, with the counts, rather than none.
    /// </summary>
    public static bool Settled(IReadOnlyCollection<string?> states) =>
        states.Count > 0 && states.All(state => state is "completed" or "failed" or "cancelled") && states.Any(state => state == "completed");

    /// <summary>"3 of 5 files complete · 2 failed": the package window's line, with what didn't finish counted.</summary>
    public static string Summary(IReadOnlyCollection<string?> states)
    {
        var completed = states.Count(state => state == "completed");
        var failed = states.Count(state => state == "failed");
        var cancelled = states.Count(state => state == "cancelled");
        return $"{completed} of {states.Count} files complete" + (failed > 0 ? $" · {failed} failed" : string.Empty)
               + (cancelled > 0 ? $" · {cancelled} cancelled" : string.Empty);
    }

    /// <summary>The package window's title once it has settled, without the app's name; null while files are still going.</summary>
    public static string? Title(IReadOnlyCollection<string?> states)
    {
        if (!Settled(states)) return null;
        var failed = states.Count(state => state == "failed");
        var cancelled = states.Count(state => state == "cancelled");
        if (failed > 0) return $"Package finished, {failed} of {states.Count} files failed";
        if (cancelled > 0) return $"Package finished, {cancelled} of {states.Count} files cancelled";
        return "Package complete";
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
            HostKinds.Linux => PathStyle.Linux,
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

    public const string CantOpen = "That folder can't be opened.";

    /// <summary>The theme token for a problem under the list.</summary>
    public const string ErrorBrush = "InboundErrorTextBrush";

    /// <summary>The theme token for information under the list (review of UI.B16: a notice is never red).</summary>
    public const string NoticeBrush = "InboundSecondaryTextBrush";

    /// <summary>A line under the list, and whether it reports a problem.</summary>
    public sealed record Message(string Text, bool IsError)
    {
        /// <summary>Red only for a problem; anything else in secondary text.</summary>
        public string Brush => IsError ? ErrorBrush : NoticeBrush;
    }

    /// <summary>'Choose this folder' with nothing opened or typed: a problem.</summary>
    public static readonly Message NothingChosen = new("Open a folder first.", IsError: true);

    /// <summary>'New folder' at the list of roots: a hint, not a problem.</summary>
    public static readonly Message NewFolderNeedsAFolder = new("Open one of the folders first; a new folder is made inside it.", IsError: false);

    /// <param name="Rows">Each row's text, and its whole path for the tooltip and for opening it. The contract's own
    /// public type, which the list's binding can read.</param>
    /// <param name="Message">The line under the list: the server's refusal (red) or the long-list notice (grey); null for none.</param>
    /// <param name="Empty">Shown in place of the list when it has no rows; null when it has some.</param>
    public sealed record View(IReadOnlyList<FolderEntryDto> Rows, Message? Message, string? Empty, bool CanGoUp, bool CanCreate);

    /// <summary>A refused <c>folders.browse</c> or <c>folders.create</c>: the list stays as it was, and this says why, in red.</summary>
    public static Message Refusal(FolderBrowseResult result)
        => new(string.IsNullOrWhiteSpace(result.Error) ? CantOpen : result.Error, IsError: true);

    /// <summary>The roots are listed with their whole paths, a folder's contents by name.</summary>
    public static View For(FolderBrowseResult result)
    {
        var rows = result.Path is null
            ? result.Roots.Select(root => new FolderEntryDto { Name = root, Path = root }).ToList()
            : result.Folders;
        string? empty = rows.Count > 0 ? null : result.Path is null ? NoRoots : EmptyFolder;
        var notice = result.Truncated ? new Message(TruncatedNotice, IsError: false) : null;
        return new View(rows, notice, empty, result.Path is not null, result.Path is not null);
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
    /// The server's refusal of a folder outside its roots, the one where listing them helps: told by its
    /// <see cref="JobActionErrorCodes.OutsideRoots"/> code, never by its wording. A server older than the code gets
    /// its reason shown without the list.
    /// </summary>
    public static bool IsOutsideRoots(JobActionResponse? result) => result is { Ok: false, ErrorCode: JobActionErrorCodes.OutsideRoots };

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

/// <summary>The words of Options while another server is shown (U-WR-4, U-WR-6, U-WR-13).</summary>
internal static class RemoteOptionsText
{
    public const string ThisPcUnreachable =
        "This PC's Host isn't running, so Options can't be shown. Open Inbound again to start it.";

    /// <summary>Options opens anyway, with this PC's own pages: they never needed the server.</summary>
    public static string ServerUnreachable(string serverName) =>
        $"Can't reach {serverName}, so its options can't be shown right now. This PC's own options are here, and {serverName}'s come back once it can be reached.";

    /// <summary>
    /// The first line of the server's own page. The Docker server's web UI has every option; the Windows app's web bridge
    /// has the same short list as here, so its folders and the rest are changed in the app on that PC.
    /// </summary>
    public static string ServerPageHint(string serverName, string? hostKind) => hostKind == HostKinds.Server
        ? $"{serverName}'s own options, saved on {serverName}. Its other options (proxy, site logins, categories and more) are changed in its web UI."
        : $"{serverName}'s own options that can be changed from another device, saved on {serverName}. Its folders and its other options are changed in the Inbound app on {serverName}.";

    /// <summary>Under a provider's API key: where the key is kept. Null server: this PC.</summary>
    public static string ProviderKeyNote(string? serverName, string? hostKind) => serverName is null
        ? "These buttons take effect straight away, whichever way this window is closed. The key is kept encrypted for your Windows account."
        : hostKind == HostKinds.Windows
            ? $"This is the account {serverName} uses, and the key is saved on {serverName}, encrypted for the Windows account Inbound runs under there. These buttons take effect straight away, whichever way this window is closed."
            : hostKind == HostKinds.Server
                ? $"This is the account {serverName} uses, and the key is saved on {serverName}, encrypted in its data folder. These buttons take effect straight away, whichever way this window is closed."
                : $"This is the account {serverName} uses, and the key is saved on {serverName}. These buttons take effect straight away, whichever way this window is closed.";
}

/// <summary>What Add URL says when the Host won't add an address (U-WR-2).</summary>
internal static class AddRules
{
    /// <summary>Before Start: the Host said it would refuse this address, so Start and Download later are off.</summary>
    public static string RefusedText(string reason, ServerTarget target) =>
        $"{reason.Trim()} So this address can't be added to {target.Name}; Start download and Download later are off.";

    /// <summary>After Start: shown in the dialog, which opens again with everything that was typed.</summary>
    public static string NotAddedText(string? error, ServerTarget target) =>
        $"Not added{(target.IsLocal ? string.Empty : $" to {target.Name}")}: {(string.IsNullOrWhiteSpace(error) ? "the Host rejected the request." : error.Trim())} Change what is needed and try again, or Cancel.";

    /// <summary>How often a dialog that couldn't be shown again is tried again, a tenth of a second apart, before its
    /// reason goes to the status bar instead.</summary>
    public const int MostReshowTries = 100;

    /// <summary>
    /// After a show of the add dialog (U-WR-2). <paramref name="shown"/> is false when it couldn't be shown (another
    /// dialog was still open on the window); <paramref name="problem"/> is why the Start that was chosen didn't add it;
    /// <paramref name="lastProblem"/> is the reason it is being shown again, null on its first show. A dialog shown again
    /// holds what was typed, so it is tried again rather than dropped.
    /// </summary>
    public static ReshowStep AfterShow(bool shown, string? problem, string? lastProblem, int failedShows)
    {
        if (!shown) return lastProblem is null ? ReshowStep.Stop : failedShows < MostReshowTries ? ReshowStep.TryAgain : ReshowStep.GiveUp;
        return problem is null ? ReshowStep.Stop : ReshowStep.ShowAgain;
    }

    /// <summary>
    /// The refusal that holds after a file-info check (U-WR-2). A refusal from the Host sets it, an answer without one
    /// clears it, and a check that got no answer (timed out, failed, or was overtaken) leaves it as it was.
    /// </summary>
    public static string? RefusalAfter(string? current, bool answered, bool refused, string? error)
    {
        if (!answered) return current;
        return refused ? error ?? "This address can't be added." : null;
    }
}

/// <summary>What Add URL does after its dialog closes (<see cref="AddRules.AfterShow"/>).</summary>
internal enum ReshowStep
{
    /// <summary>Added, cancelled, or never shown: nothing typed is waiting.</summary>
    Stop,
    /// <summary>The Start failed: show the dialog again, with the reason and everything typed.</summary>
    ShowAgain,
    /// <summary>It couldn't be shown again yet: wait a moment and try again.</summary>
    TryAgain,
    /// <summary>It couldn't be shown again after many tries: the reason goes to the status bar.</summary>
    GiveUp,
}
