using System.Globalization;
using DownloadManager.Contracts;
using DownloadManager.Sites;

namespace DownloadManager.Desktop.WinUI;

/// <summary>What a command of the server pane (menu, toolbar, keys, context menu) is. Shared by the Site menu and the menus.</summary>
internal enum SiteCommand
{
    Download, DownloadTo, Open, Rename, Delete, NewFolder, Upload, UploadFolder, Refresh, PasteAddress, CopyAddress, CopyPath, StarFolder, Preview, Sync,
}

/// <summary>What a command of the This PC pane is.</summary>
internal enum LocalCommand
{
    Upload, Open, ShowInExplorer, Rename, Delete, DeleteForGood, NewFolder, Refresh, CopyPath,
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
    /// this PC), "Download to…" (a folder picked on this PC) and "Upload files…" (files picked on this PC, which that server
    /// can't read) can't work there, so they are off, as Two panes is. "Upload a folder…" picks on that server and stays.
    /// </summary>
    public static bool OffForOtherServer(SiteCommand command) => command is SiteCommand.Open or SiteCommand.DownloadTo or SiteCommand.Upload;

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
        var getGroup = new List<SiteCommand> { SiteCommand.Download, SiteCommand.DownloadTo };
        if (one) getGroup.Add(SiteCommand.Open);
        // SITES.13: a file can be previewed (text or image).
        if (one && !picked[0].IsFolder) getGroup.Add(SiteCommand.Preview);
        groups.Add(getGroup);
        var change = new List<SiteCommand>();
        if (one) change.Add(SiteCommand.Rename);
        change.Add(SiteCommand.Delete);
        if (canNewFolder) change.Add(SiteCommand.NewFolder);
        groups.Add(change);
        var last = new List<SiteCommand> { SiteCommand.CopyPath, SiteCommand.CopyAddress };
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
        groups.Add(one ? [LocalCommand.Rename, LocalCommand.Delete, LocalCommand.DeleteForGood] : [LocalCommand.Delete, LocalCommand.DeleteForGood]);
        groups.Add([LocalCommand.CopyPath]);
        groups.Add([LocalCommand.NewFolder]);
        return groups;
    }

    /// <summary>"Copy path": the selected items' paths on the remote, one per line, or the open folder's when nothing is selected.</summary>
    public static string Paths(string folder, IReadOnlyCollection<string> names) =>
        names.Count == 0 ? folder : string.Join('\n', names.Select(name => SitesBrowseRules.Join(folder, name)));

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
        SiteCommand.CopyPath => "Copy path",
        SiteCommand.StarFolder => "Star this folder",
        SiteCommand.Preview => "Preview",
        SiteCommand.Sync => "Sync linked folder…",
        _ => command.ToString(),
    };

    public static string Label(LocalCommand command) => command switch
    {
        LocalCommand.Upload => "Upload",
        LocalCommand.Open => "Open",
        LocalCommand.ShowInExplorer => "Show in Explorer",
        LocalCommand.Rename => "Rename",
        LocalCommand.Delete => "Delete (Recycle Bin)",
        LocalCommand.DeleteForGood => "Delete permanently…",
        LocalCommand.NewFolder => "New folder",
        LocalCommand.Refresh => "Refresh",
        LocalCommand.CopyPath => "Copy path",
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
        SiteCommand.Preview => "Space",
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

    // ---- The overwrite question, one file at a time (SITES.11 R5) ----------------------------------------------

    /// <summary>One side of a name that is on both: its size (-1 when not known) and last-changed moment.</summary>
    internal readonly record struct ConflictSide(long Size, DateTimeOffset? Modified);

    /// <summary>A picked name and the same name in the target, with what is known of each. A side can be null (not looked at).</summary>
    internal sealed record ConflictPair(string Name, bool IsFolder, ConflictSide? Source, ConflictSide? Target);

    /// <summary>How old the target is against the source.</summary>
    internal enum TargetAge { Unknown, Newer, Older, Same }

    /// <summary>Moments this close count as the same (file systems and servers keep time to different precision).</summary>
    public static readonly TimeSpan SameMoment = TimeSpan.FromSeconds(2);

    /// <summary>Replace is recommended only for a target older than the source by more than this, and only with <see cref="TimesArePrecise"/>.</summary>
    public static readonly TimeSpan ReplaceRecommendedAfter = TimeSpan.FromDays(1);

    /// <summary>
    /// Whether a remote's file times are good enough to recommend Replace on: SFTP, WebDAV and the cloud kinds are; FTP (with or
    /// without TLS) is not (a listing may give minutes, local time or no year), nor a kind not known.
    /// </summary>
    public static bool TimesArePrecise(string? kind) => kind is { Length: > 0 } && !string.Equals(kind, SiteKindCatalog.Ftp, StringComparison.OrdinalIgnoreCase);

    internal static TargetAge AgeOf(ConflictPair pair)
    {
        if (pair.IsFolder || pair.Source?.Modified is not { Year: > 1 } source || pair.Target?.Modified is not { Year: > 1 } target) return TargetAge.Unknown;
        var difference = target - source;
        if (difference.Duration() <= SameMoment) return TargetAge.Same;
        return difference > TimeSpan.Zero ? TargetAge.Newer : TargetAge.Older;
    }

    /// <summary>The picked names that are in the target, as pairs (in picked order). Sides come from the dictionaries by name.</summary>
    internal static List<ConflictPair> ConflictPairs(IEnumerable<SiteTransferNameDto> picked, IReadOnlyDictionary<string, ConflictSide> sources,
        IReadOnlyList<KeyValuePair<string, ConflictSide>> targets, bool ignoreCase)
    {
        var comparer = ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var there = new Dictionary<string, ConflictSide>(comparer);
        foreach (var (name, side) in targets) there.TryAdd(name, side);
        var pairs = new List<ConflictPair>();
        foreach (var name in picked)
        {
            if (!there.TryGetValue(name.Name, out var target)) continue;
            ConflictSide? source = sources.TryGetValue(name.Name, out var known) ? known : null;
            pairs.Add(new ConflictPair(name.Name, name.IsFolder, source, target));
        }

        return pairs;
    }

    /// <summary>
    /// The Keep both name the question may promise, or null to say "under a numbered name". The Host picks its name itself when
    /// the copy goes (it looks for each candidate in the target), so a name is shown only when the window's look is sure to be
    /// the Host's: the target is listed in full and neither the name nor a candidate is there in another case only (a server
    /// may or may not tell "A.txt" from "a.txt").
    /// </summary>
    public static string? ShownKeepBothName(string name, IReadOnlyCollection<string> taken, bool targetComplete)
    {
        if (!targetComplete) return null;
        var exact = new HashSet<string>(taken, StringComparer.Ordinal);
        if (!exact.Contains(name)) return null;
        var loose = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
        for (var number = 2; number < 1000; number++)
        {
            var candidate = SiteTransferPaths.Numbered(name, number);
            if (exact.Contains(candidate)) continue;
            return loose.Contains(candidate) ? null : candidate;
        }

        return null;
    }

    /// <summary>
    /// The choice for names the question did not show (not listed in a target only partly listed): the one "Do this for the
    /// others" applied, when it was ticked and the target is partly listed; otherwise Skip.
    /// </summary>
    public static string ChoiceForUnlisted(bool targetComplete, string? appliedToOthers) =>
        !targetComplete && appliedToOthers is Skip or Replace or KeepBoth ? appliedToOthers : Skip;

    /// <summary>"notes (2).txt": the first number from 2 that is not in <paramref name="taken"/>; the Host's own rule (SiteTransferPaths.Numbered).</summary>
    public static string KeepBothName(string name, IEnumerable<string> taken, bool ignoreCase)
    {
        var used = new HashSet<string>(taken, ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        for (var number = 2; number < 1000; number++)
        {
            var candidate = SiteTransferPaths.Numbered(name, number);
            if (!used.Contains(candidate)) return candidate;
        }

        return SiteTransferPaths.Numbered(name, 2);
    }

    /// <summary>"notes.txt is newer on the server" / "is older on" / "is already on"; "this PC" for a download. A folder is always "already".</summary>
    internal static string OverwriteTitle(ConflictPair pair, bool upload)
    {
        var where = upload ? "the server" : "this PC";
        return AgeOf(pair) switch
        {
            TargetAge.Newer => $"{pair.Name} is newer on {where}",
            TargetAge.Older => $"{pair.Name} is older on {where}",
            _ => $"{pair.Name} is already on {where}",
        };
    }

    /// <summary>The two rows ("On this PC", "On the server") with size, date and "(newer)", source first; none for a folder.</summary>
    internal static List<(string Label, string Text)> OverwriteRows(ConflictPair pair, bool upload)
    {
        if (pair.IsFolder) return [];
        var age = AgeOf(pair);
        var source = SideText(pair.Source, age == TargetAge.Older);
        var target = SideText(pair.Target, age == TargetAge.Newer);
        return upload ? [("On this PC", source), ("On the server", target)] : [("On the server", source), ("On this PC", target)];
    }

    private static string SideText(ConflictSide? side, bool newer)
    {
        var parts = new List<string>();
        if (side is { } known)
        {
            if (SitesBrowseRules.SizeText(known.Size, isFolder: false) is { Length: > 0 } size) parts.Add(size);
            if (SitesBrowseRules.ModifiedText(known.Modified) is { Length: > 0 } moment) parts.Add(moment);
        }

        var text = parts.Count == 0 ? "not known" : string.Join(" · ", parts);
        return newer ? text + " (newer)" : text;
    }

    internal readonly record struct OverwriteChoice(string Value, string Label, bool Recommended);

    /// <summary>
    /// Skip, Replace, Keep both, in that order, worded for the direction and the ages. The safer one is marked: Skip when the
    /// target is newer, the same or not known, Replace only when the target is older by more than a day on a remote whose
    /// times are precise (<see cref="TimesArePrecise"/>). <paramref name="keepName"/> is the name Keep both gives
    /// (<see cref="ShownKeepBothName"/>), or null for "under a numbered name"; folders say the new copies are numbered.
    /// </summary>
    internal static OverwriteChoice[] OverwriteChoices(ConflictPair pair, bool upload, string? keepName, bool preciseTimes)
    {
        var age = AgeOf(pair);
        var copy = upload ? "the server copy" : "the copy on this PC";
        string skip, replace, keep;
        if (pair.IsFolder)
        {
            skip = "Skip files that are already there";
            replace = "Replace files that are already there";
            keep = "Keep both, number the new copies";
        }
        else
        {
            skip = upload ? "Skip, keep the server copy" : "Skip, keep the copy on this PC";
            replace = age switch
            {
                TargetAge.Newer => $"Replace {copy} with the older file",
                TargetAge.Older => $"Replace {copy} with the newer file",
                _ => $"Replace {copy}",
            };
            var verb = upload ? "upload" : "download";
            keep = keepName is { Length: > 0 } ? $"Keep both, {verb} as {keepName}" : $"Keep both, {verb} under a numbered name";
        }

        // Replace only on times that can be trusted and a clear gap; Skip otherwise (it loses nothing).
        var replaceFirst = preciseTimes && age == TargetAge.Older
            && pair.Source?.Modified is { } source && pair.Target?.Modified is { } target && source - target > ReplaceRecommendedAfter;
        return
        [
            new OverwriteChoice(Skip, skip, !replaceFirst),
            new OverwriteChoice(Replace, replace, replaceFirst),
            new OverwriteChoice(KeepBoth, keep, false),
        ];
    }

    public const string RecommendedMark = " (recommended)";

    /// <summary>"Do this for the other 3 files" (or "items" when a folder is among them); null when none are left.</summary>
    public static string? ApplyToOthersLabel(int others, bool anyFolder)
    {
        if (others <= 0) return null;
        var noun = anyFolder ? (others == 1 ? "item" : "items") : (others == 1 ? "file" : "files");
        return others == 1 ? $"Do this for the other {noun}" : $"Do this for the other {others.ToString(CultureInfo.InvariantCulture)} {noun}";
    }

    internal readonly record struct ConflictBatch(string Conflict, IReadOnlyList<SiteTransferNameDto> Names);

    /// <summary>
    /// The batches to send, one per choice (Skip, Replace, Keep both; the Host takes one choice per batch). A name with no choice
    /// of its own (not in the target, or not looked at) goes with <paramref name="otherChoice"/>. A file the user chose to skip
    /// is not sent at all; a folder always is, since what is inside it follows the choice and may be new.
    /// </summary>
    internal static List<ConflictBatch> ConflictBatches(IReadOnlyList<SiteTransferNameDto> picked, IReadOnlyDictionary<string, string> choices, string otherChoice = Skip)
    {
        var groups = new Dictionary<string, List<SiteTransferNameDto>> { [Skip] = [], [Replace] = [], [KeepBoth] = [] };
        foreach (var name in picked)
        {
            var has = choices.TryGetValue(name.Name, out var own);
            if (has && own == Skip && !name.IsFolder) continue;
            var choice = has ? own! : otherChoice;
            groups[groups.ContainsKey(choice) ? choice : Skip].Add(name);
        }

        return [.. new[] { Skip, Replace, KeepBoth }.Where(key => groups[key].Count > 0).Select(key => new ConflictBatch(key, groups[key]))];
    }

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
        // Also run, mount or install something, or open a connection, when opened.
        ".ws", ".jnlp", ".rdp", ".theme", ".themepack", ".search-ms", ".searchConnector-ms", ".msu", ".cab", ".iso", ".img", ".vhd",
        ".vhdx", ".wsb", ".pyz", ".pyc", ".ade", ".adp", ".mde", ".accde", ".xbap", ".one",
    };

    /// <summary>A file "Open" asks about before running: a program, script, installer or shortcut.</summary>
    public static bool IsRunnable(string name)
    {
        var trimmed = (name ?? "").TrimEnd(' ', '.');
        var dot = trimmed.LastIndexOf('.');
        return dot >= 0 && Runnable.Contains(trimmed[dot..]);
    }

    public static string RunQuestion(string name, string remote) => $"{name} is a program or script from {remote}. Run it?";

    /// <summary>A downloaded file the mark of the web could not be written to: Windows won't warn about it, so Open asks.</summary>
    public static string UnmarkedQuestion(string name, string remote) =>
        $"{name} came from {remote}, but it could not be marked as from the internet, so Windows won't warn about it. Open it anyway?";

    /// <summary>A program or script on this PC that the This PC pane's Open would run.</summary>
    public static string LocalRunQuestion(string name) => $"{name} is a program or script. Run it?";

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
        var here = SiteTransferRules.SamePathForm(folder);
        foreach (var t in transfers)
        {
            if (SiteTransferRules.CanClear(t) && !SiteTransferRules.CanResume(t) || string.IsNullOrEmpty(t.LocalFolder)) continue;
            if (!string.Equals(SiteTransferRules.SamePathForm(t.LocalFolder), here, StringComparison.OrdinalIgnoreCase)) continue;
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

    // ---- Drag and drop (R2c) ------------------------------------------------------------------------------
    // Out of scope: dragging server files out to Explorer (it would need virtual files) and moving items within one pane.

    /// <summary>Where a drag comes from: a row of the This PC pane, a row of the server pane, or Windows Explorer.</summary>
    public enum DragFrom { None, ThisPc, Server, Explorer }

    /// <summary>The pane a drag is over.</summary>
    public enum DropPane { Server, ThisPc }

    /// <summary>NoDrop: the no-drop cursor and nothing happens. Do: the drop starts the transfer. Refuse: the drop is taken and the reason goes in the status line.</summary>
    public enum DropKind { NoDrop, Do, Refuse }

    /// <summary>What a drag over a pane means. <see cref="TargetFolder"/> is where it lands (null when NoDrop).</summary>
    public readonly record struct DropPlan(DropKind Kind, string Caption, string? TargetFolder, string? Why)
    {
        public static DropPlan No { get; } = new(DropKind.NoDrop, "", null, null);
    }

    /// <summary>A drop that is taken but not carried out; <paramref name="why"/> goes in the status line.</summary>
    public static DropPlan Refused(string why) => new(DropKind.Refuse, "", null, why);

    /// <summary>The no-drop caption over the server pane while the window manages another Inbound server.</summary>
    public const string DropOtherServerCaption = "Not while managing another server";

    /// <summary>Why this PC's files can't be dropped while the window manages another Inbound server.</summary>
    public const string DropOtherServerReason =
        "Dropping files from this PC doesn't work while this window shows another server: this PC's files can't be sent through that server's Host.";

    /// <summary>
    /// What a drag of <paramref name="source"/> over <paramref name="pane"/> does. The target is the folder the pane shows, or a
    /// folder row under the pointer (<paramref name="row"/>); a file row, "..", the pane the drag came from, a pane that is not
    /// ready (no remote shown, still loading, no folder) and Explorer onto This PC are no-drop. The same commands as the
    /// gutter arrows and Upload/Download run on a drop, so their conflict question and in-flight guard apply.
    /// </summary>
    public static DropPlan PlanDrop(DragFrom source, DropPane pane, SitesBrowseRules.PaneLine? row, string paneFolder, bool paneReady, bool otherServer)
    {
        if (source == DragFrom.None) return DropPlan.No;
        if (pane == DropPane.Server)
        {
            if (source == DragFrom.Server) return DropPlan.No;
        }
        else if (source != DragFrom.Server || otherServer) return DropPlan.No;

        // No remote shown (or still loading): plain no-drop, whichever server the window shows.
        if (!paneReady) return DropPlan.No;
        // Another server: this PC's files can't go through its Host. No-drop (no Drop event follows) with a short caption.
        if (pane == DropPane.Server && otherServer) return new DropPlan(DropKind.NoDrop, DropOtherServerCaption, null, DropOtherServerReason);
        string target;
        if (row is not { } line) target = paneFolder;
        else if (!line.IsFolder || !SitesBrowseRules.IsActionable(line)) return DropPlan.No;
        else target = pane == DropPane.Server ? SitesBrowseRules.Join(paneFolder, line.Name) : System.IO.Path.Combine(paneFolder, line.Name);
        if (pane == DropPane.ThisPc && target.Length == 0) return DropPlan.No;
        return new DropPlan(DropKind.Do, (pane == DropPane.Server ? "Upload to " + SitesBrowseRules.Normalize(target) : "Download to " + target), target, null);
    }

    public const string NotFilesOnThisPc = "Some of the dragged items aren't files on this PC (for example, inside a zip); drag them from a folder.";

    public const string WholeDriveText = "A whole drive or share can't be uploaded; drag a folder on it.";

    public const string TransferStartingText = "A transfer is starting; try again.";

    /// <summary>
    /// The names of what Explorer dropped (full paths): all from one folder, each a file or a folder by its attributes
    /// (<paramref name="attributesOf"/>, null when the path is gone). A drive or share root is refused first; so is a folder
    /// that is a junction or symbolic link (it would upload another folder's contents under this name).
    /// </summary>
    public static (string? Folder, List<SiteTransferNameDto> Names, string? Problem) ExplorerNames(IReadOnlyList<string> paths, Func<string, System.IO.FileAttributes?> attributesOf)
    {
        if (paths.Count == 0) return (null, [], "Nothing was dropped.");
        if (paths.Any(IsRootPath)) return (null, [], WholeDriveText);
        var (folder, _, problem) = SiteTransferRules.UploadNames(paths);
        if (problem is not null || folder is null) return (null, [], problem?.Replace("Choose files", "Drag items") ?? "Nothing was dropped.");
        var names = new List<SiteTransferNameDto>();
        foreach (var path in paths)
        {
            var name = System.IO.Path.GetFileName(path.TrimEnd('\\', '/'));
            if (attributesOf(path) is not { } attributes) return (null, [], $"{name} is no longer there.");
            var isFolder = attributes.HasFlag(System.IO.FileAttributes.Directory);
            if (isFolder && attributes.HasFlag(System.IO.FileAttributes.ReparsePoint)) return (null, [], $"{name} is a link to another folder; drag the folder itself.");
            names.Add(new SiteTransferNameDto { Name = name, IsFolder = isFolder });
        }

        return (folder, names, null);
    }

    /// <summary>A drive root (C:\) or a share root (\\srv\share).</summary>
    public static bool IsRootPath(string path)
    {
        var trimmed = (path ?? "").TrimEnd('\\', '/');
        if (trimmed.Length == 0) return true;
        var root = System.IO.Path.GetPathRoot(path)?.TrimEnd('\\', '/');
        return root is { Length: > 0 } && string.Equals(trimmed, root, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A pane's drag as it started: the site (null for This PC), the folder and the names.</summary>
    internal readonly record struct DragStart(string? SiteId, string Folder, IReadOnlyList<SiteTransferNameDto> Names);

    public const string DragMovedText = "The folder the items were dragged from is no longer shown; drag them again.";

    /// <summary>
    /// Why a drop of a drag that started in <paramref name="start"/> can't run now: the source pane shows another folder (or
    /// remote) than when the drag started. Null when it is the same. This PC folders compare without case.
    /// </summary>
    public static string? DragMoved(DragStart start, string? siteNow, string folderNow, bool local)
    {
        var same = local
            ? string.Equals(start.Folder.TrimEnd('\\', '/'), (folderNow ?? "").TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)
            : start.SiteId == siteNow && SitesBrowseRules.Normalize(start.Folder) == SitesBrowseRules.Normalize(folderNow);
        return same ? null : DragMovedText;
    }

    /// <summary>An upload's target folder that is not the one shown, and could not be read: nothing is asked, existing files are skipped.</summary>
    public static string UnreadTargetText(string folder) => $"{folder} could not be read; files already there are skipped.";
}
