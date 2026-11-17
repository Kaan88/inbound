using System.Collections.ObjectModel;
using DownloadManager.Contracts;

namespace DownloadManager.Mobile;

/// <summary>
/// The Downloads screen's state, fed by a Host through <see cref="IHostConnection"/>: the snapshot, then job.event frames. UI-agnostic:
/// it only needs a way to run code on the UI thread. Events are coalesced: no timer runs while nothing arrives (no ticks while idle),
/// and while downloads run the UI is told at most once per <c>flushInterval</c> (250 ms). New jobs are inserted on top; existing rows
/// never move. <see cref="Rows"/> and the rows' properties are touched on the UI thread only.
/// </summary>
public sealed class DownloadsModel : IDisposable
{
    /// <summary>How often a burst of job events is shown, by default.</summary>
    public static readonly TimeSpan DefaultFlushInterval = TimeSpan.FromMilliseconds(250);

    private readonly IHostConnection _host;
    private readonly Action<Action> _post;
    private readonly TimeSpan _flushInterval;
    private readonly Dictionary<Guid, JobRow> _byId = new();
    private readonly Dictionary<Guid, (JobDto Job, string Type)> _pending = new();
    // Jobs the Host said are gone. Ids are never reused, so any later event for one is stale (the Host announces progress
    // outside its lock, so a progress frame can arrive after the removal) and must not bring the row back.
    private readonly HashSet<Guid> _removed = new();
    // The UpdatedAt of the last state shown per job (UI thread only), so a progress frame mapped before a newer change
    // (announced outside the Host's lock, so it can arrive after "paused" or "completed") does not undo it.
    private readonly Dictionary<Guid, DateTimeOffset> _shownAt = new();

    /// <summary>
    /// A progress frame older than what is already known for the job. Only progress is judged: every other change is
    /// applied as it comes. A frame more than <see cref="StaleWindow"/> older is applied anyway, so a phone clock set back
    /// cannot freeze a row (a stale frame in flight is milliseconds old).
    /// </summary>
    private static bool IsStaleProgress(JobDto incoming, string type, DateTimeOffset known) =>
        type.Equals("progress", StringComparison.OrdinalIgnoreCase)
        && incoming.UpdatedAt < known && known - incoming.UpdatedAt < StaleWindow;

    private static readonly TimeSpan StaleWindow = TimeSpan.FromSeconds(5);
    private readonly object _gate = new();
    private readonly Timer _timer;
    private bool _flushScheduled;
    private bool _shown;
    private bool _gotSnapshot;
    private bool _started;
    private bool _disposed;

    /// <param name="host">The session with the Host.</param>
    /// <param name="post">Runs an action on the UI thread.</param>
    /// <param name="flushInterval">How long job events are gathered before the rows change; zero or less shows each event at once (tests).</param>
    public DownloadsModel(IHostConnection host, Action<Action> post, TimeSpan? flushInterval = null)
    {
        _host = host;
        _post = post;
        _flushInterval = flushInterval ?? DefaultFlushInterval;
        _timer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public ObservableCollection<JobRow> Rows { get; } = new();

    /// <summary>The folder new downloads go to when none is named, from the snapshot's settings; empty until the snapshot is shown.</summary>
    public string DefaultDownloadDirectory { get; private set; } = "";

    /// <summary>The Host's settings as the snapshot (and later settings.changed frames) carried them; defaults until the snapshot is shown.</summary>
    public SettingsDto Settings { get; private set; } = new();

    /// <summary>Raised on the UI thread once, after the snapshot's rows are in <see cref="Rows"/> (also for an empty snapshot).</summary>
    public event Action? FirstSnapshotShown;

    /// <summary>Opens the session; the snapshot arrives as a frame and is shown through <see cref="Rows"/>.</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_started) throw new InvalidOperationException("The model was already started.");
        _started = true;
        await _host.OpenAsync(OnFrame, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsProgress(string type) => type.Equals("progress", StringComparison.OrdinalIgnoreCase);

    private static bool IsRemoval(string type) =>
        type.Equals("removed", StringComparison.OrdinalIgnoreCase) || type.Equals("deleted", StringComparison.OrdinalIgnoreCase);

    private void OnFrame(IpcMessage m)
    {
        // A screen that went away ignores whatever the Host still sends it.
        if (Disposed) return;
        if (m.Type == MessageTypes.JobsSnapshot && !_gotSnapshot)
        {
            _gotSnapshot = true;
            var snap = m.GetPayload<SnapshotDto>();
            _post(() => ApplySnapshot(snap));
        }
        else if (m.Type == MessageTypes.JobEvent && _gotSnapshot)
        {
            var e = m.GetPayload<JobEventDto>();
            lock (_gate)
            {
                if (_removed.Contains(e.Job.Id)) return;
                if (_pending.TryGetValue(e.Job.Id, out var waiting) && IsStaleProgress(e.Job, e.EventType, waiting.Job.UpdatedAt)) return;
                if (IsRemoval(e.EventType)) _removed.Add(e.Job.Id);
                // A progress frame keeps the kind of change already waiting: "added" then "progress" in one batch must still add the row
                // (the list drops progress for a job it doesn't know). Seen on the phones: a download that started at once never appeared.
                var type = e.EventType;
                if (IsProgress(type) && _pending.TryGetValue(e.Job.Id, out var earlier) && !IsProgress(earlier.Type)) type = earlier.Type;
                _pending[e.Job.Id] = (e.Job, type);
                if (_flushScheduled) return;
                _flushScheduled = true;
            }

            if (_flushInterval <= TimeSpan.Zero) Flush();
            else
            {
                // Under the gate, so Dispose cannot run between the check and the Change.
                lock (_gate)
                {
                    if (!_disposed) _timer.Change(_flushInterval, Timeout.InfiniteTimeSpan);
                }
            }
        }
    }

    private void ApplySnapshot(SnapshotDto snap)
    {
        if (Disposed) return;
        Settings = snap.Settings ?? new SettingsDto();
        DefaultDownloadDirectory = Settings.DefaultDownloadDirectory ?? "";
        foreach (var j in snap.Jobs.OrderByDescending(x => x.CreatedAt))
        {
            var row = new JobRow { Id = j.Id };
            row.ApplyFrom(j);
            _byId[j.Id] = row;
            _shownAt[j.Id] = j.UpdatedAt;
            Rows.Add(row);
        }

        _shown = true;
        FirstSnapshotShown?.Invoke();
    }

    private void Flush()
    {
        (JobDto Job, string Type)[] batch;
        lock (_gate)
        {
            batch = _pending.Values.ToArray();
            _pending.Clear();
            _flushScheduled = false;
        }

        _post(() =>
        {
            if (!_shown || Disposed) return;
            foreach (var (job, type) in batch)
            {
                if (IsRemoval(type))
                {
                    _shownAt.Remove(job.Id);
                    if (_byId.Remove(job.Id, out var gone)) Rows.Remove(gone);
                    continue;
                }

                // Batches posted from different threads (no flush interval) can land out of order: never re-add a removed job.
                bool wasRemoved;
                lock (_gate) wasRemoved = _removed.Contains(job.Id);
                if (wasRemoved) continue;

                if (_shownAt.TryGetValue(job.Id, out var known) && IsStaleProgress(job, type, known)) continue;
                if (_byId.TryGetValue(job.Id, out var row)) row.ApplyFrom(job);
                // A job the list does not know is added by any change but progress: a progress frame for an unknown job is
                // one that outlived it (removed before this session's snapshot), and a new job always arrives as "added".
                else if (type.Equals("progress", StringComparison.OrdinalIgnoreCase)) continue;
                else
                {
                    row = new JobRow { Id = job.Id };
                    row.ApplyFrom(job);
                    _byId[job.Id] = row;
                    Rows.Insert(InsertIndexFor(row), row);
                }

                _shownAt[job.Id] = job.UpdatedAt;
            }
        });
    }

    /// <summary>Newest first: a new row goes above the first row that is not newer than it (index 0 for a job just added).</summary>
    private int InsertIndexFor(JobRow row)
    {
        var i = 0;
        while (i < Rows.Count && Rows[i].CreatedAt > row.CreatedAt) i++;
        return i;
    }

    /// <summary>
    /// Command job.add: adds one download. <paramref name="destination"/> null means the Host's default folder; <paramref name="startMode"/>
    /// is one of <see cref="StartModes"/>. The reply says whether the Host accepted it (an address it cannot use comes back with an
    /// <see cref="AddJobResponse.Error"/>, it does not throw). The new row arrives as a job event, not from this call.
    /// </summary>
    public Task<AddJobResponse> AddAsync(string url, string? destination = null, string? fileName = null,
        string startMode = StartModes.StartNow, CancellationToken cancellationToken = default) =>
        AddAsync(new AddJobRequest
        {
            Url = url,
            DestinationDirectory = string.IsNullOrWhiteSpace(destination) ? null : destination,
            FileName = string.IsNullOrEmpty(fileName) ? null : fileName,
            StartMode = startMode,
        }, cancellationToken);

    /// <summary>Command job.add with a request built by a screen (New download); the source is always this app and the policy is whatever the request says.</summary>
    public async Task<AddJobResponse> AddAsync(AddJobRequest request, CancellationToken cancellationToken = default)
    {
        request.Source = "android";
        var reply = await _host.RequestAsync(IpcMessage.Create(MessageTypes.JobAdd, request), cancellationToken).ConfigureAwait(false);
        return reply.Type == MessageTypes.JobAddResult
            ? reply.GetPayload<AddJobResponse>()
            : new AddJobResponse { Accepted = false, Error = "The Host answered with " + reply.Type + "." };
    }

    /// <summary>Command file.metadata: what the Host can tell about an address before it is added (name, size). Never throws for a bad address; the result says.</summary>
    public async Task<FileMetadataResult> ProbeAsync(string url, CancellationToken cancellationToken = default)
    {
        var reply = await _host.RequestAsync(IpcMessage.Create(MessageTypes.FileMetadata, new FileMetadataRequest { Url = url }), cancellationToken).ConfigureAwait(false);
        return reply.Type == MessageTypes.FileMetadataResult
            ? reply.GetPayload<FileMetadataResult>()
            : new FileMetadataResult { Ok = false, Error = "The Host answered with " + reply.Type + "." };
    }

    private async Task<CommandResult> ActionAsync(string type, Guid id, bool deleteFile = false, CancellationToken cancellationToken = default)
    {
        try
        {
            var reply = await _host.RequestAsync(IpcMessage.Create(type, new JobActionRequest { JobId = id, DeleteFile = deleteFile }), cancellationToken).ConfigureAwait(false);
            if (reply.Type != MessageTypes.JobActionResult) return CommandResult.Fail("The Host answered with " + reply.Type + ".");
            var r = reply.GetPayload<JobActionResponse>();
            return r.Ok ? CommandResult.Success : CommandResult.Fail(r.Error ?? "The Host refused.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { return CommandResult.Fail(e.Message); }
    }

    public Task<CommandResult> PauseAsync(Guid id) => ActionAsync(MessageTypes.JobPause, id);

    public Task<CommandResult> ResumeAsync(Guid id) => ActionAsync(MessageTypes.JobResume, id);

    public Task<CommandResult> RetryAsync(Guid id) => ActionAsync(MessageTypes.JobRetry, id);

    /// <summary>Adds the download again from its address, under a new name when the file is there (the Host's Redownload).</summary>
    public Task<CommandResult> RedownloadAsync(Guid id) => ActionAsync(MessageTypes.JobRedownload, id);

    /// <summary>Command jobs.pauseAll / jobs.resumeAll (the summary card's button).</summary>
    public async Task<CommandResult> PauseAllAsync(bool pause)
    {
        try
        {
            var reply = await _host.RequestAsync(IpcMessage.Create(pause ? MessageTypes.JobsPauseAll : MessageTypes.JobsResumeAll, new JobActionRequest()), CancellationToken.None).ConfigureAwait(false);
            if (reply.Type != MessageTypes.HostActionResult) return CommandResult.Fail("The Host answered with " + reply.Type + ".");
            var r = reply.GetPayload<HostActionResult>();
            return r.Ok ? CommandResult.Success : CommandResult.Fail(r.Message ?? "The Host refused.");
        }
        catch (Exception e) { return CommandResult.Fail(e.Message); }
    }

    /// <summary>Command job.remove for one or more downloads: the list entry, and with <paramref name="deleteFile"/> the file (or part file) too.</summary>
    public async Task<CommandResult> RemoveAsync(IReadOnlyCollection<Guid> ids, bool deleteFile)
    {
        if (ids.Count == 0) return CommandResult.Success;
        try
        {
            var reply = await _host.RequestAsync(IpcMessage.Create(MessageTypes.JobRemove,
                new JobActionRequest { JobId = ids.First(), JobIds = ids.ToList(), DeleteFile = deleteFile }), CancellationToken.None).ConfigureAwait(false);
            if (reply.Type != MessageTypes.JobActionResult) return CommandResult.Fail("The Host answered with " + reply.Type + ".");
            var r = reply.GetPayload<JobActionResponse>();
            return r.Ok ? CommandResult.Success : CommandResult.Fail(r.Error ?? "The Host refused.");
        }
        catch (Exception e) { return CommandResult.Fail(e.Message); }
    }

    /// <summary>Command job.segments: the connections' progress of one running download (empty when none).</summary>
    public async Task<IReadOnlyList<SegmentDto>> SegmentsAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var reply = await _host.RequestAsync(IpcMessage.Create(MessageTypes.JobSegments, new JobActionRequest { JobId = id }), cancellationToken).ConfigureAwait(false);
        return reply.Type == MessageTypes.JobSegmentsResult ? reply.GetPayload<List<SegmentDto>>() ?? new List<SegmentDto>() : Array.Empty<SegmentDto>();
    }

    /// <summary>True once <see cref="Dispose"/> ran (the screen went away).</summary>
    public bool Disposed
    {
        get { lock (_gate) return _disposed; }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _timer.Dispose();
        }
    }
}

/// <summary>The outcome of a command that only says yes or no.</summary>
public readonly record struct CommandResult(bool Ok, string? Error)
{
    public static CommandResult Success => new(true, null);

    public static CommandResult Fail(string error) => new(false, error);
}
