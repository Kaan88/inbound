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
        var older = Batch("running", minute: 1);
        var newer = Batch("paused", minute: 2);
        var all = new[] { older, newer, Batch("failed"), Batch("done"), Batch("canceled") };
        Assert.Equal([newer, older], SiteTransferRules.InTab(all, SiteTransferRules.ActiveTab));
        Assert.Single(SiteTransferRules.InTab(all, SiteTransferRules.FailedTab));
        Assert.Equal(2, SiteTransferRules.InTab(all, SiteTransferRules.DoneTab).Count);
        Assert.Equal("Running and waiting (2)", SiteTransferRules.TabLabel(all, SiteTransferRules.ActiveTab));
        Assert.Equal("Failed", SiteTransferRules.TabLabel([], SiteTransferRules.FailedTab));
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
    public void TextsSayWhereAndHowFar()
    {
        var down = Batch("running", done: 1, skipped: 1, running: 1, speed: 2048);
        Assert.Equal("Download from Home NAS", SiteTransferRules.Title(down));
        Assert.Equal(@"/pub → C:\In", SiteTransferRules.Route(down));
        Assert.Equal("2 of 4 files, 1.0 KB of 4.0 KB, 2.0 KB/s", SiteTransferRules.StateText(down));
        Assert.Equal(@"C:\In → /pub", SiteTransferRules.Route(Batch("running", "upload")));
        Assert.StartsWith("Waiting:", SiteTransferRules.StateText(Batch("running")));
        Assert.StartsWith("Paused when Sites was turned off.", SiteTransferRules.StateText(Batch("paused", message: "Paused when Sites was turned off.")));
        Assert.StartsWith("Paused. ", SiteTransferRules.StateText(Batch("paused")));
        Assert.Equal("Stopped: The server can't be reached.", SiteTransferRules.StateText(Batch("failed", message: "The server can't be reached.")));
        Assert.Equal("Done: 3 of 4 files copied, 1 skipped (already there)", SiteTransferRules.StateText(Batch("done", done: 3, skipped: 1)));
    }

    [Fact]
    public void EmptyFoldersMakeTheCountItemsAndTheirLineSaysSo()
    {
        var withFolder = Batch("done", done: 4);
        withFolder.Folders = 1;
        Assert.Equal("Done: 4 of 4 items copied", SiteTransferRules.StateText(withFolder));
        Assert.Equal("Folder made · empty folder",
            SiteTransferRules.ItemText(new SiteTransferItemDto { Path = "a/empty", State = "done", IsFolder = true }));
        Assert.Equal("Copied · 1.0 KB", SiteTransferRules.ItemText(new SiteTransferItemDto { Path = "a/f", State = "done", Size = 1024 }));
        Assert.Equal("Waiting", SiteTransferRules.ItemText(new SiteTransferItemDto { Path = "a/f", State = "waiting", Size = -1 }));
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
    public void TheChipSaysWhatRunsAndFailsAndIsQuietOtherwise()
    {
        Assert.Null(SiteTransferRules.Summary([]));
        Assert.Null(SiteTransferRules.Summary([Batch("done"), Batch("canceled")]));
        Assert.Equal("Site transfers: 2 transfers running, 3.0 KB/s, 1 failed",
            SiteTransferRules.Summary([Batch("running", running: 1, speed: 1024), Batch("running", speed: 2048), Batch("failed", failed: 1)]));
        Assert.Equal("Site transfers: 1 transfer waiting, 1 paused", SiteTransferRules.Summary([Batch("running"), Batch("paused")]));
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
