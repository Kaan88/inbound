using System.Collections.Specialized;
using DownloadManager.Contracts;
using DownloadManager.Mobile;

namespace DownloadManager.Mobile.Tests;

public sealed class DownloadsModelTests
{
    private static (DownloadsModel Model, FakeHostConnection Host, List<Action> Posted) Make(bool inlinePost = true)
    {
        var host = new FakeHostConnection();
        var posted = new List<Action>();
        var model = new DownloadsModel(host, a => { if (inlinePost) a(); else posted.Add(a); }, TimeSpan.Zero);
        return (model, host, posted);
    }

    [Fact]
    public async Task Start_opens_the_session_once()
    {
        var (model, host, _) = Make();
        await model.StartAsync(CancellationToken.None);
        Assert.Equal(1, host.Opened);
        await Assert.ThrowsAsync<InvalidOperationException>(() => model.StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Snapshot_fills_the_rows_newest_first_and_raises_the_signal_once()
    {
        var (model, host, _) = Make();
        var shown = 0;
        model.FirstSnapshotShown += () => shown++;
        await model.StartAsync(CancellationToken.None);
        host.Snapshot("/storage/emulated/0/Download",
            Jobs.Make("completed", "old.bin", minute: 1), Jobs.Make("queued", "new.bin", minute: 9), Jobs.Make("paused", "mid.bin", minute: 5));
        Assert.Equal(["new.bin", "mid.bin", "old.bin"], model.Rows.Select(r => r.Name));
        Assert.Equal(1, shown);
        Assert.Equal("/storage/emulated/0/Download", model.DefaultDownloadDirectory);
        host.Snapshot("/other");   // a second snapshot is ignored: the list is the first one plus events
        Assert.Equal(3, model.Rows.Count);
        Assert.Equal(1, shown);
    }

    [Fact]
    public async Task An_empty_snapshot_still_signals()
    {
        var (model, host, _) = Make();
        var shown = false;
        model.FirstSnapshotShown += () => shown = true;
        await model.StartAsync(CancellationToken.None);
        host.Snapshot();
        Assert.True(shown);
        Assert.Empty(model.Rows);
    }

    [Fact]
    public async Task The_snapshot_is_applied_on_the_ui_thread_only()
    {
        var (model, host, posted) = Make(inlinePost: false);
        await model.StartAsync(CancellationToken.None);
        host.Snapshot("/dl", Jobs.Make("queued"));
        Assert.Empty(model.Rows);
        Assert.Single(posted);
        posted[0]();
        Assert.Single(model.Rows);
    }

    [Fact]
    public async Task Events_before_the_snapshot_are_dropped()
    {
        var (model, host, _) = Make();
        await model.StartAsync(CancellationToken.None);
        host.Event(Jobs.Make("downloading"), "added");
        host.Snapshot();
        Assert.Empty(model.Rows);
    }

    [Fact]
    public async Task Added_job_is_inserted_on_top()
    {
        var (model, host, _) = Make();
        await model.StartAsync(CancellationToken.None);
        host.Snapshot("/dl", Jobs.Make("completed", "first.bin", minute: 1));
        var added = new List<int>();
        model.Rows.CollectionChanged += (_, e) => { if (e.Action == NotifyCollectionChangedAction.Add) added.Add(e.NewStartingIndex); };
        host.Event(Jobs.Make("queued", "second.bin", minute: 2), "added");
        Assert.Equal(["second.bin", "first.bin"], model.Rows.Select(r => r.Name));
        Assert.Equal([0], added);
    }

    [Fact]
    public async Task Jobs_added_in_one_burst_end_up_newest_first_whatever_the_event_order()
    {
        var (model, host, _) = Make();
        await model.StartAsync(CancellationToken.None);
        host.Snapshot("/dl", Jobs.Make("completed", "old.bin", minute: 1));
        host.Event(Jobs.Make("queued", "c.bin", minute: 4), "added");
        host.Event(Jobs.Make("queued", "b.bin", minute: 3), "added");
        host.Event(Jobs.Make("queued", "d.bin", minute: 5), "added");
        Assert.Equal(["d.bin", "c.bin", "b.bin", "old.bin"], model.Rows.Select(r => r.Name));
    }

    [Fact]
    public async Task Update_changes_the_row_in_place_and_does_not_move_it()
    {
        var (model, host, _) = Make();
        await model.StartAsync(CancellationToken.None);
        var a = Jobs.Make("queued", "a.bin", minute: 1);
        var b = Jobs.Make("queued", "b.bin", minute: 2);
        host.Snapshot("/dl", a, b);
        var rowA = model.Rows[1];
        var moves = 0;
        model.Rows.CollectionChanged += (_, _) => moves++;
        a.State = "downloading"; a.TotalBytes = 1000; a.ReceivedBytes = 500; a.BytesPerSecond = 100;
        host.Event(a, "updated");
        Assert.Same(rowA, model.Rows[1]);
        Assert.Equal(StatusKind.Active, rowA.Kind);
        Assert.Equal(0.5, rowA.Progress, 3);
        Assert.Equal(0, moves);
    }

    [Fact]
    public async Task Update_for_an_unknown_job_adds_it()
    {
        var (model, host, _) = Make();
        await model.StartAsync(CancellationToken.None);
        host.Snapshot();
        host.Event(Jobs.Make("downloading", "x.bin", total: 10, received: 1), "updated");
        Assert.Single(model.Rows);
    }

    [Theory]
    [InlineData("removed")]
    [InlineData("deleted")]
    [InlineData("Removed")]
    public async Task Removed_job_leaves_the_list(string eventType)
    {
        var (model, host, _) = Make();
        await model.StartAsync(CancellationToken.None);
        var a = Jobs.Make("completed", "a.bin", minute: 1);
        var b = Jobs.Make("completed", "b.bin", minute: 2);
        host.Snapshot("/dl", a, b);
        host.Event(a, eventType);
        Assert.Equal(["b.bin"], model.Rows.Select(r => r.Name));
    }

    [Fact]
    public async Task Removing_a_job_that_is_not_listed_is_harmless()
    {
        var (model, host, _) = Make();
        await model.StartAsync(CancellationToken.None);
        host.Snapshot("/dl", Jobs.Make("queued"));
        host.Event(Jobs.Make("queued"), "removed");
        Assert.Single(model.Rows);
    }

    // The Host announces progress outside its lock, so a progress frame can arrive after the removal (review, 2026-11-17).
    // Job ids are never reused (every job gets Guid.NewGuid()), so any event after a removal is stale.
    [Fact]
    public async Task A_late_event_for_a_removed_job_does_not_bring_the_row_back()
    {
        var (model, host, _) = Make();
        await model.StartAsync(CancellationToken.None);
        var a = Jobs.Make("downloading", "a.bin", total: 100, received: 10);
        host.Snapshot("/dl", a);
        host.Event(a, "removed");
        a.ReceivedBytes = 20;
        host.Event(a, "progress");
        Assert.Empty(model.Rows);
    }

    [Fact]
    public async Task A_late_event_in_the_same_batch_does_not_undo_the_removal()
    {
        var host = new FakeHostConnection();
        var posted = new List<Action>();
        using var model = new DownloadsModel(host, a => { lock (posted) posted.Add(a); }, TimeSpan.FromMilliseconds(30));
        await model.StartAsync(CancellationToken.None);
        var a = Jobs.Make("downloading", "a.bin", total: 100, received: 10);
        var b = Jobs.Make("queued", "b.bin", minute: 1);
        host.Snapshot("/dl", a, b);
        posted[0]();
        posted.Clear();
        host.Event(a, "removed");
        a.ReceivedBytes = 20;
        host.Event(a, "progress");   // would overwrite the pending removal
        for (var i = 0; i < 100 && posted.Count == 0; i++) await Task.Delay(20);
        Assert.Single(posted);
        posted[0]();
        Assert.Equal(["b.bin"], model.Rows.Select(r => r.Name));
    }

    [Fact]
    public async Task A_batch_that_lands_after_the_removal_does_not_add_the_row_again()
    {
        // No interval: each event posts its own batch, and posts from two threads can land in either order.
        var (model, host, posted) = Make(inlinePost: false);
        await model.StartAsync(CancellationToken.None);
        var a = Jobs.Make("downloading", "a.bin", total: 100, received: 10);
        host.Snapshot("/dl", a);
        posted[0]();
        posted.Clear();
        host.Event(a, "progress");
        host.Event(a, "removed");
        Assert.Equal(2, posted.Count);
        posted[1]();   // the removal lands first
        posted[0]();   // then the older progress batch
        Assert.Empty(model.Rows);
    }

    // Fable B3 (2026-11-17): a progress frame mapped before "completed" but delivered after it must not undo it.
    [Fact]
    public async Task A_late_progress_frame_does_not_undo_a_newer_state()
    {
        var (model, host, _) = Make();
        await model.StartAsync(CancellationToken.None);
        var t = new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
        var a = Jobs.Make("downloading", "a.bin", total: 100, received: 50);
        a.UpdatedAt = t;
        host.Snapshot("/dl", a);
        host.Event(Copy(a, "completed", 100, t.AddMilliseconds(200)), "updated");
        host.Event(Copy(a, "downloading", 90, t.AddMilliseconds(100)), "progress");
        Assert.Equal(StatusKind.Done, model.Rows[0].Kind);
        host.Event(Copy(a, "downloading", 95, t.AddMilliseconds(300)), "progress");   // newer progress is applied
        Assert.Equal(0.95, model.Rows[0].Progress, 3);
    }

    [Fact]
    public async Task A_late_progress_frame_does_not_undo_a_newer_state_in_the_same_batch()
    {
        var host = new FakeHostConnection();
        var posted = new List<Action>();
        using var model = new DownloadsModel(host, a => { lock (posted) posted.Add(a); }, TimeSpan.FromMilliseconds(30));
        await model.StartAsync(CancellationToken.None);
        var t = new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
        var a = Jobs.Make("downloading", "a.bin", total: 100, received: 50);
        a.UpdatedAt = t;
        host.Snapshot("/dl", a);
        posted[0]();
        posted.Clear();
        host.Event(Copy(a, "paused", 80, t.AddMilliseconds(200)), "paused");
        host.Event(Copy(a, "downloading", 70, t.AddMilliseconds(100)), "progress");
        for (var i = 0; i < 100 && posted.Count == 0; i++) await Task.Delay(20);
        Assert.Single(posted);
        posted[0]();
        Assert.Equal(0.8, model.Rows[0].Progress, 3);
        Assert.NotEqual(StatusKind.Active, model.Rows[0].Kind);
    }

    [Fact]
    public async Task A_progress_frame_far_older_than_the_last_is_applied_so_a_clock_set_back_cannot_freeze_a_row()
    {
        var (model, host, _) = Make();
        await model.StartAsync(CancellationToken.None);
        var t = new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
        var a = Jobs.Make("downloading", "a.bin", total: 100, received: 10);
        a.UpdatedAt = t;
        host.Snapshot("/dl", a);
        host.Event(Copy(a, "downloading", 20, t.AddHours(-1)), "progress");
        Assert.Equal(0.2, model.Rows[0].Progress, 3);
    }

    // Fable B4: a job removed before this session's snapshot is in neither the list nor the removed set.
    [Fact]
    public async Task A_progress_frame_for_an_unknown_job_adds_no_row()
    {
        var (model, host, _) = Make();
        await model.StartAsync(CancellationToken.None);
        host.Snapshot();
        host.Event(Jobs.Make("downloading", "ghost.bin", total: 10, received: 5), "progress");
        Assert.Empty(model.Rows);
        host.Event(Jobs.Make("queued", "new.bin"), "added");
        Assert.Single(model.Rows);
    }

    private static JobDto Copy(JobDto j, string state, long received, DateTimeOffset at) => new()
    {
        Id = j.Id, Url = j.Url, FileName = j.FileName, State = state, TotalBytes = j.TotalBytes, ReceivedBytes = received,
        Progress = j.TotalBytes is > 0 ? (double)received / j.TotalBytes.Value : null, CreatedAt = j.CreatedAt, UpdatedAt = at,
    };

    [Fact]
    public async Task Frames_after_dispose_change_nothing()
    {
        var (model, host, _) = Make();
        var shown = 0;
        model.FirstSnapshotShown += () => shown++;
        await model.StartAsync(CancellationToken.None);
        model.Dispose();
        host.Snapshot("/dl", Jobs.Make("queued"));
        host.Event(Jobs.Make("queued", "late.bin"), "added");
        Assert.Empty(model.Rows);
        Assert.Equal(0, shown);
    }

    [Fact]
    public async Task A_snapshot_posted_before_dispose_and_run_after_it_changes_nothing()
    {
        var (model, host, posted) = Make(inlinePost: false);
        var shown = 0;
        model.FirstSnapshotShown += () => shown++;
        await model.StartAsync(CancellationToken.None);
        host.Snapshot("/dl", Jobs.Make("queued"));
        model.Dispose();
        posted[0]();
        Assert.Empty(model.Rows);
        Assert.Equal(0, shown);
    }

    [Fact]
    public async Task Unrelated_frames_are_ignored()
    {
        var (model, host, _) = Make();
        await model.StartAsync(CancellationToken.None);
        host.Push(IpcMessage.Create(MessageTypes.ServerHello));
        host.Snapshot("/dl", Jobs.Make("queued"));
        host.Push(IpcMessage.Create(MessageTypes.SettingsChanged));
        Assert.Single(model.Rows);
    }

    [Fact]
    public async Task Events_are_gathered_and_the_last_state_of_each_job_wins_when_an_interval_is_set()
    {
        var host = new FakeHostConnection();
        var posted = new List<Action>();
        using var model = new DownloadsModel(host, a => { lock (posted) posted.Add(a); }, TimeSpan.FromMilliseconds(30));
        await model.StartAsync(CancellationToken.None);
        var a = Jobs.Make("queued", "a.bin");
        host.Snapshot("/dl", a);
        posted[0]();
        posted.Clear();
        a.State = "downloading"; a.TotalBytes = 100; a.ReceivedBytes = 10;
        host.Event(a);
        a.ReceivedBytes = 90;
        host.Event(a);
        for (var i = 0; i < 100 && posted.Count == 0; i++) await Task.Delay(20);
        Assert.Single(posted);   // two events, one batch
        posted[0]();
        Assert.Equal(0.9, model.Rows[0].Progress, 3);
    }

    // ----- commands -----

    [Fact]
    public async Task Add_sends_job_add_with_the_address_and_the_defaults()
    {
        var (model, host, _) = Make();
        var result = await model.AddAsync("http://example.test/a.zip");
        var sent = Assert.Single(host.Sent);
        Assert.Equal(MessageTypes.JobAdd, sent.Type);
        var request = sent.GetPayload<AddJobRequest>();
        Assert.Equal("http://example.test/a.zip", request.Url);
        Assert.Null(request.DestinationDirectory);
        Assert.Null(request.FileName);
        Assert.Equal(StartModes.StartNow, request.StartMode);
        Assert.Equal("rename", request.ConflictPolicy);
        Assert.True(result.Accepted);
    }

    [Fact]
    public async Task Add_passes_destination_file_name_and_start_mode()
    {
        var (model, host, _) = Make();
        await model.AddAsync("http://e.test/x", "/storage/emulated/0/Download", "x.bin", StartModes.Later);
        var request = host.Sent[0].GetPayload<AddJobRequest>();
        Assert.Equal("/storage/emulated/0/Download", request.DestinationDirectory);
        Assert.Equal("x.bin", request.FileName);
        Assert.Equal(StartModes.Later, request.StartMode);
    }

    [Fact]
    public async Task Add_treats_blank_destination_and_name_as_not_given()
    {
        var (model, host, _) = Make();
        await model.AddAsync("http://e.test/x", "  ", "");
        var request = host.Sent[0].GetPayload<AddJobRequest>();
        Assert.Null(request.DestinationDirectory);
        Assert.Null(request.FileName);
    }

    [Fact]
    public async Task Add_returns_the_hosts_refusal_instead_of_throwing()
    {
        var (model, host, _) = Make();
        host.Reply = m => IpcMessage.Create(MessageTypes.JobAddResult, new AddJobResponse { Accepted = false, Error = "Not an address", ErrorCode = "badUrl" }, m.Id);
        var result = await model.AddAsync("nonsense");
        Assert.False(result.Accepted);
        Assert.Equal("Not an address", result.Error);
    }

    [Fact]
    public async Task Add_reports_an_unexpected_reply_as_a_refusal()
    {
        var (model, host, _) = Make();
        host.Reply = m => IpcMessage.Create(MessageTypes.SettingsResult, new SettingsResult(), m.Id);
        var result = await model.AddAsync("http://e.test/x");
        Assert.False(result.Accepted);
        Assert.Contains(MessageTypes.SettingsResult, result.Error);
    }

    [Fact]
    public async Task Add_sends_each_call_as_its_own_command()
    {
        var (model, host, _) = Make();
        await model.AddAsync("http://e.test/1");
        await model.AddAsync("http://e.test/2");
        Assert.Equal(2, host.Sent.Count);
        Assert.NotEqual(host.Sent[0].Id, host.Sent[1].Id);
    }
}
