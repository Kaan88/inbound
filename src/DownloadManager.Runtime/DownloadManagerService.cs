using System.Globalization;
using System.Text.Json;
using DownloadManager.Contracts;
using DownloadManager.Core;
using DownloadManager.Core.Providers;
using DownloadManager.Persistence;

namespace DownloadManager.Runtime;

public sealed class JobChangeEventArgs(JobDto job, string changeType) : EventArgs
{
    public JobDto Job { get; } = job;
    public string ChangeType { get; } = changeType;
}

public sealed record ScheduleAction(
    Guid ScheduleId,
    int QueueId,
    string Action,
    DateOnly Date,
    bool Ok,
    string? Error);

public sealed partial class DownloadManagerService
{
    private readonly StoragePaths _paths;
    private readonly IJobStore _jobStore;
    private readonly IDownloadCheckpointStore _checkpointStore;
    private readonly ISettingsStore _settingsStore;
    private readonly IDownloadEngine _engine;
    private readonly Action<string>? _log;
    private readonly TimeProvider _timeProvider;
    private readonly bool _enableSchedulerTimer;
    private readonly ICompletionActionExecutor _completionActions;
    private readonly IPostDownloadHook _postDownloadHook;
    private readonly ISleepPrevention _sleepPrevention;
    private readonly IMediaCheckpointStore? _mediaCheckpoints;
    private readonly IDownloadEngine? _ftpEngine;
    private readonly IRemoteLinkBroker? _linkBroker;
    private readonly IRemoteSecretMatcher? _linkSecrets;
    private static readonly HttpClient UpdateClient = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly SemaphoreSlim _updateInstallGate = new(1, 1);
    private bool _updateInstallScheduled;
    private bool _updateQuiescing;
    public event EventHandler? UpdateShutdownRequested;
    public event EventHandler<UpdateProgressDto>? UpdateProgressChanged;
    private UpdateProgressDto? _updateProgress;
    public event EventHandler<string>? UpdateDeferred;
    private readonly object _sync = new();
    private readonly List<string> _orphanPartFiles = new();
    private bool _sleepPreventionActive;
    private readonly Dictionary<Guid, JobRuntime> _jobs = new();
    private readonly HashSet<int> _completionFiredQueues = new();
    private readonly SemaphoreSlim _persistGate = new(1, 1);
    private readonly SemaphoreSlim _settingsUpdateGate = new(1, 1);
    private readonly SemaphoreSlim _metadataGate = new(2, 2);
    private readonly SemaphoreSlim _addProbeGate = new(4, 4);
    private AppSettings _settings = new();
    private CancellationTokenSource? _schedulerCts;
    private Task? _schedulerTask;
    private bool _shuttingDown;
    private bool _initialized;

    public DownloadManagerService(
        StoragePaths paths,
        IJobStore jobStore,
        IDownloadCheckpointStore checkpointStore,
        ISettingsStore settingsStore,
        IDownloadEngine engine,
        Action<string>? log = null,
        TimeProvider? timeProvider = null,
        bool enableSchedulerTimer = true,
        ICompletionActionExecutor? completionActions = null,
        ISleepPrevention? sleepPrevention = null,
        IMediaCheckpointStore? mediaCheckpoints = null,
        IDownloadEngine? ftpEngine = null,
        IPostDownloadHook? postDownloadHook = null,
        IRemoteLinkBroker? linkBroker = null,
        IRemoteSecretMatcher? linkSecrets = null)
    {
        _paths = paths;
        _jobStore = jobStore;
        _checkpointStore = checkpointStore;
        _settingsStore = settingsStore;
        _engine = engine;
        _log = log;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _enableSchedulerTimer = enableSchedulerTimer;
        _completionActions = completionActions ?? NullCompletionActionExecutor.Instance;
        _sleepPrevention = sleepPrevention ?? NullSleepPrevention.Instance;
        _mediaCheckpoints = mediaCheckpoints;
        _ftpEngine = ftpEngine;
        _postDownloadHook = postDownloadHook ?? NullPostDownloadHook.Instance;
        _linkBroker = linkBroker;
        _linkSecrets = linkSecrets;
    }

    public event EventHandler<JobChangeEventArgs>? JobChanged;
    public event EventHandler<AppSettings>? SettingsChanged;

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    public bool TrayAvailable { get; set; }
    public Func<bool>? NativeHostRegistered { get; set; }
    public string StateDirectory => _paths.Root;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _jobStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
        _settings = await _settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        _engine.UpdateProxy(_settings.Proxy);
        _engine.UpdateSiteLogins(BuildSiteLogins(_settings));
        _engine.UpdateFfmpegPath(_settings.FfmpegPath);
        _engine.UpdateHostConnectionLimit(_settings.MaxConnectionsPerHost);
        _ftpEngine?.UpdateProxy(_settings.Proxy);
        _ftpEngine?.UpdateSiteLogins(BuildSiteLogins(_settings));
        _ftpEngine?.UpdateFfmpegPath(_settings.FfmpegPath);

        var jobs = await _jobStore.LoadAllAsync(cancellationToken).ConfigureAwait(false);

        foreach (var job in jobs)
        {
            var runtime = new JobRuntime(job);
            _jobs[job.Id] = runtime;
        }

        // Removals a crash interrupted: the user already decided, so these jobs must not run again.
        foreach (var removal in jobs.Where(job => job.RemovalPending).ToList())
        {
            await RemoveCoreAsync(removal.Id, removal.DeleteFileOnCancel, cancellationToken).ConfigureAwait(false);
        }

        foreach (var runtime in _jobs.Values)
        {
            var changed = await ReconcileAsync(runtime, cancellationToken).ConfigureAwait(false);
            // Queue policy is authoritative; a crash between settings and job
            // writes must not leave the denormalized display name stale.
            var queue = _settings.Queues.FirstOrDefault(q => q.Id == runtime.Job.QueueId);
            if (queue is not null && runtime.Job.QueueName != queue.Name)
            {
                runtime.Job.QueueName = runtime.Job.IsQueued ? queue.Name : "None";
                changed = true;
            }
            if (changed)
            {
                await PersistAsync(runtime, cancellationToken).ConfigureAwait(false);
            }
        }

        await ScrubRecentUrlsAsync(cancellationToken).ConfigureAwait(false);
        ScanOrphanPartFiles();
        // A crash can land between a job's final save and its package's, so this may have news.
        await AdoptUntrackedPackagesAsync(cancellationToken).ConfigureAwait(false);
        await AnnouncePackageCompletionsAsync(cancellationToken).ConfigureAwait(false);
        _initialized = true;
        Schedule();
        StartSchedulerLoop();
    }

    public SnapshotDto GetSnapshot()
    {
        lock (_sync)
        {
            return new SnapshotDto
            {
                UpdateProgress = _updateProgress,
                Jobs = _jobs.Values
                    .Select(r => Map(r.Job))
                    .OrderByDescending(j => j.CreatedAt)
                    .ToList(),
                Settings = MapSettings(_settings),
                Status = BuildStatusLocked(),
            };
        }
    }

    public async Task<IReadOnlyList<SegmentDto>> GetSegmentsAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        bool known;
        lock (_sync)
        {
            known = _jobs.ContainsKey(id);
        }

        if (!known)
        {
            return Array.Empty<SegmentDto>();
        }

        DownloadCheckpoint? checkpoint;
        try
        {
            checkpoint = await _checkpointStore.LoadAsync(id, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Only a display, and it can look again.
            return Array.Empty<SegmentDto>();
        }

        if (checkpoint is null)
        {
            return Array.Empty<SegmentDto>();
        }

        return checkpoint.Segments
            .OrderBy(segment => segment.Start)
            .Select(segment =>
            {
                var length = Math.Max(1, segment.End - segment.Start + 1);
                return new SegmentDto
                {
                    Start = segment.Start,
                    End = segment.End,
                    Completed = segment.Completed,
                    IsComplete = segment.IsComplete,
                    Fraction = Math.Clamp(segment.Completed / (double)length, 0, 1),
                };
            })
            .ToList();
    }

    public IReadOnlyList<ConnectionTelemetryDto> GetConnections(Guid id)
    {
        lock (_sync) if (!_jobs.ContainsKey(id)) return Array.Empty<ConnectionTelemetryDto>();
        if (_engine is not IConnectionTelemetrySource source) return Array.Empty<ConnectionTelemetryDto>();
        return source.GetConnections(id).Take(128).Select(connection => new ConnectionTelemetryDto
        {
            Id = connection.Id, Host = connection.Host, State = connection.State,
            RangeStart = connection.RangeStart, RangeEnd = connection.RangeEnd,
            ReceivedBytes = connection.ReceivedBytes, BytesPerSecond = connection.BytesPerSecond,
            StatusCode = connection.StatusCode, HttpVersion = connection.HttpVersion, StartedAt = connection.StartedAt,
        }).ToList();
    }

    public JobDto? GetJob(Guid id)
    {
        lock (_sync)
        {
            return _jobs.TryGetValue(id, out var runtime) ? Map(runtime.Job) : null;
        }
    }

    public async Task<FileMetadataResult> ReadFileMetadataAsync(FileMetadataRequest request, CancellationToken cancellationToken = default)
    {
        if (!TryValidateUrl(request.Url, out var uri, out var error) || uri.Scheme is not ("http" or "https"))
            return new FileMetadataResult { Error = string.IsNullOrWhiteSpace(error) ? "File information is available for HTTP/HTTPS URLs." : error };
        if (!TryCopyHeaders(request.Headers, out var headers, out error)) return new FileMetadataResult { Error = error };
        if (_engine is not IFileMetadataReader reader) return new FileMetadataResult { Error = "This engine cannot preview file information." };
        if (!await _metadataGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return new FileMetadataResult { Error = "Other file information requests are still running. Try again shortly." };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            var metadata = await reader.ReadMetadataAsync(new DownloadJob { Url = uri.AbsoluteUri, RequestHeaders = headers,
                UserAgent = request.UserAgent, Referrer = request.Referrer }, timeout.Token).ConfigureAwait(false);
            return new FileMetadataResult { Ok = true, FileName = ServerNameWithoutSecret(metadata.FileName, uri.AbsoluteUri, metadata.FinalUrl), TotalBytes = metadata.TotalBytes,
                ContentType = metadata.ContentType, FinalUrl = MaskIfProviderLink(metadata.FinalUrl) };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new FileMetadataResult { Error = "File information request timed out. You can still start the download." }; }
        catch (Exception ex) when (ex is HttpRequestException or ArgumentException or IOException or InvalidOperationException)
        { return new FileMetadataResult { Error = ex.Message }; }
        finally { _metadataGate.Release(); }
    }

    public async Task<AddJobResponse> AddJobAsync(AddJobRequest request, CancellationToken cancellationToken = default)
    {
        // A provider's package counts its files and drives what follows them, so no client may join one.
        if (request.PackageKey?.Trim().StartsWith(RemotePackagePrefix, StringComparison.OrdinalIgnoreCase) == true)
        {
            return new AddJobResponse { Accepted = false, Error = "That package key is reserved.", ErrorCode = "invalid_package" };
        }

        // Outside the settings gate: the probe is network-bound and must not block other adds.
        await ResolveServerFileNameAsync(request, cancellationToken).ConfigureAwait(false);
        await _settingsUpdateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (RefuseIfPackageFull(request) is { } refused) return refused;
            var result = await AddJobCoreAsync(request, cancellationToken).ConfigureAwait(false);
            if (result.Accepted && result.Job is { } job)
            {
                await AssignPackageAfterAddAsync(job.Id, request, cancellationToken).ConfigureAwait(false);
                // A small file can finish before its package is saved, which no job event then reports.
                if (GetJob(job.Id)?.State == "completed") RequestPackageCheck();
            }

            return result;
        }
        finally { _settingsUpdateGate.Release(); }
    }

    /// <summary>
    /// When a download is added without a file name, asks the server for it (Content-Disposition,
    /// then the final URL after redirects), as browsers do. The name decides the category and
    /// therefore the folder, so it has to be known before the job is created. Failures and
    /// timeouts keep the name taken from the URL; the download itself is unaffected.
    /// </summary>
    private async Task ResolveServerFileNameAsync(AddJobRequest request, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.FileName) || _engine is not IFileMetadataReader reader) return;
        if (!TryValidateUrl(request.Url, out var uri, out _) || uri.Scheme is not ("http" or "https")) return;
        if (!TryCopyHeaders(request.Headers, out var headers, out _)) return;
        await _addProbeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var metadata = await reader.ReadMetadataAsync(new DownloadJob
            {
                Url = uri.AbsoluteUri,
                FinalUrl = request.FinalUrl,
                RequestHeaders = headers,
                UserAgent = request.UserAgent,
                Referrer = request.Referrer,
            }, timeout.Token).ConfigureAwait(false);
            var serverName = ServerNameWithoutSecret(metadata.FileName, uri.AbsoluteUri, metadata.FinalUrl);
            if (serverName is not null && !string.Equals(serverName, "download", StringComparison.OrdinalIgnoreCase))
                request.FileName = serverName;
        }
        // Any probe failure (timeout, HTTP error, refused range) falls back to the URL name.
        catch (Exception) when (!cancellationToken.IsCancellationRequested) { }
        finally { _addProbeGate.Release(); }
    }

    private async Task<AddJobResponse> AddJobCoreAsync(
        AddJobRequest request,
        CancellationToken cancellationToken,
        RemoteAddContext? remote = null)
    {
        if (request.Priority is { } requestedPriority && !JobPriorities.IsValid(requestedPriority))
        {
            return new AddJobResponse { Accepted = false, Error = "Choose a priority of High, Normal or Low.", ErrorCode = "invalid_priority" };
        }

        Uri uri;
        if (remote is not null)
        {
            uri = new Uri(request.Url, UriKind.Absolute);
        }
        else if (!TryValidateUrl(request.Url, out uri, out var urlError))
        {
            return new AddJobResponse { Accepted = false, Error = urlError, ErrorCode = "invalid_url" };
        }

        var owner = string.IsNullOrWhiteSpace(request.OwnerExtensionId) ? null : request.OwnerExtensionId.Trim();
        var clientRequestId = string.IsNullOrWhiteSpace(request.ClientRequestId) ? null : request.ClientRequestId.Trim();
        if (remote is null)
        {
            // Claiming a provider's request id would make a later real add find a stranger's job.
            if (owner?.StartsWith(RemoteOwnerPrefix, StringComparison.Ordinal) == true)
            {
                return new AddJobResponse { Accepted = false, Error = "That owner name is reserved.", ErrorCode = "invalid_owner" };
            }

            if (ProviderLinks.IsMasked(request.Url) || ProviderLinks.IsMasked(request.FinalUrl))
            {
                return new AddJobResponse
                {
                    Accepted = false,
                    Error = "This address has its key hidden, so it cannot download anything. Paste the original link.",
                    ErrorCode = "masked_url",
                };
            }
        }

        if (owner is not null && clientRequestId is not null)
        {
            lock (_sync)
            {
                var existing = _jobs.Values.FirstOrDefault(r =>
                    string.Equals(r.Job.OwnerExtensionId, owner, StringComparison.Ordinal) &&
                    string.Equals(r.Job.ClientRequestId, clientRequestId, StringComparison.Ordinal));
                if (existing is not null)
                {
                    return Duplicate(existing.Job);
                }
            }

            var claimed = await _jobStore.FindClaimedRequestAsync(owner, clientRequestId, cancellationToken)
                .ConfigureAwait(false);
            if (claimed is not null)
            {
                var existing = await _jobStore.LoadAsync(claimed.Value, cancellationToken).ConfigureAwait(false);
                if (existing is not null)
                {
                    lock (_sync)
                    {
                        _jobs[existing.Id] = _jobs.TryGetValue(existing.Id, out var existingRuntime)
                            ? existingRuntime
                            : new JobRuntime(existing);
                    }

                    return Duplicate(existing);
                }

                // A claim without a job is a crash residue; remove it and continue.
                await _jobStore.DeleteClaimAsync(owner, clientRequestId, cancellationToken).ConfigureAwait(false);
            }
        }

        var startMode = ParseEnum(request.StartMode, StartMode.StartNow);
        var source = ParseEnum(request.Source, DownloadSource.Api);
        if (source == DownloadSource.Remote && remote is null)
        {
            source = DownloadSource.Api;
        }

        var conflictPolicy = ParseEnum(request.ConflictPolicy, ConflictPolicy.Rename);
        var (jobUrl, finalUrl, protectedUrl) = SplitOffProviderLink(request.Url, request.FinalUrl);

        var fileName = string.IsNullOrWhiteSpace(request.FileName)
            ? FileNameResolver.Resolve(null, uri)
            : FileNameResolver.Sanitize(request.FileName);
        // A name taken from the address would carry the key into the path, the list and every client.
        if (_linkSecrets?.Holds(fileName) == true || NameIsSecretOfAddress(fileName, request.Url)) fileName = "download";
        var browserDownload = source is DownloadSource.ChromeAuto or DownloadSource.ChromeContextMenu;
        var categoryId = request.CategoryId;
        if (Guid.TryParseExact(categoryId, "N", out var requestedCategoryGuid)) categoryId = requestedCategoryGuid.ToString("N");
        lock (_sync)
        {
            if (!string.IsNullOrWhiteSpace(categoryId) && !FileCategories.IsBuiltIn(categoryId) &&
                !_settings.CustomCategories.Any(category => category.Id == categoryId))
                return new AddJobResponse { Accepted = false, Error = "Category not found.", ErrorCode = "invalid_category" };
            categoryId = string.IsNullOrWhiteSpace(categoryId)
                ? _settings.CustomCategories.FirstOrDefault(category => category.Extensions.Contains(
                    Path.GetExtension(fileName).TrimStart('.'), StringComparer.OrdinalIgnoreCase))?.Id ?? FileCategories.For(fileName)
                : FileCategories.IsBuiltIn(categoryId) ? categoryId.ToLowerInvariant() : categoryId;
        }
        var destination = ResolveDestination(request.DestinationDirectory, fileName,
            useLastFolder: !browserDownload, useCategoryFolder: !browserDownload, categoryId: categoryId);
        string finalPath;
        try
        {
            Directory.CreateDirectory(destination);
            finalPath = ResolveFinalPath(destination, fileName, conflictPolicy);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new AddJobResponse
            {
                Accepted = false,
                Error = $"Cannot use destination '{destination}': {ex.Message}",
                ErrorCode = "invalid_destination",
            };
        }

        if (!TryCopyHeaders(request.Headers, out var headers, out var headerError))
        {
            return new AddJobResponse { Accepted = false, Error = headerError, ErrorCode = "invalid_headers" };
        }

        var queueId = _settings.Queues.Any(q => q.Id == request.QueueId)
            ? request.QueueId
            : _settings.DefaultQueueId;
        var queue = _settings.Queues.First(q => q.Id == queueId);

        var providerCap = remote?.MaxConnections ?? (protectedUrl is not null ? ProviderLinks.MaxConnections : int.MaxValue);
        var job = new DownloadJob
        {
            Url = jobUrl,
            FinalUrl = finalUrl,
            ProtectedUrl = protectedUrl,
            RemoteSource = remote?.Source,
            ExpectedTotalBytes = remote?.ExpectedTotalBytes,
            ExpectedMd5 = remote?.ExpectedMd5,
            PageUrl = MaskIfProviderLink(request.PageUrl),
            Referrer = MaskIfProviderLink(request.Referrer),
            UserAgent = request.UserAgent,
            QueueId = queueId,
            QueueName = queue.Name,
            Priority = request.Priority ?? JobPriorities.Normal,
            FileName = Path.GetFileName(finalPath),
            CategoryId = categoryId,
            CategoryIsAutomatic = string.IsNullOrWhiteSpace(request.CategoryId),
            DestinationDirectory = destination,
            FinalFilePath = finalPath,
            PartFilePath = string.Empty,
            StartMode = startMode,
            QueueProcessing = startMode == StartMode.Queue,
            Source = source,
            ClientRequestId = clientRequestId,
            OwnerExtensionId = owner,
            RequestHeaders = headers,
            RequestedConnections = Math.Clamp(
                Math.Min(
                    Math.Min(request.Connections ?? _settings.DefaultConnections, EffectiveConnectionLimit(uri)),
                    providerCap),
                1, 32),
            SpeedLimitBytesPerSecond = request.SpeedLimitBytesPerSecond ?? _settings.GlobalSpeedLimitBytesPerSecond,
            Description = request.Description,
            ExpectedSha256 = string.IsNullOrWhiteSpace(request.ExpectedSha256) ? null : request.ExpectedSha256.Trim(),
            ConflictPolicy = conflictPolicy,
            State = DownloadState.Queued,
            AutoResumeAfterRestart = request.AutoResumeAfterRestart ?? _settings.AutoResumeUnfinishedOnStartup,
            TotalBytes = remote?.ExpectedTotalBytes,
            ReceivedBytes = 0,
        };

        // Keep the payload on the destination volume. Segments are written at their
        // final offsets into this file, so completion is a same-volume rename and
        // never needs free space on the state drive or a cross-volume copy.
        job.PartFilePath = BuildPartPath(finalPath, job.Id);

        try
        {
            await _jobStore.SaveAsync(job, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new AddJobResponse
            {
                Accepted = false,
                Error = $"Could not persist the download: {ex.Message}",
                ErrorCode = "persistence_failed",
            };
        }

        if (owner is not null && clientRequestId is not null)
        {
            bool claimed;
            try
            {
                claimed = await _jobStore.TryClaimRequestAsync(owner, clientRequestId, job.Id, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The saved job has no claim and isn't in memory yet, so a retry would make a second
                // one for the same request, and the next start would load both.
                var takenBack = true;
                try
                {
                    await _jobStore.DeleteAsync(job.Id, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception deleteFailure) when (deleteFailure is IOException or UnauthorizedAccessException)
                {
                    // It will be back at the next start whatever happens, so it had better count now: in
                    // memory, a retry finds it by its owner and request id, with or without the claim.
                    _log?.Invoke($"Could not take back job {job.Id} after its claim failed: {deleteFailure.GetType().Name}.");
                    takenBack = false;
                }

                if (takenBack)
                {
                    return new AddJobResponse
                    {
                        Accepted = false,
                        Error = $"Could not persist the download: {ex.Message}",
                        ErrorCode = "persistence_failed",
                    };
                }

                claimed = true;
            }

            if (!claimed)
            {
                var existingId = await _jobStore.FindClaimedRequestAsync(owner, clientRequestId, cancellationToken)
                    .ConfigureAwait(false);
                if (existingId is not null && existingId != job.Id)
                {
                    await _jobStore.DeleteAsync(job.Id, cancellationToken).ConfigureAwait(false);
                    DeleteFileQuietly(job.PartFilePath);
                    var existing = await _jobStore.LoadAsync(existingId.Value, cancellationToken).ConfigureAwait(false);
                    if (existing is not null)
                    {
                        lock (_sync)
                        {
                            if (!_jobs.ContainsKey(existing.Id))
                            {
                                _jobs[existing.Id] = new JobRuntime(existing);
                            }
                        }

                        return Duplicate(existing);
                    }
                }
            }
        }

        var runtime = new JobRuntime(job);
        lock (_sync)
        {
            _jobs[job.Id] = runtime;
            _completionFiredQueues.Remove(queueId);
        }

        Raise(runtime, "added");
        Schedule();

        var settingsChanged = false;
        if (!browserDownload && request.RememberDestination != false &&
            !string.IsNullOrWhiteSpace(request.DestinationDirectory))
        {
            lock (_sync)
            {
                if (!string.IsNullOrWhiteSpace(request.CategoryId))
                {
                    var custom = _settings.CustomCategories.FirstOrDefault(category => category.Id == categoryId);
                    if (custom is not null) custom.DestinationDirectory = destination;
                    else _settings.CategoryFolders[categoryId!] = destination;
                }
                else _settings.LastDestinationDirectory = destination;
            }
            settingsChanged = true;
        }

        if (source is not (DownloadSource.Import or DownloadSource.Remote) && protectedUrl is null)
        {
            lock (_sync)
            {
                _settings.RecentUrls ??= new List<string>();
                _settings.RecentUrls.RemoveAll(url =>
                    string.Equals(url, job.Url, StringComparison.OrdinalIgnoreCase));
                _settings.RecentUrls.Insert(0, job.Url);
                if (_settings.RecentUrls.Count > 20)
                {
                    _settings.RecentUrls.RemoveRange(20, _settings.RecentUrls.Count - 20);
                }
            }

            settingsChanged = true;
        }

        if (settingsChanged)
        {
            try
            {
                await _settingsStore.SaveAsync(_settings, cancellationToken).ConfigureAwait(false);
                SettingsChanged?.Invoke(this, _settings);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log?.Invoke($"Could not remember recent settings: {ex.Message}");
            }
        }

        return new AddJobResponse { Accepted = true, Job = Map(job) };
    }

    public async Task<JobActionResponse> PauseAsync(Guid id, CancellationToken cancellationToken = default)
    {
        JobRuntime? runtime;
        lock (_sync)
        {
            if (!_jobs.TryGetValue(id, out runtime))
            {
                return new JobActionResponse { Ok = false, Error = "Job not found." };
            }

            if (!runtime.Job.CanPause)
            {
                return new JobActionResponse { Ok = false, Error = $"Job is {runtime.Job.State}.", Job = Map(runtime.Job) };
            }

            runtime.PauseRequested = true;
            runtime.Job.QueueRetryAt = null;
            runtime.Job.State = DownloadState.Paused;
            runtime.Job.BytesPerSecond = null;
            runtime.Job.EstimatedRemaining = null;
            runtime.Job.ActiveConnections = 0;
            runtime.Job.Touch();
            runtime.Cts?.Cancel();
        }

        await PersistAsync(runtime, cancellationToken).ConfigureAwait(false);
        Raise(runtime, "paused");
        return new JobActionResponse { Ok = true, Job = Map(runtime.Job) };
    }

    public async Task<JobActionResponse> ResumeAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _settingsUpdateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await ResumeCoreAsync(id, cancellationToken).ConfigureAwait(false); }
        finally { _settingsUpdateGate.Release(); }
    }

    /// <summary>
    /// A client sees a job's outcome as soon as it is decided, a moment before its run has finished saving it. An
    /// answer of "still running" to someone who was just told it failed would be wrong, so that moment is waited out.
    /// </summary>
    private async Task WaitForEndedRunAsync(Guid id, CancellationToken cancellationToken)
    {
        Task? runner;
        lock (_sync)
        {
            runner = _jobs.TryGetValue(id, out var runtime) && runtime.Job.State is DownloadState.Failed or DownloadState.Cancelled or DownloadState.Paused
                ? runtime.Runner
                : null;
        }

        if (runner is { IsCompleted: false })
        {
            await Task.WhenAny(runner, Task.Delay(TimeSpan.FromSeconds(5), cancellationToken)).ConfigureAwait(false);
        }
    }

    private async Task<JobActionResponse> ResumeCoreAsync(Guid id, CancellationToken cancellationToken)
    {
        await WaitForEndedRunAsync(id, cancellationToken).ConfigureAwait(false);

        JobRuntime? runtime;
        Task? stopping = null;
        lock (_sync)
        {
            if (!_jobs.TryGetValue(id, out runtime))
            {
                return new JobActionResponse { Ok = false, Error = "Job not found." };
            }

            if (runtime.Job.State is DownloadState.Completed)
            {
                return new JobActionResponse { Ok = false, Error = "Job is already complete.", Job = Map(runtime.Job) };
            }

            if (runtime.Starting || (runtime.Runner is { IsCompleted: false }))
            {
                // Undoing a cancel right away waits for the transfer to wind down, as after a pause.
                if (!runtime.PauseRequested && (!runtime.CancelRequested || runtime.CancelledForRemoval))
                {
                    return new JobActionResponse { Ok = false, Error = "Job is already running.", Job = Map(runtime.Job) };
                }

                stopping = runtime.Runner;
            }
        }

        if (stopping is not null)
        {
            var finished = await Task.WhenAny(stopping, Task.Delay(TimeSpan.FromSeconds(10), cancellationToken))
                .ConfigureAwait(false);
            if (finished != stopping)
            {
                return new JobActionResponse { Ok = false, Error = "The download is still stopping. Try again.", Job = GetJob(id) };
            }
        }

        lock (_sync)
        {
            if (!_jobs.TryGetValue(id, out runtime))
            {
                return new JobActionResponse { Ok = false, Error = "Job not found." };
            }

            runtime.PauseRequested = false;
            runtime.CancelRequested = false;
            runtime.CancelledForRemoval = false;
            runtime.Job.QueueRetryAt = null;
            runtime.Job.State = DownloadState.Queued;
            runtime.Job.StartMode = StartMode.StartNow;
            runtime.Job.Failure = null;
            runtime.Job.CompletedAt = null;
            runtime.Job.CancelledByPackageId = null;
            runtime.Job.AutoResumeAfterRestart = true;
            runtime.Job.Touch();
        }

        await PersistAsync(runtime, cancellationToken).ConfigureAwait(false);
        Raise(runtime, "queued");
        Schedule();
        return new JobActionResponse { Ok = true, Job = Map(runtime.Job) };
    }

    public async Task<JobActionResponse> CancelAsync(
        Guid id,
        bool deleteFile = true,
        CancellationToken cancellationToken = default,
        bool onlyIfUnfinished = false,
        Guid? cancelledByPackage = null)
    {
        JobRuntime? runtime;
        lock (_sync)
        {
            if (!_jobs.TryGetValue(id, out runtime))
            {
                return new JobActionResponse { Ok = false, Error = "Job not found." };
            }

            if (onlyIfUnfinished && runtime.Job.IsTerminal)
                return new JobActionResponse { Ok = false, Error = "This download has already finished. Its saved file has been kept." };

            runtime.CancelRequested = true;
            runtime.DeleteOnCancel = deleteFile;
            runtime.Job.DeleteFileOnCancel = deleteFile;
            runtime.Job.QueueRetryAt = null;
            runtime.Job.State = DownloadState.Cancelled;
            runtime.Job.CancelledByPackageId = cancelledByPackage;
            runtime.Job.BytesPerSecond = null;
            runtime.Job.EstimatedRemaining = null;
            runtime.Job.ActiveConnections = 0;
            runtime.Job.Failure = new JobFailure(FailureKind.Cancelled, "Cancelled by the user.", false, true, DateTimeOffset.UtcNow);
            runtime.Job.Touch();
            runtime.Cts?.Cancel();
        }

        if (deleteFile)
        {
            await DeletePayloadAsync(runtime.Job, includeFinal: true, cancellationToken).ConfigureAwait(false);
        }

        await PersistAsync(runtime, cancellationToken).ConfigureAwait(false);
        Raise(runtime, "cancelled");
        Schedule();
        return new JobActionResponse { Ok = true, Job = Map(runtime.Job) };
    }

    public async Task<JobActionResponse> RemoveAsync(
        Guid id,
        bool deleteFile,
        CancellationToken cancellationToken = default)
    {
        await _settingsUpdateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            bool inPackage;
            lock (_sync)
            {
                if (!_jobs.ContainsKey(id)) return new JobActionResponse { Error = "Job not found." };
                inPackage = _settings.DownloadGroups.Any(group => group.JobIds.Contains(id));
            }

            var removed = await RemoveCoreAsync(id, deleteFile, cancellationToken).ConfigureAwait(false);
            // Losing its last unfinished file can leave a package finished.
            if (removed.Ok && inPackage) RequestPackageCheck();
            return removed;
        }
        finally { _settingsUpdateGate.Release(); }
    }

    /// <summary>
    /// Removes several downloads (a package) as one operation. Every member is stopped first, so
    /// their transfers wind down together and the scheduler cannot start a queued member that is
    /// about to be removed; each is then removed as <see cref="RemoveAsync"/> would.
    /// </summary>
    public async Task<JobActionResponse> RemoveManyAsync(
        IReadOnlyList<Guid> ids,
        bool deleteFile,
        CancellationToken cancellationToken = default)
    {
        if (ids.Count > 1000) return new JobActionResponse { Error = "Remove at most 1000 downloads at a time." };
        var unique = ids.Distinct().ToList();
        var names = new Dictionary<Guid, string>();
        lock (_sync)
        {
            // The gate is released between removals, and what is left partway through
            // a package's removal can look finished. Its files are about to go.
            _packageChecksHeld++;
            foreach (var id in unique)
            {
                if (!_jobs.TryGetValue(id, out var runtime)) continue;
                names[id] = runtime.Job.FileName;
                runtime.AdmissionSuspended = true;
                if (runtime.Runner is { IsCompleted: false })
                {
                    runtime.CancelRequested = true;
                    runtime.CancelledForRemoval = true;
                    runtime.Cts?.Cancel();
                }
            }
        }

        var failures = new List<string>();
        try
        {
            foreach (var id in unique)
            {
                var result = await RemoveAsync(id, deleteFile, cancellationToken).ConfigureAwait(false);
                if (!result.Ok && result.Error != "Job not found.")
                    failures.Add($"{names.GetValueOrDefault(id, id.ToString("D"))}: {result.Error}");
            }
        }
        finally
        {
            lock (_sync)
            {
                _packageChecksHeld--;
                foreach (var id in unique)
                    if (_jobs.TryGetValue(id, out var runtime)) runtime.AdmissionSuspended = false;
            }
            RequestPackageCheck();
            Schedule();
        }
        return failures.Count == 0
            ? new JobActionResponse { Ok = true }
            : new JobActionResponse { Error = string.Join(Environment.NewLine, failures.Take(5)) };
    }

    private async Task<JobActionResponse> RemoveCoreAsync(Guid id, bool deleteFile, CancellationToken cancellationToken)
    {
        JobRuntime? runtime;
        Task? runner;
        lock (_sync)
        {
            if (!_jobs.TryGetValue(id, out runtime))
            {
                return new JobActionResponse { Ok = false, Error = "Job not found." };
            }

            runtime.CancelRequested = true;
            runtime.CancelledForRemoval = true;
            runtime.Cts?.Cancel();
            runner = runtime.Runner;
        }

        if (runner is not null)
        {
            try
            {
                var settled = await Task.WhenAny(runner, Task.Delay(TimeSpan.FromSeconds(15), cancellationToken)).ConfigureAwait(false);
                if (settled != runner)
                    return new JobActionResponse { Error = "The download is still stopping. Retry removal after it stops.", Job = Map(runtime.Job) };
            }
            catch (OperationCanceledException)
            {
                // continue with removal
            }
        }

        // Recorded before anything is taken apart, so a kill from here on still ends with the job
        // gone at the next start, not running again outside its package.
        var deleteOnCancelBefore = runtime.Job.DeleteFileOnCancel;
        if (await SaveRemovalMarkerAsync(runtime, true, deleteFile).ConfigureAwait(false) is { } markerError)
        {
            lock (_sync) runtime.Job.DeleteFileOnCancel = deleteOnCancelBefore;
            return new JobActionResponse { Error = "The stopped download was retained because its removal could not be saved. " + markerError, Job = Map(runtime.Job) };
        }

        AppSettings archived;
        bool leftPackage;
        lock (_sync)
        {
            var groups = CloneGroups(_settings.DownloadGroups);
            var own = groups.FirstOrDefault(group => group.JobIds.Contains(id));
            leftPackage = own is not null;
            if (own is not null)
            {
                own.JobIds.Remove(id);
                // A package of one is just a download. Only this job's package can have become
                // one here; another with a single member may simply still be getting its files.
                if (own.JobIds.Count < 2) groups.Remove(own);
            }

            archived = _settings.WithDownloadGroups(groups);
            archived.ArchivedDownloads = _settings.ArchivedDownloads.Where(record => record.Id != id).ToList();
            archived.ArchivedDownloads.Add(new ArchivedDownloadRecord { Id = id, ReceivedBytes = runtime.Job.ReceivedBytes,
                State = runtime.Job.State, CreatedAt = runtime.Job.CreatedAt, CompletedAt = runtime.Job.CompletedAt });
        }
        try { await _settingsStore.SaveAsync(archived, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await SaveRemovalMarkerAsync(runtime, false, deleteOnCancelBefore).ConfigureAwait(false);
            return new JobActionResponse { Error = "The stopped download was retained because its statistics could not be saved. " + ex.Message, Job = Map(runtime.Job) };
        }
        lock (_sync)
        {
            _settings.ArchivedDownloads = archived.ArchivedDownloads;
            _settings.DownloadGroups = archived.DownloadGroups;
        }
        if (leftPackage) SettingsChanged?.Invoke(this, _settings);

        if (deleteFile)
        {
            await DeletePayloadAsync(runtime.Job, includeFinal: true, CancellationToken.None).ConfigureAwait(false);
        }

        await _persistGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            lock (_sync) runtime.CancelledForRemoval = true;
            await _jobStore.DeleteAsync(id, CancellationToken.None).ConfigureAwait(false);
            lock (_sync) _jobs.Remove(id);
        }
        finally { _persistGate.Release(); }
        JobChanged?.Invoke(this, new JobChangeEventArgs(Map(runtime.Job), "removed"));
        Schedule();
        return new JobActionResponse { Ok = true, Job = Map(runtime.Job) };
    }

    /// <summary>Writes directly, because the usual save skips a job that is being removed. Returns why it failed.</summary>
    private async Task<string?> SaveRemovalMarkerAsync(JobRuntime runtime, bool pending, bool deleteFile)
    {
        DownloadJob snapshot;
        lock (_sync)
        {
            runtime.Job.RemovalPending = pending;
            runtime.Job.DeleteFileOnCancel = deleteFile;
            snapshot = runtime.Job.Clone();
        }

        await _persistGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await _jobStore.SaveAsync(snapshot, CancellationToken.None).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            lock (_sync) runtime.Job.RemovalPending = false;
            _log?.Invoke($"Could not save the removal of job {snapshot.Id}: {ex.Message}");
            return ex.Message;
        }
        finally { _persistGate.Release(); }
    }

    public async Task PauseAllAsync(CancellationToken cancellationToken = default)
    {
        List<JobRuntime> paused;
        lock (_sync)
        {
            paused = _jobs.Values.Where(r => r.Job.CanPause).ToList();
            foreach (var runtime in paused)
            {
                runtime.PauseRequested = true;
                runtime.Job.QueueRetryAt = null;
                runtime.Job.State = DownloadState.Paused;
                runtime.Job.BytesPerSecond = null;
                runtime.Job.EstimatedRemaining = null;
                runtime.Job.ActiveConnections = 0;
                runtime.Job.Touch();
                runtime.Cts?.Cancel();
            }
        }

        foreach (var runtime in paused)
        {
            await PersistAsync(runtime, cancellationToken).ConfigureAwait(false);
            Raise(runtime, "paused");
        }
    }

    public async Task ResumeAllAsync(CancellationToken cancellationToken = default)
    {
        await _settingsUpdateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await ResumeAllCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { _settingsUpdateGate.Release(); }
    }

    private async Task ResumeAllCoreAsync(CancellationToken cancellationToken)
    {
        List<JobRuntime> resumed;
        lock (_sync)
        {
            resumed = _jobs.Values
                .Where(r => !r.Starting && r.Runner is not { IsCompleted: false } &&
                            r.Job.State is DownloadState.Paused or DownloadState.Failed or DownloadState.Cancelled)
                .ToList();
            foreach (var runtime in resumed)
            {
                runtime.PauseRequested = false;
                runtime.CancelRequested = false;
                runtime.Job.QueueRetryAt = null;
                runtime.Job.State = DownloadState.Queued;
                runtime.Job.StartMode = StartMode.StartNow;
                runtime.Job.Failure = null;
                runtime.Job.CompletedAt = null;
                runtime.Job.Touch();
            }
        }

        foreach (var runtime in resumed)
        {
            await PersistAsync(runtime, cancellationToken).ConfigureAwait(false);
            Raise(runtime, "queued");
        }

        Schedule();
    }

    public async Task<SettingsResult> UpdateSettingsAsync(
        SettingsDto settings,
        CancellationToken cancellationToken = default)
    {
        await _settingsUpdateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await UpdateSettingsCoreAsync(settings, cancellationToken).ConfigureAwait(false); }
        finally { _settingsUpdateGate.Release(); }
    }

    private async Task<SettingsResult> UpdateSettingsCoreAsync(SettingsDto settings, CancellationToken cancellationToken)
    {
        settings.EventSoundFiles ??= new(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in settings.EventSoundFiles)
        {
            if (key is not ("completed" or "failed" or "queueFinished") || value is null ||
                (value.Length > 0 && value != "@system" &&
                 (!Path.IsPathFullyQualified(value) || !string.Equals(Path.GetExtension(value), ".wav", StringComparison.OrdinalIgnoreCase))))
                return new SettingsResult { Ok = false, Error = "Event sounds must be disabled, system sounds, or absolute WAV file paths.", Settings = MapSettings(_settings) };
        }
        if ((settings.Queues ?? new()).Any(queue => queue.RetryLimit is < 0 or > 100) ||
            (settings.Schedules ?? new()).Any(schedule => schedule.RepeatEveryMinutes is < 1 or > 1440 ||
                schedule.RepeatEveryMinutes is not null && !schedule.TimedStartEnabled ||
                !schedule.StartOnHostStartup && !schedule.TimedStartEnabled))
            return new SettingsResult { Error = "Select a startup or timed trigger, synchronization interval 1–1440 minutes, and retry limit 0–100 or unlimited.", Settings = MapSettings(_settings) };
        var categories = settings.CustomCategories ?? new();
        var categoryIds = new HashSet<string>(StringComparer.Ordinal);
        var categoryNames = new HashSet<string>(FileCategories.Names.Append("other"), StringComparer.OrdinalIgnoreCase);
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (categories.Count > 100) return new SettingsResult { Ok = false, Error = "At most 100 custom categories are supported.", Settings = MapSettings(_settings) };
        foreach (var category in categories)
        {
            if (category is null) return new SettingsResult { Ok = false, Error = "Invalid category.", Settings = MapSettings(_settings) };
            category.Name = category.Name?.Trim() ?? string.Empty;
            if (Guid.TryParseExact(category.Id, "N", out var categoryGuid)) category.Id = categoryGuid.ToString("N");
            category.Extensions = (category.Extensions ?? new()).Select(extension => (extension ?? string.Empty).Trim().TrimStart('.').ToLowerInvariant()).Distinct().ToList();
            if (!Guid.TryParseExact(category.Id, "N", out _) || !categoryIds.Add(category.Id) ||
                category.Name.Length is < 1 or > 64 || category.Name.Any(char.IsControl) || !categoryNames.Add(category.Name) ||
                category.Extensions.Count > 64 || category.Extensions.Any(extension => extension.Length is < 1 or > 32 ||
                    extension.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')) || !extensions.Add(extension)))
                return new SettingsResult { Ok = false, Error = "Categories need unique IDs/names and unambiguous file extensions.", Settings = MapSettings(_settings) };
            try
            {
                if (!string.IsNullOrWhiteSpace(category.DestinationDirectory)) category.DestinationDirectory = Path.GetFullPath(category.DestinationDirectory);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
            { return new SettingsResult { Ok = false, Error = "Invalid category folder.", Settings = MapSettings(_settings) }; }
        }
        lock (_sync)
        {
            var deleting = (settings.DeleteCategoryIds ?? new()).ToHashSet(StringComparer.Ordinal);
            if (deleting.Any(id => !_settings.CustomCategories.Any(category => category.Id == id) || categoryIds.Contains(id)))
                return new SettingsResult { Ok = false, Error = "The category list changed. Refresh Options before deleting categories.", Settings = MapSettings(_settings) };
            if (_settings.CustomCategories.Any(category => !categoryIds.Contains(category.Id) && !deleting.Contains(category.Id)))
                return new SettingsResult { Ok = false, Error = "Existing custom categories must be retained. Refresh settings before saving.", Settings = MapSettings(_settings) };
            if (_jobs.Values.Any(runtime => runtime.Job.CategoryId is { } id && deleting.Contains(id)))
                return new SettingsResult { Ok = false, Error = "Change the category of this category's downloads before deleting it. No settings or files were changed.", Settings = MapSettings(_settings) };
        }
        if (string.IsNullOrWhiteSpace(settings.DefaultDownloadDirectory))
        {
            return new SettingsResult { Ok = false, Error = "Default download directory is required.", Settings = MapSettings(_settings) };
        }

        string directory;
        try
        {
            directory = Path.GetFullPath(settings.DefaultDownloadDirectory);
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new SettingsResult { Ok = false, Error = ex.Message, Settings = MapSettings(_settings) };
        }

        settings.DefaultDownloadDirectory = directory;
        settings.MaxConcurrentDownloads = Math.Clamp(settings.MaxConcurrentDownloads, 1, 16);
        settings.DefaultConnections = Math.Clamp(settings.DefaultConnections, 1, 32);
        settings.MaxConnectionsPerHost = Math.Clamp(settings.MaxConnectionsPerHost, 1, 64);
        if (settings.GlobalSpeedLimitBytesPerSecond is <= 0)
        {
            settings.GlobalSpeedLimitBytesPerSecond = null;
        }

        var next = new AppSettings
        {
            CategoryFoldersRoot = _settings.CategoryFoldersRoot,
            DownloadGroups = CloneGroups(_settings.DownloadGroups),
            ArchivedDownloads = new(_settings.ArchivedDownloads),
            CustomCategories = categories.Select(category => new DownloadCategory { Id = category.Id, Name = category.Name,
                DestinationDirectory = category.DestinationDirectory, Extensions = new(category.Extensions) }).ToList(),
            DefaultDownloadDirectory = directory,
            MaxConcurrentDownloads = settings.MaxConcurrentDownloads,
            DefaultConnections = settings.DefaultConnections,
            MaxConnectionsPerHost = settings.MaxConnectionsPerHost,
            ConnectionProfiles = settings.ConnectionProfiles
                .Where(profile => !string.IsNullOrWhiteSpace(profile.Pattern))
                .Select(profile => new ConnectionProfile
                {
                    Pattern = profile.Pattern.Trim(),
                    MaxConnections = Math.Clamp(profile.MaxConnections, 1, 32),
                })
                .Take(50)
                .ToList(),
            GlobalSpeedLimitBytesPerSecond = settings.GlobalSpeedLimitBytesPerSecond,
            LaunchAtStartup = settings.LaunchAtStartup,
            ClipboardMonitoring = settings.ClipboardMonitoring,
            BrowserIntegrationEnabled = settings.BrowserIntegrationEnabled,
            NotifyOnCompletion = settings.NotifyOnCompletion,
            PlayCompletionSound = settings.PlayCompletionSound,
            EventSoundFiles = new Dictionary<string, string>(settings.EventSoundFiles ?? new(), StringComparer.OrdinalIgnoreCase),
            ShowCompletionDialog = settings.ShowCompletionDialog,
            AutoResumeUnfinishedOnStartup = settings.AutoResumeUnfinishedOnStartup,
            AllowLocalNetwork = settings.AllowLocalNetwork,
            PreventSleepWhileDownloading = settings.PreventSleepWhileDownloading,
            FfmpegPath = settings.FfmpegPath,
            PostDownloadCommand = settings.PostDownloadCommand,
            UpdateManifestUrl = settings.UpdateManifestUrl,
            UpdatePublicKey = settings.UpdatePublicKey,
            UpdateChannel = settings.UpdateChannel,
            AutoCheckUpdates = settings.AutoCheckUpdates,
            UpdateCheckIntervalHours = settings.UpdateCheckIntervalHours,
            SkippedUpdateVersion = settings.SkippedUpdateVersion,
            LastUpdateCheck = settings.LastUpdateCheck,
            AllowSystemCompletionActions = settings.AllowSystemCompletionActions,
            Theme = settings.Theme,
            LastDestinationDirectory = settings.LastDestinationDirectory,
            RecentUrls = new List<string>(settings.RecentUrls ?? new List<string>()),
            CategoryFolders = new Dictionary<string, string>(
                settings.CategoryFolders ?? new Dictionary<string, string>(),
                StringComparer.OrdinalIgnoreCase),
            ExcludedFileExtensions = settings.ExcludedFileExtensions,
            ExcludedHosts = settings.ExcludedHosts,
            InterceptedFileExtensions = settings.InterceptedFileExtensions,
            Proxy = MapProxy(settings.Proxy),
            SiteLogins = MapSiteLogins(settings.SiteLogins, _settings.SiteLogins),
            DefaultQueueId = settings.DefaultQueueId,
            Queues = MapQueues(settings.Queues, _settings.Queues, settings.MaxConcurrentDownloads),
            Schedules = MapSchedules(settings.Schedules, _settings.Schedules),
        };

        if (settings.Proxy?.ClearPassword != true &&
            string.IsNullOrEmpty(next.Proxy.Password) &&
            !string.IsNullOrEmpty(_settings.Proxy.Password) &&
            string.Equals(next.Proxy.Address, _settings.Proxy.Address, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(next.Proxy.Username, _settings.Proxy.Username, StringComparison.Ordinal))
        {
            next.Proxy.Password = _settings.Proxy.Password;
        }

        if (!next.Queues.Any(q => q.Id == next.DefaultQueueId))
        {
            next.DefaultQueueId = next.Queues[0].Id;
        }

        lock (_sync)
        {
            var retainedQueueIds = next.Queues.Select(queue => queue.Id).ToHashSet();
            if (_jobs.Values.Any(runtime => !retainedQueueIds.Contains(runtime.Job.QueueId)) ||
                next.Schedules.Any(schedule => !retainedQueueIds.Contains(schedule.QueueId)))
                return new SettingsResult
                {
                    Ok = false,
                    Error = "Move downloads and schedules before removing their queue. No settings were changed.",
                    Settings = MapSettings(_settings),
                };
        }
        await _settingsStore.SaveAsync(next, cancellationToken).ConfigureAwait(false);
        _engine.UpdateHostConnectionLimit(next.MaxConnectionsPerHost);
        lock (_sync)
        {
            _settings = next;
            var queueNames = next.Queues.ToDictionary(q => q.Id, q => q.Name);
            foreach (var runtime in _jobs.Values)
            {
                if (queueNames.TryGetValue(runtime.Job.QueueId, out var queueName))
                {
                    runtime.Job.QueueName = runtime.Job.IsQueued ? queueName : "None";
                }
            }
        }

        _engine.UpdateProxy(next.Proxy);
        _engine.UpdateSiteLogins(BuildSiteLogins(next));
        _engine.UpdateFfmpegPath(next.FfmpegPath);
        _ftpEngine?.UpdateProxy(next.Proxy);
        _ftpEngine?.UpdateSiteLogins(BuildSiteLogins(next));
        _ftpEngine?.UpdateFfmpegPath(next.FfmpegPath);
        UpdateSleepPrevention();
        SettingsChanged?.Invoke(this, next);
        Schedule();
        return new SettingsResult { Ok = true, Settings = MapSettings(next) };
    }

    public async Task<SettingsResult> QueueActionAsync(
        QueueActionRequest request,
        CancellationToken cancellationToken = default)
    {
        await _settingsUpdateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (request.Action.Equals("delete", StringComparison.OrdinalIgnoreCase) && request.MoveContentsToQueueId is { } destination)
            {
                List<Guid> members;
                lock (_sync)
                {
                    if (request.QueueId == _settings.DefaultQueueId || destination == request.QueueId ||
                        !_settings.Queues.Any(queue => queue.Id == request.QueueId) ||
                        !_settings.Queues.Any(queue => queue.Id == destination))
                        return new SettingsResult { Error = "Choose another existing queue. The default queue cannot be deleted.", Settings = MapSettings(_settings) };
                    members = _jobs.Values.Where(runtime => runtime.Job.QueueId == request.QueueId).Select(runtime => runtime.Job.Id).ToList();
                }
                // Persist each move before deleting its old queue. Interruption leaves a valid,
                // partially moved queue that can be retried, never orphaned downloads.
                try
                {
                    foreach (var id in members)
                    {
                        var moved = await MoveToQueueCoreAsync(id, destination, cancellationToken).ConfigureAwait(false);
                        if (!moved.Ok) return new SettingsResult { Error = "Queue retained; some downloads may already have moved. " + moved.Error, Settings = MapSettings(_settings) };
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return new SettingsResult { Error = "Queue retained because reassignment could not be saved. Some downloads may already have moved. " + ex.Message, Settings = MapSettings(_settings) };
                }
                lock (_sync)
                    foreach (var schedule in _settings.Schedules.Where(schedule => schedule.QueueId == request.QueueId))
                        schedule.QueueId = destination;
            }
            return await QueueActionCoreAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally { _settingsUpdateGate.Release(); }
    }

    private async Task<SettingsResult> QueueActionCoreAsync(QueueActionRequest request, CancellationToken cancellationToken)
    {
        List<JobRuntime> paused = new();
        List<JobRuntime> resumed = new();
        List<JobRuntime> renamed = new();
        string? error = null;

        lock (_sync)
        {
            var queue = _settings.Queues.FirstOrDefault(q => q.Id == request.QueueId);
            if (request.Action.Equals("create", StringComparison.OrdinalIgnoreCase))
            {
                if (!ValidQueuePolicy(request))
                    return new SettingsResult { Ok = false, Error = "Invalid queue name, concurrency or quota policy.", Settings = MapSettings(_settings) };
                var usedIds = _settings.Queues.Select(q => q.Id).ToHashSet();
                var nextId = 0;
                while (usedIds.Contains(nextId) && nextId < int.MaxValue) nextId++;
                if (usedIds.Contains(nextId))
                    return new SettingsResult { Ok = false, Error = "No queue identifiers are available.", Settings = MapSettings(_settings) };
                queue = new DownloadQueue
                {
                    Id = nextId, Name = request.Name!.Trim(), IsRunning = false,
                    Order = _settings.Queues.Count,
                    MaxConcurrentDownloads = request.MaxConcurrentDownloads,
                    QuotaEnabled = request.QuotaEnabled ?? false,
                    QuotaBytes = request.QuotaBytes ?? 200L * 1024 * 1024,
                    QuotaWindowHours = request.QuotaWindowHours ?? 5,
                };
                _settings.Queues.Add(queue);
            }
            if (queue is null)
            {
                return new SettingsResult { Ok = false, Error = "Queue not found.", Settings = MapSettings(_settings) };
            }

            switch (request.Action.ToLowerInvariant())
            {
                case "create":
                    break;
                case "delete":
                    if (queue.Id == _settings.DefaultQueueId)
                        error = "The default queue cannot be deleted.";
                    else if (_jobs.Values.Any(r => r.Job.QueueId == queue.Id) ||
                             _settings.Schedules.Any(s => s.QueueId == queue.Id))
                        error = "Move this queue's downloads and schedules before deleting it. No downloads were removed.";
                    else
                        _settings.Queues.Remove(queue);
                    break;
                case "moveup":
                case "movedown":
                    var ordered = _settings.Queues.OrderBy(q => q.Order).ThenBy(q => q.Id).ToList();
                    var position = ordered.FindIndex(q => q.Id == queue.Id);
                    var target = position + (request.Action.Equals("moveup", StringComparison.OrdinalIgnoreCase) ? -1 : 1);
                    if (target >= 0 && target < ordered.Count)
                        (ordered[position], ordered[target]) = (ordered[target], ordered[position]);
                    for (var index = 0; index < ordered.Count; index++) ordered[index].Order = index;
                    _settings.Queues = ordered;
                    break;
                case "policy":
                    if (!ValidQueuePolicy(request))
                    {
                        error = "Enter a queue name, concurrency 1–16, and valid quota limits (up to 10 TiB / 8760 hours).";
                        break;
                    }
                    queue.Name = request.Name!.Trim();
                    queue.MaxConcurrentDownloads = request.MaxConcurrentDownloads;
                    if (request.QuotaEnabled is { } enabled) queue.QuotaEnabled = enabled;
                    if (request.QuotaBytes is { } bytes) queue.QuotaBytes = bytes;
                    if (request.QuotaWindowHours is { } hours) queue.QuotaWindowHours = hours;
                    // Usage and the current window belong to the Host, never the editor.
                    break;
                case "reorderfiles":
                    var members = _jobs.Values.Where(r => r.Job.IsQueued && r.Job.QueueId == queue.Id)
                        .Select(r => r.Job.Id).ToHashSet();
                    if (request.JobOrder is null || request.JobOrder.Count != members.Count ||
                        request.JobOrder.Distinct().Count() != members.Count ||
                        request.JobOrder.Any(id => !members.Contains(id)))
                    {
                        error = "The queue's file list changed. Reopen Files and try again.";
                        break;
                    }
                    queue.JobOrder = request.JobOrder.ToList();
                    break;
                case "start":
                    _completionFiredQueues.Remove(queue.Id);
                    queue.IsRunning = true;
                    foreach (var runtime in _jobs.Values.Where(r =>
                                 r.Job.IsQueued && r.Job.QueueId == queue.Id && (r.Job.State is DownloadState.Paused or DownloadState.Queued ||
                                     r.Job.State == DownloadState.Failed && !r.Job.IsTerminal)))
                    {
                        if (runtime.Starting || runtime.Runner is { IsCompleted: false })
                        {
                            // The pause is still settling; requeue once the runner exits.
                            runtime.ResumeWhenStopped = true;
                            continue;
                        }

                        runtime.PauseRequested = false;
                        runtime.CancelRequested = false;
                        runtime.CancelledForRemoval = false;
                        runtime.Job.QueueProcessing = true;
                        runtime.Job.QueueRetryCount = 0;
                        runtime.Job.QueueRetryAt = null;
                        runtime.Job.State = DownloadState.Queued;
                        runtime.Job.StartMode = StartMode.StartNow;
                        runtime.Job.Failure = null;
                        runtime.Job.Touch();
                        resumed.Add(runtime);
                    }

                    break;
                case "stop":
                    queue.IsRunning = false;
                    foreach (var waiting in _jobs.Values.Where(runtime => runtime.Job.IsQueued && runtime.Job.QueueId == queue.Id && runtime.Job.QueueRetryAt is not null))
                    {
                        waiting.Job.QueueRetryAt = null;
                        waiting.Job.Touch();
                        renamed.Add(waiting);
                    }
                    paused = _jobs.Values
                        .Where(r => r.Job.IsQueued && r.Job.QueueId == queue.Id &&
                                    (r.Starting || r.Runner is { IsCompleted: false }))
                        .ToList();
                    foreach (var runtime in paused)
                    {
                        runtime.PauseRequested = true;
                        runtime.Job.State = DownloadState.Paused;
                        runtime.Job.BytesPerSecond = null;
                        runtime.Job.EstimatedRemaining = null;
                        runtime.Job.ActiveConnections = 0;
                        runtime.Job.Touch();
                        runtime.Cts?.Cancel();
                    }

                    break;
                case "setmax":
                    queue.MaxConcurrentDownloads = Math.Clamp(request.MaxConcurrentDownloads, 1, 16);
                    break;
                default:
                    error = "Unknown queue action.";
                    break;
            }

            foreach (var runtime in _jobs.Values.Where(r => r.Job.IsQueued && r.Job.QueueId == queue.Id))
            {
                if (runtime.Job.QueueName != queue.Name)
                {
                    runtime.Job.QueueName = queue.Name;
                    renamed.Add(runtime);
                }
            }
        }

        if (error is not null)
        {
            return new SettingsResult { Ok = false, Error = error, Settings = MapSettings(_settings) };
        }

        foreach (var runtime in paused)
        {
            await PersistAsync(runtime, cancellationToken).ConfigureAwait(false);
            Raise(runtime, "paused");
        }

        foreach (var runtime in resumed)
        {
            await PersistAsync(runtime, cancellationToken).ConfigureAwait(false);
            Raise(runtime, "queued");
        }

        await _settingsStore.SaveAsync(_settings, cancellationToken).ConfigureAwait(false);
        foreach (var runtime in renamed)
        {
            await PersistAsync(runtime, cancellationToken).ConfigureAwait(false);
            Raise(runtime, "updated");
        }
        SettingsChanged?.Invoke(this, _settings);
        Schedule();
        return new SettingsResult { Ok = true, Settings = MapSettings(_settings) };
    }

    private static bool ValidQueuePolicy(QueueActionRequest request)
        => !string.IsNullOrWhiteSpace(request.Name) && request.Name.Trim().Length <= 80 &&
           request.MaxConcurrentDownloads is >= 1 and <= 16 &&
           request.QuotaBytes is not (< 1 or > 10995116277760L) &&
           request.QuotaWindowHours is not (< 1 or > 8760) &&
           (request.QuotaEnabled != true || (request.QuotaBytes is not null && request.QuotaWindowHours is not null));

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        var install = InstalledBuild.TryLoad();
        if (install is null)
        {
            return new UpdateCheckResult
            {
                Ok = false,
                Status = "notInstalled",
                Error = "Portable and development builds do not auto-update.",
            };
        }

        if (string.IsNullOrWhiteSpace(_settings.UpdateManifestUrl) ||
            string.IsNullOrWhiteSpace(_settings.UpdatePublicKey))
        {
            return new UpdateCheckResult
            {
                Ok = false,
                Status = "notConfigured",
                InstalledVersion = install.Version,
                Error = "Update manifest URL/public key are not configured.",
            };
        }

        if (!Uri.TryCreate(_settings.UpdateManifestUrl, UriKind.Absolute, out var manifestUri) ||
            (manifestUri.Scheme != "https" && !manifestUri.IsLoopback))
        {
            return new UpdateCheckResult
            {
                Ok = false,
                Status = "invalid",
                InstalledVersion = install.Version,
                Error = "The update manifest must use HTTPS (loopback is allowed for testing).",
            };
        }

        try
        {
            using var response = await UpdateClient.GetAsync(manifestUri, cancellationToken)
                .ConfigureAwait(false);
            if (response.RequestMessage?.RequestUri is { } finalManifestUri &&
                finalManifestUri.Scheme != "https" &&
                !finalManifestUri.IsLoopback)
            {
                return new UpdateCheckResult
                {
                    Ok = false,
                    Status = "invalid",
                    InstalledVersion = install.Version,
                    Error = "The update manifest redirected to a non-HTTPS host.",
                };
            }

            if (!response.IsSuccessStatusCode)
            {
                return new UpdateCheckResult
                {
                    Ok = false,
                    Status = "invalid",
                    InstalledVersion = install.Version,
                    Error = $"Update manifest returned HTTP {(int)response.StatusCode}.",
                };
            }

            if (response.Content.Headers.ContentLength is > 1024 * 1024)
            {
                return new UpdateCheckResult
                {
                    Ok = false,
                    Status = "invalid",
                    InstalledVersion = install.Version,
                    Error = "Update manifest is too large.",
                };
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var manifest = UpdateManifestVerifier.Parse(json);
            if (manifest is null ||
                !UpdateManifestVerifier.Verify(manifest, _settings.UpdatePublicKey))
            {
                return new UpdateCheckResult
                {
                    Ok = false,
                    Status = "invalid",
                    InstalledVersion = install.Version,
                    Error = "The update manifest signature is invalid.",
                };
            }

            if (!string.Equals(manifest.Channel, _settings.UpdateChannel, StringComparison.OrdinalIgnoreCase))
            {
                return new UpdateCheckResult
                {
                    Ok = true,
                    Status = "upToDate",
                    InstalledVersion = install.Version,
                    AvailableVersion = manifest.Version,
                };
            }

            if (!IsNewerVersion(manifest.Version, install.Version))
            {
                return new UpdateCheckResult
                {
                    Ok = true,
                    Status = "upToDate",
                    InstalledVersion = install.Version,
                    AvailableVersion = manifest.Version,
                    InstallerUrl = manifest.InstallerUrl,
                    Size = manifest.Size,
                    Sha256 = manifest.Sha256,
                };
            }

            return new UpdateCheckResult
            {
                Ok = true,
                Status = "updateAvailable",
                InstalledVersion = install.Version,
                AvailableVersion = manifest.Version,
                InstallerUrl = manifest.InstallerUrl,
                Size = manifest.Size,
                Sha256 = manifest.Sha256,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult
            {
                Ok = false,
                Status = "invalid",
                InstalledVersion = install.Version,
                Error = ex.Message,
            };
        }
    }

    public async Task<UpdateDownloadResult> DownloadUpdateAsync(
        CancellationToken cancellationToken = default)
    {
        var check = await CheckForUpdatesAsync(cancellationToken).ConfigureAwait(false);
        return await DownloadVerifiedUpdateAsync(check, cancellationToken).ConfigureAwait(false);
    }

    private void ReportUpdateProgress(string stage, string? version, long received = 0,
        long? total = null, string? error = null)
    {
        var progress = new UpdateProgressDto
        {
            Stage = stage, Version = version, ReceivedBytes = received, TotalBytes = total, Error = error,
        };
        lock (_sync) _updateProgress = progress;
        UpdateProgressChanged?.Invoke(this, progress);
    }

    private async Task<UpdateDownloadResult> DownloadVerifiedUpdateAsync(
        UpdateCheckResult check, CancellationToken cancellationToken)
    {
        try
        {
            var result = await DownloadUpdateCoreAsync(check, cancellationToken).ConfigureAwait(false);
            if (!result.Ok) ReportUpdateProgress("failed", check.AvailableVersion, error: result.Error);
            return result;
        }
        catch (OperationCanceledException)
        {
            ReportUpdateProgress("failed", check.AvailableVersion, error: "Update download cancelled.");
            throw;
        }
    }

    private async Task<UpdateDownloadResult> DownloadUpdateCoreAsync(
        UpdateCheckResult check, CancellationToken cancellationToken)
    {
        if (!check.Ok || check.Status != "updateAvailable" ||
            string.IsNullOrWhiteSpace(check.InstallerUrl) ||
            string.IsNullOrWhiteSpace(check.Sha256) || check.Size is not > 0)
        {
            return new UpdateDownloadResult
            {
                Ok = false,
                Error = check.Error ?? "No update is available to download.",
            };
        }

        if (!Uri.TryCreate(check.InstallerUrl, UriKind.Absolute, out var installerUri) ||
            (installerUri.Scheme != "https" && !installerUri.IsLoopback))
        {
            return new UpdateDownloadResult
            {
                Ok = false,
                Error = "The installer URL must use HTTPS (loopback is allowed for testing).",
            };
        }

        if (check.Size > 1024L * 1024 * 1024)
        {
            return new UpdateDownloadResult { Ok = false, Error = "The update is too large." };
        }

        var directory = Path.Combine(_paths.Root, "updates");
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, $"installer-{check.AvailableVersion}.zip");
        ReportUpdateProgress("downloading", check.AvailableVersion, total: check.Size);
        try
        {
            // "Download" followed by "Install now" must not fetch it twice. The hash is from the
            // signed manifest, so this is exactly as trustworthy as a fresh download.
            if (File.Exists(target) && new FileInfo(target).Length == check.Size)
            {
                ReportUpdateProgress("verifying", check.AvailableVersion, check.Size ?? 0, check.Size);
            }

            if (await MatchesManifestAsync(target, check, cancellationToken).ConfigureAwait(false))
            {
                return new UpdateDownloadResult { Ok = true, Path = target, Size = check.Size, Sha256 = check.Sha256 };
            }

            using var response = await UpdateClient.GetAsync(
                installerUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.RequestMessage?.RequestUri is { } finalInstallerUri &&
                finalInstallerUri.Scheme != "https" &&
                !finalInstallerUri.IsLoopback)
            {
                return new UpdateDownloadResult
                {
                    Ok = false,
                    Error = "The installer redirected to a non-HTTPS host.",
                };
            }

            if (!response.IsSuccessStatusCode)
            {
                return new UpdateDownloadResult
                {
                    Ok = false,
                    Error = $"Installer download returned HTTP {(int)response.StatusCode}.",
                };
            }

            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken)
                             .ConfigureAwait(false))
            await using (var destination = new FileStream(
                             target, FileMode.Create, FileAccess.Write, FileShare.None, 81_920,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[81_920];
                long written = 0;
                long nextProgress = 0;
                while (true)
                {
                    var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    written += read;
                    if (written > check.Size)
                    {
                        destination.Close();
                        DeleteFileQuietly(target);
                        return new UpdateDownloadResult
                        {
                            Ok = false,
                            Error = "The installer is larger than the signed size.",
                        };
                    }

                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                        .ConfigureAwait(false);
                    if (Environment.TickCount64 >= nextProgress)
                    {
                        ReportUpdateProgress("downloading", check.AvailableVersion, written, check.Size);
                        nextProgress = Environment.TickCount64 + 200;
                    }
                }
            }

            var length = new FileInfo(target).Length;
            ReportUpdateProgress("verifying", check.AvailableVersion, length, check.Size);
            var hash = await Hashing.Sha256FileAsync(target, cancellationToken).ConfigureAwait(false);
            if (length != check.Size ||
                !hash.Equals(check.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                DeleteFileQuietly(target);
                return new UpdateDownloadResult
                {
                    Ok = false,
                    Error = "Installer size or SHA-256 did not match the signed manifest.",
                };
            }

            return new UpdateDownloadResult
            {
                Ok = true,
                Path = target,
                Size = length,
                Sha256 = hash,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            DeleteFileQuietly(target);
            throw;
        }
        catch (Exception ex)
        {
            DeleteFileQuietly(target);
            return new UpdateDownloadResult { Ok = false, Error = ex.Message };
        }
    }

    private static async Task<bool> MatchesManifestAsync(string path, UpdateCheckResult check, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length != check.Size) return false;
            var hash = await Hashing.Sha256FileAsync(path, cancellationToken).ConfigureAwait(false);
            return hash.Equals(check.Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Best effort. A package is of no use once its update has been applied or has failed, and each
    /// takes hundreds of MB. A folder the helper is still running from is left for next time.
    /// </summary>
    private void RemoveUpdatePackages(string? keepVersion = null)
    {
        try
        {
            var directory = Path.Combine(_paths.Root, "updates");
            if (!Directory.Exists(directory)) return;
            foreach (var stage in Directory.EnumerateDirectories(directory, "stage-*"))
            {
                if (Path.GetFileName(stage) == $"stage-{keepVersion}") continue;
                try { Directory.Delete(stage, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }

            foreach (var package in Directory.EnumerateFiles(directory, "installer-*.zip"))
            {
                if (Path.GetFileName(package) != $"installer-{keepVersion}.zip") DeleteFileQuietly(package);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public async Task<UpdateInstallResult> InstallUpdateAsync(
        UpdateInstallRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        if (!await _updateInstallGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return new UpdateInstallResult { Ok = false, Status = "busy", Error = "An update installation is already in progress." };
        try
        {
            if (_updateInstallScheduled)
                return new UpdateInstallResult { Ok = false, Status = "busy", Error = "An update is already staged for restart." };
            var result = await InstallUpdateCoreAsync(request, cancellationToken).ConfigureAwait(false);
            if (result.Ok) _updateInstallScheduled = true;
            else ReportUpdateProgress(result.Status == "busy" ? "postponed" : "failed",
                result.Version, error: result.Error);
            return result;
        }
        finally
        {
            lock (_sync)
            {
                if (!_updateInstallScheduled) _updateQuiescing = false;
            }
            _updateInstallGate.Release();
            if (!_updateInstallScheduled) Schedule();
        }
    }

    private async Task<UpdateInstallResult> InstallUpdateCoreAsync(
        UpdateInstallRequest? request,
        CancellationToken cancellationToken)
    {
        var install = InstalledBuild.TryLoad();
        if (install is null)
        {
            return new UpdateInstallResult
            {
                Ok = false,
                Status = "notInstalled",
                Error = "Portable and development builds do not auto-update.",
            };
        }

        // A Host started while the helper copies from the stage folder (the browser bridge can do
        // that) must not pull that folder out from under it.
        if (UpdateBeingInstalled())
        {
            return new UpdateInstallResult { Ok = false, Status = "busy", Error = "An update is already being installed." };
        }

        if (request?.Force != true)
        {
            bool active;
            lock (_sync)
            {
                active = _jobs.Values.Any(runtime =>
                    runtime.Starting ||
                    runtime.Job.State is DownloadState.Connecting or DownloadState.Downloading
                        or DownloadState.Verifying or DownloadState.RetryWaiting);
            }

            if (active)
            {
                return new UpdateInstallResult
                {
                    Ok = false,
                    Status = "busy",
                    Error = "Downloads are active. Use Install anyway or wait for them to finish.",
                };
            }
        }

        var check = await CheckForUpdatesAsync(cancellationToken).ConfigureAwait(false);
        if (!check.Ok || check.Status != "updateAvailable" ||
            string.IsNullOrWhiteSpace(check.AvailableVersion))
        {
            return new UpdateInstallResult
            {
                Ok = false,
                Status = check.Status,
                Error = check.Error ?? "No update is available to install.",
            };
        }

        // Use the exact verified manifest selected for this installation. A
        // second feed request can return another release during CDN propagation.
        var download = await DownloadVerifiedUpdateAsync(check, cancellationToken).ConfigureAwait(false);
        if (!download.Ok || string.IsNullOrWhiteSpace(download.Path))
        {
            return new UpdateInstallResult
            {
                Ok = false,
                Status = "downloadFailed",
                Error = download.Error ?? "The installer could not be downloaded.",
            };
        }

        lock (_sync)
        {
            // Downloads may have started while the update package was fetched.
            // Recheck and stop admitting new runners atomically with Schedule.
            if (request?.Force != true && _jobs.Values.Any(runtime =>
                    runtime.Starting || runtime.Job.State is DownloadState.Connecting or
                        DownloadState.Downloading or DownloadState.Verifying or DownloadState.RetryWaiting))
                return new UpdateInstallResult { Ok = false, Status = "busy", Error = "Downloads became active while the update was downloading." };
            _updateQuiescing = true;
        }
        RemoveUpdatePackages(keepVersion: check.AvailableVersion);
        var staging = Path.Combine(_paths.Root, "updates", $"stage-{check.AvailableVersion}");
        ReportUpdateProgress("staging", check.AvailableVersion, download.Size ?? 0, download.Size);
        try
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }

            Directory.CreateDirectory(staging);
            System.IO.Compression.ZipFile.ExtractToDirectory(download.Path, staging, overwriteFiles: true);
            var installer = Path.Combine(staging, "apply-update.ps1");
            if (!File.Exists(installer))
            {
                return new UpdateInstallResult
                {
                    Ok = false,
                    Status = "invalidPackage",
                    Error = "The update package has no apply-update.ps1.",
                    Version = check.AvailableVersion,
                };
            }

            var journal = UpdateJournal;
            journal.ClearForNewInstall();
            var resultPath = journal.ResultPath;
            var start = new System.Diagnostics.ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = staging,
            };
            foreach (var argument in new[]
                     {
                         "-NoProfile", "-ExecutionPolicy", "Bypass",
                         "-File", installer,
                         "-PackageDir", staging,
                         "-InstallRoot", install.InstallRoot,
                         "-Version", check.AvailableVersion,
                         "-ResultPath", resultPath,
                         "-HostPid", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                     })
            {
                start.ArgumentList.Add(argument);
            }

            using var helper = System.Diagnostics.Process.Start(start);
            if (helper is null)
            {
                return new UpdateInstallResult
                {
                    Ok = false,
                    Status = "launchFailed",
                    Error = "The update helper could not be started.",
                    Version = check.AvailableVersion,
                };
            }

            journal.MarkPending(check.AvailableVersion, helper.Id);
            ReportUpdateProgress("installing", check.AvailableVersion, download.Size ?? 0, download.Size);

            // The helper waits for this Host to exit, so one that has already gone never got going
            // and nothing will bring the app back. Staying up is the only way to keep it running.
            // The caller's token is not used: the helper is running now whatever the caller wants.
            if (await ExitsWithinAsync(helper, UpdateHelperStartGrace).ConfigureAwait(false))
            {
                var outcome = journal.TakeUnreported(_ => false);
                _log?.Invoke($"Update helper for {check.AvailableVersion} exited at once " +
                    $"with code {helper.ExitCode}; the Host stays up. {outcome?.Error}");
                return new UpdateInstallResult
                {
                    Ok = false,
                    Status = "launchFailed",
                    Error = outcome?.Error ?? "The update helper stopped as soon as it started.",
                    Version = check.AvailableVersion,
                };
            }

            _log?.Invoke(
                $"Update {check.AvailableVersion} staged; the Host will shut down for the installer.");
            return new UpdateInstallResult
            {
                Ok = true,
                Status = "scheduled",
                Version = check.AvailableVersion,
                ResultPath = resultPath,
            };
        }
        catch (Exception ex)
        {
            return new UpdateInstallResult
            {
                Ok = false,
                Status = "failed",
                Error = ex.Message,
                Version = check.AvailableVersion,
            };
        }
    }

    private UpdateInstallJournal UpdateJournal => new(Path.Combine(_paths.Root, "updates"));

    private static readonly TimeSpan UpdateHelperStartGrace = TimeSpan.FromSeconds(5);

    private static async Task<bool> ExitsWithinAsync(System.Diagnostics.Process process, TimeSpan time)
    {
        using var timeout = new CancellationTokenSource(time);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// What became of the update applied before this Host started, once. A success is told when
    /// that version is now installed, a failure only while that version is still not installed
    /// (Setup may have installed it since). A failure also shows in the update window.
    /// </summary>
    public UpdateInstallOutcome? TakeUpdateInstallOutcome()
    {
        UpdateInstallOutcome? outcome;
        try { outcome = UpdateJournal.TakeUnreported(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        var installed = InstalledBuild.TryLoad()?.Version;
        if (outcome is null || installed is null) return null;
        RemoveUpdatePackages();
        var stillMissing = IsNewerVersion(outcome.Version, installed);
        if (outcome.Succeeded) return stillMissing || IsNewerVersion(installed, outcome.Version) ? null : outcome;
        if (!stillMissing) return null;
        ReportUpdateProgress("failed", outcome.Version, error: outcome.Error);
        return outcome;
    }

    /// <summary>True while an automatic retry of a failed update would be too soon; the user can still install it.</summary>
    public bool UpdateFailedRecently(string version) => UpdateJournal.FailedRecently(version, TimeSpan.FromHours(24));

    /// <summary>True while the helper of an earlier Host is still applying an update.</summary>
    public bool UpdateBeingInstalled() => UpdateJournal.HelperStillRunning();

    public async Task<SettingsResult> DeferUpdateAsync(
        string version,
        CancellationToken cancellationToken = default)
    {
        await _settingsUpdateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await DeferUpdateCoreAsync(version, cancellationToken).ConfigureAwait(false); }
        finally { _settingsUpdateGate.Release(); }
    }

    private async Task<SettingsResult> DeferUpdateCoreAsync(string version, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return new SettingsResult { Ok = false, Error = "Version is required." };
        }

        _settings.SkippedUpdateVersion = version.Trim();
        await _settingsStore.SaveAsync(_settings, cancellationToken).ConfigureAwait(false);
        SettingsChanged?.Invoke(this, _settings);
        UpdateDeferred?.Invoke(this, _settings.SkippedUpdateVersion);
        return new SettingsResult { Ok = true, Settings = MapSettings(_settings) };
    }

    public void RequestUpdateShutdown() => UpdateShutdownRequested?.Invoke(this, EventArgs.Empty);

    private static bool IsNewerVersion(string candidate, string installed)
    {
        static string Clean(string value) => value.Trim().TrimStart('v', 'V');
        return Version.TryParse(Clean(candidate), out var candidateVersion) &&
               Version.TryParse(Clean(installed), out var installedVersion) &&
               candidateVersion > installedVersion;
    }

    public async Task<JobActionResponse> UpdateJobAsync(
        JobUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        await _settingsUpdateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _persistGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { return await UpdateJobCoreAsync(request, cancellationToken).ConfigureAwait(false); }
            finally { _persistGate.Release(); }
        }
        finally { _settingsUpdateGate.Release(); }
    }

    private async Task<JobActionResponse> UpdateJobCoreAsync(JobUpdateRequest request, CancellationToken cancellationToken)
    {
        JobRuntime? runtime;
        DownloadJob? beforeEdit = null;
        string? moveFrom = null;
        string? moveTo = null;
        lock (_sync)
        {
            if (!_jobs.TryGetValue(request.JobId, out runtime))
            {
                return new JobActionResponse { Ok = false, Error = "Job not found." };
            }

            beforeEdit = runtime.Job.Clone();
            string? categoryId = request.CategoryId;
            if (categoryId is not null)
            {
                if (Guid.TryParseExact(categoryId, "N", out var categoryGuid)) categoryId = categoryGuid.ToString("N");
                if (categoryId.Length > 0 && !FileCategories.IsBuiltIn(categoryId) &&
                    !_settings.CustomCategories.Any(category => category.Id == categoryId))
                    return new JobActionResponse { Ok = false, Error = "Category not found." };
                if (request.NewFilePath is not null)
                    return new JobActionResponse { Ok = false, Error = "Change category and Move/Rename in separate requests." };
            }

            if (request.CompletionOptions is { } completion)
            {
                if (runtime.Job.State == DownloadState.Completed || runtime.Job.CompletionActionsStarted)
                    return new JobActionResponse { Error = "Completion actions can be edited before the download completes." };
                if (request.NewFilePath is not null)
                    return new JobActionResponse { Error = "Change completion actions and Move/Rename in separate requests." };
                if (completion.SystemAction is not ("none" or "exit" or "shutdown" or "restart" or "logoff"))
                    return new JobActionResponse { Error = "Unknown completion system action." };
                if (completion.SystemAction != "none" && !_settings.AllowSystemCompletionActions)
                    return new JobActionResponse { Error = "Enable system completion actions in Scheduler before choosing this action." };
                if (completion.ProgramPath is { Length: > 0 } program &&
                    (!Path.IsPathFullyQualified(program) || !string.Equals(Path.GetExtension(program), ".exe", StringComparison.OrdinalIgnoreCase)))
                    return new JobActionResponse { Error = "Choose an absolute executable path for the completion program." };
            }

            if (request.Priority is { } priority)
            {
                if (!JobPriorities.IsValid(priority))
                    return new JobActionResponse { Error = "Choose a priority of High, Normal or Low." };
                if (runtime.Job.State == DownloadState.Completed)
                    return new JobActionResponse { Error = "This download has already finished, so its priority has no effect." };
                if (request.NewFilePath is not null)
                    return new JobActionResponse { Error = "Change the priority and Move/Rename in separate requests." };
            }

            if (request.SpeedLimitBytesPerSecond is < 0)
                return new JobActionResponse { Ok = false, Error = "The speed limit must be zero (unlimited) or a positive number of bytes per second." };
            if (request.NewFilePath is not null &&
                (request.SpeedLimitBytesPerSecond is not null || request.ShowCompletionDialog is not null))
                return new JobActionResponse { Ok = false, Error = "Update transfer settings and Move/Rename in separate requests." };
            if (request.SpeedLimitBytesPerSecond is not null &&
                (runtime.Job.IsTerminal || !SupportsLiveSpeedLimit(runtime.Job)))
                return new JobActionResponse { Ok = false, Error = "Live speed limits are available for unfinished HTTP/HTTPS file downloads." };
            if (request.SpeedLimitBytesPerSecond is not null)
                runtime.Job.SpeedLimitBytesPerSecond = request.SpeedLimitBytesPerSecond;
            if (request.ShowCompletionDialog is not null)
                runtime.Job.ShowCompletionDialog = request.ShowCompletionDialog;
            if (request.Priority is { } newPriority)
                runtime.Job.Priority = newPriority;

            if (request.Description is not null)
            {
                runtime.Job.Description = string.IsNullOrWhiteSpace(request.Description)
                    ? null
                    : request.Description.Trim();
            }

            if (request.NewFilePath is not null)
            {
                if (runtime.Job.State != DownloadState.Completed)
                {
                    return new JobActionResponse
                    {
                        Ok = false,
                        Error = "Move/Rename is available after the download completes.",
                    };
                }

                string target;
                try
                {
                    target = Path.GetFullPath(request.NewFilePath.Trim());
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
                {
                    return new JobActionResponse { Ok = false, Error = ex.Message };
                }

                var directory = Path.GetDirectoryName(target);
                if (string.IsNullOrWhiteSpace(directory))
                {
                    return new JobActionResponse { Ok = false, Error = "The new path has no folder." };
                }

                if (!string.Equals(target, runtime.Job.FinalFilePath, StringComparison.OrdinalIgnoreCase))
                {
                    if (File.Exists(target))
                    {
                        return new JobActionResponse { Ok = false, Error = "The target file already exists." };
                    }

                    if (!File.Exists(runtime.Job.FinalFilePath))
                    {
                        return new JobActionResponse { Ok = false, Error = "The completed file no longer exists." };
                    }

                    moveFrom = runtime.Job.FinalFilePath;
                    moveTo = target;
                }
            }

            if (categoryId is not null)
            {
                runtime.Job.CategoryIsAutomatic = categoryId.Length == 0;
                runtime.Job.CategoryId = categoryId.Length == 0
                    ? _settings.CustomCategories.FirstOrDefault(category => category.Extensions.Contains(
                        Path.GetExtension(runtime.Job.FileName).TrimStart('.'), StringComparer.OrdinalIgnoreCase))?.Id
                        ?? FileCategories.For(runtime.Job.FileName)
                    : FileCategories.IsBuiltIn(categoryId) ? categoryId.ToLowerInvariant() : categoryId;
                // Classification never relocates payloads or changes an active transfer's destination.
            }

            if (request.CompletionOptions is { } options)
                runtime.Job.CompletionOptions = new JobCompletionOptions { ProgramPath = options.ProgramPath,
                    OpenFile = options.OpenFile, SystemAction = options.SystemAction, ForceSystemAction = options.ForceSystemAction };

            runtime.Job.Touch();
        }

        if (moveTo is not null)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(moveTo)!);
                File.Move(moveFrom!, moveTo);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return new JobActionResponse { Ok = false, Error = ex.Message };
            }

            lock (_sync)
            {
                runtime.Job.FinalFilePath = moveTo;
                runtime.Job.FileName = Path.GetFileName(moveTo);
                runtime.Job.DestinationDirectory = Path.GetDirectoryName(moveTo)!;
                runtime.Job.PartFilePath = Path.Combine(
                    runtime.Job.DestinationDirectory,
                    $"{runtime.Job.FileName}.{runtime.Job.Id:N}.part");
                runtime.Job.Touch();
            }
        }

        try
        {
            await PersistAsync(runtime, cancellationToken,
                requireSuccess: request.CategoryId is not null || request.CompletionOptions is not null || request.Priority is not null,
                gateHeld: true).ConfigureAwait(false);
        }
        catch
        {
            lock (_sync)
            {
                if (request.CategoryId is not null)
                {
                    runtime.Job.CategoryIsAutomatic = beforeEdit!.CategoryIsAutomatic;
                    runtime.Job.CategoryId = beforeEdit.CategoryIsAutomatic
                        ? _settings.CustomCategories.FirstOrDefault(category => category.Extensions.Contains(
                            Path.GetExtension(runtime.Job.FileName).TrimStart('.'), StringComparer.OrdinalIgnoreCase))?.Id ?? FileCategories.For(runtime.Job.FileName)
                        : beforeEdit.CategoryId;
                }
                if (request.CompletionOptions is not null) runtime.Job.CompletionOptions = beforeEdit!.CompletionOptions;
                if (request.Description is not null) runtime.Job.Description = beforeEdit!.Description;
                if (request.SpeedLimitBytesPerSecond is not null) runtime.Job.SpeedLimitBytesPerSecond = beforeEdit!.SpeedLimitBytesPerSecond;
                if (request.ShowCompletionDialog is not null) runtime.Job.ShowCompletionDialog = beforeEdit!.ShowCompletionDialog;
                if (request.Priority is not null) runtime.Job.Priority = beforeEdit!.Priority;
                runtime.Job.Touch();
            }
            throw;
        }
        Raise(runtime, "updated");
        if (request.Priority is not null) Schedule();
        return new JobActionResponse { Ok = true, Job = Map(runtime.Job) };
    }

    public async Task<JobActionResponse> MoveToQueueAsync(
        Guid id,
        int queueId,
        CancellationToken cancellationToken = default)
    {
        await _settingsUpdateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await MoveToQueueCoreAsync(id, queueId, cancellationToken).ConfigureAwait(false); }
        finally { _settingsUpdateGate.Release(); }
    }

    private async Task<JobActionResponse> MoveToQueueCoreAsync(Guid id, int queueId, CancellationToken cancellationToken)
    {
        await _persistGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await MoveToQueueUnderPersistGateAsync(id, queueId, cancellationToken).ConfigureAwait(false); }
        finally { _persistGate.Release(); }
    }

    private async Task<JobActionResponse> MoveToQueueUnderPersistGateAsync(Guid id, int queueId, CancellationToken cancellationToken)
    {
        JobRuntime? runtime;
        DownloadQueue? queue;
        var paused = false;
        bool oldIsQueued;
        bool oldQueueProcessing;
        DateTimeOffset? oldQueueRetryAt;
        int oldQueueId;
        string? oldQueueName;
        lock (_sync)
        {
            if (!_jobs.TryGetValue(id, out runtime))
            {
                return new JobActionResponse { Ok = false, Error = "Job not found." };
            }

            queue = _settings.Queues.FirstOrDefault(q => q.Id == queueId);
            if (queue is null)
            {
                return new JobActionResponse { Ok = false, Error = "Queue not found." };
            }

            runtime.AdmissionSuspended = true;
            oldQueueProcessing = runtime.Job.QueueProcessing;
            oldQueueRetryAt = runtime.Job.QueueRetryAt;
            oldIsQueued = runtime.Job.IsQueued;
            runtime.Job.IsQueued = true;
            oldQueueId = runtime.Job.QueueId;
            oldQueueName = runtime.Job.QueueName;
            runtime.Job.QueueProcessing = true;
            runtime.Job.QueueRetryAt = null;
            runtime.Job.QueueId = queue.Id;
            runtime.Job.QueueName = queue.Name;
            runtime.Job.Touch();

            if (!queue.IsRunning && (runtime.Starting || runtime.Runner is { IsCompleted: false }))
            {
                runtime.PauseRequested = true;
                runtime.Job.State = DownloadState.Paused;
                runtime.Job.BytesPerSecond = null;
                runtime.Job.EstimatedRemaining = null;
                runtime.Job.ActiveConnections = 0;
                runtime.Job.Touch();
                runtime.Cts?.Cancel();
                paused = true;
            }
        }

        try { await PersistAsync(runtime, cancellationToken, requireSuccess: true, gateHeld: true).ConfigureAwait(false); }
        catch
        {
            lock (_sync)
            {
                runtime.Job.QueueProcessing = oldQueueProcessing;
                runtime.Job.QueueRetryAt = oldQueueRetryAt;
                runtime.Job.IsQueued = oldIsQueued;
                runtime.Job.QueueId = oldQueueId;
                runtime.Job.QueueName = oldQueueName;
                runtime.Job.Touch();
            }
            throw;
        }
        finally { lock (_sync) runtime.AdmissionSuspended = false; }
        lock (_sync) if (runtime.Job.State != DownloadState.Completed) _completionFiredQueues.Remove(queue.Id);
        Raise(runtime, paused ? "paused" : "updated");
        Schedule();
        return new JobActionResponse { Ok = true, Job = Map(runtime.Job) };
    }

    /// <param name="explicitDirect">The user chose a direct request for an address nobody could classify.</param>
    public async Task<JobActionResponse> RefreshUrlAsync(
        Guid id,
        string newUrl,
        bool explicitDirect = false,
        CancellationToken cancellationToken = default)
    {
        if (!TryValidateUrl(newUrl, out var uri, out var urlError))
        {
            return new JobActionResponse { Ok = false, Error = urlError };
        }

        await WaitForEndedRunAsync(id, cancellationToken).ConfigureAwait(false);

        // Asking may wait on the network, so never under the lock, and the job may have moved on
        // meanwhile. So everything is looked at again afterwards, with this answer kept.
        var addressAllowed = false;
        while (true)
        {
            JobRuntime? runtime;
            var askFirst = false;
            lock (_sync)
            {
                if (!_jobs.TryGetValue(id, out runtime))
                {
                    return new JobActionResponse { Ok = false, Error = "Job not found." };
                }

                if (runtime.Job.RemoteSource is not null)
                {
                    return new JobActionResponse
                    {
                        Ok = false,
                        Error = "This download gets its address from its provider, so it cannot be changed here.",
                        Job = Map(runtime.Job),
                    };
                }

                // The refresh dialog starts from the masked address. Accepting it unchanged
                // keeps the real one; any other masked address has nothing real behind it.
                var keepProtected = runtime.Job.ProtectedUrl is not null && newUrl == runtime.Job.Url;
                if (!keepProtected && ProviderLinks.IsMasked(newUrl))
                {
                    return new JobActionResponse
                    {
                        Ok = false,
                        Error = "This address has its key hidden, so it cannot download anything. Paste the original link.",
                        Job = Map(runtime.Job),
                    };
                }

                if (runtime.Job.State == DownloadState.Completed)
                {
                    return new JobActionResponse { Ok = false, Error = "Job is already complete.", Job = Map(runtime.Job) };
                }

                if (runtime.Starting || runtime.Runner is { IsCompleted: false })
                {
                    return new JobActionResponse
                    {
                        Ok = false,
                        Error = "Pause or stop the download before refreshing its address.",
                        Job = Map(runtime.Job),
                    };
                }

                // The address it already has stays usable, whatever it is: refusing that would strand a retry.
                if (!addressAllowed && DirectAddresses is not null && !keepProtected && !IsSameAddress(runtime.Job.Url, uri))
                {
                    askFirst = true;
                }
                else
                {
                    runtime.PauseRequested = false;
                    runtime.CancelRequested = false;
                    runtime.CancelledForRemoval = false;
                    // The saved login belongs to the site it came from, and the engine trusts whatever site
                    // the job now points at. A move to another site must leave it behind.
                    if (!Uri.TryCreate(runtime.Job.Url, UriKind.Absolute, out var previous) ||
                        previous.GetLeftPart(UriPartial.Authority) != uri.GetLeftPart(UriPartial.Authority))
                    {
                        runtime.Job.RequestHeaders.Remove("Cookie");
                        runtime.Job.RequestHeaders.Remove("Authorization");
                        runtime.Job.RequestHeaders.Remove("Proxy-Authorization");
                        runtime.Job.Referrer = null;
                    }

                    if (!keepProtected)
                    {
                        var (jobUrl, _, protectedUrl) = SplitOffProviderLink(uri.AbsoluteUri, null);
                        runtime.Job.Url = jobUrl;
                        runtime.Job.ProtectedUrl = protectedUrl;
                    }

                    runtime.Job.ResolvedUrl = null;
                    runtime.Job.FinalUrl = null;
                    runtime.Job.State = DownloadState.Queued;
                    runtime.Job.StartMode = StartMode.StartNow;
                    runtime.Job.Failure = null;
                    runtime.Job.CompletedAt = null;
                    runtime.Job.AutoResumeAfterRestart = true;
                    runtime.Job.Touch();
                }
            }

            if (askFirst)
            {
                if (await DirectAddresses!.WhyNotDirectAsync(newUrl, explicitDirect, cancellationToken).ConfigureAwait(false) is { } refusal)
                {
                    return new JobActionResponse { Ok = false, Error = refusal, Job = GetJob(id) };
                }

                addressAllowed = true;
                continue;
            }

            await PersistAsync(runtime, cancellationToken).ConfigureAwait(false);
            Raise(runtime, "refreshed");
            Schedule();
            return new JobActionResponse { Ok = true, Job = Map(runtime.Job) };
        }
    }

    private static bool IsSameAddress(string current, Uri next)
        => current == next.OriginalString || current == next.AbsoluteUri ||
           (Uri.TryCreate(current, UriKind.Absolute, out var parsed) && parsed.AbsoluteUri == next.AbsoluteUri);

    /// <summary>
    /// Evaluates the configured one-time/daily queue schedules against a local
    /// timestamp. Public so tests can drive time deterministically; the host calls
    /// it from its 30-second scheduler loop with <see cref="TimeProvider.GetLocalNow"/>.
    /// </summary>
    private static bool MatchesDay(string days, DayOfWeek day)
    {
        if (string.IsNullOrWhiteSpace(days))
        {
            return true;
        }

        var token = day.ToString()[..3];
        return days
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(value => value.Length >= 3 &&
                          value[..3].Equals(token, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryParseScheduleTime(string? value, out TimeOnly time)
    {
        if (!string.IsNullOrWhiteSpace(value) &&
            TimeOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out time))
        {
            return true;
        }

        time = default;
        return false;
    }

    private void StartSchedulerLoop()
    {
        if (!_enableSchedulerTimer || _schedulerCts is not null)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _schedulerCts = cts;
        _schedulerTask = Task.Run(() => SchedulerLoopAsync(cts.Token), CancellationToken.None);
    }

    private async Task SchedulerLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await EvaluateSchedulesAsync(_timeProvider.GetLocalNow(), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Scheduler startup evaluation failed: {ex.Message}");
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await EvaluateSchedulesAsync(_timeProvider.GetLocalNow(), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _log?.Invoke($"Scheduler tick failed: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        List<JobRuntime> active;
        Task packageCheck;
        var schedulerTask = _schedulerTask;
        _schedulerCts?.Cancel();
        lock (_sync)
        {
            _shuttingDown = true;
            packageCheck = _packageCheckWorker;
            active = _jobs.Values.Where(r => r.Starting || r.Runner is { IsCompleted: false }).ToList();
            foreach (var runtime in active)
            {
                runtime.PauseRequested = true;
                runtime.Cts?.Cancel();
            }
        }

        try
        {
            _sleepPrevention.SetActive(false);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Could not release sleep prevention: {ex.Message}");
        }

        _sleepPreventionActive = false;

        foreach (var runtime in active)
        {
            if (runtime.Runner is not null)
            {
                try
                {
                    await Task.WhenAny(runtime.Runner, Task.Delay(TimeSpan.FromSeconds(10), cancellationToken))
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // continue
                }
            }

            lock (_sync)
            {
                if (!runtime.Job.IsTerminal)
                {
                    runtime.Job.State = DownloadState.Paused;
                    runtime.Job.BytesPerSecond = null;
                    runtime.Job.EstimatedRemaining = null;
                    runtime.Job.ActiveConnections = 0;
                    runtime.Job.Touch();
                }
            }

            await PersistAsync(runtime, CancellationToken.None).ConfigureAwait(false);
        }

        // A check still running finishes its save; one that never ran is picked up by the next start.
        try { await Task.WhenAny(packageCheck, Task.Delay(TimeSpan.FromSeconds(5), cancellationToken)).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* continue */ }

        if (schedulerTask is not null)
        {
            try
            {
                await Task.WhenAny(schedulerTask, Task.Delay(TimeSpan.FromSeconds(2), cancellationToken))
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // shutdown is best effort
            }
        }

        _schedulerCts?.Dispose();
        _schedulerCts = null;
        _schedulerTask = null;
    }

    public const string OrphanQuarantineFolderName = ".dm-orphans";

    /// <summary>
    /// Rescans for unattached <c>.part</c> files and either deletes them or moves them
    /// into a <see cref="OrphanQuarantineFolderName"/> folder next to where they were found.
    /// Files that became attached to a job since the last scan are never touched.
    /// </summary>
    public Task<CleanOrphansResponse> CleanOrphanPartFilesAsync(bool delete, CancellationToken cancellationToken)
    {
        ScanOrphanPartFiles();
        List<string> candidates;
        lock (_sync)
        {
            candidates = _orphanPartFiles.ToList();
        }

        var response = new CleanOrphansResponse { Ok = true };
        foreach (var path in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // Hold the job lock across the check and the move/delete so a job added or
                // re-pointed concurrently can never have its part file taken away.
                lock (_sync)
                {
                    if (AttachedPartPathsLocked().Contains(NormalizePath(path)) || !File.Exists(path))
                    {
                        continue;
                    }

                    CleanOrphanLocked(path, delete);
                }

                response.Processed.Add(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                response.Failed.Add(path);
                _log?.Invoke($"Could not clean orphan partial file '{path}': {ex.Message}");
            }
        }

        lock (_sync)
        {
            _orphanPartFiles.RemoveAll(response.Processed.Contains);
        }

        response.Ok = response.Failed.Count == 0;
        if (!response.Ok)
        {
            response.Error = $"{response.Failed.Count} orphan file(s) could not be cleaned";
        }

        return Task.FromResult(response);
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    private HashSet<string> AttachedPartPathsLocked()
        => _jobs.Values
            .Select(runtime => runtime.Job.PartFilePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => NormalizePath(path!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private void CleanOrphanLocked(string path, bool delete)
    {
        if (delete)
        {
            File.Delete(path);
            _log?.Invoke($"Deleted orphan partial file: {path}");
            return;
        }

        var folder = Path.Combine(Path.GetDirectoryName(path)!, OrphanQuarantineFolderName);
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, Path.GetFileName(path));
        var counter = 1;
        while (File.Exists(target))
        {
            target = Path.Combine(folder,
                $"{Path.GetFileNameWithoutExtension(path)} ({counter++}){Path.GetExtension(path)}");
        }

        File.Move(path, target);
        _log?.Invoke($"Quarantined orphan partial file: {path} -> {target}");
    }

    private void ScanOrphanPartFiles()
    {
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        HashSet<string> known;
        lock (_sync)
        {
            foreach (var runtime in _jobs.Values)
            {
                if (!string.IsNullOrWhiteSpace(runtime.Job.DestinationDirectory))
                {
                    directories.Add(runtime.Job.DestinationDirectory);
                }
            }

            known = AttachedPartPathsLocked();
        }

        if (!string.IsNullOrWhiteSpace(_settings.DefaultDownloadDirectory))
        {
            directories.Add(_settings.DefaultDownloadDirectory);
        }

        if (!string.IsNullOrWhiteSpace(_settings.LastDestinationDirectory))
        {
            directories.Add(_settings.LastDestinationDirectory);
        }

        foreach (var folder in _settings.CategoryFolders.Values)
        {
            if (!string.IsNullOrWhiteSpace(folder))
            {
                directories.Add(folder);
            }
        }

        lock (_sync)
        {
            _orphanPartFiles.Clear();
        }

        foreach (var directory in directories)
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            try
            {
                foreach (var path in Directory.EnumerateFiles(
                             directory, "*.part", SearchOption.TopDirectoryOnly))
                {
                    if (known.Contains(NormalizePath(path)))
                    {
                        continue;
                    }

                    lock (_sync)
                    {
                        _orphanPartFiles.Add(path);
                    }

                    _log?.Invoke($"Orphan partial file detected (not attached to a job): {path}");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log?.Invoke($"Could not scan '{directory}' for orphan partial files: {ex.Message}");
            }
        }
    }

    private async Task<bool> ReconcileAsync(JobRuntime runtime, CancellationToken cancellationToken)
    {
        var changed = false;
        var job = runtime.Job;
        changed |= await ProtectStoredProviderLinksAsync(job, cancellationToken).ConfigureAwait(false);
        if (job.ReceivedBytes < 0)
        {
            job.ReceivedBytes = 0;
            changed = true;
        }

        // Completed payloads have already been atomically renamed. Their absent
        // part file is expected and must not erase recorded download bytes.
        if (job.State == DownloadState.Completed)
        {
            if (!File.Exists(job.FinalFilePath))
            {
                // Deleted on purpose after a verified extraction, so nothing has gone wrong with this download.
                if (job.FileRemovedAfterExtraction)
                {
                    await DeleteCheckpointQuietlyAsync(job.Id).ConfigureAwait(false);
                    return changed;
                }

                job.State = DownloadState.Failed;
                job.Failure = new JobFailure(FailureKind.DestinationUnavailable,
                    "The completed file is missing from its destination.", false, false, DateTimeOffset.UtcNow);
                return true;
            }
            if (job.ReceivedBytes == 0)
            {
                job.ReceivedBytes = new FileInfo(job.FinalFilePath).Length;
                changed = job.ReceivedBytes != 0 || changed;
            }

            // A kill between saving this job and deleting its checkpoint leaves one behind.
            await DeleteCheckpointQuietlyAsync(job.Id).ConfigureAwait(false);
            return changed;
        }

        DownloadCheckpoint? checkpoint = null;
        try
        {
            checkpoint = await _checkpointStore.LoadAsync(job.Id, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The run reads it again before it starts, and won't go on without it.
            _log?.Invoke($"Could not read the checkpoint of job {job.Id} at startup: {ex.GetType().Name}");
        }

        if (checkpoint is not null && TryAdoptFinishedFile(job, checkpoint))
        {
            await DeleteCheckpointQuietlyAsync(job.Id).ConfigureAwait(false);
            return true;
        }

        if (checkpoint is not null)
        {
            if (checkpoint.TotalBytes is > 0 && job.TotalBytes != checkpoint.TotalBytes)
            {
                job.TotalBytes = checkpoint.TotalBytes;
                changed = true;
            }

            if (checkpoint.CompletedBytes != job.ReceivedBytes)
            {
                job.ReceivedBytes = checkpoint.CompletedBytes;
                changed = true;
            }

            job.ETag ??= checkpoint.ETag;
            job.LastModified ??= checkpoint.LastModified;
            job.ContentType ??= checkpoint.ContentType;
        }

        var partLength = File.Exists(job.PartFilePath)
            ? new FileInfo(job.PartFilePath).Length
            : (long?)null;
        if (checkpoint is not null &&
            // Segmented files are preallocated and contain completed ranges at
            // noncontiguous offsets. Their aggregate byte count is not a safe
            // truncation offset; the engine validates their layout on resume.
            checkpoint.Segments.Count <= 1 &&
            partLength is { } longPartLength &&
            longPartLength > checkpoint.CompletedBytes)
        {
            try
            {
                using (var stream = new FileStream(
                           job.PartFilePath, FileMode.Open, FileAccess.Write, FileShare.Read))
                {
                    stream.SetLength(checkpoint.CompletedBytes);
                }

                job.ReceivedBytes = checkpoint.CompletedBytes;
                changed = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // If the extra bytes cannot be removed safely, discard the
                // metadata claim rather than resuming over unknown data.
                job.ReceivedBytes = 0;
                changed = true;
            }
        }

        if (job.ReceivedBytes > 0 && partLength is null)
        {
            // The partial payload is gone. Resume will start from zero rather than
            // pretend the missing bytes are present.
            job.ReceivedBytes = 0;
            changed = true;
        }
        else if (partLength is { } length && job.ReceivedBytes > length)
        {
            // A truncated or externally shortened part file cannot support the
            // metadata byte count; clamp it so resume never trusts phantom bytes.
            job.ReceivedBytes = length;
            changed = true;
        }

        if (job.State is DownloadState.Connecting or DownloadState.Downloading or DownloadState.RetryWaiting or DownloadState.Verifying)
        {
            job.State = job.AutoResumeAfterRestart && _settings.AutoResumeUnfinishedOnStartup
                ? DownloadState.Queued
                : DownloadState.Paused;
            changed = true;
        }

        if (job.State == DownloadState.Completed)
        {
            if (!File.Exists(job.FinalFilePath))
            {
                job.State = DownloadState.Failed;
                job.Failure = new JobFailure(FailureKind.DestinationUnavailable,
                    "The completed file is missing from its destination.", false, false, DateTimeOffset.UtcNow);
                changed = true;
            }
        }

        if (job.State == DownloadState.Cancelled && job.DeleteFileOnCancel && File.Exists(job.PartFilePath))
        {
            DeleteFileQuietly(job.PartFilePath);
            await _checkpointStore.DeleteAsync(job.Id, cancellationToken).ConfigureAwait(false);
        }

        return changed;
    }

    /// <summary>
    /// The process died after the final rename and before the job was saved as completed. Without
    /// this the job downloads the whole file again and leaves a second copy beside the first.
    /// </summary>
    private bool TryAdoptFinishedFile(DownloadJob job, DownloadCheckpoint checkpoint)
    {
        if (checkpoint.FinalizingTo is not { } finalPath ||
            job.State == DownloadState.Cancelled ||
            File.Exists(job.PartFilePath))
        {
            return false;
        }

        var final = new FileInfo(finalPath);
        if (!final.Exists || final.Length != checkpoint.CompletedBytes)
        {
            return false;
        }

        job.State = DownloadState.Completed;
        job.Failure = null;
        job.CompletedAt = _timeProvider.GetUtcNow();
        job.FinalFilePath = finalPath;
        job.FileName = final.Name;
        job.ReceivedBytes = final.Length;
        job.TotalBytes = final.Length;
        job.ETag = checkpoint.ETag ?? job.ETag;
        job.LastModified = checkpoint.LastModified ?? job.LastModified;
        job.ContentType = checkpoint.ContentType ?? job.ContentType;
        _log?.Invoke($"Job {job.Id}: its file was already in place after a restart, so it was not downloaded again.");
        return true;
    }

    private async Task DeleteCheckpointQuietlyAsync(Guid jobId)
    {
        try { await _checkpointStore.DeleteAsync(jobId, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"Could not delete the checkpoint of job {jobId}: {ex.Message}");
        }
    }

    private void ResetQuotaWindowsLocked(DateTimeOffset now)
    {
        foreach (var queue in _settings.Queues)
        {
            ResetQuotaWindowLocked(queue, now);
        }
    }

    private static void ResetQuotaWindowLocked(DownloadQueue queue, DateTimeOffset now)
    {
        if (!queue.QuotaEnabled)
        {
            return;
        }

        if (queue.QuotaWindowStart is null ||
            now - queue.QuotaWindowStart.Value >= TimeSpan.FromHours(queue.QuotaWindowHours))
        {
            queue.QuotaWindowStart = now;
            queue.QuotaBytesUsed = 0;
        }
    }

    private bool AddQuotaUsageLocked(JobRuntime runtime, long received)
    {
        var queue = runtime.Job.IsQueued ? _settings.Queues.FirstOrDefault(q => q.Id == runtime.Job.QueueId) : null;
        if (queue is null || !queue.QuotaEnabled)
        {
            return false;
        }

        var now = _timeProvider.GetUtcNow();
        ResetQuotaWindowLocked(queue, now);
        var delta = received - runtime.QuotaReportedBytes;
        if (delta > 0)
        {
            queue.QuotaBytesUsed += delta;
        }

        runtime.QuotaReportedBytes = received;
        if (!queue.IsRunning || queue.QuotaBytesUsed < queue.QuotaBytes)
        {
            return false;
        }

        queue.IsRunning = false;
        foreach (var active in _jobs.Values.Where(r =>
                     r.Job.IsQueued && r.Job.QueueId == queue.Id &&
                     (r.Starting || r.Runner is { IsCompleted: false })))
        {
            active.PauseRequested = true;
            active.Job.State = DownloadState.Paused;
            active.Job.BytesPerSecond = null;
            active.Job.EstimatedRemaining = null;
            active.Job.ActiveConnections = 0;
            active.Job.Touch();
            active.Cts?.Cancel();
        }

        return true;
    }

    private async Task SaveSettingsQuietlyAsync()
    {
        await _settingsUpdateGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _settingsStore.SaveAsync(_settings, CancellationToken.None).ConfigureAwait(false);
            SettingsChanged?.Invoke(this, _settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"Could not persist quota state: {ex.Message}");
        }
        finally { _settingsUpdateGate.Release(); }
    }

    private void Schedule()
    {
        List<JobRuntime> toStart;
        lock (_sync)
        {
            if (!_initialized || _shuttingDown || _updateQuiescing)
            {
                return;
            }

            ResetQuotaWindowsLocked(_timeProvider.GetUtcNow());
            var running = _jobs.Values
                .Where(r => r.Starting || r.Runner is { IsCompleted: false })
                .ToList();
            var activeTotal = running.Count;
            var slots = _settings.MaxConcurrentDownloads - activeTotal;
            if (slots <= 0)
            {
                return;
            }

            var queueDefaults = _settings.Queues.FirstOrDefault(q => q.Id == _settings.DefaultQueueId)
                                ?? _settings.Queues[0];
            var perQueueActive = running
                .GroupBy(r => r.Job.IsQueued ? r.Job.QueueId : -1)
                .ToDictionary(g => g.Key, g => g.Count());

            toStart = new List<JobRuntime>();
            var fileOrder = _settings.Queues
                .SelectMany(queue => (queue.JobOrder ?? new()).Select((id, index) =>
                    (QueueId: queue.Id, JobId: id, Index: index)))
                .GroupBy(item => (item.QueueId, item.JobId))
                .ToDictionary(group => group.Key, group => group.First().Index);
            foreach (var runtime in _jobs.Values
                         .Where(r => !r.AdmissionSuspended && !r.Starting && (r.Runner is null || r.Runner.IsCompleted) &&
                                     r.Job.State == DownloadState.Queued &&
                                     r.Job.StartMode != StartMode.Later)
                         .OrderByDescending(r => r.Job.Priority)
                         .ThenBy(r => fileOrder.GetValueOrDefault((r.Job.QueueId, r.Job.Id), int.MaxValue))
                         .ThenBy(r => r.Job.CreatedAt))
            {
                if (slots <= 0)
                {
                    break;
                }

                var queue = runtime.Job.IsQueued ? _settings.Queues.FirstOrDefault(q => q.Id == runtime.Job.QueueId) ?? queueDefaults
                    : new DownloadQueue { Id = -1, IsRunning = true, MaxConcurrentDownloads = _settings.MaxConcurrentDownloads };
                if (!queue.IsRunning)
                {
                    continue;
                }

                if (queue.QuotaEnabled && queue.QuotaBytesUsed >= queue.QuotaBytes)
                {
                    queue.IsRunning = false;
                    continue;
                }

                var queueActive = perQueueActive.GetValueOrDefault(queue.Id);
                if (queueActive >= queue.MaxConcurrentDownloads)
                {
                    continue;
                }

                toStart.Add(runtime);
                perQueueActive[queue.Id] = queueActive + 1;
                slots--;
            }

            foreach (var runtime in toStart)
            {
                runtime.Starting = true;
            }
        }

        foreach (var runtime in toStart)
        {
            runtime.Runner = RunJobAsync(runtime);
        }

        UpdateSleepPrevention();
    }

    private void UpdateSleepPrevention()
    {
        bool active;
        lock (_sync)
        {
            active = _settings.PreventSleepWhileDownloading &&
                     _jobs.Values.Any(r =>
                         r.Starting ||
                         r.Job.State is DownloadState.Connecting or DownloadState.Downloading
                             or DownloadState.RetryWaiting or DownloadState.Verifying);
            if (active == _sleepPreventionActive)
            {
                return;
            }

            _sleepPreventionActive = active;
        }

        try
        {
            _sleepPrevention.SetActive(active);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Could not update sleep prevention: {ex.Message}");
        }
    }

    private async Task RunJobAsync(JobRuntime runtime)
    {
        var job = runtime.Job;
        CancellationTokenSource? cts = null;
        lock (_sync)
        {
            // Schedule picks a job and calls this after releasing the lock, and a cancel in that
            // gap finds no transfer to stop. Going on would undo it and run the whole download.
            if (runtime.CancelRequested)
            {
                job.State = DownloadState.Cancelled;
                job.Touch();
                runtime.Starting = false;
            }
            else
            {
                if (runtime.PauseRequested || _shuttingDown)
                {
                    if (runtime.ResumeWhenStopped && !_shuttingDown)
                    {
                        runtime.PauseRequested = false;
                        runtime.ResumeWhenStopped = false;
                    }
                    else
                    {
                        job.State = DownloadState.Paused;
                        job.BytesPerSecond = null;
                        job.EstimatedRemaining = null;
                        job.ActiveConnections = 0;
                        job.Touch();
                        runtime.Starting = false;
                        return;
                    }
                }

                runtime.Cts?.Dispose();
                cts = new CancellationTokenSource();
                runtime.Cts = cts;
                job.KeepCheckpointAfterFinalize = true;
                job.State = DownloadState.Connecting;
                job.Failure = null;
                job.StartedAt ??= DateTimeOffset.UtcNow;
                job.CompletedAt = null;
                job.FileRemovedAfterExtraction = false;
                job.ActiveConnections = 0;
                job.Touch();
            }
        }

        if (cts is null)
        {
            // The slot this job held is free again.
            Schedule();
            return;
        }

        await PersistAsync(runtime, CancellationToken.None).ConfigureAwait(false);
        Raise(runtime, "updated");

        var lastProgressEvent = DateTimeOffset.MinValue;
        var lastProgressSave = DateTimeOffset.MinValue;
        var progress = new InlineProgress<TransferProgress>(p =>
        {
            JobDto? dto = null;
            var quotaReached = false;
            var now = DateTimeOffset.UtcNow;
            lock (_sync)
            {
                if (job.State is DownloadState.Paused or DownloadState.Cancelled || runtime.CancelledForRemoval)
                {
                    return;
                }

                job.ReceivedBytes = p.ReceivedBytes;
                if (p.TotalBytes is not null)
                {
                    job.TotalBytes = p.TotalBytes;
                }

                job.BytesPerSecond = p.BytesPerSecond;
                job.EstimatedRemaining = p.EstimatedRemaining;
                job.ActiveConnections = p.ActiveConnections;
                // Follow the engine's phase so a job waiting between retries (or still
                // connecting) is not shown as downloading at 0 B/s.
                if (job.State is DownloadState.Connecting or DownloadState.Queued or DownloadState.RetryWaiting
                    or DownloadState.Downloading)
                {
                    job.State = p.Phase switch
                    {
                        var phase when phase.StartsWith("Retrying", StringComparison.Ordinal) => DownloadState.RetryWaiting,
                        "Connecting" when p.BytesPerSecond <= 0 => DownloadState.Connecting,
                        _ => DownloadState.Downloading,
                    };
                }

                quotaReached = AddQuotaUsageLocked(runtime, p.ReceivedBytes);
                job.Touch();
                if (now - lastProgressEvent >= TimeSpan.FromMilliseconds(300))
                {
                    lastProgressEvent = now;
                    dto = Map(job);
                }
            }

            if (quotaReached)
            {
                _log?.Invoke($"Queue {job.QueueId} reached its download quota; pausing.");
                _ = SaveSettingsQuietlyAsync();
            }

            if (dto is not null)
            {
                JobChanged?.Invoke(this, new JobChangeEventArgs(dto, "progress"));
            }

            if (now - lastProgressSave >= TimeSpan.FromSeconds(5))
            {
                lastProgressSave = now;
                _ = PersistQuietlyAsync(runtime);
            }
        });

        DownloadResult result;
        try
        {
            var engine = job.Url.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase)
                ? _ftpEngine
                : _engine;
            if (engine is null)
            {
                throw new InvalidOperationException("FTP support is not configured.");
            }

            result = await ExecuteWithLinksAsync(runtime, engine, progress, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            result = DownloadResult.Fail(DownloadState.Cancelled,
                new JobFailure(FailureKind.Cancelled, "The download was cancelled.", false, true, DateTimeOffset.UtcNow),
                job.ReceivedBytes, job.TotalBytes);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Job {job.Id} crashed: {UrlRedactor.RedactText(ex.ToString())}");
            result = DownloadResult.Fail(DownloadState.Failed,
                new JobFailure(FailureKind.Unknown, UrlRedactor.RedactText(ex.Message), false, true, DateTimeOffset.UtcNow),
                job.ReceivedBytes, job.TotalBytes);
        }

        var deletePayload = false;
        var deleteFinal = false;
        string changeType;
        lock (_sync)
        {
            // A provider's link is for one run; a later run asks for a fresh one.
            job.ResolvedUrl = null;
            job.ReceivedBytes = result.BytesWritten;
            if (result.TotalBytes is not null)
            {
                job.TotalBytes = result.TotalBytes;
            }

            job.BytesPerSecond = null;
            job.EstimatedRemaining = null;
            job.ActiveConnections = 0;
            job.ActiveConnections = 0;

            // The file is where it landed, whatever else happened while it got there.
            if (result.Success)
            {
                job.FinalFilePath = result.FinalFilePath ?? job.FinalFilePath;
                job.FileName = Path.GetFileName(job.FinalFilePath);
            }

            // A file that is finished stays finished unless the user wanted it gone: a stop that
            // arrives just after the rename must not leave it looking like one to download again.
            if (runtime.CancelRequested && (!result.Success || runtime.DeleteOnCancel || runtime.CancelledForRemoval))
            {
                job.State = DownloadState.Cancelled;
                job.Failure = new JobFailure(FailureKind.Cancelled, "Cancelled by the user.", false, true, DateTimeOffset.UtcNow);
                deletePayload = runtime.DeleteOnCancel;
                deleteFinal = result.Success && runtime.DeleteOnCancel && !runtime.CancelledForRemoval;
                changeType = "cancelled";
            }
            else if (result.Success)
            {
                job.State = DownloadState.Completed;
                job.Failure = null;
                job.CompletedAt = DateTimeOffset.UtcNow;
                job.CancelledByPackageId = null;
                job.ETag = result.ETag ?? job.ETag;
                job.LastModified = result.LastModified ?? job.LastModified;
                job.ContentType = result.ContentType ?? job.ContentType;
                runtime.CancelRequested = false;
                runtime.PauseRequested = false;
                runtime.ResumeWhenStopped = false;
                changeType = "completed";
            }
            else if (runtime.PauseRequested || _shuttingDown)
            {
                if (runtime.ResumeWhenStopped && !_shuttingDown)
                {
                    runtime.PauseRequested = false;
                    runtime.ResumeWhenStopped = false;
                    job.State = DownloadState.Queued;
                    job.Failure = null;
                    changeType = "queued";
                }
                else
                {
                    job.State = DownloadState.Paused;
                    job.Failure = null;
                    changeType = "paused";
                }
            }
            else
            {
                job.State = DownloadState.Failed;
                job.Failure = result.Failure ?? new JobFailure(FailureKind.Unknown,
                    "The download failed for an unknown reason.", false, true, DateTimeOffset.UtcNow);
                var queue = _settings.Queues.FirstOrDefault(item => item.Id == job.QueueId);
                // A failed check means the whole file arrived and was wrong. Repeating that
                // unattended can spend the same gigabytes all night and still end here.
                if (job.Failure.Kind != FailureKind.VerificationFailed &&
                    job.IsQueued && job.QueueProcessing && queue is { IsRunning: true } && !job.IsTerminal &&
                    (queue.RetryLimit is null || job.QueueRetryCount < queue.RetryLimit))
                {
                    job.QueueRetryCount++;
                    job.QueueRetryAt = _timeProvider.GetUtcNow().AddSeconds(30);
                }
                changeType = "failed";
            }

            job.Touch();
        }

        if (deletePayload)
        {
            DeleteFileQuietly(job.PartFilePath);
            if (deleteFinal) DeleteFileQuietly(job.FinalFilePath);
            await DeleteCheckpointQuietlyAsync(job.Id).ConfigureAwait(false);
        }

        if (changeType == "completed")
        {
            try
            {
                await PersistAsync(runtime, CancellationToken.None, requireSuccess: true).ConfigureAwait(false);
                // Only now: until the job says completed, this is what lets a restart recognise the file.
                await _checkpointStore.DeleteAsync(job.Id, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or InvalidOperationException)
            {
                // Already logged if it was the save, and the next start recovers from the checkpoint.
            }
        }
        else
        {
            await PersistAsync(runtime, CancellationToken.None).ConfigureAwait(false);
        }

        Raise(runtime, changeType);
        if (changeType == "completed") RequestPackageCheck();

        var lateResume = false;
        lock (_sync)
        {
            runtime.Starting = false;
            runtime.Runner = null;
            runtime.Cts?.Dispose();
            runtime.Cts = null;
            runtime.CancelledForRemoval = false;

            // A queue start can land after the final state was decided above but while the
            // runner still looked alive; it then only set ResumeWhenStopped. Honor it here so
            // the job is not left paused forever.
            if (runtime.ResumeWhenStopped)
            {
                runtime.ResumeWhenStopped = false;
                if (job.State == DownloadState.Paused && !_shuttingDown && !runtime.CancelRequested)
                {
                    runtime.PauseRequested = false;
                    job.State = DownloadState.Queued;
                    job.StartMode = StartMode.StartNow;
                    job.Failure = null;
                    job.Touch();
                    lateResume = true;
                }
            }
        }

        if (lateResume)
        {
            await PersistAsync(runtime, CancellationToken.None).ConfigureAwait(false);
            Raise(runtime, "queued");
        }

        if (await BeginCompletionActionsAsync(runtime, changeType).ConfigureAwait(false))
        {
            await MaybeRunPostDownloadHookAsync(job, changeType).ConfigureAwait(false);
            if (job.CompletionOptions.SystemAction != "none")
            {
                await MaybeRunCompletionActionsAsync(job, changeType, CancellationToken.None, skipSystemAction: true).ConfigureAwait(false);
                await RunJobCompletionActionsAsync(job).ConfigureAwait(false);
            }
            else
            {
                await RunJobCompletionActionsAsync(job).ConfigureAwait(false);
                await MaybeRunCompletionActionsAsync(job, changeType, CancellationToken.None).ConfigureAwait(false);
            }
        }
        Schedule();
    }

    private async Task MaybeRunPostDownloadHookAsync(DownloadJob job, string changeType)
    {
        if (changeType != "completed")
        {
            return;
        }

        var command = job.CompletionOptions.ProgramPath ?? _settings.PostDownloadCommand;
        if (string.IsNullOrWhiteSpace(command))
        {
            return;
        }

        if (!File.Exists(job.FinalFilePath))
        {
            _log?.Invoke($"Post-download hook skipped: '{job.FinalFilePath}' does not exist.");
            return;
        }

        try
        {
            await _postDownloadHook.RunAsync(command, job.FinalFilePath, CancellationToken.None)
                .ConfigureAwait(false);
            _log?.Invoke($"Post-download hook ran for '{job.FileName}'.");
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Post-download hook failed for '{job.FileName}': {ex.Message}");
        }
    }

    private async Task MaybeRunCompletionActionsAsync(
        DownloadJob job,
        string changeType,
        CancellationToken cancellationToken, bool skipSystemAction = false)
    {
        if (changeType != "completed" || !job.IsQueued)
        {
            return;
        }

        ScheduleRule? schedule;
        bool allowSystemActions;
        bool blockedByOtherDownloads;
        lock (_sync)
        {
            if (_completionFiredQueues.Contains(job.QueueId))
            {
                return;
            }

            schedule = _settings.Schedules.FirstOrDefault(s =>
                s.Enabled &&
                s.QueueId == job.QueueId &&
                (s.OpenFileWhenDone || s.ExitWhenDone || s.ShutdownWhenDone));
            if (schedule is null)
            {
                return;
            }

            var queueUnfinished = _jobs.Values.Any(r =>
                r.Job.Id != job.Id &&
                r.Job.IsQueued && r.Job.QueueId == job.QueueId &&
                (r.Job.QueueRetryAt is not null || r.Job.State is not (DownloadState.Completed or DownloadState.Failed or DownloadState.Cancelled)));
            if (queueUnfinished)
            {
                return;
            }

            _completionFiredQueues.Add(job.QueueId);
            allowSystemActions = _settings.AllowSystemCompletionActions;
            blockedByOtherDownloads = _jobs.Values.Any(r =>
                r.Job.Id != job.Id &&
                (r.Job.QueueRetryAt is not null || r.Job.State is not (DownloadState.Completed or DownloadState.Failed or DownloadState.Cancelled)));
        }

        if (schedule.OpenFileWhenDone && !job.CompletionOptions.OpenFile)
        {
            if (File.Exists(job.FinalFilePath))
            {
                try
                {
                    await _completionActions.OpenFileAsync(job.FinalFilePath, cancellationToken)
                        .ConfigureAwait(false);
                    _log?.Invoke($"Completion action opened '{job.FinalFilePath}'.");
                }
                catch (Exception ex)
                {
                    _log?.Invoke($"Completion action could not open '{job.FinalFilePath}': {ex.Message}");
                }
            }
            else
            {
                _log?.Invoke($"Completion action skipped open: '{job.FinalFilePath}' does not exist.");
            }
        }

        if (skipSystemAction) return;

        if (!schedule.ExitWhenDone && !schedule.ShutdownWhenDone)
        {
            return;
        }

        if (!allowSystemActions)
        {
            _log?.Invoke("Completion action exit/shutdown blocked: system completion actions are disabled.");
            return;
        }

        if (blockedByOtherDownloads)
        {
            _log?.Invoke("Completion action exit/shutdown blocked: other downloads are unfinished.");
            return;
        }

        try
        {
            if (schedule.ShutdownWhenDone)
            {
                await _completionActions.RequestShutdownAsync(
                    schedule.ShutdownMode, schedule.ForceShutdown, cancellationToken).ConfigureAwait(false);
                _log?.Invoke($"Completion action requested shutdown ({schedule.ShutdownMode}).");
            }
            else
            {
                await _completionActions.RequestExitAsync(cancellationToken).ConfigureAwait(false);
                _log?.Invoke("Completion action requested host exit.");
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Completion action failed: {ex.Message}");
        }
    }

    private async Task PersistAsync(JobRuntime runtime, CancellationToken cancellationToken, bool requireSuccess = false, bool gateHeld = false)
    {
        DownloadJob clone;
        lock (_sync)
        {
            if (runtime.CancelledForRemoval)
            {
                if (requireSuccess) throw new InvalidOperationException("The download is being removed.");
                return;
            }

            if (runtime.Job.CategoryIsAutomatic && runtime.CategoryFileName != runtime.Job.FileName)
            {
                runtime.Job.CategoryId = _settings.CustomCategories.FirstOrDefault(category => category.Extensions.Contains(
                    Path.GetExtension(runtime.Job.FileName).TrimStart('.'), StringComparer.OrdinalIgnoreCase))?.Id ?? FileCategories.For(runtime.Job.FileName);
                runtime.CategoryFileName = runtime.Job.FileName;
            }
            clone = runtime.Job.Clone();
        }

        if (!gateHeld) await _persistGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                if (runtime.CancelledForRemoval || !_jobs.ContainsKey(clone.Id))
                {
                    if (requireSuccess) throw new InvalidOperationException("The download is being removed.");
                    return;
                }
                clone = runtime.Job.Clone();
            }
            await _jobStore.SaveAsync(clone, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            _log?.Invoke($"Could not persist job {clone.Id}: {ex.Message}");
            if (requireSuccess) throw;
        }
        finally
        {
            if (!gateHeld) _persistGate.Release();
        }
    }

    /// <summary>
    /// Records that the completed downloads of these files are gone on purpose, or are not after all. Saved
    /// before it returns, because the caller deletes the files next. Throws if it could not be saved.
    /// </summary>
    public async Task MarkFilesRemovedAfterExtractionAsync(IReadOnlyCollection<string> paths, bool removed, CancellationToken cancellationToken = default)
    {
        List<JobRuntime> marked;
        lock (_sync)
        {
            marked = _jobs.Values
                .Where(runtime => runtime.Job.State == DownloadState.Completed && runtime.Job.FileRemovedAfterExtraction != removed &&
                    paths.Contains(runtime.Job.FinalFilePath, StringComparer.OrdinalIgnoreCase))
                .ToList();
            foreach (var runtime in marked)
            {
                runtime.Job.FileRemovedAfterExtraction = removed;
                runtime.Job.Touch();
            }
        }

        foreach (var runtime in marked)
        {
            try
            {
                await PersistAsync(runtime, cancellationToken, requireSuccess: true).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // It is being removed from the list, so there is nothing left to mark.
                continue;
            }

            Raise(runtime, "updated");
        }
    }

    private async Task PersistQuietlyAsync(JobRuntime runtime)
    {
        try
        {
            await PersistAsync(runtime, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Background persistence failed: {ex.Message}");
        }
    }

    private void Raise(JobRuntime runtime, string changeType)
    {
        JobDto dto;
        lock (_sync)
        {
            dto = Map(runtime.Job);
        }

        JobChanged?.Invoke(this, new JobChangeEventArgs(dto, changeType));
    }

    private async Task DeletePayloadAsync(DownloadJob job, bool includeFinal, CancellationToken cancellationToken)
    {
        DeleteFileQuietly(job.PartFilePath);
        DeleteFileQuietly(job.PartFilePath + ".video");
        DeleteFileQuietly(job.PartFilePath + ".audio");
        DeleteFileQuietly(job.PartFilePath + ".mux.mp4");
        if (includeFinal)
        {
            DeleteFileQuietly(job.FinalFilePath);
        }

        // A transfer still winding down can be replacing this file, which Windows refuses to
        // delete meanwhile. The runner deletes it again when it ends, and a stray one is harmless.
        await DeleteCheckpointQuietlyAsync(job.Id).ConfigureAwait(false);
        if (_mediaCheckpoints is not null)
        {
            await _mediaCheckpoints.DeleteAsync(job.Id, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    private int EffectiveConnectionLimit(Uri uri)
    {
        var limit = _settings.MaxConnectionsPerHost;
        var profile = _settings.ConnectionProfiles
            .Where(candidate => HostMatches(candidate.Pattern, uri))
            .OrderByDescending(candidate => candidate.Pattern.Length)
            .FirstOrDefault();
        if (profile is not null)
        {
            limit = Math.Min(limit, profile.MaxConnections);
        }

        return Math.Clamp(limit, 1, 32);
    }

    private static bool HostMatches(string pattern, Uri uri)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return false;
        }

        var value = pattern.Contains(':') ? uri.Authority : uri.Host;
        var regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern.Trim())
            .Replace("\\*", ".*")
            .Replace("\\?", ".") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(
            value, regex, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private string ResolveDestination(string? requested, string fileName, bool useLastFolder = true, bool useCategoryFolder = true, string? categoryId = null)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            return Path.GetFullPath(requested.Trim());
        }

        var category = categoryId ?? FileCategories.For(fileName);
        var customFolder = _settings.CustomCategories.FirstOrDefault(item => item.Id == category)?.DestinationDirectory;
        if (useCategoryFolder && !string.IsNullOrWhiteSpace(customFolder)) return Path.GetFullPath(customFolder);
        if (useCategoryFolder && _settings.CategoryFolders.TryGetValue(category, out var categoryFolder) &&
            !string.IsNullOrWhiteSpace(categoryFolder))
        {
            return Path.GetFullPath(categoryFolder.Trim());
        }

        var directory = (useLastFolder ? _settings.LastDestinationDirectory : null) ?? _settings.DefaultDownloadDirectory;
        return Path.GetFullPath(directory);
    }

    /// <summary>
    /// Payload path for a job. It deliberately lives next to the final file so the
    /// transfer consumes space on the chosen destination volume and completion is
    /// an atomic rename on that same volume.
    /// </summary>
    private static string BuildPartPath(string finalPath, Guid jobId)
        => $"{finalPath}.{jobId:N}.part";

    private static string ResolveFinalPath(string directory, string fileName, ConflictPolicy policy)
    {
        var candidate = Path.Combine(directory, FileNameResolver.Sanitize(fileName));
        if (policy == ConflictPolicy.Overwrite)
        {
            if (Directory.Exists(candidate))
            {
                throw new IOException($"A directory exists at {candidate}.");
            }

            return candidate;
        }

        return FileNameResolver.ResolveUniquePath(directory, Path.GetFileName(candidate), policy);
    }

    private static bool TryValidateUrl(string? value, out Uri uri, out string error)
    {
        uri = null!;
        error = string.Empty;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed))
        {
            error = "Enter an absolute URL.";
            return false;
        }

        if (parsed.Scheme is not ("http" or "https" or "ftp"))
        {
            error = "Only http, https, and ftp URLs are supported.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(parsed.Host))
        {
            error = "The URL has no host.";
            return false;
        }

        uri = parsed;
        return true;
    }

    private static bool TryCopyHeaders(
        Dictionary<string, string>? source,
        out Dictionary<string, string> headers,
        out string error)
    {
        headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        error = string.Empty;
        if (source is null)
        {
            return true;
        }

        foreach (var (key, value) in source)
        {
            if (string.IsNullOrWhiteSpace(key) || key.Any(c => c is '\r' or '\n' or ':'))
            {
                error = "A header name is invalid.";
                return false;
            }

            if (value is null || value.Any(c => c is '\r' or '\n'))
            {
                error = $"Header '{key}' has an invalid value.";
                return false;
            }

            headers[key.Trim()] = value;
        }

        return true;
    }

    private static T ParseEnum<T>(string? value, T fallback) where T : struct, Enum
        => Enum.TryParse<T>(value, ignoreCase: true, out var parsed) ? parsed : fallback;

    private static AddJobResponse Duplicate(DownloadJob job)
        => new() { Accepted = true, Duplicate = true, Job = Map(job) };

    private static void DeleteFileQuietly(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // best effort
        }
        catch (UnauthorizedAccessException)
        {
            // best effort
        }
    }

    private HostStatusDto BuildStatusLocked()
    {
        var active = _jobs.Values.Count(r => r.Job.State is DownloadState.Connecting or DownloadState.Downloading
            or DownloadState.RetryWaiting or DownloadState.Verifying);
        var queued = _jobs.Values.Count(r => r.Job.State == DownloadState.Queued);
        return new HostStatusDto
        {
            HostVersion = Protocol.HostVersion,
            ProtocolVersion = Protocol.Version,
            StartedAt = StartedAt,
            TrayAvailable = TrayAvailable,
            NativeHostRegistered = NativeHostRegistered?.Invoke() ?? false,
            ActiveDownloads = active,
            QueuedDownloads = queued,
            TotalJobs = _jobs.Count,
            StateDirectory = _paths.Root,
            BrowserIntegration = !_settings.BrowserIntegrationEnabled
                ? "disabled"
                : NativeHostRegistered?.Invoke() == true
                    ? "connected"
                    : "native host not registered",
            OrphanPartFiles = _orphanPartFiles.Where(File.Exists).ToList(),
        };
    }

    private static bool SupportsLiveSpeedLimit(DownloadJob job)
        => job.RemoteSource is not null ||
           Uri.TryCreate(job.Url, UriKind.Absolute, out var uri) &&
           uri.Scheme is "http" or "https" &&
           !uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) &&
           !uri.AbsolutePath.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase) &&
           !job.FileName.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) &&
           !job.FileName.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase) &&
           !(job.ContentType?.Contains("mpegurl", StringComparison.OrdinalIgnoreCase) ?? false) &&
           !(job.ContentType?.Contains("dash+xml", StringComparison.OrdinalIgnoreCase) ?? false) &&
           !(job.Description?.Contains("mpegurl", StringComparison.OrdinalIgnoreCase) ?? false) &&
           !(job.Description?.Contains("m3u8", StringComparison.OrdinalIgnoreCase) ?? false) &&
           !(job.Description?.Contains("dash+xml", StringComparison.OrdinalIgnoreCase) ?? false) &&
           !(job.Description?.Contains(".mpd", StringComparison.OrdinalIgnoreCase) ?? false);

    public static JobDto Map(DownloadJob job)
        => new()
        {
            Id = job.Id,
            Url = job.Url,
            FinalUrl = job.FinalUrl,
            PageUrl = job.PageUrl,
            FileName = job.FileName,
            IsQueued = job.IsQueued,
            QueueProcessing = job.QueueProcessing, QueueRetryCount = job.QueueRetryCount, QueueRetryAt = job.QueueRetryAt,
            CategoryId = job.CategoryId ?? FileCategories.For(job.FileName),
            CategoryIsAutomatic = job.CategoryIsAutomatic,
            DestinationDirectory = job.DestinationDirectory,
            FinalFilePath = job.FinalFilePath,
            PartFilePath = job.PartFilePath,
            TotalBytes = job.TotalBytes,
            ReceivedBytes = job.ReceivedBytes,
            Progress = job.ProgressFraction,
            State = JsonNamingPolicy.CamelCase.ConvertName(job.State.ToString()),
            CreatedAt = job.CreatedAt,
            UpdatedAt = job.UpdatedAt,
            StartedAt = job.StartedAt,
            CompletedAt = job.CompletedAt,
            ContentType = job.ContentType,
            ETag = job.ETag,
            LastModified = job.LastModified,
            RequestedConnections = job.RequestedConnections,
            ActiveConnections = job.ActiveConnections,
            BytesPerSecond = job.BytesPerSecond,
            SpeedLimitBytesPerSecond = job.SpeedLimitBytesPerSecond,
            CanChangeSpeedLimit = !job.IsTerminal && SupportsLiveSpeedLimit(job),
            CanCancel = !job.IsTerminal,
            ResumeSupported = job.ResumeSupported,
            ShowCompletionDialog = job.ShowCompletionDialog,
            CompletionOptions = new JobCompletionOptionsDto { ProgramPath = job.CompletionOptions.ProgramPath,
                OpenFile = job.CompletionOptions.OpenFile, SystemAction = job.CompletionOptions.SystemAction,
                ForceSystemAction = job.CompletionOptions.ForceSystemAction },
            EstimatedRemaining = job.EstimatedRemaining,
            Source = JsonNamingPolicy.CamelCase.ConvertName(job.Source.ToString()),
            ClientRequestId = job.ClientRequestId,
            OwnerExtensionId = job.OwnerExtensionId,
            QueueId = job.QueueId,
            Priority = job.Priority,
            CancelledByPackageId = job.CancelledByPackageId,
            Description = job.Description,
            ExpectedSha256 = job.ExpectedSha256,
            QueueName = job.QueueName,
            StartMode = JsonNamingPolicy.CamelCase.ConvertName(job.StartMode.ToString()),
            ConflictPolicy = JsonNamingPolicy.CamelCase.ConvertName(job.ConflictPolicy.ToString()),
            CanPause = job.CanPause,
            CanResume = job.CanResume,
            IsTerminal = job.IsTerminal,
            IsRemote = job.RemoteSource is not null,
            RemoteProvider = job.RemoteSource?.Provider,
            FileRemovedAfterExtraction = job.FileRemovedAfterExtraction,
            StatusNote = job.FileRemovedAfterExtraction ? "Archive removed after extraction" : null,
            Failure = job.Failure is null
                ? null
                : new FailureDto
                {
                    Kind = JsonNamingPolicy.CamelCase.ConvertName(job.Failure.Kind.ToString()),
                    Message = job.Failure.Message,
                    IsRetryable = job.Failure.IsRetryable,
                    IsResumable = job.Failure.IsResumable,
                    OccurredAt = job.Failure.OccurredAt,
                    HttpStatus = job.Failure.HttpStatus,
                },
        };

    public static SettingsDto MapSettings(AppSettings settings)
        => new()
        {
            DefaultDownloadDirectory = settings.DefaultDownloadDirectory,
            MaxConcurrentDownloads = settings.MaxConcurrentDownloads,
            DefaultConnections = settings.DefaultConnections,
            MaxConnectionsPerHost = settings.MaxConnectionsPerHost,
            ConnectionProfiles = settings.ConnectionProfiles
                .Select(profile => new ConnectionProfileDto
                {
                    Pattern = profile.Pattern,
                    MaxConnections = profile.MaxConnections,
                })
                .ToList(),
            GlobalSpeedLimitBytesPerSecond = settings.GlobalSpeedLimitBytesPerSecond,
            Proxy = MapProxy(settings.Proxy),
            SiteLogins = settings.SiteLogins.Select(login => new SiteLoginDto
            {
                Id = login.Id,
                Site = login.Site,
                Username = login.Username,
                HasPassword = !string.IsNullOrWhiteSpace(login.PasswordProtected),
            }).ToList(),
            LaunchAtStartup = settings.LaunchAtStartup,
            ClipboardMonitoring = settings.ClipboardMonitoring,
            BrowserIntegrationEnabled = settings.BrowserIntegrationEnabled,
            NotifyOnCompletion = settings.NotifyOnCompletion,
            PlayCompletionSound = settings.PlayCompletionSound,
            EventSoundFiles = new Dictionary<string, string>(settings.EventSoundFiles ?? new(), StringComparer.OrdinalIgnoreCase),
            ShowCompletionDialog = settings.ShowCompletionDialog,
            AutoResumeUnfinishedOnStartup = settings.AutoResumeUnfinishedOnStartup,
            AllowLocalNetwork = settings.AllowLocalNetwork,
            PreventSleepWhileDownloading = settings.PreventSleepWhileDownloading,
            FfmpegPath = settings.FfmpegPath,
            PostDownloadCommand = settings.PostDownloadCommand,
            UpdateManifestUrl = settings.UpdateManifestUrl,
            UpdatePublicKey = settings.UpdatePublicKey,
            UpdateChannel = settings.UpdateChannel,
            AutoCheckUpdates = settings.AutoCheckUpdates,
            UpdateCheckIntervalHours = settings.UpdateCheckIntervalHours,
            SkippedUpdateVersion = settings.SkippedUpdateVersion,
            LastUpdateCheck = settings.LastUpdateCheck,
            AllowSystemCompletionActions = settings.AllowSystemCompletionActions,
            Theme = settings.Theme,
            LastDestinationDirectory = settings.LastDestinationDirectory,
            RecentUrls = new List<string>(settings.RecentUrls ?? new List<string>()),
            DownloadGroups = MapGroups(settings.DownloadGroups),
            CustomCategories = settings.CustomCategories.Select(category => new CategoryDefinitionDto { Id = category.Id, Name = category.Name,
                DestinationDirectory = category.DestinationDirectory, Extensions = new(category.Extensions) }).ToList(),
            CategoryFolders = new Dictionary<string, string>(
                settings.CategoryFolders ?? new Dictionary<string, string>(),
                StringComparer.OrdinalIgnoreCase),
            ExcludedFileExtensions = settings.ExcludedFileExtensions,
            ExcludedHosts = settings.ExcludedHosts,
            InterceptedFileExtensions = settings.InterceptedFileExtensions,
            DefaultQueueId = settings.DefaultQueueId,
            Queues = settings.Queues.Select(q => new QueueDto
            {
                Id = q.Id,
                Name = q.Name,
                MaxConcurrentDownloads = q.MaxConcurrentDownloads,
                IsRunning = q.IsRunning,
                Order = q.Order,
                JobOrder = q.JobOrder?.ToList() ?? new(),
                RetryLimit = q.RetryLimit,
                QuotaEnabled = q.QuotaEnabled,
                QuotaBytes = q.QuotaBytes,
                QuotaWindowHours = q.QuotaWindowHours,
                QuotaWindowStart = q.QuotaWindowStart,
                QuotaBytesUsed = q.QuotaBytesUsed,
            }).ToList(),
            Schedules = settings.Schedules.Select(s => new ScheduleDto
            {
                Id = s.Id,
                Name = s.Name,
                QueueId = s.QueueId,
                Enabled = s.Enabled,
                Kind = s.Kind,
                StartDate = s.StartDate,
                Days = s.Days,
                StartTime = s.StartTime,
                StopTime = s.StopTime,
                OpenFileWhenDone = s.OpenFileWhenDone,
                ExitWhenDone = s.ExitWhenDone,
                ShutdownWhenDone = s.ShutdownWhenDone,
                ShutdownMode = s.ShutdownMode,
                ForceShutdown = s.ForceShutdown,
                StartOnHostStartup = s.StartOnHostStartup, TimedStartEnabled = s.TimedStartEnabled,
                RepeatEveryMinutes = s.RepeatEveryMinutes,
                LastRunAt = s.LastRunAt, LastRunError = s.LastRunError,
                LastStartDate = s.LastStartDate,
                LastStopDate = s.LastStopDate,
            }).ToList(),
        };

    private static ProxyDto MapProxy(ProxySettings? proxy)
        => new()
        {
            Mode = proxy?.Mode ?? "none",
            Address = proxy?.Address,
            Username = proxy?.Username,
            Password = null,
            HasPassword = !string.IsNullOrEmpty(proxy?.Password),
        };

    private static ProxySettings MapProxy(ProxyDto? proxy)
        => new()
        {
            Mode = proxy?.Mode?.Trim().ToLowerInvariant() switch
            {
                "system" => "system",
                "manual" => "manual",
                _ => "none",
            },
            Address = string.IsNullOrWhiteSpace(proxy?.Address) ? null : proxy.Address.Trim(),
            Username = string.IsNullOrWhiteSpace(proxy?.Username) ? null : proxy.Username.Trim(),
            Password = string.IsNullOrWhiteSpace(proxy?.Password) ? null : proxy.Password,
        };

    private static List<SiteLoginCredential> BuildSiteLogins(AppSettings settings)
    {
        var result = new List<SiteLoginCredential>();
        foreach (var login in settings.SiteLogins)
        {
            var password = SecretProtector.Unprotect(login.PasswordProtected);
            if (password is null)
            {
                continue;
            }

            result.Add(new SiteLoginCredential(login.Site, login.Username, password));
        }

        return result;
    }

    private static List<SiteLogin> MapSiteLogins(
        List<SiteLoginDto>? requested,
        List<SiteLogin> existing)
    {
        if (requested is null)
        {
            return existing.ToList();
        }

        var existingById = existing.ToDictionary(login => login.Id);
        var result = new List<SiteLogin>();
        foreach (var login in requested)
        {
            var site = login.Site?.Trim() ?? string.Empty;
            var username = login.Username?.Trim() ?? string.Empty;
            if (site.Length == 0 || username.Length == 0)
            {
                continue;
            }

            var id = login.Id == Guid.Empty ? Guid.NewGuid() : login.Id;
            existingById.TryGetValue(id, out var previous);
            var passwordProtected = !string.IsNullOrEmpty(login.Password)
                ? SecretProtector.Protect(login.Password)
                : previous is not null &&
                  string.Equals(previous.Site, site, StringComparison.OrdinalIgnoreCase) &&
                  string.Equals(previous.Username, username, StringComparison.Ordinal)
                    ? previous.PasswordProtected
                    : string.Empty;
            result.Add(new SiteLogin
            {
                Id = id,
                Site = site,
                Username = username,
                PasswordProtected = passwordProtected,
            });
        }

        return result;
    }

    private static List<DownloadQueue> MapQueues(
        List<QueueDto>? requested,
        List<DownloadQueue> existing,
        int defaultMax)
    {
        var source = requested is { Count: > 0 }
            ? requested.Select((q, index) => new DownloadQueue
            {
                Id = q.Id,
                Name = string.IsNullOrWhiteSpace(q.Name) ? $"Queue {index + 1}" : q.Name.Trim(),
                MaxConcurrentDownloads = Math.Clamp(q.MaxConcurrentDownloads, 1, 16),
                IsRunning = q.IsRunning,
                Order = index,
                RetryLimit = q.RetryLimit,
                QuotaEnabled = q.QuotaEnabled,
                QuotaBytes = Math.Clamp(q.QuotaBytes, 1, 10L * 1024 * 1024 * 1024 * 1024),
                QuotaWindowHours = Math.Clamp(q.QuotaWindowHours, 1, 24 * 365),
                QuotaWindowStart = q.QuotaWindowStart,
                QuotaBytesUsed = Math.Max(0, q.QuotaBytesUsed),
                JobOrder = existing.FirstOrDefault(current => current.Id == q.Id)?.JobOrder?.ToList() ?? new(),
            }).ToList()
            : existing.Select((q, index) => new DownloadQueue
            {
                Id = q.Id,
                Name = q.Name,
                MaxConcurrentDownloads = q.MaxConcurrentDownloads,
                IsRunning = q.IsRunning,
                Order = index,
                RetryLimit = q.RetryLimit,
                QuotaEnabled = q.QuotaEnabled,
                QuotaBytes = q.QuotaBytes,
                QuotaWindowHours = q.QuotaWindowHours,
                QuotaWindowStart = q.QuotaWindowStart,
                QuotaBytesUsed = q.QuotaBytesUsed,
                JobOrder = q.JobOrder?.ToList() ?? new(),
            }).ToList();

        if (source.Count == 0)
        {
            source.Add(new DownloadQueue
            {
                Id = 0,
                Name = "Main",
                MaxConcurrentDownloads = Math.Clamp(defaultMax, 1, 16),
                IsRunning = true,
            });
        }

        var seen = new HashSet<int>();
        for (var i = 0; i < source.Count; i++)
        {
            if (!seen.Add(source[i].Id))
            {
                source[i].Id = seen.Max() + 1;
                seen.Add(source[i].Id);
            }

            source[i].Order = i;
        }

        return source;
    }

    private static List<ScheduleRule> MapSchedules(
        List<ScheduleDto>? requested,
        List<ScheduleRule> existing)
    {
        // A null list means "not supplied"; an empty list means the caller
        // explicitly removed every schedule and must be honored.
        if (requested is null)
        {
            return existing.ToList();
        }

        var existingById = existing.ToDictionary(s => s.Id);
        return requested.Select(s =>
        {
            var id = s.Id == Guid.Empty ? Guid.NewGuid() : s.Id;
            existingById.TryGetValue(id, out var previous);
            var triggerChanged = previous is not null && (previous.QueueId != s.QueueId || previous.Kind != s.Kind ||
                previous.StartDate != s.StartDate || previous.Days != s.Days || previous.StartTime != s.StartTime ||
                previous.StopTime != s.StopTime || previous.StartOnHostStartup != s.StartOnHostStartup ||
                previous.TimedStartEnabled != s.TimedStartEnabled || previous.RepeatEveryMinutes != s.RepeatEveryMinutes ||
                !previous.Enabled && s.Enabled);
            return new ScheduleRule
            {
                Id = id,
                Name = s.Name,
                QueueId = s.QueueId,
                Enabled = s.Enabled,
                Kind = string.Equals(s.Kind, "oneTime", StringComparison.OrdinalIgnoreCase)
                    ? "oneTime"
                    : "daily",
                StartDate = s.StartDate,
                Days = s.Days,
                StartTime = s.StartTime,
                StopTime = s.StopTime,
                OpenFileWhenDone = s.OpenFileWhenDone,
                ExitWhenDone = s.ExitWhenDone,
                ShutdownWhenDone = s.ShutdownWhenDone,
                ShutdownMode = s.ShutdownMode,
                ForceShutdown = s.ForceShutdown,
                StartOnHostStartup = s.StartOnHostStartup, TimedStartEnabled = s.TimedStartEnabled,
                RepeatEveryMinutes = s.RepeatEveryMinutes,
                // Run state is owned by the host scheduler, never by a UI payload.
                LastRunAt = triggerChanged ? null : previous?.LastRunAt, LastRunError = triggerChanged ? null : previous?.LastRunError,
                LastStartDate = triggerChanged ? null : previous?.LastStartDate,
                LastStopDate = triggerChanged ? null : previous?.LastStopDate,
            };
        }).ToList();
    }

    private sealed class JobRuntime
    {
        public JobRuntime(DownloadJob job)
        {
            Job = job;
            CategoryFileName = job.FileName;
            QuotaReportedBytes = job.ReceivedBytes;
        }

        public DownloadJob Job { get; }
        public string CategoryFileName { get; set; }
        public long QuotaReportedBytes { get; set; }
        public CancellationTokenSource? Cts { get; set; }
        public Task? Runner { get; set; }
        public bool Starting { get; set; }
        public bool AdmissionSuspended { get; set; }
        public bool PauseRequested { get; set; }
        public bool ResumeWhenStopped { get; set; }
        public bool CancelRequested { get; set; }
        public bool DeleteOnCancel { get; set; } = true;
        public bool CancelledForRemoval { get; set; }
    }

    private sealed class InlineProgress<T>(Action<T> action) : IProgress<T>
    {
        public void Report(T value) => action(value);
    }
}
