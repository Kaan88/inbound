using DownloadManager.Contracts;
using DownloadManager.Mobile;

namespace DownloadManager.Mobile.Tests;

public sealed class DownloadListTests
{
    private static (FakeHostConnection host, DownloadsModel model, DownloadList list) Setup(params JobDto[] jobs)
    {
        var host = new FakeHostConnection();
        var model = new DownloadsModel(host, a => a(), TimeSpan.Zero);
        model.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        host.Snapshot("/dl", jobs);
        var list = new DownloadList(model, a => a());
        return (host, model, list);
    }

    private static JobDto Active(string name, int minute, long received = 100, long total = 1000, double rate = 100) =>
        Jobs.Make("downloading", name, total, received, rate, minute: minute);

    [Fact]
    public void Chips_count_each_state_view_and_paused_is_only_in_All()
    {
        var (_, _, list) = Setup(Active("a", 1), Active("b", 2), Jobs.Make("queued", "c", minute: 3), Jobs.Make("paused", "d", 10, 5, minute: 4),
            Jobs.Make("completed", "e", 10, 10, minute: 5), Jobs.Make("failed", "f", minute: 6), Jobs.Make("cancelled", "g", minute: 7));
        Assert.Equal("All 7", list.ChipLabel(DownloadFilter.All));
        Assert.Equal("Active 2", list.ChipLabel(DownloadFilter.Active));
        Assert.Equal("Queued 1", list.ChipLabel(DownloadFilter.Queued));
        Assert.Equal("Finished 1", list.ChipLabel(DownloadFilter.Finished));
        Assert.Equal("Failed 2", list.ChipLabel(DownloadFilter.Failed));
        Assert.Equal(7, list.Visible.Count);
    }

    [Fact]
    public void A_filter_shows_only_its_rows_in_the_same_newest_first_order()
    {
        var (_, _, list) = Setup(Active("old", 1), Jobs.Make("completed", "done", 5, 5, minute: 2), Active("new", 3));
        list.Filter = DownloadFilter.Active;
        Assert.Equal(["new", "old"], list.Visible.Select(r => r.Name));
        list.Filter = DownloadFilter.All;
        Assert.Equal(["new", "done", "old"], list.Visible.Select(r => r.Name));
    }

    [Fact]
    public void Search_matches_the_name_and_the_address_ignoring_case()
    {
        var (_, _, list) = Setup(Active("Ubuntu.iso", 1), Active("movie.mp4", 2));
        list.Search = "  UBU ";
        Assert.Equal(["Ubuntu.iso"], list.Visible.Select(r => r.Name));
        list.Search = "example.test/movie";
        Assert.Equal(["movie.mp4"], list.Visible.Select(r => r.Name));
        list.Search = "";
        Assert.Equal(2, list.Visible.Count);
    }

    [Fact]
    public void A_row_that_changes_state_leaves_and_joins_views_without_moving_the_others()
    {
        var a = Active("a", 1);
        var (host, _, list) = Setup(a, Active("b", 2));
        list.Filter = DownloadFilter.Active;
        var changes = new List<string>();
        list.Visible.CollectionChanged += (_, e) => changes.Add(e.Action.ToString());
        a.State = "completed"; a.TotalBytes = 1000; a.ReceivedBytes = 1000;
        host.Event(a);
        Assert.Equal(["b"], list.Visible.Select(r => r.Name));
        Assert.Equal(["Remove"], changes);
        Assert.Equal("Finished 1", list.ChipLabel(DownloadFilter.Finished));
    }

    [Fact]
    public void A_new_job_is_inserted_on_top_of_the_matching_view_only()
    {
        var (host, _, list) = Setup(Active("a", 1));
        list.Filter = DownloadFilter.Finished;
        host.Event(Active("fresh", 9), "added");
        Assert.Empty(list.Visible);
        list.Filter = DownloadFilter.All;
        Assert.Equal(["fresh", "a"], list.Visible.Select(r => r.Name));
    }

    [Fact]
    public void Summary_adds_the_rates_counts_active_and_queued_and_estimates_the_time_left()
    {
        var (_, _, list) = Setup(Active("a", 1, received: 0, total: 6000, rate: 100), Active("b", 2, received: 0, total: 6000, rate: 200), Jobs.Make("queued", "c", minute: 3));
        Assert.Equal(300, list.Summary.Rate);
        Assert.Equal(2, list.Summary.Active);
        Assert.Equal(1, list.Summary.Queued);
        Assert.Equal(40, list.Summary.SecondsLeft);
        Assert.Equal("300 B/s", list.Summary.Headline);
        Assert.Equal("2 active · 1 queued · less than 2 min left", list.Summary.Details);
    }

    [Fact]
    public void Summary_is_idle_with_no_active_downloads_and_leaves_out_what_is_zero()
    {
        var (_, _, list) = Setup(Jobs.Make("queued", "c"), Jobs.Make("completed", "d", 5, 5));
        Assert.Equal("Idle", list.Summary.Headline);
        Assert.Equal("1 queued", list.Summary.Details);
        var (_, _, empty) = Setup();
        Assert.Equal("Nothing is downloading", empty.Summary.Details);
        Assert.True(empty.NothingAtAll);
    }

    [Fact]
    public void Summary_time_left_is_unknown_when_an_active_download_has_no_size()
    {
        var (_, _, list) = Setup(Active("a", 1, total: 1000), Jobs.Make("downloading", "b", null, 10, 50, minute: 2));
        Assert.Null(list.Summary.SecondsLeft);
        Assert.Equal("2 active", list.Summary.Details);
    }

    [Fact]
    public void The_speed_history_gets_one_sample_per_second_while_active_and_none_when_idle()
    {
        var now = new DateTimeOffset(2026, 11, 17, 9, 0, 0, TimeSpan.Zero);
        var a = Active("a", 1);
        var host = new FakeHostConnection();
        var model = new DownloadsModel(host, x => x(), TimeSpan.Zero);
        model.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        host.Snapshot("/dl", a);
        var list = new DownloadList(model, x => x(), () => now);
        Assert.Single(list.Summary.History);
        host.Event(a); // same second: no new sample
        Assert.Single(list.Summary.History);
        now = now.AddSeconds(1);
        a.BytesPerSecond = 250;
        host.Event(a);
        Assert.Equal([100f, 250f], list.Summary.History);
        a.State = "paused";
        host.Event(a);
        Assert.Empty(list.Summary.History);
    }

    [Fact]
    public void Selection_starts_on_long_press_toggles_and_ends_with_the_last_row()
    {
        var (_, _, list) = Setup(Active("a", 1), Active("b", 2), Active("c", 3));
        var raised = 0;
        list.SelectionChanged += () => raised++;
        Assert.False(list.Selecting);
        list.StartSelecting(list.Visible[0]);
        Assert.True(list.Selecting);
        list.Toggle(list.Visible[1]);
        Assert.Equal("2 selected", list.SelectionTitle);
        Assert.True(list.IsSelected(list.Visible[1]));
        list.Toggle(list.Visible[0]);
        list.Toggle(list.Visible[1]);
        Assert.False(list.Selecting);
        Assert.Equal(4, raised);
    }

    [Fact]
    public void Select_all_takes_what_is_visible_and_clear_ends_it()
    {
        var (_, _, list) = Setup(Active("a", 1), Jobs.Make("completed", "b", 5, 5, minute: 2));
        list.Filter = DownloadFilter.Active;
        list.SelectAll();
        Assert.Equal(1, list.SelectedCount);
        list.ClearSelection();
        Assert.False(list.Selecting);
    }

    [Fact]
    public void A_removed_row_leaves_the_selection()
    {
        var a = Active("a", 1);
        var (host, _, list) = Setup(a, Active("b", 2));
        list.StartSelecting(list.Visible.First(r => r.Id == a.Id));
        host.Event(a, "removed");
        Assert.False(list.Selecting);
        Assert.Single(list.Visible);
    }

    [Fact]
    public void The_selection_bar_offers_play_and_pause_by_what_is_selected()
    {
        var (_, _, list) = Setup(Active("a", 1), Jobs.Make("paused", "b", 10, 5, minute: 2), Jobs.Make("completed", "c", 5, 5, minute: 3));
        list.StartSelecting(list.Visible.First(r => r.Name == "c"));
        Assert.False(list.CanPauseSelection);
        Assert.False(list.CanResumeSelection);
        list.Toggle(list.Visible.First(r => r.Name == "b"));
        Assert.True(list.CanResumeSelection);
        Assert.False(list.CanPauseSelection);
        list.Toggle(list.Visible.First(r => r.Name == "a"));
        Assert.True(list.CanPauseSelection);
    }

    [Fact]
    public void Delete_asks_about_files_only_when_there_are_some()
    {
        var finished = Jobs.Row(Jobs.Make("completed", "done.zip", 10, 10));
        var partial = Jobs.Row(Jobs.Make("paused", "half.zip", 10, 4));
        var untouched = Jobs.Row(Jobs.Make("queued", "new.zip"));
        var one = DownloadList.PlanDelete([finished]);
        Assert.Equal("Delete \"done.zip\"?", one.Title);
        Assert.True(one.HasFiles);
        Assert.Equal("Remove and delete the file", one.DeleteFilesLabel);
        var many = DownloadList.PlanDelete([finished, partial, untouched]);
        Assert.Equal("Delete 3 downloads?", many.Title);
        Assert.Equal(2, many.WithFiles);
        Assert.Equal("Remove and delete 2 files", many.DeleteFilesLabel);
        var none = DownloadList.PlanDelete([untouched]);
        Assert.False(none.HasFiles);
        Assert.Contains("Nothing has been saved", none.Message);
    }

    [Fact]
    public void A_finished_download_whose_file_was_removed_after_extraction_has_no_file_to_ask_about()
    {
        var j = Jobs.Make("completed", "x.zip", 10, 10);
        j.FileRemovedAfterExtraction = true;
        var plan = DownloadList.PlanDelete([Jobs.Row(j)]);
        Assert.False(plan.HasFiles);
        Assert.Equal(RowAction.None, Jobs.Row(j).Action);
    }
}

public sealed class RowStateTests
{
    [Theory]
    [InlineData("downloading", RowGroup.Active, RowAction.Pause)]
    [InlineData("connecting", RowGroup.Active, RowAction.Pause)]
    [InlineData("paused", RowGroup.Paused, RowAction.Resume)]
    [InlineData("queued", RowGroup.Queued, RowAction.Resume)]
    [InlineData("retrywaiting", RowGroup.Queued, RowAction.Pause)]
    [InlineData("completed", RowGroup.Finished, RowAction.ShowInFolder)]
    [InlineData("failed", RowGroup.Failed, RowAction.Retry)]
    [InlineData("cancelled", RowGroup.Failed, RowAction.Retry)]
    public void Each_state_has_a_view_and_an_end_button(string state, RowGroup group, RowAction action)
    {
        var row = Jobs.Row(Jobs.Make(state, total: 10, received: 1));
        Assert.Equal(group, row.Group);
        Assert.Equal(action, row.Action);
    }

    [Fact]
    public void A_finished_row_says_when_it_finished()
    {
        var done = new DateTimeOffset(2026, 11, 17, 13, 52, 0, TimeSpan.Zero);
        var j = Jobs.Make("completed", "invoice.pdf", 284_000, 284_000);
        j.CompletedAt = done;
        var row = new JobRow { Id = j.Id };
        row.ApplyFrom(j, done);
        Assert.Equal("Finished · 284 KB · " + done.ToLocalTime().ToString("HH:mm"), row.Subtitle);
        row.ApplyFrom(j, done.AddDays(3));
        Assert.EndsWith(done.ToLocalTime().ToString("d MMM", System.Globalization.CultureInfo.InvariantCulture), row.Subtitle);
    }

    [Fact]
    public void Changing_state_raises_the_group_and_action()
    {
        var j = Jobs.Make("downloading", total: 10, received: 1);
        var row = Jobs.Row(j);
        var raised = new List<string?>();
        row.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        j.State = "paused";
        row.ApplyFrom(j);
        Assert.Contains(nameof(JobRow.Group), raised);
        Assert.Contains(nameof(JobRow.Action), raised);
    }
}

public sealed class DownloadsModelCommandTests
{
    private static (FakeHostConnection host, DownloadsModel model) Setup()
    {
        var host = new FakeHostConnection();
        var model = new DownloadsModel(host, a => a(), TimeSpan.Zero);
        model.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        host.Snapshot("/storage/Download");
        return (host, model);
    }

    private static IpcMessage Ok(IpcMessage req) => IpcMessage.Create(MessageTypes.JobActionResult, new JobActionResponse { Ok = true }, req.Id);

    [Fact]
    public async Task Pause_resume_retry_and_redownload_send_their_command_with_the_job_id()
    {
        var (host, model) = Setup();
        host.Reply = Ok;
        var id = Guid.NewGuid();
        Assert.True((await model.PauseAsync(id)).Ok);
        Assert.True((await model.ResumeAsync(id)).Ok);
        Assert.True((await model.RetryAsync(id)).Ok);
        Assert.True((await model.RedownloadAsync(id)).Ok);
        Assert.Equal([MessageTypes.JobPause, MessageTypes.JobResume, MessageTypes.JobRetry, MessageTypes.JobRedownload], host.Sent.Select(m => m.Type));
        Assert.All(host.Sent, m => Assert.Equal(id, m.GetPayload<JobActionRequest>().JobId));
    }

    [Fact]
    public async Task A_refusal_comes_back_as_a_failed_result_with_the_hosts_words()
    {
        var (host, model) = Setup();
        host.Reply = req => IpcMessage.Create(MessageTypes.JobActionResult, new JobActionResponse { Ok = false, Error = "Job not found." }, req.Id);
        var r = await model.PauseAsync(Guid.NewGuid());
        Assert.False(r.Ok);
        Assert.Equal("Job not found.", r.Error);
    }

    [Fact]
    public async Task A_connection_that_throws_is_a_failed_result_not_an_exception()
    {
        var (host, model) = Setup();
        host.Reply = _ => throw new InvalidOperationException("gone");
        Assert.Equal("gone", (await model.ResumeAsync(Guid.NewGuid())).Error);
    }

    [Fact]
    public async Task Remove_sends_all_ids_and_the_file_choice()
    {
        var (host, model) = Setup();
        host.Reply = Ok;
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        Assert.True((await model.RemoveAsync(ids, deleteFile: true)).Ok);
        var sent = host.Sent.Single();
        Assert.Equal(MessageTypes.JobRemove, sent.Type);
        var p = sent.GetPayload<JobActionRequest>();
        Assert.True(p.DeleteFile);
        Assert.Equal(ids, p.JobIds);
        Assert.True((await model.RemoveAsync([], false)).Ok);
        Assert.Single(host.Sent);
    }

    [Fact]
    public async Task Pause_all_and_resume_all_send_their_commands()
    {
        var (host, model) = Setup();
        host.Reply = req => IpcMessage.Create(MessageTypes.HostActionResult, new HostActionResult { Ok = true }, req.Id);
        Assert.True((await model.PauseAllAsync(true)).Ok);
        Assert.True((await model.PauseAllAsync(false)).Ok);
        Assert.Equal([MessageTypes.JobsPauseAll, MessageTypes.JobsResumeAll], host.Sent.Select(m => m.Type));
    }

    [Fact]
    public async Task Segments_come_back_as_a_list_and_an_odd_reply_as_none()
    {
        var (host, model) = Setup();
        host.Reply = req => IpcMessage.Create(MessageTypes.JobSegmentsResult, new List<SegmentDto> { new() { Fraction = 0.5 }, new() { IsComplete = true, Fraction = 1 } }, req.Id);
        Assert.Equal(2, (await model.SegmentsAsync(Guid.NewGuid())).Count);
        host.Reply = req => IpcMessage.Create(MessageTypes.JobActionResult, new JobActionResponse(), req.Id);
        Assert.Empty(await model.SegmentsAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task The_snapshot_settings_are_kept_for_the_screens()
    {
        var host = new FakeHostConnection();
        var model = new DownloadsModel(host, a => a(), TimeSpan.Zero);
        await model.StartAsync(CancellationToken.None);
        host.Push(IpcMessage.Create(MessageTypes.JobsSnapshot, new SnapshotDto { Settings = new SettingsDto { DefaultConnections = 4, MaxConnectionsPerHost = 6, DefaultDownloadDirectory = "/d" } }));
        Assert.Equal(4, model.Settings.DefaultConnections);
        Assert.Equal("/d", model.DefaultDownloadDirectory);
    }
}

public sealed class NewDownloadTests
{
    private static (FakeHostConnection host, NewDownloadModel m) Setup(int def = 8, int max = 8)
    {
        var host = new FakeHostConnection();
        var model = new DownloadsModel(host, a => a(), TimeSpan.Zero);
        model.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        host.Push(IpcMessage.Create(MessageTypes.JobsSnapshot, new SnapshotDto { Settings = new SettingsDto { DefaultConnections = def, MaxConnectionsPerHost = max, DefaultDownloadDirectory = "/storage/emulated/0/Download" } }));
        return (host, new NewDownloadModel(model));
    }

    [Fact]
    public void Starts_with_the_hosts_folder_and_default_connections_within_the_maximum()
    {
        var (_, m) = Setup(def: 16, max: 6);
        Assert.Equal("/storage/emulated/0/Download", m.SaveTo);
        Assert.Equal(6, m.Connections);
        Assert.Equal(6, m.MaxConnections);
        Assert.False(m.CanDownload);
    }

    [Theory]
    [InlineData("ftp://x.test/a.zip")]
    [InlineData("magnet:?xt=urn:btih:abc")]
    [InlineData("not a link")]
    [InlineData("https://x.test/file[01-0")]
    public void An_address_that_cannot_be_downloaded_here_is_refused_at_once(string text)
    {
        var (_, m) = Setup();
        m.Address = text;
        Assert.NotNull(m.AddressError);
        Assert.False(m.CanDownload);
        Assert.Empty(m.Urls);
    }

    [Fact]
    public void A_range_and_several_lines_expand_and_hide_the_file_name()
    {
        var (_, m) = Setup();
        m.Address = "https://x.test/file[01-03].zip";
        Assert.Equal(3, m.Urls.Count);
        Assert.True(m.IsMany);
        Assert.False(m.ShowFileName);
        m.Address = "https://x.test/a.zip\nhttps://x.test/b.zip";
        Assert.Equal(2, m.Urls.Count);
        Assert.True(m.CanDownload);
    }

    [Fact]
    public async Task The_check_fills_in_the_name_and_size_but_never_over_the_users_own_name()
    {
        var (host, m) = Setup();
        host.Reply = req => IpcMessage.Create(MessageTypes.FileMetadataResult, new FileMetadataResult { Ok = true, FileName = "ubuntu.iso", TotalBytes = 6_100_000_000 }, req.Id);
        m.Address = "https://x.test/dl?id=1";
        await m.CheckAsync();
        Assert.Equal(CheckState.Ready, m.Check);
        Assert.Equal("6.1 GB", m.CheckText);
        Assert.Equal("ubuntu.iso", m.FileName);
        m.FileName = "mine.iso";
        m.Address = "https://x.test/dl?id=2";
        await m.CheckAsync();
        Assert.Equal("mine.iso", m.FileName);
    }

    [Theory]
    [InlineData("net_http_client_execution_error", "The address could not be reached.")]
    [InlineData(null, "The address could not be reached.")]
    [InlineData("Server answered 404", "Server answered 404")]
    public void A_resource_key_is_not_shown_as_the_reason(string? raw, string shown) => Assert.Equal(shown, NewDownloadModel.Readable(raw));

    [Fact]
    public async Task A_failed_check_says_why_and_still_allows_the_add_but_a_refused_one_does_not()
    {
        var (host, m) = Setup();
        host.Reply = req => IpcMessage.Create(MessageTypes.FileMetadataResult, new FileMetadataResult { Ok = false, Error = "Server answered 404" }, req.Id);
        m.Address = "https://x.test/a.zip";
        await m.CheckAsync();
        Assert.Equal(CheckState.Failed, m.Check);
        Assert.Equal("Server answered 404", m.CheckText);
        Assert.True(m.CanDownload);
        host.Reply = req => IpcMessage.Create(MessageTypes.FileMetadataResult, new FileMetadataResult { Ok = false, AddRefused = true, Error = "Local addresses are refused" }, req.Id);
        await m.CheckAsync();
        Assert.Equal(CheckState.Refused, m.Check);
        Assert.False(m.CanDownload);
    }

    [Fact]
    public async Task A_check_for_an_address_that_was_changed_meanwhile_is_dropped()
    {
        var (host, m) = Setup();
        host.Reply = req =>
        {
            m.Address = "https://x.test/new.bin"; // typed while the Host is still answering
            return IpcMessage.Create(MessageTypes.FileMetadataResult, new FileMetadataResult { Ok = true, FileName = "old.bin", TotalBytes = 5 }, req.Id);
        };
        m.Address = "https://x.test/old.bin";
        await m.CheckAsync();
        Assert.Equal(CheckState.Idle, m.Check);
        Assert.Equal("", m.FileName);
    }

    [Fact]
    public void The_request_carries_every_choice()
    {
        var (_, m) = Setup();
        m.Address = "https://x.test/a.zip";
        m.FileName = " renamed.zip ";
        m.StartLater = true;
        m.Connections = 3;
        m.Referrer = "https://x.test/page";
        m.Description = "note";
        m.Sha256 = new string('a', 64);
        m.IfExists = ConflictChoice.Overwrite;
        var r = Assert.Single(m.BuildRequests());
        Assert.Equal("https://x.test/a.zip", r.Url);
        Assert.Equal("renamed.zip", r.FileName);
        Assert.Equal(StartModes.Later, r.StartMode);
        Assert.Equal(3, r.Connections);
        Assert.Equal("https://x.test/page", r.Referrer);
        Assert.Equal("note", r.Description);
        Assert.Equal(new string('a', 64), r.ExpectedSha256);
        Assert.Equal("overwrite", r.ConflictPolicy);
    }

    [Fact]
    public void A_bad_checksum_blocks_the_download()
    {
        var (_, m) = Setup();
        m.Address = "https://x.test/a.zip";
        m.Sha256 = "abc";
        Assert.NotNull(m.ChecksumError);
        Assert.False(m.CanDownload);
        m.Sha256 = "";
        Assert.True(m.CanDownload);
    }

    [Fact]
    public async Task Submit_adds_each_address_and_stops_at_a_refusal()
    {
        var (host, m) = Setup();
        var n = 0;
        host.Reply = req => IpcMessage.Create(MessageTypes.JobAddResult,
            ++n == 2 ? new AddJobResponse { Accepted = false, Error = "No room" } : new AddJobResponse { Accepted = true }, req.Id);
        m.Address = "https://x.test/f[1-3].bin";
        var r = await m.SubmitAsync();
        Assert.Equal(1, r.Added);
        Assert.Equal("No room", r.Error);
        Assert.Equal(2, host.Sent.Count);
    }

    [Fact]
    public async Task Submit_counts_duplicates_and_describes_the_outcome()
    {
        var (host, m) = Setup();
        host.Reply = req => IpcMessage.Create(MessageTypes.JobAddResult, new AddJobResponse { Accepted = true, Duplicate = true }, req.Id);
        m.Address = "https://x.test/a.zip";
        var r = await m.SubmitAsync();
        Assert.Equal("Already in your downloads", NewDownloadModel.Describe(r));
        Assert.Equal("Download added", NewDownloadModel.Describe(new(1, 0, null)));
        Assert.Equal("Added 3 downloads (1 already there)", NewDownloadModel.Describe(new(3, 1, null)));
    }
}

public sealed class DetailsInfoTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public void An_active_download_shows_percent_rate_and_time_left()
    {
        var row = Jobs.Row(Jobs.Make("downloading", "a.iso", 6_100_000_000, 3_500_000_000, 10_900_000));
        Assert.Equal("57%", DetailsInfo.Percent(row));
        Assert.Equal("10.9 MB/s", DetailsInfo.RateLine(row));
        Assert.Equal("3.5 GB of 6.1 GB · 4 min left", DetailsInfo.ProgressLine(row, Now));
    }

    [Fact]
    public void Finished_failed_and_paused_downloads_say_so()
    {
        Assert.Equal("100%", DetailsInfo.Percent(Jobs.Row(Jobs.Make("completed", total: 5, received: 5))));
        Assert.Equal("Finished", DetailsInfo.RateLine(Jobs.Row(Jobs.Make("completed", total: 5, received: 5))));
        Assert.Equal("Cancelled", DetailsInfo.RateLine(Jobs.Row(Jobs.Make("cancelled"))));
        Assert.Equal("Server answered 404", DetailsInfo.ProgressLine(Jobs.Row(Jobs.Make("failed", note: "Server answered 404")), Now));
        Assert.Equal("Paused", DetailsInfo.RateLine(Jobs.Row(Jobs.Make("paused", total: 10, received: 3))));
        Assert.Equal("", DetailsInfo.Percent(Jobs.Row(Jobs.Make("downloading", total: null, received: 3))));
    }

    [Fact]
    public void The_list_names_the_site_the_folder_the_queue_the_time_and_the_network_rule()
    {
        var j = Jobs.Make("downloading", "a.iso");
        j.Url = "https://releases.example.org/26.10/a.iso?token=secret";
        j.DestinationDirectory = "/storage/emulated/0/Download/";
        j.QueueId = 2;
        j.CreatedAt = Now.AddMinutes(-30);
        j.Source = "chrome";
        var settings = new SettingsDto { DownloadOnlyOnUnmeteredNetwork = true, Queues = [new QueueDto { Id = 2, Name = "Night" }] };
        var lines = DetailsInfo.Lines(j, settings, Now);
        Assert.Equal("releases.example.org", lines.Single(l => l.Label == "From").Value);
        Assert.DoesNotContain("secret", string.Join(" ", lines.Select(l => l.Value)));
        Assert.Equal("Download", lines.Single(l => l.Label == "Saved to").Value);
        Assert.Equal("Night", lines.Single(l => l.Label == "Queue").Value);
        Assert.Equal("Wi-Fi only", lines.Single(l => l.Label == "Network").Value);
        Assert.EndsWith(", from Chrome", lines.Single(l => l.Label == "Added").Value);
    }

    [Fact]
    public void Lines_without_a_queue_name_or_network_rule_are_left_out()
    {
        var lines = DetailsInfo.Lines(Jobs.Make("queued"), new SettingsDto(), Now);
        Assert.Equal(["From", "Saved to", "Added"], lines.Select(l => l.Label));
    }

    [Fact]
    public void Connections_and_their_pieces()
    {
        var j = Jobs.Make("downloading", total: 10, received: 1);
        j.ActiveConnections = 8; j.RequestedConnections = 8;
        Assert.Equal("8 connections", DetailsInfo.ConnectionsTitle(Jobs.Row(j)));
        j.ActiveConnections = 0; j.RequestedConnections = 1;
        Assert.Equal("1 connection", DetailsInfo.ConnectionsTitle(Jobs.Row(j)));
        Assert.Equal([1f, 0.5f, 0f], DetailsInfo.SegmentFractions([new SegmentDto { IsComplete = true }, new SegmentDto { Fraction = 0.5 }, new SegmentDto { Fraction = -3 }]));
    }

    [Fact]
    public void Folder_names_are_the_last_part_of_a_path()
    {
        Assert.Equal("Download", DetailsInfo.FolderName("/storage/emulated/0/Download"));
        Assert.Equal("Download", DetailsInfo.FolderName("/storage/emulated/0/Download/"));
        Assert.Equal("x", DetailsInfo.FolderName("x"));
        Assert.Equal("", DetailsInfo.FolderName(null));
    }
}

public sealed class AppScreenLogicTests
{
    private sealed class Store : IKeyValueStore
    {
        public Dictionary<string, string> Values { get; } = new();

        public string? GetString(string key) => Values.GetValueOrDefault(key);

        public void SetString(string key, string value) => Values[key] = value;
    }

    [Fact]
    public void Appearance_defaults_to_the_system_theme_and_dynamic_colour_off()
    {
        var a = new Appearance(new Store());
        Assert.Equal(ThemeChoice.System, a.Theme);
        Assert.False(a.DynamicColour);
        Assert.Equal("Off: Inbound blue", a.DynamicText(true));
        Assert.Equal("Needs Android 12 or newer", a.DynamicText(false));
        Assert.Equal("Follows the system", a.ThemeText);
    }

    [Fact]
    public void Appearance_keeps_what_is_set_and_ignores_a_damaged_value()
    {
        var s = new Store();
        var a = new Appearance(s);
        a.Theme = ThemeChoice.Dark;
        a.DynamicColour = true;
        Assert.Equal(ThemeChoice.Dark, new Appearance(s).Theme);
        Assert.True(new Appearance(s).DynamicColour);
        Assert.Equal("Dark", a.ThemeText);
        s.Values["theme"] = "99";
        Assert.Equal(ThemeChoice.System, a.Theme);
        s.Values["theme"] = "nonsense";
        Assert.Equal(ThemeChoice.System, a.Theme);
    }

    [Theory]
    [InlineData(true, true, true, true, BackStep.ClosePage)]
    [InlineData(false, true, true, true, BackStep.EndSelection)]
    [InlineData(false, false, true, true, BackStep.CloseSearch)]
    [InlineData(false, false, false, true, BackStep.BackToDownloads)]
    [InlineData(false, false, false, false, BackStep.System)]
    public void Back_undoes_the_innermost_thing_first_and_leaves_the_app_to_the_system(bool page, bool selecting, bool searching, bool settings, BackStep expected) =>
        Assert.Equal(expected, BackPlan.Next(page, selecting, searching, settings));

    [Fact]
    public void First_run_shows_once()
    {
        Assert.True(FirstRunState.ShouldShow(false));
        Assert.False(FirstRunState.ShouldShow(true));
        var s = new FirstRunState("/storage/emulated/0/Download", Permission.NotGranted, false);
        Assert.Equal("Download", s.FolderLine);
        Assert.False(s.NotificationsOn);
        Assert.False(s.BatteryUnrestricted);
    }

    [Theory]
    [InlineData("https://releases.example.org/26.10/ubuntu.iso", "https://releases.example.org/26.10/ubuntu.iso")]
    [InlineData("  http://x.test/a.zip\nhttp://x.test/b.zip", "http://x.test/a.zip")]
    [InlineData("ftp://x.test/a", null)]
    [InlineData("hello", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void The_clipboard_offers_a_web_link_only(string? text, string? expected) => Assert.Equal(expected, ClipboardOffer.Find(text));

    [Fact]
    public void A_long_clipboard_link_is_shortened_to_its_site_and_file()
    {
        Assert.Equal("releases.example.org/.../ubuntu-26.iso", ClipboardOffer.Shorten("https://releases.example.org/26.10/very/long/path/ubuntu-26.iso"));
        Assert.Equal("x.test/a.zip", ClipboardOffer.Shorten("https://x.test/a.zip"));
        Assert.Equal(38, ClipboardOffer.Shorten("https://" + new string('a', 80)).Length);
    }
}

public sealed class FileKindTests
{
    [Theory]
    [InlineData("ubuntu-26.10-desktop-amd64.iso", FileKind.Disc)]
    [InlineData("dataset.tar.zst", FileKind.Archive)]
    [InlineData("TALK.MP4", FileKind.Video)]
    [InlineData("a.flac", FileKind.Audio)]
    [InlineData("a.PNG", FileKind.Image)]
    [InlineData("invoice.pdf", FileKind.Document)]
    [InlineData("noextension", FileKind.Other)]
    [InlineData(null, FileKind.Other)]
    public void The_extension_decides_the_icon(string? name, FileKind kind) => Assert.Equal(kind, FileKinds.Of(name));
}
