using System.Text.Json;
using DownloadManager.Contracts;

namespace DownloadManager.Runtime;

/// <summary>
/// UI.R41: which browsers' extensions have reached this Host through the native host, and the last day each did, kept in
/// <c>extensions-seen.json</c> in the state folder so the window can say so (Options, its one-time hint) after a restart.
/// The file is written only when a browser is new or its day has changed, so a busy extension costs at most one small
/// write per browser per day. A file that can't be read counts as none seen; one that can't be written keeps the
/// sighting for this run.
/// </summary>
public sealed class ExtensionSightings
{
    public const string FileName = "extensions-seen.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _path;
    private readonly Action<string>? _warn;
    private readonly Func<DateTimeOffset> _now;
    private readonly object _sync = new();
    private Dictionary<string, DateTimeOffset>? _seen;

    public ExtensionSightings(string stateDirectory, Action<string>? warn = null, Func<DateTimeOffset>? now = null)
    {
        _path = Path.Combine(stateDirectory, FileName);
        _warn = warn;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>A copy of what has been seen: browser (<see cref="ExtensionBrowsers"/>) to the last time it was noted.</summary>
    public Dictionary<string, DateTimeOffset> Snapshot()
    {
        lock (_sync) return new Dictionary<string, DateTimeOffset>(Load());
    }

    /// <summary>Notes that <paramref name="browser"/>'s extension reached the Host now. Unknown names are ignored.</summary>
    /// <returns>True when this browser had never been seen before.</returns>
    public bool Note(string? browser)
    {
        if (!ExtensionBrowsers.IsKnown(browser)) return false;
        lock (_sync)
        {
            var seen = Load();
            var now = _now();
            var isNew = !seen.TryGetValue(browser!, out var last);
            if (!isNew && last.UtcDateTime.Date == now.UtcDateTime.Date) return false;
            seen[browser!] = now;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                WebAuth.AtomicFile.Write(_path, JsonSerializer.SerializeToUtf8Bytes(seen, Json));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _warn?.Invoke($"Couldn't save which browser extensions have connected: {ex.GetType().Name}");
            }

            return isNew;
        }
    }

    private Dictionary<string, DateTimeOffset> Load()
    {
        if (_seen is not null) return _seen;
        _seen = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        try
        {
            if (File.Exists(_path) &&
                JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(File.ReadAllBytes(_path), Json) is { } stored)
            {
                foreach (var (browser, when) in stored)
                    if (ExtensionBrowsers.IsKnown(browser)) _seen[browser] = when;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _warn?.Invoke($"Couldn't read which browser extensions have connected: {ex.GetType().Name}");
        }

        return _seen;
    }
}
