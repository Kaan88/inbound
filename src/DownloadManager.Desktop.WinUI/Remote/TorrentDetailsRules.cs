using System.Globalization;
using DownloadManager.Contracts;

namespace DownloadManager.Desktop.WinUI;

/// <summary>Progress Details' torrent tabs that ask the Host for live lists (T2, docs/plans/torrent-mockups/c-torrent-details.html).</summary>
internal enum TorrentDetailsTab
{
    None,
    Peers,
    Trackers,
    Pieces,
}

/// <summary>
/// T2: the Peers, Trackers and Pieces &amp; Speed tabs of Progress Details. The lists are asked for only while their tab
/// shows in an open, shown window and the torrent may be on the torrent engine; nothing is asked otherwise. Kept free
/// of WinUI so they are tested (tests/DownloadManager.Core.Tests links this file).
/// </summary>
internal static class TorrentDetailsRules
{
    /// <summary>How often a shown tab asks again.</summary>
    public static readonly TimeSpan Refresh = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How often the shown tab's timer ticks: every second on Pieces &amp; Speed, where the graph takes a sample each tick;
    /// every <see cref="Refresh"/> on Peers and Trackers, so the window wakes no more than it reads.
    /// </summary>
    public static TimeSpan TickInterval(TorrentDetailsTab tab) => tab == TorrentDetailsTab.Pieces ? TimeSpan.FromSeconds(1) : Refresh;

    /// <summary>
    /// The shown tab's list is due: none was read yet (<paramref name="lastReadAt"/> null) or <see cref="Refresh"/> has
    /// nearly passed since the last read (a timer may tick a little early). Times are <see cref="Environment.TickCount64"/>.
    /// </summary>
    public static bool ReadDue(long? lastReadAt, long now)
        => lastReadAt is not { } at || now - at >= (long)Refresh.TotalMilliseconds - 200;

    public const string NotRunning = "Not running. Peers, trackers and pieces are shown while the torrent downloads or seeds.";

    public const string Unsupported = "This server doesn't list peers, trackers or pieces. Update Inbound there to see them.";

    /// <summary>The Host offers these lists: it has torrents (a server's hello lists <c>torrents</c>).</summary>
    public static bool Offered(IEnumerable<string>? capabilities) => capabilities?.Contains("torrents", StringComparer.Ordinal) == true;

    /// <summary>The torrent can be on the engine now: it downloads (or connects, or checks), or it seeds.</summary>
    public static bool MayBeRunning(JobDto? job) => job?.Torrent is not null && (TorrentRules.IsActive(job) || job.Torrent.Seeding);

    /// <summary>
    /// The shown tab's list is asked for now: a list tab shows, the window is open, shown and not minimized, the Host is
    /// connected and has the lists, and the torrent may be running. Once the Host said it isn't on the engine
    /// (<paramref name="notRunningSeen"/>), nothing is asked until the torrent's state changes.
    /// </summary>
    public static bool Polls(TorrentDetailsTab tab, bool windowShown, bool minimized, bool closed, bool connected, bool unsupported,
        bool notRunningSeen, JobDto? job)
        => tab != TorrentDetailsTab.None && windowShown && !minimized && !closed && connected && !unsupported && !notRunningSeen
           && MayBeRunning(job);

    /// <summary>What a "not running" answer holds for: the torrent's state and whether it seeds. A change asks again.</summary>
    public static (string State, bool Seeding) RunKey(JobDto job) => (job.State, job.Torrent?.Seeding == true);

    /// <summary>A tab's title with its count once a list came in: "Peers (9)".</summary>
    public static string TabTitle(string name, int? count) => count is { } shown ? $"{name} ({shown})" : name;

    // ---- Peers ----

    /// <summary>"peer 3 · IPv4": the window names a peer by its place in the list; the address is on hover.</summary>
    public static string PeerName(int number, string address)
        => $"peer {number} · {(address.Contains(':', StringComparison.Ordinal) ? "IPv6" : "IPv4")}";

    /// <summary>"203.0.113.5:51413" or "[2001:db8::1]:51413".</summary>
    public static string PeerAddress(string address, int port)
    {
        var host = address.Contains(':', StringComparison.Ordinal) ? $"[{address}]" : address;
        return port > 0 ? $"{host}:{port.ToString(CultureInfo.InvariantCulture)}" : host;
    }

    /// <summary>"uTP E in": how the peer is connected (uTP or TCP), encrypted, and whether it connected in.</summary>
    public static string PeerFlags(IEnumerable<string>? flags)
    {
        var set = (flags ?? []).ToHashSet(StringComparer.Ordinal);
        var parts = new List<string> { set.Contains("utp") ? "uTP" : "TCP" };
        if (set.Contains("encrypted")) parts.Add("E");
        if (set.Contains("incoming")) parts.Add("in");
        if (set.Contains("seed")) parts.Add("seed");
        return string.Join(" ", parts);
    }

    /// <summary>How much of the torrent a peer has: "41%".</summary>
    public static string Has(double progress)
    {
        var fraction = double.IsFinite(progress) ? Math.Clamp(progress, 0, 1) : 0;
        return $"{Math.Floor(fraction * 100).ToString("0", CultureInfo.CurrentCulture)}%";
    }

    /// <summary>"6.1 MB/s", or "0" while nothing moves.</summary>
    public static string Rate(double bytesPerSecond)
        => double.IsFinite(bytesPerSecond) && bytesPerSecond >= 1 ? $"{UiFormat.Bytes((long)bytesPerSecond)}/s" : "0";

    // ---- Trackers ----

    /// <summary>
    /// A tracker's address without what could be a passkey: the query is left out, and a path other than a plain
    /// announce is cut after the host ("https://tracker.example/…").
    /// </summary>
    public static string TrackerShown(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Host.Length == 0) return "Tracker";
        var origin = $"{uri.Scheme}://{uri.Authority}";
        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.Length == 0) return origin;
        return path is "/announce" or "/announce.php" ? origin + path : origin + "/…";
    }

    public static string TrackerStatus(string? status) => status switch
    {
        "working" => "Working",
        "updating" => "Updating",
        "error" => "Error",
        "notContacted" => "Not contacted",
        _ => "Unknown",
    };

    /// <summary>A tracker's count of seeds or peers: "–" when it didn't say (-1).</summary>
    public static string Count(int value) => value < 0 ? "–" : value.ToString("N0", CultureInfo.CurrentCulture);

    // ---- Pieces & Speed ----

    /// <summary>
    /// How much of each of <paramref name="cells"/> runs of pieces the torrent has, 0 to 1, from <see cref="TorrentPiecesResult.Ranges"/>
    /// or <see cref="TorrentPiecesResult.Bitmap"/>. Fewer cells when there are fewer pieces; none when the count is unknown.
    /// </summary>
    public static double[] Cells(TorrentPiecesResult pieces, int cells)
    {
        var count = pieces.Count;
        if (count <= 0 || cells <= 0) return [];
        cells = Math.Min(cells, count);
        var have = new long[cells];
        if (pieces.Ranges is { } ranges)
        {
            foreach (var range in ranges)
            {
                if (range is not [var first, var last]) continue;
                first = Math.Max(0, first);
                last = Math.Min(count - 1, last);
                if (last < first) continue;
                for (var cell = CellOf(first, count, cells); cell <= CellOf(last, count, cells); cell++)
                {
                    var start = Math.Max(first, StartOf(cell, count, cells));
                    var end = Math.Min(last, StartOf(cell + 1, count, cells) - 1);
                    if (end >= start) have[cell] += end - start + 1;
                }
            }
        }
        else if (pieces.Bitmap is { Length: > 0 } text)
        {
            byte[] bits;
            try
            {
                bits = Convert.FromBase64String(text);
            }
            catch (FormatException)
            {
                return new double[cells];
            }

            var known = Math.Min(count, bits.Length * 8);
            for (var piece = 0; piece < known; piece++)
            {
                if ((bits[piece >> 3] & (0x80 >> (piece & 7))) != 0) have[CellOf(piece, count, cells)]++;
            }
        }
        else if (pieces.Have >= count)
        {
            // All of them, said by the count alone.
            return Enumerable.Repeat(1.0, cells).ToArray();
        }

        var result = new double[cells];
        for (var cell = 0; cell < cells; cell++)
        {
            var size = StartOf(cell + 1, count, cells) - StartOf(cell, count, cells);
            result[cell] = size > 0 ? Math.Clamp(have[cell] / (double)size, 0, 1) : 0;
        }

        return result;
    }

    private static int CellOf(int piece, int count, int cells) => (int)((long)piece * cells / count);

    /// <summary>The first piece of a cell: the smallest piece whose cell is it.</summary>
    private static int StartOf(int cell, int count, int cells) => (int)(((long)cell * count + cells - 1) / cells);

    /// <summary>"10,124 of 15,820 pieces · 256 KB each".</summary>
    public static string PiecesSummary(TorrentPiecesResult pieces)
    {
        var have = Math.Clamp(pieces.Have, 0, Math.Max(0, pieces.Count));
        var text = $"{have.ToString("N0", CultureInfo.CurrentCulture)} of {pieces.Count.ToString("N0", CultureInfo.CurrentCulture)} pieces";
        return pieces.PieceLength > 0 ? $"{text} · {UiFormat.Bytes(pieces.PieceLength)} each" : text;
    }

    /// <summary>The rate the Speed graph draws: what is sent while the torrent seeds, what comes in otherwise.</summary>
    public static double GraphRate(JobDto job, DateTimeOffset now)
        => TorrentSeedRules.IsSeeding(job)
            ? Math.Max(0, job.Torrent!.UploadRate)
            : SpeedSample.JobRate(job.State, job.BytesPerSecond, job.UpdatedAt, now);

    public static string GraphHeading(JobDto job) => TorrentSeedRules.IsSeeding(job) ? "Upload speed" : "Download speed";

    /// <summary>The line under the graph: "Uploading at 640 KB/s · ratio 0.71 · 204 MB uploaded"; empty while it isn't running.</summary>
    public static string SpeedLine(JobDto job)
    {
        if (job.Torrent is not { } torrent) return string.Empty;
        var parts = new List<string>();
        if (TorrentSeedRules.IsSeeding(job))
        {
            parts.Add(torrent.UploadRate >= 1 ? $"Uploading at {Rate(torrent.UploadRate)}" : "Seeding");
            parts.Add($"ratio {TorrentSeedRules.Ratio(torrent.Ratio)}");
        }
        else if (TorrentRules.IsActive(job))
        {
            parts.Add(job.BytesPerSecond is >= 1 ? $"Downloading at {Rate(job.BytesPerSecond.Value)}" : "Downloading");
        }
        else
        {
            return string.Empty;
        }

        if (torrent.Uploaded > 0) parts.Add($"{UiFormat.Bytes(torrent.Uploaded)} uploaded");
        return string.Join(" · ", parts);
    }

    /// <summary>
    /// Start seeding or Stop seeding in Progress Details, for a finished torrent: Stop while it seeds; Start while it
    /// doesn't and torrents apply; neither for anything else.
    /// </summary>
    public static bool? SeedButton(JobDto? job, bool torrentsOffered, SettingsDto? settings)
    {
        if (job is null || !torrentsOffered) return null;
        if (TorrentSeedRules.CanStop(job)) return false;
        return TorrentSeedRules.CanStart(job) && TorrentRules.Applies(torrentsOffered, settings) ? true : null;
    }
}
