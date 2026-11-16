using DownloadManager.Contracts;
using DownloadManager.Sites;

namespace DownloadManager.Integration.Tests;

/// <summary>SITES.11 R1 against a real rclone: per-file progress, the row's title, and stopping one file.</summary>
public sealed partial class SiteTransfersEndToEndTests
{
    [SiteTransfersFact]
    public async Task AMovingFileReportsItsOwnProgressAndCanBeStoppedAloneWhileTheOthersFinish()
    {
        var port = StartServer(slow: true);
        var (commands, siteId) = await OpenSiteAsync(port, maxTransfers: 1);
        var target = Path.Combine(Local, "one-stopped");
        var started = await StartAsync(commands, siteId, target, SiteTransferConflicts.Skip, "slow.bin", "readme.txt");

        // The title: both names, in the order of the batch's files.
        Assert.Equal(2, started.PickedCount);
        Assert.Equal(["readme.txt", "slow.bin"], started.Picked.Order(StringComparer.Ordinal));

        // rclone names the file by the source it was given: matched, so the file has a bar and a time of its own.
        var moving = await WaitForAsync(commands, started.Id, t => t.Moving.Any(m => m.Path == "slow.bin" && m.BytesDone > 0 && m.Speed > 0));
        var file = moving.Moving.Single(m => m.Path == "slow.bin");
        Assert.Equal(6 * SlowRate, file.Size);
        Assert.NotNull(file.SecondsLeft);
        Assert.NotNull(moving.SecondsLeft);

        var stopped = await Call<SiteTransferResultDto>(commands, SitesMessageTypes.TransferStopFile,
            new SiteTransferFileRequest { Id = started.Id, Path = "slow.bin" });
        Assert.True(stopped.Ok, stopped.Message);

        // The batch goes on without it and finishes as done: a stopped file is not a failure.
        var done = await WaitForAsync(commands, started.Id, t => SiteTransferStates.IsFinished(t.State));
        Assert.Equal(SiteTransferStates.Done, done.State);
        Assert.Equal((1, 1, 0), (done.FilesDone, done.FilesStopped, done.FilesFailed));
        Assert.Null(done.FirstFailedPath);
        Assert.Empty(done.Moving);
        AssertSame(Path.Combine(Served, "pub", "readme.txt"), Path.Combine(target, "readme.txt"));
        Assert.False(File.Exists(Path.Combine(target, "slow.bin")));
        Assert.Empty(Directory.EnumerateFiles(target, "*.partial"));
        var detail = await Call<SiteTransferResultDto>(commands, SitesMessageTypes.TransferGet, new SiteTransferIdRequest { Id = started.Id });
        Assert.Equal(SiteTransferItemStates.Stopped, detail.Items!.Single(i => i.Path == "slow.bin").State);

        // Stopping a file that is finished, or one not in the batch.
        var again = await Call<SiteTransferResultDto>(commands, SitesMessageTypes.TransferStopFile,
            new SiteTransferFileRequest { Id = started.Id, Path = "readme.txt" });
        Assert.True(again.Ok);
        Assert.Equal(1, again.Transfer!.FilesDone);
        var unknown = await Call<SiteTransferResultDto>(commands, SitesMessageTypes.TransferStopFile,
            new SiteTransferFileRequest { Id = started.Id, Path = "nope.bin" });
        Assert.False(unknown.Ok);

        // The stopped file's size is set aside: nothing is left to move, so the bar is full.
        Assert.Equal(6 * SlowRate, done.BytesSetAside);
        Assert.Equal(done.Bytes - done.BytesSetAside, done.BytesDone);

        // Retry leaves a file stopped on purpose; startFile brings it back and runs the finished batch again.
        var retried = await Call<SiteTransferResultDto>(commands, SitesMessageTypes.TransferRetry, new SiteTransferIdRequest { Id = started.Id });
        Assert.True(retried.Ok, retried.Message);
        Assert.Equal((SiteTransferStates.Done, 1), (retried.Transfer!.State, retried.Transfer.FilesStopped));
        var restarted = await Call<SiteTransferResultDto>(commands, SitesMessageTypes.TransferStartFile,
            new SiteTransferFileRequest { Id = started.Id, Path = "slow.bin" });
        Assert.True(restarted.Ok, restarted.Message);
        Assert.Equal((SiteTransferStates.Running, 0), (restarted.Transfer!.State, restarted.Transfer.FilesStopped));
        await WaitForAsync(commands, started.Id, t => t.Moving.Any(m => m.Path == "slow.bin"));
        await Call<SiteTransferResultDto>(commands, SitesMessageTypes.TransferCancel, new SiteTransferIdRequest { Id = started.Id });
    }

    [SiteTransfersFact]
    public async Task ABrowseMarksTheSiteReachableAndUsedAndAServerGoneMarksItInTrouble()
    {
        var port = StartServer(slow: false);
        var (commands, siteId) = await OpenSiteAsync(port);
        var before = (await Call<SitesListDto>(commands, SitesMessageTypes.ListRequest, new { })).Sites.Single();
        Assert.Equal(((string?)null, (DateTimeOffset?)null), (before.Reach, before.LastUsedAt));

        var browsed = await Call<SiteBrowseResultDto>(commands, SitesMessageTypes.Browse, new SiteBrowseRequest { Id = siteId, Path = "/pub" });
        Assert.True(browsed.Ok, browsed.Message);
        var after = (await Call<SitesListDto>(commands, SitesMessageTypes.ListRequest, new { })).Sites.Single();
        Assert.Equal(SiteReach.Ok, after.Reach);
        Assert.NotNull(after.LastUsedAt);

        // Written to the site file in the background (the first time, since none was stored).
        var file = Path.Combine(State, "sites", siteId + ".json");
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!(await File.ReadAllTextAsync(file)).Contains("lastUsedAt", StringComparison.Ordinal) && DateTime.UtcNow < until) await Task.Delay(50);
        Assert.Contains("lastUsedAt", await File.ReadAllTextAsync(file));

        foreach (var server in _servers) Stop(server);
        _servers.Clear();
        var failed = await Call<SiteBrowseResultDto>(commands, SitesMessageTypes.Browse, new SiteBrowseRequest { Id = siteId, Path = "/pub", Refresh = true });
        Assert.False(failed.Ok);
        Assert.Equal(SiteReach.Trouble, (await Call<SitesListDto>(commands, SitesMessageTypes.ListRequest, new { })).Sites.Single().Reach);
    }

    [SiteTransfersFact]
    public async Task ADownloadWithNoFolderGoesWhereTheHostsRuleSaysAndIsMadeThere()
    {
        var port = StartServer(slow: false);
        var (commands, siteId) = await OpenSiteAsync(port);
        var byRule = Path.Combine(Local, "by-rule");
        IReadOnlyList<SiteTransferNameDto>? asked = null;
        commands.Transfers.DownloadFolder = picked => { asked = picked; return byRule; };

        var done = await StartAndFinishAsync(commands, siteId, "", SiteTransferConflicts.Skip, "readme.txt");
        Assert.Equal((SiteTransferStates.Done, byRule), (done.State, done.LocalFolder));
        Assert.Equal("readme.txt", Assert.Single(asked!).Name);
        AssertSame(Path.Combine(Served, "pub", "readme.txt"), Path.Combine(byRule, "readme.txt"));

        // The rule's folder gets the same guards as a named one: never Inbound's own data folder.
        commands.Transfers.DownloadFolder = _ => Path.Combine(State, "inside");
        var refused = await Call<SiteTransferResultDto>(commands, SitesMessageTypes.TransferStart, new SiteTransferStartRequest
        {
            SiteId = siteId, Direction = SiteTransferDirections.Download, RemoteFolder = "/pub", Names = [new SiteTransferNameDto { Name = "readme.txt" }],
        });
        Assert.False(refused.Ok);
    }

    [SiteTransfersFact]
    public async Task AWaitingFileStoppedAloneNeverStarts()
    {
        var port = StartServer(slow: true);
        var (commands, siteId) = await OpenSiteAsync(port, maxTransfers: 1);
        var target = Path.Combine(Local, "waiting-stopped");
        var started = await StartAsync(commands, siteId, target, SiteTransferConflicts.Skip, "slow.bin", "one-mb.bin");
        var running = await WaitForAsync(commands, started.Id, t => t.Moving.Count == 1);
        var waiting = running.Moving[0].Path == "slow.bin" ? "one-mb.bin" : "slow.bin";

        var stopped = await Call<SiteTransferResultDto>(commands, SitesMessageTypes.TransferStopFile,
            new SiteTransferFileRequest { Id = started.Id, Path = waiting });
        Assert.True(stopped.Ok, stopped.Message);
        Assert.Equal(1, stopped.Transfer!.FilesStopped);

        var done = await WaitForAsync(commands, started.Id, t => SiteTransferStates.IsFinished(t.State), TimeSpan.FromSeconds(60));
        Assert.Equal((SiteTransferStates.Done, 1, 1), (done.State, done.FilesDone, done.FilesStopped));
        Assert.False(File.Exists(Path.Combine(target, waiting)));
    }

    [SiteTransfersFact]
    public async Task APauseRacingAStopOfOneFileLeavesItStoppedNotWaiting()
    {
        // Review 2026-11-16: the pause cut the file first; the stop of that file landed before its ending was recorded.
        var port = StartServer(slow: true);
        var (commands, siteId) = await OpenSiteAsync(port, maxTransfers: 1);
        var started = await StartAsync(commands, siteId, Path.Combine(Local, "race"), SiteTransferConflicts.Skip, "slow.bin");
        await WaitForAsync(commands, started.Id, t => t.Moving.Count == 1);

        var pause = Call<SiteTransferResultDto>(commands, SitesMessageTypes.TransferPause, new SiteTransferIdRequest { Id = started.Id });
        var stop = Call<SiteTransferResultDto>(commands, SitesMessageTypes.TransferStopFile, new SiteTransferFileRequest { Id = started.Id, Path = "slow.bin" });
        await Task.WhenAll(pause, stop);

        var detail = await Call<SiteTransferResultDto>(commands, SitesMessageTypes.TransferGet, new SiteTransferIdRequest { Id = started.Id });
        var state = detail.Items!.Single().State;
        // Either order: the stop came first (stopped) or after the pause had recorded it waiting (then stopped directly).
        Assert.Equal(SiteTransferItemStates.Stopped, state);
        var resumed = await Call<SiteTransferResultDto>(commands, SitesMessageTypes.TransferResume, new SiteTransferIdRequest { Id = started.Id });
        Assert.True(resumed.Ok, resumed.Message);
        var done = await WaitForAsync(commands, started.Id, t => SiteTransferStates.IsFinished(t.State));
        Assert.Equal((SiteTransferStates.Done, 0, 1), (done.State, done.FilesDone, done.FilesStopped));
    }

    [SiteTransfersFact]
    public async Task AnUploadReportsItsMovingFileByItsLocalName()
    {
        // The upload's srcRemote is the path relative to the PC folder: checked against real rclone's stats.
        var port = StartServer(slow: true);
        var (commands, siteId) = await OpenSiteAsync(port);
        var source = Path.Combine(Local, "up-src");
        Directory.CreateDirectory(source);
        await File.WriteAllBytesAsync(Path.Combine(source, "big.bin"), System.Security.Cryptography.RandomNumberGenerator.GetBytes((int)(6 * SlowRate)));
        var started = await Call<SiteTransferResultDto>(commands, SitesMessageTypes.TransferStart, new SiteTransferStartRequest
        {
            SiteId = siteId,
            Direction = SiteTransferDirections.Upload,
            RemoteFolder = "/pub",
            LocalFolder = source,
            Names = [new SiteTransferNameDto { Name = "big.bin" }],
        });
        Assert.True(started.Ok, started.Message);
        await WaitForAsync(commands, started.Transfer!.Id, t => t.Moving.Any(m => m.Path == "big.bin" && m.BytesDone > 0));
        await Call<SiteTransferResultDto>(commands, SitesMessageTypes.TransferCancel, new SiteTransferIdRequest { Id = started.Transfer.Id });
    }
}
