namespace DownloadManager.Core;

public interface IHostConnectionLimiter
{
    /// <summary>
    /// Waits for one connection to <paramref name="host"/> for <paramref name="ownerId"/> (a job). A
    /// <paramref name="yieldable"/> slot may be asked to give itself up, through <see cref="HostSlot.YieldToken"/>,
    /// so that another job on the same host gets a connection; only a caller that can stop at any byte and go
    /// on later (a segment worker) should ask for one.
    /// </summary>
    Task<HostSlot> AcquireAsync(string host, Guid ownerId, bool yieldable, CancellationToken cancellationToken);
}

/// <summary>One connection to a host. Disposing it gives the connection back.</summary>
public sealed class HostSlot : IDisposable
{
    private readonly FairHostConnectionLimiter _limiter;
    // Never disposed: it has no timer or links, and a holder may still read its token after the release.
    private readonly CancellationTokenSource _yield = new();
    private int _released;
    private volatile bool _yieldSignalled;

    internal HostSlot(FairHostConnectionLimiter limiter, string host, Guid ownerId, bool yieldable, long sequence)
    {
        _limiter = limiter;
        Host = host;
        OwnerId = ownerId;
        Yieldable = yieldable;
        Sequence = sequence;
    }

    public string Host { get; }
    public Guid OwnerId { get; }
    public bool Yieldable { get; }
    internal long Sequence { get; }

    /// <summary>Set under the limiter's lock; the token below is cancelled after the lock is let go.</summary>
    internal bool YieldAsked { get; set; }

    /// <summary>True once the limiter wants this connection for another job.</summary>
    public bool YieldRequested => _yieldSignalled;

    /// <summary>Cancelled when the limiter wants this connection for another job. Never cancelled for a slot that can't yield.</summary>
    public CancellationToken YieldToken => _yield.Token;

    internal void SignalYield()
    {
        _yieldSignalled = true;
        try
        {
            _yield.Cancel();
        }
        catch (AggregateException)
        {
            // A caller's registration threw; the request still stands.
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0)
        {
            return;
        }

        _limiter.Release(this);
    }
}

/// <summary>
/// Caps connections per host across every job, and shares them out fairly (UI.B7). A segmented job holds a
/// connection for a whole range, so without sharing, the first jobs on a CDN keep every connection and a job
/// admitted later waits on "Connecting" until one of them finishes.
/// <list type="bullet">
/// <item>One gate per host, whose limit can change while connections are out. A lower limit grants nothing
/// until the connections in use drop below it, and asks the excess to yield.</item>
/// <item>A free connection goes to the waiting job holding the fewest on that host, the oldest waiter first.</item>
/// <item>A job below its fair share, <c>max(1, limit / jobs on the host)</c>, that is waiting on a full gate
/// asks the job furthest above its share to yield one yieldable connection.</item>
/// <item>A job below its share that gives a connection back keeps it for a short while (the reuse window), as
/// long as no waiting job is below its own share. A media job asks for one connection per fragment, and a
/// segmented worker asks again between ranges; without this the connection would go to the job above its
/// share, and the next request would make that job yield it back, aborting a response and flushing its file
/// each time.</item>
/// </list>
/// </summary>
public sealed class FairHostConnectionLimiter : IHostConnectionLimiter
{
    private readonly Lock _sync = new();
    private readonly Lock _notifySync = new();
    private readonly Queue<Guid> _notifications = new();
    private readonly Dictionary<string, Gate> _gates = new(StringComparer.OrdinalIgnoreCase);
    private int _limit;
    private int _active;
    private int _peak;
    private long _sequence;
    private readonly long _reuseWindowMs;

    /// <summary>How long a released connection is kept for its job when nobody waiting is short of their share.</summary>
    public static readonly TimeSpan DefaultReuseWindow = TimeSpan.FromMilliseconds(250);

    public FairHostConnectionLimiter(int limit, TimeSpan? reuseWindow = null)
    {
        _limit = Math.Max(1, limit);
        _reuseWindowMs = Math.Max(0, (long)(reuseWindow ?? DefaultReuseWindow).TotalMilliseconds);
    }

    /// <summary>Raised, never under the limiter's lock, when a job starts or stops waiting with no connection to its host.</summary>
    public event Action<Guid>? WaitingChanged;

    public int Limit
    {
        get
        {
            lock (_sync) return _limit;
        }
    }

    public int Peak
    {
        get
        {
            lock (_sync) return _peak;
        }
    }

    /// <summary>Connections out across every host right now.</summary>
    public int Active
    {
        get
        {
            lock (_sync) return _active;
        }
    }

    public int HeldBy(string host, Guid ownerId)
    {
        lock (_sync)
        {
            return _gates.TryGetValue(host, out var gate) ? gate.Held.Count(slot => slot.OwnerId == ownerId) : 0;
        }
    }

    public int ActiveOn(string host)
    {
        lock (_sync)
        {
            return _gates.TryGetValue(host, out var gate) ? gate.Held.Count : 0;
        }
    }

    /// <summary>The host this job is waiting for with no connection to it, or null.</summary>
    public string? WaitingHost(Guid ownerId)
    {
        lock (_sync)
        {
            foreach (var gate in _gates.Values)
            {
                if (gate.WaitingOwners.Contains(ownerId))
                {
                    return gate.Host;
                }
            }

            return null;
        }
    }

    public void UpdateLimit(int limit)
    {
        var effects = new Effects();
        lock (_sync)
        {
            _limit = Math.Max(1, limit);
            foreach (var gate in _gates.Values.ToList())
            {
                // A kept connection is only a courtesy; the new limit decides afresh.
                gate.Parks.Clear();
                GrantLocked(gate, effects);
                RebalanceLocked(gate, effects);
                TrackWaitingLocked(gate);
                ForgetIfIdleLocked(gate);
            }
        }

        effects.Apply();
        DispatchNotifications();
    }

    public Task<HostSlot> AcquireAsync(string host, Guid ownerId, bool yieldable, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        host ??= string.Empty;
        var effects = new Effects();
        HostSlot? immediate = null;
        Waiter? waiter = null;
        lock (_sync)
        {
            if (!_gates.TryGetValue(host, out var gate))
            {
                gate = new Gate(host);
                _gates[host] = gate;
            }

            var kept = gate.Parks.FindIndex(park => park.OwnerId == ownerId);
            if (kept >= 0)
            {
                // The connection this job gave back a moment ago, still kept for it.
                gate.Parks.RemoveAt(kept);
                immediate = GrantSlotLocked(gate, ownerId, yieldable);
                // This job may have had another request waiting with nothing held.
                TrackWaitingLocked(gate);
            }
            else if (gate.Waiters.Count == 0 && gate.Held.Count + gate.Parks.Count < _limit)
            {
                // Free connections are always handed out at once, so waiters mean the gate is full.
                // Someone else may now be above a fair share with this owner on the host; nobody is waiting, so nothing yields.
                immediate = GrantSlotLocked(gate, ownerId, yieldable);
            }
            else
            {
                waiter = new Waiter(gate, ownerId, yieldable, ++_sequence);
                gate.Waiters.Add(waiter);
                // Only kept connections can be free here; a job short of its share takes one of those at once.
                GrantLocked(gate, effects);
                RebalanceLocked(gate, effects);
                TrackWaitingLocked(gate);
            }
        }

        effects.Apply();
        DispatchNotifications();
        if (immediate is not null)
        {
            return Task.FromResult(immediate);
        }

        return cancellationToken.CanBeCanceled ? WaitAsync(waiter!, cancellationToken) : waiter!.Completion.Task;
    }

    /// <summary>
    /// The cancel registration lives exactly as long as the wait, on the caller's side: a grant on another
    /// thread can't race its creation, and nothing is left on a long-lived token once the slot is in hand.
    /// </summary>
    private async Task<HostSlot> WaitAsync(Waiter waiter, CancellationToken cancellationToken)
    {
        var registration = cancellationToken.Register(static state =>
        {
            var (limiter, pending, token) = ((FairHostConnectionLimiter, Waiter, CancellationToken))state!;
            limiter.Abandon(pending, token);
        }, (this, waiter, cancellationToken));
        try
        {
            return await waiter.Completion.Task.ConfigureAwait(false);
        }
        finally
        {
            // Unregister, not Dispose: Dispose would wait for a cancel callback running elsewhere.
            registration.Unregister();
        }
    }

    internal void Release(HostSlot slot)
    {
        var effects = new Effects();
        lock (_sync)
        {
            if (!_gates.TryGetValue(slot.Host, out var gate) || !gate.Held.Remove(slot))
            {
                return;
            }

            _active--;
            KeepForOwnerLocked(gate, slot.OwnerId);
            GrantLocked(gate, effects);
            RebalanceLocked(gate, effects);
            TrackWaitingLocked(gate);
            ForgetIfIdleLocked(gate);
        }

        effects.Apply();
        DispatchNotifications();
    }

    /// <summary>
    /// Keeps the connection just given back for its job for the reuse window, when that job is below its share
    /// and everyone waiting already has theirs. Then the job's next request gets it back at once instead of
    /// taking it from the others with a yield. A waiting job short of its share is never kept waiting by this.
    /// </summary>
    private void KeepForOwnerLocked(Gate gate, Guid ownerId)
    {
        if (_reuseWindowMs == 0 || gate.Waiters.Count == 0 || gate.Held.Count + gate.Parks.Count >= _limit)
        {
            return;
        }

        var counts = CountsLocked(gate);
        counts.TryAdd(ownerId, 0);
        var share = Math.Max(1, _limit / counts.Count);
        if (counts[ownerId] >= share || gate.Waiters.Any(waiter => counts[waiter.OwnerId] < share))
        {
            return;
        }

        gate.Parks.Add(new Park(ownerId, Environment.TickCount64 + _reuseWindowMs));
        if (gate.Parks.Count == 1)
        {
            ArmParkTimerLocked(gate);
        }
    }

    /// <summary>Connections held or kept, per job, with every waiting job counted (at zero if it holds none).</summary>
    private static Dictionary<Guid, int> CountsLocked(Gate gate)
    {
        var counts = new Dictionary<Guid, int>();
        foreach (var slot in gate.Held) counts[slot.OwnerId] = counts.GetValueOrDefault(slot.OwnerId) + 1;
        foreach (var park in gate.Parks) counts[park.OwnerId] = counts.GetValueOrDefault(park.OwnerId) + 1;
        foreach (var waiter in gate.Waiters) counts.TryAdd(waiter.OwnerId, 0);
        return counts;
    }

    private void ArmParkTimerLocked(Gate gate)
    {
        if (gate.Parks.Count == 0)
        {
            return;
        }

        gate.ParkTimer ??= new Timer(static state =>
        {
            var (limiter, owner) = ((FairHostConnectionLimiter, Gate))state!;
            limiter.ExpireParks(owner);
        }, (this, gate), Timeout.Infinite, Timeout.Infinite);
        // Kept connections are added in deadline order, so the first is the next to run out.
        var due = Math.Max(0, gate.Parks[0].Deadline - Environment.TickCount64);
        gate.ParkTimer.Change(due, Timeout.Infinite);
    }

    private void ExpireParks(Gate gate)
    {
        var effects = new Effects();
        lock (_sync)
        {
            if (!_gates.TryGetValue(gate.Host, out var current) || current != gate)
            {
                return;
            }

            var now = Environment.TickCount64;
            gate.Parks.RemoveAll(park => park.Deadline <= now);
            GrantLocked(gate, effects);
            RebalanceLocked(gate, effects);
            TrackWaitingLocked(gate);
            ForgetIfIdleLocked(gate);
            ArmParkTimerLocked(gate);
        }

        effects.Apply();
        DispatchNotifications();
    }

    private void Abandon(Waiter waiter, CancellationToken token)
    {
        var effects = new Effects();
        lock (_sync)
        {
            if (!waiter.Gate.Waiters.Remove(waiter))
            {
                // Already granted; the caller gets the slot and disposes it.
                return;
            }

            // A yield asked for on this waiter's behalf can't be taken back; the connection then goes to the next waiter.
            RebalanceLocked(waiter.Gate, effects);
            TrackWaitingLocked(waiter.Gate);
            ForgetIfIdleLocked(waiter.Gate);
        }

        waiter.Completion.TrySetCanceled(token);
        effects.Apply();
        DispatchNotifications();
    }

    private HostSlot GrantSlotLocked(Gate gate, Guid ownerId, bool yieldable)
    {
        var slot = new HostSlot(this, gate.Host, ownerId, yieldable, ++_sequence);
        gate.Held.Add(slot);
        _active++;
        _peak = Math.Max(_peak, _active);
        return slot;
    }

    private void GrantLocked(Gate gate, Effects effects)
    {
        if (gate.Parks.Count > 0 && gate.Waiters.Count > 0)
        {
            var counts = CountsLocked(gate);
            var share = Math.Max(1, _limit / counts.Count);
            if (gate.Waiters.Any(waiter => counts[waiter.OwnerId] < share))
            {
                // Someone waiting is short of their share: keeping connections for others would starve them.
                gate.Parks.Clear();
            }
        }

        while (gate.Held.Count + gate.Parks.Count < _limit && gate.Waiters.Count > 0)
        {
            // Fewest held first, so a job with none gets in ahead of one that already transfers.
            Waiter? next = null;
            var fewest = int.MaxValue;
            foreach (var waiter in gate.Waiters)
            {
                var held = gate.Held.Count(slot => slot.OwnerId == waiter.OwnerId);
                if (held < fewest || (held == fewest && waiter.Sequence < next!.Sequence))
                {
                    fewest = held;
                    next = waiter;
                }
            }

            gate.Waiters.Remove(next!);
            var slot = GrantSlotLocked(gate, next!.OwnerId, next.Yieldable);
            effects.Grants.Add((next, slot));
        }
    }

    /// <summary>
    /// Asks holders to yield until the connections that will remain fit the limit, and until no waiting job
    /// is left below its fair share while another holds more than its own. Yields already asked for count as
    /// connections on their way back, so a slow worker is not asked twice.
    /// </summary>
    private void RebalanceLocked(Gate gate, Effects effects)
    {
        for (var guard = 0; guard < 256; guard++)
        {
            var remaining = new Dictionary<Guid, int>();
            var waiting = new Dictionary<Guid, int>();
            var firstWait = new Dictionary<Guid, long>();
            foreach (var slot in gate.Held)
            {
                remaining[slot.OwnerId] = remaining.GetValueOrDefault(slot.OwnerId) + (slot.YieldAsked ? 0 : 1);
            }

            // A kept connection is its job's, and can't be asked to yield (it carries no transfer).
            foreach (var park in gate.Parks)
            {
                remaining[park.OwnerId] = remaining.GetValueOrDefault(park.OwnerId) + 1;
            }

            foreach (var waiter in gate.Waiters)
            {
                waiting[waiter.OwnerId] = waiting.GetValueOrDefault(waiter.OwnerId) + 1;
                if (!firstWait.ContainsKey(waiter.OwnerId)) firstWait[waiter.OwnerId] = waiter.Sequence;
                remaining.TryAdd(waiter.OwnerId, 0);
            }

            var total = remaining.Values.Sum();
            if (total > _limit)
            {
                if (!AskYieldLocked(gate, remaining, aboveShare: 0, effects)) break;
                continue;
            }

            if (waiting.Count == 0)
            {
                break;
            }

            var share = Math.Max(1, _limit / Math.Max(1, remaining.Count));
            // What the free connections (and those already on their way back) will do: fewest held first.
            var after = new Dictionary<Guid, int>(remaining);
            var stillWaiting = new Dictionary<Guid, int>(waiting);
            for (var free = _limit - total; free > 0; free--)
            {
                Guid? next = null;
                foreach (var (owner, count) in stillWaiting)
                {
                    if (count == 0) continue;
                    if (next is null || after[owner] < after[next.Value] ||
                        (after[owner] == after[next.Value] && firstWait[owner] < firstWait[next.Value]))
                    {
                        next = owner;
                    }
                }

                if (next is null) break;
                after[next.Value]++;
                stillWaiting[next.Value]--;
            }

            var starving = stillWaiting.Any(pair => pair.Value > 0 && after[pair.Key] < share);
            if (!starving || !AskYieldLocked(gate, after, aboveShare: share, effects))
            {
                break;
            }
        }
    }

    /// <summary>Asks one yieldable connection of the owner furthest above <paramref name="aboveShare"/> to yield.</summary>
    private static bool AskYieldLocked(Gate gate, Dictionary<Guid, int> counts, int aboveShare, Effects effects)
    {
        Guid? donor = null;
        foreach (var (owner, count) in counts)
        {
            if (count <= aboveShare) continue;
            if (!gate.Held.Any(slot => slot.OwnerId == owner && slot.Yieldable && !slot.YieldAsked)) continue;
            if (donor is null || count > counts[donor.Value]) donor = owner;
        }

        if (donor is null)
        {
            return false;
        }

        // The newest connection: the oldest are furthest into their ranges.
        var victim = gate.Held
            .Where(slot => slot.OwnerId == donor && slot.Yieldable && !slot.YieldAsked)
            .MaxBy(slot => slot.Sequence)!;
        victim.YieldAsked = true;
        effects.Yields.Add(victim);
        return true;
    }

    /// <summary>Queues a notice for each job that started or stopped waiting with nothing held on this host.</summary>
    private void TrackWaitingLocked(Gate gate)
    {
        var now = new HashSet<Guid>();
        foreach (var waiter in gate.Waiters)
        {
            if (!gate.Held.Any(slot => slot.OwnerId == waiter.OwnerId))
            {
                now.Add(waiter.OwnerId);
            }
        }

        foreach (var owner in now)
        {
            if (!gate.WaitingOwners.Contains(owner)) _notifications.Enqueue(owner);
        }

        foreach (var owner in gate.WaitingOwners)
        {
            if (!now.Contains(owner)) _notifications.Enqueue(owner);
        }

        gate.WaitingOwners = now;
    }

    private void ForgetIfIdleLocked(Gate gate)
    {
        if (gate.Held.Count == 0 && gate.Waiters.Count == 0 && gate.Parks.Count == 0)
        {
            _gates.Remove(gate.Host);
            gate.ParkTimer?.Dispose();
            gate.ParkTimer = null;
        }
    }

    /// <summary>In the order the changes happened: whoever drains the queue raises them all, one at a time.</summary>
    private void DispatchNotifications()
    {
        lock (_notifySync)
        {
            while (true)
            {
                Guid owner;
                lock (_sync)
                {
                    if (!_notifications.TryDequeue(out owner)) return;
                }

                try
                {
                    WaitingChanged?.Invoke(owner);
                }
                catch
                {
                    // An observer must not be able to break the limiter.
                }
            }
        }
    }

    private sealed class Gate(string host)
    {
        public string Host { get; } = host;
        public List<HostSlot> Held { get; } = new();
        public List<Waiter> Waiters { get; } = new();
        public HashSet<Guid> WaitingOwners { get; set; } = new();
        /// <summary>Connections given back and kept for their job a little while; they count against the limit.</summary>
        public List<Park> Parks { get; } = new();
        public Timer? ParkTimer { get; set; }
    }

    private readonly record struct Park(Guid OwnerId, long Deadline);

    private sealed class Waiter(Gate gate, Guid ownerId, bool yieldable, long sequence)
    {
        public Gate Gate { get; } = gate;
        public Guid OwnerId { get; } = ownerId;
        public bool Yieldable { get; } = yieldable;
        public long Sequence { get; } = sequence;
        public TaskCompletionSource<HostSlot> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>What a change decided, carried out after the lock: completing a task or cancelling a token can run other code.</summary>
    private sealed class Effects
    {
        public List<(Waiter Waiter, HostSlot Slot)> Grants { get; } = new();
        public List<HostSlot> Yields { get; } = new();

        public void Apply()
        {
            foreach (var (waiter, slot) in Grants)
            {
                if (!waiter.Completion.TrySetResult(slot))
                {
                    slot.Dispose();
                }
            }

            foreach (var slot in Yields)
            {
                slot.SignalYield();
            }
        }
    }
}
