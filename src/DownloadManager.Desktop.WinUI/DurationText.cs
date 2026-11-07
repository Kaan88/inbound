namespace DownloadManager.Desktop.WinUI;

/// <summary>The words for the list's Time left column.</summary>
internal static class DurationText
{
    public static string TimeLeft(TimeSpan? duration)
    {
        if (duration is not { } left || left <= TimeSpan.Zero) return string.Empty;
        if (left.TotalHours >= 1) return $"{(int)left.TotalHours}h {left.Minutes}m";
        return left.TotalMinutes >= 1 ? $"{left.Minutes}m {left.Seconds}s" : $"{Math.Max(1, left.Seconds)}s";
    }
}
