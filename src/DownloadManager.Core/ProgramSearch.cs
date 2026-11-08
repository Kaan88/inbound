namespace DownloadManager.Core;

/// <summary>Finds a program the way a shell does, in the folders of a <c>PATH</c> value, in order.</summary>
public static class ProgramSearch
{
    /// <summary>
    /// The full path of the first <paramref name="fileName"/> in the folders of <paramref name="path"/>, or null. The
    /// name is taken as given: "ffmpeg.exe" on Windows, "ffmpeg" on Linux, where programs have no extension.
    /// </summary>
    public static string? FindOnPath(string fileName, string? path)
    {
        foreach (var directory in (path ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim(), fileName);
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
            catch (ArgumentException)
            {
                // A PATH entry with characters a path can't hold names nothing.
            }
        }

        return null;
    }
}
