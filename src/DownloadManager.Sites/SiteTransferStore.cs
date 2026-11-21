using System.Text.Json;
using System.Text.Json.Serialization;
using DownloadManager.Persistence;

namespace DownloadManager.Sites;

/// <summary>The values of <see cref="SiteTransferRecord.Direction"/>.</summary>
public static class SiteTransferDirections
{
    public const string Download = "download";
    public const string Upload = "upload";
}

/// <summary>What a file does when its target is already there, chosen once for the batch.</summary>
public static class SiteTransferConflicts
{
    public const string Skip = "skip";
    public const string Replace = "replace";

    /// <summary>The new file gets a free name such as <c>report (2).pdf</c>.</summary>
    public const string KeepBoth = "keepBoth";

    public static bool IsKnown(string? value) => value is Skip or Replace or KeepBoth;
}

/// <summary>The values of <see cref="SiteTransferRecord.State"/>.</summary>
public static class SiteTransferStates
{
    /// <summary>Its files are moving, or waiting for a free place under "transfers at once".</summary>
    public const string Running = "running";

    /// <summary>Stopped by the user, or by Sites being turned off; Resume starts the files not done, each from zero.</summary>
    public const string Paused = "paused";

    /// <summary>Every file is done or skipped.</summary>
    public const string Done = "done";

    /// <summary>Nothing is left to try and at least one file failed, or the batch was stopped by a problem (a changed host key).</summary>
    public const string Failed = "failed";

    public const string Canceled = "canceled";

    public static bool IsFinished(string state) => state is Done or Failed or Canceled;
}

/// <summary>The values of <see cref="SiteTransferItem.State"/>.</summary>
public static class SiteTransferItemStates
{
    public const string Waiting = "waiting";
    public const string Running = "running";
    public const string Done = "done";
    public const string Skipped = "skipped";
    public const string Failed = "failed";
}

/// <summary>
/// One file of a batch. <see cref="Path"/> is relative to the batch's source folder with <c>/</c> separators; the target
/// is the same relative path in the target folder unless <see cref="Target"/> names another (Keep both).
/// </summary>
public sealed class SiteTransferItem
{
    public string Path { get; set; } = "";

    /// <summary>Bytes, as listed when the batch was made; -1 when unknown.</summary>
    public long Size { get; set; } = -1;

    public string State { get; set; } = SiteTransferItemStates.Waiting;

    /// <summary>The relative target when it is not <see cref="Path"/>.</summary>
    public string? Target { get; set; }

    public int Tries { get; set; }

    /// <summary>Why the last try failed, in plain words; never a secret (rclone's text is redacted before it gets here).</summary>
    public string? Error { get; set; }

    /// <summary>A failed try waits until then before the next.</summary>
    public DateTimeOffset? NotBefore { get; set; }

    /// <summary>An empty folder to make at the target (nothing is copied); <see cref="Size"/> is 0.</summary>
    public bool Folder { get; set; }
}

/// <summary>
/// One user action that moves files between a site and this PC (remote-sites.md, "Transfers"): stored as
/// <c>sites/transfers/{id}.json</c> before anything moves, so a Host restart picks it up where it was.
/// </summary>
public sealed class SiteTransferRecord
{
    public bool SafeSingleDownload { get; init; }
    /// <summary>The Host may have left the public file incomplete; never treat it as an ordinary skip.</summary>
    public bool PublicationPending { get; set; }
    /// <summary>
    /// A safe single download's staging file holds the whole file and only publication is left, so a run that had to wait
    /// publishes it instead of downloading it again (review M2).
    /// </summary>
    public bool StagedComplete { get; set; }

    public const int SafeDownloadSchemaVersion = 2;
    public const int CurrentSchemaVersion = SafeDownloadSchemaVersion;

    public int SchemaVersion { get; set; } = 1;
    public string Id { get; set; } = "";
    public string SiteId { get; set; } = "";
    public string SiteName { get; set; } = "";
    public string Direction { get; set; } = SiteTransferDirections.Download;

    /// <summary>The folder on the server, absolute (<see cref="SitePaths.Normalize"/>): the source of a download, the target of an upload.</summary>
    public string RemoteFolder { get; set; } = "/";

    /// <summary>The folder on this PC, fully qualified: the target of a download, the source of an upload.</summary>
    public string LocalFolder { get; set; } = "";

    public string Conflict { get; set; } = SiteTransferConflicts.Skip;
    public string State { get; set; } = SiteTransferStates.Running;

    /// <summary>A <c>SiteProblems</c> value when the batch was stopped by a problem that needs its own words (a changed host key).</summary>
    public string? Problem { get; set; }

    /// <summary>Why the batch stopped or was paused, when not by the user.</summary>
    public string? Message { get; set; }

    /// <summary>
    /// The batch may have been cut without rclone being able to tidy up (the Host was killed while it ran, or rclone
    /// crashed): rclone's <c>.partial</c> files for its unfinished files are looked for and removed before any of them runs
    /// again. Which file was moving is not saved, so every unfinished file's folder is looked at once.
    /// </summary>
    public bool SweepPending { get; set; }

    public List<SiteTransferItem> Items { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}

[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SiteTransferRecord))]
internal sealed partial class SiteTransfersJsonContext : JsonSerializerContext;

/// <summary>
/// The batches, one file each under <c>&lt;state&gt;/sites/transfers/</c>, written atomically (temp file, flush, replace).
/// A damaged or too-new file is reported and left in place. Only metadata lives here: the files themselves go straight to
/// their target folder.
/// </summary>
public sealed class SiteTransferStore
{
    private readonly string _folder;
    private readonly Action<string>? _log;

    public SiteTransferStore(string stateDirectory, Action<string>? log = null)
    {
        _folder = Path.Combine(stateDirectory, "sites", "transfers");
        _log = log;
    }

    public string Folder => _folder;

    public static string NewId() => Guid.NewGuid().ToString("N");

    public static byte[] Serialize(SiteTransferRecord record) =>
        JsonSerializer.SerializeToUtf8Bytes(record, SiteTransfersJsonContext.Default.SiteTransferRecord);

    /// <summary>Writes bytes made by <see cref="Serialize"/> for the batch <paramref name="id"/>.</summary>
    public Task WriteAsync(string id, byte[] bytes, CancellationToken cancellationToken = default) =>
        AtomicFile.WriteBytesAsync(PathFor(id), bytes, cancellationToken);

    public void Delete(string id)
    {
        var path = PathFor(id);
        if (File.Exists(path)) File.Delete(path);
    }

    public IReadOnlyList<SiteTransferRecord> LoadAll()
    {
        if (!Directory.Exists(_folder)) return [];
        var records = new List<SiteTransferRecord>();
        foreach (var file in Directory.EnumerateFiles(_folder, "*.json"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (!SiteStore.IsId(name)) continue;
            try
            {
                var record = JsonSerializer.Deserialize(File.ReadAllBytes(file), SiteTransfersJsonContext.Default.SiteTransferRecord);
                if (record is null) continue;
                if (record.SchemaVersion > SiteTransferRecord.CurrentSchemaVersion)
                {
                    _log?.Invoke($"Transfer file {name}.json is from a newer version of Inbound; it is left as it is.");
                    continue;
                }

                if (record.Id != name || !SiteStore.IsId(record.SiteId) || record.Items is null)
                {
                    _log?.Invoke($"Transfer file {name}.json does not describe a transfer; it is left as it is.");
                    continue;
                }

                records.Add(record);
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                _log?.Invoke($"Transfer file {name}.json could not be read ({e.GetType().Name}); it is left as it is.");
            }
        }

        return [.. records.OrderBy(r => r.CreatedAt).ThenBy(r => r.Id, StringComparer.Ordinal)];
    }

    private string PathFor(string id)
    {
        if (!SiteStore.IsId(id)) throw new SiteStoreException("That is not a transfer id.");
        return Path.Combine(_folder, id + ".json");
    }
}
