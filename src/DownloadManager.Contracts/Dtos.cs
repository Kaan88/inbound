namespace DownloadManager.Contracts;

public sealed class FailureDto
{
    public string Kind { get; set; } = "none";
    public string Message { get; set; } = string.Empty;
    public bool IsRetryable { get; set; }
    public bool IsResumable { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public int? HttpStatus { get; set; }
}

public sealed class JobDto
{
    public bool IsQueued { get; set; } = true;
    public bool QueueProcessing { get; set; }
    /// <summary>The user paused it; starting its queue leaves it paused (U-WL-2).</summary>
    public bool PausedByUser { get; set; }
    public int QueueRetryCount { get; set; }
    public DateTimeOffset? QueueRetryAt { get; set; }

    public Guid Id { get; set; }
    public string Url { get; set; } = string.Empty;
    /// <summary>
    /// The real address held a secret, such as a provider's account key, and the Host keeps it to itself.
    /// <see cref="Url"/> is then a masked copy that can't download anything, and clients show its site alone.
    /// </summary>
    public bool AddressHidden { get; set; }
    public string? FinalUrl { get; set; }
    public string? PageUrl { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string? CategoryId { get; set; }
    public bool CategoryIsAutomatic { get; set; } = true;
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
    /// <summary>A completed download whose file was deleted on purpose after a verified extraction. Not a failure, and there is no file to open.</summary>
    public bool FileRemovedAfterExtraction { get; set; }
    /// <summary>Something to say about a job beside its state, in words for the user. Null when there is nothing.</summary>
    public string? StatusNote { get; set; }
    public string? ContentType { get; set; }
    public string? ETag { get; set; }
    public string? LastModified { get; set; }
    public int RequestedConnections { get; set; }
    public int ActiveConnections { get; set; }
    /// <summary>
    /// The host name (never the address) this running download is waiting on for a free connection while it
    /// has none. Null otherwise. Added in protocol 1 without a bump: older clients ignore it.
    /// </summary>
    public string? WaitingForHost { get; set; }
    public double? BytesPerSecond { get; set; }
    public long? SpeedLimitBytesPerSecond { get; set; }
    public bool? ResumeSupported { get; set; }
    public bool? ShowCompletionDialog { get; set; }
    public JobCompletionOptionsDto CompletionOptions { get; set; } = new();
    public bool CanChangeSpeedLimit { get; set; }
    public bool CanCancel { get; set; }
    public TimeSpan? EstimatedRemaining { get; set; }
    public string Source { get; set; } = "cli";
    /// <summary>D8: a browser on another device added it; it opens no window on the Host's screen.</summary>
    public bool AddedFromAnotherDevice { get; set; }
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
    /// <summary>True when a provider supplies this file; its Url is then a reference, not an address.</summary>
    public bool IsRemote { get; set; }
    public string? RemoteProvider { get; set; }
    /// <summary>The package whose Cancel stopped this file; null when it was cancelled on its own.</summary>
    public Guid? CancelledByPackageId { get; set; }
}

public sealed class FavoriteFolderDto
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
}

/// <summary>Asks the Host how much room there is at these folders, measured where the files would be written.</summary>
public sealed class FolderSpaceRequest
{
    public List<string> Paths { get; set; } = new();
}

public sealed class FolderSpaceDto
{
    public string Path { get; set; } = string.Empty;
    /// <summary>Null when it couldn't be measured, for example a drive that didn't answer in time.</summary>
    public long? FreeBytes { get; set; }
    public long? TotalBytes { get; set; }
    /// <summary>
    /// True when the Host runs in a container and this folder isn't on a mounted volume, so files written there are
    /// lost when the container is rebuilt.
    /// </summary>
    public bool InsideContainer { get; set; }
}

public sealed class FolderSpaceResult
{
    public bool Ok { get; set; }
    public List<FolderSpaceDto> Folders { get; set; } = new();
    public string? Error { get; set; }
}

public sealed class CategoryDefinitionDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? DestinationDirectory { get; set; }
    public List<string> Extensions { get; set; } = new();
}

public sealed class QueueDto
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

public sealed class ConnectionProfileDto
{
    public string Pattern { get; set; } = "*";
    public int MaxConnections { get; set; } = 8;
}

public sealed class ScheduleDto
{
    public string? LastRunError { get; set; }
    public bool StartOnHostStartup { get; set; }
    public bool TimedStartEnabled { get; set; } = true;
    public int? RepeatEveryMinutes { get; set; }
    public DateTimeOffset? LastRunAt { get; set; }

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
    /// <summary>Remove the saved proxy password instead of keeping it when <see cref="Password"/> is empty.</summary>
    public bool ClearPassword { get; set; }
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
    /// <summary>New downloads go to their category's folder (Options, Categories); off, they go to the download folder.</summary>
    public bool UseCategoryFolders { get; set; } = true;
    /// <summary>PERF.5: closing the main window ends its process instead of hiding it; the tray starts it again. On by default
    /// (owner, 2026-11-08); settings saved with a value keep it, and settings that never stored one get on.</summary>
    public bool CloseWindowCompletely { get; set; } = true;
    public int MaxConnectionsPerHost { get; set; } = 8;
    public List<ConnectionProfileDto> ConnectionProfiles { get; set; } = new();
    public long? GlobalSpeedLimitBytesPerSecond { get; set; }
    public ProxyDto Proxy { get; set; } = new();
    public List<SiteLoginDto> SiteLogins { get; set; } = new();
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
    /// <summary>Host-owned: folders recently downloaded to, newest first. A client's copy is ignored on save.</summary>
    public List<string> RecentDestinationDirectories { get; set; } = new();
    public List<FavoriteFolderDto> FavoriteFolders { get; set; } = new();
    public List<string> RecentUrls { get; set; } = new();
    public Dictionary<string, string> CategoryFolders { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<CategoryDefinitionDto> CustomCategories { get; set; } = new();
    /// <summary>Host-owned; use group.action to mutate.</summary>
    public List<DownloadGroupDto> DownloadGroups { get; set; } = new();
    /// <summary>Explicit deletion intent for unused custom categories; snapshots always leave this empty.</summary>
    public List<string> DeleteCategoryIds { get; set; } = new();
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

    /// <summary>
    /// What kind of Host answers: <see cref="HostKinds.Windows"/> (the desktop Host) or <see cref="HostKinds.Server"/>
    /// (<c>DownloadManager.Server</c>). Null from a Host older than this field.
    /// </summary>
    public string? HostKind { get; set; }
}

/// <summary>The values of <see cref="HostHelloDto.HostKind"/>.</summary>
public static class HostKinds
{
    public const string Windows = "windows";
    public const string Server = "server";
}

/// <summary>The values of <see cref="ClientHelloDto.Role"/>.</summary>
public static class ClientRoles
{
    /// <summary>The user at this PC: the desktop window, the CLI, the browser's native host. The default.</summary>
    public const string Desktop = "desktop";

    /// <summary>
    /// A browser on another device, through the web bridge (D8). It gets the rights a network client gets on the server,
    /// limited to the app's own folders, plus the rules that matter only on a desktop.
    /// </summary>
    public const string Web = "web";
}

/// <summary>
/// PERF.7: what a client on the hello pipe asks for. Both default to true, which is what the main pipe gives everyone.
/// A one-command client (the CLI) wants neither; the browser bridge wants events but no snapshot.
/// </summary>
public sealed class ClientHelloDto
{
    /// <summary>Send <c>jobs.snapshot</c> after <c>server.hello</c>. A client that asks <c>jobs.query</c> when it needs the list sets false.</summary>
    public bool Snapshot { get; set; } = true;

    /// <summary>Send this connection the broadcasts (<c>job.event</c>, <c>host.exiting</c> and the rest).</summary>
    public bool Events { get; set; } = true;

    /// <summary>
    /// The rights this connection asks for (<see cref="ClientRoles"/>). Null or <c>desktop</c> is the user at this PC,
    /// as before. <c>web</c> asks for fewer rights; a Host that doesn't offer that role closes the connection. A client
    /// can only ever ask for less than it would get without the field.
    /// </summary>
    public string? Role { get; set; }
}

public sealed class IncompatibleDto
{
    public int MinVersion { get; set; }
    public int MaxVersion { get; set; }
    public string Message { get; set; } = string.Empty;
}

public sealed class SnapshotDto
{
    public UpdateProgressDto? UpdateProgress { get; set; }
    public List<JobDto> Jobs { get; set; } = new();
    public SettingsDto Settings { get; set; } = new();
    public HostStatusDto Status { get; set; } = new();
}

/// <summary>
/// UI.R24: total download speed, one figure per <see cref="IntervalSeconds"/> from <see cref="Start"/>, oldest first,
/// in bytes per second. It starts no earlier than the Host did; the last figure is the second under way.
/// </summary>
public sealed class SpeedHistoryDto
{
    public DateTimeOffset Start { get; set; }
    public int IntervalSeconds { get; set; } = 1;
    public List<long> BytesPerSecond { get; set; } = new();
}

public sealed class FolderPickRequest
{
    public string? InitialPath { get; set; }
}

public sealed class FolderPickResult
{
    public bool Ok { get; set; }
    public bool Cancelled { get; set; }
    public string? Path { get; set; }
    public string? Error { get; set; }
}

public sealed class FileMetadataRequest
{
    public string Url { get; set; } = string.Empty;
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? UserAgent { get; set; }
    public string? Referrer { get; set; }
}

public sealed class FileMetadataResult
{
    public bool Ok { get; set; }
    public string? FileName { get; set; }
    public long? TotalBytes { get; set; }
    public string? ContentType { get; set; }
    public string? FinalUrl { get; set; }
    public string? Error { get; set; }

    /// <summary>
    /// The Host would refuse to add this address for this caller (<see cref="Error"/> says why), so a dialog can stop
    /// before Start rather than after it (U-WR-2). False when only the preview failed.
    /// </summary>
    public bool AddRefused { get; set; }
}

public sealed class AddJobRequest
{
    public string Url { get; set; } = string.Empty;
    public string? FinalUrl { get; set; }
    public string? DestinationDirectory { get; set; }
    public string? FileName { get; set; }
    public string? CategoryId { get; set; }
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
    /// <summary>Jobs added with the same key are shown as one package (for example one pasted batch).</summary>
    public string? PackageKey { get; set; }
    public string? PackageName { get; set; }
    /// <summary>A <see cref="JobPriorities"/> value; null is normal.</summary>
    public int? Priority { get; set; }
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
    public bool OnlyIfUnfinished { get; set; }
    /// <summary>Remove only: several downloads in one request (a package). <see cref="JobId"/> is ignored when set.</summary>
    public List<Guid>? JobIds { get; set; }
}

public sealed class JobActionResponse
{
    public bool Ok { get; set; }
    public JobDto? Job { get; set; }
    public string? Error { get; set; }
    /// <summary>A <see cref="JobActionErrorCodes"/> value when a client acts on the kind of refusal; null otherwise.</summary>
    public string? ErrorCode { get; set; }
}

/// <summary>What kind of refusal a <see cref="JobActionResponse"/> is, for a client that acts on it rather than on its words.</summary>
public static class JobActionErrorCodes
{
    /// <summary>The folder is outside the folders the caller may use (a server's roots, or the app's for another device).</summary>
    public const string OutsideRoots = "outside_roots";
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
    public int? MoveContentsToQueueId { get; set; }
    public List<Guid>? JobOrder { get; set; }
    public int QueueId { get; set; }
    public string Action { get; set; } = "start";
    public int MaxConcurrentDownloads { get; set; }
    public string? Name { get; set; }
    public bool? QuotaEnabled { get; set; }
    public long? QuotaBytes { get; set; }
    public int? QuotaWindowHours { get; set; }
}

public sealed class JobQueueRequest
{
    public bool Detach { get; set; }
    public Guid JobId { get; set; }
    public int QueueId { get; set; }
}

public sealed class JobRefreshUrlRequest
{
    public Guid JobId { get; set; }
    public string Url { get; set; } = string.Empty;
    /// <summary>The user chose a direct request for an address nobody could classify. It doesn't excuse a known hoster link or folder link.</summary>
    public bool ExplicitDirect { get; set; }
}

public sealed class JobUpdateRequest
{
    public JobCompletionOptionsDto? CompletionOptions { get; set; }
    /// <summary>Null leaves unchanged; empty selects automatic classification.</summary>
    public string? CategoryId { get; set; }

    public Guid JobId { get; set; }
    public string? Description { get; set; }
    /// <summary>Null leaves unchanged; zero disables the cap; positive values set it.</summary>
    public long? SpeedLimitBytesPerSecond { get; set; }
    public bool? ShowCompletionDialog { get; set; }

    /// <summary>New absolute path for a completed download (Move/Rename).</summary>
    public string? NewFilePath { get; set; }

    /// <summary>Null leaves unchanged; otherwise a <see cref="JobPriorities"/> value.</summary>
    public int? Priority { get; set; }
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

/// <summary>
/// The folders a download can start in, for a client that may not see the settings (the browser
/// extension): the default folder and the favorites, and nothing else.
/// </summary>
public sealed class DownloadFoldersResult
{
    public bool Ok { get; set; }
    public string DefaultFolder { get; set; } = string.Empty;
    public List<FavoriteFolderDto> Favorites { get; set; } = new();
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
    public string? PackageKey { get; set; }
    public string? PackageName { get; set; }
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

public sealed class DownloadGroupDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Key { get; set; }
    public List<Guid> JobIds { get; set; } = new();
}

public sealed class GroupActionRequest
{
    public Guid GroupId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? Name { get; set; }
    public List<Guid>? JobIds { get; set; }
    /// <summary>Action "queue": the queue every file moves to.</summary>
    public int? QueueId { get; set; }
    /// <summary>Action "priority": a <see cref="JobPriorities"/> value.</summary>
    public int? Priority { get; set; }
}

public sealed class GroupActionResult
{
    public bool Ok { get; set; }
    public Guid GroupId { get; set; }
    public string? Error { get; set; }
    public Dictionary<Guid, string> FailedJobs { get; set; } = new();
    public List<DownloadGroupDto> Groups { get; set; } = new();
    /// <summary>Files the action changed.</summary>
    public int Changed { get; set; }
    /// <summary>Resume only: cancelled files it left alone, see <see cref="PackageRules.ResumeTargets"/>.</summary>
    public int LeftCancelled { get; set; }
}

public sealed class JobCompletionOptionsDto
{
    public string? ProgramPath { get; set; }
    public bool OpenFile { get; set; }
    public string SystemAction { get; set; } = "none";
    public bool ForceSystemAction { get; set; }
}

public sealed class ConnectionTelemetryDto
{
    public Guid Id { get; set; }
    public string Host { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public long? RangeStart { get; set; }
    public long? RangeEnd { get; set; }
    public long ReceivedBytes { get; set; }
    public double BytesPerSecond { get; set; }
    public int? StatusCode { get; set; }
    public string? HttpVersion { get; set; }
    public DateTimeOffset StartedAt { get; set; }
}

public sealed class DownloadStatisticsDto
{
    public DateTimeOffset CapturedAt { get; set; }
    public DateTimeOffset? FirstRecordedAt { get; set; }
    public long RecordedBytes { get; set; }
    public long CompletedBytes { get; set; }
    public long CompletedTodayBytes { get; set; }
    public int CompletedCount { get; set; }
    public int CompletedTodayCount { get; set; }
    public int FailedCount { get; set; }
    public int CancelledCount { get; set; }
    public int RetainedCount { get; set; }
    public int ArchivedCount { get; set; }
    public int ActiveCount { get; set; }
    public int QueuedCount { get; set; }
    public int PausedCount { get; set; }
    public double BytesPerSecond { get; set; }
}
