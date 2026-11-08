using DownloadManager.Contracts;

namespace DownloadManager.Runtime;

/// <summary>
/// UI.R24: the last hour of total download speed, one figure a second, so a window opened later still shows the hour
/// (owner, 2026-11-08: "Keep the hour"). It has no timer: running downloads write it from their progress reports, and
/// the seconds in which nothing reported are filled in when the next report or a reader comes, from the times alone.
/// So an idle Host does no work for it at all. The buffer is the only allocation; a reader gets its own copy.
/// <para>Not thread-safe: the service calls it under its own lock.</para>
/// </summary>
public sealed class SpeedHistory
{
    public const int Seconds = 3600;

    /// <summary>
    /// How long a running download's last figure stands for the seconds after it without a report. Reports come several
    /// times a second while bytes arrive; past this, nothing arriving means nothing was downloaded.
    /// </summary>
    private const int HoldSeconds = 2;

    private readonly float[] _values = new float[Seconds];

    /// <summary>Each running download's last figure and the second it came in.</summary>
    private readonly Dictionary<Guid, (double Rate, long Second)> _rates = new();
    private readonly long _firstSecond;
    private long _openSecond;

    public SpeedHistory(DateTimeOffset start)
    {
        _firstSecond = start.ToUnixTimeSeconds();
        _openSecond = _firstSecond;
    }

    /// <summary>A running download's current speed. Bytes per second; anything not finite or below zero counts as 0.</summary>
    public void Report(Guid jobId, double bytesPerSecond, DateTimeOffset now)
    {
        var second = Advance(now);
        if (second < 0) return;
        var rate = double.IsFinite(bytesPerSecond) && bytesPerSecond > 0 ? bytesPerSecond : 0;
        _rates[jobId] = (rate, second);
        _values[Slot(second)] = (float)TotalAt(second);
    }

    /// <summary>
    /// The download stopped running, for whatever reason. The seconds before this one still count its last figure
    /// as a running download's; this one no longer does.
    /// </summary>
    public void Stopped(Guid jobId, DateTimeOffset now)
    {
        if (!_rates.ContainsKey(jobId)) return;
        var second = Advance(now);
        _rates.Remove(jobId);
        if (second < 0) return;
        _values[Slot(second)] = (float)TotalAt(second);
    }

    /// <summary>
    /// The figures from the later of an hour ago and the Host's start up to now, oldest first. The last is the second
    /// still under way, so far.
    /// </summary>
    public SpeedHistoryDto Read(DateTimeOffset now)
    {
        var second = Advance(now);
        if (second < 0) second = _openSecond;
        var first = Math.Max(_firstSecond, second - Seconds + 1);
        var values = new List<long>((int)(second - first + 1));
        for (var s = first; s <= second; s++)
        {
            values.Add((long)Math.Round(_values[Slot(s)]));
        }

        return new SpeedHistoryDto { Start = DateTimeOffset.FromUnixTimeSeconds(first), IntervalSeconds = 1, BytesPerSecond = values };
    }

    /// <summary>
    /// The sum of the downloads whose last figure still stands at <paramref name="second"/>: each holds for
    /// <see cref="HoldSeconds"/> after its own report, so one that went quiet (or hung) stops counting on its own
    /// while others keep reporting.
    /// </summary>
    private double TotalAt(long second)
    {
        double total = 0;
        foreach (var (rate, at) in _rates.Values)
        {
            if (second - at <= HoldSeconds) total += rate;
        }

        return total;
    }

    /// <summary>
    /// Moves to <paramref name="now"/>'s second, filling those passed over. Returns it, or -1 for a time before the
    /// open second (the clock went back), which then writes nothing.
    /// </summary>
    private long Advance(DateTimeOffset now)
    {
        var second = now.ToUnixTimeSeconds();
        if (second < _openSecond) return -1;
        if (second == _openSecond) return second;

        // Only the last hour matters; a longer gap rewrites every slot once. Past the newest report's hold every
        // second is 0, so the sums stop there.
        var from = Math.Max(_openSecond + 1, second - Seconds + 1);
        var newest = long.MinValue;
        foreach (var (_, at) in _rates.Values) newest = Math.Max(newest, at);
        for (var s = from; s <= second; s++)
        {
            _values[Slot(s)] = newest != long.MinValue && s - newest <= HoldSeconds ? (float)TotalAt(s) : 0f;
        }

        _openSecond = second;
        return second;
    }

    private static int Slot(long second) => (int)(((second % Seconds) + Seconds) % Seconds);
}
