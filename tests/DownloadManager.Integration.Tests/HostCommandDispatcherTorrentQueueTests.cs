using System.Security.Cryptography;
using DownloadManager.Contracts;
using DownloadManager.Runtime;
using DownloadManager.Runtime.Ipc;
using DownloadManager.Torrents;

namespace DownloadManager.Integration.Tests;

/// <summary>T2: seeding against queues, quotas, the scheduler and the shared speed limit, on the in-memory torrent engine.</summary>
public sealed partial class HostCommandDispatcherTests
{
    private async Task<JobDto> AddTorrentAsync(HostCommandDispatcher dispatcher, string name, int? queueId = null)
    {
        var hash = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(20));
        var result = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Add, new TorrentAddRequest
        {
            Input = $"magnet:?xt=urn:btih:{hash}&dn={name}", Destination = Path.Combine(_root, "down"), QueueId = queueId,
        })).GetPayload<TorrentAddResult>();
        Assert.True(result.Ok, result.Error);
        return result.Job!;
    }

    private async Task<JobDto> UntilTorrentCompletedAsync(DownloadManagerService service, Guid jobId)
        => await UntilAsync(() =>
        {
            foreach (var worker in _torrentWorkers.ToArray()) worker.Advance();
            return service.GetSnapshot().Jobs.FirstOrDefault(j => j.Id == jobId && j.State == "completed");
        });

    private bool SeedingNow(DownloadManagerService service, Guid jobId)
        => service.GetSnapshot().Jobs.Single(j => j.Id == jobId).Torrent!.Seeding;

    [Fact]
    public async Task AQueueStopStopsItsSeedingAndItsStartDoesNotBringItBack()
    {
        var (service, _, job) = await CompletedTorrentAsync();
        await UntilAsync(() => service.GetSnapshot().Jobs.FirstOrDefault(j => j.Id == job.Id && j.Torrent!.Seeding));
        var queueId = service.GetSettings().DefaultQueueId;

        Assert.True((await service.QueueActionAsync(new QueueActionRequest { QueueId = queueId, Action = "stop" })).Ok);
        await UntilAsync(() => service.GetSnapshot().Jobs.FirstOrDefault(j => j.Id == job.Id && !j.Torrent!.Seeding));
        await UntilAsync(() => _torrentWorkers.All(w => w.Torrents.Count == 0) ? "" : null);
        var seedAdds = _torrentWorkers.SelectMany(w => w.Adds).Count(add => add["seed"]?.GetValue<bool>() == true);

        // A start (by hand or by its schedule) starts what waits to download, not seeding that was stopped.
        Assert.True((await service.QueueActionAsync(new QueueActionRequest { QueueId = queueId, Action = "start" })).Ok);
        await Task.Delay(300);
        Assert.False(SeedingNow(service, job.Id));
        Assert.Equal(seedAdds, _torrentWorkers.SelectMany(w => w.Adds).Count(add => add["seed"]?.GetValue<bool>() == true));
        Assert.Equal("completed", service.GetSnapshot().Jobs.Single(j => j.Id == job.Id).State);
    }

    [Fact]
    public async Task AQueueThatReachesItsQuotaStopsItsSeeding()
    {
        var service = await TorrentServiceAsync();
        var dispatcher = Dispatcher(service);
        var settings = service.GetSettings();
        var queue = settings.Queues.Single(q => q.Id == settings.DefaultQueueId);
        // Room for one torrent and a half: the first finishes and seeds, the second's download uses the rest up.
        var policy = await service.QueueActionAsync(new QueueActionRequest
        {
            QueueId = queue.Id, Action = "policy", Name = queue.Name, MaxConcurrentDownloads = queue.MaxConcurrentDownloads,
            QuotaEnabled = true, QuotaBytes = FakeTorrentEngine.FakeFileSize * 3 / 2, QuotaWindowHours = 24,
        });
        Assert.True(policy.Ok, policy.Error);

        var first = await AddTorrentAsync(dispatcher, "First");
        await UntilTorrentCompletedAsync(service, first.Id);
        await UntilAsync(() => service.GetSnapshot().Jobs.FirstOrDefault(j => j.Id == first.Id && j.Torrent!.Seeding));

        var second = await AddTorrentAsync(dispatcher, "Second");
        var paused = await UntilAsync(() =>
        {
            foreach (var worker in _torrentWorkers.ToArray()) worker.Advance();
            return service.GetSnapshot().Jobs.FirstOrDefault(j => j.Id == second.Id && j.State == "paused");
        });
        Assert.NotEqual("completed", paused.State);
        Assert.False(service.GetSettings().Queues.Single(q => q.Id == queue.Id).IsRunning);
        await UntilAsync(() => service.GetSnapshot().Jobs.FirstOrDefault(j => j.Id == first.Id && !j.Torrent!.Seeding));
        Assert.Equal("completed", service.GetSnapshot().Jobs.Single(j => j.Id == first.Id).State);
    }

    [Fact]
    public async Task AScheduledStopStopsSeedingInAStoppedQueueAndTheNextStartLeavesItStopped()
    {
        var service = await TorrentServiceAsync();
        var dispatcher = Dispatcher(service);
        var settings = service.GetSettings();
        settings.TorrentSeedAfterDownload = false;
        settings.Queues.Add(new QueueDto { Id = 7, Name = "Night", IsRunning = true, MaxConcurrentDownloads = 1, Order = 7 });
        Assert.True((await service.UpdateSettingsAsync(settings)).Ok);

        var job = await AddTorrentAsync(dispatcher, "Night", queueId: 7);
        await UntilTorrentCompletedAsync(service, job.Id);
        // The queue is stopped by hand; the finished torrent is then seeded by hand.
        Assert.True((await service.QueueActionAsync(new QueueActionRequest { QueueId = 7, Action = "stop" })).Ok);
        var on = await service.SetTorrentSeedingAsync(new TorrentSeedRequest { JobId = job.Id, On = true });
        Assert.True(on.Ok, on.Error);
        Assert.True(SeedingNow(service, job.Id));

        settings = service.GetSettings();
        settings.Schedules.Add(new ScheduleDto
        {
            Id = Guid.NewGuid(), Name = "Night window", QueueId = 7, Enabled = true,
            Kind = "daily", Days = "Mon,Tue,Wed,Thu,Fri,Sat,Sun", StartTime = "22:00", StopTime = "06:00",
        });
        Assert.True((await service.UpdateSettingsAsync(settings)).Ok);

        // The next morning: it has seeded since before the stop time.
        var day = DateTime.UtcNow.Date.AddDays(2);
        var stop = await service.EvaluateSchedulesAsync(new DateTimeOffset(day.AddHours(7), TimeSpan.Zero));
        Assert.Equal("stop", Assert.Single(stop).Action);
        await UntilAsync(() => service.GetSnapshot().Jobs.FirstOrDefault(j => j.Id == job.Id && !j.Torrent!.Seeding));
        await UntilAsync(() => _torrentWorkers.All(w => w.Torrents.Count == 0) ? "" : null);

        // Nothing seeds now, so the next evening's stop time has nothing to do; its start doesn't seed it again.
        var start = await service.EvaluateSchedulesAsync(new DateTimeOffset(day.AddHours(22), TimeSpan.Zero));
        Assert.Equal("start", Assert.Single(start).Action);
        await Task.Delay(300);
        Assert.False(SeedingNow(service, job.Id));
        Assert.All(_torrentWorkers, w => Assert.Empty(w.Torrents));
    }

    [Fact]
    public async Task SeedingStartedByHandAfterAStoppedQueuesStopTimeIsLeftAlone()
    {
        var service = await TorrentServiceAsync();
        var dispatcher = Dispatcher(service);
        var settings = service.GetSettings();
        settings.TorrentSeedAfterDownload = false;
        settings.Queues.Add(new QueueDto { Id = 8, Name = "Day", IsRunning = true, MaxConcurrentDownloads = 1, Order = 8 });
        settings.Schedules.Add(new ScheduleDto
        {
            Id = Guid.NewGuid(), Name = "Day window", QueueId = 8, Enabled = true,
            Kind = "daily", Days = "Mon,Tue,Wed,Thu,Fri,Sat,Sun", StartTime = "09:00", StopTime = "17:00",
        });
        Assert.True((await service.UpdateSettingsAsync(settings)).Ok);

        var job = await AddTorrentAsync(dispatcher, "Day", queueId: 8);
        await UntilTorrentCompletedAsync(service, job.Id);
        // Stopped by hand before its stop time; hours after the stop time the finished torrent is seeded by hand.
        Assert.True((await service.QueueActionAsync(new QueueActionRequest { QueueId = 8, Action = "stop" })).Ok);
        var on = await service.SetTorrentSeedingAsync(new TorrentSeedRequest { JobId = job.Id, On = true });
        Assert.True(on.Ok, on.Error);

        // The stop stays due until midnight; passes after the seeding began leave it seeding.
        var day = DateTime.UtcNow.Date.AddDays(-2);
        Assert.Empty(await service.EvaluateSchedulesAsync(new DateTimeOffset(day.AddHours(20), TimeSpan.Zero)));
        Assert.Empty(await service.EvaluateSchedulesAsync(new DateTimeOffset(day.AddHours(23), TimeSpan.Zero)));
        await Task.Delay(300);
        Assert.True(SeedingNow(service, job.Id));
    }

    // ----- One speed budget for HTTP downloads, torrent downloads and torrent uploads -----

    private async Task<DownloadManagerService> LimitedTorrentServiceAsync(long limit, long uploadLimit = 0)
    {
        var service = await TorrentServiceAsync();
        var settings = service.GetSettings();
        settings.GlobalSpeedLimitBytesPerSecond = limit;
        settings.TorrentUploadLimit = uploadLimit;
        Assert.True((await service.UpdateSettingsAsync(settings)).Ok);
        return service;
    }

    private long HttpBudget => _engine!.SpeedBudget.BytesPerSecond;

    [Fact]
    public async Task WithNoTorrentOnTheWorkerHttpDownloadsHaveTheWholeLimit()
    {
        var service = await LimitedTorrentServiceAsync(100_000);
        Assert.Equal(100_000, HttpBudget);
        Assert.Empty(_torrentWorkers);
        Assert.Equal(100_000, service.CurrentSpeedShares!.Value.Http);

        // No limit: nothing is capped, and the torrent upload limit still caps uploads.
        var settings = service.GetSettings();
        settings.GlobalSpeedLimitBytesPerSecond = null;
        settings.TorrentUploadLimit = 5_000;
        Assert.True((await service.UpdateSettingsAsync(settings)).Ok);
        Assert.Equal(0, HttpBudget);
        Assert.Null(service.CurrentSpeedShares);
        var dispatcher = Dispatcher(service);
        await AddTorrentAsync(dispatcher, "Unlimited");
        var worker = await UntilAsync(() => _torrentWorkers.FirstOrDefault(w => w.Torrents.Count == 1 && w.Limits is not null));
        Assert.Equal((0L, 5_000L), worker.Limits);
    }

    [Fact]
    public async Task WhileATorrentRunsTheLimitIsSharedAndGoesBackWhenItLeaves()
    {
        const long limit = 120_000;
        var service = await LimitedTorrentServiceAsync(limit);
        var dispatcher = Dispatcher(service);
        var job = await AddTorrentAsync(dispatcher, "Shared");
        var worker = await UntilAsync(() => _torrentWorkers.FirstOrDefault(w => w.Torrents.Count == 1 && w.Limits is not null));

        // From the start, HTTP and the torrent's download and upload stay within the one limit together.
        await UntilAsync(() => HttpBudget < limit ? "" : null);
        var (download, upload) = worker.Limits!.Value;
        Assert.InRange(HttpBudget + download + upload, 3, limit);
        Assert.True(download > 0 && upload > 0);

        // A torrent that downloads at its share and uploads little: the download gets more, the upload keeps some room.
        var id = job.Id.ToString("N");
        var before = download;
        for (var i = 0; i < 4; i++)
        {
            await Task.Delay(1100); // the split runs on progress, at most once a second
            worker.Rates(id, downRate: worker.Limits!.Value.Download, upRate: 100);
        }

        var shared = await UntilAsync(() => worker.Limits is { } now && now.Download > before ? (object)now : null);
        var limits = ((long Download, long Upload))shared;
        Assert.InRange(HttpBudget + limits.Download + limits.Upload, 3, limit);
        Assert.True(limits.Upload >= limit / SpeedBudgetSplit.FloorDivisor);
        Assert.True(HttpBudget >= limit / SpeedBudgetSplit.FloorDivisor);

        // Once it is done and the worker has closed, HTTP downloads have the whole limit again.
        await UntilTorrentCompletedAsync(service, job.Id);
        Assert.True((await service.SetTorrentSeedingAsync(new TorrentSeedRequest { JobId = job.Id, On = false })).Ok);
        await UntilAsync(() => worker.Exited.IsCompleted ? worker : null);
        await UntilAsync(() => HttpBudget == limit ? "" : null);
        Assert.Equal(limit, service.CurrentSpeedShares!.Value.Http);
    }

    [Fact]
    public async Task TheTorrentUploadLimitOnlyEverLowersTheUploadsShare()
    {
        const long limit = 90_000;
        var (service, _, job) = await CompletedTorrentAsync();
        var settings = service.GetSettings();
        settings.GlobalSpeedLimitBytesPerSecond = limit;
        settings.TorrentUploadLimit = 2_000;
        Assert.True((await service.UpdateSettingsAsync(settings)).Ok);
        await UntilAsync(() => service.GetSnapshot().Jobs.FirstOrDefault(j => j.Id == job.Id && j.Torrent!.Seeding));
        var worker = await UntilAsync(() => _torrentWorkers.FirstOrDefault(w => w.Torrents.Count == 1 && w.Limits is { Upload: 2_000 }));

        // Seeding at its cap: the cap holds, and what it can't use stays with the others.
        var id = job.Id.ToString("N");
        for (var i = 0; i < 3; i++)
        {
            await Task.Delay(1100);
            worker.Rates(id, downRate: 0, upRate: 2_000);
        }

        await Task.Delay(200);
        Assert.Equal(2_000, worker.Limits!.Value.Upload);
        Assert.InRange(HttpBudget + worker.Limits!.Value.Download + worker.Limits!.Value.Upload, limit - 3, limit);
    }

    [Fact]
    public void TheSplitKeepsTheLimitAndFollowsWhatEachKindUses()
    {
        const long limit = 100_000;
        var first = SpeedBudgetSplit.Initial(limit, uploadCap: 0);
        Assert.InRange(first.Total, limit - 3, limit);
        Assert.InRange(first.Http, 33_000, 34_000);

        // HTTP uses all of its share, the torrent download half, the upload almost nothing.
        var next = SpeedBudgetSplit.Split(limit, 0, first,
            new SpeedDemand(true, first.Http), new SpeedDemand(true, first.TorrentDownload / 2), new SpeedDemand(true, 100));
        Assert.InRange(next.Total, limit - 3, limit);
        Assert.True(next.Http > first.Http);
        Assert.True(next.TorrentDownload >= first.TorrentDownload / 2 * SpeedBudgetSplit.Headroom - 1);
        Assert.True(next.TorrentUpload >= limit / SpeedBudgetSplit.FloorDivisor);

        // Two kinds that both want more share evenly; an idle one keeps its small share.
        var even = SpeedBudgetSplit.Split(limit, 0, next,
            new SpeedDemand(true, next.Http), new SpeedDemand(true, next.TorrentDownload), new SpeedDemand(false, 0));
        Assert.Equal(even.Http, even.TorrentDownload, tolerance: 2);
        Assert.Equal(limit / SpeedBudgetSplit.FloorDivisor, even.TorrentUpload);

        // An upload cap below its share holds, and the rest goes to the others.
        var capped = SpeedBudgetSplit.Split(limit, 1_000, first,
            new SpeedDemand(true, 0), new SpeedDemand(false, 0), new SpeedDemand(true, 1_000));
        Assert.Equal(1_000, capped.TorrentUpload);
        Assert.InRange(capped.Total, limit - 3, limit);

        // A share never reaches 0 (no cap), even with a tiny limit.
        var tiny = SpeedBudgetSplit.Initial(3, 0);
        Assert.True(tiny.Http > 0 && tiny.TorrentDownload > 0 && tiny.TorrentUpload > 0);

        Assert.False(SpeedBudgetSplit.Differs(first, first with { Http = first.Http + 10 }));
        Assert.True(SpeedBudgetSplit.Differs(first, first with { Http = first.Http * 2 }));
    }
}
