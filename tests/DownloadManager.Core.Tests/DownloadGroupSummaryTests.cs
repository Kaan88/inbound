using DownloadManager.Contracts;

namespace DownloadManager.Core.Tests;

public sealed class DownloadGroupSummaryTests
{
    [Fact]
    public void UnknownMemberTotalKeepsAggregateProgressAndEtaUnknown()
    {
        var summary = DownloadGroupSummary.Create(
        [
            Job("downloading", total: 100, received: 50, rate: 25),
            Job("queued", total: null, received: 10, rate: null),
        ]);

        Assert.Equal(2, summary.MemberCount);
        Assert.Equal(60, summary.ReceivedBytes);
        Assert.Null(summary.TotalBytes);
        Assert.Null(summary.Progress);
        Assert.Null(summary.EstimatedRemaining);
    }

    [Fact]
    public void ActiveAndFailedStatesTakePrecedenceAndCompletedCountRemainsAvailable()
    {
        var activeSummary = DownloadGroupSummary.Create(
        [
            Job("completed", total: 10, received: 10),
            Job("queued", total: 10, received: 0),
            Job("paused", total: 10, received: 2),
            Job("failed", total: 10, received: 3),
            Job("downloading", total: 10, received: 4, rate: 1),
        ]);
        var failedSummary = DownloadGroupSummary.Create(
        [
            Job("completed", total: 10, received: 10),
            Job("queued", total: 10, received: 0),
            Job("paused", total: 10, received: 2),
            Job("failed", total: 10, received: 3),
        ]);

        Assert.Equal("active", activeSummary.State);
        Assert.Equal(1, activeSummary.CompletedCount);
        Assert.Equal("failed", failedSummary.State);
        Assert.Equal(1, failedSummary.CompletedCount);
    }

    [Fact]
    public void AggregateByteAndRateTotalsSaturateWithoutProducingInvalidEta()
    {
        var summary = DownloadGroupSummary.Create(
        [
            Job("downloading", total: long.MaxValue, received: long.MaxValue - 10, rate: double.MaxValue),
            Job("downloading", total: 100, received: 20, rate: double.MaxValue),
        ]);

        Assert.Equal(long.MaxValue, summary.TotalBytes);
        Assert.Equal(long.MaxValue, summary.ReceivedBytes);
        Assert.Equal(double.MaxValue, summary.BytesPerSecond);
        Assert.True(double.IsFinite(summary.BytesPerSecond));
        Assert.Equal(TimeSpan.Zero, summary.EstimatedRemaining);
    }

    [Fact]
    public void CompletedAndCancelledTerminalMixtureIsCancelledButCountsCompletions()
    {
        var summary = DownloadGroupSummary.Create(
        [
            Job("completed", total: 10, received: 10),
            Job("cancelled", total: 10, received: 4),
        ]);

        Assert.Equal("cancelled", summary.State);
        Assert.Equal(1, summary.CompletedCount);
        Assert.Equal(2, summary.MemberCount);
    }

    [Fact]
    public void EmptyGroupHasStableEmptySummary()
    {
        var summary = DownloadGroupSummary.Create(Array.Empty<JobDto>());

        Assert.Equal(0, summary.MemberCount);
        Assert.Equal(0, summary.CompletedCount);
        Assert.Equal(0, summary.ReceivedBytes);
        Assert.Null(summary.TotalBytes);
        Assert.Null(summary.Progress);
        Assert.Equal("empty", summary.State);
        Assert.Null(summary.EstimatedRemaining);
    }

    private static JobDto Job(string state, long? total, long received, double? rate = null) => new()
    {
        State = state,
        TotalBytes = total,
        ReceivedBytes = received,
        BytesPerSecond = rate,
    };
}
