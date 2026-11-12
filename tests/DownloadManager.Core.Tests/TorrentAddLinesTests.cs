using DownloadManager.Contracts;
using DownloadManager.Desktop.WinUI;

namespace DownloadManager.Core.Tests;

/// <summary>T1 step 5: the torrent add dialog's lines (<see cref="TorrentAddLines"/>).</summary>
public sealed class TorrentAddLinesTests
{
    private const string HashA = "0123456789abcdef0123456789abcdef01234567";
    private const string HashB = "89abcdef0123456789abcdef0123456789abcdef";
    private const string MagnetA = "magnet:?xt=urn:btih:" + HashA + "&dn=Film&tr=udp%3A%2F%2Ftracker.example%3A1337";
    private const string FileA = @"C:\Users\me\Downloads\film.torrent";
    private const string FileB = @"C:\Users\me\Downloads\other.torrent";

    private static TorrentInspectResult Magnet(string hash = HashA, string? name = "Film", Guid? existing = null) => new()
    {
        Ok = true, Kind = "magnet", InfoHash = hash, Name = name, ExistingJobId = existing,
    };

    private static TorrentInspectResult File(string hash = HashA, params long[] sizes) => new()
    {
        Ok = true, Kind = "torrentFile", InfoHash = hash, Name = "Sintel", TotalBytes = sizes.Sum(),
        Files = sizes.Select((size, index) => new TorrentFileDto { Index = index, Path = $"Sintel/part{index}.mkv", Size = size, Wanted = true }).ToList(),
    };

    private static TorrentAddLines ThisPcOnly(bool chooseFiles = true) => new(TorrentLineRoute.ThisPc, chooseFiles, null);

    [Fact]
    public void LinesAreAddedOncePerInputAndOnlyForTorrents()
    {
        var lines = ThisPcOnly();
        var added = lines.Add([MagnetA, " " + MagnetA + " ", FileA, FileA.ToUpperInvariant(), "https://example.com/a.zip"], fromPaste: false);
        Assert.Equal(2, added.Count);
        Assert.Equal(2, lines.Lines.Count);
        Assert.All(lines.Lines, line => Assert.Equal(TorrentLineState.Reading, line.State));
    }

    [Fact]
    public void ALineSaysWhatItIsWhileItIsRead()
    {
        var lines = ThisPcOnly();
        lines.Add([MagnetA, FileA], fromPaste: false);
        Assert.Equal("Magnet link", lines.Lines[0].Name);
        Assert.Equal("Magnet · Checking…", lines.Lines[0].Detail);
        Assert.Equal("film.torrent", lines.Lines[1].Name);
        Assert.Equal(".torrent file · Reading…", lines.Lines[1].Detail);
        Assert.True(lines.Lines[0].IsSelected);
        Assert.False(lines.Lines[0].CanSelect);
        Assert.False(lines.CanAdd);
    }

    [Fact]
    public void ReadLinesShowKindSizeAndFiles()
    {
        var lines = ThisPcOnly();
        lines.Add([MagnetA, FileA], fromPaste: false);
        lines.Inspected(lines.Lines[0], Magnet());
        lines.Inspected(lines.Lines[1], File(HashB, 1024, 2048));
        Assert.Equal("Film", lines.Lines[0].Name);
        Assert.Equal("Magnet · Size not known yet", lines.Lines[0].Detail);
        Assert.Equal("Sintel", lines.Lines[1].Name);
        Assert.Equal(".torrent file · 3.0 KB · 2 files", lines.Lines[1].Detail);
        Assert.True(lines.CanAdd);
        Assert.Equal("Add 2", lines.PrimaryText);
        Assert.Equal("Selected 3.0 KB in 2 torrents", lines.Summary());
    }

    [Fact]
    public void AFailedLineSaysWhyAndCantBeTicked()
    {
        var lines = ThisPcOnly();
        lines.Add([FileA], fromPaste: false);
        lines.Inspected(lines.Lines[0], new TorrentInspectResult { Error = "That isn't a torrent file." });
        var line = lines.Lines[0];
        Assert.Equal(TorrentLineState.Failed, line.State);
        Assert.Equal("That isn't a torrent file.", line.Detail);
        Assert.True(line.IsProblem);
        Assert.False(line.IsSelected);
        lines.Select(line, true);
        Assert.False(line.IsSelected);
        Assert.False(lines.CanAdd);
    }

    [Fact]
    public void ATorrentAlreadyInTheListCantBeAdded()
    {
        var lines = ThisPcOnly();
        lines.Add([MagnetA], fromPaste: false);
        Assert.Null(lines.Lines[0].ListedJobId);
        var existing = Guid.NewGuid();
        lines.Inspected(lines.Lines[0], Magnet(existing: existing));
        Assert.Equal(TorrentLineState.Listed, lines.Lines[0].State);
        Assert.Equal("Magnet · Already in the list", lines.Lines[0].Detail);
        Assert.False(lines.CanAdd);
        // "Show in list" ends the dialog with that job and nothing to add.
        Assert.Equal(existing, lines.Lines[0].ListedJobId);
        var show = TorrentAddChoice.ShowJob(existing);
        Assert.Equal(existing, show.ShowJobId);
        Assert.Empty(show.Picks);
    }

    [Fact]
    public void OnlyAListedLineOffersItsJob()
    {
        var lines = ThisPcOnly();
        lines.Add([FileA, FileB], fromPaste: false);
        lines.Inspected(lines.Lines[0], File(HashA, 10));
        // The same torrent as the line above wins over "already in the list".
        lines.Inspected(lines.Lines[1], Magnet(HashA, existing: Guid.NewGuid()));
        Assert.Null(lines.Lines[0].ListedJobId);
        Assert.Equal(TorrentLineState.Duplicate, lines.Lines[1].State);
        Assert.Null(lines.Lines[1].ListedJobId);
    }

    [Fact]
    public void TheSameTorrentTwiceKeepsTheFirstLine()
    {
        var lines = ThisPcOnly();
        lines.Add([FileA, FileB], fromPaste: false);
        // The second one is read first: it stays addable until the first one turns out to be the same.
        lines.Inspected(lines.Lines[1], File(HashA, 10));
        Assert.Equal(TorrentLineState.Ready, lines.Lines[1].State);
        lines.Inspected(lines.Lines[0], File(HashA, 10));
        Assert.Equal(TorrentLineState.Ready, lines.Lines[0].State);
        Assert.Equal(TorrentLineState.Duplicate, lines.Lines[1].State);
        Assert.Equal(".torrent file · Same torrent as above", lines.Lines[1].Detail);
        Assert.Single(lines.Picks());
    }

    [Fact]
    public void TypingInThePasteBoxKeepsTheLinesFromTheClipboard()
    {
        // The dialog opens with the clipboard's lines and an empty box; each pause in typing syncs the box.
        var lines = ThisPcOnly();
        lines.Add([MagnetA, FileA], fromPaste: false);
        foreach (var typed in new[] { "C", @"C:\x", FileB, FileB[..^1], string.Empty, MagnetA, FileA, string.Empty })
        {
            lines.SyncPaste(typed);
            Assert.Contains(lines.Lines, line => line.Input == MagnetA);
            Assert.Contains(lines.Lines, line => line.Input == FileA);
        }

        Assert.Equal(2, lines.Lines.Count);
    }

    [Fact]
    public void ThePasteBoxAddsAndRemovesOnlyItsOwnLines()
    {
        var lines = ThisPcOnly();
        lines.Add([FileA], fromPaste: false);
        var (added, removed) = lines.SyncPaste($"look: {MagnetA}\n{FileA}\n{FileB}");
        Assert.Equal(2, added.Count);
        Assert.Empty(removed);
        Assert.Equal(3, lines.Lines.Count);

        (added, removed) = lines.SyncPaste(string.Empty);
        Assert.Empty(added);
        Assert.Equal(2, removed.Count);
        Assert.Equal(FileA, Assert.Single(lines.Lines).Input);
        Assert.False(lines.Inspected(removed[0], Magnet()));
    }

    [Fact]
    public void LinesStopAtTheMost()
    {
        var lines = ThisPcOnly();
        var inputs = Enumerable.Range(0, TorrentAddLines.MostLines + 5).Select(i => $@"C:\t\{i}.torrent").ToList();
        lines.Add(inputs, fromPaste: false);
        Assert.Equal(TorrentAddLines.MostLines, lines.Lines.Count);
        Assert.True(lines.Capped);
        Assert.Contains($"Only the first {TorrentAddLines.MostLines} are listed.", lines.Status());
    }

    [Fact]
    public void WithAskEachTimeEveryLineStartsOnThisPcAndAddWorksAtOnce()
    {
        // T9 W1: "Ask each time" picks This PC for each torrent; Add needs no extra click.
        var lines = new TorrentAddLines(TorrentRules.DefaultRoute("ask", providerAvailable: true), true, "TorBox");
        lines.Add([MagnetA, FileA], fromPaste: false);
        lines.Inspected(lines.Lines[0], Magnet());
        lines.Inspected(lines.Lines[1], File(HashB, 1024));
        Assert.All(lines.Lines, line => Assert.Equal(TorrentLineRoute.ThisPc, line.Route));
        Assert.True(lines.CanAdd);
        Assert.Equal(string.Empty, lines.Problem());

        // Each line can still be switched.
        lines.SetRoute(lines.Lines[0], TorrentLineRoute.Provider);
        Assert.True(lines.CanAdd);
        var picks = lines.Picks();
        Assert.Equal(TorrentLineRoute.Provider, picks[0].Route);
        Assert.Equal(TorrentLineRoute.ThisPc, picks[1].Route);
    }

    [Fact]
    public void ALineWithNoRouteStillWaitsForOne()
    {
        var lines = new TorrentAddLines(TorrentLineRoute.Unset, true, "TorBox");
        lines.Add([MagnetA], fromPaste: false);
        lines.Inspected(lines.Lines[0], Magnet());
        Assert.False(lines.CanAdd);
        Assert.Equal("Choose This PC or TorBox for each ticked torrent.", lines.Problem());
    }

    [Fact]
    public void WithoutAProviderEveryLineIsThisPc()
    {
        var lines = new TorrentAddLines(TorrentLineRoute.Unset, true, null);
        lines.Add([MagnetA], fromPaste: false);
        Assert.Equal(TorrentLineRoute.ThisPc, lines.Lines[0].Route);
        lines.SetRoute(lines.Lines[0], TorrentLineRoute.Provider);
        Assert.Equal(TorrentLineRoute.ThisPc, lines.Lines[0].Route);
    }

    [Theory]
    [InlineData(true, "askWhenReady")]
    [InlineData(false, "all")]
    public void AMagnetOnThisPcAsksForItsFilesLaterWhenTheSettingIsOn(bool chooseFiles, string files)
    {
        var lines = ThisPcOnly(chooseFiles);
        lines.Add([MagnetA], fromPaste: false);
        lines.Inspected(lines.Lines[0], Magnet());
        Assert.Equal(chooseFiles, lines.MagnetNoteApplies);
        var pick = Assert.Single(lines.Picks());
        Assert.Equal(files, pick.Files);
        Assert.Equal(MagnetA, pick.Input);
        Assert.Equal("Film", pick.Name);
    }

    [Fact]
    public void ATorrentFileWithSeveralFilesCanHaveSomeChosen()
    {
        var lines = ThisPcOnly();
        lines.Add([FileA], fromPaste: false);
        var line = lines.Lines[0];
        lines.Inspected(line, File(HashA, 1024, 2048, 4096));
        Assert.True(lines.CanChooseFilesFor(line));

        lines.SetFileChoice(line, new RemoteFileChoice(false, ["0", "2"]));
        Assert.Equal(".torrent file · 5.0 KB of 7.0 KB · 2 of 3 files", line.Detail);
        Assert.Equal("Selected 5.0 KB in 1 torrent", lines.Summary());
        var pick = Assert.Single(lines.Picks());
        Assert.Equal("chosen", pick.Files);
        Assert.Equal([0, 2], pick.FileIndexes);

        lines.SetFileChoice(line, new RemoteFileChoice(true, []));
        Assert.Equal("all", Assert.Single(lines.Picks()).Files);

        lines.SetFileChoice(line, new RemoteFileChoice(false, []));
        Assert.False(lines.CanAdd);
        Assert.Equal("Choose at least one file of Sintel.", lines.Problem());

    }

    [Fact]
    public void OnTheProviderATorrentFilesTickedFilesGoByPathAndSize()
    {
        // T9 W2: the same tree applies to the provider route; its files are sent by path and size.
        var lines = new TorrentAddLines(TorrentLineRoute.ThisPc, true, "TorBox");
        lines.Add([FileA], fromPaste: false);
        var line = lines.Lines[0];
        lines.Inspected(line, File(HashA, 1024, 2048, 4096));
        lines.SetFileChoice(line, new RemoteFileChoice(false, ["1"]));
        Assert.Equal("chosen", Assert.Single(lines.Picks()).Files);

        lines.SetRoute(line, TorrentLineRoute.Provider);
        Assert.True(lines.CanChooseFilesFor(line));
        Assert.Equal(".torrent file · 2.0 KB of 7.0 KB · 1 of 3 files", line.Detail);
        var pick = Assert.Single(lines.Picks());
        Assert.Equal("chosen", pick.Files);
        var path = Assert.Single(pick.FilePaths!);
        Assert.Equal("Sintel/part1.mkv", path.Path);
        Assert.Equal(2048, path.Size);
        Assert.Null(pick.Priorities);

        lines.SetFileChoice(line, new RemoteFileChoice(true, []));
        pick = Assert.Single(lines.Picks());
        Assert.Equal("all", pick.Files);
        Assert.Null(pick.FilePaths);
    }

    [Fact]
    public void OnTheProviderAMagnetFollowsTheHostsRuleAndAnUnchosenTorrentTakesAll()
    {
        var lines = new TorrentAddLines(TorrentLineRoute.Provider, chooseFiles: false, "TorBox");
        lines.Add([MagnetA, FileA], fromPaste: false);
        lines.Inspected(lines.Lines[0], Magnet());
        lines.Inspected(lines.Lines[1], File(HashB, 1024, 2048));
        Assert.False(lines.CanChooseFilesFor(lines.Lines[1]));
        Assert.Equal([string.Empty, "all"], lines.Picks().Select(pick => pick.Files));
        Assert.False(lines.MagnetNoteApplies);
    }

    [Fact]
    public void OnTheProviderATorrentWithTooManyFilesIsNotChosenHere()
    {
        var lines = new TorrentAddLines(TorrentLineRoute.Provider, true, "TorBox");
        lines.Add([FileA], fromPaste: false);
        lines.Inspected(lines.Lines[0], File(HashA, Enumerable.Repeat(10L, TorrentAddLines.MostProviderFiles + 1).ToArray()));
        Assert.False(lines.CanChooseFilesFor(lines.Lines[0]));
        lines.SetRoute(lines.Lines[0], TorrentLineRoute.ThisPc);
        Assert.True(lines.CanChooseFilesFor(lines.Lines[0]));
    }

    [Fact]
    public void AThisPcFileChoiceIsDroppedOnTheProviderWhenItCantBeSent()
    {
        // Review: past the provider's limit the choice can't go, so the line shows and sends every file.
        var lines = new TorrentAddLines(TorrentLineRoute.ThisPc, true, "TorBox");
        lines.Add([FileA], fromPaste: false);
        var line = lines.Lines[0];
        lines.Inspected(line, File(HashA, Enumerable.Repeat(10L, TorrentAddLines.MostProviderFiles + 1).ToArray()));
        lines.SetFileChoice(line, new RemoteFileChoice(false, ["0", "1"]));
        Assert.True(line.IsPartial);
        Assert.Contains("2 of", line.Detail);

        lines.SetRoute(line, TorrentLineRoute.Provider);
        Assert.False(line.IsPartial);
        Assert.DoesNotContain("2 of", line.Detail);
        Assert.Equal(line.TotalBytes, line.WantedBytes);
        var pick = Assert.Single(lines.Picks());
        Assert.Equal("all", pick.Files);
        Assert.Null(pick.FileIndexes);
        Assert.Null(pick.FilePaths);
        // Back on This PC the choice is used again.
        lines.SetRoute(line, TorrentLineRoute.ThisPc);
        Assert.Equal("chosen", Assert.Single(lines.Picks()).Files);
    }

    [Theory]
    [InlineData(false, 100L, 200L)]
    [InlineData(true, 100L)]
    public void ATorrentFileOnTheProviderTakesEveryFileWhenNoneAreChosenHere(bool chooseFiles, params long[] sizes)
    {
        // Review: the setting off (or one file) sends "all", as This PC does, so no Choose Files window follows.
        var lines = new TorrentAddLines(TorrentLineRoute.Provider, chooseFiles, "TorBox");
        lines.Add([FileA], fromPaste: false);
        lines.Inspected(lines.Lines[0], File(HashA, sizes));
        Assert.False(lines.CanChooseFilesFor(lines.Lines[0]));
        var pick = Assert.Single(lines.Picks());
        Assert.Equal("all", pick.Files);
        Assert.Equal("all", TorrentAddRequests.ForProvider(pick, null, "startNow", null, 1).Selection);
    }

    [Fact]
    public void AMagnetOnTheProviderIsLeftToTheHostsRule()
    {
        var lines = new TorrentAddLines(TorrentLineRoute.Provider, false, "TorBox");
        lines.Add([MagnetA], fromPaste: false);
        lines.Inspected(lines.Lines[0], Magnet());
        var pick = Assert.Single(lines.Picks());
        Assert.Equal(string.Empty, pick.Files);
        Assert.Null(TorrentAddRequests.ForProvider(pick, null, "startNow", null, 1).Selection);
    }

    // ---- T9 W2: the provider's request comes from the torrent dialog ---------------------------

    private static TorrentAddPick ProviderPick(string files = "", string? category = @"D:\Video", IReadOnlyList<RemoteFilePathDto>? paths = null)
        => new(MagnetA, "Film", TorrentLineRoute.Provider, files, null) { CategoryFolder = category, FilePaths = paths };

    [Fact]
    public void TheProviderRequestTakesTheDialogsFolderStartAndQueue()
    {
        var request = TorrentAddRequests.ForProvider(ProviderPick(), @"E:\Films ", "later", 3, defaultQueueId: 1);
        Assert.Equal(MagnetA, request.Input);
        Assert.Equal(@"E:\Films", request.Destination);
        Assert.Equal("later", request.StartMode);
        Assert.Equal(3, request.QueueId);
        // Adding is the go-ahead, as in the provider's own add dialog; nothing else is asked.
        Assert.True(request.ApproveUncached);
        Assert.Null(request.Selection);
        Assert.Null(request.FilePaths);
        Assert.Null(request.FileIds);
        Assert.Null(request.LinkPassword);
    }

    [Fact]
    public void AnEmptySaveToIsTheTorrentsCategoryFolderOrTheDefault()
    {
        Assert.Equal(@"D:\Video", TorrentAddRequests.ForProvider(ProviderPick(), null, "startNow", null, 1).Destination);
        Assert.Equal(@"D:\Video", TorrentAddRequests.ForProvider(ProviderPick(), "  ", "startNow", null, 1).Destination);
        Assert.Null(TorrentAddRequests.ForProvider(ProviderPick(category: null), null, "startNow", null, 1).Destination);
        Assert.Equal(1, TorrentAddRequests.ForProvider(ProviderPick(), null, "startNow", null, 1).QueueId);
        Assert.Equal("startNow", TorrentAddRequests.ForProvider(ProviderPick(), null, "whenever", null, 1).StartMode);
    }

    [Fact]
    public void TheProviderRequestCarriesTheFileChoice()
    {
        var paths = new List<RemoteFilePathDto> { new() { Path = "Sintel/part1.mkv", Size = 2048 } };
        var chosen = TorrentAddRequests.ForProvider(ProviderPick("chosen", paths: paths), null, "startNow", 1, 1);
        Assert.Equal("chosen", chosen.Selection);
        Assert.Equal("Sintel/part1.mkv", Assert.Single(chosen.FilePaths!).Path);
        var all = TorrentAddRequests.ForProvider(ProviderPick("all", paths: paths), null, "startNow", 1, 1);
        Assert.Equal("all", all.Selection);
        Assert.Null(all.FilePaths);
    }

    [Fact]
    public void AProviderPickKnowsItsCategoryFolderFromTheHost()
    {
        var lines = new TorrentAddLines(TorrentLineRoute.Provider, true, "TorBox");
        lines.Add([MagnetA], fromPaste: false);
        var read = Magnet();
        read.Destination = @"D:\Video";
        lines.Inspected(lines.Lines[0], read);
        Assert.Equal(@"D:\Video", Assert.Single(lines.Picks()).CategoryFolder);
    }

    [Fact]
    public void TheCategoryFolderIsNotTakenFromATypedSaveTo()
    {
        // Review: Save to typed, then cleared before the next answer: the typed folder must not stand in for the category.
        var lines = new TorrentAddLines(TorrentLineRoute.Provider, true, "TorBox");
        lines.Add([MagnetA], fromPaste: false);
        var line = lines.Lines[0];
        var read = Magnet();
        read.Destination = @"D:\Video";
        lines.Inspected(line, read);
        Assert.True(lines.SpaceAnswered(line, @"E:\Typed", new TorrentInspectResult { Ok = true, Destination = @"E:\Typed", FreeBytes = 5 }));
        Assert.Equal(@"E:\Typed", line.Result!.Destination);
        Assert.Equal(5, line.Result.FreeBytes);
        Assert.Equal(@"D:\Video", Assert.Single(lines.Picks()).CategoryFolder);

        // Read first with a folder typed: no category folder until an answer for Save to empty.
        var typed = new TorrentAddLines(TorrentLineRoute.Provider, true, "TorBox");
        typed.Add([MagnetA], fromPaste: false);
        var first = Magnet();
        first.Destination = @"E:\Typed";
        typed.Inspected(typed.Lines[0], first, askedWithoutFolder: false);
        Assert.Null(Assert.Single(typed.Picks()).CategoryFolder);
        typed.SpaceAnswered(typed.Lines[0], null, new TorrentInspectResult { Ok = true, Destination = @"D:\Video" });
        Assert.Equal(@"D:\Video", Assert.Single(typed.Picks()).CategoryFolder);
    }

    [Fact]
    public void ASingleFileTorrentOrTheSettingOffTakesEveryFile()
    {
        var lines = ThisPcOnly();
        lines.Add([FileA], fromPaste: false);
        lines.Inspected(lines.Lines[0], File(HashA, 100));
        Assert.False(lines.CanChooseFilesFor(lines.Lines[0]));
        Assert.Equal("all", Assert.Single(lines.Picks()).Files);

        var off = ThisPcOnly(chooseFiles: false);
        off.Add([FileA], fromPaste: false);
        off.Inspected(off.Lines[0], File(HashA, 100, 200));
        Assert.False(off.CanChooseFilesFor(off.Lines[0]));
        Assert.Equal("all", Assert.Single(off.Picks()).Files);
    }

    [Fact]
    public void UntickedLinesAreLeftOut()
    {
        var lines = ThisPcOnly();
        lines.Add([FileA, FileB], fromPaste: false);
        lines.Inspected(lines.Lines[0], File(HashA, 10));
        lines.Inspected(lines.Lines[1], File(HashB, 10));
        lines.Select(lines.Lines[0], false);
        Assert.Null(lines.SelectAllState());
        Assert.Equal("Add", lines.PrimaryText);
        Assert.Equal(FileB, Assert.Single(lines.Picks()).Input);
        lines.ToggleAll();
        Assert.True(lines.SelectAllState());
        lines.ToggleAll();
        Assert.False(lines.CanAdd);
        Assert.Equal("No torrents selected", lines.Summary());
    }

    [Fact]
    public void NoLineShowsOrLogsAWholeMagnet()
    {
        var lines = ThisPcOnly();
        lines.Add([MagnetA], fromPaste: false);
        var line = lines.Lines[0];
        Assert.DoesNotContain("tracker", line.ToolTip, StringComparison.Ordinal);
        lines.Inspected(line, Magnet(name: null));
        Assert.Equal("magnet:?xt=urn:btih:" + HashA, line.ToolTip);
        Assert.Equal("Magnet link", line.Name);
        Assert.DoesNotContain("magnet", line.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("magnet", lines.Picks()[0].ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("magnet", new TorrentAddChoice(lines.Picks(), null, "startNow", 1).ToString(), StringComparison.Ordinal);
    }

    // ---- T1 step 6: the provider's cache, per line ---------------------------------------------

    [Fact]
    public void WithoutAnAccountNothingIsCheckedAndNothingIsShown()
    {
        var lines = ThisPcOnly();
        lines.Add([MagnetA, FileA], fromPaste: false);
        lines.Inspected(lines.Lines[0], Magnet());
        lines.Inspected(lines.Lines[1], File(HashB, 1024, 2048));
        Assert.False(lines.ChecksCache);
        Assert.Empty(lines.StartCacheChecks());
        Assert.All(lines.Lines, line => Assert.Equal(TorrentCacheState.None, line.Cache));
        Assert.All(lines.Lines, line => Assert.Equal(string.Empty, line.CacheText(null)));
    }

    [Fact]
    public void EachReadLineIsCheckedOnceByItsHashAlone()
    {
        var lines = new TorrentAddLines(TorrentLineRoute.Unset, true, "TorBox");
        lines.Add([MagnetA, FileA], fromPaste: false);

        // Nothing is asked before a line is read.
        Assert.Empty(lines.StartCacheChecks());
        lines.Inspected(lines.Lines[0], Magnet());
        var first = lines.StartCacheChecks();
        var check = Assert.Single(first);
        Assert.Same(lines.Lines[0], check.Line);
        Assert.Equal("magnet:?xt=urn:btih:" + HashA, check.Query);
        Assert.Equal(TorrentCacheState.Checking, lines.Lines[0].Cache);
        Assert.Equal("Checking TorBox…", lines.Lines[0].CacheText("TorBox"));

        // A .torrent file is asked about by its hash: the file itself is not sent for this.
        lines.Inspected(lines.Lines[1], File(HashB.ToUpperInvariant(), 1024));
        var second = Assert.Single(lines.StartCacheChecks());
        Assert.Equal("magnet:?xt=urn:btih:" + HashB, second.Query);
        Assert.Empty(lines.StartCacheChecks());
    }

    [Fact]
    public void TheAnswerSaysCachedNotCachedOrCouldNotCheck()
    {
        var lines = new TorrentAddLines(TorrentLineRoute.ThisPc, true, "TorBox");
        lines.Add([MagnetA, FileA, FileB], fromPaste: false);
        lines.Inspected(lines.Lines[0], Magnet());
        lines.Inspected(lines.Lines[1], File(HashB, 1024));
        lines.Inspected(lines.Lines[2], File("cdef0123456789abcdef0123456789abcdef0123", 1024));
        Assert.Equal(3, lines.StartCacheChecks().Count);

        Assert.True(lines.CacheChecked(lines.Lines[0], new RemoteClassifyResult { Ok = true, Kind = "magnet", Cached = true }));
        Assert.True(lines.CacheChecked(lines.Lines[1], new RemoteClassifyResult { Ok = true, Kind = "magnet", Cached = false }));
        Assert.True(lines.CacheChecked(lines.Lines[2], new RemoteClassifyResult { Ok = true, Kind = "magnet", Cached = null }));
        Assert.Equal("Cached on TorBox", lines.Lines[0].CacheText("TorBox"));
        Assert.Equal("Not cached on TorBox", lines.Lines[1].CacheText("TorBox"));
        Assert.Equal("Couldn't check TorBox", lines.Lines[2].CacheText("TorBox"));

        lines.CacheChecked(lines.Lines[0], new RemoteClassifyResult { Error = "The Host couldn't be reached." });
        Assert.Equal(TorrentCacheState.Unknown, lines.Lines[0].Cache);
        lines.CacheChecked(lines.Lines[1], null);
        Assert.Equal(TorrentCacheState.Unknown, lines.Lines[1].Cache);
    }

    // ---- T9 W3: the cache answer as a badge ----------------------------------------------------

    [Fact]
    public void TheCacheAnswerIsABadgeNextToTheProviderChoice()
    {
        var lines = new TorrentAddLines(TorrentLineRoute.ThisPc, true, "TorBox");
        lines.Add([MagnetA, FileA, FileB], fromPaste: false);
        Assert.Null(lines.Lines[0].Badge("TorBox"));
        lines.Inspected(lines.Lines[0], Magnet());
        lines.Inspected(lines.Lines[1], File(HashB, 1024));
        lines.Inspected(lines.Lines[2], File("cdef0123456789abcdef0123456789abcdef0123", 1024));
        lines.StartCacheChecks();
        Assert.Equal(new CacheBadge(CacheBadgeKind.Checking, "Checking TorBox…", "", null), lines.Lines[0].Badge("TorBox"));

        lines.CacheChecked(lines.Lines[0], new RemoteClassifyResult { Ok = true, Cached = true });
        lines.CacheChecked(lines.Lines[1], new RemoteClassifyResult { Ok = true, Cached = false });
        lines.CacheChecked(lines.Lines[2], new RemoteClassifyResult { Error = "TorBox refused the API key. Check it, then save it again." });

        var cached = lines.Lines[0].Badge("TorBox")!;
        Assert.Equal(CacheBadgeKind.Cached, cached.Kind);
        Assert.Equal("Cached on TorBox", cached.Text);
        Assert.Equal("TorBox has it, so it downloads right away", cached.Note);
        Assert.Null(cached.ToolTip);

        var notCached = lines.Lines[1].Badge("TorBox")!;
        Assert.Equal(CacheBadgeKind.NotCached, notCached.Kind);
        Assert.Equal("Not cached on TorBox", notCached.Text);
        Assert.Equal(string.Empty, notCached.Note);

        var unknown = lines.Lines[2].Badge("TorBox")!;
        Assert.Equal(CacheBadgeKind.Unknown, unknown.Kind);
        Assert.Equal("Couldn't check TorBox", unknown.Text);
        Assert.Equal("TorBox refused the API key. Check it, then save it again.", unknown.ToolTip);
        Assert.Equal("Couldn't check TorBox. TorBox refused the API key. Check it, then save it again.", unknown.Spoken);

        // No reason given: the tooltip still says something.
        lines.CacheChecked(lines.Lines[2], null);
        Assert.Equal("TorBox didn't answer the check.", lines.Lines[2].Badge("TorBox")!.ToolTip);
        Assert.Null(lines.Lines[2].Badge(null));
    }

    [Theory]
    [InlineData("Cached on TorBox", true)]
    [InlineData("Ready to download", false)]
    [InlineData("Not cached on TorBox", false)]
    [InlineData(null, false)]
    public void ACachedStatusIsShownAsTheSameBadge(string? status, bool filled)
        => Assert.Equal(filled, CacheBadge.IsCachedWords(status));

    [Fact]
    public void TheCacheNeverPicksTheRoute()
    {
        var lines = new TorrentAddLines(TorrentLineRoute.Unset, true, "TorBox");
        lines.Add([MagnetA, FileA], fromPaste: false);
        lines.Inspected(lines.Lines[0], Magnet());
        lines.Inspected(lines.Lines[1], File(HashB, 1024));
        lines.SetRoute(lines.Lines[1], TorrentLineRoute.ThisPc);
        lines.StartCacheChecks();
        lines.CacheChecked(lines.Lines[0], new RemoteClassifyResult { Ok = true, Cached = true });
        lines.CacheChecked(lines.Lines[1], new RemoteClassifyResult { Ok = true, Cached = true });

        Assert.Equal(TorrentLineRoute.Unset, lines.Lines[0].Route);
        Assert.Equal(TorrentLineRoute.ThisPc, lines.Lines[1].Route);
        Assert.False(lines.CanAdd);
    }

    [Fact]
    public void LinesThatCantBeAddedOrHaveNoV1HashAreNotAsked()
    {
        var lines = new TorrentAddLines(TorrentLineRoute.ThisPc, true, "TorBox");
        lines.Add([MagnetA, FileA, FileB], fromPaste: false);
        lines.Inspected(lines.Lines[0], Magnet(existing: Guid.NewGuid()));
        lines.Inspected(lines.Lines[1], new TorrentInspectResult { Error = "This torrent couldn't be read." });
        lines.Inspected(lines.Lines[2], File(new string('a', 64), 1024));

        Assert.Empty(lines.StartCacheChecks());
        Assert.Equal(TorrentCacheState.None, lines.Lines[0].Cache);
        Assert.Equal(TorrentCacheState.None, lines.Lines[1].Cache);
        Assert.Equal(TorrentCacheState.Unknown, lines.Lines[2].Cache);
    }

    [Fact]
    public void AnAnswerForALineThatWentIsDropped()
    {
        var lines = new TorrentAddLines(TorrentLineRoute.ThisPc, true, "TorBox");
        lines.SyncPaste(MagnetA);
        var line = lines.Lines[0];
        lines.Inspected(line, Magnet());
        Assert.Single(lines.StartCacheChecks());
        lines.SyncPaste(string.Empty);

        Assert.False(lines.IsListed(line));
        Assert.False(lines.CacheChecked(line, new RemoteClassifyResult { Ok = true, Cached = true }));
    }
}
