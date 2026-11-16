using System.Globalization;
using DownloadManager.Contracts;
using DownloadManager.Sites;

namespace DownloadManager.Desktop.WinUI;

/// <summary>What a command of the server pane (menu, toolbar, keys, context menu) is. Shared by the Site menu and the menus.</summary>
internal enum SiteCommand
{
    Download, DownloadTo, Open, Rename, Delete, NewFolder, Upload, UploadFolder, Refresh, PasteAddress, CopyAddress, StarFolder,
}

/// <summary>What a command of the This PC pane is.</summary>
internal enum LocalCommand
{
    Upload, Open, ShowInExplorer, Rename, Delete, NewFolder, Refresh,
}

/// <summary>What Enter or a double click does on the selection of the server pane.</summary>
internal enum ActivateAction
{
    None, GoUp, OpenFolder, Download,
}

/// <summary>
/// SITES.11 R2b (sites-rework.md decisions 1 to 3): the pure rules behind the Site menu, the context menus, Enter and
/// double click, the batch conflict question, "Copy address" and the temporary folder of "Open". No UI types, so they are
/// unit-tested.
/// </summary>
internal static class SitesCommandRules
{
    // The values of SiteTransferConflicts (the Sites library is the Host's; the window sends the words).
    public const string Skip = "skip";
    public const string Replace = "replace";
    public const string KeepBoth = "keepBoth";

    // ---- Menus -------------------------------------------------------------------------------------------

    /// <summary>
    /// The server pane's context menu as groups (a separator goes between groups; empty groups are left out). A click
    /// on ".." or on nothing is the empty-space menu: ".." is never acted on. Items the site cannot do (its
    /// <see cref="SiteDto.Capabilities"/>) are left out.
    /// </summary>
    public static List<List<SiteCommand>> ServerMenu(IReadOnlyCollection<string>? capabilities, IEnumerable<SitesBrowseRules.PaneLine> selected, bool otherServer = false)
    {
        var groups = ServerMenuAll(capabilities, selected);
        if (!otherServer) return groups;
        foreach (var group in groups) group.RemoveAll(OffForOtherServer);
        return [.. groups.Where(group => group.Count > 0)];
    }

    /// <summary>
    /// While the window manages another Inbound server, a download lands on that server's disks: "Open" (a file opened on
    /// this PC) and "Download to…" (a folder picked on this PC) can't work there, so they are off, as Two panes is.
    /// </summary>
    public static bool OffForOtherServer(SiteCommand command) => command is SiteCommand.Open or SiteCommand.DownloadTo;

    private static List<List<SiteCommand>> ServerMenuAll(IReadOnlyCollection<string>? capabilities, IEnumerable<SitesBrowseRules.PaneLine> selected)
    {
        var picked = SitesBrowseRules.SelectedForAction(selected);
        var canNewFolder = capabilities?.Contains(SiteCapabilities.NewFolder) == true;
        var groups = new List<List<SiteCommand>>();
        if (picked.Count == 0)
        {
            var first = new List<SiteCommand>();
            if (canNewFolder) first.Add(SiteCommand.NewFolder);
            first.Add(SiteCommand.Upload);
            groups.Add(first);
            groups.Add([SiteCommand.Refresh, SiteCommand.PasteAddress]);
            return groups;
        }

        var one = picked.Count == 1;
        groups.Add(one ? [SiteCommand.Download, SiteCommand.DownloadTo, SiteCommand.Open] : [SiteCommand.Download, SiteCommand.DownloadTo]);
        var change = new List<SiteCommand>();
        if (one) change.Add(SiteCommand.Rename);
        change.Add(SiteCommand.Delete);
        if (canNewFolder) change.Add(SiteCommand.NewFolder);
        groups.Add(change);
        var last = new List<SiteCommand> { SiteCommand.CopyAddress };
        if (one && picked[0].IsFolder) last.Add(SiteCommand.StarFolder);
        groups.Add(last);
        return groups;
    }

    /// <summary>The This PC pane's context menu as groups. <paramref name="canUpload"/>: a site is shown and its folder has loaded.</summary>
    public static List<List<LocalCommand>> LocalMenu(IEnumerable<SitesBrowseRules.PaneLine> selected, bool canUpload)
    {
        var picked = SitesBrowseRules.SelectedForAction(selected);
        if (picked.Count == 0) return [[LocalCommand.NewFolder, LocalCommand.Refresh]];
        var one = picked.Count == 1;
        var groups = new List<List<LocalCommand>>();
        var first = new List<LocalCommand>();
        if (canUpload) first.Add(LocalCommand.Upload);
        if (one)
        {
            first.Add(LocalCommand.Open);
            first.Add(LocalCommand.ShowInExplorer);
        }

        if (first.Count > 0) groups.Add(first);
        groups.Add(one ? [LocalCommand.Rename, LocalCommand.Delete] : [LocalCommand.Delete]);
        groups.Add([LocalCommand.NewFolder]);
        return groups;
    }

    public static string Label(SiteCommand command) => command switch
    {
        SiteCommand.Download => "Download",
        SiteCommand.DownloadTo => "Download to…",
        SiteCommand.Open => "Open",
        SiteCommand.Rename => "Rename",
        SiteCommand.Delete => "Delete on server…",
        SiteCommand.NewFolder => "New folder",
        SiteCommand.Upload => "Upload…",
        SiteCommand.UploadFolder => "Upload a folder…",
        SiteCommand.Refresh => "Refresh",
        SiteCommand.PasteAddress => "Paste address",
        SiteCommand.CopyAddress => "Copy address",
        SiteCommand.StarFolder => "Star this folder",
        _ => command.ToString(),
    };

    public static string Label(LocalCommand command) => command switch
    {
        LocalCommand.Upload => "Upload",
        LocalCommand.Open => "Open",
        LocalCommand.ShowInExplorer => "Show in Explorer",
        LocalCommand.Rename => "Rename",
        LocalCommand.Delete => "Delete (Recycle Bin)",
        LocalCommand.NewFolder => "New folder",
        LocalCommand.Refresh => "Refresh",
        _ => command.ToString(),
    };

    /// <summary>The key the Site menu and the context menu show beside a command (Windows spelling); empty when it has none.</summary>
    public static string KeyText(SiteCommand command) => command switch
    {
        SiteCommand.Download => "Enter",
        SiteCommand.DownloadTo => "Ctrl+Shift+S",
        SiteCommand.Open => "Ctrl+Enter",
        SiteCommand.Rename => "F2",
        SiteCommand.Delete => "Delete",
        SiteCommand.NewFolder => "Ctrl+Shift+N",
        SiteCommand.Upload => "Ctrl+U",
        SiteCommand.Refresh => "F5",
        SiteCommand.CopyAddress => "Ctrl+Shift+C",
        _ => "",
    };

    // ---- Enter and double click -------------------------------------------------------------------------

    /// <summary>
    /// Enter or a double click on the selection: ".." alone goes up, one folder opens, anything else with a file in it (or
    /// several items) downloads at once with no dialog (decisions 1 and 2).
    /// </summary>
    public static ActivateAction Activate(IReadOnlyList<SitesBrowseRules.PaneLine> selected)
    {
        if (selected.Count == 1 && selected[0].IsParent) return ActivateAction.GoUp;
        var picked = SitesBrowseRules.SelectedForAction(selected);
        if (picked.Count == 0) return ActivateAction.None;
        return picked.Count == 1 && picked[0].IsFolder ? ActivateAction.OpenFolder : ActivateAction.Download;
    }

    // ---- The conflict question --------------------------------------------------------------------------

    /// <summary>
    /// The picked names that are already in the target (files and folders: a folder that is there is merged into, and the
    /// files inside it follow the batch's choice), so the batch is asked about once. <paramref name="ignoreCase"/> is true for
    /// a folder on this PC (Windows) and for uploads (a server may not tell case apart either; asking once too often is
    /// better than overwriting unasked).
    /// </summary>
    public static List<string> AlreadyThere(IEnumerable<SiteTransferNameDto> picked, IEnumerable<string> existing, bool ignoreCase)
    {
        var comparer = ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var there = new HashSet<string>(existing, comparer);
        return [.. picked.Where(name => there.Contains(name.Name)).Select(name => name.Name)];
    }

    /// <summary>The batch question: its title and the lines under it.</summary>
    internal readonly record struct ConflictAsk(string Title, IReadOnlyList<string> Notes);

    public const string FoldersFollowNote = "Files inside the folders follow the same choice.";

    /// <summary>
    /// The question for a batch, or null when nothing needs asking. One name: "debian-13.2-amd64.iso is already in
    /// D:\Downloads\ISOs"; several: "3 of these are already in …". When the target is only partly known (a server folder
    /// with pages not loaded) it is asked anyway: "Some of these may already be there". A batch with folders says that the
    /// files inside them follow the same choice.
    /// </summary>
    public static ConflictAsk? ConflictQuestion(IReadOnlyList<string> there, string place, bool hasFolders, bool targetComplete)
    {
        if (there.Count == 0 && targetComplete) return null;
        var title = there.Count switch
        {
            0 => "Some of these may already be there",
            1 => $"{there[0]} is already in {place}",
            _ => $"{there.Count.ToString(CultureInfo.InvariantCulture)} of these are already in {place}",
        };
        var notes = new List<string>();
        if (there.Count == 0) notes.Add($"Only part of {place} is listed, so Inbound can't tell.");
        else if (!targetComplete) notes.Add($"Only part of {place} is listed, so others may be there too.");
        if (hasFolders) notes.Add(FoldersFollowNote);
        return new ConflictAsk(title, notes);
    }

    /// <summary>The choices the question lists, Skip them first (the default).</summary>
    public static readonly (string Value, string Label)[] ConflictChoices =
    [
        (Skip, "Skip them"),
        (Replace, "Replace"),
        (KeepBoth, "Keep both"),
    ];

    // ---- Copy address -----------------------------------------------------------------------------------

    // Port: as an address shows it (0 for the kind's default); DefaultPort: the kind's default (0 when ports don't apply).
    private readonly record struct Parts(string Scheme, string Host, int Port, string BasePath, string User, int DefaultPort = 0);

    private static Parts PartsOf(SiteDto site)
    {
        var host = (site.Host ?? "").Trim().Trim('[', ']');
        switch (site.Kind)
        {
            case SiteKindCatalog.Sftp:
                return new Parts("sftp", host, DefaultedPort(site.Port, 22), "", site.User ?? "", 22);
            case SiteKindCatalog.Ftp:
            {
                var tls = site.Tls ?? SiteTlsModes.Explicit;
                var scheme = tls == SiteTlsModes.None ? "ftp" : "ftps";
                var kindDefault = tls == SiteTlsModes.Implicit ? 990 : 21;
                return new Parts(scheme, host, DefaultedPort(site.Port, kindDefault), "", site.User ?? "", kindDefault);
            }

            case SiteKindCatalog.WebDav when Uri.TryCreate(site.Url ?? "", UriKind.Absolute, out var url):
                return new Parts(url.Scheme, url.DnsSafeHost, url.IsDefaultPort ? 0 : url.Port, url.AbsolutePath.TrimEnd('/'), site.User ?? "",
                    url.Scheme == Uri.UriSchemeHttps ? 443 : 80);
            case SiteKindCatalog.S3:
            {
                // The endpoint may be typed with its scheme; an Amazon site has none and shows the site's own host line.
                var endpoint = (site.Endpoint ?? "").Trim();
                if (Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && uri.Host.Length > 0) host = uri.DnsSafeHost;
                else if (endpoint.Length > 0) host = endpoint.TrimEnd('/');
                return new Parts("s3", host.Length > 0 ? host : site.Name, 0, "", "");
            }

            default:
                // A cloud drive has no address of its own: the kind and the site's name stand in for scheme and host.
                return new Parts(site.Kind, host.Length > 0 ? host : site.Name, 0, "", "");
        }
    }

    // A port the kind's own default is left out of an address.
    private static int DefaultedPort(int port, int kindDefault) => port <= 0 || port == kindDefault ? 0 : port;

    /// <summary>
    /// "sftp://user@host:2222/media/my%20files": scheme, user and port as the site holds them (a default port and a missing
    /// user are left out), an IPv6 host in brackets, each part of the path escaped. A password is never part of it.
    /// </summary>
    public static string Address(SiteDto site, string path)
    {
        var parts = PartsOf(site);
        var host = parts.Host.Contains(':') ? $"[{parts.Host}]" : parts.Host;
        var user = parts.User.Length > 0 ? Uri.EscapeDataString(parts.User) + "@" : "";
        var port = parts.Port > 0 ? ":" + parts.Port.ToString(CultureInfo.InvariantCulture) : "";
        var escaped = string.Join('/', SitesBrowseRules.Normalize(path).Split('/').Select(Uri.EscapeDataString));
        if (escaped.Length == 0) escaped = "/";
        var basePath = parts.BasePath;
        return $"{parts.Scheme}://{user}{host}{port}{basePath}{(escaped == "/" && basePath.Length > 0 ? "" : escaped)}";
    }

    /// <summary>One address per line for the selected names of <paramref name="folder"/>; the folder itself with nothing selected.</summary>
    public static string Addresses(SiteDto site, string folder, IReadOnlyList<string> names) =>
        names.Count == 0
            ? Address(site, folder)
            : string.Join(Environment.NewLine, names.Select(name => Address(site, SitesBrowseRules.Join(folder, name))));

    /// <summary>
    /// "Paste address": the path of this site that the clipboard text names, or null. A path from "/" is taken as it is; an
    /// address must be of this site (its scheme, host and port).
    /// </summary>
    public static string? PathFromText(SiteDto site, string? text)
    {
        var line = (text ?? "").Split('\n', '\r').Select(part => part.Trim().Trim('"')).FirstOrDefault(part => part.Length > 0);
        if (line is null) return null;
        if (line.StartsWith('/')) return SitesBrowseRules.Normalize(line);
        if (!Uri.TryCreate(line, UriKind.Absolute, out var uri) || uri.Host.Length == 0) return null;
        var parts = PartsOf(site);
        if (!string.Equals(uri.Scheme, parts.Scheme, StringComparison.OrdinalIgnoreCase)) return null;
        if (!string.Equals(uri.DnsSafeHost, parts.Host, StringComparison.OrdinalIgnoreCase)) return null;
        // Ports compare as used: an address without one means the kind's default, so a site on 2222 doesn't take
        // "sftp://host/x" (that is port 22), and a site on 22 takes both "sftp://host/x" and "sftp://host:22/x".
        if (parts.DefaultPort > 0)
        {
            var wanted = parts.Port > 0 ? parts.Port : parts.DefaultPort;
            var given = uri.Port > 0 ? uri.Port : parts.DefaultPort;
            if (given != wanted) return null;
        }
        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        if (parts.BasePath.Length > 0)
        {
            if (!path.StartsWith(parts.BasePath, StringComparison.Ordinal)) return null;
            path = path[parts.BasePath.Length..];
        }

        return SitesBrowseRules.Normalize(path);
    }

    // ---- Open -------------------------------------------------------------------------------------------

    /// <summary>
    /// The site's folder under which "Open" downloads: <c>&lt;temp&gt;\Inbound\open\&lt;siteId&gt;</c>, never the app's state
    /// folder. The id is kept to letters, digits, '-' and '_'.
    /// </summary>
    public static string OpenFolder(string tempRoot, string siteId)
    {
        var safe = new string([.. (siteId ?? "").Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_')]);
        return Path.Combine(OpenRoot(tempRoot), safe.Length > 0 ? safe : "site");
    }

    public static string OpenRoot(string tempRoot) => Path.Combine(tempRoot, "Inbound", "open");

    /// <summary>
    /// One folder per Open (<paramref name="openId"/>, a new GUID): a file still open in its program is never replaced by
    /// a later Open of the same name.
    /// </summary>
    public static string OpenFolder(string tempRoot, string siteId, Guid openId) => Path.Combine(OpenFolder(tempRoot, siteId), openId.ToString("N"));

    /// <summary>What a waiting Open is of: a second Open of the same file of the same site is ignored until it ends.</summary>
    public static string OpenKey(string siteId, string folder, string name) => siteId + "\n" + SitesBrowseRules.Join(folder, name);

    /// <summary>An Open folder last written more than a day ago is removed when a new Open starts.</summary>
    public static bool IsStaleOpenFolder(DateTime lastWriteUtc, DateTime nowUtc) => nowUtc - lastWriteUtc > TimeSpan.FromDays(1);

    /// <summary>The mark of the web (zone 3, the internet) that every file "Open" downloads gets, as browsers write it.</summary>
    public static string ZoneIdentifierText => "[ZoneTransfer]\r\nZoneId=3\r\n";

    // Types Windows runs (or that run something) when opened: programs, scripts, installers, shortcuts.
    private static readonly HashSet<string> Runnable = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".scr", ".pif", ".bat", ".cmd", ".ps1", ".psm1", ".psd1", ".ps1xml", ".psc1", ".vbs", ".vbe", ".js", ".jse",
        ".wsf", ".wsh", ".wsc", ".hta", ".msi", ".msp", ".mst", ".msc", ".msh", ".lnk", ".url", ".website", ".reg", ".cpl", ".inf",
        ".jar", ".appx", ".appxbundle", ".msix", ".msixbundle", ".appinstaller", ".application", ".appref-ms", ".gadget", ".scf",
        ".sct", ".chm", ".settingcontent-ms", ".library-ms", ".xll", ".py", ".pyw", ".diagcab", ".vb",
    };

    /// <summary>A file "Open" asks about before running: a program, script, installer or shortcut.</summary>
    public static bool IsRunnable(string name)
    {
        var trimmed = (name ?? "").TrimEnd(' ', '.');
        var dot = trimmed.LastIndexOf('.');
        return dot >= 0 && Runnable.Contains(trimmed[dot..]);
    }

    public static string RunQuestion(string name, string remote) => $"{name} is a program or script from {remote}. Run it?";

    // ---- This PC names -----------------------------------------------------------------------------------

    /// <summary>
    /// A name typed for Rename or New folder on this PC: trimmed, then refused when this PC can't hold it (blank, "." or "..",
    /// a trailing dot or space, CON, NUL and the like, a character Windows refuses). The name, and why not or null.
    /// </summary>
    public static (string Name, string? Problem) LocalNewName(string? typed, bool windows)
    {
        var name = (typed ?? "").Trim();
        if (name.Length == 0) return (name, "Type a name.");
        return (name, SiteTransferPaths.LocalNameProblem(name, windows));
    }

    /// <summary>
    /// A running (or resumable) batch uses <paramref name="name"/> in <paramref name="folder"/> of this PC: a batch writes into
    /// or reads from it (one of its picked items, compared without case), or into a folder inside it.
    /// </summary>
    public static bool LocalItemBusy(IReadOnlyList<SiteTransferDto> transfers, string folder, string name)
    {
        var path = Path.Combine(folder, name);
        if (SiteTransferRules.LocalInUse(transfers, path)) return true;
        var here = folder.TrimEnd('\\', '/');
        foreach (var t in transfers)
        {
            if (SiteTransferRules.CanClear(t) && !SiteTransferRules.CanResume(t) || string.IsNullOrEmpty(t.LocalFolder)) continue;
            if (!string.Equals(t.LocalFolder.TrimEnd('\\', '/'), here, StringComparison.OrdinalIgnoreCase)) continue;
            if (t.Picked.Any(picked => string.Equals(picked.Split('/', '\\')[0], name, StringComparison.OrdinalIgnoreCase))) return true;
        }

        return false;
    }

    // ---- Trouble state ----------------------------------------------------------------------------------

    /// <summary>
    /// The folder the breadcrumbs show: the one that was asked for. Opening a site at its start folder leaves the path to the
    /// Host until a listing arrives, so the site's configured start folder stands in meanwhile and when it fails.
    /// </summary>
    public static string AskedPath(bool atStart, string? startFolder, string path) =>
        atStart && !string.IsNullOrWhiteSpace(startFolder) ? SitesBrowseRules.Normalize(startFolder) : path;
}
