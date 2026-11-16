using DownloadManager.Contracts;

namespace DownloadManager.Sites;

/// <summary>
/// SITES.5: the "On this PC" column (remote-sites.md, "Transfers"): how an item on the server compares with the same
/// place under the site's linked folder on this PC. The site's start folder on the server matches the linked folder;
/// anything outside the start folder is not compared. Name, size and time only, never contents.
/// </summary>
public static class SiteHere
{
    /// <summary>Times this close count as equal: FAT and some servers keep only even seconds.</summary>
    public static readonly TimeSpan Slack = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The item's place on this PC, or null when it has none: no linked folder, a folder shown outside the site's start
    /// folder, or a name this PC can't hold.
    /// </summary>
    public static string? LocalPath(string? localFolder, string remoteRoot, string folder, string name)
    {
        if (string.IsNullOrEmpty(localFolder) || !Path.IsPathFullyQualified(localFolder)) return null;
        if (SitePaths.Normalize(remoteRoot) is not { } root || SitePaths.Normalize(folder) is not { } shown) return null;
        string inside;
        if (shown == root) inside = "";
        else if (root == "/") inside = shown[1..];
        else if (shown.StartsWith(root + "/", StringComparison.Ordinal)) inside = shown[(root.Length + 1)..];
        else return null;
        var relative = inside.Length == 0 ? name : inside + "/" + name;
        if (SiteTransferPaths.LocalPathProblem(relative) is not null) return null;
        return SiteTransferPaths.Local(localFolder, relative);
    }

    /// <summary>One of <see cref="SiteHereStates"/> for the item, or null when it is not compared.</summary>
    public static string? Of(string? localFolder, string remoteRoot, string folder, SiteItemDto item)
    {
        if (LocalPath(localFolder, remoteRoot, folder, item.Name) is not { } local) return null;
        try
        {
            if (item.IsFolder)
                return Directory.Exists(local) ? SiteHereStates.FolderHere : File.Exists(local) ? SiteHereStates.Different : SiteHereStates.NotHere;
            var file = new FileInfo(local);
            if (!file.Exists) return Directory.Exists(local) ? SiteHereStates.Different : SiteHereStates.NotHere;
            var older = item.Modified is { } remote && file.LastWriteTimeUtc < remote.UtcDateTime - Slack;
            if (item.Size < 0 || file.Length == item.Size) return older ? SiteHereStates.OlderHere : SiteHereStates.Same;
            if (older) return SiteHereStates.OlderHere;
            return file.Length < item.Size ? SiteHereStates.SomeOfIt : SiteHereStates.Different;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
