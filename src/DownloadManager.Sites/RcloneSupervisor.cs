namespace DownloadManager.Sites;

public enum RcloneState
{
    /// <summary>No rclone runs, and nothing is wrong.</summary>
    Off,
    Starting,
    Running,
    /// <summary>Asked to quit (idle, or the Host stopping).</summary>
    Stopping,
    /// <summary>rclone ended by itself; the next use starts it again after <see cref="RcloneStatus.RetryAt"/>.</summary>
    Waiting,
    /// <summary>It ended by itself <see cref="RcloneSupervisor.MaxRestarts"/> times in a row; only <see cref="RcloneSupervisor.Reset"/> clears this.</summary>
    Failed,
}

public sealed record RcloneStatus(RcloneState State, int Users, int? ProcessId, string? Error, DateTimeOffset? RetryAt);

public sealed class RcloneSupervisorOptions
{
    /// <summary>How long rclone stays up after the last user let go (a quick next browse reuses it).</summary>
    public TimeSpan IdleDelay { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How long rclone gets to quit by itself before it is killed.</summary>
    public TimeSpan StopGrace { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>The wait before the first start after a crash; it doubles each time up to <see cref="MaxRestartDelay"/>.</summary>
    public TimeSpan FirstRestartDelay { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxRestartDelay { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>A run this long starts the crash count again.</summary>
    public TimeSpan StableRun { get; init; } = TimeSpan.FromMinutes(2);
}

/// <summary>
/// rclone's life in the Host (remote-sites.md, "Lifetime"; SITES.1):
/// <list type="bullet">
/// <item>Nothing runs until something needs it: <see cref="AcquireAsync"/> starts rclone, or waits for the start already
/// under way, and returns a lease. Browsing, a running or waiting transfer, and the scheduler each hold one.</item>
/// <item>When the last lease is let go, rclone stops after <see cref="RcloneSupervisorOptions.IdleDelay"/> unless a new
/// lease comes first. No timer runs while nobody holds a lease and rclone is off.</item>
/// <item>rclone that ends by itself is not started at once: the next <see cref="AcquireAsync"/> waits 1, 2, 4 ... seconds
/// (at most a minute) after the crash, and after <see cref="MaxRestarts"/> crashes in a row (a run of two minutes starts
/// the count again) it fails with the last reason until <see cref="Reset"/> (a repair, a changed rclone path). Leases on
/// the dead rclone fail their calls with <see cref="RcloneUnavailableException"/>; their owners take a new lease.</item>
/// <item><see cref="StopAsync"/> on the Host's exit: <c>core/quit</c>, a short wait, then kill; nothing starts after.</item>
/// </list>
/// Every change of state is under one gate, so two callers can't start two rclones. <see cref="Changed"/> is raised
/// after the gate is let go; the passing states (starting, stopping) show in <see cref="Status"/> only.
/// </summary>
public sealed class RcloneSupervisor : IAsyncDisposable
{
    public const int MaxRestarts = 5;

    private readonly Func<CancellationToken, Task<IRcloneInstance>> _start;
    private readonly RcloneSupervisorOptions _options;
    private readonly Action<string>? _log;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IRcloneInstance? _instance;
    private DateTimeOffset _startedAt;
    private RcloneState _state = RcloneState.Off;
    private int _users;
    private int _crashes;
    private string? _error;
    private DateTimeOffset? _retryAt;
    private ITimer? _idleTimer;
    private long _idleGeneration;
    private bool _stopped;
    private bool _renewWhenIdle;
    private readonly CancellationTokenSource _stopping = new();
    private volatile RcloneStatus _snapshot = new(RcloneState.Off, 0, null, null, null);

    public RcloneSupervisor(Func<CancellationToken, Task<IRcloneInstance>> start, RcloneSupervisorOptions? options = null,
        Action<string>? log = null, TimeProvider? time = null)
    {
        _start = start;
        _options = options ?? new RcloneSupervisorOptions();
        _log = log;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The Host's supervisor for a real rclone at <paramref name="executable"/>, with its state in <paramref name="stateDirectory"/>.</summary>
    public static RcloneSupervisor ForExecutable(string executable, string stateDirectory, RcloneSupervisorOptions? options = null,
        Action<string>? log = null) =>
        new(async ct => await RcloneProcess.StartAsync(executable, stateDirectory, log, null, ct).ConfigureAwait(false), options, log);

    /// <summary>Raised after every change of state, outside the gate.</summary>
    public event EventHandler<RcloneStatus>? Changed;

    /// <summary>The state as of the last change; never waits (a start can hold the gate for seconds).</summary>
    public RcloneStatus Status => _snapshot;

    /// <summary>
    /// A lease on a running rclone, started if needed. Throws <see cref="RcloneUnavailableException"/> when it can't start,
    /// is failed, or the supervisor is stopped. Dispose the lease when done; the last one starts the idle delay.
    /// </summary>
    public async Task<RcloneLease> AcquireAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await AcquireCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new RcloneUnavailableException("Remote files are stopping.");
        }
    }

    private async Task<RcloneLease> AcquireCoreAsync(CancellationToken cancellationToken)
    {
        RcloneStatus? changed = null;
        // A Host that is stopping cancels a start or a backoff wait under way, so its exit never waits on them.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);
        var token = linked.Token;
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_stopped) throw new RcloneUnavailableException("Remote files are stopping.");
            if (_instance is not null && _instance.Exited.IsCompleted) NoteExitLocked(_instance);
            if (_state == RcloneState.Failed) throw new RcloneUnavailableException(_error ?? "rclone keeps failing.");

            if (_instance is null)
            {
                if (_retryAt is { } at)
                {
                    var wait = at - _time.GetUtcNow();
                    if (wait > TimeSpan.Zero) await Task.Delay(wait, _time, token).ConfigureAwait(false);
                }

                _state = RcloneState.Starting;
                Snap();
                IRcloneInstance instance;
                try
                {
                    instance = await _start(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    _state = RcloneState.Off;
                    changed = Snap();
                    throw;
                }
                catch (Exception e)
                {
                    // A start that fails counts as a crash: same backoff, same limit.
                    CountCrashLocked(e.Message);
                    changed = Snap();
                    throw e as RcloneUnavailableException ?? new RcloneUnavailableException(e.Message, e);
                }

                if (_stopping.IsCancellationRequested)
                {
                    // StopAsync came while this start ran: no lease on an rclone that is about to be stopped.
                    await instance.StopAsync(_options.StopGrace).ConfigureAwait(false);
                    await DisposeQuietlyAsync(instance).ConfigureAwait(false);
                    throw new RcloneUnavailableException("Remote files are stopping.");
                }

                _instance = instance;
                _startedAt = _time.GetUtcNow();
                _retryAt = null;
                _error = null;
                _ = WatchAsync(instance);
            }

            CancelIdleLocked();
            _users++;
            _state = RcloneState.Running;
            changed = Snap();
            return new RcloneLease(this, _instance);
        }
        finally
        {
            _gate.Release();
            if (changed is not null) Raise(changed);
        }
    }

    /// <summary>Clears <see cref="RcloneState.Failed"/> and the crash count (repair, a new rclone path).</summary>
    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        RcloneStatus changed;
        // Not a blocking wait: a start or a backoff can hold the gate for many seconds, and repair runs from a window.
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _crashes = 0;
            _retryAt = null;
            _error = null;
            if (_state is RcloneState.Failed or RcloneState.Waiting) _state = RcloneState.Off;
            changed = Snap();
        }
        finally { _gate.Release(); }
        Raise(changed);
    }

    /// <summary>
    /// SITES.6b: rclone reads some settings (the trusted-certificates file) only when it starts. The running rclone is
    /// stopped now if no lease holds it, or as soon as the last lease is let go; the next lease starts a new one. True when
    /// none runs any more (stopped now, or none was running); false when it waits for its leases.
    /// </summary>
    public async Task<bool> RenewWhenIdleAsync()
    {
        RcloneStatus? changed = null;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_instance is null || _stopped)
            {
                _renewWhenIdle = false;
                return true;
            }

            if (_users > 0)
            {
                _renewWhenIdle = true;
                return false;
            }

            CancelIdleLocked();
            await StopInstanceLocked("settings changed").ConfigureAwait(false);
            changed = Snap();
            return true;
        }
        finally
        {
            _gate.Release();
            if (changed is not null) Raise(changed);
        }
    }

    /// <summary>On the Host's exit: rclone stops and nothing starts it again.</summary>
    public async Task StopAsync()
    {
        RcloneStatus changed;
        _stopping.Cancel();
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _stopped = true;
            CancelIdleLocked();
            await StopInstanceLocked("the Host is stopping").ConfigureAwait(false);
            changed = Snap();
        }
        finally { _gate.Release(); }
        Raise(changed);
    }

    public async ValueTask DisposeAsync()
    {
        // _stopping is not disposed: a second StopAsync (Host exit after a dispose) must still be able to cancel it.
        await StopAsync().ConfigureAwait(false);
    }

    /// <summary>A lease let go. Not waited for: a start under way holds the gate, and a lease is often let go on a UI thread.</summary>
    internal void Release(IRcloneInstance instance) => _ = ReleaseAsync(instance);

    private async Task ReleaseAsync(IRcloneInstance instance)
    {
        RcloneStatus? changed = null;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            // A lease on an rclone that has since been replaced was already dropped from the count.
            if (!ReferenceEquals(instance, _instance) || _users == 0) return;
            _users--;
            if (_users == 0 && !_stopped)
            {
                if (_renewWhenIdle)
                {
                    // SITES.6b: a setting rclone reads only at start changed while it was busy; the next lease starts a new one.
                    CancelIdleLocked();
                    await StopInstanceLocked("settings changed").ConfigureAwait(false);
                }
                else ScheduleIdleLocked();
            }

            changed = Snap();
        }
        finally { _gate.Release(); }
        Raise(changed);
    }

    private void ScheduleIdleLocked()
    {
        CancelIdleLocked();
        var generation = ++_idleGeneration;
        _idleTimer = _time.CreateTimer(_ => _ = IdleStopAsync(generation), null, _options.IdleDelay, Timeout.InfiniteTimeSpan);
    }

    private void CancelIdleLocked()
    {
        _idleGeneration++;
        _idleTimer?.Dispose();
        _idleTimer = null;
    }

    private async Task IdleStopAsync(long generation)
    {
        RcloneStatus? changed = null;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            // A lease taken, or another idle timer set, since this one was: not ours to act on.
            if (generation != _idleGeneration || _users > 0 || _instance is null) return;
            _idleTimer?.Dispose();
            _idleTimer = null;
            await StopInstanceLocked("idle").ConfigureAwait(false);
            changed = Snap();
        }
        catch (Exception e)
        {
            _log?.Invoke($"rclone idle stop failed: {e.Message}");
        }
        finally { _gate.Release(); }
        if (changed is not null) Raise(changed);
    }

    private async Task StopInstanceLocked(string why)
    {
        var instance = _instance;
        if (instance is null)
        {
            if (_state is not RcloneState.Failed) _state = RcloneState.Off;
            return;
        }

        _state = RcloneState.Stopping;
        Snap();
        _instance = null;
        _users = 0;
        _renewWhenIdle = false;
        try
        {
            var quit = await instance.StopAsync(_options.StopGrace).ConfigureAwait(false);
            _log?.Invoke(quit ? $"rclone stopped ({why})." : $"rclone did not quit in time and was ended ({why}).");
        }
        catch (Exception e)
        {
            _log?.Invoke($"rclone stop ({why}) failed: {e.Message}");
        }

        try { await instance.DisposeAsync().ConfigureAwait(false); }
        catch (Exception) { }
        _state = RcloneState.Off;
    }

    private async Task WatchAsync(IRcloneInstance instance)
    {
        try { await instance.Exited.ConfigureAwait(false); }
        catch (Exception) { }
        RcloneStatus? changed = null;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (ReferenceEquals(instance, _instance))
            {
                NoteExitLocked(instance);
                changed = Snap();
            }
        }
        finally { _gate.Release(); }
        if (changed is not null) Raise(changed);
    }

    /// <summary>The current rclone ended without being asked to.</summary>
    private void NoteExitLocked(IRcloneInstance instance)
    {
        if (!ReferenceEquals(instance, _instance)) return;
        _instance = null;
        _users = 0;
        _renewWhenIdle = false;
        CancelIdleLocked();
        if (_time.GetUtcNow() - _startedAt >= _options.StableRun) _crashes = 0;
        CountCrashLocked("rclone stopped unexpectedly.");
        _ = DisposeQuietlyAsync(instance);
    }

    private void CountCrashLocked(string reason)
    {
        _crashes++;
        _error = reason;
        if (_crashes >= MaxRestarts)
        {
            _state = RcloneState.Failed;
            _retryAt = null;
            _error = $"{reason} rclone failed {_crashes} times in a row; Remote files stay unavailable until they are repaired.";
            _log?.Invoke(_error);
            return;
        }

        var delay = TimeSpan.FromTicks(Math.Min(_options.MaxRestartDelay.Ticks, _options.FirstRestartDelay.Ticks << (_crashes - 1)));
        _retryAt = _time.GetUtcNow() + delay;
        _state = RcloneState.Waiting;
        _log?.Invoke($"{reason} The next use starts it again after {delay.TotalSeconds:0.#} s.");
    }

    private static async Task DisposeQuietlyAsync(IRcloneInstance instance)
    {
        try { await instance.DisposeAsync().ConfigureAwait(false); }
        catch (Exception) { }
    }

    private RcloneStatus StatusLocked() => new(_state, _users, _instance?.ProcessId, _error, _retryAt);

    /// <summary>Publishes the state for <see cref="Status"/>; under the gate. <see cref="Changed"/> is raised by the caller after the gate.</summary>
    private RcloneStatus Snap() => _snapshot = StatusLocked();

    private void Raise(RcloneStatus status)
    {
        try { Changed?.Invoke(this, status); }
        catch (Exception) { }
    }
}

/// <summary>A use of rclone. Calls go through <see cref="Client"/>; dispose when done.</summary>
public sealed class RcloneLease : IDisposable
{
    private readonly RcloneSupervisor _owner;
    private readonly IRcloneInstance _instance;
    private int _released;

    internal RcloneLease(RcloneSupervisor owner, IRcloneInstance instance)
    {
        _owner = owner;
        _instance = instance;
    }

    public RcloneClient Client => _instance.Client;

    public int ProcessId => _instance.ProcessId;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0) _owner.Release(_instance);
    }
}
