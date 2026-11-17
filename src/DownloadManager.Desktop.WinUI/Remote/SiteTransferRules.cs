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

    /// <summary>Files that have not run yet, or wait for another try; a file the user stopped does not wait.</summary>
    public static int Waiting(SiteTransferDto t) =>
        Math.Max(0, t.Files - t.FilesDone - t.FilesSkipped - t.FilesFailed - t.FilesStopped - t.FilesRunning);

    /// <summary>The tab a batch shows under: Running and waiting (running, paused), Failed, or Done (done, canceled).</summary>
    public static string TabOf(SiteTransferDto t) => t.State switch
    {
        "running" or "paused" => ActiveTab,
        "failed" => FailedTab,
        _ => DoneTab,
    };

    /// <summary>
    /// The tab's batches, newest first; Running and waiting puts what moves first, then what is paused, then what waits
    /// (the mockup), each newest first.
    /// </summary>
    public static List<SiteTransferDto> InTab(IEnumerable<SiteTransferDto> transfers, string tab)
    {
        var shown = transfers.Where(t => TabOf(t) == tab);
        return tab == ActiveTab
            ? [.. shown.OrderBy(ActiveRank).ThenByDescending(t => t.CreatedAt)]
            : [.. shown.OrderByDescending(t => t.CreatedAt)];
    }

    // Started (moving now, or between files) first, then paused, then waiting: not started yet, or waiting for its server.
    private static int ActiveRank(SiteTransferDto t) => IsStarted(t) ? 0 : t.State == "paused" ? 1 : 2;

    /// <summary>
    /// A running batch that has started (a file moving now, or files done, or bytes moved) and is not waiting to try its
    /// server again. A running batch that is not started is waiting. Shared by the Running tab's order and the remotes' badges.
    /// </summary>
    public static bool IsStarted(SiteTransferDto t) => t.State == "running" && t.RetryAt is null
        && (t.FilesRunning > 0 || t.FilesDone + t.FilesSkipped + t.FilesFailed > 0 || t.BytesDone > 0);

    public static string TabLabel(IEnumerable<SiteTransferDto> transfers, string tab)
    {
        var count = transfers.Count(t => TabOf(t) == tab);
        var name = tab switch { ActiveTab => "Running and waiting", FailedTab => "Failed", _ => "Done" };
        return $"{name} ({count})";
    }

    public static bool IsDownload(SiteTransferDto t) => t.Direction == "download";

    /// <summary>
    /// The File Name column of a batch row: "Download ubuntu.iso", "Download photos (1,204 files)", "Upload 2 files". A batch
    /// from before the Host named what was picked reads "Download from Home NAS".
    /// </summary>
    public static string Title(SiteTransferDto t)
    {
        var verb = IsDownload(t) ? "Download" : "Upload";
        if (t.PickedCount <= 0 || t.Picked.Count == 0) return IsDownload(t) ? $"Download from {t.SiteName}" : $"Upload to {t.SiteName}";
        if (t.PickedCount == 1)
            return t.Files > 1 || t.Folders > 0 ? $"{verb} {t.Picked[0]} ({Count(t)})" : $"{verb} {t.Picked[0]}";
        return t.Folders == 0 && t.Files == t.PickedCount ? $"{verb} {t.PickedCount} files" : $"{verb} {t.PickedCount} items";
    }

    /// <summary>The Description column when nothing else needs saying: where from and to, as the mockup draws it.</summary>
    public static string Route(SiteTransferDto t) =>
        IsDownload(t) ? $"{t.SiteName} to {t.LocalFolder}" : $"{t.LocalFolder} to {t.SiteName} {t.RemoteFolder}";

    /// <summary>The whole route, with the folder on the remote, for the row's tool tip.</summary>
    public static string FullRoute(SiteTransferDto t) =>
        IsDownload(t) ? $"{t.SiteName} {t.RemoteFolder} to {t.LocalFolder}" : $"{t.LocalFolder} to {t.SiteName} {t.RemoteFolder}";

    /// <summary>"10 files"; "items" when empty folders are among them, since those are counted too.</summary>
    public static string Count(SiteTransferDto t) =>
        t.Folders > 0 ? (t.Files == 1 ? "1 item" : $"{Number(t.Files)} items") : t.Files == 1 ? "1 file" : $"{Number(t.Files)} files";

    private static string Number(long value) => value.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

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

    /// <summary>
    /// SITES.5: true when a download into <paramref name="localFolder"/> on this PC finished since
    /// <paramref name="before"/> with something copied, so the This PC pane showing it is out of date.
    /// </summary>
    public static bool DownloadLanded(IReadOnlyList<SiteTransferDto> before, IReadOnlyList<SiteTransferDto> now, string localFolder)
    {
        static string Clean(string path) => path.TrimEnd('\\', '/');
        var shown = Clean(localFolder);
        return DownloadLanded(before, now, t => string.Equals(Clean(t.LocalFolder ?? ""), shown, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// SITES.5: true when a download from the site finished since <paramref name="before"/> with something copied, so its
    /// "On this PC" column is out of date.
    /// </summary>
    public static bool DownloadLandedFrom(IReadOnlyList<SiteTransferDto> before, IReadOnlyList<SiteTransferDto> now, string siteId) =>
        DownloadLanded(before, now, t => t.SiteId == siteId);

    private static bool DownloadLanded(IReadOnlyList<SiteTransferDto> before, IReadOnlyList<SiteTransferDto> now, Func<SiteTransferDto, bool> matches)
    {
        foreach (var t in now)
        {
            if (!IsDownload(t) || t.FilesDone == 0 || t.State is "running" or "paused") continue;
            if (!matches(t)) continue;
            var earlier = before.FirstOrDefault(b => b.Id == t.Id);
            if (earlier is null || earlier.State is "running" or "paused") return true;
        }

        return false;
    }

    /// <summary>
    /// SITES.5: true when deleting <paramref name="path"/> on this PC would take away the folder of a batch that runs, is
    /// paused or can still go on: the folder itself or one holding it.
    /// </summary>
    public static bool LocalInUse(IReadOnlyList<SiteTransferDto> transfers, string path)
    {
        var target = path.TrimEnd('\\', '/');
        foreach (var t in transfers)
        {
            if (CanClear(t) && !CanResume(t) || string.IsNullOrEmpty(t.LocalFolder)) continue;
            var folder = t.LocalFolder.TrimEnd('\\', '/');
            if (string.Equals(folder, target, StringComparison.OrdinalIgnoreCase)
                || folder.StartsWith(target + "\\", StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    // ---- The list's cells (R3): what each column of a row says ------------------------------------------

    /// <summary>The main list's status colours, by name; the window maps them to its brushes.</summary>
    public enum StatusKind { Active, Done, Failed, Waiting, Neutral }

    /// <summary>The Status column: text, colour, and the bar's percent (null: no bar).</summary>
    public readonly record struct TransferStatus(string Text, StatusKind Kind, double? Percent);

    /// <summary>A row of the list: a batch, or one file of an opened batch.</summary>
    public readonly record struct TransferRef(SiteTransferDto Batch, SiteTransferItemDto? Item = null)
    {
        public bool IsFile => Item is not null;
    }

    private static string Verb(SiteTransferDto t) => IsDownload(t) ? "Downloading" : "Uploading";

    private static string Percent(double value) => value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "%";

    /// <summary>
    /// How far a batch is, 0 to 100: the bytes done of those still to move (failed and stopped files are set aside), or
    /// the files done when no size is known.
    /// </summary>
    public static double Progress(SiteTransferDto t)
    {
        var whole = t.BytesSetAside > 0 && t.BytesSetAside < t.Bytes ? t.Bytes - t.BytesSetAside : t.Bytes;
        if (whole > 0) return Math.Clamp(100.0 * t.BytesDone / whole, 0, 100);
        return t.Files > 0 ? Math.Clamp(100.0 * (t.FilesDone + t.FilesSkipped) / t.Files, 0, 100) : 0;
    }

    public static TransferStatus Status(SiteTransferDto t)
    {
        switch (t.State)
        {
            case "running":
                if (t.FilesRunning == 0) return new("Waiting", StatusKind.Waiting, null);
                var percent = Progress(t);
                return new($"{Verb(t)} {Percent(percent)}", StatusKind.Active, percent);
            case "paused":
                return new("Paused", StatusKind.Waiting, null);
            case "failed":
                return new("Failed", StatusKind.Failed, null);
            case "canceled":
                return new("Canceled", StatusKind.Neutral, null);
            default:
                return t.FilesStopped > 0 && t.FilesDone == 0 && t.FilesSkipped == 0
                    ? new("Stopped", StatusKind.Neutral, null)
                    : new("Completed", StatusKind.Done, null);
        }
    }

    /// <summary>The moving entry the Host reports for a file of a batch, or null.</summary>
    public static SiteTransferMovingDto? MovingOf(SiteTransferDto t, string path) =>
        t.Moving.FirstOrDefault(m => string.Equals(m.Path, path, StringComparison.Ordinal));

    /// <summary>A file's state; one the Host reports as moving is running even if the files list read earlier says waiting.</summary>
    private static string StateOf(SiteTransferDto t, SiteTransferItemDto item) =>
        item.State == "waiting" && MovingOf(t, item.Path) is not null ? "running" : item.State;

    /// <summary>A file's Status: its own state, with the bar and percent of a file that moves now.</summary>
    public static TransferStatus Status(SiteTransferDto t, SiteTransferItemDto item)
    {
        switch (StateOf(t, item))
        {
            case "running":
                if (item.IsFolder) return new("Making the folder", StatusKind.Active, null);
                if (MovingOf(t, item.Path) is { Size: > 0 } moving)
                {
                    var percent = Math.Clamp(100.0 * moving.BytesDone / moving.Size, 0, 100);
                    return new($"{Verb(t)} {Percent(percent)}", StatusKind.Active, percent);
                }

                return new(Verb(t), StatusKind.Active, null);
            case "done": return new("Completed", StatusKind.Done, null);
            case "skipped": return new("Skipped", StatusKind.Neutral, null);
            case "failed": return new("Failed", StatusKind.Failed, null);
            case "stopped": return new("Stopped", StatusKind.Neutral, null);
            default: return new(item.Tries > 0 ? "Waiting to retry" : "Waiting", StatusKind.Waiting, null);
        }
    }

    /// <summary>The Size column of a batch: the bytes as listed; blank when none are known.</summary>
    public static string SizeText(SiteTransferDto t) => t.Bytes > 0 ? UiFormat.Bytes(t.Bytes) : "";

    public static string SizeText(SiteTransferItemDto item) => item.Size >= 0 && !item.IsFolder ? UiFormat.Bytes(item.Size) : "";

    /// <summary>The Time left column: the Host's figure in the main list's words ("6m 12s"); blank when it has none.</summary>
    public static string TimeLeft(long? seconds) =>
        seconds is { } s && s >= 0 ? DurationText.TimeLeft(TimeSpan.FromSeconds(Math.Max(1, s))) : "";

    public static string TimeLeft(SiteTransferDto t) => t.State == "running" ? TimeLeft(t.SecondsLeft) : "";

    public static string TimeLeft(SiteTransferDto t, SiteTransferItemDto item) =>
        StateOf(t, item) == "running" && MovingOf(t, item.Path) is { } moving ? TimeLeft(moving.SecondsLeft) : "";

    /// <summary>The Transfer rate column; blank unless it moves.</summary>
    public static string RateText(long speed) => speed > 0 ? $"{UiFormat.Bytes(speed)}/s" : "";

    public static string RateText(SiteTransferDto t) => t.State == "running" && t.FilesRunning > 0 ? RateText(t.Speed) : "";

    public static string RateText(SiteTransferDto t, SiteTransferItemDto item) =>
        StateOf(t, item) == "running" && MovingOf(t, item.Path) is { } moving ? RateText(moving.Speed) : "";

    /// <summary>"14:32", the moment a waiting batch tries again, in this PC's time.</summary>
    public static string ClockText(DateTimeOffset moment) =>
        moment.LocalDateTime.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// The Description column. A failed batch says why (the Host's message, or the first file's error with its name when
    /// there are several); a batch that waits out a lost connection says for whom and until when; others say where from and
    /// to, with what was skipped, stopped or failed after it.
    /// </summary>
    public static string Description(SiteTransferDto t)
    {
        switch (t.State)
        {
            case "running" when t.RetryAt is { } at:
                var because = string.IsNullOrWhiteSpace(t.RetryReason) ? "" : " " + t.RetryReason.Trim();
                return $"Waiting for {t.SiteName}, trying again at {ClockText(at)}.{because}";
            case "failed":
                if (t.Message is { Length: > 0 } message) return message;
                if (t.FirstFailedError is { Length: > 0 } error)
                    return t.Files > 1 && t.FirstFailedPath is { Length: > 0 } path ? $"{path}: {error}" : error;
                return $"{t.FilesFailed} of {Count(t)} failed";
            case "paused" when t.Message is { Length: > 0 } paused:
                return paused;
        }

        var parts = new List<string> { Route(t) };
        // The mockup's "only new files": a batch that skips what is already at the target.
        if (t.Conflict == "skip") parts[0] += " · only new files";
        if (t.FilesSkipped > 0) parts.Add($"{t.FilesSkipped} skipped (already there)");
        if (t.FilesStopped > 0) parts.Add($"{t.FilesStopped} stopped");
        if (t.FilesFailed > 0) parts.Add($"{t.FilesFailed} failed");
        return string.Join(" · ", parts);
    }

    /// <summary>A file's Description: why it failed or waits, or what became of it.</summary>
    public static string Description(SiteTransferItemDto item)
    {
        switch (item.State)
        {
            case "failed": return item.Error ?? "";
            case "skipped": return "Already there";
            case "stopped": return "Stopped. Start this file to copy it.";
            case "waiting" when item.Tries > 0: return $"Trying again ({item.Tries} tries so far): {item.Error}";
            case "done" when item.IsFolder: return "Empty folder made";
            case "running" when item.IsFolder: return "Empty folder";
        }

        return item.Target is { } target && target != item.Path ? $"Saved as {target}" : "";
    }

    /// <summary>
    /// The files of an opened batch in the order the list shows them: moving first, then failed, stopped and waiting, then
    /// what is done; by name within each. A batch of thousands shows what needs eyes first.
    /// </summary>
    public static List<SiteTransferItemDto> OrderItems(IEnumerable<SiteTransferItemDto> items) =>
        [.. items.OrderBy(item => item.State switch { "running" => 0, "failed" => 1, "stopped" => 2, "waiting" => 3, _ => 4 })
            .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)];

    /// <summary>How many files an opened batch shows before "Show more", and how many each "Show more" adds.</summary>
    public const int FilesPerPage = 500;

    /// <summary>"Show more (1,204 more files)".</summary>
    public static string MoreText(int hidden) => $"Show more ({Number(hidden)} more {(hidden == 1 ? "file" : "files")})";

    /// <summary>What an open batch's files list is keyed by: it is fetched again only when this changes.</summary>
    public static string FilesSignature(SiteTransferDto t) =>
        $"{t.State}|{t.FilesDone}|{t.FilesSkipped}|{t.FilesFailed}|{t.FilesStopped}|{t.FilesRunning}|{t.Files}";

    public static string EmptyMessage(string tab) => tab switch
    {
        FailedTab => "No failed transfers.",
        DoneTab => "No finished transfers.",
        _ => "Nothing is moving. Select files in a remote and choose Download, or choose Upload.",
    };

    // ---- Commands a row offers (R3, decision 4) -----------------------------------------------------------

    public enum TransferCommand { Stop, Resume, Retry, StopFile, StartFile, Open, OpenFolder, ShowOnRemote, Cancel, Remove }

    public static string Label(TransferCommand command) => command switch
    {
        TransferCommand.Stop => "Stop",
        TransferCommand.Resume => "Resume",
        TransferCommand.Retry => "Retry",
        TransferCommand.StopFile => "Stop this file",
        TransferCommand.StartFile => "Start this file",
        TransferCommand.Open => "Open",
        TransferCommand.OpenFolder => "Open folder",
        TransferCommand.ShowOnRemote => "Show on the remote",
        TransferCommand.Cancel => "Cancel transfer",
        _ => "Remove from list",
    };

    /// <summary>The key a command has on the list, as a menu shows it; blank when it has none (the menu key reaches it).</summary>
    public static string KeyText(TransferCommand command) => command switch
    {
        TransferCommand.Stop or TransferCommand.Resume => "Space",
        TransferCommand.Retry => "F5",
        TransferCommand.Open => "Enter",
        TransferCommand.OpenFolder => "Ctrl+Enter",
        TransferCommand.Remove => "Delete",
        _ => "",
    };

    /// <summary>Whether something of the transfer can be opened on this PC: an upload's source always, a download's once a file landed.</summary>
    public static bool CanOpen(SiteTransferDto t) => !IsDownload(t) || t.FilesDone > 0 || t.FilesSkipped > 0;

    private static bool CanOpen(SiteTransferDto t, SiteTransferItemDto item) =>
        !IsDownload(t) || item.State is "done" or "skipped";

    /// <summary>Whether <paramref name="command"/> is valid for the row in its state; what the Host would accept.</summary>
    public static bool Valid(TransferCommand command, TransferRef row)
    {
        var t = row.Batch;
        if (row.Item is { } item)
        {
            return command switch
            {
                TransferCommand.StopFile => !item.IsFolder && item.State is "waiting" or "running" && t.State is "running" or "paused" or "failed",
                TransferCommand.StartFile => !item.IsFolder && item.State is "stopped" or "failed" && t.State != "canceled",
                TransferCommand.Open or TransferCommand.OpenFolder => CanOpen(t, item),
                TransferCommand.ShowOnRemote => true,
                _ => false,
            };
        }

        return command switch
        {
            TransferCommand.Stop => CanPause(t),
            TransferCommand.Resume => CanResume(t),
            TransferCommand.Retry => CanRetry(t),
            TransferCommand.Open or TransferCommand.OpenFolder => CanOpen(t),
            TransferCommand.ShowOnRemote => true,
            TransferCommand.Cancel => CanCancel(t),
            TransferCommand.Remove => CanClear(t),
            _ => false,
        };
    }

    /// <summary>
    /// The menu of the selected rows, in groups. A command shows when it is valid for any selected row (it then acts on those
    /// it is valid for); Open, Open folder and Show on the remote only for one row.
    /// </summary>
    public static List<List<TransferCommand>> Menu(IReadOnlyList<TransferRef> selection)
    {
        var groups = new List<List<TransferCommand>>();
        if (selection.Count == 0) return groups;
        TransferCommand[][] layout =
        [
            [TransferCommand.Stop, TransferCommand.Resume, TransferCommand.Retry, TransferCommand.StopFile, TransferCommand.StartFile],
            [TransferCommand.Open, TransferCommand.OpenFolder, TransferCommand.ShowOnRemote],
            [TransferCommand.Cancel, TransferCommand.Remove],
        ];
        foreach (var group in layout)
        {
            var shown = group.Where(command => OneRowOnly(command) ? selection.Count == 1 && Valid(command, selection[0])
                : selection.Any(row => Valid(command, row))).ToList();
            if (shown.Count > 0) groups.Add(shown);
        }

        return groups;
    }

    public static bool OneRowOnly(TransferCommand command) => command is TransferCommand.Open or TransferCommand.OpenFolder or TransferCommand.ShowOnRemote;

    /// <summary>What double click and Enter do: Open on a finished row; null for the window to open or close a batch.</summary>
    public static TransferCommand? Primary(TransferRef row)
    {
        if (row.Item is { } item) return item.State is "done" or "skipped" && Valid(TransferCommand.Open, row) ? TransferCommand.Open : null;
        return row.Batch.State == "done" && CanOpen(row.Batch) ? TransferCommand.Open : null;
    }

    /// <summary>What Space does: Stop on a running batch, Resume on a paused one, null otherwise.</summary>
    public static TransferCommand? SpaceCommand(IReadOnlyList<TransferRef> selection) =>
        selection.Any(row => Valid(TransferCommand.Stop, row)) ? TransferCommand.Stop
        : selection.Any(row => Valid(TransferCommand.Resume, row)) ? TransferCommand.Resume : null;

    /// <summary>
    /// The path on this PC that Open and Open folder act on: for an upload the local source, for a download what landed. One
    /// picked name is that file or folder; several (or an older batch) is the batch's folder; a file row is its own file.
    /// </summary>
    public static string LocalTarget(TransferRef row)
    {
        var t = row.Batch;
        if (row.Item is { } item)
        {
            var relative = IsDownload(t) ? item.Target ?? item.Path : item.Path;
            return System.IO.Path.Combine(t.LocalFolder, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
        }

        return t.PickedCount == 1 && t.Picked.Count == 1 ? System.IO.Path.Combine(t.LocalFolder, t.Picked[0]) : t.LocalFolder;
    }

    /// <summary>
    /// A download batch row of one picked name, with its files read: what landed on this PC, under the name it got (Keep both
    /// renames it). A picked file gives its item too (its state says whether it was copied or skipped); a picked folder gives
    /// the folder its first file went into. (null, null) when it can't be told: the caller opens the batch's folder.
    /// </summary>
    public static (string? Path, SiteTransferItemDto? File) PickedLocal(SiteTransferDto t, IReadOnlyList<SiteTransferItemDto> items)
    {
        if (!IsDownload(t) || t.PickedCount != 1 || t.Picked.Count != 1) return (null, null);
        var picked = t.Picked[0];
        string Local(string relative) => System.IO.Path.Combine(t.LocalFolder, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
        if (items.FirstOrDefault(item => item.Path == picked) is { } file) return (Local(file.Target ?? file.Path), file);
        if (items.FirstOrDefault(item => item.Path.StartsWith(picked + "/", StringComparison.Ordinal)) is { } inside)
        {
            var target = inside.Target ?? inside.Path;
            var cut = target.IndexOf('/');
            return (Local(cut < 0 ? target : target[..cut]), null);
        }

        return (null, null);
    }

    /// <summary>
    /// Whether Open treats the row's file as a download (marked as from the internet, a program asked about): a copied file.
    /// A skipped one was already the user's own file, and an upload's source always is: they open plainly.
    /// </summary>
    public static bool OpensAsDownloaded(TransferRef row) =>
        IsDownload(row.Batch) && (row.Item is { } item ? item.State == "done" : row.Batch.FilesDone > 0);

    /// <summary>How soon an opened batch's files are read again after its counts change: a long list less often.</summary>
    public static TimeSpan FilesReadInterval(int count) => TimeSpan.FromSeconds(count > 1000 ? 10 : 2);

    /// <summary>
    /// Puts <paramref name="rows"/> in the order of <paramref name="desired"/> with as few moves as it can: rows that are gone
    /// are removed, the longest run already in order stays, and only the others move (<paramref name="move"/>, from, to). A row
    /// that repeats in <paramref name="desired"/> is kept once, at its first place. Returns how many moves it made.
    /// </summary>
    public static int Reconcile<T>(IList<T> rows, IReadOnlyList<T> desired, Action<int, int> move) where T : class
    {
        var target = new Dictionary<T, int>(ReferenceEqualityComparer.Instance);
        var wanted = new List<T>(desired.Count);
        foreach (var row in desired)
        {
            if (target.TryAdd(row, wanted.Count)) wanted.Add(row);
        }

        for (var index = rows.Count - 1; index >= 0; index--)
        {
            if (!target.ContainsKey(rows[index])) rows.RemoveAt(index);
        }

        // Rows listed twice before (should not happen) keep their first place only.
        var once = new HashSet<T>(ReferenceEqualityComparer.Instance);
        for (var index = 0; index < rows.Count;)
        {
            if (once.Add(rows[index])) index++;
            else rows.RemoveAt(index);
        }

        var stay = InOrder([.. rows.Select(row => target[row])]).Select(index => rows[index]).ToHashSet(ReferenceEqualityComparer.Instance);
        var moves = 0;
        var guard = wanted.Count * 2 + rows.Count + 8;
        for (var at = 0; at < wanted.Count;)
        {
            var row = wanted[at];
            if (at < rows.Count && ReferenceEquals(rows[at], row))
            {
                at++;
                continue;
            }

            if (!once.Contains(row))
            {
                rows.Insert(Math.Min(at, rows.Count), row);
                once.Add(row);
                at++;
                continue;
            }

            // Present, so it is further down: the rows before "at" are wanted[0..at), all different from it.
            if (at >= rows.Count) break;
            var displaced = rows[at];
            if (stay.Contains(row) && !stay.Contains(displaced) && --guard > 0)
            {
                // A row out of order sits where one that stays belongs: it goes to its own place, further down.
                move(at, Math.Min(target[displaced], rows.Count - 1));
                moves++;
                continue;
            }

            var from = -1;
            for (var index = at + 1; index < rows.Count; index++)
            {
                if (ReferenceEquals(rows[index], row))
                {
                    from = index;
                    break;
                }
            }

            if (from < 0) break;
            move(from, at);
            moves++;
            at++;
        }

        while (rows.Count > wanted.Count) rows.RemoveAt(rows.Count - 1);
        return moves;
    }

    /// <summary>The positions of the longest increasing run of <paramref name="order"/> (O(n log n)).</summary>
    private static List<int> InOrder(IReadOnlyList<int> order)
    {
        var tails = new List<int>();
        var previous = new int[order.Count];
        for (var index = 0; index < order.Count; index++)
        {
            int low = 0, high = tails.Count;
            while (low < high)
            {
                var middle = (low + high) / 2;
                if (order[tails[middle]] < order[index]) low = middle + 1;
                else high = middle;
            }

            previous[index] = low > 0 ? tails[low - 1] : -1;
            if (low == tails.Count) tails.Add(index);
            else tails[low] = index;
        }

        var run = new List<int>();
        for (var index = tails.Count > 0 ? tails[^1] : -1; index >= 0; index = previous[index]) run.Add(index);
        run.Reverse();
        return run;
    }

    /// <summary>The folder of the remote that "Show on the remote" goes to, and the name to select in it (null: none).</summary>
    public static (string Folder, string? Select) RemoteTarget(TransferRef row)
    {
        var t = row.Batch;
        var root = t.RemoteFolder.Length == 0 ? "/" : t.RemoteFolder;
        if (row.Item is not { } item)
            return (root, t.PickedCount == 1 && t.Picked.Count == 1 ? t.Picked[0] : null);
        // A download's file is where it was listed; an upload's is under its target name.
        var relative = IsDownload(t) ? item.Path : item.Target ?? item.Path;
        var cut = relative.LastIndexOf('/');
        var folder = cut < 0 ? root : root.TrimEnd('/') + "/" + relative[..cut];
        return (folder, cut < 0 ? relative : relative[(cut + 1)..]);
    }

    public static bool CanPause(SiteTransferDto t) => t.State == "running";

    /// <summary>Paused, or stopped by a problem with files still waiting.</summary>
    public static bool CanResume(SiteTransferDto t) =>
        t.State == "paused" || t.State == "failed" && (Waiting(t) > 0 || t.Problem is not null);

    public static bool CanRetry(SiteTransferDto t) => t.State == "failed" && t.FilesFailed > 0;

    public static bool CanCancel(SiteTransferDto t) => t.State is "running" or "paused" || CanResume(t);

    public static bool CanClear(SiteTransferDto t) => t.State is "done" or "failed" or "canceled";

    /// <summary>What Clear done removes, one id at a time: finished and canceled batches. A failed batch stays until it is
    /// retried or removed by itself (an empty id would clear it too).</summary>
    public static List<string> ClearDoneTargets(IEnumerable<SiteTransferDto> transfers) =>
        [.. transfers.Where(t => t.State is "done" or "canceled").Select(t => t.Id)];

    // ---- The slim bar and the status-bar chip -------------------------------------------------------------

    /// <summary>Bytes a second the running downloads move.</summary>
    public static long DownSpeed(IEnumerable<SiteTransferDto> transfers) => transfers.Where(t => t.State == "running" && IsDownload(t)).Sum(t => t.Speed);

    /// <summary>Bytes a second the running uploads move.</summary>
    public static long UpSpeed(IEnumerable<SiteTransferDto> transfers) => transfers.Where(t => t.State == "running" && !IsDownload(t)).Sum(t => t.Speed);

    /// <summary>"118.4 MB/s", also "0 B/s".</summary>
    public static string SpeedText(long speed) => $"{UiFormat.Bytes(speed)}/s";

    private static List<string> CountParts(IReadOnlyCollection<SiteTransferDto> transfers)
    {
        var running = transfers.Where(t => t.State == "running").ToList();
        var moving = running.Count(t => t.FilesRunning > 0);
        var waiting = running.Count - moving;
        var paused = transfers.Count(t => t.State == "paused");
        var failed = transfers.Count(t => t.State == "failed");
        var parts = new List<string>();
        if (moving > 0) parts.Add($"{moving} running");
        if (waiting > 0) parts.Add($"{waiting} waiting");
        if (paused > 0) parts.Add($"{paused} paused");
        if (failed > 0) parts.Add($"{failed} failed");
        return parts;
    }

    /// <summary>The closed bar's counts, "2 running · 2 waiting"; what is left to say when nothing runs.</summary>
    public static string SlimSummary(IReadOnlyCollection<SiteTransferDto> transfers)
    {
        var parts = CountParts(transfers);
        if (parts.Count > 0) return string.Join(" · ", parts);
        return transfers.Count == 0 ? "No transfers" : "Nothing running";
    }

    /// <summary>
    /// The status chip: "Remote files · 2 running · 156 MB/s" while a batch runs, waits, is paused or failed; null when there
    /// is nothing to say (no batch, or only finished ones without failures).
    /// </summary>
    public static string? ChipText(IReadOnlyCollection<SiteTransferDto> transfers)
    {
        var parts = CountParts(transfers);
        if (parts.Count == 0) return null;
        var speed = transfers.Where(t => t.State == "running").Sum(t => t.Speed);
        if (speed > 0) parts.Add(SpeedText(speed));
        return "Remote files · " + string.Join(" · ", parts);
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
