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

    // ---- Delete on server / Delete on This PC (SITES.11 R5, delete-mockup.png) --------------------------------------

    /// <summary>A run of a dialog line; bold for the names.</summary>
    public readonly record struct TextPart(string Text, bool Bold = false);

    /// <summary>What <c>sites.count</c> found, or that it failed. A count still running is a null <see cref="DeleteCount"/>.</summary>
    public readonly record struct DeleteCount(long Files, long Bytes, bool BytesAtLeast, bool Failed, int Missing);

    public static DeleteCount CountOf(SiteCountResultDto result) =>
        result.Ok ? new DeleteCount(result.Files, result.Bytes, result.SizeUnknown, false, result.Missing) : new DeleteCount(0, 0, false, true, 0);

    public const string DeleteServerTitle = "Delete on server?";
    public const string DeleteServerButton = "Delete on server";
    public const string DeleteLocalTitle = "Delete on This PC?";
    public const string DeleteLocalButton = "Move to Recycle Bin";
    public const string CountingLine = "Counting what is inside…";
    public const string DeleteServerWarning = "Inbound cannot bring them back. Nothing on this PC is touched.";
    public const string DeleteLocalWarning = "They can be brought back from the Recycle Bin. Nothing on a remote is touched.";
    public const string DeleteNetworkButton = "Delete";
    public const string DeleteForGoodTitle = "Delete permanently?";
    public const string DeleteForGoodButton = "Delete permanently";

    /// <summary>
    /// Where a delete goes: the server; the Recycle Bin; a network folder on this PC (Windows may not take it into the Recycle
    /// Bin and then asks before deleting it for good); or this PC for good (Delete permanently, Shift+Delete).
    /// </summary>
    public enum DeletePlace { Server, RecycleBin, NetworkFolder, ForGood }

    /// <summary>The Recycle Bin, or a network folder when the folder is a UNC path (\\server\share) or on a network drive.</summary>
    public static DeletePlace LocalDeletePlace(string folder, bool networkDrive) => networkDrive || IsUncPath(folder) ? DeletePlace.NetworkFolder : DeletePlace.RecycleBin;

    /// <summary>\\server\share\…, //server/share/… or \\?\UNC\…; not \\?\C:\… (a local path written long).</summary>
    public static bool IsUncPath(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal))
            return path.Length > 8 && path.Substring(4, 4).Equals(@"UNC\", StringComparison.OrdinalIgnoreCase);
        return path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal);
    }

    public static string DeleteTitle(DeletePlace place) => place switch
    {
        DeletePlace.Server => DeleteServerTitle,
        DeletePlace.ForGood => DeleteForGoodTitle,
        _ => DeleteLocalTitle,
    };

    public static string DeleteButton(DeletePlace place) => place switch
    {
        DeletePlace.Server => DeleteServerButton,
        DeletePlace.RecycleBin => DeleteLocalButton,
        DeletePlace.NetworkFolder => DeleteNetworkButton,
        _ => DeleteForGoodButton,
    };

    /// <summary>The line under the lead: what can and can't be brought back.</summary>
    public static string DeleteWarning(DeletePlace place, int chosen) => place switch
    {
        DeletePlace.Server => DeleteServerWarning,
        DeletePlace.RecycleBin => DeleteLocalWarning,
        DeletePlace.NetworkFolder => chosen == 1
            ? "It is in a network folder, so it may not go to the Recycle Bin. If it can't, Windows asks before deleting it for good."
            : "They are in a network folder, so they may not go to the Recycle Bin. If one can't, Windows asks before deleting it for good.",
        _ => chosen == 1 ? "It does not go to the Recycle Bin and can't be brought back." : "They do not go to the Recycle Bin and can't be brought back.",
    };

    /// <summary>The server and Delete permanently count what goes, and a folder in the choice needs the tick; the Recycle Bin needs neither.</summary>
    public static bool DeleteCounts(DeletePlace place) => place is DeletePlace.Server or DeletePlace.ForGood;

    /// <summary>
    /// The first line: the name in bold (or "3 items") and what goes with it, then "will be deleted from <b>remote</b>." A
    /// null <paramref name="count"/> is a count still running (or, on This PC, none made): the line says "everything inside"
    /// until it has numbers. A null <paramref name="remoteName"/> is This PC: "will be moved to the Recycle Bin."
    /// </summary>
    public static List<TextPart> DeleteLead(IReadOnlyList<SiteTransferNameDto> chosen, DeleteCount? count, string? remoteName) =>
        DeleteLead(chosen, count, remoteName is null ? DeletePlace.RecycleBin : DeletePlace.Server, remoteName);

    /// <summary>The first line for any <see cref="DeletePlace"/>; <paramref name="remoteName"/> is used for the server only.</summary>
    public static List<TextPart> DeleteLead(IReadOnlyList<SiteTransferNameDto> chosen, DeleteCount? count, DeletePlace place, string? remoteName)
    {
        var hasFolder = chosen.Any(item => item.IsFolder);
        var parts = new List<TextPart> { new(chosen.Count == 1 ? chosen[0].Name : $"{chosen.Count} items", Bold: true) };
        DeleteCount? known = count is { Failed: false } ? count : null;
        var them = chosen.Count == 1 ? "it" : "them";
        if (known is { } c)
        {
            var size = (c.BytesAtLeast ? "at least " : "") + UiFormat.Bytes(c.Bytes);
            if (!hasFolder) parts.Add(new($" ({size})"));
            else if (c.Files == 0) parts.Add(new(" (no files inside)"));
            else parts.Add(new($" and the {c.Files.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} {(c.Files == 1 ? "file" : "files")} inside {them} ({size})"));
        }
        else if (hasFolder)
        {
            parts.Add(new($" and everything inside {them}"));
        }

        switch (place)
        {
            case DeletePlace.Server:
                parts.Add(new(" will be deleted from "));
                parts.Add(new(remoteName ?? "the server", Bold: true));
                parts.Add(new("."));
                break;
            case DeletePlace.RecycleBin:
                parts.Add(new(" will be moved to the Recycle Bin."));
                break;
            case DeletePlace.NetworkFolder:
                parts.Add(new(" will be deleted."));
                break;
            default:
                parts.Add(new(" will be deleted from this PC for good."));
                break;
        }

        if (known is { Missing: > 0 } gone) parts.Add(new(gone.Missing == 1 ? " One of them is no longer there." : $" {gone.Missing} of them are no longer there."));
        return parts;
    }

    /// <summary>
    /// The tick box a folder in the choice asks for, which the delete button waits for; null when no folder is in it. With
    /// numbers: "I understand that 1,842 files will be deleted" ("at least" when the count was cut short); without them
    /// (counting or failed): "everything inside will be deleted".
    /// </summary>
    public static string? DeleteTick(IReadOnlyList<SiteTransferNameDto> chosen, DeleteCount? count)
    {
        if (!chosen.Any(item => item.IsFolder)) return null;
        if (count is not { Failed: false } known) return "I understand that everything inside will be deleted";
        if (known.Files == 0) return chosen.Count == 1 ? "I understand that the folder will be deleted" : "I understand that the folders will be deleted";
        var number = known.Files.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
        var files = known.Files == 1 ? "file" : "files";
        return known.BytesAtLeast ? $"I understand that at least {number} {files} will be deleted" : $"I understand that {number} {files} will be deleted";
    }

    /// <summary>The count's own line under the lead: the counting note while it runs, and when it failed that the number is a floor.</summary>
    public static string? CountNote(IReadOnlyList<SiteTransferNameDto> chosen, DeleteCount? count)
    {
        if (!chosen.Any(item => item.IsFolder)) return null;
        if (count is null) return CountingLine;
        return count.Value.Failed ? "What is inside could not be counted, so there may be many more items than a count would show." : null;
    }

    // ---- This PC: the checks and words of LocalRecycle (SITES.11 R5) ------------------------------------------

    /// <summary>
    /// Why a path on this PC is never handed to a delete, or null. Only a full path is (the shell takes a relative one against
    /// its own current folder and ignores the Recycle Bin for it); no wildcards (the shell expands them); never a whole drive
    /// or share.
    /// </summary>
    public static string? LocalDeletePathProblem(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.IndexOfAny(['\0', '*', '?']) >= 0) return "The path is not valid.";
        if (!Path.IsPathFullyQualified(path)) return "The path is not a full path, so it was left alone.";
        var root = Path.GetPathRoot(path) ?? "";
        if (path.TrimEnd('\\', '/').Equals(root.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) return "A whole drive is never deleted.";
        // Win32 drops "." and ".." segments and a name's trailing dots and spaces, so "D:\x\.." is D:\ and "foo." is "foo":
        // the delete would hit another item. Such a path is never handed on.
        var segments = path[root.Length..].Split('\\', '/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "." or "..")) return "The path is not valid.";
        if (segments.Any(segment => segment.EndsWith('.') || segment.EndsWith(' '))) return UnaddressableName;
        return null;
    }

    public const string UnaddressableName = "left alone; Windows can't address this name";

    /// <summary>The shell's answer to a delete (its own DE_* codes, not Win32 errors), in words.</summary>
    public static string ShellDeleteMessage(int code) => code switch
    {
        0x78 => "Access was denied.",
        0x79 or 0x7C or 0x402 => "The path is too long or not valid.",
        1223 => "Cancelled.",
        _ => $"Windows could not delete it (0x{code.ToString("X", System.Globalization.CultureInfo.InvariantCulture)}).",
    };

    /// <summary>What Delete permanently says of an item it left: "notes.txt is in use", "… is read-only or access was denied".</summary>
    public static string LocalLeftText(string name, Exception error) => error switch
    {
        IOException io when (io.HResult & 0xFFFF) is 32 or 33 => $"{name} is in use",
        UnauthorizedAccessException => $"{name} is read-only or access was denied",
        PathTooLongException => $"{name}: the path is too long",
        _ => $"{name}: {error.Message}",
    };

    /// <summary>The pane's note after a delete on this PC that left items: the first three and how many more; null when none were left.</summary>
    /// <paramref name="cutShort"/>: the delete stopped after too many were left, so how many more is not known ("and more").
    public static string? LocalDeleteNote(DeletePlace place, IReadOnlyList<string> left, bool cutShort = false)
    {
        if (left.Count == 0) return null;
        var lead = place == DeletePlace.RecycleBin ? "Not moved to the Recycle Bin: " : "Not deleted: ";
        var more = cutShort ? "; and more"
            : left.Count > 3 ? $"; and {(left.Count - 3).ToString(System.Globalization.CultureInfo.InvariantCulture)} more" : "";
        return lead + string.Join("; ", left.Take(3)) + more;
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
