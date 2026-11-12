using System.Collections.ObjectModel;
using DownloadManager.Contracts;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// T12: how a header click orders the smaller lists that have a natural order of their own (Progress Details' Files,
/// Peers and Trackers), and how a refresh keeps that order without pulling rows from under the pointer. Nothing here
/// touches the UI, so it is tested (tests/DownloadManager.Core.Tests links this file). The download list's own rules are
/// <see cref="ListSort"/>; these use them for the direction a column starts in and for keeping blanks at the end.
/// </summary>
internal static class DetailsSort
{
    /// <summary>
    /// A click on another column sorts by it in its usual direction (<see cref="ListSort.StartsAscending"/>), a second
    /// click reverses it, and a third goes back to the list's own order (null), which these lists have and the download
    /// list keeps in its View menu instead: a torrent's file order, the busiest peers first, trackers by tier.
    /// </summary>
    public static (string? Key, bool Ascending) Next(string? currentKey, bool currentAscending, string clicked)
    {
        if (currentKey != clicked) return (clicked, ListSort.StartsAscending(clicked));
        return currentAscending == ListSort.StartsAscending(clicked) ? (clicked, !currentAscending) : (null, false);
    }

    /// <summary>The key a list is ordered by: the one picked while its column shows, else null (the list's own order).</summary>
    public static string? Shown(string? picked, Func<string, bool> isVisible) => picked is { } key && isVisible(key) ? key : null;

    /// <summary>
    /// The order to show while the user is pointing at the list (or has a row of it focused): the rows keep the places
    /// they have, rows that went are left out, and new rows go after them in sorted order. The sorted order comes back
    /// once the pointer leaves.
    /// </summary>
    public static List<T> Held<T>(IReadOnlyList<string> shownKeys, IReadOnlyList<T> sorted, Func<T, string> key)
    {
        var byKey = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var item in sorted) byKey.TryAdd(key(item), item);
        var result = new List<T>(sorted.Count);
        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (var shown in shownKeys)
        {
            if (byKey.TryGetValue(shown, out var item) && taken.Add(shown)) result.Add(item);
        }

        foreach (var item in sorted)
        {
            if (taken.Add(key(item))) result.Add(item);
        }

        return result;
    }
}

/// <summary>
/// Puts a shown list in a new order with as few changes as it can, so a refresh of a long list moves only the rows that
/// changed place instead of rebuilding every row and sending the list back to the top.
/// </summary>
internal static class RowOrder
{
    /// <summary>
    /// The rows of <paramref name="shown"/> that must move for it to read as <paramref name="target"/> (the same rows in
    /// another order): all but a longest run that is already in order. -1 when the two don't hold the same rows.
    /// </summary>
    public static int MovesNeeded<T>(IReadOnlyList<T> shown, IReadOnlyList<T> target) where T : class
        => Plan(shown, target) is { } keep ? target.Count - keep.Count : -1;

    /// <summary>
    /// Reorders <paramref name="shown"/> to match <paramref name="target"/> in place when no more than
    /// <paramref name="mostMoves"/> rows must move. False, with nothing changed, when it would take more or the rows
    /// differ; the caller then replaces the list.
    /// </summary>
    public static bool TryReorder<T>(ObservableCollection<T> shown, IReadOnlyList<T> target, int mostMoves = ListSort.MostMovesInPlace) where T : class
    {
        if (Plan(shown, target) is not { } keep) return false;
        var moving = target.Count - keep.Count;
        if (moving == 0) return true;
        if (moving > mostMoves) return false;

        // Everything out of the run goes, and then each comes back at its own place, left to right: by then every row
        // before it is in, in order, so it lands where the target has it.
        for (var at = shown.Count - 1; at >= 0; at--)
        {
            if (!keep.Contains(shown[at])) shown.RemoveAt(at);
        }

        for (var at = 0; at < target.Count; at++)
        {
            if (!keep.Contains(target[at])) shown.Insert(at, target[at]);
        }

        return true;
    }

    /// <summary>The rows that stay where they are (a longest increasing run of their places), or null when the rows differ.</summary>
    private static HashSet<T>? Plan<T>(IReadOnlyList<T> shown, IReadOnlyList<T> target) where T : class
    {
        if (shown.Count != target.Count) return null;
        var place = new Dictionary<T, int>(shown.Count, ReferenceEqualityComparer.Instance);
        for (var at = 0; at < shown.Count; at++)
        {
            if (!place.TryAdd(shown[at], at)) return null;
        }

        var places = new int[target.Count];
        for (var at = 0; at < target.Count; at++)
        {
            if (!place.TryGetValue(target[at], out var was)) return null;
            places[at] = was;
        }

        // Longest increasing subsequence of the places, O(n log n), remembering the run itself.
        var tails = new List<int>();
        var previous = new int[places.Length];
        for (var at = 0; at < places.Length; at++)
        {
            int low = 0, high = tails.Count;
            while (low < high)
            {
                var middle = (low + high) / 2;
                if (places[tails[middle]] < places[at]) low = middle + 1;
                else high = middle;
            }

            previous[at] = low > 0 ? tails[low - 1] : -1;
            if (low == tails.Count) tails.Add(at);
            else tails[low] = at;
        }

        var keep = new HashSet<T>(ReferenceEqualityComparer.Instance);
        for (var at = tails.Count > 0 ? tails[^1] : -1; at >= 0; at = previous[at]) keep.Add(target[at]);
        return keep;
    }
}

/// <summary>What a torrent file's row is sorted by, as it shows now (its box and priority may be ahead of the Host).</summary>
internal readonly record struct FileSortFacts(int Index, string Path, long Size, bool Wanted, long? Done, string Priority)
{
    /// <summary>How much of the file is there, 0 to 1; null for a skipped file or one whose progress isn't known.</summary>
    public double? Progress => !Wanted || Done is not { } done ? null : Size <= 0 ? 1 : Math.Clamp((double)done / Size, 0, 1);

    /// <summary>High 3, Normal 2, Low 1, Skip 0.</summary>
    public int PriorityRank => !Wanted ? 0 : Priority switch
    {
        TorrentPriorities.High => 3,
        TorrentPriorities.Low => 1,
        TorrentPriorities.Skip => 0,
        _ => 2,
    };
}

/// <summary>T12: the orders of Progress Details' Files, Peers and Trackers tabs, by header key.</summary>
internal static class TorrentDetailsSort
{
    /// <summary>The keys whose values change as the torrent runs: a refresh sorts again by them (unless held).</summary>
    public static bool Live(string? key) => key is "progress" or "priority" or "has" or "down" or "up" or "seeds" or "peers" or "next" or "status"
        or "done" or "received" or "sent" or "message";

    /// <summary>The files in the torrent's own order (<paramref name="rows"/> as given) or by a column.</summary>
    public static IEnumerable<T> Files<T>(IEnumerable<T> rows, Func<T, FileSortFacts> facts, string? key, bool ascending) => key switch
    {
        "name" => ListSort.Order(rows, row => facts(row).Path, NaturalStringComparer.Instance, ascending),
        "size" => ListSort.Order(rows, row => facts(row).Size, null, ascending),
        "progress" => ListSort.Order(rows, row => facts(row).Progress ?? 0, null, ascending, row => facts(row).Progress is null),
        "priority" => ListSort.Order(rows, row => facts(row).PriorityRank, null, ascending),
        "done" => ListSort.Order(rows, row => facts(row).Done ?? 0, null, ascending, row => facts(row).Done is null),
        _ => rows,
    };

    /// <summary>The peers busiest first (ties by the number each got when first seen), or by a column.</summary>
    public static IEnumerable<TorrentPeerDto> Peers(IEnumerable<TorrentPeerDto> peers, Func<TorrentPeerDto, int> number, string? key, bool ascending)
    {
        var busiest = peers.OrderByDescending(peer => peer.DownloadRate + peer.UploadRate).ThenBy(number);
        return key switch
        {
            "peer" => ListSort.Order(busiest, number, null, ascending),
            "client" => ListSort.Order(busiest, peer => peer.Client.Trim(), NaturalStringComparer.Instance, ascending, peer => string.IsNullOrWhiteSpace(peer.Client)),
            "flags" => ListSort.Order(busiest, peer => TorrentDetailsRules.PeerFlags(peer.Flags), NaturalStringComparer.Instance, ascending),
            "has" => ListSort.Order(busiest, peer => double.IsFinite(peer.Progress) ? Math.Clamp(peer.Progress, 0, 1) : 0, null, ascending),
            "down" => ListSort.Order(busiest, peer => peer.DownloadRate, null, ascending),
            "up" => ListSort.Order(busiest, peer => peer.UploadRate, null, ascending),
            "received" => ListSort.Order(busiest, peer => peer.Downloaded, null, ascending),
            "sent" => ListSort.Order(busiest, peer => peer.Uploaded, null, ascending),
            _ => busiest,
        };
    }

    /// <summary>The trackers by tier (in the order the Host gave them within one), or by a column. Unknown counts and times stay last.</summary>
    public static IEnumerable<TorrentTrackerDto> Trackers(IEnumerable<TorrentTrackerDto> trackers, string? key, bool ascending)
    {
        var byTier = trackers.OrderBy(tracker => tracker.Tier);
        return key switch
        {
            "tracker" => ListSort.Order(byTier, tracker => TrackerName(tracker.Url), NaturalStringComparer.Instance, ascending),
            "status" => ListSort.Order(byTier, tracker => TorrentDetailsRules.TrackerStatus(tracker.Status), NaturalStringComparer.Instance, ascending),
            "seeds" => ListSort.Order(byTier, tracker => tracker.Seeds, null, ascending, tracker => tracker.Seeds < 0),
            "peers" => ListSort.Order(byTier, tracker => tracker.Peers, null, ascending, tracker => tracker.Peers < 0),
            "next" => ListSort.Order(byTier, tracker => tracker.NextAnnounce, null, ascending, tracker => tracker.NextAnnounce < 0),
            "tier" => ListSort.Order(byTier, tracker => tracker.Tier, null, ascending),
            "message" => ListSort.Order(byTier, tracker => tracker.Message.Trim(), NaturalStringComparer.Instance, ascending, tracker => string.IsNullOrWhiteSpace(tracker.Message)),
            _ => byTier,
        };
    }

    /// <summary>
    /// Each tracker's row key: its address, and for the second and later times the same address is listed (another
    /// tier), "#2", "#3" in the order given, as the web UI does. Keyed by the item itself, so equal records stay apart.
    /// </summary>
    public static Dictionary<TorrentTrackerDto, string> TrackerKeys(IReadOnlyList<TorrentTrackerDto> trackers)
    {
        var keys = new Dictionary<TorrentTrackerDto, string>(trackers.Count, ReferenceEqualityComparer.Instance);
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var tracker in trackers)
        {
            var times = seen[tracker.Url] = seen.GetValueOrDefault(tracker.Url) + 1;
            keys.TryAdd(tracker, times == 1 ? tracker.Url : $"{tracker.Url}#{times}");
        }

        return keys;
    }

    /// <summary>A tracker by its host, then the rest as shown: "udp://" and "https://" don't decide the order.</summary>
    private static string TrackerName(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Host.Length > 0
            ? $"{uri.Host} {TorrentDetailsRules.TrackerShown(url)}"
            : TorrentDetailsRules.TrackerShown(url);
}
