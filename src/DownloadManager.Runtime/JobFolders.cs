using DownloadManager.Core;
using DownloadManager.Core.Providers;

namespace DownloadManager.Runtime;

/// <summary>
/// UI.B19: the folders the app made for one download, and taking them away again when the download is
/// deleted. Only a provider file is put below the folder it was given: its path in the provider's item
/// (<see cref="RemoteSource.RelativePath"/>) becomes folders under that one. Every other download goes
/// straight into the folder the user, a category or the default gave it, and that folder is never ours to remove.
/// </summary>
public sealed partial class DownloadManagerService
{
    /// <summary>
    /// The folder a download was given, and the folders below it that the app made for the download, deepest
    /// first. Empty when the download went straight into its given folder, or when its folder no longer matches
    /// its provider path (moved since, say), because then which folders are its own isn't known.
    /// </summary>
    internal static (string? Base, IReadOnlyList<string> Made) FoldersMadeFor(DownloadJob job)
    {
        if (string.IsNullOrWhiteSpace(job.DestinationDirectory) || job.RemoteSource?.RelativePath is not { } relative)
            return (null, []);
        try
        {
            var bottom = Path.TrimEndingDirectorySeparator(Path.GetFullPath(job.DestinationDirectory));
            var folder = Path.GetDirectoryName(RemotePaths.ToSafeRelativePath(relative) ?? string.Empty);
            if (string.IsNullOrEmpty(folder)) return (null, []);
            var depth = folder.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Length;

            var made = new List<string>();
            var current = bottom;
            for (var level = 0; level < depth; level++)
            {
                if (Path.GetDirectoryName(current) is not { } parent) return (null, []);
                made.Add(current);
                current = parent;
            }

            var expected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(current, folder)));
            return string.Equals(expected, bottom, StringComparison.OrdinalIgnoreCase) ? (current, made) : (null, []);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return (null, []);
        }
    }

    /// <summary>
    /// After a download is taken off the list: removes the folders the app made for it that are now empty, deepest
    /// first, and stops at the first one that isn't. Never the folder it was given or anything above it, never a
    /// download, category, favorite, last-used or server folder, never one another download on the list still uses,
    /// and nothing at all when any of them is a link or junction, because what is behind one isn't this download's.
    /// </summary>
    private void RemoveEmptyFoldersMadeFor(DownloadJob job)
    {
        var (baseFolder, made) = FoldersMadeFor(job);
        if (baseFolder is null || made.Count == 0) return;

        HashSet<string> kept;
        List<string> inUse;
        lock (_sync)
        {
            kept = KeptFoldersLocked();
            inUse = _jobs.Values.Select(runtime => Normalized(runtime.Job.DestinationDirectory))
                .Where(path => path is not null).Select(path => path!).ToList();
        }

        try
        {
            // A link anywhere on the way down means the folders below it are somewhere else: leave all of them.
            if (made.Any(folder => Directory.Exists(folder) &&
                    new DirectoryInfo(folder).Attributes.HasFlag(FileAttributes.ReparsePoint)))
                return;

            foreach (var folder in made)
            {
                if (Path.GetPathRoot(folder) is { } root && string.Equals(Path.TrimEndingDirectorySeparator(root), folder, StringComparison.OrdinalIgnoreCase))
                    return;
                if (kept.Contains(folder)) return;
                var prefix = folder + Path.DirectorySeparatorChar;
                if (inUse.Any(path => string.Equals(path, folder, StringComparison.OrdinalIgnoreCase) ||
                        path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                    return;
                // One the transfer never got to make. The folder above it can still be empty and ours.
                if (!Directory.Exists(folder)) continue;
                if (Directory.EnumerateFileSystemEntries(folder).Any()) return;
                // Not recursive: if something arrived meanwhile, this fails and the folder stays.
                Directory.Delete(folder, recursive: false);
                _log?.Invoke($"Removed the empty folder made for job {job.Id}.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            _log?.Invoke($"Kept a folder made for job {job.Id}: {ex.Message}");
        }
    }

    /// <summary>Folders a user named somewhere in the settings or the server's roots. Call under <see cref="_sync"/>.</summary>
    private HashSet<string> KeptFoldersLocked()
    {
        IEnumerable<string?> named =
        [
            _settings.DefaultDownloadDirectory,
            _settings.LastDestinationDirectory,
            _settings.CategoryFoldersRoot,
            .. _settings.CategoryFolders.Values,
            .. _settings.CustomCategories.Select(category => category.DestinationDirectory),
            .. _settings.FavoriteFolders.Select(favorite => favorite.Path),
            .. Roots?.Roots ?? [],
        ];
        return named.Select(Normalized).Where(path => path is not null).Select(path => path!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string? Normalized(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim())); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }
}
