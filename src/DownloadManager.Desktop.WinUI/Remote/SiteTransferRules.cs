using DownloadManager.Contracts;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// SITES.4 (remote-sites.md, "The agreed design in short"): what the Transfers bar, the sidebar and the status chip say
/// about site transfers, and which of a batch's buttons apply. UI-free, so the tests and every window share it.
/// </summary>
internal static class SiteTransferRules
{
    public const string ActiveTab = "active";
    public const string FailedTab = "failed";
    public const string DoneTab = "done";

    private static readonly string[] Actions =
    [
        SitesMessageTypes.TransferPause, SitesMessageTypes.TransferResume, SitesMessageTypes.TransferCancel,
        SitesMessageTypes.TransferRetry, SitesMessageTypes.TransferClear,
    ];

    public static bool IsAction(string type) => Actions.Contains(type, StringComparer.Ordinal);

    /// <summary>Files that have not run yet, or wait for another try.</summary>
    public static int Waiting(SiteTransferDto t) =>
        Math.Max(0, t.Files - t.FilesDone - t.FilesSkipped - t.FilesFailed - t.FilesRunning);

    /// <summary>The tab a batch shows under: Running and waiting (running, paused), Failed, or Done (done, canceled).</summary>
    public static string TabOf(SiteTransferDto t) => t.State switch
    {
        "running" or "paused" => ActiveTab,
        "failed" => FailedTab,
        _ => DoneTab,
    };

    /// <summary>The tab's batches, newest first.</summary>
    public static List<SiteTransferDto> InTab(IEnumerable<SiteTransferDto> transfers, string tab) =>
        [.. transfers.Where(t => TabOf(t) == tab).OrderByDescending(t => t.CreatedAt)];

    public static string TabLabel(IEnumerable<SiteTransferDto> transfers, string tab)
    {
        var count = transfers.Count(t => TabOf(t) == tab);
        var name = tab switch { ActiveTab => "Running and waiting", FailedTab => "Failed", _ => "Done" };
        return count > 0 ? $"{name} ({count})" : name;
    }

    public static bool IsDownload(SiteTransferDto t) => t.Direction == "download";

    /// <summary>"Download from Home NAS" or "Upload to Home NAS".</summary>
    public static string Title(SiteTransferDto t) =>
        IsDownload(t) ? $"Download from {t.SiteName}" : $"Upload to {t.SiteName}";

    /// <summary>Where the files go, in the words of the side they land on.</summary>
    public static string Route(SiteTransferDto t) =>
        IsDownload(t) ? $"{t.RemoteFolder} → {t.LocalFolder}" : $"{t.LocalFolder} → {t.RemoteFolder}";

    /// <summary>"10 files"; "items" when empty folders are among them, since those are counted too.</summary>
    public static string Count(SiteTransferDto t) =>
        t.Folders > 0 ? (t.Files == 1 ? "1 item" : $"{t.Files} items") : t.Files == 1 ? "1 file" : $"{t.Files} files";

    /// <summary>
    /// True when an upload into <paramref name="folder"/> of the site finished since <paramref name="before"/> with
    /// something copied, so a view of that folder is out of date.
    /// </summary>
    public static bool UploadLanded(IReadOnlyList<SiteTransferDto> before, IReadOnlyList<SiteTransferDto> now, string siteId, string folder)
    {
        static string Clean(string path) => path.Length > 1 ? path.TrimEnd('/') : path;
        var shown = Clean(folder);
        foreach (var t in now)
        {
            if (IsDownload(t) || t.SiteId != siteId || t.FilesDone == 0 || t.State is "running" or "paused") continue;
            if (Clean(t.RemoteFolder) != shown) continue;
            var earlier = before.FirstOrDefault(b => b.Id == t.Id);
            if (earlier is null || earlier.State is "running" or "paused") return true;
        }

        return false;
    }

    /// <summary>A line of a batch's Files list: what happened to it.</summary>
    public static string ItemText(SiteTransferItemDto item)
    {
        var state = item.State switch
        {
            "done" => item.IsFolder ? "Folder made" : "Copied",
            "skipped" => "Skipped: it was already there",
            "running" => item.IsFolder ? "Making the folder now" : "Moving now",
            "failed" => $"Failed: {item.Error}",
            _ => item.Tries > 0 ? $"Waiting to try again ({item.Tries} tries so far): {item.Error}" : "Waiting",
        };
        if (item.IsFolder) return state + " · empty folder";
        return item.Size >= 0 ? state + " · " + UiFormat.Bytes(item.Size) : state;
    }

    /// <summary>"3 of 10 files, 1.2 MB of 40 MB, 512 KB/s" and the like.</summary>
    public static string StateText(SiteTransferDto t)
    {
        var finished = t.FilesDone + t.FilesSkipped;
        var files = Count(t);
        var bytes = t.Bytes > 0 ? $", {UiFormat.Bytes(t.BytesDone)} of {UiFormat.Bytes(t.Bytes)}" : "";
        var skipped = t.FilesSkipped > 0 ? $", {t.FilesSkipped} skipped (already there)" : "";
        var failed = t.FilesFailed > 0 ? $", {t.FilesFailed} failed" : "";
        switch (t.State)
        {
            case "running":
                if (t.FilesRunning == 0) return $"Waiting: {finished} of {files} done{bytes}{failed}";
                var speed = t.Speed > 0 ? $", {UiFormat.Bytes(t.Speed)}/s" : "";
                return $"{finished} of {files}{bytes}{speed}{failed}";
            case "paused":
                return $"{t.Message ?? "Paused."} {finished} of {files} done{failed}. Resume starts unfinished files from the beginning.";
            case "failed":
                if (t.Message is { Length: > 0 } message) return $"Stopped: {message}";
                return $"{t.FilesFailed} of {files} failed{skipped}";
            case "canceled":
                return $"Canceled: {finished} of {files} done";
            default:
                return $"Done: {t.FilesDone} of {files} copied{skipped}";
        }
    }

    public static bool CanPause(SiteTransferDto t) => t.State == "running";

    /// <summary>Paused, or stopped by a problem with files still waiting.</summary>
    public static bool CanResume(SiteTransferDto t) =>
        t.State == "paused" || t.State == "failed" && (Waiting(t) > 0 || t.Problem is not null);

    public static bool CanRetry(SiteTransferDto t) => t.State == "failed" && t.FilesFailed > 0;

    public static bool CanCancel(SiteTransferDto t) => t.State is "running" or "paused" || CanResume(t);

    public static bool CanClear(SiteTransferDto t) => t.State is "done" or "failed" or "canceled";

    /// <summary>
    /// The status chip and the closed bar: what runs and how fast, failures, or null when there is nothing to say
    /// (no batch, or only finished ones without failures).
    /// </summary>
    public static string? Summary(IReadOnlyCollection<SiteTransferDto> transfers)
    {
        var running = transfers.Where(t => t.State == "running").ToList();
        var paused = transfers.Count(t => t.State == "paused");
        var failed = transfers.Count(t => t.State == "failed");
        var parts = new List<string>();
        if (running.Count > 0)
        {
            var moving = running.Sum(t => t.FilesRunning);
            parts.Add(moving > 0
                ? $"{(running.Count == 1 ? "1 transfer" : $"{running.Count} transfers")} running"
                : $"{(running.Count == 1 ? "1 transfer" : $"{running.Count} transfers")} waiting");
            var speed = running.Sum(t => t.Speed);
            if (speed > 0) parts.Add($"{UiFormat.Bytes(speed)}/s");
        }

        if (paused > 0) parts.Add($"{paused} paused");
        if (failed > 0) parts.Add($"{failed} failed");
        return parts.Count == 0 ? null : "Site transfers: " + string.Join(", ", parts);
    }

    /// <summary>The sidebar's mark for one site: arrows with counts of files moving, and a clock for waiting batches.</summary>
    public static string? SiteMark(IEnumerable<SiteTransferDto> transfers, string siteId)
    {
        var running = transfers.Where(t => t.SiteId == siteId && t.State == "running").ToList();
        if (running.Count == 0) return null;
        var down = running.Where(IsDownload).Sum(t => t.FilesRunning);
        var up = running.Where(t => !IsDownload(t)).Sum(t => t.FilesRunning);
        var waiting = running.Count(t => t.FilesRunning == 0);
        var parts = new List<string>();
        if (down > 0) parts.Add($"↓{down}");
        if (up > 0) parts.Add($"↑{up}");
        if (waiting > 0) parts.Add("⏱");
        return string.Join(" ", parts);
    }

    /// <summary>
    /// The sidebar site's progress line: the bytes done of the running batches of that site, in percent; null while none
    /// runs or none knows its size.
    /// </summary>
    public static double? SiteProgress(IEnumerable<SiteTransferDto> transfers, string siteId)
    {
        var running = transfers.Where(t => t.SiteId == siteId && t.State == "running").ToList();
        var total = running.Sum(t => t.Bytes);
        if (running.Count == 0 || total <= 0) return null;
        return Math.Clamp(100.0 * running.Sum(t => t.BytesDone) / total, 0, 100);
    }

    /// <summary>
    /// The toolbar's Pause transfers button (rclone has no pause: files in flight start again). It pauses every running
    /// batch; with none running and some paused it resumes those. Null when there is nothing to pause or resume.
    /// </summary>
    public static (string Label, string Type, IReadOnlyList<string> Ids)? BulkPause(IEnumerable<SiteTransferDto> transfers)
    {
        var list = transfers.ToList();
        var running = list.Where(CanPause).Select(t => t.Id).ToList();
        if (running.Count > 0) return ("Pause transfers", SitesMessageTypes.TransferPause, running);
        var paused = list.Where(t => t.State == "paused").Select(t => t.Id).ToList();
        if (paused.Count > 0) return ("Resume transfers", SitesMessageTypes.TransferResume, paused);
        return null;
    }

    /// <summary>The Failed tab's Retry all: every batch with failed files.</summary>
    public static IReadOnlyList<string> RetryAll(IEnumerable<SiteTransferDto> transfers) =>
        [.. transfers.Where(CanRetry).Select(t => t.Id)];

    /// <summary>The sidebar mark in words, for screen readers.</summary>
    public static string? SiteMarkName(IEnumerable<SiteTransferDto> transfers, string siteId)
    {
        var running = transfers.Where(t => t.SiteId == siteId && t.State == "running").ToList();
        if (running.Count == 0) return null;
        var down = running.Where(IsDownload).Sum(t => t.FilesRunning);
        var up = running.Where(t => !IsDownload(t)).Sum(t => t.FilesRunning);
        var parts = new List<string>();
        if (down > 0) parts.Add($"{down} downloading");
        if (up > 0) parts.Add($"{up} uploading");
        if (running.Any(t => t.FilesRunning == 0)) parts.Add("transfers waiting");
        return string.Join(", ", parts);
    }

    /// <summary>
    /// Files picked for an upload are sent as names in one folder; a picker that returned files from several folders (or
    /// none) can't be sent as one batch.
    /// </summary>
    public static (string? Folder, List<string> Names, string? Problem) UploadNames(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return (null, [], "Nothing was chosen.");
        var folders = paths.Select(path => System.IO.Path.GetDirectoryName(path) ?? "").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (folders.Count != 1 || folders[0].Length == 0) return (null, [], "Choose files from one folder at a time.");
        return (folders[0], [.. paths.Select(System.IO.Path.GetFileName).OfType<string>()], null);
    }

    /// <summary>The choices of "If a file is already there", in the order the dialog lists them (Skip first).</summary>
    public static readonly (string Value, string Label)[] Conflicts =
    [
        ("skip", "Skip it"),
        ("replace", "Replace it"),
        ("keepBoth", "Keep both (the new one gets a number)"),
    ];

    /// <summary>"3 items" / "report.pdf" for the dialog's first line.</summary>
    public static string WhatText(IReadOnlyList<SiteTransferNameDto> names)
    {
        if (names.Count == 1) return names[0].IsFolder ? $"the folder {names[0].Name}" : names[0].Name;
        var folders = names.Count(n => n.IsFolder);
        var files = names.Count - folders;
        var parts = new List<string>();
        if (files > 0) parts.Add(files == 1 ? "1 file" : $"{files} files");
        if (folders > 0) parts.Add(folders == 1 ? "1 folder" : $"{folders} folders");
        return string.Join(" and ", parts);
    }
}
