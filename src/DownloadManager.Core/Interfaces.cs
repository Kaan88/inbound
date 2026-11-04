namespace DownloadManager.Core;

public interface IJobStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DownloadJob>> LoadAllAsync(CancellationToken cancellationToken = default);
    Task<DownloadJob?> LoadAsync(Guid jobId, CancellationToken cancellationToken = default);
    Task SaveAsync(DownloadJob job, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the job id already claimed for this browser request, or null.
    /// Implementations must be safe against concurrent callers in one host.
    /// </summary>
    Task<Guid?> FindClaimedRequestAsync(string extensionId, string clientRequestId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Durably records a request claim. Returns false if another claim already exists.
    /// </summary>
    Task<bool> TryClaimRequestAsync(string extensionId, string clientRequestId, Guid jobId, CancellationToken cancellationToken = default);

    Task DeleteClaimAsync(string extensionId, string clientRequestId, CancellationToken cancellationToken = default);
}

public interface IDownloadCheckpointStore
{
    Task<DownloadCheckpoint?> LoadAsync(Guid jobId, CancellationToken cancellationToken = default);
    Task SaveAsync(DownloadCheckpoint checkpoint, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid jobId, CancellationToken cancellationToken = default);
}

public interface ISettingsStore
{
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}

public interface IDownloadEngine
{
    Task<DownloadResult> ExecuteAsync(
        DownloadJob job,
        DownloadCheckpoint? checkpoint,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken);

    /// <summary>Applies the current proxy configuration to subsequent requests.</summary>
    void UpdateProxy(ProxySettings settings);

    /// <summary>Replaces the in-memory site credentials used for Basic auth.</summary>
    void UpdateSiteLogins(IReadOnlyList<SiteLoginCredential> logins);
}

public interface ICompletionActionExecutor
{
    Task OpenFileAsync(string path, CancellationToken cancellationToken);

    Task RequestExitAsync(CancellationToken cancellationToken);

    Task RequestShutdownAsync(string mode, bool force, CancellationToken cancellationToken);
}

public sealed class NullCompletionActionExecutor : ICompletionActionExecutor
{
    public static NullCompletionActionExecutor Instance { get; } = new();

    public Task OpenFileAsync(string path, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task RequestExitAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task RequestShutdownAsync(string mode, bool force, CancellationToken cancellationToken)
        => Task.CompletedTask;
}

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public static SystemClock Instance { get; } = new();
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
