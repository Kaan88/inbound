using DownloadManager.Contracts;
using DownloadManager.Core;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// U-WL-1 and U-WL-2 (usability test, 2026-11-09): what starting a queue starts. Queued and "later" downloads start, and
/// so do those its own stop paused; one the user paused stays paused, and a failed one is tried again only when the
/// queue would have retried it by itself.
/// </summary>
public sealed partial class RuntimeServiceTests
{
    private async Task<JobDto> AddToMainAsync(ServiceContext ctx, string name, string route, string startMode, int? size = 262_144, int seed = 7)
    {
        var query = route == "error" ? string.Empty : $"?size={size}&seed={seed}&bps=262144";
        var url = route switch
        {
            "error" => $"{_server.BaseAddress}/error/404",
            "flaky" => $"{_server.BaseAddress}/flaky/{name}?failures=100&size=16384",
            _ => $"{_server.BaseAddress}/{route}/{name}{query}",
        };
        var response = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = url, DestinationDirectory = ctx.Destination, FileName = name, Source = "cli", QueueId = 0, StartMode = startMode,
        });
        Assert.True(response.Accepted, response.Error);
        return response.Job!;
    }

    [Fact]
    public async Task ALaterDownloadWaitsInTheRunningMainQueueUntilTheQueueIsStarted()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server, autoResume: false);
        Assert.True(ctx.Service.GetSnapshot().Settings.Queues.Single(q => q.Id == 0).IsRunning);
        var later = await AddToMainAsync(ctx, "wait-later.bin", "file", "later", 65_536, 31);

        await Task.Delay(500);
        var waiting = ctx.Service.GetJob(later.Id)!;
        Assert.Equal("queued", waiting.State);
        Assert.Equal("later", waiting.StartMode);
        Assert.Null(waiting.StartedAt);

        // The queue already runs; Start is still what lets a "later" download go, and it works.
        var start = await ctx.Service.QueueActionAsync(new QueueActionRequest { QueueId = 0, Action = "start" });
        Assert.True(start.Ok, start.Error);
        var done = await ctx.WaitFor(later.Id, j => j.State == "completed", TimeSpan.FromSeconds(30));
        Assert.Equal(Sha256Of(31, 65_536), await Hashing.Sha256FileAsync(done.FinalFilePath));
    }

    [Fact]
    public async Task StartingTheQueueLeavesADownloadTheUserPausedPaused()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server, autoResume: false);
        var states = RecordStates(ctx);
        var paused = await AddToMainAsync(ctx, "user-paused.bin", "slow", "startNow", 4 * Mb, 32);
        var later = await AddToMainAsync(ctx, "with-it-later.bin", "file", "later", 65_536, 33);
        await ctx.WaitFor(paused.Id, j => j.ReceivedBytes > 65_536, TimeSpan.FromSeconds(30));
        Assert.True((await ctx.Service.PauseAsync(paused.Id)).Ok);

        // Started at once, while the pause is still settling (the race seen in the walk), and then again later.
        var start = await ctx.Service.QueueActionAsync(new QueueActionRequest { QueueId = 0, Action = "start" });
        Assert.True(start.Ok, start.Error);
        await ctx.WaitFor(later.Id, j => j.State == "completed", TimeSpan.FromSeconds(30));
        Assert.True((await ctx.Service.QueueActionAsync(new QueueActionRequest { QueueId = 0, Action = "start" })).Ok);
        await Task.Delay(500);

        Assert.Equal("paused", ctx.Service.GetJob(paused.Id)!.State);
        var afterPause = states[paused.Id].SkipWhile(state => !state.EndsWith(":paused", StringComparison.Ordinal)).ToList();
        Assert.DoesNotContain(afterPause, state => state.EndsWith(":downloading", StringComparison.Ordinal) || state.EndsWith(":connecting", StringComparison.Ordinal));

        // A stop and a start of the queue don't turn it into the queue's pause either.
        Assert.True((await ctx.Service.QueueActionAsync(new QueueActionRequest { QueueId = 0, Action = "stop" })).Ok);
        Assert.True((await ctx.Service.QueueActionAsync(new QueueActionRequest { QueueId = 0, Action = "start" })).Ok);
        await Task.Delay(300);
        Assert.Equal("paused", ctx.Service.GetJob(paused.Id)!.State);

        // Its own Resume still works, and after that run a queue stop and start carry it on.
        Assert.True((await ctx.Service.ResumeAsync(paused.Id)).Ok);
        await ctx.WaitFor(paused.Id, j => j.State == "downloading", TimeSpan.FromSeconds(30));
        Assert.True((await ctx.Service.QueueActionAsync(new QueueActionRequest { QueueId = 0, Action = "stop" })).Ok);
        await ctx.WaitFor(paused.Id, j => j.State == "paused");
        Assert.True((await ctx.Service.QueueActionAsync(new QueueActionRequest { QueueId = 0, Action = "start" })).Ok);
        var done = await ctx.WaitFor(paused.Id, j => j.State == "completed", TimeSpan.FromSeconds(60));
        Assert.Equal(Sha256Of(32, 4 * Mb), await Hashing.Sha256FileAsync(done.FinalFilePath));
    }

    [Fact]
    public async Task StopAllCountsAsTheUsersPauseForAQueueStart()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server, autoResume: false);
        var job = await AddToMainAsync(ctx, "stop-all.bin", "slow", "startNow", 4 * Mb, 34);
        await ctx.WaitFor(job.Id, j => j.ReceivedBytes > 65_536, TimeSpan.FromSeconds(30));
        await ctx.Service.PauseAllAsync();
        await ctx.WaitFor(job.Id, j => j.State == "paused");
        Assert.True((await ctx.Service.QueueActionAsync(new QueueActionRequest { QueueId = 0, Action = "start" })).Ok);
        await Task.Delay(500);
        Assert.Equal("paused", ctx.Service.GetJob(job.Id)!.State);
    }

    [Fact]
    public async Task StartingTheQueueDoesNotRetryADownloadThatFailedOnItsOwn()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server, autoResume: false);
        var job = await AddToMainAsync(ctx, "own-failure.bin", "error", "startNow");
        var failed = await ctx.WaitFor(job.Id, j => j.State == "failed", TimeSpan.FromSeconds(30));
        Assert.False(failed.QueueProcessing);

        Assert.True((await ctx.Service.QueueActionAsync(new QueueActionRequest { QueueId = 0, Action = "start" })).Ok);
        await Task.Delay(500);
        var after = ctx.Service.GetJob(job.Id)!;
        Assert.Equal("failed", after.State);
        Assert.NotNull(after.Failure);
        Assert.Equal(failed.UpdatedAt, after.UpdatedAt);
    }

    [Fact]
    public async Task StartingTheQueueRetriesAQueuedFailureOnlyWhileItsRetriesLast()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server, autoResume: false);
        var settings = ctx.Service.GetSnapshot().Settings;
        settings.Queues.Add(new QueueDto { Id = 1, Name = "No retries", IsRunning = true, MaxConcurrentDownloads = 1, Order = 1, RetryLimit = 0 });
        settings.Queues.Add(new QueueDto { Id = 2, Name = "One retry", IsRunning = true, MaxConcurrentDownloads = 1, Order = 2, RetryLimit = 1 });
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);

        async Task<JobDto> AddFlakyAsync(int queueId)
        {
            var name = $"queue-flaky-{queueId}-{Guid.NewGuid():N}.bin";
            var response = await ctx.Service.AddJobAsync(new AddJobRequest
            {
                Url = $"{_server.BaseAddress}/flaky/{name}?failures=100&size=16384",
                DestinationDirectory = ctx.Destination, FileName = name, Source = "cli", QueueId = queueId, StartMode = "queue",
            });
            Assert.True(response.Accepted, response.Error);
            return response.Job!;
        }

        // Retries off: a queue start leaves the failure alone.
        var none = await AddFlakyAsync(1);
        var noneFailed = await ctx.WaitFor(none.Id, j => j.State == "failed");
        Assert.True((await ctx.Service.QueueActionAsync(new QueueActionRequest { QueueId = 1, Action = "start" })).Ok);
        await Task.Delay(300);
        Assert.Equal(noneFailed.UpdatedAt, ctx.Service.GetJob(none.Id)!.UpdatedAt);

        // One retry: the queue owed it when it was stopped, so the next start makes it, and only once.
        var one = await AddFlakyAsync(2);
        await ctx.WaitFor(one.Id, j => j.State == "failed" && j.QueueRetryAt is not null);
        Assert.True((await ctx.Service.QueueActionAsync(new QueueActionRequest { QueueId = 2, Action = "stop" })).Ok);
        var stopped = ctx.Service.GetJob(one.Id)!;
        Assert.Null(stopped.QueueRetryAt);
        Assert.Equal(0, stopped.QueueRetryCount);
        Assert.True((await ctx.Service.QueueActionAsync(new QueueActionRequest { QueueId = 2, Action = "start" })).Ok);
        var retried = await ctx.WaitFor(one.Id, j => j.State == "failed" && j.QueueRetryCount == 1 && j.QueueRetryAt is null && j.UpdatedAt > stopped.UpdatedAt);
        Assert.True((await ctx.Service.QueueActionAsync(new QueueActionRequest { QueueId = 2, Action = "start" })).Ok);
        await Task.Delay(300);
        Assert.Equal(retried.UpdatedAt, ctx.Service.GetJob(one.Id)!.UpdatedAt);

        // Unlimited retries (the default queue): a start tries it again.
        var main = await AddFlakyAsync(0);
        await ctx.WaitFor(main.Id, j => j.State == "failed" && j.QueueRetryAt is not null);
        Assert.True((await ctx.Service.QueueActionAsync(new QueueActionRequest { QueueId = 0, Action = "stop" })).Ok);
        var mainStopped = ctx.Service.GetJob(main.Id)!;
        Assert.True((await ctx.Service.QueueActionAsync(new QueueActionRequest { QueueId = 0, Action = "start" })).Ok);
        await ctx.WaitFor(main.Id, j => j.State == "failed" && j.UpdatedAt > mainStopped.UpdatedAt && j.QueueRetryCount >= 1);
    }
}
