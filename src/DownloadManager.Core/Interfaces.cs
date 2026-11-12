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
    /// <summary>
    /// Null means there is no usable checkpoint, and a transfer that hears this starts over, deleting what it has. So a
    /// file that exists but can't be read right now throws <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> instead,
    /// and one that can't be used at all (damaged, or saved by a newer build) throws <see cref="CheckpointUnusableException"/>.
    /// </summary>
    Task<DownloadCheckpoint?> LoadAsync(Guid jobId, CancellationToken cancellationToken = default);
    Task SaveAsync(DownloadCheckpoint checkpoint, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid jobId, CancellationToken cancellationToken = default);
}

/// <summary>
/// A checkpoint file exists but can never be used by this build: its content is damaged, or a newer build wrote it.
/// Trying again won't help, so it is not worth a retry; reading it as none would truncate the part, so callers keep
/// the part and stop instead. An <see cref="IOException"/>, so callers that only know the store contract stay safe.
/// The message is written for the user.
/// </summary>
public class CheckpointUnusableException(string message) : IOException(message);

public interface ISettingsStore
{
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}

public sealed record FileMetadata(string FileName, long? TotalBytes, string? ContentType, string FinalUrl, string? ETag = null, string? LastModified = null);

public interface IFileMetadataReader
{
    Task<FileMetadata> ReadMetadataAsync(DownloadJob request, CancellationToken cancellationToken);
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

    /// <summary>Updates the speed cap shared by every transfer together; null or zero means none.</summary>
    void UpdateSharedSpeedLimit(long? bytesPerSecond)
    {
    }

    /// <summary>
    /// Raised with a job's id when it starts or stops waiting for a connection to its host while holding none.
    /// Read <see cref="WaitingForHost"/> for the current answer; notices can arrive late.
    /// </summary>
    event Action<Guid>? HostWaitChanged
    {
        add { }
        remove { }
    }

    /// <summary>The host name a running job waits on with no connection to it, or null.</summary>
    string? WaitingForHost(Guid jobId) => null;

    /// <summary>
    /// The job's <see cref="DownloadJob.RequestedConnections"/> changed while it runs. A segmented transfer adds
    /// workers for a higher count and lets workers above a lower one stop after their current range.
    /// </summary>
    void ConnectionsChanged(Guid jobId)
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
    /// <summary>Why a post-download program can't be used, in the words a refusal shows.</summary>
    public const string ProgramRefused = "The post-download program must be the full path of an .exe file.";

    /// <summary>
    /// A program the Host may start after a download: a full path, and on Windows an <c>.exe</c>. A <c>.bat</c> or
    /// <c>.cmd</c> would run through <c>cmd.exe</c>, which reads <c>&amp;</c>, <c>%</c> and <c>^</c> in the file's name
    /// (a server or a torrent chooses that name) as commands. Linux programs have no extension, so there any full path
    /// passes and starting it says whether it can run.
    /// </summary>
    public static bool IsAllowedProgram(string? program)
        => !string.IsNullOrWhiteSpace(program) && Path.IsPathFullyQualified(program) &&
           (!OperatingSystem.IsWindows() || string.Equals(Path.GetExtension(program), ".exe", StringComparison.OrdinalIgnoreCase));

    /// <exception cref="ArgumentException">The program is not one <see cref="IsAllowedProgram"/> allows.</exception>
    public static System.Diagnostics.ProcessStartInfo Create(string command, string filePath)
    {
        // Checked here too: a command saved before this rule existed is refused when it would run.
        if (!IsAllowedProgram(command)) throw new ArgumentException(ProgramRefused, nameof(command));
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

    /// <summary>The volume's own mount on Linux, not the path's first part (<see cref="Volumes.RootOf"/>).</summary>
    public long? GetAvailableFreeSpace(string directory) => Volumes.AvailableBytes(directory);
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
