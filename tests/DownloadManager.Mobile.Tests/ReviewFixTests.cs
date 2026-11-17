using DownloadManager.Contracts;
using DownloadManager.Mobile.Remote;

namespace DownloadManager.Mobile.Tests;

/// <summary>The fixes of the Opus milestone review of Android steps 3 and 6 that live in Mobile (findings by number).</summary>
public sealed class ReviewFixTests
{
    // ---- New download (findings 9, 12) ----

    private static (FakeHostConnection Host, NewDownloadModel Model) NewDownload()
    {
        var host = new FakeHostConnection();
        var model = new DownloadsModel(host, a => a(), TimeSpan.Zero);
        model.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        host.Push(IpcMessage.Create(MessageTypes.JobsSnapshot, new SnapshotDto { Settings = new SettingsDto { DefaultConnections = 4, MaxConnectionsPerHost = 8, DefaultDownloadDirectory = "/dl" } }));
        host.Reply = req =>
        {
            var url = req.GetPayload<FileMetadataRequest>().Url;
            return IpcMessage.Create(MessageTypes.FileMetadataResult, new FileMetadataResult { Ok = true, FileName = url[(url.LastIndexOf('/') + 1)..], TotalBytes = 10 }, req.Id);
        };
        return (host, new NewDownloadModel(model));
    }

    [Fact]
    public async Task A_found_name_goes_when_the_address_changes_so_another_address_is_never_added_under_it()
    {
        var (_, m) = NewDownload();
        m.Address = "https://a.example/x.iso";
        await m.CheckAsync();
        Assert.Equal("x.iso", m.FileName);

        m.Address = "https://b.example/y.zip"; // Download tapped before the new check
        Assert.Equal("", m.FileName);
        Assert.False(m.FileNameEdited);
        Assert.Null(Assert.Single(m.BuildRequests()).FileName);

        await m.CheckAsync();
        Assert.Equal("y.zip", m.FileName);
    }

    [Fact]
    public async Task A_name_the_user_typed_stays_across_addresses_and_checks_and_clearing_it_gives_the_choice_back()
    {
        var (_, m) = NewDownload();
        m.Address = "https://a.example/x.iso";
        m.FileName = "mine.iso";
        await m.CheckAsync();
        m.Address = "https://b.example/y.zip";
        await m.CheckAsync();
        Assert.Equal("mine.iso", m.FileName);
        Assert.True(m.FileNameEdited);

        m.FileName = "";
        Assert.False(m.FileNameEdited);
        await m.CheckAsync();
        Assert.Equal("y.zip", m.FileName);
    }

    [Fact]
    public async Task A_check_that_ends_after_the_address_changed_on_another_thread_leaves_nothing_behind()
    {
        var (host, m) = NewDownload();
        var answer = new TaskCompletionSource();
        var inner = host.Reply!;
        host.Reply = req => { answer.Task.Wait(); return inner(req); };
        m.Address = "https://a.example/x.iso";
        var check = Task.Run(() => m.CheckAsync());
        await Until.TrueAsync(() => m.Check == CheckState.Checking, "the check started");

        m.Address = "https://b.example/y.zip"; // typed while the Host still answers
        answer.SetResult();
        await check;

        Assert.Equal(CheckState.Idle, m.Check);
        Assert.Null(m.CheckText);
        Assert.Equal("", m.FileName);
    }

    // ---- Selection (findings 10, 14) ----

    private static (FakeHostConnection Host, DownloadList List) List(params JobDto[] jobs)
    {
        var host = new FakeHostConnection();
        var model = new DownloadsModel(host, a => a(), TimeSpan.Zero);
        model.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        host.Snapshot("/dl", jobs);
        return (host, new DownloadList(model, a => a()));
    }

    [Fact]
    public void Changing_the_chip_drops_selected_rows_it_hides_so_a_delete_never_reaches_them()
    {
        var done = Jobs.Make("completed", "done.iso", 5, 5, minute: 1);
        var running = Jobs.Make("downloading", "run.bin", 1000, 10, 100, minute: 2);
        var (_, list) = List(done, running);
        var raised = 0;
        list.SelectionChanged += () => raised++;
        list.StartSelecting(list.Visible.First(r => r.Id == done.Id));

        list.Filter = DownloadFilter.Active;
        list.SelectAll();

        Assert.Equal([running.Id], list.SelectedRows.Select(r => r.Id));
        Assert.Equal(1, list.SelectedCount);
        Assert.True(raised >= 3);
    }

    [Fact]
    public void A_selected_row_that_leaves_the_chip_leaves_the_selection()
    {
        var running = Jobs.Make("downloading", "run.bin", 1000, 10, 100, minute: 1);
        var (host, list) = List(running, Jobs.Make("downloading", "other.bin", 1000, 10, 100, minute: 2));
        list.Filter = DownloadFilter.Active;
        list.StartSelecting(list.Visible.First(r => r.Id == running.Id));
        var raised = 0;
        list.SelectionChanged += () => raised++;

        var finished = Jobs.Make("completed", "run.bin", 1000, 1000, minute: 1, id: running.Id);
        host.Event(finished);

        Assert.False(list.Selecting);
        Assert.Empty(list.SelectedRows);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void A_selected_row_that_changes_state_tells_the_bar_to_offer_other_buttons()
    {
        var running = Jobs.Make("downloading", "run.bin", 1000, 10, 100, minute: 1);
        var (host, list) = List(running);
        list.StartSelecting(list.Visible[0]);
        Assert.True(list.CanPauseSelection);
        var changed = 0;
        list.SelectedRowsChanged += () => changed++;

        host.Event(Jobs.Make("paused", "run.bin", 1000, 10, minute: 1, id: running.Id));

        Assert.True(changed >= 1);
        Assert.True(list.Selecting);
        Assert.False(list.CanPauseSelection);
        Assert.True(list.CanResumeSelection);
    }

    // ---- Settings follow the Host (findings 2, 13) ----

    [Fact]
    public async Task A_settings_change_after_the_snapshot_reaches_the_model_and_its_listeners()
    {
        var host = new FakeHostConnection();
        using var model = new DownloadsModel(host, a => a(), TimeSpan.Zero);
        await model.StartAsync(CancellationToken.None);
        var raised = 0;
        model.SettingsChanged += () => raised++;
        host.Snapshot("/old");
        Assert.Equal(1, raised);

        host.Push(IpcMessage.Create(MessageTypes.SettingsChanged, new SettingsDto { DefaultDownloadDirectory = "/new", MaxConnectionsPerHost = 3 }));

        Assert.Equal(2, raised);
        Assert.Equal("/new", model.DefaultDownloadDirectory);
        Assert.Equal(3, new NewDownloadModel(model).MaxConnections);
    }

    [Fact]
    public async Task After_a_reconnect_the_servers_settings_reach_the_screens()
    {
        var server = new FakeServer().Accepts();
        await using var connection = server.Connection();
        using var model = new DownloadsModel(connection, act => { lock (connection) act(); }, TimeSpan.Zero);
        await model.StartAsync(CancellationToken.None);
        await Until.TrueAsync(() => { lock (connection) return model.DefaultDownloadDirectory == "/srv/dl"; }, "the first snapshot's folder");

        server.Accepts(s =>
        {
            s.Hello();
            s.Push(IpcMessage.Create(MessageTypes.JobsSnapshot, new SnapshotDto { Settings = new SettingsDto { DefaultDownloadDirectory = "/srv/elsewhere" } }));
        });
        (await server.SocketAsync(1)).Close();

        await Until.TrueAsync(() => { lock (connection) return model.DefaultDownloadDirectory == "/srv/elsewhere"; }, "the folder set while the phone was away");
    }

    // ---- A dead socket (findings 0, 1) ----

    private static RemoteHostConnection Quick(FakeServer server, TimeSpan replyTimeout) =>
        new(FakeServer.Target(), server.ConnectAsync, server.SendOnceAsync, new ReconnectBackoff(TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(80)),
            replyTimeout: replyTimeout);

    [Fact]
    public async Task A_command_that_gets_no_reply_ends_that_session_and_the_connection_opens_again()
    {
        var server = new FakeServer().Accepts().Accepts();
        await using var connection = Quick(server, TimeSpan.FromMilliseconds(200));
        await connection.OpenAsync(_ => { }, CancellationToken.None);
        var first = await server.SocketAsync(1);

        var error = await Assert.ThrowsAsync<RemoteServerException>(() =>
            connection.RequestAsync(IpcMessage.Create(MessageTypes.JobPause, new JobActionRequest { JobId = Guid.NewGuid() }), CancellationToken.None));

        Assert.Contains("Home server", error.Message);
        await server.SocketAsync(2);
        Assert.True(first.Disposed);
        await Until.TrueAsync(() => connection.State.Status == RemoteConnectionStatus.Connected, "connected again");
    }

    [Fact]
    public async Task A_caller_that_stops_waiting_does_not_take_the_connection_down()
    {
        var server = new FakeServer().Accepts();
        await using var connection = Quick(server, TimeSpan.FromSeconds(30));
        await connection.OpenAsync(_ => { }, CancellationToken.None);
        var socket = await server.SocketAsync(1);
        using var cancel = new CancellationTokenSource();

        var pending = connection.RequestAsync(IpcMessage.Create(MessageTypes.JobPause, new JobActionRequest { JobId = Guid.NewGuid() }), cancel.Token);
        await Until.TrueAsync(() => { lock (socket.Sent) return socket.Sent.Count == 1; }, "the request was sent");
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.False(socket.Disposed);
        Assert.Equal(RemoteConnectionStatus.Connected, connection.State.Status);
        Assert.Equal(1, server.Attempts);
    }

    // ---- Pairing the shown server again (findings 3, 4, 7) ----

    [Fact]
    public async Task Pairing_the_shown_server_again_changes_its_connection_stamp_and_forgets_the_old_token_with_the_new_certificate()
    {
        var pins = new List<string?>();
        var unpaired = new List<ServerTarget>();
        var tokens = new Queue<string>(["ibt_first-test-token", "ibt_second-test-token"]);
        var secrets = new MemorySecrets();
        var hosts = new HostDirectory(new MemoryHostList(), secrets, "phone",
            pair: (_, _, _, _, pin, _) => { pins.Add(pin); return Task.FromResult(new RemotePairResult { Token = tokens.Dequeue(), TokenId = "t" + pins.Count }); },
            unpair: (target, _) => { lock (unpaired) unpaired.Add(target); return Task.FromResult(true); });
        var oldPin = new string('A', 64);
        var newPin = new string('B', 64);

        var first = await hosts.PairAsync(new PairRequest("https://homeserver:8443", "pw", "Home", false, TrustedFingerprint: oldPin));
        Assert.True(hosts.Select(first.Host!.Id));
        var shown = hosts.Current.ConnectionStamp;
        var changed = 0;
        hosts.Changed += () => changed++;
        Assert.True(hosts.Select(first.Host.Id));
        Assert.Equal(0, changed); // selecting the shown host again changes nothing: only the stamp tells a re-pairing

        var again = await hosts.PairAsync(new PairRequest("https://homeserver:8443", "pw", null, false, TrustedFingerprint: newPin));

        Assert.Equal(first.Host.Id, again.Host!.Id);
        Assert.Equal(1, changed);
        Assert.NotEqual(shown, hosts.Current.ConnectionStamp);
        Assert.Equal(ServerCertificates.NormalizePin(newPin), hosts.TargetFor(hosts.Current)!.CertificatePin);
        await Until.TrueAsync(() => { lock (unpaired) return unpaired.Count == 1; }, "the old token was forgotten");
        Assert.Equal("ibt_first-test-token", unpaired[0].Token);
        Assert.Equal(ServerCertificates.NormalizePin(newPin), unpaired[0].CertificatePin);
    }
}
