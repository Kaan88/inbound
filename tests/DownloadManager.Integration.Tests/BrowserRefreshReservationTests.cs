using DownloadManager.Contracts;
using DownloadManager.Core;

namespace DownloadManager.Integration.Tests;

public sealed partial class RuntimeServiceTests
{
    [Fact]
    public async Task BrowserRefreshReservesItsPrivateNameAgainstAWindingDownQueueRetry()
    {
        RefreshStore? store = null;
        await using var ctx = await ServiceContext.CreateAsync(_server, hooks: new ContextHooks { WrapJobs = inner => store = new RefreshStore(inner) });
        using var releaseFailure = new ManualResetEventSlim();
        var failedSaveDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var block = 1;
        ctx.Service.JobChanged += (_, e) =>
        {
            if (e.ChangeType == "failed" && e.Job.QueueProcessing && Interlocked.Exchange(ref block, 0) == 1)
            { failedSaveDone.TrySetResult(); releaseFailure.Wait(TimeSpan.FromSeconds(20)); }
        };
        var settings = ctx.Service.GetSnapshot().Settings; settings.Queues[0].RetryLimit = 0;
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);
        var retry = await ctx.Service.AddJobAsync(new AddJobRequest { Url = $"{_server.BaseAddress}/error/500", FileName = "shared-refresh.bin", StartMode = "queue" });
        Assert.True(retry.Accepted, retry.Error);
        await failedSaveDone.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            var target = await ctx.Service.AddJobAsync(new AddJobRequest { Url = $"{_server.BaseAddress}/error/403", FileName = "shared-refresh.bin" });
            Assert.True(target.Accepted, target.Error);
            await ctx.WaitFor(target.Job!.Id, j => j.State == "failed");
            settings = ctx.Service.GetSnapshot().Settings; settings.Queues[0].RetryLimit = 1;
            Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);
            // The queue owns a retry now, but its failed runner is held just before name acquisition.
            Assert.True((await ctx.Service.QueueActionAsync(new QueueActionRequest { QueueId = 0, Action = "start" })).Ok);
            store!.BlockNext = true;
            var refreshing = ctx.Service.RefreshUrlAsync(target.Job.Id, $"{_server.BaseAddress}/file/shared-refresh.bin?size=4096&seed=41");
            await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            try
            {
                releaseFailure.Set();
                var renamed = await ctx.WaitFor(retry.Job!.Id, j => j.FileName != "shared-refresh.bin");
                Assert.NotEqual(target.Job.FinalFilePath, renamed.FinalFilePath);
            }
            finally { store.Release.TrySetResult(); }
            Assert.True((await refreshing).Ok);
            await ctx.WaitFor(target.Job.Id, j => j.State == "completed");
            Assert.NotEqual(ctx.Service.GetJob(target.Job.Id)!.FinalFilePath, ctx.Service.GetJob(retry.Job!.Id)!.FinalFilePath);
        }
        finally { releaseFailure.Set(); store?.Release.TrySetResult(); }
    }

    private sealed class RefreshSleepSignal : ISleepPrevention
    {
        public volatile bool Armed;
        public void Dispose() { }
        public TaskCompletionSource WentIdle { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void SetActive(bool active) { if (!active && Armed) WentIdle.TrySetResult(); }
    }

    [Fact]
    public async Task BrowserRefreshFailureRechecksAQueuedJobWhenASlotOpenedDuringItsSave()
    {
        RefreshStore? store = null;
        var sleep = new RefreshSleepSignal(); var hook = new BlockingPostDownloadHook();
        await using var ctx = await ServiceContext.CreateAsync(_server, maxConcurrent: 1, sleepPrevention: sleep,
            postDownloadHook: hook, hooks: new ContextHooks { WrapJobs = inner => store = new RefreshStore(inner) });
        using var releaseCompleted = new ManualResetEventSlim();
        var completedSaveDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.Service.JobChanged += (_, e) =>
        {
            if (e.ChangeType == "completed" && e.Job.FileName == "slot-owner.bin")
            { completedSaveDone.TrySetResult(); releaseCompleted.Wait(TimeSpan.FromSeconds(20)); }
        };
        var settings = ctx.Service.GetSnapshot().Settings;
        settings.PreventSleepWhileDownloading = true; settings.PostDownloadCommand = @"C:\fixture\scan.exe";
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);
        var blocker = await ctx.Service.AddJobAsync(new AddJobRequest { Url = $"{_server.BaseAddress}/file/slot-owner.bin?size=4096&seed=42", FileName = "slot-owner.bin" });
        Assert.True(blocker.Accepted, blocker.Error);
        await completedSaveDone.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            var target = await ctx.Service.AddJobAsync(new AddJobRequest { Url = $"{_server.BaseAddress}/file/slot-target.bin?size=4096&seed=43", FileName = "slot-target.bin" });
            Assert.True(target.Accepted, target.Error); Assert.Equal("queued", ctx.Service.GetJob(target.Job!.Id)!.State);
            store!.BlockNext = true; store.FailAfterBlock = true;
            var refreshing = ctx.Service.RefreshUrlAsync(target.Job.Id, target.Job.Url + "&fresh=1");
            await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            sleep.Armed = true; releaseCompleted.Set();
            // The first dispatch after the slot released skipped the guarded refresh target.
            await sleep.WentIdle.Task.WaitAsync(TimeSpan.FromSeconds(10));
            store.Release.TrySetResult();
            Assert.False((await refreshing).Ok);
            // Block the completed runner's hook so its final dispatch cannot conceal a missing refresh wakeup.
            await hook.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var completed = await ctx.WaitFor(target.Job.Id, j => j.State == "completed");
            Assert.Equal(target.Job.Url, completed.Url);
        }
        finally { releaseCompleted.Set(); store?.Release.TrySetResult(); hook.Release.TrySetResult(); }
    }
}
