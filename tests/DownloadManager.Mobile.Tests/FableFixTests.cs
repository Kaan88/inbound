using DownloadManager.Contracts;
using DownloadManager.Mobile;
using DownloadManager.Mobile.Remote;

namespace DownloadManager.Mobile.Tests;

/// <summary>Fable's review of the Android queue (2026-11-17, at 7573792b): the fixes that have logic outside Android.</summary>
public sealed class FableFixTests
{
    // ---- Finding 4: the first snapshot is applied as one batch ----

    [Fact]
    public async Task A_snapshot_of_3000_jobs_reaches_the_list_as_one_change_and_one_summary()
    {
        var host = new FakeHostConnection();
        var model = new DownloadsModel(host, a => a(), TimeSpan.Zero);
        await model.StartAsync(CancellationToken.None);
        // The screen's list exists before the snapshot, as on the phone.
        var list = new DownloadList(model, a => a());
        var rowChanges = 0;
        var summaries = 0;
        var visibleChanges = 0;
        model.Rows.CollectionChanged += (_, _) => rowChanges++;
        list.SummaryChanged += () => summaries++;
        list.Visible.CollectionChanged += (_, _) => visibleChanges++;
        var jobs = Enumerable.Range(0, 3000).Select(i => Jobs.Make(i % 3 == 0 ? "downloading" : "completed", $"f{i}.bin", 100, 10, 5, minute: i)).ToArray();

        host.Snapshot("/dl", jobs);

        Assert.Equal(1, rowChanges);
        Assert.Equal(1, summaries);
        Assert.Equal(3000, list.Visible.Count);
        Assert.Equal(3000, visibleChanges); // one insert per row into an empty list: linear, each at the end
        Assert.Equal("f2999.bin", list.Visible[0].Name); // newest first
        Assert.Equal("All 3000", list.ChipLabel(DownloadFilter.All));
        Assert.Equal(1000, list.Summary.Active);
    }

    // ---- Finding 3: a refused certificate is tried again ----

    private static RemoteHostConnection SlowBackoff(FakeServer server) =>
        new(FakeServer.Target(), server.ConnectAsync, server.SendOnceAsync, new ReconnectBackoff(TimeSpan.FromMilliseconds(20), TimeSpan.FromSeconds(30)));

    [Fact]
    public async Task A_refused_certificate_shows_stopped_but_a_network_change_tries_again()
    {
        var server = new FakeServer().Fails(new ServerCertificateRefusedException("Home server", pinned: true)).Accepts();
        await using var connection = SlowBackoff(server);
        await connection.OpenAsync(_ => { }, CancellationToken.None);

        Assert.Equal(RemoteConnectionStatus.Stopped, connection.State.Status);
        Assert.Contains("intercepts connections", connection.State.Message);
        Assert.Contains("compare the fingerprint", connection.State.Message);
        // Commands fail at once, not after the connect wait.
        await Assert.ThrowsAsync<RemoteServerException>(() => connection.RequestAsync(IpcMessage.Create(MessageTypes.HostPing, new { }), CancellationToken.None));
        await Task.Delay(150);
        Assert.Equal(1, server.Attempts); // waits for the longest backoff, not the short one

        connection.RetryNow(dropCurrent: true);

        await Until.TrueAsync(() => connection.State.Status == RemoteConnectionStatus.Connected, "connected after the network change");
        Assert.Equal(2, server.Attempts);
    }

    [Fact]
    public async Task A_refused_certificate_is_tried_again_when_the_screen_is_shown_again()
    {
        var server = new FakeServer().Fails(new ServerCertificateRefusedException("Home server")).Accepts();
        await using var connection = SlowBackoff(server);
        await connection.OpenAsync(_ => { }, CancellationToken.None);
        Assert.Equal(RemoteConnectionStatus.Stopped, connection.State.Status);

        connection.SetActive(false);
        await Until.TrueAsync(() => connection.State.Status == RemoteConnectionStatus.Paused, "paused");
        connection.SetActive(true);

        await Until.TrueAsync(() => connection.State.Status == RemoteConnectionStatus.Connected, "connected when shown again");
    }

    [Fact]
    public async Task A_refused_certificate_is_tried_again_at_the_longest_backoff_while_shown()
    {
        var server = new FakeServer().Fails(new ServerCertificateRefusedException("Home server")).Accepts();
        await using var connection = server.Connection(); // longest backoff 80 ms
        await connection.OpenAsync(_ => { }, CancellationToken.None);

        await Until.TrueAsync(() => connection.State.Status == RemoteConnectionStatus.Connected, "connected on the next try");
        Assert.Equal(2, server.Attempts);
    }

    // ---- Finding 16: slow replies ----

    private static RemoteHostConnection Quick(FakeServer server, TimeSpan replyTimeout) =>
        new(FakeServer.Target(), server.ConnectAsync, server.SendOnceAsync, new ReconnectBackoff(TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(80)),
            replyTimeout: replyTimeout);

    [Fact]
    public async Task A_slow_reply_from_a_server_that_keeps_sending_does_not_drop_the_session()
    {
        var server = new FakeServer().Accepts().Accepts();
        await using var connection = Quick(server, TimeSpan.FromMilliseconds(300));
        await connection.OpenAsync(_ => { }, CancellationToken.None);
        var socket = await server.SocketAsync(1);
        using var pushing = new CancellationTokenSource();
        var pusher = Task.Run(async () =>
        {
            var job = Jobs.Make("downloading", "a.bin", 100, 1, 1);
            while (!pushing.IsCancellationRequested)
            {
                socket.Event(job, "progress");
                try { await Task.Delay(40, pushing.Token); } catch (OperationCanceledException) { }
            }
        });

        await Assert.ThrowsAsync<RemoteServerException>(() =>
            connection.RequestAsync(IpcMessage.Create(MessageTypes.JobPause, new JobActionRequest { JobId = Guid.NewGuid() }), CancellationToken.None));
        pushing.Cancel();
        await pusher;

        Assert.False(socket.Disposed);
        Assert.Equal(1, server.Attempts);
        Assert.Equal(RemoteConnectionStatus.Connected, connection.State.Status);
    }

    [Fact]
    public async Task Removing_with_the_files_waits_longer_than_a_quick_command()
    {
        var server = new FakeServer().Accepts();
        await using var connection = Quick(server, TimeSpan.FromMilliseconds(150));
        await connection.OpenAsync(_ => { }, CancellationToken.None);
        var socket = await server.SocketAsync(1);
        var request = IpcMessage.Create(MessageTypes.JobRemove, new JobActionRequest { JobId = Guid.NewGuid(), DeleteFile = true });

        var pending = connection.RequestAsync(request, CancellationToken.None);
        await Task.Delay(400);
        socket.Push(IpcMessage.Create(MessageTypes.JobActionResult, new JobActionResponse { Ok = true }, request.Id));

        var reply = await pending;
        Assert.Equal(MessageTypes.JobActionResult, reply.Type);
        Assert.False(socket.Disposed);
    }

    // ---- Finding 13: a delete question asked before the download finished ----

    [Fact]
    public void A_delete_planned_before_the_download_finished_no_longer_fits_after()
    {
        var job = Jobs.Make("downloading", "movie.mkv", 1000, 500, 100);
        var row = Jobs.Row(job);
        var rows = new[] { row };
        var plan = DownloadList.PlanDelete(rows);
        var groups = DownloadList.Groups(rows);
        Assert.True(DownloadList.SamePlan(rows, plan, groups));

        job.State = "completed";
        job.ReceivedBytes = 1000;
        row.ApplyFrom(job);

        Assert.False(DownloadList.SamePlan(rows, plan, groups));
    }

    // ---- Finding 15: the resume list ----

    [Fact]
    public void Pause_all_remembers_what_it_is_about_to_pause_and_resume_takes_only_the_paused()
    {
        var running = Jobs.Make("downloading", "a.bin");
        var queued = Jobs.Make("queued", "b.bin");
        var retrying = Jobs.Make("retryWaiting", "c.bin");
        var paused = Jobs.Make("paused", "d.bin");
        var done = Jobs.Make("completed", "e.bin");
        var failedWithRetry = Jobs.Make("failed", "f.bin");
        failedWithRetry.QueueRetryAt = DateTimeOffset.UtcNow.AddMinutes(1);

        var candidates = ResumeList.PauseCandidates([running, queued, retrying, paused, done, failedWithRetry]);

        Assert.Equal([running.Id, queued.Id, retrying.Id, failedWithRetry.Id], candidates);
        var states = new Dictionary<Guid, string> { [running.Id] = "paused", [queued.Id] = "paused", [retrying.Id] = "completed", [failedWithRetry.Id] = "paused" };
        Assert.Equal([running.Id, queued.Id, failedWithRetry.Id], ResumeList.ToResume(candidates, id => states.GetValueOrDefault(id)));
        var later = Guid.NewGuid();
        Assert.Equal([later], ResumeList.Without([.. candidates, later], candidates));
    }

    // ---- Finding 9: what lifts a tripped crash guard ----

    [Fact]
    public void Only_a_user_command_that_starts_downloads_lifts_the_crash_guard()
    {
        foreach (var type in new[] { MessageTypes.JobAdd, MessageTypes.JobResume, MessageTypes.JobRetry, MessageTypes.JobRedownload, MessageTypes.JobsResumeAll })
            Assert.True(CrashGuard.IsUserStart(type), type);
        foreach (var type in new[] { MessageTypes.JobPause, MessageTypes.JobsPauseAll, MessageTypes.JobRemove, MessageTypes.JobsQuery, MessageTypes.JobEvent, MessageTypes.SettingsChanged })
            Assert.False(CrashGuard.IsUserStart(type), type);
    }

    // ---- Finding 12: the host list is written off the caller's thread ----

    [Fact]
    public async Task Seen_and_select_write_the_list_behind_and_the_newest_list_wins()
    {
        var list = new MemoryHostList();
        var hosts = new HostDirectory(list, new MemorySecrets(), "phone",
            pair: (_, _, _, _, _, _) => Task.FromResult(new RemotePairResult { Token = "ibt_test-token-not-real", TokenId = "t" }));
        var paired = (await hosts.PairAsync(new PairRequest("127.0.0.1:9000", "pw", null, false))).Host!;
        var savesAfterPairing = list.Saves;

        Assert.True(hosts.Select(paired.Id));
        hosts.Seen(paired.Id, HostKinds.Linux);
        Assert.True(hosts.Select(HostEntry.PhoneId));
        await hosts.SavedAsync();

        Assert.Equal(HostEntry.PhoneId, list.Saved.CurrentId);
        Assert.Equal(HostKinds.Linux, Assert.Single(list.Saved.Servers).HostKind);
        Assert.InRange(list.Saves - savesAfterPairing, 1, 3);
    }
}
