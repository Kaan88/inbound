using System.Globalization;

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

    /// <summary>A folder shows no size, and neither does a file the server did not give one for (-1).</summary>
    public static string SizeText(long size, bool isFolder)
        => isFolder || size < 0 ? string.Empty : UiFormat.Bytes(size);

    /// <summary>The same moment format as the download list; empty when the server gave none.</summary>
    public static string ModifiedText(DateTimeOffset? modified)
        => modified is { } moment && moment.Year > 1 ? UiFormat.Moment(moment) : string.Empty;

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
            default:
                return host.Length > 0 ? host : "The folder could not be read.";
        }
    }

    /// <summary>A problem the user fixes in Options > Sites, so the view offers to open it.</summary>
    public static bool PointsToOptions(string? problem) => problem is "hostKeyChanged" or "hostKeyNotAccepted" or "off";
}
