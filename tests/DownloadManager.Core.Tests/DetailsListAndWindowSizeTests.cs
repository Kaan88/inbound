using System.Collections.ObjectModel;
using DownloadManager.Contracts;
using DownloadManager.Desktop.WinUI;

namespace DownloadManager.Core.Tests;

/// <summary>T12: sorting and widths in Progress Details' Files, Peers and Trackers tabs.</summary>
public sealed class DetailsSortTests
{
    [Fact]
    public void A_header_starts_in_its_usual_direction_reverses_and_then_goes_back_to_the_lists_own_order()
    {
        Assert.Equal(("name", true), DetailsSort.Next(null, false, "name"));
        Assert.Equal(("name", false), DetailsSort.Next("name", true, "name"));
        Assert.Equal(((string?)null, false), DetailsSort.Next("name", false, "name"));

        // Sizes, speeds and progress read from the most first.
        Assert.Equal(("size", false), DetailsSort.Next(null, false, "size"));
        Assert.Equal(("size", true), DetailsSort.Next("size", false, "size"));
        Assert.Equal(((string?)null, false), DetailsSort.Next("size", true, "size"));

        // Another column always starts afresh.
        Assert.Equal(("down", false), DetailsSort.Next("name", true, "down"));
        Assert.Equal(("peer", true), DetailsSort.Next("down", false, "peer"));
        Assert.Equal(("next", true), DetailsSort.Next(null, false, "next"));
    }

    private static TorrentFileDto File(int index, string path, long size, bool wanted = true, long? done = null, string? priority = null)
        => new() { Index = index, Path = path, Size = size, Wanted = wanted, Done = done, Priority = priority };

    private static FileSortFacts Facts(TorrentFileDto file) => new(file.Index, file.Path, file.Size, file.Wanted, file.Done, TorrentPriorities.Of(file));

    private static List<int> FileOrder(IEnumerable<TorrentFileDto> files, string? key, bool ascending)
        => TorrentDetailsSort.Files(files, Facts, key, ascending).Select(file => file.Index).ToList();

    [Fact]
    public void Files_sort_by_number_not_text_and_ties_keep_the_torrents_order()
    {
        var files = new[]
        {
            File(0, "b/part10.rar", 900),
            File(1, "b/part2.rar", 9_000_000_000),
            File(2, "a.txt", 900),
            File(3, "b/Part1.rar", 90_000),
        };
        Assert.Equal([0, 1, 2, 3], FileOrder(files, null, true));
        Assert.Equal([2, 3, 1, 0], FileOrder(files, "name", true));
        Assert.Equal([0, 1, 3, 2], FileOrder(files, "name", false));
        // 9 GB is not "smaller" than 900 B as text would have it; equal sizes keep the torrent's order either way.
        Assert.Equal([1, 3, 0, 2], FileOrder(files, "size", false));
        Assert.Equal([0, 2, 3, 1], FileOrder(files, "size", true));
    }

    [Fact]
    public void Files_by_progress_keep_skipped_and_unknown_ones_last_in_both_directions()
    {
        var files = new[]
        {
            File(0, "skipped", 100, wanted: false, done: 100),
            File(1, "half", 100, done: 50),
            File(2, "unknown", 100, done: null),
            File(3, "done", 100, done: 100),
            File(4, "empty", 0, done: 0),
        };
        Assert.Equal([3, 4, 1, 0, 2], FileOrder(files, "progress", false));
        Assert.Equal([1, 3, 4, 0, 2], FileOrder(files, "progress", true));
    }

    [Fact]
    public void Files_by_priority_put_high_first_and_skip_last()
    {
        var files = new[]
        {
            File(0, "a", 1, priority: "low"),
            File(1, "b", 1, wanted: false, priority: "skip"),
            File(2, "c", 1, priority: "high"),
            File(3, "d", 1),
        };
        Assert.Equal([2, 3, 0, 1], FileOrder(files, "priority", false));
        Assert.Equal([1, 0, 3, 2], FileOrder(files, "priority", true));
    }

    private static TorrentPeerDto Peer(string address, long down, long up = 0, string client = "", double has = 0, params string[] flags)
        => new() { Address = address, Port = 6881, Client = client, DownloadRate = down, UploadRate = up, Progress = has, Flags = [.. flags] };

    [Fact]
    public void Peers_are_busiest_first_until_a_column_is_chosen_and_rates_sort_as_numbers()
    {
        var peers = new[]
        {
            Peer("10.0.0.1", 900, client: "qBittorrent 4.6", has: 0.5),
            Peer("10.0.0.2", 2_000_000, client: "", has: 1),
            Peer("10.0.0.3", 0, up: 5_000, client: "Deluge 2", has: 0.1),
        };
        var numbers = new Dictionary<string, int> { ["10.0.0.1"] = 3, ["10.0.0.2"] = 1, ["10.0.0.3"] = 2 };
        List<string> Order(string? key, bool ascending)
            => TorrentDetailsSort.Peers(peers, peer => numbers[peer.Address], key, ascending).Select(peer => peer.Address[^1..]).ToList();

        Assert.Equal(["2", "3", "1"], Order(null, false));
        Assert.Equal(["2", "1", "3"], Order("down", false));
        Assert.Equal(["2", "3", "1"], Order("peer", true));
        // A peer that names no client is last whichever way.
        Assert.Equal(["3", "1", "2"], Order("client", true));
        Assert.Equal(["1", "3", "2"], Order("client", false));
        Assert.Equal(["2", "1", "3"], Order("has", false));
    }

    [Fact]
    public void Trackers_keep_their_tiers_until_sorted_and_unknown_counts_and_times_stay_last()
    {
        var trackers = new[]
        {
            new TorrentTrackerDto { Url = "udp://b.example:80/announce", Tier = 1, Status = "working", Seeds = 12, Peers = 3, NextAnnounce = 1800 },
            new TorrentTrackerDto { Url = "udp://a.example:80/announce", Tier = 0, Status = "error", Seeds = -1, Peers = -1, NextAnnounce = -1 },
            new TorrentTrackerDto { Url = "https://c.example/announce", Tier = 0, Status = "updating", Seeds = 100, Peers = 40, NextAnnounce = 0 },
        };
        List<char> Order(string? key, bool ascending)
            => TorrentDetailsSort.Trackers(trackers, key, ascending).Select(tracker => tracker.Url.Split("://")[1][0]).ToList();

        Assert.Equal(['a', 'c', 'b'], Order(null, true));
        Assert.Equal(['c', 'b', 'a'], Order("seeds", false));
        Assert.Equal(['b', 'c', 'a'], Order("seeds", true));
        Assert.Equal(['c', 'b', 'a'], Order("next", true));
        Assert.Equal(['b', 'c', 'a'], Order("next", false));
        Assert.Equal(['a', 'b', 'c'], Order("tracker", true));
    }

    [Fact]
    public void Only_values_that_change_while_a_torrent_runs_sort_again_on_a_refresh()
    {
        Assert.True(TorrentDetailsSort.Live("progress"));
        Assert.True(TorrentDetailsSort.Live("down"));
        Assert.False(TorrentDetailsSort.Live("name"));
        Assert.False(TorrentDetailsSort.Live("size"));
        Assert.False(TorrentDetailsSort.Live(null));
    }

    [Fact]
    public void A_held_list_keeps_its_rows_where_they_are_drops_the_gone_and_adds_the_new_after_them()
    {
        var shown = new[] { "c", "a", "gone", "b" };
        var sorted = new[] { "a", "new2", "b", "c", "new1" };
        Assert.Equal(["c", "a", "b", "new2", "new1"], DetailsSort.Held(shown, sorted, key => key));
        Assert.Equal(sorted, DetailsSort.Held([], sorted, key => key));
    }

    [Fact]
    public void A_sort_by_a_hidden_column_waits_until_it_shows_again()
    {
        var hidden = new HashSet<string> { "priority" };
        Assert.Null(DetailsSort.Shown("priority", key => !hidden.Contains(key)));
        Assert.Equal("name", DetailsSort.Shown("name", key => !hidden.Contains(key)));
        Assert.Null(DetailsSort.Shown(null, _ => true));
        hidden.Clear();
        Assert.Equal("priority", DetailsSort.Shown("priority", key => !hidden.Contains(key)));
    }

    [Fact]
    public void A_tracker_listed_in_two_tiers_keeps_two_rows_through_a_held_order()
    {
        var first = new TorrentTrackerDto { Url = "udp://a.example:80/announce", Tier = 0 };
        var again = new TorrentTrackerDto { Url = "udp://a.example:80/announce", Tier = 1 };
        var other = new TorrentTrackerDto { Url = "udp://b.example:80/announce", Tier = 1 };
        var trackers = new List<TorrentTrackerDto> { first, other, again };
        var keys = TorrentDetailsSort.TrackerKeys(trackers);
        Assert.Equal("udp://a.example:80/announce", keys[first]);
        Assert.Equal("udp://a.example:80/announce#2", keys[again]);
        Assert.Equal("udp://b.example:80/announce", keys[other]);

        var sorted = TorrentDetailsSort.Trackers(trackers, null, true).ToList();
        var held = DetailsSort.Held([keys[other], keys[again], keys[first]], sorted, tracker => keys[tracker]);
        Assert.Equal([other, again, first], held);
    }
}

public sealed class RowOrderTests
{
    private sealed record Row(int Id);

    [Fact]
    public void One_row_that_changed_place_is_one_move_however_far_it_goes()
    {
        var rows = Enumerable.Range(0, 1000).Select(id => new Row(id)).ToList();
        var shown = new ObservableCollection<Row>(rows);
        var target = rows.Skip(1).Append(rows[0]).ToList();
        Assert.Equal(1, RowOrder.MovesNeeded(shown, target));

        var changes = 0;
        shown.CollectionChanged += (_, _) => changes++;
        Assert.True(RowOrder.TryReorder(shown, target));
        Assert.Equal(target, shown);
        // One row out and back in; the other 999 keep their containers.
        Assert.Equal(2, changes);
    }

    [Fact]
    public void A_reversal_reorders_correctly_and_too_many_moves_leave_the_list_alone()
    {
        var rows = Enumerable.Range(0, 10).Select(id => new Row(id)).ToList();
        var shown = new ObservableCollection<Row>(rows);
        var reversed = Enumerable.Reverse(rows).ToList();
        Assert.Equal(9, RowOrder.MovesNeeded(shown, reversed));
        Assert.True(RowOrder.TryReorder(shown, reversed, mostMoves: 9));
        Assert.Equal(reversed, shown);

        var big = Enumerable.Range(0, 200).Select(id => new Row(id)).ToList();
        var bigShown = new ObservableCollection<Row>(big);
        Assert.False(RowOrder.TryReorder(bigShown, Enumerable.Reverse(big).ToList()));
        Assert.Equal(big, bigShown);
    }

    [Fact]
    public void Lists_that_hold_different_rows_are_not_reordered()
    {
        var rows = new[] { new Row(1), new Row(2) };
        var shown = new ObservableCollection<Row>(rows);
        Assert.Equal(-1, RowOrder.MovesNeeded(shown, [rows[0], new Row(3)]));
        Assert.False(RowOrder.TryReorder(shown, [rows[0]]));
        Assert.True(RowOrder.TryReorder(shown, rows));
    }

    [Fact]
    public void Random_orders_come_out_exactly_as_asked()
    {
        var random = new Random(12);
        for (var round = 0; round < 50; round++)
        {
            var rows = Enumerable.Range(0, 40).Select(id => new Row(id)).ToList();
            var shown = new ObservableCollection<Row>(rows.OrderBy(_ => random.Next()));
            var target = rows.OrderBy(_ => random.Next()).ToList();
            Assert.True(RowOrder.TryReorder(shown, target, mostMoves: 40));
            Assert.Equal(target, shown);
        }
    }
}

public sealed class DetailsColumnTests
{
    [Fact]
    public void The_torrent_tabs_columns_have_unique_keys_and_their_sorts_are_known_columns()
    {
        foreach (var layout in new[] { ListColumnSets.TorrentFiles(), ListColumnSets.TorrentPeers(), ListColumnSets.TorrentTrackers() })
        {
            Assert.Equal(layout.Columns.Count, layout.Columns.Select(column => column.Key).Distinct().Count());
            Assert.Contains(layout.Columns, column => column.Key == layout.NameKey);
            Assert.Contains(layout.Columns, column => column.Key == layout.LastKey);
            Assert.All(layout.Columns, column => Assert.True(column.MinWidth <= column.DefaultWidth));
        }

        // The box has no header: nothing to sort by and nothing to read out.
        Assert.False(ListColumnSets.TorrentFiles().Columns[0].HasHeader);
    }

    [Fact]
    public void A_hidden_last_column_hands_the_filling_to_the_last_one_that_shows()
    {
        var layout = ListColumnSets.TorrentFiles();
        layout.SetVisible("priority", false);
        layout.FixName(300);
        Assert.True(layout.Size(layout.Columns.Single(column => column.Key == "progress")).Fills);
        Assert.Equal(new ColumnSize(0, false), layout.Size(layout.Columns.Single(column => column.Key == "priority")));

        layout.SetVisible("priority", true);
        Assert.False(layout.Size(layout.Columns.Single(column => column.Key == "progress")).Fills);
        Assert.True(layout.Size(layout.Columns.Single(column => column.Key == "priority")).Fills);
    }
}

public sealed class ListLayoutStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "inbound-list-layout-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void Each_list_keeps_its_own_widths_and_sort_and_a_save_never_drops_another_lists()
    {
        var path = Path.Combine(_folder, "list-layout.json");
        var one = new ListLayoutStore(path);
        var other = new ListLayoutStore(path);
        Assert.True(one.Save("progress.files", new ListLayout { Widths = { ["name"] = 321 }, SortKey = "size", SortAscending = true }));
        Assert.True(other.Save("progress.peers", new ListLayout { SortKey = "down" }));

        var files = new ListLayoutStore(path).Load("progress.files");
        Assert.Equal(321, files.Widths["name"]);
        Assert.Equal("size", files.SortKey);
        Assert.True(files.SortAscending);
        Assert.Equal("down", one.Load("progress.peers").SortKey);
        Assert.Null(one.Load("progress.trackers").SortKey);
    }

    [Fact]
    public void A_broken_file_is_the_usual_layout_and_is_replaced_on_the_next_save()
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "list-layout.json");
        File.WriteAllText(path, "{ not json");
        var store = new ListLayoutStore(path);
        Assert.Empty(store.Load("progress.files").Widths);
        Assert.True(store.Save("progress.files", new ListLayout { SortKey = "name" }));
        Assert.Equal("name", store.Load("progress.files").SortKey);
    }

    [Fact]
    public void Saved_widths_go_through_the_layouts_own_limits()
    {
        var layout = ListColumnSets.TorrentPeers();
        layout.Import(new Dictionary<string, double> { ["client"] = 3, ["down"] = double.NaN, ["nope"] = 50 });
        Assert.Equal(50, layout.Size(layout.Columns.Single(column => column.Key == "client")).Width);
        Assert.Equal(84, layout.Size(layout.Columns.Single(column => column.Key == "down")).Width);
        Assert.DoesNotContain("nope", layout.Export().Keys);
    }
}

/// <summary>T13: torrent windows and dialogs open big enough for their buttons, inside the screen, at 100 % to 175 %.</summary>
public sealed class WindowSizeRulesTests
{
    // Work areas: the screen less a 40 px taskbar.
    public static TheoryData<double, int, int> Screens => new()
    {
        { 1.0, 1366, 728 },
        { 1.25, 1920, 1040 },
        { 1.5, 1920, 1040 },
        { 1.75, 2560, 1400 },
        { 1.75, 1920, 1040 },
    };

    [Theory]
    [MemberData(nameof(Screens))]
    public void Every_progress_size_stays_inside_the_work_area(double scale, int workWidth, int workHeight)
    {
        foreach (var torrent in new[] { false, true })
        {
            foreach (var completed in new[] { false, true })
            {
                var (width, height) = WindowSizeRules.ProgressSize(torrent, completed);
                var size = WindowSizeRules.Fit(width, height, scale, workWidth, workHeight);
                Assert.True(size.Width <= workWidth - WindowSizeRules.ScreenMargin);
                Assert.True(size.Height <= workHeight - WindowSizeRules.ScreenMargin);
            }
        }
    }

    [Fact]
    public void A_torrents_progress_window_fits_its_widest_row_of_buttons_on_a_1366_by_768_screen_at_100_percent()
    {
        // Delete (with its gap), Force recheck, Announce now, Stop, Resume, Cancel download, Hide, as measured from their
        // labels at 14 px Segoe UI with the buttons' padding and glyphs, and the window's 20 px padding on each side.
        double[] buttons = [84 + 16, 132, 136, 72, 94, 149, 92];
        var needed = WindowSizeRules.OneLineWidth(buttons, 8) + 40;
        Assert.True(needed <= WindowSizeRules.TorrentProgressWidth, $"{needed} DIPs of buttons");
        var size = WindowSizeRules.Fit(WindowSizeRules.TorrentProgressWidth, WindowSizeRules.TorrentProgressHeight, 1, 1366, 728);
        Assert.Equal(new PixelSize(880, 660), size);
    }

    [Fact]
    public void Sizes_are_scaled_to_the_display_and_cut_to_the_work_area()
    {
        Assert.Equal(new PixelSize(800, 625), WindowSizeRules.Fit(640, 500, 1.25, 1920, 1040));
        Assert.Equal(new PixelSize(1540, 1016), WindowSizeRules.Fit(880, 660, 1.75, 1920, 1040));
        // A nonsense scale is taken as 100 %.
        Assert.Equal(new PixelSize(640, 500), WindowSizeRules.Fit(640, 500, double.NaN, 1920, 1040));
        Assert.Equal(new PixelSize(200, 200), WindowSizeRules.Fit(640, 500, 1, 100, 100));
    }

    [Fact]
    public void A_window_only_grows_and_is_moved_back_inside_the_work_area()
    {
        Assert.Equal(new PixelSize(860, 640), WindowSizeRules.Grown(new PixelSize(640, 640), new PixelSize(860, 600)));
        Assert.Equal((486, 40), WindowSizeRules.KeptInside(900, 40, new PixelSize(880, 600), 0, 0, 1366, 728));
        Assert.Equal((0, 0), WindowSizeRules.KeptInside(-50, -10, new PixelSize(2000, 900), 0, 0, 1366, 728));
        Assert.Equal((1500, 100), WindowSizeRules.KeptInside(1500, 100, new PixelSize(400, 300), 1366, 0, 1920, 1040));
    }

    [Theory]
    [InlineData(1000, 620)]
    [InlineData(728, 528)]
    [InlineData(500, 300)]
    [InlineData(300, 120)]
    [InlineData(0, 620)]
    public void A_dialogs_content_takes_what_the_window_leaves_after_its_title_and_buttons(double windowHeight, double expected)
        => Assert.Equal(expected, WindowSizeRules.DialogContentMaxHeight(windowHeight));

    [Fact]
    public void A_dialogs_list_shrinks_with_the_window_but_not_below_its_least()
    {
        Assert.Equal(380, WindowSizeRules.DialogListHeight(620, 40, 160, 380));
        Assert.Equal(260, WindowSizeRules.DialogListHeight(300, 40, 160, 380));
        Assert.Equal(160, WindowSizeRules.DialogListHeight(120, 40, 160, 380));
    }

    [Fact]
    public void A_row_of_buttons_stays_on_one_line_while_it_fits_and_wraps_from_the_left_when_it_doesnt()
    {
        double[] widths = [100, 0, 120, 80, 90];
        Assert.Equal(414, WindowSizeRules.OneLineWidth(widths, 8));
        Assert.Equal([0], WindowSizeRules.LineStarts(widths, 414, 8));
        // The last buttons stay together on the bottom line; the first goes above them.
        Assert.Equal([0, 1], WindowSizeRules.LineStarts(widths, 413, 8));
        Assert.Equal([0, 1, 3, 4], WindowSizeRules.LineStarts(widths, 120, 8));
        // A button wider than the row still gets a line of its own.
        Assert.Equal([0, 1, 3, 4], WindowSizeRules.LineStarts(widths, 10, 8));
        Assert.Empty(WindowSizeRules.LineStarts([], 100, 8));
    }
}
