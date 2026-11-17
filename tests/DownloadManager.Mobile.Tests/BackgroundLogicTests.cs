using DownloadManager.Mobile;

namespace DownloadManager.Mobile.Tests;

public class BackgroundLogicTests
{
    private static ActivitySummary Sum(int active, double rate = 0, ActiveFile? first = null, long received = 0, long? total = null) =>
        new(true, active, rate, first, received, total, false, 0, 0, 0);

    [Fact]
    public void Ongoing_two_files_with_known_total_is_determinate_on_the_combined_bytes()
    {
        var first = new ActiveFile(Guid.NewGuid(), "ubuntu.iso", 100, 400, TimeSpan.FromMinutes(4));
        var t = NotificationText.Ongoing(Sum(2, 16_100_000, first, 300, 1000));
        Assert.Equal("2 downloads · 16.1 MB/s", t.Title);
        Assert.Equal("ubuntu.iso · 4 min left", t.Body);
        Assert.False(t.Indeterminate);
        Assert.Equal(300, t.Progress);
        Assert.Equal(NotificationText.BarMax, t.Max);
    }

    [Fact]
    public void Ongoing_one_file_uses_its_own_bytes()
    {
        var first = new ActiveFile(Guid.NewGuid(), "a.zip", 250, 1000, null);
        var t = NotificationText.Ongoing(Sum(1, 2_000_000, first, 250, 1000));
        Assert.Equal("1 download · 2.0 MB/s", t.Title);
        Assert.Equal("a.zip", t.Body);
        Assert.Equal(250, t.Progress);
    }

    [Fact]
    public void Ongoing_is_indeterminate_when_a_size_is_unknown()
    {
        var first = new ActiveFile(Guid.NewGuid(), "a.zip", 10, null, null);
        Assert.True(NotificationText.Ongoing(Sum(1, 0, first, 10, null)).Indeterminate);
        Assert.True(NotificationText.Ongoing(Sum(3, 0, first, 10, null)).Indeterminate);
    }

    [Fact]
    public void Ongoing_without_rate_leaves_the_rate_out_and_nothing_active_says_starting()
    {
        var first = new ActiveFile(Guid.NewGuid(), "a", 0, 10, null);
        Assert.Equal("2 downloads", NotificationText.Ongoing(Sum(2, 0, first, 0, 20)).Title);
        var starting = NotificationText.Ongoing(Sum(0));
        Assert.Equal("Starting downloads", starting.Title);
        Assert.Null(starting.Body);
        Assert.True(starting.Indeterminate);
    }

    [Fact]
    public void Ongoing_progress_is_clamped()
    {
        var first = new ActiveFile(Guid.NewGuid(), "a", 20, 10, null);
        Assert.Equal(NotificationText.BarMax, NotificationText.Ongoing(Sum(1, 0, first, 20, 10)).Progress);
    }

    [Theory]
    [InlineData(1, "1 download paused")]
    [InlineData(3, "3 downloads paused")]
    public void Paused_text(int n, string expected) => Assert.Equal(expected, NotificationText.Paused(n));

    [Fact]
    public void Finished_body_names_the_size()
    {
        var f = new FinishedJob(Guid.NewGuid(), "field-recordings-vol3.zip", "/x/y.zip", 640_000_000);
        Assert.Equal("Finished · 640 MB · Tap to open", NotificationText.FinishedBody(f));
    }

    [Fact]
    public void Estimate_is_what_is_left_or_a_guess()
    {
        Assert.Equal(1_000_000, SessionBytes.Estimate(Sum(1, 0, null, 300, 1000)));
        Assert.Equal(1_000_000, SessionBytes.Estimate(Sum(1, 0, null, 1000, 1000)));
        Assert.Equal(SessionBytes.UnknownEstimate, SessionBytes.Estimate(Sum(1, 0, null, 5, null)));
        Assert.Equal(5_000_000_000 - 1_000_000_000, SessionBytes.Estimate(Sum(2, 0, null, 1_000_000_000, 5_000_000_000)));
    }

    [Theory]
    [InlineData(1_000_000_000L, 1_100_000_000L, false)]
    [InlineData(1_000_000_000L, 1_300_000_000L, true)]
    [InlineData(100_000_000L, 140_000_000L, false)]
    [InlineData(100_000_000L, 160_000_000L, true)]
    public void Notable_change(long last, long next, bool expected) => Assert.Equal(expected, SessionBytes.NotableChange(last, next));

    [Fact]
    public void Meter_throttles_by_bytes_and_time_and_counts_through_a_drop()
    {
        var m = new TransferMeter(1000, TimeSpan.FromSeconds(5));
        var t0 = new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
        Assert.Null(m.Observe(5000, t0));                          // the first observation is the basis, earlier bytes are not counted
        Assert.Equal(4000, m.Observe(9000, t0.AddSeconds(1)));
        Assert.Null(m.Observe(9500, t0.AddSeconds(2)));            // too soon
        Assert.Null(m.Observe(9600, t0.AddSeconds(7)));            // too little
        Assert.Null(m.Observe(2000, t0.AddSeconds(8)));            // a download left: rebase, nothing added
        Assert.Equal(6100, m.Observe(3500, t0.AddSeconds(14)));
    }

    [Fact]
    public void Meter_reset_starts_over()
    {
        var m = new TransferMeter(1, TimeSpan.Zero);
        var t = DateTimeOffset.UnixEpoch;
        m.Observe(100, t);
        Assert.Equal(40, m.Observe(140, t.AddSeconds(1)));
        m.Reset();
        Assert.Equal(0, m.Total);
        Assert.Null(m.Observe(50, t.AddSeconds(2)));
        Assert.Equal(30, m.Observe(80, t.AddSeconds(3)));
    }

    private static readonly byte[] RunA = [1, 2, 3, 4];
    private static readonly byte[] RunB = [9, 9, 9, 9];

    [Fact]
    public void UserStop_true_when_the_previous_runs_record_says_user_requested()
    {
        var d = UserStopDetector.Decide(RunA, [new ExitRecord(RunB, 10, 500), new ExitRecord(RunA, 10, 400)]);
        Assert.True(d.StoppedByUser);
    }

    [Theory]
    [InlineData(1_000_000L - 5_000, false)]   // killed by the update shortly before its recorded time
    [InlineData(1_000_000L + 3_000, false)]   // just after (clock order)
    [InlineData(1_000_000L - 600_000, true)]  // a stop long before the update is still the user's
    [InlineData(1_000_000L + 60_000, true)]   // and one long after it
    public void UserStop_an_exit_at_the_app_update_is_not_a_user_stop(long exitAt, bool userStop)
    {
        // API 30 to 32 report an app update as reason 10, like Task Manager Stop (review of step 2).
        var d = UserStopDetector.DecideAny([RunA], [new ExitRecord(RunA, 10, exitAt)], packageUpdatedAtMs: 1_000_000);
        Assert.Equal(userStop, d.StoppedByUser);
    }

    [Fact]
    public void UserStop_ignores_a_user_stop_of_another_run()
    {
        var d = UserStopDetector.Decide(RunA, [new ExitRecord(RunB, 10, 500), new ExitRecord(RunA, 4, 400)]);
        Assert.False(d.StoppedByUser);
        Assert.Contains("reason 4", d.Why);
    }

    [Fact]
    public void UserStop_takes_the_newest_record_of_the_run()
    {
        Assert.False(UserStopDetector.Decide(RunA, [new ExitRecord(RunA, 10, 100), new ExitRecord(RunA, 5, 200)]).StoppedByUser);
        Assert.True(UserStopDetector.Decide(RunA, [new ExitRecord(RunA, 5, 100), new ExitRecord(RunA, 10, 200)]).StoppedByUser);
    }

    [Fact]
    public void UserStop_false_without_a_record_a_summary_or_a_previous_id()
    {
        Assert.False(UserStopDetector.Decide(RunA, []).StoppedByUser);
        Assert.False(UserStopDetector.Decide(RunA, [new ExitRecord(null, 10, 1)]).StoppedByUser);
        Assert.False(UserStopDetector.Decide(null, [new ExitRecord(RunA, 10, 1)]).StoppedByUser);
        Assert.False(UserStopDetector.Decide([], [new ExitRecord([], 10, 1)]).StoppedByUser);
    }

    private static byte[] Id(byte b) => Enumerable.Repeat(b, RunIdList.IdLength).ToArray();

    [Fact]
    public void A_process_that_never_started_the_Host_does_not_lose_a_user_stop()
    {
        // Run A started the Host and consumed the decision: the file holds A only.
        var file = RunIdList.OnHostConsumed(Id(1));
        // Run B (a FileProvider query, no Host) starts: the file holds A and B, and B is stopped by the user.
        file = RunIdList.OnProcessStart(RunIdList.Parse(file), Id(2));
        Assert.Equal(2, RunIdList.Parse(file).Count);
        // Run C starts its Host: B's user stop counts, although B was not the run before it in the Host's history.
        var records = new[] { new ExitRecord(Id(1), 5, 100), new ExitRecord(Id(2), 10, 200) };
        Assert.True(UserStopDetector.DecideAny(RunIdList.Parse(file), records).StoppedByUser);
        // C consumed it: the file holds C only, and the same records no longer count at the next start.
        file = RunIdList.OnHostConsumed(Id(3));
        file = RunIdList.OnProcessStart(RunIdList.Parse(file), Id(4));
        Assert.False(UserStopDetector.DecideAny(RunIdList.Parse(file), records).StoppedByUser);
    }

    [Fact]
    public void Run_ids_keep_the_newest_sixteen_and_drop_a_torn_tail()
    {
        var ids = Enumerable.Range(1, 20).Select(i => Id((byte)i)).ToList();
        var parsed = RunIdList.Parse(RunIdList.Serialize(ids));
        Assert.Equal(RunIdList.MaxIds, parsed.Count);
        Assert.Equal(Id(5), parsed[0]);
        Assert.Equal(Id(20), parsed[^1]);
        Assert.Single(RunIdList.Parse([.. Id(7), 1, 2, 3]));
        Assert.Empty(RunIdList.Parse(null));
        Assert.False(UserStopDetector.DecideAny([], [new ExitRecord(Id(1), 10, 1)]).StoppedByUser);
    }

    [Theory]
    [InlineData(1, "1 download will resume")]
    [InlineData(3, "3 downloads will resume")]
    public void Waiting_body_counts_the_downloads(int count, string expected)
    {
        Assert.Equal("Waiting for Wi-Fi", NotificationText.WaitingTitle);
        Assert.Equal(expected, NotificationText.WaitingBody(count));
    }

    [Fact]
    public void Ongoing_carries_the_no_resume_warning_only_while_some_download_can_not_resume()
    {
        var first = new ActiveFile(Guid.NewGuid(), "a.zip", 10, 100, null);
        var plain = Sum(2, 1_000_000, first, 10, 100);
        Assert.Null(NotificationText.Ongoing(plain).Warning);
        var one = NotificationText.Ongoing(plain with { NoResumeOnMeteredCount = 1 });
        Assert.Equal("1 download can't wait for Wi-Fi: stopping it starts over", one.Warning);
        Assert.Equal("2 downloads · 1.0 MB/s", one.Title);
        Assert.Equal("2 downloads can't wait for Wi-Fi: stopping them starts over", NotificationText.Ongoing(plain with { NoResumeOnMeteredCount = 2 }).Warning);
        // The warning is part of the value, so a change of it replaces the notification.
        Assert.NotEqual(NotificationText.Ongoing(plain), one);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    public void Metered_unless_the_network_says_it_is_not_metered_even_for_a_while(bool notMetered, bool temporarily, bool expected) =>
        Assert.Equal(expected, MeteredPolicy.IsMetered(notMetered, temporarily));

    [Theory]
    [InlineData(false, true, false)]  // unlocked and the screen on: the user is there, keep going and warn
    [InlineData(true, true, true)]    // locked, screen on: stop at once
    [InlineData(true, false, true)]   // locked, screen off
    [InlineData(false, false, true)]  // screen off before the lock took effect: nobody looks at the warning
    public void Non_resumable_downloads_stop_at_once_unless_the_user_is_using_the_phone(bool locked, bool interactive, bool stop) =>
        Assert.Equal(stop, MeteredPolicy.StopNonResumableAtOnce(locked, interactive));
}
