using DownloadManager.Contracts;
using DownloadManager.Core;
using DownloadManager.Persistence;
using TestServer;

namespace DownloadManager.Integration.Tests;

public sealed partial class RuntimeServiceTests
{
    [Fact]
    public async Task RevokedBeforeInitializationBlocksManualAndScheduledQueueStartsUntilRestored()
    {
        // Before initialization this setter only stores the flag and completes synchronously.
        var hooks = new ContextHooks
        {
            BeforeInitialize = service => Assert.True(service.SetPlatformTransfersAllowedAsync(false).IsCompletedSuccessfully),
        };
        await using var ctx = await ServiceContext.CreateAsync(_server, hooks: hooks);
        Assert.True((await ctx.Service.QueueActionAsync(new QueueActionRequest { QueueId = 0, Action = "stop" })).Ok);
        var settings = ctx.Service.GetSnapshot().Settings;
        settings.Schedules.Add(new ScheduleDto
        {
            Id = Guid.NewGuid(), QueueId = 0, Name = "Lease test", Enabled = true,
            Kind = "daily", Days = "Wed", StartTime = "09:00",
        });
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);
        const int size = 65_536, seed = 741;
        var added = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/file/lease-held.bin?size={size}&seed={seed}",
            DestinationDirectory = ctx.Destination, FileName = "lease-held.bin", QueueId = 0, StartMode = "queue",
        });
        Assert.True(added.Accepted, added.Error);
        var id = added.Job!.Id;
        Assert.True((await ctx.Service.QueueActionAsync(new QueueActionRequest { QueueId = 0, Action = "start" })).Ok);
        await AssertLeaseHeld(ctx, id);
        Assert.True((await ctx.Service.QueueActionAsync(new QueueActionRequest { QueueId = 0, Action = "stop" })).Ok);
        var beforeStart = new DateTimeOffset(2026, 11, 4, 8, 59, 0, TimeSpan.Zero);
        await ctx.Service.EvaluateSchedulesAsync(beforeStart);
        var scheduled = Assert.Single(await ctx.Service.EvaluateSchedulesAsync(beforeStart.AddMinutes(1)));
        Assert.Equal("start", scheduled.Action); Assert.True(scheduled.Ok, scheduled.Error);
        await AssertLeaseHeld(ctx, id);
        Assert.True(ctx.Service.GetSnapshot().Settings.Queues.Single(q => q.Id == 0).IsRunning);
        await ctx.Service.SetPlatformTransfersAllowedAsync(true);
        var finished = await ctx.WaitFor(id, j => j.State == "completed");
        Assert.Equal(LeaseHash(seed, size), await Hashing.Sha256FileAsync(finished.FinalFilePath));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RevocationPreemptsRunningWorkAndRestoreCompletesIt(bool resumable)
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        const int size = 524_288, seed = 742;
        var route = resumable ? "slow" : "norange";
        var added = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/{route}/lease-running.bin?size={size}&seed={seed}&bps=131072",
            DestinationDirectory = ctx.Destination, FileName = "lease-running.bin", QueueId = 0, StartMode = "queue",
        });
        Assert.True(added.Accepted, added.Error);
        var id = added.Job!.Id;
        var running = await ctx.WaitFor(id, j => j.State == "downloading" && j.ReceivedBytes >= 32_768);
        Assert.Equal(resumable, running.ResumeSupported);
        await ctx.Service.SetPlatformTransfersAllowedAsync(false);
        var held = await ctx.WaitFor(id, j => j.State == "queued");
        Assert.True(held.ReceivedBytes > 0);
        // Wait for the engine to finish its cancellation and write a resumable checkpoint.
        if (resumable)
        {
            var checkpoints = new JsonCheckpointStore(ctx.Paths);
            var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            DownloadCheckpoint? checkpoint = null;
            while (DateTimeOffset.UtcNow < deadline)
            {
                checkpoint = await checkpoints.LoadAsync(id);
                if (checkpoint?.CompletedBytes > 0) break;
                await Task.Delay(50);
            }
            Assert.NotNull(checkpoint); Assert.True(checkpoint!.CompletedBytes > 0);
        }
        await Task.Delay(400);
        Assert.Equal("queued", ctx.Service.GetJob(id)!.State);
        var saved = await new JsonJobStore(ctx.Paths).LoadAsync(id);
        Assert.NotNull(saved); Assert.Equal(DownloadState.Queued, saved!.State);
        Assert.True(ctx.Service.GetSnapshot().Settings.Queues.Single(q => q.Id == 0).IsRunning);
        await ctx.Service.SetPlatformTransfersAllowedAsync(true);
        var finished = await ctx.WaitFor(id, j => j.State == "completed", TimeSpan.FromSeconds(20));
        Assert.Equal(LeaseHash(seed, size), await Hashing.Sha256FileAsync(finished.FinalFilePath));
    }

    [Fact]
    public async Task RestoringLeaseDoesNotUndoManualPause()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        const int size = 262_144, seed = 743;
        var added = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/slow/lease-paused.bin?size={size}&seed={seed}&bps=131072",
            DestinationDirectory = ctx.Destination, FileName = "lease-paused.bin", QueueId = 0, StartMode = "queue",
        });
        Assert.True(added.Accepted, added.Error);
        var id = added.Job!.Id;
        await ctx.WaitFor(id, j => j.State == "downloading" && j.ReceivedBytes > 16_384);
        var paused = await ctx.Service.PauseAsync(id);
        Assert.True(paused.Ok, paused.Error);
        await ctx.WaitFor(id, j => j.State == "paused");
        await ctx.Service.SetPlatformTransfersAllowedAsync(false);
        await ctx.Service.SetPlatformTransfersAllowedAsync(true);
        await Task.Delay(400);
        Assert.Equal("paused", ctx.Service.GetJob(id)!.State);
        Assert.True((await ctx.Service.ResumeAsync(id)).Ok);
        var finished = await ctx.WaitFor(id, j => j.State == "completed", TimeSpan.FromSeconds(20));
        Assert.Equal(LeaseHash(seed, size), await Hashing.Sha256FileAsync(finished.FinalFilePath));
    }

    private static async Task AssertLeaseHeld(ServiceContext ctx, Guid id)
    {
        await Task.Delay(300);
        var job = ctx.Service.GetJob(id)!;
        Assert.Equal("queued", job.State); Assert.Equal(0, job.ReceivedBytes);
        Assert.Equal("Waiting for Android to allow background work", ctx.Service.GetSnapshot().Settings.Queues.Single(q => q.Id == 0).ConditionWaitReason);
    }
    private static string LeaseHash(int seed, int size) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(TestContent.Generate(seed, size))).ToLowerInvariant();
}
