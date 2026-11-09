using DownloadManager.Contracts;
using DownloadManager.Desktop.WinUI;

namespace DownloadManager.Core.Tests;

/// <summary>U-WL-1, U-WL-3 and U-WL-13 (usability test, 2026-11-09).</summary>
public sealed class QueueCommandsTests
{
    private static readonly QueueDto Main = new() { Id = 0, Name = "Main", IsRunning = true };

    private static JobDto Job(string state, string startMode = "startNow", int queueId = 0, bool pausedByUser = false, bool isQueued = true)
        => new() { Id = Guid.NewGuid(), State = state, StartMode = startMode, QueueId = queueId, PausedByUser = pausedByUser, IsQueued = isQueued };

    [Fact]
    public void A_running_queue_offers_Start_while_a_later_download_waits_for_it()
    {
        JobDto[] jobs = [Job("queued", "later"), Job("downloading")];
        Assert.True(QueueCommands.CanStart(Main, jobs));
        Assert.True(QueueCommands.CanStop(Main));
        Assert.Equal("running, 1 waiting for Start", QueueCommands.State(Main, jobs));
    }

    [Fact]
    public void A_running_queue_with_nothing_waiting_offers_only_Stop()
    {
        // Queued "start now" downloads wait for a free slot, not for Start; one the user paused or one that failed
        // is not started by the queue either.
        JobDto[] jobs = [Job("queued"), Job("paused", pausedByUser: true), Job("failed"), Job("completed")];
        Assert.False(QueueCommands.CanStart(Main, jobs));
        Assert.Equal("running", QueueCommands.State(Main, jobs));
    }

    [Fact]
    public void A_download_the_queue_paused_waits_for_Start_and_other_queues_dont_count()
    {
        Assert.True(QueueCommands.CanStart(Main, [Job("paused")]));
        Assert.False(QueueCommands.CanStart(Main, [Job("queued", "later", queueId: 1)]));
        Assert.False(QueueCommands.CanStart(Main, [Job("queued", "later", isQueued: false)]));
    }

    [Fact]
    public void A_stopped_queue_offers_Start_and_not_Stop()
    {
        var stopped = new QueueDto { Id = 0, Name = "Main", IsRunning = false };
        Assert.True(QueueCommands.CanStart(stopped, []));
        Assert.False(QueueCommands.CanStop(stopped));
        Assert.Equal("stopped", QueueCommands.State(stopped, [Job("queued", "later")]));
    }

    [Fact]
    public void A_failed_download_says_why_in_its_note_and_the_progress_window()
    {
        var failed = Job("failed");
        failed.Failure = new FailureDto { Kind = "network", Message = " No connection could be made because the target machine actively refused it. (127.0.0.1:1) " };
        Assert.Equal("Failed: No connection could be made because the target machine actively refused it. (127.0.0.1:1)", JobNotes.Note(failed));
        Assert.Equal("No connection could be made because the target machine actively refused it. (127.0.0.1:1)", JobNotes.FailureMessage(failed));
        Assert.StartsWith("Failed: No connection", JobNotes.Description(JobNotes.Note(failed), "my words"), StringComparison.Ordinal);

        // An address in the reason is never shown.
        failed.Failure.Message = "HTTP 403 from https://example.test/file.bin?token=secret";
        Assert.DoesNotContain("token=secret", JobNotes.Note(failed), StringComparison.Ordinal);

        // Anything else, or a failure with no words, keeps its old note.
        var paused = Job("paused");
        paused.Failure = new FailureDto { Message = "Paused by the user." };
        Assert.Null(JobNotes.Note(paused));
        var silent = Job("failed");
        silent.Failure = new FailureDto { Message = "  " };
        Assert.Null(JobNotes.Note(silent));
        Assert.Null(JobNotes.FailureMessage(silent));
    }

    [Fact]
    public void Open_and_Open_With_wait_for_a_finished_file_and_Open_Folder_does_not()
    {
        var unfinished = FileMenuRules.Plan(singleFile: true, package: false, sideRows: 0, removableSideRows: 0, finished: false);
        Assert.Equal(FileMenuRules.NotFinishedReason, unfinished.LaunchReason);
        Assert.Null(unfinished.ShellReason);
        Assert.Equal("Open (not downloaded yet)", FileMenuRules.Label("Open", unfinished.LaunchReason));

        var finished = FileMenuRules.Plan(singleFile: true, package: false, sideRows: 0, removableSideRows: 0, finished: true);
        Assert.Null(finished.LaunchReason);

        // A reason that already applies wins: a package, or a file on another server.
        var package = FileMenuRules.Plan(singleFile: false, package: true, sideRows: 0, removableSideRows: 0, finished: false);
        Assert.Equal(package.ShellReason, package.LaunchReason);
    }
}
