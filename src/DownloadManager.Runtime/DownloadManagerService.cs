using System.Globalization;
using System.Text.Json;
using DownloadManager.Contracts;
using DownloadManager.Core;
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

public sealed class DownloadManagerService
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
    private readonly IGrabberProjectStore? _grabberProjectStore;
    private readonly IDownloadEngine? _ftpEngine;
    private static readonly HttpClient UpdateClient = new() { Timeout = TimeSpan.FromSeconds(30) };
    public event EventHandler? UpdateShutdownRequested;
    public event EventHandler<string>? UpdateDeferred;
    private readonly object _sync = new();
    private readonly List<GrabberProject> _grabberProjects = new();
    private readonly List<string> _orphanPartFiles = new();
    private readonly HashSet<Guid> _runningGrabberProjects = new();
    private readonly Dictionary<Guid, DateTimeOffset> _grabberProgressSaved = new();
    private bool _sleepPreventionActive;
    private readonly Dictionary<Guid, JobRuntime> _jobs = new();
    private readonly HashSet<int> _completionFiredQueues = new();
    private readonly SemaphoreSlim _persistGate = new(1, 1);
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
        IGrabberProjectStore? grabberProjects = null,
        IDownloadEngine? ftpEngine = null,
        IPostDownloadHook? postDownloadHook = null)
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
        _grabberProjectStore = grabberProjects;
        _ftpEngine = ftpEngine;
        _postDownloadHook = postDownloadHook ?? NullPostDownloadHook.Instance;
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
        if (_grabberProjectStore is not null)
        {
            var projects = await _grabberProjectStore.LoadAllAsync(cancellationToken).ConfigureAwait(false);
            var interrupted = new List<GrabberProject>();
            lock (_sync)
            {
                _grabberProjects.AddRange(projects);
                foreach (var project in _grabberProjects.Where(project =>
                             string.Equals(project.Status, "running", StringComparison.OrdinalIgnoreCase)))
                {
                    project.Status = "interrupted";
                    project.UpdatedAt = _timeProvider.GetUtcNow();
                    interrupted.Add(project.Clone());
                }
            }

            foreach (var project in interrupted)
            {
                await _grabberProjectStore.SaveAsync(project, cancellationToken).ConfigureAwait(false);
            }
        }

        var jobs = await _jobStore.LoadAllAsync(cancellationToken).ConfigureAwait(false);

        foreach (var job in jobs)
        {
            var runtime = new JobRuntime(job);
            _jobs[job.Id] = runtime;
        }

        foreach (var runtime in _jobs.Values)
        {
            var changed = await ReconcileAsync(runtime, cancellationToken).ConfigureAwait(false);
            if (changed)
            {
                await PersistAsync(runtime, cancellationToken).ConfigureAwait(false);
            }
        }

        ScanOrphanPartFiles();
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

        var checkpoint = await _checkpointStore.LoadAsync(id, cancellationToken).ConfigureAwait(false);
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

    public JobDto? GetJob(Guid id)
    {
        lock (_sync)
        {
            return _jobs.TryGetValue(id, out var runtime) ? Map(runtime.Job) : null;
        }
    }

    public async Task<AddJobResponse> AddJobAsync(AddJobRequest request, CancellationToken cancellationToken = default)
    {
        if (!TryValidateUrl(request.Url, out var uri, out var urlError))
        {
            return new AddJobResponse { Accepted = false, Error = urlError, ErrorCode = "invalid_url" };
        }

        var owner = string.IsNullOrWhiteSpace(request.OwnerExtensionId) ? null : request.OwnerExtensionId.Trim();
        var clientRequestId = string.IsNullOrWhiteSpace(request.ClientRequestId) ? null : request.ClientRequestId.Trim();
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
        var conflictPolicy = ParseEnum(request.ConflictPolicy, ConflictPolicy.Rename);

        var fileName = string.IsNullOrWhiteSpace(request.FileName)
            ? FileNameResolver.Resolve(null, uri)
            : FileNameResolver.Sanitize(request.FileName);
        var destination = ResolveDestination(request.DestinationDirectory, fileName);
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

        var job = new DownloadJob
        {
            Url = request.Url,
            FinalUrl = request.FinalUrl,
            PageUrl = request.PageUrl,
            Referrer = request.Referrer,
            UserAgent = request.UserAgent,
            QueueId = queueId,
            QueueName = queue.Name,
            FileName = Path.GetFileName(finalPath),
            DestinationDirectory = destination,
            FinalFilePath = finalPath,
            PartFilePath = string.Empty,
            StartMode = startMode,
            Source = source,
            ClientRequestId = clientRequestId,
            OwnerExtensionId = owner,
            RequestHeaders = headers,
            RequestedConnections = Math.Clamp(
                Math.Min(
                    request.Connections ?? _settings.DefaultConnections,
                    EffectiveConnectionLimit(uri)),
                1, 32),
            SpeedLimitBytesPerSecond = request.SpeedLimitBytesPerSecond ?? _settings.GlobalSpeedLimitBytesPerSecond,
            Description = request.Description,
            ExpectedSha256 = string.IsNullOrWhiteSpace(request.ExpectedSha256) ? null : request.ExpectedSha256.Trim(),
            ConflictPolicy = conflictPolicy,
            State = DownloadState.Queued,
            AutoResumeAfterRestart = request.AutoResumeAfterRestart ?? _settings.AutoResumeUnfinishedOnStartup,
            TotalBytes = null,
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
            var claimed = await _jobStore.TryClaimRequestAsync(owner, clientRequestId, job.Id, cancellationToken)
                .ConfigureAwait(false);
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
        if (request.RememberDestination != false &&
            !string.IsNullOrWhiteSpace(request.DestinationDirectory) &&
            !string.Equals(_settings.LastDestinationDirectory, destination, StringComparison.OrdinalIgnoreCase))
        {
            _settings.LastDestinationDirectory = destination;
            settingsChanged = true;
        }

        if (source is not (DownloadSource.Grabber or DownloadSource.Import))
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
                if (!runtime.PauseRequested)
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
            runtime.Job.State = DownloadState.Queued;
            runtime.Job.StartMode = StartMode.StartNow;
            runtime.Job.Failure = null;
            runtime.Job.CompletedAt = null;
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
        CancellationToken cancellationToken = default)
    {
        JobRuntime? runtime;
        lock (_sync)
        {
            if (!_jobs.TryGetValue(id, out runtime))
            {
                return new JobActionResponse { Ok = false, Error = "Job not found." };
            }

            runtime.CancelRequested = true;
            runtime.DeleteOnCancel = deleteFile;
            runtime.Job.DeleteFileOnCancel = deleteFile;
            runtime.Job.State = DownloadState.Cancelled;
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
                await Task.WhenAny(runner, Task.Delay(TimeSpan.FromSeconds(15), cancellationToken)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // continue with removal
            }
        }

        if (deleteFile)
        {
            DeleteFileQuietly(runtime.Job.FinalFilePath);
            DeleteFileQuietly(runtime.Job.PartFilePath);
            await _checkpointStore.DeleteAsync(id, CancellationToken.None).ConfigureAwait(false);
        }

        lock (_sync)
        {
            _jobs.Remove(id);
        }

        await _jobStore.DeleteAsync(id, CancellationToken.None).ConfigureAwait(false);
        JobChanged?.Invoke(this, new JobChangeEventArgs(Map(runtime.Job), "removed"));
        Schedule();
        return new JobActionResponse { Ok = true, Job = Map(runtime.Job) };
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
            BrowserAutoIntercept = settings.BrowserAutoIntercept,
            NotifyOnCompletion = settings.NotifyOnCompletion,
            PlayCompletionSound = settings.PlayCompletionSound,
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

        if (string.IsNullOrEmpty(next.Proxy.Password) &&
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
                    runtime.Job.QueueName = queueName;
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
        List<JobRuntime> paused = new();
        List<JobRuntime> resumed = new();
        string? error = null;

        lock (_sync)
        {
            var queue = _settings.Queues.FirstOrDefault(q => q.Id == request.QueueId);
            if (queue is null)
            {
                return new SettingsResult { Ok = false, Error = "Queue not found.", Settings = MapSettings(_settings) };
            }

            switch (request.Action.ToLowerInvariant())
            {
                case "start":
                    queue.IsRunning = true;
                    foreach (var runtime in _jobs.Values.Where(r =>
                                 r.Job.QueueId == queue.Id && r.Job.State == DownloadState.Paused))
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
                        runtime.Job.State = DownloadState.Queued;
                        runtime.Job.StartMode = StartMode.StartNow;
                        runtime.Job.Failure = null;
                        runtime.Job.Touch();
                        resumed.Add(runtime);
                    }

                    break;
                case "stop":
                    queue.IsRunning = false;
                    paused = _jobs.Values
                        .Where(r => r.Job.QueueId == queue.Id &&
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

            foreach (var runtime in _jobs.Values.Where(r => r.Job.QueueId == queue.Id))
            {
                runtime.Job.QueueName = queue.Name;
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
        SettingsChanged?.Invoke(this, _settings);
        Schedule();
        return new SettingsResult { Ok = true, Settings = MapSettings(_settings) };
    }

    public async Task<SiteGrabberResult> RunSiteGrabberAsync(
        SiteGrabberRequest request,
        CancellationToken cancellationToken = default,
        Action<int, int, int>? progress = null)
    {
        if (!TryValidateUrl(request.StartUrl, out _, out var urlError))
        {
            return new SiteGrabberResult { Ok = false, Error = urlError };
        }

        string destination;
        try
        {
            destination = string.IsNullOrWhiteSpace(request.DestinationDirectory)
                ? _settings.DefaultDownloadDirectory
                : request.DestinationDirectory.Trim();
            destination = Path.GetFullPath(destination);
            Directory.CreateDirectory(destination);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new SiteGrabberResult { Ok = false, Error = $"Cannot use destination: {ex.Message}" };
        }

        var grabber = new SiteGrabber(
            destination, _log, _engine.CreateAuxiliaryHandler, _engine.GetSiteAuthorization);
        return await grabber.RunAsync(
            request,
            file =>
            {
                var fullPath = Path.Combine(
                    destination, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                return AddJobAsync(new AddJobRequest
                {
                    Url = file.Url.AbsoluteUri,
                    DestinationDirectory = Path.GetDirectoryName(fullPath),
                    FileName = Path.GetFileName(fullPath),
                    StartMode = request.StartMode,
                    QueueId = request.QueueId,
                    Connections = request.Connections > 0 ? request.Connections : null,
                    Source = "grabber",
                    ConflictPolicy = "rename",
                    RememberDestination = false,
                }, cancellationToken);
            },
            cancellationToken,
            progress).ConfigureAwait(false);
    }

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
        try
        {
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
                }
            }

            var length = new FileInfo(target).Length;
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

    public async Task<UpdateInstallResult> InstallUpdateAsync(
        UpdateInstallRequest? request = null,
        CancellationToken cancellationToken = default)
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

        var download = await DownloadUpdateAsync(cancellationToken).ConfigureAwait(false);
        if (!download.Ok || string.IsNullOrWhiteSpace(download.Path))
        {
            return new UpdateInstallResult
            {
                Ok = false,
                Status = "downloadFailed",
                Error = download.Error ?? "The installer could not be downloaded.",
            };
        }

        var staging = Path.Combine(_paths.Root, "updates", $"stage-{check.AvailableVersion}");
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

            var resultPath = Path.Combine(_paths.Root, "updates", "install-result.json");
            var start = new System.Diagnostics.ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
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

            var helper = System.Diagnostics.Process.Start(start);
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

    public async Task<SettingsResult> DeferUpdateAsync(
        string version,
        CancellationToken cancellationToken = default)
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

    public GrabberProjectsResult GetGrabberProjects()
    {
        lock (_sync)
        {
            return new GrabberProjectsResult
            {
                Projects = _grabberProjects
                    .OrderByDescending(project => project.CreatedAt)
                    .Select(MapGrabberProject)
                    .ToList(),
            };
        }
    }

    public async Task<GrabberProjectResult> CreateGrabberProjectAsync(
        GrabberProjectCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!TryValidateUrl(request.Request.StartUrl, out var uri, out var urlError))
        {
            return new GrabberProjectResult { Ok = false, Error = urlError };
        }

        var now = _timeProvider.GetUtcNow();
        var project = new GrabberProject
        {
            Id = Guid.NewGuid(),
            Name = string.IsNullOrWhiteSpace(request.Name)
                ? $"Grab {uri.Host}"
                : request.Name.Trim(),
            StartUrl = uri.AbsoluteUri,
            DestinationDirectory = string.IsNullOrWhiteSpace(request.Request.DestinationDirectory)
                ? null
                : request.Request.DestinationDirectory.Trim(),
            MaxDepth = Math.Clamp(request.Request.MaxDepth, 0, 5),
            MaxPages = Math.Clamp(request.Request.MaxPages, 1, 500),
            SameHostOnly = request.Request.SameHostOnly,
            Extensions = request.Request.Extensions
                .Select(value => value.Trim().TrimStart('.'))
                .Where(value => value.Length > 0)
                .ToList(),
            IncludePatterns = request.Request.IncludePatterns
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .ToList(),
            ExcludePatterns = request.Request.ExcludePatterns
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .ToList(),
            SavePages = request.Request.SavePages,
            RewriteLinks = request.Request.RewriteLinks,
            RespectRobotsTxt = request.Request.RespectRobotsTxt,
            StartMode = request.Request.StartMode,
            QueueId = request.Request.QueueId,
            Connections = request.Request.Connections,
            Status = "pending",
            CreatedAt = now,
            UpdatedAt = now,
        };

        lock (_sync)
        {
            _grabberProjects.Add(project);
        }

        if (_grabberProjectStore is not null)
        {
            await _grabberProjectStore.SaveAsync(project.Clone(), cancellationToken).ConfigureAwait(false);
        }

        return new GrabberProjectResult { Ok = true, Project = MapGrabberProject(project) };
    }

    public async Task<GrabberProjectResult> RunGrabberProjectAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        GrabberProject project;
        GrabberProject snapshot;
        lock (_sync)
        {
            var existing = _grabberProjects.FirstOrDefault(candidate => candidate.Id == id);
            if (existing is null)
            {
                return new GrabberProjectResult { Ok = false, Error = "Grabber project not found." };
            }

            if (!_runningGrabberProjects.Add(id))
            {
                return new GrabberProjectResult
                {
                    Ok = false,
                    Error = "This grabber project is already running.",
                    Project = MapGrabberProject(existing),
                };
            }

            existing.Status = "running";
            existing.LastRunAt = _timeProvider.GetUtcNow();
            existing.UpdatedAt = existing.LastRunAt.Value;
            existing.LastError = null;
            existing.PagesCrawled = 0;
            existing.FilesQueued = 0;
            existing.FilesSkipped = 0;
            project = existing;
            snapshot = existing.Clone();
        }

        if (_grabberProjectStore is not null)
        {
            await _grabberProjectStore.SaveAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }

        var lastProgressSave = DateTimeOffset.MinValue;
        var run = new SiteGrabberResult();
        try
        {
            run = await RunSiteGrabberAsync(
                ToGrabberRequest(snapshot),
                cancellationToken,
                (pages, queued, skipped) =>
                {
                    GrabberProject? save = null;
                    lock (_sync)
                    {
                        project.PagesCrawled = pages;
                        project.FilesQueued = queued;
                        project.FilesSkipped = skipped;
                        project.UpdatedAt = _timeProvider.GetUtcNow();
                        if (project.UpdatedAt - lastProgressSave >= TimeSpan.FromSeconds(2))
                        {
                            lastProgressSave = project.UpdatedAt;
                            save = project.Clone();
                        }
                    }

                    if (save is not null)
                    {
                        _ = SaveGrabberProjectQuietlyAsync(save);
                    }
                }).ConfigureAwait(false);

            lock (_sync)
            {
                project.PagesCrawled = run.PagesCrawled;
                project.FilesQueued = run.FilesQueued;
                project.FilesSkipped = run.FilesSkipped;
                project.LastError = run.Ok
                    ? null
                    : run.Error ?? (run.Errors.Count > 0 ? run.Errors[0] : "The grabber failed.");
                project.Status = run.Ok ? "completed" : "failed";
                project.UpdatedAt = _timeProvider.GetUtcNow();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lock (_sync)
            {
                project.Status = "interrupted";
                project.LastError = "The grabber was cancelled.";
                project.UpdatedAt = _timeProvider.GetUtcNow();
            }

            run = new SiteGrabberResult { Ok = false, Error = project.LastError };
        }
        catch (Exception ex)
        {
            lock (_sync)
            {
                project.Status = "failed";
                project.LastError = ex.Message;
                project.UpdatedAt = _timeProvider.GetUtcNow();
            }

            run = new SiteGrabberResult { Ok = false, Error = ex.Message };
        }
        finally
        {
            lock (_sync)
            {
                _runningGrabberProjects.Remove(id);
            }
        }

        GrabberProjectDto dto;
        lock (_sync)
        {
            dto = MapGrabberProject(project);
        }

        if (_grabberProjectStore is not null)
        {
            await _grabberProjectStore.SaveAsync(project.Clone(), cancellationToken).ConfigureAwait(false);
        }

        _log?.Invoke(
            $"Grabber project '{project.Name}' {project.Status}: {project.PagesCrawled} pages, " +
            $"{project.FilesQueued} queued, {project.FilesSkipped} skipped.");
        return new GrabberProjectResult
        {
            Ok = run.Ok,
            Error = run.Ok ? null : run.Error,
            Project = dto,
            Run = run,
        };
    }

    public async Task<GrabberProjectResult> RemoveGrabberProjectAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        GrabberProject? removed = null;
        lock (_sync)
        {
            var project = _grabberProjects.FirstOrDefault(candidate => candidate.Id == id);
            if (project is null)
            {
                return new GrabberProjectResult { Ok = false, Error = "Grabber project not found." };
            }

            if (_runningGrabberProjects.Contains(id))
            {
                return new GrabberProjectResult
                {
                    Ok = false,
                    Error = "Stop the running grabber before removing its project.",
                    Project = MapGrabberProject(project),
                };
            }

            _grabberProjects.Remove(project);
            removed = project;
        }

        if (_grabberProjectStore is not null)
        {
            await _grabberProjectStore.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
        }

        return new GrabberProjectResult { Ok = true, Project = removed is null ? null : MapGrabberProject(removed) };
    }

    private async Task SaveGrabberProjectQuietlyAsync(GrabberProject project)
    {
        if (_grabberProjectStore is null)
        {
            return;
        }

        try
        {
            await _grabberProjectStore.SaveAsync(project, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"Could not save grabber progress: {ex.Message}");
        }
    }

    private static SiteGrabberRequest ToGrabberRequest(GrabberProject project) => new()
    {
        StartUrl = project.StartUrl,
        DestinationDirectory = project.DestinationDirectory,
        MaxDepth = project.MaxDepth,
        MaxPages = project.MaxPages,
        SameHostOnly = project.SameHostOnly,
        Extensions = new List<string>(project.Extensions),
        IncludePatterns = new List<string>(project.IncludePatterns),
        ExcludePatterns = new List<string>(project.ExcludePatterns),
        SavePages = project.SavePages,
        RewriteLinks = project.RewriteLinks,
        RespectRobotsTxt = project.RespectRobotsTxt,
        StartMode = project.StartMode,
        QueueId = project.QueueId,
        Connections = project.Connections,
    };

    private static GrabberProjectDto MapGrabberProject(GrabberProject project) => new()
    {
        Id = project.Id,
        Name = project.Name,
        Request = ToGrabberRequest(project),
        Status = project.Status,
        PagesCrawled = project.PagesCrawled,
        FilesQueued = project.FilesQueued,
        FilesSkipped = project.FilesSkipped,
        LastError = project.LastError,
        CreatedAt = project.CreatedAt,
        UpdatedAt = project.UpdatedAt,
        LastRunAt = project.LastRunAt,
    };

    public async Task<JobActionResponse> UpdateJobAsync(
        JobUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        JobRuntime? runtime;
        string? moveFrom = null;
        string? moveTo = null;
        lock (_sync)
        {
            if (!_jobs.TryGetValue(request.JobId, out runtime))
            {
                return new JobActionResponse { Ok = false, Error = "Job not found." };
            }

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

        await PersistAsync(runtime, cancellationToken).ConfigureAwait(false);
        Raise(runtime, "updated");
        return new JobActionResponse { Ok = true, Job = Map(runtime.Job) };
    }

    public async Task<JobActionResponse> MoveToQueueAsync(
        Guid id,
        int queueId,
        CancellationToken cancellationToken = default)
    {
        JobRuntime? runtime;
        DownloadQueue? queue;
        var paused = false;
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

        await PersistAsync(runtime, cancellationToken).ConfigureAwait(false);
        Raise(runtime, paused ? "paused" : "updated");
        Schedule();
        return new JobActionResponse { Ok = true, Job = Map(runtime.Job) };
    }

    public async Task<JobActionResponse> RefreshUrlAsync(
        Guid id,
        string newUrl,
        CancellationToken cancellationToken = default)
    {
        if (!TryValidateUrl(newUrl, out var uri, out var urlError))
        {
            return new JobActionResponse { Ok = false, Error = urlError };
        }

        JobRuntime? runtime;
        lock (_sync)
        {
            if (!_jobs.TryGetValue(id, out runtime))
            {
                return new JobActionResponse { Ok = false, Error = "Job not found." };
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

            runtime.PauseRequested = false;
            runtime.CancelRequested = false;
            runtime.CancelledForRemoval = false;
            runtime.Job.Url = uri.AbsoluteUri;
            runtime.Job.FinalUrl = null;
            runtime.Job.State = DownloadState.Queued;
            runtime.Job.StartMode = StartMode.StartNow;
            runtime.Job.Failure = null;
            runtime.Job.CompletedAt = null;
            runtime.Job.AutoResumeAfterRestart = true;
            runtime.Job.Touch();
        }

        await PersistAsync(runtime, cancellationToken).ConfigureAwait(false);
        Raise(runtime, "refreshed");
        Schedule();
        return new JobActionResponse { Ok = true, Job = Map(runtime.Job) };
    }

    /// <summary>
    /// Evaluates the configured one-time/daily queue schedules against a local
    /// timestamp. Public so tests can drive time deterministically; the host calls
    /// it from its 30-second scheduler loop with <see cref="TimeProvider.GetLocalNow"/>.
    /// </summary>
    public async Task<IReadOnlyList<ScheduleAction>> EvaluateSchedulesAsync(
        DateTimeOffset localNow,
        CancellationToken cancellationToken = default)
    {
        var today = DateOnly.FromDateTime(localNow.DateTime);
        var nowTime = TimeOnly.FromDateTime(localNow.DateTime);
        var due = new List<(Guid Id, int QueueId, string Action, Guid? GrabberProjectId)>();

        lock (_sync)
        {
            if (!_initialized || _shuttingDown)
            {
                return Array.Empty<ScheduleAction>();
            }

            foreach (var schedule in _settings.Schedules)
            {
                if (!schedule.Enabled)
                {
                    continue;
                }

                if (schedule.GrabberProjectId is null &&
                    !_settings.Queues.Any(q => q.Id == schedule.QueueId))
                {
                    continue;
                }

                if (schedule.GrabberProjectId is { } configuredProject &&
                    _grabberProjects.All(project => project.Id != configuredProject))
                {
                    continue;
                }

                if (!TryParseScheduleTime(schedule.StartTime, out var start))
                {
                    continue;
                }

                var hasStop = TryParseScheduleTime(schedule.StopTime, out var stop);
                var isOneTime = string.Equals(schedule.Kind, "oneTime", StringComparison.OrdinalIgnoreCase);
                var dayMatches = !isOneTime && MatchesDay(schedule.Days, localNow.DayOfWeek);
                var dateMatches = isOneTime ? schedule.StartDate == today : dayMatches;
                var overnight = hasStop && stop <= start;

                var startDue = dateMatches &&
                               nowTime >= start &&
                               schedule.LastStartDate != today;

                // A same-day window that already closed is missed, not started late.
                if (startDue && hasStop && !overnight && nowTime >= stop)
                {
                    startDue = false;
                }

                // If an overnight window opened yesterday, it is already running;
                // don't start it again after midnight.
                if (startDue && overnight && schedule.LastStartDate == today.AddDays(-1) && nowTime < stop)
                {
                    startDue = false;
                }

                if (startDue)
                {
                    due.Add((schedule.Id, schedule.QueueId, "start", schedule.GrabberProjectId));
                    continue;
                }

                if (schedule.GrabberProjectId is not null)
                {
                    continue;
                }

                if (!hasStop || nowTime < stop || schedule.LastStopDate == today)
                {
                    continue;
                }

                var startedInWindow = overnight
                    ? schedule.LastStartDate == today || schedule.LastStartDate == today.AddDays(-1)
                    : schedule.LastStartDate == today;
                if (startedInWindow)
                {
                    due.Add((schedule.Id, schedule.QueueId, "stop", null));
                }
            }
        }

        var results = new List<ScheduleAction>(due.Count);
        foreach (var action in due)
        {
            lock (_sync)
            {
                if (_shuttingDown)
                {
                    break;
                }
            }

            if (action.GrabberProjectId is Guid projectId)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await RunGrabberProjectAsync(projectId, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _log?.Invoke($"Scheduled grabber project {projectId} failed: {ex}");
                    }
                });

                lock (_sync)
                {
                    var schedule = _settings.Schedules.FirstOrDefault(s => s.Id == action.Id);
                    if (schedule is not null)
                    {
                        schedule.LastStartDate = today;
                    }
                }

                await _settingsStore.SaveAsync(_settings, cancellationToken).ConfigureAwait(false);
                SettingsChanged?.Invoke(this, _settings);
                _log?.Invoke($"Scheduler started grabber project {projectId} for {today:yyyy-MM-dd}.");
                results.Add(new ScheduleAction(action.Id, action.QueueId, "grabber", today, true, null));
                continue;
            }

            var reply = await QueueActionAsync(
                new QueueActionRequest { QueueId = action.QueueId, Action = action.Action },
                cancellationToken).ConfigureAwait(false);

            if (reply.Ok)
            {
                lock (_sync)
                {
                    var schedule = _settings.Schedules.FirstOrDefault(s => s.Id == action.Id);
                    if (schedule is not null)
                    {
                        if (action.Action == "start")
                        {
                            schedule.LastStartDate = today;
                        }
                        else
                        {
                            schedule.LastStopDate = today;
                        }
                    }
                }

                await _settingsStore.SaveAsync(_settings, cancellationToken).ConfigureAwait(false);
                SettingsChanged?.Invoke(this, _settings);
                _log?.Invoke($"Scheduler {action.Action} queue {action.QueueId} for {today:yyyy-MM-dd}.");
            }

            results.Add(new ScheduleAction(
                action.Id,
                action.QueueId,
                action.Action,
                today,
                reply.Ok,
                reply.Error));
        }

        return results;
    }

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
        var schedulerTask = _schedulerTask;
        _schedulerCts?.Cancel();
        lock (_sync)
        {
            _shuttingDown = true;
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
        if (job.ReceivedBytes < 0)
        {
            job.ReceivedBytes = 0;
            changed = true;
        }

        var checkpoint = await _checkpointStore.LoadAsync(job.Id, cancellationToken).ConfigureAwait(false);
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
        var queue = _settings.Queues.FirstOrDefault(q => q.Id == runtime.Job.QueueId);
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
                     r.Job.QueueId == queue.Id &&
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
        try
        {
            await _settingsStore.SaveAsync(_settings, CancellationToken.None).ConfigureAwait(false);
            SettingsChanged?.Invoke(this, _settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"Could not persist quota state: {ex.Message}");
        }
    }

    private void Schedule()
    {
        List<JobRuntime> toStart;
        lock (_sync)
        {
            if (!_initialized || _shuttingDown)
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
                .GroupBy(r => r.Job.QueueId)
                .ToDictionary(g => g.Key, g => g.Count());

            toStart = new List<JobRuntime>();
            foreach (var runtime in _jobs.Values
                         .Where(r => !r.Starting && (r.Runner is null || r.Runner.IsCompleted) &&
                                     r.Job.State == DownloadState.Queued &&
                                     r.Job.StartMode != StartMode.Later)
                         .OrderByDescending(r => r.Job.Priority)
                         .ThenBy(r => r.Job.CreatedAt))
            {
                if (slots <= 0)
                {
                    break;
                }

                var queue = _settings.Queues.FirstOrDefault(q => q.Id == runtime.Job.QueueId) ?? queueDefaults;
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
        CancellationTokenSource cts;
        lock (_sync)
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
            job.State = DownloadState.Connecting;
            job.Failure = null;
            job.StartedAt ??= DateTimeOffset.UtcNow;
            job.CompletedAt = null;
            job.ActiveConnections = 0;
            job.Touch();
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
                if (job.State is DownloadState.Connecting or DownloadState.Queued or DownloadState.RetryWaiting)
                {
                    job.State = DownloadState.Downloading;
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
            var checkpoint = await _checkpointStore.LoadAsync(job.Id, CancellationToken.None).ConfigureAwait(false);
            var engine = job.Url.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase)
                ? _ftpEngine
                : _engine;
            if (engine is null)
            {
                throw new InvalidOperationException("FTP support is not configured.");
            }

            result = await engine.ExecuteAsync(job, checkpoint, progress, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            result = DownloadResult.Fail(DownloadState.Cancelled,
                new JobFailure(FailureKind.Cancelled, "The download was cancelled.", false, true, DateTimeOffset.UtcNow),
                job.ReceivedBytes, job.TotalBytes);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Job {job.Id} crashed: {ex}");
            result = DownloadResult.Fail(DownloadState.Failed,
                new JobFailure(FailureKind.Unknown, ex.Message, false, true, DateTimeOffset.UtcNow),
                job.ReceivedBytes, job.TotalBytes);
        }

        var deletePayload = false;
        string changeType;
        lock (_sync)
        {
            job.ReceivedBytes = result.BytesWritten;
            if (result.TotalBytes is not null)
            {
                job.TotalBytes = result.TotalBytes;
            }

            job.BytesPerSecond = null;
            job.EstimatedRemaining = null;
            job.ActiveConnections = 0;
            job.ActiveConnections = 0;

            if (runtime.CancelRequested)
            {
                job.State = DownloadState.Cancelled;
                job.Failure = new JobFailure(FailureKind.Cancelled, "Cancelled by the user.", false, true, DateTimeOffset.UtcNow);
                deletePayload = runtime.DeleteOnCancel;
                changeType = "cancelled";
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
            else if (result.Success)
            {
                job.State = DownloadState.Completed;
                job.Failure = null;
                job.CompletedAt = DateTimeOffset.UtcNow;
                job.FinalFilePath = result.FinalFilePath ?? job.FinalFilePath;
                job.FileName = Path.GetFileName(job.FinalFilePath);
                job.ETag = result.ETag ?? job.ETag;
                job.LastModified = result.LastModified ?? job.LastModified;
                job.ContentType = result.ContentType ?? job.ContentType;
                changeType = "completed";
            }
            else
            {
                job.State = DownloadState.Failed;
                job.Failure = result.Failure ?? new JobFailure(FailureKind.Unknown,
                    "The download failed for an unknown reason.", false, true, DateTimeOffset.UtcNow);
                changeType = "failed";
            }

            job.Touch();
        }

        if (deletePayload)
        {
            DeleteFileQuietly(job.PartFilePath);
            await _checkpointStore.DeleteAsync(job.Id, CancellationToken.None).ConfigureAwait(false);
        }

        await PersistAsync(runtime, CancellationToken.None).ConfigureAwait(false);
        Raise(runtime, changeType);

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

        await MaybeRunPostDownloadHookAsync(job, changeType).ConfigureAwait(false);
        await MaybeRunCompletionActionsAsync(job, changeType, CancellationToken.None).ConfigureAwait(false);
        Schedule();
    }

    private async Task MaybeRunPostDownloadHookAsync(DownloadJob job, string changeType)
    {
        if (changeType != "completed")
        {
            return;
        }

        var command = _settings.PostDownloadCommand;
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
        CancellationToken cancellationToken)
    {
        if (changeType != "completed")
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
                r.Job.QueueId == job.QueueId &&
                r.Job.State is not (DownloadState.Completed or DownloadState.Failed or DownloadState.Cancelled));
            if (queueUnfinished)
            {
                return;
            }

            _completionFiredQueues.Add(job.QueueId);
            allowSystemActions = _settings.AllowSystemCompletionActions;
            blockedByOtherDownloads = _jobs.Values.Any(r =>
                r.Job.Id != job.Id &&
                r.Job.State is not (DownloadState.Completed or DownloadState.Failed or DownloadState.Cancelled));
        }

        if (schedule.OpenFileWhenDone)
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

    private async Task PersistAsync(JobRuntime runtime, CancellationToken cancellationToken)
    {
        DownloadJob clone;
        lock (_sync)
        {
            if (runtime.CancelledForRemoval)
            {
                return;
            }

            clone = runtime.Job.Clone();
        }

        await _persistGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _jobStore.SaveAsync(clone, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            _log?.Invoke($"Could not persist job {clone.Id}: {ex.Message}");
        }
        finally
        {
            _persistGate.Release();
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

        await _checkpointStore.DeleteAsync(job.Id, cancellationToken).ConfigureAwait(false);
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

    private string ResolveDestination(string? requested, string fileName)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            return Path.GetFullPath(requested.Trim());
        }

        var category = FileCategories.For(fileName);
        if (_settings.CategoryFolders.TryGetValue(category, out var categoryFolder) &&
            !string.IsNullOrWhiteSpace(categoryFolder))
        {
            return Path.GetFullPath(categoryFolder.Trim());
        }

        var directory = _settings.LastDestinationDirectory ?? _settings.DefaultDownloadDirectory;
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

    public static JobDto Map(DownloadJob job)
        => new()
        {
            Id = job.Id,
            Url = job.Url,
            FinalUrl = job.FinalUrl,
            PageUrl = job.PageUrl,
            FileName = job.FileName,
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
            EstimatedRemaining = job.EstimatedRemaining,
            Source = JsonNamingPolicy.CamelCase.ConvertName(job.Source.ToString()),
            ClientRequestId = job.ClientRequestId,
            OwnerExtensionId = job.OwnerExtensionId,
            QueueId = job.QueueId,
            Priority = job.Priority,
            Description = job.Description,
            ExpectedSha256 = job.ExpectedSha256,
            QueueName = job.QueueName,
            StartMode = JsonNamingPolicy.CamelCase.ConvertName(job.StartMode.ToString()),
            ConflictPolicy = JsonNamingPolicy.CamelCase.ConvertName(job.ConflictPolicy.ToString()),
            CanPause = job.CanPause,
            CanResume = job.CanResume,
            IsTerminal = job.IsTerminal,
            Failure = job.Failure is null
                ? null
                : new FailureDto
                {
                    Kind = JsonNamingPolicy.CamelCase.ConvertName(job.Failure.Kind.ToString()),
                    Message = job.Failure.Message,
                    IsRetryable = job.Failure.IsRetryable,
                    IsResumable = job.Failure.IsResumable,
                    OccurredAt = job.Failure.OccurredAt,
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
            BrowserAutoIntercept = settings.BrowserAutoIntercept,
            NotifyOnCompletion = settings.NotifyOnCompletion,
            PlayCompletionSound = settings.PlayCompletionSound,
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
            DefaultQueueId = settings.DefaultQueueId,
            Queues = settings.Queues.Select(q => new QueueDto
            {
                Id = q.Id,
                Name = q.Name,
                MaxConcurrentDownloads = q.MaxConcurrentDownloads,
                IsRunning = q.IsRunning,
                Order = q.Order,
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
                GrabberProjectId = s.GrabberProjectId,
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
                QuotaEnabled = q.QuotaEnabled,
                QuotaBytes = Math.Clamp(q.QuotaBytes, 1, 10L * 1024 * 1024 * 1024 * 1024),
                QuotaWindowHours = Math.Clamp(q.QuotaWindowHours, 1, 24 * 365),
                QuotaWindowStart = q.QuotaWindowStart,
                QuotaBytesUsed = Math.Max(0, q.QuotaBytesUsed),
            }).ToList()
            : existing.Select((q, index) => new DownloadQueue
            {
                Id = q.Id,
                Name = q.Name,
                MaxConcurrentDownloads = q.MaxConcurrentDownloads,
                IsRunning = q.IsRunning,
                Order = index,
                QuotaEnabled = q.QuotaEnabled,
                QuotaBytes = q.QuotaBytes,
                QuotaWindowHours = q.QuotaWindowHours,
                QuotaWindowStart = q.QuotaWindowStart,
                QuotaBytesUsed = q.QuotaBytesUsed,
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
            return new ScheduleRule
            {
                Id = id,
                Name = s.Name,
                QueueId = s.QueueId,
                GrabberProjectId = s.GrabberProjectId,
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
                // Run state is owned by the host scheduler, never by a UI payload.
                LastStartDate = previous?.LastStartDate,
                LastStopDate = previous?.LastStopDate,
            };
        }).ToList();
    }

    private sealed class JobRuntime
    {
        public JobRuntime(DownloadJob job)
        {
            Job = job;
            QuotaReportedBytes = job.ReceivedBytes;
        }

        public DownloadJob Job { get; }
        public long QuotaReportedBytes { get; set; }
        public CancellationTokenSource? Cts { get; set; }
        public Task? Runner { get; set; }
        public bool Starting { get; set; }
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
