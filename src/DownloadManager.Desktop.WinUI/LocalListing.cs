namespace DownloadManager.Desktop.WinUI;

/// <summary>One file or folder of this PC in the Two panes view (SITES.5).</summary>
public sealed record LocalEntry(string Name, bool IsFolder, long Size, DateTimeOffset? Modified);

/// <summary>
/// SITES.5: the folders and files of one folder on this PC for the Two panes view, without UI types (linked into
/// Core.Tests). Folders first, then names without regard to case, as in Explorer. Links and junctions are listed but
/// never followed into by an upload (the Host skips them).
/// </summary>
public static class LocalListing
{
    /// <summary>Entries one folder shows at most.</summary>
    public const int MaxEntries = 20_000;

    public static (List<LocalEntry> Entries, string? Problem) Read(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder)) return ([], "Type a full folder path, such as C:\\Downloads.");
        try
        {
            var info = new DirectoryInfo(folder);
            if (!info.Exists) return ([], "That folder is not on this PC.");
            var options = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false, AttributesToSkip = 0 };
            var entries = new List<LocalEntry>();
            foreach (var entry in info.EnumerateFileSystemInfos("*", options))
            {
                var isFolder = entry is DirectoryInfo;
                entries.Add(new LocalEntry(entry.Name, isFolder, isFolder ? -1 : ((FileInfo)entry).Length,
                    new DateTimeOffset(entry.LastWriteTimeUtc, TimeSpan.Zero)));
                if (entries.Count > MaxEntries) return ([], $"The folder holds more than {MaxEntries:N0} items, more than this view shows.");
            }

            entries.Sort(Compare);
            return (entries, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return ([], "That folder can't be read: " + e.Message);
        }
    }

    /// <summary>Folders first, then by name without regard to case.</summary>
    public static int Compare(LocalEntry a, LocalEntry b)
    {
        if (a.IsFolder != b.IsFolder) return a.IsFolder ? -1 : 1;
        var byName = StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
        return byName != 0 ? byName : StringComparer.Ordinal.Compare(a.Name, b.Name);
    }

    /// <summary>The folder above, or null at a drive's root (or a share's).</summary>
    public static string? Parent(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return null;
        var trimmed = Path.TrimEndingDirectorySeparator(folder);
        return Path.GetDirectoryName(trimmed) is { Length: > 0 } parent ? parent : null;
    }

    /// <summary>
    /// The folder on this PC that matches a server folder, for a site with a linked folder: the site's start folder is
    /// the linked folder, and folders below it map name by name. Null outside the start folder, without a linked folder,
    /// or for a name Windows can't hold. Whether it exists is the caller's question.
    /// </summary>
    public static string? LinkedFolder(string? localFolder, string? remoteRoot, string serverFolder)
    {
        if (string.IsNullOrWhiteSpace(localFolder) || !Path.IsPathFullyQualified(localFolder)) return null;
        var start = "/" + (remoteRoot ?? "").Trim('/');
        var shown = "/" + serverFolder.Trim('/');
        string inside;
        if (shown == start) inside = "";
        else if (start == "/") inside = shown[1..];
        else if (shown.StartsWith(start + "/", StringComparison.Ordinal)) inside = shown[(start.Length + 1)..];
        else return null;
        var folder = localFolder;
        foreach (var part in inside.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part is "." or ".." || part.IndexOfAny(BadNameChars) >= 0 || part.EndsWith('.') || part.EndsWith(' ')) return null;
            folder = Path.Combine(folder, part);
        }

        return folder;
    }

    private static readonly char[] BadNameChars = ['<', '>', ':', '"', '\\', '|', '?', '*', '\0'];

    /// <summary>The folder's own name, to select it after going up.</summary>
    public static string Leaf(string folder) => Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
}
