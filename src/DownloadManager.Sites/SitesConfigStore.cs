using System.Text.Json;
using System.Text.Json.Serialization;
using DownloadManager.Persistence;

namespace DownloadManager.Sites;

/// <summary>Options › Sites: the switch and "Use my own rclone" (remote-sites.md, "The switch"). Off until turned on.</summary>
public sealed record SitesConfig
{
    public bool Enabled { get; init; }
    public string? OwnRclonePath { get; init; }
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
