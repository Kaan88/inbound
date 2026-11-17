using DownloadManager.Contracts;
using DownloadManager.Sites;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// SITES.13 against a real rclone: a copy between remotes, a remote's own speed limit, a one-way sync, a preview, and
/// transfers that wait for a schedule.
/// </summary>
public sealed partial class SiteTransfersEndToEndTests
{
    [SiteTransfersFact]
    public async Task ACopyToAnotherFolderOfTheSameRemoteArrivesWholeOnTheServer()
    {
        var port = StartServer(slow: false);
        var (commands, siteId) = await OpenSiteAsync(port);

        var started = await Call<SiteTransferResultDto>(commands, SitesMessageTypes.TransferStart, new SiteTransferStartRequest
        {
            SiteId = siteId,
            Direction = SiteTransferDirections.Between,
            RemoteFolder = "/pub",
            TargetSiteId = siteId,
            TargetFolder = "/copy",
            Names = [new SiteTransferNameDto { Name = "docs", IsFolder = true }, new SiteTransferNameDto { Name = "one-mb.bin" }],
        });
        Assert.True(started.Ok, started.Message);
        Assert.Equal(("/copy", siteId, ""), (started.Transfer!.TargetFolder, started.Transfer.TargetSiteId, started.Transfer.LocalFolder));

        var done = await WaitForAsync(commands, started.Transfer.Id, t => SiteTransferStates.IsFinished(t.State));
        Assert.Equal(SiteTransferStates.Done, done.State);
        Assert.Equal(3, done.FilesDone);
        AssertSame(Path.Combine(Served, "pub", "one-mb.bin"), Path.Combine(Served, "copy", "one-mb.bin"));
        AssertSame(Path.Combine(Served, "pub", "docs", "deep", "b.txt"), Path.Combine(Served, "copy", "docs", "deep", "b.txt"));

        // Skip: the same again leaves the copies alone.
        var again = await Call<SiteTransferResultDto>(commands, SitesMessageTypes.TransferStart, new SiteTransferStartRequest
        {
            SiteId = siteId, Direction = SiteTransferDirections.Between, RemoteFolder = "/pub", TargetSiteId = siteId, TargetFolder = "/copy",
            Names = [new SiteTransferNameDto { Name = "one-mb.bin" }],
        });
        var skipped = await WaitForAsync(commands, again.Transfer!.Id, t => SiteTransferStates.IsFinished(t.State));
        Assert.Equal(1, skipped.FilesSkipped);
    }

    [SiteTransfersFact]
    public async Task ARemotesOwnDownloadLimitSlowsItsFiles()
    {
        var port = StartServer(slow: false);
        var (commands, siteId) = await OpenSiteAsync(port);
        var limited = await Call<SiteResultDto>(commands, SitesMessageTypes.Save, new SiteSaveRequest
        {
            Id = siteId, Name = "Loopback", Kind = SiteKinds.Sftp, Host = "127.0.0.1", Port = port, User = User,
            AuthMethod = SiteAuthMethods.Password, DownLimit = 256,
        });
        Assert.True(limited.Ok, limited.Message);

        // 256 KiB/s for a 1 MiB file, as the overall limit's test: about three seconds after rclone's first burst.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var done = await StartAndFinishAsync(commands, siteId, Path.Combine(Local, "remote-limit"), SiteTransferConflicts.Skip, "one-mb.bin");
        clock.Stop();
        Assert.Equal(SiteTransferStates.Done, done.State);
        Assert.True(clock.Elapsed >= TimeSpan.FromSeconds(2.5), $"took {clock.Elapsed}");
        AssertSame(Path.Combine(Served, "pub", "one-mb.bin"), Path.Combine(Local, "remote-limit", "one-mb.bin"));
    }

    [SiteTransfersFact]
    public async Task ASyncCopiesNewAndChangedFilesAndLeavesNewerOnesAlone()
    {
        var port = StartServer(slow: false);
        var (commands, siteId) = await OpenSiteAsync(port);
        var mirror = Path.Combine(Local, "mirror");
        var request = new SiteSyncRequest { SiteId = siteId, Direction = SiteTransferDirections.Download, RemoteFolder = "/pub/docs", LocalFolder = mirror };

        var plan = await Call<SiteSyncPlanDto>(commands, SitesMessageTypes.SyncPlan, request);
        Assert.True(plan.Ok, plan.Message);
        Assert.Equal(["a.txt", "deep/b.txt"], plan.Files.Select(f => f.Path));
        Assert.All(plan.Files, f => Assert.Equal(SiteSyncReasons.New, f.Reason));
        Assert.Equal(3, plan.Bytes);

        var ran = await Call<SiteTransferResultDto>(commands, SitesMessageTypes.SyncRun, request);
        Assert.True(ran.Ok, ran.Message);
        await WaitForAsync(commands, ran.Transfer!.Id, t => SiteTransferStates.IsFinished(t.State));
        AssertSame(Path.Combine(Served, "pub", "docs", "deep", "b.txt"), Path.Combine(mirror, "deep", "b.txt"));

        var nothing = await Call<SiteTransferResultDto>(commands, SitesMessageTypes.SyncRun, request);
        Assert.True(nothing.Ok);
        Assert.Null(nothing.Transfer);

        // Changed on the server: copied again. Changed here later: left alone.
        await File.WriteAllTextAsync(Path.Combine(Served, "pub", "docs", "a.txt"), "changed");
        File.SetLastWriteTimeUtc(Path.Combine(Served, "pub", "docs", "a.txt"), DateTime.UtcNow.AddMinutes(1));
        await File.WriteAllTextAsync(Path.Combine(mirror, "deep", "b.txt"), "mine, newer");
        File.SetLastWriteTimeUtc(Path.Combine(mirror, "deep", "b.txt"), DateTime.UtcNow.AddHours(1));
        var second = await Call<SiteSyncPlanDto>(commands, SitesMessageTypes.SyncPlan, request);
        Assert.Equal(("a.txt", SiteSyncReasons.Changed), (second.Files.Single().Path, second.Files.Single().Reason));
        Assert.Equal(1, second.NewerThere);
    }

    [SiteTransfersFact]
    public async Task APreviewReadsTheStartOfATextFile()
    {
        var port = StartServer(slow: false);
        var (commands, siteId) = await OpenSiteAsync(port);

        var preview = await Call<SitePreviewDto>(commands, SitesMessageTypes.Preview, new SitePreviewRequest { Id = siteId, Path = "/pub/readme.txt" });
        Assert.True(preview.Ok, preview.Message);
        Assert.Equal((SitePreviewKinds.Text, "read me", false), (preview.Kind, System.Text.Encoding.UTF8.GetString(preview.Data!), preview.Truncated));

        var part = await Call<SitePreviewDto>(commands, SitesMessageTypes.Preview, new SitePreviewRequest { Id = siteId, Path = "/pub/one-mb.bin", MaxBytes = 1000 });
        Assert.True(part.Truncated);
    }

    [SiteTransfersFact]
    public async Task ABatchThatWaitsForAScheduleStartsAtTheSchedulesStartAndSoDoesTheScheduledSync()
    {
        var port = StartServer(slow: false);
        var (commands, siteId) = await OpenSiteAsync(port);
        var target = Path.Combine(Local, "scheduled");

        var waiting = await Call<SiteTransferResultDto>(commands, SitesMessageTypes.TransferStart, new SiteTransferStartRequest
        {
            SiteId = siteId, Direction = SiteTransferDirections.Download, RemoteFolder = "/pub", LocalFolder = target, WaitForSchedule = true,
            Names = [new SiteTransferNameDto { Name = "readme.txt" }],
        });
        Assert.True(waiting.Ok, waiting.Message);
        Assert.Equal((SiteTransferStates.Paused, true), (waiting.Transfer!.State, waiting.Transfer.WaitsForSchedule));
        var kept = await Call<SiteResultDto>(commands, SitesMessageTypes.SyncSchedule, new SiteSyncScheduleRequest
        {
            SiteId = siteId, Direction = SiteTransferDirections.Download, RemoteFolder = "/pub/docs", LocalFolder = Path.Combine(Local, "nightly"),
        });
        Assert.True(kept.Ok, kept.Message);

        await commands.ScheduleAsync(start: true);

        var done = await WaitForAsync(commands, waiting.Transfer.Id, t => SiteTransferStates.IsFinished(t.State));
        Assert.Equal(SiteTransferStates.Done, done.State);
        Assert.True(File.Exists(Path.Combine(target, "readme.txt")));
        var sync = commands.Transfers.List().Transfers.Single(t => t.Id != waiting.Transfer.Id);
        await WaitForAsync(commands, sync.Id, t => SiteTransferStates.IsFinished(t.State));
        Assert.True(File.Exists(Path.Combine(Local, "nightly", "deep", "b.txt")));
    }
}
