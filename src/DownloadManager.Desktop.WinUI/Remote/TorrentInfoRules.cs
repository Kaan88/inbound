using System.Globalization;
using DownloadManager.Contracts;

namespace DownloadManager.Desktop.WinUI;

/// <summary>What the main window's Torrent Info pane says for one torrent; "—" where a live figure isn't known now.</summary>
internal sealed record TorrentInfoView(
    string Name,
    string Status,
    double Percent,
    string Progress,
    string Down,
    string Up,
    string Swarm,
    string Ratio,
    string? Goal,
    string TimeLeft,
    string Folder,
    string InfoHash,
    string Added,
    string Trackers);

/// <summary>
/// T7: the Torrent Info pane on the right of the main window's list (View › Torrent Info). It shows when it is turned
/// on and exactly one torrent is selected, and gives the facts Progress Details' Overview has, from what the list already
/// knows. Only the trackers' summary and the v2 info hash are asked for, and only while the pane is seen: the trackers on
/// the torrent's own progress updates at most every <see cref="TorrentControlRules.DetailsReadMs"/> while it runs (no
/// timer), the details once per <see cref="DetailsKey"/> until the v2 hash is known. Kept free of WinUI so they are tested (tests/DownloadManager.Core.Tests links
/// this file).
/// </summary>
internal static class TorrentInfoRules
{
    /// <summary>The pane's width when the list leaves room for it.</summary>
    public const double PreferredWidth = 320;

    /// <summary>The narrowest the pane gets before the list gives way.</summary>
    public const double LeastWidth = 260;

    /// <summary>
    /// The narrowest the list may become beside the pane while its columns' own minimum isn't known; below that the pane
    /// stays away.
    /// </summary>
    public const double LeastListWidth = 520;

    public const string Dash = "—";

    /// <summary>
    /// The torrent the pane shows: the pane is on, the download list shows (not My provider's full view) and exactly one
    /// download is selected, a torrent. Null otherwise.
    /// </summary>
    public static JobDto? Shown(bool on, bool listShown, IReadOnlyList<JobDto> selection)
        => on && listShown && selection.Count == 1 && selection[0].Torrent is not null ? selection[0] : null;

    /// <summary>
    /// The pane's width beside a list area <paramref name="available"/> wide whose columns need
    /// <paramref name="listMinimum"/>: as wide as <see cref="PreferredWidth"/> when the columns still fit, narrower down to
    /// <see cref="LeastWidth"/> so they fit. Null (no pane) when the columns wouldn't fit beside even the narrowest pane, so
    /// selecting a torrent never makes the list scroll sideways (the user can narrow a column or widen the window); also
    /// when the list would be left under <see cref="LeastListWidth"/> or the width isn't known yet.
    /// </summary>
    public static double? Width(double available, double listMinimum)
    {
        if (!double.IsFinite(available) || available <= 0) return null;
        var least = double.IsFinite(listMinimum) ? Math.Max(listMinimum, LeastListWidth) : LeastListWidth;
        if (available - LeastWidth < least) return null;
        return Math.Min(PreferredWidth, available - least);
    }

    /// <summary>The pane is on screen to be read: it shows a torrent in a window that shows and isn't minimized.</summary>
    public static bool Seen(bool windowShown, bool minimized, JobDto? shown) => windowShown && !minimized && shown is not null;

    /// <summary>
    /// The pane asks the Host to report this torrent every second (<c>torrent.watch</c>): it is seen and the torrent runs
    /// (downloads, connects, checks or seeds). A stopped or finished torrent that doesn't seed has nothing that moves.
    /// </summary>
    public static bool Watches(bool seen, JobDto? shown) => seen && TorrentControlRules.CanAnnounce(shown);

    /// <summary>
    /// The trackers' summary is read now: the pane is seen, the Host has torrents, the torrent may be on the engine, and
    /// either this torrent's state changed since the last read (<paramref name="keyChanged"/>, a new selection too) or
    /// <see cref="TorrentControlRules.DetailsReadMs"/> passed. Called on the torrent's own updates, never from a timer.
    /// </summary>
    public static bool TrackersDue(bool seen, bool offered, JobDto? shown, bool keyChanged, long sinceLastReadMs)
        => seen && offered && TorrentDetailsRules.MayBeRunning(shown)
           && (keyChanged || sinceLastReadMs >= TorrentControlRules.DetailsReadMs);

    /// <summary>
    /// The details (for the v2 info hash) are read on a Host with the torrent controls while the hash isn't known, once per
    /// <see cref="DetailsKey"/>: the Host gives the v2 hash only while the torrent runs on the engine, so a torrent first
    /// shown paused, queued or without its metadata is asked again once that changes.
    /// </summary>
    public static bool DetailsDue(bool seen, bool controls, bool readForThisKey, bool v2Known)
        => seen && controls && !readForThisKey && !v2Known;

    /// <summary>What a details read holds for: the torrent, its state, whether it seeds and whether it has its metadata.</summary>
    public static (Guid Job, string State, bool Seeding, bool Metadata) DetailsKey(JobDto job)
        => (job.Id, job.State, job.Torrent?.Seeding == true, job.Torrent?.HasMetadata == true);

    /// <summary>"2 of 3 working"; what the pane says while the list isn't known or the torrent doesn't run.</summary>
    public static string TrackersText(IReadOnlyList<TorrentTrackerDto>? trackers, bool mayRun, bool offered)
    {
        if (!offered) return Dash;
        if (!mayRun) return "Shown while the torrent runs";
        if (trackers is null) return Dash;
        if (trackers.Count == 0) return "None: peers come from the DHT and other peers";
        var working = trackers.Count(tracker => tracker.Status == "working");
        return $"{working.ToString("N0", CultureInfo.CurrentCulture)} of {trackers.Count.ToString("N0", CultureInfo.CurrentCulture)} working";
    }

    /// <summary>How much of the wanted files is in, 0 to 100; 100 once complete.</summary>
    public static double Percent(JobDto job)
    {
        if (job.State == "completed") return 100;
        var wanted = Wanted(job);
        if (wanted <= 0) return 0;
        return Math.Clamp(Math.Floor(job.ReceivedBytes * 100.0 / wanted), 0, 100);
    }

    /// <summary>"41% · 392 MB of 1.0 GB"; "100% · 1.0 GB" once complete; "Getting file list" before the metadata.</summary>
    public static string ProgressText(JobDto job)
    {
        if (job.Torrent is { HasMetadata: false }) return "Getting file list";
        var wanted = Wanted(job);
        var percent = Percent(job).ToString("0", CultureInfo.CurrentCulture);
        if (job.State == "completed") return wanted > 0 ? $"100% · {UiFormat.Bytes(wanted)}" : "100%";
        return wanted > 0
            ? $"{percent}% · {UiFormat.Bytes(Math.Max(0, job.ReceivedBytes))} of {UiFormat.Bytes(wanted)}"
            : UiFormat.Bytes(Math.Max(0, job.ReceivedBytes));
    }

    /// <summary>A speed as the pane shows it: "6.1 MB/s", and "0 B/s" when nothing moves.</summary>
    public static string RateText(double bytesPerSecond)
        => double.IsFinite(bytesPerSecond) && bytesPerSecond >= 1 ? $"{UiFormat.Bytes((long)bytesPerSecond)}/s" : ZeroRate;

    /// <summary>A known zero speed ("0 B/s").</summary>
    public static string ZeroRate => $"{UiFormat.Bytes(0)}/s";

    /// <summary>
    /// The download speed while it downloads ("6.1 MB/s", "0 B/s" when nothing comes); "0 B/s" once it is complete,
    /// seeding or not (T7.B1: nothing is left to download); "—" otherwise (paused, queued, failed).
    /// </summary>
    public static string DownText(JobDto job)
        => job.Torrent is null ? Dash
            : TorrentRules.IsActive(job) ? RateText(job.BytesPerSecond ?? 0)
            : job.State == "completed" ? ZeroRate
            : Dash;

    /// <summary>The upload speed while it runs ("640 KB/s", "0 B/s" when nothing goes), else "—".</summary>
    public static string UpText(JobDto job)
        => job.Torrent is { } torrent && TorrentListRules.IsRunning(job) ? RateText(torrent.UploadRate) : Dash;

    /// <summary>"12 seeds · 29 peers" (the other peers, without the seeds) while it runs, else "—".</summary>
    public static string SwarmText(JobDto job)
    {
        if (TorrentListRules.Seeds(job) is not { } seeds || TorrentListRules.OtherPeers(job) is not { } peers) return Dash;
        var seedWord = seeds == 1 ? "1 seed" : $"{seeds.ToString("N0", CultureInfo.CurrentCulture)} seeds";
        return $"{seedWord} · {TorrentRules.Peers(peers)}";
    }

    /// <summary>The time left while it downloads and it is known, else "—". A seeding torrent's goal line says its own.</summary>
    public static string TimeLeftText(JobDto job)
        => TorrentRules.IsActive(job) && DurationText.TimeLeft(job.EstimatedRemaining) is { Length: > 0 } left ? left : Dash;

    /// <summary>Where its files go: the save folder, or the finished torrent's own folder or file's folder.</summary>
    public static string FolderText(JobDto job)
    {
        if (!string.IsNullOrWhiteSpace(job.DestinationDirectory)) return job.DestinationDirectory;
        if (string.IsNullOrWhiteSpace(job.FinalFilePath)) return Dash;
        return Path.GetDirectoryName(job.FinalFilePath) is { Length: > 0 } folder ? folder : job.FinalFilePath;
    }

    /// <summary>The pane's words for one torrent. <paramref name="stateWord"/> is the list's word for its state.</summary>
    public static TorrentInfoView Describe(JobDto job, string stateWord, SettingsDto? settings, DateTimeOffset now,
        string? infoHashV2, IReadOnlyList<TorrentTrackerDto>? trackers, bool offered)
    {
        var torrent = job.Torrent ?? new TorrentDto();
        var name = !string.IsNullOrWhiteSpace(job.FileName) ? job.FileName : torrent.Name.Length > 0 ? torrent.Name : "Torrent";
        var status = torrent.Checking ? "Checking files" : TorrentRules.StatusText(job) ?? stateWord;
        return new TorrentInfoView(
            name,
            status,
            Percent(job),
            ProgressText(job),
            DownText(job),
            UpText(job),
            SwarmText(job),
            TorrentListRules.RatioLine(torrent),
            TorrentListRules.GoalLine(job, settings, now),
            TimeLeftText(job),
            FolderText(job),
            TorrentControlRules.HashText(torrent.InfoHash, infoHashV2),
            TorrentControlRules.AddedText(job.CreatedAt, torrent.Source),
            TrackersText(trackers, TorrentDetailsRules.MayBeRunning(job), offered));
    }

    /// <summary>What the trackers' summary holds for: the torrent and its state. A change reads them again.</summary>
    public static (Guid Job, string State, bool Seeding) ReadKey(JobDto job) => (job.Id, job.State, job.Torrent?.Seeding == true);

    private static long Wanted(JobDto job)
        => job.TotalBytes is > 0 ? job.TotalBytes.Value : Math.Max(0, job.Torrent?.WantedBytes ?? 0);
}
