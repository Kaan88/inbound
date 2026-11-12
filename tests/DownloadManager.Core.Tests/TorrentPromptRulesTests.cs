using DownloadManager.Contracts;
using DownloadManager.Desktop.WinUI;

namespace DownloadManager.Core.Tests;

/// <summary>T1 step 5 review: Choose Files opens by itself when a magnet's list comes in, and when an open row reads its files.</summary>
public sealed class TorrentPromptRulesTests
{
    private static JobDto Torrent(Guid? id = null, string state = "downloading", bool metadata = true, bool awaiting = false, int files = 11) => new()
    {
        Id = id ?? Guid.NewGuid(),
        Url = "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567",
        State = state,
        Torrent = new TorrentDto { Name = "Film", HasMetadata = metadata, AwaitingFileChoice = awaiting, FileCount = files, WantedCount = files },
    };

    [Fact]
    public void TheChoiceArrivesWhenARowSeesItsTorrentStartWaiting()
    {
        var id = Guid.NewGuid();
        var gettingList = Torrent(id, metadata: false, files: 0);
        var waiting = Torrent(id, awaiting: true);

        Assert.True(TorrentRules.FileChoiceArrived(gettingList, waiting));
        Assert.True(TorrentRules.FileChoiceArrived(Torrent(id), waiting));
    }

    [Fact]
    public void ARowBuiltAlreadyWaitingNeverCounts()
    {
        // A snapshot after a restart or a server switch builds a new row: there is no earlier update.
        Assert.False(TorrentRules.FileChoiceArrived(null, Torrent(awaiting: true)));
    }

    [Fact]
    public void StillWaitingOrNotWaitingIsNoArrival()
    {
        var id = Guid.NewGuid();
        Assert.False(TorrentRules.FileChoiceArrived(Torrent(id, awaiting: true), Torrent(id, awaiting: true)));
        Assert.False(TorrentRules.FileChoiceArrived(Torrent(id, metadata: false), Torrent(id)));
        Assert.False(TorrentRules.FileChoiceArrived(Torrent(id, awaiting: true), Torrent(id)));
        // Waiting without a list is nothing to choose from yet.
        Assert.False(TorrentRules.FileChoiceArrived(Torrent(id, metadata: false), Torrent(id, metadata: false, awaiting: true)));
        // Another job in the same row object isn't this one changing.
        Assert.False(TorrentRules.FileChoiceArrived(Torrent(), Torrent(awaiting: true)));
    }

    [Fact]
    public void TheFirstArrivedWaitingTorrentOpens()
    {
        var first = Torrent(awaiting: true);
        var second = Torrent(awaiting: true);
        var rows = new[] { (Torrent(), false), (first, true), (second, true) };

        Assert.Equal(first.Id, TorrentRules.FileChoiceToOpen(rows, new HashSet<Guid>(), torrentsOffered: true, dialogOpen: false));
    }

    [Fact]
    public void ATorrentIsAskedOnceWhateverTheAnswer()
    {
        var first = Torrent(awaiting: true);
        var second = Torrent(awaiting: true);
        var rows = new[] { (first, true), (second, true) };

        Assert.Equal(second.Id, TorrentRules.FileChoiceToOpen(rows, new HashSet<Guid> { first.Id }, true, false));
        Assert.Null(TorrentRules.FileChoiceToOpen(rows, new HashSet<Guid> { first.Id, second.Id }, true, false));
    }

    [Fact]
    public void NothingOpensWhileADialogIsOpenOrTorrentsArentOffered()
    {
        var rows = new[] { (Torrent(awaiting: true), true) };

        Assert.Null(TorrentRules.FileChoiceToOpen(rows, new HashSet<Guid>(), torrentsOffered: true, dialogOpen: true));
        Assert.Null(TorrentRules.FileChoiceToOpen(rows, new HashSet<Guid>(), torrentsOffered: false, dialogOpen: false));
    }

    [Fact]
    public void OnlyTorrentsThatStillWaitAndCanChooseOpen()
    {
        var rows = new[]
        {
            // Waiting when the list was loaded.
            (Torrent(awaiting: true), false),
            // Arrived, but chosen meanwhile (the CLI or another window).
            (Torrent(), true),
            // Arrived, then cancelled.
            (Torrent(state: "cancelled", awaiting: true), true),
        };

        Assert.Null(TorrentRules.FileChoiceToOpen(rows, new HashSet<Guid>(), true, false));
    }

    [Fact]
    public void AnOpenRowReadsItsFilesAgainOnlyWhenStateCountOrMetadataChange()
    {
        var id = Guid.NewGuid();
        var read = TorrentRules.FilesReadKey(Torrent(id));

        // A progress update changes none of them: no read.
        var progressed = Torrent(id);
        progressed.ReceivedBytes = 12345;
        progressed.Torrent!.Peers = 40;
        Assert.Equal(read, TorrentRules.FilesReadKey(progressed));

        Assert.NotEqual(read, TorrentRules.FilesReadKey(Torrent(id, state: "paused")));
        Assert.NotEqual(read, TorrentRules.FilesReadKey(Torrent(id, files: 12)));
        // A file choice saved anywhere else changes the wanted set.
        var chosen = Torrent(id);
        chosen.Torrent!.WantedCount -= 1;
        Assert.NotEqual(read, TorrentRules.FilesReadKey(chosen));
        Assert.NotEqual(TorrentRules.FilesReadKey(Torrent(id, metadata: false, files: 0)), TorrentRules.FilesReadKey(Torrent(id, files: 0)));
    }

    // ---- Walk fix: one place to choose (Progress Details or the main window's dialog) ----

    [Fact]
    public void AnOpenProgressWindowIsThePlaceToChooseAndNoDialogOpens()
    {
        var waiting = Torrent(awaiting: true);
        var rows = new[] { (waiting, true) };

        var next = TorrentRules.NextFileChoice(rows, new HashSet<Guid>(), true, id => id == waiting.Id, mayOpenDialog: true);
        Assert.Equal((waiting.Id, FileChoicePlace.ProgressWindow), next);

        // Without one, the main window's dialog, as before.
        next = TorrentRules.NextFileChoice(rows, new HashSet<Guid>(), true, _ => false, mayOpenDialog: true);
        Assert.Equal((waiting.Id, FileChoicePlace.Dialog), next);
    }

    [Fact]
    public void TheProgressWindowIsBroughtForwardEvenWhileTheMainWindowCantOpenADialog()
    {
        // Hidden, another dialog open, or already asking about another torrent.
        var inWindow = Torrent(awaiting: true);
        var noWindow = Torrent(awaiting: true);
        var rows = new[] { (noWindow, true), (inWindow, true) };

        Assert.Equal((inWindow.Id, FileChoicePlace.ProgressWindow),
            TorrentRules.NextFileChoice(rows, new HashSet<Guid>(), true, id => id == inWindow.Id, mayOpenDialog: false));
        Assert.Null(TorrentRules.NextFileChoice(rows, new HashSet<Guid> { inWindow.Id }, true, id => id == inWindow.Id, mayOpenDialog: false));
    }

    [Fact]
    public void AChoiceHandedToTheProgressWindowIsAskedOnceSoClosingItOpensNothing()
    {
        // The main window marks it asked once that window came forward: after it closes, no dialog opens.
        var waiting = Torrent(awaiting: true);
        var rows = new[] { (waiting, true) };
        var asked = new HashSet<Guid> { waiting.Id };

        Assert.Null(TorrentRules.NextFileChoice(rows, asked, true, _ => false, mayOpenDialog: true));
    }

    [Fact]
    public void NoProgressWindowIsLookedForWhenNothingWaits()
    {
        var looked = new List<Guid>();
        var rows = new[] { (Torrent(awaiting: true), false), (Torrent(), true), (Torrent(state: "cancelled", awaiting: true), true) };

        Assert.Null(TorrentRules.NextFileChoice(rows, new HashSet<Guid>(), true, id => { looked.Add(id); return true; }, true));
        Assert.Null(TorrentRules.NextFileChoice([(Torrent(awaiting: true), true)], new HashSet<Guid>(), torrentsOffered: false, id => { looked.Add(id); return true; }, true));
        Assert.Empty(looked);
    }
    [Fact]
    public void AChoiceWaitsForTheProgressWindowTheHostIsOpeningThenTakesTheDialog()
    {
        // The file list came in before the window the Host opens for a new download had started.
        var waiting = Torrent(awaiting: true);
        var rows = new[] { (waiting, true) };
        Assert.Null(TorrentRules.NextFileChoice(rows, new HashSet<Guid>(), true, _ => false, mayOpenDialog: true, _ => true));
        // It shows: the choice is made there.
        Assert.Equal((waiting.Id, FileChoicePlace.ProgressWindow),
            TorrentRules.NextFileChoice(rows, new HashSet<Guid>(), true, _ => true, mayOpenDialog: true, _ => true));
        // It never came (or was closed): the dialog.
        Assert.Equal((waiting.Id, FileChoicePlace.Dialog),
            TorrentRules.NextFileChoice(rows, new HashSet<Guid>(), true, _ => false, mayOpenDialog: true, _ => false));
    }

    [Fact]
    public void OnlyAFreshDownloadThisPcsHostOpensAWindowForIsWaitedFor()
    {
        var now = new DateTimeOffset(2026, 11, 11, 18, 0, 0, TimeSpan.Zero);
        JobDto Added(string source = "ui", string start = "startNow", bool other = false, double secondsAgo = 2)
        {
            var job = Torrent(awaiting: true);
            job.Source = source;
            job.StartMode = start;
            job.AddedFromAnotherDevice = other;
            job.CreatedAt = now.AddSeconds(-secondsAgo);
            return job;
        }

        Assert.True(TorrentRules.ProgressWindowComing(Added(), localHost: true, windowSeen: false, now));
        Assert.False(TorrentRules.ProgressWindowComing(Added(), localHost: false, windowSeen: false, now));
        Assert.False(TorrentRules.ProgressWindowComing(Added(), localHost: true, windowSeen: true, now));
        Assert.False(TorrentRules.ProgressWindowComing(Added(secondsAgo: 11), localHost: true, windowSeen: false, now));
        Assert.False(TorrentRules.ProgressWindowComing(Added(start: "addPaused"), localHost: true, windowSeen: false, now));
        Assert.False(TorrentRules.ProgressWindowComing(Added(other: true), localHost: true, windowSeen: false, now));
        foreach (var source in new[] { "cli", "cliBatch", "cliImport", "remote" })
            Assert.False(TorrentRules.ProgressWindowComing(Added(source), localHost: true, windowSeen: false, now));
    }
}
