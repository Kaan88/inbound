using System.Text;
using System.Text.Json;
using DownloadManager.Contracts;
using DownloadManager.Mobile.Torrents;

namespace DownloadManager.Mobile.Tests;

public sealed class TorrentTests
{
    private const string Magnet = "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&dn=Debian";

    private static JobDto Torrent(string state, Action<JobDto, TorrentDto>? shape = null, long? total = 3_900_000_000, long received = 2_500_000_000)
    {
        var job = Jobs.Make(state, "debian.iso", total, received, 18_600_000);
        job.Torrent = new TorrentDto { InfoHash = "0123", Name = "debian.iso", HasMetadata = true, FileCount = 3, WantedCount = 3, Seeds = 48, Peers = 9, UploadRate = 1_200_000 };
        shape?.Invoke(job, job.Torrent);
        return job;
    }

    private static IpcMessage Reply(IpcMessage request, string type, object payload) => payload switch
    {
        TorrentInspectResult p => IpcMessage.Create(type, p, request.Id),
        TorrentAddResult p => IpcMessage.Create(type, p, request.Id),
        TorrentFilesResult p => IpcMessage.Create(type, p, request.Id),
        TorrentActionResult p => IpcMessage.Create(type, p, request.Id),
        TorrentDetailsResult p => IpcMessage.Create(type, p, request.Id),
        TorrentPeersResult p => IpcMessage.Create(type, p, request.Id),
        TorrentTrackersResult p => IpcMessage.Create(type, p, request.Id),
        TorrentPiecesResult p => IpcMessage.Create(type, p, request.Id),
        HostActionResult p => IpcMessage.Create(type, p, request.Id),
        _ => throw new ArgumentException("payload"),
    };

    private static List<TorrentFileDto> ThreeFiles() =>
    [
        new() { Index = 0, Path = "debian/debian.iso", Size = 3_900_000_000, Wanted = true },
        new() { Index = 1, Path = "debian/SHA256SUMS", Size = 1000, Wanted = true },
        new() { Index = 2, Path = "debian/SHA256SUMS.sign", Size = 1000, Wanted = true },
    ];

    // ---- Rows ----

    [Fact]
    public void A_downloading_torrent_says_size_both_rates_and_seeds()
    {
        var info = TorrentRowInfo.For(Torrent("downloading"), new SettingsDto())!;
        Assert.Equal("2.5 GB of 3.9 GB · ↓ 18.6 MB/s · ↑ 1.2 MB/s · 48 seeds", info.Subtitle);
        Assert.Equal(TorrentTone.Neutral, info.Tone);
        Assert.Null(TorrentRowInfo.For(Jobs.Make("downloading"), null));
    }

    [Fact]
    public void A_torrent_without_a_file_list_or_a_choice_says_so()
    {
        Assert.Equal("Getting file list · 3 peers", TorrentRowInfo.For(Torrent("connecting", (_, t) => { t.HasMetadata = false; t.Peers = 3; }), null)!.Subtitle);
        var choose = TorrentRowInfo.For(Torrent("paused", (_, t) => t.AwaitingFileChoice = true), null)!;
        Assert.Equal("Choose files to download", choose.Subtitle);
        Assert.Equal(TorrentTone.Amber, choose.Tone);
        Assert.Equal("Checking files", TorrentRowInfo.For(Torrent("verifying", (_, t) => t.Checking = true), null)!.Subtitle);
    }

    [Fact]
    public void A_seeding_torrent_is_green_with_its_upload_rate_and_ratio_goal()
    {
        var job = Torrent("completed", (_, t) => { t.Seeding = true; t.UploadRate = 640_000; t.Ratio = 0.71; });
        var info = TorrentRowInfo.For(job, new SettingsDto { TorrentSeedAfterDownload = true, TorrentSeedRatio = 1.0 })!;
        Assert.Equal("Seeding · ↑ 640 KB/s · ratio 0.71 of 1.0", info.Subtitle);
        Assert.Equal(TorrentTone.Done, info.Tone);
        // The torrent's own goal wins; without a ratio goal nothing is claimed.
        job.Torrent!.SeedGoal = "ratio"; job.Torrent.SeedGoalValue = 2.5;
        Assert.Contains("ratio 0.71 of 2.5", TorrentRowInfo.For(job, null)!.Subtitle);
        job.Torrent.SeedGoal = "hours"; job.Torrent.SeedGoalValue = 12;
        Assert.EndsWith("ratio 0.71", TorrentRowInfo.For(job, new SettingsDto())!.Subtitle);
        // Finished and not seeding is the usual "Finished" row.
        Assert.Null(TorrentRowInfo.For(Torrent("completed", (_, t) => t.Seeding = false), null));
    }

    [Fact]
    public void The_row_gets_a_Stop_button_while_it_seeds_and_none_while_it_waits_for_files()
    {
        var seeding = Jobs.Row(Torrent("completed", (_, t) => { t.Seeding = true; t.Ratio = 0.5; }));
        Assert.Equal(RowAction.StopSeeding, seeding.Action);
        Assert.Equal(StatusKind.Done, seeding.Kind);
        Assert.True(seeding.ShowBar);
        Assert.StartsWith("Seeding", seeding.Subtitle);
        Assert.Equal(RowGroup.Finished, seeding.Group);

        var waiting = Jobs.Row(Torrent("paused", (_, t) => t.AwaitingFileChoice = true));
        Assert.Equal(RowAction.None, waiting.Action);
        Assert.Equal("Choose files to download", waiting.Subtitle);

        var downloading = Jobs.Row(Torrent("downloading"));
        Assert.Equal(RowAction.Pause, downloading.Action);
        Assert.Contains("48 seeds", downloading.Subtitle);
    }

    [Fact]
    public async Task The_Torrents_chip_counts_torrent_rows_whatever_their_state()
    {
        var host = new FakeHostConnection();
        var model = new DownloadsModel(host, a => a(), TimeSpan.Zero);
        await model.StartAsync(CancellationToken.None);
        host.Snapshot("/dl", Torrent("downloading"), Torrent("completed", (_, t) => t.Seeding = true), Jobs.Make("downloading", "plain.bin", 10, 5, 1));
        var list = new DownloadList(model, a => a());
        Assert.Equal("Torrents 2", list.ChipLabel(DownloadFilter.Torrents));
        list.Filter = DownloadFilter.Torrents;
        Assert.Equal(2, list.Visible.Count);
        Assert.All(list.Visible, r => Assert.NotNull(r.Job.Torrent));
        // The chips run All, Active, Torrents, ..., Finished.
        var order = Enum.GetValues<DownloadFilter>().ToList();
        Assert.True(order.IndexOf(DownloadFilter.Active) < order.IndexOf(DownloadFilter.Torrents));
        Assert.True(order.IndexOf(DownloadFilter.Torrents) < order.IndexOf(DownloadFilter.Finished));
    }

    // ---- Words ----

    [Fact]
    public void Seed_goals_are_written_as_the_mockup_does()
    {
        Assert.Equal("seed until 1.0 or 48 h", TorrentText.SeedUntil("global", null, new SettingsDto { TorrentSeedAfterDownload = true, TorrentSeedRatio = 1, TorrentSeedHours = 48 }));
        Assert.Equal("seed until 2.5", TorrentText.SeedUntil("ratio", 2.5, null));
        Assert.Equal("seed for 12 h", TorrentText.SeedUntil("hours", 12, null));
        Assert.Equal("seeds until stopped", TorrentText.SeedUntil("never", null, null));
        Assert.Equal("doesn't seed", TorrentText.SeedUntil("global", null, new SettingsDto { TorrentSeedAfterDownload = false }));
        Assert.Equal("Until ratio 1.0 or 48 hours", TorrentText.SeedingSubtitle(new SettingsDto { TorrentSeedAfterDownload = true, TorrentSeedRatio = 1, TorrentSeedHours = 48 }));
        Assert.Equal("Until ratio 2.5", TorrentText.SeedingSubtitle(new SettingsDto { TorrentSeedAfterDownload = true, TorrentSeedRatio = 2.5, TorrentSeedHours = 0 }));
        Assert.Equal("Stops when the download is done", TorrentText.SeedingSubtitle(new SettingsDto { TorrentSeedAfterDownload = false }));
    }

    [Fact]
    public void Settings_rows_and_the_group_title_follow_the_host()
    {
        var settings = new SettingsDto { TorrentListenPort = 51413, TorrentDht = true, TorrentEncryption = "prefer" };
        Assert.Equal("Port 51413, DHT, encryption, speed", TorrentText.ConnectionsSubtitle(settings));
        settings.TorrentDht = false; settings.TorrentEncryption = "require"; settings.TorrentUploadLimit = 500_000;
        Assert.Equal("Port 51413, no DHT, encryption required, up to 500 KB/s", TorrentText.ConnectionsSubtitle(settings));
        Assert.Equal("Torrents on this phone", TorrentText.GroupTitle(true, "x"));
        Assert.Equal("Torrents on Office PC", TorrentText.GroupTitle(false, "Office PC"));
        Assert.Equal("Off: magnets go to TorBox", TorrentText.DownloadHereSubtitle(false, true));
        Assert.Equal("Peers see this phone's IP address, and it uploads to them while it downloads.", TorrentText.PhoneHint(true, ""));
        Assert.StartsWith("Peers see Office PC's IP address", TorrentText.PhoneHint(false, "Office PC"));
    }

    [Fact]
    public void The_details_card_lines_and_the_overview_rows()
    {
        var job = Torrent("downloading", (j, t) => { t.Ratio = 0.08; j.EstimatedRemaining = TimeSpan.FromSeconds(78); });
        Assert.Equal("↓ 18.6 MB/s · ↑ 1.2 MB/s", TorrentText.RateLine(job));
        Assert.Equal("2.5 GB of 3.9 GB · 1 min 18 s left", TorrentText.ProgressLine(job));
        // t4 shows both directions while it downloads, the upload at 0 too.
        Assert.Equal("↓ 18.6 MB/s · ↑ 0 B/s", TorrentText.RateLine(Torrent("downloading", (_, t) => t.UploadRate = 0)));
        Assert.Equal("Error · timed out", TorrentText.TrackerLine(new TorrentTrackerDto { Status = "error", Seeds = -1, Peers = -1, Message = "timed out" }));
        Assert.Equal("Error · refused for (address)", TorrentText.TrackerLine(new TorrentTrackerDto { Status = "error", Seeds = -1, Peers = -1, Message = "refused for https://t.example/announce?passkey=abc" }));

        var trackers = new TorrentTrackersResult { Ok = true, Trackers = [new TorrentTrackerDto { Seeds = 212, Peers = 31 }, new TorrentTrackerDto { Seeds = -1, Peers = -1 }] };
        var pieces = new TorrentPiecesResult { Ok = true, Count = 15820, Have = 10124 };
        var details = new TorrentDetailsResult { Ok = true, PieceCount = 15820, AddedAt = new DateTimeOffset(2026, 11, 21, 14, 1, 0, TimeSpan.Zero) };
        var settings = new SettingsDto { TorrentSeedAfterDownload = true, TorrentSeedRatio = 1, TorrentSeedHours = 48, TorrentListenPort = 51413 };
        var now = details.AddedAt.AddMinutes(30);
        var lines = TorrentText.Overview(job, details, trackers, pieces, settings, "Downloads / Disc images", now).ToDictionary(l => l.Label, l => l.Value);
        Assert.Equal("48 of 212", lines["Seeds"]);
        Assert.Equal("9 of 31", lines["Peers"]);
        Assert.Equal("0.08 · seed until 1.0 or 48 h", lines["Ratio"]);
        Assert.Equal("10,124 of 15,820", lines["Pieces"]);
        Assert.Equal("Downloads / Disc images", lines["Saved to"]);
        Assert.Equal("port 51413", lines["Network"]);
        Assert.EndsWith(", magnet", lines["Added"]);
        Assert.StartsWith("Today ", lines["Added"]);

        // Nothing from the Host: connected counts only, and no rows made up.
        var bare = TorrentText.Overview(job, null, null, null, null, "", now).Select(l => l.Label).ToList();
        Assert.DoesNotContain("Pieces", bare);
        Assert.DoesNotContain("Saved to", bare);
        Assert.DoesNotContain("Network", bare);
    }

    [Fact]
    public void Peers_and_trackers_never_show_addresses_or_tracker_messages()
    {
        var peer = new TorrentPeerDto { Address = "203.0.113.5", Port = 51413, Flags = ["utp", "encrypted"], Progress = 0.416, DownloadRate = 6_100_000, UploadRate = 0 };
        Assert.Equal("peer 3 · IPv4 · uTP · encrypted", TorrentText.PeerTitle(3, peer));
        Assert.DoesNotContain("203.0.113.5", TorrentText.PeerTitle(3, peer) + TorrentText.PeerLine(peer));
        Assert.Equal("has 41% · ↓ 6.1 MB/s", TorrentText.PeerLine(peer));
        Assert.Equal("peer 1 · IPv6 · TCP", TorrentText.PeerTitle(1, new TorrentPeerDto { Address = "2001:db8::1" }));

        Assert.Equal("https://tracker.example/…", TorrentText.TrackerShown("https://tracker.example/abcd1234secret/announce?passkey=1"));
        Assert.Equal("https://tracker.example/announce", TorrentText.TrackerShown("https://tracker.example/announce?passkey=1"));
        var line = TorrentText.TrackerLine(new TorrentTrackerDto { Status = "working", Seeds = 212, Peers = 31, Message = "passkey=abc" });
        Assert.Equal("Working · 212 seeds · 31 peers", line);
    }

    // ---- Client ----

    [Fact]
    public async Task The_client_sends_torrent_messages_and_reads_the_replies()
    {
        var host = new FakeHostConnection();
        var id = Guid.NewGuid();
        host.Reply = request => request.Type switch
        {
            TorrentMessageTypes.Details => Reply(request, TorrentMessageTypes.DetailsResult, new TorrentDetailsResult { Ok = true, PieceCount = 7 }),
            TorrentMessageTypes.Seed => Reply(request, TorrentMessageTypes.ActionResult, new TorrentActionResult { Ok = true }),
            TorrentMessageTypes.SetFiles => Reply(request, TorrentMessageTypes.ActionResult, new TorrentActionResult { Ok = true }),
            TorrentMessageTypes.Watch => Reply(request, TorrentMessageTypes.ActionResult, new TorrentActionResult { Ok = true }),
            _ => Reply(request, MessageTypes.HostActionResult, new HostActionResult { Ok = false, Message = "no" }),
        };
        var client = new TorrentClient(host);
        Assert.Equal(7, (await client.DetailsAsync(id)).PieceCount);
        Assert.True((await client.SeedAsync(id, false)).Ok);
        Assert.True((await client.SetFilesAsync(id, [2, 0])).Ok);
        Assert.True((await client.WatchAsync(true)).Ok);

        Assert.Equal([TorrentMessageTypes.Details, TorrentMessageTypes.Seed, TorrentMessageTypes.SetFiles, TorrentMessageTypes.Watch], host.Sent.Select(m => m.Type));
        var seed = host.Sent[1].GetPayload<TorrentSeedRequest>();
        Assert.Equal(id, seed.JobId);
        Assert.False(seed.On);
        Assert.Equal([0, 2], host.Sent[2].GetPayload<TorrentSetFilesRequest>().FileIndexes);
        Assert.Equal(TorrentClient.WatchLeaseSeconds, host.Sent[3].GetPayload<TorrentWatchRequest>().LeaseSeconds);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => client.PeersAsync(id));
        Assert.Equal("no", refused.Message);
    }

    [Fact]
    public async Task A_reply_of_the_wrong_kind_is_an_error_and_support_comes_from_the_hello()
    {
        var host = new FakeHostConnection();
        host.Reply = request => Reply(request, TorrentMessageTypes.ActionResult, new TorrentActionResult { Ok = true });
        await Assert.ThrowsAsync<InvalidDataException>(() => new TorrentClient(host).FilesAsync(Guid.NewGuid()));
        Assert.True(TorrentClient.Offered(new HostHelloDto { Capabilities = ["sites", "torrents"] }));
        Assert.False(TorrentClient.Offered(new HostHelloDto { Capabilities = ["sites"] }));
        Assert.False(TorrentClient.Offered(null));
    }

    [Fact]
    public void A_file_goes_out_as_its_bytes_and_a_magnet_waits_for_the_choice()
    {
        var bytes = Encoding.ASCII.GetBytes("d4:infod4:name1:aee");
        var request = TorrentClient.FileRequest(bytes, "/dl", [2, 0]);
        Assert.Equal(Convert.ToBase64String(bytes), request.Data);
        Assert.Equal("chosen", request.Files);
        Assert.Equal([0, 2], request.FileIndexes);
        Assert.Equal("", request.Input);
        Assert.Equal("all", TorrentClient.FileRequest(bytes, null, null).Files);
        var magnet = TorrentClient.MagnetRequest(Magnet, null);
        Assert.Equal("askWhenReady", magnet.Files);
        Assert.Equal(Magnet, magnet.Input);
    }

    // ---- Add model ----

    private static TorrentAddModel Model(FakeHostConnection host) => new(new TorrentClient(host));

    [Fact]
    public async Task A_magnet_is_added_to_find_its_files_then_Download_names_them()
    {
        var host = new FakeHostConnection();
        var jobId = Guid.NewGuid();
        var files = ThreeFiles();
        host.Reply = request => request.Type switch
        {
            TorrentMessageTypes.Inspect => Reply(request, TorrentMessageTypes.InspectResult, new TorrentInspectResult { Ok = true, Kind = "magnet", Name = "Debian", Destination = "/dl/Torrents" }),
            TorrentMessageTypes.Add => Reply(request, TorrentMessageTypes.AddResult, new TorrentAddResult { Ok = true, Job = new JobDto { Id = jobId, Torrent = new TorrentDto() } }),
            TorrentMessageTypes.Files => Reply(request, TorrentMessageTypes.FilesResult, new TorrentFilesResult { Ok = true, Files = files }),
            TorrentMessageTypes.SetFiles => Reply(request, TorrentMessageTypes.ActionResult, new TorrentActionResult { Ok = true }),
            _ => throw new InvalidOperationException(request.Type),
        };
        var model = Model(host);
        Assert.True(await model.CheckMagnetAsync(" " + Magnet + " "));
        Assert.Equal(TorrentCheck.Waiting, model.Check);
        Assert.Equal(jobId, model.PendingJobId);
        Assert.Equal("/dl/Torrents", model.Destination);
        Assert.False(model.CanDownload);
        var add = host.Sent.Single(m => m.Type == TorrentMessageTypes.Add).GetPayload<TorrentAddRequest>();
        Assert.Equal("askWhenReady", add.Files);

        // Still no file list: only the peers move.
        var searching = new JobDto { Id = jobId, State = "downloading", Torrent = new TorrentDto { Peers = 12 } };
        Assert.True(await model.PollAsync(searching));
        Assert.Equal(TorrentCheck.Waiting, model.Check);
        Assert.False(await model.PollAsync(searching));
        Assert.DoesNotContain(host.Sent, m => m.Type == TorrentMessageTypes.Files);

        var found = new JobDto { Id = jobId, State = "paused", Torrent = new TorrentDto { Peers = 34, HasMetadata = true, AwaitingFileChoice = true } };
        Assert.True(await model.PollAsync(found));
        Assert.Equal(TorrentCheck.Found, model.Check);
        Assert.Equal("3 files · 3.9 GB", model.FoundTitle);
        Assert.Equal("File list found through 34 peers", model.FoundDetail);
        Assert.Equal("Download 3.9 GB", model.DownloadLabel);
        Assert.Equal("Files · 3 of 3 · 3.9 GB", model.Tree!.Summary);

        // Leave the big file out: only the two small ones go.
        var iso = model.Tree.Roots[0].Children.First(n => n.Name == "debian.iso");
        model.Tree.Toggle(iso);
        Assert.Equal("Download 2.0 KB", model.DownloadLabel);
        var result = await model.DownloadAsync(null);
        Assert.True(result.Done);
        Assert.Null(model.PendingJobId);
        var set = host.Sent.Last().GetPayload<TorrentSetFilesRequest>();
        Assert.Equal(jobId, set.JobId);
        Assert.Equal([1, 2], set.FileIndexes);
    }

    [Fact]
    public async Task A_magnet_already_in_the_list_is_not_added_again()
    {
        var host = new FakeHostConnection();
        host.Reply = request => Reply(request, TorrentMessageTypes.InspectResult, new TorrentInspectResult { Ok = true, Name = "Debian", ExistingJobId = Guid.NewGuid() });
        var model = Model(host);
        await model.CheckMagnetAsync(Magnet);
        Assert.Equal(TorrentCheck.Existing, model.Check);
        Assert.Null(model.PendingJobId);
        Assert.DoesNotContain(host.Sent, m => m.Type == TorrentMessageTypes.Add);
        Assert.False(model.CanDownload);
    }

    [Fact]
    public async Task Text_that_is_not_a_magnet_and_a_Host_with_torrents_off_say_so()
    {
        var host = new FakeHostConnection();
        var model = Model(host);
        await model.CheckMagnetAsync("hello");
        Assert.Equal(TorrentCheck.NotInput, model.Check);
        Assert.Empty(host.Sent);

        host.Reply = request => Reply(request, TorrentMessageTypes.InspectResult, new TorrentInspectResult { Error = "Torrents are turned off in Options." });
        await model.CheckMagnetAsync(Magnet);
        Assert.Equal(TorrentCheck.Off, model.Check);
        Assert.Equal(TorrentText.TurnedOff, model.Message);

        host.Reply = request => Reply(request, TorrentMessageTypes.InspectResult, new TorrentInspectResult { Error = "That magnet is not valid." });
        await model.CheckMagnetAsync(Magnet);
        Assert.Equal(TorrentCheck.Failed, model.Check);
        Assert.Equal("That magnet is not valid.", model.Message);
    }

    [Fact]
    public async Task A_new_text_drops_an_answer_still_on_its_way_and_reports_the_stray_job()
    {
        var host = new FakeHostConnection();
        var model = Model(host);
        var stray = Guid.NewGuid();
        host.Reply = request =>
        {
            if (request.Type == TorrentMessageTypes.Inspect) return Reply(request, TorrentMessageTypes.InspectResult, new TorrentInspectResult { Ok = true, Name = "Debian" });
            model.Reset();
            return Reply(request, TorrentMessageTypes.AddResult, new TorrentAddResult { Ok = true, Job = new JobDto { Id = stray, Torrent = new TorrentDto() } });
        };
        Assert.False(await model.CheckMagnetAsync(Magnet));
        Assert.Equal(TorrentCheck.None, model.Check);
        // The job the Host made after the text changed is returned for the screen to remove, once.
        Assert.Equal([stray], model.TakeStrays());
        Assert.Empty(model.TakeStrays());
        Assert.Null(model.PendingJobId);
    }

    [Fact]
    public async Task Changing_the_text_or_the_folder_hands_back_the_waiting_job_and_a_chosen_folder_is_used()
    {
        var host = new FakeHostConnection();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var next = first;
        host.Reply = request => request.Type == TorrentMessageTypes.Inspect
            ? Reply(request, TorrentMessageTypes.InspectResult, new TorrentInspectResult { Ok = true })
            : Reply(request, TorrentMessageTypes.AddResult, new TorrentAddResult { Ok = true, Job = new JobDto { Id = next, Torrent = new TorrentDto() } });
        var model = Model(host);
        await model.CheckMagnetAsync(Magnet);
        next = second;
        await model.CheckMagnetAsync(Magnet, "/dl/Other");
        Assert.Equal(second, model.PendingJobId);
        Assert.Equal([first], model.TakeStrays());
        Assert.Equal("/dl/Other", host.Sent.Last(m => m.Type == TorrentMessageTypes.Add).GetPayload<TorrentAddRequest>().Destination);
        Assert.Equal("/dl/Other", host.Sent.Last(m => m.Type == TorrentMessageTypes.Inspect).GetPayload<TorrentInspectRequest>().Destination);

        model.Reset();
        Assert.Equal(TorrentCheck.None, model.Check);
        Assert.Equal([second], model.TakeStrays());
        model.MarkOff();
        Assert.Equal(TorrentCheck.Off, model.Check);
    }

    [Fact]
    public async Task A_torrent_file_on_this_phones_host_is_listed_and_added_with_its_bytes()
    {
        var host = new FakeHostConnection();
        var bytes = Encoding.ASCII.GetBytes("torrent bytes");
        host.Reply = request => request.Type switch
        {
            TorrentMessageTypes.Inspect => Reply(request, TorrentMessageTypes.InspectResult, new TorrentInspectResult
            { Ok = true, Kind = "torrentFile", Name = "Debian", Files = ThreeFiles(), Destination = "/dl/Torrents", FreeBytes = 10_000_000_000 }),
            TorrentMessageTypes.Add => Reply(request, TorrentMessageTypes.AddResult, new TorrentAddResult { Ok = true, Job = new JobDto { Torrent = new TorrentDto() } }),
            _ => throw new InvalidOperationException(request.Type),
        };
        var model = Model(host);
        await model.CheckFileAsync(bytes, "debian.torrent");
        Assert.Equal(TorrentCheck.Found, model.Check);
        Assert.True(model.IsFile);
        Assert.Equal("File list read from the torrent file", model.FoundDetail);
        // The Host is sent the bytes, never a path on this phone.
        Assert.Equal(Convert.ToBase64String(bytes), host.Sent[0].GetPayload<TorrentInspectRequest>().Data);
        Assert.Equal("", host.Sent[0].GetPayload<TorrentInspectRequest>().Input);

        Assert.True((await model.DownloadAsync("/dl/Mine")).Done);
        var add = host.Sent.Last().GetPayload<TorrentAddRequest>();
        Assert.Equal(Convert.ToBase64String(bytes), add.Data);
        Assert.Equal("chosen", add.Files);
        Assert.Equal([0, 1, 2], add.FileIndexes);
        Assert.Equal("/dl/Mine", add.Destination);
        // The path is never part of what is added.
        Assert.Equal("", add.Input);
    }

    [Fact]
    public async Task A_torrent_file_for_an_older_host_is_added_whole_and_a_full_disk_stops_Download()
    {
        var host = new FakeHostConnection();
        host.Reply = request => Reply(request, TorrentMessageTypes.InspectResult, new TorrentInspectResult { Error = TorrentAddModel.OlderHostEmptyInput });
        var model = Model(host);
        await model.CheckFileAsync([1, 2, 3], "a.torrent");
        Assert.Equal(TorrentCheck.FileUnlisted, model.Check);
        Assert.True(model.CanDownload);
        Assert.Equal("Download", model.DownloadLabel);

        // A damaged file is the Host's answer, not an older Host.
        host.Reply = request => Reply(request, TorrentMessageTypes.InspectResult, new TorrentInspectResult { Error = "The saved .torrent file is damaged." });
        await model.CheckFileAsync([1, 2, 3], "a.torrent");
        Assert.Equal(TorrentCheck.Failed, model.Check);

        host.Reply = request => Reply(request, TorrentMessageTypes.InspectResult, new TorrentInspectResult
        { Ok = true, Name = "Big", Files = ThreeFiles(), SpaceProblem = "Not enough free space: the chosen files need 3.9 GB and the folder has 1 GB free." });
        await model.CheckFileAsync([1, 2, 3], "a.torrent");
        Assert.Equal(TorrentCheck.Found, model.Check);
        Assert.False(model.CanDownload);
    }

    [Fact]
    public async Task A_refused_set_of_files_keeps_the_pending_job_so_Cancel_can_remove_it()
    {
        var host = new FakeHostConnection();
        var jobId = Guid.NewGuid();
        host.Reply = request => request.Type switch
        {
            TorrentMessageTypes.Inspect => Reply(request, TorrentMessageTypes.InspectResult, new TorrentInspectResult { Ok = true, Name = "Debian" }),
            TorrentMessageTypes.Add => Reply(request, TorrentMessageTypes.AddResult, new TorrentAddResult { Ok = true, Job = new JobDto { Id = jobId, Torrent = new TorrentDto() } }),
            TorrentMessageTypes.Files => Reply(request, TorrentMessageTypes.FilesResult, new TorrentFilesResult { Ok = true, Files = ThreeFiles() }),
            _ => Reply(request, TorrentMessageTypes.ActionResult, new TorrentActionResult { Error = "Not enough free space." }),
        };
        var model = Model(host);
        await model.CheckMagnetAsync(Magnet);
        await model.PollAsync(new JobDto { Id = jobId, Torrent = new TorrentDto { HasMetadata = true } });
        var result = await model.DownloadAsync(null);
        Assert.False(result.Done);
        Assert.Equal("Not enough free space.", result.Error);
        Assert.Equal(jobId, model.PendingJobId);
    }

    [Fact]
    public async Task A_pending_magnet_whose_job_failed_or_vanished_ends_the_wait()
    {
        var host = new FakeHostConnection();
        var jobId = Guid.NewGuid();
        host.Reply = request => request.Type == TorrentMessageTypes.Inspect
            ? Reply(request, TorrentMessageTypes.InspectResult, new TorrentInspectResult { Ok = true })
            : Reply(request, TorrentMessageTypes.AddResult, new TorrentAddResult { Ok = true, Job = new JobDto { Id = jobId, Torrent = new TorrentDto() } });
        var model = Model(host);
        await model.CheckMagnetAsync(Magnet);
        Assert.True(await model.PollAsync(new JobDto { Id = jobId, State = "failed", Failure = new FailureDto { Message = "No peers." }, Torrent = new TorrentDto() }));
        Assert.Equal(TorrentCheck.Failed, model.Check);
        Assert.Equal("No peers.", model.Message);

        await model.CheckMagnetAsync(Magnet);
        Assert.True(await model.PollAsync(null));
        Assert.Equal(TorrentCheck.Failed, model.Check);
        Assert.Null(model.PendingJobId);
    }

    [Fact]
    public void The_Files_tab_tree_starts_with_the_wanted_files_chosen()
    {
        var files = ThreeFiles();
        files[1].Wanted = false;
        var tree = TorrentFileTrees.Build(files, wantedOnly: true);
        Assert.Equal([0, 2], TorrentFileTrees.Indexes(tree));
        Assert.Equal(3, tree.TotalCount);
        Assert.Equal([0, 1, 2], TorrentFileTrees.Indexes(TorrentFileTrees.Build(files)));
        // The JSON round trip of the request is what the Host reads.
        var json = JsonSerializer.Serialize(TorrentClient.MagnetRequest(Magnet, null), IpcJsonContext.Default.TorrentAddRequest);
        Assert.Contains("askWhenReady", json);
    }
}
