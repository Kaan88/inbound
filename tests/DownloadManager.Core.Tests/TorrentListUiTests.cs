using System.Globalization;
using DownloadManager.Contracts;
using DownloadManager.Desktop.WinUI;

namespace DownloadManager.Core.Tests;

/// <summary>DT-4, DT-5, DT-6 and DT-10 in the desktop window: the torrent views, their columns, the seeding goal and Copy Magnet Link.</summary>
public sealed class TorrentListUiTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    private static JobDto Torrent(string state = "downloading", bool seeding = false, int peers = 0, int seeds = 0, double ratio = 0,
        double uploadRate = 0, long uploaded = 0, long wanted = 1000, DateTimeOffset? since = null, string url = "") => new()
    {
        Id = Guid.NewGuid(),
        FileName = "Film",
        Url = url,
        State = state,
        Torrent = new TorrentDto
        {
            InfoHash = "0123456789abcdef0123456789abcdef01234567", Name = "Film", HasMetadata = true, FileCount = 1, WantedCount = 1,
            WantedBytes = wanted, Seeding = seeding, SeedingSince = since, Peers = peers, Seeds = seeds, Ratio = ratio,
            UploadRate = uploadRate, Uploaded = uploaded,
        },
    };

    private static JobDto Plain(string state = "downloading") => new() { Id = Guid.NewGuid(), FileName = "a.zip", Url = "https://example.com/a.zip", State = state };

    private static SettingsDto Settings(double ratio = 1.0, double hours = 48, bool seedAfter = true)
        => new() { TorrentSeedRatio = ratio, TorrentSeedHours = hours, TorrentSeedAfterDownload = seedAfter };

    // ---- DT-5: the views and their counts ----

    [Theory]
    [InlineData("downloading", false, "Downloading")]
    [InlineData("connecting", false, "Downloading")]
    [InlineData("verifying", false, "Downloading")]
    [InlineData("queued", false, "Downloading")]
    [InlineData("retryWaiting", false, "Downloading")]
    [InlineData("completed", true, "Seeding")]
    [InlineData("completed", false, "Stopped")]
    [InlineData("paused", false, "Stopped")]
    [InlineData("failed", false, "Stopped")]
    [InlineData("cancelled", false, "Stopped")]
    public void EachTorrentIsInOneView(string state, bool seeding, string expected)
        => Assert.Equal(expected, TorrentListRules.GroupOf(Torrent(state, seeding))?.ToString());

    [Fact]
    public void AJobThatIsntATorrentIsInNoTorrentView()
    {
        Assert.Null(TorrentListRules.GroupOf(Plain()));
        Assert.Null(TorrentListRules.GroupOf(null));
        foreach (var tag in TorrentListRules.SubTags.Append(TorrentRules.SidebarTag)) Assert.False(TorrentListRules.Matches(tag, Plain()));
    }

    [Fact]
    public void TheViewsMatchTheirTorrentsAndTorrentsMatchesAll()
    {
        var downloading = Torrent();
        var seeding = Torrent("completed", seeding: true);
        var stopped = Torrent("paused");
        Assert.True(TorrentListRules.Matches(TorrentListRules.DownloadingTag, downloading));
        Assert.False(TorrentListRules.Matches(TorrentListRules.DownloadingTag, seeding));
        Assert.True(TorrentListRules.Matches(TorrentListRules.SeedingTag, seeding));
        Assert.False(TorrentListRules.Matches(TorrentListRules.SeedingTag, stopped));
        Assert.True(TorrentListRules.Matches(TorrentListRules.StoppedTag, stopped));
        Assert.All(new[] { downloading, seeding, stopped }, job => Assert.True(TorrentListRules.Matches(TorrentRules.SidebarTag, job)));
        Assert.False(TorrentListRules.Matches("finished", seeding));
    }

    [Fact]
    public void TheCountsAddUpAndLeaveOtherJobsOut()
    {
        var counts = TorrentListRules.Count([Torrent(), Torrent("queued"), Torrent("completed", seeding: true), Torrent("completed", seeding: true),
            Torrent("paused"), Plain(), Plain("completed")]);
        Assert.Equal(new TorrentCounts(5, 2, 2, 1), counts);
        Assert.Equal(5, counts.Of(TorrentRules.SidebarTag));
        Assert.Equal(2, counts.Of(TorrentListRules.DownloadingTag));
        Assert.Equal(2, counts.Of(TorrentListRules.SeedingTag));
        Assert.Equal(1, counts.Of(TorrentListRules.StoppedTag));
    }

    [Fact]
    public void ACountIsBlankForNoneAndTheItemSaysHowMany()
    {
        Assert.Equal(string.Empty, TorrentListRules.CountText(0));
        Assert.Equal(3.ToString("N0", CultureInfo.CurrentCulture), TorrentListRules.CountText(3));
        Assert.Equal("Seeding, none", TorrentListRules.SpokenItem("Seeding", 0));
        Assert.Equal("Seeding, 1 torrent", TorrentListRules.SpokenItem("Seeding", 1));
        Assert.Equal("Torrents, 5 torrents", TorrentListRules.SpokenItem("Torrents", 5));
    }

    [Fact]
    public void TheTorrentViewsAreTorrentsAndTheThreeUnderIt()
    {
        Assert.True(TorrentListRules.IsTorrentView(TorrentRules.SidebarTag));
        Assert.All(TorrentListRules.SubTags, tag => Assert.True(TorrentListRules.IsTorrentView(tag)));
        Assert.All(TorrentListRules.SubTags, tag => Assert.StartsWith(TorrentRules.SidebarTag + ":", tag, StringComparison.Ordinal));
        Assert.False(TorrentListRules.IsSubTag(TorrentRules.SidebarTag));
        Assert.False(TorrentListRules.IsTorrentView("all"));
        Assert.False(TorrentListRules.IsTorrentView(null));
    }

    // ---- DT-6: the columns ----

    [Fact]
    public void SeedsAndPeersShowWhileItRunsAndPeersLeaveTheSeedsOut()
    {
        var running = Torrent(peers: 12, seeds: 5);
        Assert.Equal(5, TorrentListRules.Seeds(running));
        Assert.Equal(7, TorrentListRules.OtherPeers(running));
        Assert.Equal("5", TorrentListRules.CountCell(TorrentListRules.Seeds(running)));

        var seeding = Torrent("completed", seeding: true, peers: 3, seeds: 0);
        Assert.Equal(0, TorrentListRules.Seeds(seeding));
        Assert.Equal(3, TorrentListRules.OtherPeers(seeding));

        var paused = Torrent("paused", peers: 4, seeds: 2);
        Assert.Null(TorrentListRules.Seeds(paused));
        Assert.Null(TorrentListRules.OtherPeers(paused));
        Assert.Equal(string.Empty, TorrentListRules.CountCell(null));
        Assert.Null(TorrentListRules.Seeds(Plain()));

        // A count that came in out of step never goes below none.
        Assert.Equal(0, TorrentListRules.OtherPeers(Torrent(peers: 2, seeds: 5)));
    }

    [Fact]
    public void UpAndRatioShowForTorrentsOnly()
    {
        Assert.Equal($"{UiFormat.Bytes(640 * 1024)}/s", TorrentListRules.UpCell(Torrent("completed", seeding: true, uploadRate: 640 * 1024)));
        Assert.Equal(string.Empty, TorrentListRules.UpCell(Torrent("completed", seeding: true, uploadRate: 0.5)));
        Assert.Equal(string.Empty, TorrentListRules.UpCell(Torrent(uploadRate: double.NaN)));
        Assert.Null(TorrentListRules.UploadRate(Plain()));
        Assert.Equal(0.71.ToString("0.00", CultureInfo.CurrentCulture), TorrentListRules.RatioCell(Torrent(ratio: 0.71)));
        Assert.Equal(string.Empty, TorrentListRules.RatioCell(Plain()));
    }

    [Fact]
    public void TheTorrentColumnsSortAndHaveRoomInTheRowTemplate()
    {
        foreach (var key in TorrentListRules.ColumnKeys)
        {
            Assert.True(ListColumnSets.IsDownloadSort(key));
            Assert.False(ListSort.StartsAscending(key));
        }

        var source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "DownloadManager.Desktop.WinUI", "MainWindow.Lists.cs"));
        Assert.All(TorrentListRules.ColumnKeys, key => Assert.Contains($"\"{key}\" => ListSort.Order", source, StringComparison.Ordinal));
    }

    // Review finding 1: a sort by a torrent column must not hold where that column is hidden, nor after a restart there.
    [Theory]
    [InlineData("seeds")]
    [InlineData("peers")]
    [InlineData("up")]
    [InlineData("ratio")]
    public void ATorrentColumnsSortHoldsOnlyInTheTorrentViews(string key)
    {
        Assert.Equal((key, true), TorrentListRules.ShownSort(key, true, TorrentRules.SidebarTag));
        Assert.All(TorrentListRules.SubTags, tag => Assert.Equal((key, false), TorrentListRules.ShownSort(key, false, tag)));
        Assert.Equal(("added", false), TorrentListRules.ShownSort(key, true, "all"));
        Assert.Equal(("added", false), TorrentListRules.ShownSort(key, true, "finished"));
        Assert.Equal(("added", false), TorrentListRules.ShownSort(key, true, "category:video"));
        Assert.Equal(("added", false), TorrentListRules.ShownSort(key, true, null));
    }

    [Theory]
    [InlineData("name", true)]
    [InlineData("size", false)]
    [InlineData("eta", true)]
    [InlineData("rate", false)]
    [InlineData("added", true)]
    public void EveryOtherSortHoldsEverywhere(string key, bool ascending)
    {
        Assert.Equal((key, ascending), TorrentListRules.ShownSort(key, ascending, "all"));
        Assert.Equal((key, ascending), TorrentListRules.ShownSort(key, ascending, TorrentListRules.SeedingTag));
    }

    [Fact]
    public void TheListIsPutInTheShownSort()
    {
        var winUi = Path.Combine(RepoRoot(), "src", "DownloadManager.Desktop.WinUI");
        var lists = File.ReadAllText(Path.Combine(winUi, "MainWindow.Lists.cs"));
        Assert.Contains("TorrentListRules.ShownSort(_sortKey, _sortAscending, _category)", lists, StringComparison.Ordinal);
        Assert.Contains("var (key, ascending) = ShownSort;", lists, StringComparison.Ordinal);
        // The switch reads only the shown key and direction, never the chosen ones.
        var switchStart = lists.IndexOf("Sorted(IEnumerable<JobViewModel> rows, string key, bool ascending) => key switch", StringComparison.Ordinal);
        Assert.True(switchStart >= 0, "The sort switch was not found.");
        var switchBody = lists[switchStart..lists.IndexOf("};", switchStart, StringComparison.Ordinal)];
        Assert.DoesNotContain("_sortKey", switchBody, StringComparison.Ordinal);
        Assert.DoesNotContain("_sortAscending", switchBody, StringComparison.Ordinal);
        var main = File.ReadAllText(Path.Combine(winUi, "MainWindow.xaml.cs"));
        Assert.DoesNotContain("_sortKey == \"added\"", main, StringComparison.Ordinal);
    }

    // Review finding 2: every change of the shown settings takes the rows to the new seeding goal, not only a snapshot.
    [Fact]
    public void EverySettingsChangeSyncsTheSeedingGoal()
    {
        var winUi = Path.Combine(RepoRoot(), "src", "DownloadManager.Desktop.WinUI");
        var main = File.ReadAllText(Path.Combine(winUi, "MainWindow.xaml.cs"));
        Assert.Matches(new System.Text.RegularExpressions.Regex(
            @"private SettingsDto\? _settings\s*\{\s*get => _shownSettings;\s*set\s*\{\s*_shownSettings = value;\s*SyncSeedGoal\(\);\s*\}\s*\}"), main);
        foreach (var file in Directory.GetFiles(winUi, "MainWindow*.cs"))
        {
            var source = File.ReadAllText(file);
            // Only the property writes the backing field, and only SyncSeedGoal sets the rows' goal.
            Assert.Equal(file.EndsWith("MainWindow.xaml.cs", StringComparison.Ordinal) ? 1 : 0,
                System.Text.RegularExpressions.Regex.Matches(source, @"\b_shownSettings\s*=[^=]").Count);
            Assert.Equal(file.EndsWith("MainWindow.Torrents.cs", StringComparison.Ordinal) ? 1 : 0,
                System.Text.RegularExpressions.Regex.Matches(source, @"JobViewModel\.SeedGoal\s*=[^=]").Count);
        }
    }

    // Review finding 3: a hidden Seeding goal line leaves no 4 px gap (Grid RowSpacing counts an empty row).
    [Fact]
    public void TheTorrentFactsGridKeepsNoSpacingForAHiddenRow()
    {
        var xaml = System.Xml.Linq.XDocument.Load(Path.Combine(RepoRoot(), "src", "DownloadManager.Desktop.WinUI", "DownloadWindow.xaml"));
        System.Xml.Linq.XNamespace ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        System.Xml.Linq.XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var facts = xaml.Descendants(ui + "Grid").Single(grid => (string?)grid.Attribute(x + "Name") == "TorrentFacts");
        Assert.Null(facts.Attribute("RowSpacing"));
        foreach (var name in new[] { "SeedGoalHeader", "SeedGoalText" })
        {
            var line = facts.Elements(ui + "TextBlock").Single(block => (string?)block.Attribute(x + "Name") == name);
            Assert.Equal("4", (string?)line.Attribute("Grid.Row"));
            Assert.Equal("0,4,0,0", (string?)line.Attribute("Margin"));
        }
    }

    // ---- The seeding goal (DT-6's bar, DT-10's line) ----

    [Fact]
    public void TheGoalIsTheSettingsRatioAndHoursAndZeroIsNone()
    {
        Assert.Equal(new SeedGoal(1.0, 48), SeedGoal.From(Settings()));
        Assert.Equal(new SeedGoal(0, 48), SeedGoal.From(Settings(ratio: double.NaN)));
        Assert.False(SeedGoal.From(null).IsSet);
        Assert.False(SeedGoal.From(Settings(0, 0)).IsSet);
    }

    [Fact]
    public void ATorrentsOwnGoalWinsOverOptions()
    {
        var options = new SeedGoal(1.0, 48);
        var job = Torrent("completed", seeding: true, ratio: 1.0, since: Now.AddHours(-1));
        Assert.Equal(1, TorrentListRules.GoalProgress(job, options, Now)); // Options' ratio 1.0 is reached
        job.Torrent!.SeedGoal = "ratio";
        job.Torrent.SeedGoalValue = 4;
        Assert.Equal(0.25, TorrentListRules.GoalProgress(job, options, Now)!.Value, 6);
        Assert.StartsWith("Ratio 4", TorrentListRules.GoalLine(job, Settings(1, 48), Now));
        Assert.Contains("own option", TorrentListRules.GoalLine(job, Settings(1, 48), Now));
        job.Torrent.SeedGoal = "hours";
        job.Torrent.SeedGoalValue = 2;
        Assert.Equal(0.5, TorrentListRules.GoalProgress(job, options, Now)!.Value, 6);
        job.Torrent.SeedGoal = "never";
        Assert.Null(TorrentListRules.GoalProgress(job, options, Now));
        Assert.Null(TorrentListRules.TimeToGoal(job, options, Now));
        Assert.StartsWith("Never stops", TorrentListRules.GoalLine(job, Settings(1, 48), Now));
        job.Torrent.SeedGoal = "global";
        Assert.Equal(1, TorrentListRules.GoalProgress(job, options, Now));
    }

    [Fact]
    public void GoalProgressIsTheNearerOfRatioAndTime()
    {
        var goal = new SeedGoal(1.0, 48);
        // Ratio 0.25 of 1.0, 24 hours of 48: half way by time.
        var job = Torrent("completed", seeding: true, ratio: 0.25, since: Now.AddHours(-24));
        Assert.Equal(0.5, TorrentListRules.GoalProgress(job, goal, Now)!.Value, 6);
        // Ratio 0.8 of 1.0, 2 hours of 48: most of the way by ratio.
        job = Torrent("completed", seeding: true, ratio: 0.8, since: Now.AddHours(-2));
        Assert.Equal(0.8, TorrentListRules.GoalProgress(job, goal, Now)!.Value, 6);
        // Past the goal stays full.
        job = Torrent("completed", seeding: true, ratio: 3, since: Now.AddHours(-2));
        Assert.Equal(1, TorrentListRules.GoalProgress(job, goal, Now));
        // Only a ratio, or only the time.
        Assert.Equal(0.25, TorrentListRules.GoalProgress(Torrent("completed", seeding: true, ratio: 0.5, since: Now.AddHours(-40)), new SeedGoal(2, 0), Now)!.Value, 6);
        Assert.Equal(0.5, TorrentListRules.GoalProgress(Torrent("completed", seeding: true, ratio: 5, since: Now.AddHours(-6)), new SeedGoal(0, 12), Now)!.Value, 6);
    }

    [Fact]
    public void ThereIsNoGoalProgressWhileItDoesntSeedOrWithNoGoal()
    {
        Assert.Null(TorrentListRules.GoalProgress(Torrent("completed", ratio: 0.5), new SeedGoal(1, 48), Now));
        Assert.Null(TorrentListRules.GoalProgress(Torrent(), new SeedGoal(1, 48), Now));
        Assert.Null(TorrentListRules.GoalProgress(Torrent("completed", seeding: true), SeedGoal.None, Now));
        Assert.Null(TorrentListRules.TimeToGoal(Torrent("completed", seeding: true), SeedGoal.None, Now));
    }

    [Fact]
    public void TimeToGoalIsTheSoonerOfTimeLeftAndTheRatioAtTheCurrentRate()
    {
        var goal = new SeedGoal(1.0, 48);
        // 10 hours left by time; 600 of 1000 bytes still to send at 1 B/s is 10 minutes.
        var job = Torrent("completed", seeding: true, uploaded: 400, wanted: 1000, uploadRate: 1, since: Now.AddHours(-38));
        Assert.Equal(TimeSpan.FromMinutes(10), TorrentListRules.TimeToGoal(job, goal, Now));
        // Nothing sent now: the time limit alone says it.
        job = Torrent("completed", seeding: true, uploaded: 400, wanted: 1000, uploadRate: 0, since: Now.AddHours(-38));
        Assert.Equal(TimeSpan.FromHours(10), TorrentListRules.TimeToGoal(job, goal, Now));
        // The ratio reached, or the time up: zero.
        Assert.Equal(TimeSpan.Zero, TorrentListRules.TimeToGoal(Torrent("completed", seeding: true, uploaded: 1000, wanted: 1000, since: Now), goal, Now));
        Assert.Equal(TimeSpan.Zero, TorrentListRules.TimeToGoal(Torrent("completed", seeding: true, since: Now.AddHours(-50)), goal, Now));
        // Only a ratio, and nothing sent now: nothing can be said.
        Assert.Null(TorrentListRules.TimeToGoal(Torrent("completed", seeding: true, uploaded: 0, wanted: 1000, since: Now), new SeedGoal(1, 0), Now));
    }

    [Fact]
    public void TheGoalReadsAsOptionsSetsIt()
    {
        var one = 1.0.ToString("0.00", CultureInfo.CurrentCulture);
        Assert.Equal($"Ratio {one} or 48 hours, whichever comes first", TorrentListRules.GoalText(new SeedGoal(1, 48)));
        Assert.Equal($"Ratio {one}", TorrentListRules.GoalText(new SeedGoal(1, 0)));
        Assert.Equal("1 hour", TorrentListRules.GoalText(new SeedGoal(0, 1)));
        Assert.Equal($"{1.5.ToString("0.##", CultureInfo.CurrentCulture)} hours", TorrentListRules.GoalText(new SeedGoal(0, 1.5)));
        Assert.Null(TorrentListRules.GoalText(SeedGoal.None));
    }

    [Fact]
    public void TheDetailsGoalLineSaysTheTimeToItWhileItSeeds()
    {
        var one = 1.0.ToString("0.00", CultureInfo.CurrentCulture);
        var goal = $"Ratio {one} or 48 hours, whichever comes first";
        var seeding = Torrent("completed", seeding: true, uploaded: 0, wanted: 1000, since: Now.AddHours(-47));
        Assert.Equal($"{goal} · about 1h 0m left", TorrentListRules.GoalLine(seeding, Settings(), Now));
        var reached = Torrent("completed", seeding: true, uploaded: 2000, wanted: 1000, since: Now);
        Assert.Equal($"{goal} · reached", TorrentListRules.GoalLine(reached, Settings(), Now));
        Assert.Equal(goal, TorrentListRules.GoalLine(Torrent(), Settings(), Now));
        Assert.Equal($"{goal} · seeding after a download is off", TorrentListRules.GoalLine(Torrent(), Settings(seedAfter: false), Now));
        // A finished torrent can still be seeded by hand, to the same goal.
        Assert.Equal(goal, TorrentListRules.GoalLine(Torrent("completed"), Settings(seedAfter: false), Now));
        Assert.Null(TorrentListRules.GoalLine(Torrent(), null, Now));
        Assert.Null(TorrentListRules.GoalLine(Plain(), Settings(), Now));
    }

    [Fact]
    public void TheRatioLineSaysWhatWasSent()
        => Assert.Equal($"{0.71.ToString("0.00", CultureInfo.CurrentCulture)} · uploaded {UiFormat.Bytes(204 * 1024 * 1024)}",
            TorrentListRules.RatioLine(new TorrentDto { Ratio = 0.71, Uploaded = 204 * 1024 * 1024 }));

    // ---- DT-4: Copy Magnet Link ----

    [Fact]
    public void ATorrentCopiesTheMagnetItWasAddedWith()
    {
        const string magnet = "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&dn=Film&tr=udp%3A%2F%2Ftracker.example%3A80";
        Assert.Equal(magnet, TorrentListRules.MagnetOf(Torrent(url: magnet)));
        // A job that holds no magnet copies the short one.
        Assert.Equal("magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567", TorrentListRules.MagnetOf(Torrent(url: "")));
        Assert.Null(TorrentListRules.MagnetOf(Plain()));
    }

    [Fact]
    public void SeveralTorrentsCopyOneMagnetALineAndAnyOtherJobCopiesNothing()
    {
        var first = Torrent(url: "magnet:?xt=urn:btih:aaaa");
        var second = Torrent(url: "magnet:?xt=urn:btih:bbbb");
        Assert.Equal($"magnet:?xt=urn:btih:aaaa{Environment.NewLine}magnet:?xt=urn:btih:bbbb", TorrentListRules.MagnetsToCopy([first, second]));
        Assert.Null(TorrentListRules.MagnetsToCopy([first, Plain()]));
        Assert.Null(TorrentListRules.MagnetsToCopy([]));
        Assert.Equal("Copy Magnet Link", TorrentListRules.CopyItemText(1));
        Assert.Equal("Copy Magnet Links", TorrentListRules.CopyItemText(2));
        Assert.Equal("Copied the magnet link of 'Film'.", TorrentListRules.CopiedSaid("Film", 1));
        Assert.Equal("Copied 2 magnet links.", TorrentListRules.CopiedSaid(null, 2));
    }

    [Fact]
    public void TheWindowWiresTheViewsTheMenuItemAndTheDetailsLines()
    {
        var winUi = Path.Combine(RepoRoot(), "src", "DownloadManager.Desktop.WinUI");
        var main = File.ReadAllText(Path.Combine(winUi, "MainWindow.xaml.cs"));
        Assert.Contains("TorrentListRules.IsSubTag(_category) => job.IsTorrent && TorrentListRules.Matches(_category, job.Dto)", main, StringComparison.Ordinal);
        Assert.Contains("CreateContextItem(\"Copy Magnet Link\", MenuCopyMagnet_Click)", main, StringComparison.Ordinal);
        Assert.Contains("TorrentListRules.IsTorrentView(_uiState.Category)", main, StringComparison.Ordinal);
        var details = File.ReadAllText(Path.Combine(winUi, "DownloadWindow.xaml"));
        Assert.Contains("x:Name=\"SeedGoalText\"", details, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"TorrentRatioText\"", details, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "DownloadManager.slnx"))) folder = folder.Parent;
        return folder?.FullName ?? throw new InvalidOperationException("The repository wasn't found.");
    }
}
