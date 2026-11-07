using DownloadManager.Contracts;
using DownloadManager.Core.Providers;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// What every window and list shows of a job's address, through <see cref="ShownAddress"/>, so that a direct
/// provider link reads as its site everywhere and never as a link with its key or the account's file in it.
/// </summary>
internal static class JobAddress
{
    /// <summary>A provider's file has no address of its own, only a reference to the account's item.</summary>
    public static bool HasAddress(JobDto job)
        => !job.IsRemote && !job.Url.StartsWith("remote://", StringComparison.OrdinalIgnoreCase);

    public static bool IsHidden(JobDto job) => ShownAddress.IsHidden(job.Url, job.AddressHidden);

    /// <summary>The address as it may be shown or copied. Empty when the job has none.</summary>
    public static string Shown(JobDto job) => HasAddress(job) ? ShownAddress.For(job.Url, job.AddressHidden) : string.Empty;

    public static string Label(JobDto job) => ShownAddress.Label(job.Url, job.AddressHidden);

    /// <summary>The row's second line: what went wrong, or else where it comes from.</summary>
    public static string Detail(JobDto job)
        => job.Failure?.Message is { Length: > 0 } failure ? failure : Shown(job);

    /// <summary>The Description column: the user's words, or the page the download came from.</summary>
    public static string? Own(JobDto job)
        => job.Description ?? (string.IsNullOrWhiteSpace(job.PageUrl) ? job.PageUrl : ShownAddress.For(job.PageUrl));

    /// <summary>The refresh box starts empty for a hidden address, since what the list holds can't download anything.</summary>
    public static string RefreshStart(JobDto job) => IsHidden(job) ? string.Empty : job.Url;
}
