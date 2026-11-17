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

    /// <summary>Raised on the UI thread once, after the snapshot's rows are in <see cref="Rows"/> (also for an empty snapshot).</summary>
    public event Action? FirstSnapshotShown;

    /// <summary>Opens the session; the snapshot arrives as a frame and is shown through <see cref="Rows"/>.</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_started) throw new InvalidOperationException("The model was already started.");
        _started = true;
        await _host.OpenAsync(OnFrame, cancellationToken).ConfigureAwait(false);
    }

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
                if (IsRemoval(e.EventType)) _removed.Add(e.Job.Id);
                _pending[e.Job.Id] = (e.Job, e.EventType);
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
        DefaultDownloadDirectory = snap.Settings?.DefaultDownloadDirectory ?? "";
        foreach (var j in snap.Jobs.OrderByDescending(x => x.CreatedAt))
        {
            var row = new JobRow { Id = j.Id };
            row.ApplyFrom(j);
            _byId[j.Id] = row;
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
                    if (_byId.Remove(job.Id, out var gone)) Rows.Remove(gone);
                    continue;
                }

                // Batches posted from different threads (no flush interval) can land out of order: never re-add a removed job.
                bool wasRemoved;
                lock (_gate) wasRemoved = _removed.Contains(job.Id);
                if (wasRemoved) continue;

                if (_byId.TryGetValue(job.Id, out var row)) row.ApplyFrom(job);
                else
                {
                    row = new JobRow { Id = job.Id };
                    row.ApplyFrom(job);
                    _byId[job.Id] = row;
                    Rows.Insert(InsertIndexFor(row), row);
                }
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
    public async Task<AddJobResponse> AddAsync(string url, string? destination = null, string? fileName = null,
        string startMode = StartModes.StartNow, CancellationToken cancellationToken = default)
    {
        var request = IpcMessage.Create(MessageTypes.JobAdd, new AddJobRequest
        {
            Url = url,
            DestinationDirectory = string.IsNullOrWhiteSpace(destination) ? null : destination,
            FileName = string.IsNullOrEmpty(fileName) ? null : fileName,
            StartMode = startMode,
            Source = "android",
            ConflictPolicy = "rename",
        });
        var reply = await _host.RequestAsync(request, cancellationToken).ConfigureAwait(false);
        return reply.Type == MessageTypes.JobAddResult
            ? reply.GetPayload<AddJobResponse>()
            : new AddJobResponse { Accepted = false, Error = "The Host answered with " + reply.Type + "." };
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
