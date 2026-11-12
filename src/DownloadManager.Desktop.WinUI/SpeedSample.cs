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

    /// <summary>One download's figure for the graph at <paramref name="now"/>.</summary>
    public static double JobRate(string state, double? bytesPerSecond, DateTimeOffset updatedAt, DateTimeOffset now) =>
        IsActive(state) && now - updatedAt <= Fresh && bytesPerSecond is { } rate && double.IsFinite(rate) ? Math.Max(0, rate) : 0;

    /// <summary>The graph's sample: null (a gap) while not connected, otherwise the downloads' figures added up.</summary>
    public static double? Total(bool connected, IEnumerable<double> rates) => connected ? rates.Sum() : null;
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
