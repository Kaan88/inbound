using DownloadManager.Contracts;
using DownloadManager.Desktop.WinUI;

namespace DownloadManager.Core.Tests;

/// <summary>UI.R32: My provider inside the main window (A, C and B's notice).</summary>
public sealed class ProviderViewsTests
{
    private static readonly string[] Kinds = ["torrents", "webdl", "usenet"];

    // ---- State carried between the sidebar view, the drawer and a window of its own ----

    [Fact]
    public void The_open_tab_carries_over_while_the_account_still_has_it()
    {
        var state = new ProviderViewState("webdl", null, null, null);
        Assert.Equal("webdl", ProviderViewMemory.Tab(state, Kinds));
        Assert.Equal("torrents", ProviderViewMemory.Tab(state, ["torrents"]));
        Assert.Equal("torrents", ProviderViewMemory.Tab(null, Kinds));
        Assert.Null(ProviderViewMemory.Tab(state, []));
    }

    [Fact]
    public void The_chosen_item_carries_over_only_while_it_is_still_listed()
    {
        var state = new ProviderViewState("torrents", "42", "40", null);
        Assert.Equal("42", ProviderViewMemory.Selected(state, ["40", "41", "42"]));
        Assert.Null(ProviderViewMemory.Selected(state, ["40", "41"]));
        Assert.Null(ProviderViewMemory.Selected(state with { SelectedItemId = null }, ["42"]));
    }

    [Fact]
    public void The_scroll_carries_over_as_the_top_row_then_the_chosen_one_then_the_start()
    {
        var state = new ProviderViewState("torrents", "42", "40", null);
        Assert.Equal("40", ProviderViewMemory.Top(state, ["40", "41", "42"]));
        Assert.Equal("42", ProviderViewMemory.Top(state, ["41", "42"]));
        Assert.Null(ProviderViewMemory.Top(state, ["41"]));
    }

    [Fact]
    public void The_typed_folder_carries_over_before_the_last_used_and_the_default()
    {
        Assert.Equal(@"D:\Media", ProviderViewMemory.Destination(new ProviderViewState(null, null, null, @"D:\Media"), @"E:\Last", @"C:\Default"));
        Assert.Equal(@"E:\Last", ProviderViewMemory.Destination(new ProviderViewState(null, null, null, " "), @"E:\Last", @"C:\Default"));
        Assert.Equal(@"C:\Default", ProviderViewMemory.Destination(null, null, @"C:\Default"));
        Assert.Equal(string.Empty, ProviderViewMemory.Destination(null, null, null));
    }

    // ---- The "On list" tag ----

    private static RemoteJobDto Job(string state) => new() { Id = Guid.NewGuid(), State = state, ItemId = "7", Provider = "torbox" };

    private static JobDto File(string state, long? total, long received) => new() { Id = Guid.NewGuid(), State = state, TotalBytes = total, ReceivedBytes = received };

    [Fact]
    public void An_item_with_no_job_or_only_a_failed_or_removed_one_is_not_on_the_list()
    {
        Assert.Null(ProviderOnList.Tag(null, []));
        Assert.Null(ProviderOnList.Tag(Job("failed"), []));
        Assert.Null(ProviderOnList.Tag(Job("removed"), []));
    }

    [Fact]
    public void A_job_still_waiting_on_the_provider_is_on_the_list_with_no_figure()
    {
        Assert.Equal("On list", ProviderOnList.Tag(Job("waiting"), []));
        Assert.Equal("On list", ProviderOnList.Tag(Job("awaitingSelection"), []));
        Assert.Equal("On list", ProviderOnList.Tag(Job("localStarted"), []));
    }

    [Fact]
    public void Local_files_give_the_share_of_their_bytes_received()
    {
        var files = new[] { File("completed", 400, 400), File("downloading", 600, 210), File("queued", 1000, 0) };
        // 610 of 2000 bytes.
        Assert.Equal("On list · 30%", ProviderOnList.Tag(Job("localStarted"), files));
    }

    [Fact]
    public void It_never_says_100_percent_before_every_file_is_complete_and_says_done_after()
    {
        var almost = new[] { File("completed", 1000, 1000), File("verifying", 1000, 1000) };
        Assert.Equal("On list · 99%", ProviderOnList.Tag(Job("localStarted"), almost));
        var done = new[] { File("completed", 1000, 1000), File("completed", null, 0) };
        Assert.Equal("On list · done", ProviderOnList.Tag(Job("localStarted"), done));
    }

    [Fact]
    public void Files_of_unknown_size_give_no_figure()
        => Assert.Equal("On list", ProviderOnList.Tag(Job("localStarted"), [File("connecting", null, 0)]));

    // ---- The drawer on a narrow window ----

    [Fact]
    public void A_pinned_drawer_docks_while_the_list_keeps_its_room_and_covers_it_otherwise()
    {
        Assert.True(ProviderDrawerLayout.Inline(pinned: true, available: 1300, drawerWidth: 420));
        Assert.True(ProviderDrawerLayout.Inline(pinned: true, available: 420 + ProviderDrawerLayout.MinimumListWidth, drawerWidth: 420));
        Assert.False(ProviderDrawerLayout.Inline(pinned: true, available: 420 + ProviderDrawerLayout.MinimumListWidth - 1, drawerWidth: 420));
        Assert.False(ProviderDrawerLayout.Inline(pinned: false, available: 2000, drawerWidth: 420));
    }

    [Fact]
    public void The_drawers_width_stays_within_its_limits_and_the_room_there_is()
    {
        Assert.Equal(ProviderDrawerLayout.DefaultWidth, ProviderDrawerLayout.Width(null, 1400));
        Assert.Equal(ProviderDrawerLayout.MinimumWidth, ProviderDrawerLayout.Width(100, 1400));
        Assert.Equal(ProviderDrawerLayout.MaximumWidth, ProviderDrawerLayout.Width(5000, 3000));
        Assert.Equal(500, ProviderDrawerLayout.Width(900, 620));
        Assert.Equal(ProviderDrawerLayout.MinimumWidth, ProviderDrawerLayout.Width(600, 300));
        Assert.Equal(ProviderDrawerLayout.DefaultWidth, ProviderDrawerLayout.Width(double.NaN, 1400));
    }

    // ---- The notice and the row menu ----

    [Fact]
    public void The_notice_says_how_many_files_came_from_which_item()
    {
        Assert.Equal("Added 4 files from Blender Open Movies", ProviderNotice.Added(4, "Blender Open Movies"));
        Assert.Equal("Added 1 file from x.iso", ProviderNotice.Added(1, "x.iso"));
        Assert.Equal("Added all files from x", ProviderNotice.Added(null, "x"));
    }

    [Fact]
    public void The_row_menu_leads_to_the_list_and_to_a_window_of_its_own_where_they_apply()
    {
        var job = Job("localStarted");
        var main = RemoteRules.ItemMenu(canDownload: true, job, "TorBox", acting: false, loading: false, onList: true, ownWindow: true);
        Assert.Equal(["open", "downloadAll", null, "showInList", "copyName", "refresh", "ownWindow", null, "deleteFromProvider"], main.Select(entry => entry.Action));
        Assert.Equal("Show in download list", main.Single(entry => entry.Action == "showInList").Label);
        Assert.Equal("Open in its own window", main.Single(entry => entry.Action == "ownWindow").Label);

        var own = RemoteRules.ItemMenu(canDownload: true, null, "TorBox", acting: false, loading: false);
        Assert.DoesNotContain(own, entry => entry.Action is "showInList" or "ownWindow");
    }
}

public sealed class ProviderOpeningTests
{
    [Fact]
    public void My_provider_opens_in_the_full_view_unless_the_drawer_is_chosen()
    {
        Assert.False(ProviderOpening.OpensDrawer(null));
        Assert.False(ProviderOpening.OpensDrawer("full"));
        Assert.False(ProviderOpening.OpensDrawer("Drawer"));
        Assert.False(ProviderOpening.OpensDrawer("something else"));
        Assert.True(ProviderOpening.OpensDrawer("drawer"));
        Assert.Equal(ProviderOpening.Full, ProviderOpening.Choices[0]);
        Assert.Equal(ProviderOpening.Drawer, ProviderOpening.Normalize("drawer"));
        Assert.Equal(ProviderOpening.Full, ProviderOpening.Normalize(""));
    }
}
