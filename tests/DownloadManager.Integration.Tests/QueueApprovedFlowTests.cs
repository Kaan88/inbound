using System.Collections.Concurrent;
using DownloadManager.Contracts;
using DownloadManager.Core;
using DownloadManager.Persistence;
using DownloadManager.Runtime;

namespace DownloadManager.Integration.Tests;

public sealed partial class RuntimeServiceTests
{
    [Fact]
    public async Task QueueSummaryWaitsForEveryMemberAndLeavesQueueReadyForTheNextRun()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var settings = ctx.Service.GetSettings();
        var q = settings.Queues.Single(q => q.Id == settings.DefaultQueueId);
        q.NotifyWhenFinished = true; q.RequireCharging = true;
        await ctx.Service.SetQueuePowerStateAsync(false, false);
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);
        var notices = new ConcurrentQueue<QueueFinishedEventArgs>();
        var firstNotice = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextNotice = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.Service.QueueFinished += (_, e) => { notices.Enqueue(e); if (notices.Count == 1) firstNotice.TrySetResult(); else nextNotice.TrySetResult(); };
        var ids = new List<Guid>();
        foreach (var name in new[] { "queue-one.bin", "queue-two.bin" })
        {
            var added = await ctx.Service.AddJobAsync(new AddJobRequest { Url = $"{_server.BaseAddress}/file/{name}?size=32768&seed=741", FileName = name, DestinationDirectory = ctx.Destination, QueueId = q.Id, StartMode = "queue" });
            Assert.True(added.Accepted, added.Error); ids.Add(added.Job!.Id);
        }
        Assert.Empty(notices);
        await ctx.Service.SetQueuePowerStateAsync(true, false);
        foreach (var id in ids) await ctx.WaitFor(id, j => j.State == "completed");
        await firstNotice.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Single(notices); Assert.Equal(2, notices.Single().Completed);
        Assert.True(ctx.Service.GetSettings().Queues.Single(q => q.Id == settings.DefaultQueueId).IsRunning);
        var next = await ctx.Service.AddJobAsync(new AddJobRequest { Url = $"{_server.BaseAddress}/file/queue-next.bin?size=32768&seed=742", FileName = "queue-next.bin", DestinationDirectory = ctx.Destination, QueueId = q.Id, StartMode = "queue" });
        Assert.True(next.Accepted, next.Error); await ctx.WaitFor(next.Job!.Id, j => j.State == "completed");
        await nextNotice.Task.WaitAsync(TimeSpan.FromSeconds(10)); Assert.Equal(2, notices.Count);
    }

    [Fact]
    public async Task ConditionalMoveAndDetachDoNotOverwriteAnotherClientsMembership()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var settings = ctx.Service.GetSettings();
        var created = await ctx.Service.QueueActionAsync(new QueueActionRequest { Action = "create", Name = "Other client", MaxConcurrentDownloads = 1 });
        Assert.True(created.Ok, created.Error); var other = created.Settings.Queues.Single(q => q.Name == "Other client").Id;
        var added = await ctx.Service.AddJobAsync(new AddJobRequest { Url = $"{_server.BaseAddress}/file/membership.bin?size=32768&seed=745", FileName = "membership.bin", DestinationDirectory = ctx.Destination, StartMode = "later" });
        Assert.True(added.Accepted, added.Error);
        Assert.True((await ctx.Service.MoveToQueueAsync(added.Job!.Id, other)).Ok);
        var refused = await ctx.Service.MoveToQueueAsync(added.Job.Id, settings.DefaultQueueId, expectedQueueId: settings.DefaultQueueId);
        Assert.False(refused.Ok); Assert.Equal(other, ctx.Service.GetJob(added.Job.Id)!.QueueId);
        refused = await ctx.Service.DetachFromQueueAsync(added.Job.Id, expectedQueueId: settings.DefaultQueueId);
        Assert.False(refused.Ok); Assert.True(ctx.Service.GetJob(added.Job.Id)!.IsQueued);
        Assert.True((await ctx.Service.DetachFromQueueAsync(added.Job.Id, expectedQueueId: other)).Ok);
    }

    [Fact]
    public async Task QueueSummaryPreferencePersistsAndOldClientsDoNotClearIt()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var settings = ctx.Service.GetSettings(); settings.Queues[0].NotifyWhenFinished = true;
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);
        settings = ctx.Service.GetSettings(); settings.Queues[0].NotifyWhenFinished = null;
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);
        Assert.True(ctx.Service.GetSettings().Queues[0].NotifyWhenFinished);
        Assert.True((await new JsonSettingsStore(ctx.Paths).LoadAsync()).Queues[0].NotifyWhenFinished);
        settings = ctx.Service.GetSettings(); settings.Queues[0].NotifyWhenFinished = false;
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);
        var noticed = false; ctx.Service.QueueFinished += (_, _) => noticed = true;
        var added = await ctx.Service.AddJobAsync(new AddJobRequest { Url = $"{_server.BaseAddress}/file/no-summary.bin?size=32768&seed=743", FileName = "no-summary.bin", DestinationDirectory = ctx.Destination });
        Assert.True(added.Accepted, added.Error); await ctx.WaitFor(added.Job!.Id, j => j.State == "completed");
        Assert.False(noticed);
    }

    [Fact]
    public async Task QueueDeleteRemovesSchedulesKeepsFilesAndDestinationRunState()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var created = await ctx.Service.QueueActionAsync(new QueueActionRequest { Action = "create", Name = "Delete me", MaxConcurrentDownloads = 1 });
        Assert.True(created.Ok, created.Error); var source = created.Settings.Queues.Single(q => q.Name == "Delete me");
        Assert.True((await ctx.Service.QueueActionAsync(new QueueActionRequest { Action = "start", QueueId = source.Id })).Ok);
        var added = await ctx.Service.AddJobAsync(new AddJobRequest { Url = $"{_server.BaseAddress}/file/keep-me.bin?size=32768&seed=744", FileName = "keep-me.bin", DestinationDirectory = ctx.Destination, QueueId = source.Id, StartMode = "now" });
        Assert.True(added.Accepted, added.Error); var done = await ctx.WaitFor(added.Job!.Id, j => j.State == "completed");
        var settings = ctx.Service.GetSettings(); var target = settings.DefaultQueueId;
        settings.Schedules.Add(new ScheduleDto { Id = Guid.NewGuid(), QueueId = source.Id, Name = "Remove me", StartTime = "23:59", Enabled = false });
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);
        Assert.True((await ctx.Service.QueueActionAsync(new QueueActionRequest { Action = "stop", QueueId = target })).Ok);
        var deleted = await ctx.Service.QueueActionAsync(new QueueActionRequest { Action = "delete", QueueId = source.Id, MoveContentsToQueueId = target, RemoveSchedules = true });
        Assert.True(deleted.Ok, deleted.Error); Assert.DoesNotContain(deleted.Settings.Queues, q => q.Id == source.Id);
        Assert.DoesNotContain(deleted.Settings.Schedules, s => s.Name == "Remove me");
        Assert.False(deleted.Settings.Queues.Single(q => q.Id == target).IsRunning);
        Assert.Equal(target, ctx.Service.GetJob(done.Id)!.QueueId); Assert.True(File.Exists(done.FinalFilePath));
        var persisted = await new JsonSettingsStore(ctx.Paths).LoadAsync();
        Assert.DoesNotContain(persisted.Queues, q => q.Id == source.Id); Assert.DoesNotContain(persisted.Schedules, s => s.Name == "Remove me");
    }

    [Fact]
    public async Task QueueDeleteSaveFailureRetainsLiveAndPersistedQueueAndSchedules()
    {
        QueueCreationFailingStore? store = null;
        await using var ctx = await ServiceContext.CreateAsync(_server, hooks: new ContextHooks { WrapSettings = inner => store = new QueueCreationFailingStore(inner) });
        var created = await ctx.Service.QueueActionAsync(new QueueActionRequest { Action = "create", Name = "Kept", MaxConcurrentDownloads = 1 });
        Assert.True(created.Ok, created.Error); var source = created.Settings.Queues.Single(q => q.Name == "Kept");
        var settings = ctx.Service.GetSettings(); settings.Schedules.Add(new ScheduleDto { QueueId = source.Id, Name = "Kept schedule", Enabled = false });
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok); store!.Fail = true;
        var refused = await ctx.Service.QueueActionAsync(new QueueActionRequest { Action = "delete", QueueId = source.Id, MoveContentsToQueueId = settings.DefaultQueueId, RemoveSchedules = true });
        Assert.False(refused.Ok); Assert.Contains(ctx.Service.GetSettings().Queues, q => q.Id == source.Id);
        Assert.Contains(ctx.Service.GetSettings().Schedules, s => s.Name == "Kept schedule" && s.QueueId == source.Id);
        var persisted = await new JsonSettingsStore(ctx.Paths).LoadAsync(); Assert.Contains(persisted.Queues, q => q.Id == source.Id); Assert.Contains(persisted.Schedules, s => s.QueueId == source.Id);
        store.Fail = false;
    }
}
