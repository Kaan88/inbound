namespace DownloadManager.Runtime;

/// <summary>
/// MAC.6: Options' "Pause downloads on a phone hotspot, in Low Data Mode or in Low Power Mode". The Host reports what
/// the Mac says (the menu-bar helper watches the network path and the power state). When saving starts, the downloads
/// running at that moment are paused and remembered; when it ends, those still paused are resumed. Nothing else is
/// touched: a download the user pauses meanwhile and resumes stays theirs, one they start on the hotspot runs, and
/// downloads that were paused before stay paused.
/// </summary>
public sealed class SavingPauses
{
    private readonly Func<IReadOnlyList<(Guid Id, string State)>> _jobs;
    private readonly Func<Guid, Task> _pause;
    private readonly Func<Guid, Task> _resume;
    private readonly Action<string> _log;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _order = new(1, 1); // one change's pauses and resumes before the next's
    private readonly HashSet<Guid> _paused = [];
    private readonly HashSet<Guid> _settled = []; // paused by this and seen paused; until then a late progress event is ignored
    private bool _costly;
    private bool _lowPower;
    private bool _enabled;
    private bool _saving;

    public SavingPauses(Func<IReadOnlyList<(Guid Id, string State)>> jobs, Func<Guid, Task> pause, Func<Guid, Task> resume, Action<string> log)
    {
        _jobs = jobs;
        _pause = pause;
        _resume = resume;
        _log = log;
    }

    /// <summary>The downloads this paused and will resume (for tests and the log).</summary>
    public IReadOnlyCollection<Guid> Paused
    {
        get { lock (_gate) return _paused.ToArray(); }
    }

    /// <summary>
    /// A download's state changed. One this paused that runs again was the user's doing (or a schedule's), so it is
    /// theirs from then on: a later pause of theirs is not undone when saving ends.
    /// </summary>
    public void Observe(Guid id, string state)
    {
        lock (_gate)
        {
            if (state == "paused") { if (_paused.Contains(id)) _settled.Add(id); }
            else if (_settled.Remove(id)) _paused.Remove(id);
        }
    }

    /// <summary>The helper's <c>network expensive|constrained|normal</c>.</summary>
    public Task NetworkAsync(string kind)
    {
        lock (_gate) _costly = kind is "expensive" or "constrained";
        return ApplyAsync();
    }

    /// <summary>The helper's <c>power low|normal</c>.</summary>
    public Task PowerAsync(string state)
    {
        lock (_gate) _lowPower = state == "low";
        return ApplyAsync();
    }

    /// <summary>The setting; turning it off resumes what it paused.</summary>
    public Task EnabledAsync(bool on)
    {
        lock (_gate) _enabled = on;
        return ApplyAsync();
    }

    private async Task ApplyAsync()
    {
        await _order.WaitAsync().ConfigureAwait(false);
        try { await ApplyInOrderAsync().ConfigureAwait(false); }
        finally { _order.Release(); }
    }

    private async Task ApplyInOrderAsync()
    {
        List<Guid> pause = [], resume = [];
        string? reason = null;
        lock (_gate)
        {
            var saving = _enabled && (_costly || _lowPower);
            if (saving == _saving) return;
            _saving = saving;
            if (saving)
            {
                foreach (var (id, state) in _jobs())
                {
                    if (state is "connecting" or "downloading" or "retryWaiting" && _paused.Add(id)) pause.Add(id);
                }

                reason = _lowPower ? "Low Power Mode" : "a costly network";
            }
            else
            {
                var states = _jobs().ToDictionary(job => job.Id, job => job.State);
                // Only those still paused: one the user resumed or deleted meanwhile is left alone.
                resume.AddRange(_paused.Where(id => states.TryGetValue(id, out var state) && state == "paused"));
                _paused.Clear();
                _settled.Clear();
            }
        }

        if (reason is not null) _log($"Saving for {reason}: pausing {pause.Count} download(s).");
        else _log($"Saving ended: resuming {resume.Count} download(s).");
        foreach (var id in pause) await Try(_pause, id).ConfigureAwait(false);
        foreach (var id in resume) await Try(_resume, id).ConfigureAwait(false);
    }

    private async Task Try(Func<Guid, Task> action, Guid id)
    {
        try
        {
            await action(id).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log($"A saving pause or resume failed: {ex.GetType().Name}");
        }
    }
}
