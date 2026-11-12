using DownloadManager.Contracts;

namespace DownloadManager.Runtime;

/// <summary>
/// UI.R24: the last hour of total download speed, one figure a second, so a window opened later still shows the hour
/// (owner, 2026-11-08: "Keep the hour"). It has no timer: running downloads write it from their progress reports, and
/// the seconds in which nothing reported are filled in when the next report or a reader comes, from the times alone.
/// So an idle Host does no work for it at all. The buffer is the only allocation; a reader gets its own copy.
/// <para>T8: the torrents' upload is kept the same way beside it, from the torrents' own reports (while they download and
/// while they seed), so a window opened later draws the upload line for the hour too.</para>
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

    /// <summary>
    /// T8: how long a torrent's last upload figure stands. The torrent worker reports every second while a window watches
    /// torrents and every 5 seconds otherwise, so this covers the slower pace.
    /// </summary>
    public const int UploadHoldSeconds = 6;

    private readonly Channel _download = new(HoldSeconds);
    private readonly Channel _upload = new(UploadHoldSeconds);

    /// <summary>A total of one kind: its figure for each second of the hour, and each source's last figure.</summary>
    private sealed class Channel(int hold)
    {
        public readonly float[] Values = new float[Seconds];
        /// <summary>Each source's last figure and the second it came in.</summary>
        public readonly Dictionary<Guid, (double Rate, long Second)> Rates = new();
        public readonly int Hold = hold;
        /// <summary>Anything was ever reported (T8: the upload list is left out until then).</summary>
        public bool Used;

        /// <summary>The sum of the sources whose last figure still stands at <paramref name="second"/>.</summary>
        public double TotalAt(long second)
        {
            double total = 0;
            foreach (var (rate, at) in Rates.Values)
            {
                if (second - at <= Hold) total += rate;
            }

            return total;
        }

        /// <summary>Fills the seconds from <paramref name="from"/> to <paramref name="to"/>, which nothing reported in.</summary>
        public void Fill(long from, long to)
        {
            var newest = long.MinValue;
            foreach (var (_, at) in Rates.Values) newest = Math.Max(newest, at);
            for (var s = from; s <= to; s++)
            {
                Values[Slot(s)] = newest != long.MinValue && s - newest <= Hold ? (float)TotalAt(s) : 0f;
            }
        }

        public void Report(Guid id, double rate, long second)
        {
            Used = true;
            Rates[id] = (rate, second);
            Values[Slot(second)] = (float)TotalAt(second);
        }

        public void Stopped(Guid id, long second)
        {
            Rates.Remove(id);
            if (second >= 0) Values[Slot(second)] = (float)TotalAt(second);
        }

        public List<long> Read(long first, long last)
        {
            var values = new List<long>((int)(last - first + 1));
            for (var s = first; s <= last; s++) values.Add((long)Math.Round(Values[Slot(s)]));
            return values;
        }
    }

    /// <summary>
    /// F-12: each running download's own figures, so Progress Details opened during a download shows what came before.
    /// Kept only while it runs (about 42 KB each) and dropped when it stops.
    /// </summary>
    private readonly Dictionary<Guid, JobTrack> _jobs = new();

    private sealed class JobTrack(long first)
    {
        public readonly float[] Values = new float[Seconds];
        public readonly long[] At = new long[Seconds];
        public readonly long First = first;
    }

    /// <summary>A step back of more than this is the clock being set back, not reports arriving out of order.</summary>
    private const int ClockStepSeconds = 10;

    private readonly long _firstSecond;
    private long _openSecond;

    /// <summary>
    /// Seconds added to the wall clock's since it was last set back, so the history's own seconds never go backwards.
    /// The latest second is always the clock's now; older ones keep the times they were written at.
    /// </summary>
    private long _offset;

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
        var rate = Clean(bytesPerSecond);
        _download.Report(jobId, rate, second);
        if (!_jobs.TryGetValue(jobId, out var track)) _jobs[jobId] = track = new JobTrack(second);
        track.Values[Slot(second)] = (float)rate;
        track.At[Slot(second)] = second;
    }

    /// <summary>
    /// The download stopped running, for whatever reason. The seconds before this one still count its last figure
    /// as a running download's; this one no longer does.
    /// </summary>
    public void Stopped(Guid jobId, DateTimeOffset now)
    {
        _jobs.Remove(jobId);
        if (!_download.Rates.ContainsKey(jobId)) return;
        _download.Stopped(jobId, Advance(now));
    }

    /// <summary>T8: a torrent's current upload, while it downloads or seeds. Bytes per second, cleaned as <see cref="Report"/>'s.</summary>
    public void ReportUpload(Guid jobId, double bytesPerSecond, DateTimeOffset now)
    {
        var second = Advance(now);
        if (second < 0) return;
        _upload.Report(jobId, Clean(bytesPerSecond), second);
    }

    /// <summary>T8: the torrent sends no more (it stopped, or stopped seeding). This second no longer counts its figure.</summary>
    public void UploadStopped(Guid jobId, DateTimeOffset now)
    {
        if (!_upload.Rates.ContainsKey(jobId)) return;
        _upload.Stopped(jobId, Advance(now));
    }

    private static double Clean(double bytesPerSecond) => double.IsFinite(bytesPerSecond) && bytesPerSecond > 0 ? bytesPerSecond : 0;

    /// <summary>
    /// The figures from the later of an hour ago and the Host's start up to now, oldest first. The last is the second
    /// still under way, so far.
    /// </summary>
    public SpeedHistoryDto Read(DateTimeOffset now)
    {
        var second = Advance(now);
        if (second < 0) second = _openSecond;
        var first = Math.Max(_firstSecond, second - Seconds + 1);
        return new SpeedHistoryDto
        {
            Start = DateTimeOffset.FromUnixTimeSeconds(first - _offset),
            IntervalSeconds = 1,
            BytesPerSecond = _download.Read(first, second),
            UploadBytesPerSecond = _upload.Used ? _upload.Read(first, second) : null,
        };
    }

    /// <summary>
    /// F-12: one running download's figures since it started running (at most the last hour), by the same hold rule
    /// as the total: a second without a report carries the last figure for <see cref="HoldSeconds"/>, then counts 0.
    /// Null when it isn't running.
    /// </summary>
    public SpeedHistoryDto? Read(Guid jobId, DateTimeOffset now)
    {
        if (!_jobs.TryGetValue(jobId, out var track)) return null;
        var second = Advance(now);
        if (second < 0) second = _openSecond;
        var first = Math.Max(track.First, second - Seconds + 1);
        var values = new List<long>((int)Math.Max(0, second - first + 1));
        (double Rate, long Second) last = (0, long.MinValue);
        for (var s = first; s <= second; s++)
        {
            var slot = Slot(s);
            if (track.At[slot] == s) last = (track.Values[slot], s);
            values.Add(last.Second != long.MinValue && s - last.Second <= HoldSeconds ? (long)Math.Round(last.Rate) : 0);
        }

        return new SpeedHistoryDto { JobId = jobId, Start = DateTimeOffset.FromUnixTimeSeconds(first - _offset), IntervalSeconds = 1, BytesPerSecond = values };
    }

    /// <summary>
    /// Moves to <paramref name="now"/>'s second, filling those passed over. Returns it, or -1 for a time a little before
    /// the open second (a report that arrived late), which then writes nothing. A clock set back further carries on from
    /// the open second, so the graph doesn't stand still until the clock catches up.
    /// </summary>
    private long Advance(DateTimeOffset now)
    {
        var second = now.ToUnixTimeSeconds() + _offset;
        if (second < _openSecond)
        {
            if (_openSecond - second <= ClockStepSeconds) return -1;
            _offset += _openSecond - second;
            return _openSecond;
        }
        if (second == _openSecond) return second;

        // Only the last hour matters; a longer gap rewrites every slot once. Past the newest report's hold every
        // second is 0, so the sums stop there.
        // A source that went quiet (or hung) stops counting on its own after its hold, while others keep reporting.
        var from = Math.Max(_openSecond + 1, second - Seconds + 1);
        _download.Fill(from, second);
        _upload.Fill(from, second);

        _openSecond = second;
        return second;
    }

    private static int Slot(long second) => (int)(((second % Seconds) + Seconds) % Seconds);
}
