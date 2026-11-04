using System.Text.Json.Serialization;

namespace DownloadManager.Core;

public enum DownloadState
{
    Queued,
    Connecting,
    Downloading,
    RetryWaiting,
    Verifying,
    Paused,
    Completed,
    Failed,
    Cancelled,
}

public enum FailureKind
{
    None,
    Network,
    Timeout,
    Dns,
    Tls,
    Http,
    Unauthorized,
    Forbidden,
    ExpiredUrl,
    NotFound,
    RangeUnsupported,
    RemoteChanged,
    InvalidContent,
    MuxingFailed,
    LiveStream,
    DrmProtected,
    DiskFull,
    Permission,
    DestinationUnavailable,
    Cancelled,
    Unknown,
}

public enum DownloadSource
{
    Cli,
    Api,
    ChromeContextMenu,
    ChromeAuto,
    Clipboard,
    Batch,
    ClipboardMonitor,
    Import,
    Grabber,
}

public enum StartMode
{
    StartNow,
    Queue,
    Later,
}

public enum ConflictPolicy
{
    Rename,
    Overwrite,
    Fail,
}

public sealed record JobFailure(
    FailureKind Kind,
    string Message,
    bool IsRetryable,
    bool IsResumable,
    DateTimeOffset OccurredAt)
{
    public static JobFailure None { get; } = new(FailureKind.None, string.Empty, false, false, DateTimeOffset.MinValue);
}

public sealed class DownloadJob
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Url { get; set; } = string.Empty;
    public string? FinalUrl { get; set; }
    public string? PageUrl { get; set; }
    public string? Referrer { get; set; }
    public string? UserAgent { get; set; }

    public string FileName { get; set; } = string.Empty;
    public string DestinationDirectory { get; set; } = string.Empty;
    public string FinalFilePath { get; set; } = string.Empty;
    public string PartFilePath { get; set; } = string.Empty;

    public long? TotalBytes { get; set; }
    public long ReceivedBytes { get; set; }
    public DownloadState State { get; set; } = DownloadState.Queued;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    public string? ETag { get; set; }
    public string? LastModified { get; set; }
    public string? ContentType { get; set; }

    public int RequestedConnections { get; set; } = 1;
    public int ActiveConnections { get; set; }
    public double? BytesPerSecond { get; set; }
    public long? SpeedLimitBytesPerSecond { get; set; }
    public TimeSpan? EstimatedRemaining { get; set; }

    public DownloadSource Source { get; set; } = DownloadSource.Cli;
    public string? ClientRequestId { get; set; }
    public string? OwnerExtensionId { get; set; }
    public int QueueId { get; set; }
    public string? QueueName { get; set; }
    public int Priority { get; set; }
    public string? Description { get; set; }
    public string? ExpectedSha256 { get; set; }

    public StartMode StartMode { get; set; } = StartMode.StartNow;
    public ConflictPolicy ConflictPolicy { get; set; } = ConflictPolicy.Rename;
    public bool DeleteFileOnCancel { get; set; } = true;
    public bool AutoResumeAfterRestart { get; set; } = true;

    public JobFailure? Failure { get; set; }
    public Dictionary<string, string> RequestHeaders { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool IsTerminal => State is DownloadState.Completed or DownloadState.Cancelled ||
                             (State is DownloadState.Failed && IsPermanentContentFailure);

    [JsonIgnore]
    private bool IsPermanentContentFailure =>
        Failure is { Kind: FailureKind.DrmProtected or FailureKind.LiveStream };

    [JsonIgnore]
    public bool IsActive => State is DownloadState.Connecting or DownloadState.Downloading or DownloadState.Verifying or DownloadState.RetryWaiting;

    [JsonIgnore]
    public bool CanPause => State is DownloadState.Queued or DownloadState.Connecting or DownloadState.Downloading or DownloadState.RetryWaiting;

    [JsonIgnore]
    public bool CanResume => State is DownloadState.Paused or DownloadState.Cancelled ||
                             (State is DownloadState.Failed && !IsPermanentContentFailure);

    [JsonIgnore]
    public double? ProgressFraction => TotalBytes is > 0 ? Math.Clamp((double)ReceivedBytes / TotalBytes.Value, 0, 1) : null;

    public void Touch() => UpdatedAt = DateTimeOffset.UtcNow;

    public DownloadJob Clone()
    {
        var clone = (DownloadJob)MemberwiseClone();
        clone.RequestHeaders = new Dictionary<string, string>(RequestHeaders, StringComparer.OrdinalIgnoreCase);
        return clone;
    }
}

public sealed record TransferProgress(
    long ReceivedBytes,
    long? TotalBytes,
    double BytesPerSecond,
    TimeSpan? EstimatedRemaining,
    int ActiveConnections,
    string Phase);

public sealed record DownloadResult(
    bool Success,
    DownloadState FinalState,
    JobFailure? Failure,
    long BytesWritten,
    long? TotalBytes,
    string? FinalFilePath,
    string? ETag,
    string? LastModified,
    string? ContentType)
{
    public static DownloadResult Ok(long bytes, long? total, string finalPath, string? etag, string? lastModified, string? contentType)
        => new(true, DownloadState.Completed, null, bytes, total, finalPath, etag, lastModified, contentType);

    public static DownloadResult Fail(DownloadState state, JobFailure failure, long bytes = 0, long? total = null)
        => new(false, state, failure, bytes, total, null, null, null, null);
}

public sealed class SiteLogin
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Host, host:port, or URL/path prefix, for example example.com/private.</summary>
    public string Site { get; set; } = string.Empty;

    public string Username { get; set; } = string.Empty;

    /// <summary>DPAPI-protected password blob. Never store the plaintext here.</summary>
    public string PasswordProtected { get; set; } = string.Empty;
}

public sealed record SiteLoginCredential(string Site, string Username, string Password);

public sealed class ProxySettings
{
    /// <summary>none, system, or manual.</summary>
    public string Mode { get; set; } = "none";

    /// <summary>host:port or scheme://host:port for manual mode.</summary>
    public string? Address { get; set; }

    public string? Username { get; set; }

    /// <summary>
    /// In-memory plaintext only; never serialized. Persisted as
    /// <see cref="PasswordProtected"/> (DPAPI).
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? Password { get; set; }

    /// <summary>DPAPI-protected password blob written by the settings store.</summary>
    public string? PasswordProtected { get; set; }

    /// <summary>
    /// Read-only migration hook for settings files written before DPAPI
    /// protection: a legacy plaintext <c>password</c> value is adopted on load
    /// and is never written back.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("password")]
    [System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyPlaintextPassword
    {
        get => null;
        set
        {
            if (!string.IsNullOrEmpty(value))
            {
                Password ??= value;
            }
        }
    }
}

public sealed record EngineOptions
{
    public int MaxAttempts { get; init; } = 5;
    public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromSeconds(1);
    public double RetryBackoffFactor { get; init; } = 2.0;
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromSeconds(90);
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public int BufferSize { get; init; } = 128 * 1024;
    public int MaxRedirects { get; init; } = 10;
    public bool AllowLocalNetwork { get; init; } = true;
    public int MaxConnectionsPerHost { get; init; } = 8;
    public string AcceptEncoding { get; init; } = "identity";
    public long MinimumFreeSpaceReserveBytes { get; init; } = 1024 * 1024;

    /// <summary>Mutable proxy state; updated at runtime when settings change.</summary>
    public ProxySettings Proxy { get; init; } = new();

    /// <summary>Explicit ffmpeg.exe path; empty means PATH/auto-detect.</summary>
    public string? FfmpegPath { get; init; }
}

public sealed class DownloadCheckpoint
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public Guid JobId { get; set; }
    public string Url { get; set; } = string.Empty;
    public string? FinalUrl { get; set; }
    public string? ETag { get; set; }
    public string? LastModified { get; set; }
    public long? TotalBytes { get; set; }
    public string? ContentType { get; set; }
    public List<SegmentCheckpoint> Segments { get; set; } = new();
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public long CompletedBytes => Segments.Sum(s => s.Completed);
}

public sealed class MediaCheckpoint
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public Guid JobId { get; set; }
    public string Track { get; set; } = "media";
    public string Signature { get; set; } = string.Empty;
    public int CompletedSegments { get; set; }
    public long CompletedBytes { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class SegmentCheckpoint
{
    public long Start { get; set; }
    public long End { get; set; }
    public long Completed { get; set; }
    public bool IsComplete => Completed >= End - Start + 1;
}

public sealed class DownloadQueue
{
    public int Id { get; set; }
    public string Name { get; set; } = "Main";
    public int MaxConcurrentDownloads { get; set; } = 3;
    public bool IsRunning { get; set; } = true;
    public int Order { get; set; }

    public bool QuotaEnabled { get; set; }
    public long QuotaBytes { get; set; } = 200L * 1024 * 1024;
    public int QuotaWindowHours { get; set; } = 5;
    public DateTimeOffset? QuotaWindowStart { get; set; }
    public long QuotaBytesUsed { get; set; }
}

public sealed class ScheduleRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Daily";
    public int QueueId { get; set; }
    public Guid? GrabberProjectId { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>daily or oneTime.</summary>
    public string Kind { get; set; } = "daily";

    /// <summary>Local date for a oneTime schedule.</summary>
    public DateOnly? StartDate { get; set; }

    public string Days { get; set; } = "Mon,Tue,Wed,Thu,Fri,Sat,Sun";
    public string StartTime { get; set; } = "09:00";
    public string? StopTime { get; set; }

    /// <summary>Local date the queue was last started by this schedule.</summary>
    public DateOnly? LastStartDate { get; set; }

    /// <summary>Local date the queue was last stopped by this schedule.</summary>
    public DateOnly? LastStopDate { get; set; }

    public bool OpenFileWhenDone { get; set; }
    public bool ExitWhenDone { get; set; }
    public bool ShutdownWhenDone { get; set; }
    public string ShutdownMode { get; set; } = "shutdown";
    public bool ForceShutdown { get; set; }
}

public sealed class GrabberProject
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Grabber project";
    public string StartUrl { get; set; } = string.Empty;
    public string? DestinationDirectory { get; set; }
    public int MaxDepth { get; set; } = 2;
    public int MaxPages { get; set; } = 50;
    public bool SameHostOnly { get; set; } = true;
    public List<string> Extensions { get; set; } = new();
    public List<string> IncludePatterns { get; set; } = new();
    public List<string> ExcludePatterns { get; set; } = new();
    public bool SavePages { get; set; } = true;
    public bool RewriteLinks { get; set; } = true;
    public bool RespectRobotsTxt { get; set; } = true;
    public string StartMode { get; set; } = "now";
    public int QueueId { get; set; }
    public int Connections { get; set; }

    /// <summary>pending, running, completed, failed, interrupted.</summary>
    public string Status { get; set; } = "pending";
    public int PagesCrawled { get; set; }
    public int FilesQueued { get; set; }
    public int FilesSkipped { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastRunAt { get; set; }

    public GrabberProject Clone()
    {
        var clone = (GrabberProject)MemberwiseClone();
        clone.Extensions = new List<string>(Extensions);
        clone.IncludePatterns = new List<string>(IncludePatterns);
        clone.ExcludePatterns = new List<string>(ExcludePatterns);
        return clone;
    }
}

public sealed class ConnectionProfile
{
    public string Pattern { get; set; } = "*";
    public int MaxConnections { get; set; } = 8;
}

public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string DefaultDownloadDirectory { get; set; } = GetDefaultDownloadDirectory();
    public int MaxConcurrentDownloads { get; set; } = 3;
    public int DefaultQueueId { get; set; }
    public List<DownloadQueue> Queues { get; set; } =
    [
        new DownloadQueue { Id = 0, Name = "Main", MaxConcurrentDownloads = 3, IsRunning = true, Order = 0 },
    ];
    public List<ScheduleRule> Schedules { get; set; } = new();
    public int DefaultConnections { get; set; } = 8;
    public int MaxConnectionsPerHost { get; set; } = 8;
    public List<ConnectionProfile> ConnectionProfiles { get; set; } = new();
    public long? GlobalSpeedLimitBytesPerSecond { get; set; }
    public ProxySettings Proxy { get; set; } = new();
    public List<SiteLogin> SiteLogins { get; set; } = new();
    public bool LaunchAtStartup { get; set; }
    public bool ClipboardMonitoring { get; set; }
    public bool BrowserIntegrationEnabled { get; set; } = true;
    public bool BrowserAutoIntercept { get; set; } = true;
    public bool NotifyOnCompletion { get; set; } = true;
    public bool PlayCompletionSound { get; set; } = true;
    public bool ShowCompletionDialog { get; set; } = true;
    public bool AutoResumeUnfinishedOnStartup { get; set; } = true;
    public bool AllowLocalNetwork { get; set; } = true;
    public bool PreventSleepWhileDownloading { get; set; } = true;

    /// <summary>Explicit ffmpeg.exe path; empty means PATH/auto-detect.</summary>
    public string? FfmpegPath { get; set; }

    /// <summary>Program run once after each successful download; file path is one argument.</summary>
    public string? PostDownloadCommand { get; set; }
    public string? UpdateManifestUrl { get; set; }
    public string? UpdatePublicKey { get; set; }
    public string UpdateChannel { get; set; } = "stable";
    public bool AutoCheckUpdates { get; set; }
    public int UpdateCheckIntervalHours { get; set; } = 24;
    public string? SkippedUpdateVersion { get; set; }
    public DateTimeOffset? LastUpdateCheck { get; set; }

    /// <summary>Enables schedule exit/shutdown completion actions. Off by default.</summary>
    public bool AllowSystemCompletionActions { get; set; }

    public string Theme { get; set; } = "System";
    public string? LastDestinationDirectory { get; set; }
    public List<string> RecentUrls { get; set; } = new();
    public Dictionary<string, string> CategoryFolders { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> ExcludedFileExtensions { get; set; } = new();
    public List<string> ExcludedHosts { get; set; } = new();
    public List<string> InterceptedFileExtensions { get; set; } = new()
    {
        "3gp", "7z", "aac", "ace", "aif", "apk", "arj", "asf", "avi", "bin", "bz2",
        "exe", "gz", "gzip", "img", "iso", "lzh", "m4a", "m4v", "mkv", "mov", "mp3",
        "mpa", "mpe", "mpeg", "mpg", "msi", "msu", "ogg", "ogv", "plj", "pps", "ppt",
        "qt", "ra", "rar", "rm", "rmvb", "sea", "sit", "sitx", "tar", "wav", "wma",
        "wmv", "z", "zip"
    };

    public static string GetDefaultDownloadDirectory()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var downloads = Path.Combine(profile, "Downloads");
        return Directory.Exists(downloads) ? downloads : profile;
    }
}
