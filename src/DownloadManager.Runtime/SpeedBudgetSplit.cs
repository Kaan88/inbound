namespace DownloadManager.Runtime;

/// <summary>What each kind of transfer may use of the overall speed limit (T2), in bytes/s. Never 0, which would be no cap.</summary>
public readonly record struct SpeedShares(long Http, long TorrentDownload, long TorrentUpload)
{
    /// <summary>Site transfers' share (SITES.4), applied to rclone; 0 while no site transfer takes part in the split.</summary>
    public long Sites { get; init; }

    public long Total => Http + TorrentDownload + TorrentUpload + Sites;
}

/// <summary>One kind of transfer in a split: whether any of it runs now, and how fast it went (bytes/s).</summary>
public readonly record struct SpeedDemand(bool Active, double Rate);

/// <summary>
/// T2: the overall speed limit is one budget for HTTP downloads, torrent downloads, torrent uploads and site transfers
/// together. A kind that used most of its share wants more; one that used less keeps what it used and some room to grow;
/// the rest is shared out evenly. Each kind keeps a small share even while idle, so one that starts can get going before
/// the next split. The shares never add up to more than the limit (unless the limit is below 4 bytes/s).
/// Site transfers take part only while one runs (<c>sites</c> not null); with none, the three other kinds split as before.
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

    /// <summary>As <see cref="Initial(long, long)"/>, with site transfers taking part when <paramref name="sites"/> is true and
    /// torrents only when <paramref name="torrents"/> is true.</summary>
    public static SpeedShares Initial(long limit, long uploadCap, bool torrents, bool sites)
        => Split(limit, uploadCap, previous: null, new SpeedDemand(true, 0), new SpeedDemand(true, 0), new SpeedDemand(true, 0),
            sites ? new SpeedDemand(true, 0) : null, torrents);

    /// <param name="limit">The overall speed limit, above 0.</param>
    /// <param name="uploadCap">Options › Torrents' upload limit; 0 is none (the upload's share alone caps it).</param>
    /// <param name="previous">The last split, which the rates were measured under; null for none.</param>
    public static SpeedShares Split(long limit, long uploadCap, SpeedShares? previous, SpeedDemand http, SpeedDemand download, SpeedDemand upload)
        => Split(limit, uploadCap, previous, http, download, upload, sites: null, torrents: true);

    /// <param name="sites">Site transfers' demand while one runs; null leaves them out (their share is 0).</param>
    /// <param name="torrents">False while the torrent worker is off: HTTP and sites split the limit between them, and the
    /// torrent shares are the even three-way split kept for when the worker starts (which splits again at once).</param>
    public static SpeedShares Split(long limit, long uploadCap, SpeedShares? previous, SpeedDemand http, SpeedDemand download,
        SpeedDemand upload, SpeedDemand? sites, bool torrents = true)
    {
        if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit));
        var uploadLimit = uploadCap > 0 ? uploadCap : long.MaxValue;
        // Kinds by index: 0 HTTP, 1 torrent download, 2 torrent upload, 3 sites.
        var all = new[] { http, download, upload, sites ?? default };
        var caps = new[] { long.MaxValue, long.MaxValue, uploadLimit, long.MaxValue };
        var before = previous is { } p ? new[] { p.Http, p.TorrentDownload, p.TorrentUpload, p.Sites } : null;
        var kinds = new List<int> { 0 };
        if (torrents) kinds.AddRange([1, 2]);
        if (sites is not null) kinds.Add(3);

        var shares = Shares(limit, [.. kinds.Select(i => caps[i])], before is null ? null : [.. kinds.Select(i => before[i])],
            [.. kinds.Select(i => all[i])]);
        var result = new long[4];
        for (var n = 0; n < kinds.Count; n++) result[kinds[n]] = shares[n];
        if (!torrents)
        {
            var even = Shares(limit, [long.MaxValue, long.MaxValue, uploadLimit], null,
                [new SpeedDemand(true, 0), new SpeedDemand(true, 0), new SpeedDemand(true, 0)]);
            (result[1], result[2]) = (even[1], even[2]);
        }

        return new SpeedShares(result[0], result[1], result[2]) { Sites = result[3] };
    }

    private static long[] Shares(long limit, long[] caps, long[]? before, SpeedDemand[] demands)
    {
        var count = demands.Length;
        var floor = Math.Max(1, limit / FloorDivisor);
        var wants = new double[count];
        for (var i = 0; i < count; i++)
        {
            var demand = demands[i];
            double want;
            if (!demand.Active) want = floor;
            else if (before is null || demand.Rate >= FullShare * before[i]) want = double.PositiveInfinity;
            else want = Math.Max(floor, demand.Rate * Headroom);
            wants[i] = Math.Min(want, caps[i]);
        }

        // Fill the smallest wants first; each takes at most an even share of what is left.
        var shares = new double[count];
        var remaining = (double)limit;
        var order = Enumerable.Range(0, count).OrderBy(i => wants[i]).ToArray();
        for (var n = 0; n < count; n++)
        {
            var i = order[n];
            var give = Math.Min(wants[i], remaining / (count - n));
            shares[i] = give;
            remaining -= give;
        }

        // What nobody wanted goes evenly to the kinds below their cap, so any of them can speed up before the next split.
        while (remaining >= 1)
        {
            var open = Enumerable.Range(0, count).Where(i => shares[i] < caps[i]).ToArray();
            if (open.Length == 0) break;
            var each = remaining / open.Length;
            foreach (var i in open)
            {
                var give = Math.Min(each, caps[i] - shares[i]);
                shares[i] += give;
                remaining -= give;
            }
        }

        return [.. shares.Select(share => Math.Max(1, (long)Math.Floor(share)))];
    }

    /// <summary>Whether <paramref name="next"/> differs enough from <paramref name="applied"/> to send: a new limit resets the
    /// HTTP limiter's credit and costs the worker a command, so a change of a few percent waits.</summary>
    public static bool Differs(SpeedShares applied, SpeedShares next)
    {
        return Far(applied.Http, next.Http) || Far(applied.TorrentDownload, next.TorrentDownload)
            || Far(applied.TorrentUpload, next.TorrentUpload) || Far(applied.Sites, next.Sites);

        static bool Far(long a, long b) => Math.Abs(a - b) > Math.Max(a, b) / 20;
    }
}
