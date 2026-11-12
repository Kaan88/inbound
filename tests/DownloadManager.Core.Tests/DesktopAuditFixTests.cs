using DownloadManager.Contracts;
using DownloadManager.Desktop.WinUI;

namespace DownloadManager.Core.Tests;

/// <summary>Audit 4 (2026-11-12), WinUI desktop track: the rules behind its fixes.</summary>
public sealed class DesktopAuditFixTests
{
    private static SettingsDto Settings() => new()
    {
        DefaultDownloadDirectory = @"D:\dl",
        MaxConcurrentDownloads = 3,
        GlobalSpeedLimitBytesPerSecond = null,
        Queues = [new QueueDto { Id = 0, Name = "Main", IsRunning = true, QuotaBytesUsed = 0 }],
        Schedules = [],
        SiteLogins = [new SiteLoginDto { Id = Guid.Parse("11111111-1111-1111-1111-111111111111"), Site = "a.example", Username = "ann", HasPassword = true }],
        FavoriteFolders = [new FavoriteFolderDto { Name = "Films", Path = @"D:\films" }],
        CategoryFolders = new(StringComparer.OrdinalIgnoreCase) { ["video"] = @"D:\video" },
        CustomCategories = [new CategoryDefinitionDto { Id = "c1", Name = "Books", Extensions = ["epub"] }],
    };

    // H1, scenario 1: Options open; Stop Queue and a new schedule; OK in Options must not undo them.
    [Fact]
    public void H1_Options_save_keeps_queue_state_and_schedules_changed_while_it_was_open()
    {
        var atOpen = Settings();
        var edited = Settings();
        edited.MaxConcurrentDownloads = 5;
        var latest = Settings();
        latest.Queues[0].IsRunning = false;
        latest.Queues[0].QuotaBytesUsed = 1234;
        latest.Schedules.Add(new ScheduleDto { Id = Guid.NewGuid(), Name = "Night" });

        var sent = OptionsSettingsRebase.Rebase(atOpen, edited, latest);

        Assert.False(sent.Queues[0].IsRunning);
        Assert.Equal(1234, sent.Queues[0].QuotaBytesUsed);
        Assert.Single(sent.Schedules);
        Assert.Equal(5, sent.MaxConcurrentDownloads);
    }

    [Fact]
    public void H1_a_field_left_as_it_was_keeps_the_value_set_elsewhere_and_a_changed_one_wins()
    {
        var atOpen = Settings();
        var edited = Settings();
        edited.DefaultDownloadDirectory = @"E:\new";
        var latest = Settings();
        latest.GlobalSpeedLimitBytesPerSecond = 500_000; // the toolbar limit menu, while Options was open
        latest.DefaultDownloadDirectory = @"F:\cli";

        var sent = OptionsSettingsRebase.Rebase(atOpen, edited, latest);

        Assert.Equal(500_000, sent.GlobalSpeedLimitBytesPerSecond);
        Assert.Equal(@"E:\new", sent.DefaultDownloadDirectory);
    }

    [Fact]
    public void H1_lists_are_merged_per_entry_so_additions_elsewhere_survive_an_edit_here()
    {
        var atOpen = Settings();
        var edited = Settings();
        edited.SiteLogins[0].Username = "anna";
        edited.FavoriteFolders.Add(new FavoriteFolderDto { Name = "Music", Path = @"D:\music" });
        edited.CustomCategories.Clear();
        edited.DeleteCategoryIds = ["c1"];
        var latest = Settings();
        var cliLogin = new SiteLoginDto { Id = Guid.NewGuid(), Site = "b.example", Username = "bob" };
        latest.SiteLogins.Add(cliLogin);
        latest.FavoriteFolders.Add(new FavoriteFolderDto { Name = "Star", Path = @"D:\starred" });
        latest.CustomCategories.Add(new CategoryDefinitionDto { Id = "c2", Name = "Code" });

        var sent = OptionsSettingsRebase.Rebase(atOpen, edited, latest);

        Assert.Equal(["anna", "bob"], sent.SiteLogins.Select(login => login.Username));
        Assert.Equal([@"D:\films", @"D:\music", @"D:\starred"], sent.FavoriteFolders.Select(favorite => favorite.Path));
        Assert.Equal(["c2"], sent.CustomCategories.Select(category => category.Id));
        Assert.Equal(["c1"], sent.DeleteCategoryIds);
    }

    [Fact]
    public void H1_an_untouched_entry_removed_elsewhere_stays_removed_and_maps_merge_per_key()
    {
        var atOpen = Settings();
        var edited = Settings();
        edited.CategoryFolders["music"] = @"D:\music";
        edited.EventSoundFiles["completed"] = @"C:\ding.wav";
        var latest = Settings();
        latest.SiteLogins.Clear(); // `dm site remove` while Options was open
        latest.CategoryFolders["video"] = @"E:\video"; // moved by the Host meanwhile

        var sent = OptionsSettingsRebase.Rebase(atOpen, edited, latest);

        Assert.Empty(sent.SiteLogins);
        Assert.Equal(@"E:\video", sent.CategoryFolders["video"]);
        Assert.Equal(@"D:\music", sent.CategoryFolders["music"]);
        Assert.Equal(@"C:\ding.wav", sent.EventSoundFiles["completed"]);
    }

    [Fact]
    public void H1_a_typed_proxy_password_is_a_change_and_an_untouched_proxy_keeps_the_hosts()
    {
        var atOpen = Settings();
        var edited = Settings();
        var latest = Settings();
        latest.Proxy = new ProxyDto { Mode = "manual", Address = "http://p:8080", HasPassword = true };
        Assert.Equal("http://p:8080", OptionsSettingsRebase.Rebase(atOpen, edited, latest).Proxy.Address);

        edited.Proxy = new ProxyDto { Mode = "manual", Address = "http://q:3128", Password = "x" };
        var sent = OptionsSettingsRebase.Rebase(atOpen, edited, Settings());
        Assert.Equal("http://q:3128", sent.Proxy.Address);
        Assert.Equal("x", sent.Proxy.Password);
    }

    private static JobDto Queued(int queueId, int priority = 0, int minutes = 0) => new()
    {
        Id = Guid.NewGuid(), IsQueued = true, QueueId = queueId, Priority = priority,
        CreatedAt = DateTimeOffset.UnixEpoch.AddMinutes(minutes), State = "queued",
    };

    // M2: a download added to the queue from the main window shows in the Files tab, and a staged reorder still applies.
    [Fact]
    public void M2_a_download_that_joins_the_queue_makes_the_files_list_stale_and_joins_a_staged_order_at_the_end()
    {
        var a = Queued(1, minutes: 1);
        var b = Queued(1, minutes: 2);
        var shown = new List<Guid> { b.Id, a.Id }; // reordered by the user, not applied yet
        var c = Queued(1, minutes: 3);
        JobDto[] members = [a, b, c, Queued(2)];
        var inQueue = SchedulerFileRules.Members(members, 1);

        Assert.True(SchedulerFileRules.ListIsStale(shown, inQueue, []));
        Assert.Equal([b.Id, a.Id, c.Id], SchedulerFileRules.MergeOrder(shown, inQueue, [], []));
    }

    [Fact]
    public void M2_a_download_that_left_drops_out_and_one_staged_to_leave_stays_out()
    {
        var a = Queued(1);
        var b = Queued(1, minutes: 1);
        var gone = Guid.NewGuid();
        var inQueue = SchedulerFileRules.Members([a, b], 1);

        Assert.Equal([a.Id], SchedulerFileRules.MergeOrder([gone, a.Id], inQueue, [], [b.Id]));
        Assert.False(SchedulerFileRules.ListIsStale([a.Id], inQueue, [b.Id]));
    }

    [Fact]
    public void M2_the_hosts_order_is_priority_then_saved_order_then_oldest()
    {
        var old = Queued(1, minutes: 1);
        var newer = Queued(1, minutes: 2);
        var urgent = Queued(1, priority: 5, minutes: 3);
        var saved = Queued(1, minutes: 4);

        Assert.Equal([urgent.Id, saved.Id, old.Id, newer.Id],
            SchedulerFileRules.HostOrder([old, newer, urgent, saved], [saved.Id]));
    }

    [Fact]
    public void H1_torrent_options_changed_here_are_sent_and_untouched_ones_keep_the_hosts()
    {
        var atOpen = Settings();
        var edited = Settings();
        edited.TorrentSeedRatio = 2;
        var latest = Settings();
        latest.TorrentsEnabled = true; // turned on from the web UI while Options was open

        var sent = OptionsSettingsRebase.Rebase(atOpen, edited, latest);

        Assert.Equal(2, sent.TorrentSeedRatio);
        Assert.True(sent.TorrentsEnabled);
    }

    // L5: a reconnect's read fills the per-torrent Options form only when the user hasn't changed it since.
    [Fact]
    public void L5_a_read_after_a_reconnect_keeps_edits_not_yet_applied()
    {
        var filled = new TorrentControlRules.OptionsForm(double.NaN, 100, "global", double.NaN, false);

        Assert.True(TorrentControlRules.ReadFillsForm(null, filled));
        Assert.True(TorrentControlRules.ReadFillsForm(filled, filled with { }));
        Assert.False(TorrentControlRules.ReadFillsForm(filled, filled with { UploadKilobytes = 250 }));
        Assert.False(TorrentControlRules.ReadFillsForm(filled, filled with { Sequential = true }));
    }

    // L7: Redownload of a file still being verified asks to stop it first.
    [Theory]
    [InlineData("verifying", true)]
    [InlineData("downloading", true)]
    [InlineData("paused", true)]
    [InlineData("completed", false)]
    [InlineData("failed", false)]
    [InlineData("cancelled", false)]
    public void L7_redownload_needs_a_stop_while_the_download_can_still_move(string state, bool needsStop)
        => Assert.Equal(needsStop, FileMenuRules.RedownloadNeedsStop(state));

    // H1 (review): a field the form shows coarser than it is stored. The toolbar's 0.3 MB/s stores 314,573 B/s, which
    // the KB/s box shows as 307 and gives back as 314,368. Untouched, that must not count as a change on OK.
    [Fact]
    public void H1_an_untouched_speed_limit_that_is_not_whole_KB_keeps_the_value_set_elsewhere()
    {
        var stored = Settings();
        stored.GlobalSpeedLimitBytesPerSecond = 314_573;
        var atOpen = OptionsSettingsRebase.AsShown(Clone(stored));
        // What Options sends for a box left alone: the shown KB/s times 1024 (MainWindow: kb * 1024L).
        var edited = Clone(stored);
        edited.GlobalSpeedLimitBytesPerSecond = stored.GlobalSpeedLimitBytesPerSecond / 1024 * 1024;
        var latest = Clone(stored);
        latest.GlobalSpeedLimitBytesPerSecond = 2_621_440; // the CLI set 2.5 MB/s while Options was open

        Assert.Equal(2_621_440, OptionsSettingsRebase.Rebase(atOpen, edited, latest).GlobalSpeedLimitBytesPerSecond);

        // A limit typed in the box is still sent.
        var typed = Clone(stored);
        typed.GlobalSpeedLimitBytesPerSecond = 500 * 1024;
        Assert.Equal(500 * 1024, OptionsSettingsRebase.Rebase(OptionsSettingsRebase.AsShown(Clone(stored)), typed, Clone(latest)).GlobalSpeedLimitBytesPerSecond);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(1_048_576L, 1_048_576L)]
    [InlineData(314_573L, 314_368L)]
    [InlineData(100L, 1024L)] // the box holds at least 1 KB/s
    public void H1_the_speed_limit_counts_as_the_KB_box_shows_it(long? stored, long? shown)
    {
        var settings = Settings();
        settings.GlobalSpeedLimitBytesPerSecond = stored;
        Assert.Equal(shown, OptionsSettingsRebase.AsShown(settings).GlobalSpeedLimitBytesPerSecond);
    }

    // H1 (review): the proxy is rebuilt from the page on every OK. An address kept under "system" comes back empty, and
    // a stored "https://host:port/" comes back as host:port; untouched, neither may overwrite a proxy changed elsewhere.
    [Theory]
    [InlineData("system", "http://old:3128", null)]
    [InlineData("manual", "https://proxy.example:8080/", "proxy.example:8080")]
    public void H1_an_untouched_proxy_that_the_page_rebuilds_differently_keeps_the_hosts(string mode, string address, string? rebuilt)
    {
        var stored = Settings();
        stored.Proxy = new ProxyDto { Mode = mode, Address = address, Username = "u", HasPassword = true };
        var atOpen = OptionsSettingsRebase.AsShown(Clone(stored));
        Assert.Equal(rebuilt, atOpen.Proxy.Address);
        // What the proxy page sends when nothing on it was touched.
        var edited = Clone(stored);
        edited.Proxy = new ProxyDto
        {
            Mode = mode,
            Address = mode == "manual" ? ProxyAddress.Compose(ProxyAddress.Split(address).ProtocolIndex, ProxyAddress.Split(address).HostPort) : null,
            Username = "u",
            HasPassword = true,
        };
        var latest = Clone(stored);
        latest.Proxy = new ProxyDto { Mode = "manual", Address = "socks5://elsewhere:1080" };

        Assert.Equal("socks5://elsewhere:1080", OptionsSettingsRebase.Rebase(atOpen, edited, latest).Proxy.Address);
    }

    private static SettingsDto Clone(SettingsDto settings) =>
        System.Text.Json.JsonSerializer.Deserialize<SettingsDto>(System.Text.Json.JsonSerializer.Serialize(settings))!;

    // M2 (review): a reorder staged on queue 1, then queue 2 shown, then a download joins queue 1 from the main window.
    // Apply merges with the Host's members as they are now, so the order still matches the queue and is sent.
    [Fact]
    public void M2_a_reorder_staged_on_a_queue_not_on_screen_takes_in_a_download_that_joined_it_on_apply()
    {
        var a = Queued(1, minutes: 1);
        var b = Queued(1, minutes: 2);
        List<Guid> staged = [b.Id, a.Id];
        var joined = Queued(1, minutes: 3);
        var queue = new QueueDto { Id = 1, Name = "Main" };
        JobDto[] latest = [a, b, joined, Queued(2)];

        var order = SchedulerFileRules.OrderToApply(staged, latest, queue, []);

        Assert.Equal([b.Id, a.Id, joined.Id], order);
        Assert.True(order.ToHashSet().SetEquals(SchedulerFileRules.Members(latest, 1).Select(job => job.Id)));
    }

    // M11 (review): job events that came while the Scheduler's jobs.query was out are put onto its reply.
    [Fact]
    public void M11_job_events_during_a_refresh_are_kept_over_an_older_reply_and_removals_apply()
    {
        var at = DateTimeOffset.UnixEpoch.AddHours(1);
        var started = Queued(1);
        started.UpdatedAt = at;
        var removed = Queued(1);
        var added = Queued(2);
        // The reply was built before the start: the download still queued there.
        var replyCopy = new JobDto { Id = started.Id, IsQueued = true, QueueId = 1, State = "queued", UpdatedAt = at.AddSeconds(-5) };
        var newer = new JobDto { Id = removed.Id, IsQueued = true, QueueId = 1, State = "paused", UpdatedAt = at.AddSeconds(10) };
        var jobs = new List<JobDto> { replyCopy, removed };
        var stale = new JobDto { Id = added.Id, State = "queued", UpdatedAt = at };
        var current = new JobDto { Id = added.Id, State = "downloading", UpdatedAt = at.AddSeconds(1) };
        jobs.Add(current);

        SchedulerFileRules.ReplayJobEvents(jobs, [
            (new JobDto { Id = started.Id, IsQueued = true, QueueId = 1, State = "downloading", UpdatedAt = at }, "updated"),
            (stale, "updated"),
            (newer, "removed"),
        ]);

        Assert.Equal("downloading", jobs.Single(job => job.Id == started.Id).State);
        Assert.Same(current, jobs.Single(job => job.Id == added.Id));
        Assert.DoesNotContain(jobs, job => job.Id == removed.Id);
    }

    // M11 (review): a job the reply does not have yet (added while it was out) is added.
    [Fact]
    public void M11_a_download_added_while_the_refresh_was_out_is_added_to_the_reply()
    {
        var jobs = new List<JobDto>();
        var added = Queued(1);
        SchedulerFileRules.ReplayJobEvents(jobs, [(added, "added")]);
        Assert.Same(added, Assert.Single(jobs));
    }
}
