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
}

public static class JobListFile
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static string Serialize(IEnumerable<JobDto> jobs)
    {
        var entries = jobs
            // A provider's file is reached through the account, so its reference means nothing in a list.
            .Where(job => !string.IsNullOrWhiteSpace(job.Url) && !job.IsRemote)
            .Select(job => new JobListEntry
            {
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
