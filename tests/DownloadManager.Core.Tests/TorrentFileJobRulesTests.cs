using DownloadManager.Contracts;
using DownloadManager.Desktop.WinUI;

namespace DownloadManager.Core.Tests;

/// <summary>
/// T10 (docs/plans/t10-torrent-links.md, option A), the window's side: a finished .torrent download offers Open as
/// Torrent, a dialog line for its file is added by the download's id, the Host's launch may name several files, and a
/// progress window already open for it closes as the torrent dialog takes over.
/// </summary>
public sealed class TorrentFileJobRulesTests
{
    private static readonly string Folder = OperatingSystem.IsWindows() ? @"C:\Users\me\Downloads" : "/home/me/Downloads";
    private static readonly string FilePath = Path.Combine(Folder, "ubuntu.torrent");

    private static JobDto Finished(bool ready = true, string state = "completed", string? path = null) => new()
    {
        Id = Guid.NewGuid(), State = state, TorrentFileReady = ready, FinalFilePath = path ?? FilePath, FileName = "ubuntu.torrent",
    };

    [Fact]
    public void Open_as_torrent_is_for_one_finished_torrent_file_download_only()
    {
        Assert.True(TorrentRules.OpensAsTorrent(Finished()));
        Assert.False(TorrentRules.OpensAsTorrent(Finished(ready: false)));
        Assert.False(TorrentRules.OpensAsTorrent(Finished(state: "downloading")));
        var removed = Finished();
        removed.FileRemovedAfterExtraction = true;
        Assert.False(TorrentRules.OpensAsTorrent(removed));
        var torrent = Finished();
        torrent.Torrent = new TorrentDto { InfoHash = new string('a', 40) };
        Assert.False(TorrentRules.OpensAsTorrent(torrent));
        var provider = Finished();
        provider.IsRemote = true;
        Assert.False(TorrentRules.OpensAsTorrent(provider));
        Assert.False(TorrentRules.OpensAsTorrent(null));
        // A server's download outside this session's folders comes without its path, and the server refuses it by id too.
        Assert.False(TorrentRules.OpensAsTorrent(Finished(path: " ")));
    }

    [Fact]
    public void On_another_server_only_a_rows_own_open_as_torrent_is_the_servers_file()
    {
        var finished = Finished();
        JobDto[] jobs = [finished];
        var opened = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal) { FilePath };
        var none = new HashSet<string>();

        // This PC's Host: the same path is the same file, from wherever it came.
        Assert.Equal(finished.Id, TorrentRules.TorrentFileJobFor(jobs, FilePath, otherServer: false, none));
        // Another server: the same path imported or opened from Explorer is a file on this PC, to be uploaded.
        Assert.Null(TorrentRules.TorrentFileJobFor(jobs, FilePath, otherServer: true, none));
        // That server's row's Open as Torrent: its own file.
        Assert.Equal(finished.Id, TorrentRules.TorrentFileJobFor(jobs, " " + FilePath, otherServer: true, opened));
        Assert.Null(TorrentRules.TorrentFileJobFor(jobs, Path.Combine(Folder, "other.torrent"), otherServer: true, opened));
        Assert.Null(TorrentRules.TorrentFileJobFor(jobs, null, otherServer: true, opened));
    }

    [Fact]
    public void A_dialog_line_for_the_file_of_a_finished_torrent_download_is_added_by_that_download()
    {
        var finished = Finished();
        var other = Finished(path: Path.Combine(Folder, "other.torrent"));
        var notReady = Finished(ready: false, path: Path.Combine(Folder, "plain.torrent"));
        JobDto[] jobs = [other, finished, notReady];

        Assert.Equal(finished.Id, TorrentRules.TorrentFileJobFor(jobs, FilePath));
        Assert.Equal(finished.Id, TorrentRules.TorrentFileJobFor(jobs, "  " + FilePath + " "));
        if (OperatingSystem.IsWindows()) Assert.Equal(finished.Id, TorrentRules.TorrentFileJobFor(jobs, FilePath.ToUpperInvariant()));
        // A .torrent file that is no finished torrent download, a magnet, and nothing at all: no download.
        Assert.Null(TorrentRules.TorrentFileJobFor(jobs, Path.Combine(Folder, "plain.torrent")));
        Assert.Null(TorrentRules.TorrentFileJobFor(jobs, Path.Combine(Folder, "elsewhere.torrent")));
        Assert.Null(TorrentRules.TorrentFileJobFor(jobs, "magnet:?xt=urn:btih:" + new string('b', 40)));
        Assert.Null(TorrentRules.TorrentFileJobFor(jobs, " "));
    }

    [Fact]
    public void An_open_progress_window_closes_when_its_download_turns_out_to_be_a_torrent_file_here()
    {
        var settings = new SettingsDto { TorrentsEnabled = true };
        Assert.True(TorrentRules.ProgressClosesForTorrentFile(Finished(), localHost: true, torrentsOffered: true, settings, openedByHand: false));
        // Opened by hand, another server's, torrents off, not a torrent file, or not finished: it stays.
        Assert.False(TorrentRules.ProgressClosesForTorrentFile(Finished(), localHost: true, torrentsOffered: true, settings, openedByHand: true));
        Assert.False(TorrentRules.ProgressClosesForTorrentFile(Finished(), localHost: false, torrentsOffered: true, settings, openedByHand: false));
        Assert.False(TorrentRules.ProgressClosesForTorrentFile(Finished(), localHost: true, torrentsOffered: true, new SettingsDto { TorrentsEnabled = false }, openedByHand: false));
        Assert.False(TorrentRules.ProgressClosesForTorrentFile(Finished(ready: false), localHost: true, torrentsOffered: true, settings, openedByHand: false));
        Assert.False(TorrentRules.ProgressClosesForTorrentFile(Finished(state: "downloading"), localHost: true, torrentsOffered: true, settings, openedByHand: false));
    }

    [Fact]
    public void With_torrents_on_but_unavailable_an_open_progress_window_stays_as_the_host_opens_its_completion_window()
    {
        var on = new SettingsDto { TorrentsEnabled = true };
        // No torrent engine on the Host, or its kill switch: the hello lists no "torrents", and the Host's TorrentsOn
        // (TorrentsAvailable && TorrentsEnabled) is false, so it offers no dialog and opens the completion window.
        Assert.False(TorrentRules.ProgressClosesForTorrentFile(Finished(), localHost: true, torrentsOffered: false, on, openedByHand: false));
        // Unless an account takes torrents, which the Host offers the dialog for then too.
        Assert.True(TorrentRules.ProgressClosesForTorrentFile(Finished(), localHost: true, torrentsOffered: false, on, openedByHand: false, providerTakesTorrents: true));
        Assert.True(TorrentRules.ProgressClosesForTorrentFile(Finished(), localHost: true, torrentsOffered: true, on, openedByHand: false));
    }

    [Fact]
    public void With_torrents_off_an_open_progress_window_closes_when_an_account_takes_torrents_as_the_host_offers_it_then()
    {
        var off = new SettingsDto { TorrentsEnabled = false };
        // The Host's rule (TorrentFileOffers.TorrentsCanGo): torrents on, or an account that takes torrents.
        Assert.True(TorrentRules.ProgressClosesForTorrentFile(Finished(), localHost: true, torrentsOffered: true, off, openedByHand: false, providerTakesTorrents: true));
        Assert.False(TorrentRules.ProgressClosesForTorrentFile(Finished(), localHost: true, torrentsOffered: true, off, openedByHand: false, providerTakesTorrents: false));
        Assert.False(TorrentRules.ProgressClosesForTorrentFile(Finished(), localHost: true, torrentsOffered: true, off, openedByHand: true, providerTakesTorrents: true));
        Assert.False(TorrentRules.ProgressClosesForTorrentFile(Finished(), localHost: false, torrentsOffered: true, off, openedByHand: false, providerTakesTorrents: true));
        // The window asks the Host about accounts only for such a download, once, at completion.
        Assert.True(TorrentRules.AsksAboutTorrentFile(Finished(), localHost: true, openedByHand: false));
        Assert.False(TorrentRules.AsksAboutTorrentFile(Finished(ready: false), localHost: true, openedByHand: false));
        Assert.False(TorrentRules.AsksAboutTorrentFile(Finished(state: "downloading"), localHost: true, openedByHand: false));
        Assert.False(TorrentRules.AsksAboutTorrentFile(Finished(), localHost: false, openedByHand: false));
        Assert.False(TorrentRules.AsksAboutTorrentFile(Finished(), localHost: true, openedByHand: true));
        // T10.B: a quiet browser download gets the Host's torrent dialog too, so the same rule holds for it.
        var quiet = Finished();
        quiet.QuietWindows = true;
        Assert.True(TorrentRules.AsksAboutTorrentFile(quiet, localHost: true, openedByHand: false));
        Assert.True(TorrentRules.ProgressClosesForTorrentFile(quiet, localHost: true, torrentsOffered: true, off, openedByHand: false, providerTakesTorrents: true));
        Assert.False(TorrentRules.ProgressClosesForTorrentFile(quiet, localHost: true, torrentsOffered: true, off, openedByHand: false, providerTakesTorrents: false));

        static RemoteAccountDto Account(bool key, bool unreadable, params string[] capabilities)
            => new() { Provider = "torbox", HasKey = key, KeyUnreadable = unreadable, Capabilities = [.. capabilities] };
        Assert.True(TorrentRules.ProviderTakesTorrents([Account(true, false, "links"), Account(true, false, "Torrents")]));
        Assert.False(TorrentRules.ProviderTakesTorrents([Account(false, false, "torrents")]));
        Assert.False(TorrentRules.ProviderTakesTorrents([Account(true, true, "torrents")]));
        Assert.False(TorrentRules.ProviderTakesTorrents([Account(true, false, "links")]));
        Assert.False(TorrentRules.ProviderTakesTorrents([]));
    }

    [Fact]
    public void An_add_from_a_finished_download_sends_its_id_and_no_path_or_upload()
    {
        var id = Guid.NewGuid();
        var request = new TorrentAddRequest
        {
            Input = FilePath, FromJobId = id, OnlyIfListed = true, Destination = Folder, QueueId = 2, StartMode = "later", Files = "chosen",
            FileIndexes = [1], Description = "d", Priorities = [new TorrentFilePriorityDto { Index = 1, Priority = "high" }],
            Options = new TorrentOptionsDto(),
        };
        var payload = RemoteClient.TorrentAddPayload(request, sent: string.Empty);
        Assert.Equal(id, payload.FromJobId);
        Assert.True(payload.OnlyIfListed);
        Assert.Equal(string.Empty, payload.Input);
        Assert.Equal(Folder, payload.Destination);
        Assert.Equal(2, payload.QueueId);
        Assert.Equal("later", payload.StartMode);
        Assert.Equal("chosen", payload.Files);
        Assert.Equal([1], payload.FileIndexes);
        Assert.Equal("d", payload.Description);
        Assert.Same(request.Priorities, payload.Priorities);
        Assert.Same(request.Options, payload.Options);
        // On the wire, as the Host reads it.
        var wire = IpcMessage.Create(TorrentMessageTypes.Add, payload).GetPayload<TorrentAddRequest>();
        Assert.Equal(id, wire.FromJobId);
        Assert.True(wire.OnlyIfListed);
    }

    [Fact]
    public void The_hosts_offer_is_told_apart_from_an_explorer_open_through_the_queue()
    {
        var folder = Path.Combine(Path.GetTempPath(), "t10-queue-" + Guid.NewGuid().ToString("N"));
        try
        {
            var now = DateTimeOffset.UtcNow;
            var explorer = Path.Combine(Folder, "explorer.torrent");
            TorrentOpenRequests.Enqueue(folder, FilePath, now, fromDownload: true);
            TorrentOpenRequests.Enqueue(folder, explorer, now.AddTicks(1));
            var fromDownloads = new HashSet<string>(StringComparer.Ordinal);
            Assert.Equal([FilePath, explorer], TorrentOpenRequests.Drain(folder, now, fromDownloads));
            Assert.Equal([FilePath], fromDownloads);
            // Drain without the set still takes it.
            TorrentOpenRequests.Enqueue(folder, FilePath, now, fromDownload: true);
            Assert.Equal([FilePath], TorrentOpenRequests.Drain(folder, now));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }

        Assert.True(TorrentOpenRequests.IsFromDownload(["app.exe", "--from-download", "--open-torrent", FilePath]));
        Assert.False(TorrentOpenRequests.IsFromDownload(["app.exe", "--open-torrent", FilePath]));
        // The Host's launch (TorrentFileOffers.Arguments, checked in the Integration tests) is this.
        Assert.Equal("--from-download", TorrentOpenRequests.FromDownloadArgument);
        string[] hostLaunch = ["app.exe", "--from-download", "--open-torrent", FilePath];
        Assert.True(TorrentOpenRequests.IsFromDownload(hostLaunch));
        Assert.Equal([FilePath], TorrentOpenRequests.ParseAll(hostLaunch));
    }

    [Fact]
    public void The_hosts_offer_is_not_shown_while_another_server_is_and_a_finished_downloads_share_path_is_kept()
    {
        var finished = Guid.NewGuid();
        const string share = @"\\nas\share\linux.torrent";
        var explorer = Path.Combine(Folder, "explorer.torrent");
        Guid? JobFor(string input) => input == share ? finished : null;

        // This PC's Host shown: its offer and Explorer's open both go to the dialog; a share path only when it is a
        // finished download's file (read by the Host by its id), never a share path from elsewhere.
        var shown = TorrentRules.QueuedToOpen([FilePath, explorer, share, @"\\other\x.torrent"], new HashSet<string> { FilePath }, otherServer: false,
            JobFor, out var kept, out var off);
        Assert.Equal([FilePath, explorer, share], shown);
        Assert.False(kept);
        Assert.True(off);

        // Another server shown: this PC's offer stays here; Explorer's open goes on as before.
        var remote = TorrentRules.QueuedToOpen([FilePath, explorer], new HashSet<string> { FilePath }, otherServer: true, _ => null, out kept, out off);
        Assert.Equal([explorer], remote);
        Assert.True(kept);
        Assert.False(off);
        Assert.Empty(TorrentRules.QueuedToOpen([FilePath], new HashSet<string> { FilePath }, otherServer: true, _ => null, out kept, out _));
        Assert.True(kept);
    }

    [Fact]
    public void The_hosts_launch_may_name_several_files_each_after_its_own_argument()
    {
        var second = Path.Combine(Folder, "debian.torrent");
        Assert.Equal([FilePath, second],
            TorrentOpenRequests.ParseAll(["app.exe", "--open-torrent", FilePath, "--open-torrent", second, "--open-torrent", FilePath]));
        // Each value is checked as one from Explorer is: a relative path or another file is left out.
        Assert.Equal([FilePath], TorrentOpenRequests.ParseAll(["app.exe", "--open-torrent", "relative.torrent", "--open-torrent", FilePath, "--open-torrent"]));
        Assert.Empty(TorrentOpenRequests.ParseAll(["app.exe", "--job", Guid.NewGuid().ToString()]));
        // The single form still takes the first.
        Assert.Equal(FilePath, TorrentOpenRequests.Parse(["app.exe", "--open-torrent", FilePath, "--open-torrent", second]));
    }

    [Fact]
    public void Lines_already_in_the_list_are_handed_back_so_their_finished_download_can_go()
    {
        var lines = new TorrentAddLines(TorrentLineRoute.ThisPc, chooseFiles: true, providerName: null);
        var added = lines.Add([FilePath, Path.Combine(Folder, "new.torrent")], fromPaste: false);
        Assert.Equal(2, added.Count);
        lines.Inspected(added[0], new TorrentInspectResult { Ok = true, InfoHash = new string('c', 40), Name = "ubuntu", ExistingJobId = Guid.NewGuid(), Files = [] });
        lines.Inspected(added[1], new TorrentInspectResult { Ok = true, InfoHash = new string('d', 40), Name = "new", Files = [] });
        Assert.Equal([FilePath], lines.ListedInputs());
    }
}
