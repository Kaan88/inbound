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
    // MuxingFailed, LiveStream and DrmProtected came from stream downloading, cut in 0.7.2. They stay so the job files
    // of older versions still load.
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
    /// <summary>
    /// Something this app no longer downloads: a streaming playlist or an FTP address (both cut in 0.7.2). Final: trying again only
    /// repeats it, so neither Resume All nor a queue's retries take it.
    /// </summary>
    Unsupported,
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

    /// <summary>
    /// The user paused it (its own Pause, or Stop All). Starting its queue, by hand or by a schedule, leaves it paused;
    /// a pause from a queue stop, a quota, a move or an exit doesn't set it, so those resume with the queue (U-WL-2).
    /// Cleared when the job runs again.
    /// </summary>
    public bool PausedByUser { get; set; }

    /// <summary>
    /// The Host paused it because it was exiting (tray Exit, an update, logoff) while it ran. At the next start it goes
    /// back to the queue when "resume unfinished downloads at start" is on, as one a kill interrupted does (audit 2 M11).
    /// Cleared when the job runs again.
    /// </summary>
    public bool PausedByExit { get; set; }
    public int QueueRetryCount { get; set; }
    public DateTimeOffset? QueueRetryAt { get; set; }

    /// <summary>
    /// 2 from 0.7.2. A job file is still written as 1 unless it holds a value 0.7.1 can't read (see
    /// <see cref="SchemaVersionToWrite"/>): 0.7.1 sets an unreadable job file aside but keeps one with a newer schema.
    /// </summary>
    public const int CurrentSchemaVersion = 2;

    /// <summary>The schema 0.7.1 and older read.</summary>
    public const int SchemaVersionOlderBuildsRead = 1;

    /// <summary>
    /// The schema this job is saved with: <see cref="CurrentSchemaVersion"/> only while it holds a value older builds can't
    /// read (<see cref="FailureKind.Unsupported"/>, 0.7.2), so after a downgrade that job stays on disk for this build
    /// and every other job still loads in the older one.
    /// </summary>
    [JsonIgnore]
    public int SchemaVersionToWrite => Failure?.Kind == FailureKind.Unsupported ? CurrentSchemaVersion : SchemaVersionOlderBuildsRead;

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

    /// <summary>
    /// UI.B19: the folder a provider file was given (its package's folder). The app makes the file's own path folders
    /// below it, and records the ones it created in <see cref="MadeFolders"/>. This folder itself is never one of them.
    /// Null for every other download, which goes straight into the folder it was given.
    /// </summary>
    public string? MadeFoldersBase { get; set; }

    /// <summary>
    /// UI.B19: the folders the app created for this download, recorded when it created them, so deleting the download
    /// can take them away again once they are empty. A folder that already existed is never listed. An older record
    /// has none, and then no folder is removed.
    /// </summary>
    public List<string> MadeFolders { get; set; } = new();

    /// <summary>
    /// The final file this download wrote: where its finished file landed. Deleting an unfinished download deletes
    /// the file at <see cref="FinalFilePath"/> only when it is this one, so a file it never wrote, such as the one an
    /// Overwrite download would have replaced, stays.
    /// </summary>
    public string? WrittenFinalPath { get; set; }

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

    /// <summary>
    /// The Host found this completed download's file missing from its folder at start (moved or deleted outside the
    /// app). The job stays completed with a note instead of becoming a failure the queue would download again (audit 2
    /// H2). Cleared when the file is there again at a later start, or when the job runs again.
    /// </summary>
    public bool FileMissing { get; set; }

    /// <summary>
    /// T10: when it completed, this download looked like a torrent (its content type or name) and its file read as a
    /// .torrent file within <c>TorrentMetadata.MaxFileBytes</c>. Cleared when the job runs again.
    /// </summary>
    public bool TorrentFileReady { get; set; }

    public string? ETag { get; set; }
    public string? LastModified { get; set; }
    public string? ContentType { get; set; }

    /// <summary>
    /// The content type the browser hand-off carried when the download was added. Unlike <see cref="ContentType"/>,
    /// which the transfer overwrites with the server's answer, nothing changes it later, so whether a download is an
    /// HLS playlist or a DASH manifest (<see cref="StreamManifestHint"/>) is decided by the same facts on every run.
    /// </summary>
    public string? HandoffContentType { get; set; }

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

    /// <summary>
    /// D8: a browser on another device added it (the web UI, or a hand-off through the web bridge), so it opens no
    /// progress or completion window on this PC's screen: nobody there asked for it. A flag beside <see cref="Source"/>
    /// rather than a source of its own, so a job file written now still loads in an older build, which ignores a
    /// property it doesn't know but would set aside a job whose source it can't read.
    /// </summary>
    public bool AddedFromAnotherDevice { get; set; }

    /// <summary>
    /// EXT.R12: the browser extension that sent it shows its progress on its toolbar icon and asked the app to open no
    /// window for it (no progress window as it starts, no completion window), so the browser keeps the focus. A flag of
    /// its own, like <see cref="AddedFromAnotherDevice"/>: an older build ignores it.
    /// </summary>
    public bool QuietWindows { get; set; }

    /// <summary>
    /// A browser on another device added this download or last changed its address (D8), so it never reaches this PC
    /// or its local network (<see cref="NetworkAddresses"/>), whatever <c>AllowLocalNetwork</c> says. Every request,
    /// redirect and connection is checked, not just the first address.
    /// </summary>
    public bool PublicNetworkOnly { get; set; }

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
    /// <summary>Set on a torrent job (T1); null on every other job.</summary>
    public TorrentJobInfo? Torrent { get; set; }

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

    /// <summary>
    /// When this job last started running in this Host's lifetime (review 5:T3). Unlike <see cref="StartedAt"/>, which
    /// is the first start ever, it is set again on every start, so a resumed or restored job counts from now. Memory only.
    /// </summary>
    [JsonIgnore]
    public DateTimeOffset? RunStartedAt { get; set; }

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
        Failure is { Kind: FailureKind.DrmProtected or FailureKind.LiveStream or FailureKind.Unsupported };

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
        clone.Torrent = Torrent?.Clone();
        clone.MadeFolders = new List<string>(MadeFolders);
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

    /// <summary>
    /// Host, host:port, or URL/path prefix, for example example.com/private. Without a scheme it is sent over
    /// https only, to that host; *.example.com takes in subdomains; http:// allows http (SEC.L1).
    /// </summary>
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

    /// <summary>
    /// The schedule that started the queue's current run, whose when-done actions its end takes (audit 2 M10). Saved,
    /// so a run that goes on after a restart keeps them (review 2 L4). The Host's own; never from a client.
    /// </summary>
    public Guid? ArmedScheduleId { get; set; }
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

    /// <summary>
    /// Local date on which the schedule was made, turned on or had its trigger changed after that day's stop time
    /// (review 2 L2). That day's stop is only recorded: the schedule did not start what runs then. The Host's own.
    /// </summary>
    public DateOnly? SetAfterStopDate { get; set; }

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

    /// <summary>
    /// The Host's settings revision, in memory only: it grows when a queue or schedule is added or removed, and a
    /// client sends back the one its copy came with, so a stale copy can't remove what it never saw (audit 4 H1).
    /// </summary>
    [JsonIgnore]
    public long Revision { get; set; }
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

    /// <summary>Options › Torrents: download torrents on this PC (T1). Off by default; the kill switch wins.</summary>
    public bool TorrentsEnabled { get; set; }
    /// <summary>With a provider account: where magnets and .torrent files go (<see cref="TorrentRoutes"/>).</summary>
    public string TorrentRoute { get; set; } = TorrentRoutes.Ask;
    /// <summary>Ask which files to download before a multi-file torrent starts.</summary>
    public bool TorrentChooseFiles { get; set; } = true;
    /// <summary>
    /// T14: torrents go to a folder for their category (<see cref="TorrentFolderRules"/>); off, each goes straight to the
    /// torrent folder. Apart from <see cref="UseCategoryFolders"/>, which is for other downloads only. Settings from
    /// before T14 get the value <see cref="UseCategoryFolders"/> had, since that one decided for torrents then.
    /// </summary>
    public bool UseTorrentCategoryFolders { get; set; } = true;
    /// <summary>
    /// T14: the torrent folder, which holds the torrents' category folders (Video, Music...). Null: torrents use the
    /// same folders as other downloads, which is what settings from before T14 get, so an upgrade moves nothing. A new
    /// install gets Torrents under the download folder (<see cref="NewInstall"/>), which follows that folder when it moves.
    /// </summary>
    public string? TorrentCategoryFoldersRoot { get; set; }
    /// <summary>Open magnet links and .torrent files with this app (a per-user registration the window makes).</summary>
    public bool TorrentHandler { get; set; }
    /// <summary>T2: keep sharing a torrent after it finished downloading, until <see cref="TorrentSeedRatio"/> or
    /// <see cref="TorrentSeedHours"/> is reached, whichever comes first.</summary>
    public bool TorrentSeedAfterDownload { get; set; } = true;
    /// <summary>Stop seeding once this much was uploaded per byte downloaded; 0 is no ratio limit.</summary>
    public double TorrentSeedRatio { get; set; } = 1.0;
    /// <summary>Stop seeding after this many hours; 0 is no time limit. Never both 0.</summary>
    public double TorrentSeedHours { get; set; } = 48;
    /// <summary>Upload cap for torrents in bytes/s; 0 is none of its own. Torrent uploads also share
    /// <see cref="GlobalSpeedLimitBytesPerSecond"/> with every download (one budget, T2), so this only ever lowers them.</summary>
    public long TorrentUploadLimit { get; set; }
    /// <summary>
    /// T2: the port the torrent engine listens on, chosen once at random from the dynamic range when torrents are first
    /// turned on (or typed in Options), so a firewall or router rule made for it keeps working. 0: not chosen yet.
    /// </summary>
    public int TorrentListenPort { get; set; }
    /// <summary>T2: ask the router to forward the torrent port (UPnP and NAT-PMP). Off by default everywhere.</summary>
    public bool TorrentUpnp { get; set; }
    /// <summary>T3: find peers through the DHT (no tracker needed). On by default, as before T3.</summary>
    public bool TorrentDht { get; set; } = true;
    /// <summary>T3: peer exchange (PEX): peers tell each other of more peers.</summary>
    public bool TorrentPex { get; set; } = true;
    /// <summary>T3: local peer discovery (LSD) on this network. Off by default, as before T3.</summary>
    public bool TorrentLsd { get; set; }
    /// <summary>T3: <c>prefer</c>, <c>require</c> or <c>off</c> (<see cref="TorrentEncryptionModes"/>).</summary>
    public string TorrentEncryption { get; set; } = TorrentEncryptionModes.Prefer;
    /// <summary>T3: how many finished torrents seed at once; 0 is all of them.</summary>
    public int TorrentSeedsAtOnce { get; set; }
    /// <summary>T3: connections per torrent; 0 is no limit of its own (the total still applies).</summary>
    public int TorrentConnectionsPerTorrent { get; set; }
    /// <summary>T3: connections of every torrent together; 0 is the torrent engine's own default.</summary>
    public int TorrentMaxConnections { get; set; }
    /// <summary>T3: peers and trackers go through the proxy of Options › Proxy (HTTP or SOCKS5 typed in there).</summary>
    public bool TorrentUseProxy { get; set; }

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

    /// <summary>The settings of a new install: the defaults, and torrents in their own folder (T14).</summary>
    public static AppSettings NewInstall()
    {
        var settings = new AppSettings();
        settings.TorrentCategoryFoldersRoot = TorrentFolderRules.DefaultRoot(settings.DefaultDownloadDirectory);
        return settings;
    }

    public static string GetDefaultDownloadDirectory()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var downloads = Path.Combine(profile, "Downloads");
        return Directory.Exists(downloads) ? downloads : profile;
    }
}
