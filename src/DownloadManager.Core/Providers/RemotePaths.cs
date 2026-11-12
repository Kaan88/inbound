namespace DownloadManager.Core.Providers;

/// <summary>Turns a provider's untrusted file path into a safe path relative to the download folder.</summary>
public static class RemotePaths
{
    /// <summary>NTFS allows 255, and a part file's suffix goes on top of the name.</summary>
    public const int LongestSegment = 200;
    /// <summary>Every job carries this path several times over, to every client.</summary>
    public const int LongestPath = 512;

    /// <summary>
    /// Returns a relative path using the platform separator, or null when nothing safe is left.
    /// Anything that climbs out of the folder is refused outright rather than repaired, because
    /// a provider sending one is not a provider whose other names should be guessed at. So is a path
    /// too long to create, because shortening it could make two files collide.
    /// </summary>
    public static string? ToSafeRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var unified = path.Replace('\\', '/');
        if (unified.StartsWith('/') || unified.StartsWith("~", StringComparison.Ordinal)) return null;
        if (unified.Length >= 2 && unified[1] == ':') return null;
        if (unified.Length > LongestPath * 2) return null;

        var safe = new List<string>();
        foreach (var part in unified.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = part.Trim();
            if (trimmed is "." or "..") return null;
            var clean = CleanSegment(part);
            if (clean is null || clean.Length > LongestSegment) return null;
            safe.Add(clean);
        }

        if (safe.Count == 0) return null;
        var relative = string.Join(Path.DirectorySeparatorChar, safe);
        return relative.Length <= LongestPath ? relative : null;
    }

    /// <summary>Makes one path segment legal on Windows, keeping it recognisable. Null if nothing is left.</summary>
    public static string? CleanSegment(string segment)
    {
        var buffer = new char[segment.Length];
        for (var i = 0; i < segment.Length; i++)
        {
            var c = segment[i];
            buffer[i] = FileNameResolver.IsDeceptiveOrControl(c) || c is '<' or '>' or ':' or '"' or '|' or '?' or '*' ? '_' : c;
        }

        // Windows silently drops trailing dots and spaces, which would make two names collide.
        var clean = new string(buffer).Trim().TrimEnd('.', ' ');
        if (clean.Length == 0 || clean is "." or "..") return null;
        return FileNameResolver.IsReservedDeviceName(clean) || Archives.ArchiveWorkingFolders.IsReserved(clean) ? "_" + clean : clean;
    }
}
