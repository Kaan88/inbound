namespace DownloadManager.Core;

/// <summary>Rules for saved and recent download folders, shared by the settings store and every save.</summary>
public static class DownloadFolders
{
    public const int MaxRecent = 5;
    public const int MaxFavorites = 50;

    /// <summary>Trimmed, without empty paths or repeats, each with a name: its own, or the folder's last part.</summary>
    public static List<FavoriteFolder> Normalize(IEnumerable<FavoriteFolder>? favorites)
    {
        var seen = new HashSet<string>(PathNames.Comparer);
        var result = new List<FavoriteFolder>();
        foreach (var favorite in favorites ?? [])
        {
            var path = favorite?.Path?.Trim();
            if (string.IsNullOrWhiteSpace(path) || !seen.Add(TrimEnd(path))) continue;
            var name = favorite!.Name?.Trim();
            result.Add(new FavoriteFolder { Name = string.IsNullOrWhiteSpace(name) ? DefaultName(path) : name, Path = path });
            if (result.Count == MaxFavorites) break;
        }

        return result;
    }

    /// <summary>The name a favorite gets when none is given: the folder's last part, or the path for a drive root.</summary>
    public static string DefaultName(string path)
    {
        var name = Path.GetFileName(TrimEnd(path.Trim()));
        return string.IsNullOrWhiteSpace(name) ? path.Trim() : name;
    }

    /// <summary>The recent list with this folder put first.</summary>
    public static List<string> Remember(IEnumerable<string>? recent, string folder)
    {
        var list = new List<string> { folder.Trim() };
        list.AddRange((recent ?? []).Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()));
        return NormalizeRecent(list);
    }

    public static List<string> NormalizeRecent(IEnumerable<string>? recent)
        => (recent ?? []).Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim())
            .DistinctBy(TrimEnd, PathNames.Comparer).Take(MaxRecent).ToList();

    private static string TrimEnd(string path)
    {
        var trimmed = path.TrimEnd('\\', '/');
        // "C:\" and "/" stay roots.
        return trimmed.Length == 0 || trimmed.EndsWith(':') ? path : trimmed;
    }
}

/// <summary>How much room a folder has, measured where its files would be written.</summary>
public readonly record struct FolderSpace(long? FreeBytes, long? TotalBytes, bool InsideContainer);

/// <summary>
/// Measures free space for a folder. On Linux the folder's own mount is used, not the root of its path: in a container
/// <c>/</c> is the container's own layer, while a download folder is usually a mounted volume with the real disk
/// behind it.
/// </summary>
public static class FolderSpaceMeter
{
    private static readonly bool InContainer =
        !OperatingSystem.IsWindows() &&
        (File.Exists("/.dockerenv") || string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase));

    /// <summary>Gives up after <paramref name="timeout"/>, so a network drive that doesn't answer can't hold a window.</summary>
    public static async Task<FolderSpace> MeasureAsync(string folder, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        try
        {
            return await Task.Run(() => Measure(folder), cancellationToken).WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return new FolderSpace(null, null, false);
        }
    }

    public static FolderSpace Measure(string folder)
    {
        try
        {
            var full = Path.GetFullPath(folder.Trim());
            if (OperatingSystem.IsWindows())
            {
                var root = Path.GetPathRoot(full);
                if (string.IsNullOrWhiteSpace(root)) return new FolderSpace(null, null, false);
                var drive = new DriveInfo(root);
                return drive.IsReady ? new FolderSpace(drive.AvailableFreeSpace, drive.TotalSize, false) : new FolderSpace(null, null, false);
            }

            var mounts = DriveInfo.GetDrives().Select(drive => drive.Name).ToList();
            var mount = MountFor(full, mounts);
            if (mount is null) return new FolderSpace(null, null, false);
            var info = new DriveInfo(mount);
            return new FolderSpace(info.AvailableFreeSpace, info.TotalSize, InContainer && mount == "/");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new FolderSpace(null, null, false);
        }
    }

    /// <summary>The longest mount point that holds this Unix path, matching whole path parts only.</summary>
    public static string? MountFor(string fullPath, IEnumerable<string> mountPoints)
    {
        string? best = null;
        foreach (var raw in mountPoints)
        {
            if (string.IsNullOrEmpty(raw)) continue;
            var mount = raw.Length > 1 ? raw.TrimEnd('/') : raw;
            var holds = mount == "/" || fullPath == mount || fullPath.StartsWith(mount + "/", StringComparison.Ordinal);
            if (holds && (best is null || mount.Length > best.Length)) best = mount;
        }

        return best;
    }
}
