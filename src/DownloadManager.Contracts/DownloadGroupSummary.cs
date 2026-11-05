namespace DownloadManager.Contracts;

/// <summary>A read-only aggregate view of the jobs in a download group.</summary>
public sealed record DownloadGroupSummary
{
    public int MemberCount { get; init; }
    public int CompletedCount { get; init; }
    public long ReceivedBytes { get; init; }
    public long? TotalBytes { get; init; }
    public double BytesPerSecond { get; init; }
    /// <summary>Aggregate fraction from 0 to 1; null when any member has an unknown total.</summary>
    public double? Progress { get; init; }
    public string State { get; init; } = "empty";
    public TimeSpan? EstimatedRemaining { get; init; }

    public static DownloadGroupSummary Create(IEnumerable<JobDto> children)
    {
        ArgumentNullException.ThrowIfNull(children);

        var jobs = children.ToArray();
        if (jobs.Any(static job => job is null))
            throw new ArgumentException("A group cannot contain a null job.", nameof(children));

        if (jobs.Length == 0)
            return new DownloadGroupSummary();

        long received = 0;
        long total = 0;
        var allTotalsKnown = true;
        var speed = 0d;
        var completed = 0;
        var hasActive = false;
        var hasFailed = false;
        var hasPaused = false;
        var hasQueued = false;
        var hasCancelled = false;

        foreach (var job in jobs)
        {
            received = SaturatingAdd(received, Math.Max(0, job.ReceivedBytes));
            if (job.TotalBytes is long bytes && bytes >= 0)
                total = SaturatingAdd(total, bytes);
            else
                allTotalsKnown = false;

            var state = job.State?.Trim();
            if (IsActive(state))
            {
                hasActive = true;
                if (job.BytesPerSecond is double rate && double.IsFinite(rate) && rate > 0)
                    speed += rate;
                if (!double.IsFinite(speed)) speed = double.MaxValue;
            }
            else if (EqualsState(state, "failed")) hasFailed = true;
            else if (EqualsState(state, "paused")) hasPaused = true;
            else if (EqualsState(state, "queued")) hasQueued = true;
            else if (EqualsState(state, "cancelled")) hasCancelled = true;
            else if (EqualsState(state, "completed")) completed++;
            else hasQueued = true;
        }

        var allCompleted = completed == jobs.Length;
        var stateName = hasActive ? "active"
            : hasFailed ? "failed"
            : hasPaused ? "paused"
            : hasQueued ? "queued"
            : hasCancelled ? "cancelled"
            : allCompleted ? "completed"
            : "cancelled"; // Only completed/cancelled terminal mixtures reach here.

        var knownTotal = allTotalsKnown ? total : (long?)null;
        double? progress = knownTotal is null ? null
            : knownTotal.Value == 0 ? (allCompleted ? 1d : 0d)
            : Math.Min(1d, (double)received / knownTotal.Value);

        TimeSpan? remaining = null;
        if (knownTotal is long totalBytes && speed > 0)
        {
            var remainingBytes = Math.Max(0L, totalBytes - Math.Min(received, totalBytes));
            var seconds = remainingBytes / speed;
            remaining = seconds >= TimeSpan.MaxValue.TotalSeconds - 1 ? TimeSpan.MaxValue : TimeSpan.FromSeconds(seconds);
        }

        return new DownloadGroupSummary
        {
            MemberCount = jobs.Length,
            CompletedCount = completed,
            ReceivedBytes = received,
            TotalBytes = knownTotal,
            BytesPerSecond = speed,
            Progress = progress,
            State = stateName,
            EstimatedRemaining = remaining
        };
    }

    private static bool IsActive(string? state) =>
        EqualsState(state, "downloading") || EqualsState(state, "connecting") ||
        EqualsState(state, "verifying") || EqualsState(state, "retryWaiting");

    private static bool EqualsState(string? actual, string expected) =>
        string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

    private static long SaturatingAdd(long left, long right) =>
        left > long.MaxValue - right ? long.MaxValue : left + right;
}
