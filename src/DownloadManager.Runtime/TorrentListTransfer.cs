using DownloadManager.Contracts;
using DownloadManager.Core;

namespace DownloadManager.Runtime;

/// <summary>Export and import of a download list's torrents (<c>torrent.listEntries</c>, <c>torrent.import</c>).</summary>
public sealed partial class DownloadManagerService
{
    public const string TorrentAlreadyListedText = "Already in the list.";

    /// <summary>
    /// What a saved list keeps of each torrent: its magnet, its .torrent when it came from one, and the files chosen. One
    /// page at a time, in job id order, kept under <see cref="TorrentListPaging.PageChars"/>.
    /// </summary>
    public async Task<TorrentListEntriesResult> GetTorrentListEntriesAsync(TorrentListEntriesRequest? request = null,
        CancellationToken cancellationToken = default, CallerScope? caller = null)
    {
        var after = request?.After;
        List<(Guid Id, string Url, TorrentJobInfo Info)> torrents;
        lock (_sync)
        {
            torrents = _jobs.Values
                .Where(runtime => runtime.Job.Torrent is not null && !runtime.CancelledForRemoval &&
                                  (after is null || runtime.Job.Id.CompareTo(after.Value) > 0))
                .Select(runtime => (runtime.Job.Id, runtime.Job.Url, runtime.Job.Torrent!.Clone()))
                .OrderBy(item => item.Id)
                .ToList();
        }

        var result = new TorrentListEntriesResult { Ok = true };
        long size = 0;
        Guid? last = null;
        foreach (var (id, url, info) in torrents)
        {
            if (caller is not null && HeldBy(id, caller) == false) continue;
            var data = info.Magnet is null ? await TorrentStore.LoadMetadataAsync(id, cancellationToken).ConfigureAwait(false) : null;
            var files = await TorrentStore.LoadFilesAsync(id, cancellationToken).ConfigureAwait(false);
            var entry = TorrentListPaging.Fitting(new TorrentListEntry
            {
                InfoHash = info.InfoHash,
                Name = info.Name,
                Magnet = info.Magnet ?? url,
                Data = data is null ? null : Convert.ToBase64String(data),
                // A magnet whose file list has not come yet keeps the choice it was imported with.
                FileIndexes = files is { Count: > 0 } && files.Any(file => !file.Wanted)
                    ? files.Where(file => file.Wanted).Select(file => file.Index).ToList()
                    : info.HasMetadata ? null : info.PendingFileIndexes,
            });
            var entrySize = TorrentListPaging.SizeOf(entry);
            if (last is not null && size + entrySize > TorrentListPaging.PageChars)
            {
                result.Next = last;
                break;
            }

            result.Entries[id] = entry;
            size += entrySize;
            last = id;
        }

        return result;
    }

    /// <summary>
    /// Adds an imported list's torrents as <c>torrent.add</c> would. One already in the list is skipped, and so is every one
    /// while torrents are off. A magnet saved with a choice of files gets that choice when its file list comes, or is asked
    /// for it when the list does not have every file the choice names.
    /// </summary>
    public async Task<TorrentImportResult> ImportTorrentsAsync(TorrentImportRequest request, CancellationToken cancellationToken = default, CallerScope? caller = null)
    {
        var result = new TorrentImportResult { Ok = true };
        if (TorrentsRefusal() is { } refused)
        {
            result.Skipped = request.Entries.Count;
            result.SkipReason = refused;
            return result;
        }

        foreach (var entry in request.Entries)
        {
            var torrent = entry.Torrent;
            var data = torrent?.Data;
            var chosen = torrent?.FileIndexes;
            var add = new TorrentAddRequest
            {
                Input = (string.IsNullOrWhiteSpace(torrent?.Magnet) ? entry.Url : torrent.Magnet).Trim(),
                Data = data,
                Destination = string.IsNullOrWhiteSpace(entry.DestinationDirectory) ? null : entry.DestinationDirectory,
                QueueId = entry.QueueId,
                StartMode = request.StartMode ?? entry.StartMode,
                Files = chosen is null ? "all" : string.IsNullOrWhiteSpace(data) ? "askWhenReady" : "chosen",
                FileIndexes = chosen,
                Description = entry.Description,
            };
            TorrentAddResult added;
            try
            {
                added = await AddTorrentAsync(add, cancellationToken, caller).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                added = new TorrentAddResult { Error = ex.Message };
            }

            if (added.Ok && added.Existing)
            {
                result.Skipped++;
                result.SkipReason ??= TorrentAlreadyListedText;
            }
            else if (added.Ok)
            {
                result.Added++;
            }
            else
            {
                result.Failed++;
                result.FirstError ??= added.Error;
            }
        }

        return result;
    }
}
