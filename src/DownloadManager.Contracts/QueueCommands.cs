namespace DownloadManager.Contracts;

/// <summary>
/// When a queue's Start and Stop are offered, and how its state reads, kept free of WinUI so they can be tested. A
/// running queue still waits for Start before its "later" downloads, and before the ones its stop, a move or an exit
/// paused, go (U-WL-1); the Host's queue start takes the same ones (U-WL-2). A download the user paused is not one.
/// The window and the CLI use it; the web UI has the same rule in <c>webui/src/lib/queues.ts</c>.
/// </summary>
public static class QueueCommands
{
    /// <summary>The downloads of this queue that only a Start lets go.</summary>
    public static int WaitingForStart(QueueDto queue, IEnumerable<JobDto> jobs)
        => jobs.Count(job => job.IsQueued && job.QueueId == queue.Id && IsWaitingForStart(job));

    public static bool IsWaitingForStart(JobDto job)
        => job.State == "queued" && string.Equals(job.StartMode, "later", StringComparison.OrdinalIgnoreCase) ||
           job.State == "paused" && !job.PausedByUser;

    /// <summary>Start is offered for a stopped queue, and for a running one that still holds downloads waiting for it.</summary>
    public static bool CanStart(QueueDto queue, IEnumerable<JobDto> jobs) => !queue.IsRunning || WaitingForStart(queue, jobs) > 0;

    public static bool CanStop(QueueDto queue) => queue.IsRunning;

    /// <summary>"running", "stopped", or "running, 2 waiting for Start".</summary>
    public static string State(QueueDto queue, IEnumerable<JobDto> jobs)
    {
        if (!queue.IsRunning) return "stopped";
        var waiting = WaitingForStart(queue, jobs);
        return waiting == 0 ? "running" : $"running, {waiting} waiting for Start";
    }
}
