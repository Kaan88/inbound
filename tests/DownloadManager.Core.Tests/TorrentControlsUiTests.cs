using DownloadManager.Contracts;
using DownloadManager.Desktop.WinUI;

namespace DownloadManager.Core.Tests;

/// <summary>
/// T3 in the desktop window: file priorities, Force recheck and Announce now, a torrent's own Options tab, the Overview's
/// facts, the trackers' next announce, the DHT chip, watching, Options › Torrents' peer settings, the Add dialog's free
/// space and the gating of an older Host (<see cref="TorrentControlRules"/>, <see cref="TorrentPriorities"/>).
/// </summary>
public sealed class TorrentControlsUiTests
{
    private const string HashA = "0123456789abcdef0123456789abcdef01234567";

    private static JobDto Torrent(string state = "downloading", bool metadata = true, bool checking = false, bool seeding = false,
        long received = 100) => new()
    {
        Id = Guid.NewGuid(),
        FileName = "Film",
        State = state,
        ReceivedBytes = received,
        UpdatedAt = DateTimeOffset.UtcNow,
        Torrent = new TorrentDto
        {
            InfoHash = HashA, Name = "Film", HasMetadata = metadata, FileCount = 1, WantedCount = 1, Checking = checking, Seeding = seeding,
        },
    };

    // ---- Gating ----

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, null, false)]
    [InlineData(false, true, false)]
    public void ControlsShowOnlyWhenTheHostHasTorrentsAndAnsweredTheProbe(bool offered, bool? probe, bool expected)
        => Assert.Equal(expected, TorrentControlRules.Supported(offered, probe));

    [Fact]
    public void TheProbeAnswerTellsAnOlderHostApart()
    {
        Assert.True(TorrentControlRules.ProbeAnswer(TorrentMessageTypes.OptionsResult, false));
        Assert.False(TorrentControlRules.ProbeAnswer("host.result", true));
        Assert.Null(TorrentControlRules.ProbeAnswer("host.result", false));
        Assert.Null(TorrentControlRules.ProbeAnswer(null, false));
    }

    // ---- Priorities ----

    [Fact]
    public void AFilesPriorityComesFromTheHostAndAnOlderHostIsNormal()
    {
        Assert.Equal("high", TorrentPriorities.Of(new TorrentFileDto { Wanted = true, Priority = "high" }));
        Assert.Equal("normal", TorrentPriorities.Of(new TorrentFileDto { Wanted = true }));
        Assert.Equal("normal", TorrentPriorities.Of(new TorrentFileDto { Wanted = true, Priority = "urgent" }));
        Assert.Equal("normal", TorrentPriorities.Of(new TorrentFileDto { Wanted = true, Priority = "skip" }));
        Assert.Equal("skip", TorrentPriorities.Of(new TorrentFileDto { Wanted = false, Priority = "high" }));
        Assert.Equal(["high", "normal", "low", "skip"], TorrentPriorities.Menu);
    }

    [Fact]
    public void AFolderShowsItsOnePriorityOrMixed()
    {
        Assert.Equal("High", TorrentPriorities.FolderText(["high", "high"]));
        Assert.Equal("mixed", TorrentPriorities.FolderText(["high", "low"]));
        Assert.Equal(string.Empty, TorrentPriorities.FolderText([]));
    }

    [Fact]
    public void OnlyChosenFilesThatArentNormalAreSent()
    {
        Assert.Null(TorrentPriorities.ToSend(null));
        Assert.Null(TorrentPriorities.ToSend(new Dictionary<int, string> { [0] = "normal", [1] = "skip" }));
        var sent = TorrentPriorities.ToSend(new Dictionary<int, string> { [3] = "low", [1] = "high", [2] = "normal", [4] = "odd" })!;
        Assert.Equal([1, 3], sent.Select(item => item.Index));
        Assert.Equal(["high", "low"], sent.Select(item => item.Priority));
    }

    private static RemoteFileDto File(string id, string path, long size = 100) => new()
    {
        FileId = id, RelativePath = path, Size = size, Selectable = true,
    };

    private static RemoteFileNode Find(RemoteFileTree tree, string name) => tree.Visible().First(node => node.Name == name);

    [Fact]
    public void TheTreeSetsAFolderAtOnceAndSaysMixedWhenItsFilesDiffer()
    {
        var tree = RemoteFileTree.Build([File("0", "Show/a.mkv"), File("1", "Show/b.mkv"), File("2", "c.nfo")]);
        tree.SelectAll();
        var folder = Find(tree, "Show");
        Assert.Equal("Normal", RemoteFileTree.PriorityText(folder));
        Assert.Empty(tree.Priorities);

        tree.SetPriority(folder, TorrentPriorities.High);
        Assert.Equal("High", RemoteFileTree.PriorityText(folder));
        Assert.Equal("High", RemoteFileTree.PriorityText(Find(tree, "a.mkv")));
        Assert.Equal(new Dictionary<string, string> { ["0"] = "high", ["1"] = "high" }, tree.Priorities);

        tree.SetPriority(Find(tree, "b.mkv"), TorrentPriorities.Low);
        Assert.Equal("mixed", RemoteFileTree.PriorityText(folder));

        // Skip leaves the file out; a folder partly chosen is mixed, and the skipped file shows Skip.
        tree.SetPriority(Find(tree, "b.mkv"), TorrentPriorities.Skip);
        Assert.Equal("Skip", RemoteFileTree.PriorityText(Find(tree, "b.mkv")));
        Assert.Equal("mixed", RemoteFileTree.PriorityText(folder));
        Assert.Equal(2, tree.ChosenCount);
        Assert.Equal(new Dictionary<string, string> { ["0"] = "high" }, tree.Priorities);

        // Choosing a skipped file again chooses it at that priority.
        tree.SetPriority(Find(tree, "b.mkv"), TorrentPriorities.High);
        Assert.Equal("High", RemoteFileTree.PriorityText(folder));
        Assert.Equal(3, tree.ChosenCount);

        tree.SetPriority(folder, TorrentPriorities.Skip);
        Assert.Equal("Skip", RemoteFileTree.PriorityText(folder));
        Assert.Equal(1, tree.ChosenCount);
        Assert.Empty(tree.Priorities);
    }

    [Fact]
    public void PrioritiesByIdAreAppliedAndUnknownOnesIgnored()
    {
        var tree = RemoteFileTree.Build([File("0", "a.mkv"), File("1", "b.mkv"), File("2", "c.mkv")]);
        tree.SelectAll();
        tree.SetPriorities(new Dictionary<string, string> { ["0"] = "low", ["1"] = "skip", ["2"] = "nonsense", ["9"] = "high" });
        Assert.Equal("Low", RemoteFileTree.PriorityText(Find(tree, "a.mkv")));
        Assert.Equal("Skip", RemoteFileTree.PriorityText(Find(tree, "b.mkv")));
        Assert.Equal("Normal", RemoteFileTree.PriorityText(Find(tree, "c.mkv")));
        Assert.Equal(new Dictionary<string, string> { ["0"] = "low" }, tree.Priorities);
    }

    private static TorrentInspectResult Inspected(long? free = null, string? destination = null, params long[] sizes) => new()
    {
        Ok = true, Kind = "torrentFile", InfoHash = HashA, Name = "Sintel", TotalBytes = sizes.Sum(),
        Files = sizes.Select((size, index) => new TorrentFileDto { Index = index, Path = $"Sintel/part{index}.mkv", Size = size, Wanted = true }).ToList(),
        FreeBytes = free, Destination = destination,
    };

    [Fact]
    public void TheAddDialogSendsTheChosenFilesPriorities()
    {
        var lines = new TorrentAddLines(TorrentLineRoute.ThisPc, true, null);
        lines.Add([@"C:\t\sintel.torrent"], fromPaste: false);
        var line = lines.Lines[0];
        lines.Inspected(line, Inspected(null, null, 1024, 2048, 4096));

        lines.SetFileChoice(line, new RemoteFileChoice(false, ["0", "2"]), new Dictionary<string, string> { ["2"] = "high", ["0"] = "normal" });
        var pick = Assert.Single(lines.Picks());
        Assert.Equal([0, 2], pick.FileIndexes);
        var priority = Assert.Single(pick.Priorities!);
        Assert.Equal(2, priority.Index);
        Assert.Equal("high", priority.Priority);

        lines.SetFileChoice(line, new RemoteFileChoice(true, []));
        Assert.Null(Assert.Single(lines.Picks()).Priorities);
    }

    [Fact]
    public void TheAddDialogSaysTheFreeSpaceAndWhenTheChosenFilesDontFit()
    {
        var lines = new TorrentAddLines(TorrentLineRoute.ThisPc, true, null);
        lines.Add([@"C:\t\sintel.torrent"], fromPaste: false);
        var line = lines.Lines[0];
        Assert.Null(TorrentAddLines.SpaceOf(line));

        lines.Inspected(line, Inspected(3000, @"D:\Downloads", 1024, 2048, 4096));
        var (words, problem) = TorrentAddLines.SpaceOf(line)!.Value;
        Assert.True(problem);
        Assert.StartsWith("Not enough space", words);
        Assert.EndsWith(@"is free in D:\Downloads.", words);

        // Fewer files fit.
        lines.SetFileChoice(line, new RemoteFileChoice(false, ["1"]));
        (words, problem) = TorrentAddLines.SpaceOf(line)!.Value;
        Assert.False(problem);
        Assert.EndsWith(@"free in D:\Downloads", words);

        // An older Host doesn't measure it.
        var older = new TorrentAddLines(TorrentLineRoute.ThisPc, true, null);
        older.Add([@"C:\t\sintel.torrent"], fromPaste: false);
        older.Inspected(older.Lines[0], Inspected(null, null, 10));
        Assert.Null(TorrentAddLines.SpaceOf(older.Lines[0]));
    }

    [Fact]
    public void SpaceWordsFollowWhatTheHostMeasured()
    {
        Assert.Null(TorrentControlRules.SpaceWords(null, 10, "D:\\"));
        Assert.Null(TorrentControlRules.SpaceWords(-1, 10, "D:\\"));
        Assert.Equal((UiFormat.Bytes(5000) + " free", false), TorrentControlRules.SpaceWords(5000, null, null));
        Assert.True(TorrentControlRules.SpaceWords(10, 11, null)!.Value.IsProblem);
    }

    // ---- Force recheck and Announce now ----

    [Fact]
    public void RecheckAppliesToATorrentWithSomethingOnDisk()
    {
        Assert.True(TorrentControlRules.CanRecheck(Torrent("paused")));
        Assert.True(TorrentControlRules.CanRecheck(Torrent("downloading")));
        Assert.False(TorrentControlRules.CanRecheck(Torrent("downloading", received: 0)));
        Assert.False(TorrentControlRules.CanRecheck(Torrent("downloading", metadata: false)));
        Assert.False(TorrentControlRules.CanRecheck(Torrent("downloading", checking: true)));
        Assert.False(TorrentControlRules.CanRecheck(Torrent("cancelled")));
        Assert.False(TorrentControlRules.CanRecheck(Torrent("completed")));
        Assert.True(TorrentControlRules.CanRecheck(Torrent("completed", seeding: true)));
        Assert.False(TorrentControlRules.CanRecheck(null));
        Assert.False(TorrentControlRules.CanRecheck(new JobDto { Id = Guid.NewGuid(), FileName = "a.zip", State = "paused", ReceivedBytes = 5 }));
    }

    [Fact]
    public void AnnounceAppliesWhileTheTorrentRuns()
    {
        Assert.True(TorrentControlRules.CanAnnounce(Torrent("downloading")));
        Assert.True(TorrentControlRules.CanAnnounce(Torrent("connecting", metadata: false)));
        Assert.True(TorrentControlRules.CanAnnounce(Torrent("completed", seeding: true)));
        Assert.False(TorrentControlRules.CanAnnounce(Torrent("paused")));
        Assert.False(TorrentControlRules.CanAnnounce(Torrent("completed")));
        Assert.False(TorrentControlRules.CanAnnounce(null));
    }

    [Fact]
    public void TargetsAreTheSelectedTorrentsTheCommandAppliesTo()
    {
        var running = Torrent("downloading");
        var paused = Torrent("paused");
        Assert.Equal([running.Id], TorrentControlRules.Targets([running, paused], TorrentControlRules.CanAnnounce));
    }

    [Fact]
    public void TheStatusBarSaysWhatStarted()
    {
        Assert.Equal("Film: checking its files.", TorrentControlRules.Said(true, "Film", 1, 0, null));
        Assert.Equal("Checking the files of 3 torrents.", TorrentControlRules.Said(true, null, 3, 0, null));
        Assert.Equal("Film: asking the trackers for peers.", TorrentControlRules.Said(false, "Film", 1, 0, null));
        Assert.Equal("Film: the check didn't start. It is busy.", TorrentControlRules.Said(true, "Film", 0, 1, " It is busy. "));
        Assert.Equal("The announce didn't start.", TorrentControlRules.Said(false, null, 0, 2, null));
        Assert.Equal("2 started, 1 didn't. Nope.", TorrentControlRules.Said(false, null, 2, 1, "Nope."));
    }

    // ---- Options tab ----

    [Fact]
    public void TheOptionsTabShowsWhatTheHostHas()
    {
        var form = TorrentControlRules.FormOf(new TorrentOptionsDto { DownloadLimit = 512 * 1024, UploadLimit = 0, SeedGoal = "ratio", SeedGoalValue = 2, Sequential = true });
        Assert.Equal(512, form.DownloadKilobytes);
        Assert.True(double.IsNaN(form.UploadKilobytes));
        Assert.Equal("ratio", form.Goal);
        Assert.Equal(2, form.GoalValue);
        Assert.True(form.Sequential);

        var empty = TorrentControlRules.FormOf(null);
        Assert.Equal("global", empty.Goal);
        Assert.True(double.IsNaN(empty.GoalValue));
        Assert.False(empty.Sequential);

        Assert.Equal("global", TorrentControlRules.FormOf(new TorrentOptionsDto { SeedGoal = "forever" }).Goal);
        Assert.True(double.IsNaN(TorrentControlRules.FormOf(new TorrentOptionsDto { SeedGoal = "never", SeedGoalValue = 3 }).GoalValue));
    }

    [Theory]
    [InlineData(double.NaN, double.NaN, "global", double.NaN, null)]
    [InlineData(1.0, 2097151.0, "never", double.NaN, null)]
    [InlineData(0.0, double.NaN, "global", double.NaN, "Enter a limit from 1 to 2,097,151 KB/s, or leave it empty for none.")]
    [InlineData(double.NaN, 0.4, "global", double.NaN, "Enter a limit from 1 to 2,097,151 KB/s, or leave it empty for none.")]
    [InlineData(2097152.0, double.NaN, "global", double.NaN, "Enter a limit from 1 to 2,097,151 KB/s, or leave it empty for none.")]
    [InlineData(double.NaN, -1.0, "global", double.NaN, "Enter a limit from 1 to 2,097,151 KB/s, or leave it empty for none.")]
    [InlineData(double.NaN, double.NaN, "ratio", 0.01, null)]
    [InlineData(double.NaN, double.NaN, "ratio", 100.0, null)]
    [InlineData(double.NaN, double.NaN, "ratio", 0.0, "Enter a seeding ratio from 0.01 to 100.")]
    [InlineData(double.NaN, double.NaN, "ratio", double.NaN, "Enter a seeding ratio from 0.01 to 100.")]
    [InlineData(double.NaN, double.NaN, "hours", 0.1, null)]
    [InlineData(double.NaN, double.NaN, "hours", 8761.0, "Enter seeding hours from 0.1 to 8760.")]
    public void TheOptionsTabFollowsTheHostsRules(double down, double up, string goal, double value, string? expected)
        => Assert.Equal(expected, TorrentControlRules.Problem(new TorrentControlRules.OptionsForm(down, up, goal, value, false)));

    [Fact]
    public void TheOptionsTabSendsEveryField()
    {
        var id = Guid.NewGuid();
        var dto = TorrentControlRules.ToDto(id, new TorrentControlRules.OptionsForm(100, double.NaN, "hours", 12, true));
        Assert.Equal(id, dto.JobId);
        Assert.Equal(102400, dto.DownloadLimit);
        Assert.Equal(0, dto.UploadLimit);
        Assert.Equal("hours", dto.SeedGoal);
        Assert.Equal(12, dto.SeedGoalValue);
        Assert.True(dto.Sequential);

        var global = TorrentControlRules.ToDto(id, new TorrentControlRules.OptionsForm(double.NaN, 0, "global", 5, false));
        Assert.Null(global.SeedGoalValue);
        Assert.Equal(int.MaxValue, TorrentControlRules.LimitBytes(TorrentControlRules.MostLimitKilobytes + 1));
    }

    [Fact]
    public void SavedOptionsSayWhatTheHostAdded()
    {
        Assert.Equal("Saved. The torrent uses them now.", TorrentControlRules.SavedText(true, null));
        Assert.Equal("Saved. They apply when the torrent starts.", TorrentControlRules.SavedText(false, " "));
        // An older torrent program: saved, but the download order isn't taken; the Host's words, not "they apply".
        const string older = "The torrent program in this installation is too old for that. Update the app.";
        Assert.Equal($"Saved. {older}", TorrentControlRules.SavedText(false, older));
    }

    [Theory]
    // The tab opens on a Host with the controls: read.
    [InlineData(true, true, true, false, false, false, true)]
    // Loaded, being read, or a read already failed: not again on each update.
    [InlineData(true, true, true, true, false, false, false)]
    [InlineData(true, true, true, false, true, false, false)]
    [InlineData(true, true, true, false, false, true, false)]
    // Hidden, an older (or not yet asked) Host, or no connection: nothing.
    [InlineData(false, true, true, false, false, false, false)]
    [InlineData(true, false, true, false, false, false, false)]
    [InlineData(true, true, false, false, false, false, false)]
    public void TheOptionsTabReadsWhenItShowsAndIsntLoaded(bool shown, bool controls, bool connected, bool loaded, bool busy, bool tried, bool expected)
        => Assert.Equal(expected, TorrentControlRules.OptionsReadDue(shown, controls, connected, loaded, busy, tried));

    [Fact]
    public void TheV2HashIsKeptBetweenReads()
    {
        var v2 = new string('b', 64);
        Assert.Null(TorrentControlRules.KeptV2(null, null));
        Assert.Equal(v2, TorrentControlRules.KeptV2(null, v2));
        // A read of a stopped torrent has no v2 hash: the last one stays.
        Assert.Equal(v2, TorrentControlRules.KeptV2(v2, null));
        Assert.Equal(v2, TorrentControlRules.KeptV2(v2, "short"));
        var other = new string('c', 64);
        Assert.Equal(other, TorrentControlRules.KeptV2(v2, other));
        Assert.Equal($"v1 {HashA} · v2 {v2}", TorrentControlRules.HashText(HashA, TorrentControlRules.KeptV2(v2, null)));
    }

    [Fact]
    public void TheGlobalGoalSaysOptionsTorrentsChoice()
    {
        Assert.Equal("Global goal (don't seed)", TorrentControlRules.GlobalGoalText(new SettingsDto { TorrentSeedAfterDownload = false }));
        var both = TorrentControlRules.GlobalGoalText(new SettingsDto { TorrentSeedAfterDownload = true, TorrentSeedRatio = 1, TorrentSeedHours = 48 });
        Assert.StartsWith("Global goal (ratio ", both);
        Assert.EndsWith(" or 48 h)", both);
        Assert.Equal("Global goal (Options › Torrents)", TorrentControlRules.GlobalGoalText(null));
    }

    // ---- Overview, trackers, chip ----

    [Theory]
    [InlineData(null, "—")]
    [InlineData(-1L, "—")]
    [InlineData(0L, "now")]
    [InlineData(45L, "45 s")]
    [InlineData(60L, "1 min")]
    [InlineData(1410L, "24 min")]
    [InlineData(3600L, "1 h")]
    [InlineData(3780L, "1 h 3 min")]
    public void TheNextAnnounceIsSaidShortly(long? seconds, string expected)
        => Assert.Equal(expected, TorrentControlRules.AnnounceIn(seconds));

    [Fact]
    public void TheOverviewFactsAreWords()
    {
        Assert.Equal("—", TorrentControlRules.WastedText(null, null));
        Assert.EndsWith(" · no hash failures", TorrentControlRules.WastedText(0, 0));
        Assert.EndsWith(" · 1 hash failure", TorrentControlRules.WastedText(100, 1));
        Assert.Equal("No", TorrentControlRules.PrivateText(false));
        Assert.StartsWith("Yes", TorrentControlRules.PrivateText(true));
        Assert.Equal("—", TorrentControlRules.PrivateText(null));
        Assert.Equal(HashA, TorrentControlRules.HashText(HashA, null));
        var v2 = new string('b', 64);
        Assert.Equal($"v1 {HashA} · v2 {v2}", TorrentControlRules.HashText(HashA, v2));
        Assert.Equal(string.Empty, TorrentControlRules.PiecesText(0, 0));
        Assert.EndsWith(" · from a .torrent file", TorrentControlRules.AddedText(DateTimeOffset.UtcNow, "file"));
        Assert.EndsWith(" · from a magnet link", TorrentControlRules.AddedText(DateTimeOffset.UtcNow, "magnet"));
    }

    [Fact]
    public void FactsAreReadAgainOnlyWhileTheOverviewShowsARunningTorrent()
    {
        Assert.True(TorrentControlRules.DetailsDue(true, true, 10_000));
        Assert.False(TorrentControlRules.DetailsDue(true, true, 9_999));
        Assert.False(TorrentControlRules.DetailsDue(false, true, 60_000));
        Assert.False(TorrentControlRules.DetailsDue(true, false, 60_000));
    }

    [Fact]
    public void TheChipSaysTheDhtNodes()
    {
        Assert.Null(TorrentControlRules.DhtText(null));
        Assert.Equal("DHT off", TorrentControlRules.DhtText(-1));
        Assert.Equal("DHT 1 node", TorrentControlRules.DhtText(1));
        Assert.Equal("DHT 0 nodes", TorrentControlRules.DhtText(0));
        Assert.Equal("Torrents: 1 active · DHT 1 node", TorrentControlRules.ChipText("Torrents: 1 active", "DHT 1 node"));
        Assert.Equal("Torrents: 1 active", TorrentControlRules.ChipText("Torrents: 1 active", null));
        Assert.Null(TorrentControlRules.ChipText(null, "DHT 1 node"));
    }

    // ---- Watching ----

    [Fact]
    public void AWindowWatchesOnlyWhileItShowsARunningTorrent()
    {
        var running = Torrent("downloading");
        var paused = Torrent("paused");
        Assert.True(TorrentControlRules.Watches(true, false, true, true, [paused, running]));
        Assert.False(TorrentControlRules.Watches(true, false, true, true, [paused, null]));
        Assert.False(TorrentControlRules.Watches(false, false, true, true, [running]));
        Assert.False(TorrentControlRules.Watches(true, true, true, true, [running]));
        Assert.False(TorrentControlRules.Watches(true, false, false, true, [running]));
        Assert.False(TorrentControlRules.Watches(true, false, true, false, [running]));
        Assert.True(TorrentControlRules.LeaseSeconds is >= 10 and <= 300);
        Assert.True(TorrentControlRules.RenewEvery < TimeSpan.FromSeconds(TorrentControlRules.LeaseSeconds));
    }

    // ---- Options › Torrents ----

    [Fact]
    public void EncryptionChoiceFallsBackToPrefer()
    {
        Assert.Equal(1, TorrentControlRules.EncryptionIndex("require"));
        Assert.Equal(2, TorrentControlRules.EncryptionIndex("off"));
        Assert.Equal(0, TorrentControlRules.EncryptionIndex(null));
        Assert.Equal(0, TorrentControlRules.EncryptionIndex("odd"));
        Assert.Equal(0, TorrentControlRules.WholeOrZero(double.NaN));
        Assert.Equal(3, TorrentControlRules.WholeOrZero(2.6));
    }

    [Theory]
    [InlineData(0, 0, 0, null)]
    [InlineData(1000, 2000, 20000, null)]
    [InlineData(5, 2, 10, null)]
    [InlineData(1001, 0, 0, "Enter torrents seeding at once from 0 to 1000 (0 is all of them).")]
    [InlineData(0, 1, 0, "Enter connections per torrent from 2 to 2000, or 0 for no limit of its own.")]
    [InlineData(0, 2001, 0, "Enter connections per torrent from 2 to 2000, or 0 for no limit of its own.")]
    [InlineData(0, 0, 9, "Enter torrent connections in all from 10 to 20000, or 0 for the torrent program's own default.")]
    [InlineData(0, 0, 20001, "Enter torrent connections in all from 10 to 20000, or 0 for the torrent program's own default.")]
    public void LimitsFollowTheHostsRules(int seeds, int perTorrent, int total, string? expected)
        => Assert.Equal(expected, TorrentControlRules.LimitsProblem(seeds, perTorrent, total));

    [Fact]
    public void TheProxyForTorrentsMustBeTypedAndNotSocks4()
    {
        Assert.Contains("system proxy", TorrentControlRules.ProxyProblem("system", null));
        Assert.Contains("only a proxy typed", TorrentControlRules.ProxyProblem("none", null));
        Assert.Contains("only a proxy typed", TorrentControlRules.ProxyProblem("manual", " "));
        Assert.Contains("SOCKS4 proxy", TorrentControlRules.ProxyProblem("manual", "socks4://p:1080"));
        Assert.Contains("SOCKS4a proxy", TorrentControlRules.ProxyProblem("manual", "socks4a://p:1080"));
        Assert.Null(TorrentControlRules.ProxyProblem("manual", "socks5://p:1080"));
        Assert.Null(TorrentControlRules.ProxyProblem("manual", "p:8080"));

        Assert.False(TorrentControlRules.ProxyNote(false, "system", null).IsProblem);
        Assert.True(TorrentControlRules.ProxyNote(true, "system", null).IsProblem);
        Assert.Contains("no UDP", TorrentControlRules.ProxyNote(true, "manual", "p:8080").Words);
        Assert.Contains("through the proxy", TorrentControlRules.ProxyNote(true, "manual", "socks5://p:1080").Words);
    }
}
