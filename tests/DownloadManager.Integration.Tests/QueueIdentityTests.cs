using DownloadManager.Contracts;
using DownloadManager.Persistence;
using DownloadManager.Runtime;

namespace DownloadManager.Integration.Tests;

/// <summary>Queue IDs may be reused; stale identities must not authorize changes to their replacements.</summary>
public sealed partial class HostCommandDispatcherTests
{
    private static async Task<QueueDto> IdentityCreateAsync(DownloadManagerService service, string name)
    {
        var created = await service.QueueActionAsync(new QueueActionRequest { Action = "create", Name = name, MaxConcurrentDownloads = 1 });
        Assert.True(created.Ok, created.Error);
        var queue = Assert.Single(created.Settings.Queues, q => q.Name == name);
        Assert.NotNull(queue.Generation);
        Assert.NotEqual(Guid.Empty, queue.Generation);
        return queue;
    }

    private static async Task<QueueDto> IdentityReplaceEmptyAsync(DownloadManagerService service, QueueDto original, string name)
    {
        var removed = await service.QueueActionAsync(new QueueActionRequest
        {
            Action = "delete", QueueId = original.Id, ExpectedQueueGeneration = original.Generation,
        });
        Assert.True(removed.Ok, removed.Error);
        var replacement = await IdentityCreateAsync(service, name);
        Assert.Equal(original.Id, replacement.Id);
        Assert.NotEqual(original.Generation, replacement.Generation);
        return replacement;
    }

    private async Task<JobDto> IdentityAddAsync(DownloadManagerService service, QueueDto queue, string name)
    {
        var destination = Path.Combine(_root, "identity-downloads");
        Directory.CreateDirectory(destination);
        var added = await service.AddJobWithoutAskingServerAsync(new AddJobRequest
        {
            Url = "https://example.test/" + name, FileName = name, DestinationDirectory = destination,
            StartMode = StartModes.Later, QueueId = queue.Id, RequireQueue = true, ExpectedQueueGeneration = queue.Generation,
        });
        Assert.True(added.Accepted, added.Error);
        return added.Job!;
    }

    [Fact]
    public async Task ReusedQueueIdRejectsStaleDeleteAndStrictAdmissionWithoutMutatingReplacement()
    {
        var service = await ServiceAsync();
        var old = await IdentityCreateAsync(service, "Old queue");
        var replacement = await IdentityReplaceEmptyAsync(service, old, "Replacement");
        var existing = await IdentityAddAsync(service, replacement, "existing.bin");
        var settings = service.GetSettings();
        var scheduleId = Guid.NewGuid();
        settings.Schedules.Add(new ScheduleDto { Id = scheduleId, Name = "Keep", QueueId = replacement.Id, Enabled = false });
        var changed = await service.UpdateSettingsAsync(settings);
        Assert.True(changed.Ok, changed.Error);
        var before = service.GetJob(existing.Id)!;
        var revision = service.GetSettings().Revision;
        var dispatcher = Dispatcher(service); var session = Session();

        var delete = (await AskAsync(dispatcher, session, MessageTypes.QueueAction, new QueueActionRequest
        {
            Action = "delete", QueueId = old.Id, ExpectedQueueGeneration = old.Generation,
            MoveContentsToQueueId = settings.DefaultQueueId, RemoveSchedules = true,
        })).GetPayload<SettingsResult>();
        Assert.False(delete.Ok);
        var current = Assert.Single(service.GetSettings().Queues, q => q.Id == replacement.Id);
        Assert.Equal(replacement.Generation, current.Generation);
        Assert.Contains(service.GetSettings().Schedules, s => s.Id == scheduleId && s.QueueId == replacement.Id);
        var unchanged = service.GetJob(existing.Id)!;
        Assert.Equal(before.QueueId, unchanged.QueueId);
        Assert.Equal(before.IsQueued, unchanged.IsQueued);
        Assert.Equal(before.UpdatedAt, unchanged.UpdatedAt);
        Assert.Equal(revision, service.GetSettings().Revision);

        var admission = await service.AddJobWithoutAskingServerAsync(new AddJobRequest
        {
            Url = "https://example.test/stale.bin", FileName = "stale.bin", DestinationDirectory = Path.Combine(_root, "identity-downloads"),
            StartMode = StartModes.Later, QueueId = old.Id, RequireQueue = true, ExpectedQueueGeneration = old.Generation,
        });
        Assert.False(admission.Accepted);
        Assert.Equal("queue_missing", admission.ErrorCode);
        Assert.Single(service.GetSnapshot().Jobs);
        Assert.Equal(revision, service.GetSettings().Revision);
        var persisted = await new JsonSettingsStore(_paths).LoadAsync();
        Assert.Contains(persisted.Queues, q => q.Id == replacement.Id && q.Generation == replacement.Generation);
        Assert.Contains(persisted.Schedules, s => s.Id == scheduleId && s.QueueId == replacement.Id);
        var jobStore = new JsonJobStore(_paths); await jobStore.InitializeAsync();
        var durableJob = Assert.Single(await jobStore.LoadAllAsync());
        Assert.Equal(replacement.Id, durableJob.QueueId);
        Assert.True(durableJob.IsQueued);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task StaleSourceOrDestinationGenerationRefusesMoveWithoutChangingMembership(bool staleSource, bool detach)
    {
        var service = await ServiceAsync();
        var source = await IdentityCreateAsync(service, "Source");
        var destination = await IdentityCreateAsync(service, "Destination");
        QueueDto original;
        if (staleSource)
        {
            original = source;
            source = await IdentityReplaceEmptyAsync(service, source, "Replacement source");
        }
        else
        {
            original = destination;
            destination = await IdentityReplaceEmptyAsync(service, destination, "Replacement destination");
        }
        var job = await IdentityAddAsync(service, source, "queue-move.bin");
        var before = service.GetJob(job.Id)!;
        var dispatcher = Dispatcher(service); var session = Session();
        var request = new JobQueueRequest
        {
            JobId = job.Id, QueueId = destination.Id, Detach = detach, ExpectedQueueId = source.Id,
            ExpectedSourceQueueGeneration = staleSource ? original.Generation : source.Generation,
            ExpectedDestinationQueueGeneration = detach ? null : staleSource ? destination.Generation : original.Generation,
        };
        var refused = (await AskAsync(dispatcher, session, MessageTypes.JobMoveToQueue, request)).GetPayload<JobActionResponse>();
        Assert.False(refused.Ok);
        var unchanged = service.GetJob(job.Id)!;
        Assert.True(unchanged.IsQueued);
        Assert.Equal(source.Id, unchanged.QueueId);
        Assert.Equal(before.UpdatedAt, unchanged.UpdatedAt);
        var jobStore = new JsonJobStore(_paths); await jobStore.InitializeAsync();
        var durable = Assert.Single(await jobStore.LoadAllAsync());
        Assert.True(durable.IsQueued);
        Assert.Equal(source.Id, durable.QueueId);

        // The same request with current identities succeeds, confirming that the refusal is not an unrelated policy failure.
        request.ExpectedSourceQueueGeneration = source.Generation;
        request.ExpectedDestinationQueueGeneration = detach ? null : destination.Generation;
        var accepted = (await AskAsync(dispatcher, session, MessageTypes.JobMoveToQueue, request)).GetPayload<JobActionResponse>();
        Assert.True(accepted.Ok, accepted.Error);
        var moved = service.GetJob(job.Id)!;
        if (detach) Assert.False(moved.IsQueued);
        else { Assert.True(moved.IsQueued); Assert.Equal(destination.Id, moved.QueueId); }
    }
}
