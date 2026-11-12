using System.Collections.Concurrent;
using System.Text;
using DownloadManager.Contracts;
using DownloadManager.Runtime;
using DownloadManager.Runtime.Remote;
using TestServer;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// T10 (docs/plans/t10-torrent-links.md, option A): a download that turns out to be a .torrent file is offered as a
/// torrent. The extension does not change: the link is downloaded as any other, with the browser's cookie, from
/// TestServer here. When it finishes, a download that looks like a torrent (its type or name) and whose file reads as
/// one gets <see cref="JobDto.TorrentFileReady"/>; it opens no progress or completion window, the Host batches such
/// completions into one launch of the torrent dialog, and <see cref="TorrentAddRequest.FromJobId"/> adds it and then
/// removes the download and its file.
/// </summary>
public sealed partial class HostCommandDispatcherTests
{
    private const string Cookie = "dmsession=valid-auth-token";

    private async Task<TestServerHost> TorrentFileServerAsync(byte[] file)
    {
        var server = await TestServerHost.StartAsync();
        _cleanup.Add(server);
        server.SetTorrentFile(file);
        return server;
    }

    /// <summary>A download handed over by the browser, with its login cookie; returns its id once it finished.</summary>
    private async Task<JobDto> DownloadFromBrowserAsync(DownloadManagerService service, TestServerHost server, string query, string? fileName,
        string? contentType = null, string folder = "dl-torrents", string source = "chromeAuto", string? packageKey = null, bool? quietWindows = null)
    {
        var added = await service.AddJobAsync(new AddJobRequest
        {
            Url = $"{server.BaseAddress}/auth/torrent/get{query}",
            DestinationDirectory = Path.Combine(_root, folder),
            FileName = fileName,
            ContentType = contentType,
            Source = source,
            StartMode = "startNow",
            PackageKey = packageKey,
            QuietWindows = quietWindows,
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Cookie"] = Cookie },
        });
        Assert.True(added.Accepted, added.Error);
        var id = added.Job!.Id;
        return await UntilAsync(() => service.GetJob(id) is { State: "completed" or "failed" } done ? done : null);
    }

    [Fact]
    public void A_download_looks_like_a_torrent_by_its_type_or_its_name_without_being_read()
    {
        Assert.True(TorrentFileRule.LooksLikeTorrent("application/x-bittorrent", null, "get", "https://a.example/get"));
        Assert.True(TorrentFileRule.LooksLikeTorrent("Application/X-BitTorrent; charset=binary", null, "get", null));
        Assert.True(TorrentFileRule.LooksLikeTorrent(null, "application/x-bittorrent", "get", null));
        // An octet-stream with a .torrent name (Content-Disposition, the add's name, or the address's path).
        Assert.True(TorrentFileRule.LooksLikeTorrent("application/octet-stream", null, "ubuntu.torrent", null));
        Assert.True(TorrentFileRule.LooksLikeTorrent("application/octet-stream", null, "get", "https://a.example/files/ubuntu%20x.TORRENT"));
        // Anything else is an ordinary file: a .torrent only in the query, another type, another name.
        Assert.False(TorrentFileRule.LooksLikeTorrent("application/octet-stream", null, "get.php", "https://a.example/get.php?file=a.torrent"));
        Assert.False(TorrentFileRule.LooksLikeTorrent("application/zip", "text/html", "film.mkv", "https://a.example/film.mkv"));
        Assert.False(TorrentFileRule.LooksLikeTorrent(null, null, null, null));
    }

    [Fact]
    public async Task Only_a_real_torrent_file_within_the_cap_counts()
    {
        Directory.CreateDirectory(_root);
        var (bytes, _) = SingleFileTorrent("ubuntu.iso", 40_000);
        var good = Path.Combine(_root, "good.torrent");
        await File.WriteAllBytesAsync(good, bytes);
        Assert.True(TorrentFileRule.IsTorrentFile(good));

        // A login page saved as x.torrent, an empty file, a missing one, and one over the cap.
        var html = Path.Combine(_root, "html.torrent");
        await File.WriteAllTextAsync(html, "<html><body>Please sign in</body></html>");
        Assert.False(TorrentFileRule.IsTorrentFile(html));
        var empty = Path.Combine(_root, "empty.torrent");
        await File.WriteAllBytesAsync(empty, []);
        Assert.False(TorrentFileRule.IsTorrentFile(empty));
        Assert.False(TorrentFileRule.IsTorrentFile(Path.Combine(_root, "missing.torrent")));
        Assert.False(TorrentFileRule.IsTorrentFile(null));
        var big = Path.Combine(_root, "big.torrent");
        await using (var stream = File.Create(big))
        {
            await stream.WriteAsync(bytes);
            stream.SetLength(TorrentMetadata.MaxFileBytes + 1L);
        }

        Assert.False(TorrentFileRule.IsTorrentFile(big));
    }

    [Fact]
    public async Task A_torrent_link_behind_a_login_downloads_with_the_cookie_and_is_offered_as_a_torrent_with_no_window()
    {
        var service = await TorrentServiceAsync();
        var (bytes, _) = SingleFileTorrent("ubuntu.iso", 40_000);
        var server = await TorrentFileServerAsync(bytes);
        var windows = new ConcurrentQueue<DownloadWindowRequest>();
        var launches = new ConcurrentQueue<IReadOnlyList<string>>();
        using var decisions = new DownloadWindows(service, () => true, windows.Enqueue) { TorrentFilesOffered = () => true };
        using var offers = new TorrentFileOffers(service, () => true, launches.Enqueue, wait: TimeSpan.FromMilliseconds(50));

        // The browser said it was a torrent: no window as it starts, and none when it finishes.
        var done = await DownloadFromBrowserAsync(service, server, "", "ubuntu.torrent", TorrentFileRule.ContentType);
        Assert.Equal("completed", done.State);
        Assert.True(done.TorrentFileReady);
        var launch = Assert.Single(await UntilAsync(() => launches.IsEmpty ? null : launches));
        Assert.Equal([done.FinalFilePath], launch);
        await Task.Delay(100);
        Assert.Empty(windows);
    }

    [Fact]
    public async Task An_octet_stream_named_torrent_by_its_server_counts_and_other_files_are_never_read()
    {
        var service = await TorrentServiceAsync();
        var (bytes, _) = SingleFileTorrent("ubuntu.iso", 40_000);
        var server = await TorrentFileServerAsync(bytes);

        // Only Content-Disposition says .torrent.
        var named = await DownloadFromBrowserAsync(service, server, "?type=application/octet-stream&name=ubuntu.torrent", null);
        Assert.Equal("completed", named.State);
        Assert.EndsWith(".torrent", named.FinalFilePath, StringComparison.OrdinalIgnoreCase);
        Assert.True(named.TorrentFileReady);

        // The same bytes as an ordinary file (another type and name): the rule doesn't open it, so it stays a file.
        var plain = await DownloadFromBrowserAsync(service, server, "?type=application/octet-stream&name=data.bin", "data.bin");
        Assert.Equal("completed", plain.State);
        Assert.False(plain.TorrentFileReady);
    }

    [Fact]
    public async Task A_file_that_is_no_torrent_stays_a_plain_file_and_gets_its_completion_window()
    {
        var service = await TorrentServiceAsync();
        // Typed and named as a torrent, but not one (cut off, or a page a site sends with the torrent's type). A page
        // sent as text/html already fails as a download (the engine's error-page check), so it never gets here.
        var server = await TorrentFileServerAsync(Encoding.UTF8.GetBytes("d8:announce35:http://tracker.example/announce4:info"));
        var windows = new ConcurrentQueue<DownloadWindowRequest>();
        var launches = new ConcurrentQueue<IReadOnlyList<string>>();
        using var decisions = new DownloadWindows(service, () => true, windows.Enqueue) { TorrentFilesOffered = () => true };
        using var offers = new TorrentFileOffers(service, () => true, launches.Enqueue, wait: TimeSpan.FromMilliseconds(20));

        var done = await DownloadFromBrowserAsync(service, server, "", "x.torrent", TorrentFileRule.ContentType);
        Assert.Equal("completed", done.State);
        Assert.False(done.TorrentFileReady);
        // Its name looked like a torrent, so no window as it started; it wasn't one, so its completion window opens.
        var only = Assert.Single(await UntilAsync(() => windows.IsEmpty ? null : windows));
        Assert.Equal(new DownloadWindowRequest(done.Id, IsPackage: false), only);
        await Task.Delay(100);
        Assert.Empty(launches);
    }

    [Fact]
    public async Task With_torrents_going_nowhere_a_torrent_file_keeps_its_windows()
    {
        var service = await TorrentServiceAsync(enabled: false);
        var (bytes, _) = SingleFileTorrent("ubuntu.iso", 40_000);
        var server = await TorrentFileServerAsync(bytes);
        var windows = new ConcurrentQueue<DownloadWindowRequest>();
        var launches = new ConcurrentQueue<IReadOnlyList<string>>();
        // As the Host decides it: torrents off and no account that takes them.
        var offered = () => TorrentFileOffers.TorrentsCanGo(service, null);
        Assert.False(offered());
        using var decisions = new DownloadWindows(service, () => true, windows.Enqueue) { TorrentFilesOffered = offered };
        using var offers = new TorrentFileOffers(service, offered, launches.Enqueue, wait: TimeSpan.FromMilliseconds(20));

        var done = await DownloadFromBrowserAsync(service, server, "", "ubuntu.torrent", TorrentFileRule.ContentType);
        Assert.True(done.TorrentFileReady); // the row still offers Open as Torrent
        await UntilAsync(() => windows.Count >= 2 ? windows : null);
        Assert.Equal([new DownloadWindowRequest(done.Id, false), new DownloadWindowRequest(done.Id, false)], windows.ToArray());
        await Task.Delay(100);
        Assert.Empty(launches);
    }

    [Fact]
    public async Task A_quiet_browser_torrent_link_still_opens_the_torrent_dialog_and_no_other_window()
    {
        // T10.B (owner, 2026-11-12): the extension's default handoff asks for no app window, but a .torrent file is the
        // exception: the torrent dialog opens for it. Its progress and completion windows stay away as before.
        var service = await TorrentServiceAsync();
        var (bytes, _) = SingleFileTorrent("ubuntu.iso", 40_000);
        var server = await TorrentFileServerAsync(bytes);
        var windows = new ConcurrentQueue<DownloadWindowRequest>();
        var launches = new ConcurrentQueue<IReadOnlyList<string>>();
        using var decisions = new DownloadWindows(service, () => true, windows.Enqueue) { TorrentFilesOffered = () => true };
        using var offers = new TorrentFileOffers(service, () => true, launches.Enqueue, wait: TimeSpan.FromMilliseconds(20));

        var done = await DownloadFromBrowserAsync(service, server, "", "ubuntu.torrent", TorrentFileRule.ContentType, quietWindows: true);
        Assert.Equal("completed", done.State);
        Assert.True(done.QuietWindows);
        Assert.True(done.TorrentFileReady);
        Assert.True(TorrentFileOffers.Offers(done, offered: true));
        var launch = Assert.Single(await UntilAsync(() => launches.IsEmpty ? null : launches));
        Assert.Equal([done.FinalFilePath], launch);
        await Task.Delay(100);
        Assert.Empty(windows);
    }

    [Fact]
    public async Task A_quiet_browser_torrent_link_with_torrents_going_nowhere_stays_a_quiet_ordinary_file()
    {
        var service = await TorrentServiceAsync(enabled: false);
        var (bytes, _) = SingleFileTorrent("ubuntu.iso", 40_000);
        var server = await TorrentFileServerAsync(bytes);
        var windows = new ConcurrentQueue<DownloadWindowRequest>();
        var launches = new ConcurrentQueue<IReadOnlyList<string>>();
        var offered = () => TorrentFileOffers.TorrentsCanGo(service, null);
        Assert.False(offered());
        using var decisions = new DownloadWindows(service, () => true, windows.Enqueue) { TorrentFilesOffered = offered };
        using var offers = new TorrentFileOffers(service, offered, launches.Enqueue, wait: TimeSpan.FromMilliseconds(20));

        var done = await DownloadFromBrowserAsync(service, server, "", "ubuntu.torrent", TorrentFileRule.ContentType, quietWindows: true);
        Assert.Equal("completed", done.State);
        Assert.True(done.TorrentFileReady); // the row still offers Open as Torrent
        Assert.False(TorrentFileOffers.Offers(done, offered: false));
        await Task.Delay(200);
        Assert.Empty(launches);
        Assert.Empty(windows);
    }

    [Fact]
    public async Task Ten_torrent_files_that_finish_together_open_one_dialog_and_nothing_waits_while_idle()
    {
        var service = await TorrentServiceAsync();
        var (bytes, _) = SingleFileTorrent("ubuntu.iso", 40_000);
        var server = await TorrentFileServerAsync(bytes);
        var time = new StepTime();
        var launches = new ConcurrentQueue<IReadOnlyList<string>>();
        using var offers = new TorrentFileOffers(service, () => true, launches.Enqueue, time);
        // Handlers run in the order they were added, so once this one has seen a completion, the offers have too.
        var announced = new ConcurrentDictionary<Guid, byte>();
        service.JobChanged += (_, e) => { if (e.ChangeType == "completed") announced.TryAdd(e.Job.Id, 0); };
        async Task<JobDto> FinishedAsync(string query, string name, string? type, string source = "chromeAuto")
        {
            var job = await DownloadFromBrowserAsync(service, server, query, name, type, source: source);
            await UntilAsync(() => announced.ContainsKey(job.Id) ? job : null);
            return job;
        }

        // An ordinary download and a CLI one start no wait.
        await FinishedAsync("?type=application/octet-stream", "data.bin", null);
        await FinishedAsync("", "cli.torrent", TorrentFileRule.ContentType, source: "cli");
        Assert.Equal(0, time.TimersMade);

        var paths = new List<string>();
        for (var i = 0; i < 10; i++)
        {
            paths.Add((await FinishedAsync("", $"link{i}.torrent", TorrentFileRule.ContentType)).FinalFilePath);
            // Each completion moves the launch on by the wait.
            time.Advance(TorrentFileOffers.Wait - TimeSpan.FromMilliseconds(100));
        }

        Assert.Empty(launches);
        Assert.Equal(1, time.TimersMade);
        time.Advance(TimeSpan.FromMilliseconds(100));
        var launch = Assert.Single(launches);
        Assert.Equal(paths, launch);
        Assert.Equal(0, time.TimersRunning);

        // A long list is split so each launch's command line stays short; nothing is left out.
        var many = Enumerable.Range(0, 300).Select(n => $@"C:\Users\me\Downloads\{new string('x', 200)}{n}.torrent").ToList();
        var split = TorrentFileOffers.Launches(many);
        Assert.True(split.Count > 1);
        Assert.Equal(many, split.SelectMany(part => part));
        Assert.All(split, part => Assert.True(part.Sum(path => path.Length + 20) <= TorrentFileOffers.MostCommandLineChars));
        // Marked as this Host's offer, so a window showing another server leaves it on this PC.
        Assert.Equal(["--from-download", "--open-torrent", "a.torrent", "--open-torrent", "b.torrent"], TorrentFileOffers.Arguments(["a.torrent", "b.torrent"]));
    }

    [Fact]
    public async Task Adding_from_the_finished_download_removes_it_and_its_file()
    {
        var service = await TorrentServiceAsync();
        var (bytes, hash) = SingleFileTorrent("ubuntu.iso", 40_000);
        var server = await TorrentFileServerAsync(bytes);
        var done = await DownloadFromBrowserAsync(service, server, "", "ubuntu.torrent", TorrentFileRule.ContentType);
        var session = Session();

        var inspected = (await AskAsync(Dispatcher(service), session, TorrentMessageTypes.Inspect, new TorrentInspectRequest { FromJobId = done.Id }))
            .GetPayload<TorrentInspectResult>();
        Assert.True(inspected.Ok, inspected.Error);
        Assert.Equal(hash, inspected.InfoHash);
        Assert.Equal("ubuntu.iso", inspected.Name);

        var added = (await AskAsync(Dispatcher(service), session, TorrentMessageTypes.Add, new TorrentAddRequest
        {
            FromJobId = done.Id, Destination = Path.Combine(_root, "torrent-out"), StartMode = "later",
        })).GetPayload<TorrentAddResult>();
        Assert.True(added.Ok, added.Error);
        Assert.False(added.Existing);
        Assert.Equal(hash, added.Job!.Torrent!.InfoHash);
        Assert.Null(service.GetJob(done.Id));
        Assert.False(File.Exists(done.FinalFilePath));
    }

    [Fact]
    public async Task A_torrent_already_in_the_list_removes_the_copy_and_shows_the_existing_one()
    {
        var service = await TorrentServiceAsync();
        var (bytes, hash) = SingleFileTorrent("ubuntu.iso", 40_000);
        var server = await TorrentFileServerAsync(bytes);
        var first = await DownloadFromBrowserAsync(service, server, "", "ubuntu.torrent", TorrentFileRule.ContentType);
        var second = await DownloadFromBrowserAsync(service, server, "", "ubuntu.torrent", TorrentFileRule.ContentType);
        Assert.NotEqual(first.FinalFilePath, second.FinalFilePath);
        var session = Session();

        var added = (await AskAsync(Dispatcher(service), session, TorrentMessageTypes.Add, new TorrentAddRequest { FromJobId = first.Id, StartMode = "later" }))
            .GetPayload<TorrentAddResult>();
        Assert.True(added.Ok, added.Error);
        var again = (await AskAsync(Dispatcher(service), session, TorrentMessageTypes.Add, new TorrentAddRequest { FromJobId = second.Id, StartMode = "later" }))
            .GetPayload<TorrentAddResult>();
        Assert.True(again.Ok, again.Error);
        Assert.True(again.Existing);
        Assert.Equal(added.Job!.Id, again.Job!.Id);
        Assert.Equal(hash, again.Job.Torrent!.InfoHash);
        Assert.Null(service.GetJob(second.Id));
        Assert.False(File.Exists(second.FinalFilePath));
    }

    [Fact]
    public async Task Removing_a_listed_copy_never_adds_the_torrent()
    {
        var service = await TorrentServiceAsync();
        var (bytes, hash) = SingleFileTorrent("ubuntu.iso", 40_000);
        var server = await TorrentFileServerAsync(bytes);
        var first = await DownloadFromBrowserAsync(service, server, "", "ubuntu.torrent", TorrentFileRule.ContentType);
        var second = await DownloadFromBrowserAsync(service, server, "", "ubuntu.torrent", TorrentFileRule.ContentType);
        var session = Session();

        // Not in the list: refused, nothing added, and the download and its file stay.
        var none = (await AskAsync(Dispatcher(service), session, TorrentMessageTypes.Add, new TorrentAddRequest { FromJobId = first.Id, OnlyIfListed = true }))
            .GetPayload<TorrentAddResult>();
        Assert.False(none.Ok);
        Assert.Equal(DownloadManagerService.TorrentNoLongerListedText, none.Error);
        Assert.DoesNotContain(service.GetSnapshot().Jobs, job => job.Torrent is not null);
        Assert.NotNull(service.GetJob(first.Id));
        Assert.True(File.Exists(first.FinalFilePath));

        // Listed: the copy goes and the existing torrent is the answer.
        var added = (await AskAsync(Dispatcher(service), session, TorrentMessageTypes.Add, new TorrentAddRequest { FromJobId = first.Id, StartMode = "later" }))
            .GetPayload<TorrentAddResult>();
        Assert.True(added.Ok, added.Error);
        var copy = (await AskAsync(Dispatcher(service), session, TorrentMessageTypes.Add, new TorrentAddRequest { FromJobId = second.Id, OnlyIfListed = true }))
            .GetPayload<TorrentAddResult>();
        Assert.True(copy.Ok, copy.Error);
        Assert.True(copy.Existing);
        Assert.Equal(added.Job!.Id, copy.Job!.Id);
        Assert.Null(service.GetJob(second.Id));
        Assert.False(File.Exists(second.FinalFilePath));

        // The torrent was removed after the dialog read the line: the clean-up adds nothing back.
        var third = await DownloadFromBrowserAsync(service, server, "", "ubuntu.torrent", TorrentFileRule.ContentType);
        var removed = await service.RemoveAsync(added.Job.Id, deleteFile: true, CancellationToken.None);
        Assert.True(removed.Ok, removed.Error);
        var late = (await AskAsync(Dispatcher(service), session, TorrentMessageTypes.Add, new TorrentAddRequest { FromJobId = third.Id, OnlyIfListed = true }))
            .GetPayload<TorrentAddResult>();
        Assert.False(late.Ok);
        Assert.DoesNotContain(service.GetSnapshot().Jobs, job => job.Torrent?.InfoHash == hash);
        Assert.NotNull(service.GetJob(third.Id));
        Assert.True(File.Exists(third.FinalFilePath));
    }

    [Fact]
    public async Task A_torrent_file_in_a_package_is_decided_once_and_another_file_still_opens_the_package_window()
    {
        var service = await TorrentServiceAsync();
        service.PackageSettleDelay = TimeSpan.FromSeconds(2);
        var (bytes, _) = SingleFileTorrent("ubuntu.iso", 40_000);
        var server = await TorrentFileServerAsync(bytes);
        var windows = new ConcurrentQueue<DownloadWindowRequest>();
        var asked = 0;
        using var decisions = new DownloadWindows(service, () => true, windows.Enqueue)
        {
            TorrentFilesOffered = () => { Interlocked.Increment(ref asked); return true; },
        };
        var key = Guid.NewGuid().ToString("N");

        var torrent = await DownloadFromBrowserAsync(service, server, "", "ubuntu.torrent", TorrentFileRule.ContentType, packageKey: key);
        Assert.True(torrent.TorrentFileReady);
        // Its start was skipped once; its later progress didn't ask again.
        Assert.Equal(1, Volatile.Read(ref asked));
        Assert.Empty(windows);

        var plain = await DownloadFromBrowserAsync(service, server, "?type=application/octet-stream&name=data.bin", "data.bin", packageKey: key);
        Assert.Equal("completed", plain.State);
        Assert.NotNull(service.FindGroupOf(plain.Id));
        var opened = await UntilAsync(() => windows.IsEmpty ? null : windows);
        Assert.True(opened.First().IsPackage);
    }

    [Fact]
    public async Task A_failed_add_from_a_download_leaves_the_row_and_its_file()
    {
        var service = await TorrentServiceAsync();
        var (bytes, _) = SingleFileTorrent("ubuntu.iso", 40_000);
        var server = await TorrentFileServerAsync(bytes);
        var done = await DownloadFromBrowserAsync(service, server, "", "ubuntu.torrent", TorrentFileRule.ContentType);
        var plain = await DownloadFromBrowserAsync(service, server, "?type=application/octet-stream&name=data.bin", "data.bin");
        var session = Session();

        // Not a .torrent download, an unknown one, and a bad choice of files: each refused, and nothing is removed.
        var notTorrent = (await AskAsync(Dispatcher(service), session, TorrentMessageTypes.Add, new TorrentAddRequest { FromJobId = plain.Id }))
            .GetPayload<TorrentAddResult>();
        Assert.False(notTorrent.Ok);
        Assert.NotNull(service.GetJob(plain.Id));
        var unknown = (await AskAsync(Dispatcher(service), session, TorrentMessageTypes.Add, new TorrentAddRequest { FromJobId = Guid.NewGuid() }))
            .GetPayload<TorrentAddResult>();
        Assert.False(unknown.Ok);
        var badChoice = (await AskAsync(Dispatcher(service), session, TorrentMessageTypes.Add, new TorrentAddRequest
        {
            FromJobId = done.Id, Files = "chosen", FileIndexes = [7],
        })).GetPayload<TorrentAddResult>();
        Assert.False(badChoice.Ok);
        Assert.NotNull(service.GetJob(done.Id));
        Assert.True(File.Exists(done.FinalFilePath));

        // The file went from the folder: said so, and the row stays.
        File.Delete(done.FinalFilePath);
        var gone = (await AskAsync(Dispatcher(service), session, TorrentMessageTypes.Add, new TorrentAddRequest { FromJobId = done.Id }))
            .GetPayload<TorrentAddResult>();
        Assert.False(gone.Ok);
        Assert.Equal("The .torrent file is no longer there.", gone.Error);
        Assert.NotNull(service.GetJob(done.Id));
    }

    [Fact]
    public async Task With_torrents_off_an_add_from_a_download_says_so_and_the_row_stays()
    {
        var service = await TorrentServiceAsync(enabled: false);
        var (bytes, _) = SingleFileTorrent("ubuntu.iso", 40_000);
        var server = await TorrentFileServerAsync(bytes);
        var done = await DownloadFromBrowserAsync(service, server, "", "ubuntu.torrent", TorrentFileRule.ContentType);
        var refused = (await AskAsync(Dispatcher(service), Session(), TorrentMessageTypes.Add, new TorrentAddRequest { FromJobId = done.Id }))
            .GetPayload<TorrentAddResult>();
        Assert.False(refused.Ok);
        Assert.Equal("Torrents are turned off. Turn them on in Options > Torrents.", refused.Error);
        Assert.NotNull(service.GetJob(done.Id));
        Assert.True(File.Exists(done.FinalFilePath));
    }

    /// <summary>A clock that moves only when told, and counts the timers made on it.</summary>
    private sealed class StepTime : TimeProvider
    {
        private readonly Lock _sync = new();
        private readonly List<StepTimer> _timers = [];
        private DateTimeOffset _now = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);

        public int TimersMade { get; private set; }

        public int TimersRunning
        {
            get { lock (_sync) return _timers.Count; }
        }

        public override DateTimeOffset GetUtcNow()
        {
            lock (_sync) return _now;
        }

        public void Advance(TimeSpan by)
        {
            List<StepTimer> due;
            lock (_sync)
            {
                _now += by;
                due = _timers.Where(timer => timer.Due is { } at && at <= _now).ToList();
                foreach (var timer in due) timer.Due = null;
            }

            foreach (var timer in due) timer.Fire();
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new StepTimer(this, callback, state);
            lock (_sync)
            {
                TimersMade++;
                _timers.Add(timer);
            }

            timer.Change(dueTime, period);
            return timer;
        }

        private sealed class StepTimer(StepTime owner, TimerCallback callback, object? state) : ITimer
        {
            public DateTimeOffset? Due { get; set; }

            public void Fire() => callback(state);

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner._sync) Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
                return true;
            }

            public void Dispose()
            {
                lock (owner._sync)
                {
                    Due = null;
                    owner._timers.Remove(this);
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
