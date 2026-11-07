using DownloadManager.Core.Providers;

namespace DownloadManager.Runtime.Remote;

/// <summary>A tray balloon about remote items: their provider, and counts. Never a name, a link or anything else of an item.</summary>
public sealed record RemoteNotice(string Title, string Message);

/// <summary>
/// Tells the user when remote items come to need them: ready to choose files, or failed. What already needed them
/// when this started isn't news. A change only queues here, as everything listening to the coordinator must. The
/// first one starts a wait, and then what still needs the user is counted, so a batch of adds is one balloon and a
/// choice made meanwhile is none. Only a count rising says anything again, measured from the last count less what
/// has been dealt with since. Copied rows wait for Enter and blocked ones retry by themselves, so neither counts.
/// Nothing is said while the app's own window is in front, where the rows are on show anyway. Ready to choose follows
/// the completion notification setting; a failure always shows.
/// </summary>
public sealed class RemoteNotices : IDisposable
{
    /// <summary>From the first change to the count.</summary>
    public static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _time;
    private readonly Func<bool> _tellReady;
    private readonly Func<bool> _appInFront;
    private readonly Action<RemoteNotice> _tell;
    private readonly Action<string>? _log;
    private readonly Lock _sync = new();

    /// <summary>Every job as last heard of. Guarded by <see cref="_sync"/>.</summary>
    private readonly Dictionary<Guid, Heard> _jobs = [];

    /// <summary>What needed the user at the last count and still does, in the state it was counted in. Guarded by <see cref="_sync"/>.</summary>
    private readonly Dictionary<Guid, RemoteJobState> _counted = [];

    private ITimer? _timer;
    private bool _disposed;
    private RemoteJobCoordinator? _source;

    private readonly record struct Heard(string Provider, RemoteJobState State, bool Held);

    /// <param name="atStart">What is on the list now. What of it needs the user is already counted.</param>
    /// <param name="tellReady">Whether ready to choose is told about. Read at each count.</param>
    /// <param name="appInFront">Whether the app's own window is in front. Read at each count.</param>
    /// <param name="tell">Must not block. Called on the timer's thread.</param>
    public RemoteNotices(IEnumerable<RemoteJobView> atStart, TimeProvider time, Func<bool> tellReady, Func<bool> appInFront, Action<RemoteNotice> tell, Action<string>? log = null)
    {
        _time = time;
        _tellReady = tellReady;
        _appInFront = appInFront;
        _tell = tell;
        _log = log;
        foreach (var job in atStart)
        {
            var heard = Of(job);
            _jobs[job.Id] = heard;
            if (NeedsUser(heard)) _counted[job.Id] = heard.State;
        }
    }

    /// <summary>Listens to the coordinator from now on. What is on its list now is what is already counted.</summary>
    public static RemoteNotices Listen(RemoteJobCoordinator jobs, TimeProvider time, Func<bool> tellReady, Func<bool> appInFront, Action<RemoteNotice> tell, Action<string>? log = null)
    {
        var notices = new RemoteNotices(jobs.List(), time, tellReady, appInFront, tell, log) { _source = jobs };
        jobs.Changed += notices.OnChanged;
        jobs.Removed += notices.OnRemoved;
        return notices;
    }

    /// <summary>Only queues: called while the coordinator tells its listeners, so nothing here calls back into it.</summary>
    public void Saw(RemoteJobView job)
    {
        lock (_sync)
        {
            if (_disposed) return;
            var heard = Of(job);
            _jobs[job.Id] = heard;

            // Dealt with, or needing something else of the user now: what was counted of it no longer stands.
            if (_counted.TryGetValue(job.Id, out var counted) && (!NeedsUser(heard) || counted != heard.State)) _counted.Remove(job.Id);
            if (NeedsUser(heard) && !_counted.ContainsKey(job.Id)) _timer ??= _time.CreateTimer(_ => Count(), null, Wait, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>A job left the list.</summary>
    public void Gone(Guid id)
    {
        lock (_sync)
        {
            _jobs.Remove(id);
            _counted.Remove(id);
        }
    }

    /// <summary>The balloon for one provider's counts, which is all it says.</summary>
    public static RemoteNotice Notice(string provider, int ready, int failed)
    {
        var parts = new List<string>(2);
        if (ready > 0) parts.Add($"{ready} ready to choose");
        if (failed > 0) parts.Add($"{failed} failed");
        return new RemoteNotice(provider, string.Join(" · ", parts));
    }

    public void Dispose()
    {
        if (_source is { } source)
        {
            source.Changed -= OnChanged;
            source.Removed -= OnRemoved;
        }

        lock (_sync)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }

    private void OnChanged(object? sender, RemoteJobView job) => Saw(job);

    private void OnRemoved(object? sender, Guid id) => Gone(id);

    private void Count()
    {
        // Read before the lock: the setting and the screen are someone else's.
        var tellReady = Ask(_tellReady, true);
        var inFront = Ask(_appInFront, false);
        var notices = new List<RemoteNotice>();
        lock (_sync)
        {
            _timer?.Dispose();
            _timer = null;
            if (_disposed) return;

            var needing = _jobs.Where(pair => NeedsUser(pair.Value)).ToList();
            var news = needing
                .Where(pair => !(_counted.TryGetValue(pair.Key, out var counted) && counted == pair.Value.State))
                .Where(pair => tellReady || pair.Value.State == RemoteJobState.Failed)
                .Select(pair => pair.Value.Provider)
                .ToHashSet(StringComparer.Ordinal);

            // Whatever is said now, all of it has been counted: the app in front showed it, and what isn't told of isn't news later.
            _counted.Clear();
            foreach (var (id, heard) in needing) _counted[id] = heard.State;
            if (inFront) return;

            foreach (var provider in news.Order(StringComparer.Ordinal))
            {
                var states = needing.Where(pair => pair.Value.Provider == provider).Select(pair => pair.Value.State).ToList();
                notices.Add(Notice(provider, tellReady ? states.Count(state => state == RemoteJobState.AwaitingSelection) : 0, states.Count(state => state == RemoteJobState.Failed)));
            }
        }

        foreach (var notice in notices)
        {
            try
            {
                _tell(notice);
            }
            catch (Exception ex)
            {
                // Telling is never worth failing anything else.
                _log?.Invoke($"A remote notice failed: {ex.GetType().Name}");
            }
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
            _log?.Invoke($"A remote notice couldn't be decided: {ex.GetType().Name}");
            return otherwise;
        }
    }

    private static Heard Of(RemoteJobView job) => new(job.ProviderName, job.State, job.HeldForApproval);

    /// <summary>Copied rows wait for Enter and blocked ones retry by themselves: only these need the user.</summary>
    private static bool NeedsUser(Heard heard) => !heard.Held && heard.State is RemoteJobState.AwaitingSelection or RemoteJobState.Failed;
}
