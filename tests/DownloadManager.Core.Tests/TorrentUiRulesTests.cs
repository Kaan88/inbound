using DownloadManager.Contracts;
using DownloadManager.Desktop.WinUI;

namespace DownloadManager.Core.Tests;

/// <summary>T1 step 5: the window's torrent rules (<see cref="TorrentRules"/>).</summary>
public sealed class TorrentUiRulesTests
{
    private const string Hash = "0123456789abcdef0123456789abcdef01234567";

    private static JobDto Torrent(string state = "downloading", bool metadata = true, bool awaiting = false, int peers = 4,
        int files = 11, int wanted = 11) => new()
    {
        Id = Guid.NewGuid(),
        Url = $"magnet:?xt=urn:btih:{Hash}&dn=Film&tr=udp%3A%2F%2Ftracker.example%3A1337",
        State = state,
        Torrent = new TorrentDto
        {
            InfoHash = Hash, Name = "Film", HasMetadata = metadata, AwaitingFileChoice = awaiting, Peers = peers,
            FileCount = files, WantedCount = wanted,
        },
    };

    [Fact]
    public void TorrentsApplyOnlyWhereTheHostOffersThemAndTheyAreTurnedOn()
    {
        Assert.True(TorrentRules.Applies(true, new SettingsDto { TorrentsEnabled = true }));
        Assert.False(TorrentRules.Applies(true, new SettingsDto { TorrentsEnabled = false }));
        Assert.False(TorrentRules.Applies(false, new SettingsDto { TorrentsEnabled = true }));
        Assert.False(TorrentRules.Applies(true, null));
    }

    [Theory]
    [InlineData("magnet:?xt=urn:btih:" + Hash, true)]
    [InlineData(@"C:\Users\me\Downloads\film.torrent", true)]
    [InlineData("https://example.com/film.torrent", false)]
    [InlineData("https://example.com/film.mkv", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void TheTorrentDialogTakesMagnetsAndTorrentFiles(string? input, bool expected)
        => Assert.Equal(expected, TorrentRules.IsTorrentInput(input));

    [Theory]
    [InlineData("ask", true, "ThisPc")]
    [InlineData("thisPc", true, "ThisPc")]
    [InlineData("provider", true, "Provider")]
    [InlineData("unknown", true, "ThisPc")]
    [InlineData("ask", false, "ThisPc")]
    [InlineData("provider", false, "ThisPc")]
    public void ALineStartsOnTheRouteTheSettingGivesWhenThereIsAChoice(string setting, bool provider, string expected)
        => Assert.Equal(expected, TorrentRules.DefaultRoute(setting, provider).ToString());

    [Theory]
    [InlineData("ask", "Provider")]
    [InlineData("thisPc", "ThisPc")]
    [InlineData("provider", "Provider")]
    public void AskEachTimePicksTheProviderWhenThisPcCantTakeTorrents(string setting, string expected)
        => Assert.Equal(expected, TorrentRules.DefaultRoute(setting, providerAvailable: true, thisPcAvailable: false).ToString());

    [Theory]
    [InlineData("ask", 0)]
    [InlineData("thisPc", 0)]
    [InlineData("provider", 1)]
    [InlineData(null, 0)]
    public void OptionsShowsTwoRoutesAndAskAsThisPc(string? saved, int index)
        => Assert.Equal(index, TorrentRules.RouteOptionIndex(saved));

    [Theory]
    [InlineData(0, "ask", "ask")]
    [InlineData(0, "provider", "thisPc")]
    [InlineData(0, null, "thisPc")]
    [InlineData(1, "ask", "provider")]
    public void OptionsSavesAskUnchangedWhenThisPcIsKept(int index, string? saved, string expected)
        => Assert.Equal(expected, TorrentRules.RouteToSave(index, saved));

    [Fact]
    public void TheStatusSaysWhatATorrentIsDoing()
    {
        Assert.Equal("Downloading · 4 peers", TorrentRules.StatusText(Torrent()));
        Assert.Equal("Getting file list · 1 peer", TorrentRules.StatusText(Torrent(metadata: false, peers: 1)));
        Assert.Equal("Choose files to download", TorrentRules.StatusText(Torrent(state: "paused", awaiting: true)));
        Assert.Null(TorrentRules.StatusText(Torrent(state: "paused")));
        Assert.Null(TorrentRules.StatusText(new JobDto { State = "downloading" }));
    }

    [Fact]
    public void TheFilesSummaryCountsTheChosenFiles()
    {
        Assert.Equal("11 files", TorrentRules.FilesSummary(Torrent().Torrent!));
        Assert.Equal("3 of 11 files", TorrentRules.FilesSummary(Torrent(wanted: 3).Torrent!));
        Assert.Equal("1 file", TorrentRules.FilesSummary(Torrent(files: 1, wanted: 1).Torrent!));
        Assert.Equal("File list not known yet", TorrentRules.FilesSummary(Torrent(metadata: false).Torrent!));
    }

    [Fact]
    public void Review_5_T3_a_magnet_still_without_its_file_list_after_a_minute_says_it_may_never_come()
    {
        var now = new DateTimeOffset(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
        var looking = Torrent(metadata: false);
        looking.RunStartedAt = now.AddSeconds(-59);
        Assert.Null(TorrentRules.FileListWaitNote(looking, now));
        looking.RunStartedAt = now.AddMinutes(-1);
        Assert.Equal("Still looking for the file list. This torrent may have no one sharing it.", TorrentRules.FileListWaitNote(looking, now));
        // Not while stopped, once the list is known, or without a start time.
        var stopped = Torrent(state: "paused", metadata: false);
        stopped.RunStartedAt = now.AddHours(-1);
        Assert.Null(TorrentRules.FileListWaitNote(stopped, now));
        var known = Torrent();
        known.RunStartedAt = now.AddHours(-1);
        Assert.Null(TorrentRules.FileListWaitNote(known, now));
        Assert.Null(TorrentRules.FileListWaitNote(Torrent(metadata: false), now));
    }

    [Fact]
    public void Review_5_T3_a_magnet_resumed_seconds_ago_counts_from_this_run_not_its_first_start()
    {
        var now = new DateTimeOffset(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
        var resumed = Torrent(metadata: false);
        resumed.StartedAt = now.AddHours(-1);
        resumed.RunStartedAt = now.AddSeconds(-10);
        Assert.Null(TorrentRules.FileListWaitNote(resumed, now));
        // An older Host sends only the first start, which can't tell a resumed run from a long one: no note.
        resumed.RunStartedAt = null;
        Assert.Null(TorrentRules.FileListWaitNote(resumed, now));
    }

    [Fact]
    public void TheChipShowsOnlyWhileATorrentRuns()
    {
        Assert.Null(TorrentRules.ChipText([Torrent(state: "paused"), new JobDto { State = "downloading" }]));
        Assert.Equal("Torrents: 2 active · 47 peers", TorrentRules.ChipText([Torrent(peers: 41), Torrent(state: "connecting", peers: 6), Torrent(state: "completed")]));
    }

    [Fact]
    public void ATorrentRowShowsAShortMagnetNotTheTrackers()
    {
        var shown = JobAddress.Shown(Torrent());
        Assert.Equal("magnet:?xt=urn:btih:" + Hash, shown);
        Assert.DoesNotContain("tracker", JobAddress.Detail(Torrent()));
    }

    [Fact]
    public void TheFileTreeUsesTheIndexAsTheIdAndGivesTheChoiceBack()
    {
        TorrentFileDto[] files =
        [
            new() { Index = 0, Path = "Film/a.mkv", Size = 100, Wanted = true },
            new() { Index = 2, Path = "Film/b.srt", Size = 5, Wanted = false },
        ];
        var tree = TorrentRules.TreeFiles(files);
        Assert.Equal(["0", "2"], tree.Select(file => file.FileId));
        Assert.All(tree, file => Assert.True(file.Selectable));
        Assert.Equal(["0"], TorrentRules.WantedIds(files));
        Assert.Equal([0, 2], TorrentRules.Indexes(new RemoteFileChoice(true, []), files));
        Assert.Equal([2], TorrentRules.Indexes(new RemoteFileChoice(false, ["2"]), files));
        Assert.Null(TorrentRules.Indexes(new RemoteFileChoice(false, ["x"]), files));
    }

    [Fact]
    public void AFileSaysHowFarItIs()
    {
        Assert.Equal("Skipped", TorrentRules.FileProgress(new TorrentFileDto { Size = 10, Wanted = false, Done = 0 }));
        Assert.Equal("", TorrentRules.FileProgress(new TorrentFileDto { Size = 10, Wanted = true }));
        Assert.Equal("71%", TorrentRules.FileProgress(new TorrentFileDto { Size = 100, Wanted = true, Done = 71 }));
        Assert.Equal("99%", TorrentRules.FileProgress(new TorrentFileDto { Size = 1000, Wanted = true, Done = 999 }));
        Assert.Equal("Done", TorrentRules.FileProgress(new TorrentFileDto { Size = 100, Wanted = true, Done = 100 }));
    }

    [Fact]
    public void AShownFileListIsReadAgainForProgressOnlyWhileItsTorrentDownloadsAndNotTooOften()
    {
        Assert.True(TorrentRules.FileProgressDue(Torrent(), listShown: true, TorrentRules.FileProgressReadMs));
        Assert.False(TorrentRules.FileProgressDue(Torrent(), listShown: true, TorrentRules.FileProgressReadMs - 1));
        Assert.False(TorrentRules.FileProgressDue(Torrent(), listShown: false, 60_000));
        Assert.False(TorrentRules.FileProgressDue(Torrent(state: "paused"), listShown: true, 60_000));
        Assert.False(TorrentRules.FileProgressDue(Torrent(state: "completed"), listShown: true, 60_000));
        Assert.False(TorrentRules.FileProgressDue(Torrent(metadata: false), listShown: true, 60_000));
    }

    [Fact]
    public void FilesCanBeChosenForOneUnfinishedTorrentWithItsList()
    {
        Assert.True(TorrentRules.CanChooseFiles([Torrent(state: "paused", awaiting: true)]));
        Assert.False(TorrentRules.CanChooseFiles([Torrent(metadata: false)]));
        Assert.False(TorrentRules.CanChooseFiles([Torrent(state: "completed")]));
        Assert.False(TorrentRules.CanChooseFiles([Torrent(), Torrent()]));
        Assert.True(TorrentRules.BlocksAddressCommands([new JobDto(), Torrent()]));
        Assert.False(TorrentRules.BlocksAddressCommands([new JobDto()]));
    }

    [Fact]
    public void ATorrentRowOpensOnlyWhenItHasSeveralKnownFiles()
    {
        Assert.True(TorrentRules.ShowsFiles(Torrent().Torrent));
        Assert.False(TorrentRules.ShowsFiles(Torrent(files: 1, wanted: 1).Torrent));
        Assert.False(TorrentRules.ShowsFiles(Torrent(metadata: false).Torrent));
        Assert.False(TorrentRules.ShowsFiles(null));
    }

    [Fact]
    public void ARowWaitingForItsChoiceOpensTheChoice()
    {
        Assert.True(TorrentRules.PrimaryIsChooseFiles(Torrent(state: "paused", awaiting: true)));
        Assert.False(TorrentRules.PrimaryIsChooseFiles(Torrent()));
        Assert.False(TorrentRules.PrimaryIsChooseFiles(new JobDto()));
        Assert.False(TorrentRules.PrimaryIsChooseFiles(null));
    }

    [Fact]
    public void TheTorrentsViewStaysWhileTorrentsAreListed()
    {
        Assert.True(TorrentRules.SidebarShows(applies: true, anyTorrentJobs: false));
        Assert.True(TorrentRules.SidebarShows(applies: false, anyTorrentJobs: true));
        Assert.False(TorrentRules.SidebarShows(applies: false, anyTorrentJobs: false));
    }

    [Fact]
    public void AFileRowShowsItsNameAndHowFarItIs()
    {
        Assert.Equal("film.mkv", TorrentRules.FileRowName("Film/extras/film.mkv"));
        Assert.Equal("film.mkv", TorrentRules.FileRowName("film.mkv"));
        Assert.Equal("Skipped", TorrentRules.FileRowStatus(new TorrentFileDto { Size = 10, Wanted = false }, "Downloading"));
        Assert.Equal("Downloading", TorrentRules.FileRowStatus(new TorrentFileDto { Size = 10, Wanted = true }, "Downloading"));
        Assert.Equal("50%", TorrentRules.FileRowStatus(new TorrentFileDto { Size = 10, Wanted = true, Done = 5 }, "Downloading"));
        Assert.Equal("1 more file: open Progress Details", TorrentRules.MoreFiles(1));
        Assert.Equal("99800 more files: open Progress Details", TorrentRules.MoreFiles(99800));
    }

    [Fact]
    public void AChoiceIsSaidInTheStatusBar()
    {
        Assert.Equal("Film: downloading all 11 files.", TorrentRules.ChoiceSaid("Film", 11, 11));
        Assert.Equal("Film: downloading 3 files of 11.", TorrentRules.ChoiceSaid("Film", 3, 11));
        Assert.Equal("Film: downloading 1 file of 11.", TorrentRules.ChoiceSaid("Film", 1, 11));
    }

    [Fact]
    public void TheProviderDialogOpensOnWhatWasGiven()
    {
        const string magnet = "magnet:?xt=urn:btih:" + Hash;
        const string file = @"C:\Users\me\Downloads\film.torrent";
        Assert.Equal(AddFocus.TorrentFiles, TorrentRules.ProviderFocus([file]));
        Assert.Equal(AddFocus.TorrentFiles, TorrentRules.ProviderFocus([file, @"D:\other.torrent"]));
        Assert.Equal(AddFocus.Magnet, TorrentRules.ProviderFocus([magnet]));
        Assert.Equal(AddFocus.Links, TorrentRules.ProviderFocus([magnet, magnet + "1"]));
        Assert.Equal(AddFocus.Links, TorrentRules.ProviderFocus([magnet, file]));
        Assert.Equal(AddFocus.Links, TorrentRules.ProviderFocus([]));
    }

    [Theory]
    [InlineData(@"\\server\share\film.torrent", true)]
    [InlineData("//server/share/film.torrent", true)]
    [InlineData(@"\\?\C:\film.torrent", true)]
    [InlineData(@"\\.\pipe\film.torrent", true)]
    [InlineData(@"\??\UNC\server\film.torrent", true)]
    [InlineData(@"  \\server\share\film.torrent", true)]
    [InlineData(@"C:\Users\me\film.torrent", false)]
    [InlineData("magnet:?xt=urn:btih:" + Hash, false)]
    public void ANetworkPathIsNeverOpenedFromHere(string input, bool expected)
        => Assert.Equal(expected, TorrentRules.IsNetworkOrDevicePath(input));

    [Theory]
    [InlineData(MessageTypes.HostActionResult, "Unknown message type 'torrent.files'.", "Torrents aren't available here.")]
    [InlineData(MessageTypes.HostActionResult, "The disk is full.", "The disk is full.")]
    [InlineData(MessageTypes.HostActionResult, "", "Inbound could not do that.")]
    [InlineData("somethingElse", "The disk is full.", "Torrents aren't available here.")]
    public void ATorrentFilesErrorReplySaysTheHostsReason(string type, string message, string expected)
        => Assert.Equal(expected, HostConnection.TorrentReplyError(IpcMessage.Create(type, new HostActionResult { Ok = false, Message = message })));

    [Fact]
    public void ExportListFailsOnAHostErrorAndSavesWithoutTorrentsOnlyForAHostWithoutThem()
    {
        // A Host without torrents: the list is saved with its magnets alone.
        Assert.Null(HostConnection.ListEntriesPage(IpcMessage.Create(MessageTypes.HostActionResult,
            new HostActionResult { Ok = false, Message = "Unknown message type 'torrent.listEntries'." })));
        // Any other error is the Host's, and the export stops with its words instead of saving the list without them.
        var error = Assert.Throws<InvalidOperationException>(() => HostConnection.ListEntriesPage(IpcMessage.Create(MessageTypes.HostActionResult,
            new HostActionResult { Ok = false, Message = "The file is in use." })));
        Assert.Equal("The file is in use.", error.Message);
        Assert.NotNull(HostConnection.ListEntriesPage(IpcMessage.Create(TorrentMessageTypes.ListEntriesResult, new TorrentListEntriesResult())));
    }

    /// <summary>Review 5:F6b: per-site limits are rows of a site and a number, not typed pattern=number lines.</summary>
    [Fact]
    public void PerSiteLimitRowsBecomeProfiles()
    {
        var (profiles, problem) = OptionsSettingsRebase.SiteLimits([(" *.a.com ", 2), ("", double.NaN), ("b.org", 4)]);
        Assert.Null(problem);
        Assert.Equal(["*.a.com=2", "b.org=4"], profiles.Select(profile => $"{profile.Pattern}={profile.MaxConnections}"));
        Assert.Equal(OptionsSettingsRebase.SiteLimitNeedsSite, OptionsSettingsRebase.SiteLimits([(" ", 3)]).Problem);
        Assert.Equal("The limit for a.com must be 1 to 32 connections.", OptionsSettingsRebase.SiteLimits([("a.com", double.NaN)]).Problem);
        Assert.Equal("The limit for a.com must be 1 to 32 connections.", OptionsSettingsRebase.SiteLimits([("a.com", 33)]).Problem);
        Assert.Empty(OptionsSettingsRebase.SiteLimits([("a.com", 0)]).Profiles);
    }

    /// <summary>Review 5:A6: ordinary links dropped or pasted on the window go to Add Links, with torrents on or off.</summary>
    [Fact]
    public void DroppedOrPastedLinksGoToAddLinks()
    {
        Assert.Equal((false, true, (string?)null), TorrentRules.Intake(torrents: 0, links: 2, torrentsCanBeAdded: false, offThisPc: false, dropped: true));
        Assert.Equal((true, true, (string?)null), TorrentRules.Intake(1, 1, true, false, false));
        Assert.Equal((true, false, (string?)null), TorrentRules.Intake(1, 0, true, false, true));
        // A magnet alone with torrents off says so rather than nothing.
        Assert.Equal((false, false, TorrentRules.TorrentsCantBeAdded), TorrentRules.Intake(1, 0, false, false, true));
        Assert.Equal((false, false, TorrentRules.OnlyLocalTorrentFiles), TorrentRules.Intake(0, 0, true, true, true));
        Assert.Equal((false, false, TorrentRules.NothingToDrop), TorrentRules.Intake(0, 0, true, false, true));
        Assert.Equal((false, false, TorrentRules.NothingOnClipboard), TorrentRules.Intake(0, 0, false, false, false));
        Assert.Equal(["https://example.com/a.zip"],
            LinkExtractor.HttpLinks("see https://example.com/a.zip and ftp://example.com/b.iso, magnet:?xt=urn:btih:" + Hash));
    }
}
