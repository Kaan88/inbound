using DownloadManager.Contracts;
using DownloadManager.Mobile;

namespace DownloadManager.Mobile.Tests;

public sealed class QueueWakePlanTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void No_pending_work_or_scheduler_timer_has_no_wake()
    {
        var plan = QueueWakePlan.Next(new SnapshotDto(), schedulerWake: null, Now,
            retryNotBefore: Now, unattended: true, guardTripped: false);

        Assert.Null(plan);
    }

    [Fact]
    public void A_tripped_crash_guard_cancels_pending_and_scheduled_wakes()
    {
        var queue = Queue(1, charging: true, unmetered: true);
        var snapshot = Snapshot([queue], [Job(1)]);
        var plan = QueueWakePlan.Next(snapshot, Now.AddHours(1), Now, Now, unattended: true, guardTripped: true);

        Assert.Null(plan);
    }

    [Fact]
    public void Foreground_pending_work_does_not_wake_immediately_but_keeps_a_future_timer()
    {
        var queue = Queue(1, charging: true, unmetered: true);
        var snapshot = Snapshot([queue], [Job(1)]);

        Assert.Null(QueueWakePlan.Next(snapshot, schedulerWake: null, Now, Now, unattended: false, guardTripped: false));

        var timer = Now.AddHours(2);
        var plan = QueueWakePlan.Next(snapshot, timer, Now, Now, unattended: false, guardTripped: false);
        Assert.Equal(timer, plan!.At);
        Assert.False(plan.Charging);
        Assert.False(plan.Unmetered);
    }

    [Fact]
    public void Stopped_queues_and_later_jobs_do_not_create_pending_work()
    {
        var stopped = Queue(1, isRunning: false, charging: true, unmetered: true);
        var running = Queue(2, charging: true, unmetered: true);
        var snapshot = Snapshot([stopped, running], [Job(1), Job(2, startMode: StartModes.Later)]);

        Assert.Null(QueueWakePlan.Next(snapshot, schedulerWake: null, Now, Now, unattended: true, guardTripped: false));
    }

    [Fact]
    public void Detached_jobs_do_not_inherit_their_former_queue_conditions()
    {
        var queue = Queue(1, charging: true, unmetered: true);
        var detached = Job(1, isQueued: false);
        var plan = QueueWakePlan.Next(Snapshot([queue], [detached]), schedulerWake: null, Now, Now,
            unattended: true, guardTripped: false);

        Assert.Equal(Now, plan!.At);
        Assert.False(plan.Charging);
        Assert.False(plan.Unmetered);
    }

    [Fact]
    public void Pending_work_uses_shared_conditions_and_relaxes_them_to_the_least_restrictive_queue()
    {
        var bothRequireConditions = Snapshot(
            [Queue(1, charging: true, unmetered: true), Queue(2, charging: true, unmetered: true)],
            [Job(1), Job(2)]);
        var strict = QueueWakePlan.Next(bothRequireConditions, schedulerWake: null, Now, Now,
            unattended: true, guardTripped: false)!;
        Assert.Equal(Now, strict.At);
        Assert.True(strict.Charging);
        Assert.True(strict.Unmetered);

        var mixedRequirements = Snapshot(
            [Queue(1, charging: true, unmetered: true), Queue(2, charging: false, unmetered: false)],
            [Job(1), Job(2)]);
        var relaxed = QueueWakePlan.Next(mixedRequirements, schedulerWake: null, Now, Now,
            unattended: true, guardTripped: false)!;
        Assert.Equal(Now, relaxed.At);
        Assert.False(relaxed.Charging);
        Assert.False(relaxed.Unmetered);
    }

    [Fact]
    public void Pending_retry_uses_backoff_but_an_earlier_schedule_timer_is_unconstrained()
    {
        var queue = Queue(1, charging: true, unmetered: true);
        var snapshot = Snapshot([queue], [Job(1)]);
        var retry = Now.AddHours(2);
        var pendingPlan = QueueWakePlan.Next(snapshot, schedulerWake: null, Now, retry,
            unattended: true, guardTripped: false)!;
        Assert.Equal(retry, pendingPlan.At);
        Assert.True(pendingPlan.Charging);
        Assert.True(pendingPlan.Unmetered);

        var earlierTimer = Now.AddHours(1);
        var timerPlan = QueueWakePlan.Next(snapshot, earlierTimer, Now, retry,
            unattended: true, guardTripped: false)!;
        Assert.Equal(earlierTimer, timerPlan.At);
        Assert.False(timerPlan.Charging);
        Assert.False(timerPlan.Unmetered);
    }

    [Fact]
    public void Global_unmetered_rule_applies_even_to_a_future_scheduler_wake()
    {
        var snapshot = new SnapshotDto { Settings = new SettingsDto { DownloadOnlyOnUnmeteredNetwork = true } };
        var timer = Now.AddHours(1);
        var plan = QueueWakePlan.Next(snapshot, timer, Now, Now, unattended: false, guardTripped: false)!;

        Assert.Equal(timer, plan.At);
        Assert.False(plan.Charging);
        Assert.True(plan.Unmetered);
    }

    [Fact]
    public void A_past_scheduler_wake_is_clamped_to_now()
    {
        var plan = QueueWakePlan.Next(new SnapshotDto(), Now.AddMinutes(-5), Now, Now,
            unattended: false, guardTripped: false);

        Assert.Equal(Now, plan!.At);
        Assert.False(plan.Charging);
        Assert.False(plan.Unmetered);
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(0, 0)]
    [InlineData(5, 0)]
    [InlineData(120, 60)]
    public void Another_queue_timer_bounds_a_constrained_pending_wake(int timerMinutes, int retryMinutes)
    {
        var snapshot = Snapshot([Queue(1, charging: true, unmetered: true), Queue(2, isRunning: false)], [Job(1), Job(2)]);
        var timer = Now.AddMinutes(timerMinutes);
        var plan = QueueWakePlan.Next(snapshot, timer, Now, Now.AddMinutes(retryMinutes), true, false)!;
        Assert.Equal(timer < Now ? Now : timer, plan.OverrideConstraintsAt);
        Assert.True(plan.At <= plan.OverrideConstraintsAt);
        if (timerMinutes > retryMinutes) { Assert.True(plan.Charging); Assert.True(plan.Unmetered); }
    }

    [Fact]
    public void Constrained_work_without_a_timer_never_bypasses_its_os_conditions()
    {
        var plan = QueueWakePlan.Next(Snapshot([Queue(1, charging: true)], [Job(1)]), null, Now, Now, true, false)!;
        Assert.True(plan.Charging);
        Assert.Null(plan.OverrideConstraintsAt);
    }

    private static SnapshotDto Snapshot(IReadOnlyList<QueueDto> queues, IReadOnlyList<JobDto> jobs) =>
        new() { Settings = new SettingsDto { Queues = queues.ToList() }, Jobs = jobs.ToList() };

    private static QueueDto Queue(int id, bool isRunning = true, bool charging = false, bool unmetered = false) => new()
    {
        Id = id,
        IsRunning = isRunning,
        RequireCharging = charging,
        RequireUnmeteredNetwork = unmetered,
    };

    private static JobDto Job(int queueId, bool isQueued = true, string startMode = "startNow") => new()
    {
        State = "queued",
        QueueId = queueId,
        IsQueued = isQueued,
        StartMode = startMode,
    };
}
