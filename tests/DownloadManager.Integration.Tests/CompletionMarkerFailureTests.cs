using DownloadManager.Contracts;
using DownloadManager.Core;
using DownloadManager.Persistence;
using DownloadManager.Runtime;

namespace DownloadManager.Integration.Tests;

public sealed partial class RuntimeServiceTests
{
    [Fact]
    public async Task FailedCompletionMarkerWriteKeepsPayloadButPreventsEverySideEffect()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "test-work", "marker-failure-" + Guid.NewGuid().ToString("N"));
        var paths = new StoragePaths(Path.Combine(root, "state"));
        var destination = Path.Combine(root, "downloads");
        Directory.CreateDirectory(destination);
        var durable = new JsonJobStore(paths);
        var store = new RejectCompletionMarkerStore(durable);
        var checkpoints = new JsonCheckpointStore(paths);
        var settings = new JsonSettingsStore(paths);
        await settings.SaveAsync(new AppSettings
        {
            DefaultDownloadDirectory = destination,
            DefaultConnections = 1,
            PostDownloadCommand = Path.Combine(destination, "must-not-execute.exe"),
        });
        using var engine = new HttpDownloadEngine(new EngineOptions { AllowLocalNetwork = true, MaxAttempts = 1 }, checkpoints: checkpoints);
        var hook = new RecordingPostDownloadHook();
        var actions = new RecordingCompletionExecutor();
        var skipped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new DownloadManagerService(paths, store, checkpoints, settings, engine,
            enableSchedulerTimer: false, completionActions: actions, postDownloadHook: hook,
            log: message =>
            {
                if (message.StartsWith("Completion actions skipped because their execution marker", StringComparison.Ordinal))
                    skipped.TrySetResult();
            });
        try
        {
            await service.InitializeAsync();
            var add = await service.AddJobAsync(new AddJobRequest
            {
                Url = $"{_server.BaseAddress}/file/marker-failure.bin?size=32768&seed=907",
                DestinationDirectory = destination, FileName = "marker-failure.bin", StartMode = "later", Source = "cli",
            });
            Assert.True(add.Accepted, add.Error);
            Assert.True((await service.UpdateJobAsync(new JobUpdateRequest
            {
                JobId = add.Job!.Id, CompletionOptions = new JobCompletionOptionsDto { OpenFile = true },
            })).Ok);
            Assert.True((await service.ResumeAsync(add.Job.Id)).Ok);
            await skipped.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Empty(hook.Calls);
            Assert.Equal(0, actions.OpenAttempts);
            Assert.Equal(0, actions.ExitCount);
            Assert.Equal(0, actions.ShutdownCount);
            Assert.Equal("completed", service.GetJob(add.Job.Id)!.State);
            var saved = await durable.LoadAsync(add.Job.Id);
            Assert.NotNull(saved);
            Assert.False(saved.CompletionActionsStarted);
            Assert.Equal(32768, new FileInfo(saved.FinalFilePath).Length);
            Assert.True(store.RejectedMarkers > 0);
        }
        finally
        {
            await service.ShutdownAsync();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private sealed class RejectCompletionMarkerStore(IJobStore inner) : IJobStore
    {
        public int RejectedMarkers { get; private set; }
        public Task InitializeAsync(CancellationToken cancellationToken = default) => inner.InitializeAsync(cancellationToken);
        public Task<IReadOnlyList<DownloadJob>> LoadAllAsync(CancellationToken cancellationToken = default) => inner.LoadAllAsync(cancellationToken);
        public Task<DownloadJob?> LoadAsync(Guid jobId, CancellationToken cancellationToken = default) => inner.LoadAsync(jobId, cancellationToken);
        public Task SaveAsync(DownloadJob job, CancellationToken cancellationToken = default)
        {
            if (!job.CompletionActionsStarted) return inner.SaveAsync(job, cancellationToken);
            RejectedMarkers++;
            return Task.FromException(new IOException("Injected marker write failure."));
        }
        public Task DeleteAsync(Guid jobId, CancellationToken cancellationToken = default) => inner.DeleteAsync(jobId, cancellationToken);
        public Task<Guid?> FindClaimedRequestAsync(string extensionId, string clientRequestId, CancellationToken cancellationToken = default)
            => inner.FindClaimedRequestAsync(extensionId, clientRequestId, cancellationToken);
        public Task<bool> TryClaimRequestAsync(string extensionId, string clientRequestId, Guid jobId, CancellationToken cancellationToken = default)
            => inner.TryClaimRequestAsync(extensionId, clientRequestId, jobId, cancellationToken);
        public Task DeleteClaimAsync(string extensionId, string clientRequestId, CancellationToken cancellationToken = default)
            => inner.DeleteClaimAsync(extensionId, clientRequestId, cancellationToken);
    }
}
