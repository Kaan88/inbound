namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// The last minute of one download's speed, as the progress window saw it. Samples come from the job
/// updates the window already receives, so nothing extra is asked of the Host, and nothing runs between
/// updates. One sample is kept per second, the latest in that second winning.
/// </summary>
internal sealed class SpeedHistory
{
    public const int Seconds = 60;
    private readonly List<(long Second, double Rate)> _samples = [];

    public IReadOnlyList<(long Second, double Rate)> Samples => _samples;

    public void Add(DateTimeOffset at, double bytesPerSecond)
    {
        var second = at.ToUnixTimeSeconds();
        var rate = double.IsFinite(bytesPerSecond) ? Math.Max(0, bytesPerSecond) : 0;
        if (_samples.Count > 0 && _samples[^1].Second == second) _samples[^1] = (second, rate);
        else if (_samples.Count == 0 || _samples[^1].Second < second) _samples.Add((second, rate));
        else return; // A clock that went back: keep what is there rather than draw a line backwards.
        var oldest = second - Seconds;
        var drop = 0;
        while (drop < _samples.Count && _samples[drop].Second < oldest) drop++;
        if (drop > 0) _samples.RemoveRange(0, drop);
    }

    public void Clear() => _samples.Clear();

    public double Peak => _samples.Count == 0 ? 0 : _samples.Max(sample => sample.Rate);

    /// <summary>Each sample as (x from 0 to 1 across the minute ending at the latest sample, rate).</summary>
    public IReadOnlyList<(double X, double Rate)> Points()
    {
        if (_samples.Count == 0) return [];
        var end = _samples[^1].Second;
        return _samples.Select(sample => (1 - (end - sample.Second) / (double)Seconds, sample.Rate)).ToList();
    }
}

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
