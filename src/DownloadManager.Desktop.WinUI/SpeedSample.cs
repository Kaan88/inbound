namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// UI.B20: the one rule both speed graphs sample by, once a second on the window's own clock. A running download
/// whose last update is recent counts at that update's rate until the next one arrives; a download that has gone quiet
/// (stalled, connecting, waiting to retry) or stopped counts as 0. Only a lost Host connection is a gap.
/// </summary>
internal static class SpeedSample
{
    /// <summary>How long an update's rate is held before the download counts as transferring nothing.</summary>
    public static readonly TimeSpan Fresh = TimeSpan.FromSeconds(10);

    public static bool IsActive(string state) => state is "connecting" or "downloading" or "verifying" or "retryWaiting";

    /// <summary>
    /// Review 5:E1: a row whose figures change on their own (a download under way, a seeding torrent). While the Host is
    /// lost its speed, time left and status are not known; the web UI's notCurrentView by the same rule.
    /// </summary>
    public static bool Moves(string state, bool seeding) => IsActive(state) || IsSeeding(state, seeding);

    /// <summary>One download's figure for the graph at <paramref name="now"/>.</summary>
    public static double JobRate(string state, double? bytesPerSecond, DateTimeOffset updatedAt, DateTimeOffset now) =>
        IsActive(state) && now - updatedAt <= Fresh && bytesPerSecond is { } rate && double.IsFinite(rate) ? Math.Max(0, rate) : 0;

    /// <summary>The graph's sample: null (a gap) while not connected, otherwise the downloads' figures added up.</summary>
    public static double? Total(bool connected, IEnumerable<double> rates) => connected ? rates.Sum() : null;

    // ---- T8: the torrents' upload ----

    /// <summary>The torrent finished and is shared with other peers now (TorrentSeedRules.IsSeeding's rule).</summary>
    public static bool IsSeeding(string state, bool seeding) => seeding && state == "completed";

    /// <summary>
    /// One torrent's upload figure at <paramref name="now"/>. A downloading torrent's counts while its last update is
    /// recent, as <see cref="JobRate"/>'s. A seeding torrent's counts as the Host last said: its updates don't move
    /// UpdatedAt, and the Host sets it to 0 when seeding stops. Anything else sends nothing.
    /// </summary>
    public static double UploadRate(string state, bool seeding, double bytesPerSecond, DateTimeOffset updatedAt, DateTimeOffset now)
    {
        if (!double.IsFinite(bytesPerSecond) || bytesPerSecond <= 0) return 0;
        if (IsSeeding(state, seeding)) return bytesPerSecond;
        return IsActive(state) && now - updatedAt <= Fresh ? bytesPerSecond : 0;
    }

    /// <summary>
    /// The upload line, its legend and its figure show while a torrent downloads or seeds (owner, 2026-11-12: "it can be
    /// hidden if no torrents are active").
    /// </summary>
    public static bool TorrentRuns(bool torrent, string state, bool seeding) => torrent && (IsActive(state) || IsSeeding(state, seeding));

    /// <summary>
    /// The upload figure on the bar: the newest sample, or, with none (the graph took no upload sample yet, say a
    /// torrent that seeds and sends nothing in a window just opened), the figure worked out from the jobs now, as the web
    /// UI shows it. Null (a dash) only while neither is known.
    /// </summary>
    public static double? UploadFigure(double? sampled, double? live) => sampled ?? live;

    /// <summary>
    /// The one-second sample runs while this job moves: a download under way, or a seeding torrent that sends something.
    /// A torrent that seeds and sends nothing wakes nothing (the Host's history fills the wait in when sampling resumes).
    /// </summary>
    public static bool KeepsSampling(string state, bool seeding, double uploadBytesPerSecond)
        => IsActive(state) || IsSeeding(state, seeding) && double.IsFinite(uploadBytesPerSecond) && uploadBytesPerSecond >= 1;
}

/// <summary>
/// Review 4:N4: how far this PC's clock is ahead of the Host's (negative: behind), so <see cref="SpeedSample.JobRate"/>
/// compares an update's UpdatedAt (the Host's clock) with the Host's "now", not this PC's. Another server's clock can be
/// off by more than <see cref="SpeedSample.Fresh"/>, and then every rate counted as 0 while the list moved. The web
/// UI's ServerClock (webui list.ts) by the same rule: the smallest gap seen between the time here and a job update's
/// UpdatedAt. An update reaches the window within a moment of being made, so that is the clocks' difference plus the
/// moment. Zero until an update was seen. Observed on the connection's reader, read on the window's thread.
/// </summary>
public sealed class ServerClock
{
    private long _smallestTicks = long.MaxValue;

    public void Observe(DateTimeOffset updatedAt, DateTimeOffset now)
    {
        var gap = (now - updatedAt).Ticks;
        var seen = Interlocked.Read(ref _smallestTicks);
        while (gap < seen)
        {
            var was = Interlocked.CompareExchange(ref _smallestTicks, gap, seen);
            if (was == seen) return;
            seen = was;
        }
    }

    /// <summary>Another Host from now on (a retargeted connection): nothing seen of the last one's clock counts.</summary>
    public void Reset() => Interlocked.Exchange(ref _smallestTicks, long.MaxValue);

    public TimeSpan Offset => Interlocked.Read(ref _smallestTicks) is var ticks && ticks != long.MaxValue ? TimeSpan.FromTicks(ticks) : TimeSpan.Zero;

    /// <summary>The Host's time at this PC's <paramref name="now"/>.</summary>
    public DateTimeOffset ServerNow(DateTimeOffset now) => now - Offset;
}

/// <summary>
/// Audit M11: a Progress or package chart takes no samples while its download waits (paused or queued) with the Host
/// connected, so an open window on a paused download wakes nothing. The line through the wait is known: 0. When the
/// download moves again, ends, or the connection drops, the wait's seconds are added as zeros at once, so the graph
/// shows a flat 0 rather than a gap. A dropped connection ends the wait at that moment; what follows is a gap.
/// </summary>
internal sealed class ChartIdle
{
    private DateTimeOffset? _from;

    /// <summary>True while a wait is under way.</summary>
    public bool Waiting => _from is not null;

    /// <summary>
    /// Called on every change. <paramref name="waiting"/>: the download (or a package member) can still move but none
    /// does. Returns the zeros to add now (one a second, after the wait's first sample and before
    /// <paramref name="now"/>), and whether a wait begins now, in which case the caller takes one sample at once.
    /// </summary>
    public (IReadOnlyList<DateTimeOffset> Zeros, bool Begins) Update(bool waiting, bool connected, DateTimeOffset now)
    {
        if (waiting && connected)
        {
            if (_from is not null) return ([], false);
            _from = now;
            return ([], true);
        }
        if (_from is not { } from) return ([], false);
        _from = null;
        return (Seconds(from, now), false);
    }

    private static List<DateTimeOffset> Seconds(DateTimeOffset from, DateTimeOffset now)
    {
        var zeros = new List<DateTimeOffset>();
        var first = from.AddSeconds(1);
        // The graph keeps an hour; older seconds would be dropped at once.
        var oldest = now.AddSeconds(-SpeedHistoryLength);
        if (first < oldest) first = from.AddSeconds(Math.Ceiling((oldest - from).TotalSeconds));
        for (var at = first; at < now; at = at.AddSeconds(1)) zeros.Add(at);
        return zeros;
    }

    private const int SpeedHistoryLength = 3600;
}
