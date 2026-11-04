namespace DownloadManager.Contracts;

public sealed class FailureDto
{
    public string Kind { get; set; } = "none";
    public string Message { get; set; } = string.Empty;
    public bool IsRetryable { get; set; }
    public bool IsResumable { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

public sealed class JobDto
{
    public Guid Id { get; set; }
    public string Url { get; set; } = string.Empty;
    public string? FinalUrl { get; set; }
    public string? PageUrl { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string DestinationDirectory { get; set; } = string.Empty;
    public string FinalFilePath { get; set; } = string.Empty;
    public string PartFilePath { get; set; } = string.Empty;
    public long? TotalBytes { get; set; }
    public long ReceivedBytes { get; set; }
    public double? Progress { get; set; }
    public string State { get; set; } = "queued";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? ContentType { get; set; }
    public string? ETag { get; set; }
    public string? LastModified { get; set; }
    public int RequestedConnections { get; set; }
    public int ActiveConnections { get; set; }
    public double? BytesPerSecond { get; set; }
    public long? SpeedLimitBytesPerSecond { get; set; }
    public TimeSpan? EstimatedRemaining { get; set; }
    public string Source { get; set; } = "cli";
    public string? ClientRequestId { get; set; }
    public string? OwnerExtensionId { get; set; }
    public int QueueId { get; set; }
    public int Priority { get; set; }
    public string? Description { get; set; }
    public string? ExpectedSha256 { get; set; }
    public string? QueueName { get; set; }
    public string StartMode { get; set; } = "startNow";
    public string ConflictPolicy { get; set; } = "rename";
    public bool CanPause { get; set; }
    public bool CanResume { get; set; }
    public bool IsTerminal { get; set; }
    public FailureDto? Failure { get; set; }
}

public sealed class QueueDto
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

public sealed class ConnectionProfileDto
{
    public string Pattern { get; set; } = "*";
    public int MaxConnections { get; set; } = 8;
}

public sealed class ScheduleDto
{
    public Guid? GrabberProjectId { get; set; }
    public Guid Id { get; set; }
    public string Name { get; set; } = "Daily";
    public int QueueId { get; set; }
    public bool Enabled { get; set; } = true;
    public string Kind { get; set; } = "daily";
    public DateOnly? StartDate { get; set; }
    public string Days { get; set; } = "Mon,Tue,Wed,Thu,Fri,Sat,Sun";
    public string StartTime { get; set; } = "09:00";
    public string? StopTime { get; set; }
    public DateOnly? LastStartDate { get; set; }
    public DateOnly? LastStopDate { get; set; }
    public bool OpenFileWhenDone { get; set; }
    public bool ExitWhenDone { get; set; }
    public bool ShutdownWhenDone { get; set; }
    public string ShutdownMode { get; set; } = "shutdown";
    public bool ForceShutdown { get; set; }
}

public sealed class ProxyDto
{
    public string Mode { get; set; } = "none";
    public string? Address { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public bool HasPassword { get; set; }
}

public sealed class SiteLoginDto
{
    public Guid Id { get; set; }
    public string Site { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;

    /// <summary>Plaintext is accepted on updates and never returned on reads.</summary>
    public string? Password { get; set; }

    public bool HasPassword { get; set; }
}

public sealed class SettingsDto
{
    public string DefaultDownloadDirectory { get; set; } = string.Empty;
    public int MaxConcurrentDownloads { get; set; } = 3;
    public int DefaultQueueId { get; set; }
    public List<QueueDto> Queues { get; set; } = new();
    public List<ScheduleDto> Schedules { get; set; } = new();
    public int DefaultConnections { get; set; } = 8;
    public int MaxConnectionsPerHost { get; set; } = 8;
    public List<ConnectionProfileDto> ConnectionProfiles { get; set; } = new();
    public long? GlobalSpeedLimitBytesPerSecond { get; set; }
    public ProxyDto Proxy { get; set; } = new();
    public List<SiteLoginDto> SiteLogins { get; set; } = new();
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
    public string? FfmpegPath { get; set; }
    public string? PostDownloadCommand { get; set; }
    public string? UpdateManifestUrl { get; set; }
    public string? UpdatePublicKey { get; set; }
    public string UpdateChannel { get; set; } = "stable";
    public bool AutoCheckUpdates { get; set; }
    public int UpdateCheckIntervalHours { get; set; } = 24;
    public string? SkippedUpdateVersion { get; set; }
    public DateTimeOffset? LastUpdateCheck { get; set; }
    public bool AllowSystemCompletionActions { get; set; }
    public string Theme { get; set; } = "System";
    public string? LastDestinationDirectory { get; set; }
    public List<string> RecentUrls { get; set; } = new();
    public Dictionary<string, string> CategoryFolders { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> ExcludedFileExtensions { get; set; } = new();
    public List<string> ExcludedHosts { get; set; } = new();
    public List<string> InterceptedFileExtensions { get; set; } = new();
}

public sealed class HostStatusDto
{
    public string HostVersion { get; set; } = string.Empty;
    public int ProtocolVersion { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public bool TrayAvailable { get; set; }
    public bool NativeHostRegistered { get; set; }
    public int ActiveDownloads { get; set; }
    public int QueuedDownloads { get; set; }
    public int TotalJobs { get; set; }
    public string StateDirectory { get; set; } = string.Empty;
    public string BrowserIntegration { get; set; } = "unknown";
    public string? LastError { get; set; }
    public List<string> OrphanPartFiles { get; set; } = new();
}

public sealed class HostHelloDto
{
    public string HostVersion { get; set; } = string.Empty;
    public int ProtocolVersion { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public List<string> Capabilities { get; set; } = new();
}

public sealed class IncompatibleDto
{
    public int MinVersion { get; set; }
    public int MaxVersion { get; set; }
    public string Message { get; set; } = string.Empty;
}

public sealed class SnapshotDto
{
    public List<JobDto> Jobs { get; set; } = new();
    public SettingsDto Settings { get; set; } = new();
    public HostStatusDto Status { get; set; } = new();
}

public sealed class AddJobRequest
{
    public string Url { get; set; } = string.Empty;
    public string? FinalUrl { get; set; }
    public string? DestinationDirectory { get; set; }
    public string? FileName { get; set; }
    public string StartMode { get; set; } = "startNow";
    public string Source { get; set; } = "cli";
    public string? ClientRequestId { get; set; }
    public string? OwnerExtensionId { get; set; }
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? Referrer { get; set; }
    public string? UserAgent { get; set; }
    public string? PageUrl { get; set; }
    public int? Connections { get; set; }
    public long? SpeedLimitBytesPerSecond { get; set; }
    public string? Description { get; set; }
    public string ConflictPolicy { get; set; } = "rename";
    public string? ExpectedSha256 { get; set; }
    public bool? AutoResumeAfterRestart { get; set; }
    public bool? RememberDestination { get; set; }
    public int QueueId { get; set; }
}

public sealed class AddJobResponse
{
    public bool Accepted { get; set; }
    public bool Duplicate { get; set; }
    public JobDto? Job { get; set; }
    public string? Error { get; set; }
    public string? ErrorCode { get; set; }
}

public sealed class JobActionRequest
{
    public Guid JobId { get; set; }
    public bool DeleteFile { get; set; }
}

public sealed class JobActionResponse
{
    public bool Ok { get; set; }
    public JobDto? Job { get; set; }
    public string? Error { get; set; }
}

public sealed class CleanOrphansRequest
{
    /// <summary>When true orphans are deleted; otherwise moved to a per-folder quarantine.</summary>
    public bool Delete { get; set; }
}

public sealed class CleanOrphansResponse
{
    public bool Ok { get; set; }
    public List<string> Processed { get; set; } = new();
    public List<string> Failed { get; set; } = new();
    public string? Error { get; set; }
}

public sealed class QueueActionRequest
{
    public int QueueId { get; set; }
    public string Action { get; set; } = "start";
    public int MaxConcurrentDownloads { get; set; }
}

public sealed class JobQueueRequest
{
    public Guid JobId { get; set; }
    public int QueueId { get; set; }
}

public sealed class JobRefreshUrlRequest
{
    public Guid JobId { get; set; }
    public string Url { get; set; } = string.Empty;
}

public sealed class JobUpdateRequest
{
    public Guid JobId { get; set; }
    public string? Description { get; set; }

    /// <summary>New absolute path for a completed download (Move/Rename).</summary>
    public string? NewFilePath { get; set; }
}

public sealed class SiteGrabberRequest
{
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
    /// <summary>Honor each host's robots.txt for pages and files (default on).</summary>
    public bool RespectRobotsTxt { get; set; } = true;
    public string StartMode { get; set; } = "now";
    public int QueueId { get; set; }
    public int Connections { get; set; }
}

public sealed class SiteGrabberResult
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public int PagesCrawled { get; set; }
    public int FilesQueued { get; set; }
    public int FilesSkipped { get; set; }
    public int RobotsBlocked { get; set; }
    public List<string> Errors { get; set; } = new();
}

public sealed class GrabberProjectDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public SiteGrabberRequest Request { get; set; } = new();
    public string Status { get; set; } = "pending";
    public int PagesCrawled { get; set; }
    public int FilesQueued { get; set; }
    public int FilesSkipped { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? LastRunAt { get; set; }
}

public sealed class GrabberProjectCreateRequest
{
    public string Name { get; set; } = string.Empty;
    public SiteGrabberRequest Request { get; set; } = new();
}

public sealed class GrabberProjectActionRequest
{
    public Guid Id { get; set; }
}

public sealed class GrabberProjectResult
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public GrabberProjectDto? Project { get; set; }
    public SiteGrabberResult? Run { get; set; }
}

public sealed class GrabberProjectsResult
{
    public List<GrabberProjectDto> Projects { get; set; } = new();
}

public sealed class SegmentDto
{
    public long Start { get; set; }
    public long End { get; set; }
    public long Completed { get; set; }
    public bool IsComplete { get; set; }
    public double Fraction { get; set; }
}

public sealed class JobEventDto
{
    public JobDto Job { get; set; } = new();
    public string EventType { get; set; } = "updated";
}

public sealed class SettingsUpdateRequest
{
    public SettingsDto Settings { get; set; } = new();
}

public sealed class SettingsResult
{
    public bool Ok { get; set; }
    public SettingsDto Settings { get; set; } = new();
    public string? Error { get; set; }
}

public sealed class HostActionRequest
{
    public string Action { get; set; } = string.Empty;
}

public sealed class HostActionResult
{
    public bool Ok { get; set; }
    public string? Message { get; set; }
}

public sealed class HandoffRequest
{
    public string RequestId { get; set; } = string.Empty;
    public string ExtensionId { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string? FinalUrl { get; set; }
    public string? PageUrl { get; set; }
    public string? Referrer { get; set; }
    public string? UserAgent { get; set; }
    public string? SuggestedFileName { get; set; }
    public string? MimeType { get; set; }
    public long? TotalBytes { get; set; }
    public string StartMode { get; set; } = "startNow";
    public bool IsAutoIntercept { get; set; }
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? DestinationDirectory { get; set; }
}

public sealed class HandoffAck
{
    public bool Accepted { get; set; }
    public bool Duplicate { get; set; }
    public Guid JobId { get; set; }
    public JobDto? Job { get; set; }
    public string? Error { get; set; }
    public string? ErrorCode { get; set; }
}
