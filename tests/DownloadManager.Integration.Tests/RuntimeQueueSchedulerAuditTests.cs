using System.Collections.Concurrent;
using DownloadManager.Contracts;
using DownloadManager.Core;
using DownloadManager.Persistence;
using DownloadManager.Runtime;
using TestServer;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// The audit fixes for queues, the scheduler, settings and completion actions (audit 2 H1, H2, M8-M13, M18, M19, L8,
/// L13; audit 4 H1 on the Host's side).
/// </summary>
public sealed partial class RuntimeServiceTests
{
    // ----- audit 2 H2: a completed download whose file is gone at start -----

    [Fact]
    public async Task ACompletedDownloadWhoseFileIsMissingAtStartStaysCompletedAndIsNotDownloadedAgain()
    {
        var ctx = await ServiceContext.CreateAsync(_server);
        var unplugged = Path.Combine(ctx.Destination, "usb");
        Directory.CreateDirectory(unplugged);
        var moved = await AddAndCompleteAsync(ctx, ctx.Destination, "moved.bin", 801);
        var onDrive = await AddAndCompleteAsync(ctx, unplugged, "on-drive.bin", 802);
        await ctx.Service.ShutdownAsync();

        // One file moved away by the user; the other's whole folder gone, as a drive that isn't mounted yet.
        File.Delete(moved.FinalFilePath);
        Directory.Delete(unplugged, recursive: true);

        await using var restarted = await ServiceContext.CreateAsync(_server, ctx.Paths, ctx.Destination);
        var missing = restarted.Service.GetJob(moved.Id)!;
        Assert.Equal("completed", missing.State);
        Assert.Equal("File not found in its folder", missing.StatusNote);
        Assert.Null(missing.Failure);
        var unreachable = restarted.Service.GetJob(onDrive.Id)!;
        Assert.Equal("completed", unreachable.State);
        Assert.Null(unreachable.StatusNote);

        // Neither Resume All nor a queue start downloads them again.
        await restarted.Service.ResumeAllAsync();
        Assert.True((await restarted.Service.QueueActionAsync(new QueueActionRequest { QueueId = 0, Action = "start" })).Ok);
        Assert.Equal("completed", restarted.Service.GetJob(moved.Id)!.State);
        Assert.Equal("completed", restarted.Service.GetJob(onDrive.Id)!.State);
        Assert.False(File.Exists(moved.FinalFilePath));

        // The file back where it was: the note goes at the next start.
        await restarted.Service.ShutdownAsync();
        await File.WriteAllBytesAsync(moved.FinalFilePath, TestContent.Generate(801, 65_536));
        await using var again = await ServiceContext.CreateAsync(_server, ctx.Paths, ctx.Destination);
        Assert.Null(again.Service.GetJob(moved.Id)!.StatusNote);
        Assert.Equal("completed", again.Service.GetJob(moved.Id)!.State);
        await ctx.DisposeAsync();
    }

    [Fact]
    public async Task AFinishedDownloadAnOlderBuildMarkedFailedForAMissingFileIsCompletedAgain()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "test-work", Guid.NewGuid().ToString("N"));
        var paths = new StoragePaths(Path.Combine(root, "state"));
        var destination = Path.Combine(root, "downloads");
        Directory.CreateDirectory(destination);
        paths.EnsureCreated();
        var store = new JsonJobStore(paths);
        await store.InitializeAsync();
        var job = new DownloadJob
        {
            Url = $"{_server.BaseAddress}/file/old.bin?size=1024&seed=1",
            FileName = "old.bin",
            DestinationDirectory = destination,
            FinalFilePath = Path.Combine(destination, "old.bin"),
            State = DownloadState.Failed,
            CompletedAt = DateTimeOffset.UtcNow.AddDays(-1),
            ReceivedBytes = 1024,
            TotalBytes = 1024,
            Failure = new JobFailure(FailureKind.DestinationUnavailable,
                "The completed file is missing from its destination.", false, false, DateTimeOffset.UtcNow),
        };
        job.PartFilePath = $"{job.FinalFilePath}.{job.Id:N}.part";
        await store.SaveAsync(job);

        await using var ctx = await ServiceContext.CreateAsync(_server, paths, destination);
        var restored = ctx.Service.GetJob(job.Id)!;
        Assert.Equal("completed", restored.State);
        Assert.Equal("File not found in its folder", restored.StatusNote);
        Directory.Delete(root, recursive: true);
    }

    // ----- audit 2 M11: an exit pauses what runs; the next start resumes it when that is on -----

    [Fact]
    public async Task DownloadsAnExitPausedResumeAtTheNextStartButOneTheUserPausedStaysPaused()
    {
        var ctx = await ServiceContext.CreateAsync(_server, maxConcurrent: 2);
        var running = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/slow/exit-running.bin?size=1048576&seed=811&bps=262144",
            DestinationDirectory = ctx.Destination, FileName = "exit-running.bin", Source = "cli",
        });
        var paused = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/slow/exit-paused.bin?size=1048576&seed=812&bps=262144",
            DestinationDirectory = ctx.Destination, FileName = "exit-paused.bin", Source = "cli",
        });
        await ctx.WaitFor(running.Job!.Id, j => j.ReceivedBytes > 65_536);
        await ctx.WaitFor(paused.Job!.Id, j => j.ReceivedBytes > 0);
        Assert.True((await ctx.Service.PauseAsync(paused.Job.Id)).Ok);
        await ctx.WaitFor(paused.Job.Id, j => j.State == "paused");

        await ctx.Service.ShutdownAsync();
        Assert.Equal("paused", ctx.Service.GetJob(running.Job.Id)!.State);

        await using var restarted = await ServiceContext.CreateAsync(_server, ctx.Paths, ctx.Destination, maxConcurrent: 2);
        var completed = await restarted.WaitFor(running.Job.Id, j => j.State == "completed", TimeSpan.FromSeconds(30));
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(TestContent.Generate(811, 1_048_576))).ToLowerInvariant(), await Hashing.Sha256FileAsync(completed.FinalFilePath));
        Assert.Equal("paused", restarted.Service.GetJob(paused.Job.Id)!.State);
        await ctx.DisposeAsync();
    }

    [Fact]
    public async Task DownloadsAnExitPausedStayPausedWhenResumeAtStartIsOff()
    {
        var ctx = await ServiceContext.CreateAsync(_server, autoResume: false);
        var running = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/slow/exit-off.bin?size=1048576&seed=813&bps=262144",
            DestinationDirectory = ctx.Destination, FileName = "exit-off.bin", Source = "cli",
        });
        await ctx.WaitFor(running.Job!.Id, j => j.ReceivedBytes > 65_536);
        await ctx.Service.ShutdownAsync();

        await using var restarted = await ServiceContext.CreateAsync(_server, ctx.Paths, ctx.Destination, autoResume: false);
        Assert.Equal("paused", restarted.Service.GetJob(running.Job.Id)!.State);
        // The exit's mark is gone, so a later start doesn't take it as an exit's pause either.
        await restarted.Service.ShutdownAsync();
        await using var third = await ServiceContext.CreateAsync(_server, ctx.Paths, ctx.Destination, autoResume: true);
        Assert.Equal("paused", third.Service.GetJob(running.Job.Id)!.State);
        await ctx.DisposeAsync();
    }

    // ----- audit 2 L13: Resume All -----

    [Fact]
    public async Task ResumeAllResumesPausedDownloadsButNotCancelledOnesOrPermanentFailures()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var paused = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/file/resume-all.bin?size=65536&seed=821",
            DestinationDirectory = ctx.Destination, FileName = "resume-all.bin", Source = "cli", StartMode = "later",
        });
        Assert.True((await ctx.Service.PauseAsync(paused.Job!.Id)).Ok);
        await ctx.WaitFor(paused.Job.Id, j => j.State == "paused");
        var cancelled = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/file/resume-cancelled.bin?size=65536&seed=822",
            DestinationDirectory = ctx.Destination, FileName = "resume-cancelled.bin", Source = "cli", StartMode = "later",
        });
        Assert.True((await ctx.Service.CancelAsync(cancelled.Job!.Id, deleteFile: true)).Ok);
        var live = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/live/playlist.m3u8",
            DestinationDirectory = ctx.Destination, FileName = "live.m3u8", Source = "cli",
        });
        await ctx.WaitFor(live.Job!.Id, j => j.State == "failed", TimeSpan.FromSeconds(30));

        await ctx.Service.ResumeAllAsync();

        await ctx.WaitFor(paused.Job.Id, j => j.State == "completed", TimeSpan.FromSeconds(30));
        Assert.Equal("cancelled", ctx.Service.GetJob(cancelled.Job.Id)!.State);
        Assert.Equal("failed", ctx.Service.GetJob(live.Job.Id)!.State);
    }

    // ----- audit 2 M13: Clean Orphans -----

    [Fact]
    public async Task CleanOrphansTakesOnlyThisAppsOwnPartFiles()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var browsers = Path.Combine(ctx.Destination, "setup.exe.part");
        var other = Path.Combine(ctx.Destination, "movie.mkv.abc.part");
        var ours = Path.Combine(ctx.Destination, $"lost.bin.{Guid.NewGuid():N}.part");
        foreach (var path in new[] { browsers, other, ours }) await File.WriteAllTextAsync(path, "partial");

        var cleaned = await ctx.Service.CleanOrphanPartFilesAsync(delete: true, CancellationToken.None);
        Assert.True(cleaned.Ok, cleaned.Error);
        Assert.False(File.Exists(ours));
        Assert.True(File.Exists(browsers));
        Assert.True(File.Exists(other));
    }

    /// <summary>Review 1 N3: a stream's kept tracks are part files too; a job's own stay, a lost one's go.</summary>
    [Fact]
    public async Task CleanOrphansTakesAStreamsLeftTracksButNotAJobsOwn()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var kept = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/file/kept.bin?size=65536&seed=830",
            DestinationDirectory = ctx.Destination, FileName = "kept.bin", Source = "cli", StartMode = "later",
        });
        var lost = Path.Combine(ctx.Destination, $"lost.mp4.{Guid.NewGuid():N}.part");
        string[] lostTracks = [lost + ".video", lost + ".audio", lost + ".mux.mp4"];
        string[] keptTracks = [kept.Job!.PartFilePath + ".video", kept.Job.PartFilePath + ".audio", kept.Job.PartFilePath + ".mux.mp4"];
        foreach (var path in lostTracks.Concat(keptTracks)) await File.WriteAllTextAsync(path, "track");

        var cleaned = await ctx.Service.CleanOrphanPartFilesAsync(delete: true, CancellationToken.None);
        Assert.True(cleaned.Ok, cleaned.Error);
        Assert.All(lostTracks, path => Assert.False(File.Exists(path), path));
        Assert.All(keptTracks, path => Assert.True(File.Exists(path), path));
    }

    // ----- audit 2 M12: a removal that gave up waiting -----

    [Fact]
    public async Task ARemovalThatTimesOutStillSavesTheStoppedRunSoARestartDoesNotDownloadItAgain()
    {
        // A transfer that ignores cancellation (a hung share): the removal gives up after its 15 s wait.
        var stuck = new IgnoresCancellation();
        var ctx = await ServiceContext.CreateAsync(_server, handler: stuck);
        var added = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/file/hung.bin?size=65536&seed=835",
            DestinationDirectory = ctx.Destination, FileName = "hung.bin", Source = "cli",
        });
        Assert.True(added.Accepted, added.Error);
        await stuck.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var removal = await ctx.Service.RemoveAsync(added.Job!.Id, deleteFile: true);
        Assert.False(removal.Ok);
        Assert.Contains("still stopping", removal.Error, StringComparison.Ordinal);

        // The transfer ends at last; what it did (stopped, data deleted) is saved although the removal gave up.
        stuck.Release.SetResult();
        await ctx.WaitFor(added.Job.Id, j => j.State == "cancelled", TimeSpan.FromSeconds(30));
        await ctx.Service.ShutdownAsync();
        Assert.Equal(DownloadState.Cancelled, (await new JsonJobStore(ctx.Paths).LoadAllAsync()).Single(job => job.Id == added.Job.Id).State);

        await using var restarted = await ServiceContext.CreateAsync(_server, ctx.Paths, ctx.Destination);
        await Task.Delay(500);
        Assert.Equal("cancelled", restarted.Service.GetJob(added.Job.Id)!.State);
        Assert.False(File.Exists(Path.Combine(ctx.Destination, "hung.bin")));
        await ctx.DisposeAsync();
    }

    private sealed class IgnoresCancellation : HttpMessageHandler
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task.ConfigureAwait(false);
            throw new HttpRequestException("The share went away.");
        }
    }

    // ----- audit 2 M8 and L8: when a schedule's stop acts -----

    [Fact]
    public async Task AStopThatPassedWhileTheQueueWasIdleDoesNotStopAStartByHandLater()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        await AddQueueAndScheduleAsync(ctx, "09:00", "18:00");
        var day = new DateTime(2026, 11, 4);

        // The queue finished early and is idle at 18:00; at 20:00 the user starts it by hand.
        Assert.Empty(await ctx.Service.EvaluateSchedulesAsync(At(day, 18, 30)));
        Assert.True((await ctx.Service.QueueActionAsync(new QueueActionRequest { QueueId = 1, Action = "start" })).Ok);
        Assert.Empty(await ctx.Service.EvaluateSchedulesAsync(At(day, 20, 0)));
        Assert.Empty(await ctx.Service.EvaluateSchedulesAsync(At(day, 23, 59)));
        Assert.True(QueueRunning(ctx, 1));

        // The next day's window starts and stops as usual.
        Assert.Equal("start", Assert.Single(await ctx.Service.EvaluateSchedulesAsync(At(day.AddDays(1), 9, 0))).Action);
        Assert.Equal("stop", Assert.Single(await ctx.Service.EvaluateSchedulesAsync(At(day.AddDays(1), 18, 0))).Action);
        Assert.False(QueueRunning(ctx, 1));
    }

    [Fact]
    public async Task AStopMissedAcrossMidnightStillStopsTheRunTheScheduleStarted()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        await AddQueueAndScheduleAsync(ctx, "09:00", "18:00");
        var day = new DateTime(2026, 11, 4);
        Assert.Equal("start", Assert.Single(await ctx.Service.EvaluateSchedulesAsync(At(day, 9, 0))).Action);

        // Asleep from 17:00 until 01:00 the next day.
        var stop = Assert.Single(await ctx.Service.EvaluateSchedulesAsync(At(day.AddDays(1), 1, 0)));
        Assert.Equal("stop", stop.Action);
        Assert.False(QueueRunning(ctx, 1));
        Assert.Empty(await ctx.Service.EvaluateSchedulesAsync(At(day.AddDays(1), 2, 0)));
    }

    [Theory]
    // Friday's run, asleep from Friday 17:00 until Monday 01:00 (2026-11-06 is a Friday).
    [InlineData("Mon,Tue,Wed,Thu,Fri", 3, 1)]
    // Any day's, asleep until Sunday 08:00, before that day's window.
    [InlineData("Mon,Tue,Wed,Thu,Fri,Sat,Sun", 2, 8)]
    public async Task AStopMissedAcrossDaysOfSleepStillStopsTheRunTheScheduleStarted(string days, int daysAsleep, int wakeHour)
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var settings = ctx.Service.GetSettings();
        settings.Queues.Add(new QueueDto { Id = 1, Name = "Window", MaxConcurrentDownloads = 1, IsRunning = false, Order = 1 });
        settings.Schedules.Add(new ScheduleDto
        {
            Id = Guid.NewGuid(), Name = "Window", QueueId = 1, Enabled = true, Kind = "daily",
            Days = days, StartTime = "09:00", StopTime = "18:00",
        });
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);
        var friday = new DateTime(2026, 11, 6);
        Assert.Equal("start", Assert.Single(await ctx.Service.EvaluateSchedulesAsync(At(friday, 9, 0))).Action);

        var stop = Assert.Single(await ctx.Service.EvaluateSchedulesAsync(At(friday.AddDays(daysAsleep), wakeHour, 0)));
        Assert.Equal("stop", stop.Action);
        Assert.False(QueueRunning(ctx, 1));
        // Acted once: a start by hand afterwards is not stopped by it.
        Assert.True((await ctx.Service.QueueActionAsync(new QueueActionRequest { QueueId = 1, Action = "start" })).Ok);
        Assert.Empty(await ctx.Service.EvaluateSchedulesAsync(At(friday.AddDays(daysAsleep), wakeHour, 30)));
        Assert.True(QueueRunning(ctx, 1));
    }

    // ----- audit 2 M10: the when-done actions of the run a schedule started, also when it ends in a failure -----

    [Fact]
    public async Task AScheduledRunWhoseLastDownloadFailsStillTakesItsExitAction()
    {
        var executor = new RecordingCompletionExecutor();
        await using var ctx = await ServiceContext.CreateAsync(_server, completionActions: executor);
        var settings = ctx.Service.GetSnapshot().Settings;
        settings.AllowSystemCompletionActions = true;
        settings.Queues.Add(new QueueDto { Id = 1, Name = "Night", MaxConcurrentDownloads = 1, IsRunning = false, Order = 1, RetryLimit = 0 });
        settings.Schedules.Add(new ScheduleDto
        {
            Id = Guid.NewGuid(), Name = "Night", QueueId = 1, Enabled = true, Kind = "daily", StartTime = "01:00",
            ExitWhenDone = true,
        });
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);
        var live = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/live/playlist.m3u8",
            DestinationDirectory = ctx.Destination, FileName = "live.m3u8", Source = "cli", QueueId = 1,
        });
        Assert.Equal("queued", ctx.Service.GetJob(live.Job!.Id)!.State);

        Assert.True(Assert.Single(await ctx.Service.EvaluateSchedulesAsync(At(new DateTime(2026, 11, 4), 1, 0))).Ok);
        await ctx.WaitFor(live.Job.Id, j => j.State == "failed", TimeSpan.FromSeconds(30));
        await WaitUntil(() => executor.ExitCount == 1, "the exit action");
    }

    [Fact]
    public async Task AScheduledStartWithNothingToDoLeavesNoActionsForADownloadAddedByHandLater()
    {
        var executor = new RecordingCompletionExecutor();
        var log = new ConcurrentQueue<string>();
        await using var ctx = await ServiceContext.CreateAsync(_server, completionActions: executor,
            hooks: new ContextHooks { Log = log.Enqueue });
        var settings = ctx.Service.GetSnapshot().Settings;
        settings.AllowSystemCompletionActions = true;
        settings.Schedules.Add(new ScheduleDto
        {
            Id = Guid.NewGuid(), Name = "Night", QueueId = 0, Enabled = true, Kind = "daily", StartTime = "01:00",
            ExitWhenDone = true,
        });
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);

        // 01:00: the queue is empty, so the schedule's start has nothing to do. At 14:00 the user adds a download.
        Assert.True(Assert.Single(await ctx.Service.EvaluateSchedulesAsync(At(new DateTime(2026, 11, 4), 1, 0))).Ok);
        Assert.True(QueueRunning(ctx, 0));
        var manual = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/file/by-hand.bin?size=65536&seed=832",
            DestinationDirectory = ctx.Destination, FileName = "by-hand.bin", Source = "cli",
        });
        await ctx.WaitFor(manual.Job!.Id, j => j.State == "completed", TimeSpan.FromSeconds(30));
        await WaitUntil(() => log.Any(line => line.Contains("did not start", StringComparison.Ordinal)), "the skipped actions to be logged");
        Assert.Equal(0, executor.ExitCount);
    }

    [Fact]
    public async Task AScheduledRunThatEndsWithItsActionsOffLeavesNoneForALaterRunByHand()
    {
        var executor = new RecordingCompletionExecutor();
        var log = new ConcurrentQueue<string>();
        await using var ctx = await ServiceContext.CreateAsync(_server, completionActions: executor,
            hooks: new ContextHooks { Log = log.Enqueue });
        var scheduleId = Guid.NewGuid();
        var settings = ctx.Service.GetSnapshot().Settings;
        settings.AllowSystemCompletionActions = true;
        settings.Schedules.Add(new ScheduleDto
        {
            Id = scheduleId, Name = "Night", QueueId = 0, Enabled = true, Kind = "daily", StartTime = "01:00",
        });
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);
        var waiting = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/file/scheduled.bin?size=65536&seed=833",
            DestinationDirectory = ctx.Destination, FileName = "scheduled.bin", Source = "cli", StartMode = "later",
        });
        Assert.True(Assert.Single(await ctx.Service.EvaluateSchedulesAsync(At(new DateTime(2026, 11, 4), 1, 0))).Ok);
        await ctx.WaitFor(waiting.Job!.Id, j => j.State == "completed", TimeSpan.FromSeconds(30));

        // The run ended with no when-done action set; one turned on afterwards belongs to the next scheduled run only.
        var withExit = ctx.Service.GetSnapshot().Settings;
        withExit.Schedules.Single(schedule => schedule.Id == scheduleId).ExitWhenDone = true;
        Assert.True((await ctx.Service.UpdateSettingsAsync(withExit)).Ok);
        var manual = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/file/by-hand2.bin?size=65536&seed=834",
            DestinationDirectory = ctx.Destination, FileName = "by-hand2.bin", Source = "cli",
        });
        await ctx.WaitFor(manual.Job!.Id, j => j.State == "completed", TimeSpan.FromSeconds(30));
        await WaitUntil(() => log.Any(line => line.Contains("did not start", StringComparison.Ordinal)), "the skipped actions to be logged");
        Assert.Equal(0, executor.ExitCount);
    }

    [Theory]
    // As a web session's add leaves it (the address check itself is the dispatcher's, not this test's).
    [InlineData(true, false, "added from another device")]
    // A provider's file: nothing says who chose it, so a web session could have, through remote.add into this queue.
    [InlineData(false, true, "a provider's files are never opened")]
    public async Task AScheduleOpensNoFileThatABrowserOnAnotherDeviceAdded(bool addedFromAnotherDevice, bool providerFile, string reason)
    {
        var executor = new RecordingCompletionExecutor();
        var log = new ConcurrentQueue<string>();
        var root = Path.Combine(AppContext.BaseDirectory, "test-work", Guid.NewGuid().ToString("N"));
        var paths = new StoragePaths(Path.Combine(root, "state"));
        var destination = Path.Combine(root, "downloads");
        Directory.CreateDirectory(destination);
        paths.EnsureCreated();
        var store = new JsonJobStore(paths);
        await store.InitializeAsync();
        var job = new DownloadJob
        {
            Url = $"{_server.BaseAddress}/file/from-web.bin?size=65536&seed=831",
            FileName = "from-web.bin",
            DestinationDirectory = destination,
            FinalFilePath = Path.Combine(destination, "from-web.bin"),
            State = DownloadState.Queued,
            StartMode = StartMode.Later,
            AddedFromAnotherDevice = addedFromAnotherDevice,
            Source = providerFile ? DownloadSource.Remote : DownloadSource.Api,
        };
        job.PartFilePath = $"{job.FinalFilePath}.{job.Id:N}.part";
        await store.SaveAsync(job);

        await using var ctx = await ServiceContext.CreateAsync(_server, paths, destination, completionActions: executor,
            hooks: new ContextHooks { Log = log.Enqueue });
        var settings = ctx.Service.GetSnapshot().Settings;
        settings.AllowSystemCompletionActions = true;
        settings.Schedules.Add(new ScheduleDto
        {
            Id = Guid.NewGuid(), Name = "Open", QueueId = 0, Enabled = true, Kind = "daily", StartTime = "01:00",
            OpenFileWhenDone = true, ExitWhenDone = true,
        });
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);
        Assert.True(Assert.Single(await ctx.Service.EvaluateSchedulesAsync(At(new DateTime(2026, 11, 4), 1, 0))).Ok);

        await ctx.WaitFor(job.Id, j => j.State == "completed", TimeSpan.FromSeconds(30));
        // The exit comes after the open would have, so once it is there the open was decided.
        await WaitUntil(() => executor.ExitCount == 1, "the exit action");
        Assert.Empty(executor.OpenedPaths);
        Assert.Contains(log, line => line.Contains(reason, StringComparison.Ordinal));
        await ctx.Service.ShutdownAsync();
        Directory.Delete(root, recursive: true);
    }

    // ----- audit 2 M18: the scheduler wakes only for what is due -----

    [Fact]
    public async Task TheSchedulerArmsNoWakeWhenNothingIsPendingAndOneForTheNextSchedule()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        Assert.Null(ctx.Service.NextSchedulerWake);

        var settings = ctx.Service.GetSnapshot().Settings;
        var scheduleId = Guid.NewGuid();
        settings.Schedules.Add(new ScheduleDto { Id = scheduleId, Name = "Daily", QueueId = 0, Enabled = true, StartTime = "03:00" });
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);
        var wake = ctx.Service.NextSchedulerWake;
        Assert.NotNull(wake);
        var now = DateTimeOffset.Now;
        Assert.InRange(wake.Value, now, now.AddHours(1).AddSeconds(5));
        var nextThree = now.Date.AddHours(3) is var today && today > now.DateTime ? today : today.AddDays(1);
        if (nextThree - now.DateTime < TimeSpan.FromMinutes(59))
            Assert.Equal(nextThree, wake.Value.ToLocalTime().DateTime, TimeSpan.FromSeconds(2));

        var removed = ctx.Service.GetSnapshot().Settings;
        removed.Schedules.Clear();
        Assert.True((await ctx.Service.UpdateSettingsAsync(removed)).Ok);
        Assert.Null(ctx.Service.NextSchedulerWake);
    }

    [Fact]
    public async Task TheSchedulerTimerStartsAQueueAtItsScheduledTime()
    {
        // The system's clock and timers, moved so that a schedule's start time is a few seconds away.
        var local = TimeZoneInfo.Local;
        var noon = DateTime.Today.AddHours(12);
        var noonAt = new DateTimeOffset(noon, local.GetUtcOffset(noon));
        var time = new WebUiSupervisorTests.OffsetTime();
        time.Offset = noonAt.AddSeconds(-6) - TimeProvider.System.GetUtcNow();

        var root = Path.Combine(AppContext.BaseDirectory, "test-work", Guid.NewGuid().ToString("N"));
        var paths = new StoragePaths(Path.Combine(root, "state"));
        paths.EnsureCreated();
        var checkpoints = new JsonCheckpointStore(paths);
        using var engine = new HttpDownloadEngine(new EngineOptions { AllowLocalNetwork = true }, checkpoints: checkpoints);
        var service = new DownloadManagerService(paths, new JsonJobStore(paths), checkpoints, new JsonSettingsStore(paths), engine,
            timeProvider: time, enableSchedulerTimer: true);
        try
        {
            await service.InitializeAsync();
            var settings = service.GetSettings();
            settings.DefaultDownloadDirectory = Path.Combine(root, "downloads");
            settings.Queues.Add(new QueueDto { Id = 1, Name = "Noon", MaxConcurrentDownloads = 1, IsRunning = false, Order = 1 });
            settings.Schedules.Add(new ScheduleDto { Id = Guid.NewGuid(), Name = "Noon", QueueId = 1, Enabled = true, StartTime = "12:00" });
            Assert.True((await service.UpdateSettingsAsync(settings)).Ok);
            Assert.False(service.GetSettings().Queues.Single(queue => queue.Id == 1).IsRunning);
            Assert.InRange(service.NextSchedulerWake!.Value, noonAt, noonAt.AddSeconds(2));

            await WaitUntil(() => service.GetSettings().Queues.Single(queue => queue.Id == 1).IsRunning,
                "the schedule to start the queue", TimeSpan.FromSeconds(20));
            // Then it waits for the next instant, no sooner than the next stop or start (at most an hour). The queue is
            // running before the evaluation that started it has armed the next wake, so this waits for that.
            await WaitUntil(() => service.NextSchedulerWake is { } next && next - time.GetUtcNow() > TimeSpan.FromMinutes(59),
                "the next wake an hour on", TimeSpan.FromSeconds(10));
        }
        finally
        {
            await service.ShutdownAsync();
            await DeleteWhenReleasedAsync(root);
        }
    }

    [Fact]
    public async Task AResumeFromSleepStartsAQueueWhoseStartCameDueWhileAsleep()
    {
        // 09:00, a start at 12:00: the timer waits an hour at most. Then the PC "sleeps" until 12:00:05, which a timer
        // doesn't count, and the Host reports the resume.
        var local = TimeZoneInfo.Local;
        var nine = DateTime.Today.AddHours(9);
        var noon = DateTime.Today.AddHours(12);
        var time = new WebUiSupervisorTests.OffsetTime();
        time.Offset = new DateTimeOffset(nine, local.GetUtcOffset(nine)) - TimeProvider.System.GetUtcNow();

        var root = Path.Combine(AppContext.BaseDirectory, "test-work", Guid.NewGuid().ToString("N"));
        var paths = new StoragePaths(Path.Combine(root, "state"));
        paths.EnsureCreated();
        var checkpoints = new JsonCheckpointStore(paths);
        using var engine = new HttpDownloadEngine(new EngineOptions { AllowLocalNetwork = true }, checkpoints: checkpoints);
        var service = new DownloadManagerService(paths, new JsonJobStore(paths), checkpoints, new JsonSettingsStore(paths), engine,
            timeProvider: time, enableSchedulerTimer: true);
        try
        {
            await service.InitializeAsync();
            var settings = service.GetSettings();
            settings.DefaultDownloadDirectory = Path.Combine(root, "downloads");
            settings.Queues.Add(new QueueDto { Id = 1, Name = "Noon", MaxConcurrentDownloads = 1, IsRunning = false, Order = 1 });
            settings.Schedules.Add(new ScheduleDto { Id = Guid.NewGuid(), Name = "Noon", QueueId = 1, Enabled = true, StartTime = "12:00" });
            Assert.True((await service.UpdateSettingsAsync(settings)).Ok);
            Assert.True(service.NextSchedulerWake!.Value - time.GetUtcNow() > TimeSpan.FromMinutes(59));

            time.Offset = new DateTimeOffset(noon.AddSeconds(5), local.GetUtcOffset(noon)) - TimeProvider.System.GetUtcNow();
            service.NotifyClockChanged();
            await WaitUntil(() => service.GetSettings().Queues.Single(queue => queue.Id == 1).IsRunning,
                "the resume to start the queue", TimeSpan.FromSeconds(10));
        }
        finally
        {
            await service.ShutdownAsync();
            await DeleteWhenReleasedAsync(root);
        }
    }

    // ----- audit 2 M9: quota use survives a restart -----

    [Fact]
    public async Task QueueQuotaUseIsKeptAcrossARestart()
    {
        var ctx = await ServiceContext.CreateAsync(_server);
        var settings = ctx.Service.GetSnapshot().Settings;
        var queue = settings.Queues.Single(q => q.Id == 0);
        queue.QuotaEnabled = true;
        queue.QuotaBytes = 100L * 1024 * 1024;
        queue.QuotaWindowHours = 24;
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);
        var job = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/file/quota-kept.bin?size=524288&seed=841",
            DestinationDirectory = ctx.Destination, FileName = "quota-kept.bin", Source = "cli",
        });
        await ctx.WaitFor(job.Job!.Id, j => j.State == "completed", TimeSpan.FromSeconds(30));
        var used = ctx.Service.GetSettings().Queues.Single(q => q.Id == 0).QuotaBytesUsed;
        Assert.True(used >= 524_288, used.ToString());
        await ctx.Service.ShutdownAsync();

        await using var restarted = await ServiceContext.CreateAsync(_server, ctx.Paths, ctx.Destination);
        Assert.Equal(used, restarted.Service.GetSettings().Queues.Single(q => q.Id == 0).QuotaBytesUsed);
        await ctx.DisposeAsync();
    }

    // ----- audit 4 H1: a stale copy of the settings -----

    [Fact]
    public async Task AStaleSettingsCopyKeepsWhatWasAddedSinceAndBringsNothingRemovedBack()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var stale = ctx.Service.GetSettings();
        Assert.True(stale.Revision > 0);

        // Another client adds a queue and a schedule after the stale copy was taken.
        var other = ctx.Service.GetSettings();
        other.Queues.Add(new QueueDto { Id = 5, Name = "Added elsewhere", MaxConcurrentDownloads = 1, IsRunning = false });
        var addedSchedule = new ScheduleDto { Id = Guid.NewGuid(), Name = "Added elsewhere", QueueId = 5, Enabled = true, StartTime = "04:00" };
        other.Schedules.Add(addedSchedule);
        Assert.True((await ctx.Service.UpdateSettingsAsync(other)).Ok);
        Assert.True((await ctx.Service.QueueActionAsync(new QueueActionRequest { QueueId = 5, Action = "start" })).Ok);

        // The stale copy is saved with an unrelated change: what it never saw stays, and the queue keeps running.
        stale.MaxConcurrentDownloads = 4;
        var saved = await ctx.Service.UpdateSettingsAsync(stale);
        Assert.True(saved.Ok, saved.Error);
        Assert.Equal(4, saved.Settings.MaxConcurrentDownloads);
        Assert.Contains(saved.Settings.Queues, q => q.Id == 5 && q.IsRunning);
        Assert.Contains(saved.Settings.Schedules, s => s.Id == addedSchedule.Id);

        // A copy that saw them removes them by leaving them out.
        var fresh = ctx.Service.GetSettings();
        fresh.Schedules.RemoveAll(s => s.Id == addedSchedule.Id);
        Assert.True((await ctx.Service.UpdateSettingsAsync(fresh)).Ok);
        Assert.DoesNotContain(ctx.Service.GetSettings().Schedules, s => s.Id == addedSchedule.Id);

        // A copy from before the removal that still lists it doesn't bring it back.
        var resurrect = other;
        Assert.True((await ctx.Service.UpdateSettingsAsync(resurrect)).Ok);
        Assert.DoesNotContain(ctx.Service.GetSettings().Schedules, s => s.Id == addedSchedule.Id);

        // A client without a revision (older) removes what it leaves out, as before.
        var legacy = ctx.Service.GetSettings();
        legacy.Revision = 0;
        legacy.Queues.RemoveAll(q => q.Id == 5);
        Assert.True((await ctx.Service.UpdateSettingsAsync(legacy)).Ok);
        Assert.DoesNotContain(ctx.Service.GetSettings().Queues, q => q.Id == 5);
    }

    [Fact]
    public async Task ASettingsSaveNeitherStartsNorStopsAQueue()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var copy = ctx.Service.GetSettings();
        Assert.True(copy.Queues.Single(q => q.Id == 0).IsRunning);
        Assert.True((await ctx.Service.QueueActionAsync(new QueueActionRequest { QueueId = 0, Action = "stop" })).Ok);

        copy.Queues.Single(q => q.Id == 0).IsRunning = true;
        copy.Queues.Single(q => q.Id == 0).QuotaBytesUsed = 12345;
        Assert.True((await ctx.Service.UpdateSettingsAsync(copy)).Ok);
        var queue = ctx.Service.GetSettings().Queues.Single(q => q.Id == 0);
        Assert.False(queue.IsRunning);
        Assert.Equal(0, queue.QuotaBytesUsed);

        var stop = ctx.Service.GetSettings();
        stop.Queues.Single(q => q.Id == 0).IsRunning = false;
        Assert.True((await ctx.Service.QueueActionAsync(new QueueActionRequest { QueueId = 0, Action = "start" })).Ok);
        Assert.True((await ctx.Service.UpdateSettingsAsync(stop)).Ok);
        Assert.True(ctx.Service.GetSettings().Queues.Single(q => q.Id == 0).IsRunning);
    }

    // ----- audit 2 M19: lists are never shared with a copy or changed in place -----

    [Fact]
    public async Task SettingsCopiesShareNoListWithTheHostAndASaveChangesNoListInPlace()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var update = ctx.Service.GetSettings();
        update.ExcludedHosts = ["Example.COM"];
        Assert.True((await ctx.Service.UpdateSettingsAsync(update)).Ok);

        var first = ctx.Service.GetSettings();
        first.ExcludedHosts.Add("changed.example");
        first.InterceptedFileExtensions.Clear();
        var second = ctx.Service.GetSettings();
        Assert.Equal(["example.com"], second.ExcludedHosts);
        Assert.NotEmpty(second.InterceptedFileExtensions);

        var store = new JsonSettingsStore(ctx.Paths);
        var settings = await store.LoadAsync();
        var held = new List<string> { ".EXE", " Zip " };
        settings.ExcludedFileExtensions = held;
        await store.SaveAsync(settings);
        Assert.Equal([".EXE", " Zip "], held);
        Assert.Equal(["exe", "zip"], settings.ExcludedFileExtensions);
    }

    private async Task<JobDto> AddAndCompleteAsync(ServiceContext ctx, string folder, string name, int seed)
    {
        var added = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/file/{name}?size=65536&seed={seed}",
            DestinationDirectory = folder, FileName = name, Source = "cli",
        });
        Assert.True(added.Accepted, added.Error);
        return await ctx.WaitFor(added.Job!.Id, j => j.State == "completed", TimeSpan.FromSeconds(30));
    }

    private static async Task AddQueueAndScheduleAsync(ServiceContext ctx, string start, string stop)
    {
        var settings = ctx.Service.GetSettings();
        settings.Queues.Add(new QueueDto { Id = 1, Name = "Window", MaxConcurrentDownloads = 1, IsRunning = false, Order = 1 });
        settings.Schedules.Add(new ScheduleDto
        {
            Id = Guid.NewGuid(), Name = "Window", QueueId = 1, Enabled = true, Kind = "daily",
            Days = "Mon,Tue,Wed,Thu,Fri,Sat,Sun", StartTime = start, StopTime = stop,
        });
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);
    }

    /// <summary>The evaluation the wake started may still be saving the schedule as the service shuts down.</summary>
    private static async Task DeleteWhenReleasedAsync(string root)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { Directory.Delete(root, recursive: true); return; }
            catch (IOException) when (attempt < 50) { await Task.Delay(100); }
        }
    }

    private static DateTimeOffset At(DateTime day, int hour, int minute) => new(day.AddHours(hour).AddMinutes(minute), TimeSpan.Zero);

    private static bool QueueRunning(ServiceContext ctx, int id) => ctx.Service.GetSettings().Queues.Single(q => q.Id == id).IsRunning;
}
