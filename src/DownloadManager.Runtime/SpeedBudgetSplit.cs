namespace DownloadManager.Runtime;

/// <summary>What each kind of transfer may use of the overall speed limit (T2), in bytes/s. Never 0, which would be no cap.</summary>
public readonly record struct SpeedShares(long Http, long TorrentDownload, long TorrentUpload)
{
    public long Total => Http + TorrentDownload + TorrentUpload;
}

/// <summary>One kind of transfer in a split: whether any of it runs now, and how fast it went (bytes/s).</summary>
public readonly record struct SpeedDemand(bool Active, double Rate);

/// <summary>
/// T2: the overall speed limit is one budget for HTTP and FTP downloads, torrent downloads and torrent uploads together.
/// A kind that used most of its share wants more; one that used less keeps what it used and some room to grow; the
/// rest is shared out evenly. Each kind keeps a small share even while idle, so one that starts can get going before
/// the next split. The shares never add up to more than the limit (unless the limit is below 3 bytes/s).
/// </summary>
public static class SpeedBudgetSplit
{
    /// <summary>Each kind keeps at least the limit over this.</summary>
    public const int FloorDivisor = 20;
    /// <summary>A kind that used this much of its share wants more.</summary>
    public const double FullShare = 0.8;
    /// <summary>One that used less is given this much more than it used.</summary>
    public const double Headroom = 1.25;

    /// <summary>The split before anything is measured: every kind wants all it can get.</summary>
    public static SpeedShares Initial(long limit, long uploadCap)
        => Split(limit, uploadCap, previous: null, new SpeedDemand(true, 0), new SpeedDemand(true, 0), new SpeedDemand(true, 0));

    /// <param name="limit">The overall speed limit, above 0.</param>
    /// <param name="uploadCap">Options › Torrents' upload limit; 0 is none (the upload's share alone caps it).</param>
    /// <param name="previous">The last split, which the rates were measured under; null for none.</param>
    public static SpeedShares Split(long limit, long uploadCap, SpeedShares? previous, SpeedDemand http, SpeedDemand download, SpeedDemand upload)
    {
        if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit));
        var floor = Math.Max(1, limit / FloorDivisor);
        var caps = new[] { long.MaxValue, long.MaxValue, uploadCap > 0 ? uploadCap : long.MaxValue };
        var demands = new[] { http, download, upload };
        var before = previous is { } p ? new[] { p.Http, p.TorrentDownload, p.TorrentUpload } : null;
        var wants = new double[3];
        for (var i = 0; i < 3; i++)
        {
            var demand = demands[i];
            double want;
            if (!demand.Active) want = floor;
            else if (before is null || demand.Rate >= FullShare * before[i]) want = double.PositiveInfinity;
            else want = Math.Max(floor, demand.Rate * Headroom);
            wants[i] = Math.Min(want, caps[i]);
        }

        // Fill the smallest wants first; each takes at most an even share of what is left.
        var shares = new double[3];
        var remaining = (double)limit;
        var order = Enumerable.Range(0, 3).OrderBy(i => wants[i]).ToArray();
        for (var n = 0; n < 3; n++)
        {
            var i = order[n];
            var give = Math.Min(wants[i], remaining / (3 - n));
            shares[i] = give;
            remaining -= give;
        }

        // What nobody wanted goes evenly to the kinds below their cap, so any of them can speed up before the next split.
        while (remaining >= 1)
        {
            var open = Enumerable.Range(0, 3).Where(i => shares[i] < caps[i]).ToArray();
            if (open.Length == 0) break;
            var each = remaining / open.Length;
            foreach (var i in open)
            {
                var give = Math.Min(each, caps[i] - shares[i]);
                shares[i] += give;
                remaining -= give;
            }
        }

        return new SpeedShares(Bytes(shares[0]), Bytes(shares[1]), Bytes(shares[2]));

        static long Bytes(double share) => Math.Max(1, (long)Math.Floor(share));
    }

    /// <summary>Whether <paramref name="next"/> differs enough from <paramref name="applied"/> to send: a new limit resets the
    /// HTTP limiter's credit and costs the worker a command, so a change of a few percent waits.</summary>
    public static bool Differs(SpeedShares applied, SpeedShares next)
    {
        return Far(applied.Http, next.Http) || Far(applied.TorrentDownload, next.TorrentDownload) || Far(applied.TorrentUpload, next.TorrentUpload);

        static bool Far(long a, long b) => Math.Abs(a - b) > Math.Max(a, b) / 20;
    }
}
