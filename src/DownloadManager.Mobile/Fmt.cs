using System.Globalization;

namespace DownloadManager.Mobile;

/// <summary>Sizes, rates and times as the Downloads rows write them (decimal units, as the desktop window does).</summary>
public static class Fmt
{
    public static string Bytes(long b)
    {
        string[] u = ["B", "KB", "MB", "GB", "TB"];
        double v = b; var i = 0;
        while (v >= 1000 && i < u.Length - 1) { v /= 1000; i++; }
        return (i == 0 ? v.ToString("0", CultureInfo.InvariantCulture) : v.ToString(v >= 100 ? "0" : "0.0", CultureInfo.InvariantCulture)) + " " + u[i];
    }

    public static string Rate(double bytesPerSecond) => Bytes((long)bytesPerSecond) + "/s";

    public static string Eta(double seconds) =>
        seconds < 90 ? $"{Math.Max(1, (int)seconds)} s left" : seconds < 5400 ? $"{(int)Math.Round(seconds / 60)} min left" : $"{seconds / 3600:0.#} h left";

    /// <summary>"about 7 min": a total time left for a whole list, not one file.</summary>
    public static string About(double seconds) =>
        seconds < 90 ? "less than 2 min" : seconds < 5400 ? $"about {(int)Math.Round(seconds / 60)} min" : $"about {seconds / 3600:0.#} h";

    /// <summary>A moment as a row writes it: the time of day when it was today, otherwise the day and month.</summary>
    public static string When(DateTimeOffset moment, DateTimeOffset now)
    {
        var m = moment.ToLocalTime();
        var n = now.ToLocalTime();
        return m.Date == n.Date ? m.ToString("HH:mm", CultureInfo.InvariantCulture)
            : m.Date == n.Date.AddDays(-1) ? "Yesterday " + m.ToString("HH:mm", CultureInfo.InvariantCulture)
            : m.ToString("d MMM", CultureInfo.InvariantCulture);
    }
}
