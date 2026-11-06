namespace DownloadManager.Desktop.WinUI;

/// <summary>Why a File menu command has nothing to act on. A null reason means the command is on. Nothing here touches the UI.</summary>
internal sealed record FileMenuPlan(string? FileReason, string? LocalReason, string? RemoveReason);

internal static class FileMenuRules
{
    /// <param name="singleFile">The selection's first local row is a file, which is what Open and its like act on.</param>
    /// <param name="package">The selection's first local row is a package.</param>
    /// <param name="sideRows">Selected rows with no local download behind them: remote jobs and archive records.</param>
    /// <param name="removableSideRows">Those of them that Remove can take off the list. An archive still extracting can't go.</param>
    public static FileMenuPlan Plan(bool singleFile, bool package, int sideRows, int removableSideRows)
    {
        var local = singleFile || package;
        var noLocalRow = local ? null : sideRows > 0 ? "no local file selected" : "select a download first";
        return new FileMenuPlan(
            singleFile ? null : package ? "select a file, not a package" : noLocalRow,
            noLocalRow,
            local || removableSideRows > 0 ? null : sideRows > 0 ? "cancel the extraction first" : "select a download first");
    }

    public static string Label(string text, string? reason) => reason is null ? text : $"{text} ({reason})";
}

internal static class ProgressBarRules
{
    /// <summary>The moving bar means bytes are being worked on and their total isn't known. A failed or paused download isn't doing that.</summary>
    public static bool ShowsMovingBar(string state, long? totalBytes)
        => totalBytes is not > 0 && state is "connecting" or "downloading" or "verifying";
}
