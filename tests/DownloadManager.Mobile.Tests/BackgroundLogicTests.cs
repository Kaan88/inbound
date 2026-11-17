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
        Assert.Equal(5000, m.Observe(5000, t0));
        Assert.Null(m.Observe(9000, t0.AddSeconds(1)));            // too soon
        Assert.Equal(9000, m.Observe(9000, t0.AddSeconds(6)));
        Assert.Null(m.Observe(9500, t0.AddSeconds(12)));           // too little
        Assert.Null(m.Observe(2000, t0.AddSeconds(13)));           // a download left: rebase, nothing added
        Assert.Equal(11_000, m.Observe(3500, t0.AddSeconds(20)));
    }

    [Fact]
    public void Meter_reset_starts_over()
    {
        var m = new TransferMeter(1, TimeSpan.Zero);
        var t = DateTimeOffset.UnixEpoch;
        m.Observe(100, t);
        m.Reset();
        Assert.Equal(0, m.Total);
        Assert.Equal(50, m.Observe(50, t.AddSeconds(1)));
    }

    private static readonly byte[] RunA = [1, 2, 3, 4];
    private static readonly byte[] RunB = [9, 9, 9, 9];

    [Fact]
    public void UserStop_true_when_the_previous_runs_record_says_user_requested()
    {
        var d = UserStopDetector.Decide(RunA, [new ExitRecord(RunB, 10, 500), new ExitRecord(RunA, 10, 400)]);
        Assert.True(d.StoppedByUser);
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
}
