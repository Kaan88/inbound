using System.Globalization;
using DownloadManager.Contracts;

namespace DownloadManager.Desktop.WinUI;

/// <summary>Where a magnet or .torrent line goes when a provider account (TorBox) could take it too (T1).</summary>
internal enum TorrentLineRoute
{
    /// <summary>Not chosen yet: the setting is "Ask each time" and the line waits for a choice.</summary>
    Unset,
    ThisPc,
    Provider,
}

/// <summary>Where a torrent's file choice is made when its list comes in (<see cref="TorrentRules.NextFileChoice"/>).</summary>
internal enum FileChoicePlace
{
    /// <summary>The main window's Choose Files dialog.</summary>
    Dialog,

    /// <summary>The torrent's open Progress Details window, brought to the front on its Files tab.</summary>
    ProgressWindow,
}

/// <summary>
/// The window's torrent rules (T1, docs/plans/torrent-t1-design.md section 6), kept free of WinUI so they are tested
/// (tests/DownloadManager.Core.Tests links this file).
/// </summary>
internal static class TorrentRules
{
    public const string SidebarTag = "torrents";

    /// <summary>Torrents can be added and are shown: the Host has the engine and they are turned on in its Options.</summary>
    public static bool Applies(bool torrentsOffered, SettingsDto? settings) => torrentsOffered && settings?.TorrentsEnabled == true;

    public static bool IsTorrent(JobDto? job) => job?.Torrent is not null;

    /// <summary>A magnet or a .torrent file's path, the inputs the torrent dialog takes.</summary>
    public static bool IsTorrentInput(string? text)
        => text is not null && (LinkExtractor.IsMagnet(text.Trim()) || LinkExtractor.IsTorrentPath(text.Trim()));

    /// <summary>The route a new line starts with, from the setting (<c>ask</c>, <c>thisPc</c>, <c>provider</c>).</summary>
    public static TorrentLineRoute DefaultRoute(string? setting, bool providerAvailable)
    {
        if (!providerAvailable) return TorrentLineRoute.ThisPc;
        return setting switch
        {
            "thisPc" => TorrentLineRoute.ThisPc,
            "provider" => TorrentLineRoute.Provider,
            _ => TorrentLineRoute.Unset,
        };
    }

    public static bool IsActive(JobDto job) => job.State is "connecting" or "downloading" or "verifying";

    /// <summary>The Status column of a torrent row, or null for the usual text.</summary>
    public static string? StatusText(JobDto job)
    {
        if (job.Torrent is not { } torrent) return null;
        if (torrent.AwaitingFileChoice) return "Choose files to download";
        if (job.State == "paused" && job.Failure?.Kind == "diskFull") return "Paused · The disk is full";
        // T2: a finished torrent that seeds says so, with its ratio; one that doesn't stays "Complete".
        if (TorrentSeedRules.StatusText(job) is { } seeding) return seeding;
        if (!IsActive(job)) return null;
        var peers = Peers(torrent.Peers);
        return torrent.HasMetadata ? $"Downloading · {peers}" : $"Getting file list · {peers}";
    }

    /// <summary>"1 file", "11 files" or "3 of 11 files"; "File list not known yet" before the metadata.</summary>
    public static string FilesSummary(TorrentDto torrent)
    {
        if (!torrent.HasMetadata) return "File list not known yet";
        var total = torrent.FileCount == 1 ? "1 file" : $"{torrent.FileCount} files";
        return torrent.WantedCount >= torrent.FileCount ? total : $"{torrent.WantedCount} of {total}";
    }

    public static string Peers(int peers) => peers == 1 ? "1 peer" : $"{peers} peers";

    /// <summary>
    /// The status-bar chip, shown while a torrent runs or seeds (the engine runs then): "Torrents: 2 active · 47 peers",
    /// "Torrents: 2 active · 1 seeding · 50 peers" or "Torrents: 1 seeding · 3 peers".
    /// </summary>
    public static string? ChipText(IEnumerable<JobDto> jobs)
    {
        var list = jobs.ToList();
        var active = list.Where(job => job.Torrent is not null && IsActive(job)).ToList();
        var seeding = list.Where(TorrentSeedRules.IsSeeding).ToList();
        if (active.Count == 0 && seeding.Count == 0) return null;
        var parts = new List<string>();
        if (active.Count > 0) parts.Add($"{active.Count} active");
        if (seeding.Count > 0) parts.Add($"{seeding.Count} seeding");
        parts.Add(Peers(active.Concat(seeding).Sum(job => job.Torrent!.Peers)));
        return "Torrents: " + string.Join(" · ", parts);
    }

    /// <summary>A short magnet that names the torrent, without trackers: what a torrent row shows as its address.</summary>
    public static string ShortMagnet(TorrentDto torrent) => "magnet:?xt=urn:btih:" + torrent.InfoHash;

    /// <summary>The file tree's view of a torrent's files: the index is the file's id.</summary>
    public static List<RemoteFileDto> TreeFiles(IEnumerable<TorrentFileDto> files)
        => files.Select(file => new RemoteFileDto
        {
            FileId = file.Index.ToString(CultureInfo.InvariantCulture),
            RelativePath = file.Path,
            Size = file.Size,
            Selectable = true,
        }).ToList();

    /// <summary>The wanted files' ids, for the tree's starting choice.</summary>
    public static List<string> WantedIds(IEnumerable<TorrentFileDto> files)
        => files.Where(file => file.Wanted).Select(file => file.Index.ToString(CultureInfo.InvariantCulture)).ToList();

    /// <summary>The tree's choice as torrent file indexes; null when it can't be read (an id that isn't an index).</summary>
    public static List<int>? Indexes(RemoteFileChoice choice, IEnumerable<TorrentFileDto> files)
    {
        if (choice.All) return files.Select(file => file.Index).ToList();
        var indexes = new List<int>();
        foreach (var id in choice.FileIds)
        {
            if (!int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var index)) return null;
            indexes.Add(index);
        }

        return indexes;
    }

    /// <summary>How a file of a torrent stands: "Skipped", "Done", "71%", or "" when not known.</summary>
    public static string FileProgress(TorrentFileDto file)
    {
        if (!file.Wanted) return "Skipped";
        if (file.Done is not { } done) return string.Empty;
        if (file.Size <= 0 || done >= file.Size) return "Done";
        return $"{Math.Min(99, done * 100 / file.Size)}%";
    }

    /// <summary>A torrent can't be moved, renamed, given another address or redownloaded from the list (T1).</summary>
    public static bool BlocksAddressCommands(IEnumerable<JobDto> selection) => selection.Any(IsTorrent);

    /// <summary>"Choose Files…" applies to one torrent whose files are known and that hasn't finished.</summary>
    public static bool CanChooseFiles(IReadOnlyList<JobDto> selection)
        => selection is [{ Torrent: { HasMetadata: true } } job] && job.State is not ("completed" or "cancelled");

    // ---- The main list (T1 step 5) ----

    /// <summary>The most file rows an open torrent row lists; the rest are in Progress Details.</summary>
    public const int MostFileRows = 200;

    /// <summary>A torrent row opens to list its files when it has more than one and they are known.</summary>
    public static bool ShowsFiles(TorrentDto? torrent) => torrent is { HasMetadata: true, FileCount: > 1 };

    /// <summary>The row's primary action (double-click, Enter) is the file choice it waits for.</summary>
    public static bool PrimaryIsChooseFiles(JobDto? job) => job?.Torrent is { HasMetadata: true, AwaitingFileChoice: true };

    /// <summary>The Torrents view shows while torrents apply, and while torrent jobs are on the list whatever the setting.</summary>
    public static bool SidebarShows(bool applies, bool anyTorrentJobs) => applies || anyTorrentJobs;

    /// <summary>A file row's name: the last part of its path.</summary>
    public static string FileRowName(string path)
    {
        var trimmed = path.TrimEnd('/', '\\');
        var cut = trimmed.LastIndexOfAny(['/', '\\']);
        return cut < 0 ? trimmed : trimmed[(cut + 1)..];
    }

    /// <summary>A file row's Status: "Skipped", how far the file is, or, when that isn't known, its torrent's state word.</summary>
    public static string FileRowStatus(TorrentFileDto file, string parentWord)
        => FileProgress(file) is { Length: > 0 } progress ? progress : parentWord;

    /// <summary>The row under the first <see cref="MostFileRows"/> files of a bigger torrent.</summary>
    public static string MoreFiles(int count) => count == 1 ? "1 more file: open Progress Details" : $"{count} more files: open Progress Details";

    /// <summary>What the status bar says after a file choice was sent.</summary>
    public static string ChoiceSaid(string name, int chosen, int total)
    {
        var files = chosen == 1 ? "1 file" : $"{chosen} files";
        return chosen >= total ? $"{name}: downloading all {files}." : $"{name}: downloading {files} of {total}.";
    }

    // ---- Add flows (review fixes) ----

    /// <summary>The provider dialog's focus for torrent inputs: .torrent files when all are, one magnet, else links.</summary>
    public static AddFocus ProviderFocus(IReadOnlyList<string> inputs)
    {
        if (inputs.Count > 0 && inputs.All(input => LinkExtractor.IsTorrentPath(input.Trim()))) return AddFocus.TorrentFiles;
        return inputs is [var only] && LinkExtractor.IsMagnet(only.Trim()) ? AddFocus.Magnet : AddFocus.Links;
    }

    /// <summary>
    /// A path on another computer or a Windows device (<c>\\server\share</c>, <c>//server</c>, <c>\\?\</c>). It is never
    /// opened from here: even reading it sends this Windows account's credentials there.
    /// </summary>
    public static bool IsNetworkOrDevicePath(string input)
    {
        var path = input.Trim();
        return path.Length >= 2 && path[0] is '\\' or '/' && path[1] is '\\' or '/' or '?';
    }

    public const string OnlyLocalTorrentFiles = "Only .torrent files on a drive of this computer can be added.";

    // ---- Asking for files (review fixes)

    /// <summary>
    /// The torrent started waiting for its file choice between two updates of the same row: its file list just came
    /// in. A row first seen waiting (built from a snapshot, after a restart or a server switch) never counts, so a
    /// restart doesn't open a dialog per waiting torrent.
    /// </summary>
    public static bool FileChoiceArrived(JobDto? before, JobDto now)
        => before is not null && before.Id == now.Id && !PrimaryIsChooseFiles(before) && PrimaryIsChooseFiles(now);

    /// <summary>
    /// The torrent whose file choice opens by itself now, or null: the first one whose list came in while this window
    /// watched (<paramref name="arrived"/>), that still waits and hasn't been asked about. None while a dialog is open
    /// or already asking (it waits for the next update then), or while the Host doesn't offer torrents. One at a time;
    /// a cancelled choice is never asked again (the row's status and the bold menu item stay).
    /// </summary>
    public static Guid? FileChoiceToOpen(
        IEnumerable<(JobDto Job, bool Arrived)> rows, IReadOnlySet<Guid> asked, bool torrentsOffered, bool dialogOpen)
        => NextFileChoice(rows, asked, torrentsOffered, _ => false, mayOpenDialog: !dialogOpen)?.JobId;

    /// <summary>
    /// Where the next torrent whose file list came in is chosen, or null (<see cref="FileChoiceToOpen"/> says which
    /// torrents). Its Progress Details window, when one is open (<paramref name="progressWindowOpen"/>), is the one
    /// place: it already shows the files with Start, so it comes to the front on its Files tab and the main window opens
    /// no dialog of its own, even while the main window is hidden or busy with another dialog. Otherwise the main
    /// window's Choose Files dialog, only when it may open one now (<paramref name="mayOpenDialog"/>: shown, no other
    /// dialog open, not asking already). Either way the torrent then counts as asked, once: closing that Progress
    /// Details window later opens nothing (the row's status and the bold menu item stay).
    /// </summary>
    public static (Guid JobId, FileChoicePlace Place)? NextFileChoice(
        IEnumerable<(JobDto Job, bool Arrived)> rows,
        IReadOnlySet<Guid> asked,
        bool torrentsOffered,
        Func<Guid, bool> progressWindowOpen,
        bool mayOpenDialog,
        Func<Guid, bool>? progressWindowComing = null)
    {
        if (!torrentsOffered) return null;
        foreach (var (job, arrived) in rows)
        {
            if (!arrived || !PrimaryIsChooseFiles(job) || !CanChooseFiles([job]) || asked.Contains(job.Id)) continue;
            if (progressWindowOpen(job.Id)) return (job.Id, FileChoicePlace.ProgressWindow);
            // Its window is on its way: it is asked there once it shows, or here once the wait is over.
            if (progressWindowComing?.Invoke(job.Id) == true) continue;
            if (mayOpenDialog) return (job.Id, FileChoicePlace.Dialog);
        }

        return null;
    }

    /// <summary>How long a just-added torrent's file choice waits for the progress window the Host opens for it.</summary>
    public static readonly TimeSpan ProgressWindowGrace = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The Host of this PC opens this download's Progress Details window by itself as it starts (the rule of
    /// Runtime's DownloadWindows: added here, not by the CLI or another device, to start now), and that window may not
    /// be up yet: a magnet's file list can come in before the new window's process has started. So, for a short while
    /// after the add and until the window has been seen, the main window doesn't open its own Choose Files for it
    /// (<see cref="NextFileChoice"/>). Another server's Host opens no window on this PC.
    /// </summary>
    public static bool ProgressWindowComing(JobDto job, bool localHost, bool windowSeen, DateTimeOffset now)
        => localHost && !windowSeen && !job.AddedFromAnotherDevice && job.StartMode == "startNow" &&
           job.Source is not ("cli" or "cliBatch" or "cliImport" or "remote") &&
           now - job.CreatedAt < ProgressWindowGrace;

    /// <summary>How often, at most, a shown file list is read again while its torrent downloads.</summary>
    public const long FileProgressReadMs = 2000;

    /// <summary>A shown file list of a downloading torrent is read again for each file's progress once
    /// <see cref="FileProgressReadMs"/> passed since the last read.</summary>
    public static bool FileProgressDue(JobDto job, bool listShown, long sinceLastReadMs)
        => listShown && job.State == "downloading" && job.Torrent is { HasMetadata: true } && sinceLastReadMs >= FileProgressReadMs;

    /// <summary>
    /// What an open torrent row's file list follows: it is read again when one of these changes (or after Choose
    /// Files); while the torrent downloads, <see cref="FileProgressDue"/> also reads it again.
    /// </summary>
    public static (string State, int FileCount, bool HasMetadata, int Wanted, long WantedBytes) FilesReadKey(JobDto job)
        => (job.State, job.Torrent?.FileCount ?? 0, job.Torrent?.HasMetadata == true, job.Torrent?.WantedCount ?? 0, job.Torrent?.WantedBytes ?? 0);
}
