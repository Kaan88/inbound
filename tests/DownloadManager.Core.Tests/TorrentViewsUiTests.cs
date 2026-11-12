using DownloadManager.Contracts;
using DownloadManager.Desktop.WinUI;

namespace DownloadManager.Core.Tests;

/// <summary>T7: the extra views under Torrents (Active, Stalled, Checking, Waiting for files, Completed, Errored).</summary>
public sealed class TorrentViewsUiTests
{
    private static JobDto Torrent(string state = "downloading", double? down = null, double up = 0, bool seeding = false,
        bool checking = false, bool awaiting = false, bool metadata = true, int peers = 0, string? failure = null, int seeds = 0, DateTimeOffset? started = null) => new()
    {
        StartedAt = started,
        Id = Guid.NewGuid(),
        FileName = "Film",
        State = state,
        BytesPerSecond = down,
        Failure = failure is null ? null : new FailureDto { Kind = failure, Message = "x" },
        Torrent = new TorrentDto
        {
            InfoHash = "0123456789abcdef0123456789abcdef01234567", Name = "Film", HasMetadata = metadata, FileCount = 2, WantedCount = 2,
            Seeding = seeding, UploadRate = up, Checking = checking, AwaitingFileChoice = awaiting, Peers = peers, Seeds = seeds,
        },
    };

    private static JobDto Plain(string state = "failed") => new() { Id = Guid.NewGuid(), FileName = "a.zip", State = state, BytesPerSecond = 5000 };

    // ---- Active ----

    [Theory]
    [InlineData("downloading", 2048d, 0d, false, true)] // receives
    [InlineData("downloading", 0d, 500d, false, true)] // receives nothing but sends
    [InlineData("completed", null, 500d, true, true)] // seeds and sends
    [InlineData("completed", null, 0d, true, false)] // seeds, nobody takes anything
    [InlineData("downloading", 0.5d, 0d, false, false)] // under 1 B/s is nothing
    [InlineData("paused", 2048d, 500d, false, false)] // a stale figure on a stopped torrent
    [InlineData("completed", null, 500d, false, false)] // finished, not seeding
    [InlineData("queued", null, 0d, false, false)]
    public void ActiveIsATorrentMovingDataNow(string state, double? down, double up, bool seeding, bool expected)
        => Assert.Equal(expected, TorrentListRules.Matches(TorrentListRules.ActiveTag, Torrent(state, down, up, seeding)));

    // ---- Stalled ----

    [Theory]
    [InlineData("downloading", null, false, false, true)]
    [InlineData("connecting", 0d, false, false, true)]
    [InlineData("downloading", 0.9d, false, false, true)]
    [InlineData("downloading", 1d, false, false, false)]
    [InlineData("downloading", null, true, false, false)] // checking is its own view
    [InlineData("downloading", null, false, true, false)] // so is the file choice
    [InlineData("queued", null, false, false, false)] // waiting its turn is not stalled
    [InlineData("paused", null, false, false, false)]
    [InlineData("retryWaiting", null, false, false, false)]
    [InlineData("completed", null, false, false, false)]
    public void StalledIsADownloadThatShouldRunAndReceivesNothing(string state, double? down, bool checking, bool awaiting, bool expected)
        => Assert.Equal(expected, TorrentListRules.IsStalled(Torrent(state, down, checking: checking, awaiting: awaiting)));

    [Fact]
    public void AMagnetWithoutItsFileListAndNoPeersIsStalled()
    {
        // The file choice needs the list, so "awaiting" without metadata doesn't take it out of Stalled.
        Assert.True(TorrentListRules.IsStalled(Torrent("connecting", metadata: false, awaiting: true)));
        Assert.False(TorrentListRules.IsStalled(Torrent("connecting", down: 3000, metadata: false)));
    }

    [Fact]
    public void ATorrentWithAPeerIsNotStalledSoARateDipDoesNotMoveIt()
    {
        Assert.False(TorrentListRules.IsStalled(Torrent("downloading", down: 0, peers: 1)));
        Assert.False(TorrentListRules.IsStalled(Torrent("downloading", down: 0, seeds: 2)));
        // One that sends has a peer, so it is Active and not Stalled.
        var sending = Torrent("downloading", down: 0, up: 300, peers: 1);
        Assert.True(TorrentListRules.IsTransferring(sending));
        Assert.False(TorrentListRules.IsStalled(sending));
    }

    [Fact]
    public void ATorrentIsStalledOnlyAfterItsFirstMinute()
    {
        var now = new DateTimeOffset(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
        Assert.False(TorrentListRules.IsStalled(Torrent("connecting", started: now.AddSeconds(-5)), now));
        Assert.False(TorrentListRules.IsStalled(Torrent("connecting", started: now.AddSeconds(-59)), now));
        Assert.True(TorrentListRules.IsStalled(Torrent("connecting", started: now.AddSeconds(-60)), now));
        Assert.True(TorrentListRules.IsStalled(Torrent("downloading", started: now.AddHours(-2)), now));
        Assert.False(TorrentListRules.Matches(TorrentListRules.StalledTag, Torrent("connecting", started: now.AddSeconds(-5)), now));
        Assert.Equal(0, TorrentListRules.CountExtras([Torrent("connecting", started: now.AddSeconds(-5))], now).Stalled);
        Assert.Equal(1, TorrentListRules.CountExtras([Torrent("connecting", started: now.AddMinutes(-5))], now).Stalled);
    }

    // ---- Checking, Waiting for files, Completed, Errored ----

    [Fact]
    public void CheckingFollowsTheWorkersCheck()
    {
        Assert.True(TorrentListRules.Matches(TorrentListRules.CheckingTag, Torrent(checking: true)));
        Assert.True(TorrentListRules.Matches(TorrentListRules.CheckingTag, Torrent("completed", seeding: true, checking: true)));
        Assert.False(TorrentListRules.Matches(TorrentListRules.CheckingTag, Torrent()));
    }

    [Theory]
    [InlineData("downloading", true, true, true)]
    [InlineData("paused", true, true, true)]
    [InlineData("downloading", true, false, false)] // no file list yet
    [InlineData("downloading", false, true, false)]
    [InlineData("completed", true, true, false)]
    [InlineData("cancelled", true, true, false)]
    public void WaitingForFilesNeedsTheListAndAnOpenChoice(string state, bool awaiting, bool metadata, bool expected)
        => Assert.Equal(expected, TorrentListRules.Matches(TorrentListRules.FilesTag, Torrent(state, awaiting: awaiting, metadata: metadata)));

    [Fact]
    public void CompletedIsFinishedAndNotSeeding()
    {
        Assert.True(TorrentListRules.Matches(TorrentListRules.CompletedTag, Torrent("completed")));
        Assert.False(TorrentListRules.Matches(TorrentListRules.CompletedTag, Torrent("completed", seeding: true)));
        Assert.False(TorrentListRules.Matches(TorrentListRules.CompletedTag, Torrent("paused")));
    }

    [Theory]
    [InlineData("failed", null, true)]
    [InlineData("paused", "diskFull", true)]
    [InlineData("paused", null, false)] // paused by the user
    [InlineData("paused", "network", false)]
    [InlineData("downloading", null, false)]
    public void ErroredIsAFailureOrAFullDisk(string state, string? failure, bool expected)
        => Assert.Equal(expected, TorrentListRules.Matches(TorrentListRules.ErroredTag, Torrent(state, failure: failure)));

    [Fact]
    public void AJobThatIsntATorrentIsInNoExtraView()
    {
        foreach (var tag in TorrentListRules.ExtraTags)
        {
            Assert.False(TorrentListRules.Matches(tag, Plain()));
            Assert.False(TorrentListRules.Matches(tag, Plain("downloading")));
        }

        Assert.Equal(default, TorrentListRules.CountExtras([Plain(), Plain("downloading"), Plain("completed")]));
    }

    [Fact]
    public void TheExtraCountsAddUp()
    {
        var counts = TorrentListRules.CountExtras([
            Torrent(down: 4096, up: 100), // active
            Torrent(), // stalled
            Torrent(down: 0, up: 300, peers: 1), // active, not stalled: sends to a peer, receives nothing
            Torrent(checking: true), // checking
            Torrent("paused", awaiting: true), // waiting for files
            Torrent("completed"), // completed
            Torrent("completed", up: 50, seeding: true), // active
            Torrent("failed"), // errored
            Torrent("paused", failure: "diskFull"), // errored
        ]);
        Assert.Equal(new TorrentExtraCounts(Active: 3, Stalled: 1, Checking: 1, Files: 1, Completed: 1, Errored: 2), counts);
        Assert.Equal(3, counts.Of(TorrentListRules.ActiveTag));
        Assert.Equal(1, counts.Of(TorrentListRules.StalledTag));
        Assert.Equal(1, counts.Of(TorrentListRules.CheckingTag));
        Assert.Equal(1, counts.Of(TorrentListRules.FilesTag));
        Assert.Equal(1, counts.Of(TorrentListRules.CompletedTag));
        Assert.Equal(2, counts.Of(TorrentListRules.ErroredTag));
        Assert.Equal(0, counts.Of(TorrentListRules.SeedingTag));
    }

    // ---- In the sidebar ----

    [Fact]
    public void TheExtraViewsComeAfterTheMainThreeAndAreTorrentViews()
    {
        Assert.Equal(TorrentListRules.MainTags.Concat(TorrentListRules.ExtraTags), TorrentListRules.SubTags);
        Assert.Equal(6, TorrentListRules.ExtraTags.Count);
        Assert.All(TorrentListRules.ExtraTags, tag =>
        {
            Assert.True(TorrentListRules.IsSubTag(tag));
            Assert.True(TorrentListRules.IsExtraTag(tag));
            Assert.True(TorrentListRules.IsTorrentView(tag));
        });
        Assert.All(TorrentListRules.MainTags, tag => Assert.False(TorrentListRules.IsExtraTag(tag)));
        Assert.False(TorrentListRules.IsExtraTag(null));
        Assert.False(TorrentListRules.IsSubTag(null));
    }

    [Fact]
    public void AnExtraViewShowsOnlyWithTorrentsOrWhileItIsShown()
    {
        // The main three always show, empty or not.
        Assert.True(TorrentListRules.ViewShows(TorrentListRules.StoppedTag, 0, "all"));
        Assert.False(TorrentListRules.ViewShows(TorrentListRules.StalledTag, 0, "all"));
        Assert.True(TorrentListRules.ViewShows(TorrentListRules.StalledTag, 2, "all"));
        // Emptied while shown, it stays until another view is chosen; a saved view about to come back shows too.
        Assert.True(TorrentListRules.ViewShows(TorrentListRules.StalledTag, 0, TorrentListRules.StalledTag));
        Assert.True(TorrentListRules.ViewShows(TorrentListRules.ErroredTag, 0, "all", TorrentListRules.ErroredTag));
        Assert.False(TorrentListRules.ViewShows(TorrentListRules.ErroredTag, 0, "all", TorrentListRules.StalledTag));
    }

    [Fact]
    public void ASortBySwarmColumnsHoldsInTheExtraViews()
        => Assert.Equal(("peers", true), TorrentListRules.ShownSort("peers", true, TorrentListRules.ActiveTag));
}
