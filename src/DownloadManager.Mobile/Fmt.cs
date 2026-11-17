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
}
