namespace DownloadManager.Host;

/// <summary>
/// How long the clipboard listener waits before reading again while another program holds the clipboard: short at
/// first, then every 2 s, for about half a minute in all (the 600 ms poll it replaced kept trying until it could
/// read). Only runs after a refused read, so an idle Host is not woken by it.
/// </summary>
internal static class ClipboardRetrySchedule
{
    internal static readonly TimeSpan Longest = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan GiveUpAfter = TimeSpan.FromSeconds(30);

    /// <summary>The wait before retry number <paramref name="attempt"/> (0 first), or null when it is time to stop.</summary>
    public static TimeSpan? Delay(int attempt, TimeSpan waitedSoFar)
    {
        if (attempt < 0 || waitedSoFar >= GiveUpAfter) return null;
        var delay = TimeSpan.FromMilliseconds(250 * Math.Pow(2, Math.Min(attempt, 3)));
        return delay > Longest ? Longest : delay;
    }
}
