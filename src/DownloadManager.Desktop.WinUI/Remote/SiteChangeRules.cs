using DownloadManager.Contracts;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// SITES.5: the words and choices of New folder, Rename and Delete on server, without UI types so Core.Tests can check
/// them (linked there).
/// </summary>
public static class SiteChangeRules
{
    /// <summary>"New folder", or "New folder (2)" and so on when that name is taken in the folder shown.</summary>
    public static string NewFolderName(IEnumerable<string> names)
    {
        var taken = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains("New folder")) return "New folder";
        for (var number = 2; ; number++)
        {
            var candidate = $"New folder ({number})";
            if (!taken.Contains(candidate)) return candidate;
        }
    }

    /// <summary>How much of a name a rename box selects first: all of a folder's, a file's name without its last extension.</summary>
    public static int RenameSelectionLength(string name, bool isFolder)
    {
        if (isFolder) return name.Length;
        var dot = name.LastIndexOf('.');
        return dot > 0 ? dot : name.Length;
    }

    /// <summary>The delete question: what goes, counted on the server, and that it can't be undone.</summary>
    public static string DeleteQuestion(IReadOnlyList<SiteTransferNameDto> chosen, SiteCountResultDto count, string siteName)
    {
        var what = chosen.Count == 1
            ? (chosen[0].IsFolder ? $"the folder \"{chosen[0].Name}\"" : $"\"{chosen[0].Name}\"")
            : $"{chosen.Count} items";
        var files = count.Files == 1 ? "1 file" : $"{count.Files:N0} files";
        var bytes = count.SizeUnknown ? $"at least {UiFormat.Bytes(count.Bytes)}" : UiFormat.Bytes(count.Bytes);
        var folders = count.Folders == 1 ? "the folder" : "the folders";
        var inside = count.Files == 0 && count.Folders > 0 ? $"There are no files in {folders}."
            : count.Folders > 0 ? $"This removes {files} ({bytes}) on {siteName}, including everything inside {folders}."
            : $"This removes {files} ({bytes}) on {siteName}.";
        var missing = count.Missing switch
        {
            0 => "",
            1 => " One of them is no longer there.",
            _ => $" {count.Missing} of them are no longer there.",
        };
        return $"Delete {what} from the server? {inside}{missing} It does not go to a Recycle Bin and can't be undone.";
    }

    /// <summary>The items a delete could not remove, one line each, or null when all went.</summary>
    public static string? DeleteFailures(SiteChangeResultDto result)
    {
        if (result.Items.Count == 0) return result.Ok ? null : result.Message ?? "The delete failed.";
        var failed = result.Items.Where(i => !i.Ok).ToList();
        if (failed.Count == 0) return null;
        return string.Join("\n", failed.Select(i => $"{i.Name}: {i.Message}"));
    }
}
