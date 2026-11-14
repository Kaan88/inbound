namespace DownloadManager.Desktop.WinUI;

/// <summary>What a batch item asks the window to do.</summary>
internal enum CoalescedOp
{
    /// <summary>Add or change: the newest state of the key.</summary>
    Upsert,

    /// <summary>The key is gone.</summary>
    Remove,

    /// <summary>A whole-state replacement (a snapshot). Everything the reset covers that came before it is dropped.</summary>
    Reset,

    /// <summary>An event that is applied in its place and never merged with another (a connection going down or up).</summary>
    Barrier,
}

/// <summary>One thing to apply. <see cref="Key"/> is the key the event was posted under; <see cref="Kind"/> is the caller's own event name.</summary>
internal readonly record struct CoalescedItem<TKey, TKind, TValue>(CoalescedOp Op, TKey Key, TKind Kind, TValue Value);

/// <summary>
/// How the coalescer asks for a tick. <see cref="Schedule"/> may be called from any thread, once per batch (not once per
/// event) and starts one one-shot timer on the UI thread that ends in <see cref="EventCoalescer{TKey,TKind,TValue}.Drain"/>.
/// <see cref="Cancel"/> is called on the UI thread when a flush already took what the timer was for. While nothing is pending
/// nothing is scheduled, so an idle window has no timer running.
/// </summary>
internal interface ITickScheduler
{
    void Schedule();

    void Cancel();
}

/// <summary>
/// Gathers events from a connection thread and hands them to the UI thread as one batch per tick (PERF.16, PERF.19).
/// <para>
/// Rules, per key: the newest <c>Upsert</c> wins and keeps the strongest kind (<c>mergeKind</c>); a <c>Remove</c> after an
/// upsert wins; an upsert after a remove is applied as the remove and then the upsert, so a row that went and came back is
/// made again. A <c>Reset</c> drops what is pending for the keys it covers. A <c>Barrier</c> is applied where it stood and
/// seals what came before it: later events for the same key do not merge across it. Items come out in the order of their
/// newest event, so a change that arrived after a barrier or a reset is applied after it.
/// </para>
/// <para>
/// A new event family (the Sites events, say) joins like this: pick a key that includes the family (so two families never
/// merge), post with <see cref="Upsert"/> / <see cref="Remove"/> from the event handler on any thread, and apply the
/// <see cref="CoalescedItem{TKey,TKind,TValue}"/>s the window gets from <see cref="Drain"/> in one place. The status bar,
/// buttons and counts are refreshed once after the whole batch. Use <see cref="Barrier"/> for an event that must keep its
/// place, and <see cref="FlushNow"/> for something the user is waiting on.
/// </para>
/// Thread-safety: the lock only guards the pending set; the scheduler is called after it is released, and no user code runs
/// inside it (the merge functions are called inside it: keep them pure and quick).
/// </summary>
internal sealed class EventCoalescer<TKey, TKind, TValue> where TKey : notnull
{
    private sealed class Entry
    {
        public CoalescedOp Op;
        public TKey Key = default!;
        public TKind Kind = default!;
        public TValue Value = default!;
        public long Seq;
        public bool RemovedFirst;
        public TKind RemovedKind = default!;
        public TValue RemovedValue = default!;
        public long RemovedSeq;
    }

    private readonly object _gate = new();
    private readonly ITickScheduler _scheduler;
    private readonly Func<TKind, TKind, TKind> _mergeKind;
    private readonly Func<TValue, TValue, TValue> _mergeValue;
    private readonly Dictionary<TKey, Entry> _current = new();
    private readonly List<Entry> _sealed = new();
    private long _seq;
    private bool _armed;

    /// <param name="mergeKind">(older, newer) to the kind kept when two upserts of a key meet; the default keeps the newer.</param>
    /// <param name="mergeValue">(older, newer) to the value kept; the default keeps the newer. Lets the caller carry what the older one saw.</param>
    public EventCoalescer(ITickScheduler scheduler, Func<TKind, TKind, TKind>? mergeKind = null, Func<TValue, TValue, TValue>? mergeValue = null)
    {
        _scheduler = scheduler;
        _mergeKind = mergeKind ?? ((_, newer) => newer);
        _mergeValue = mergeValue ?? ((_, newer) => newer);
    }

    /// <summary>True while something waits for a tick.</summary>
    public bool HasPending
    {
        get
        {
            lock (_gate) return _current.Count > 0 || _sealed.Count > 0;
        }
    }

    /// <param name="keepPlace">A later upsert of the same key keeps the first one's place in the batch, so rows are added in the
    /// order they were first announced. Without it the item moves to its newest event's place.</param>
    public void Upsert(TKey key, TKind kind, TValue value, bool keepPlace = false)
    {
        bool arm;
        lock (_gate)
        {
            if (_current.TryGetValue(key, out var entry))
            {
                if (entry.Op == CoalescedOp.Remove)
                {
                    entry.RemovedFirst = true;
                    entry.RemovedKind = entry.Kind;
                    entry.RemovedValue = entry.Value;
                    entry.RemovedSeq = entry.Seq;
                    entry.Op = CoalescedOp.Upsert;
                    entry.Kind = kind;
                    entry.Value = value;
                }
                else
                {
                    entry.Kind = entry.Op == CoalescedOp.Upsert ? _mergeKind(entry.Kind, kind) : kind;
                    entry.Value = entry.Op == CoalescedOp.Upsert ? _mergeValue(entry.Value, value) : value;
                    var merged = entry.Op == CoalescedOp.Upsert;
                    entry.Op = CoalescedOp.Upsert;
                    if (merged && keepPlace) goto placed;
                }

                entry.Seq = ++_seq;
            placed:;
            }
            else
            {
                _current[key] = new Entry { Op = CoalescedOp.Upsert, Key = key, Kind = kind, Value = value, Seq = ++_seq };
            }

            arm = Arm();
        }

        if (arm) _scheduler.Schedule();
    }

    /// <summary>The key is gone. Whatever was pending for it is dropped; the removal itself is still applied.</summary>
    public void Remove(TKey key, TKind kind, TValue value)
    {
        bool arm;
        lock (_gate)
        {
            _current[key] = new Entry { Op = CoalescedOp.Remove, Key = key, Kind = kind, Value = value, Seq = ++_seq };
            arm = Arm();
        }

        if (arm) _scheduler.Schedule();
    }

    /// <summary>
    /// A whole-state replacement under <paramref name="key"/> (one per family and source). Pending entries whose key
    /// <paramref name="covers"/> (default: all) are dropped, barriers excepted; a later reset under the same key replaces this one.
    /// </summary>
    public void Reset(TKey key, TKind kind, TValue value, Func<TKey, bool>? covers = null)
    {
        bool arm;
        lock (_gate)
        {
            covers ??= _ => true;
            foreach (var stale in _current.Keys.Where(covers).ToList()) _current.Remove(stale);
            _sealed.RemoveAll(entry => entry.Op != CoalescedOp.Barrier && covers(entry.Key));
            _current[key] = new Entry { Op = CoalescedOp.Reset, Key = key, Kind = kind, Value = value, Seq = ++_seq };
            arm = Arm();
        }

        if (arm) _scheduler.Schedule();
    }

    /// <summary>An event that keeps its place: what was pending before it is sealed, so nothing posted later merges into it.</summary>
    public void Barrier(TKey key, TKind kind, TValue value)
    {
        bool arm;
        lock (_gate)
        {
            _sealed.AddRange(_current.Values);
            _current.Clear();
            _sealed.Add(new Entry { Op = CoalescedOp.Barrier, Key = key, Kind = kind, Value = value, Seq = ++_seq });
            arm = Arm();
        }

        if (arm) _scheduler.Schedule();
    }

    /// <summary>Takes everything pending, oldest event first; empty when nothing is. Call on the UI thread (a tick or a flush).</summary>
    public List<CoalescedItem<TKey, TKind, TValue>> Drain()
    {
        List<Entry> taken;
        lock (_gate)
        {
            _armed = false;
            if (_current.Count == 0 && _sealed.Count == 0) return [];
            taken = new List<Entry>(_sealed.Count + _current.Count);
            taken.AddRange(_sealed);
            taken.AddRange(_current.Values);
            _sealed.Clear();
            _current.Clear();
        }

        var items = new List<(long Seq, CoalescedItem<TKey, TKind, TValue> Item)>(taken.Count + 4);
        foreach (var entry in taken)
        {
            if (entry.RemovedFirst)
                items.Add((entry.RemovedSeq, new(CoalescedOp.Remove, entry.Key, entry.RemovedKind, entry.RemovedValue)));
            items.Add((entry.Seq, new(entry.Op, entry.Key, entry.Kind, entry.Value)));
        }

        items.Sort((a, b) => a.Seq.CompareTo(b.Seq));
        return items.Select(pair => pair.Item).ToList();
    }

    /// <summary>For the user's own action: takes what is pending now and cancels the tick that was for it. UI thread.</summary>
    public List<CoalescedItem<TKey, TKind, TValue>> FlushNow()
    {
        bool wasArmed;
        lock (_gate) wasArmed = _armed;
        var items = Drain();
        if (wasArmed) _scheduler.Cancel();
        return items;
    }

    /// <summary>Drops everything pending (the source went away).</summary>
    public void Clear()
    {
        bool wasArmed;
        lock (_gate)
        {
            _current.Clear();
            _sealed.Clear();
            wasArmed = _armed;
            _armed = false;
        }

        if (wasArmed) _scheduler.Cancel();
    }

    // True when this event is the first since the last drain: the caller then asks the scheduler, outside the lock.
    private bool Arm()
    {
        if (_armed) return false;
        _armed = true;
        return true;
    }
}
