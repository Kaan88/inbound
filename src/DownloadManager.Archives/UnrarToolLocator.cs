namespace DownloadManager.Archives;

/// <summary>Finds UnRAR: the configured path, then WinRAR under Program Files, then PATH.</summary>
public static class UnrarToolLocator
{
    private const string ToolName = "UnRAR.exe";

    /// <param name="programFilesDirectories">Null for the machine's own. An empty list skips this step.</param>
    /// <param name="pathVariable">Null for this process's PATH. An empty string skips this step.</param>
    public static string? Find(
        string? configuredPath,
        IEnumerable<string>? programFilesDirectories = null,
        string? pathVariable = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
        {
            return Path.GetFullPath(configuredPath);
        }

        foreach (var folder in programFilesDirectories ?? DefaultProgramFiles())
        {
            var candidate = Path.Combine(folder, "WinRAR", ToolName);
            if (File.Exists(candidate)) return candidate;
        }

        foreach (var raw in (pathVariable ?? Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var folder = raw.Trim().Trim('"');

            // A relative PATH entry would run whatever sits in the current folder, which for
            // this app can be a folder of downloads.
            if (!Path.IsPathFullyQualified(folder)) continue;
            var candidate = Path.Combine(folder, ToolName);
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    private static IEnumerable<string> DefaultProgramFiles()
    {
        var folders = new[]
        {
            Environment.GetEnvironmentVariable("ProgramW6432"),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        };
        return folders.Where(folder => !string.IsNullOrEmpty(folder)).Select(folder => folder!).Distinct(StringComparer.OrdinalIgnoreCase);
    }
}
