using System.Globalization;
using DownloadManager.Contracts;

namespace DownloadManager.Desktop.WinUI;

/// <summary>Which of the Torrents view's three groups a torrent is in (<see cref="TorrentListRules.GroupOf"/>).</summary>
internal enum TorrentGroup
{
    Downloading,
    Seeding,
    Stopped,
}

/// <summary>How many torrents the sidebar's Torrents items count.</summary>
internal readonly record struct TorrentCounts(int All, int Downloading, int Seeding, int Stopped)
{
    public int Of(string tag) => tag switch
    {
        TorrentListRules.DownloadingTag => Downloading,
        TorrentListRules.SeedingTag => Seeding,
        TorrentListRules.StoppedTag => Stopped,
        _ => All,
    };
}

/// <summary>
/// The Host's seeding policy as Options sets it (TorrentSeedRatio, TorrentSeedHours): seeding ends at the ratio or after
/// the hours, whichever comes first; 0 is no limit of that kind. It applies to every seeding torrent, also one started by
/// hand (Runtime's TorrentSeeding).
/// </summary>
internal readonly record struct SeedGoal(double Ratio, double Hours)
{
    public static SeedGoal None => default;

    public static SeedGoal From(SettingsDto? settings)
        => settings is null ? None : new SeedGoal(Clean(settings.TorrentSeedRatio), Clean(settings.TorrentSeedHours));

    /// <summary>
    /// T3: the goal that holds for this torrent: its own (torrent.setOptions) when it has one, Options' otherwise; a
    /// torrent set never to stop has none.
    /// </summary>
    public SeedGoal For(JobDto job)
    {
        if (job.Torrent is not { } torrent) return this;
        var value = torrent.SeedGoalValue is { } v ? Clean(v) : 0;
        return torrent.SeedGoal switch
        {
            "ratio" => new SeedGoal(value, 0),
            "hours" => new SeedGoal(0, value),
            "never" => None,
            _ => this,
        };
    }

    /// <summary>The torrent has a goal of its own, not Options'.</summary>
    public static bool IsOwn(JobDto job) => job.Torrent?.SeedGoal is "ratio" or "hours" or "never";

    public bool HasRatio => Ratio > 0;

    public bool HasHours => Hours > 0;

    public bool IsSet => HasRatio || HasHours;

    private static double Clean(double value) => double.IsFinite(value) && value > 0 ? value : 0;
}

/// <summary>
/// The main list's torrent views and columns (DT-4, DT-5, DT-6, DT-10; docs/plans/torrent-mockups/a-main-window.html and
/// c-torrent-details.html): the Downloading, Seeding and Stopped views with their counts, the Seeds, Peers, Up and Ratio
/// columns, a seeding torrent's way to its seeding goal, and the magnet a torrent row copies. Kept free of WinUI so they
/// are tested (tests/DownloadManager.Core.Tests links this file).
/// </summary>
internal static class TorrentListRules
{
    public const string DownloadingTag = "torrents:downloading";
    public const string SeedingTag = "torrents:seeding";
    public const string StoppedTag = "torrents:stopped";

    /// <summary>The views under Torrents, in the sidebar's order.</summary>
    public static readonly IReadOnlyList<string> SubTags = [DownloadingTag, SeedingTag, StoppedTag];

    /// <summary>The columns only the torrent views show.</summary>
    public static readonly IReadOnlyList<string> ColumnKeys = ["seeds", "peers", "up", "ratio"];

    public static bool IsSubTag(string? category) => category is DownloadingTag or SeedingTag or StoppedTag;

    /// <summary>Torrents, or one of the views under it.</summary>
    public static bool IsTorrentView(string? category) => category == TorrentRules.SidebarTag || IsSubTag(category);

    /// <summary>
    /// The order the list is put in. A sort by Seeds, Peers, Up or Ratio holds in the torrent views only: elsewhere those
    /// columns are hidden, so the list runs in the order of addition (newest first, as it starts) and the chosen sort
    /// comes back with the torrent views.
    /// </summary>
    public static (string Key, bool Ascending) ShownSort(string key, bool ascending, string? category)
        => ColumnKeys.Contains(key) && !IsTorrentView(category) ? ("added", false) : (key, ascending);

    /// <summary>
    /// Seeding: finished and shared now. Downloading: not finished, and running or waiting its turn. Stopped: everything
    /// else (paused, failed, cancelled, or finished and not seeding). Null for a job that isn't a torrent.
    /// </summary>
    public static TorrentGroup? GroupOf(JobDto? job)
    {
        if (job?.Torrent is null) return null;
        if (TorrentSeedRules.IsSeeding(job)) return TorrentGroup.Seeding;
        return job.State is "paused" or "failed" or "cancelled" or "completed" ? TorrentGroup.Stopped : TorrentGroup.Downloading;
    }

    public static bool Matches(string category, JobDto? job) => GroupOf(job) is { } group && category switch
    {
        TorrentRules.SidebarTag => true,
        DownloadingTag => group == TorrentGroup.Downloading,
        SeedingTag => group == TorrentGroup.Seeding,
        StoppedTag => group == TorrentGroup.Stopped,
        _ => false,
    };

    public static TorrentCounts Count(IEnumerable<JobDto> jobs)
    {
        int all = 0, downloading = 0, seeding = 0, stopped = 0;
        foreach (var job in jobs)
        {
            switch (GroupOf(job))
            {
                case TorrentGroup.Downloading: downloading++; break;
                case TorrentGroup.Seeding: seeding++; break;
                case TorrentGroup.Stopped: stopped++; break;
                default: continue;
            }

            all++;
        }

        return new TorrentCounts(all, downloading, seeding, stopped);
    }

    /// <summary>A sidebar item's count: empty for none, so an empty view isn't marked.</summary>
    public static string CountText(int count) => count > 0 ? count.ToString("N0", CultureInfo.CurrentCulture) : string.Empty;

    /// <summary>What a screen reader says for a sidebar item: "Seeding, 2 torrents".</summary>
    public static string SpokenItem(string title, int count)
        => count switch { 0 => $"{title}, none", 1 => $"{title}, 1 torrent", _ => $"{title}, {count.ToString("N0", CultureInfo.CurrentCulture)} torrents" };

    // ---- Columns ----

    /// <summary>The torrent is on the engine now, so its peer counts mean something: it downloads or seeds.</summary>
    public static bool IsRunning(JobDto job) => job.Torrent is not null && (TorrentRules.IsActive(job) || TorrentSeedRules.IsSeeding(job));

    /// <summary>The seeds connected now; null while it doesn't run.</summary>
    public static int? Seeds(JobDto job) => IsRunning(job) ? Math.Max(0, job.Torrent!.Seeds) : null;

    /// <summary>The other peers connected now (the Host's peer count holds the seeds too); null while it doesn't run.</summary>
    public static int? OtherPeers(JobDto job) => IsRunning(job) ? Math.Max(0, job.Torrent!.Peers - Math.Max(0, job.Torrent.Seeds)) : null;

    public static string CountCell(int? count) => count is { } shown ? shown.ToString(CultureInfo.CurrentCulture) : string.Empty;

    /// <summary>What it sends now, in bytes per second; null while nothing is sent.</summary>
    public static double? UploadRate(JobDto job)
        => job.Torrent is { UploadRate: var rate } && double.IsFinite(rate) && rate >= 1 ? rate : null;

    /// <summary>The Up column: "640 KB/s", or empty while nothing is sent.</summary>
    public static string UpCell(JobDto job) => UploadRate(job) is { } rate ? $"{UiFormat.Bytes((long)rate)}/s" : string.Empty;

    /// <summary>The Ratio column: "0.71" for any torrent, empty for anything else.</summary>
    public static string RatioCell(JobDto job) => job.Torrent is { } torrent ? TorrentSeedRules.Ratio(torrent.Ratio) : string.Empty;

    // ---- The seeding goal ----

    /// <summary>
    /// How far a seeding torrent is to its goal, 0 to 1: the nearer of the ratio and the time, since the first reached
    /// ends seeding. Null when it doesn't seed or there is no goal.
    /// </summary>
    public static double? GoalProgress(JobDto job, SeedGoal goal, DateTimeOffset now)
    {
        goal = goal.For(job);
        if (!TorrentSeedRules.IsSeeding(job) || !goal.IsSet) return null;
        var torrent = job.Torrent!;
        var progress = 0d;
        if (goal.HasRatio && double.IsFinite(torrent.Ratio)) progress = Math.Max(progress, torrent.Ratio / goal.Ratio);
        if (goal.HasHours && torrent.SeedingSince is { } since) progress = Math.Max(progress, (now - since).TotalHours / goal.Hours);
        return Math.Clamp(progress, 0, 1);
    }

    /// <summary>
    /// How long until seeding ends by itself: the sooner of the time left and, while it sends, the time the rest of the
    /// ratio takes at its current rate. Zero once a goal is reached; null when it doesn't seed or nothing can be said.
    /// </summary>
    public static TimeSpan? TimeToGoal(JobDto job, SeedGoal goal, DateTimeOffset now)
    {
        goal = goal.For(job);
        if (!TorrentSeedRules.IsSeeding(job) || !goal.IsSet) return null;
        var torrent = job.Torrent!;
        TimeSpan? best = null;
        if (goal.HasHours && torrent.SeedingSince is { } since)
        {
            var left = since + TimeSpan.FromHours(goal.Hours) - now;
            best = left < TimeSpan.Zero ? TimeSpan.Zero : left;
        }

        if (goal.HasRatio && torrent.WantedBytes > 0)
        {
            var missing = goal.Ratio * torrent.WantedBytes - torrent.Uploaded;
            TimeSpan? byRatio = missing <= 0 ? TimeSpan.Zero
                : UploadRate(job) is { } rate && missing / rate < TimeSpan.MaxValue.TotalSeconds / 2 ? TimeSpan.FromSeconds(Math.Ceiling(missing / rate))
                : null;
            if (byRatio is { } ratioLeft && (best is null || ratioLeft < best)) best = ratioLeft;
        }

        return best;
    }

    /// <summary>"Ratio 1.00 or 48 hours, whichever comes first", "Ratio 1.00", "48 hours"; null with no goal.</summary>
    public static string? GoalText(SeedGoal goal)
    {
        var ratio = goal.HasRatio ? $"Ratio {TorrentSeedRules.Ratio(goal.Ratio)}" : null;
        var hours = goal.HasHours ? Hours(goal.Hours) : null;
        return (ratio, hours) switch
        {
            ({ } r, { } h) => $"{r} or {h}, whichever comes first",
            ({ } r, null) => r,
            (null, { } h) => h,
            _ => null,
        };
    }

    private static string Hours(double hours)
        => hours == 1 ? "1 hour" : $"{hours.ToString("0.##", CultureInfo.CurrentCulture)} hours";

    /// <summary>
    /// Progress Details' "Seeding goal" line: the goal, and while it seeds how long until it is reached ("about 1h 10m
    /// left", "reached"). Says when seeding after a download is off. Null when the settings aren't known or there is no goal.
    /// </summary>
    public static string? GoalLine(JobDto job, SettingsDto? settings, DateTimeOffset now)
    {
        if (job.Torrent is null || settings is null) return null;
        var goal = SeedGoal.From(settings).For(job);
        if (job.Torrent.SeedGoal == "never") return "Never stops (this torrent's own option)";
        if (GoalText(goal) is not { } text) return null;
        if (SeedGoal.IsOwn(job)) text += " (this torrent's own option)";
        if (TorrentSeedRules.IsSeeding(job))
        {
            return TimeToGoal(job, goal, now) switch
            {
                { } left when left <= TimeSpan.Zero => $"{text} · reached",
                { } left => $"{text} · about {DurationText.TimeLeft(left)} left",
                null => text,
            };
        }

        return settings.TorrentSeedAfterDownload || job.State == "completed" ? text : $"{text} · seeding after a download is off";
    }

    /// <summary>Progress Details' "Ratio" line: "0.71 · uploaded 1.0 GB".</summary>
    public static string RatioLine(TorrentDto torrent)
        => $"{TorrentSeedRules.Ratio(torrent.Ratio)} · uploaded {UiFormat.Bytes(Math.Max(0, torrent.Uploaded))}";

    // ---- Copy Magnet Link ----

    /// <summary>The torrent's magnet, as it was added (with its trackers); the short magnet when the job holds none.</summary>
    public static string? MagnetOf(JobDto job)
    {
        if (job.Torrent is not { } torrent) return null;
        var url = job.Url?.Trim();
        if (url is { Length: > 0 } && url.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)) return url;
        return torrent.InfoHash.Length > 0 ? TorrentRules.ShortMagnet(torrent) : null;
    }

    /// <summary>The selected torrents' magnets, one a line; null when a selected job isn't a torrent or has none.</summary>
    public static string? MagnetsToCopy(IReadOnlyList<JobDto> selection)
    {
        if (selection.Count == 0) return null;
        var magnets = new List<string>(selection.Count);
        foreach (var job in selection)
        {
            if (MagnetOf(job) is not { } magnet) return null;
            magnets.Add(magnet);
        }

        return string.Join(Environment.NewLine, magnets);
    }

    public static string CopyItemText(int count) => count > 1 ? "Copy Magnet Links" : "Copy Magnet Link";

    public static string CopiedSaid(string? name, int count)
        => count == 1 && name is { Length: > 0 } ? $"Copied the magnet link of '{name}'." : $"Copied {count} magnet links.";
}
