using System.Text.Json;
using System.Text.Json.Serialization;
using DownloadManager.Persistence;

namespace DownloadManager.Sites;

/// <summary>Options › Sites: the switch and "Use my own rclone" (remote-sites.md, "The switch"). Off until turned on.</summary>
public sealed record SitesConfig
{
    public bool Enabled { get; init; }
    public string? OwnRclonePath { get; init; }

    /// <summary>"Update rclone automatically": on by default (owner, 2026-11-15).</summary>
    public bool AutoUpdateRclone { get; init; } = true;

    /// <summary>"Transfers at once": files moving at the same time across all sites, apart from "downloads at once" (owner, 2026-11-15).</summary>
    public int MaxTransfers { get; init; } = DefaultMaxTransfers;

    public const int DefaultMaxTransfers = 3;
    public const int MostTransfers = 10;

    /// <summary><see cref="MaxTransfers"/> kept between 1 and <see cref="MostTransfers"/>, whatever the file says.</summary>
    public int TransfersAtOnce => Math.Clamp(MaxTransfers, 1, MostTransfers);
}

[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SitesConfig))]
internal sealed partial class SitesConfigJsonContext : JsonSerializerContext;

/// <summary><c>&lt;state&gt;/sites/config.json</c>, written atomically. A damaged file reads as off, and says so.</summary>
public sealed class SitesConfigStore(string stateDirectory)
{
    private readonly string _path = Path.Combine(stateDirectory, "sites", "config.json");

    public SitesConfig Load(out string? problem)
    {
        problem = null;
        if (!File.Exists(_path)) return new SitesConfig();
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllBytes(_path), SitesConfigJsonContext.Default.SitesConfig) ?? new SitesConfig();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            problem = $"Sites settings could not be read ({e.GetType().Name}); Sites stays off until it is turned on again.";
            return new SitesConfig();
        }
    }

    public Task SaveAsync(SitesConfig config, CancellationToken cancellationToken = default) =>
        AtomicFile.WriteBytesAsync(_path, JsonSerializer.SerializeToUtf8Bytes(config, SitesConfigJsonContext.Default.SitesConfig), cancellationToken);
}
