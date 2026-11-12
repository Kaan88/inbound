using System.Text.Json;

namespace DownloadManager.Contracts;

public sealed class JobListEntry
{
    public string Url { get; set; } = string.Empty;
    public string? FileName { get; set; }
    public string? CategoryId { get; set; }
    public string? DestinationDirectory { get; set; }
    public string? Description { get; set; }
    public int QueueId { get; set; }
    public int RequestedConnections { get; set; }
    public string StartMode { get; set; } = "later";
    public string ConflictPolicy { get; set; } = "rename";
    public string? ExpectedSha256 { get; set; }
    /// <summary>The content type the browser hand-off gave, sent again as <c>AddJobRequest.ContentType</c>, so a
    /// stream at an address without .m3u8 or .mpd is still a stream (review 1 N6), and fails as one since 0.7.2. Left out
    /// when there was none.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? ContentType { get; set; }
    /// <summary>A torrent job's own part; null for every other download. <see cref="Url"/> is then its magnet.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public TorrentListEntry? Torrent { get; set; }
}

/// <summary>A torrent in a saved list: added again through <c>torrent.import</c>, never as a plain address.</summary>
public sealed class TorrentListEntry
{
    public string InfoHash { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string? Magnet { get; set; }
    /// <summary>The .torrent file (base64) when the torrent was added from one and the Host still has it.</summary>
    public string? Data { get; set; }
    /// <summary>The files chosen, by index; null when every file is wanted or the file list was not known.</summary>
    public List<int>? FileIndexes { get; set; }
}

public static class JobListFile
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    /// <summary>An entry that is a torrent (from this list format, or a bare magnet in an older or hand-made list).</summary>
    public static bool IsTorrent(JobListEntry entry)
        => entry.Torrent is not null || entry.Url.TrimStart().StartsWith("magnet:", StringComparison.OrdinalIgnoreCase);

    /// <param name="torrents">Each torrent job's entry from <c>torrent.listEntries</c>; a torrent missing from it (an
    /// older Host) is saved with its magnet only.</param>
    public static string Serialize(IEnumerable<JobDto> jobs, IReadOnlyDictionary<Guid, TorrentListEntry>? torrents = null)
    {
        var entries = jobs
            // A provider's file is reached through the account, so its reference means nothing in a list.
            // A hidden address is a masked copy: it can't download anything, and it names a file on the account.
            .Where(job => !string.IsNullOrWhiteSpace(job.Url) && !job.IsRemote && !job.AddressHidden)
            .Select(job => new JobListEntry
            {
                Torrent = job.Torrent is not { } torrent ? null
                    : torrents?.GetValueOrDefault(job.Id) ?? new TorrentListEntry { InfoHash = torrent.InfoHash, Name = torrent.Name, Magnet = job.Url },
                Url = job.Url,
                FileName = job.FileName,
                CategoryId = job.CategoryId,
                DestinationDirectory = job.DestinationDirectory,
                Description = job.Description,
                QueueId = job.QueueId,
                RequestedConnections = job.RequestedConnections,
                StartMode = job.StartMode,
                ConflictPolicy = job.ConflictPolicy,
                ExpectedSha256 = job.ExpectedSha256,
                ContentType = job.Torrent is null ? job.HandoffContentType : null,
            })
            .ToList();
        return JsonSerializer.Serialize(entries, Options);
    }

    public static IReadOnlyList<JobListEntry> Deserialize(string json)
    {
        try
        {
            var entries = JsonSerializer.Deserialize<List<JobListEntry>>(json, Options);
            if (entries is null)
            {
                throw new FormatException("The download list is empty.");
            }

            return entries
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Url))
                .ToList();
        }
        catch (JsonException ex)
        {
            throw new FormatException($"The download list is not valid JSON: {ex.Message}", ex);
        }
    }
}
