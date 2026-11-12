using DownloadManager.Contracts;
using DownloadManager.Core;
using DownloadManager.Runtime.Remote;

namespace DownloadManager.Runtime;

/// <summary>
/// T10: a finished download that is a .torrent file (<see cref="DownloadJob.TorrentFileReady"/>) is added as a torrent
/// by its id (<see cref="TorrentAddRequest.FromJobId"/>): the Host reads the file itself, so no path travels, and it
/// removes the download and its file once the torrent is in the list.
/// </summary>
public sealed partial class DownloadManagerService
{
    internal const string NotATorrentFileJobText = "That download is not a .torrent file that can be added.";
    internal const string TorrentFileGoneText = "The .torrent file is no longer there.";
    public const string TorrentNoLongerListedText = "That torrent is no longer in the list, so the download stays.";

    /// <summary>Whether the job looks like a .torrent file by its type, name or address (<see cref="TorrentFileRule"/>). Nothing is read.</summary>
    internal bool LooksLikeTorrentFile(Guid id)
    {
        lock (_sync) return _jobs.TryGetValue(id, out var runtime) && TorrentFileRule.LooksLikeTorrent(runtime.Job);
    }

    /// <summary>The finished .torrent file of job <paramref name="id"/>, read and checked, or why it can't be added.</summary>
    private async Task<(TorrentInput? Input, string? Error)> ReadTorrentFileJobAsync(Guid id, CallerScope? caller, CancellationToken cancellationToken)
    {
        string path;
        lock (_sync)
        {
            if (!_jobs.TryGetValue(id, out var runtime) || runtime.CancelledForRemoval) return (null, "That download is no longer in the list.");
            var job = runtime.Job;
            if (job.State != DownloadState.Completed || !job.TorrentFileReady || job.Torrent is not null || job.FileRemovedAfterExtraction)
                return (null, NotATorrentFileJobText);
            path = job.FinalFilePath;
        }

        if (caller is not null && HeldBy(id, caller) == false) return (null, CallerScope.OutsideJobRefused);
        byte[] bytes;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return (null, TorrentFileGoneText);
            if (info.Length > TorrentMetadata.MaxFileBytes) return (null, "This .torrent file is too large to be a real one.");
            bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return (null, "That .torrent file can't be read.");
        }

        return TorrentFromBytes(bytes);
    }

    /// <summary>
    /// After an add from job <paramref name="id"/> that put the torrent in the list (new or already there): the download
    /// and its .torrent file go. The torrent kept the file's bytes, so nothing is lost. A removal that fails leaves the
    /// row, which can still be removed by hand; the add itself stands.
    /// </summary>
    private async Task RemoveTorrentFileJobAsync(Guid id, CallerScope? caller)
    {
        try
        {
            var removed = await RemoveAsync(id, deleteFile: true, CancellationToken.None, caller).ConfigureAwait(false);
            if (!removed.Ok) _log?.Invoke($"The .torrent download was added as a torrent but stays in the list: {removed.Error}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _log?.Invoke($"The .torrent download was added as a torrent but stays in the list: {ex.GetType().Name}");
        }
    }
}
