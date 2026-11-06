using DownloadManager.Runtime;
using Xunit;

namespace DownloadManager.Integration.Tests;

public sealed class UpdateInstallJournalTests : IDisposable
{
    private static readonly Func<int, bool> Dead = _ => false;
    private static readonly Func<int, bool> Running = _ => true;

    private readonly string _directory = Path.Combine(AppContext.BaseDirectory, "test-work", Guid.NewGuid().ToString("N"));
    private readonly UpdateInstallJournal _journal;

    public UpdateInstallJournalTests()
    {
        Directory.CreateDirectory(_directory);
        _journal = new UpdateInstallJournal(_directory);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    /// <summary>The file exactly as apply-update.ps1 writes it.</summary>
    private void WriteResult(string status, string version, string error, string at) => File.WriteAllText(_journal.ResultPath,
        $$"""
        {
            "status":  "{{status}}",
            "version":  "{{version}}",
            "error":  "{{error}}",
            "at":  "{{at}}"
        }
        """);

    [Fact]
    public void AFailureIsToldOnce()
    {
        WriteResult("failed", "0.3.2", "The file is in use.", "2026-11-06T01:53:20.8080028+03:00");

        var outcome = _journal.TakeUnreported(Dead);
        Assert.NotNull(outcome);
        Assert.False(outcome.Succeeded);
        Assert.Equal("0.3.2", outcome.Version);
        Assert.Equal("The file is in use.", outcome.Error);
        Assert.Equal(DateTimeOffset.Parse("2026-11-06T01:53:20.8080028+03:00"), outcome.At);

        Assert.Null(_journal.TakeUnreported(Dead));
    }

    [Fact]
    public void ASuccessHasNoError()
    {
        WriteResult("succeeded", "0.3.3", "", "2026-11-06T01:53:20+03:00");
        var outcome = _journal.TakeUnreported(Dead);
        Assert.NotNull(outcome);
        Assert.True(outcome.Succeeded);
        Assert.Null(outcome.Error);
    }

    [Fact]
    public void ALaterResultIsToldToo()
    {
        WriteResult("succeeded", "0.3.3", "", "2026-11-06T01:53:20+03:00");
        Assert.True(_journal.TakeUnreported(Dead)!.Succeeded);

        WriteResult("failed", "0.3.3", "The new version did not start.", "2026-11-06T01:53:30+03:00");
        Assert.False(_journal.TakeUnreported(Dead)!.Succeeded);
    }

    [Fact]
    public void AHelperThatDiedWithoutAResultCountsAsAFailure()
    {
        var now = DateTimeOffset.Parse("2026-11-06T02:00:00+03:00");
        _journal.MarkPending("0.3.3", 4242);

        var outcome = _journal.TakeUnreported(Dead, now);
        Assert.NotNull(outcome);
        Assert.False(outcome.Succeeded);
        Assert.Equal("0.3.3", outcome.Version);
        Assert.NotNull(outcome.Error);

        Assert.Null(_journal.TakeUnreported(Dead, now));
        Assert.True(_journal.FailedRecently("0.3.3", TimeSpan.FromHours(24), now));
    }

    [Fact]
    public void AHelperStillWorkingIsLeftAlone()
    {
        _journal.MarkPending("0.3.3", 4242);
        Assert.Null(_journal.TakeUnreported(Running));
        // Once it has gone, the attempt is still accounted for.
        Assert.NotNull(_journal.TakeUnreported(Dead));
    }

    [Fact]
    public void AResultIsReadEvenWhileItsHelperIsFinishing()
    {
        // A rollback records the result, then restarts the old Host just before it exits.
        _journal.MarkPending("0.3.3", 4242);
        WriteResult("failed", "0.3.3", "The new version did not start.", "2026-11-06T01:53:20+03:00");
        Assert.False(_journal.TakeUnreported(Running)!.Succeeded);
        Assert.Null(_journal.TakeUnreported(Dead));
    }

    [Fact]
    public void AnotherProgramWithTheHelpersProcessIdDoesNotCountAsTheHelper()
    {
        // After a restart the number can belong to anything; here, this test process.
        _journal.MarkPending("0.3.3", Environment.ProcessId);
        Assert.False(_journal.TakeUnreported()!.Succeeded);
    }

    [Fact]
    public void NothingToTellWithoutAnUpdate() => Assert.Null(_journal.TakeUnreported(Dead));

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"status":"failed","version":"0.3.3","at":"yesterday"}""")]
    public void AnUnreadableResultIsIgnored(string content)
    {
        File.WriteAllText(_journal.ResultPath, content);
        Assert.Null(_journal.TakeUnreported(Dead));
        Assert.False(_journal.FailedRecently("0.3.3", TimeSpan.FromHours(24)));
    }

    [Fact]
    public void OnlyARecentFailureOfThatVersionHoldsBackTheAutomaticRetry()
    {
        var failedAt = DateTimeOffset.Parse("2026-11-06T01:53:20+03:00");
        var day = TimeSpan.FromHours(24);
        WriteResult("failed", "0.3.3", "x", "2026-11-06T01:53:20+03:00");
        Assert.True(_journal.FailedRecently("0.3.3", day, failedAt.AddHours(1)));
        Assert.False(_journal.FailedRecently("0.3.3", day, failedAt.AddHours(25)));
        Assert.False(_journal.FailedRecently("0.3.4", day, failedAt.AddHours(1)));

        WriteResult("succeeded", "0.3.3", "", "2026-11-06T01:53:20+03:00");
        Assert.False(_journal.FailedRecently("0.3.3", day, failedAt.AddHours(1)));
    }

    [Fact]
    public void ANewInstallForgetsThePreviousOne()
    {
        WriteResult("failed", "0.3.3", "x", "2026-11-06T01:53:20+03:00");
        Assert.NotNull(_journal.TakeUnreported(Dead));

        _journal.ClearForNewInstall();
        Assert.False(_journal.FailedRecently("0.3.3", TimeSpan.FromHours(24), DateTimeOffset.Parse("2026-11-06T02:00:00+03:00")));
        Assert.Null(_journal.TakeUnreported(Dead));

        // The same timestamp is not mistaken for the one already reported.
        WriteResult("failed", "0.3.3", "x", "2026-11-06T01:53:20+03:00");
        Assert.NotNull(_journal.TakeUnreported(Dead));
    }
}
