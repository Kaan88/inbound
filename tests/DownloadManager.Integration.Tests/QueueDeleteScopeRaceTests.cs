using DownloadManager.Contracts;
using DownloadManager.Core;
using DownloadManager.Persistence;
using DownloadManager.Runtime;
using DownloadManager.Runtime.WebAuth;

namespace DownloadManager.Integration.Tests;

public sealed partial class HostCommandDispatcherTests
{
    // These holds create a real gate ordering without sleeps: the first settings save holds the settings gate;
    // the outside job's subsequent move holds it again while its own durable save is in flight.
    private sealed class ScopeRaceSettingsStore(ISettingsStore inner) : ISettingsStore
    {
        public bool Hold { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) => inner.LoadAsync(cancellationToken);
        public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            if (Hold) { Hold = false; Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); }
            await inner.SaveAsync(settings, cancellationToken);
        }
    }
    private sealed class ScopeRaceJobStore(IJobStore inner) : IJobStore
    {
        public Guid HoldId { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task InitializeAsync(CancellationToken cancellationToken = default) => inner.InitializeAsync(cancellationToken);
        public Task<IReadOnlyList<DownloadJob>> LoadAllAsync(CancellationToken cancellationToken = default) => inner.LoadAllAsync(cancellationToken);
        public Task<DownloadJob?> LoadAsync(Guid jobId, CancellationToken cancellationToken = default) => inner.LoadAsync(jobId, cancellationToken);
        public async Task SaveAsync(DownloadJob job, CancellationToken cancellationToken = default)
        {
            if (job.Id == HoldId) { HoldId = Guid.Empty; Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); }
            await inner.SaveAsync(job, cancellationToken);
        }
        public Task DeleteAsync(Guid jobId, CancellationToken cancellationToken = default) => inner.DeleteAsync(jobId, cancellationToken);
        public Task<Guid?> FindClaimedRequestAsync(string extensionId, string clientRequestId, CancellationToken cancellationToken = default) => inner.FindClaimedRequestAsync(extensionId, clientRequestId, cancellationToken);
        public Task<bool> TryClaimRequestAsync(string extensionId, string clientRequestId, Guid jobId, CancellationToken cancellationToken = default) => inner.TryClaimRequestAsync(extensionId, clientRequestId, jobId, cancellationToken);
        public Task DeleteClaimAsync(string extensionId, string clientRequestId, CancellationToken cancellationToken = default) => inner.DeleteClaimAsync(extensionId, clientRequestId, cancellationToken);
    }

    [Fact]
    public async Task ScopedDeleteRechecksMembersAfterWaitingForTheSettingsGate()
    {
        Directory.CreateDirectory(Inside); Directory.CreateDirectory(Outside);
        var disk = new JsonJobStore(_paths); await disk.InitializeAsync();
        var inside = SeededJob(Inside, "scope-inside.bin");
        var outside = SeededJob(Outside, "scope-outside.bin");
        await disk.SaveAsync(inside); await disk.SaveAsync(outside);
        var jobs = new ScopeRaceJobStore(disk);
        var settings = new ScopeRaceSettingsStore(new JsonSettingsStore(_paths));
        var checkpoints = new JsonCheckpointStore(_paths);
        _engine = new HttpDownloadEngine(new EngineOptions { MaxAttempts = 1, AllowLocalNetwork = true }, checkpoints: checkpoints);
        _service = new DownloadManagerService(_paths, jobs, checkpoints, settings, _engine, enableSchedulerTimer: false);
        await _service.InitializeAsync();
        var web = await WebAsync(service: _service);
        var queue = await IdentityCreateAsync(_service, "Scoped queue");
        Assert.True((await _service.MoveToQueueAsync(inside.Id, queue.Id)).Ok);
        Assert.NotEqual(queue.Id, _service.GetJob(outside.Id)!.QueueId);

        // The scoped dispatcher's preliminary snapshot contains only the authorized inside job.
        // The already-queued outside move obtains the gate ahead of the delete after the settings save releases it.
        settings.Hold = true;
        var update = _service.GetSettings(); update.MaxConcurrentDownloads = update.MaxConcurrentDownloads == 3 ? 4 : 3;
        var holding = _service.UpdateSettingsAsync(update);
        Task<JobActionResponse>? move = null;
        Task<IpcMessage>? deletion = null;
        try
        {
            await settings.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            jobs.HoldId = outside.Id;
            move = _service.MoveToQueueAsync(outside.Id, queue.Id);
            Assert.False(move.IsCompleted);
            deletion = AskAsync(web.Dispatcher, web.Session, MessageTypes.QueueAction, new QueueActionRequest
            {
                Action = "delete", QueueId = queue.Id, ExpectedQueueGeneration = queue.Generation,
                MoveContentsToQueueId = 0, RemoveSchedules = true,
            });
            Assert.False(deletion.IsCompleted); // the early scope check passed; service is waiting at the gate
            settings.Release.TrySetResult();
            Assert.True((await holding.WaitAsync(TimeSpan.FromSeconds(10))).Ok);
            await jobs.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(deletion.IsCompleted);
            Assert.Equal(queue.Id, _service.GetJob(outside.Id)!.QueueId);
            jobs.Release.TrySetResult();
            Assert.True((await move.WaitAsync(TimeSpan.FromSeconds(10))).Ok);
            var beforeInside = _service.GetJob(inside.Id)!; var beforeOutside = _service.GetJob(outside.Id)!;
            var beforeSettings = _service.GetSettings();
            var result = (await deletion.WaitAsync(TimeSpan.FromSeconds(10))).GetPayload<SettingsResult>();
            Assert.False(result.Ok);
            Assert.Equal(CallerScope.OutsideJobRefused, result.Error);
            Assert.Equal(queue.Id, _service.GetJob(inside.Id)!.QueueId);
            Assert.Equal(queue.Id, _service.GetJob(outside.Id)!.QueueId);
            Assert.Equal(beforeInside.UpdatedAt, _service.GetJob(inside.Id)!.UpdatedAt);
            Assert.Equal(beforeOutside.UpdatedAt, _service.GetJob(outside.Id)!.UpdatedAt);
            Assert.Equal(beforeSettings.Revision, _service.GetSettings().Revision);
            Assert.Contains(_service.GetSettings().Queues, q => q.Id == queue.Id && q.Generation == queue.Generation);
            Assert.Equal(queue.Id, (await disk.LoadAsync(inside.Id))!.QueueId);
            Assert.Equal(queue.Id, (await disk.LoadAsync(outside.Id))!.QueueId);
        }
        finally
        {
            settings.Release.TrySetResult(); jobs.Release.TrySetResult();
            if (move is not null) await move.WaitAsync(TimeSpan.FromSeconds(10));
            if (deletion is not null) await deletion.WaitAsync(TimeSpan.FromSeconds(10));
            await holding.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task DeleteRejectsReplacedDestinationGenerationBeforeMovingMembers()
    {
        var service = await ServiceAsync();
        var source = await IdentityCreateAsync(service, "Source");
        var oldDestination = await IdentityCreateAsync(service, "Old destination");
        var destination = await IdentityReplaceEmptyAsync(service, oldDestination, "Replacement destination");
        var job = await IdentityAddAsync(service, source, "destination-race.bin");
        var prior = service.GetJob(job.Id)!; var before = service.GetSettings();
        var dispatcher = Dispatcher(service); var session = Session();
        var result = (await AskAsync(dispatcher, session, MessageTypes.QueueAction, new QueueActionRequest
        {
            Action = "delete", QueueId = source.Id, ExpectedQueueGeneration = source.Generation,
            MoveContentsToQueueId = destination.Id, ExpectedDestinationQueueGeneration = oldDestination.Generation,
        })).GetPayload<SettingsResult>();
        Assert.False(result.Ok);
        Assert.Equal(source.Id, service.GetJob(job.Id)!.QueueId);
        Assert.Equal(prior.UpdatedAt, service.GetJob(job.Id)!.UpdatedAt);
        Assert.Equal(before.Revision, service.GetSettings().Revision);
        Assert.Contains(service.GetSettings().Queues, q => q.Id == source.Id && q.Generation == source.Generation);
        Assert.Contains(service.GetSettings().Queues, q => q.Id == destination.Id && q.Generation == destination.Generation);
        Assert.Equal(source.Id, (await new JsonJobStore(_paths).LoadAsync(job.Id))!.QueueId);
    }

    [Fact]
    public async Task UnrelatedSettingsSaveAndReloadKeepQueueIdentity()
    {
        var service = await ServiceAsync();
        var queue = await IdentityCreateAsync(service, "Keep identity");
        var settings = service.GetSettings(); settings.MaxConcurrentDownloads = settings.MaxConcurrentDownloads == 3 ? 4 : 3;
        Assert.True((await service.UpdateSettingsAsync(settings)).Ok);
        Assert.Equal(queue.Generation, service.GetSettings().Queues.Single(q => q.Id == queue.Id).Generation);
        var disk = await new JsonSettingsStore(_paths).LoadAsync();
        Assert.Equal(queue.Generation, disk.Queues.Single(q => q.Id == queue.Id).Generation);
    }
}
