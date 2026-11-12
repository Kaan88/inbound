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

    [Theory]
    [InlineData("forbidden", 403, "The server returned HTTP 403. The site may want you signed in: add a login in Options > Site logins, or download it from the browser.")]
    [InlineData("unauthorized", 401, "The server returned HTTP 401. The site may want you signed in: add a login in Options > Site logins, or download it from the browser.")]
    [InlineData("unauthorized", 407, "The server returned HTTP 407.")]
    [InlineData("notFound", 404, "The server returned HTTP 404. The link may have expired: try Refresh Download Address.")]
    [InlineData("http", 503, "The server returned HTTP 503. The site had a problem. Try again later.")]
    [InlineData("http", 400, "The server returned HTTP 400.")]
    [InlineData("network", null, "The server returned HTTP 403.")]
    public void Review_5_E2_a_failure_named_by_its_HTTP_code_says_what_to_do_next(string kind, int? status, string expected)
    {
        var failed = Job("failed");
        failed.Url = "https://example.test/file.bin";
        failed.Failure = new FailureDto { Kind = kind, HttpStatus = status, Message = kind == "network" ? "The server returned HTTP 403." : $"The server returned HTTP {status}." };
        Assert.Equal(expected, JobNotes.FailureMessage(failed));
        Assert.Equal($"Failed: {expected}", JobNotes.FailureReason(failed));
    }

    [Fact]
    public void Review_5_E2_a_not_found_without_an_HTTP_404_gets_no_Refresh_step()
    {
        // An FTP 550 is notFound with no HTTP code, and Refresh Download Address doesn't fit an FTP path.
        var failed = Job("failed");
        failed.Url = "ftp://example.test/pub/file.bin";
        failed.Failure = new FailureDto { Kind = "notFound", Message = "FTP server returned 550: File unavailable." };
        Assert.Equal("FTP server returned 550: File unavailable.", JobNotes.FailureMessage(failed));
    }

    [Fact]
    public void Review_5_E2_a_providers_file_gets_no_Refresh_step()
    {
        var failed = Job("failed");
        failed.Url = "remote://torbox/1/2";
        failed.Failure = new FailureDto { Kind = "notFound", HttpStatus = 404, Message = "The server returned HTTP 404." };
        Assert.Equal("The server returned HTTP 404.", JobNotes.FailureMessage(failed));
    }

    [Fact]
    public void Review_5_A8_a_download_later_reads_Not_started_and_one_waiting_for_a_slot_Queued()
    {
        Assert.Equal("Not started", JobNotes.NotStartedText(Job("queued", "later")));
        Assert.Null(JobNotes.NotStartedText(Job("queued")));
        Assert.Null(JobNotes.NotStartedText(Job("queued", "queue")));
        Assert.Null(JobNotes.NotStartedText(Job("paused", "later")));
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
