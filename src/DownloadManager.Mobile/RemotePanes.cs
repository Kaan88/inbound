using System.Globalization;
using DownloadManager.Contracts;

namespace DownloadManager.Mobile;

/// <summary>How a pane orders its rows (r5: Name, Newest first, Largest first). Folders always come first.</summary>
public enum RemoteSort { Name, Newest, Largest }

/// <summary>The row text and ordering rules of the Remote Files redesign (mockups r1 to r6).</summary>
public static class RemoteFileList
{
    /// <summary>Folders first (by name), then files in the chosen order; ties fall back to the name.</summary>
    public static List<SiteItemDto> Sort(IEnumerable<SiteItemDto> items, RemoteSort sort)
    {
        var all = items.ToList();
        var folders = all.Where(item => item.IsFolder).OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Name, StringComparer.Ordinal);
        IEnumerable<SiteItemDto> files = all.Where(item => !item.IsFolder);
        files = sort switch
        {
            RemoteSort.Newest => files.OrderByDescending(item => item.Modified.HasValue).ThenByDescending(item => item.Modified)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase),
            RemoteSort.Largest => files.OrderByDescending(item => item.Size >= 0).ThenByDescending(item => item.Size)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase),
            _ => files.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.Name, StringComparer.Ordinal),
        };
        return [.. folders, .. files];
    }

    /// <summary>A size as the mockup rows write it: "24 MB", "3.2 MB" (no trailing ".0").</summary>
    public static string Size(long bytes) => Fmt.Bytes(bytes).Replace(".0 ", " ", StringComparison.Ordinal);

    /// <summary>"17 May" in the current year, "21 Dec 2025" in another: a row never hides the year it needs (owner item 9).</summary>
    public static string Date(DateTimeOffset when, DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        if (now - when is { } age && age >= TimeSpan.Zero && age < TimeSpan.FromMinutes(5)) return "Just now";
        var local = TimeZoneInfo.ConvertTime(when, zone ?? TimeZoneInfo.Local);
        var today = TimeZoneInfo.ConvertTime(now, zone ?? TimeZoneInfo.Local);
        return local.ToString(local.Year == today.Year ? "d MMM" : "d MMM yyyy", CultureInfo.InvariantCulture);
    }

    /// <summary>The second line of a row: "Folder · 24 items", or "24 MB · 17 May".</summary>
    public static string Detail(SiteItemDto item, DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        if (item.IsFolder)
            return item.ChildCount is int count ? $"Folder · {count:N0} {(count == 1 ? "item" : "items")}" : "Folder";
        var parts = new List<string> { item.Size >= 0 ? Size(item.Size) : "Size unknown" };
        if (item.Modified is { } modified) parts.Add(Date(modified, now, zone));
        return string.Join(" · ", parts);
    }

    /// <summary>The bottom bar's first line: "2 files · 800 MB", "1 folder", "1 folder · 2 files · 3 MB".</summary>
    public static string Summary(IReadOnlyCollection<SiteItemDto> selected)
    {
        int folders = selected.Count(item => item.IsFolder);
        var files = selected.Where(item => !item.IsFolder).ToArray();
        var parts = new List<string>();
        if (folders > 0) parts.Add(folders + (folders == 1 ? " folder" : " folders"));
        if (files.Length > 0)
        {
            parts.Add(files.Length + (files.Length == 1 ? " file" : " files"));
            long bytes = files.Where(item => item.Size >= 0).Sum(item => item.Size);
            bool unknown = files.Any(item => item.Size < 0);
            if (!unknown || bytes > 0) parts.Add(Size(bytes) + (unknown ? " + unknown" : ""));
            else parts.Add("size unknown");
        }
        return parts.Count == 0 ? "Nothing selected" : string.Join(" · ", parts);
    }

    /// <summary>"Home NAS/media/recordings" from a location name and a path below its root; the root alone is just the name.</summary>
    public static string Destination(string name, string path)
    {
        var trimmed = path.Trim('/');
        return trimmed.Length == 0 ? name : name + "/" + trimmed;
    }
}

/// <summary>One chip of the breadcrumb strip.</summary>
public sealed record RemoteCrumb(string Label, string Path);

public static class RemoteCrumbs
{
    /// <summary>
    /// The chips for <paramref name="path"/>, starting at a root chip. A site's chips never reach above its configured start
    /// folder (review R3): a path outside the root is shown from the root.
    /// </summary>
    public static List<RemoteCrumb> Build(string rootLabel, string rootPath, string path)
    {
        string root = Normalize(rootPath);
        string current = Normalize(path);
        var crumbs = new List<RemoteCrumb> { new(rootLabel, root) };
        if (!IsUnder(root, current) || current == root) return crumbs;
        string rest = root == "/" ? current[1..] : current[(root.Length + 1)..];
        string walk = root;
        foreach (var segment in rest.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            walk = walk == "/" ? "/" + segment : walk + "/" + segment;
            crumbs.Add(new RemoteCrumb(segment, walk));
        }
        return crumbs;
    }

    /// <summary>The folder above <paramref name="path"/>, or null when it is the root (or outside it).</summary>
    public static string? Parent(string rootPath, string path)
    {
        string root = Normalize(rootPath), current = Normalize(path);
        if (current == root || !IsUnder(root, current)) return null;
        int slash = current.LastIndexOf('/');
        string parent = slash <= 0 ? "/" : current[..slash];
        return IsUnder(root, parent) ? parent : root;
    }

    /// <summary>The label of a site's root chip: its start folder's name, or the service for a top folder.</summary>
    public static string RootLabel(SiteDto site)
    {
        var folder = Normalize(site.RemoteFolder);
        if (folder != "/") return folder[(folder.LastIndexOf('/') + 1)..];
        return site.Kind switch
        {
            SiteKindCatalog.Drive => "My Drive",
            SiteKindCatalog.OneDrive => "OneDrive",
            SiteKindCatalog.Dropbox => "Dropbox",
            _ => RemoteLocationText.DisplayName(site),
        };
    }

    public static string Normalize(string? path)
    {
        var segments = (path ?? "").Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 0 ? "/" : "/" + string.Join("/", segments);
    }

    private static bool IsUnder(string root, string path) =>
        root == "/" || path == root || path.StartsWith(root + "/", StringComparison.Ordinal);

    /// <summary>The phone pane's chips: "Phone", then "Download" and its folders (<c>Download/Inbound</c>).</summary>
    public static List<RemoteCrumb> BuildPhone(string path)
    {
        var crumbs = new List<RemoteCrumb> { new("Phone", "") };
        string walk = "";
        foreach (var segment in (path ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            walk = walk.Length == 0 ? segment : walk + "/" + segment;
            crumbs.Add(new RemoteCrumb(segment, walk));
        }
        return crumbs;
    }
}

/// <summary>How a location is named and described in the lists and the pane picker (owner items 1 and 2).</summary>
public static class RemoteLocationText
{
    public static string ServiceName(string kind) => kind.ToLowerInvariant() switch
    {
        SiteKindCatalog.Drive => "Google Drive",
        SiteKindCatalog.OneDrive => "OneDrive",
        SiteKindCatalog.Dropbox => "Dropbox",
        SiteKindCatalog.WebDav => "WebDAV",
        SiteKindCatalog.S3 => "S3",
        SiteKindCatalog.B2 => "Backblaze B2",
        SiteKindCatalog.Sftp => "SFTP",
        SiteKindCatalog.Ftp => "FTP",
        var other => other.ToUpperInvariant(),
    };

    /// <summary>
    /// The name to show. A cloud location still carrying the bare rclone remote name ("drive", "onedrive") shows the service;
    /// a name the user chose is kept as typed.
    /// </summary>
    public static string DisplayName(SiteDto site)
    {
        string name = site.Name.Trim();
        if (SiteKindCatalog.SignsIn(site.Kind) && (name.Length == 0 || name.Equals(site.Kind, StringComparison.OrdinalIgnoreCase)
            || name.Equals("gdrive", StringComparison.OrdinalIgnoreCase)))
            return ServiceName(site.Kind);
        return name.Length == 0 ? ServiceName(site.Kind) : name;
    }
}

/// <summary>What a bottom-bar transfer between the two panes is (r1, r4, r6).</summary>
public enum RemoteTransferMode
{
    /// <summary>A location to this phone.</summary>
    Download,

    /// <summary>This phone to a location.</summary>
    Upload,

    /// <summary>One location to another, relayed by the Host's rclone (r6); an older Host lacks it.</summary>
    LocationToLocation,

    /// <summary>The phone on both sides: nothing to copy.</summary>
    None,
}

public static class RemoteTransfers
{
    public static RemoteTransferMode Mode(bool sourceIsPhone, bool targetIsPhone) =>
        sourceIsPhone ? targetIsPhone ? RemoteTransferMode.None : RemoteTransferMode.Upload
        : targetIsPhone ? RemoteTransferMode.Download : RemoteTransferMode.LocationToLocation;

    /// <summary>
    /// The phone pane's path ("", "Download", "Download/Inbound") as a folder under the phone's Download folder, or null
    /// when it is not inside Download (the phone's root holds nothing a transfer can use).
    /// </summary>
    public static string? DownloadRelative(string phonePath)
    {
        var segments = (phonePath ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments[0] != "Download") return null;
        return string.Join("/", segments.Skip(1));
    }

    /// <summary>
    /// r6: the selection in <paramref name="sourceFolder"/> of one location goes to <paramref name="targetFolder"/> of the
    /// other, straight from one to the other through the Host (<see cref="SiteTransferStartRequest.RemoteCopyCapability"/>).
    /// Files already at the target are skipped, as the pane's other transfers do.
    /// </summary>
    public static SiteTransferStartRequest Copy(string sourceSiteId, string sourceFolder, IEnumerable<SiteItemDto> items,
        string targetSiteId, string targetFolder, string conflict = RemoteConflicts.Skip) => new()
    {
        SiteId = sourceSiteId,
        Direction = "between",
        RemoteFolder = RemoteCrumbs.Normalize(sourceFolder),
        TargetSiteId = targetSiteId,
        TargetFolder = RemoteCrumbs.Normalize(targetFolder),
        Conflict = conflict,
        Names = [.. items.Select(item => new SiteTransferNameDto { Name = item.Name, IsFolder = item.IsFolder })],
    };

    /// <summary>A Transfers row's title: "download · NAS", or "copy · Google Drive → Home NAS" for a copy (r6).</summary>
    public static string Title(SiteTransferDto transfer) =>
        (transfer.Direction == "between" ? "copy" : transfer.Direction) + " · " + transfer.SiteName
        + (transfer.Direction == "between" && !string.IsNullOrEmpty(transfer.TargetSiteName) ? " → " + transfer.TargetSiteName : "");

    /// <summary>The caption under a phone destination, "Download/Inbound/Music".</summary>
    public static string PhoneLabel(string phonePath)
    {
        var relative = DownloadRelative(phonePath);
        return relative is null ? "Phone" : relative.Length == 0 ? "Download" : "Download/" + relative;
    }
}

/// <summary>
/// The SFTP host key question (review R1 and M1). First: nothing trusted yet. MoreKeys: every key we trusted is still offered and the
/// server offers more (the phone used to save only the first, so rclone could negotiate one it did not know). Changed: a trusted
/// key's type is now offered with another fingerprint, or none of the trusted keys is offered; that is shown as a warning.
/// Unchanged: every offered key is already trusted.
/// </summary>
public enum SftpKeyCase { First, MoreKeys, Changed, Unchanged }

public sealed record SftpKeyReview(SftpKeyCase Case, IReadOnlyList<SiteHostKeyDto> Offered, IReadOnlyList<SiteHostKeyDto> Trusted)
{
    public static SftpKeyReview Compare(IReadOnlyList<SiteHostKeyDto> trusted, IReadOnlyList<SiteHostKeyDto> offered)
    {
        if (trusted.Count == 0) return new(SftpKeyCase.First, offered, trusted);
        bool Known(SiteHostKeyDto key) => trusted.Any(old => old.Fingerprint == key.Fingerprint);
        bool conflict = offered.Any(key => !Known(key) && trusted.Any(old => string.Equals(old.Type, key.Type, StringComparison.OrdinalIgnoreCase)));
        bool matches = offered.Any(Known);
        if (conflict || !matches) return new(SftpKeyCase.Changed, offered, trusted);
        return new(offered.All(Known) ? SftpKeyCase.Unchanged : SftpKeyCase.MoreKeys, offered, trusted);
    }

    /// <summary>The <c>host_keys</c> entries to save: every key the server offers, once each (the Host accepts up to 16).</summary>
    public IReadOnlyList<string> Entries =>
        [.. Offered.Select(key => key.Entry).Where(entry => entry.Length > 0).Distinct(StringComparer.Ordinal).Take(16)];

    /// <summary>A fingerprint broken into two even lines, so it never wraps with a stray last character.</summary>
    public static string FingerprintLines(string fingerprint)
    {
        if (fingerprint.Length <= 24) return fingerprint;
        int half = (fingerprint.Length + 1) / 2;
        return fingerprint[..half] + "\n" + fingerprint[half..];
    }
}

/// <summary>
/// What a transfer does when a name is already at the target, asked once per batch and only when the target's loaded
/// listing shows a clash (owner, 2026-11-22). The values are the Host's conflict names.
/// </summary>
public static class RemoteConflicts
{
    public const string Skip = "skip";
    public const string Replace = "replace";
    public const string KeepBoth = "keepBoth";

    /// <summary>
    /// The selected items (top level) whose names are already in <paramref name="targetListing"/>. A null listing (none loaded,
    /// or the target is not one a pane lists) means no check: nothing clashes. Names compare ignoring case, so a doubtful
    /// match asks rather than overwrites.
    /// </summary>
    public static List<SiteItemDto> Clashes(IEnumerable<SiteItemDto> selected, IEnumerable<SiteItemDto>? targetListing)
    {
        if (targetListing is null) return [];
        var existing = new HashSet<string>(targetListing.Select(item => item.Name), StringComparer.OrdinalIgnoreCase);
        return [.. selected.Where(item => existing.Contains(item.Name))];
    }

    /// <summary>
    /// "3 files already exist in Home NAS/photos", "report.pdf already exists in ...", "2 folders already exist in ...",
    /// "4 items already exist in ..." (files and folders mixed).
    /// </summary>
    public static string Title(IReadOnlyList<SiteItemDto> clashing, string place)
    {
        if (clashing.Count == 1) return $"{clashing[0].Name} already exists in {place}";
        int folders = clashing.Count(item => item.IsFolder);
        string noun = folders == 0 ? "files" : folders == clashing.Count ? "folders" : "items";
        return $"{clashing.Count} {noun} already exist in {place}";
    }
}
