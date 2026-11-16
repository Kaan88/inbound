using System.Globalization;
using DownloadManager.Contracts;

namespace DownloadManager.Desktop.WinUI;

/// <summary>One clickable part of the path above the list: the root "/" or one folder, with the absolute path it opens.</summary>
internal readonly record struct SiteCrumb(string Name, string Path);

/// <summary>
/// SITES.3: the pure rules behind the Sites browse view (remote-sites.md, "Clients"): remote paths are absolute and
/// '/'-separated whatever the server is, breadcrumbs, going up, how sizes and dates read, paging and the words for
/// problems. No UI, so it is unit-tested.
/// </summary>
internal static class SitesBrowseRules
{
    public const string Root = "/";

    /// <summary>How many items one request asks for (the Host's default; its maximum is 2000).</summary>
    public const int PageSize = 500;

    /// <summary>
    /// An absolute path in one spelling: a leading slash, single slashes, no trailing one (but the root), "." and ".."
    /// resolved ("/.." stays the root). Only "/" separates: a backslash is part of a name on an SFTP server, as the Host
    /// takes it. Null or blank is the root.
    /// </summary>
    public static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return Root;
        var kept = new List<string>();
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..")
            {
                if (kept.Count > 0) kept.RemoveAt(kept.Count - 1);
                continue;
            }

            kept.Add(part);
        }

        return kept.Count == 0 ? Root : "/" + string.Join('/', kept);
    }

    /// <summary>The folder above, or null at the root.</summary>
    public static string? Parent(string? path)
    {
        var normal = Normalize(path);
        if (normal == Root) return null;
        var cut = normal.LastIndexOf('/');
        return cut <= 0 ? Root : normal[..cut];
    }

    public static bool IsRoot(string? path) => Normalize(path) == Root;

    /// <summary>The path of an entry of a folder.</summary>
    public static string Join(string? folder, string name)
    {
        var normal = Normalize(folder);
        var leaf = (name ?? string.Empty).Trim('/');
        if (leaf.Length == 0) return normal;
        return normal == Root ? "/" + leaf : normal + "/" + leaf;
    }

    /// <summary>The root first, then each folder down to the path, each with the path it opens.</summary>
    public static IReadOnlyList<SiteCrumb> Crumbs(string? path)
    {
        var crumbs = new List<SiteCrumb> { new(Root, Root) };
        var at = Root;
        foreach (var part in Normalize(path).Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            at = Join(at, part);
            crumbs.Add(new SiteCrumb(part, at));
        }

        return crumbs;
    }

    /// <summary>The last part of a path, or "/" for the root.</summary>
    public static string LeafName(string? path)
    {
        var crumbs = Crumbs(path);
        return crumbs[^1].Name;
    }

    /// <summary>
    /// What a starred folder is called in the Starred menu: its name, or with its parent's name when another star has the
    /// same name. The whole path is the tooltip; long paths cut at the menu's edge hid the part that tells them apart.
    /// </summary>
    public static string StarLabel(string star, IReadOnlyCollection<string> stars)
    {
        var leaf = LeafName(star);
        if (stars.Count(other => LeafName(other) == leaf) < 2 || Parent(star) is not { } parent) return leaf;
        return $"{leaf} (in {LeafName(parent)})";
    }

    /// <summary>SITES.5: the "On this PC" words for a <see cref="SiteHereStates"/> value; empty when not compared.</summary>
    public static string HereText(string? here) => here switch
    {
        SiteHereStates.Same => "Same",
        SiteHereStates.OlderHere => "Older here",
        SiteHereStates.SomeOfIt => "Some of it",
        SiteHereStates.NotHere => "Not here",
        SiteHereStates.Different => "Different here",
        // A folder still to be compared shows nothing until the Host answers (sites.here).
        SiteHereStates.FolderHere => "",
        _ => "",
    };

    /// <summary>The column shows for a site with a linked folder on this PC.</summary>
    public static bool ShowsHere(SiteDto? site) => !string.IsNullOrWhiteSpace(site?.LocalFolder);

    /// <summary>A folder shows no size, and neither does a file the server did not give one for (-1).</summary>

    public static string SizeText(long size, bool isFolder)
        => isFolder || size < 0 ? string.Empty : UiFormat.Bytes(size);

    /// <summary>The same moment format as the download list; empty when the server gave none.</summary>
    public static string ModifiedText(DateTimeOffset? modified, DateTime? now = null)
        => modified is { } moment && moment.Year > 1 ? DateText(moment.LocalDateTime, now ?? DateTime.Now) : string.Empty;

    /// <summary>"Nov 05 14:02", or "Nov 05 2025" when the year is not this one. No seconds.</summary>
    public static string DateText(DateTime local, DateTime nowLocal)
        => local.Year == nowLocal.Year
            ? local.ToString("MMM dd HH:mm", CultureInfo.InvariantCulture)
            : local.ToString("MMM dd yyyy", CultureInfo.InvariantCulture);

    /// <summary>The Host sends pages; more wait while what has come is less than the total.</summary>
    public static bool HasMore(long offset, long loaded, long total) => offset + loaded < total;

    /// <summary>Where the next page starts, which is also how many are already here.</summary>
    public static int NextOffset(int offset, int loaded) => offset + loaded;

    public static string CountText(long shown, long total)
    {
        if (total <= 0 && shown <= 0) return "Empty folder";
        if (total > shown) return string.Create(CultureInfo.CurrentCulture, $"Showing {shown:N0} of {total:N0} items");
        var count = Math.Max(shown, total);
        return count == 1 ? "1 item" : string.Create(CultureInfo.CurrentCulture, $"{count:N0} items");
    }

    public static bool IsStarred(IEnumerable<string>? stars, string? path)
    {
        if (stars is null) return false;
        var wanted = Normalize(path);
        return stars.Any(star => Normalize(star) == wanted);
    }

    /// <summary>Starred folders in the order of their paths, each once.</summary>
    public static List<string> OrderedStars(IEnumerable<string>? stars)
        => (stars ?? []).Select(Normalize).Distinct(StringComparer.Ordinal).OrderBy(star => star, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Wording for a failed listing. A host-key problem points to Options > Sites, where the keys are handled.</summary>
    public static string ProblemText(string? problem, string? message, string? offered, IReadOnlyCollection<string>? accepted)
    {
        var host = (message ?? string.Empty).Trim();
        switch (problem)
        {
            case "hostKeyChanged":
                var accepted1 = accepted is { Count: > 0 } ? $" Accepted: {string.Join(", ", accepted)}." : string.Empty;
                var offered1 = string.IsNullOrWhiteSpace(offered) ? string.Empty : $" The server now offers {offered}.";
                return "This server's host key is not the one you accepted, so Inbound did not connect. That can mean the server was reinstalled, or that someone is intercepting the connection." +
                    offered1 + accepted1 + " Check with the server's owner, then replace the key in Options > Sites.";
            case "hostKeyNotAccepted":
                return "This site's host key has not been accepted yet. Open Options > Sites to check it and accept it.";
            case "off":
                return "Sites is off. Turn it on in Options > Sites.";
            case "certificateNotTrusted":
                return "This server's certificate is not signed by an authority this PC trusts, so Inbound did not connect. If it is your own server, open Options > Sites and Test the site to see the certificate and choose whether to trust it.";
            case "signInNeeded":
                return "This site is not signed in, or the service no longer accepts its sign-in. Open Options > Sites and choose Sign in.";
            default:
                return host.Length > 0 ? host : "The folder could not be read.";
        }
    }

    /// <summary>A problem the user fixes in Options > Sites, so the view offers to open it.</summary>
    public static bool PointsToOptions(string? problem) => problem is "hostKeyChanged" or "hostKeyNotAccepted" or "certificateNotTrusted" or "signInNeeded" or "off";

    // ---- R2a: the pane's chip, sorting, filter, footers and the trouble state ----------------------------

    /// <summary>The kind chip beside the site's name: "SFTP", "FTP", "WebDAV".</summary>
    public static string KindChipText(string? kind) => kind switch
    {
        null or "" => string.Empty,
        SiteKindCatalog.Sftp => "SFTP",
        SiteKindCatalog.Ftp => "FTP",
        SiteKindCatalog.WebDav => "WebDAV",
        SiteKindCatalog.S3 => "S3",
        SiteKindCatalog.Drive => "Drive",
        SiteKindCatalog.OneDrive => "OneDrive",
        SiteKindCatalog.Dropbox => "Dropbox",
        _ => kind.ToUpperInvariant(),
    };

    public const string SortName = "name";
    public const string SortSize = "size";
    public const string SortModified = "modified";
    public const string SortHere = "here";

    /// <summary>What a pane needs of a line to put it in order.</summary>
    internal readonly record struct SortItem(string Name, bool IsFolder, long Size, DateTimeOffset? Modified, string? Here);

    /// <summary>Folders first whatever the direction; then the column; equal lines by name.</summary>
    public static int CompareItems(SortItem a, SortItem b, string key, bool ascending)
    {
        if (a.IsFolder != b.IsFolder) return a.IsFolder ? -1 : 1;
        var byColumn = key switch
        {
            SortSize => a.Size.CompareTo(b.Size),
            SortModified => (a.Modified ?? DateTimeOffset.MinValue).CompareTo(b.Modified ?? DateTimeOffset.MinValue),
            SortHere => HereRank(a.Here).CompareTo(HereRank(b.Here)),
            _ => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name),
        };
        if (!ascending) byColumn = -byColumn;
        if (byColumn != 0) return byColumn;
        var byName = StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
        return byName != 0 ? byName : StringComparer.Ordinal.Compare(a.Name, b.Name);
    }

    private static int HereRank(string? here) => here switch
    {
        SiteHereStates.NotHere => 1,
        SiteHereStates.SomeOfIt => 2,
        SiteHereStates.OlderHere => 3,
        SiteHereStates.Different => 4,
        SiteHereStates.Same => 5,
        _ => 0,
    };

    /// <summary>A click on a column: the same column turns the order round, another starts ascending.</summary>
    public static (string Key, bool Ascending) NextSort(string currentKey, bool currentAscending, string clickedKey)
        => currentKey == clickedKey ? (currentKey, !currentAscending) : (clickedKey, true);

    /// <summary>The Filter box: part of a name, without regard to case; empty keeps everything.</summary>
    public static bool FilterMatches(string name, string? filter)
        => string.IsNullOrWhiteSpace(filter) || name.Contains(filter.Trim(), StringComparison.CurrentCultureIgnoreCase);

    /// <summary>"Same", "older" or "other": the colour of an "On this PC" text.</summary>
    public static string HereTone(string? here) => here switch
    {
        SiteHereStates.Same => "same",
        SiteHereStates.OlderHere => "older",
        _ => "other",
    };

    /// <summary>
    /// The server pane's footer. Selected: "1 of 6 selected · 5.8 GB · 2 already on this PC"; in Server only the last part
    /// says where downloads go. Nothing selected: "6 items · 2 already on this PC". The ".." line is never counted by the caller.
    /// </summary>
    public static string ServerFooter(int shown, long total, int selected, long selectedBytes, int sameCount, bool linked, bool serverOnly, string? linkedFolder)
    {
        var parts = new List<string>();
        if (selected > 0)
        {
            parts.Add(string.Create(CultureInfo.CurrentCulture, $"{selected:N0} of {shown:N0} selected"));
            if (selectedBytes > 0) parts.Add(UiFormat.Bytes(selectedBytes));
            if (serverOnly)
                parts.Add(linked && !string.IsNullOrWhiteSpace(linkedFolder) ? $"downloads go to {linkedFolder}" : "downloads go by your category rules");
            else if (linked)
                parts.Add($"{sameCount:N0} already on this PC");
        }
        else
        {
            parts.Add(CountText(shown, total));
            if (linked && shown > 0) parts.Add($"{sameCount:N0} already on this PC");
        }

        return string.Join(" · ", parts);
    }

    /// <summary>One line of a pane as the footer and the actions see it; <paramref name="IsParent"/> is the ".." line.</summary>
    internal readonly record struct PaneLine(string Name, bool IsFolder, long Size, string? Here = null, bool IsParent = false);

    /// <summary>What a pane's footer counts: the lines shown, what is selected and its bytes, "Same" lines, files and folders.</summary>
    internal readonly record struct FooterCount(int Shown, int Selected, long SelectedBytes, int Same, int Files, int Folders, long Bytes);

    /// <summary>The ".." line (and a "." or ".." name, which no folder really holds) is never an item to act on.</summary>
    public static bool IsActionable(PaneLine line) => !line.IsParent && line.Name is not ("" or "." or "..");

    /// <summary>
    /// The selected lines a transfer, rename or delete gets: never the ".." line, each name once, in the order selected.
    /// Rename takes it only when exactly one is left.
    /// </summary>
    public static List<PaneLine> SelectedForAction(IEnumerable<PaneLine> selected)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return [.. selected.Where(line => IsActionable(line) && seen.Add(line.Name))];
    }

    /// <summary>The numbers a footer shows. The ".." line is never counted, shown or selected; only files count bytes.</summary>
    public static FooterCount FooterCounts(IEnumerable<PaneLine> shown, IEnumerable<PaneLine> selected)
    {
        var lines = shown.Where(IsActionable).ToList();
        var picked = SelectedForAction(selected);
        var files = lines.Count(line => !line.IsFolder);
        return new FooterCount(
            lines.Count,
            picked.Count,
            picked.Where(line => !line.IsFolder && line.Size > 0).Sum(line => line.Size),
            lines.Count(line => line.Here == SiteHereStates.Same),
            files,
            lines.Count - files,
            lines.Where(line => !line.IsFolder && line.Size > 0).Sum(line => line.Size));
    }

    /// <summary>The This PC pane's footer: "3 files and 1 folder · 5.5 GB", or "1 of 4 selected · 3.4 GB".</summary>
    public static string LocalFooter(int files, int folders, long bytes, int selected, long selectedBytes)
    {
        var parts = new List<string>();
        if (selected > 0)
        {
            parts.Add(string.Create(CultureInfo.CurrentCulture, $"{selected:N0} of {files + folders:N0} selected"));
            if (selectedBytes > 0) parts.Add(UiFormat.Bytes(selectedBytes));
        }
        else
        {
            if (files + folders == 0) return "Empty folder";
            parts.Add(CountPhrase(files, folders));
            if (bytes > 0) parts.Add(UiFormat.Bytes(bytes));
        }

        return string.Join(" · ", parts);
    }

    /// <summary>"3 files and 1 folder", "1 file", "2 folders".</summary>
    public static string CountPhrase(int files, int folders)
    {
        var f = files == 1 ? "1 file" : string.Create(CultureInfo.CurrentCulture, $"{files:N0} files");
        var d = folders == 1 ? "1 folder" : string.Create(CultureInfo.CurrentCulture, $"{folders:N0} folders");
        return files > 0 && folders > 0 ? $"{f} and {d}" : files > 0 ? f : d;
    }

    /// <summary>"Linked folders: D:\Downloads\ISOs follows /media/isos"; null when the site has no linked folder.</summary>
    public static string? LinkedLine(string? localFolder, string? remoteFolder)
        => string.IsNullOrWhiteSpace(localFolder) ? null : $"Linked folders: {localFolder} follows {Normalize(remoteFolder)}";

    /// <summary>
    /// A listing that failed for no named reason is a server that did not answer, so the pane tries again by itself. A
    /// problem the user fixes (host key, sign-in, certificate, Sites off) or a listing that changed is not.
    /// </summary>
    public static bool IsCantReach(string? problem)
        => problem == SiteProblems.CantReach;

    /// <summary>Seconds before the next automatic try: 10, 30, 60, then 60 again. <paramref name="attempt"/> counts failures from 0.</summary>
    public static int RetryDelaySeconds(int attempt) => attempt <= 0 ? 10 : attempt == 1 ? 30 : 60;

    /// <summary>Automatic tries of one trouble spell: at most this many, and none after <see cref="MaxAutoRetryTime"/>.</summary>
    public const int MaxAutoTries = 5;

    public static readonly TimeSpan MaxAutoRetryTime = TimeSpan.FromMinutes(10);

    /// <summary>Another automatic try is allowed after <paramref name="triesDone"/> of them, <paramref name="elapsed"/> into the spell.</summary>
    public static bool AutoRetryAllowed(int triesDone, TimeSpan elapsed) => triesDone < MaxAutoTries && elapsed < MaxAutoRetryTime;

    /// <summary>"Trying again at 14:32." in local time: one timer for the try, no ticking countdown (idle wake-ups).</summary>
    public static string RetryAtText(DateTime localAt) => $"Trying again at {localAt.ToString("HH:mm", CultureInfo.InvariantCulture)}.";

    public const string NotRetryingText = "Not trying again on its own; choose Retry now.";

    /// <summary>
    /// What the browse view's connection depends on. When this changes while the folder can't be read, the change may be
    /// the fix (Edit site, a new key, a sign-in), so the folder is read again; a rename, a star or a reach change is not.
    /// A new password on a site that had one already does not show here (the Host never sends it); Retry now covers that.
    /// </summary>
    public static string ConnectionKey(SiteDto? site)
    {
        if (site is null) return string.Empty;
        return string.Join('\n',
            site.Kind, site.Host, site.Port.ToString(CultureInfo.InvariantCulture), site.User, site.AuthMethod,
            site.HasPassword, site.HasPrivateKey, site.HasKeyPassphrase, Normalize(site.RemoteFolder), site.Url ?? "", site.Endpoint ?? "",
            site.Tls ?? "", site.Provider ?? "", site.Region ?? "", site.ClientId ?? "", site.HasClientSecret, site.SignedIn,
            string.Join(',', site.HostKeys.Select(key => key.Fingerprint)), site.TrustedCertificates.Count.ToString(CultureInfo.InvariantCulture));
    }

    public static string TroubleTitle(string? problem, string siteName)
        => IsCantReach(problem) ? $"Can't reach {siteName}" : $"Can't connect to {siteName}";

    /// <summary>The line under the title: the Host's own words (or the known words for a problem the user fixes).</summary>
    public static string TroubleWhy(string? problem, string? message, string? offered, IReadOnlyCollection<string>? accepted)
    {
        var words = ProblemText(problem, message, offered, accepted);
        return string.IsNullOrWhiteSpace(message) && IsCantReach(problem) ? "The server did not answer." : words;
    }

    /// <summary>"Last listed 2 min ago" for the footer of the trouble state.</summary>
    public static string LastListedText(DateTimeOffset listed, DateTimeOffset now)
    {
        var age = now - listed;
        if (age < TimeSpan.FromMinutes(1)) return "Last listed just now";
        if (age < TimeSpan.FromHours(1)) return $"Last listed {(int)age.TotalMinutes} min ago";
        if (age < TimeSpan.FromDays(1)) return $"Last listed {(int)age.TotalHours} h ago";
        return $"Last listed {(int)age.TotalDays} d ago";
    }
}

/// <summary>
/// SITES.11: the "On this PC" answers of sites.here, kept per site, folder and name for <see cref="Keep"/>. A reload after
/// a rename, a new folder, a delete or an automatic retry reuses them instead of walking the folders again; F5 and opening
/// the folder anew forget the folder's answers first.
/// </summary>
internal sealed class SiteHereCache
{
    public static readonly TimeSpan Keep = TimeSpan.FromMinutes(2);

    private readonly Dictionary<(string Site, string Path, string Name), (string Here, DateTimeOffset At)> _answers = [];

    public int Count => _answers.Count;

    public bool TryGet(string site, string path, string name, DateTimeOffset now, out string here)
    {
        var key = (site, SitesBrowseRules.Normalize(path), name);
        if (_answers.TryGetValue(key, out var answer) && now - answer.At < Keep && now >= answer.At)
        {
            here = answer.Here;
            return true;
        }

        _answers.Remove(key);
        here = string.Empty;
        return false;
    }

    public void Put(string site, string path, string name, string here, DateTimeOffset now)
    {
        // Old answers go when the store grows, so it never keeps more than a few folders' worth.
        if (_answers.Count >= 2000)
        {
            foreach (var old in _answers.Where(pair => now - pair.Value.At >= Keep).Select(pair => pair.Key).ToList()) _answers.Remove(old);
        }

        _answers[(site, SitesBrowseRules.Normalize(path), name)] = (here, now);
    }

    /// <summary>F5 or the folder opened anew: its answers are asked for again.</summary>
    public void Forget(string site, string path)
    {
        var folder = SitesBrowseRules.Normalize(path);
        foreach (var key in _answers.Keys.Where(key => key.Site == site && key.Path == folder).ToList()) _answers.Remove(key);
    }
}
