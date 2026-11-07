using DownloadManager.Contracts;

namespace DownloadManager.Core.Tests;

public sealed class PackageRulesTests
{
    private static readonly Guid PackageId = Guid.NewGuid();

    private static JobDto Job(string state, Guid? cancelledBy = null, bool isQueued = true, int queueId = 0) => new()
    {
        Id = Guid.NewGuid(),
        State = state,
        CanResume = state is "paused" or "failed" or "cancelled",
        CanPause = state is "queued" or "connecting" or "downloading" or "retryWaiting",
        IsTerminal = state is "completed" or "cancelled",
        CancelledByPackageId = cancelledBy,
        IsQueued = isQueued,
        QueueId = queueId,
    };

    [Fact]
    public void ResumeSkipsCancelledFilesButNotPausedOrFailedOnes()
    {
        var paused = Job("paused");
        var failed = Job("failed");
        var cancelled = Job("cancelled");
        var targets = PackageRules.ResumeTargets(PackageId, [paused, failed, cancelled, Job("completed"), Job("downloading")]);
        Assert.Equal(new[] { paused.Id, failed.Id }, targets.Select(job => job.Id));
    }

    [Fact]
    public void ResumeUndoesAPackageCancelOnlyWhenItCoversEveryFile()
    {
        var all = new[] { Job("cancelled", PackageId), Job("cancelled", PackageId) };
        Assert.Equal(2, PackageRules.ResumeTargets(PackageId, all).Count);

        Assert.Empty(PackageRules.ResumeTargets(PackageId, [Job("cancelled", PackageId), Job("completed")]));
        Assert.Empty(PackageRules.ResumeTargets(PackageId, [Job("cancelled", PackageId), Job("cancelled")]));
        Assert.Empty(PackageRules.ResumeTargets(PackageId, [Job("cancelled"), Job("cancelled")]));
        Assert.Empty(PackageRules.ResumeTargets(PackageId, [Job("cancelled", Guid.NewGuid()), Job("cancelled", Guid.NewGuid())]));
        Assert.Empty(PackageRules.ResumeTargets(PackageId, []));
    }

    [Fact]
    public void AnUncoveredPackageCancelStillResumesWhatIsNotCancelled()
    {
        var paused = Job("paused");
        var targets = PackageRules.ResumeTargets(PackageId, [Job("cancelled", PackageId), paused]);
        Assert.Equal(paused.Id, Assert.Single(targets).Id);
    }

    [Fact]
    public void CancelSkipsFinishedAndCancelledFiles()
    {
        var failed = Job("failed");
        var queued = Job("queued");
        var targets = PackageRules.CancelTargets([failed, queued, Job("completed"), Job("cancelled")]);
        Assert.Equal(new[] { failed.Id, queued.Id }, targets.Select(job => job.Id));
    }

    [Fact]
    public void PriorityOnlyTouchesUnfinishedFiles()
    {
        var paused = Job("paused");
        Assert.Equal(paused.Id, Assert.Single(PackageRules.PriorityTargets([paused, Job("completed")])).Id);
    }

    [Theory]
    [InlineData(new[] { 0, 0 }, "1", 0)]
    [InlineData(new[] { 2, 2 }, "3", 2)]
    [InlineData(new[] { 0, 2 }, PackageRules.MixedQueueMarker, null)]
    public void QueueColumnShowsTheSharedQueueOrAMarker(int[] queues, string expected, int? shared)
    {
        var jobs = queues.Select(queue => Job("queued", queueId: queue)).ToList();
        Assert.Equal(expected, PackageRules.QueueText(jobs));
        Assert.Equal(shared, PackageRules.SharedQueueId(jobs));
    }

    [Fact]
    public void QueueColumnTreatsNoQueueAsItsOwnValue()
    {
        var none = new[] { Job("queued", isQueued: false), Job("queued", isQueued: false) };
        Assert.Equal(string.Empty, PackageRules.QueueText(none));
        Assert.Null(PackageRules.SharedQueueId(none));

        var some = new[] { Job("queued", isQueued: false), Job("queued") };
        Assert.Equal(PackageRules.MixedQueueMarker, PackageRules.QueueText(some));
        Assert.Equal(string.Empty, PackageRules.QueueText([]));
    }

    [Theory]
    [InlineData("high", JobPriorities.High)]
    [InlineData(" LOW ", JobPriorities.Low)]
    [InlineData("Normal", JobPriorities.Normal)]
    public void PrioritiesParseFromTheirNames(string text, int expected)
    {
        Assert.True(JobPriorities.TryParse(text, out var priority));
        Assert.Equal(expected, priority);
        Assert.True(JobPriorities.IsValid(priority));
    }

    [Theory]
    [InlineData("urgent")]
    [InlineData("")]
    [InlineData(null)]
    public void UnknownPrioritiesDoNotParse(string? text)
    {
        Assert.False(JobPriorities.TryParse(text, out _));
    }

    [Fact]
    public void OnlyTheThreeNamedPrioritiesAreValid()
    {
        Assert.False(JobPriorities.IsValid(2));
        Assert.False(JobPriorities.IsValid(-2));
        Assert.Equal("High", JobPriorities.Name(JobPriorities.High));
        Assert.Equal("Normal", JobPriorities.Name(JobPriorities.Normal));
        Assert.Equal("Low", JobPriorities.Name(JobPriorities.Low));
    }
}
