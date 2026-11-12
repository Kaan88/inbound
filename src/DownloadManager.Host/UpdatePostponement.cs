namespace DownloadManager.Host;

/// <summary>
/// What the Host does when an automatic update install is refused as busy: one balloon per version, and a
/// retry that waits longer each time (the monitor also retries when the last active download ends). The retry
/// installs the version already found; it never fetches the manifest again.
/// </summary>
internal sealed class UpdatePostponement
{
    internal static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan MaxDelay = TimeSpan.FromHours(1);

    /// <summary>The least time between two retries brought forward by the end of downloads.</summary>
    internal static readonly TimeSpan EarlyRetryGap = TimeSpan.FromMinutes(1);

    private string? _notifiedVersion;
    private TimeSpan _nextDelay = FirstDelay;
    private DateTimeOffset? _lastEarlyRetry;

    /// <summary>
    /// Whether the refusal was the downloads, the only reason the end of the last download can clear. Another busy
    /// reason (an install already running or staged) waits for the growing delay alone.
    /// </summary>
    public static bool WaitsForDownloads(string? error) =>
        error is not null && error.StartsWith("Downloads", StringComparison.Ordinal);

    /// <summary>
    /// True when a retry may be brought forward now, at most once a minute: a job that is still starting is refused
    /// as a download but not counted as one, so without the gap every job event could bring another try.
    /// </summary>
    public bool TryEarlyRetry(DateTimeOffset now)
    {
        if (_lastEarlyRetry is { } last && now - last < EarlyRetryGap) return false;
        _lastEarlyRetry = now;
        return true;
    }

    /// <summary>True the first time a version is postponed; false for every later postponement of it.</summary>
    public bool ShouldNotify(string? version)
    {
        var key = version ?? string.Empty;
        if (string.Equals(_notifiedVersion, key, StringComparison.OrdinalIgnoreCase)) return false;
        _notifiedVersion = key;
        _nextDelay = FirstDelay;
        return true;
    }

    /// <summary>The wait before the next automatic try; it doubles each time, up to an hour.</summary>
    public TimeSpan NextDelay()
    {
        var delay = _nextDelay;
        var doubled = TimeSpan.FromTicks(Math.Min(_nextDelay.Ticks * 2, MaxDelay.Ticks));
        _nextDelay = doubled;
        return delay;
    }

    /// <summary>After an install starts or the update goes away: the next postponement notifies again.</summary>
    public void Reset()
    {
        _notifiedVersion = null;
        _nextDelay = FirstDelay;
        _lastEarlyRetry = null;
    }

    /// <summary>The balloon text for the reason the service gave; each busy reason says what it is.</summary>
    public static string Message(string? error, bool willRetry) =>
        string.IsNullOrWhiteSpace(error)
            ? willRetry ? "The update could not start yet; it will be tried again." : "The update could not start yet."
            : WaitsForDownloads(error)
                ? willRetry
                    ? "Downloads are active; the update will install when they finish."
                    : "Downloads are active; the update will not restart the Host yet."
                : error;
}
