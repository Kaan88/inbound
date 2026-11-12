using DownloadManager.Contracts;

namespace DownloadManager.Runtime;

/// <summary>
/// "Ready to choose files": tells the user once when magnets that wait for their choice of files have their file list,
/// as <see cref="Remote.RemoteNotices"/> does for a provider's items. A torrent's first change to waiting starts a short
/// wait, so a batch of magnets is one notice and a choice made meanwhile is none. Nothing is said while the app's own
/// window is in front, where the rows show it, or when completion notifications are off. What already waited when this
/// started isn't news. A timer exists only while something waits to be told.
/// </summary>
public sealed class TorrentNotices : IDisposable
{
    /// <summary>From the first torrent ready to the notice.</summary>
    public static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly TimeProvider _time;
    private readonly Func<bool> _tellReady;
    private readonly Func<bool> _appInFront;
    private readonly Action<TorrentNotice> _tell;
    private readonly Action<string>? _log;
    private readonly Lock _sync = new();

    /// <summary>Waiting torrents not told about yet, with their names. Guarded by <see cref="_sync"/>.</summary>
    private readonly Dictionary<Guid, string> _pending = [];

    /// <summary>Waiting torrents already told about (or waiting at the start). Guarded by <see cref="_sync"/>.</summary>
    private readonly HashSet<Guid> _told = [];

    private ITimer? _timer;
    private bool _disposed;

    /// <summary>A notice's title and words.</summary>
    public sealed record TorrentNotice(string Title, string Message);

    /// <param name="atStart">The jobs now: those already waiting are not news.</param>
    /// <param name="tell">Must not block. Called on the timer's thread.</param>
    public TorrentNotices(IEnumerable<JobDto> atStart, TimeProvider time, Func<bool> tellReady, Func<bool> appInFront,
        Action<TorrentNotice> tell, Action<string>? log = null)
    {
        _time = time;
        _tellReady = tellReady;
        _appInFront = appInFront;
        _tell = tell;
        _log = log;
        foreach (var job in atStart)
            if (Waiting(job)) _told.Add(job.Id);
    }

    /// <summary>Listens to the service from now on.</summary>
    public static TorrentNotices Listen(DownloadManagerService service, TimeProvider time, Func<bool> tellReady, Func<bool> appInFront,
        Action<TorrentNotice> tell, Action<string>? log = null)
    {
        var notices = new TorrentNotices(service.GetSnapshot().Jobs, time, tellReady, appInFront, tell, log);
        notices.Attach(service);
        return notices;
    }

    private DownloadManagerService? _attached;

    private void Attach(DownloadManagerService service)
    {
        _attached = service;
        service.JobChanged += OnJobChanged;
    }

    private void OnJobChanged(object? sender, JobChangeEventArgs e)
    {
        if (e.ChangeType == "progress") return;
        Saw(e.Job, e.ChangeType == "removed");
    }

    /// <summary>A job as it is now.</summary>
    public void Saw(JobDto job, bool removed = false)
    {
        lock (_sync)
        {
            if (_disposed) return;
            if (removed || !Waiting(job))
            {
                _pending.Remove(job.Id);
                _told.Remove(job.Id);
                return;
            }

            if (_told.Contains(job.Id) || _pending.ContainsKey(job.Id)) return;
            _pending[job.Id] = string.IsNullOrWhiteSpace(job.Torrent!.Name) ? job.FileName : job.Torrent.Name;
            _timer ??= _time.CreateTimer(_ => Count(), null, Wait, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>The notice for these torrents' names.</summary>
    public static TorrentNotice Notice(IReadOnlyList<string> names)
        => new("Ready to choose files", names.Count == 1 ? names[0] : $"{names.Count} torrents");

    public void Dispose()
    {
        if (_attached is { } service) service.JobChanged -= OnJobChanged;
        lock (_sync)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }

    private void Count()
    {
        // Read before the lock: the setting and the screen are someone else's.
        var tellReady = Ask(_tellReady, true);
        var inFront = Ask(_appInFront, false);
        List<string> names;
        lock (_sync)
        {
            _timer?.Dispose();
            _timer = null;
            if (_disposed) return;
            names = [.. _pending.Values];
            foreach (var id in _pending.Keys) _told.Add(id);
            _pending.Clear();
        }

        if (names.Count == 0 || !tellReady || inFront) return;
        try
        {
            _tell(Notice(names));
        }
        catch (Exception ex)
        {
            _log?.Invoke($"A torrent notice failed: {ex.GetType().Name}");
        }
    }

    private bool Ask(Func<bool> question, bool otherwise)
    {
        try
        {
            return question();
        }
        catch (Exception ex)
        {
            _log?.Invoke($"A torrent notice couldn't be decided: {ex.GetType().Name}");
            return otherwise;
        }
    }

    private static bool Waiting(JobDto job) => job.Torrent is { AwaitingFileChoice: true };
}
