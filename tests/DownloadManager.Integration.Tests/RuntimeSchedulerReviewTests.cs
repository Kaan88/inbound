using DownloadManager.Contracts;
using DownloadManager.Core;
using DownloadManager.Persistence;
using DownloadManager.Runtime;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// The scheduler findings of the 2026-11-12 review (review 2: M1, M2, L2, L3, L4, L9; audit L11 and the unparsable
/// schedule time of L12).
/// </summary>
public sealed partial class RuntimeServiceTests
{
    // ----- review 2 M1: a schedule saved inside its window acts at once, not up to an hour later -----

    [Fact]
    public async Task AScheduleSavedInsideItsWindowStartsTheQueueAtOnce()
    {
        var (service, root, _) = await ClockedServiceAsync(DateTime.Today.AddHours(12).AddMinutes(5), timer: true);
        try
        {
            var settings = service.GetSettings();
            settings.Queues.Add(new QueueDto { Id = 1, Name = "Noon", MaxConcurrentDownloads = 1, IsRunning = false, Order = 1 });
            settings.Schedules.Add(new ScheduleDto
            {
                Id = Guid.NewGuid(), Name = "Noon", QueueId = 1, Enabled = true, StartTime = "12:00", StopTime = "18:00",
            });
            Assert.True((await service.UpdateSettingsAsync(settings)).Ok);
            await WaitUntil(() => service.GetSettings().Queues.Single(queue => queue.Id == 1).IsRunning,
                "the saved schedule to start its queue", TimeSpan.FromSeconds(10));
        }
        finally
        {
            await service.ShutdownAsync();
            await DeleteWhenReleasedAsync(root);
        }
    }

    // ----- review 2 M2: a missed stop of one schedule doesn't end another schedule's run on the same queue -----

    [Fact]
    public async Task AMissedStopOfOneScheduleDoesNotEndAnotherSchedulesRunOnTheSameQueue()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var settings = ctx.Service.GetSettings();
        settings.Queues.Add(new QueueDto { Id = 1, Name = "Window", MaxConcurrentDownloads = 1, IsRunning = false, Order = 1 });
        // The afternoon's is listed first.
        settings.Schedules.Add(new ScheduleDto
        {
            Id = Guid.NewGuid(), Name = "Afternoon", QueueId = 1, Enabled = true, Kind = "daily", StartTime = "14:00", StopTime = "18:00",
        });
        settings.Schedules.Add(new ScheduleDto
        {
            Id = Guid.NewGuid(), Name = "Morning", QueueId = 1, Enabled = true, Kind = "daily", StartTime = "06:00", StopTime = "09:00",
        });
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);
        var day = new DateTime(2026, 11, 4);
        Assert.Equal("start", Assert.Single(await ctx.Service.EvaluateSchedulesAsync(At(day, 6, 0))).Action);

        // Asleep from 08:00 until 14:05: the morning's stop never ran, and the afternoon's window is open now.
        var woke = await ctx.Service.EvaluateSchedulesAsync(At(day, 14, 5));
        Assert.DoesNotContain(woke, action => action.Action == "stop");
        Assert.True(QueueRunning(ctx, 1));
        Assert.Empty(await ctx.Service.EvaluateSchedulesAsync(At(day, 14, 30)));
        Assert.True(QueueRunning(ctx, 1));
        // The afternoon's own stop still ends it.
        Assert.Equal("stop", Assert.Single(await ctx.Service.EvaluateSchedulesAsync(At(day, 18, 0))).Action);
        Assert.False(QueueRunning(ctx, 1));
    }

    [Fact]
    public async Task InOneEvaluationAScheduleStopsBeforeAnotherStarts()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var settings = ctx.Service.GetSettings();
        settings.Queues.Add(new QueueDto { Id = 1, Name = "One", MaxConcurrentDownloads = 1, IsRunning = true, Order = 1 });
        settings.Queues.Add(new QueueDto { Id = 2, Name = "Two", MaxConcurrentDownloads = 1, IsRunning = false, Order = 2 });
        settings.Schedules.Add(new ScheduleDto
        {
            Id = Guid.NewGuid(), Name = "Two", QueueId = 2, Enabled = true, Kind = "daily", StartTime = "10:00", StopTime = "12:00",
        });
        settings.Schedules.Add(new ScheduleDto
        {
            Id = Guid.NewGuid(), Name = "One", QueueId = 1, Enabled = true, Kind = "daily", StartTime = "06:00", StopTime = "09:00",
        });
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);
        var actions = await ctx.Service.EvaluateSchedulesAsync(At(new DateTime(2026, 11, 4), 10, 0));
        Assert.Equal(["stop", "start"], actions.Select(action => action.Action));
    }

    // ----- review 2 L2: a schedule made after today's stop time doesn't stop a running queue -----

    [Fact]
    public async Task AScheduleMadeAfterTodaysStopTimeDoesNotStopARunningQueue()
    {
        var (service, root, time) = await ClockedServiceAsync(DateTime.Today.AddHours(20), timer: false);
        try
        {
            Assert.True(service.GetSettings().Queues.Single(queue => queue.Id == 0).IsRunning);
            var settings = service.GetSettings();
            var scheduleId = Guid.NewGuid();
            settings.Schedules.Add(new ScheduleDto
            {
                Id = scheduleId, Name = "Day", QueueId = 0, Enabled = true, Kind = "daily", StartTime = "09:00", StopTime = "18:00",
            });
            Assert.True((await service.UpdateSettingsAsync(settings)).Ok);
            Assert.Empty(await service.EvaluateSchedulesAsync(time.GetLocalNow()));
            Assert.True(service.GetSettings().Queues.Single(queue => queue.Id == 0).IsRunning);

            // The same for one turned off and on again, and for one whose stop time moves to before now.
            var off = service.GetSettings();
            off.Schedules.Single(schedule => schedule.Id == scheduleId).Enabled = false;
            Assert.True((await service.UpdateSettingsAsync(off)).Ok);
            var on = service.GetSettings();
            on.Schedules.Single(schedule => schedule.Id == scheduleId).Enabled = true;
            on.Schedules.Single(schedule => schedule.Id == scheduleId).StopTime = "19:00";
            Assert.True((await service.UpdateSettingsAsync(on)).Ok);
            Assert.Empty(await service.EvaluateSchedulesAsync(time.GetLocalNow().AddMinutes(1)));
            Assert.True(service.GetSettings().Queues.Single(queue => queue.Id == 0).IsRunning);

            // Tomorrow's window starts and stops as usual.
            var tomorrow = DateTime.SpecifyKind(DateTime.Today.AddDays(1), DateTimeKind.Unspecified);
            Assert.Equal("start", Assert.Single(await service.EvaluateSchedulesAsync(At(tomorrow, 9, 0))).Action);
            Assert.Equal("stop", Assert.Single(await service.EvaluateSchedulesAsync(At(tomorrow, 19, 0))).Action);
            Assert.False(service.GetSettings().Queues.Single(queue => queue.Id == 0).IsRunning);
        }
        finally
        {
            await service.ShutdownAsync();
            await DeleteWhenReleasedAsync(root);
        }
    }

    // ----- review 2 L3: synchronisation leaves a finished file alone when its folder can't be reached -----

    [Fact]
    public async Task PeriodicSynchronisationLeavesAFinishedFileAloneWhenItsFolderCantBeReached()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server, autoResume: false);
        var unplugged = Path.Combine(ctx.Destination, "usb");
        Directory.CreateDirectory(unplugged);
        var done = await AddAndCompleteAsync(ctx, unplugged, "sync-on-drive.bin", 851);
        var settings = ctx.Service.GetSnapshot().Settings;
        settings.Schedules.Add(new ScheduleDto
        {
            Id = Guid.NewGuid(), Name = "Periodic sync", QueueId = 0, Enabled = true, Kind = "daily", StartTime = "10:00",
            RepeatEveryMinutes = 1,
        });
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);
        Directory.Delete(unplugged, recursive: true);

        Assert.True(Assert.Single(await ctx.Service.EvaluateSchedulesAsync(new DateTimeOffset(2026, 11, 4, 10, 0, 0, TimeSpan.Zero))).Ok);
        await Task.Delay(300);
        var after = ctx.Service.GetJob(done.Id)!;
        Assert.Equal("completed", after.State);
        Assert.Equal(done.CompletedAt, after.CompletedAt);
        Assert.False(Directory.Exists(unplugged));
    }

    // ----- review 2 L4: a schedule's when-done action survives a restart during its run -----

    [Fact]
    public async Task AScheduledRunsExitActionStillComesWhenTheHostRestartedDuringTheRun()
    {
        var firstExecutor = new RecordingCompletionExecutor();
        var ctx = await ServiceContext.CreateAsync(_server, completionActions: firstExecutor);
        var settings = ctx.Service.GetSnapshot().Settings;
        settings.AllowSystemCompletionActions = true;
        settings.Queues.Add(new QueueDto { Id = 1, Name = "Night", MaxConcurrentDownloads = 1, IsRunning = false, Order = 1 });
        settings.Schedules.Add(new ScheduleDto
        {
            Id = Guid.NewGuid(), Name = "Night", QueueId = 1, Enabled = true, Kind = "daily", StartTime = "01:00", ExitWhenDone = true,
        });
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);
        var slow = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/slow/night-run.bin?size=1048576&seed=852&bps=262144",
            DestinationDirectory = ctx.Destination, FileName = "night-run.bin", Source = "cli", QueueId = 1,
        });
        Assert.True(Assert.Single(await ctx.Service.EvaluateSchedulesAsync(At(new DateTime(2026, 11, 4), 1, 0))).Ok);
        await ctx.WaitFor(slow.Job!.Id, j => j.ReceivedBytes > 65_536);
        // An update's restart in the middle of the run.
        await ctx.Service.ShutdownAsync();
        Assert.Equal(0, firstExecutor.ExitCount);

        var executor = new RecordingCompletionExecutor();
        await using var restarted = await ServiceContext.CreateAsync(_server, ctx.Paths, ctx.Destination, completionActions: executor);
        await restarted.WaitFor(slow.Job.Id, j => j.State == "completed", TimeSpan.FromSeconds(30));
        await WaitUntil(() => executor.ExitCount == 1, "the scheduled run's exit action");
        await ctx.DisposeAsync();
    }

    [Fact]
    public async Task AnArmLeftFromARunThatHadEndedIsDroppedAtTheNextStart()
    {
        var executor = new RecordingCompletionExecutor();
        var ctx = await ServiceContext.CreateAsync(_server, completionActions: executor);
        var scheduleId = Guid.NewGuid();
        var settings = ctx.Service.GetSnapshot().Settings;
        settings.AllowSystemCompletionActions = true;
        settings.Schedules.Add(new ScheduleDto
        {
            Id = scheduleId, Name = "Night", QueueId = 0, Enabled = true, Kind = "daily", StartTime = "01:00", ExitWhenDone = true,
        });
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);
        await ctx.Service.ShutdownAsync();

        // As a crash would leave it: the arm saved, the run's download already finished.
        var store = new JsonSettingsStore(ctx.Paths);
        var saved = await store.LoadAsync();
        saved.Queues.Single(queue => queue.Id == 0).ArmedScheduleId = scheduleId;
        await store.SaveAsync(saved);

        var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
        await using var restarted = await ServiceContext.CreateAsync(_server, ctx.Paths, ctx.Destination, completionActions: executor,
            hooks: new ContextHooks { Log = log.Enqueue });
        var manual = await restarted.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/file/after-crash.bin?size=65536&seed=853",
            DestinationDirectory = restarted.Destination, FileName = "after-crash.bin", Source = "cli",
        });
        await restarted.WaitFor(manual.Job!.Id, j => j.State == "completed", TimeSpan.FromSeconds(30));
        await WaitUntil(() => log.Any(line => line.Contains("did not start", StringComparison.Ordinal)), "the skipped actions to be logged");
        Assert.Equal(0, executor.ExitCount);
        await ctx.DisposeAsync();
    }

    [Fact]
    public async Task AnArmTakenWhileASettingsSaveIsUnderWayStaysTaken()
    {
        HoldsSaves? held = null;
        var executor = new RecordingCompletionExecutor();
        var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
        await using var ctx = await ServiceContext.CreateAsync(_server, completionActions: executor,
            hooks: new ContextHooks { WrapSettings = store => held = new HoldsSaves(store), Log = log.Enqueue });
        var settings = ctx.Service.GetSnapshot().Settings;
        settings.AllowSystemCompletionActions = true;
        settings.Queues.Add(new QueueDto { Id = 1, Name = "Night", MaxConcurrentDownloads = 1, IsRunning = false, Order = 1 });
        settings.Schedules.Add(new ScheduleDto
        {
            Id = Guid.NewGuid(), Name = "Night", QueueId = 1, Enabled = true, Kind = "daily", StartTime = "01:00", ExitWhenDone = true,
        });
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);
        var run = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/slow/arm-race.bin?size=524288&seed=855&bps=262144",
            DestinationDirectory = ctx.Destination, FileName = "arm-race.bin", Source = "cli", QueueId = 1,
        });
        Assert.True(Assert.Single(await ctx.Service.EvaluateSchedulesAsync(At(new DateTime(2026, 11, 4), 1, 0))).Ok);

        // A settings save is under way (copied, not yet swapped in) when the scheduled run ends and takes its arm.
        held!.Holding = true;
        var saving = ctx.Service.UpdateSettingsAsync(ctx.Service.GetSnapshot().Settings);
        await held.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await ctx.WaitFor(run.Job!.Id, j => j.State == "completed", TimeSpan.FromSeconds(30));
        await WaitUntil(() => executor.ExitCount == 1, "the scheduled run's exit action", TimeSpan.FromSeconds(15));
        held.Holding = false;
        held.Release.SetResult();
        Assert.True((await saving.WaitAsync(TimeSpan.FromSeconds(10))).Ok);

        // The queue still runs; a download added by hand later ends a run the schedule did not start.
        var manual = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/file/arm-race-manual.bin?size=65536&seed=856",
            DestinationDirectory = ctx.Destination, FileName = "arm-race-manual.bin", Source = "cli", QueueId = 1,
        });
        await ctx.WaitFor(manual.Job!.Id, j => j.State == "completed", TimeSpan.FromSeconds(30));
        await WaitUntil(() => log.Any(line => line.Contains("did not start", StringComparison.Ordinal)), "the skipped actions to be logged");
        Assert.Equal(1, executor.ExitCount);
    }

    private sealed class HoldsSaves(ISettingsStore inner) : ISettingsStore
    {
        public volatile bool Holding;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) => inner.LoadAsync(cancellationToken);

        public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            if (Holding)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            await inner.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
        }
    }

    // ----- review 2 L9: no wake for a one-time schedule whose date has passed -----

    [Fact]
    public async Task APastOneTimeScheduleArmsNoWake()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var settings = ctx.Service.GetSnapshot().Settings;
        settings.Schedules.Add(new ScheduleDto
        {
            Id = Guid.NewGuid(), Name = "Once", QueueId = 0, Enabled = true, Kind = "oneTime",
            StartDate = DateOnly.FromDateTime(DateTime.Today.AddDays(-2)), StartTime = "03:00", StopTime = "04:00",
        });
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);
        Assert.Null(ctx.Service.NextSchedulerWake);

        // One whose date is ahead still arms one.
        var ahead = ctx.Service.GetSnapshot().Settings;
        ahead.Schedules.Single().StartDate = DateOnly.FromDateTime(DateTime.Today.AddDays(3));
        Assert.True((await ctx.Service.UpdateSettingsAsync(ahead)).Ok);
        Assert.NotNull(ctx.Service.NextSchedulerWake);
    }

    // ----- audit L11: synchronisation doesn't hold back other schedules -----

    [Fact]
    public async Task ASlowSynchronisationDoesNotHoldBackAnotherSchedulesStart()
    {
        var hold = new HoldsMetadata();
        await using var ctx = await ServiceContext.CreateAsync(_server, autoResume: false, handler: hold);
        var done = await AddAndCompleteAsync(ctx, ctx.Destination, "sync-slow.bin", 854);
        var settings = ctx.Service.GetSnapshot().Settings;
        settings.Queues.Add(new QueueDto { Id = 1, Name = "Other", MaxConcurrentDownloads = 1, IsRunning = false, Order = 1 });
        // The synchronising schedule is listed first.
        settings.Schedules.Add(new ScheduleDto
        {
            Id = Guid.NewGuid(), Name = "Periodic sync", QueueId = 0, Enabled = true, Kind = "daily", StartTime = "10:00",
            RepeatEveryMinutes = 1,
        });
        settings.Schedules.Add(new ScheduleDto { Id = Guid.NewGuid(), Name = "Other", QueueId = 1, Enabled = true, Kind = "daily", StartTime = "10:00" });
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);

        hold.Holding = true;
        var evaluation = ctx.Service.EvaluateSchedulesAsync(new DateTimeOffset(2026, 11, 4, 10, 0, 0, TimeSpan.Zero));
        await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        hold.Holding = false;
        // The synchronising start comes last in the pass, so the other schedule's start is not held back by it.
        await WaitUntil(() => QueueRunning(ctx, 1), "the other schedule's start while the metadata is read", TimeSpan.FromSeconds(5));
        // Another evaluation waits for this one (it could otherwise stop the queue before this one starts it), and then
        // finds the schedule already run.
        var next = ctx.Service.EvaluateSchedulesAsync(new DateTimeOffset(2026, 11, 4, 10, 0, 30, TimeSpan.Zero));
        await Task.Delay(200);
        Assert.False(next.IsCompleted);
        hold.Release.SetResult();
        var held = await evaluation.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal([1, 0], held.Select(action => action.QueueId));
        Assert.All(held, action => Assert.True(action.Ok));
        Assert.Empty(await next.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("completed", ctx.Service.GetJob(done.Id)!.State);
    }

    private sealed class HoldsMetadata : DelegatingHandler
    {
        public HoldsMetadata() : base(new HttpClientHandler()) { }
        public volatile bool Holding;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Holding)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }

    // ----- audit L12: a schedule time the Host can't read is refused when saved -----

    [Theory]
    [InlineData("25:00", null)]
    [InlineData("nine", null)]
    [InlineData("09:00", "18:75")]
    public async Task AScheduleTimeTheHostCantReadIsRefused(string start, string? stop)
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var settings = ctx.Service.GetSnapshot().Settings;
        settings.Schedules.Add(new ScheduleDto { Id = Guid.NewGuid(), Name = "Bad", QueueId = 0, Enabled = true, StartTime = start, StopTime = stop });
        var result = await ctx.Service.UpdateSettingsAsync(settings);
        Assert.False(result.Ok);
        Assert.Empty(ctx.Service.GetSettings().Schedules);

        // An empty stop time is no stop time.
        var fine = ctx.Service.GetSnapshot().Settings;
        fine.Schedules.Add(new ScheduleDto { Id = Guid.NewGuid(), Name = "Good", QueueId = 0, Enabled = true, StartTime = "09:00", StopTime = "" });
        Assert.True((await ctx.Service.UpdateSettingsAsync(fine)).Ok);
    }

    /// <summary>A service on a clock set to <paramref name="local"/> (this machine's zone), going on from there.</summary>
    private static async Task<(DownloadManagerService Service, string Root, WebUiSupervisorTests.OffsetTime Time)> ClockedServiceAsync(
        DateTime local, bool timer)
    {
        var time = new WebUiSupervisorTests.OffsetTime();
        time.Offset = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)) - TimeProvider.System.GetUtcNow();
        var root = Path.Combine(AppContext.BaseDirectory, "test-work", Guid.NewGuid().ToString("N"));
        var paths = new StoragePaths(Path.Combine(root, "state"));
        paths.EnsureCreated();
        var checkpoints = new JsonCheckpointStore(paths);
        var engine = new HttpDownloadEngine(new EngineOptions { AllowLocalNetwork = true }, checkpoints: checkpoints);
        var service = new DownloadManagerService(paths, new JsonJobStore(paths), checkpoints, new JsonSettingsStore(paths), engine,
            timeProvider: time, enableSchedulerTimer: timer);
        await service.InitializeAsync();
        var settings = service.GetSettings();
        settings.DefaultDownloadDirectory = Path.Combine(root, "downloads");
        Assert.True((await service.UpdateSettingsAsync(settings)).Ok);
        return (service, root, time);
    }
}
