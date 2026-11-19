using DownloadManager.Contracts;
using DownloadManager.Core;

namespace DownloadManager.Integration.Tests;

public sealed partial class RuntimeServiceTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task AQueueStartWhileAFailedRunIsStillSavingRunsTheOwedRetryOnce(int limit)
    {
        BlockingJobStore? store = null;
        await using var ctx = await ServiceContext.CreateAsync(_server, hooks: new ContextHooks { WrapJobs = inner => store = new BlockingJobStore(inner) });
        var settings = ctx.Service.GetSnapshot().Settings;
        settings.Queues[0].RetryLimit = limit;
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);
        store!.BlockAnyJobIn = DownloadState.Failed;
        var added = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/flaky/winding-retry-{Guid.NewGuid():N}.bin?failures=100&size=4096",
            FileName = "winding-retry.bin", StartMode = "queue", QueueId = 0,
        });
        Assert.True(added.Accepted, added.Error);
        await store.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        var pending = ctx.Service.GetJob(added.Job!.Id)!;
        Assert.Equal("failed", pending.State); Assert.Equal(1, pending.QueueRetryCount); Assert.NotNull(pending.QueueRetryAt);
        var start = ctx.Service.QueueActionAsync(new QueueActionRequest { QueueId = 0, Action = "start" });
        store.Release();
        Assert.True((await start).Ok);
        var retried = await ctx.WaitFor(pending.Id, j => j.State == "failed" && j.UpdatedAt > pending.UpdatedAt &&
            j.QueueRetryCount == limit && (limit == 1 ? j.QueueRetryAt is null : j.QueueRetryAt is not null));
        Assert.Equal(limit, retried.QueueRetryCount);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task StopThenStartWhileFailedRunIsSavingPreservesTheOwedRetry(int limit)
    {
        BlockingJobStore? store = null;
        await using var ctx = await ServiceContext.CreateAsync(_server, hooks: new ContextHooks { WrapJobs = inner => store = new BlockingJobStore(inner) });
        var settings = ctx.Service.GetSettings(); settings.Queues[0].RetryLimit = limit;
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);
        store!.BlockAnyJobIn = DownloadState.Failed;
        var added = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/flaky/stop-winding-{Guid.NewGuid():N}.bin?failures=100&size=4096",
            FileName = "stop-winding.bin", StartMode = "queue", QueueId = 0,
        });
        Assert.True(added.Accepted, added.Error);
        await store.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        Task<SettingsResult> stop;
        JobDto stopped;
        try
        {
            // The controlled failed save keeps the runner alive while Stop publishes its transition.
            stop = ctx.Service.QueueActionAsync(new QueueActionRequest { QueueId = 0, Action = "stop" });
            stopped = ctx.Service.GetJob(added.Job!.Id)!;
        }
        finally { store.Release(); }
        Assert.True((await stop).Ok);
        Assert.Equal("failed", stopped.State);
        Assert.Equal(0, stopped.QueueRetryCount); Assert.Null(stopped.QueueRetryAt);
        Assert.True((await ctx.Service.QueueActionAsync(new QueueActionRequest { QueueId = 0, Action = "start" })).Ok);
        var retried = await ctx.WaitFor(stopped.Id, j => j.State == "failed" && j.UpdatedAt > stopped.UpdatedAt &&
            j.QueueRetryCount == limit && (limit == 1 ? j.QueueRetryAt is null : j.QueueRetryAt is not null));
        Assert.Equal(limit, retried.QueueRetryCount);
    }
}
