using DownloadManager.Contracts;
using DownloadManager.Mobile;

namespace DownloadManager.Mobile.Tests;

/// <summary>A clock and timers the test moves by hand.</summary>
internal sealed class ManualTime : TimeProvider
{
    private DateTimeOffset _now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
    private readonly List<ManualTimer> _timers = new();

    public override DateTimeOffset GetUtcNow() => _now;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var t = new ManualTimer(this, callback, state);
        t.Change(dueTime, period);
        _timers.Add(t);
        return t;
    }

    public int Armed => _timers.Count(t => t.Due is not null);

    public void Advance(TimeSpan by)
    {
        _now += by;
        foreach (var t in _timers.ToArray())
            if (t.Due is { } d && d <= _now) { t.Due = null; t.Fire(); }
    }

    private sealed class ManualTimer(ManualTime owner, TimerCallback cb, object? state) : ITimer
    {
        public DateTimeOffset? Due { get; set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
            return true;
        }

        public void Fire() => cb(state);

        public void Dispose() { Due = null; }

        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}

public class ActivityStateTests
{
    private sealed class Counter { public int N; public void Hit() => N++; }

    private static (ActivityState S, ManualTime T, Counter C) Make(params JobDto[] jobs)
    {
        var t = new ManualTime();
        var s = new ActivityState(t);
        var c = new Counter();
        s.Changed += c.Hit;
        s.ApplySnapshot(jobs);
        return (s, t, c);
    }

    private static JobDto J(string state, string name = "f.bin", long? total = null, long received = 0, double? rate = null,
        string startMode = "startNow", DateTimeOffset? retryAt = null, int minute = 0)
    {
        var j = Jobs.Make(state, name, total, received, rate, minute: minute);
        j.StartMode = startMode;
        j.QueueRetryAt = retryAt;
        return j;
    }

    private static JobDto Clone(JobDto j, string state, long? received = null, double? rate = null) => new()
    {
        Id = j.Id, Url = j.Url, FileName = j.FileName, State = state, TotalBytes = j.TotalBytes,
        ReceivedBytes = received ?? j.ReceivedBytes, BytesPerSecond = rate ?? j.BytesPerSecond, CreatedAt = j.CreatedAt,
        UpdatedAt = j.UpdatedAt, StartMode = j.StartMode, FinalFilePath = j.FinalFilePath,
    };

    [Theory]
    [InlineData("starting", "startNow", true)]
    [InlineData("connecting", "startNow", true)]
    [InlineData("downloading", "startNow", true)]
    [InlineData("verifying", "startNow", true)]
    [InlineData("retrywaiting", "startNow", true)]
    [InlineData("RetryWaiting", "startNow", true)]
    [InlineData("queued", "startNow", true)]
    [InlineData("queued", "queue", true)]
    [InlineData("queued", "later", false)]
    [InlineData("paused", "startNow", false)]
    [InlineData("failed", "startNow", false)]
    [InlineData("completed", "startNow", false)]
    [InlineData("cancelled", "startNow", false)]
    public void KeepAlive_by_state(string state, string startMode, bool expected)
    {
        var (s, _, _) = Make(J(state, startMode: startMode));
        Assert.Equal(expected, s.Summary.KeepAlive);
    }

    [Theory]
    [InlineData("failed", true)]
    [InlineData("queued", true)]
    [InlineData("paused", false)]
    [InlineData("completed", false)]
    [InlineData("cancelled", false)]
    public void KeepAlive_with_a_queue_retry_time(string state, bool expected)
    {
        var (s, _, _) = Make(J(state, startMode: "later", retryAt: DateTimeOffset.UnixEpoch));
        Assert.Equal(expected, s.Summary.KeepAlive);
    }

    [Fact]
    public void Only_idle_jobs_means_no_keep_alive_and_counts_paused()
    {
        var (s, _, _) = Make(J("paused"), J("paused"), J("failed"), J("completed"), J("queued", startMode: "later"));
        Assert.False(s.Summary.KeepAlive);
        Assert.Equal(2, s.Summary.PausedCount);
    }

    private static readonly HostStatusDto Held = new() { WaitingForMeteredNetwork = true };

    private static QueueDto Q(int id, bool running) => new() { Id = id, IsRunning = running };

    [Fact]
    public void Metered_hold_does_not_keep_alive_and_counts_waiting()
    {
        var s = new ActivityState(new ManualTime());
        s.ApplySnapshot([J("queued"), J("queued"), J("queued", startMode: "later"), J("paused")], Held);
        Assert.False(s.Summary.KeepAlive);
        Assert.True(s.Summary.WaitingForNetwork);
        Assert.Equal(2, s.Summary.WaitingCount);

        s.SetNetworkWait(false);
        Assert.False(s.Summary.WaitingForNetwork);
        Assert.Equal(0, s.Summary.WaitingCount);
        Assert.True(s.Summary.KeepAlive);
    }

    [Fact]
    public void Network_wait_push_sets_and_lifts_the_hold()
    {
        var (s, _, _) = Make(J("queued"));
        Assert.True(s.Summary.KeepAlive);
        s.SetNetworkWait(true);
        Assert.Equal(1, s.Summary.WaitingCount);
        Assert.False(s.Summary.KeepAlive);
        s.SetNetworkWait(false);
        Assert.True(s.Summary.KeepAlive);
    }

    [Fact]
    public void A_hold_with_nothing_that_would_run_is_not_waiting()
    {
        var s = new ActivityState(new ManualTime());
        s.ApplySnapshot([J("paused"), J("failed"), J("completed"), J("queued", startMode: "later")], Held);
        Assert.False(s.Summary.WaitingForNetwork);
        Assert.Equal(0, s.Summary.WaitingCount);
        Assert.False(s.Summary.KeepAlive);
    }

    [Fact]
    public void A_queue_retry_is_held_too_but_running_jobs_keep_alive_through_the_hold()
    {
        var s = new ActivityState(new ManualTime());
        s.ApplySnapshot([J("failed", startMode: "later", retryAt: DateTimeOffset.UnixEpoch), J("queued")], Held);
        Assert.False(s.Summary.KeepAlive);
        Assert.Equal(2, s.Summary.WaitingCount);

        s.ApplySnapshot([J("failed", startMode: "later", retryAt: DateTimeOffset.UnixEpoch), J("downloading", rate: 1), J("retrywaiting")], Held);
        Assert.True(s.Summary.KeepAlive);
        Assert.Equal(1, s.Summary.WaitingCount);
        Assert.Equal(1, s.Summary.ActiveCount);
    }

    [Fact]
    public void A_job_kept_running_without_resume_keeps_alive_and_is_counted()
    {
        var stuck = J("downloading", total: 100, received: 10, rate: 1);
        stuck.RunningOnMeteredWithoutResume = true;
        var s = new ActivityState(new ManualTime());
        s.ApplySnapshot([stuck, J("queued")], Held);
        Assert.True(s.Summary.KeepAlive);
        Assert.Equal(1, s.Summary.NoResumeOnMeteredCount);
        Assert.Equal(1, s.Summary.WaitingCount);

        var done = Clone(stuck, "completed", received: 100);
        s.OnJobChanged(done, "updated");
        Assert.Equal(0, s.Summary.NoResumeOnMeteredCount);
        Assert.False(s.Summary.KeepAlive);
    }

    [Fact]
    public void The_no_resume_count_ignores_a_job_that_is_not_active()
    {
        var queued = J("queued");
        queued.RunningOnMeteredWithoutResume = true;
        var (s, _, _) = Make(queued, J("downloading", rate: 1));
        Assert.Equal(0, s.Summary.NoResumeOnMeteredCount);
    }

    [Fact]
    public void Queued_jobs_of_a_stopped_queue_do_not_keep_alive()
    {
        var inStopped = J("queued");
        inStopped.QueueId = 2;
        var s = new ActivityState(new ManualTime());
        s.ApplySnapshot([inStopped], null, [Q(0, true), Q(2, false)]);
        Assert.False(s.Summary.KeepAlive);

        s.SetQueues([Q(0, true), Q(2, true)]);
        Assert.True(s.Summary.KeepAlive);
        s.SetQueues([Q(0, true), Q(2, false)]);
        Assert.False(s.Summary.KeepAlive);
    }

    [Fact]
    public void A_job_outside_the_queues_runs_whatever_its_queue_id_says()
    {
        // Fable 9f02bcd #1: the Host ignores the queue of a job that is not queued, so a stopped main queue must not
        // make the keep-alive end while that job is about to start.
        var direct = J("queued");
        direct.QueueId = 0;
        direct.IsQueued = false;
        var s = new ActivityState(new ManualTime());
        s.ApplySnapshot([direct], null, [Q(0, false)]);
        Assert.True(s.Summary.KeepAlive);
    }

    [Fact]
    public void A_flagged_job_waiting_to_retry_keeps_the_no_resume_warning()
    {
        // Fable 9f02bcd #5: it retries on mobile data, so the warning (and its Stop) must stay.
        var j = J("retrywaiting");
        j.RunningOnMeteredWithoutResume = true;
        var s = new ActivityState(new ManualTime());
        s.ApplySnapshot([j]);
        Assert.Equal(1, s.Summary.NoResumeOnMeteredCount);
        Assert.True(s.Summary.KeepAlive);
    }

    [Fact]
    public void A_job_of_an_unknown_queue_counts_and_a_snapshot_without_queues_keeps_the_known_ones()
    {
        var job = J("queued");
        job.QueueId = 7;
        var s = new ActivityState(new ManualTime());
        s.ApplySnapshot([job], null, [Q(0, true)]);
        Assert.True(s.Summary.KeepAlive);

        s.SetQueues([Q(7, false)]);
        Assert.False(s.Summary.KeepAlive);
        s.ApplySnapshot([job]);
        Assert.False(s.Summary.KeepAlive);
    }

    [Fact]
    public void A_stopped_queue_holds_its_retries_but_not_running_or_paused_jobs()
    {
        var retry = J("failed", startMode: "later", retryAt: DateTimeOffset.UnixEpoch);
        retry.QueueId = 3;
        var running = J("downloading", rate: 1);
        running.QueueId = 3;
        var paused = J("paused");
        paused.QueueId = 3;
        var s = new ActivityState(new ManualTime());
        s.ApplySnapshot([retry, paused], null, [Q(3, false)]);
        Assert.False(s.Summary.KeepAlive);
        Assert.Equal(1, s.Summary.PausedCount);

        s.ApplySnapshot([retry, running], null, [Q(3, false)]);
        Assert.True(s.Summary.KeepAlive);
    }

    [Fact]
    public void Notification_figures_sum_the_active_jobs_and_name_the_oldest()
    {
        var a = J("downloading", "old.bin", total: 1000, received: 400, rate: 100, minute: 0);
        var b = J("downloading", "new.bin", total: 3000, received: 600, rate: 50, minute: 5);
        var (s, _, _) = Make(b, a, J("queued"));
        var m = s.Summary;
        Assert.Equal(2, m.ActiveCount);
        Assert.Equal(150, m.BytesPerSecond);
        Assert.Equal(1000, m.ActiveReceivedBytes);
        Assert.Equal(4000, m.ActiveTotalBytes);
        Assert.Equal("old.bin", m.First!.Name);
        Assert.Equal(TimeSpan.FromSeconds(6), m.First.TimeLeft);
    }

    [Fact]
    public void Unknown_size_gives_no_total()
    {
        var (s, _, _) = Make(J("downloading", total: 1000, received: 1, rate: 1), J("connecting"));
        Assert.Null(s.Summary.ActiveTotalBytes);
    }

    [Fact]
    public void Completion_is_reported_once_and_not_for_snapshot_jobs()
    {
        var done = J("completed", "old.bin", total: 5);
        var run = J("downloading", "new.bin", total: 640, received: 10, rate: 1);
        var (s, _, _) = Make(done, run);
        Assert.Empty(s.TakeFinished());

        var fin = Clone(run, "completed", received: 640);
        fin.FinalFilePath = "/dl/new.bin";
        s.OnJobChanged(fin, "updated");
        s.OnJobChanged(fin, "updated");
        s.OnJobChanged(done, "updated");
        var f = Assert.Single(s.TakeFinished());
        Assert.Equal((run.Id, "new.bin", "/dl/new.bin", 640L), (f.Id, f.FileName, f.FinalPath, f.SizeBytes));
        Assert.Empty(s.TakeFinished());
    }

    [Fact]
    public void A_job_added_already_completed_while_observed_is_reported()
    {
        var (s, _, _) = Make();
        s.OnJobChanged(J("completed", "x.bin", total: 9), "added");
        Assert.Single(s.TakeFinished());
    }

    [Fact]
    public void Completion_raises_Changed_at_once()
    {
        var run = J("downloading", total: 10, received: 5, rate: 1);
        var (s, _, c) = Make(run);
        var before = c.N;
        s.OnJobChanged(Clone(run, "completed", received: 10), "updated");
        Assert.Equal(before + 1, c.N);
        Assert.False(s.Summary.KeepAlive);
    }

    [Fact]
    public void Completion_with_other_jobs_still_running_is_immediate_too()
    {
        var a = J("downloading", total: 10, received: 5, rate: 1);
        var (s, _, c) = Make(a, J("downloading", total: 10, received: 5, rate: 1));
        var before = c.N;
        s.OnJobChanged(Clone(a, "completed", received: 10), "updated");
        Assert.Equal(before + 1, c.N);
        Assert.True(s.Summary.KeepAlive);
    }

    [Fact]
    public void Removed_jobs_drop_out_and_stay_out()
    {
        var run = J("downloading", total: 10, received: 5, rate: 1);
        var (s, _, _) = Make(run, J("paused"));
        s.OnJobChanged(run, "removed");
        Assert.Equal(0, s.Summary.ActiveCount);
        Assert.False(s.Summary.KeepAlive);
        // A late progress (or any) event for the removed id changes nothing.
        s.OnJobChanged(Clone(run, "downloading", received: 6), "progress");
        s.OnJobChanged(Clone(run, "downloading", received: 7), "updated");
        Assert.Equal(0, s.Summary.ActiveCount);
        Assert.False(s.Summary.KeepAlive);
    }

    [Fact]
    public void Progress_for_an_unknown_job_is_ignored_but_added_is_not()
    {
        var (s, _, _) = Make();
        s.OnJobChanged(J("downloading", rate: 1), "progress");
        Assert.False(s.Summary.KeepAlive);
        s.OnJobChanged(J("downloading", rate: 1), "added");
        Assert.True(s.Summary.KeepAlive);
    }

    [Fact]
    public void A_stale_progress_frame_does_not_undo_a_newer_state()
    {
        var run = J("downloading", total: 10, received: 5, rate: 1);
        run.UpdatedAt = DateTimeOffset.UnixEpoch.AddSeconds(100);
        var (s, _, _) = Make(run);
        var paused = Clone(run, "paused", received: 5);
        paused.UpdatedAt = run.UpdatedAt.AddSeconds(1);
        s.OnJobChanged(paused, "paused");
        var late = Clone(run, "downloading", received: 6);
        late.UpdatedAt = run.UpdatedAt.AddMilliseconds(500);
        s.OnJobChanged(late, "progress");
        Assert.Equal(0, s.Summary.ActiveCount);
        Assert.Equal(1, s.Summary.PausedCount);
    }

    [Fact]
    public void Events_before_the_snapshot_are_ignored()
    {
        var s = new ActivityState(new ManualTime());
        s.OnJobChanged(J("downloading"), "added");
        Assert.False(s.Summary.KeepAlive);
    }

    [Fact]
    public void Many_progress_events_in_a_second_give_one_Changed()
    {
        var run = J("downloading", total: 1000, received: 0, rate: 10);
        var (s, t, c) = Make(run);
        var before = c.N;
        for (var i = 1; i <= 50; i++) s.OnJobChanged(Clone(run, "downloading", received: i, rate: 10 + i), "progress");
        Assert.Equal(before, c.N);
        t.Advance(TimeSpan.FromMilliseconds(999));
        Assert.Equal(before, c.N);
        t.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(before + 1, c.N);
        Assert.Equal(0, t.Armed);

        // The next burst gives one more.
        s.OnJobChanged(Clone(run, "downloading", received: 99, rate: 1), "progress");
        s.OnJobChanged(Clone(run, "downloading", received: 100, rate: 2), "progress");
        t.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(before + 2, c.N);
    }

    [Fact]
    public void A_keep_alive_flip_is_immediate_and_absorbs_the_pending_one()
    {
        var run = J("downloading", total: 1000, received: 0, rate: 10);
        var (s, t, c) = Make(run);
        var before = c.N;
        s.OnJobChanged(Clone(run, "downloading", received: 5, rate: 11), "progress");
        Assert.Equal(before, c.N);
        s.OnJobChanged(Clone(run, "paused", received: 5), "paused");
        Assert.Equal(before + 1, c.N);
        Assert.False(s.Summary.KeepAlive);
        Assert.Equal(0, t.Armed);
        t.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(before + 1, c.N);

        s.OnJobChanged(Clone(run, "queued"), "queued");
        Assert.Equal(before + 2, c.N);
        Assert.True(s.Summary.KeepAlive);
    }

    [Fact]
    public void Nothing_changes_means_no_Changed_and_no_timer()
    {
        var run = J("downloading", total: 1000, received: 0, rate: 10);
        var (s, t, c) = Make(run);
        var after = c.N;
        // Identical events change nothing and arm nothing.
        s.OnJobChanged(run, "progress");
        s.OnJobChanged(run, "updated");
        Assert.Equal(0, t.Armed);
        t.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(after, c.N);

        var (idle, t2, c2) = Make(J("paused"), J("completed"));
        t2.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(0, t2.Armed);
        Assert.Equal(1, c2.N);
        Assert.False(idle.Summary.KeepAlive);
    }

    [Fact]
    public void Dispose_stops_everything()
    {
        var run = J("downloading", total: 1000, received: 0, rate: 10);
        var (s, t, c) = Make(run);
        s.OnJobChanged(Clone(run, "downloading", received: 5, rate: 11), "progress");
        var before = c.N;
        s.Dispose();
        t.Advance(TimeSpan.FromSeconds(1));
        s.OnJobChanged(Clone(run, "paused"), "paused");
        Assert.Equal(before, c.N);
    }
}
