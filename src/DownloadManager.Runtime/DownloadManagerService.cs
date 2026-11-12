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
    private const int MaxManifestBytes = 1024 * 1024;
    internal static string WindowsPowerShellPath => Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
    private readonly SemaphoreSlim _updateInstallGate = new(1, 1);
    private bool _updateInstallScheduled;
    private bool _updateQuiescing;
    public event EventHandler? UpdateShutdownRequested;
    public event EventHandler<UpdateProgressDto>? UpdateProgressChanged;
    private UpdateProgressDto? _updateProgress;
    public event EventHandler<string>? UpdateDeferred;

    /// <summary>UI.R37: raised with the new value (null when none is known any more) when an update check changes it.</summary>
    public event EventHandler<string?>? LatestVersionChanged;
    private string? _latestVersion;
    // The channel and manifest the value came from: after either changes in settings it says nothing until a new check.
    private string? _latestChannel;
    private string? _latestManifestUrl;

    /// <summary>
    /// UI.R37: the newest version the last successful update check found on this Host's own channel, while it runs; null
    /// before one, after updates are turned off or the channel or manifest changes. A failed check leaves it as it was:
    /// it says nothing new about the latest.
    /// </summary>
    public string? LatestVersion
    {
        get { lock (_sync) return CurrentLatestVersionLocked(); }
    }

    private string? CurrentLatestVersionLocked()
        => _latestVersion is not null &&
           !string.IsNullOrWhiteSpace(_settings.UpdateManifestUrl) && !string.IsNullOrWhiteSpace(_settings.UpdatePublicKey) &&
           string.Equals(_latestChannel, _settings.UpdateChannel, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(_latestManifestUrl, _settings.UpdateManifestUrl, StringComparison.Ordinal)
            ? _latestVersion
            : null;

    /// <summary>Records what a check found on the current channel and manifest; null when it found that none is known.</summary>
    private void NoteLatestVersion(string? version)
    {
        lock (_sync)
        {
            _latestVersion = version;
            _latestChannel = _settings.UpdateChannel;
            _latestManifestUrl = _settings.UpdateManifestUrl;
        }
        AnnounceLatestVersion();
    }

    // The value clients last heard, so each change is announced once whether a check or a settings change made it.
    private string? _latestAnnounced;

    private void AnnounceLatestVersion()
    {
        string? current;
        lock (_sync)
        {
            current = CurrentLatestVersionLocked();
            if (string.Equals(current, _latestAnnounced, StringComparison.OrdinalIgnoreCase)) return;
            _latestAnnounced = current;
        }
        LatestVersionChanged?.Invoke(this, current);
    }
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
        IRemoteSecretMatcher? linkSecrets = null,
        ITorrentDownloadEngine? torrentEngine = null)
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
        AttachTorrentEngine(torrentEngine);
        _engine.HostWaitChanged += OnHostWaitChanged;
    }

    public event EventHandler<JobChangeEventArgs>? JobChanged;
    public event EventHandler<AppSettings>? SettingsChanged;

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

    /// <summary>UI.R24. Guarded by <c>_sync</c>; written from progress reports, so it costs nothing while idle.</summary>
    private readonly SpeedHistory _speedHistory = new(DateTimeOffset.UtcNow);

    /// <summary>UI.R24: the last hour of total download speed, for a window that has just connected.</summary>
    /// <summary>F-12: one running download's figures, or an empty history for one that isn't running.</summary>
    public SpeedHistoryDto GetSpeedHistory(Guid jobId)
    {
        lock (_sync)
        {
            return _speedHistory.Read(jobId, DateTimeOffset.UtcNow)
                ?? new SpeedHistoryDto { JobId = jobId, Start = DateTimeOffset.UtcNow, BytesPerSecond = [] };
        }
    }

    public SpeedHistoryDto GetSpeedHistory()
    {
        lock (_sync)
        {
            return _speedHistory.Read(DateTimeOffset.UtcNow);
        }
    }
    public bool TrayAvailable { get; set; }
    public Func<NativeHostRegistrationState>? NativeHostState { get; set; }

    /// <summary>
    /// Where downloads may be written, for a Host whose clients are not the machine's user (the server's
    /// <c>DOWNLOADMANAGER_ROOTS</c>). Every add, move and folder setting is held to these, whatever the client asked for or
    /// a setting says, so a client can't make the server write outside them. Null (the desktop Host) allows any folder.
    /// </summary>
    public PathRoots? Roots { get; set; }
    public string StateDirectory => _paths.Root;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _jobStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
        _settings = await _settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        _settings.Revision = StartingRevision(_timeProvider);
        _engine.UpdateProxy(_settings.Proxy);
        _engine.UpdateSiteLogins(BuildSiteLogins(_settings));
        _engine.UpdateFfmpegPath(_settings.FfmpegPath);
        _engine.UpdateHostConnectionLimit(_settings.MaxConnectionsPerHost);
        // One budget for HTTP, FTP and torrents (T2); the Host gives HTTP and FTP one limiter (PERF.6).
        ApplySpeedLimitsAndRate(_settings);
        ApplyTorrentSeedSettings(_settings);
        ApplyTorrentPeerSettings(_settings);
        await ChooseTorrentPortAsync(cancellationToken).ConfigureAwait(false);
        _torrentEngine?.UpdateListen(_settings.TorrentListenPort, _settings.TorrentUpnp);
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

        bool armsDropped;
        lock (_sync) armsDropped = DropEndedRunArmsLocked();
        if (armsDropped)
        {
            try { await _settingsStore.SaveAsync(_settings, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Dropped in memory all the same; the next settings save writes it.
                _log?.Invoke($"Could not save the schedule arms of ended runs: {ex.Message}");
            }
        }
        await ScrubRecentUrlsAsync(cancellationToken).ConfigureAwait(false);
        ScanOrphanPartFiles(removeEmptyQuarantine: true);
        // A crash can land between a job's final save and its package's, so this may have news.
        await AdoptUntrackedPackagesAsync(cancellationToken).ConfigureAwait(false);
        await AnnouncePackageCompletionsAsync(cancellationToken).ConfigureAwait(false);
        _initialized = true;
        Schedule();
        StartSchedulerLoop();
        ResumeSeedingAfterStart();
    }

    /// <summary>
    /// Free and total space at each folder, measured here on the Host's side, where the files are written. Each folder
    /// has its own time limit, so one drive that doesn't answer leaves the others measured.
    /// </summary>
    /// <param name="caller">A caller limited to some folders gets nothing measured outside them (no figures).</param>
    public async Task<FolderSpaceResult> GetFolderSpaceAsync(FolderSpaceRequest request, CancellationToken cancellationToken, CallerScope? caller = null)
    {
        var paths = (request.Paths ?? new()).Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => path.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(64).ToList();
        var measured = await Task.WhenAll(paths.Select(path => caller is null
                ? FolderSpaceMeter.MeasureAsync(path, TimeSpan.FromSeconds(2), cancellationToken)
                : caller.Roots.Allowed(path) is { } inside
                    ? FolderSpaceMeter.MeasureAsync(inside, TimeSpan.FromSeconds(2), cancellationToken)
                    : Task.FromResult(new FolderSpace(null, null, false))))
            .ConfigureAwait(false);
        return new FolderSpaceResult
        {
            Ok = true,
            Folders = paths.Select((path, index) => new FolderSpaceDto
            {
                Path = path,
                FreeBytes = measured[index].FreeBytes,
                TotalBytes = measured[index].TotalBytes,
                InsideContainer = measured[index].InsideContainer,
            }).ToList(),
        };
    }

    public SnapshotDto GetSnapshot() => GetSnapshot(newest: null);

    /// <param name="newest">
    /// Only this many jobs, newest first (audit 3 N5): the browser popup asks for 20 every second, and the others are
    /// then not mapped at all. Null or not above 0 lists every job.
    /// </param>
    public SnapshotDto GetSnapshot(int? newest)
    {
        lock (_sync)
        {
            // A stable sort: jobs created at the same time keep the order they had, as when every job is listed.
            var ordered = _jobs.Values.OrderByDescending(r => r.Job.CreatedAt);
            var jobs = (newest is > 0 ? ordered.Take(newest.Value) : ordered)
                .Select(r => Map(r.Job))
                .ToList();
            LastSnapshotJobsMapped = jobs.Count;
            return new SnapshotDto
            {
                UpdateProgress = _updateProgress,
                Jobs = jobs,
                Settings = MapSettings(_settings),
                Status = BuildStatusLocked(),
            };
        }
    }

    /// <summary>How many jobs the last snapshot mapped, so a test can see a caller asked for only the newest few.</summary>
    internal int LastSnapshotJobsMapped { get; private set; }

    /// <summary>
    /// The settings alone. <see cref="GetSnapshot"/> maps and sorts every job and reads the browser
    /// registration from the registry, which a caller that only wants a setting should not pay for.
    /// </summary>
    public SettingsDto GetSettings()
    {
        lock (_sync)
        {
            return MapSettings(_settings);
        }
    }

    /// <summary>The Host's status, without the job list (it still reads the browser registration).</summary>
    public HostStatusDto GetStatus()
    {
        lock (_sync)
        {
            return BuildStatusLocked();
        }
    }

    /// <summary>
    /// The number of active downloads, as <see cref="HostStatusDto.ActiveDownloads"/> counts them. The tray asks
    /// at most once a second while anything changes, so this neither copies the job list nor touches the registry.
    /// </summary>
    public int ActiveDownloadCount()
    {
        lock (_sync)
        {
            return CountActiveLocked();
        }
    }

    private int CountActiveLocked() => _jobs.Values.Count(r => r.Job.State is DownloadState.Connecting or DownloadState.Downloading
        or DownloadState.RetryWaiting or DownloadState.Verifying);

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

    /// <summary>
    /// Whether every file of this download is inside the caller's folders. Null when there is no such download. The
    /// paths are read under the lock and checked outside it, because the check resolves links on disk.
    /// </summary>
    public bool? HeldBy(Guid id, CallerScope caller)
    {
        string destination, finalPath, partPath;
        lock (_sync)
        {
            if (!_jobs.TryGetValue(id, out var runtime)) return null;
            (destination, finalPath, partPath) = (runtime.Job.DestinationDirectory, runtime.Job.FinalFilePath, runtime.Job.PartFilePath);
        }

        return caller.Holds(destination, finalPath, partPath);
    }

    private JobActionResponse? RefuseOutsideCaller(Guid id, CallerScope caller)
        => HeldBy(id, caller) switch
        {
            null => new JobActionResponse { Ok = false, Error = "Job not found." },
            false => new JobActionResponse { Ok = false, Error = CallerScope.OutsideJobRefused, Job = GetJob(id) },
            true => null,
        };

    public async Task<FileMetadataResult> ReadFileMetadataAsync(FileMetadataRequest request, CancellationToken cancellationToken = default,
        CallerScope? caller = null)
    {
        if (!TryValidateUrl(request.Url, out var uri, out var error) || uri.Scheme is not ("http" or "https"))
            return new FileMetadataResult { Error = string.IsNullOrWhiteSpace(error) ? "File information is available for HTTP/HTTPS URLs." : error };
        if (caller is not null && CallerScope.DescribeAddress(request.Url) is { } refused) return new FileMetadataResult { Error = refused, AddRefused = true };
        if (!TryCopyHeaders(request.Headers, out var headers, out error)) return new FileMetadataResult { Error = error };
        if (_engine is not IFileMetadataReader reader) return new FileMetadataResult { Error = "This engine cannot preview file information." };
        if (!await _metadataGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return new FileMetadataResult { Error = "Other file information requests are still running. Try again shortly." };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            var metadata = await reader.ReadMetadataAsync(new DownloadJob { Url = uri.AbsoluteUri, RequestHeaders = headers,
                UserAgent = request.UserAgent, Referrer = request.Referrer, PublicNetworkOnly = caller is not null }, timeout.Token).ConfigureAwait(false);
            return new FileMetadataResult { Ok = true, FileName = ServerNameWithoutSecret(metadata.FileName, uri.AbsoluteUri, metadata.FinalUrl), TotalBytes = metadata.TotalBytes,
                ContentType = metadata.ContentType, FinalUrl = MaskIfProviderLink(metadata.FinalUrl) };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new FileMetadataResult { Error = "File information request timed out. You can still start the download." }; }
        catch (Exception ex) when (ex is HttpRequestException or ArgumentException or IOException or InvalidOperationException)
        {
            // The server's answer ("404 (Not Found)") holds no local path, so every caller hears it; the other kinds can.
            return new FileMetadataResult { Error = caller is not null && ex is HttpRequestException ? UrlRedactor.RedactText(ex.Message) : ErrorText(ex, caller) };
        }
        finally { _metadataGate.Release(); }
    }

    /// <param name="caller">A caller that isn't this machine's user (<see cref="CallerScope"/>); null for one who is.</param>
    public async Task<AddJobResponse> AddJobAsync(AddJobRequest request, CancellationToken cancellationToken = default, CallerScope? caller = null)
    {
        if (RefuseReservedPackage(request) is { } reserved) return reserved;
        // Before the name is asked for: that request would already reach the address.
        if (caller is not null && RefuseForCaller(request) is { } refused) return refused;

        // Outside the settings gate: the probe is network-bound and must not block other adds.
        await ResolveServerFileNameAsync(request, cancellationToken, caller).ConfigureAwait(false);
        return await AddWithNameSettledAsync(request, cancellationToken, caller).ConfigureAwait(false);
    }

    /// <summary>The address, the folder as text and the owner name, for a caller that isn't this machine's user.</summary>
    private static AddJobResponse? RefuseForCaller(AddJobRequest request)
    {
        if ((CallerScope.DescribeAddress(request.Url) ?? CallerScope.DescribeAddress(request.FinalUrl)) is { } address)
            return new AddJobResponse { Accepted = false, Error = address, ErrorCode = "local_network" };
        if (!string.IsNullOrWhiteSpace(request.DestinationDirectory) && PathRoots.DescribeUnsafeText(request.DestinationDirectory.Trim()) is { } text)
            return new AddJobResponse { Accepted = false, Error = text, ErrorCode = "invalid_destination" };
        // Its own request ids can't find a download a local client (the browser's native host, say) added.
        if (!string.IsNullOrWhiteSpace(request.OwnerExtensionId))
            request.OwnerExtensionId = CallerScope.OwnerPrefix + request.OwnerExtensionId.Trim();
        return null;
    }

    /// <summary>
    /// The same add, with no request to the address's server for a name. For a link that mustn't be contacted
    /// until the user says so: with <see cref="StartMode.Later"/>, nothing goes out until it is started.
    /// </summary>
    public async Task<AddJobResponse> AddJobWithoutAskingServerAsync(AddJobRequest request, CancellationToken cancellationToken = default)
        => RefuseReservedPackage(request) ?? await AddWithNameSettledAsync(request, cancellationToken).ConfigureAwait(false);

    /// <summary>A provider's package counts its files and drives what follows them, so no client may join one.</summary>
    private static AddJobResponse? RefuseReservedPackage(AddJobRequest request)
        => request.PackageKey?.Trim().StartsWith(RemotePackagePrefix, StringComparison.OrdinalIgnoreCase) == true
            ? new AddJobResponse { Accepted = false, Error = "That package key is reserved.", ErrorCode = "invalid_package" }
            : null;

    private async Task<AddJobResponse> AddWithNameSettledAsync(AddJobRequest request, CancellationToken cancellationToken, CallerScope? caller = null)
    {
        await _settingsUpdateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (RefuseIfPackageFull(request) is { } refused) return refused;

            // In its package before anyone hears of it or it can start: what opens a window or tells of a
            // finish looks the package up then, and a small first file could otherwise finish alone.
            var result = await AddJobCoreAsync(request, cancellationToken,
                beforeAnnounce: id => AssignPackageAfterAddAsync(id, request, cancellationToken), caller: caller).ConfigureAwait(false);

            // A repeat too, because a crash can land between saving the job and saving its package.
            if (result is { Accepted: true, Duplicate: true, Job: { } repeated })
            {
                await AssignPackageAfterAddAsync(repeated.Id, request, cancellationToken).ConfigureAwait(false);
                if (GetJob(repeated.Id)?.State == "completed") RequestPackageCheck();
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
    private async Task ResolveServerFileNameAsync(AddJobRequest request, CancellationToken cancellationToken, CallerScope? caller = null)
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
                PublicNetworkOnly = caller is not null,
            }, timeout.Token).ConfigureAwait(false);
            var serverName = ServerNameWithoutSecret(metadata.FileName, uri.AbsoluteUri, metadata.FinalUrl);
            // The address's own last part is no answer: left unset, the transfer can still find the real name.
            if (serverName is not null && !string.Equals(serverName, "download", StringComparison.OrdinalIgnoreCase) &&
                serverName != FileNameResolver.Resolve(null, uri))
                request.FileName = serverName;
        }
        // Any probe failure (timeout, HTTP error, refused range) falls back to the URL name, and the transfer may name it later.
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Never the address: the kind and the status are all that says why.
            var status = ex is HttpRequestException { StatusCode: { } code } ? $", HTTP {(int)code}" : string.Empty;
            _log?.Invoke($"The server's name for a new download wasn't read ({ex.GetType().Name}{status}).");
        }
        finally { _addProbeGate.Release(); }
    }

    private async Task<AddJobResponse> AddJobCoreAsync(
        AddJobRequest request,
        CancellationToken cancellationToken,
        RemoteAddContext? remote = null,
        Func<Guid, Task>? beforeAnnounce = null,
        CallerScope? caller = null)
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

        // A mistyped start mode ("--start latr") is refused, not read as "start now" (audit 1, C-3).
        if (StartModes.Normalize(request.StartMode) is not { } startModeName)
            return new AddJobResponse { Accepted = false, Error = "Unknown start mode. Use now, queue or later.", ErrorCode = "invalid_start_mode" };
        var startMode = Enum.Parse<StartMode>(startModeName, ignoreCase: true);
        var source = ParseEnum(request.Source, DownloadSource.Api);
        if (source == DownloadSource.Remote && remote is null)
        {
            source = DownloadSource.Api;
        }

        // D8: whatever a caller from another device calls it (an add, an import, a browser hand-off through the bridge),
        // it is marked as theirs, so it opens no window on this PC's screen. Its source stays what it asked for, which
        // still decides the rest. A provider's file (Remote) opens none anyway.
        var fromAnotherDevice = caller is not null && source != DownloadSource.Remote;

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
        // A caller limited to the app's folders never gets the last folder: it can be anywhere.
        var destination = ResolveDestination(request.DestinationDirectory, fileName,
            useLastFolder: !browserDownload && caller is null, useCategoryFolder: !browserDownload, categoryId: categoryId);
        if ((caller?.Roots ?? Roots) is { } roots)
        {
            // Checked on the resolved folder, which is also the one used: a link inside a root can't lead the file out.
            if (roots.Allowed(destination) is not { } inside)
                return new AddJobResponse { Accepted = false, Error = roots.OutsideTextFor(destination), ErrorCode = "invalid_destination" };
            destination = inside;
        }

        string finalPath;
        List<string> madeFolders;
        try
        {
            // UI.B19: a provider file's path folders below its package's folder are recorded as made for it.
            madeFolders = CreateFolderRecordingMade(destination, remote?.GivenFolder);
            finalPath = ResolveFinalPath(destination, fileName, conflictPolicy);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new AddJobResponse
            {
                Accepted = false,
                Error = caller is null ? $"Cannot use destination '{destination}': {ex.Message}" : ErrorText(ex, caller),
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
            NameFromAddress = remote is null && string.IsNullOrWhiteSpace(request.FileName),
            CategoryId = categoryId,
            CategoryIsAutomatic = string.IsNullOrWhiteSpace(request.CategoryId),
            DestinationDirectory = destination,
            MadeFoldersBase = remote?.GivenFolder,
            MadeFolders = madeFolders,
            FinalFilePath = finalPath,
            PartFilePath = string.Empty,
            StartMode = startMode,
            QueueProcessing = startMode == StartMode.Queue,
            Source = source,
            AddedFromAnotherDevice = fromAnotherDevice,
            QuietWindows = request.QuietWindows == true,
            ClientRequestId = clientRequestId,
            OwnerExtensionId = owner,
            PublicNetworkOnly = caller is not null,
            RequestHeaders = headers,
            RequestedConnections = Math.Clamp(
                Math.Min(
                    Math.Min(request.Connections ?? _settings.DefaultConnections, EffectiveConnectionLimit(uri)),
                    providerCap),
                1, 32),
            // The global limit is shared by all downloads in the engine (UI.B5), not copied into each one.
            SpeedLimitBytesPerSecond = request.SpeedLimitBytesPerSecond,
            Description = request.Description,
            ContentType = string.IsNullOrWhiteSpace(request.ContentType) ? null : request.ContentType.Trim(),
            HandoffContentType = string.IsNullOrWhiteSpace(request.ContentType) ? null : request.ContentType.Trim(),
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
                Error = caller is null ? $"Could not persist the download: {ex.Message}" : ErrorText(ex, caller),
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
                        Error = caller is null ? $"Could not persist the download: {ex.Message}" : ErrorText(ex, caller),
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

        if (beforeAnnounce is not null) await beforeAnnounce(job.Id).ConfigureAwait(false);
        Raise(runtime, "added");
        Schedule();

        var settingsChanged = false;
        if (!browserDownload && request.RememberDestination != false &&
            !string.IsNullOrWhiteSpace(request.DestinationDirectory))
        {
            lock (_sync)
            {
                // UI.B6: a category's folder changes only when the add asks for it. Re-adds, imports and other
                // clients that don't say leave the Options value alone; the manual-download folder is still remembered.
                if (!string.IsNullOrWhiteSpace(request.CategoryId))
                {
                    if (request.RememberDestination == true)
                    {
                        var custom = _settings.CustomCategories.FirstOrDefault(category => category.Id == categoryId);
                        if (custom is not null) custom.DestinationDirectory = destination;
                        // A new dictionary, not a change to the one a save may be writing out (audit 2 M19).
                        else _settings.CategoryFolders = new Dictionary<string, string>(_settings.CategoryFolders, StringComparer.OrdinalIgnoreCase)
                            { [categoryId!] = destination };
                    }
                }
                else _settings.LastDestinationDirectory = destination;
                _settings.RecentDestinationDirectories = DownloadFolders.Remember(_settings.RecentDestinationDirectories, destination);
            }
            settingsChanged = true;
        }

        if (source is not (DownloadSource.Import or DownloadSource.CliImport or DownloadSource.Remote) && protectedUrl is null)
        {
            lock (_sync)
            {
                // A new list, not a change to the one a save may be writing out (audit 2 M19).
                var recent = new List<string>(_settings.RecentUrls ?? new List<string>());
                recent.RemoveAll(url =>
                    string.Equals(url, job.Url, StringComparison.OrdinalIgnoreCase));
                recent.Insert(0, job.Url);
                if (recent.Count > 20)
                {
                    recent.RemoveRange(20, recent.Count - 20);
                }
                _settings.RecentUrls = recent;
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

    /// <summary>
    /// Whether a queue start tries this failed download again: the queue would have retried it by itself. A retry that
    /// is already waiting was counted when it was set, so it runs even when that used the last one.
    /// </summary>
    /// <summary>
    /// A torrent failure that trying again cannot fix (access denied, a path too long, a file list that can't be used).
    /// The torrent engine has no retry of its own, so a queue must not retry these either. HTTP keeps its own rules.
    /// </summary>
    private static bool TorrentFailureIsFinal(DownloadJob job) => job.Torrent is not null && job.Failure is { IsRetryable: false };

    private static bool QueueRetriesFailedLocked(DownloadJob job, DownloadQueue queue)
        => job.State == DownloadState.Failed && !job.IsTerminal && job.QueueProcessing &&
           job.Failure?.Kind != FailureKind.VerificationFailed && !TorrentFailureIsFinal(job) &&
           (job.QueueRetryAt is not null || queue.RetryLimit is null || job.QueueRetryCount < queue.RetryLimit);

    /// <summary>
    /// A stop from the user, a queue or its quota. It wins over a lower limit's requeue and over a queue
    /// start that is still winding down, so the runner's exit leaves the job Paused instead of Queued.
    /// </summary>
    private static void RequestStopLocked(JobRuntime runtime)
    {
        runtime.PauseRequested = true;
        runtime.Preempted = false;
        runtime.ResumeWhenStopped = false;
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

            RequestStopLocked(runtime);

            runtime.Job.QueueRetryAt = null;
            runtime.Job.PausedByUser = true;
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
            runtime.Job.PausedByUser = false;
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
        Guid? cancelledByPackage = null,
        CallerScope? caller = null)
    {
        if (deleteFile && caller is not null && RefuseOutsideCaller(id, caller) is { } outside) return outside;
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
            runtime.CancelledBy = caller;
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
            // UI.B19. Its data is gone, so the folders made for it aren't needed until it starts again, which makes
            // them again. One still stopping takes them away itself once it has stopped (it could still be writing).
            if (!IsRunning(runtime)) await RemoveEmptyFoldersMadeForAsync(runtime.Job, caller, stillListed: true).ConfigureAwait(false);
        }

        await PersistAsync(runtime, cancellationToken).ConfigureAwait(false);
        Raise(runtime, "cancelled");
        Schedule();
        return new JobActionResponse { Ok = true, Job = Map(runtime.Job) };
    }

    public async Task<JobActionResponse> RemoveAsync(
        Guid id,
        bool deleteFile,
        CancellationToken cancellationToken = default,
        CallerScope? caller = null)
    {
        if (deleteFile && caller is not null && RefuseOutsideCaller(id, caller) is { } outside) return outside;
        await _settingsUpdateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            bool inPackage;
            lock (_sync)
            {
                if (!_jobs.ContainsKey(id)) return new JobActionResponse { Error = "Job not found." };
                inPackage = _settings.DownloadGroups.Any(group => group.JobIds.Contains(id));
            }

            var removed = await RemoveCoreAsync(id, deleteFile, cancellationToken, caller).ConfigureAwait(false);
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
        CancellationToken cancellationToken = default,
        CallerScope? caller = null)
    {
        if (ids.Count > 1000) return new JobActionResponse { Error = "Remove at most 1000 downloads at a time." };
        var unique = ids.Distinct().ToList();
        // All or none, before any of them is stopped. One already gone is skipped below, as it is for any caller.
        if (deleteFile && caller is not null && unique.Where(id => HeldBy(id, caller) == false).Take(1).ToList() is [var outsideId])
            return new JobActionResponse { Ok = false, Error = CallerScope.OutsideJobRefused, Job = GetJob(outsideId) };
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
                    runtime.DeleteOnCancel = deleteFile;
                    runtime.Cts?.Cancel();
                }
            }
        }

        var failures = new List<string>();
        try
        {
            foreach (var id in unique)
            {
                var result = await RemoveAsync(id, deleteFile, cancellationToken, caller).ConfigureAwait(false);
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

    private async Task<JobActionResponse> RemoveCoreAsync(Guid id, bool deleteFile, CancellationToken cancellationToken,
        CallerScope? caller = null)
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
            // What the runner does with the partial data as it stops: the same as for one that had already
            // stopped, so keeping it doesn't depend on whether the transfer was still winding down (UI.B19).
            runtime.DeleteOnCancel = deleteFile;
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

        // A seeding torrent leaves the torrent engine first, so its files are no longer open (T2).
        await StopTorrentSeedingAsync(runtime).ConfigureAwait(false);

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
            return new JobActionResponse { Error = "The stopped download was retained because its statistics could not be saved. " + ErrorText(ex, caller), Job = Map(runtime.Job) };
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
        if (runtime.Job.Torrent is not null) await DeleteTorrentDataQuietlyAsync(id).ConfigureAwait(false);
        // UI.B19. After it is off the list, so its own folder no longer counts as in use. With its data kept,
        // the folder still holds that data and stays. A caller limited to some folders removes none outside them.
        await RemoveEmptyFoldersMadeForAsync(runtime.Job, caller, stillListed: false).ConfigureAwait(false);
        AnnounceJobChange(Map(runtime.Job), "removed");
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
                RequestStopLocked(runtime);
                runtime.Job.QueueRetryAt = null;
                runtime.Job.PausedByUser = true;
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

        // Stop All stops seeding too (T2).
        StopSeedingWhere(_ => true);
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
            // Audit 2 L13: what was paused or failed and can go on. A download the user cancelled stays cancelled, and
            // one that failed for good (DRM, a live stream) stays failed; each can still be resumed on its own.
            resumed = _jobs.Values
                .Where(r => !r.Starting && r.Runner is not { IsCompleted: false } && !r.Job.RemovalPending &&
                            r.Job.State is DownloadState.Paused or DownloadState.Failed && r.Job.CanResume)
                .ToList();
            foreach (var runtime in resumed)
            {
                // As a single Resume does.
                runtime.PauseRequested = false;
                runtime.CancelRequested = false;
                runtime.CancelledForRemoval = false;
                runtime.Job.QueueRetryAt = null;
                runtime.Job.PausedByUser = false;
                runtime.Job.State = DownloadState.Queued;
                runtime.Job.StartMode = StartMode.StartNow;
                runtime.Job.Failure = null;
                runtime.Job.CompletedAt = null;
                runtime.Job.CancelledByPackageId = null;
                runtime.Job.AutoResumeAfterRestart = true;
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

    /// <param name="caller">A caller that isn't this machine's user; null for one who is.</param>
    public async Task<SettingsResult> UpdateSettingsAsync(
        SettingsDto settings,
        CancellationToken cancellationToken = default,
        CallerScope? caller = null)
    {
        await _settingsUpdateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await UpdateSettingsCoreAsync(settings, cancellationToken, caller).ConfigureAwait(false);
            // UI.R37: another channel or manifest, or updates turned off, leaves no latest version known.
            if (result.Ok) AnnounceLatestVersion();
            return result;
        }
        finally { _settingsUpdateGate.Release(); }
    }

    private async Task<SettingsResult> UpdateSettingsCoreAsync(SettingsDto settings, CancellationToken cancellationToken, CallerScope? caller = null)
    {
        // T14: a client from before the torrent folders sends neither, and keeps the Host's.
        KeepUnsentTorrentFolders(settings, _settings);
        if (!string.IsNullOrWhiteSpace(settings.TorrentCategoryFoldersRoot))
        {
            try
            {
                if (!Path.IsPathFullyQualified(settings.TorrentCategoryFoldersRoot.Trim())) throw new ArgumentException("not a full path");
                settings.TorrentCategoryFoldersRoot = Path.GetFullPath(settings.TorrentCategoryFoldersRoot.Trim());
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
            {
                return new SettingsResult { Ok = false, Error = "The torrent folder must be a full path, or empty for the same folders as other downloads.", Settings = MapSettings(_settings) };
            }
        }

        settings.EventSoundFiles ??= new(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in settings.EventSoundFiles)
        {
            if (key is not ("completed" or "failed" or "queueFinished") || value is null ||
                (value.Length > 0 && value != "@system" &&
                 (!Path.IsPathFullyQualified(value) || !string.Equals(Path.GetExtension(value), ".wav", StringComparison.OrdinalIgnoreCase))))
                return new SettingsResult { Ok = false, Error = "Event sounds must be disabled, system sounds, or absolute WAV file paths.", Settings = MapSettings(_settings) };
        }
        // The same rule as a download's own completion program. Only a changed command is refused, so a save of other
        // settings still goes through with one kept from before (it is refused when it would run).
        if (!string.IsNullOrWhiteSpace(settings.PostDownloadCommand) &&
            !string.Equals(settings.PostDownloadCommand.Trim(), _settings.PostDownloadCommand?.Trim(), StringComparison.Ordinal) &&
            !PostDownloadCommandLine.IsAllowedProgram(settings.PostDownloadCommand.Trim()))
            return new SettingsResult { Ok = false, Error = PostDownloadCommandLine.ProgramRefused, Settings = MapSettings(_settings) };
        if ((settings.Queues ?? new()).Any(queue => queue.RetryLimit is < 0 or > 100) ||
            (settings.Schedules ?? new()).Any(schedule => schedule.RepeatEveryMinutes is < 1 or > 1440 ||
                schedule.RepeatEveryMinutes is not null && !schedule.TimedStartEnabled ||
                !schedule.StartOnHostStartup && !schedule.TimedStartEnabled))
            return new SettingsResult { Error = "Select a startup or timed trigger, synchronization interval 1–1440 minutes, and retry limit 0–100 or unlimited.", Settings = MapSettings(_settings) };
        // Audit L12: a time the scheduler can't read would be kept and never act. Only a new or changed one is refused,
        // so a save of other settings still goes through with one kept from before.
        if ((settings.Schedules ?? new()).Any(schedule => schedule is not null &&
                (!TryParseScheduleTime(schedule.StartTime, out _) && !ScheduleTimeKept(schedule.Id, schedule.StartTime, stop: false) ||
                 !string.IsNullOrWhiteSpace(schedule.StopTime) && !TryParseScheduleTime(schedule.StopTime, out _) &&
                 !ScheduleTimeKept(schedule.Id, schedule.StopTime, stop: true))))
            return new SettingsResult { Error = "Enter schedule times as hours and minutes, such as 09:00.", Settings = MapSettings(_settings) };
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

        if (Roots is { } roots && DescribeFolderOutsideRoots(settings, categories, roots) is { } outside)
        {
            return new SettingsResult { Ok = false, Error = outside, Settings = MapSettings(_settings) };
        }

        string directory;
        try
        {
            directory = Path.GetFullPath(settings.DefaultDownloadDirectory);
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new SettingsResult { Ok = false, Error = ErrorText(ex, caller), Settings = MapSettings(_settings) };
        }

        settings.DefaultDownloadDirectory = directory;
        settings.MaxConcurrentDownloads = Math.Clamp(settings.MaxConcurrentDownloads, 1, 16);
        settings.DefaultConnections = Math.Clamp(settings.DefaultConnections, 1, 32);
        settings.MaxConnectionsPerHost = Math.Clamp(settings.MaxConnectionsPerHost, 1, 64);
        if (settings.GlobalSpeedLimitBytesPerSecond is <= 0)
        {
            settings.GlobalSpeedLimitBytesPerSecond = null;
        }
        if (settings.TorrentListenPort != 0 && !TorrentListenPorts.IsValid(settings.TorrentListenPort))
            return new SettingsResult { Ok = false, Error = $"The torrent port must be {TorrentListenPorts.Min} to {TorrentListenPorts.Max}.", Settings = MapSettings(_settings) };

        if (TorrentSeedSettingsError(settings) is { } seedError)
        {
            return new SettingsResult { Ok = false, Error = seedError, Settings = MapSettings(_settings) };
        }

        settings.TorrentEncryption = settings.TorrentEncryption?.Trim().ToLowerInvariant() ?? string.Empty;
        if (TorrentPeerSettingsError(settings, MapProxy(settings.Proxy)) is { } peerError)
        {
            return new SettingsResult { Ok = false, Error = peerError, Settings = MapSettings(_settings) };
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
            UseCategoryFolders = settings.UseCategoryFolders,
            CloseWindowCompletely = settings.CloseWindowCompletely,
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
            TorrentsEnabled = settings.TorrentsEnabled,
            TorrentRoute = TorrentRoutes.IsKnown(settings.TorrentRoute) ? settings.TorrentRoute : TorrentRoutes.Ask,
            TorrentChooseFiles = settings.TorrentChooseFiles,
            UseTorrentCategoryFolders = settings.UseTorrentCategoryFolders ?? true,
            TorrentCategoryFoldersRoot = string.IsNullOrWhiteSpace(settings.TorrentCategoryFoldersRoot) ? null : settings.TorrentCategoryFoldersRoot,
            TorrentHandler = settings.TorrentHandler,
            TorrentSeedAfterDownload = settings.TorrentSeedAfterDownload,
            TorrentSeedRatio = settings.TorrentSeedRatio,
            TorrentSeedHours = settings.TorrentSeedHours,
            TorrentUploadLimit = settings.TorrentUploadLimit,
            TorrentListenPort = NextTorrentPort(settings),
            TorrentUpnp = settings.TorrentUpnp,
            TorrentDht = settings.TorrentDht,
            TorrentPex = settings.TorrentPex,
            TorrentLsd = settings.TorrentLsd,
            TorrentEncryption = settings.TorrentEncryption,
            TorrentSeedsAtOnce = settings.TorrentSeedsAtOnce,
            TorrentConnectionsPerTorrent = settings.TorrentConnectionsPerTorrent,
            TorrentMaxConnections = settings.TorrentMaxConnections,
            TorrentUseProxy = settings.TorrentUseProxy,
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
            // Host-owned: a client's copy may be older than the last add. A folder a client remembers as the last
            // one, as a provider's add does, goes on it.
            RecentDestinationDirectories = !string.IsNullOrWhiteSpace(settings.LastDestinationDirectory) &&
                                           !string.Equals(settings.LastDestinationDirectory, _settings.LastDestinationDirectory, StringComparison.OrdinalIgnoreCase)
                ? DownloadFolders.Remember(_settings.RecentDestinationDirectories, settings.LastDestinationDirectory)
                : new List<string>(_settings.RecentDestinationDirectories),
            FavoriteFolders = DownloadFolders.Normalize((settings.FavoriteFolders ?? new()).Select(favorite =>
                new FavoriteFolder { Name = favorite.Name, Path = favorite.Path })),
            // A client's copy can predate a key being stored, so the same rule as the scrub applies to every save.
            RecentUrls = (settings.RecentUrls ?? new List<string>()).Where(url => !HoldsProviderSecret(url)).ToList(),
            CategoryFolders = new Dictionary<string, string>(
                settings.CategoryFolders ?? new Dictionary<string, string>(),
                StringComparer.OrdinalIgnoreCase),
            ExcludedFileExtensions = new List<string>(settings.ExcludedFileExtensions ?? new()),
            ExcludedHosts = new List<string>(settings.ExcludedHosts ?? new()),
            InterceptedFileExtensions = new List<string>(settings.InterceptedFileExtensions ?? new()),
            Proxy = MapProxy(settings.Proxy),
            SiteLogins = MapSiteLogins(settings.SiteLogins, _settings.SiteLogins),
            DefaultQueueId = settings.DefaultQueueId,
            // Audit 4 H1: run state and quota use stay the Host's, and a stale copy removes nothing it never saw.
            Queues = MergeQueues(settings.Queues, settings.Revision, settings.MaxConcurrentDownloads),
            Schedules = MergeSchedules(settings.Schedules, settings.Revision),
        };

        if (settings.Proxy?.ClearPassword != true &&
            string.IsNullOrEmpty(next.Proxy.Password) &&
            !string.IsNullOrEmpty(_settings.Proxy.Password) &&
            string.Equals(next.Proxy.Address, _settings.Proxy.Address, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(next.Proxy.Username, _settings.Proxy.Username, StringComparison.Ordinal))
        {
            next.Proxy.Password = _settings.Proxy.Password;
        }
        else if (settings.Proxy?.ClearPassword != true &&
                 string.IsNullOrEmpty(next.Proxy.Password) &&
                 !string.IsNullOrEmpty(_settings.Proxy.PasswordProtected) &&
                 string.Equals(next.Proxy.Address, _settings.Proxy.Address, StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(next.Proxy.Username, _settings.Proxy.Username, StringComparison.Ordinal))
        {
            // A saved password this account can't open (audit 1, L8) stays sealed rather than being dropped.
            next.Proxy.PasswordProtected = _settings.Proxy.PasswordProtected;
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
        // UI.B5: the default queue has its own maximum, and the lower one wins, so a raised overall limit did
        // nothing for queued downloads. Where the default queue matched the old overall limit, and this request
        // leaves it alone, it follows the new one.
        if (next.MaxConcurrentDownloads != _settings.MaxConcurrentDownloads &&
            next.Queues.FirstOrDefault(queue => queue.Id == next.DefaultQueueId) is { } defaultQueue &&
            _settings.Queues.FirstOrDefault(queue => queue.Id == defaultQueue.Id) is { } before &&
            before.MaxConcurrentDownloads == _settings.MaxConcurrentDownloads &&
            defaultQueue.MaxConcurrentDownloads == before.MaxConcurrentDownloads)
        {
            defaultQueue.MaxConcurrentDownloads = next.MaxConcurrentDownloads;
        }
        await _settingsStore.SaveAsync(next, cancellationToken).ConfigureAwait(false);
        _engine.UpdateHostConnectionLimit(next.MaxConnectionsPerHost);
        ApplyTorrentSeedSettings(next);
        ApplyTorrentPeerSettings(next);
        _torrentEngine?.UpdateListen(next.TorrentListenPort, next.TorrentUpnp);
        List<JobRuntime> reconnected;
        List<JobRuntime> running;
        List<Guid> torrentsStopped;
        lock (_sync)
        {
            // What each download would have got from the old default, read before the settings change.
            // UI.R21: running ones too, provider and protected links among them ("Apply to all", owner 2026-11-08);
            // a segmented transfer takes the new count without stopping.
            // Any input of DefaultConnectionsLocked counts: the default, the per-host limit and the connection profiles.
            var candidates = !ConnectionInputsChanged(_settings, next)
                ? []
                : _jobs.Values.Where(runtime => IsUnstartedForConnections(runtime) || IsRunningForConnections(runtime))
                    .Select(runtime => (Runtime: runtime, Old: DefaultConnectionsLocked(runtime, _settings.DefaultConnections)))
                    .Where(item => item.Old is not null)
                    .ToList();
            // Turning torrents off stops the ones running; they wait queued (not paused by the user) with the others, and
            // go on when torrents are turned on again.
            torrentsStopped = _settings.TorrentsEnabled && !next.TorrentsEnabled
                ? _jobs.Values.Where(runtime => runtime.Job.Torrent is not null && (runtime.Starting || runtime.Runner is { IsCompleted: false }) &&
                        !runtime.PauseRequested && !runtime.CancelRequested)
                    .Select(runtime => runtime.Job.Id).ToList()
                : [];
            foreach (var id in torrentsStopped)
            {
                var stopping = _jobs[id];
                stopping.PauseRequested = true;
                stopping.ResumeWhenStopped = true;
                stopping.Job.State = DownloadState.Queued;
                stopping.Job.BytesPerSecond = null;
                stopping.Job.EstimatedRemaining = null;
                stopping.Job.Touch();
                stopping.Cts?.Cancel();
            }
            // Turning torrents off stops seeding too (T2).
            if (_settings.TorrentsEnabled && !next.TorrentsEnabled)
                StopSeedingWhere(_ => true);
            NoteSettingsRevisionLocked(_settings, next);
            // Review 2 L4: a queue's arm is the Host's. A run that ended while this save was under way took it from the
            // queue being replaced (under _sync only), so the live value wins over the copy made before.
            foreach (var queue in next.Queues)
                queue.ArmedScheduleId = _settings.Queues.FirstOrDefault(current => current.Id == queue.Id)?.ArmedScheduleId;
            _settings = next;
            reconnected = [];
            // UI.B5: a changed default reaches downloads that haven't started. One whose count was chosen by hand
            // (not what the old default gave it) keeps it. A provider job's count is never chosen by hand (TB.L1),
            // so a running one always follows, within its provider's cap.
            foreach (var (runtime, previous) in candidates)
            {
                if (runtime.Job.RemoteSource is null && runtime.Job.RequestedConnections != previous &&
                    !SavedUnderTheOldProviderLinkCap(runtime.Job, previous)) continue;
                if (DefaultConnectionsLocked(runtime, next.DefaultConnections) is not { } connections) continue;
                if (connections == runtime.Job.RequestedConnections) continue;
                runtime.Job.RequestedConnections = connections;
                reconnected.Add(runtime);
            }
            running = reconnected.Where(IsRunningForConnections).ToList();
            var queueNames = next.Queues.ToDictionary(q => q.Id, q => q.Name);
            foreach (var runtime in _jobs.Values)
            {
                if (queueNames.TryGetValue(runtime.Job.QueueId, out var queueName))
                {
                    runtime.Job.QueueName = runtime.Job.IsQueued ? queueName : "None";
                }
            }
        }

        // PERF.6 and T2: one budget for HTTP, FTP and torrents, applied once the new settings are in force, so a split
        // made on a torrent's progress reads them too.
        ApplySpeedLimitsAndRate(next);
        _engine.UpdateProxy(next.Proxy);
        _engine.UpdateSiteLogins(BuildSiteLogins(next));
        _engine.UpdateFfmpegPath(next.FfmpegPath);
        _ftpEngine?.UpdateProxy(next.Proxy);
        _ftpEngine?.UpdateSiteLogins(BuildSiteLogins(next));
        _ftpEngine?.UpdateFfmpegPath(next.FfmpegPath);
        UpdateSleepPrevention();
        foreach (var runtime in reconnected)
        {
            await PersistAsync(runtime, cancellationToken).ConfigureAwait(false);
            Raise(runtime, "updated");
        }
        foreach (var runtime in running)
        {
            _engine.ConnectionsChanged(runtime.Job.Id);
        }
        foreach (var id in torrentsStopped)
        {
            JobRuntime? stopped;
            lock (_sync) _jobs.TryGetValue(id, out stopped);
            if (stopped is not null) Raise(stopped, "updated");
        }
        await RequeueExcessAsync(cancellationToken).ConfigureAwait(false);
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

    /// <param name="armForSchedule">A schedule's start: its when-done actions are armed for this run, before anything
    /// can start, and only when the run has something to do (audit 2 M10).</param>
    private async Task<SettingsResult> QueueActionCoreAsync(QueueActionRequest request, CancellationToken cancellationToken, Guid? armForSchedule = null)
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
                // A new list, not a change to the one a save may be writing out (audit 2 M19).
                _settings.Queues = new List<DownloadQueue>(_settings.Queues) { queue };
                NoteQueueAddedLocked(queue.Id);
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
                    {
                        _settings.Queues = _settings.Queues.Where(item => item != queue).ToList();
                        NoteQueueRemovedLocked(queue.Id);
                    }
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
                    // A schedule that starts it arms its when-done actions again after this (audit 2 M10).
                    queue.ArmedScheduleId = null;
                    queue.IsRunning = true;
                    // U-WL-2: starting a queue starts what waits in it: queued and "later" downloads, and those its own
                    // stop, quota, a move or an exit paused. One the user paused stays paused, and a failed one is tried
                    // again only when the queue would have retried it by itself (it ran from the queue, retries are on
                    // and some are left).
                    foreach (var runtime in _jobs.Values.Where(r =>
                                 r.Job.IsQueued && r.Job.QueueId == queue.Id && (r.Job.State == DownloadState.Queued ||
                                     r.Job.State == DownloadState.Paused && !r.Job.PausedByUser ||
                                     r.Job.State == DownloadState.Failed && QueueRetriesFailedLocked(r.Job, queue))))
                    {
                        var retry = runtime.Job.State == DownloadState.Failed;
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
                        // A retry counts against the queue's limit, as one the queue makes itself; a retry that was already
                        // waiting was counted when it was set. Anything else starts a fresh run.
                        runtime.Job.QueueRetryCount = !retry ? 0
                            : runtime.Job.QueueRetryAt is null ? runtime.Job.QueueRetryCount + 1 : runtime.Job.QueueRetryCount;
                        runtime.Job.QueueRetryAt = null;
                        runtime.Job.State = DownloadState.Queued;
                        runtime.Job.StartMode = StartMode.StartNow;
                        runtime.Job.Failure = null;
                        runtime.Job.Touch();
                        resumed.Add(runtime);
                    }

                    // Armed before Schedule() below, so a download that ends at once still finds its run armed. A start
                    // with nothing to do arms nothing: a download added by hand later is not this run's (audit 2 M10).
                    if (armForSchedule is { } armed && _jobs.Values.Any(r => r.Job.IsQueued && r.Job.QueueId == queue.Id &&
                            (r.Job.QueueRetryAt is not null || r.Job.State is not (DownloadState.Completed or DownloadState.Failed or
                                DownloadState.Cancelled or DownloadState.Paused))))
                        queue.ArmedScheduleId = armed;
                    break;
                case "stop":
                    queue.IsRunning = false;
                    queue.ArmedScheduleId = null;
                    // Stopping a queue (by hand or by its schedule) stops its seeding torrents too (T2).
                    StopSeedingWhere(runtime => runtime.Job.IsQueued && runtime.Job.QueueId == queue.Id);
                    foreach (var waiting in _jobs.Values.Where(runtime => runtime.Job.IsQueued && runtime.Job.QueueId == queue.Id && runtime.Job.QueueRetryAt is not null))
                    {
                        // The retry it was waiting for never ran; it is given back, so the next start can make it.
                        waiting.Job.QueueRetryAt = null;
                        waiting.Job.QueueRetryCount = Math.Max(0, waiting.Job.QueueRetryCount - 1);
                        waiting.Job.Touch();
                        renamed.Add(waiting);
                    }
                    // A download that already finished (its runner still winding up, or a torrent about to seed) stays
                    // completed; only its seeding stops, above.
                    paused = _jobs.Values
                        .Where(r => r.Job.IsQueued && r.Job.QueueId == queue.Id && r.Job.State != DownloadState.Completed &&
                                    (r.Starting || r.Runner is { IsCompleted: false }))
                        .ToList();
                    foreach (var runtime in paused)
                    {
                        RequestStopLocked(runtime);
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
        await RequeueExcessAsync(cancellationToken).ConfigureAwait(false);
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
            // UI.R37: with updates off nothing is known about the latest any more.
            NoteLatestVersion(null);
            return new UpdateCheckResult
            {
                Ok = false,
                Status = "notConfigured",
                InstalledVersion = install.Version,
                Error = "Update manifest URL/public key are not configured.",
            };
        }

        if (!Uri.TryCreate(ManifestUrlFor(install, _settings.UpdateManifestUrl), UriKind.Absolute, out var manifestUri) ||
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
            // Headers only: the body is read below up to its limit, since a chunked answer has no length to check first (L4).
            using var response = await UpdateClient.GetAsync(manifestUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
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

            var json = response.Content.Headers.ContentLength is > MaxManifestBytes
                ? null
                : await ReadManifestAsync(response.Content, cancellationToken).ConfigureAwait(false);
            if (json is null)
            {
                return new UpdateCheckResult
                {
                    Ok = false,
                    Status = "invalid",
                    InstalledVersion = install.Version,
                    Error = "Update manifest is too large.",
                };
            }

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
                // UI.R37: a manifest of another channel says nothing about this channel's latest, nor does an older value.
                NoteLatestVersion(null);
                return new UpdateCheckResult
                {
                    Ok = true,
                    Status = "upToDate",
                    InstalledVersion = install.Version,
                    AvailableVersion = manifest.Version,
                };
            }

            // UI.R37: only a manifest of this Host's own channel says what the latest is; one of another channel doesn't.
            NoteLatestVersion(manifest.Version);
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

    /// <summary>The name of the AppImage's signed manifest next to the Windows one in a release.</summary>
    public const string AppImageManifestName = "manifest-linux-x64.json";

    /// <summary>
    /// An AppImage reads its own manifest, which names the .AppImage instead of the Windows package. It sits next to the
    /// Windows one in the same release, so a manifest address ending in <c>manifest.json</c> points to it by its name.
    /// </summary>
    public static string ManifestUrlFor(InstallMetadata install, string manifestUrl)
    {
        if (install.AppImagePath is null) return manifestUrl;
        const string windows = "/manifest.json";
        return manifestUrl.EndsWith(windows, StringComparison.OrdinalIgnoreCase)
            ? manifestUrl[..^windows.Length] + "/" + AppImageManifestName
            : manifestUrl;
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
        if (install.AppImagePath is { } appImage)
            return ReplaceAppImage(appImage, download.Path, check.AvailableVersion, download.Size);
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
            // By its full path: a bare name is looked for in the Host's own and current folders first (report 3, L3).
            var start = new System.Diagnostics.ProcessStartInfo(WindowsPowerShellPath)
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

            DateTimeOffset? helperStarted;
            try { helperStarted = helper.StartTime; }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception) { helperStarted = null; }
            journal.MarkPending(check.AvailableVersion, helper.Id, helperStarted);
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

    /// <summary>
    /// The AppImage update: the verified file is copied next to the running one, made executable, flushed, and renamed
    /// over it, so the old one stays whole until the rename. The running program keeps its mount of the old file. A
    /// restarter started now waits for this Host to exit and then starts the new file, opening the window again only when
    /// one was open.
    /// </summary>
    private UpdateInstallResult ReplaceAppImage(string appImage, string package, string version, long? size)
    {
        var folder = Path.GetDirectoryName(appImage)!;
        var temporary = Path.Combine(folder, $".{Path.GetFileName(appImage)}.{version}.new");
        ReportUpdateProgress("installing", version, size ?? 0, size);
        try
        {
            File.Copy(package, temporary, overwrite: true);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            using (var stream = new FileStream(temporary, FileMode.Open, FileAccess.ReadWrite)) stream.Flush(flushToDisk: true);
            File.Move(temporary, appImage, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DeleteFileQuietly(temporary);
            return new UpdateInstallResult
            {
                Ok = false,
                Status = "failed",
                Error = $"The AppImage could not be replaced ({ex.GetType().Name}). Check that its folder is writable: {folder}",
                Version = version,
            };
        }

        var journal = UpdateJournal;
        journal.ClearForNewInstall();
        journal.RecordSucceeded(version);
        RemoveUpdatePackages();
        try
        {
            AppImageRestarter.Start(appImage, Environment.ProcessId);
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _log?.Invoke($"AppImage {version} is in place, but the restarter did not start ({ex.GetType().Name}); start Inbound again.");
        }

        _log?.Invoke($"AppImage updated to {version}; the Host exits and the new one starts.");
        return new UpdateInstallResult { Ok = true, Status = "scheduled", Version = version, ResultPath = journal.ResultPath };
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

    /// <summary>
    /// A completion program is a full path to something the OS can start. On Windows that is an .exe; Linux programs have
    /// no extension, so there any absolute path passes and the start itself says whether it can run. A server refuses
    /// completion programs from clients anyway (<c>SupportsProgramSettings</c>); this keeps a saved one valid there.
    /// </summary>
    public static bool IsCompletionProgramPath(string program) => PostDownloadCommandLine.IsAllowedProgram(program);

    /// <summary>
    /// The manifest's text, read no further than <see cref="MaxManifestBytes"/>; null when it is longer. The client's
    /// timeout covers only the headers (they are read first), so the body has its own limit, 30 seconds unless
    /// <paramref name="limit"/> says otherwise: a server that sends it slowly can't hold the check open.
    /// </summary>
    /// <exception cref="TimeoutException">The body did not arrive within the limit.</exception>
    public static async Task<string?> ReadManifestAsync(HttpContent content, CancellationToken cancellationToken, TimeSpan? limit = null)
    {
        using var timed = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timed.CancelAfter(limit ?? TimeSpan.FromSeconds(30));
        var buffer = new byte[MaxManifestBytes + 1];
        var length = 0;
        try
        {
            await using var stream = await content.ReadAsStreamAsync(timed.Token).ConfigureAwait(false);
            int read;
            while (length < buffer.Length &&
                   (read = await stream.ReadAsync(buffer.AsMemory(length), timed.Token).ConfigureAwait(false)) > 0)
                length += read;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The update manifest took too long to arrive.");
        }

        if (length > MaxManifestBytes) return null;
        var bom = length >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF ? 3 : 0;
        return System.Text.Encoding.UTF8.GetString(buffer, bom, length - bom);
    }

    private static bool IsNewerVersion(string candidate, string installed)
    {
        static string Clean(string value) => value.Trim().TrimStart('v', 'V');
        return Version.TryParse(Clean(candidate), out var candidateVersion) &&
               Version.TryParse(Clean(installed), out var installedVersion) &&
               candidateVersion > installedVersion;
    }

    /// <param name="caller">
    /// A caller limited to some folders may change only a download whose files are all inside them, and may move or
    /// rename it only to a path inside them.
    /// </param>
    public async Task<JobActionResponse> UpdateJobAsync(
        JobUpdateRequest request,
        CancellationToken cancellationToken = default,
        CallerScope? caller = null)
    {
        // A caller limited to some folders hears nothing about a download outside them, not even why a name is bad.
        if (caller is not null && RefuseOutsideCaller(request.JobId, caller) is { } outsideFirst) return outsideFirst;
        if (request.NewFileName is not null)
        {
            if (RenameInPlace(request) is not { } renamed) return RenameRefusal(request);
            request = renamed;
        }

        if (caller is not null)
        {
            if (request.NewFilePath is { } newPath && PathRoots.DescribeUnsafeText(newPath.Trim()) is { } text)
                return new JobActionResponse { Ok = false, Error = text, Job = GetJob(request.JobId) };
        }

        await _settingsUpdateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _persistGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { return await UpdateJobCoreAsync(request, cancellationToken, caller).ConfigureAwait(false); }
            finally { _persistGate.Release(); }
        }
        finally { _settingsUpdateGate.Release(); }
    }

    private static readonly HashSet<string> DeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", "CLOCK$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    /// <summary>
    /// EXT.R11: why <paramref name="name"/> can't be a downloaded file's new name, or null when it can. A bare name only:
    /// no folder or separator, nothing Windows refuses in a name, no device name, no trailing dot or space.
    /// </summary>
    public static string? DescribeBadFileName(string? name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0) return "Type a file name.";
        if (trimmed.Length > 255) return "That name is too long.";
        if (trimmed is "." or "..") return "That name can't be used.";
        if (trimmed.IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|']) >= 0 ||
            trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || trimmed.Any(char.IsControl))
            return "A file name can't contain \\ / : * ? \" < > |.";
        if (trimmed.EndsWith('.')) return "A file name can't end with a dot.";
        var stem = trimmed.Split('.')[0].TrimEnd();
        if (DeviceNames.Contains(stem)) return "That name is reserved by Windows.";
        return null;
    }

    /// <summary>
    /// The request with <see cref="JobUpdateRequest.NewFileName"/> turned into a path in the file's own folder, or null
    /// when it can't be (<see cref="RenameRefusal"/> says why). Only a completed download whose file is there.
    /// </summary>
    private JobUpdateRequest? RenameInPlace(JobUpdateRequest request)
    {
        if (request.NewFilePath is not null || DescribeBadFileName(request.NewFileName) is not null) return null;
        string current;
        lock (_sync)
        {
            if (!_jobs.TryGetValue(request.JobId, out var runtime) || runtime.Job.State != DownloadState.Completed) return null;
            current = runtime.Job.FinalFilePath;
        }

        if (string.IsNullOrWhiteSpace(current) || !File.Exists(current) || Path.GetDirectoryName(current) is not { Length: > 0 } folder)
            return null;
        return new JobUpdateRequest
        {
            JobId = request.JobId,
            NewFilePath = Path.Combine(folder, request.NewFileName!.Trim()),
            CategoryId = request.CategoryId,
            CompletionOptions = request.CompletionOptions,
            Description = request.Description,
            SpeedLimitBytesPerSecond = request.SpeedLimitBytesPerSecond,
            ShowCompletionDialog = request.ShowCompletionDialog,
            Priority = request.Priority,
        };
    }

    private JobActionResponse RenameRefusal(JobUpdateRequest request)
    {
        var job = GetJob(request.JobId);
        var error = request.NewFilePath is not null ? "Give a new name or a new path, not both."
            : DescribeBadFileName(request.NewFileName) is { } bad ? bad
            : job is null ? "Job not found."
            : job.State != "completed" ? "Rename is available after the download completes."
            : "The completed file no longer exists.";
        return new JobActionResponse { Ok = false, Error = error, Job = job };
    }

    /// <summary>
    /// EXT.R11: the desktop's Redownload for one download: added again from its address into its folder with its name,
    /// and saved under a new name when that file is there (rename policy), so nothing is overwritten. Refused while it
    /// runs or waits, for a provider's file (no real address here) and for an address whose key is hidden.
    /// </summary>
    public async Task<JobActionResponse> RedownloadAsync(Guid jobId, CancellationToken cancellationToken = default, CallerScope? caller = null)
    {
        if (caller is not null && RefuseOutsideCaller(jobId, caller) is { } outside) return outside;
        AddJobRequest add;
        lock (_sync)
        {
            if (!_jobs.TryGetValue(jobId, out var runtime)) return new JobActionResponse { Ok = false, Error = "Job not found." };
            var job = runtime.Job;
            var shown = Map(job);
            if (job.State is not (DownloadState.Completed or DownloadState.Failed or DownloadState.Cancelled))
                return new JobActionResponse { Ok = false, Error = "Stop the current download before redownloading.", Job = shown };
            if (job.RemoteSource is not null || job.Url.StartsWith("remote://", StringComparison.OrdinalIgnoreCase))
                return new JobActionResponse
                {
                    Ok = false, Job = shown,
                    Error = "This file comes from a provider account, so it can't be redownloaded from here. Add it again from the account.",
                };
            if (shown.AddressHidden || ProviderLinks.IsMasked(job.Url))
                return new JobActionResponse
                {
                    Ok = false, Job = shown,
                    Error = "This address has its key hidden, so it can't be sent again. Paste the original link to download the file again.",
                };
            add = new AddJobRequest
            {
                Url = job.Url,
                DestinationDirectory = job.DestinationDirectory,
                FileName = job.FileName,
                CategoryId = shown.CategoryId,
                ConflictPolicy = "rename",
                RememberDestination = false,
                Source = "api",
            };
        }

        var added = await AddJobAsync(add, cancellationToken, caller).ConfigureAwait(false);
        return added.Accepted
            ? new JobActionResponse { Ok = true, Job = added.Job }
            : new JobActionResponse { Ok = false, Error = added.Error ?? "The redownload was refused.", ErrorCode = added.ErrorCode };
    }

    private async Task<JobActionResponse> UpdateJobCoreAsync(JobUpdateRequest request, CancellationToken cancellationToken, CallerScope? caller)
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

            if (request.NewFilePath is not null && runtime.Job.Torrent is not null)
                return new JobActionResponse { Ok = false, Error = "A torrent's files can't be moved or renamed.", Job = Map(runtime.Job) };
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
                    return new JobActionResponse { Error = "Unknown computer action." };
                if (completion.SystemAction != "none" && !_settings.AllowSystemCompletionActions)
                    return new JobActionResponse { Error = "Allow computer actions in the Scheduler before choosing this action." };
                if (completion.ProgramPath is { Length: > 0 } program && !IsCompletionProgramPath(program))
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
            // T3: a torrent has its own download and upload limits (torrent.setOptions).
            if (request.SpeedLimitBytesPerSecond is not null && runtime.Job.Torrent is not null)
                return new JobActionResponse { Ok = false, Error = TorrentSpeedLimitRefused };
            if (request.SpeedLimitBytesPerSecond is not null &&
                (runtime.Job.IsTerminal || !SupportsLiveSpeedLimit(runtime.Job)))
                return new JobActionResponse { Ok = false, Error = "Live speed limits are available for unfinished HTTP/HTTPS and FTP file downloads." };
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
                    return new JobActionResponse { Ok = false, Error = ErrorText(ex, caller) };
                }

                var directory = Path.GetDirectoryName(target);
                if (string.IsNullOrWhiteSpace(directory))
                {
                    return new JobActionResponse { Ok = false, Error = "The new path has no folder." };
                }

                if ((caller?.Roots ?? Roots) is { } roots)
                {
                    if (roots.Allowed(directory) is not { } inside)
                        return new JobActionResponse { Ok = false, Error = roots.OutsideTextFor(directory), ErrorCode = JobActionErrorCodes.OutsideRoots };
                    directory = inside;
                    target = Path.Combine(inside, Path.GetFileName(target));
                }

                // Only Windows sees a.txt and A.TXT as one file; on Linux changing the case is a real move.
                if (!PathNames.Same(target, runtime.Job.FinalFilePath))
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
                else if (!string.Equals(target, runtime.Job.FinalFilePath, StringComparison.Ordinal))
                {
                    // EXT.R11 review: the same file with only the case changed (report.pdf to Report.pdf on Windows). It
                    // is still renamed on disk and in the list, or the change would be reported done without happening.
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
                return new JobActionResponse { Ok = false, Error = ErrorText(ex, caller) };
            }

            lock (_sync)
            {
                // Only a finished file is moved, and it stays this download's own.
                if (PathNames.Same(runtime.Job.WrittenFinalPath, moveFrom) || runtime.Job.State == DownloadState.Completed)
                    runtime.Job.WrittenFinalPath = moveTo;
                runtime.Job.FinalFilePath = moveTo;
                runtime.Job.FileName = Path.GetFileName(moveTo);
                runtime.Job.NameFromAddress = false;
                runtime.Job.DestinationDirectory = Path.GetDirectoryName(moveTo)!;
                runtime.Job.PartFilePath = Path.Combine(
                    runtime.Job.DestinationDirectory,
                    $"{runtime.Job.FileName}.{runtime.Job.Id:N}.part");
                runtime.Job.Touch();
            }
        }

        try
        {
            // Audit 2 L27: a moved file whose new place isn't saved would be shown as missing; it goes back instead.
            await PersistAsync(runtime, cancellationToken,
                requireSuccess: request.CategoryId is not null || request.CompletionOptions is not null || request.Priority is not null || moveTo is not null,
                gateHeld: true).ConfigureAwait(false);
        }
        catch (Exception saveError) when (moveTo is not null)
        {
            var putBack = true;
            try
            {
                File.Move(moveTo, moveFrom!);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                putBack = false;
                _log?.Invoke($"Job {runtime.Job.Id}: its moved file could not be put back after the save failed: {ex.GetType().Name}");
            }

            if (putBack)
            {
                lock (_sync)
                {
                    runtime.Job.WrittenFinalPath = beforeEdit!.WrittenFinalPath;
                    runtime.Job.FinalFilePath = beforeEdit.FinalFilePath;
                    runtime.Job.FileName = beforeEdit.FileName;
                    runtime.Job.NameFromAddress = beforeEdit.NameFromAddress;
                    runtime.Job.DestinationDirectory = beforeEdit.DestinationDirectory;
                    runtime.Job.PartFilePath = beforeEdit.PartFilePath;
                    // The failed save already took the new name's automatic category; the old name's comes back with it.
                    runtime.Job.CategoryIsAutomatic = beforeEdit.CategoryIsAutomatic;
                    runtime.Job.CategoryId = beforeEdit.CategoryId;
                    runtime.CategoryFileName = beforeEdit.FileName;
                    if (request.CompletionOptions is not null) runtime.Job.CompletionOptions = beforeEdit.CompletionOptions;
                    if (request.Description is not null) runtime.Job.Description = beforeEdit.Description;
                    if (request.SpeedLimitBytesPerSecond is not null) runtime.Job.SpeedLimitBytesPerSecond = beforeEdit.SpeedLimitBytesPerSecond;
                    if (request.ShowCompletionDialog is not null) runtime.Job.ShowCompletionDialog = beforeEdit.ShowCompletionDialog;
                    if (request.Priority is not null) runtime.Job.Priority = beforeEdit.Priority;
                    runtime.Job.Touch();
                }
            }
            else
            {
                // The record in memory follows the file to its new place; other windows and the web UI must too.
                Raise(runtime, "updated");
            }

            _log?.Invoke($"Job {runtime.Job.Id}: Move/Rename was not saved: {saveError.GetType().Name}");
            return new JobActionResponse
            {
                Ok = false,
                Error = putBack
                    ? "The download's record could not be saved, so the file was left where it was."
                    : "The file was moved, but the download's record could not be saved.",
                Job = Map(runtime.Job),
            };
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
        try
        {
            var moved = await MoveToQueueCoreAsync(id, queueId, cancellationToken).ConfigureAwait(false);
            if (!moved.Ok) return moved;

            // A running download moved into a queue that is already full: the same rule as a lowered limit
            // (owner, 2026-11-08: "Yes, same rule") sends the queue's newest extra one back to Queued.
            await RequeueExcessAsync(cancellationToken).ConfigureAwait(false);
            lock (_sync)
            {
                if (_jobs.TryGetValue(id, out var runtime)) moved.Job = Map(runtime.Job);
            }

            return moved;
        }
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
                RequestStopLocked(runtime);
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

    /// <summary>
    /// Points a download at a new address. One that hasn't finished goes back to the queue and starts from there. A
    /// finished one keeps its file and stays finished: only the address it would be downloaded from again changes
    /// (Redownload uses it), so nothing is downloaded and nothing happens to the file. A new address is still checked
    /// with <see cref="DirectAddresses"/> first, as for any download: a provider's own link must not become a direct
    /// address a redownload would use, and that check may ask the provider about the address.
    /// </summary>
    /// <param name="explicitDirect">The user chose a direct request for an address nobody could classify.</param>
    /// <param name="caller">
    /// A caller limited to some folders may point only a download inside them at a new address, never one on this PC or
    /// its local network, and the download then stays off those for good (<see cref="DownloadJob.PublicNetworkOnly"/>).
    /// </param>
    public async Task<JobActionResponse> RefreshUrlAsync(
        Guid id,
        string newUrl,
        bool explicitDirect = false,
        CancellationToken cancellationToken = default,
        CallerScope? caller = null)
    {
        if (!TryValidateUrl(newUrl, out var uri, out var urlError))
        {
            return new JobActionResponse { Ok = false, Error = urlError };
        }

        if (caller is not null)
        {
            if (CallerScope.DescribeAddress(newUrl) is { } refused) return new JobActionResponse { Ok = false, Error = refused, Job = GetJob(id) };
            if (RefuseOutsideCaller(id, caller) is { } outside) return outside;
            // What the user set to happen to this file would then happen to whatever the new address sends.
            if (GetJob(id) is { CompletionOptions: var options } job && (options.OpenFile || !string.IsNullOrWhiteSpace(options.ProgramPath)))
                return new JobActionResponse { Ok = false, Error = CallerScope.CompletionActionsKeepAddress, Job = job };
        }

        await WaitForEndedRunAsync(id, cancellationToken).ConfigureAwait(false);

        // Asking may wait on the network, so never under the lock, and the job may have moved on
        // meanwhile. So everything is looked at again afterwards, with this answer kept.
        var addressAllowed = false;
        while (true)
        {
            JobRuntime? runtime;
            var askFirst = false;
            var finished = false;
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

                finished = runtime.Job.State == DownloadState.Completed;
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
                    if (caller is not null) runtime.Job.PublicNetworkOnly = true;
                    if (!finished)
                    {
                        runtime.Job.State = DownloadState.Queued;
                        runtime.Job.StartMode = StartMode.StartNow;
                        runtime.Job.Failure = null;
                        runtime.Job.CompletedAt = null;
                        runtime.Job.AutoResumeAfterRestart = true;
                    }

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
            if (finished)
            {
                // Still finished: the list shows the new address, and no client takes it for a restart.
                Raise(runtime, "updated");
                return new JobActionResponse { Ok = true, Job = Map(runtime.Job) };
            }

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

    /// <summary>Whether the saved schedule <paramref name="id"/> already has this start (or stop) time.</summary>
    private bool ScheduleTimeKept(Guid id, string? value, bool stop)
    {
        lock (_sync)
            return _settings.Schedules.FirstOrDefault(schedule => schedule.Id == id) is { } saved &&
                   string.Equals(stop ? saved.StopTime : saved.StartTime, value, StringComparison.Ordinal);
    }

    private void StartSchedulerLoop()
    {
        if (_schedulerCts is not null)
        {
            return;
        }

        SettingsChanged += OnSchedulerSettingsChanged;
        if (!_enableSchedulerTimer)
        {
            // No timer: tests evaluate by hand. The next wake is still worked out, for them to read.
            ArmSchedulerTimer();
            return;
        }

        var cts = new CancellationTokenSource();
        _schedulerCts = cts;
        _schedulerTask = Task.Run(() => SchedulerLoopAsync(cts.Token), CancellationToken.None);
    }

    /// <summary>
    /// The evaluation at start. After it, the scheduler wakes only when something comes due (audit 2 M18): the
    /// evaluation arms a timer for that instant, and a settings change arms it again (<see cref="ArmSchedulerTimer"/>).
    /// </summary>
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
            ArmSchedulerTimer();
        }
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        List<JobRuntime> active;
        Task packageCheck;
        var schedulerTask = _schedulerTask;
        _schedulerCts?.Cancel();
        StopSchedulerTimer();
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

        lock (_sleepPreventionGate)
        {
            try
            {
                _sleepPrevention.SetActive(false);
            }
            catch (Exception ex)
            {
                _log?.Invoke($"Could not release sleep prevention: {ex.Message}");
            }

            lock (_sync) _sleepPreventionActive = false;
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
                    // Audit 2 M11: the exit paused it, not the user; the next start resumes it when that is on.
                    if (!runtime.Job.PausedByUser) runtime.Job.PausedByExit = true;
                    runtime.Job.BytesPerSecond = null;
                    runtime.Job.EstimatedRemaining = null;
                    runtime.Job.ActiveConnections = 0;
                    runtime.Job.Touch();
                }
            }

            await PersistAsync(runtime, CancellationToken.None).ConfigureAwait(false);
        }

        // Quota use counted since the last save (audit 2 M9).
        await SaveQuotaUseQuietlyAsync(force: true).ConfigureAwait(false);
        await SaveTakenArmsQuietlyAsync().ConfigureAwait(false);

        // The torrent worker last: every torrent has been paused and saved, so it only has to exit.
        if (_torrentEngine is IAsyncDisposable torrents)
        {
            try { await torrents.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _log?.Invoke($"Could not close the torrent worker: {ex.Message}"); }
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
    /// Files that became attached to a job since the last scan are never touched, and what is in a quarantine folder is
    /// never rescanned or deleted: it is kept for the user. A quarantine folder with nothing left in it is removed, here
    /// and at each start (docs\architecture.md, "Deleting a download").
    /// </summary>
    /// <param name="caller">A caller limited to some folders has only the leftovers inside them cleaned.</param>
    public Task<CleanOrphansResponse> CleanOrphanPartFilesAsync(bool delete, CancellationToken cancellationToken, CallerScope? caller = null)
    {
        var scanned = ScanOrphanPartFiles();
        List<string> candidates;
        lock (_sync)
        {
            candidates = _orphanPartFiles.ToList();
        }

        if (caller is not null) candidates = candidates.Where(path => caller.Roots.Allowed(path) is not null).ToList();

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
                    var basePath = PartBaseOf(path);
                    if (AttachedPartPathsLocked().Contains(NormalizePath(basePath)) || IsTorrentPartLocked(basePath) || !File.Exists(path))
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

        foreach (var directory in scanned)
        {
            if (caller is not null && caller.Roots.Allowed(directory) is null) continue;
            RemoveEmptyQuarantine(directory);
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

    /// <summary>
    /// UI.B19: the app's quarantine folder next to <paramref name="directory"/>, removed once nothing is left in it.
    /// Never a link, never recursively, so whatever is in it keeps it.
    /// </summary>
    private void RemoveEmptyQuarantine(string directory)
    {
        var quarantine = Path.Combine(directory, OrphanQuarantineFolderName);
        try
        {
            if (!Directory.Exists(quarantine) || new DirectoryInfo(quarantine).Attributes.HasFlag(FileAttributes.ReparsePoint)) return;
            if (Directory.EnumerateFileSystemEntries(quarantine).Any()) return;
            Directory.Delete(quarantine, recursive: false);
            _log?.Invoke($"Removed the empty quarantine folder in '{directory}'.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"Kept the quarantine folder in '{directory}': {ex.Message}");
        }
    }

    /// <summary>Finds the unattached partial files and returns the folders it looked in. An empty quarantine folder
    /// in one of them is removed on the way when asked (at startup, with no caller to limit it).</summary>
    /// <summary>A part file this app makes: <c>&lt;final name&gt;.&lt;32 hex digits of the job id&gt;.part</c>.</summary>
    private static bool IsOwnPartFileName(string fileName)
    {
        const int Hex = 32;
        fileName = PartBaseOf(fileName);
        if (!fileName.EndsWith(".part", StringComparison.OrdinalIgnoreCase)) return false;
        var end = fileName.Length - ".part".Length;
        var start = end - Hex;
        if (start < 1 || fileName[start - 1] != '.') return false;
        for (var i = start; i < end; i++)
            if (!char.IsAsciiHexDigit(fileName[i])) return false;
        return true;
    }

    /// <summary>The tracks the engine keeps beside a stream's part file (<c>.part.video</c>, <c>.part.audio</c>,
    /// <c>.part.mux.mp4</c>); they belong to whatever that part file belongs to.</summary>
    private static readonly string[] PartTrackSuffixes = [".video", ".audio", ".mux.mp4"];

    /// <summary>The part file a track file goes with, or <paramref name="path"/> itself.</summary>
    private static string PartBaseOf(string path)
    {
        foreach (var suffix in PartTrackSuffixes)
        {
            if (path.EndsWith(".part" + suffix, StringComparison.OrdinalIgnoreCase)) return path[..^suffix.Length];
        }

        return path;
    }

    private List<string> ScanOrphanPartFiles(bool removeEmptyQuarantine = false)
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
                // Review 1 N3: a stream's kept tracks too, which a pause or a failed mux leaves beside its part file.
                var found = Directory.EnumerateFiles(directory, "*.part", SearchOption.TopDirectoryOnly)
                    .Concat(PartTrackSuffixes.SelectMany(suffix =>
                        Directory.EnumerateFiles(directory, "*.part" + suffix, SearchOption.TopDirectoryOnly)));
                foreach (var path in found)
                {
                    // Audit 2 M13: only this app's own part files (name.<job id>.part). A browser's or another download
                    // manager's "name.part" in the same folder is theirs, still being written, and never ours to clean.
                    if (!IsOwnPartFileName(Path.GetFileName(path))) continue;
                    var basePath = PartBaseOf(path);
                    bool attached;
                    lock (_sync) attached = known.Contains(NormalizePath(basePath)) || IsTorrentPartLocked(basePath);
                    if (attached)
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

            if (removeEmptyQuarantine) RemoveEmptyQuarantine(directory);
        }

        return directories.ToList();
    }

    private async Task<bool> ReconcileAsync(JobRuntime runtime, CancellationToken cancellationToken)
    {
        var changed = false;
        var job = runtime.Job;
        changed |= await ProtectStoredProviderLinksAsync(job, cancellationToken).ConfigureAwait(false);
        if (job.Torrent is not null)
        {
            // Many files and the worker's own resume data: none of the single part file's checks apply.
            return await ReconcileTorrentAsync(job).ConfigureAwait(false) | changed;
        }
        if (job.ReceivedBytes < 0)
        {
            job.ReceivedBytes = 0;
            changed = true;
        }

        // An older build turned a finished download whose file was missing into this failure (audit 2 H2): it is
        // completed again, and the check below gives it the note instead.
        if (job.State == DownloadState.Failed && job.CompletedAt is not null &&
            job.Failure is { Kind: FailureKind.DestinationUnavailable, Message: "The completed file is missing from its destination." })
        {
            job.State = DownloadState.Completed;
            job.Failure = null;
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

                // Audit 2 H2: the download finished, and its file was moved or deleted since, or its drive isn't there
                // yet. It stays completed with a note: a failure would be downloaded all over again by the next queue
                // start or Resume All. A folder that can't be reached (a drive not mounted yet) says nothing at all.
                if (!job.FileMissing && FolderReachable(job.FinalFilePath))
                {
                    job.FileMissing = true;
                    _log?.Invoke($"Job {job.Id}: its completed file is not in its folder; it stays completed.");
                    changed = true;
                }
                return changed;
            }
            if (job.FileMissing)
            {
                job.FileMissing = false;
                changed = true;
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

        // Audit 2 M11: paused only because the Host exited while it ran (tray Exit, an update, logoff), so it goes on
        // as one a kill interrupted does. A pause by the user, a queue stop or a quota is not this mark.
        if (job.PausedByExit)
        {
            if (job.State == DownloadState.Paused && !job.PausedByUser &&
                job.AutoResumeAfterRestart && _settings.AutoResumeUnfinishedOnStartup)
            {
                job.State = DownloadState.Queued;
                job.StartMode = StartMode.StartNow;
            }
            job.PausedByExit = false;
            changed = true;
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
        job.WrittenFinalPath = finalPath;
        job.FileName = final.Name;
        job.ReceivedBytes = final.Length;
        job.TotalBytes = final.Length;
        job.ETag = checkpoint.ETag ?? job.ETag;
        job.LastModified = checkpoint.LastModified ?? job.LastModified;
        job.ContentType = checkpoint.ContentType ?? job.ContentType;
        // L28: the kill may have come before the completed download was marked (REV.4); marking again is harmless.
        if (job.Torrent is null) MarkOfTheWeb.TryWrite(finalPath, job.EffectiveUrl, _log);
        _log?.Invoke($"Job {job.Id}: its file was already in place after a restart, so it was not downloaded again.");
        return true;
    }

    /// <summary>Whether the folder of <paramref name="path"/> is there to look in: false for a drive not mounted yet.</summary>
    private static bool FolderReachable(string path)
    {
        try { return Path.GetDirectoryName(path) is { Length: > 0 } folder && Directory.Exists(folder); }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { return false; }
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
            _quotaUseUnsaved = true;
        }

        runtime.QuotaReportedBytes = received;
        if (!queue.IsRunning || queue.QuotaBytesUsed < queue.QuotaBytes)
        {
            return false;
        }

        queue.IsRunning = false;
        // A queue that reached its quota stops its seeding torrents too (T2).
        StopSeedingWhere(r => r.Job.IsQueued && r.Job.QueueId == queue.Id);
        foreach (var active in _jobs.Values.Where(r =>
                     r.Job.IsQueued && r.Job.QueueId == queue.Id && r.Job.State != DownloadState.Completed &&
                     (r.Starting || r.Runner is { IsCompleted: false })))
        {
            RequestStopLocked(active);
            active.Job.State = DownloadState.Paused;
            active.Job.BytesPerSecond = null;
            active.Job.EstimatedRemaining = null;
            active.Job.ActiveConnections = 0;
            active.Job.Touch();
            active.Cts?.Cancel();
        }

        return true;
    }

    // Audit 2 M9: quota use is saved with the 5-second progress save and at exit, so a restart doesn't start it at zero.
    private bool _quotaUseUnsaved;
    private DateTimeOffset _quotaUseSavedAt = DateTimeOffset.MinValue;

    /// <summary>Saves the settings for quota use counted since the last save, at most every 5 seconds. Never waits for another save.</summary>
    private async Task SaveQuotaUseQuietlyAsync(bool force = false)
    {
        lock (_sync)
        {
            var now = _timeProvider.GetUtcNow();
            if (!_quotaUseUnsaved || !force && now - _quotaUseSavedAt < TimeSpan.FromSeconds(5)) return;
            _quotaUseSavedAt = now;
        }
        // At exit it waits a little for a save under way; otherwise the next progress save tries again.
        if (!await _settingsUpdateGate.WaitAsync(force ? TimeSpan.FromSeconds(5) : TimeSpan.Zero).ConfigureAwait(false)) return;
        try
        {
            lock (_sync) _quotaUseUnsaved = false;
            await _settingsStore.SaveAsync(_settings, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            lock (_sync) _quotaUseUnsaved = true;
            _log?.Invoke($"Could not save quota use: {ex.Message}");
        }
        finally { _settingsUpdateGate.Release(); }
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
                                     r.Job.StartMode != StartMode.Later &&
                                     // A torrent waits in its queue while torrents are off.
                                     (r.Job.Torrent is null || (TorrentsAvailable && _settings.TorrentsEnabled)))
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
                // The last run's link is no guide to this one's host or cap; a change before the new link comes is
                // taken when it comes (UI.R21).
                runtime.RunLink = null;
            }
        }

        foreach (var runtime in toStart)
        {
            BeforeRunForTesting?.Invoke(runtime.Job.Id);
            runtime.Runner = RunJobAsync(runtime);
        }

        UpdateSleepPrevention();
    }

    /// <summary>
    /// Review 2 L10: an exception's words can hold local paths and the Windows user name. This machine's user gets them;
    /// a caller from another device gets the kind only, as the dispatcher's own catch says it (L16), and the log the words.
    /// </summary>
    private string ErrorText(Exception ex, CallerScope? caller)
    {
        if (caller is null) return ex.Message;
        _log?.Invoke($"A web session's request failed: {UrlRedactor.RedactText(ex.Message)}");
        return $"That could not be done ({ex.GetType().Name}). The app's log has the details.";
    }

    /// <summary>Tests only: called in the gap between a job being picked and its runner starting (audit 2 L14).</summary>
    internal Action<Guid>? BeforeRunForTesting { get; set; }

    /// <summary>
    /// Audit 2 L10: decided and applied under one gate, so two callers can't apply their answers out of order and leave
    /// sleep prevented with nothing downloading. The gate is taken before <c>_sync</c>, never inside it.
    /// </summary>
    private readonly object _sleepPreventionGate = new();

    private void UpdateSleepPrevention()
    {
        lock (_sleepPreventionGate)
        {
            bool active;
            lock (_sync)
            {
                active = !_shuttingDown && _settings.PreventSleepWhileDownloading &&
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
                if (runtime.Preempted && !_shuttingDown)
                {
                    // A lower limit stopped it before its transfer began: back in the queue, as if never picked.
                    // It was saved and announced as Queued by whoever stopped it; the Schedule below runs as for a cancel.
                    runtime.Preempted = false;
                    runtime.PauseRequested = false;
                    runtime.ResumeWhenStopped = false;
                    job.State = DownloadState.Queued;
                    job.Touch();
                    runtime.Starting = false;
                }
                else
                {
                    var pausedBeforeStart = false;
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
                            // Audit 2 L14: not a return. The slot it held is free, and the Schedule below fills it.
                            pausedBeforeStart = true;
                        }
                    }

                    if (!pausedBeforeStart)
                    {
                        runtime.Cts?.Dispose();
                        cts = new CancellationTokenSource();
                        runtime.Cts = cts;
                        job.PausedByUser = false;
                        job.KeepCheckpointAfterFinalize = true;
                        job.State = DownloadState.Connecting;
                        job.Failure = null;
                        job.StartedAt ??= DateTimeOffset.UtcNow;
                        job.RunStartedAt = DateTimeOffset.UtcNow;
                        job.CompletedAt = null;
                        job.FileRemovedAfterExtraction = false;
                        job.FileMissing = false;
                        job.TorrentFileReady = false;
                        job.PausedByExit = false;
                        job.ActiveConnections = 0;
                        job.Touch();
                    }
                }
            }
        }

        if (cts is null)
        {
            // The slot this job held is free again.
            Schedule();
            return;
        }

        // UI.B19: folders a cancel took away are made, and recorded, again before the engine needs them.
        RecreateMadeFolders(job);
        await PersistAsync(runtime, CancellationToken.None).ConfigureAwait(false);
        Raise(runtime, "updated");

        var lastProgressEvent = DateTimeOffset.MinValue;
        var lastProgressSave = DateTimeOffset.MinValue;
        var progress = new InlineProgress<TransferProgress>(p =>
        {
            JobDto? dto = null;
            var quotaReached = false;
            DateTimeOffset now;
            lock (_sync)
            {
                // Read under the lock, so reports reach the speed history in time order and none is dropped as
                // coming from an earlier second.
                now = DateTimeOffset.UtcNow;
                if (job.State is DownloadState.Paused or DownloadState.Cancelled || runtime.CancelledForRemoval)
                {
                    // Its share of the total speed ends now, not when its wind-down finishes (UI.R24).
                    _speedHistory.Stopped(job.Id, now);
                    _speedHistory.UploadStopped(job.Id, now);
                    return;
                }

                job.ReceivedBytes = p.ReceivedBytes;
                if (p.TotalBytes is not null)
                {
                    job.TotalBytes = p.TotalBytes;
                }

                // A transfer a lower limit sent back to the queue is already shown, and maybe saved, as Queued
                // (UI.R21). A report from its wind-down still counts its bytes, but must not make it look as
                // if it were running again.
                var windingDown = runtime.Preempted || (runtime.PauseRequested && runtime.ResumeWhenStopped);
                _speedHistory.Report(job.Id, windingDown ? 0 : p.BytesPerSecond, now);
                if (!windingDown)
                {
                    job.BytesPerSecond = p.BytesPerSecond;
                    job.EstimatedRemaining = p.EstimatedRemaining;
                    job.ActiveConnections = p.ActiveConnections;
                    runtime.LastPhase = p.Phase;
                }

                // Follow the engine's phase so a job waiting between retries (or still
                // connecting) is not shown as downloading at 0 B/s.
                if (!windingDown && job.State is DownloadState.Connecting or DownloadState.Queued or DownloadState.RetryWaiting
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

            // T2: while the torrent worker runs, a download's progress is when the speed budget is split again (at most once
            // a second); otherwise this returns at once.
            SplitSpeedBudgetSoon();

            if (quotaReached)
            {
                _log?.Invoke($"Queue {job.QueueId} reached its download quota; pausing.");
                _ = SaveSettingsQuietlyAsync();
            }

            if (dto is not null)
            {
                AnnounceJobChange(dto, "progress");
            }

            if (now - lastProgressSave >= TimeSpan.FromSeconds(5))
            {
                lastProgressSave = now;
                _ = PersistQuietlyAsync(runtime);
                _ = SaveQuotaUseQuietlyAsync();
            }
        });

        DownloadResult result;
        try
        {
            if (job.Torrent is not null)
            {
                // A torrent has no link to refresh and no HTTP checkpoint; its resume data is the worker's.
                result = TorrentsOn && _torrentEngine is { } torrents
                    ? await torrents.ExecuteAsync(job, null, progress, cts.Token).ConfigureAwait(false)
                    : TorrentUnavailableResult(job);
            }
            else
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
            job.WaitingForHost = null;
            runtime.LastPhase = null;
            runtime.Preempted = false;
            runtime.TorrentUploadRate = 0;
            // T8: what it sent while it downloaded ends with the download; seeding reports its own.
            if (job.Torrent is { Seeding: false } stoppedTorrent) stoppedTorrent.UploadRate = 0;
            _speedHistory.Stopped(job.Id, DateTimeOffset.UtcNow);
            _speedHistory.UploadStopped(job.Id, DateTimeOffset.UtcNow);

            // The file is where it landed, whatever else happened while it got there.
            if (result.Success)
            {
                job.FinalFilePath = result.FinalFilePath ?? job.FinalFilePath;
                job.FileName = Path.GetFileName(job.FinalFilePath);
                job.WrittenFinalPath = job.FinalFilePath;
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
                // Before the stop flags go: a stop that came as it finished means no seeding either.
                MarkSeedingPendingLocked(runtime);
                runtime.CancelRequested = false;
                runtime.PauseRequested = false;
                runtime.ResumeWhenStopped = false;
                changeType = "completed";
            }
            else if (job.Torrent is not null && result.FinalState == DownloadState.Paused && result.Failure is { Kind: FailureKind.None } &&
                     !runtime.PauseRequested && !_shuttingDown)
            {
                // The file list is in and the user chooses files first: paused, without the worker, holding no slot. A
                // choice that came while the run was ending goes on now, as Resume would.
                if (job.Torrent.AwaitingFileChoice)
                {
                    job.State = DownloadState.Paused;
                    changeType = "paused";
                }
                else
                {
                    job.State = DownloadState.Queued;
                    job.StartMode = StartMode.StartNow;
                    job.PausedByUser = false;
                    changeType = "queued";
                }

                job.Failure = null;
            }
            else if (job.Torrent is not null && result.FinalState == DownloadState.Paused && result.Failure is { Kind: FailureKind.DiskFull } &&
                     !runtime.PauseRequested && !_shuttingDown)
            {
                // The disk is full: paused with the reason on show, until the user makes room and resumes it. Not a
                // queue's to restart, which would only fill the disk again.
                job.State = DownloadState.Paused;
                job.PausedByUser = true;
                job.Failure = result.Failure;
                changeType = "paused";
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
                if (job.Failure.Kind != FailureKind.VerificationFailed && !TorrentFailureIsFinal(job) &&
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
            await DeleteTorrentPayloadAsync(job, deleteFinal).ConfigureAwait(false);
            await DeleteCheckpointQuietlyAsync(job.Id).ConfigureAwait(false);
            // UI.B19: a cancel that deleted its data while it was still running takes its folders away now.
            bool cancelledToStay;
            CallerScope? cancelledBy;
            lock (_sync) (cancelledToStay, cancelledBy) = (changeType == "cancelled" && !runtime.CancelledForRemoval, runtime.CancelledBy);
            if (cancelledToStay) await RemoveEmptyFoldersMadeForAsync(job, cancelledBy, stillListed: true).ConfigureAwait(false);
        }

        if (changeType == "completed")
        {
            // REV.4: before anything opens or extracts it, the file is marked as from the internet, as a browser marks it.
            if (job.Torrent is null) MarkOfTheWeb.TryWrite(job.FinalFilePath, job.EffectiveUrl, _log);
            else await MarkTorrentFilesAsync(job).ConfigureAwait(false);
            // T10: a download that looks like a torrent, and whose file reads as one, is offered as a torrent. Only such a
            // download's file is read (TorrentFileRule); every other file is left unopened.
            if (TorrentFileRule.LooksLikeTorrent(job) && TorrentFileRule.IsTorrentFile(job.FinalFilePath))
            {
                lock (_sync) job.TorrentFileReady = true;
            }
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
        // The scheduler sleeps until something is due (audit 2 M18); a queue retry now is.
        if (changeType == "failed" && job.QueueRetryAt is not null) ArmSchedulerTimer();
        if (changeType == "completed") RequestPackageCheck();
        if (changeType == "completed" && job.Torrent is not null) StartSeedingAfterDownload(runtime);

        var lateResume = false;
        bool removalUnfinished;
        lock (_sync)
        {
            runtime.Starting = false;
            runtime.Runner = null;
            runtime.Cts?.Dispose();
            runtime.Cts = null;
            // Audit 2 M12: a removal that gave up waiting for this run left the job listed, and the save above was
            // skipped for it. What the run did (its data deleted, Cancelled) is saved below, so a restart doesn't
            // bring back a job that downloads again from zero. A removal that went on has taken the job off the list.
            removalUnfinished = runtime.CancelledForRemoval && _jobs.ContainsKey(job.Id) && !job.RemovalPending;
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
        else if (removalUnfinished)
        {
            await PersistAsync(runtime, CancellationToken.None).ConfigureAwait(false);
        }

        // Audit 2 L7: the freed slot is filled now, not after a hook or action that can take minutes. A download that
        // starts here also counts as unfinished for an exit or shutdown below, as it should.
        Schedule();
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
        else if (changeType is "failed" or "cancelled" && !removalUnfinished)
        {
            // Audit 2 M10: a schedule's run can end on a failure or a cancel too; its exit or shutdown still applies.
            await MaybeRunCompletionActionsAsync(job, changeType, CancellationToken.None).ConfigureAwait(false);
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

    /// <summary>
    /// Takes the queue's arm (audit 2 M10). Review 2 L4: the arm is saved with the queue, so one left on disk would hand
    /// a later run by hand the schedule's actions after a restart; <see cref="SaveTakenArmsQuietlyAsync"/> saves that it
    /// is gone. Under <c>_sync</c>.
    /// </summary>
    private Guid? TakeArmLocked(int queueId)
    {
        if (_settings.Queues.FirstOrDefault(queue => queue.Id == queueId) is not { ArmedScheduleId: { } armed } queue) return null;
        queue.ArmedScheduleId = null;
        _armsTakenUnsaved = true;
        return armed;
    }

    private bool _armsTakenUnsaved;

    /// <summary>Saves the settings for arms taken since the last save. Waits a little for a save under way; the next
    /// settings save (or the one at exit) carries it otherwise, and a start drops an arm whose run had ended.</summary>
    private async Task SaveTakenArmsQuietlyAsync()
    {
        lock (_sync)
            if (!_armsTakenUnsaved) return;
        if (!await _settingsUpdateGate.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false)) return;
        try
        {
            lock (_sync) _armsTakenUnsaved = false;
            await _settingsStore.SaveAsync(_settings, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            lock (_sync) _armsTakenUnsaved = true;
            _log?.Invoke($"Could not save the end of a scheduled run: {ex.Message}");
        }
        finally { _settingsUpdateGate.Release(); }
    }

    /// <summary>
    /// Review 2 L4: at start, a queue keeps the arm of the schedule that started its run only while that run goes on
    /// (downloads an exit paused resume, audit 2 M11). One whose run had ended, as a crash can leave it, is dropped.
    /// </summary>
    private bool DropEndedRunArmsLocked()
    {
        var dropped = false;
        foreach (var queue in _settings.Queues.Where(queue => queue.ArmedScheduleId is not null))
        {
            if (queue.IsRunning && _jobs.Values.Any(r => r.Job.IsQueued && r.Job.QueueId == queue.Id &&
                    (r.Job.QueueRetryAt is not null || r.Job.State is not (DownloadState.Completed or DownloadState.Failed or
                        DownloadState.Cancelled or DownloadState.Paused))))
                continue;
            queue.ArmedScheduleId = null;
            dropped = true;
        }
        return dropped;
    }

    private async Task MaybeRunCompletionActionsAsync(
        DownloadJob job,
        string changeType,
        CancellationToken cancellationToken, bool skipSystemAction = false)
    {
        if (changeType is not ("completed" or "failed" or "cancelled") || !job.IsQueued)
        {
            return;
        }

        ScheduleRule? schedule = null;
        var allowSystemActions = false;
        var blockedByOtherDownloads = false;
        lock (_sync)
        {
            // A failure the queue retries by itself doesn't end the run.
            if (_completionFiredQueues.Contains(job.QueueId) || job.QueueRetryAt is not null)
            {
                return;
            }

            var withActions = _settings.Schedules.Where(s =>
                s.Enabled &&
                s.QueueId == job.QueueId &&
                (s.OpenFileWhenDone || s.ExitWhenDone || s.ShutdownWhenDone)).ToList();
            var queueUnfinished = _jobs.Values.Any(r =>
                r.Job.Id != job.Id &&
                r.Job.IsQueued && r.Job.QueueId == job.QueueId &&
                (r.Job.QueueRetryAt is not null || r.Job.State is not (DownloadState.Completed or DownloadState.Failed or DownloadState.Cancelled)));
            if (queueUnfinished)
            {
                return;
            }

            if (withActions.Count == 0)
            {
                // The run is over: an arm left behind would hand a later run by hand the actions turned on again since.
                TakeArmLocked(job.QueueId);
            }
            else
            {
                _completionFiredQueues.Add(job.QueueId);
                // Audit 2 M10: only a run the schedule started; a manual start or a stop disarms it, and the run's end
                // uses the arm up whether or not its actions run.
                schedule = TakeArmLocked(job.QueueId) is { } armedId
                    ? withActions.FirstOrDefault(s => s.Id == armedId)
                    : null;
                if (schedule is null)
                    _log?.Invoke($"Completion actions skipped: queue {job.QueueId} finished a run its schedule did not start.");

                allowSystemActions = _settings.AllowSystemCompletionActions;
                blockedByOtherDownloads = _jobs.Values.Any(r =>
                    r.Job.Id != job.Id &&
                    (r.Job.QueueRetryAt is not null || r.Job.State is not (DownloadState.Completed or DownloadState.Failed or DownloadState.Cancelled)));
            }
        }

        // Review 2 L4: the arm taken above is saved before any action (an exit or shutdown ends the Host).
        await SaveTakenArmsQuietlyAsync().ConfigureAwait(false);
        if (schedule is null) return;

        // Audit 2 H1: never a file a browser on another device added; opening it would run what that browser chose. A
        // provider's file neither: who chose it (added, selected or approved, from any client, often later) isn't kept
        // with the download, and it opens no window either (DownloadWindows).
        var chosenElsewhere = job.AddedFromAnotherDevice || job.Source == DownloadSource.Remote;
        if (schedule.OpenFileWhenDone && !job.CompletionOptions.OpenFile && changeType == "completed" && !chosenElsewhere)
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
        else if (schedule.OpenFileWhenDone && chosenElsewhere)
        {
            _log?.Invoke(job.AddedFromAnotherDevice
                ? "Completion action skipped open: the download was added from another device."
                : "Completion action skipped open: a provider's files are never opened by a schedule.");
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
                    paths.Contains(runtime.Job.FinalFilePath, PathNames.Comparer))
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

        // T2: a torrent whose file goes after extraction can't be shared any longer; its seeding stops before the delete.
        if (removed)
            foreach (var runtime in marked.Where(runtime => runtime.Job.Torrent is not null))
                await StopTorrentSeedingAsync(runtime).ConfigureAwait(false);
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

        AnnounceJobChange(dto, changeType);
    }

    /// <summary>
    /// Tells each <see cref="JobChanged"/> subscriber, one at a time. A subscriber that throws is logged and the rest are
    /// still told: the runner that announced must go on, or its job stays Connecting with nothing running it (audit 2 L14).
    /// </summary>
    private void AnnounceJobChange(JobDto dto, string changeType)
    {
        if (JobChanged is not { } handlers) return;
        var args = new JobChangeEventArgs(dto, changeType);
        foreach (EventHandler<JobChangeEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, args);
            }
            catch (Exception ex)
            {
                _log?.Invoke($"A job change listener failed for job {dto.Id}: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Whether the file at <see cref="DownloadJob.FinalFilePath"/> is one this download wrote. A completed one's is, as
    /// it is for a record saved before <see cref="DownloadJob.WrittenFinalPath"/> existed.
    /// </summary>
    internal static bool WroteFinalFile(DownloadJob job)
        => !string.IsNullOrWhiteSpace(job.FinalFilePath) &&
           (job.State == DownloadState.Completed || PathNames.Same(job.WrittenFinalPath, job.FinalFilePath));

    private async Task DeletePayloadAsync(DownloadJob job, bool includeFinal, CancellationToken cancellationToken)
    {
        await DeleteTorrentPayloadAsync(job, includeFinal).ConfigureAwait(false);
        DeleteFileQuietly(job.PartFilePath);
        DeleteFileQuietly(job.PartFilePath + ".video");
        DeleteFileQuietly(job.PartFilePath + ".audio");
        DeleteFileQuietly(job.PartFilePath + ".mux.mp4");
        // Only a file this download wrote. An unfinished one's final path can name a file it never wrote, such as the
        // one an Overwrite download would replace at the end; that one is the user's and stays.
        if (includeFinal && WroteFinalFile(job))
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

    /// <summary>Whether a download's default count could differ between the two settings (UI.R21).</summary>
    private static bool ConnectionInputsChanged(AppSettings before, AppSettings after) =>
        before.DefaultConnections != after.DefaultConnections ||
        before.MaxConnectionsPerHost != after.MaxConnectionsPerHost ||
        !before.ConnectionProfiles.Select(profile => (profile.Pattern, profile.MaxConnections))
            .SequenceEqual(after.ConnectionProfiles.Select(profile => (profile.Pattern, profile.MaxConnections)));

    /// <summary>
    /// Running downloads whose count a changed default may follow (UI.R21): the user's own HTTP downloads, provider
    /// jobs and protected (pasted provider) links. Not FTP: an FTP download is one connection by design
    /// (the FTP engine has no segments), so for a running one the change is a
    /// no-op and its record is left as it is.
    /// </summary>
    private static bool IsRunningForConnections(JobRuntime runtime) =>
        (runtime.Starting || runtime.Runner is { IsCompleted: false }) &&
        !runtime.PauseRequested && !runtime.CancelRequested && runtime.Job.Torrent is null &&
        !runtime.Job.Url.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Waiting, never begun, not a provider job: a new default connection count can still apply to it. A provider
    /// job takes the current default afresh on each run (<see cref="ProviderConnectionsLocked"/>).
    /// </summary>
    private static bool IsUnstartedForConnections(JobRuntime runtime) =>
        runtime.Job.State is DownloadState.Queued or DownloadState.Paused &&
        runtime.Job.ReceivedBytes == 0 &&
        !runtime.Starting && runtime.Runner is null or { IsCompleted: true } &&
        runtime.Job.RemoteSource is null;

    /// <summary>
    /// The count a default of <paramref name="defaultConnections"/> gives this job under the current settings, or
    /// null when that can't be known yet. Each kind is clamped as its run clamps it: the user's own by the host's
    /// limit, a protected link also by <see cref="ProviderLinks.MaxConnections"/>, and a provider job by the host
    /// and cap of its current run's link. Before a provider run has its link, null: the run then takes the new
    /// default itself. Call under <see cref="_sync"/>.
    /// </summary>
    /// <summary>The cap on a pasted provider link's connections up to 0.5.3, before TB.L1 measured 16.</summary>
    private const int OldProviderLinkCap = 4;

    /// <summary>
    /// A pasted provider link saved at the old cap of 4 while the default would have given it more. Up to 0.5.3 every
    /// count of 4 or more was cut to 4, so that 4 is the cap, not a choice, and the link follows the default like any
    /// other. A count below 4 was chosen and stays.
    /// </summary>
    private static bool SavedUnderTheOldProviderLinkCap(DownloadJob job, int? previous)
        => job.ProtectedUrl is not null && job.RequestedConnections == OldProviderLinkCap && previous > OldProviderLinkCap;

    private int? DefaultConnectionsLocked(JobRuntime runtime, int defaultConnections)
    {
        var job = runtime.Job;
        Uri? address;
        var cap = int.MaxValue;
        if (job.RemoteSource is not null)
        {
            if (runtime.RunLink is not { } link) return null;
            (address, cap) = link;
        }
        else if (job.ProtectedUrl is not null)
        {
            cap = ProviderLinks.MaxConnections;
            if (!Uri.TryCreate(job.ProtectedUrl, UriKind.Absolute, out address) &&
                !Uri.TryCreate(job.Url, UriKind.Absolute, out address)) return null;
        }
        else if (!Uri.TryCreate(job.Url, UriKind.Absolute, out address))
        {
            return null;
        }

        return Math.Clamp(Math.Min(Math.Min(defaultConnections, EffectiveConnectionLimit(address)), cap), 1, 32);
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

    /// <summary>
    /// A folder setting the server's roots don't hold, in words, or null. The download folder is always checked, because
    /// saving creates it. The others only when they change: a folder saved before the roots were narrowed is kept, so a
    /// client sending its copy back doesn't fail on it, and an add that would use it is refused then.
    /// </summary>
    private string? DescribeFolderOutsideRoots(SettingsDto settings, List<CategoryDefinitionDto> categories, PathRoots roots)
    {
        string? Outside(string what, string? requested, string? current) =>
            !string.IsNullOrWhiteSpace(requested) && !PathNames.Same(requested.Trim(), current?.Trim()) && roots.Allowed(requested) is null
                ? $"{what} is outside the folders this server may use."
                : null;

        if (roots.Allowed(settings.DefaultDownloadDirectory) is null) return "The download folder is outside the folders this server may use.";
        lock (_sync)
        {
            if (Outside("The last download folder", settings.LastDestinationDirectory, _settings.LastDestinationDirectory) is { } last) return last;
            if (Outside("The torrent folder", settings.TorrentCategoryFoldersRoot, _settings.TorrentCategoryFoldersRoot) is { } torrents) return torrents;
            foreach (var (category, folder) in settings.CategoryFolders ?? new())
            {
                _settings.CategoryFolders.TryGetValue(category, out var current);
                if (Outside($"The folder for {category}", folder, current) is { } problem) return problem;
            }

            foreach (var category in categories)
            {
                var current = _settings.CustomCategories.FirstOrDefault(item => item.Id == category.Id)?.DestinationDirectory;
                if (Outside($"The folder for {category.Name}", category.DestinationDirectory, current) is { } problem) return problem;
            }
        }

        return null;
    }

    private string ResolveDestination(string? requested, string fileName, bool useLastFolder = true, bool useCategoryFolder = true, string? categoryId = null)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            return Path.GetFullPath(requested.Trim());
        }

        // Folders by category can be turned off in Options; then everything goes to the download folder.
        useCategoryFolder &= _settings.UseCategoryFolders;
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

    /// <summary>T14: where a torrent goes, on this PC or through a provider: <paramref name="requested"/>, else <see cref="TorrentFolderRules"/>.</summary>
    private string ResolveTorrentDestination(string? requested, string? torrentName, bool useLastFolder)
    {
        if (!string.IsNullOrWhiteSpace(requested)) return Path.GetFullPath(requested.Trim());
        return TorrentFolderRules.Resolve(_settings, FileCategories.For(torrentName), useLastFolder);
    }

    /// <summary>T14: what a save from a client that predates the torrent folders leaves out (null) stays as the Host has it.</summary>
    internal static void KeepUnsentTorrentFolders(SettingsDto requested, AppSettings current)
    {
        requested.UseTorrentCategoryFolders ??= current.UseTorrentCategoryFolders;
        requested.TorrentCategoryFoldersRoot ??= current.TorrentCategoryFoldersRoot ?? string.Empty;
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
        var active = CountActiveLocked();
        var queued = _jobs.Values.Count(r => r.Job.State == DownloadState.Queued);
        var nativeHost = NativeHostState?.Invoke() ?? default;
        return new HostStatusDto
        {
            HostVersion = Protocol.HostVersion,
            ProtocolVersion = Protocol.Version,
            StartedAt = StartedAt,
            TrayAvailable = TrayAvailable,
            NativeHostRegistered = nativeHost.IsRegistered,
            ActiveDownloads = active,
            QueuedDownloads = queued,
            TotalJobs = _jobs.Count,
            StateDirectory = _paths.Root,
            BrowserIntegration = nativeHost.IntegrationText(_settings.BrowserIntegrationEnabled),
            OrphanPartFiles = _orphanPartFiles.Where(File.Exists).ToList(),
            LatestVersion = CurrentLatestVersionLocked(),
        };
    }

    private static bool SupportsLiveSpeedLimit(DownloadJob job)
        => job.RemoteSource is not null ||
           // PERF.6: the FTP engine reads the download's own limit as it goes, like the HTTP one.
           (Uri.TryCreate(job.Url, UriKind.Absolute, out var ftp) && ftp.Scheme == "ftp") ||
           Uri.TryCreate(job.Url, UriKind.Absolute, out var uri) &&
           uri.Scheme is "http" or "https" &&
           !uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) &&
           !uri.AbsolutePath.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase) &&
           !job.FileName.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) &&
           !job.FileName.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase) &&
           !StreamManifestHint.HintsStream(job.HandoffContentType, job.Description);

    /// <summary>
    /// What every client is told about a download. For one whose address is hidden (a direct provider link) or that is
    /// a provider's file, the page it came from is its site alone and the address it ended up at is left out, so no
    /// client has to know the rule to keep the account's links out of sight; the desktop, too, now gets the site alone.
    /// Its address itself is sent as stored: a link with a key in it is stored masked, and every client shows a hidden
    /// one as its site (<see cref="ShownAddress"/>, by <c>AddressHidden</c>). Its note never holds more than a host
    /// today; <see cref="UrlRedactor.HideAddresses"/> is applied to it all the same, so a future note can't leak one.
    /// </summary>
    public static JobDto Map(DownloadJob job)
    {
        var hidden = job.RemoteSource is null && (job.ProtectedUrl is not null || ShownAddress.IsHidden(job.Url));
        var concealed = hidden || job.RemoteSource is not null;
        var note = job.FileRemovedAfterExtraction ? "Archive removed after extraction"
            : job.State == DownloadState.Completed && job.FileMissing ? "File not found in its folder"
            : WaitingHostShown(job) is { } waitingHost ? WaitingForConnectionText(waitingHost)
            : job.State == DownloadState.Completed && job.Torrent is { Seeding: false, SeedingStoppedNote: { Length: > 0 } seedingStopped }
                ? seedingStopped
            : null;
        return new()
        {
            Id = job.Id,
            Url = job.Url,
            AddressHidden = hidden,
            FinalUrl = concealed ? null : job.FinalUrl,
            PageUrl = concealed ? SiteOfPage(job.PageUrl) : job.PageUrl,
            FileName = job.FileName,
            IsQueued = job.IsQueued,
            QueueProcessing = job.QueueProcessing, QueueRetryCount = job.QueueRetryCount, QueueRetryAt = job.QueueRetryAt,
            PausedByUser = job.PausedByUser,
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
            RunStartedAt = job.RunStartedAt,
            CompletedAt = job.CompletedAt,
            ContentType = job.ContentType,
            ETag = job.ETag,
            LastModified = job.LastModified,
            RequestedConnections = job.RequestedConnections,
            ActiveConnections = job.ActiveConnections,
            WaitingForHost = WaitingHostShown(job),
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
            AddedFromAnotherDevice = job.AddedFromAnotherDevice,
            QuietWindows = job.QuietWindows,
            Torrent = job.Torrent is { } torrent ? new TorrentDto
            {
                InfoHash = torrent.InfoHash, Name = torrent.Name, HasMetadata = torrent.HasMetadata,
                AwaitingFileChoice = torrent.AwaitingFileChoice, FileCount = torrent.FileCount, WantedCount = torrent.WantedCount,
                TotalBytes = torrent.TotalBytes, WantedBytes = torrent.WantedBytes, Peers = torrent.Peers, Seeds = torrent.Seeds,
                Seeding = torrent.Seeding, SeedingSince = torrent.SeedingSince, Uploaded = torrent.Uploaded,
                Ratio = Math.Round(torrent.Ratio, 3), UploadRate = torrent.UploadRate,
                Source = torrent.Magnet is null ? "file" : "magnet",
                Checking = torrent.WorkerState == "checking" &&
                           (torrent.Seeding || job.State is DownloadState.Connecting or DownloadState.Downloading or DownloadState.Verifying),
                RecheckPending = torrent.RecheckOnStart,
                DownloadLimit = torrent.DownloadLimit, UploadLimit = torrent.UploadLimit,
                SeedGoal = torrent.SeedGoal ?? TorrentSeedGoals.Global,
                SeedGoalValue = torrent.SeedGoal is TorrentSeedGoals.Ratio or TorrentSeedGoals.Hours ? torrent.SeedGoalValue : null,
                Sequential = torrent.Sequential,
            } : null,
            ClientRequestId = job.ClientRequestId,
            OwnerExtensionId = job.OwnerExtensionId,
            QueueId = job.QueueId,
            Priority = job.Priority,
            CancelledByPackageId = job.CancelledByPackageId,
            Description = job.Description,
            ExpectedSha256 = job.ExpectedSha256,
            HandoffContentType = job.HandoffContentType,
            QueueName = job.QueueName,
            StartMode = JsonNamingPolicy.CamelCase.ConvertName(job.StartMode.ToString()),
            ConflictPolicy = JsonNamingPolicy.CamelCase.ConvertName(job.ConflictPolicy.ToString()),
            CanPause = job.CanPause,
            CanResume = job.CanResume,
            IsTerminal = job.IsTerminal,
            IsRemote = job.RemoteSource is not null,
            RemoteProvider = job.RemoteSource?.Provider,
            FileRemovedAfterExtraction = job.FileRemovedAfterExtraction,
            TorrentFileReady = job.TorrentFileReady && job.State == DownloadState.Completed,
            StatusNote = concealed && note is not null ? UrlRedactor.HideAddresses(note) : note,
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
    }

    /// <summary>
    /// A page address as its site alone (scheme, host and a port other than the default, without any user name), the
    /// form every client shows for a hidden download. Null for none, or one that can't be read as an address.
    /// </summary>
    private static string? SiteOfPage(string? pageUrl)
    {
        if (string.IsNullOrWhiteSpace(pageUrl) || !Uri.TryCreate(pageUrl.Trim(), UriKind.Absolute, out var page) || page.Host.Length == 0)
            return null;
        return page.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped) + "/";
    }

    // Every list is a copy (audit 2 M19): a DTO is serialised without the lock, while the store may rewrite the Host's lists.
    public static SettingsDto MapSettings(AppSettings settings)
        => new()
        {
            Revision = settings.Revision,
            DefaultDownloadDirectory = settings.DefaultDownloadDirectory,
            MaxConcurrentDownloads = settings.MaxConcurrentDownloads,
            DefaultConnections = settings.DefaultConnections,
            UseCategoryFolders = settings.UseCategoryFolders,
            CloseWindowCompletely = settings.CloseWindowCompletely,
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
            TorrentsEnabled = settings.TorrentsEnabled,
            TorrentRoute = settings.TorrentRoute,
            TorrentChooseFiles = settings.TorrentChooseFiles,
            UseTorrentCategoryFolders = settings.UseTorrentCategoryFolders,
            TorrentCategoryFoldersRoot = settings.TorrentCategoryFoldersRoot ?? string.Empty,
            TorrentHandler = settings.TorrentHandler,
            TorrentSeedAfterDownload = settings.TorrentSeedAfterDownload,
            TorrentSeedRatio = settings.TorrentSeedRatio,
            TorrentSeedHours = settings.TorrentSeedHours,
            TorrentUploadLimit = settings.TorrentUploadLimit,
            TorrentListenPort = settings.TorrentListenPort,
            TorrentUpnp = settings.TorrentUpnp,
            TorrentDht = settings.TorrentDht,
            TorrentPex = settings.TorrentPex,
            TorrentLsd = settings.TorrentLsd,
            TorrentEncryption = TorrentEncryptionModes.IsKnown(settings.TorrentEncryption) ? settings.TorrentEncryption : TorrentEncryptionModes.Prefer,
            TorrentSeedsAtOnce = settings.TorrentSeedsAtOnce,
            TorrentConnectionsPerTorrent = settings.TorrentConnectionsPerTorrent,
            TorrentMaxConnections = settings.TorrentMaxConnections,
            TorrentUseProxy = settings.TorrentUseProxy,
            FfmpegPath = settings.FfmpegPath,
            PostDownloadCommand = settings.PostDownloadCommand,
            // Review 2:L5: a program kept from before the .exe rule is refused when it would run; Options says so.
            PostDownloadCommandProblem = string.IsNullOrWhiteSpace(settings.PostDownloadCommand) ||
                PostDownloadCommandLine.IsAllowedProgram(settings.PostDownloadCommand.Trim())
                ? null
                : $"This program won't run after downloads. {PostDownloadCommandLine.ProgramRefused}",
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
            RecentDestinationDirectories = new List<string>(settings.RecentDestinationDirectories ?? new List<string>()),
            FavoriteFolders = (settings.FavoriteFolders ?? new()).Select(favorite =>
                new FavoriteFolderDto { Name = favorite.Name, Path = favorite.Path }).ToList(),
            RecentUrls = new List<string>(settings.RecentUrls ?? new List<string>()),
            DownloadGroups = MapGroups(settings.DownloadGroups),
            CustomCategories = settings.CustomCategories.Select(category => new CategoryDefinitionDto { Id = category.Id, Name = category.Name,
                DestinationDirectory = category.DestinationDirectory, Extensions = new(category.Extensions) }).ToList(),
            CategoryFolders = new Dictionary<string, string>(
                settings.CategoryFolders ?? new Dictionary<string, string>(),
                StringComparer.OrdinalIgnoreCase),
            ExcludedFileExtensions = new List<string>(settings.ExcludedFileExtensions ?? new()),
            ExcludedHosts = new List<string>(settings.ExcludedHosts ?? new()),
            InterceptedFileExtensions = new List<string>(settings.InterceptedFileExtensions ?? new()),
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
        /// <summary>
        /// Stopped by a lower simultaneous-downloads limit (UI.R21), not by the user: it goes back to Queued with
        /// its data, and a stop that lands before the transfer starts must not let it run anyway.
        /// </summary>
        public bool Preempted { get; set; }
        /// <summary>The engine's last phase, so a limit change leaves a job alone while it verifies or muxes.</summary>
        public string? LastPhase { get; set; }
        public bool CancelRequested { get; set; }
        public bool DeleteOnCancel { get; set; } = true;
        /// <summary>The limited caller (D8) whose cancel deleted the data, so the folders it then removes stay in its folders.</summary>
        public CallerScope? CancelledBy { get; set; }
        public bool CancelledForRemoval { get; set; }
        /// <summary>
        /// A provider run's link host and the provider's per-file cap, in memory only, so a changed default can
        /// reach the running transfer within that cap (UI.R21). Null until the run has its link. Under <c>_sync</c>.
        /// </summary>
        public (Uri Address, int Cap)? RunLink { get; set; }
        /// <summary>A downloading torrent's upload rate (bytes/s), for the shared speed budget (T2). Under <c>_sync</c>.</summary>
        public double TorrentUploadRate { get; set; }
    }

    private sealed class InlineProgress<T>(Action<T> action) : IProgress<T>
    {
        public void Report(T value) => action(value);
    }
}
