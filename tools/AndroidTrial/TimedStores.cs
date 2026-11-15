using DownloadManager.Core;

namespace AndroidTrial;

/// <summary>Decorators that time the store calls DownloadManagerService.InitializeAsync makes (it can't be instrumented: no change under src/).</summary>
internal sealed class TimedJobStore(IJobStore inner) : IJobStore
{
    public Task InitializeAsync(CancellationToken cancellationToken = default) => StartupTrace.TimeAsync("jobStore.Initialize", () => inner.InitializeAsync(cancellationToken));

    public Task<IReadOnlyList<DownloadJob>> LoadAllAsync(CancellationToken cancellationToken = default) => StartupTrace.TimeAsync("jobStore.LoadAll", () => inner.LoadAllAsync(cancellationToken));

    public Task<DownloadJob?> LoadAsync(Guid jobId, CancellationToken cancellationToken = default) => StartupTrace.TimeAsync("jobStore.Load", () => inner.LoadAsync(jobId, cancellationToken));

    public Task SaveAsync(DownloadJob job, CancellationToken cancellationToken = default) => StartupTrace.TimeAsync("jobStore.Save", () => inner.SaveAsync(job, cancellationToken));

    public Task DeleteAsync(Guid jobId, CancellationToken cancellationToken = default) => StartupTrace.TimeAsync("jobStore.Delete", () => inner.DeleteAsync(jobId, cancellationToken));

    public Task<Guid?> FindClaimedRequestAsync(string extensionId, string clientRequestId, CancellationToken cancellationToken = default) => inner.FindClaimedRequestAsync(extensionId, clientRequestId, cancellationToken);

    public Task<bool> TryClaimRequestAsync(string extensionId, string clientRequestId, Guid jobId, CancellationToken cancellationToken = default) => inner.TryClaimRequestAsync(extensionId, clientRequestId, jobId, cancellationToken);

    public Task DeleteClaimAsync(string extensionId, string clientRequestId, CancellationToken cancellationToken = default) => inner.DeleteClaimAsync(extensionId, clientRequestId, cancellationToken);
}

internal sealed class TimedSettingsStore(ISettingsStore inner) : ISettingsStore
{
    public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) => StartupTrace.TimeAsync("settings.Load", () => inner.LoadAsync(cancellationToken));

    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) => StartupTrace.TimeAsync("settings.Save", () => inner.SaveAsync(settings, cancellationToken));
}

internal sealed class TimedCheckpointStore(IDownloadCheckpointStore inner) : IDownloadCheckpointStore
{
    public Task<DownloadCheckpoint?> LoadAsync(Guid jobId, CancellationToken cancellationToken = default) => StartupTrace.TimeAsync("checkpoint.Load", () => inner.LoadAsync(jobId, cancellationToken));

    public Task SaveAsync(DownloadCheckpoint checkpoint, CancellationToken cancellationToken = default) => StartupTrace.TimeAsync("checkpoint.Save", () => inner.SaveAsync(checkpoint, cancellationToken));

    public Task DeleteAsync(Guid jobId, CancellationToken cancellationToken = default) => StartupTrace.TimeAsync("checkpoint.Delete", () => inner.DeleteAsync(jobId, cancellationToken));
}
