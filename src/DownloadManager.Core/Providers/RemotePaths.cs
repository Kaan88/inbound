namespace DownloadManager.Core.Providers;

/// <summary>Turns a provider's untrusted file path into a safe path relative to the download folder.</summary>
public static class RemotePaths
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Returns a relative path using the platform separator, or null when nothing safe is left.
    /// Anything that climbs out of the folder is refused outright rather than repaired, because
    /// a provider sending one is not a provider whose other names should be guessed at.
    /// </summary>
    public static string? ToSafeRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var unified = path.Replace('\\', '/');
        if (unified.StartsWith('/') || unified.StartsWith("~", StringComparison.Ordinal)) return null;
        if (unified.Length >= 2 && unified[1] == ':') return null;

        var safe = new List<string>();
        foreach (var part in unified.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = part.Trim();
            if (trimmed is "." or "..") return null;
            var clean = CleanSegment(part);
            if (clean is null) return null;
            safe.Add(clean);
        }

        return safe.Count == 0 ? null : string.Join(Path.DirectorySeparatorChar, safe);
    }

    /// <summary>Makes one path segment legal on Windows, keeping it recognisable. Null if nothing is left.</summary>
    public static string? CleanSegment(string segment)
    {
        var buffer = new char[segment.Length];
        for (var i = 0; i < segment.Length; i++)
        {
            var c = segment[i];
            buffer[i] = c < 32 || c is '<' or '>' or ':' or '"' or '|' or '?' or '*' ? '_' : c;
        }

        // Windows silently drops trailing dots and spaces, which would make two names collide.
        var clean = new string(buffer).Trim().TrimEnd('.', ' ');
        if (clean.Length == 0 || clean is "." or "..") return null;
        var stem = clean.Split('.', 2)[0].TrimEnd();
        return ReservedNames.Contains(stem) ? "_" + clean : clean;
    }
}
