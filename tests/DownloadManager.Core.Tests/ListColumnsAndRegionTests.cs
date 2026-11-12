using System.Xml.Linq;
using DownloadManager.Contracts;
using DownloadManager.Desktop.WinUI;

namespace DownloadManager.Core.Tests;

public sealed class ListColumnLayoutTests
{
    private static ListColumnLayout Layout() => new(
        [new ListColumn("name", "Name", 200, 100), new ListColumn("size", "Size", 80, 40), new ListColumn("last", "Last", 100, 60)],
        "name",
        "last",
        chrome: 8);

    private static ColumnSize Size(ListColumnLayout layout, string key) => layout.Size(layout.Columns.Single(column => column.Key == key));

    [Fact]
    public void Until_something_is_resized_the_name_column_takes_the_room_left()
    {
        var layout = Layout();
        Assert.True(Size(layout, "name").Fills);
        Assert.False(Size(layout, "last").Fills);
        Assert.Equal(388, layout.MinimumTotal);
    }

    [Fact]
    public void The_first_resize_fixes_the_name_where_it_is_and_the_last_column_takes_over_filling()
    {
        var layout = Layout();
        layout.FixName(431);
        Assert.Equal(new ColumnSize(431, false), Size(layout, "name"));
        Assert.True(Size(layout, "last").Fills);
        layout.FixName(999);
        Assert.Equal(431, Size(layout, "name").Width);
    }

    [Fact]
    public void A_column_never_goes_under_its_minimum_or_over_the_most()
    {
        var layout = Layout();
        layout.FixName(200);
        Assert.True(layout.Resize("size", 3, 80));
        Assert.Equal(40, Size(layout, "size").Width);
        Assert.True(layout.Resize("size", 1e9, 40));
        Assert.Equal(ListColumnLayout.MostWidth, Size(layout, "size").Width);
    }

    [Fact]
    public void Dragging_a_filling_column_inward_changes_nothing_until_it_is_past_its_minimum()
    {
        var layout = Layout();
        layout.FixName(200);

        // On screen it has room to spare, so this would only make the list scroll sideways sooner.
        Assert.False(layout.Resize("last", 400, 500));
        Assert.Equal(100, Size(layout, "last").Width);

        Assert.True(layout.Resize("last", 80, 500));
        Assert.Equal(80, Size(layout, "last").Width);

        Assert.True(layout.Resize("last", 600, 500));
        Assert.Equal(600, Size(layout, "last").Width);
    }

    [Fact]
    public void Reset_puts_a_column_back_and_the_name_back_to_filling()
    {
        var layout = Layout();
        layout.FixName(300);
        layout.Resize("size", 120, 80);
        layout.Reset("size");
        Assert.Equal(80, Size(layout, "size").Width);
        layout.Reset("name");
        Assert.True(Size(layout, "name").Fills);
    }

    [Fact]
    public void A_hidden_column_takes_no_room_and_comes_back_at_its_width()
    {
        var layout = Layout();
        layout.Resize("size", 120, 80);
        layout.SetAvailable("size", false);
        Assert.Equal(new ColumnSize(0, false), Size(layout, "size"));
        Assert.Equal(308, layout.MinimumTotal);
        layout.SetAvailable("size", true);
        Assert.Equal(120, Size(layout, "size").Width);
    }

    [Fact]
    public void Saved_widths_go_round_and_nonsense_in_the_file_is_left_out()
    {
        var layout = Layout();
        layout.FixName(300);
        layout.Resize("size", 123, 80);
        var again = Layout();
        again.Import(layout.Export());
        Assert.Equal(300, Size(again, "name").Width);
        Assert.Equal(123, Size(again, "size").Width);

        var odd = Layout();
        odd.Import(new Dictionary<string, double> { ["size"] = double.NaN, ["gone"] = 50, ["last"] = -5 });
        Assert.Equal(80, Size(odd, "size").Width);
        Assert.Equal(60, Size(odd, "last").Width);
        Assert.Equal(["last"], odd.Export().Keys);
        Assert.False(odd.NameIsFixed);

        var none = Layout();
        none.Import(null);
        Assert.Empty(none.Export());
    }

    [Fact]
    public void The_download_list_starts_at_the_width_it_always_had()
    {
        var layout = ListColumnSets.Downloads();
        layout.SetAvailable("extraction", false);
        ListColumnSets.UseDownloadsChoices(layout, torrentView: false, choices: null);
        Assert.Equal(1040, layout.MinimumTotal);
        layout.SetAvailable("extraction", true);
        Assert.Equal(1180, layout.MinimumTotal);

        // DT-6: Seeds, Peers, Up and Ratio in the torrent views until chosen otherwise; UI.R42's three stay hidden there too.
        layout.SetAvailable("extraction", false);
        ListColumnSets.UseDownloadsChoices(layout, torrentView: true, choices: null);
        Assert.Equal(1040 + 52 + 52 + 78 + 52, layout.MinimumTotal);
        Assert.All(["uploaded", "seedTime", "seedGoal"], key => Assert.False(layout.IsVisible(key)));
    }

    // ---- UI.R42: the columns menu ----

    [Fact]
    public void Every_torrent_column_starts_hidden_outside_the_torrent_views()
    {
        var layout = ListColumnSets.Downloads();
        foreach (var key in ListColumnSets.TorrentColumns)
        {
            Assert.False(layout.IsVisible(key));
            Assert.True(layout.CanChoose(key));
        }

        Assert.All(ListColumnSets.TorrentViewColumns, key => Assert.Contains(key, ListColumnSets.TorrentColumns));
        Assert.All(["name", "queue", "size", "status", "eta", "rate", "lastTry", "description"], key => Assert.True(layout.IsVisible(key)));
    }

    [Fact]
    public void The_name_column_cant_be_hidden_and_a_column_with_no_header_isnt_in_the_menu()
    {
        var layout = ListColumnSets.Downloads();
        Assert.False(layout.CanChoose("name"));
        Assert.False(layout.Choose("name", false));
        Assert.True(layout.IsVisible("name"));
        Assert.DoesNotContain(layout.MenuColumns, column => column.Key == "icon");
        Assert.Contains(layout.MenuColumns, column => column.Key == "name");

        // A saved file can't hide it either.
        layout.ImportChoices(new Dictionary<string, bool> { ["name"] = false, ["icon"] = false, ["gone"] = true, ["size"] = false });
        Assert.True(layout.IsVisible("name"));
        Assert.Equal(["size"], layout.ExportChoices().Keys);
    }

    [Fact]
    public void A_choice_shows_or_hides_a_column_and_takes_or_gives_its_room()
    {
        var layout = ListColumnSets.Downloads();
        layout.SetAvailable("extraction", false);
        var start = layout.MinimumTotal;
        Assert.True(layout.Choose("ratio", true));
        Assert.True(layout.IsVisible("ratio"));
        Assert.Equal(start + 52, layout.MinimumTotal);
        Assert.False(layout.Choose("ratio", true));
        Assert.True(layout.Choose("queue", false));
        Assert.Equal(start + 52 - 30, layout.MinimumTotal);
        Assert.Equal(new Dictionary<string, bool> { ["ratio"] = true, ["queue"] = false }, layout.ExportChoices());
    }

    [Fact]
    public void A_column_with_nothing_to_show_stays_away_whatever_was_chosen_and_comes_back_as_chosen()
    {
        var layout = ListColumnSets.Downloads();
        layout.SetAvailable("extraction", false);
        Assert.DoesNotContain(layout.MenuColumns, column => column.Key == "extraction");
        Assert.False(layout.Choose("extraction", true));
        Assert.False(layout.IsVisible("extraction"));
        layout.SetAvailable("extraction", true);
        Assert.True(layout.IsVisible("extraction"));
        Assert.True(layout.Choose("extraction", false));
        layout.SetAvailable("extraction", false);
        layout.SetAvailable("extraction", true);
        Assert.False(layout.IsVisible("extraction"));
    }

    [Fact]
    public void Hiding_the_focused_header_cell_moves_the_keyboard_to_the_nearest_shown_one()
    {
        var layout = ListColumnSets.Downloads();
        layout.SetAvailable("extraction", false);
        // Still shown: it stays where it is.
        Assert.Equal("size", layout.FocusAfterHiding("size"));
        // Size hidden: Status, to its right.
        layout.Choose("size", false);
        Assert.Equal("status", layout.FocusAfterHiding("size"));
        // Description is the last: Last Try Date, to its left, past the hidden torrent columns.
        layout.Choose("description", false);
        Assert.Equal("lastTry", layout.FocusAfterHiding("description"));
        // Nothing shown on either side but the name: the name, which always shows. The icon column has no header.
        foreach (var key in new[] { "queue", "status", "eta", "rate", "lastTry" }) layout.Choose(key, false);
        Assert.Equal("name", layout.FocusAfterHiding("queue"));
        Assert.Equal("name", layout.FocusAfterHiding("gone"));
    }

    [Fact]
    public void Reset_columns_puts_back_what_shows_at_first_and_the_widths()
    {
        var layout = ListColumnSets.Downloads();
        layout.FixName(400);
        layout.Resize("size", 150, 78);
        layout.Choose("size", false);
        layout.Choose("seeds", true);
        layout.ResetColumns();
        Assert.Empty(layout.Export());
        Assert.Empty(layout.ExportChoices());
        Assert.True(layout.IsVisible("size"));
        Assert.False(layout.IsVisible("seeds"));
        Assert.True(layout.Size(layout.Columns.Single(column => column.Key == "name")).Fills);
    }

    [Fact]
    public void The_torrent_views_and_the_other_views_keep_their_own_choices()
    {
        var layout = ListColumnSets.Downloads();
        var others = new Dictionary<string, bool> { ["ratio"] = true };
        var torrents = new Dictionary<string, bool> { ["seeds"] = false, ["uploaded"] = true };

        ListColumnSets.UseDownloadsChoices(layout, torrentView: false, others);
        Assert.True(layout.IsVisible("ratio"));
        Assert.False(layout.IsVisible("seeds"));
        Assert.False(layout.IsVisible("uploaded"));

        ListColumnSets.UseDownloadsChoices(layout, torrentView: true, torrents);
        Assert.False(layout.IsVisible("seeds"));
        Assert.True(layout.IsVisible("peers"));
        Assert.True(layout.IsVisible("ratio"));
        Assert.True(layout.IsVisible("uploaded"));

        ListColumnSets.UseDownloadsChoices(layout, torrentView: false, others);
        Assert.False(layout.IsVisible("peers"));
        Assert.False(layout.IsVisible("uploaded"));
    }

    [Theory]
    [InlineData("seeds")]
    [InlineData("ratio")]
    [InlineData("uploaded")]
    [InlineData("size")]
    public void A_sort_holds_only_where_its_column_shows(string key)
    {
        var layout = ListColumnSets.Downloads();
        ListColumnSets.UseDownloadsChoices(layout, torrentView: true, new Dictionary<string, bool> { [key] = true });
        Assert.Equal((key, true), ListColumnSets.ShownSort(layout, key, true));
        layout.Choose(key, false);
        Assert.Equal(("added", false), ListColumnSets.ShownSort(layout, key, true));
        Assert.Equal(("added", true), ListColumnSets.ShownSort(layout, "added", true));
    }

    [Fact]
    public void Each_list_with_a_header_has_a_name_that_shows_and_new_columns_hidden_at_first()
    {
        Assert.False(ListColumnSets.TorrentFiles().IsVisible("done"));
        Assert.False(ListColumnSets.TorrentPeers().IsVisible("received"));
        Assert.False(ListColumnSets.TorrentPeers().IsVisible("sent"));
        Assert.False(ListColumnSets.TorrentTrackers().IsVisible("tier"));
        Assert.False(ListColumnSets.TorrentTrackers().IsVisible("message"));
        foreach (var layout in new[] { ListColumnSets.Downloads(), ListColumnSets.ProviderItems(), ListColumnSets.TorrentFiles(), ListColumnSets.TorrentPeers(), ListColumnSets.TorrentTrackers() })
        {
            Assert.False(layout.CanChoose(layout.NameKey));
            Assert.Equal(layout.Columns.Select(column => column.Key).Distinct().Count(), layout.Columns.Count);
        }
    }

    [Fact]
    public void The_header_opens_the_menu_from_a_right_click_and_from_the_keyboard()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "DownloadManager.Desktop.WinUI", "ColumnHeaderRow.cs"));
        // A ContextFlyout opens on a right-click and on a context request, which Shift+F10 and the menu key raise.
        Assert.Contains("_header.ContextFlyout = menu;", source, StringComparison.Ordinal);
        Assert.Contains("\"Reset columns\"", source, StringComparison.Ordinal);
        Assert.Contains("ToggleMenuFlyoutItem", source, StringComparison.Ordinal);

        // Every list with a header passes the callback that gives it the menu.
        var winUi = Path.Combine(RepoRoot(), "src", "DownloadManager.Desktop.WinUI");
        Assert.Contains("SaveColumnWidths, ColumnsChosen)", File.ReadAllText(Path.Combine(winUi, "MainWindow.Lists.cs")), StringComparison.Ordinal);
        Assert.Contains("ProviderShownColumns = _columns.ExportChoices()", File.ReadAllText(Path.Combine(winUi, "ProviderPanel.cs")), StringComparison.Ordinal);
        Assert.Contains("Shown = _layout.ExportChoices()", File.ReadAllText(Path.Combine(winUi, "DetailsTable.cs")), StringComparison.Ordinal);
    }

    [Fact]
    public void Row_cells_take_their_visibility_from_the_header_alone()
    {
        // The header collapses a hidden column's cells; a binding on a cell's visibility would fight it.
        var winUi = Path.Combine(RepoRoot(), "src", "DownloadManager.Desktop.WinUI");
        foreach (var file in new[] { "MainWindow.xaml", "DownloadWindow.xaml" })
        {
            var text = File.ReadAllText(Path.Combine(winUi, file));
            Assert.DoesNotContain("TorrentColumnsVisibility", text, StringComparison.Ordinal);
            Assert.DoesNotContain("ExtractionColumnVisibility", text, StringComparison.Ordinal);
            Assert.DoesNotContain("PriorityVisibility", text, StringComparison.Ordinal);
            Assert.DoesNotContain("NextAnnounceVisibility", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Each_row_template_has_a_column_per_header_column_at_the_same_width()
    {
        var path = Path.Combine(RepoRoot(), "src", "DownloadManager.Desktop.WinUI", "MainWindow.xaml");
        var ns = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml/presentation");
        var row = XDocument.Load(path).Descendants(ns + "ListView")
            .First(list => (string?)list.Attribute(XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml") + "Name") == "JobsList")
            .Descendants(ns + "Grid.ColumnDefinitions").First();
        var widths = row.Elements(ns + "ColumnDefinition").Select(column => (string?)column.Attribute("Width")).ToList();
        var columns = ListColumnSets.Downloads().Columns;
        Assert.Equal(columns.Count, widths.Count);
        for (var i = 0; i < columns.Count; i++)
        {
            if (widths[i] is "*" or "Auto") continue;
            Assert.Equal(columns[i].DefaultWidth, double.Parse(widths[i]!, System.Globalization.CultureInfo.InvariantCulture));
        }

        var source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "DownloadManager.Desktop.WinUI", "ProviderPanel.cs"));
        var template = source[source.IndexOf("private const string ItemTemplate", StringComparison.Ordinal)..];
        template = template[..template.IndexOf("</Grid.ColumnDefinitions>", StringComparison.Ordinal)];
        Assert.Equal(ListColumnSets.ProviderItems().Columns.Count, template.Split("<ColumnDefinition ").Length - 1);
    }

    private static string RepoRoot()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "DownloadManager.slnx"))) folder = folder.Parent;
        return folder?.FullName ?? throw new InvalidOperationException("The repository wasn't found.");
    }
}

public sealed class ListSortTests
{
    private static List<string> Rows(int count) => Enumerable.Range(0, count).Select(index => $"row {index}").ToList();

    [Fact]
    public void A_row_appearing_above_a_long_list_keeps_the_list_and_its_place()
    {
        var current = Rows(100);
        var target = current.Prepend("new row").ToList();

        Assert.False(ListSort.ReplacesWholeList(sorting: false, current, target));
    }

    [Fact]
    public void A_row_going_from_above_a_long_list_keeps_the_list_and_its_place()
    {
        var current = Rows(100);

        Assert.False(ListSort.ReplacesWholeList(sorting: false, current, current.Skip(1).ToList()));
    }

    [Fact]
    public void Only_a_sort_of_a_long_list_replaces_it()
    {
        var current = Rows(100);
        var reversed = Enumerable.Reverse(current).ToList();

        Assert.True(ListSort.ReplacesWholeList(sorting: true, current, reversed));
        Assert.False(ListSort.ReplacesWholeList(sorting: false, current, reversed));
    }

    [Fact]
    public void A_sort_of_a_short_list_moves_its_rows()
    {
        var current = Rows(ListSort.MostMovesInPlace);
        var reversed = Enumerable.Reverse(current).ToList();

        Assert.False(ListSort.ReplacesWholeList(sorting: true, current, reversed));
    }

    [Fact]
    public void A_second_click_on_a_header_reverses_and_another_column_starts_in_its_usual_direction()
    {
        Assert.Equal(("name", true), ListSort.Next("added", false, "name"));
        Assert.Equal(("name", false), ListSort.Next("name", true, "name"));
        Assert.Equal(("name", true), ListSort.Next("name", false, "name"));
        Assert.Equal(("size", false), ListSort.Next("name", true, "size"));
        Assert.Equal(("rate", false), ListSort.Next("size", false, "rate"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Rows_with_nothing_to_compare_stay_at_the_end_either_way(bool ascending)
    {
        long?[] rows = [null, 5, 1, null, 9];
        var sorted = ListSort.Order(rows, row => row ?? 0, null, ascending, row => row is null).ToList();
        Assert.Equal(ascending ? [1, 5, 9, null, null] : new long?[] { 9, 5, 1, null, null }, sorted);
    }

    [Fact]
    public void Equal_rows_keep_their_order()
    {
        (int Key, string Name)[] rows = [(1, "b"), (1, "a"), (0, "c")];
        Assert.Equal(["c", "b", "a"], ListSort.Order(rows, row => row.Key, null, true).Select(row => row.Name));
        Assert.Equal(["b", "a", "c"], ListSort.Order(rows, row => row.Key, null, false).Select(row => row.Name));
    }

    [Fact]
    public void A_screen_reader_hears_the_order_in_the_header()
    {
        Assert.Equal("Size", ListSort.SpokenHeader("Size", false, true));
        Assert.Equal("Size, sorted ascending", ListSort.SpokenHeader("Size", true, true));
        Assert.Equal("Size, sorted descending", ListSort.SpokenHeader("Size", true, false));
    }

    [Fact]
    public void A_saved_sort_must_be_one_this_list_has()
    {
        Assert.True(ListColumnSets.IsDownloadSort("added"));
        Assert.True(ListColumnSets.IsDownloadSort("eta"));
        Assert.False(ListColumnSets.IsDownloadSort("icon"));
        Assert.False(ListColumnSets.IsDownloadSort("nonsense"));
        Assert.True(ListColumnSets.IsProviderSort("expires"));
        Assert.False(ListColumnSets.IsProviderSort(null));
    }

    [Fact]
    public void My_provider_sorts_by_what_the_item_says_with_unknown_values_last()
    {
        var early = new RemoteItemDto { ItemId = "a", Name = "file 10", Size = 5, FileCount = 2, CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) };
        var late = new RemoteItemDto { ItemId = "b", Name = "file 9", Size = 7, FileCount = 1, CreatedAt = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero) };
        var bare = new RemoteItemDto { ItemId = "c", Name = "file 1" };
        RemoteItemDto[] items = [early, bare, late];
        string[] Ids(string key, bool ascending) => RemoteItemSort
            .Order(items, item => item, item => RemoteText.Item(item, "Provider", null), key, ascending)
            .Select(item => item.ItemId).ToArray();

        Assert.Equal(["c", "b", "a"], Ids("name", true));
        Assert.Equal(["b", "a", "c"], Ids("size", false));
        Assert.Equal(["a", "b", "c"], Ids("size", true));
        Assert.Equal(["a", "b", "c"], Ids("files", false));
        Assert.Equal(["b", "a", "c"], Ids("added", false));
    }
}

public sealed class RegionSelectionTests
{
    [Theory]
    [InlineData(true, true, false, false, true)]
    [InlineData(false, true, false, false, false)]
    [InlineData(true, false, false, false, false)]
    [InlineData(true, true, true, false, false)]
    [InlineData(true, true, false, true, false)]
    public void Only_a_left_mouse_press_off_a_rows_own_controls_can_start_a_box(bool mouse, bool left, bool own, bool locked, bool expected)
        => Assert.Equal(expected, RegionSelection.CanStart(mouse, left, own, locked));

    [Fact]
    public void A_wobble_stays_a_click()
    {
        Assert.False(RegionSelection.PastThreshold(10, 10, 13, 14));
        Assert.True(RegionSelection.PastThreshold(10, 10, 10, 16));
        Assert.True(RegionSelection.PastThreshold(10, 10, 4, 10));
    }

    [Fact]
    public void A_box_touches_every_row_it_overlaps_whichever_way_it_was_dragged()
    {
        Assert.Equal(new RowRange(0, 0), RegionSelection.RowsTouched(1, 26, 0, 27, 10));
        Assert.Equal(new RowRange(0, 1), RegionSelection.RowsTouched(1, 28, 0, 27, 10));
        Assert.Equal(new RowRange(0, 1), RegionSelection.RowsTouched(28, 1, 0, 27, 10));
        Assert.Equal(new RowRange(1, 1), RegionSelection.RowsTouched(27, 54, 0, 27, 10));
        Assert.Equal(new RowRange(2, 2), RegionSelection.RowsTouched(60, 70, 0, 27, 10));
    }

    [Fact]
    public void Rows_are_counted_from_where_the_first_one_starts_and_past_the_ends_it_stops_at_them()
    {
        Assert.Equal(new RowRange(1, 1), RegionSelection.RowsTouched(40, 45, 10, 27, 10));
        Assert.Equal(new RowRange(0, 9), RegionSelection.RowsTouched(-500, 5000, 0, 27, 10));
    }

    [Fact]
    public void A_box_in_the_blank_below_the_rows_touches_none_and_neither_does_an_empty_list()
    {
        Assert.Null(RegionSelection.RowsTouched(280, 300, 0, 27, 10));
        Assert.Null(RegionSelection.RowsTouched(0, 100, 0, 27, 0));
        Assert.Null(RegionSelection.RowsTouched(0, 100, 0, 0, 10));
    }

    [Fact]
    public void Rows_scrolled_out_of_sight_still_count()
    {
        // Started at row 3, and now 5000 pixels of list further on.
        Assert.Equal(new RowRange(3, 188), RegionSelection.RowsTouched(90, 5100, 0, 27, 1000));
    }

    [Fact]
    public void It_scrolls_only_near_the_edges_and_faster_the_further_out()
    {
        Assert.Equal(0, RegionSelection.AutoScrollStep(100, 300));
        Assert.True(RegionSelection.AutoScrollStep(5, 300) < 0);
        Assert.True(RegionSelection.AutoScrollStep(295, 300) > 0);
        Assert.True(RegionSelection.AutoScrollStep(340, 300) > RegionSelection.AutoScrollStep(295, 300));
        Assert.Equal(RegionSelection.MostScrollPerTick, RegionSelection.AutoScrollStep(5000, 300));
        Assert.Equal(-RegionSelection.MostScrollPerTick, RegionSelection.AutoScrollStep(-5000, 300));
        Assert.Equal(0, RegionSelection.AutoScrollStep(100, 0));
    }

    [Fact]
    public void A_short_list_has_room_to_stop_scrolling_in_the_middle()
        => Assert.Equal(0, RegionSelection.AutoScrollStep(20, 40));

    [Fact]
    public void Only_what_changes_is_added_and_removed()
    {
        Same([2, 3], [], RegionSelection.Change(null, new RowRange(2, 3)));
        Same([4], [], RegionSelection.Change(new RowRange(2, 3), new RowRange(2, 4)));
        Same([1], [3], RegionSelection.Change(new RowRange(2, 3), new RowRange(1, 2)));
        Same([], [2, 3], RegionSelection.Change(new RowRange(2, 3), null));
    }

    private static void Same(int[] add, int[] remove, (List<int> Add, List<int> Remove) change)
    {
        Assert.Equal(add, change.Add);
        Assert.Equal(remove, change.Remove);
    }

    [Fact]
    public void The_box_stays_inside_the_list_and_its_start_moves_with_the_content()
    {
        Assert.Equal(new RegionBox(10, 20, 40, 30), RegionSelection.Box(10, 20, 50, 50, 0, 200, 100));
        Assert.Equal(new RegionBox(10, 20, 40, 30), RegionSelection.Box(50, 120, 10, 50, 100, 200, 100));
        Assert.Equal(new RegionBox(0, 0, 200, 100), RegionSelection.Box(-5, -5, 500, 500, 0, 200, 100));
        var scrolled = RegionSelection.Box(10, 20, 10, 50, 100, 200, 100);
        Assert.Equal(0, scrolled.Top);
        Assert.Equal(50, scrolled.Height);
    }
}

public sealed class RegionTickingTests
{
    private static RemoteFileDto File(string id, string path, bool selectable = true) => new() { FileId = id, RelativePath = path, Size = 10, Selectable = selectable };

    private static RemoteFileTree Tree() => RemoteFileTree.Build(
        [File("a", "top/a.bin"), File("b", "top/b.bin"), File("c", "top/inner/c.bin"), File("d", "e.bin"), File("x", "x.bin", selectable: false)]);

    private static RemoteFileNode Row(RemoteFileTree tree, string name) => tree.Visible().First(node => node.Name == name);

    [Fact]
    public void A_box_ticks_the_files_it_touches_and_a_later_box_forgets_them()
    {
        var tree = Tree();
        tree.ChooseRows([], [Row(tree, "a.bin"), Row(tree, "b.bin")]);
        Assert.Equal(["a", "b"], tree.ChosenFileIds.Order());
        tree.ChooseRows([], [Row(tree, "b.bin")]);
        Assert.Equal(["b"], tree.ChosenFileIds);
    }

    [Fact]
    public void With_ctrl_what_was_ticked_stays()
    {
        var tree = Tree();
        tree.SetChecked(Row(tree, "e.bin"), true);
        var keep = tree.ChosenFileIds.ToList();
        tree.ChooseRows(keep, [Row(tree, "a.bin")]);
        Assert.Equal(["a", "d"], tree.ChosenFileIds.Order());
        tree.ChooseRows(keep, []);
        Assert.Equal(["d"], tree.ChosenFileIds);
    }

    [Fact]
    public void An_open_folders_row_is_passed_over_so_a_box_over_part_of_it_takes_only_that_part()
    {
        var tree = Tree();
        var folder = Row(tree, "top");
        Assert.True(folder.IsExpanded);
        tree.ChooseRows([], [folder, Row(tree, "a.bin")]);
        Assert.Equal(["a"], tree.ChosenFileIds);
    }

    [Fact]
    public void A_closed_folders_row_stands_for_everything_under_it()
    {
        var tree = Tree();
        var folder = Row(tree, "top");
        folder.IsExpanded = false;
        tree.ChooseRows([], [folder]);
        Assert.Equal(["a", "b", "c"], tree.ChosenFileIds.Order());
    }

    [Fact]
    public void A_file_that_cant_be_chosen_stays_unticked_under_a_box()
    {
        var tree = Tree();
        tree.ChooseRows([], [Row(tree, "x.bin")]);
        Assert.Empty(tree.ChosenFileIds);
    }
}

public sealed class ProgressDetailRulesTests
{
    private static JobDto Job(string state) => new() { Id = Guid.NewGuid(), State = state, IsTerminal = state == "completed" };

    [Fact]
    public void The_file_someone_picked_stays_while_it_is_in_the_package()
    {
        var running = Job("downloading");
        var done = Job("completed");
        Assert.Equal(done.Id, ProgressDetailRules.FileToShow([running, done], done.Id));
        Assert.Equal(running.Id, ProgressDetailRules.FileToShow([running], done.Id));
    }

    [Fact]
    public void With_nothing_picked_it_shows_what_is_moving_then_what_is_unfinished_then_the_first()
    {
        var done = Job("completed");
        var queued = Job("queued");
        var running = Job("downloading");
        Assert.Equal(running.Id, ProgressDetailRules.FileToShow([done, queued, running], null));
        Assert.Equal(queued.Id, ProgressDetailRules.FileToShow([done, queued], null));
        Assert.Equal(done.Id, ProgressDetailRules.FileToShow([done], null));
        Assert.Null(ProgressDetailRules.FileToShow([], null));
    }

    [Fact]
    public void Only_a_package_of_one_file_goes_straight_to_that_files_window()
    {
        var one = Guid.NewGuid();
        var two = Guid.NewGuid();
        var listed = new HashSet<Guid> { one, two };
        Assert.Equal(one, ProgressDetailRules.OnlyFile(new DownloadGroupDto { JobIds = [one] }, listed.Contains));
        Assert.Null(ProgressDetailRules.OnlyFile(new DownloadGroupDto { JobIds = [one, two] }, listed.Contains));
        Assert.Equal(two, ProgressDetailRules.OnlyFile(new DownloadGroupDto { JobIds = [Guid.NewGuid(), two] }, listed.Contains));
        Assert.Null(ProgressDetailRules.OnlyFile(null, listed.Contains));
    }
}
