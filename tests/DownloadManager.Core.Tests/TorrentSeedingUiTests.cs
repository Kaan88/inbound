using System.Globalization;
using DownloadManager.Contracts;
using DownloadManager.Desktop.WinUI;

namespace DownloadManager.Core.Tests;

/// <summary>T2 in the desktop window: seeding settings, list words, Start/Stop Seeding and the details tabs.</summary>
public sealed class TorrentSeedingUiTests
{
    private static string Ratio(double value) => value.ToString("0.00", CultureInfo.CurrentCulture);

    private static JobDto Torrent(string state = "completed", bool seeding = false, double ratio = 0, double uploadRate = 0, int peers = 0,
        long uploaded = 0, double? rate = null) => new()
    {
        Id = Guid.NewGuid(),
        FileName = "Film",
        State = state,
        BytesPerSecond = rate,
        UpdatedAt = DateTimeOffset.UtcNow,
        Torrent = new TorrentDto
        {
            InfoHash = "0123456789abcdef0123456789abcdef01234567", Name = "Film", HasMetadata = true, FileCount = 1, WantedCount = 1,
            Seeding = seeding, Ratio = ratio, UploadRate = uploadRate, Peers = peers, Uploaded = uploaded,
        },
    };

    // ---- Options › Torrents ----

    [Theory]
    [InlineData(1.0, 48.0, 0.0, null)]
    [InlineData(0.0, 48.0, 0.0, null)]
    [InlineData(1.0, 0.0, 0.0, null)]
    [InlineData(double.NaN, 48.0, double.NaN, null)]
    [InlineData(100.0, 8760.0, 500.0, null)]
    [InlineData(-0.1, 48.0, 0.0, "Enter a seeding ratio from 0 to 100 (0 is no ratio limit).")]
    [InlineData(100.5, 48.0, 0.0, "Enter a seeding ratio from 0 to 100 (0 is no ratio limit).")]
    [InlineData(double.PositiveInfinity, 48.0, 0.0, "Enter a seeding ratio from 0 to 100 (0 is no ratio limit).")]
    [InlineData(1.0, 8761.0, 0.0, "Enter seeding hours from 0 to 8760 (0 is no time limit).")]
    [InlineData(1.0, -1.0, 0.0, "Enter seeding hours from 0 to 8760 (0 is no time limit).")]
    [InlineData(0.0, 0.0, 0.0, "Seeding needs a ratio or a time limit; both can't be 0.")]
    [InlineData(double.NaN, double.NaN, 0.0, "Seeding needs a ratio or a time limit; both can't be 0.")]
    [InlineData(1.0, 48.0, -1.0, "The torrent upload limit can't be below 0.")]
    public void SeedingFieldsFollowTheHostsRulesAndWords(double ratio, double hours, double uploadKb, string? expected)
        => Assert.Equal(expected, TorrentSeedRules.Problem(ratio, hours, uploadKb));

    [Fact]
    public void TheUploadLimitIsTypedInKilobytesAndEmptyIsZero()
    {
        Assert.Equal(0, TorrentSeedRules.UploadLimitBytes(double.NaN));
        Assert.Equal(0, TorrentSeedRules.UploadLimitBytes(0));
        Assert.Equal(512_000, TorrentSeedRules.UploadLimitBytes(500));
        Assert.Equal(1536, TorrentSeedRules.UploadLimitBytes(1.5));
        Assert.True(double.IsNaN(TorrentSeedRules.UploadLimitShown(0)));
        Assert.Equal(500, TorrentSeedRules.UploadLimitShown(512_000));
    }

    [Fact]
    public void AnUntouchedUploadLimitBoxKeepsTheSavedBytes()
    {
        // 1000 B/s shows as 0.98 KB/s; saving the untouched box must not make it 1004 B/s.
        Assert.Equal(1000, TorrentSeedRules.UploadLimitToSave(TorrentSeedRules.UploadLimitShown(1000), 1000));
        // Under 6 B/s shows as 0 but is still a limit; an untouched box keeps it.
        Assert.Equal(5, TorrentSeedRules.UploadLimitToSave(TorrentSeedRules.UploadLimitShown(5), 5));
        Assert.Equal(0, TorrentSeedRules.UploadLimitToSave(double.NaN, 0));
        // A changed box saves what it says.
        Assert.Equal(2048, TorrentSeedRules.UploadLimitToSave(2, 1000));
        Assert.Equal(0, TorrentSeedRules.UploadLimitToSave(double.NaN, 1000));
        Assert.Equal(0, TorrentSeedRules.UploadLimitToSave(0, 1000));
        Assert.Equal(512_000, TorrentSeedRules.UploadLimitToSave(500, 0));
        var page = File.ReadAllText(Path.Combine(RepoRoot(), "src", "DownloadManager.Desktop.WinUI", "TorrentOptionsPage.cs"));
        Assert.Contains("TorrentSeedRules.UploadLimitToSave(_uploadLimit.Value, _savedUploadLimit)", page, StringComparison.Ordinal);
    }

    [Fact]
    public void TheOptionsPageGreysTheSeedingFieldsWithTheOthersAndHidesTheFirewallOutsideWindows()
    {
        var page = File.ReadAllText(Path.Combine(RepoRoot(), "src", "DownloadManager.Desktop.WinUI", "TorrentOptionsPage.cs"));
        Assert.Contains("dependent.Add(_seed);", page, StringComparison.Ordinal);
        Assert.Contains("dependent.Add(_uploadLimit);", page, StringComparison.Ordinal);
        Assert.Contains("_seedRatio.IsEnabled = _seedHours.IsEnabled = _enabled.IsChecked == true && _seed.IsChecked == true;", page, StringComparison.Ordinal);
        Assert.Contains("var offerFirewall = offerHandler && OperatingSystem.IsWindows();", page, StringComparison.Ordinal);
        Assert.Contains("TorrentSeedRules.Problem(", page, StringComparison.Ordinal);
    }

    // ---- The list ----

    [Fact]
    public void ASeedingTorrentShowsItsRatioAndUploadRate()
    {
        var seeding = Torrent(seeding: true, ratio: 0.714, uploadRate: 640 * 1024, peers: 3);
        Assert.Equal($"Seeding · ratio {Ratio(0.71)}", TorrentRules.StatusText(seeding));
        Assert.Equal("640.0 KB/s up", TorrentSeedRules.RateText(seeding));
        Assert.Equal(string.Empty, TorrentSeedRules.RateText(Torrent(seeding: true)));
    }

    [Fact]
    public void AFinishedTorrentThatDoesntSeedStaysComplete()
    {
        var done = Torrent();
        Assert.Null(TorrentRules.StatusText(done));
        Assert.Null(TorrentSeedRules.RateText(done));
        // A flag on a job that isn't completed is not seeding.
        Assert.Null(TorrentSeedRules.StatusText(Torrent(state: "paused", seeding: true)));
        Assert.Null(TorrentSeedRules.RateText(new JobDto { State = "completed" }));
    }

    [Fact]
    public void TheChipCountsSeedingTorrentsToo()
    {
        Assert.Null(TorrentRules.ChipText([Torrent()]));
        Assert.Equal("Torrents: 1 seeding · 3 peers", TorrentRules.ChipText([Torrent(seeding: true, peers: 3), Torrent()]));
        Assert.Equal("Torrents: 1 active · 1 seeding · 7 peers",
            TorrentRules.ChipText([Torrent(state: "downloading", peers: 4), Torrent(seeding: true, peers: 3)]));
    }

    /// <summary>Review 4:L9: Delete Completed takes seeding torrents too (they are completed), and says that stops them seeding.</summary>
    [Fact]
    public void Delete_completed_says_seeding_stops()
    {
        Assert.Equal("Remove 3 completed item(s) from the list? The downloaded files will stay on disk.",
            TorrentSeedRules.DeleteCompletedText([Torrent(), Torrent(), new JobDto { State = "completed" }]));
        Assert.Equal("Remove 2 completed item(s) from the list? The downloaded files will stay on disk. "
            + "1 of them is a torrent that is seeding; removing it stops seeding.",
            TorrentSeedRules.DeleteCompletedText([Torrent(), Torrent(seeding: true)]));
        Assert.Equal("Remove 2 completed item(s) from the list? The downloaded files will stay on disk. "
            + "2 of them are torrents that are seeding; removing them stops seeding.",
            TorrentSeedRules.DeleteCompletedText([Torrent(seeding: true), Torrent(seeding: true)]));
    }

    // ---- Start Seeding and Stop Seeding ----

    [Fact]
    public void StartAndStopSeedingActOnTheTorrentsTheyApplyTo()
    {
        var done = Torrent();
        var seeding = Torrent(seeding: true);
        var running = Torrent(state: "downloading");
        var plain = new JobDto { Id = Guid.NewGuid(), State = "completed" };
        Assert.Equal([done.Id], TorrentSeedRules.Targets([done, seeding, running, plain], on: true));
        Assert.Equal([seeding.Id], TorrentSeedRules.Targets([done, seeding, running, plain], on: false));
    }

    [Fact]
    public void StartSeedingNeedsTorrentsOnAndStopSeedingOnlyTheCapability()
    {
        var on = new SettingsDto { TorrentsEnabled = true };
        var off = new SettingsDto { TorrentsEnabled = false };
        JobDto[] both = [Torrent(), Torrent(seeding: true)];
        Assert.Equal((true, true), TorrentSeedRules.Commands(both, true, on));
        Assert.Equal((false, true), TorrentSeedRules.Commands(both, true, off));
        // A server without torrents: neither.
        Assert.Equal((false, false), TorrentSeedRules.Commands(both, false, on));
        Assert.Equal((false, false), TorrentSeedRules.Commands([Torrent(state: "downloading")], true, on));
    }

    [Fact]
    public void TheStatusBarSaysHowSeedingChangesWent()
    {
        Assert.Equal("Film: seeding.", TorrentSeedRules.Said(true, "Film", 1, 0, null));
        Assert.Equal("Film: seeding stopped.", TorrentSeedRules.Said(false, "Film", 1, 0, null));
        Assert.Equal("3 torrents seeding.", TorrentSeedRules.Said(true, null, 3, 0, null));
        Assert.Equal("Seeding stopped for 2 torrents.", TorrentSeedRules.Said(false, null, 2, 0, null));
        Assert.Equal("Seeding didn't start: Torrents are turned off.", TorrentSeedRules.Said(true, "Film", 0, 1, "Torrents are turned off."));
        Assert.Equal("2 seeding. 1 didn't start: Only a finished torrent can be seeded.",
            TorrentSeedRules.Said(true, null, 2, 1, "Only a finished torrent can be seeded."));
        Assert.Equal("Seeding didn't stop for 2 torrents: the Host didn't say why.", TorrentSeedRules.Said(false, null, 0, 2, null));
    }

    [Fact]
    public void TheMainWindowOffersSeedingInTheFileAndContextMenus()
    {
        var root = RepoRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "src", "DownloadManager.Desktop.WinUI", "MainWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(root, "src", "DownloadManager.Desktop.WinUI", "MainWindow.xaml.cs"));
        Assert.Contains("x:Name=\"FileStartSeedingItem\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"FileStopSeedingItem\"", xaml, StringComparison.Ordinal);
        Assert.Contains("CreateContextItem(\"Start Seeding\", MenuStartSeeding_Click)", code, StringComparison.Ordinal);
        Assert.Contains("CreateContextItem(\"Stop Seeding\", MenuStopSeeding_Click)", code, StringComparison.Ordinal);
        Assert.Contains("SyncSeedingItems(FileStartSeedingItem, FileStopSeedingItem);", code, StringComparison.Ordinal);
    }

    // ---- Progress Details: Peers, Trackers, Pieces & Speed ----

    [Fact]
    public void TheListsAreReadOnlyWhileTheirTabShowsAndTheTorrentMayRun()
    {
        var running = Torrent(state: "downloading");
        Assert.True(TorrentDetailsRules.Polls(TorrentDetailsTab.Peers, true, false, false, true, false, false, running));
        Assert.True(TorrentDetailsRules.Polls(TorrentDetailsTab.Pieces, true, false, false, true, false, false, Torrent(seeding: true)));
        Assert.False(TorrentDetailsRules.Polls(TorrentDetailsTab.None, true, false, false, true, false, false, running));
        Assert.False(TorrentDetailsRules.Polls(TorrentDetailsTab.Peers, false, false, false, true, false, false, running)); // hidden
        Assert.False(TorrentDetailsRules.Polls(TorrentDetailsTab.Peers, true, true, false, true, false, false, running)); // minimized
        Assert.False(TorrentDetailsRules.Polls(TorrentDetailsTab.Peers, true, false, true, true, false, false, running)); // closed
        Assert.False(TorrentDetailsRules.Polls(TorrentDetailsTab.Peers, true, false, false, false, false, false, running)); // disconnected
        Assert.False(TorrentDetailsRules.Polls(TorrentDetailsTab.Peers, true, false, false, true, true, false, running)); // older Host
        Assert.False(TorrentDetailsRules.Polls(TorrentDetailsTab.Peers, true, false, false, true, false, true, running)); // said not running
        Assert.False(TorrentDetailsRules.Polls(TorrentDetailsTab.Trackers, true, false, false, true, false, false, Torrent())); // finished, not seeding
        Assert.False(TorrentDetailsRules.Polls(TorrentDetailsTab.Trackers, true, false, false, true, false, false, Torrent(state: "paused")));
        Assert.False(TorrentDetailsRules.Polls(TorrentDetailsTab.Trackers, true, false, false, true, false, false, null));
    }

    [Fact]
    public void AShownTabIsReadAtOnceThenEveryTwoSeconds()
    {
        // Nothing read yet: due at once, whatever the clock says (TickCount64 starts near 0 after boot).
        Assert.True(TorrentDetailsRules.ReadDue(null, 0));
        Assert.True(TorrentDetailsRules.ReadDue(null, 5_000));
        Assert.True(TorrentDetailsRules.ReadDue(null, long.MaxValue));
        Assert.False(TorrentDetailsRules.ReadDue(10_000, 10_000));
        Assert.False(TorrentDetailsRules.ReadDue(10_000, 11_000));
        Assert.True(TorrentDetailsRules.ReadDue(10_000, 11_800)); // a tick a little early still reads
        Assert.True(TorrentDetailsRules.ReadDue(10_000, 12_000));
    }

    [Fact]
    public void OnlyThePiecesTabWakesEverySecond()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), TorrentDetailsRules.TickInterval(TorrentDetailsTab.Pieces));
        Assert.Equal(TorrentDetailsRules.Refresh, TorrentDetailsRules.TickInterval(TorrentDetailsTab.Peers));
        Assert.Equal(TorrentDetailsRules.Refresh, TorrentDetailsRules.TickInterval(TorrentDetailsTab.Trackers));
    }

    [Fact]
    public void ANotRunningAnswerHoldsUntilTheStateChanges()
    {
        Assert.Equal(TorrentDetailsRules.RunKey(Torrent()), TorrentDetailsRules.RunKey(Torrent()));
        Assert.NotEqual(TorrentDetailsRules.RunKey(Torrent()), TorrentDetailsRules.RunKey(Torrent(seeding: true)));
        Assert.NotEqual(TorrentDetailsRules.RunKey(Torrent(state: "paused")), TorrentDetailsRules.RunKey(Torrent(state: "downloading")));
    }

    [Fact]
    public void TheTabsNeedTheTorrentsCapability()
    {
        Assert.True(TorrentDetailsRules.Offered(["torrents", "archives"]));
        Assert.False(TorrentDetailsRules.Offered(["archives"]));
        Assert.False(TorrentDetailsRules.Offered(null));
    }

    [Fact]
    public void APeerIsNamedByItsPlaceAndItsAddressIsOnHover()
    {
        Assert.Equal("peer 3 · IPv4", TorrentDetailsRules.PeerName(3, "203.0.113.5"));
        Assert.Equal("peer 1 · IPv6", TorrentDetailsRules.PeerName(1, "2001:db8::1"));
        Assert.Equal("203.0.113.5:51413", TorrentDetailsRules.PeerAddress("203.0.113.5", 51413));
        Assert.Equal("[2001:db8::1]:6881", TorrentDetailsRules.PeerAddress("2001:db8::1", 6881));
        Assert.Equal("uTP E in", TorrentDetailsRules.PeerFlags(["utp", "encrypted", "incoming", "interested"]));
        Assert.Equal("TCP", TorrentDetailsRules.PeerFlags(null));
        Assert.Equal("TCP seed", TorrentDetailsRules.PeerFlags(["seed", "outgoing"]));
        Assert.Equal("41%", TorrentDetailsRules.Has(0.419));
        Assert.Equal("100%", TorrentDetailsRules.Has(1.2));
        Assert.Equal("0%", TorrentDetailsRules.Has(double.NaN));
        Assert.Equal("0", TorrentDetailsRules.Rate(0));
        Assert.Equal("1.5 MB/s", TorrentDetailsRules.Rate(1.5 * 1024 * 1024));
    }

    [Theory]
    [InlineData("http://bttracker.debian.org:6969/announce", "http://bttracker.debian.org:6969/announce")]
    [InlineData("udp://tracker.example.org:1337", "udp://tracker.example.org:1337")]
    [InlineData("https://tracker.example/announce?passkey=secret", "https://tracker.example/announce")]
    [InlineData("https://private.example/0123456789abcdef/announce", "https://private.example/…")]
    [InlineData("not a url", "Tracker")]
    public void ATrackerIsShownWithoutAPasskey(string url, string expected)
        => Assert.Equal(expected, TorrentDetailsRules.TrackerShown(url));

    [Fact]
    public void TrackerWordsAndCounts()
    {
        Assert.Equal("Working", TorrentDetailsRules.TrackerStatus("working"));
        Assert.Equal("Not contacted", TorrentDetailsRules.TrackerStatus("notContacted"));
        Assert.Equal("Error", TorrentDetailsRules.TrackerStatus("error"));
        Assert.Equal("Updating", TorrentDetailsRules.TrackerStatus("updating"));
        Assert.Equal("Unknown", TorrentDetailsRules.TrackerStatus(null));
        Assert.Equal("–", TorrentDetailsRules.Count(-1));
        Assert.Equal(198.ToString("N0", CultureInfo.CurrentCulture), TorrentDetailsRules.Count(198));
        Assert.Equal("Peers (9)", TorrentDetailsRules.TabTitle("Peers", 9));
        Assert.Equal("Peers", TorrentDetailsRules.TabTitle("Peers", null));
    }

    [Fact]
    public void PieceCellsComeFromRanges()
    {
        // 10 pieces, 5 cells of 2: pieces 0-3 and 6 had.
        var cells = TorrentDetailsRules.Cells(new TorrentPiecesResult { Count = 10, Have = 5, Ranges = [[0, 3], [6, 6]] }, 5);
        Assert.Equal([1.0, 1.0, 0.0, 0.5, 0.0], cells);
        // More cells than pieces: one each.
        Assert.Equal([1.0, 0.0, 1.0], TorrentDetailsRules.Cells(new TorrentPiecesResult { Count = 3, Ranges = [[0, 0], [2, 2]] }, 100));
        // Ranges out of bounds are cut to the torrent.
        Assert.Equal([1.0, 1.0], TorrentDetailsRules.Cells(new TorrentPiecesResult { Count = 2, Ranges = [[-5, 9]] }, 2));
        Assert.Empty(TorrentDetailsRules.Cells(new TorrentPiecesResult { Count = 0 }, 10));
    }

    [Fact]
    public void PieceCellsComeFromTheBitmapHighestBitFirst()
    {
        // 12 pieces: 1010 0000 | 1111 ....
        var bitmap = Convert.ToBase64String([0b1010_0000, 0b1111_0000]);
        var cells = TorrentDetailsRules.Cells(new TorrentPiecesResult { Count = 12, Bitmap = bitmap }, 3);
        Assert.Equal([0.5, 0.0, 1.0], cells);
        Assert.Equal(new double[3], TorrentDetailsRules.Cells(new TorrentPiecesResult { Count = 12, Bitmap = "not base64!" }, 3));
        // Neither: all when the count says so.
        Assert.Equal([1.0, 1.0], TorrentDetailsRules.Cells(new TorrentPiecesResult { Count = 4, Have = 4 }, 2));
    }

    [Fact]
    public void PieceCellsCoverAnUnevenSplit()
    {
        // 7 pieces in 3 cells: 0-2, 3-4, 5-6.
        var cells = TorrentDetailsRules.Cells(new TorrentPiecesResult { Count = 7, Ranges = [[2, 5]] }, 3);
        Assert.Equal(1.0 / 3, cells[0], 6);
        Assert.Equal(1.0, cells[1], 6);
        Assert.Equal(0.5, cells[2], 6);
    }

    [Fact]
    public void ThePiecesSummaryAndTheSpeedLine()
    {
        var summary = TorrentDetailsRules.PiecesSummary(new TorrentPiecesResult { Count = 15820, Have = 10124, PieceLength = 256 * 1024 });
        Assert.Equal($"{10124.ToString("N0", CultureInfo.CurrentCulture)} of {15820.ToString("N0", CultureInfo.CurrentCulture)} pieces · 256.0 KB each", summary);
        Assert.Equal($"Uploading at 640.0 KB/s · ratio {Ratio(0.5)} · 1.0 MB uploaded",
            TorrentDetailsRules.SpeedLine(Torrent(seeding: true, ratio: 0.5, uploadRate: 640 * 1024, uploaded: 1024 * 1024)));
        Assert.Equal("Downloading at 2.0 MB/s", TorrentDetailsRules.SpeedLine(Torrent(state: "downloading", rate: 2 * 1024 * 1024)));
        Assert.Equal(string.Empty, TorrentDetailsRules.SpeedLine(Torrent()));
        Assert.Equal("Upload speed", TorrentDetailsRules.GraphHeading(Torrent(seeding: true)));
        Assert.Equal("Download speed", TorrentDetailsRules.GraphHeading(Torrent(state: "downloading")));
        Assert.Equal(640 * 1024, TorrentDetailsRules.GraphRate(Torrent(seeding: true, uploadRate: 640 * 1024), DateTimeOffset.UtcNow));
    }

    [Fact]
    public void TheDetailsSeedButtonStartsOrStopsAFinishedTorrent()
    {
        var on = new SettingsDto { TorrentsEnabled = true };
        Assert.Equal(true, TorrentDetailsRules.SeedButton(Torrent(), true, on));
        Assert.Equal(false, TorrentDetailsRules.SeedButton(Torrent(seeding: true), true, on));
        Assert.Null(TorrentDetailsRules.SeedButton(Torrent(), true, new SettingsDto()));
        Assert.Equal(false, TorrentDetailsRules.SeedButton(Torrent(seeding: true), true, new SettingsDto()));
        Assert.Null(TorrentDetailsRules.SeedButton(Torrent(state: "downloading"), true, on));
        Assert.Null(TorrentDetailsRules.SeedButton(Torrent(seeding: true), false, on));
        Assert.Null(TorrentDetailsRules.SeedButton(null, true, on));
    }

    [Fact]
    public void TheDetailsWindowReadsTheListsOnlyThroughTheRules()
    {
        var code = File.ReadAllText(Path.Combine(RepoRoot(), "src", "DownloadManager.Desktop.WinUI", "DownloadWindow.TorrentTabs.cs"));
        Assert.Contains("TorrentDetailsRules.Polls(tab, AppWindow.IsVisible, Minimized, _closed, _connected", code, StringComparison.Ordinal);
        Assert.Contains("_torrentTimer.Stop();", code, StringComparison.Ordinal);
        Assert.Contains("TorrentDetailsRules.ReadDue(_torrentQueriedAt, Environment.TickCount64)", code, StringComparison.Ordinal);
        Assert.Contains("_torrentTimer.Interval = TorrentDetailsRules.TickInterval(tab);", code, StringComparison.Ordinal);
        Assert.DoesNotContain("long.MinValue", code, StringComparison.Ordinal);
        // Back from a dropped connection both "older Host" and "not running" are forgotten and the shown tab is read again.
        var reconnect = code[code.IndexOf("_connection.ConnectionChanged +=", StringComparison.Ordinal)..];
        reconnect = reconnect[..reconnect.IndexOf("};", StringComparison.Ordinal)];
        Assert.Contains("_torrentUnsupported = false;", reconnect, StringComparison.Ordinal);
        Assert.Contains("_notRunningFor = null;", reconnect, StringComparison.Ordinal);
        Assert.Contains("SyncTorrentPolling();", reconnect, StringComparison.Ordinal);
        var window = File.ReadAllText(Path.Combine(RepoRoot(), "src", "DownloadManager.Desktop.WinUI", "DownloadWindow.xaml"));
        foreach (var tab in new[] { "PeersTab", "TrackersTab", "PiecesTab" })
            Assert.Contains($"x:Name=\"{tab}\"", window, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DownloadManager.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }
}
