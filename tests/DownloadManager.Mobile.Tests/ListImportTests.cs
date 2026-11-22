using System.Text;
using System.Text.Json;
using DownloadManager.Contracts;
using DownloadManager.Mobile.Lists;
using DownloadManager.Mobile.Torrents;

namespace DownloadManager.Mobile.Tests;

public sealed class ListImportTests
{
    private const string Magnet = "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&dn=Debian+12";

    private static JobListEntry Link(string url, string? name = null, string? folder = null, int queue = 0) =>
        new() { Url = url, FileName = name, DestinationDirectory = folder, QueueId = queue };

    private static JobListEntry TorrentEntry(string? name = "arch.iso", string? folder = null) =>
        new() { Url = Magnet, DestinationDirectory = folder, Torrent = new TorrentListEntry { InfoHash = "0123", Name = name, Magnet = Magnet } };

    private static byte[] File(IEnumerable<JobListEntry> entries) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entries.ToList(), new JsonSerializerOptions(JsonSerializerDefaults.Web)));

    private static ListImportModel Read(IEnumerable<JobListEntry> entries, bool paired = false, string host = "Home PC") =>
        ListImportModel.TryRead(File(entries), "list.json", paired, host)!;

    private static (DownloadsModel Downloads, FakeHostConnection Host) Connect(Func<IpcMessage, IpcMessage?>? reply = null)
    {
        var host = new FakeHostConnection { Reply = r => reply?.Invoke(r) ?? IpcMessage.Create(MessageTypes.JobAddResult, new AddJobResponse { Accepted = true }, r.Id) };
        return (new DownloadsModel(host, a => a(), TimeSpan.Zero), host);
    }

    private static IpcMessage Added(IpcMessage r, bool accepted = true, string? error = null) =>
        IpcMessage.Create(MessageTypes.JobAddResult, new AddJobResponse { Accepted = accepted, Error = error }, r.Id);

    private static List<AddJobRequest> Adds(FakeHostConnection host) =>
        host.Sent.Where(m => m.Type == MessageTypes.JobAdd).Select(m => m.GetPayload<AddJobRequest>()).ToList();

    // ---- Reading ----

    [Fact]
    public void A_file_that_is_not_a_list_or_has_no_downloads_is_refused()
    {
        Assert.Null(ListImportModel.TryRead(Encoding.UTF8.GetBytes("{\"photos\": 3}"), "photos-backup.json", false, ""));
        Assert.Null(ListImportModel.TryRead(Encoding.UTF8.GetBytes("not json at all"), "a.txt", false, ""));
        Assert.Null(ListImportModel.TryRead(Encoding.UTF8.GetBytes("null"), "a.json", false, ""));
        Assert.Null(ListImportModel.TryRead(Encoding.UTF8.GetBytes("[]"), "empty.json", false, ""));
        Assert.Null(ListImportModel.TryRead([], "zero.json", false, ""));
        Assert.Equal("That isn't an Inbound list", ListImportModel.NotAListTitle);
    }

    [Fact]
    public void Counts_split_downloads_into_links_and_torrents()
    {
        var model = Read([Link("https://a.test/1.zip"), Link("https://a.test/2.zip"), TorrentEntry(), Link("https://a.test/3.zip")]);
        Assert.Equal(4, model.Downloads);
        Assert.Equal(3, model.Links);
        Assert.Equal(1, model.Torrents);
        Assert.Equal("list.json", model.FileName);
    }

    [Fact]
    public void A_bare_magnet_counts_as_a_torrent()
    {
        var model = ListImportModel.TryRead(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { new { url = Magnet } })), "m.json", false, "")!;
        Assert.Equal(1, model.Torrents);
        Assert.Equal(0, model.Links);
        Assert.Equal("Debian 12", model.Preview[0].Name);
    }

    // ---- Preview ----

    [Fact]
    public void Names_come_from_the_entry_then_the_torrent_then_the_address()
    {
        Assert.Equal("my.iso", ListImportModel.NameOf(Link("https://a.test/x/y.iso", "my.iso")));
        Assert.Equal("arch.iso", ListImportModel.NameOf(TorrentEntry("arch.iso")));
        Assert.Equal("field recordings.zip", ListImportModel.NameOf(Link("https://a.test/dl/field%20recordings.zip")));
        Assert.Equal("file.bin", ListImportModel.NameOf(Link("https://a.test/dir/file.bin/")));
        Assert.Equal("https://a.test", ListImportModel.NameOf(Link("https://a.test")));
        Assert.Equal("not an address", ListImportModel.NameOf(Link("not an address")));
    }

    [Fact]
    public void The_preview_shows_four_rows_with_their_icons_and_says_how_many_more()
    {
        var entries = new List<JobListEntry> { Link("https://a.test/a.zip"), Link("https://a.test/b.zip"), TorrentEntry("c.iso"), Link("https://a.test/d.zip") };
        for (var i = 0; i < 10; i++) entries.Add(Link($"https://a.test/more{i}.zip"));
        var model = Read(entries);
        Assert.Equal(["a.zip", "b.zip", "c.iso", "d.zip"], model.Preview.Select(r => r.Name));
        Assert.Equal([false, false, true, false], model.Preview.Select(r => r.IsTorrent));
        Assert.Equal("and 10 more", model.MoreText);
        Assert.Null(Read(entries.Take(4)).MoreText);
        Assert.Null(Read(entries.Take(2)).MoreText);
    }

    // ---- Refusals ----

    [Fact]
    public void Ftp_and_other_addresses_are_refused_before_sending_but_a_torrent_never_is()
    {
        var model = Read([Link("ftp://mirror.example.org/old.iso"), Link("ftps://x.test/a"), Link("file:///sdcard/a.zip"), Link("nonsense"), TorrentEntry(), Link("https://ok.test/a.zip")]);
        Assert.Equal(4, model.Refused.Count);
        Assert.Equal("ftp://mirror.example.org/old.iso: FTP addresses aren't supported.", model.Refused[0].BannerText);
        Assert.Equal("ftps://x.test/a: FTP addresses aren't supported.", model.Refused[1].BannerText);
        Assert.Equal("file:///sdcard/a.zip: That isn't a web address.", model.Refused[2].BannerText);
        Assert.Equal("nonsense: That isn't a web address.", model.Refused[3].BannerText);
        Assert.Equal(2, model.SendCount);
        Assert.Equal("4 can't be added", model.RefusedTitle);
        Assert.Equal("ftp://mirror.example.org/old.iso: FTP addresses aren't supported.\nand 3 more", model.RefusedBody);
    }

    [Fact]
    public void One_refusal_names_itself_and_none_shows_no_banner()
    {
        var one = Read([Link("ftp://mirror.example.org/old.iso"), Link("https://ok.test/a.zip")]);
        Assert.Equal("1 can't be added", one.RefusedTitle);
        Assert.Equal("ftp://mirror.example.org/old.iso: FTP addresses aren't supported.", one.RefusedBody);
        var none = Read([Link("https://ok.test/a.zip")]);
        Assert.Null(none.RefusedTitle);
        Assert.Null(none.RefusedBody);
    }

    // ---- Words ----

    [Fact]
    public void Save_to_and_the_button_follow_the_phone_or_the_paired_host()
    {
        var entries = new[] { Link("https://a.test/a.zip"), Link("ftp://x.test/b") };
        var phone = Read(entries);
        Assert.Equal("Save to", phone.SaveToLabel);
        Assert.Equal("Download/Inbound", phone.SaveToValue("/storage/emulated/0/Download/Inbound"));
        Assert.StartsWith("The list's folders are on another computer, so files go here.", phone.SaveToHint);
        Assert.Equal("Add 1", phone.AddLabel);
        phone.ChosenFolder = "/storage/emulated/0/Movies";
        Assert.Equal("Movies", phone.SaveToValue("/dl"));

        var paired = Read(entries, paired: true);
        Assert.Equal("Save to, on Home PC", paired.SaveToLabel);
        Assert.Equal("Each download's own folder", paired.SaveToValue("/dl"));
        Assert.Equal("Folders from the list that Home PC doesn't have use its download folder. Torrents go to its torrent list.", paired.SaveToHint);
        Assert.Equal("Add 1 on Home PC", paired.AddLabel);
        paired.ChosenFolder = "D:\\Stuff";
        Assert.Equal("D:\\Stuff", paired.SaveToValue("/dl"));
    }

    [Fact]
    public void The_button_is_off_when_nothing_can_be_sent()
    {
        Assert.False(Read([Link("ftp://x.test/a")]).CanSend);
        Assert.Equal("Add 0", Read([Link("ftp://x.test/a")]).AddLabel);
        Assert.True(Read([Link("https://x.test/a")]).CanSend);
        Assert.True(Read([TorrentEntry()]).CanSend);
    }

    // ---- Sending links ----

    [Fact]
    public async Task Each_link_is_added_with_the_lists_own_settings()
    {
        var (downloads, host) = Connect();
        var entry = new JobListEntry
        {
            Url = "https://a.test/a.zip", FileName = "a.zip", CategoryId = "archives", DestinationDirectory = "D:\\X", Description = "d", QueueId = 3,
            RequestedConnections = 8, StartMode = "now", ConflictPolicy = "overwrite", ExpectedSha256 = "ab", ContentType = "application/zip",
        };
        var other = Link("https://a.test/b.zip");
        var result = await Read([entry, other], paired: true).SendAsync(downloads, null);
        Assert.Equal(2, result.Added);
        Assert.Empty(result.Skipped);
        var sent = Adds(host);
        Assert.Equal(2, sent.Count);
        var a = sent[0];
        Assert.Equal(("https://a.test/a.zip", "a.zip", "archives", "D:\\X", false, "d", 3, 8), (a.Url, a.FileName, a.CategoryId, a.DestinationDirectory, a.RememberDestination, a.Description, a.QueueId, a.Connections));
        Assert.Equal(("now", "overwrite", "ab", "application/zip"), (a.StartMode, a.ConflictPolicy, a.ExpectedSha256, a.ContentType));
        Assert.Null(sent[1].Connections);
        Assert.Null(sent[1].DestinationDirectory);
        Assert.Equal("Added 2 from the list", result.SnackbarText);
        Assert.Null(result.ActionText);
    }

    [Fact]
    public async Task On_the_phone_the_lists_folders_are_dropped_unless_one_was_chosen()
    {
        var (downloads, host) = Connect();
        var model = Read([Link("https://a.test/a.zip", folder: "D:\\X")]);
        await model.SendAsync(downloads, null);
        Assert.Null(Adds(host)[0].DestinationDirectory);

        host.Sent.Clear();
        model.ChosenFolder = "/storage/emulated/0/Movies";
        await model.SendAsync(downloads, null);
        Assert.Equal("/storage/emulated/0/Movies", Adds(host)[0].DestinationDirectory);
    }

    [Fact]
    public async Task On_a_paired_host_a_chosen_folder_wins_over_the_entrys_own()
    {
        var (downloads, host) = Connect();
        var model = Read([Link("https://a.test/a.zip", folder: "D:\\X")], paired: true);
        model.ChosenFolder = "E:\\All";
        await model.SendAsync(downloads, null);
        Assert.Equal("E:\\All", Adds(host).Single().DestinationDirectory);
    }

    [Fact]
    public async Task A_paired_host_that_refuses_the_lists_folder_gets_one_retry_without_it()
    {
        var calls = 0;
        var (downloads, host) = Connect(r =>
        {
            var folder = r.GetPayload<AddJobRequest>().DestinationDirectory;
            calls++;
            return Added(r, folder is null, folder is null ? null : "That folder isn't allowed.");
        });
        var result = await Read([Link("https://a.test/a.zip", folder: "D:\\Gone")], paired: true).SendAsync(downloads, null);
        Assert.Equal(1, result.Added);
        Assert.Empty(result.Skipped);
        Assert.Equal(2, calls);
        Assert.Equal(["D:\\Gone", null], Adds(host).Select(a => a.DestinationDirectory));
    }

    [Fact]
    public async Task A_link_refused_even_without_the_folder_is_skipped_with_the_hosts_words_and_the_rest_go_on()
    {
        var (downloads, host) = Connect(r =>
            r.GetPayload<AddJobRequest>().Url.Contains("bad") ? Added(r, false, "Not allowed here.") : Added(r));
        var result = await Read([Link("https://a.test/bad.zip", "bad.zip", folder: "D:\\X"), Link("https://a.test/good.zip")], paired: true).SendAsync(downloads, null);
        Assert.Equal(1, result.Added);
        var skipped = Assert.Single(result.Skipped);
        Assert.Equal("bad.zip: Not allowed here.", skipped.DetailsText);
        // Own folder first, then the retry without it, then the next link.
        Assert.Equal(3, Adds(host).Count);
        Assert.Equal("Added 1 from the list · 1 skipped", result.SnackbarText);
        Assert.Equal("Details", result.ActionText);
    }

    [Fact]
    public async Task Without_a_folder_a_refusal_is_not_retried_and_a_throw_is_skipped_with_its_message()
    {
        var n = 0;
        var (downloads, host) = Connect(r =>
        {
            n++;
            if (n == 2) throw new InvalidOperationException("The Host went away.");
            return Added(r, false, null);
        });
        var result = await Read([Link("https://a.test/a.zip"), Link("https://a.test/b.zip"), Link("https://a.test/c.zip")]).SendAsync(downloads, null);
        Assert.Equal(0, result.Added);
        Assert.Equal(3, result.Skipped.Count);
        Assert.Equal(3, Adds(host).Count);
        Assert.Equal("The Host did not accept this download.", result.Skipped[0].Reason);
        Assert.Equal("The Host went away.", result.Skipped[1].Reason);
    }

    [Fact]
    public async Task Refused_entries_are_never_sent_and_join_the_skipped_list_first()
    {
        var (downloads, host) = Connect();
        var result = await Read([Link("ftp://mirror.example.org/old.iso"), Link("https://a.test/a.zip")]).SendAsync(downloads, null);
        Assert.Equal(1, result.Added);
        Assert.Single(Adds(host));
        Assert.Equal("old.iso: FTP addresses aren't supported.", result.Skipped.Single().DetailsText);
        Assert.Equal("Added 1 from the list · 1 skipped", result.SnackbarText);
        Assert.Equal("old.iso: FTP addresses aren't supported.", result.DetailsBody);
    }

    [Fact]
    public async Task Details_lists_every_skipped_entry_by_name_or_address()
    {
        var (downloads, _) = Connect(r => Added(r, false, "Nope."));
        var result = await Read([Link("https://a.test/dir/x.zip"), Link("https://a.test/y.zip", "Y file"), Link("ftp://f.test/z")]).SendAsync(downloads, null);
        Assert.Equal("z: FTP addresses aren't supported.\n\nx.zip: Nope.\n\nY file: Nope.", result.DetailsBody);
        Assert.Equal("Skipped from the list", ListImportResult.DetailsTitle);
    }

    // ---- Sending torrents ----

    private static IpcMessage ImportReply(IpcMessage r, TorrentImportResult result) => IpcMessage.Create(TorrentMessageTypes.ImportResult, result, r.Id);

    [Fact]
    public async Task Torrents_go_in_one_import_request_and_count_as_added()
    {
        var (downloads, host) = Connect(r => r.Type == TorrentMessageTypes.Import ? ImportReply(r, new TorrentImportResult { Ok = true, Added = 2 }) : null);
        var model = Read([TorrentEntry("a"), Link("https://a.test/a.zip"), TorrentEntry("b")]);
        var result = await model.SendAsync(downloads, new TorrentClient(host));
        Assert.Equal(3, result.Added);
        Assert.Empty(result.Skipped);
        var imports = host.Sent.Where(m => m.Type == TorrentMessageTypes.Import).ToList();
        Assert.Single(imports);
        Assert.Equal(2, imports[0].GetPayload<TorrentImportRequest>().Entries.Count);
        Assert.Single(Adds(host));
    }

    [Fact]
    public async Task Torrent_folders_follow_the_same_rules_on_a_copy()
    {
        var (downloads, host) = Connect(r => r.Type == TorrentMessageTypes.Import ? ImportReply(r, new TorrentImportResult { Ok = true, Added = 1 }) : null);
        var source = TorrentEntry(folder: "D:\\Torrents");

        // Phone, no folder chosen: the entry's folder is of another computer.
        var phone = Read([source]);
        await phone.SendAsync(downloads, new TorrentClient(host));
        Assert.Null(host.Sent.Last().GetPayload<TorrentImportRequest>().Entries[0].DestinationDirectory);
        Assert.Equal("D:\\Torrents", phone.Entries[0].DestinationDirectory);

        // Phone, a folder chosen.
        phone.ChosenFolder = "/storage/emulated/0/T";
        await phone.SendAsync(downloads, new TorrentClient(host));
        Assert.Equal("/storage/emulated/0/T", host.Sent.Last().GetPayload<TorrentImportRequest>().Entries[0].DestinationDirectory);

        // Paired: the entry's own folder stays unless one folder was chosen.
        var paired = Read([source], paired: true);
        await paired.SendAsync(downloads, new TorrentClient(host));
        Assert.Equal("D:\\Torrents", host.Sent.Last().GetPayload<TorrentImportRequest>().Entries[0].DestinationDirectory);
        paired.ChosenFolder = "E:\\All";
        await paired.SendAsync(downloads, new TorrentClient(host));
        Assert.Equal("E:\\All", host.Sent.Last().GetPayload<TorrentImportRequest>().Entries[0].DestinationDirectory);
    }

    [Fact]
    public async Task A_host_without_torrents_skips_each_torrent_with_that_reason_and_still_adds_links()
    {
        var (downloads, host) = Connect();
        var result = await Read([TorrentEntry("arch.iso"), Link("https://a.test/a.zip")]).SendAsync(downloads, null);
        Assert.Equal(1, result.Added);
        Assert.Equal("arch.iso: This Host doesn't take torrents.", result.Skipped.Single().DetailsText);
        Assert.DoesNotContain(host.Sent, m => m.Type == TorrentMessageTypes.Import);
    }

    [Fact]
    public async Task A_host_that_does_not_know_the_import_request_skips_the_torrents_with_its_words()
    {
        var (downloads, host) = Connect(r => r.Type == TorrentMessageTypes.Import
            ? IpcMessage.Create(MessageTypes.HostActionResult, new HostActionResult { Ok = false, Message = "Unknown request." }, r.Id) : null);
        var result = await Read([TorrentEntry("a"), TorrentEntry("b")]).SendAsync(downloads, new TorrentClient(host));
        Assert.Equal(0, result.Added);
        Assert.Equal(["a: Unknown request.", "b: Unknown request."], result.Skipped.Select(s => s.DetailsText));
    }

    [Fact]
    public async Task Torrents_the_host_skipped_or_failed_are_counted_in_the_skipped_list()
    {
        var (downloads, host) = Connect(r => ImportReply(r, new TorrentImportResult { Ok = true, Added = 1, Skipped = 1, SkipReason = "Already in the list.", Failed = 1, FirstError = "Bad torrent." }));
        var result = await Read([TorrentEntry("a"), TorrentEntry("b"), TorrentEntry("c")]).SendAsync(downloads, new TorrentClient(host));
        Assert.Equal(1, result.Added);
        Assert.Equal(2, result.Skipped.Count);
        Assert.Equal(["Already in the list.", "Bad torrent."], result.Skipped.Select(s => s.Reason));
    }

    [Fact]
    public async Task When_every_torrent_was_skipped_each_is_named_with_the_reason()
    {
        var (downloads, host) = Connect(r => ImportReply(r, new TorrentImportResult { Ok = true, Skipped = 2, SkipReason = "Torrents are off." }));
        var result = await Read([TorrentEntry("a"), TorrentEntry("b")]).SendAsync(downloads, new TorrentClient(host));
        Assert.Equal(["a: Torrents are off.", "b: Torrents are off."], result.Skipped.Select(s => s.DetailsText));
    }

    [Fact]
    public async Task Sending_is_flagged_only_while_it_runs()
    {
        var (downloads, _) = Connect();
        var model = Read([Link("https://a.test/a.zip")]);
        Assert.False(model.Sending);
        await model.SendAsync(downloads, null);
        Assert.False(model.Sending);
        Assert.True(model.CanSend);
    }
}
