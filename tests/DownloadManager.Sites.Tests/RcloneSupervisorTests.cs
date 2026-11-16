using DownloadManager.Sites;

namespace DownloadManager.Sites.Tests;

/// <summary>The supervisor's rules with a stand-in rclone and a hand-driven clock: no process, no waiting on real time.</summary>
public sealed class RcloneSupervisorTests
{
    private static readonly RcloneSupervisorOptions Options = new()
    {
        IdleDelay = TimeSpan.FromSeconds(60),
        StopGrace = TimeSpan.FromSeconds(1),
        FirstRestartDelay = TimeSpan.FromSeconds(1),
        MaxRestartDelay = TimeSpan.FromSeconds(8),
        StableRun = TimeSpan.FromMinutes(2),
    };

    [Fact]
    public async Task Nothing_starts_until_a_lease_is_taken()
    {
        var factory = new FakeFactory();
        await using var supervisor = new RcloneSupervisor(factory.StartAsync, Options, time: factory.Time);
        Assert.Equal(RcloneState.Off, supervisor.Status.State);
        Assert.Equal(0, factory.Started);
        Assert.Equal(0, factory.Time.ActiveTimers);
    }

    [Fact]
    public async Task Concurrent_leases_share_one_rclone()
    {
        var factory = new FakeFactory { StartDelay = TimeSpan.FromMilliseconds(50) };
        await using var supervisor = new RcloneSupervisor(factory.StartAsync, Options, time: factory.Time);
        var leases = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => supervisor.AcquireAsync()));
        Assert.Equal(1, factory.Started);
        Assert.Single(leases.Select(l => l.ProcessId).Distinct());
        Assert.Equal(8, supervisor.Status.Users);
        foreach (var lease in leases) lease.Dispose();
        await WaitFor(() => supervisor.Status.Users == 0);
    }

    [Fact]
    public async Task Last_lease_starts_the_idle_delay_and_rclone_stops_after_it()
    {
        var factory = new FakeFactory();
        await using var supervisor = new RcloneSupervisor(factory.StartAsync, Options, time: factory.Time);
        var lease = await supervisor.AcquireAsync();
        var instance = factory.Instances.Single();
        lease.Dispose();
        await WaitFor(() => supervisor.Status.Users == 0 && factory.Time.ActiveTimers == 1);

        factory.Time.Advance(TimeSpan.FromSeconds(59));
        Assert.False(instance.StopRequested);
        factory.Time.Advance(TimeSpan.FromSeconds(1));
        await WaitFor(() => supervisor.Status.State == RcloneState.Off);
        Assert.True(instance.StopRequested);
        Assert.True(instance.Disposed);
        Assert.Equal(0, factory.Time.ActiveTimers);
    }

    [Fact]
    public async Task Renew_stops_an_idle_rclone_at_once_and_a_busy_one_when_its_last_lease_goes()
    {
        var factory = new FakeFactory();
        await using var supervisor = new RcloneSupervisor(factory.StartAsync, Options, time: factory.Time);
        Assert.True(await supervisor.RenewWhenIdleAsync()); // none running

        (await supervisor.AcquireAsync()).Dispose();
        await WaitFor(() => supervisor.Status.Users == 0 && factory.Time.ActiveTimers == 1);
        Assert.True(await supervisor.RenewWhenIdleAsync());
        Assert.True(factory.Instances[0].StopRequested);
        Assert.Equal(0, factory.Time.ActiveTimers);

        var first = await supervisor.AcquireAsync();
        var second = await supervisor.AcquireAsync();
        Assert.Equal(2, factory.Started);
        Assert.False(await supervisor.RenewWhenIdleAsync());
        first.Dispose();
        await WaitFor(() => supervisor.Status.Users == 1);
        Assert.False(factory.Instances[1].StopRequested);
        second.Dispose();
        // No idle wait: the next lease starts an rclone that reads the new settings.
        await WaitFor(() => factory.Instances[1].StopRequested && supervisor.Status.State == RcloneState.Off);
        using var third = await supervisor.AcquireAsync();
        Assert.Equal(3, factory.Started);
    }

    [Fact]
    public async Task A_lease_taken_during_the_idle_delay_keeps_rclone_and_cancels_the_stop()
    {
        var factory = new FakeFactory();
        await using var supervisor = new RcloneSupervisor(factory.StartAsync, Options, time: factory.Time);
        (await supervisor.AcquireAsync()).Dispose();
        await WaitFor(() => factory.Time.ActiveTimers == 1);
        factory.Time.Advance(TimeSpan.FromSeconds(30));
        using var again = await supervisor.AcquireAsync();
        Assert.Equal(0, factory.Time.ActiveTimers);
        factory.Time.Advance(TimeSpan.FromMinutes(5));
        await Task.Delay(20);
        Assert.Equal(1, factory.Started);
        Assert.False(factory.Instances.Single().StopRequested);
        Assert.Equal(RcloneState.Running, supervisor.Status.State);
    }

    [Fact]
    public async Task Disposing_a_lease_twice_counts_once()
    {
        var factory = new FakeFactory();
        await using var supervisor = new RcloneSupervisor(factory.StartAsync, Options, time: factory.Time);
        var first = await supervisor.AcquireAsync();
        using var second = await supervisor.AcquireAsync();
        first.Dispose();
        first.Dispose();
        await WaitFor(() => supervisor.Status.Users == 1);
        await Task.Delay(20);
        Assert.Equal(1, supervisor.Status.Users);
    }

    [Fact]
    public async Task A_crash_waits_one_second_before_the_next_start_then_doubles()
    {
        var factory = new FakeFactory();
        await using var supervisor = new RcloneSupervisor(factory.StartAsync, Options, time: factory.Time);
        using (await supervisor.AcquireAsync()) { }
        factory.Instances[^1].Crash();
        await WaitFor(() => supervisor.Status.State == RcloneState.Waiting);
        Assert.Equal(factory.Time.GetUtcNow() + TimeSpan.FromSeconds(1), supervisor.Status.RetryAt);

        var next = supervisor.AcquireAsync();
        await Task.Delay(20);
        Assert.False(next.IsCompleted);
        Assert.Equal(1, factory.Started);
        factory.Time.Advance(TimeSpan.FromSeconds(1));
        using (await next.WaitAsync(TimeSpan.FromSeconds(5))) { }
        Assert.Equal(2, factory.Started);

        factory.Instances[^1].Crash();
        await WaitFor(() => supervisor.Status.State == RcloneState.Waiting);
        Assert.Equal(factory.Time.GetUtcNow() + TimeSpan.FromSeconds(2), supervisor.Status.RetryAt);
    }

    [Fact]
    public async Task Five_crashes_in_a_row_fail_until_reset()
    {
        var factory = new FakeFactory();
        await using var supervisor = new RcloneSupervisor(factory.StartAsync, Options, time: factory.Time);
        for (var i = 0; i < RcloneSupervisor.MaxRestarts; i++)
        {
            var take = supervisor.AcquireAsync();
            factory.Time.Advance(TimeSpan.FromSeconds(10));
            using (await take.WaitAsync(TimeSpan.FromSeconds(5))) { }
            factory.Instances[^1].Crash();
            var expected = i + 1 == RcloneSupervisor.MaxRestarts ? RcloneState.Failed : RcloneState.Waiting;
            await WaitFor(() => supervisor.Status.State == expected);
        }

        var error = await Assert.ThrowsAsync<RcloneUnavailableException>(() => supervisor.AcquireAsync());
        Assert.Contains("5 times in a row", error.Message);
        Assert.Equal(RcloneSupervisor.MaxRestarts, factory.Started);

        await supervisor.ResetAsync();
        Assert.Equal(RcloneState.Off, supervisor.Status.State);
        using (await supervisor.AcquireAsync()) { }
        Assert.Equal(RcloneSupervisor.MaxRestarts + 1, factory.Started);
    }

    [Fact]
    public async Task A_run_longer_than_two_minutes_starts_the_crash_count_again()
    {
        var factory = new FakeFactory();
        await using var supervisor = new RcloneSupervisor(factory.StartAsync, Options, time: factory.Time);
        for (var i = 0; i < 3; i++)
        {
            var take = supervisor.AcquireAsync();
            factory.Time.Advance(TimeSpan.FromSeconds(10));
            using (await take.WaitAsync(TimeSpan.FromSeconds(5))) { }
            factory.Instances[^1].Crash();
            await WaitFor(() => supervisor.Status.State == RcloneState.Waiting && factory.Instances[^1].Disposed);
        }

        var stable = supervisor.AcquireAsync();
        factory.Time.Advance(TimeSpan.FromSeconds(10));
        using (await stable.WaitAsync(TimeSpan.FromSeconds(5)))
        {
            factory.Time.Advance(TimeSpan.FromMinutes(3));
            factory.Instances[^1].Crash();
            await WaitFor(() => supervisor.Status.State == RcloneState.Waiting);
        }

        // Counted from one again: the first delay.
        Assert.Equal(factory.Time.GetUtcNow() + TimeSpan.FromSeconds(1), supervisor.Status.RetryAt);
    }

    [Fact]
    public async Task A_failed_start_counts_as_a_crash_and_reports_the_reason()
    {
        var factory = new FakeFactory { FailNext = 1 };
        await using var supervisor = new RcloneSupervisor(factory.StartAsync, Options, time: factory.Time);
        var error = await Assert.ThrowsAsync<RcloneUnavailableException>(() => supervisor.AcquireAsync());
        Assert.Equal("rclone could not be started: test", error.Message);
        Assert.Equal(RcloneState.Waiting, supervisor.Status.State);
        Assert.Equal("rclone could not be started: test", supervisor.Status.Error);
    }

    [Fact]
    public async Task Stop_ends_rclone_and_refuses_new_leases()
    {
        var factory = new FakeFactory();
        var supervisor = new RcloneSupervisor(factory.StartAsync, Options, time: factory.Time);
        var lease = await supervisor.AcquireAsync();
        await supervisor.StopAsync();
        Assert.True(factory.Instances.Single().StopRequested);
        Assert.Equal(RcloneState.Off, supervisor.Status.State);
        await Assert.ThrowsAsync<RcloneUnavailableException>(() => supervisor.AcquireAsync());
        lease.Dispose();
        await supervisor.DisposeAsync();
    }

    [Fact]
    public async Task Stop_does_not_wait_for_a_backoff_under_way()
    {
        var factory = new FakeFactory();
        var supervisor = new RcloneSupervisor(factory.StartAsync, Options, time: factory.Time);
        using (await supervisor.AcquireAsync()) { }
        factory.Instances[^1].Crash();
        await WaitFor(() => supervisor.Status.State == RcloneState.Waiting);
        var waiting = supervisor.AcquireAsync();
        await Task.Delay(20);
        Assert.False(waiting.IsCompleted);

        await supervisor.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<RcloneUnavailableException>(() => waiting);
        Assert.Equal(1, factory.Started);
    }

    [Fact]
    public async Task A_lease_on_a_crashed_rclone_is_not_counted_against_its_replacement()
    {
        var factory = new FakeFactory();
        await using var supervisor = new RcloneSupervisor(factory.StartAsync, Options, time: factory.Time);
        var old = await supervisor.AcquireAsync();
        factory.Instances[^1].Crash();
        await WaitFor(() => supervisor.Status.State == RcloneState.Waiting);
        var take = supervisor.AcquireAsync();
        factory.Time.Advance(TimeSpan.FromSeconds(1));
        using var fresh = await take.WaitAsync(TimeSpan.FromSeconds(5));
        old.Dispose();
        await Task.Delay(20);
        Assert.Equal(1, supervisor.Status.Users);
        Assert.Equal(0, factory.Time.ActiveTimers);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("The condition did not become true.");
            await Task.Delay(5);
        }
    }

    private sealed class FakeFactory
    {
        private int _nextPid = 1000;
        public ManualTime Time { get; } = new();
        public List<FakeInstance> Instances { get; } = [];
        public int Started;
        public int FailNext;
        public TimeSpan StartDelay;

        public async Task<IRcloneInstance> StartAsync(CancellationToken cancellationToken)
        {
            if (StartDelay > TimeSpan.Zero) await Task.Delay(StartDelay, cancellationToken);
            Interlocked.Increment(ref Started);
            if (FailNext > 0)
            {
                FailNext--;
                throw new RcloneUnavailableException("rclone could not be started: test");
            }

            var instance = new FakeInstance(Interlocked.Increment(ref _nextPid));
            lock (Instances) Instances.Add(instance);
            return instance;
        }
    }

    private sealed class FakeInstance(int pid) : IRcloneInstance
    {
        private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public RcloneClient Client { get; } = new(new Uri("http://127.0.0.1:9/"), "u", "pass-for-tests");
        public int ProcessId { get; } = pid;
        public Task Exited => _exited.Task;
        public bool StopRequested { get; private set; }
        public bool Disposed { get; private set; }

        public void Crash() => _exited.TrySetResult();

        public Task<bool> StopAsync(TimeSpan grace)
        {
            StopRequested = true;
            _exited.TrySetResult();
            return Task.FromResult(true);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            Client.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>A clock that moves only when told to; its timers fire inside <see cref="Advance"/>.</summary>
internal sealed class ManualTime : TimeProvider
{
    private readonly object _lock = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    public int ActiveTimers
    {
        get { lock (_lock) return _timers.Count(t => t.Due is not null); }
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_lock) return _now;
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (_lock) _timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        DateTimeOffset target;
        lock (_lock) target = _now + by;
        while (true)
        {
            ManualTimer? next;
            lock (_lock)
            {
                next = _timers.Where(t => t.Due is { } due && due <= target).OrderBy(t => t.Due).FirstOrDefault();
                if (next is null)
                {
                    _now = target;
                    return;
                }

                _now = next.Due!.Value;
                next.Due = null;
            }

            next.Fire();
        }
    }

    private sealed class ManualTimer(ManualTime owner, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset? Due { get; set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._lock) Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
            return true;
        }

        public void Fire() => callback(state);

        public void Dispose()
        {
            lock (owner._lock)
            {
                Due = null;
                owner._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
