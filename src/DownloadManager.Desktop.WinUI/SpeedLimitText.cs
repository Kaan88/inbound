using System.Globalization;

namespace DownloadManager.Desktop.WinUI;

/// <summary>The words of Downloads &gt; Speed Limit, kept free of WinUI so they can be tested.</summary>
internal static class SpeedLimitText
{
    public const long MegabytePerSecond = 1024 * 1024;

    public static string Speed(long bytesPerSecond)
    {
        var megabytes = bytesPerSecond / (double)MegabytePerSecond;
        return megabytes >= 1
            ? $"{megabytes.ToString("0.##", CultureInfo.CurrentCulture)} MB/s"
            : $"{(bytesPerSecond / 1024d).ToString("0.#", CultureInfo.CurrentCulture)} KB/s";
    }

    /// <summary>True when the cap (0 for none) is one of the menu's own values, which then carries the mark.</summary>
    public static bool IsPreset(long bytesPerSecond, IEnumerable<long> presets) => bytesPerSecond == 0 || presets.Contains(bytesPerSecond);

    /// <summary>"Other…", or with the current cap in it when no preset matches, so the menu always marks something.</summary>
    public static string OtherLabel(long bytesPerSecond, IEnumerable<long> presets)
        => IsPreset(bytesPerSecond, presets) ? "Other…" : $"Other ({Speed(bytesPerSecond)})…";
}
