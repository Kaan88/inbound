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

public interface IHostConnectionLimiter
{
    Task<IDisposable> AcquireAsync(string host, CancellationToken cancellationToken);
}

/// <summary>
/// Caps concurrent connections per host across every job sharing the limiter.
/// Keys include the active limit so a settings change starts a fresh gate for
/// new acquisitions instead of resizing a semaphore still in use.
/// </summary>
public sealed class HostConnectionLimiter : IHostConnectionLimiter
{
    private readonly object _sync = new();
    private readonly Dictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);
    private int _limit;
    private int _active;
    private int _peak;

    public HostConnectionLimiter(int limit)
    {
        _limit = Math.Max(1, limit);
    }

    public int Limit
    {
        get
        {
            lock (_sync)
            {
                return _limit;
            }
        }
    }

    public void UpdateLimit(int limit)
    {
        lock (_sync)
        {
            _limit = Math.Max(1, limit);
        }
    }

    public async Task<IDisposable> AcquireAsync(string host, CancellationToken cancellationToken)
    {
        SemaphoreSlim gate;
        lock (_sync)
        {
            var key = $"{host}|{_limit}";
            if (!_gates.TryGetValue(key, out gate!))
            {
                gate = new SemaphoreSlim(_limit, _limit);
                _gates[key] = gate;
            }
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            _active++;
            _peak = Math.Max(_peak, _active);
        }

        return new GateReleaser(gate, this);
    }

    public int Peak
    {
        get
        {
            lock (_sync)
            {
                return _peak;
            }
        }
    }

    private sealed class GateReleaser(SemaphoreSlim gate, HostConnectionLimiter owner) : IDisposable
    {
        public void Dispose()
        {
            lock (owner._sync)
            {
                owner._active--;
            }

            gate.Release();
        }
    }
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

    /// <summary>Updates the ffmpeg.exe path used for media muxing.</summary>
    void UpdateFfmpegPath(string? path);

    /// <summary>Updates the cross-job per-host connection cap.</summary>
    void UpdateHostConnectionLimit(int limit)
    {
    }
}

public interface IPostDownloadHook
{
    Task RunAsync(string command, string filePath, CancellationToken cancellationToken);
}

public sealed class NullPostDownloadHook : IPostDownloadHook
{
    public static NullPostDownloadHook Instance { get; } = new();

    public Task RunAsync(string command, string filePath, CancellationToken cancellationToken)
        => Task.CompletedTask;
}

public static class PostDownloadCommandLine
{
    public static System.Diagnostics.ProcessStartInfo Create(string command, string filePath)
    {
        var start = new System.Diagnostics.ProcessStartInfo(command)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // ArgumentList passes the path as one argument without shell parsing.
        start.ArgumentList.Add(filePath);
        return start;
    }
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

public sealed record MediaMuxResult(bool Success, string? Error);

public interface IMediaCheckpointStore
{
    Task<MediaCheckpoint?> LoadAsync(Guid jobId, string track, CancellationToken cancellationToken = default);

    Task SaveAsync(MediaCheckpoint checkpoint, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid jobId, string? track = null, CancellationToken cancellationToken = default);
}

public interface IFreeSpaceProvider
{
    long? GetAvailableFreeSpace(string directory);
}

public sealed class DriveFreeSpaceProvider : IFreeSpaceProvider
{
    public static DriveFreeSpaceProvider Instance { get; } = new();

    public long? GetAvailableFreeSpace(string directory)
    {
        try
        {
            var full = Path.GetFullPath(directory);
            var root = Path.GetPathRoot(full);
            if (string.IsNullOrWhiteSpace(root))
            {
                return null;
            }

            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}

public interface IMediaMuxer
{
    Task<MediaMuxResult> MuxAsync(
        string videoPath,
        string audioPath,
        string outputPath,
        CancellationToken cancellationToken);
}

public interface ISleepPrevention : IDisposable
{
    /// <summary>Holds or releases the system-sleep power request.</summary>
    void SetActive(bool active);
}

public sealed class NullSleepPrevention : ISleepPrevention
{
    public static NullSleepPrevention Instance { get; } = new();

    public void SetActive(bool active)
    {
    }

    public void Dispose()
    {
    }
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
