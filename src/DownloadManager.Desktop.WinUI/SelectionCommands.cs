using DownloadManager.Contracts;
using DownloadManager.Core.Providers;

namespace DownloadManager.Desktop.WinUI;

/// <summary>Why a File menu command has nothing to act on. A null reason means the command is on. Nothing here touches the UI.</summary>
/// <param name="OpenReason">For what needs the file itself to be there: Open, Open Folder, Move/Rename.</param>
internal sealed record FileMenuPlan(string? FileReason, string? LocalReason, string? RemoveReason, string? OpenReason = null);

internal static class FileMenuRules
{
    /// <summary>A finished download whose archive went on purpose after extraction. Nothing is wrong with it, and there is no file to act on.</summary>
    public const string FileRemovedReason = "archive removed after extraction";

    /// <param name="singleFile">The selection's first local row is a file, which is what Open and its like act on.</param>
    /// <param name="package">The selection's first local row is a package.</param>
    /// <param name="sideRows">Selected rows with no local download behind them: remote jobs and archive records.</param>
    /// <param name="removableSideRows">Those of them that Remove can take off the list. An archive still extracting can't go.</param>
    /// <param name="fileRemoved">That file's archive was removed after extraction.</param>
    public static FileMenuPlan Plan(bool singleFile, bool package, int sideRows, int removableSideRows, bool fileRemoved = false)
    {
        var local = singleFile || package;
        var noLocalRow = local ? null : sideRows > 0 ? "no local file selected" : "select a download first";
        var fileReason = singleFile ? null : package ? "select a file, not a package" : noLocalRow;
        return new FileMenuPlan(
            fileReason,
            noLocalRow,
            local || removableSideRows > 0 ? null : sideRows > 0 ? "cancel the extraction first" : "select a download first",
            fileReason ?? (fileRemoved ? FileRemovedReason : null));
    }

    public static string Label(string text, string? reason) => reason is null ? text : $"{text} ({reason})";
}

/// <summary>What the Host says about a finished download beyond its state. It sits beside the state and never changes it.</summary>
internal static class JobNotes
{
    private const string RemovedWords = "Archive removed after extraction";

    /// <summary>The Host's words, with any address taken out. Null when it has nothing to say.</summary>
    public static string? Note(JobDto job)
    {
        var note = UrlRedactor.HideAddresses(job.StatusNote);
        if (!string.IsNullOrWhiteSpace(note)) return note;
        return job.FileRemovedAfterExtraction ? RemovedWords : null;
    }

    /// <summary>The note goes first, because the Description column is often cut short.</summary>
    public static string Description(string? note, string? own)
    {
        if (string.IsNullOrWhiteSpace(note)) return own ?? string.Empty;
        return string.IsNullOrWhiteSpace(own) ? note : $"{note} · {own}";
    }

    /// <summary>For a delete question about downloads whose archives are already gone.</summary>
    public static string NothingToDelete(int count) => count == 1
        ? "Its archive was removed after extraction, so no file is deleted."
        : "Their archives were removed after extraction, so no files are deleted.";

    /// <summary>Says why a command that wants the file has nothing to act on.</summary>
    public static string NoFile(string? note) => $"{(string.IsNullOrWhiteSpace(note) ? RemovedWords : note.TrimEnd('.'))}, so there is no file to open.";
}

internal static class ProgressBarRules
{
    /// <summary>The moving bar means bytes are being worked on and their total isn't known. A failed or paused download isn't doing that.</summary>
    public static bool ShowsMovingBar(string state, long? totalBytes)
        => totalBytes is not > 0 && state is "connecting" or "downloading" or "verifying";
}
