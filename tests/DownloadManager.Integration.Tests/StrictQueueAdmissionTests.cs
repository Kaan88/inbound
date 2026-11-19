using DownloadManager.Contracts;

namespace DownloadManager.Integration.Tests;

public sealed partial class RuntimeServiceTests
{
    [Fact]
    public async Task ExplicitQueueDeletedDuringMetadataProbeIsRefusedWithoutFallback()
    {
        using var handler = new HoldsMetadata();
        await using var ctx = await ServiceContext.CreateAsync(_server, handler: handler);
        var made = await ctx.Service.QueueActionAsync(new QueueActionRequest { Action = "create", Name = "Selected", MaxConcurrentDownloads = 1 });
        Assert.True(made.Ok, made.Error);
        var queue = made.Settings.Queues.Single(q => q.Name == "Selected").Id;
        handler.Holding = true;
        var add = ctx.Service.AddJobAsync(new AddJobRequest { Url = $"{_server.BaseAddress}/file/strict.bin?size=16384&seed=18", QueueId = queue, RequireQueue = true, DestinationDirectory = ctx.Destination });
        try
        {
            await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var deleted = await ctx.Service.QueueActionAsync(new QueueActionRequest { Action = "delete", QueueId = queue, MoveContentsToQueueId = made.Settings.DefaultQueueId });
            Assert.True(deleted.Ok, deleted.Error);
        }
        finally { handler.Holding = false; handler.Release.TrySetResult(); }
        var result = await add.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(result.Accepted); Assert.Equal("queue_missing", result.ErrorCode);
        Assert.Empty(ctx.Service.GetSnapshot().Jobs);
    }

    [Fact]
    public async Task LegacyQueueFallbackAndExplicitZeroRemainCompatible()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var legacy = await ctx.Service.AddJobAsync(new AddJobRequest { Url = $"{_server.BaseAddress}/file/legacy.bin", FileName = "legacy.bin", QueueId = 999, StartMode = StartModes.Later, DestinationDirectory = ctx.Destination });
        Assert.True(legacy.Accepted, legacy.Error); Assert.Equal(ctx.Service.GetSettings().DefaultQueueId, legacy.Job!.QueueId);
        var strict = await ctx.Service.AddJobAsync(new AddJobRequest { Url = $"{_server.BaseAddress}/file/main.bin", FileName = "main.bin", QueueId = 0, RequireQueue = true, StartMode = StartModes.Later, DestinationDirectory = ctx.Destination });
        Assert.True(strict.Accepted, strict.Error); Assert.Equal(0, strict.Job!.QueueId);
    }
}
