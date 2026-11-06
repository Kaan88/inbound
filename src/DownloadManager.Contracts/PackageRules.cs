namespace DownloadManager.Contracts;

/// <summary>Priorities the scheduler orders by. Higher starts first; equal ones keep queue order.</summary>
public static class JobPriorities
{
    public const int Low = -1;
    public const int Normal = 0;
    public const int High = 1;

    public static bool IsValid(int priority) => priority is Low or Normal or High;

    public static bool TryParse(string? text, out int priority)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "high": priority = High; return true;
            case "normal": priority = Normal; return true;
            case "low": priority = Low; return true;
            default: priority = Normal; return false;
        }
    }

    public static string Name(int priority) => priority > Normal ? "High" : priority < Normal ? "Low" : "Normal";
}

/// <summary>
/// What a package's Pause, Resume and Cancel touch. The Host applies these and the frontends use
/// them to enable their buttons, so a button is never live for an action the Host would skip.
/// </summary>
public static class PackageRules
{
    /// <summary>Shown in the Q column when a package's files are in different queues.</summary>
    public const string MixedQueueMarker = "*";

    public static IReadOnlyList<JobDto> PauseTargets(IEnumerable<JobDto> children)
        => children.Where(child => child.CanPause).ToList();

    /// <summary>
    /// Resume restarts paused and failed files but leaves cancelled ones cancelled, because
    /// cancelling a file was a decision about that file. The one exception undoes a package
    /// Cancel: when that Cancel stopped every file in the package, Resume brings them all back.
    /// A file cancelled on its own, or one that had already finished when the package was
    /// cancelled, means the Cancel did not cover the whole package, so nothing is revived and
    /// each file has to be resumed by itself.
    /// </summary>
    public static IReadOnlyList<JobDto> ResumeTargets(Guid packageId, IReadOnlyCollection<JobDto> children)
    {
        var undoCancel = children.Count > 0 && children.All(child =>
            IsCancelled(child) && child.CancelledByPackageId == packageId);
        return children.Where(child => child.CanResume && (undoCancel || !IsCancelled(child))).ToList();
    }

    /// <summary>Finished and already cancelled files are left as they are.</summary>
    public static IReadOnlyList<JobDto> CancelTargets(IEnumerable<JobDto> children)
        => children.Where(child => !child.IsTerminal && !IsCancelled(child)).ToList();

    /// <summary>A finished file has nothing left to schedule.</summary>
    public static IReadOnlyList<JobDto> PriorityTargets(IEnumerable<JobDto> children)
        => children.Where(child => !IsCompleted(child)).ToList();

    /// <summary>The queue every file is in; null when they differ or none is in a queue.</summary>
    public static int? SharedQueueId(IReadOnlyCollection<JobDto> children)
    {
        var queues = children.Select(QueueKey).Distinct().ToList();
        return queues.Count == 1 && queues[0] >= 0 ? queues[0] : null;
    }

    /// <summary>The Q column: the shared queue's number, a marker when mixed, blank when none.</summary>
    public static string QueueText(IReadOnlyCollection<JobDto> children)
    {
        var queues = children.Select(QueueKey).Distinct().ToList();
        return queues.Count switch
        {
            0 => string.Empty,
            1 => queues[0] >= 0 ? (queues[0] + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty,
            _ => MixedQueueMarker,
        };
    }

    private static int QueueKey(JobDto child) => child.IsQueued ? child.QueueId : -1;

    private static bool IsCancelled(JobDto child) => string.Equals(child.State, "cancelled", StringComparison.OrdinalIgnoreCase);

    private static bool IsCompleted(JobDto child) => string.Equals(child.State, "completed", StringComparison.OrdinalIgnoreCase);
}
