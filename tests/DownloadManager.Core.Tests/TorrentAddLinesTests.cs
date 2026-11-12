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
    public void WithAskEachTimeEveryLineWaitsForARoute()
    {
        var lines = new TorrentAddLines(TorrentLineRoute.Unset, true, "TorBox");
        lines.Add([MagnetA], fromPaste: false);
        lines.Inspected(lines.Lines[0], Magnet());
        Assert.Equal(TorrentLineRoute.Unset, lines.Lines[0].Route);
        Assert.False(lines.CanAdd);
        Assert.Equal("Choose This PC or TorBox for each ticked torrent.", lines.Problem());
        lines.SetRoute(lines.Lines[0], TorrentLineRoute.Provider);
        Assert.True(lines.CanAdd);
        var pick = Assert.Single(lines.Picks());
        Assert.Equal(TorrentLineRoute.Provider, pick.Route);
        Assert.Equal("all", pick.Files);
        Assert.Null(pick.FileIndexes);
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
    public void OnTheProviderTheFileChoiceIsNotUsed()
    {
        var lines = new TorrentAddLines(TorrentLineRoute.ThisPc, true, "TorBox");
        lines.Add([FileA], fromPaste: false);
        var line = lines.Lines[0];
        lines.Inspected(line, File(HashA, 1024, 2048, 4096));
        lines.SetFileChoice(line, new RemoteFileChoice(false, ["1"]));
        Assert.Equal("chosen", Assert.Single(lines.Picks()).Files);

        // Its own flow asks about the files.
        lines.SetRoute(line, TorrentLineRoute.Provider);
        Assert.False(lines.CanChooseFilesFor(line));
        Assert.Equal(".torrent file · 7.0 KB · 3 files", line.Detail);
        var pick = Assert.Single(lines.Picks());
        Assert.Equal("all", pick.Files);
        Assert.Null(pick.FileIndexes);
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
