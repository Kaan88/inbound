using System.Globalization;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// UI.R31: the one way the desktop writes sizes and moments, so a file's size reads the same in the list, the tiles,
/// the dialogs and Statistics. Binary multiples with one decimal ("2.9 MB"); whole bytes below 1 KB.
/// </summary>
internal static class UiFormat
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string Bytes(long bytes)
    {
        var (number, unit) = Split(bytes);
        return $"{number} {unit}";
    }

    /// <summary>The number and its unit apart, so a tile can show the unit smaller.</summary>
    public static (string Number, string Unit) Split(long bytes)
    {
        bytes = Math.Max(0, bytes);
        var unit = UnitOf(bytes);
        return (Number(bytes, unit), Units[unit]);
    }

    /// <summary>Received of total in the total's unit: ("2.6", "/ 4.2 GB").</summary>
    public static (string Number, string Unit) Of(long received, long total)
    {
        total = Math.Max(0, total);
        var unit = UnitOf(total);
        return (Number(Math.Max(0, received), unit), $"/ {Number(total, unit)} {Units[unit]}");
    }

    /// <summary>A moment as the download list's Last Try Date column writes it: "Nov 09 19:19:28 2026".</summary>
    public static string Moment(DateTimeOffset value)
        => value.LocalDateTime.ToString("MMM dd HH:mm:ss yyyy", CultureInfo.InvariantCulture);

    private static int UnitOf(long bytes)
    {
        var unit = 0;
        double value = bytes;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit;
    }

    private static string Number(long bytes, int unit)
        => unit == 0
            ? bytes.ToString(CultureInfo.CurrentCulture)
            : (bytes / Math.Pow(1024, unit)).ToString("0.0", CultureInfo.CurrentCulture);
}
