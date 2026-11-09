using DownloadManager.Contracts;
using DownloadManager.Core.Providers;

namespace DownloadManager.Desktop.WinUI;

/// <summary>Why a File menu command has nothing to act on. A null reason means the command is on. Nothing here touches the UI.</summary>
/// <param name="OpenReason">For what needs the file itself to be there: Open, Open Folder, Move/Rename.</param>
/// <param name="ShellReason">For what this PC's shell does with the file (Open, Open With, Open Folder): also off for a
/// download on another server, whose file is on that machine.</param>
/// <param name="LaunchReason">For Open and Open With, which start the file itself: also off until the download has
/// finished, since there is no file yet (U-WL-13). Open Folder can still show the folder.</param>
internal sealed record FileMenuPlan(string? FileReason, string? LocalReason, string? RemoveReason, string? OpenReason = null, string? ShellReason = null,
    string? LaunchReason = null);

/// <summary>Which file the progress windows talk about. Nothing here touches the UI.</summary>
internal static class ProgressDetailRules
{
    /// <summary>
    /// The file whose details a package window shows: the one someone picked while it is still there,
    /// otherwise the first that is doing something, then the first unfinished, then the first.
    /// </summary>
    public static Guid? FileToShow(IReadOnlyList<JobDto> members, Guid? picked)
    {
        if (picked is { } id && members.Any(job => job.Id == id)) return id;
        return (members.FirstOrDefault(job => job.State is "connecting" or "downloading" or "verifying")
            ?? members.FirstOrDefault(job => !job.IsTerminal)
            ?? members.FirstOrDefault())?.Id;
    }

    /// <summary>A package of one file has nothing to add to that file's own window, which a package of one is shown as on the list too.</summary>
    public static Guid? OnlyFile(DownloadGroupDto? group, Func<Guid, bool> isListed)
        => group?.JobIds.Where(isListed).ToList() is [var only] ? only : null;
}

internal static class FileMenuRules
{
    /// <summary>A finished download whose archive went on purpose after extraction. Nothing is wrong with it, and there is no file to act on.</summary>
    public const string FileRemovedReason = "archive removed after extraction";

    /// <summary>An unfinished, failed or cancelled download has no file to open yet.</summary>
    public const string NotFinishedReason = "not downloaded yet";

    /// <param name="singleFile">The selection's first local row is a file, which is what Open and its like act on.</param>
    /// <param name="package">The selection's first local row is a package.</param>
    /// <param name="sideRows">Selected rows with no local download behind them: remote jobs and archive records.</param>
    /// <param name="removableSideRows">Those of them that Delete can take off the list. An archive still extracting can't go.</param>
    /// <param name="fileRemoved">That file's archive was removed after extraction.</param>
    /// <param name="remote">The window shows another server (<see cref="RemoteDataRules"/>).</param>
    /// <param name="finished">That file's download has completed.</param>
    public static FileMenuPlan Plan(bool singleFile, bool package, int sideRows, int removableSideRows, bool fileRemoved = false, bool remote = false,
        bool finished = true)
    {
        var local = singleFile || package;
        var noLocalRow = local ? null : sideRows > 0 ? "no local file selected" : "select a download first";
        var fileReason = singleFile ? null : package ? "select a file, not a package" : noLocalRow;
        var openReason = fileReason ?? (fileRemoved ? FileRemovedReason : null);
        var shellReason = openReason ?? (RemoteDataRules.MayUseFiles(remote) ? null : RemoteDataRules.OnAnotherServer);
        return new FileMenuPlan(
            fileReason,
            noLocalRow,
            local || removableSideRows > 0 ? null : sideRows > 0 ? "cancel the extraction first" : "select a download first",
            openReason,
            shellReason,
            shellReason ?? (finished ? null : NotFinishedReason));
    }

    public static string Label(string text, string? reason) => reason is null ? text : $"{text} ({reason})";

    /// <summary>Grouping takes two rows or more with files behind them. A package row alone holds several files, and is one already.</summary>
    /// <param name="files">The files the selection stands for, a package's included.</param>
    /// <param name="localRows">The selected rows that are files or packages.</param>
    public static bool CanGroup(int files, int localRows) => files >= 2 && localRows >= 2;
}

/// <summary>What the Host says about a finished download beyond its state. It sits beside the state and never changes it.</summary>
internal static class JobNotes
{
    private const string RemovedWords = "Archive removed after extraction";

    /// <summary>The Host's words, with any address taken out. Null when it has nothing to say.</summary>
    public static string? Note(JobDto job)
    {
        // The wait for a connection is the row's status itself, so it isn't said twice.
        if (WaitingText(job) is not null) return job.FileRemovedAfterExtraction ? RemovedWords : null;
        var note = UrlRedactor.HideAddresses(job.StatusNote);
        // U-WL-3: a failed download says why, first, in the Status tooltip, the Description column and Properties.
        var reason = FailureReason(job);
        if (reason is not null) return string.IsNullOrWhiteSpace(note) ? reason : $"{reason} {note}";
        if (!string.IsNullOrWhiteSpace(note)) return note;
        return job.FileRemovedAfterExtraction ? RemovedWords : null;
    }

    /// <summary>"Failed: " and the Host's reason, with any address taken out. Null unless the download failed with one.</summary>
    public static string? FailureReason(JobDto job) => FailureMessage(job) is { } message ? $"Failed: {message}" : null;

    /// <summary>The Host's reason a download failed, with any address taken out, for a window whose heading already says Failed.</summary>
    public static string? FailureMessage(JobDto job)
    {
        if (job.State != "failed" || job.Failure is not { } failure) return null;
        var message = UrlRedactor.HideAddresses(failure.Message).Trim();
        return string.IsNullOrWhiteSpace(message) ? null : message;
    }

    /// <summary>UI.B7: what a running download waiting for a free connection to its host says. The host name only.</summary>
    public static string? WaitingText(JobDto job)
        => job.State is "connecting" or "downloading" or "retryWaiting" && !string.IsNullOrWhiteSpace(job.WaitingForHost)
            ? $"Waiting for a connection to {job.WaitingForHost}"
            : null;

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

/// <summary>What Delete… acts on (UI.R25). Nothing here touches the UI.</summary>
internal static class DeleteRules
{
    /// <summary>The selected downloads; or, from the list's context menu when only an archive record is selected,
    /// the download behind it, as the context menu's Remove did. The File menu, toolbar and Delete key act on the
    /// selection only, so for them a selection of side rows alone removes those rows.</summary>
    public static IReadOnlyList<T> Targets<T>(IReadOnlyList<T> selected, T? recordDownload, bool fromContextMenu) where T : class
        => selected.Count > 0 ? selected : fromContextMenu && recordDownload is not null ? [recordDownload] : [];
}
