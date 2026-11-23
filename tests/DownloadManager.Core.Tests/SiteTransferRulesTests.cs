using DownloadManager.Contracts;
using DownloadManager.Desktop.WinUI;

namespace DownloadManager.Core.Tests;

/// <summary>SITES.4: what the Transfers bar, the sidebar and the status chip say, and which buttons a batch offers.</summary>
public sealed class SiteTransferRulesTests
{
    private static SiteTransferDto Batch(string state, string direction = "download", int files = 4, int done = 0, int skipped = 0,
        int failed = 0, int running = 0, long speed = 0, string site = "s1", string? message = null, string? problem = null, int minute = 0) => new()
    {
        Id = Guid.NewGuid().ToString("N"), SiteId = site, SiteName = "Home NAS", Direction = direction, RemoteFolder = "/pub",
        LocalFolder = @"C:\In", State = state, Files = files, FilesDone = done, FilesSkipped = skipped, FilesFailed = failed,
        FilesRunning = running, Bytes = 4096, BytesDone = 1024, Speed = speed, Message = message, Problem = problem,
        CreatedAt = new DateTimeOffset(2026, 11, 15, 10, minute, 0, TimeSpan.Zero),
    };

    [Fact]
    public void TabsFollowTheStateNewestFirst()
    {
        var older = NotStarted(Batch("running", minute: 1));
        var newer = Batch("paused", minute: 2);
        var all = new[] { older, newer, Batch("failed"), Batch("done"), Batch("canceled") };
        // Running and waiting: started first, then paused, then waiting (not started, or waiting for its server), newest first in each.
        Assert.Equal([newer, older], SiteTransferRules.InTab(all, SiteTransferRules.ActiveTab));
        var moving = Batch("running", running: 1, minute: 0);
        var moving2 = Batch("running", running: 2, minute: 5);
        var waits = NotStarted(Batch("running", minute: 9));
        // Between two files (none moving this second) it has started all the same.
        var between = NotStarted(Batch("running", done: 1, minute: 3));
        var bytesOnly = NotStarted(Batch("running", minute: 4));
        bytesOnly.BytesDone = 10;
        var retrying = Batch("running", done: 1, running: 0, minute: 8);
        retrying.RetryAt = DateTimeOffset.UnixEpoch;
        Assert.Equal([moving2, bytesOnly, between, moving, newer, waits, retrying, older],
            SiteTransferRules.InTab([.. all, moving, waits, moving2, between, bytesOnly, retrying], SiteTransferRules.ActiveTab));
        var failedOld = Batch("failed", minute: 1);
        var failedNew = Batch("failed", minute: 3);
        Assert.Equal([failedNew, failedOld], SiteTransferRules.InTab([failedOld, failedNew], SiteTransferRules.FailedTab));
        Assert.Single(SiteTransferRules.InTab(all, SiteTransferRules.FailedTab));
        Assert.Equal(2, SiteTransferRules.InTab(all, SiteTransferRules.DoneTab).Count);
        Assert.Equal("Running and waiting (2)", SiteTransferRules.TabLabel(all, SiteTransferRules.ActiveTab));
        Assert.Equal("Failed (0)", SiteTransferRules.TabLabel([], SiteTransferRules.FailedTab));
    }

    private static SiteTransferDto NotStarted(SiteTransferDto t)
    {
        t.BytesDone = 0;
        return t;
    }

    [Fact]
    public void ButtonsMatchWhatTheHostAccepts()
    {
        var running = Batch("running", running: 1);
        Assert.True(SiteTransferRules.CanPause(running));
        Assert.True(SiteTransferRules.CanCancel(running));
        Assert.False(SiteTransferRules.CanResume(running) || SiteTransferRules.CanClear(running) || SiteTransferRules.CanRetry(running));

        var paused = Batch("paused");
        Assert.True(SiteTransferRules.CanResume(paused) && SiteTransferRules.CanCancel(paused));

        // Stopped (connections, sign-in) with files still waiting: Resume and Cancel, and Clear.
        var stopped = Batch("failed", done: 1, message: "The server can't be reached.");
        Assert.Equal(3, SiteTransferRules.Waiting(stopped));
        Assert.True(SiteTransferRules.CanResume(stopped) && SiteTransferRules.CanCancel(stopped) && SiteTransferRules.CanClear(stopped));
        Assert.False(SiteTransferRules.CanRetry(stopped));

        // Files ran out of tries: Retry and Clear, nothing to resume.
        var spent = Batch("failed", done: 3, failed: 1);
        Assert.True(SiteTransferRules.CanRetry(spent) && SiteTransferRules.CanClear(spent));
        Assert.False(SiteTransferRules.CanResume(spent) || SiteTransferRules.CanCancel(spent));

        Assert.False(SiteTransferRules.CanCancel(Batch("canceled")));
        Assert.True(SiteTransferRules.CanClear(Batch("done", done: 4)));
    }

    [Fact]
    public void AStoppedBatchOffersTheFixBrowsingOffersForItsProblem()
    {
        // SITES.10 (6): the sign-in, certificate and key a listing asks about are asked about here too, in its words.
        Assert.Equal(SiteProblems.SignInNeeded, SiteTransferRules.FixableProblem(Batch("failed", problem: SiteProblems.SignInNeeded)));
        Assert.Equal(SiteProblems.CertificateNotTrusted, SiteTransferRules.FixableProblem(Batch("failed", problem: SiteProblems.CertificateNotTrusted)));
        Assert.Equal(SiteProblems.HostKeyChanged, SiteTransferRules.FixableProblem(Batch("failed", problem: SiteProblems.HostKeyChanged)));
        Assert.Equal("Sign in", SitesBrowseRules.FixButtonText(SiteTransferRules.FixableProblem(Batch("failed", problem: SiteProblems.SignInNeeded))));
        // Nothing to fix on another failure, on a batch still moving, or on one whose files spent their tries.
        Assert.Null(SiteTransferRules.FixableProblem(Batch("failed", done: 3, failed: 1, message: "The file is gone.")));
        Assert.Null(SiteTransferRules.FixableProblem(Batch("running", running: 1, problem: SiteProblems.SignInNeeded)));
        Assert.Null(SiteTransferRules.FixableProblem(Batch("paused", problem: SiteProblems.SignInNeeded)));
    }

    [Fact]
    public void StoppedFilesDoNotWaitAndClearDoneLeavesFailures()
    {
        // 4 files: 1 done, 2 stopped, none running: only 1 still waits, so a failed batch with nothing else left can't resume.
        var stopped = Stopped(Batch("failed", done: 1), 2);
        Assert.Equal(1, SiteTransferRules.Waiting(stopped));
        var allStopped = Stopped(Batch("failed", done: 1, failed: 1), 2);
        Assert.Equal(0, SiteTransferRules.Waiting(allStopped));
        Assert.False(SiteTransferRules.CanResume(allStopped));

        var done = Batch("done", done: 4);
        var canceled = Batch("canceled");
        var failed = Batch("failed", failed: 1);
        Assert.Equal([done.Id, canceled.Id], SiteTransferRules.ClearDoneTargets([Batch("running"), done, failed, canceled]));
        Assert.Empty(SiteTransferRules.ClearDoneTargets([failed, Batch("paused")]));
    }

    private static List<string> Apply(List<string> rows, string[] desired, out int moves)
    {
        moves = SiteTransferRules.Reconcile(rows, desired, (from, to) =>
        {
            var row = rows[from];
            rows.RemoveAt(from);
            rows.Insert(to, row);
        });
        return rows;
    }

    [Fact]
    public void TheListMovesOnlyTheRowsThatChangedPlace()
    {
        string a = "a", b = "b", c = "c", d = "d", e = "e";
        Assert.Equal([b, c, d, a], Apply([a, b, c, d], [b, c, d, a], out var moves));
        Assert.Equal(1, moves);
        Assert.Equal([d, a, b, c], Apply([a, b, c, d], [d, a, b, c], out moves));
        Assert.Equal(1, moves);
        // Gone rows go, new ones come in, nothing else moves.
        Assert.Equal([a, e, c], Apply([a, b, c], [a, e, c], out moves));
        Assert.Equal(0, moves);
        // A row asked for twice (two files of one path) is kept once.
        Assert.Equal([a, b], Apply([b], [a, b, a], out _));
        Assert.Equal([e, d, c, b, a], Apply([a, b, c, d, e], [e, d, c, b, a], out _));
        Assert.Empty(Apply([a, b], [], out _));
    }

    [Fact]
    public void OpenOnABatchRowFindsWhatLandedAndMarksOnlyCopiedFiles()
    {
        var one = Picked(Batch("done", files: 1, done: 1), 1, "a.iso");
        var renamed = new SiteTransferItemDto { Path = "a.iso", Target = "a (2).iso", State = "done" };
        var (path, file) = SiteTransferRules.PickedLocal(one, [renamed]);
        Assert.Equal(Path.Combine(@"C:\In", "a (2).iso"), path);
        Assert.Same(renamed, file);

        var folder = Picked(Batch("done", files: 3, done: 3), 1, "photos");
        var (folderPath, none) = SiteTransferRules.PickedLocal(folder, [new SiteTransferItemDto { Path = "photos/x.jpg", Target = "photos (2)/x.jpg" }]);
        Assert.Equal(Path.Combine(@"C:\In", "photos (2)"), folderPath);
        Assert.Null(none);
        Assert.Null(SiteTransferRules.PickedLocal(folder, []).Path);
        Assert.Null(SiteTransferRules.PickedLocal(Picked(Batch("done", "upload", files: 1), 1, "a.iso"), [renamed]).Path);

        var batch = Batch("done", files: 2, done: 1, skipped: 1);
        Assert.True(SiteTransferRules.OpensAsDownloaded(new(batch, new SiteTransferItemDto { Path = "a", State = "done" })));
        Assert.False(SiteTransferRules.OpensAsDownloaded(new(batch, new SiteTransferItemDto { Path = "b", State = "skipped" })));
        Assert.False(SiteTransferRules.OpensAsDownloaded(new(Batch("done", files: 1, skipped: 1))));
        Assert.False(SiteTransferRules.OpensAsDownloaded(new(Batch("done", "upload", files: 1, done: 1))));

        Assert.Equal(TimeSpan.FromSeconds(2), SiteTransferRules.FilesReadInterval(1000));
        Assert.Equal(TimeSpan.FromSeconds(10), SiteTransferRules.FilesReadInterval(1001));
    }

    [Fact]
    public void ItemsTitleAndPausedAndOnlyNewDescriptions()
    {
        // One file and one folder picked: "items", whatever the file count.
        var mixed = Picked(Batch("running", files: 2), 2, "a.txt", "photos");
        mixed.Folders = 1;
        Assert.Equal("Download 2 items", SiteTransferRules.Title(mixed));

        // Paused by the user (no message): the route; paused for a reason: the reason.
        Assert.Equal(@"Home NAS to C:\In", SiteTransferRules.Description(Batch("paused")));
        Assert.Equal("Paused: the disk is full.", SiteTransferRules.Description(Batch("paused", message: "Paused: the disk is full.")));

        var onlyNew = Batch("running");
        onlyNew.Conflict = "skip";
        Assert.Equal(@"Home NAS to C:\In · only new files", SiteTransferRules.Description(onlyNew));
        onlyNew.Conflict = "replace";
        Assert.Equal(@"Home NAS to C:\In", SiteTransferRules.Description(onlyNew));
    }

    private static SiteTransferDto Picked(SiteTransferDto t, int count, params string[] names)
    {
        t.PickedCount = count;
        t.Picked = [.. names];
        return t;
    }

    [Fact]
    public void RowTextsFollowTheMockup()
    {
        // File Name: what was picked, with the count of a folder; Description: where from and to.
        var one = Picked(Batch("running", files: 1, running: 1), 1, "ubuntu.iso");
        Assert.Equal("Download ubuntu.iso", SiteTransferRules.Title(one));
        var folder = Picked(Batch("running", files: 1204), 1, "photos-2026");
        Assert.Equal("Download photos-2026 (1,204 files)", SiteTransferRules.Title(folder));
        Assert.Equal("Upload 2 files", SiteTransferRules.Title(Picked(Batch("running", "upload", files: 2), 2, "a", "b")));
        Assert.Equal("Upload 3 items", SiteTransferRules.Title(Picked(Batch("running", "upload", files: 40), 3, "a", "b", "c")));
        Assert.Equal("Download from Home NAS", SiteTransferRules.Title(Batch("running")));
        Assert.Equal("Upload to Home NAS", SiteTransferRules.Title(Batch("running", "upload")));
        Assert.Equal(@"Home NAS to C:\In", SiteTransferRules.Route(one));
        Assert.Equal(@"C:\In to Home NAS /pub", SiteTransferRules.Route(Batch("running", "upload")));
        Assert.Equal(@"Home NAS /pub to C:\In", SiteTransferRules.FullRoute(one));
        Assert.Equal(@"Home NAS to C:\In", SiteTransferRules.Description(one));
        Assert.Equal(@"Home NAS to C:\In · 1 skipped (already there) · 1 stopped · 2 failed",
            SiteTransferRules.Description(Stopped(Batch("done", done: 1, skipped: 1, failed: 2), 1)));
    }

    private static SiteTransferDto Stopped(SiteTransferDto t, int stopped)
    {
        t.FilesStopped = stopped;
        return t;
    }

    [Fact]
    public void StatusSaysWhatHappensAndHowFar()
    {
        var down = Batch("running", done: 1, running: 1, speed: 2048);
        down.Bytes = 1000;
        down.BytesDone = 412;
        down.SecondsLeft = 372;
        Assert.Equal(new SiteTransferRules.TransferStatus("Downloading 41.2%", SiteTransferRules.StatusKind.Active, 41.2), SiteTransferRules.Status(down));
        Assert.Equal("Uploading 41.2%", SiteTransferRules.Status(new SiteTransferDto
        {
            State = "running", Direction = "upload", FilesRunning = 1, Files = 1, Bytes = 1000, BytesDone = 412,
        }).Text);
        Assert.Equal("6m 12s", SiteTransferRules.TimeLeft(down));
        Assert.Equal("2.0 KB/s", SiteTransferRules.RateText(down));
        Assert.Equal("", SiteTransferRules.TimeLeft(Batch("paused")));
        Assert.Equal("", SiteTransferRules.RateText(Batch("running"))); // waiting: no file moves

        // Failed and stopped files are set aside, so the bar reaches 100 % with the rest.
        var aside = Batch("running", running: 1);
        aside.Bytes = 1000;
        aside.BytesDone = 300;
        aside.BytesSetAside = 700;
        Assert.Equal(100, SiteTransferRules.Progress(aside));

        Assert.Equal(("Waiting", SiteTransferRules.StatusKind.Waiting), (SiteTransferRules.Status(Batch("running")).Text, SiteTransferRules.Status(Batch("running")).Kind));
        Assert.Equal("Paused", SiteTransferRules.Status(Batch("paused")).Text);
        Assert.Equal(SiteTransferRules.StatusKind.Failed, SiteTransferRules.Status(Batch("failed", failed: 1)).Kind);
        Assert.Equal(SiteTransferRules.StatusKind.Done, SiteTransferRules.Status(Batch("done", done: 4)).Kind);
        Assert.Equal("Completed", SiteTransferRules.Status(Batch("done", done: 4)).Text);
        Assert.Equal("Stopped", SiteTransferRules.Status(Stopped(Batch("done"), 4)).Text);
        Assert.Equal("Canceled", SiteTransferRules.Status(Batch("canceled")).Text);
    }

    [Fact]
    public void ADescriptionSaysWhyItFailedOrWaits()
    {
        var failed = Batch("failed", failed: 1, files: 1);
        failed.FirstFailedError = "Home NAS refused the write to /media/isos.";
        failed.FirstFailedPath = "render.mov";
        Assert.Equal("Home NAS refused the write to /media/isos.", SiteTransferRules.Description(failed));
        failed.Files = 3;
        Assert.Equal("render.mov: Home NAS refused the write to /media/isos.", SiteTransferRules.Description(failed));
        Assert.Equal("The server can't be reached.", SiteTransferRules.Description(Batch("failed", message: "The server can't be reached.")));
        Assert.Equal("2 of 4 files failed", SiteTransferRules.Description(Batch("failed", failed: 2)));

        var waiting = Batch("running");
        waiting.RetryAt = new DateTimeOffset(new DateTime(2026, 11, 16, 14, 32, 0, DateTimeKind.Local));
        Assert.Equal("Waiting for Home NAS, trying again at 14:32.", SiteTransferRules.Description(waiting));
        waiting.RetryReason = "Connection timed out.";
        Assert.Equal("Waiting for Home NAS, trying again at 14:32. Connection timed out.", SiteTransferRules.Description(waiting));
        // Once the wait is over the route is back.
        waiting.RetryAt = null;
        Assert.StartsWith(@"C:\In to ", SiteTransferRules.Description(Batch("running", "upload")));
        Assert.Equal("Paused when remote files were turned off.", SiteTransferRules.Description(Batch("paused", message: "Paused when remote files were turned off.")));
    }

    [Fact]
    public void AFilesCellsFollowItsStateAndTheMovingReport()
    {
        var batch = Batch("running", running: 1);
        batch.Moving = [new SiteTransferMovingDto { Path = "a/big.bin", Size = 2000, BytesDone = 500, Speed = 1024, SecondsLeft = 90 }];
        var big = new SiteTransferItemDto { Path = "a/big.bin", State = "running", Size = 2000 };
        Assert.Equal("Downloading 25.0%", SiteTransferRules.Status(batch, big).Text);
        Assert.Equal(25, SiteTransferRules.Status(batch, big).Percent);
        Assert.Equal("1m 30s", SiteTransferRules.TimeLeft(batch, big));
        Assert.Equal("1.0 KB/s", SiteTransferRules.RateText(batch, big));
        // The files list was read before the file began: the Host's report wins.
        var early = new SiteTransferItemDto { Path = "a/big.bin", State = "waiting", Size = 2000 };
        Assert.Equal("Downloading 25.0%", SiteTransferRules.Status(batch, early).Text);
        // No report: no bar, no rate.
        var quiet = new SiteTransferItemDto { Path = "a/other.bin", State = "running", Size = 10 };
        Assert.Null(SiteTransferRules.Status(batch, quiet).Percent);
        Assert.Equal("", SiteTransferRules.RateText(batch, quiet));

        Assert.Equal("Waiting to retry", SiteTransferRules.Status(batch, new SiteTransferItemDto { Path = "x", State = "waiting", Tries = 1 }).Text);
        Assert.Equal("Stopped", SiteTransferRules.Status(batch, new SiteTransferItemDto { Path = "x", State = "stopped" }).Text);
        Assert.Equal("Failed", SiteTransferRules.Status(batch, new SiteTransferItemDto { Path = "x", State = "failed", Error = "Denied." }).Text);
        Assert.Equal("Denied.", SiteTransferRules.Description(new SiteTransferItemDto { Path = "x", State = "failed", Error = "Denied." }));
        Assert.Equal("Already there", SiteTransferRules.Description(new SiteTransferItemDto { Path = "x", State = "skipped" }));
        Assert.Equal("Empty folder made", SiteTransferRules.Description(new SiteTransferItemDto { Path = "x", State = "done", IsFolder = true }));
        Assert.Equal("1.0 KB", SiteTransferRules.SizeText(new SiteTransferItemDto { Path = "x", State = "done", Size = 1024 }));
        Assert.Equal("", SiteTransferRules.SizeText(new SiteTransferItemDto { Path = "x", State = "waiting", Size = -1 }));
    }

    [Fact]
    public void ABatchOfThousandsListsWhatNeedsEyesFirstAndPagesTheRest()
    {
        var items = new List<SiteTransferItemDto>();
        for (var index = 0; index < 1204; index++) items.Add(new SiteTransferItemDto { Path = $"p/{index:D4}.jpg", State = "done" });
        items.Add(new SiteTransferItemDto { Path = "z-waiting.jpg", State = "waiting" });
        items.Add(new SiteTransferItemDto { Path = "y-running.jpg", State = "running" });
        items.Add(new SiteTransferItemDto { Path = "x-failed.jpg", State = "failed" });
        items.Add(new SiteTransferItemDto { Path = "w-stopped.jpg", State = "stopped" });
        var ordered = SiteTransferRules.OrderItems(items);
        Assert.Equal(["y-running.jpg", "x-failed.jpg", "w-stopped.jpg", "z-waiting.jpg", "p/0000.jpg"], ordered.Take(5).Select(i => i.Path));
        Assert.Equal(500, SiteTransferRules.FilesPerPage);
        Assert.Equal("Show more (704 more files)", SiteTransferRules.MoreText(704));
        Assert.Equal("Show more (1 more file)", SiteTransferRules.MoreText(1));
        // The list is read again only when a count changed.
        var batch = Batch("running", running: 1);
        var before = SiteTransferRules.FilesSignature(batch);
        batch.BytesDone += 100;
        batch.Speed += 5;
        Assert.Equal(before, SiteTransferRules.FilesSignature(batch));
        batch.FilesDone++;
        Assert.NotEqual(before, SiteTransferRules.FilesSignature(batch));
    }

    private static SiteTransferRules.TransferRef File(SiteTransferDto batch, string state, string path = "a/f.bin", bool folder = false) =>
        new(batch, new SiteTransferItemDto { Path = path, State = state, IsFolder = folder, Size = 10 });

    private static List<SiteTransferRules.TransferCommand> Flat(IReadOnlyList<SiteTransferRules.TransferRef> selection) =>
        [.. SiteTransferRules.Menu(selection).SelectMany(group => group)];

    [Fact]
    public void ARowOffersOnlyTheCommandsItsStateAllows()
    {
        var running = Batch("running", running: 1);
        Assert.Equal([SiteTransferRules.TransferCommand.Stop, SiteTransferRules.TransferCommand.ShowOnRemote, SiteTransferRules.TransferCommand.Cancel],
            Flat([new(running)]));
        // A download that has copied nothing yet has nothing to open; an upload's source is always there.
        Assert.DoesNotContain(SiteTransferRules.TransferCommand.Open, Flat([new(running)]));
        Assert.Contains(SiteTransferRules.TransferCommand.Open, Flat([new(Batch("running", "upload", running: 1))]));

        Assert.Equal(
            [SiteTransferRules.TransferCommand.Resume, SiteTransferRules.TransferCommand.ShowOnRemote, SiteTransferRules.TransferCommand.Cancel],
            Flat([new(Batch("paused"))]));
        // Failed with three files copied: Retry, the copied ones can be opened, nothing to resume or cancel.
        Assert.Equal(
            [SiteTransferRules.TransferCommand.Retry, SiteTransferRules.TransferCommand.Open, SiteTransferRules.TransferCommand.OpenFolder,
                SiteTransferRules.TransferCommand.ShowOnRemote, SiteTransferRules.TransferCommand.Remove],
            Flat([new(Batch("failed", done: 3, failed: 1))]));
        Assert.Equal(
            [SiteTransferRules.TransferCommand.Open, SiteTransferRules.TransferCommand.OpenFolder, SiteTransferRules.TransferCommand.ShowOnRemote, SiteTransferRules.TransferCommand.Remove],
            Flat([new(Batch("done", done: 4))]));
        // Nothing copied from a download: nothing to open.
        Assert.Equal([SiteTransferRules.TransferCommand.ShowOnRemote, SiteTransferRules.TransferCommand.Remove], Flat([new(Batch("canceled"))]));
        Assert.Empty(SiteTransferRules.Menu([]));
    }

    [Fact]
    public void FileRowsStopAndStartOneFileAndOpenWhatLanded()
    {
        var running = Batch("running", running: 1);
        Assert.Equal([SiteTransferRules.TransferCommand.StopFile, SiteTransferRules.TransferCommand.ShowOnRemote], Flat([File(running, "waiting")]));
        Assert.Contains(SiteTransferRules.TransferCommand.StopFile, Flat([File(running, "running")]));
        Assert.Equal([SiteTransferRules.TransferCommand.StartFile, SiteTransferRules.TransferCommand.ShowOnRemote], Flat([File(running, "stopped")]));
        Assert.Contains(SiteTransferRules.TransferCommand.StartFile, Flat([File(Batch("done"), "failed")]));
        Assert.Contains(SiteTransferRules.TransferCommand.StartFile, Flat([File(Batch("failed", failed: 1), "failed")]));
        // A done file can be opened; a finished batch's waiting file does not exist, a canceled batch's file starts nothing.
        Assert.Equal(
            [SiteTransferRules.TransferCommand.Open, SiteTransferRules.TransferCommand.OpenFolder, SiteTransferRules.TransferCommand.ShowOnRemote],
            Flat([File(Batch("done", done: 4), "done")]));
        Assert.DoesNotContain(SiteTransferRules.TransferCommand.StartFile, Flat([File(Batch("canceled"), "stopped")]));
        // A made empty folder is not stopped and is not "started".
        Assert.DoesNotContain(SiteTransferRules.TransferCommand.StopFile, Flat([File(running, "waiting", "a/empty", folder: true)]));
        // A file row never offers the batch's commands.
        Assert.DoesNotContain(SiteTransferRules.TransferCommand.Remove, Flat([File(Batch("done", done: 4), "done")]));
        Assert.DoesNotContain(SiteTransferRules.TransferCommand.Stop, Flat([File(running, "running")]));
        // An upload's file can be opened from the source before it is sent.
        Assert.Contains(SiteTransferRules.TransferCommand.Open, Flat([File(Batch("running", "upload", running: 1), "waiting")]));
    }

    [Fact]
    public void ManyRowsOfferWhatAnyOfThemCanDoAndOnlyOneRowOpens()
    {
        var running = Batch("running", running: 1);
        var failed = Batch("failed", failed: 1);
        var many = Flat([new(running), new(failed)]);
        Assert.Contains(SiteTransferRules.TransferCommand.Stop, many);
        Assert.Contains(SiteTransferRules.TransferCommand.Retry, many);
        Assert.Contains(SiteTransferRules.TransferCommand.Remove, many);
        Assert.DoesNotContain(SiteTransferRules.TransferCommand.ShowOnRemote, many);
        Assert.DoesNotContain(SiteTransferRules.TransferCommand.Open, many);
        // Valid says it per row, so a command acts only on the rows it fits.
        Assert.False(SiteTransferRules.Valid(SiteTransferRules.TransferCommand.Stop, new(failed)));
        Assert.True(SiteTransferRules.Valid(SiteTransferRules.TransferCommand.Retry, new(failed)));
    }

    [Fact]
    public void DoubleClickOpensAFinishedRowAndSpaceStopsOrResumes()
    {
        Assert.Equal(SiteTransferRules.TransferCommand.Open, SiteTransferRules.Primary(new(Batch("done", done: 4))));
        Assert.Null(SiteTransferRules.Primary(new(Batch("running", running: 1))));
        Assert.Null(SiteTransferRules.Primary(new(Batch("failed", failed: 1))));
        Assert.Null(SiteTransferRules.Primary(new(Batch("done"))));
        Assert.Equal(SiteTransferRules.TransferCommand.Open, SiteTransferRules.Primary(File(Batch("done", done: 4), "done")));
        Assert.Null(SiteTransferRules.Primary(File(Batch("running", running: 1), "waiting")));
        Assert.Equal(SiteTransferRules.TransferCommand.Stop, SiteTransferRules.SpaceCommand([new(Batch("running", running: 1))]));
        Assert.Equal(SiteTransferRules.TransferCommand.Resume, SiteTransferRules.SpaceCommand([new(Batch("paused"))]));
        Assert.Null(SiteTransferRules.SpaceCommand([new(Batch("done", done: 1))]));
        Assert.Equal("Remove from list", SiteTransferRules.Label(SiteTransferRules.TransferCommand.Remove));
        Assert.Equal("Show on the remote", SiteTransferRules.Label(SiteTransferRules.TransferCommand.ShowOnRemote));
        Assert.Equal("Delete", SiteTransferRules.KeyText(SiteTransferRules.TransferCommand.Remove));
    }

    [Fact]
    public void OpenActsOnThisPcForBothDirectionsAndShowGoesToTheRemoteFolder()
    {
        var download = Picked(Batch("done", done: 1, files: 1), 1, "iso.bin");
        Assert.Equal(Path.Combine(@"C:\In", "iso.bin"), SiteTransferRules.LocalTarget(new(download)));
        var upload = Picked(Batch("done", "upload", done: 1, files: 1), 1, "report.pdf");
        Assert.Equal(Path.Combine(@"C:\In", "report.pdf"), SiteTransferRules.LocalTarget(new(upload)));
        // Several names (or an older batch): the folder.
        Assert.Equal(@"C:\In", SiteTransferRules.LocalTarget(new(Picked(Batch("done", done: 2), 2, "a", "b"))));
        Assert.Equal(@"C:\In", SiteTransferRules.LocalTarget(new(Batch("done", done: 2))));
        // A file row: a download's target name (Keep both), an upload's source.
        var item = new SiteTransferItemDto { Path = "sub/a.txt", Target = "sub/a (2).txt", State = "done" };
        Assert.Equal(Path.Combine(@"C:\In", "sub", "a (2).txt"), SiteTransferRules.LocalTarget(new(Batch("done", done: 1), item)));
        Assert.Equal(Path.Combine(@"C:\In", "sub", "a.txt"), SiteTransferRules.LocalTarget(new(Batch("done", "upload", done: 1), item)));

        Assert.Equal(("/pub", "iso.bin"), SiteTransferRules.RemoteTarget(new(download)));
        Assert.Equal(("/pub", null), SiteTransferRules.RemoteTarget(new(Batch("done"))));
        Assert.Equal(("/pub/sub", "a.txt"), SiteTransferRules.RemoteTarget(new(Batch("done", done: 1), item)));
        Assert.Equal(("/pub/sub", "a (2).txt"), SiteTransferRules.RemoteTarget(new(Batch("done", "upload", done: 1), item)));
        Assert.Equal(("/pub", "top.txt"), SiteTransferRules.RemoteTarget(new(Batch("done", done: 1), new SiteTransferItemDto { Path = "top.txt", State = "done" })));
    }

    [Fact]
    public void TheSlimBarHasASpeedPerDirectionAndCounts()
    {
        var down = Batch("running", running: 1, speed: 2048);
        var up = Batch("running", "upload", running: 1, speed: 1024);
        var all = new[] { down, up, Batch("running"), Batch("running", "upload"), Batch("failed", failed: 1), Batch("done") };
        Assert.Equal(2048, SiteTransferRules.DownSpeed(all));
        Assert.Equal(1024, SiteTransferRules.UpSpeed(all));
        Assert.Equal("2.0 KB/s", SiteTransferRules.SpeedText(2048));
        Assert.Equal("0 B/s", SiteTransferRules.SpeedText(0));
        Assert.Equal("2 running · 2 waiting · 1 failed", SiteTransferRules.SlimSummary(all));
        Assert.Equal("No transfers", SiteTransferRules.SlimSummary([]));
        Assert.Equal("Nothing running", SiteTransferRules.SlimSummary([Batch("done")]));
    }

    [Fact]
    public void TheChipSaysRemoteFilesAndIsQuietOtherwise()
    {
        Assert.Null(SiteTransferRules.ChipText([]));
        Assert.Null(SiteTransferRules.ChipText([Batch("done"), Batch("canceled")]));
        Assert.Equal("Remote files · 2 running · 1 failed · 3.0 KB/s",
            SiteTransferRules.ChipText([Batch("running", running: 1, speed: 1024), Batch("running", running: 1, speed: 2048), Batch("failed", failed: 1)]));
        Assert.Equal("Remote files · 1 waiting · 1 paused", SiteTransferRules.ChipText([Batch("running"), Batch("paused")]));
        Assert.DoesNotContain("Site", SiteTransferRules.ChipText([Batch("running")])!);
    }

    [Fact]
    public void AnUploadThatLandsInTheShownFolderAsksForARelist()
    {
        var running = Batch("running", "upload", done: 0);
        var done = Batch("done", "upload", done: 1);
        done.Id = running.Id;
        Assert.True(SiteTransferRules.UploadLanded([running], [done], "s1", "/pub/"));
        // Already finished before, another folder, another site, a download, or nothing copied: no relist.
        Assert.False(SiteTransferRules.UploadLanded([done], [done], "s1", "/pub"));
        Assert.False(SiteTransferRules.UploadLanded([running], [done], "s1", "/other"));
        Assert.False(SiteTransferRules.UploadLanded([running], [done], "s2", "/pub"));
        var down = Batch("done", done: 1);
        Assert.False(SiteTransferRules.UploadLanded([], [down], "s1", "/pub"));
        Assert.False(SiteTransferRules.UploadLanded([], [Batch("failed", "upload", failed: 4)], "s1", "/pub"));
        // A batch first seen already finished (started and done between two updates) counts.
        Assert.True(SiteTransferRules.UploadLanded([], [done], "s1", "/pub"));
    }

    [Fact]
    public void ACopyBetweenRemotesRefreshesTheTargetFolderNotTheSource()
    {
        var running = Batch("running", "between", done: 0);
        running.TargetSiteId = "s2";
        running.TargetFolder = "/in/";
        var done = Batch("done", "between", done: 1);
        done.Id = running.Id;
        done.TargetSiteId = "s2";
        done.TargetFolder = "/in/";
        Assert.True(SiteTransferRules.CopyLanded([running], [done], "s2", "/in"));
        Assert.False(SiteTransferRules.CopyLanded([done], [done], "s2", "/in"));
        Assert.False(SiteTransferRules.CopyLanded([running], [done], "s1", "/pub"));
        Assert.False(SiteTransferRules.CopyLanded([running], [done], "s2", "/other"));
        // The source side is not refreshed by an upload test either.
        Assert.False(SiteTransferRules.UploadLanded([running], [done], "s1", "/pub"));
    }

    [Fact]
    public void PauseTransfersPausesWhatRunsAndResumesWhenAllArePaused()
    {
        var running = Batch("running");
        var paused = Batch("paused");
        var bulk = SiteTransferRules.BulkPause([running, paused, Batch("done")]);
        Assert.Equal("Pause transfers", bulk!.Value.Label);
        Assert.Equal(SitesMessageTypes.TransferPause, bulk.Value.Type);
        Assert.Equal([running.Id], bulk.Value.Ids);
        var resume = SiteTransferRules.BulkPause([paused, Batch("failed", failed: 1)]);
        Assert.Equal(("Resume transfers", SitesMessageTypes.TransferResume), (resume!.Value.Label, resume.Value.Type));
        Assert.Equal([paused.Id], resume.Value.Ids);
        Assert.Null(SiteTransferRules.BulkPause([Batch("done"), Batch("canceled")]));

        var failed = Batch("failed", failed: 2);
        Assert.Equal([failed.Id], SiteTransferRules.RetryAll([failed, Batch("failed", message: "Stopped."), Batch("done")]));
    }

    [Fact]
    public void TheSidebarLineIsTheBytesOfTheSitesRunningBatches()
    {
        var a = Batch("running");
        a.Bytes = 300; a.BytesDone = 100;
        var b = Batch("running", "upload");
        b.Bytes = 100; b.BytesDone = 100;
        var other = Batch("running", site: "s2");
        other.Bytes = 1000;
        Assert.Equal(50, SiteTransferRules.SiteProgress([a, b, other, Batch("done")], "s1"));
        Assert.Null(SiteTransferRules.SiteProgress([Batch("done")], "s1"));
        Assert.Null(SiteTransferRules.SiteProgress([Batch("running")], "s9"));
    }

    [Fact]
    public void TheSidebarMarkCountsFilesMovingEachWay()
    {
        var all = new[]
        {
            Batch("running", running: 2), Batch("running", "upload", running: 1), Batch("running", minute: 3),
            Batch("running", running: 1, site: "other"), Batch("paused", site: "s1"),
        };
        Assert.Equal("↓2 ↑1 ⏱", SiteTransferRules.SiteMark(all, "s1"));
        Assert.Equal("2 downloading, 1 uploading, transfers waiting", SiteTransferRules.SiteMarkName(all, "s1"));
        Assert.Equal("↓1", SiteTransferRules.SiteMark(all, "other"));
        Assert.Null(SiteTransferRules.SiteMark(all, "none"));
    }

    [Fact]
    public void PickedFilesMustShareAFolder()
    {
        var one = SiteTransferRules.UploadNames([@"C:\Docs\a.txt", @"C:\docs\b.txt"]);
        Assert.Equal(@"C:\Docs", one.Folder);
        Assert.Equal(["a.txt", "b.txt"], one.Names);
        Assert.Null(one.Problem);
        Assert.NotNull(SiteTransferRules.UploadNames([@"C:\A\a.txt", @"C:\B\b.txt"]).Problem);
        Assert.NotNull(SiteTransferRules.UploadNames([]).Problem);
    }

    [Fact]
    public void WhatIsMovedIsNamedPlainly()
    {
        Assert.Equal("report.pdf", SiteTransferRules.WhatText([new SiteTransferNameDto { Name = "report.pdf" }]));
        Assert.Equal("the folder photos", SiteTransferRules.WhatText([new SiteTransferNameDto { Name = "photos", IsFolder = true }]));
        Assert.Equal("2 files and 1 folder", SiteTransferRules.WhatText([
            new SiteTransferNameDto { Name = "a" }, new SiteTransferNameDto { Name = "b" }, new SiteTransferNameDto { Name = "c", IsFolder = true }]));
        Assert.Equal("skip", SiteTransferRules.Conflicts[0].Value);
        Assert.True(SiteTransferRules.IsAction(SitesMessageTypes.TransferPause));
        Assert.False(SiteTransferRules.IsAction(SitesMessageTypes.TransferStart));
    }
}
