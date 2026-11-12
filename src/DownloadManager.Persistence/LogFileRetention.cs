using System.Globalization;

namespace DownloadManager.Persistence;

/// <summary>
/// Keeps the daily log files (<c>&lt;prefix&gt;-yyyyMMdd.log</c>) from growing without end: files older than
/// <see cref="KeepDays"/> are deleted when a logger starts, and one day's file stops at <see cref="MaxDailyBytes"/>
/// with a closing line that says so.
/// </summary>
public static class LogFileRetention
{
    public const int KeepDays = 30;
    public const long MaxDailyBytes = 20L * 1024 * 1024;

    /// <summary>The last line a full day's file gets.</summary>
    public const string CapReachedLine = "Log file reached its size limit for today; further lines are dropped until tomorrow.";

    /// <summary>
    /// Deletes <c>&lt;prefix&gt;-yyyyMMdd.log</c> files whose date is more than <paramref name="keepDays"/> days
    /// before <paramref name="today"/>. Only names that match exactly are touched; a failure is ignored.
    /// </summary>
    public static int DeleteOld(string directory, string prefix, DateTime today, int keepDays = KeepDays)
    {
        var deleted = 0;
        try
        {
            if (!Directory.Exists(directory)) return 0;
            var cutoff = today.Date.AddDays(-keepDays);
            foreach (var path in Directory.EnumerateFiles(directory, prefix + "-*.log", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileNameWithoutExtension(path);
                if (name.Length != prefix.Length + 9 || name[prefix.Length] != '-') continue;
                if (!DateTime.TryParseExact(name.AsSpan(prefix.Length + 1), "yyyyMMdd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var date))
                    continue;
                if (date >= cutoff) continue;
                try
                {
                    File.Delete(path);
                    deleted++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Another process may hold it; the next start tries again.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return deleted;
    }

    /// <summary>The current size of a day's file, or 0 when it does not exist or cannot be read.</summary>
    public static long CurrentSize(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
