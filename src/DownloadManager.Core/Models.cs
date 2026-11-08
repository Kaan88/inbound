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
    /// <summary>The bytes all arrived and are not the file that was asked for. Retrying unattended only repeats that.</summary>
    VerificationFailed,
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
    Remote,
    /// <summary>
    /// UI.B14: CLI add-batch. Headless like <see cref="Cli"/> (no progress window per job), and unlike a single CLI
    /// add, a running main window opens no completion window per job either: a long batch would flood windows.
    /// </summary>
    CliBatch,
    /// <summary>UI.B14: CLI import of a list. Headless and without per-job completion windows, like <see cref="CliBatch"/>.</summary>
    CliImport,
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
    DateTimeOffset OccurredAt,
    int? HttpStatus = null)
{
    public static JobFailure None { get; } = new(FailureKind.None, string.Empty, false, false, DateTimeOffset.MinValue);
}

/// <summary>Which file on which provider a job downloads. Holds no link and no secret.</summary>
public sealed record RemoteSource(
    string Provider,
    string Kind,
    string ItemId,
    string FileId,
    string? RelativePath = null,
    string? RemoteJobId = null)
{
    /// <summary>The job's durable identity, stored in <see cref="DownloadJob.Url"/>. It is not an address anything can fetch.</summary>
    public string ToReferenceUrl()
        => $"remote://{Provider}/{Kind}/{Uri.EscapeDataString(ItemId)}/{Uri.EscapeDataString(FileId)}";
}

public sealed class DownloadJob
{
    public bool IsQueued { get; set; } = true;
    public bool QueueProcessing { get; set; }
    public int QueueRetryCount { get; set; }
    public DateTimeOffset? QueueRetryAt { get; set; }

    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Url { get; set; } = string.Empty;
    public string? FinalUrl { get; set; }
    public string? PageUrl { get; set; }
    public string? Referrer { get; set; }
    public string? UserAgent { get; set; }

    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// Nobody gave a name and the server wasn't asked or didn't answer before the job was made, so
    /// <see cref="FileName"/> came from the address. The transfer's own first answer may still name the file.
    /// </summary>
    public bool NameFromAddress { get; set; }
    public string? CategoryId { get; set; }
    public bool CategoryIsAutomatic { get; set; } = true;
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

    /// <summary>
    /// This completed download's file was deleted on purpose, after a verified extraction read it. Saved before
    /// the delete, so a restart that finds the file gone knows why and leaves the job completed.
    /// </summary>
    public bool FileRemovedAfterExtraction { get; set; }

    public string? ETag { get; set; }
    public string? LastModified { get; set; }
    public string? ContentType { get; set; }

    public int RequestedConnections { get; set; } = 1;
    public int ActiveConnections { get; set; }
    public double? BytesPerSecond { get; set; }
    private long _speedLimitBytesPerSecond;
    public long? SpeedLimitBytesPerSecond
    {
        get { var value = Volatile.Read(ref _speedLimitBytesPerSecond); return value > 0 ? value : null; }
        set => Volatile.Write(ref _speedLimitBytesPerSecond, value.GetValueOrDefault());
    }
    public bool? ResumeSupported { get; set; }
    public bool? ShowCompletionDialog { get; set; }
    public JobCompletionOptions CompletionOptions { get; set; } = new();
    public bool CompletionActionsStarted { get; set; }
    public TimeSpan? EstimatedRemaining { get; set; }

    public DownloadSource Source { get; set; } = DownloadSource.Cli;
    public string? ClientRequestId { get; set; }
    public string? OwnerExtensionId { get; set; }
    public int QueueId { get; set; }
    public string? QueueName { get; set; }
    public int Priority { get; set; }
    /// <summary>The package whose Cancel stopped this job. A job cancelled on its own has none.</summary>
    public Guid? CancelledByPackageId { get; set; }
    public string? Description { get; set; }
    public string? ExpectedSha256 { get; set; }
    public string? ExpectedMd5 { get; set; }
    /// <summary>The size a provider promised. A response of any other size is not this file.</summary>
    public long? ExpectedTotalBytes { get; set; }
    public RemoteSource? RemoteSource { get; set; }

    /// <summary>
    /// A real address too sensitive for <see cref="Url"/>, which then holds a masked copy. Plaintext
    /// in memory, like <see cref="RequestHeaders"/>; the job store writes it only as ciphertext. It
    /// stays as ciphertext here when it could not be decrypted at load.
    /// </summary>
    public string? ProtectedUrl { get; set; }

    /// <summary>The address this run should fetch. Memory only, and never copied to another field.</summary>
    [JsonIgnore]
    public string? ResolvedUrl { get; set; }

    /// <summary>
    /// The job's owner deletes the checkpoint itself once the job is saved as completed, because
    /// until then the checkpoint is the only record of where the finished file went.
    /// </summary>
    [JsonIgnore]
    public bool KeepCheckpointAfterFinalize { get; set; }

    /// <summary>
    /// The host this running job waits on for a free connection while it holds none (UI.B7). Runtime only: a
    /// restarted job waits again or doesn't, so nothing about it is worth saving.
    /// </summary>
    [JsonIgnore]
    public string? WaitingForHost { get; set; }

    /// <summary>The user removed this job. A restart finishes that instead of bringing the job back.</summary>
    public bool RemovalPending { get; set; }

    /// <summary>The address requests go to. Credentials stay bound to <see cref="Url"/>'s origin.</summary>
    [JsonIgnore]
    public string EffectiveUrl =>
        !string.IsNullOrWhiteSpace(ResolvedUrl) ? ResolvedUrl
        : !string.IsNullOrWhiteSpace(FinalUrl) ? FinalUrl
        : Url;

    public StartMode StartMode { get; set; } = StartMode.StartNow;
    public ConflictPolicy ConflictPolicy { get; set; } = ConflictPolicy.Rename;
    public bool DeleteFileOnCancel { get; set; } = true;
    public bool AutoResumeAfterRestart { get; set; } = true;

    public JobFailure? Failure { get; set; }
    public Dictionary<string, string> RequestHeaders { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> RequestHeadersProtected { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool IsTerminal => State is DownloadState.Completed or DownloadState.Cancelled ||
                             (State is DownloadState.Failed && IsPermanentContentFailure);

    [JsonIgnore]
    private bool IsPermanentContentFailure =>
        Failure is { Kind: FailureKind.DrmProtected or FailureKind.LiveStream };

    [JsonIgnore]
    public bool IsActive => State is DownloadState.Connecting or DownloadState.Downloading or DownloadState.Verifying or DownloadState.RetryWaiting;

    [JsonIgnore]
    public bool CanPause => QueueRetryAt is not null || State is DownloadState.Queued or DownloadState.Connecting or DownloadState.Downloading or DownloadState.RetryWaiting;

    [JsonIgnore]
    public bool CanResume => State is DownloadState.Paused or DownloadState.Cancelled ||
                             (State is DownloadState.Failed && !IsPermanentContentFailure);

    [JsonIgnore]
    public double? ProgressFraction => TotalBytes is > 0 ? Math.Clamp((double)ReceivedBytes / TotalBytes.Value, 0, 1) : null;

    public void Touch() => UpdatedAt = DateTimeOffset.UtcNow;

    public DownloadJob Clone()
    {
        var clone = (DownloadJob)MemberwiseClone();
        clone.CompletionOptions = CompletionOptions.Clone();
        clone.RequestHeaders = new Dictionary<string, string>(RequestHeaders, StringComparer.OrdinalIgnoreCase);
        clone.RequestHeadersProtected = new Dictionary<string, string>(RequestHeadersProtected, StringComparer.OrdinalIgnoreCase);
        return clone;
    }
}

public sealed class JobCompletionOptions
{
    /// <summary>Null inherits the global program; empty disables it for this job.</summary>
    public string? ProgramPath { get; set; }
    public bool OpenFile { get; set; }
    public string SystemAction { get; set; } = "none";
    public bool ForceSystemAction { get; set; }
    public JobCompletionOptions Clone() => (JobCompletionOptions)MemberwiseClone();
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

}

public sealed record EngineOptions
{
    public int MaxAttempts { get; init; } = 5;
    public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromSeconds(1);
    public double RetryBackoffFactor { get; init; } = 2.0;
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>The longest a server's Retry-After can make a retry wait.</summary>
    public TimeSpan MaxRetryAfterDelay { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromSeconds(90);
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public int BufferSize { get; init; } = 128 * 1024;
    public int MaxRedirects { get; init; } = 10;
    public bool AllowLocalNetwork { get; init; } = true;
    public int MaxConnectionsPerHost { get; init; } = 8;
    public string AcceptEncoding { get; init; } = "identity";
    public long MinimumFreeSpaceReserveBytes { get; init; } = 1024 * 1024;

    /// <summary>
    /// How often progress is made durable. Each save first flushes the part file to disk, so that
    /// a checkpoint never describes bytes a power cut could still lose; that flush is why this is
    /// not shorter. A kill costs at most this much re-downloading.
    /// </summary>
    public TimeSpan CheckpointInterval { get; init; } = TimeSpan.FromSeconds(5);

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

    /// <summary>
    /// Written just before the part file is renamed to this path. With the part gone and this
    /// file at the recorded size, a restart knows the rename happened and the file is the job's.
    /// </summary>
    public string? FinalizingTo { get; set; }

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
    public int? RetryLimit { get; set; }
    public List<Guid> JobOrder { get; set; } = new();
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
    public string? LastRunError { get; set; }
    public bool StartOnHostStartup { get; set; }
    public bool TimedStartEnabled { get; set; } = true;
    public int? RepeatEveryMinutes { get; set; }
    public DateTimeOffset? LastRunAt { get; set; }

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Daily";
    public int QueueId { get; set; }
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

public sealed class ConnectionProfile
{
    public string Pattern { get; set; } = "*";
    public int MaxConnections { get; set; } = 8;
}

/// <summary>A saved download folder with the user's name for it, such as "Movies".</summary>
public sealed class FavoriteFolder
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
}

public sealed class DownloadCategory
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? DestinationDirectory { get; set; }
    public List<string> Extensions { get; set; } = new();
}

public sealed class ArchivedDownloadRecord
{
    public Guid? SourceJobId { get; set; }
    public Guid Id { get; set; }
    public long ReceivedBytes { get; set; }
    public DownloadState State { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class DownloadGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    /// <summary>Automatic package identity (multipart archive or browser batch); null for manual packages.</summary>
    public string? Key { get; set; }
    /// <summary>The name was chosen by the Host and follows the members' file names until the user renames it.</summary>
    public bool AutoNamed { get; set; }
    public List<Guid> JobIds { get; set; } = new();

    /// <summary>
    /// The last completion announced, as the newest member's finish time. A later finish is a new
    /// completion, so this also tells a restart which ones were already announced.
    /// </summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>
    /// False on packages saved before completions were announced. Those may have finished long
    /// ago, so the first start records them instead of announcing them.
    /// </summary>
    public bool CompletionTracked { get; set; }
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
    /// <summary>New downloads go to their category's folder (Options, Categories); off, they go to the download folder.</summary>
    public bool UseCategoryFolders { get; set; } = true;
    /// <summary>PERF.5: closing the main window ends its process instead of hiding it; the tray starts it again. On by default
    /// (owner, 2026-11-08); settings saved with a value keep it, and settings that never stored one get on.</summary>
    public bool CloseWindowCompletely { get; set; } = true;
    public int MaxConnectionsPerHost { get; set; } = 8;
    public List<ConnectionProfile> ConnectionProfiles { get; set; } = new();
    public long? GlobalSpeedLimitBytesPerSecond { get; set; }
    public ProxySettings Proxy { get; set; } = new();
    public List<SiteLogin> SiteLogins { get; set; } = new();
    public bool LaunchAtStartup { get; set; }
    public bool ClipboardMonitoring { get; set; }
    public bool BrowserIntegrationEnabled { get; set; } = true;
    public bool NotifyOnCompletion { get; set; } = true;
    public bool PlayCompletionSound { get; set; } = true;
    public Dictionary<string, string> EventSoundFiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool ShowCompletionDialog { get; set; } = true;
    public bool AutoResumeUnfinishedOnStartup { get; set; } = true;
    public bool AllowLocalNetwork { get; set; } = true;
    public bool PreventSleepWhileDownloading { get; set; } = true;

    /// <summary>Explicit ffmpeg.exe path; empty means PATH/auto-detect.</summary>
    public string? FfmpegPath { get; set; }

    /// <summary>Program run once after each successful download; file path is one argument.</summary>
    public string? PostDownloadCommand { get; set; }
    public string? UpdateManifestUrl { get; set; } = ReleaseUpdates.ManifestUrl;
    public string? UpdatePublicKey { get; set; } = ReleaseUpdates.PublicKey;
    public string UpdateChannel { get; set; } = "stable";
    public bool AutoCheckUpdates { get; set; } = true;
    public int UpdateCheckIntervalHours { get; set; } = 24;
    public string? SkippedUpdateVersion { get; set; }
    public DateTimeOffset? LastUpdateCheck { get; set; }

    /// <summary>Enables schedule exit/shutdown completion actions. Off by default.</summary>
    public bool AllowSystemCompletionActions { get; set; }

    public string Theme { get; set; } = "System";
    public string? LastDestinationDirectory { get; set; }

    /// <summary>Folders recently downloaded to, newest first, at most <see cref="DownloadFolders.MaxRecent"/>.</summary>
    public List<string> RecentDestinationDirectories { get; set; } = new();

    /// <summary>Named folders the user saved, in their order, offered first wherever a folder is chosen.</summary>
    public List<FavoriteFolder> FavoriteFolders { get; set; } = new();
    public List<string> RecentUrls { get; set; } = new();
    public Dictionary<string, string> CategoryFolders { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Default download folder the built-in category folders were last derived from (Compressed,
    /// Documents, Music, Programs, Video subfolders). Null until first seeded. When the default folder
    /// changes, category folders still at their derived value follow it; customized or cleared ones
    /// are left alone.
    /// </summary>
    public string? CategoryFoldersRoot { get; set; }
    public List<DownloadCategory> CustomCategories { get; set; } = new();
    public List<DownloadGroup> DownloadGroups { get; set; } = new();
    public List<ArchivedDownloadRecord> ArchivedDownloads { get; set; } = new();

    public AppSettings WithDownloadGroups(List<DownloadGroup> groups)
    {
        var copy = (AppSettings)MemberwiseClone();
        copy.DownloadGroups = groups;
        return copy;
    }

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
