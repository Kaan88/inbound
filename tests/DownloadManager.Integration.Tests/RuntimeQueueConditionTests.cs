using DownloadManager.Contracts;
using DownloadManager.Core;
using TestServer;

namespace DownloadManager.Integration.Tests;

public sealed partial class RuntimeServiceTests
{
    [Fact]
    public async Task QueueWaitsForAllNetworkAndPowerConditionsThenDownloadsTheCorrectBytes()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        await ctx.Service.SetNetworkMeteredAsync(true);
        await ctx.Service.SetQueuePowerStateAsync(charging: false, batterySaver: true);
        var settings = ctx.Service.GetSnapshot().Settings;
        var queue = settings.Queues.Single(q => q.Id == 0);
        queue.RequireUnmeteredNetwork = true;
        queue.RequireCharging = true;
        queue.RequireBatterySaverOff = true;
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);

        const int size = 65_536, seed = 436;
        var added = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/file/conditions.bin?size={size}&seed={seed}",
            DestinationDirectory = ctx.Destination, FileName = "conditions.bin", QueueId = 0, StartMode = "queue",
        });
        Assert.True(added.Accepted, added.Error);
        var id = added.Job!.Id;
        await AssertQueueHeld(ctx, id, "Waiting for Wi-Fi");
        await ctx.Service.SetNetworkMeteredAsync(false);
        await AssertQueueHeld(ctx, id, "Waiting for charging");
        await ctx.Service.SetQueuePowerStateAsync(charging: true, batterySaver: true);
        await AssertQueueHeld(ctx, id, "Waiting for battery saver to turn off");
        await ctx.Service.SetQueuePowerStateAsync(charging: true, batterySaver: false);
        var completed = await ctx.WaitFor(id, j => j.State == "completed");
        var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(TestContent.Generate(seed, size))).ToLowerInvariant();
        Assert.Equal(expected, await Hashing.Sha256FileAsync(completed.FinalFilePath));
        Assert.Null(ctx.Service.GetSnapshot().Settings.Queues.Single(q => q.Id == 0).ConditionWaitReason);
    }

    [Fact]
    public async Task QueuePolicySurvivesRestartUnknownPowerHoldsAndOldSettingsDoNotEraseIt()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        await ctx.Service.SetNetworkMeteredAsync(true);
        await ctx.Service.SetQueuePowerStateAsync(charging: false, batterySaver: true);
        var settings = ctx.Service.GetSnapshot().Settings;
        var q = settings.Queues.Single(q => q.Id == 0);
        q.RequireUnmeteredNetwork = true; q.RequireCharging = true; q.RequireBatterySaverOff = true;
        q.SpeedLimitBytesPerSecond = 131_072;
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);

        // An old client does not know these fields and sends null for each one.
        var old = ctx.Service.GetSnapshot().Settings;
        var oldQueue = old.Queues.Single(x => x.Id == 0);
        oldQueue.RequireUnmeteredNetwork = null; oldQueue.RequireCharging = null;
        oldQueue.RequireBatterySaverOff = null; oldQueue.SpeedLimitBytesPerSecond = null;
        old.Theme = "Dark";
        var saved = await ctx.Service.UpdateSettingsAsync(old);
        Assert.True(saved.Ok, saved.Error);
        var retained = saved.Settings.Queues.Single(x => x.Id == 0);
        Assert.Equal(true, retained.RequireUnmeteredNetwork); Assert.Equal(true, retained.RequireCharging);
        Assert.Equal(true, retained.RequireBatterySaverOff); Assert.Equal(131_072L, retained.SpeedLimitBytesPerSecond);

        var added = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/file/power-after-restart.bin?size=32768&seed=437",
            DestinationDirectory = ctx.Destination, FileName = "power-after-restart.bin", QueueId = 0, StartMode = "queue",
        });
        Assert.True(added.Accepted, added.Error);
        await AssertQueueHeld(ctx, added.Job!.Id, "Waiting for Wi-Fi");
        await ctx.Service.ShutdownAsync();
        var persisted = await File.ReadAllTextAsync(ctx.Paths.SettingsFile);
        Assert.DoesNotContain("conditionWaitReason", persisted, StringComparison.OrdinalIgnoreCase);

        await using var restarted = await ServiceContext.CreateAsync(_server, ctx.Paths, ctx.Destination);
        var loaded = restarted.Service.GetSnapshot().Settings.Queues.Single(x => x.Id == 0);
        Assert.Equal(true, loaded.RequireUnmeteredNetwork); Assert.Equal(true, loaded.RequireCharging);
        Assert.Equal(true, loaded.RequireBatterySaverOff); Assert.Equal(131_072L, loaded.SpeedLimitBytesPerSecond);
        // The old metered report was transient. With no new power report, charging must still hold work.
        await AssertQueueHeld(restarted, added.Job.Id, "Waiting for charging");
        await restarted.Service.SetQueuePowerStateAsync(charging: true, batterySaver: false);
        var finished = await restarted.WaitFor(added.Job.Id, j => j.State == "completed");
        var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(TestContent.Generate(437, 32768))).ToLowerInvariant();
        Assert.Equal(expected, await Hashing.Sha256FileAsync(finished.FinalFilePath));
    }

    [Fact]
    public async Task EnvironmentChangesDoNotRestartAManuallyStoppedQueue()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var settings = ctx.Service.GetSnapshot().Settings;
        var q = settings.Queues.Single(x => x.Id == 0);
        q.RequireUnmeteredNetwork = true; q.RequireCharging = true; q.RequireBatterySaverOff = true;
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);
        Assert.True((await ctx.Service.QueueActionAsync(new QueueActionRequest { QueueId = 0, Action = "stop" })).Ok);
        var added = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/file/manual-stop.bin?size=16384&seed=438",
            DestinationDirectory = ctx.Destination, FileName = "manual-stop.bin", QueueId = 0, StartMode = "queue",
        });
        Assert.True(added.Accepted, added.Error);
        await ctx.Service.SetNetworkMeteredAsync(true);
        await ctx.Service.SetQueuePowerStateAsync(charging: false, batterySaver: true);
        await ctx.Service.SetNetworkMeteredAsync(false);
        await ctx.Service.SetQueuePowerStateAsync(charging: true, batterySaver: false);
        await Task.Delay(300);
        Assert.False(ctx.Service.GetSnapshot().Settings.Queues.Single(x => x.Id == 0).IsRunning);
        Assert.Equal("queued", ctx.Service.GetJob(added.Job!.Id)!.State);
        Assert.Equal(0, ctx.Service.GetJob(added.Job.Id)!.ReceivedBytes);
        Assert.True((await ctx.Service.QueueActionAsync(new QueueActionRequest { QueueId = 0, Action = "start" })).Ok);
        await ctx.WaitFor(added.Job.Id, j => j.State == "completed");
    }

    [Fact]
    public async Task SettingsCompareAndSwapRejectsStaleRevisionAndKeepsLatestPolicy()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var stale = ctx.Service.GetSnapshot().Settings;
        var originalRevision = stale.Revision;
        var latest = ctx.Service.GetSnapshot().Settings;
        latest.Queues.Single(q => q.Id == 0).RequireCharging = true;
        latest.Queues.Single(q => q.Id == 0).SpeedLimitBytesPerSecond = 65_536;
        var changed = await ctx.Service.UpdateSettingsAsync(latest, expectedRevision: originalRevision);
        Assert.True(changed.Ok, changed.Error);
        Assert.True(changed.Settings.Revision > originalRevision);
        stale.Queues.Single(q => q.Id == 0).Name = "Stale name";
        var refused = await ctx.Service.UpdateSettingsAsync(stale, expectedRevision: originalRevision);
        Assert.False(refused.Ok);
        Assert.Equal(changed.Settings.Revision, refused.Settings.Revision);
        var actual = ctx.Service.GetSnapshot().Settings;
        Assert.Equal(changed.Settings.Revision, actual.Revision);
        Assert.Equal(changed.Settings.Queues.Single(q => q.Id == 0).Name, actual.Queues.Single(q => q.Id == 0).Name);
        Assert.Equal(true, actual.Queues.Single(q => q.Id == 0).RequireCharging);
        Assert.Equal(65_536L, actual.Queues.Single(q => q.Id == 0).SpeedLimitBytesPerSecond);
    }

    private static async Task AssertQueueHeld(ServiceContext ctx, Guid jobId, string reason)
    {
        // Give a mistakenly scheduled fast download time to start; check both state and bytes.
        await Task.Delay(300);
        var job = ctx.Service.GetJob(jobId)!;
        Assert.Equal("queued", job.State);
        Assert.Equal(0, job.ReceivedBytes);
        var queue = ctx.Service.GetSnapshot().Settings.Queues.Single(q => q.Id == 0);
        Assert.True(queue.IsRunning);
        Assert.Equal(reason, queue.ConditionWaitReason);
    }
}
