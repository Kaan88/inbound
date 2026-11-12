using System.Globalization;
using System.Text;
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
/// <remarks>
/// UI.B31: an extension keeps no connection, so one in use before 0.7.2 (whose native host named no browser) was unknown
/// until it next called. Given the logs folder, the Host reads its own native-host logs once per state folder (see
/// <see cref="NativeHostLogSightings"/>) and adds what they show; the file then holds <see cref="LogsReadKey"/>, so it is
/// never done again. An older Host reading the file leaves that name out, as it does any name that isn't a browser.
/// </remarks>
public sealed class ExtensionSightings
{
    public const string FileName = "extensions-seen.json";

    /// <summary>UI.B31: in the file, when the native-host logs were read; never in <see cref="Snapshot"/>.</summary>
    public const string LogsReadKey = "logsRead";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _path;
    private readonly string? _nativeHostLogs;
    private readonly Action<string>? _warn;
    private readonly Func<DateTimeOffset> _now;
    private readonly object _sync = new();
    private Dictionary<string, DateTimeOffset>? _seen;
    private DateTimeOffset? _logsRead;

    /// <param name="nativeHostLogs">
    /// UI.B31: the folder the native host logs to (<c>native-host-yyyyMMdd.log</c>), read once for sightings from before
    /// the native host named its browser; null reads none.
    /// </param>
    public ExtensionSightings(string stateDirectory, Action<string>? warn = null, Func<DateTimeOffset>? now = null,
        string? nativeHostLogs = null)
    {
        _path = Path.Combine(stateDirectory, FileName);
        _warn = warn;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _nativeHostLogs = nativeHostLogs;
    }

    /// <summary>
    /// A copy of what has been seen: browser (<see cref="ExtensionBrowsers"/>, or <see cref="ExtensionBrowsers.Chromium"/>
    /// or <see cref="ExtensionBrowsers.Unknown"/>) to the last time it was noted.
    /// </summary>
    public Dictionary<string, DateTimeOffset> Snapshot()
    {
        lock (_sync) return new Dictionary<string, DateTimeOffset>(Load());
    }

    /// <summary>
    /// Notes that <paramref name="browser"/>'s extension reached the Host now. Names that aren't
    /// <see cref="ExtensionBrowsers.IsSighting"/> are ignored.
    /// </summary>
    /// <returns>True when the list changed: this browser is new, or this is its first time today (UTC).</returns>
    public bool Note(string? browser)
    {
        if (!ExtensionBrowsers.IsSighting(browser)) return false;
        lock (_sync)
        {
            var seen = Load();
            var now = _now();
            if (seen.TryGetValue(browser!, out var last) && last.UtcDateTime.Date == now.UtcDateTime.Date) return false;
            seen[browser!] = now;
            Save(seen);
            return true;
        }
    }

    private void Save(Dictionary<string, DateTimeOffset> seen)
    {
        var stored = new Dictionary<string, DateTimeOffset>(seen, StringComparer.Ordinal);
        if (_logsRead is { } read) stored[LogsReadKey] = read;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            WebAuth.AtomicFile.Write(_path, JsonSerializer.SerializeToUtf8Bytes(stored, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _warn?.Invoke($"Couldn't save which browser extensions have connected: {ex.GetType().Name}");
        }
    }

    private Dictionary<string, DateTimeOffset> Load()
    {
        if (_seen is not null) return _seen;
        _seen = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var readable = true;
        try
        {
            if (File.Exists(_path) &&
                JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(File.ReadAllBytes(_path), Json) is { } stored)
            {
                foreach (var (browser, when) in stored)
                {
                    if (ExtensionBrowsers.IsSighting(browser)) _seen[browser] = when;
                    else if (browser == LogsReadKey) _logsRead = when;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            readable = false;
            _warn?.Invoke($"Couldn't read which browser extensions have connected: {ex.GetType().Name}");
        }

        // UI.B31: once per state folder. A file that can't be read is not overwritten here; the next sighting replaces it.
        if (readable && _logsRead is null && _nativeHostLogs is { } logs) SeedFromLogs(logs);
        return _seen;
    }

    private void SeedFromLogs(string logs)
    {
        var now = _now();
        Dictionary<string, DateTimeOffset> found;
        try
        {
            found = NativeHostLogSightings.Read(logs, now);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Never fails the Host: the logs only add what the extension would say at its next call anyway.
            _warn?.Invoke($"Couldn't read the native host's logs for earlier extensions: {ex.GetType().Name}");
            found = [];
        }

        foreach (var (browser, when) in found)
        {
            if (!_seen!.TryGetValue(browser, out var known) || known < when) _seen[browser] = when;
        }

        _logsRead = now;
        Save(_seen!);
    }
}

/// <summary>
/// UI.B31: the extensions the native host's own logs show, for <see cref="ExtensionSightings"/>. A native host logs
/// <c>&lt;time&gt; Native host started. Caller=chromium|firefox PID=…</c> and, from 0.7.2, <c>&lt;time&gt; Browser=chrome|edge|firefox</c>
/// right after. A Browser line names the browser; without one, Caller=firefox is Firefox and Caller=chromium is Chrome or
/// Edge (<see cref="ExtensionBrowsers.Chromium"/>). Each browser gets the time of its last line. Bounded: only the files
/// of the last <see cref="Days"/> days, of each only its last <see cref="MaxBytesPerFile"/> bytes, and newest first to
/// <see cref="MaxTotalBytes"/> in all.
/// </summary>
public static class NativeHostLogSightings
{
    public const int Days = 30;
    public const int MaxBytesPerFile = 2 * 1024 * 1024;
    public const int MaxTotalBytes = 8 * 1024 * 1024;
    private const string FilePrefix = "native-host-";
    private const string Started = "Native host started. Caller=";
    private const string BrowserIs = "Browser=";

    /// <summary>What the logs in <paramref name="directory"/> show; nothing when it doesn't exist.</summary>
    public static Dictionary<string, DateTimeOffset> Read(string directory, DateTimeOffset now)
    {
        var found = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        if (!Directory.Exists(directory)) return found;
        var oldest = now.UtcDateTime.Date.AddDays(-Days);
        var files = new List<(DateTime Day, string Path)>();
        foreach (var file in Directory.EnumerateFiles(directory, FilePrefix + "*.log"))
        {
            var day = Path.GetFileNameWithoutExtension(file)[FilePrefix.Length..];
            if (DateTime.TryParseExact(day, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) &&
                date >= oldest)
                files.Add((date, file));
        }

        // Newest first, until MaxTotalBytes have been read.
        long budget = MaxTotalBytes;
        foreach (var (_, file) in files.OrderByDescending(f => f.Day))
        {
            if (budget <= 0) break;
            try
            {
                var lines = ReadTail(file, (int)Math.Min(MaxBytesPerFile, budget), out var read);
                budget -= read;
                Merge(found, Parse(lines));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A file another native host is writing, or one that went: the others still count.
            }
        }

        return found;
    }

    /// <summary>
    /// The sightings in one log's lines. Several native hosts can write to one file at once, so a Browser line belongs to
    /// the latest start before it, within <see cref="BrowserLineWithin"/>, that no other Browser line has taken. Lines that
    /// are neither are passed over.
    /// </summary>
    public static Dictionary<string, DateTimeOffset> Parse(IEnumerable<string> lines)
    {
        var found = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var starts = new List<(string Caller, DateTimeOffset When, bool Named)>();
        foreach (var line in lines)
        {
            var space = line.IndexOf(' ');
            if (space <= 0 || !DateTimeOffset.TryParse(line.AsSpan(0, space), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var when))
                continue;
            var text = line.AsSpan(space + 1);
            if (text.StartsWith(Started, StringComparison.Ordinal))
            {
                var caller = text[Started.Length..];
                var end = caller.IndexOf(' ');
                starts.Add((caller[..(end < 0 ? caller.Length : end)].ToString(), when, false));
            }
            else if (text.StartsWith(BrowserIs, StringComparison.Ordinal))
            {
                var browser = text[BrowserIs.Length..].Trim().ToString();
                var owner = -1;
                for (var i = starts.Count - 1; i >= 0; i--)
                {
                    if (starts[i].Named || starts[i].When > when) continue;
                    if (when - starts[i].When <= BrowserLineWithin) owner = i;
                    break;
                }

                if (owner >= 0) starts[owner] = starts[owner] with { Named = ExtensionBrowsers.IsKnown(browser) };
                if (ExtensionBrowsers.IsKnown(browser)) Add(found, browser, when);
            }
        }

        // A start no Browser line named: from before 0.7.2, or one that said "(unknown)".
        foreach (var start in starts)
        {
            if (start.Named) continue;
            // "(refused)" and anything else name no extension of ours.
            if (start.Caller == "firefox") Add(found, ExtensionBrowsers.Firefox, start.When);
            else if (start.Caller == "chromium") Add(found, ExtensionBrowsers.Chromium, start.When);
        }

        return found;
    }

    /// <summary>How long after its start a native host's Browser line can come.</summary>
    public static readonly TimeSpan BrowserLineWithin = TimeSpan.FromSeconds(10);

    private static void Add(Dictionary<string, DateTimeOffset> found, string browser, DateTimeOffset when)
    {
        if (!found.TryGetValue(browser, out var known) || known < when) found[browser] = when;
    }

    private static void Merge(Dictionary<string, DateTimeOffset> into, Dictionary<string, DateTimeOffset> from)
    {
        foreach (var (browser, when) in from) Add(into, browser, when);
    }

    private static List<string> ReadTail(string file, int maxBytes, out long read)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var skipFirst = false;
        read = Math.Min(stream.Length, maxBytes);
        if (stream.Length > maxBytes)
        {
            stream.Seek(-maxBytes, SeekOrigin.End);
            // The first line read is likely cut.
            skipFirst = true;
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        var lines = new List<string>();
        if (skipFirst) reader.ReadLine();
        while (reader.ReadLine() is { } line) lines.Add(line);
        return lines;
    }
}
