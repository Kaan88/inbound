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

public sealed class DownloadManagerService
{
    private readonly StoragePaths _paths;
    private readonly IJobStore _jobStore;
    private readonly IDownloadCheckpointStore _checkpointStore;
    private readonly ISettingsStore _settingsStore;
    private readonly IDownloadEngine _engine;
    private readonly Action<string>? _log;
    private readonly object _sync = new();
    private readonly Dictionary<Guid, JobRuntime> _jobs = new();
    private readonly SemaphoreSlim _persistGate = new(1, 1);
    private AppSettings _settings = new();
    private bool _shuttingDown;
    private bool _initialized;

    public DownloadManagerService(
        StoragePaths paths,
        IJobStore jobStore,
        IDownloadCheckpointStore checkpointStore,
        ISettingsStore settingsStore,
        IDownloadEngine engine,
        Action<string>? log = null)
    {
        _paths = paths;
        _jobStore = jobStore;
        _checkpointStore = checkpointStore;
        _settingsStore = settingsStore;
        _engine = engine;
        _log = log;
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

        _initialized = true;
        Schedule();
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

        var destination = ResolveDestination(request.DestinationDirectory);
        Directory.CreateDirectory(_paths.PartialsDirectory);
        var fileName = string.IsNullOrWhiteSpace(request.FileName)
            ? FileNameResolver.Resolve(null, uri)
            : FileNameResolver.Sanitize(request.FileName);
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
            PartFilePath = Path.Combine(_paths.PartialsDirectory, $"{Guid.NewGuid():N}.part"),
            StartMode = startMode,
            Source = source,
            ClientRequestId = clientRequestId,
            OwnerExtensionId = owner,
            RequestHeaders = headers,
            RequestedConnections = Math.Clamp(request.Connections ?? _settings.DefaultConnections, 1, 32),
            SpeedLimitBytesPerSecond = request.SpeedLimitBytesPerSecond ?? _settings.GlobalSpeedLimitBytesPerSecond,
            Description = request.Description,
            ExpectedSha256 = string.IsNullOrWhiteSpace(request.ExpectedSha256) ? null : request.ExpectedSha256.Trim(),
            ConflictPolicy = conflictPolicy,
            State = DownloadState.Queued,
            AutoResumeAfterRestart = request.AutoResumeAfterRestart ?? _settings.AutoResumeUnfinishedOnStartup,
            TotalBytes = null,
            ReceivedBytes = 0,
        };

        // Use the generated job id for the part path so concurrent/unrelated jobs can never share one.
        job.PartFilePath = Path.Combine(_paths.PartialsDirectory, $"{job.Id:N}.part");

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
        }

        Raise(runtime, "added");
        Schedule();
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
            Theme = settings.Theme,
            LastDestinationDirectory = settings.LastDestinationDirectory,
            ExcludedFileExtensions = settings.ExcludedFileExtensions,
            ExcludedHosts = settings.ExcludedHosts,
            InterceptedFileExtensions = settings.InterceptedFileExtensions,
            DefaultQueueId = settings.DefaultQueueId,
            Queues = MapQueues(settings.Queues, _settings.Queues, settings.MaxConcurrentDownloads),
            Schedules = MapSchedules(settings.Schedules, _settings.Schedules),
        };

        if (!next.Queues.Any(q => q.Id == next.DefaultQueueId))
        {
            next.DefaultQueueId = next.Queues[0].Id;
        }

        await _settingsStore.SaveAsync(next, cancellationToken).ConfigureAwait(false);
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

    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        List<JobRuntime> active;
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

        if (job.ReceivedBytes > 0 && !File.Exists(job.PartFilePath))
        {
            // The partial payload is gone. Resume will start from zero rather than
            // pretend the missing bytes are present.
            job.ReceivedBytes = 0;
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

    private void Schedule()
    {
        List<JobRuntime> toStart;
        lock (_sync)
        {
            if (!_initialized || _shuttingDown)
            {
                return;
            }

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

                job.Touch();
                if (now - lastProgressEvent >= TimeSpan.FromMilliseconds(300))
                {
                    lastProgressEvent = now;
                    dto = Map(job);
                }
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
            result = await _engine.ExecuteAsync(job, checkpoint, progress, cts.Token).ConfigureAwait(false);
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

        lock (_sync)
        {
            runtime.Starting = false;
            runtime.Runner = null;
            runtime.Cts?.Dispose();
            runtime.Cts = null;
            runtime.CancelledForRemoval = false;
        }

        Schedule();
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
        if (includeFinal)
        {
            DeleteFileQuietly(job.FinalFilePath);
        }

        await _checkpointStore.DeleteAsync(job.Id, cancellationToken).ConfigureAwait(false);
    }

    private string ResolveDestination(string? requested)
    {
        var directory = string.IsNullOrWhiteSpace(requested)
            ? _settings.LastDestinationDirectory ?? _settings.DefaultDownloadDirectory
            : requested.Trim();
        return Path.GetFullPath(directory);
    }

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

        if (parsed.Scheme is not ("http" or "https"))
        {
            error = "Only http and https URLs are supported.";
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
            Theme = settings.Theme,
            LastDestinationDirectory = settings.LastDestinationDirectory,
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
            }).ToList(),
            Schedules = settings.Schedules.Select(s => new ScheduleDto
            {
                Id = s.Id,
                Name = s.Name,
                QueueId = s.QueueId,
                Enabled = s.Enabled,
                Days = s.Days,
                StartTime = s.StartTime,
                StopTime = s.StopTime,
            }).ToList(),
        };

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
            }).ToList()
            : existing.Select((q, index) => new DownloadQueue
            {
                Id = q.Id,
                Name = q.Name,
                MaxConcurrentDownloads = q.MaxConcurrentDownloads,
                IsRunning = q.IsRunning,
                Order = index,
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
        if (requested is not { Count: > 0 })
        {
            return existing.ToList();
        }

        return requested.Select(s => new ScheduleRule
        {
            Id = s.Id == Guid.Empty ? Guid.NewGuid() : s.Id,
            Name = s.Name,
            QueueId = s.QueueId,
            Enabled = s.Enabled,
            Days = s.Days,
            StartTime = s.StartTime,
            StopTime = s.StopTime,
        }).ToList();
    }

    private sealed class JobRuntime
    {
        public JobRuntime(DownloadJob job)
        {
            Job = job;
        }

        public DownloadJob Job { get; }
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
