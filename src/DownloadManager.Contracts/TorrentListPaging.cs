namespace DownloadManager.Contracts;

/// <summary>
/// Keeps <c>torrent.listEntries</c> replies and <c>torrent.import</c> requests under <see cref="Protocol.MaxFrameBytes"/>:
/// a list with many or large .torrent files goes in several messages. A .torrent too large for one message on its own
/// is left out, and its magnet stands for it.
/// </summary>
public static class TorrentListPaging
{
    /// <summary>The most characters one page carries, half a frame: room for the envelope and escaped text.</summary>
    public const int PageChars = Protocol.MaxFrameBytes / 2;

    /// <summary>An upper bound of the characters an entry takes in JSON (text may be escaped to six characters each).</summary>
    public static long SizeOf(TorrentListEntry? torrent)
        => torrent is null ? 0
            : (torrent.Data?.Length ?? 0) + 6L * ((torrent.Magnet?.Length ?? 0) + (torrent.Name?.Length ?? 0) + torrent.InfoHash.Length) +
              12L * (torrent.FileIndexes?.Count ?? 0) + 256;

    public static long SizeOf(JobListEntry entry)
        => SizeOf(entry.Torrent) + 6L * (entry.Url.Length + (entry.FileName?.Length ?? 0) + (entry.DestinationDirectory?.Length ?? 0) +
           (entry.Description?.Length ?? 0) + (entry.CategoryId?.Length ?? 0) + (entry.ExpectedSha256?.Length ?? 0)) + 512;

    /// <summary>The entry itself, or a copy without its .torrent when that alone would not fit in a page.</summary>
    public static TorrentListEntry Fitting(TorrentListEntry torrent)
        => SizeOf(torrent) <= PageChars ? torrent
            : new TorrentListEntry { InfoHash = torrent.InfoHash, Name = torrent.Name, Magnet = torrent.Magnet, FileIndexes = torrent.FileIndexes };

    /// <summary>The entries of an import in groups that each fit one <c>torrent.import</c> request, in order.</summary>
    public static List<List<JobListEntry>> Chunk(IEnumerable<JobListEntry> entries)
    {
        var chunks = new List<List<JobListEntry>>();
        var current = new List<JobListEntry>();
        long size = 0;
        foreach (var original in entries)
        {
            var entry = original;
            if (entry.Torrent is { } torrent && !ReferenceEquals(Fitting(torrent), torrent))
            {
                entry = Copy(entry);
                entry.Torrent = Fitting(torrent);
            }

            var entrySize = SizeOf(entry);
            if (current.Count > 0 && size + entrySize > PageChars)
            {
                chunks.Add(current);
                current = [];
                size = 0;
            }

            current.Add(entry);
            size += entrySize;
        }

        if (current.Count > 0) chunks.Add(current);
        return chunks;
    }

    /// <summary>Adds one request's result to the total of an import sent in several.</summary>
    public static void AddTo(TorrentImportResult total, TorrentImportResult part)
    {
        total.Added += part.Added;
        total.Skipped += part.Skipped;
        total.Failed += part.Failed;
        total.SkipReason ??= part.SkipReason;
        total.FirstError ??= part.FirstError;
    }

    private static JobListEntry Copy(JobListEntry entry) => new()
    {
        Url = entry.Url,
        FileName = entry.FileName,
        CategoryId = entry.CategoryId,
        DestinationDirectory = entry.DestinationDirectory,
        Description = entry.Description,
        QueueId = entry.QueueId,
        RequestedConnections = entry.RequestedConnections,
        StartMode = entry.StartMode,
        ConflictPolicy = entry.ConflictPolicy,
        ExpectedSha256 = entry.ExpectedSha256,
        Torrent = entry.Torrent,
    };
}
