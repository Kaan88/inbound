namespace DownloadManager.Desktop.WinUI;

/// <summary>Words and figures for the progress window's ring and tiles.</summary>
internal static class ProgressOverviewText
{
    /// <summary>A byte count as its number and its unit, so a tile can show the unit smaller (UI.R31: <see cref="UiFormat"/>).</summary>
    public static (string Number, string Unit) Split(long bytes) => UiFormat.Split(bytes);

    /// <summary>Received of total in the total's unit: "2.6", "/ 4.2 GB".</summary>
    public static (string Number, string Unit) Of(long received, long total) => UiFormat.Of(received, total);

    /// <summary>UI.R31: a duration in the list's words: "4m 23s", "1h 5m", "12s", "under 1s".</summary>
    public static string Duration(TimeSpan? duration)
    {
        if (duration is not { } value || value < TimeSpan.Zero) return "—";
        if (value.TotalDays >= 1) return $"{(int)value.TotalDays}d {value.Hours}h";
        if (value < TimeSpan.FromSeconds(1)) return "under 1s";
        return DurationText.TimeLeft(value);
    }

    /// <summary>How long a finished download took, when the Host recorded both ends.</summary>
    public static TimeSpan? Took(DateTimeOffset? started, DateTimeOffset? completed) =>
        started is { } from && completed is { } to && to > from ? to - from : null;

    /// <summary>The failure's short name for the Error tile: the HTTP status when there is one.</summary>
    public static string ErrorName(int? httpStatus, string kind) => httpStatus is { } status
        ? $"HTTP {status}"
        : string.IsNullOrWhiteSpace(kind) || kind == "none" ? "Failed"
        : char.ToUpperInvariant(kind[0]) + System.Text.RegularExpressions.Regex.Replace(kind[1..], "(?<=[a-z])(?=[A-Z])", " ").ToLowerInvariant();
}
