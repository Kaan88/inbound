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

    /// <summary>The most files a folder's comparison reads; a bigger folder is not compared.</summary>
    public const int MaxFolderFiles = 20_000;

    /// <summary>The most folders, the folder itself included, a folder's comparison lists.</summary>
    public const int MaxFolderFolders = 2_000;

    /// <summary>
    /// SITES.11 (owner, 2026-11-16: "build"): a folder on the server compared, file by file, with the same folder here.
    /// <paramref name="files"/> are every file under it on the server, their paths relative to it (<c>/</c>-separated).
    /// Null when the folder is not compared (no place here, or not a folder here). The rules are <see cref="FolderTally"/>'s.
    /// </summary>
    public static string? OfFolder(string? localFolder, string remoteRoot, string parent, string name,
        IEnumerable<(string Path, long Size, DateTimeOffset? Modified)> files)
    {
        if (LocalPath(localFolder, remoteRoot, parent, name) is not { } local || !Directory.Exists(local)) return null;
        var folder = SitePaths.Normalize(parent == "/" ? "/" + name : parent + "/" + name);
        if (folder is null) return null;
        var tally = new FolderTally();
        foreach (var (path, size, modified) in files)
        {
            var slash = path.LastIndexOf('/');
            var at = slash < 0 ? folder : folder + "/" + path[..slash];
            tally.Add(Of(localFolder, remoteRoot, at, new SiteItemDto { Name = path[(slash + 1)..], Size = size, Modified = modified }));
        }

        return tally.Result;
    }

    /// <summary>
    /// The files of one folder on the server, counted by their state here. Same: every file is here and the same (an empty
    /// folder too). Older here: all here, none cut short or different, some older. Not here: none is here. Some of it:
    /// anything between. Files only here are not counted; a file that can't be read here counts as not here.
    /// </summary>
    public sealed class FolderTally
    {
        private int _count, _same, _older, _missing;

        public void Add(string? state)
        {
            _count++;
            switch (state)
            {
                case SiteHereStates.Same: _same++; break;
                case SiteHereStates.OlderHere: _older++; break;
                case SiteHereStates.NotHere or null: _missing++; break;
            }
        }

        /// <summary>True when no further file can change the answer: it is Some of it.</summary>
        public bool Settled => (_missing > 0 && _missing < _count) || _count > _same + _older + _missing;

        public string Result =>
            _same == _count ? SiteHereStates.Same
            : _missing == _count ? SiteHereStates.NotHere
            : _missing == 0 && _same + _older == _count ? SiteHereStates.OlderHere
            : SiteHereStates.SomeOfIt;
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
