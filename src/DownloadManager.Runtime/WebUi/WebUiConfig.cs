using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DownloadManager.Contracts;

namespace DownloadManager.Runtime.WebUi;

/// <summary>
/// The web UI's own settings (D8), in <c>webui.json</c> in the data folder: never part of <c>settings.json</c>, its
/// export or import, or <c>settings.update</c>, so neither a web session nor an imported file can turn it on. The
/// password is not here: it is the bridge's <c>auth.json</c> beside it.
/// </summary>
public sealed class WebUiConfig
{
    public int Version { get; set; } = 1;
    public bool Enabled { get; set; }
    public int Port { get; set; } = WebUiDefaults.Port;
    public List<string> AlsoReachableAs { get; set; } = [];

    public WebUiConfig Copy() => new() { Version = Version, Enabled = Enabled, Port = Port, AlsoReachableAs = [.. AlsoReachableAs] };

    /// <summary>Why this port can't be used, or null.</summary>
    public static string? PortProblem(int port) =>
        port is < WebUiDefaults.MinPort or > WebUiDefaults.MaxPort
            ? $"The port must be between {WebUiDefaults.MinPort.ToString(CultureInfo.CurrentCulture)} and {WebUiDefaults.MaxPort.ToString(CultureInfo.CurrentCulture)}."
            : null;

    /// <summary>At most this many extra names.</summary>
    public const int MaxNames = 16;

    /// <summary>
    /// The names as they are kept: trimmed, lower case, without repeats. Null with <paramref name="problem"/> when one
    /// isn't a host name (a scheme, a port, a path, an IP address, which needs no listing, or something else).
    /// </summary>
    public static List<string>? CleanNames(IEnumerable<string?> names, out string? problem)
    {
        problem = null;
        var clean = new List<string>();
        foreach (var given in names)
        {
            var name = given?.Trim().TrimEnd('.').ToLowerInvariant() ?? string.Empty;
            if (name.Length == 0) continue;
            if (name.Length > 253 || Uri.CheckHostName(name) != UriHostNameType.Dns || name.Contains('%'))
            {
                problem = $"\"{(name.Length > 60 ? name[..60] + "..." : name)}\" isn't a computer name. Type names such as mypc or mypc.home, without http:// or a port.";
                return null;
            }

            if (!clean.Contains(name, StringComparer.Ordinal)) clean.Add(name);
        }

        if (clean.Count > MaxNames)
        {
            problem = $"At most {MaxNames} names can be listed.";
            return null;
        }

        return clean;
    }
}

/// <summary>
/// Reads and writes <c>webui.json</c>. A write goes to a temporary file in the same folder, is flushed to disk and then
/// moved over the old one, so a kill at any point leaves one or the other. A file that can't be read counts as the
/// defaults (off): the safe side.
/// </summary>
public sealed class WebUiConfigStore
{
    public const string FileName = "webui.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public WebUiConfigStore(string dataDirectory) => Path = System.IO.Path.Combine(dataDirectory, FileName);

    public string Path { get; }

    /// <summary>The saved settings; the defaults when there are none, and when the file is damaged (<paramref name="problem"/> says so).</summary>
    public WebUiConfig Load(out string? problem)
    {
        problem = null;
        if (!File.Exists(Path)) return new WebUiConfig();
        try
        {
            var config = JsonSerializer.Deserialize<WebUiConfig>(File.ReadAllText(Path, Encoding.UTF8), Json) ?? new WebUiConfig();
            config.AlsoReachableAs = WebUiConfig.CleanNames(config.AlsoReachableAs ?? [], out _) ?? [];
            if (WebUiConfig.PortProblem(config.Port) is not null) config.Port = WebUiDefaults.Port;
            return config;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            problem = $"The web UI settings ({FileName}) could not be read ({ex.GetType().Name}); the web UI stays off.";
            return new WebUiConfig();
        }
    }

    public async Task SaveAsync(WebUiConfig config, CancellationToken cancellationToken = default)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(config, Json);
        var temp = Path + ".tmp";
        await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
        {
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, Path, overwrite: true);
    }
}
