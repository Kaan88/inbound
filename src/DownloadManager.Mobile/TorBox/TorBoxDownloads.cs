using DownloadManager.Contracts;

namespace DownloadManager.Mobile.TorBox;

/// <summary>A TorBox job still being fetched on TorBox, as a row of Downloads (t3): "On TorBox · 37% · then to this phone".</summary>
public sealed record TorBoxDownloadRow(Guid Id, string Name, string Lead, string Subtitle, double Progress, DateTimeOffset CreatedAt);

/// <summary>Which TorBox jobs Downloads shows above its download rows, and how they read. Plain logic, no views.</summary>
public static class TorBoxDownloads
{
    public const string Lead = "On TorBox";

    /// <summary>
    /// A job TorBox is still fetching (or has queued): the OnTorBox group of the TorBox screen, minus a job waiting for approval
    /// (nothing was sent, so nothing is being fetched). Failed jobs stay on the TorBox screen; a started job is an ordinary download.
    /// </summary>
    public static bool Qualifies(RemoteJobDto job) =>
        TorBoxRows.IsTorBox(job) && !job.ApprovalNeeded && TorBoxRows.From(job, null, "").Group == TorBoxGroup.OnTorBox;

    /// <summary>The provider's progress as 0..1; anything unknown or out of range is clamped.</summary>
    public static double Progress(RemoteJobDto job) => double.IsNaN(job.Progress) ? 0 : Math.Clamp(job.Progress, 0, 1);

    /// <param name="hostName">The paired PC or server's name; null or empty for this phone.</param>
    public static string Subtitle(RemoteJobDto job, string? hostName)
    {
        var percent = (int)Math.Round(Progress(job) * 100);
        var where = string.IsNullOrWhiteSpace(hostName) ? "this phone" : hostName.Trim();
        return Lead + (percent > 0 ? $" · {percent}%" : "") + " · then to " + where;
    }

    /// <summary>Newest first; the optional search matches the name, as it does for download rows.</summary>
    public static IReadOnlyList<TorBoxDownloadRow> Rows(IEnumerable<RemoteJobDto> jobs, string? hostName, string? query = null)
    {
        var q = query?.Trim();
        return jobs.Where(Qualifies)
            .Select(j => new TorBoxDownloadRow(j.Id, Name(j), Lead, Subtitle(j, hostName), Progress(j), j.CreatedAt))
            .Where(r => string.IsNullOrEmpty(q) || r.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.CreatedAt).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The rows count in the All and Active chips only.</summary>
    public static bool ShownIn(DownloadFilter filter) => filter is DownloadFilter.All or DownloadFilter.Active;

    private static string Name(RemoteJobDto job) =>
        !string.IsNullOrWhiteSpace(job.Name) ? job.Name! : !string.IsNullOrWhiteSpace(job.DisplayHost) ? job.DisplayHost! : "TorBox item";
}
