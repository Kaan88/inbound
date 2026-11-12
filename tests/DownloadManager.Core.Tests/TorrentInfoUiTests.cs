using System.Globalization;
using DownloadManager.Contracts;
using DownloadManager.Desktop.WinUI;

namespace DownloadManager.Core.Tests;

/// <summary>
/// T7: the main window's Torrent Info pane (<see cref="TorrentInfoRules"/>): when it shows, how wide, when it watches and
/// reads, and what it says for each state of a torrent.
/// </summary>
public sealed class TorrentInfoUiTests : IDisposable
{
    private const string HashA = "0123456789abcdef0123456789abcdef01234567";
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;

    public TorrentInfoUiTests() => CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

    public void Dispose() => CultureInfo.CurrentCulture = _culture;

    private static JobDto Torrent(string state = "downloading", bool metadata = true, bool seeding = false, long received = 512 * 1024,
        long total = 1024 * 1024, int peers = 10, int seeds = 3, double rate = 2048, double upRate = 1024) => new()
    {
        Id = Guid.NewGuid(),
        FileName = "Film",
        State = state,
        ReceivedBytes = received,
        TotalBytes = total,
        BytesPerSecond = rate,
        EstimatedRemaining = TimeSpan.FromMinutes(4),
        DestinationDirectory = @"D:\Downloads",
        CreatedAt = Now,
        UpdatedAt = Now,
        Torrent = new TorrentDto
        {
            InfoHash = HashA, Name = "Film", HasMetadata = metadata, FileCount = 1, WantedCount = 1, WantedBytes = total,
            Peers = peers, Seeds = seeds, Seeding = seeding, UploadRate = upRate, Ratio = 0.5, Uploaded = 512 * 1024,
        },
    };

    private static JobDto Plain() => new() { Id = Guid.NewGuid(), FileName = "file.zip", State = "downloading" };

    // ---- When it shows ----

    [Fact]
    public void ItShowsForExactlyOneSelectedTorrentWhileTurnedOnAndTheListShows()
    {
        var torrent = Torrent();
        Assert.Same(torrent, TorrentInfoRules.Shown(true, true, [torrent]));
        Assert.Null(TorrentInfoRules.Shown(false, true, [torrent]));
        Assert.Null(TorrentInfoRules.Shown(true, false, [torrent]));
        Assert.Null(TorrentInfoRules.Shown(true, true, []));
        Assert.Null(TorrentInfoRules.Shown(true, true, [torrent, Torrent()]));
        Assert.Null(TorrentInfoRules.Shown(true, true, [Plain()]));
    }

    [Fact]
    public void ItTakesItsWidthWhenTheColumnsStillFitAndNarrowsBeforeTheyWouldNot()
    {
        Assert.Equal(TorrentInfoRules.PreferredWidth, TorrentInfoRules.Width(1600, 1040));
        Assert.Equal(300, TorrentInfoRules.Width(1340, 1040));
        Assert.Equal(TorrentInfoRules.LeastWidth, TorrentInfoRules.Width(1040 + TorrentInfoRules.LeastWidth, 1040));
        Assert.Equal(TorrentInfoRules.PreferredWidth, TorrentInfoRules.Width(1600, double.NaN));
    }

    [Fact]
    public void ItStaysAwayRatherThanMakeTheListScrollSideways()
    {
        // The columns can't fit beside even the narrowest pane: no pane, so selecting a torrent never adds a scroller.
        Assert.Null(TorrentInfoRules.Width(1100, 1040));
        Assert.Null(TorrentInfoRules.Width(1040 + TorrentInfoRules.LeastWidth - 1, 1040));
        // The Torrents view's wider columns at the default window: the pane needs a wider window or narrower columns.
        Assert.Null(TorrentInfoRules.Width(1420, 1274));
        Assert.Equal(TorrentInfoRules.LeastWidth, TorrentInfoRules.Width(1274 + TorrentInfoRules.LeastWidth, 1274));
    }

    [Fact]
    public void ItStaysAwayWhenTheListWouldBeLeftTooNarrowOrTheWidthIsNotKnown()
    {
        Assert.Null(TorrentInfoRules.Width(TorrentInfoRules.LeastWidth + TorrentInfoRules.LeastListWidth - 1, 500));
        Assert.NotNull(TorrentInfoRules.Width(TorrentInfoRules.LeastWidth + TorrentInfoRules.LeastListWidth, 500));
        Assert.Null(TorrentInfoRules.Width(0, 500));
        Assert.Null(TorrentInfoRules.Width(double.NaN, 500));
        // Never so wide that the list is left under its least width.
        Assert.Equal(TorrentInfoRules.LeastWidth + 20, TorrentInfoRules.Width(TorrentInfoRules.LeastWidth + TorrentInfoRules.LeastListWidth + 20, 0));
    }

    // ---- When it watches and reads ----

    [Fact]
    public void ItIsSeenOnlyInAShownUnminimizedWindowWithATorrent()
    {
        var torrent = Torrent();
        Assert.True(TorrentInfoRules.Seen(true, false, torrent));
        Assert.False(TorrentInfoRules.Seen(false, false, torrent));
        Assert.False(TorrentInfoRules.Seen(true, true, torrent));
        Assert.False(TorrentInfoRules.Seen(true, false, null));
    }

    [Theory]
    [InlineData("downloading", false, true)]
    [InlineData("connecting", false, true)]
    [InlineData("verifying", false, true)]
    [InlineData("completed", true, true)]
    [InlineData("completed", false, false)]
    [InlineData("paused", false, false)]
    [InlineData("queued", false, false)]
    [InlineData("failed", false, false)]
    public void ItWatchesOnlyWhileSeenAndTheTorrentRuns(string state, bool seeding, bool expected)
    {
        var torrent = Torrent(state, seeding: seeding);
        Assert.Equal(expected, TorrentInfoRules.Watches(true, torrent));
        Assert.False(TorrentInfoRules.Watches(false, torrent));
        Assert.False(TorrentInfoRules.Watches(true, null));
    }

    [Fact]
    public void TheTrackersAreReadOnAChangeAndThenAtMostEveryTenSecondsWhileItRuns()
    {
        var running = Torrent();
        Assert.True(TorrentInfoRules.TrackersDue(true, true, running, keyChanged: true, sinceLastReadMs: 0));
        Assert.False(TorrentInfoRules.TrackersDue(true, true, running, keyChanged: false, sinceLastReadMs: 9_999));
        Assert.True(TorrentInfoRules.TrackersDue(true, true, running, keyChanged: false, sinceLastReadMs: TorrentControlRules.DetailsReadMs));
        // Not seen, a Host without torrents, or a torrent that doesn't run: nothing is read.
        Assert.False(TorrentInfoRules.TrackersDue(false, true, running, true, long.MaxValue));
        Assert.False(TorrentInfoRules.TrackersDue(true, false, running, true, long.MaxValue));
        Assert.False(TorrentInfoRules.TrackersDue(true, true, Torrent("paused"), true, long.MaxValue));
        Assert.False(TorrentInfoRules.TrackersDue(true, true, Torrent("completed"), true, long.MaxValue));
        Assert.True(TorrentInfoRules.TrackersDue(true, true, Torrent("completed", seeding: true), true, 0));
    }

    [Fact]
    public void TheReadKeyChangesWithTheTorrentAndItsState()
    {
        var torrent = Torrent();
        var key = TorrentInfoRules.ReadKey(torrent);
        Assert.Equal(key, TorrentInfoRules.ReadKey(torrent));
        torrent.State = "paused";
        Assert.NotEqual(key, TorrentInfoRules.ReadKey(torrent));
        Assert.NotEqual(TorrentInfoRules.ReadKey(Torrent()), TorrentInfoRules.ReadKey(Torrent()));
    }

    [Fact]
    public void TheDetailsAreReadOncePerKeyOnAHostWithTheControlsUntilTheV2HashIsKnown()
    {
        Assert.True(TorrentInfoRules.DetailsDue(true, true, false, false));
        Assert.False(TorrentInfoRules.DetailsDue(true, true, true, false));
        Assert.False(TorrentInfoRules.DetailsDue(true, false, false, false));
        Assert.False(TorrentInfoRules.DetailsDue(false, true, false, false));
        Assert.False(TorrentInfoRules.DetailsDue(true, true, false, v2Known: true));
    }

    [Fact]
    public void ATorrentShownPausedIsAskedForItsDetailsAgainOnceItRuns()
    {
        // The Host gives the v2 hash only while the torrent runs: the read made while paused doesn't hold once it starts.
        var torrent = Torrent("paused");
        var readWhilePaused = TorrentInfoRules.DetailsKey(torrent);
        Assert.Equal(readWhilePaused, TorrentInfoRules.DetailsKey(torrent));
        torrent.State = "downloading";
        Assert.NotEqual(readWhilePaused, TorrentInfoRules.DetailsKey(torrent));
        Assert.True(TorrentInfoRules.DetailsDue(true, true, readForThisKey: readWhilePaused == TorrentInfoRules.DetailsKey(torrent), v2Known: false));

        // A magnet that gets its metadata while it runs is asked again too.
        var magnet = Torrent(metadata: false);
        var before = TorrentInfoRules.DetailsKey(magnet);
        magnet.Torrent!.HasMetadata = true;
        Assert.NotEqual(before, TorrentInfoRules.DetailsKey(magnet));
    }

    // ---- What it says ----

    [Fact]
    public void TheTrackersSummaryCountsTheWorkingOnes()
    {
        var trackers = new List<TorrentTrackerDto>
        {
            new() { Url = "https://a.example/announce", Status = "working" },
            new() { Url = "https://b.example/announce", Status = "error" },
            new() { Url = "https://c.example/announce", Status = "working" },
            new() { Url = "https://d.example/announce", Status = "updating" },
        };
        Assert.Equal("2 of 4 working", TorrentInfoRules.TrackersText(trackers, mayRun: true, offered: true));
        Assert.Equal("None: peers come from the DHT and other peers", TorrentInfoRules.TrackersText([], true, true));
        Assert.Equal(TorrentInfoRules.Dash, TorrentInfoRules.TrackersText(null, true, true));
        Assert.Equal("Shown while the torrent runs", TorrentInfoRules.TrackersText(trackers, false, true));
        Assert.Equal(TorrentInfoRules.Dash, TorrentInfoRules.TrackersText(trackers, true, false));
    }

    [Fact]
    public void ADownloadingTorrentShowsItsSpeedsSwarmAndTimeLeft()
    {
        var view = TorrentInfoRules.Describe(Torrent(), "Downloading", null, Now, null, null, true);
        Assert.Equal("Film", view.Name);
        Assert.Equal("Downloading · 10 peers", view.Status);
        Assert.Equal(50, view.Percent);
        Assert.Equal($"50% · {UiFormat.Bytes(512 * 1024)} of {UiFormat.Bytes(1024 * 1024)}", view.Progress);
        Assert.Equal($"{UiFormat.Bytes(2048)}/s", view.Down);
        Assert.Equal($"{UiFormat.Bytes(1024)}/s", view.Up);
        Assert.Equal("3 seeds · 7 peers", view.Swarm);
        Assert.Equal("4m 0s", view.TimeLeft);
        Assert.Equal(@"D:\Downloads", view.Folder);
        Assert.Equal(HashA, view.InfoHash);
        Assert.EndsWith("from a magnet link", view.Added);
        Assert.Equal(TorrentInfoRules.Dash, view.Trackers);
        Assert.Null(view.Goal);
    }

    [Fact]
    public void APausedTorrentHasNoLiveFigures()
    {
        var view = TorrentInfoRules.Describe(Torrent("paused"), "Paused", null, Now, null, null, true);
        Assert.Equal("Paused", view.Status);
        Assert.Equal(TorrentInfoRules.Dash, view.Down);
        Assert.Equal(TorrentInfoRules.Dash, view.Up);
        Assert.Equal(TorrentInfoRules.Dash, view.Swarm);
        Assert.Equal(TorrentInfoRules.Dash, view.TimeLeft);
        Assert.Equal("Shown while the torrent runs", view.Trackers);
        Assert.Equal(50, view.Percent);
    }

    [Fact]
    public void ASeedingTorrentShowsItsUploadRatioAndGoal()
    {
        var job = Torrent("completed", seeding: true, received: 1024 * 1024);
        job.Torrent!.SeedingSince = Now.AddHours(-1);
        var settings = new SettingsDto { TorrentSeedRatio = 2, TorrentSeedHours = 0, TorrentSeedAfterDownload = true };
        var view = TorrentInfoRules.Describe(job, "Complete", settings, Now, null, [new TorrentTrackerDto { Status = "working" }], true);
        Assert.Equal("Seeding · ratio 0.50", view.Status);
        Assert.Equal(100, view.Percent);
        Assert.Equal($"100% · {UiFormat.Bytes(1024 * 1024)}", view.Progress);
        Assert.Equal(TorrentInfoRules.Dash, view.Down);
        Assert.Equal($"{UiFormat.Bytes(1024)}/s", view.Up);
        Assert.Equal(TorrentInfoRules.Dash, view.TimeLeft);
        Assert.StartsWith("0.50 · uploaded", view.Ratio);
        Assert.NotNull(view.Goal);
        Assert.Equal("1 of 1 working", view.Trackers);
    }

    [Fact]
    public void ATorrentWhoseOwnGoalIsNeverSaysItDoesNotSeed()
    {
        var job = Torrent("completed", received: 1024 * 1024);
        job.Torrent!.SeedGoal = "never";
        var settings = new SettingsDto { TorrentSeedRatio = 2, TorrentSeedHours = 0, TorrentSeedAfterDownload = true };
        var view = TorrentInfoRules.Describe(job, "Complete", settings, Now, null, null, true);
        Assert.Equal("Don't seed (this torrent's own option)", view.Goal);
    }

    [Fact]
    public void AFinishedTorrentThatDoesNotSeedSaysComplete()
    {
        var view = TorrentInfoRules.Describe(Torrent("completed", received: 1024 * 1024), "Complete", null, Now, null, null, true);
        Assert.Equal("Complete", view.Status);
        Assert.Equal(TorrentInfoRules.Dash, view.Up);
        Assert.Equal("Shown while the torrent runs", view.Trackers);
    }

    [Fact]
    public void AMagnetWithoutItsFileListSaysSo()
    {
        var job = Torrent(metadata: false, received: 0, total: 0);
        job.TotalBytes = null;
        var view = TorrentInfoRules.Describe(job, "Downloading", null, Now, null, null, true);
        Assert.Equal("Getting file list", view.Progress);
        Assert.Equal(0, view.Percent);
        Assert.StartsWith("Getting file list", view.Status);
    }

    [Fact]
    public void ACheckTakesThePlaceOfTheStatusAndTheV2HashIsAdded()
    {
        var job = Torrent("verifying");
        job.Torrent!.Checking = true;
        var v2 = new string('b', 64);
        var view = TorrentInfoRules.Describe(job, "Verifying", null, Now, v2, null, true);
        Assert.Equal("Checking files", view.Status);
        Assert.Equal($"v1 {HashA} · v2 {v2}", view.InfoHash);
    }

    [Fact]
    public void TheFolderFallsBackToTheFinishedPathsFolder()
    {
        var job = Torrent("completed");
        job.DestinationDirectory = string.Empty;
        job.FinalFilePath = @"D:\Done\Film";
        Assert.Equal(@"D:\Done", TorrentInfoRules.FolderText(job));
        job.FinalFilePath = string.Empty;
        Assert.Equal(TorrentInfoRules.Dash, TorrentInfoRules.FolderText(job));
    }
}
