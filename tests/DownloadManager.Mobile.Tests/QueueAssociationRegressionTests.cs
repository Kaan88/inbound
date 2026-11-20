using DownloadManager.Contracts;
using DownloadManager.Mobile;

namespace DownloadManager.Mobile.Tests;

public sealed class QueueAssociationRegressionTests
{
    [Theory]
    [InlineData(false, 1, true, 1)]
    [InlineData(true, 1, true, 2)]
    [InlineData(true, 1, false, 1)]
    public void Association_only_updates_notify_after_the_new_job_is_available(
        bool wasQueued, int previousQueue, bool isQueued, int nextQueue)
    {
        var id = Guid.NewGuid();
        var before = Jobs.Make("queued", id: id);
        before.IsQueued = wasQueued;
        before.QueueId = previousQueue;
        var row = Jobs.Row(before);
        var after = Jobs.Make("queued", id: id);
        after.IsQueued = isQueued;
        after.QueueId = nextQueue;
        var changes = new List<string?>();
        JobDto? observed = null;
        row.PropertyChanged += (_, e) =>
        {
            changes.Add(e.PropertyName);
            if (e.PropertyName == nameof(JobRow.Job)) observed = row.Job;
        };

        row.ApplyFrom(after);

        Assert.Equal([nameof(JobRow.Job)], changes);
        Assert.Same(after, observed);
        Assert.Equal(isQueued, row.Job.IsQueued);
        Assert.Equal(nextQueue, row.Job.QueueId);
    }

    [Fact]
    public void Equivalent_queue_association_on_a_fresh_snapshot_does_not_notify()
    {
        var id = Guid.NewGuid();
        var before = Jobs.Make("queued", id: id);
        before.IsQueued = true;
        before.QueueId = 4;
        var row = Jobs.Row(before);
        var after = Jobs.Make("queued", id: id);
        after.IsQueued = true;
        after.QueueId = 4;
        var changes = new List<string?>();
        row.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        row.ApplyFrom(after);

        Assert.Empty(changes);
        Assert.Same(after, row.Job);
    }

    [Fact]
    public void Upcoming_start_uses_only_a_future_host_value_for_a_stopped_enabled_queue()
    {
        var now = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
        var queue = new QueueDto { IsRunning = false, NextTimedStartAt = now.AddMinutes(1) };

        Assert.Equal(now.AddMinutes(1), QueueSettingsModel.ReportedUpcomingStart(queue, true, now));
        Assert.Null(QueueSettingsModel.ReportedUpcomingStart(queue, false, now));
        queue.IsRunning = true;
        Assert.Null(QueueSettingsModel.ReportedUpcomingStart(queue, true, now));
    }

    [Fact]
    public void Missing_or_expired_host_start_falls_back_without_calculating_a_new_time()
    {
        var now = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
        var queue = new QueueDto { IsRunning = false };

        Assert.Null(QueueSettingsModel.ReportedUpcomingStart(queue, true, now));
        queue.NextTimedStartAt = now.AddTicks(-1);
        Assert.Null(QueueSettingsModel.ReportedUpcomingStart(queue, true, now));
        queue.NextTimedStartAt = now;
        Assert.Null(QueueSettingsModel.ReportedUpcomingStart(queue, true, now));
        queue.NextTimedStartAt = now.AddTicks(1);
        Assert.Equal(queue.NextTimedStartAt, QueueSettingsModel.ReportedUpcomingStart(queue, true, now));
        Assert.Null(QueueSettingsModel.ReportedUpcomingStart(queue, true, now.AddTicks(1)));
    }
}
