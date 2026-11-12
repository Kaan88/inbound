namespace DownloadManager.Core;

/// <summary>
/// T14 (owner, 2026-11-12): where a torrent goes when no folder was typed. Torrents have their own category folders,
/// apart from other downloads', and each kind can be turned off by itself.
/// <list type="bullet">
/// <item>A torrent folder set (<see cref="AppSettings.TorrentCategoryFoldersRoot"/>): with torrent category folders on,
/// the folder for the torrent's category under it (Torrents\Video); off, or a torrent with no category, the torrent
/// folder itself. It stays out of the download folder either way, so turning the subfolders off doesn't mix torrents in
/// with other downloads.</item>
/// <item>No torrent folder (settings from before T14, or the user cleared it): the same folders as other downloads.
/// The category's folder for other downloads while both switches are on (other downloads only use it while
/// <see cref="AppSettings.UseCategoryFolders"/> is on, so "the same folders" stays true); else the download folder.</item>
/// </list>
/// With a torrent folder, <see cref="AppSettings.UseCategoryFolders"/> is not looked at: it is for other downloads only.
/// </summary>
public static class TorrentFolderRules
{
    /// <summary>The torrent folder's name under the download folder on a new install.</summary>
    public const string FolderName = "Torrents";

    /// <summary>The built-in categories and their folders' names, as under the download folder.</summary>
    public static readonly IReadOnlyList<(string Id, string Name)> BuiltInFolders =
    [
        ("compressed", "Compressed"), ("documents", "Documents"), ("music", "Music"),
        ("programs", "Programs"), ("video", "Video"),
    ];

    public static string DefaultRoot(string defaultDownloadDirectory) => Path.Combine(defaultDownloadDirectory, FolderName);

    /// <summary>The folder a torrent of <paramref name="categoryId"/> goes to, as a full path.</summary>
    /// <param name="useLastFolder">Only while torrents share the other downloads' folders: the last folder used comes before the download folder, as for other downloads.</param>
    public static string Resolve(AppSettings settings, string? categoryId, bool useLastFolder = false)
    {
        var category = categoryId ?? "other";
        if (string.IsNullOrWhiteSpace(settings.TorrentCategoryFoldersRoot))
        {
            if (settings.UseTorrentCategoryFolders && settings.UseCategoryFolders && CategoryFolder(settings, category) is { } shared) return Path.GetFullPath(shared);
            return Path.GetFullPath((useLastFolder ? settings.LastDestinationDirectory : null) ?? settings.DefaultDownloadDirectory);
        }

        var root = Path.GetFullPath(settings.TorrentCategoryFoldersRoot.Trim());
        if (!settings.UseTorrentCategoryFolders || SubfolderName(settings, category) is not { } name) return root;
        return Path.Combine(root, name);
    }

    /// <summary>
    /// Where a torrent the provider downloaded goes with no folder named. With no torrent folder, the download folder,
    /// as for the provider's other downloads and for its torrents before T14, so an upgrade moves nothing.
    /// </summary>
    public static string ResolveForProvider(AppSettings settings, string? categoryId)
        => string.IsNullOrWhiteSpace(settings.TorrentCategoryFoldersRoot)
            ? Path.GetFullPath(settings.DefaultDownloadDirectory)
            : Resolve(settings, categoryId);

    /// <summary>The folder other downloads of <paramref name="category"/> go to: a custom category's own, else the built-in one's. Null when it has none.</summary>
    public static string? CategoryFolder(AppSettings settings, string category)
    {
        var custom = settings.CustomCategories.FirstOrDefault(item => item.Id == category)?.DestinationDirectory;
        if (!string.IsNullOrWhiteSpace(custom)) return custom.Trim();
        return settings.CategoryFolders.TryGetValue(category, out var folder) && !string.IsNullOrWhiteSpace(folder) ? folder.Trim() : null;
    }

    /// <summary>The name of a category's folder under the torrent folder; null for "other" and an unknown category.</summary>
    public static string? SubfolderName(AppSettings settings, string category)
    {
        var builtIn = BuiltInFolders.FirstOrDefault(item => string.Equals(item.Id, category, StringComparison.OrdinalIgnoreCase)).Name;
        var name = builtIn ?? settings.CustomCategories.FirstOrDefault(item => item.Id == category)?.Name;
        if (string.IsNullOrWhiteSpace(name)) return null;
        var safe = FileNameResolver.Sanitize(name.Trim());
        return string.IsNullOrWhiteSpace(safe) || safe is "." or ".." ? null : safe;
    }
}
