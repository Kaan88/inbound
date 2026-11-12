using System.Globalization;
using DownloadManager.Contracts;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// T2 seeding in the desktop window: the Options fields, the list's words for a seeding torrent and the Start seeding /
/// Stop seeding commands. Kept free of WinUI so they are tested (tests/DownloadManager.Core.Tests links this file).
/// </summary>
internal static class TorrentSeedRules
{
    public const double MostRatio = 100;
    public const double MostHours = 8760;

    /// <summary>The upload limit box's largest value, as the total speed limit's (KB/s).</summary>
    public const double MostUploadKilobytes = 10_000_000;

    /// <summary>An empty box counts as 0, which the Host reads as "no limit" for the ratio and the hours.</summary>
    public static double OrZero(double value) => double.IsNaN(value) ? 0 : value;

    /// <summary>
    /// Why the seeding fields can't be saved, or null: the Host's own rules and words (Runtime's TorrentSeeding), so the
    /// page says it before the Host refuses. Values as typed (KB/s for the limit); empty boxes are 0.
    /// </summary>
    public static string? Problem(double ratio, double hours, double uploadKilobytes)
    {
        ratio = OrZero(ratio);
        hours = OrZero(hours);
        uploadKilobytes = OrZero(uploadKilobytes);
        if (!double.IsFinite(ratio) || ratio is < 0 or > MostRatio)
            return "Enter a seeding ratio from 0 to 100 (0 is no ratio limit).";
        if (!double.IsFinite(hours) || hours is < 0 or > MostHours)
            return "Enter seeding hours from 0 to 8760 (0 is no time limit).";
        if (ratio == 0 && hours == 0)
            return "Seeding needs a ratio or a time limit; both can't be 0.";
        if (!double.IsFinite(uploadKilobytes) || uploadKilobytes < 0)
            return "The torrent upload limit can't be below 0.";
        if (uploadKilobytes > MostUploadKilobytes)
            return "Enter a torrent upload limit up to 10,000,000 KB/s, or leave it empty.";
        return null;
    }

    /// <summary>The box's KB/s as the setting's bytes per second; empty or 0 is 0 (the total speed limit applies).</summary>
    public static long UploadLimitBytes(double kilobytes)
        => double.IsFinite(kilobytes) && kilobytes > 0 ? (long)Math.Round(kilobytes * 1024) : 0;

    /// <summary>
    /// The upload limit to save: the saved bytes per second while the box still shows what it was opened with, so a limit
    /// that isn't a whole number of KB (set in the web UI or the settings file) isn't rounded by an untouched box.
    /// </summary>
    public static long UploadLimitToSave(double kilobytes, long savedBytesPerSecond)
    {
        var shown = UploadLimitShown(savedBytesPerSecond);
        var untouched = double.IsNaN(kilobytes) ? double.IsNaN(shown) : kilobytes.Equals(shown);
        return untouched ? Math.Max(0, savedBytesPerSecond) : UploadLimitBytes(kilobytes);
    }

    /// <summary>The setting's bytes per second as the box shows it: empty (NaN) for 0.</summary>
    public static double UploadLimitShown(long bytesPerSecond)
        => bytesPerSecond > 0 ? Math.Round(bytesPerSecond / 1024.0, 2) : double.NaN;

    // ---- The list ----

    /// <summary>The torrent finished and is shared with other peers now.</summary>
    public static bool IsSeeding(JobDto? job) => job is { State: "completed", Torrent.Seeding: true };

    /// <summary>"0.71": a ratio as the list and the details show it.</summary>
    public static string Ratio(double ratio)
        => (double.IsFinite(ratio) ? Math.Max(0, ratio) : 0).ToString("0.00", CultureInfo.CurrentCulture);

    /// <summary>The Status column of a seeding torrent: "Seeding · ratio 0.71"; null when it doesn't seed.</summary>
    public static string? StatusText(JobDto job)
        => IsSeeding(job) ? $"Seeding · ratio {Ratio(job.Torrent!.Ratio)}" : null;

    /// <summary>The Transfer rate column of a seeding torrent: "640 KB/s up", empty while nothing is sent; null when it doesn't seed.</summary>
    public static string? RateText(JobDto job)
    {
        if (!IsSeeding(job)) return null;
        var rate = job.Torrent!.UploadRate;
        return double.IsFinite(rate) && rate >= 1 ? $"{UiFormat.Bytes((long)rate)}/s up" : string.Empty;
    }

    /// <summary>
    /// Delete Completed's question for the <paramref name="completed"/> downloads it removes. A seeding torrent is
    /// completed too, so it goes with them; the question says that stops it seeding (review 4:L9). The web UI's
    /// deleteCompletedText says the same.
    /// </summary>
    public static string DeleteCompletedText(IReadOnlyCollection<JobDto> completed)
    {
        var text = $"Remove {completed.Count} completed item(s) from the list? The downloaded files will stay on disk.";
        var seeding = completed.Count(IsSeeding);
        return seeding switch
        {
            0 => text,
            1 => $"{text} 1 of them is a torrent that is seeding; removing it stops seeding.",
            _ => $"{text} {seeding} of them are torrents that are seeding; removing them stops seeding.",
        };
    }

    // ---- Start seeding and Stop seeding ----

    /// <summary>A finished torrent that doesn't seed now: Start seeding acts on it. The Host refuses one whose files
    /// aren't all in place and says so.</summary>
    public static bool CanStart(JobDto job) => job is { State: "completed", Torrent.Seeding: false };

    public static bool CanStop(JobDto job) => IsSeeding(job);

    /// <summary>The selected torrents Start seeding (<paramref name="on"/>) or Stop seeding acts on.</summary>
    public static List<Guid> Targets(IEnumerable<JobDto> selection, bool on)
        => selection.Where(job => on ? CanStart(job) : CanStop(job)).Select(job => job.Id).Distinct().ToList();

    /// <summary>
    /// Start seeding shows while torrents apply (offered and turned on: the Host refuses while they are off) and a
    /// selected torrent can start; Stop seeding shows while the Host offers torrents and a selected one seeds.
    /// </summary>
    public static (bool Start, bool Stop) Commands(IEnumerable<JobDto> selection, bool torrentsOffered, SettingsDto? settings)
    {
        var jobs = selection.ToList();
        return (TorrentRules.Applies(torrentsOffered, settings) && jobs.Any(CanStart), torrentsOffered && jobs.Any(CanStop));
    }

    /// <summary>What the status bar says after Start seeding or Stop seeding went to the Host.</summary>
    public static string Said(bool on, string? name, int done, int failed, string? firstError)
    {
        var reason = string.IsNullOrWhiteSpace(firstError) ? "the Host didn't say why." : firstError.Trim();
        if (failed == 0)
        {
            if (done == 1 && name is { Length: > 0 }) return on ? $"{name}: seeding." : $"{name}: seeding stopped.";
            return on ? $"{done} torrents seeding." : $"Seeding stopped for {done} torrents.";
        }

        var verb = on ? "start" : "stop";
        if (done == 0)
            return failed == 1 ? $"Seeding didn't {verb}: {reason}" : $"Seeding didn't {verb} for {failed} torrents: {reason}";
        var ok = on ? $"{done} seeding." : $"{done} stopped.";
        return failed == 1 ? $"{ok} 1 didn't {verb}: {reason}" : $"{ok} {failed} didn't {verb}: {reason}";
    }
}
