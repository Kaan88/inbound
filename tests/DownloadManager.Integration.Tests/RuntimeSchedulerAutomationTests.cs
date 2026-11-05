using DownloadManager.Contracts;
using DownloadManager.Core;

namespace DownloadManager.Integration.Tests;

public sealed partial class RuntimeServiceTests
{
    [Fact]
    public async Task StartupOnlyScheduleStartsLaterJobOnce()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server, autoResume: false);
        var settings = ctx.Service.GetSnapshot().Settings;
        settings.Queues.Add(new QueueDto
        {
            Id = 1, Name = "Startup", IsRunning = false, MaxConcurrentDownloads = 1, Order = 1,
        });
        var scheduleId = Guid.NewGuid();
        settings.Schedules.Add(new ScheduleDto
        {
            Id = scheduleId, Name = "Start on host startup", QueueId = 1,
            StartOnHostStartup = true, TimedStartEnabled = false,
        });
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);

        var add = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/slow/startup-once.bin?size=131072&seed=650&bps=262144",
            DestinationDirectory = ctx.Destination, FileName = "startup-once.bin",
            Source = "cli", QueueId = 1, StartMode = "later",
        });
        Assert.True(add.Accepted, add.Error);
        Assert.Equal("queued", ctx.Service.GetJob(add.Job!.Id)!.State);

        var startup = await ctx.Service.EvaluateSchedulesAsync(new DateTimeOffset(2026, 11, 5, 2, 0, 0, TimeSpan.Zero));
        var action = Assert.Single(startup);
        Assert.Equal(scheduleId, action.ScheduleId);
        Assert.Equal("start", action.Action);
        Assert.True(action.Ok, action.Error);
        var completed = await ctx.WaitFor(add.Job.Id, job => job.State == "completed");
        Assert.Equal(131072, completed.ReceivedBytes);

        Assert.Empty(await ctx.Service.EvaluateSchedulesAsync(new DateTimeOffset(2026, 11, 5, 2, 1, 0, TimeSpan.Zero)));
        Assert.Equal("completed", ctx.Service.GetJob(add.Job.Id)!.State);
    }

    [Fact]
    public async Task OvernightScheduleStartsOnceAcrossMidnightAndStopsAtBoundary()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var settings = ctx.Service.GetSnapshot().Settings;
        settings.Queues.Add(new QueueDto
        {
            Id = 1, Name = "Overnight", IsRunning = false, MaxConcurrentDownloads = 1, Order = 1,
        });
        var scheduleId = Guid.NewGuid();
        settings.Schedules.Add(new ScheduleDto
        {
            Id = scheduleId, Name = "Overnight window", QueueId = 1, Enabled = true,
            Kind = "daily", Days = "Mon,Tue,Wed,Thu,Fri,Sat,Sun", StartTime = "22:00", StopTime = "06:00",
        });
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);

        var tuesdayStart = new DateTimeOffset(2026, 11, 3, 22, 0, 0, TimeSpan.Zero);
        var started = await ctx.Service.EvaluateSchedulesAsync(tuesdayStart);
        Assert.Equal("start", Assert.Single(started).Action);
        Assert.True(ctx.Service.GetSnapshot().Settings.Queues.Single(queue => queue.Id == 1).IsRunning);

        // The previous day's start remains the governing window after midnight.
        var afterMidnight = await ctx.Service.EvaluateSchedulesAsync(tuesdayStart.AddHours(3));
        Assert.DoesNotContain(afterMidnight, action => action.Action == "start");
        Assert.True(ctx.Service.GetSnapshot().Settings.Queues.Single(queue => queue.Id == 1).IsRunning);

        var stop = await ctx.Service.EvaluateSchedulesAsync(tuesdayStart.AddHours(8));
        Assert.Equal("stop", Assert.Single(stop).Action);
        Assert.False(ctx.Service.GetSnapshot().Settings.Queues.Single(queue => queue.Id == 1).IsRunning);

        var wednesdayStart = await ctx.Service.EvaluateSchedulesAsync(tuesdayStart.AddHours(24));
        Assert.Equal("start", Assert.Single(wednesdayStart).Action);
        Assert.True(ctx.Service.GetSnapshot().Settings.Queues.Single(queue => queue.Id == 1).IsRunning);
    }

    [Fact]
    public async Task QueueRetryLimitZeroDisablesRetriesAndOneAllowsExactlyOneRetry()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server, autoResume: false);
        var settings = ctx.Service.GetSnapshot().Settings;
        settings.Queues.Add(new QueueDto
        {
            Id = 1, Name = "No retries", IsRunning = true, MaxConcurrentDownloads = 1, Order = 1, RetryLimit = 0,
        });
        settings.Queues.Add(new QueueDto
        {
            Id = 2, Name = "One retry", IsRunning = true, MaxConcurrentDownloads = 1, Order = 2, RetryLimit = 1,
        });
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);

        var noRetryName = $"no-retry-{Guid.NewGuid():N}.bin";
        var noRetry = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/flaky/{noRetryName}?failures=100&size=16384",
            DestinationDirectory = ctx.Destination, FileName = noRetryName, Source = "cli",
            QueueId = 1, StartMode = "queue",
        });
        Assert.True(noRetry.Accepted, noRetry.Error);
        var failedWithoutRetry = await ctx.WaitFor(noRetry.Job!.Id, job => job.State == "failed");
        Assert.Equal(0, failedWithoutRetry.QueueRetryCount);
        Assert.Null(failedWithoutRetry.QueueRetryAt);

        var oneRetryName = $"one-retry-{Guid.NewGuid():N}.bin";
        var oneRetry = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/flaky/{oneRetryName}?failures=100&size=16384&seed=85",
            DestinationDirectory = ctx.Destination, FileName = oneRetryName, Source = "cli",
            QueueId = 2, StartMode = "queue",
        });
        Assert.True(oneRetry.Accepted, oneRetry.Error);
        var firstFailure = await ctx.WaitFor(oneRetry.Job!.Id,
            job => job.State == "failed" && job.QueueRetryAt is not null);
        Assert.Equal(1, firstFailure.QueueRetryCount);
        var retryDue = firstFailure.QueueRetryAt!.Value;

        // Evaluation time is injectable; advance past the persisted due instant without a wall-clock wait.
        await ctx.Service.EvaluateSchedulesAsync(retryDue.AddSeconds(1));
        var failedAfterSingleRetry = await ctx.WaitFor(oneRetry.Job.Id,
            job => job.State == "failed" && job.QueueRetryCount == 1 && job.QueueRetryAt is null);
        Assert.Equal(1, failedAfterSingleRetry.QueueRetryCount);
        Assert.Null(failedAfterSingleRetry.QueueRetryAt);
        await ctx.Service.EvaluateSchedulesAsync(retryDue.AddMinutes(2));
        await Task.Delay(100);
        Assert.Equal("failed", ctx.Service.GetJob(oneRetry.Job.Id)!.State);
    }

    [Fact]
    public async Task DetachedLaterJobSurvivesRestartAndQueueStartDoesNotAdmitIt()
    {
        var ctx = await ServiceContext.CreateAsync(_server, autoResume: false);
        try
        {
            var settings = ctx.Service.GetSnapshot().Settings;
            settings.Queues.Add(new QueueDto
            {
                Id = 1, Name = "Stopped", IsRunning = false, MaxConcurrentDownloads = 1, Order = 1,
            });
            Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);
            var add = await ctx.Service.AddJobAsync(new AddJobRequest
            {
                Url = $"{_server.BaseAddress}/file/detached-later.bin?size=32768&seed=128",
                DestinationDirectory = ctx.Destination, FileName = "detached-later.bin",
                Source = "cli", QueueId = 1, StartMode = "later",
            });
            Assert.True(add.Accepted, add.Error);

            var detached = await ctx.Service.DetachFromQueueAsync(add.Job!.Id);
            Assert.True(detached.Ok, detached.Error);
            Assert.False(detached.Job!.IsQueued);
            Assert.Equal("later", detached.Job.StartMode);
            await ctx.Service.ShutdownAsync();

            await using var restarted = await ServiceContext.CreateAsync(_server, ctx.Paths, ctx.Destination,
                autoResume: false);
            var reloaded = restarted.Service.GetJob(add.Job.Id);
            Assert.NotNull(reloaded);
            Assert.False(reloaded!.IsQueued);
            Assert.Equal("later", reloaded.StartMode);
            Assert.Equal("queued", reloaded.State);

            Assert.True((await restarted.Service.QueueActionAsync(new QueueActionRequest
            {
                QueueId = 0, Action = "start",
            })).Ok);
            var afterQueueStart = restarted.Service.GetJob(add.Job.Id)!;
            Assert.False(afterQueueStart.IsQueued);
            Assert.Equal("later", afterQueueStart.StartMode);
            Assert.Equal("queued", afterQueueStart.State);
            Assert.False(File.Exists(afterQueueStart.FinalFilePath));
        }
        finally
        {
            await ctx.DisposeAsync();
        }
    }

    [Fact]
    public async Task RepeatedScheduleWithUnchangedValidatorsKeepsCompletedFileAndTimestamp()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server, autoResume: false);
        const int size = 65536;
        var added = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/file/unchanged-sync.bin?size={size}&seed=224",
            DestinationDirectory = ctx.Destination, FileName = "unchanged-sync.bin", Source = "cli",
        });
        Assert.True(added.Accepted, added.Error);
        var initial = await ctx.WaitFor(added.Job!.Id, job => job.State == "completed");
        var originalCompletedAt = initial.CompletedAt;
        var originalHash = await Hashing.Sha256FileAsync(initial.FinalFilePath);

        var settings = ctx.Service.GetSnapshot().Settings;
        var scheduleId = Guid.NewGuid();
        settings.Schedules.Add(new ScheduleDto
        {
            Id = scheduleId, Name = "Periodic sync", QueueId = 0, Enabled = true,
            Kind = "daily", Days = "Mon,Tue,Wed,Thu,Fri,Sat,Sun", StartTime = "10:00",
            RepeatEveryMinutes = 1,
        });
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);

        var firstRun = new DateTimeOffset(2026, 11, 4, 10, 0, 0, TimeSpan.Zero);
        var firstResult = await ctx.Service.EvaluateSchedulesAsync(firstRun);
        Assert.True(Assert.Single(firstResult).Ok);
        var afterFirst = ctx.Service.GetJob(initial.Id)!;
        Assert.Equal("completed", afterFirst.State);
        Assert.Equal(originalCompletedAt, afterFirst.CompletedAt);
        Assert.Equal(size, afterFirst.ReceivedBytes);
        Assert.Equal(originalHash, await Hashing.Sha256FileAsync(afterFirst.FinalFilePath));

        var secondResult = await ctx.Service.EvaluateSchedulesAsync(firstRun.AddMinutes(1));
        Assert.True(Assert.Single(secondResult).Ok);
        var afterSecond = ctx.Service.GetJob(initial.Id)!;
        Assert.Equal("completed", afterSecond.State);
        Assert.Equal(originalCompletedAt, afterSecond.CompletedAt);
        Assert.Equal(size, afterSecond.ReceivedBytes);
        Assert.Equal(originalHash, await Hashing.Sha256FileAsync(afterSecond.FinalFilePath));
        Assert.Equal(firstRun.AddMinutes(1), ctx.Service.GetSnapshot().Settings.Schedules.Single(s => s.Id == scheduleId).LastRunAt);
    }
}
