namespace DownloadManager.Core;

/// <summary>
/// How this machine's file system compares names. Windows folds capital letters (NTFS keeps <c>a.txt</c> and
/// <c>A.TXT</c> as one file); Linux keeps them apart, so <c>/downloads/A</c> and <c>/downloads/a</c> are two folders.
/// Use this where a wrong answer would merge two real paths or let one pass a containment check for another. Names
/// that are not paths (extensions, categories, volume-set grouping) keep their own rules: see
/// <c>docs\docker-webui-plan.md</c>, "Case-sensitive names".
/// </summary>
public static class PathNames
{
    /// <summary>OrdinalIgnoreCase on Windows, Ordinal elsewhere.</summary>
    public static StringComparison Comparison { get; } =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>The comparer that goes with <see cref="Comparison"/>, for sets and dictionaries of paths.</summary>
    public static StringComparer Comparer { get; } =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static bool Same(string? left, string? right) => string.Equals(left, right, Comparison);

    /// <summary>
    /// An archive or listing path with this machine's separator. Windows reads both <c>/</c> and <c>\</c> as a separator
    /// and 7-Zip lists either, so <c>/</c> becomes <c>\</c> there. On Linux only <c>/</c> separates, and a <c>\</c> is part
    /// of a name; it is left as it is, and <c>ExtractionSafety</c> refuses such a name rather than guess.
    /// </summary>
    public static string ToNativeSeparators(string path) =>
        OperatingSystem.IsWindows() ? path.Replace('/', '\\') : path;
}

/// <summary>Which mounted volume holds a path, for free space and "same volume" checks.</summary>
public static class Volumes
{
    /// <summary>
    /// The root of the volume that holds <paramref name="path"/>: the drive (<c>C:\</c>) on Windows, the longest mount point
    /// that holds it on Linux (from <see cref="DriveInfo.GetDrives"/>, which reads <c>/proc/self/mountinfo</c>). In a
    /// container <c>/downloads</c> is usually its own mount while <c>/</c> is the container's layer, so the path's first
    /// part would name the wrong disk. Null when it can't be told.
    /// </summary>
    public static string? RootOf(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (OperatingSystem.IsWindows())
            {
                var root = Path.GetPathRoot(full);
                return string.IsNullOrWhiteSpace(root) ? null : root;
            }

            return FolderSpaceMeter.MountFor(full, DriveInfo.GetDrives().Select(drive => drive.Name));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Whether two paths sit on one volume, so a rename between them is a move and not a copy.</summary>
    public static bool Same(string left, string right)
    {
        var first = RootOf(left);
        var second = RootOf(right);
        return first is not null && second is not null && PathNames.Same(first, second);
    }

    /// <summary>Free bytes for the volume holding <paramref name="path"/>, or null when that can't be told, as on a share that doesn't answer.</summary>
    public static long? AvailableBytes(string path)
    {
        try
        {
            return RootOf(path) is { } root ? new DriveInfo(root).AvailableFreeSpace : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
