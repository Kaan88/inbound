namespace DownloadManager.Desktop.WinUI;

/// <summary>Words and figures for the progress window's ring and tiles.</summary>
internal static class ProgressOverviewText
{
    /// <summary>A byte count as its number and its unit, so a tile can show the unit smaller.</summary>
    public static (string Number, string Unit) Split(long bytes) => bytes >= 1024L * 1024 * 1024
        ? ($"{bytes / (1024.0 * 1024 * 1024):N2}", "GB") : bytes >= 1024L * 1024
        ? ($"{bytes / (1024.0 * 1024):N2}", "MB") : bytes >= 1024 ? ($"{bytes / 1024.0:N1}", "KB") : ($"{bytes:N0}", "B");

    /// <summary>Received of total in the total's unit: "2.60", "/ 4.20 GB".</summary>
    public static (string Number, string Unit) Of(long received, long total)
    {
        var (totalNumber, unit) = Split(total);
        var scale = unit switch { "GB" => 1024.0 * 1024 * 1024, "MB" => 1024.0 * 1024, "KB" => 1024.0, _ => 1 };
        var number = unit == "B" ? $"{received:N0}" : (received / scale).ToString(unit == "KB" ? "N1" : "N2");
        return (number, $"/ {totalNumber} {unit}");
    }

    /// <summary>"4 min 23 s", "1 h 05 min", "12 s".</summary>
    public static string Duration(TimeSpan? duration)
    {
        if (duration is not { } value || value < TimeSpan.Zero) return "—";
        if (value.TotalDays >= 1) return $"{(int)value.TotalDays} d {value.Hours} h";
        if (value.TotalHours >= 1) return $"{(int)value.TotalHours} h {value.Minutes:D2} min";
        if (value.TotalMinutes >= 1) return $"{value.Minutes} min {value.Seconds} s";
        return $"{Math.Max(0, (int)Math.Round(value.TotalSeconds))} s";
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
