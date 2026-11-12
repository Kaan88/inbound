using System.Text.Json.Nodes;

namespace DownloadManager.Torrents.Tests;

/// <summary>Finds the built worker. Order: INBOUND_TORRENT_WORKER, then artifacts\torrent-worker\&lt;rid&gt;\ in the repository.</summary>
internal static class WorkerBinary
{
    public static readonly string? Path = Find();

    public static string Missing =>
        "The native torrent worker is not built (scripts\\build-torrent-worker.ps1), and INBOUND_TORRENT_WORKER is not set.";

    private static string? Find()
    {
        var fromEnv = Environment.GetEnvironmentVariable("INBOUND_TORRENT_WORKER");
        if (!string.IsNullOrEmpty(fromEnv)) return File.Exists(fromEnv) ? fromEnv : null;
        var rid = OperatingSystem.IsWindows() ? "win-x64"
            : System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "linux-arm64" : "linux-x64";
        var name = OperatingSystem.IsWindows() ? "inbound-torrent.exe" : "inbound-torrent";
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (!File.Exists(System.IO.Path.Combine(dir.FullName, "DownloadManager.slnx"))) continue;
            var candidate = System.IO.Path.Combine(dir.FullName, "artifacts", "torrent-worker", rid, name);
            return File.Exists(candidate) ? candidate : null;
        }
        return null;
    }
}

/// <summary>A test that needs the worker binary; skips, saying why, when it is missing.</summary>
public sealed class WorkerFactAttribute : FactAttribute
{
    public WorkerFactAttribute()
    {
        if (WorkerBinary.Path is null) Skip = WorkerBinary.Missing;
    }
}

/// <summary>One running worker plus what the tests read from it.</summary>
internal sealed class Worker : IAsyncDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _name;
    private readonly List<JsonObject> _seen = new();
    private readonly Dictionary<string, string> _blobs = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _changed = new(0, int.MaxValue);
    private readonly Task _pump;

    private Worker(TorrentWorkerProcess process, ITestOutputHelper output, string name)
    {
        Process = process;
        _output = output;
        _name = name;
        _pump = Task.Run(PumpAsync);
    }

    public TorrentWorkerProcess Process { get; }

    public static Worker Start(ITestOutputHelper output, string name, bool testHooks = false, string? dhtState = null)
    {
        var env = testHooks ? new Dictionary<string, string> { ["INBOUND_TORRENT_TEST_HOOKS"] = "1" } : null;
        var process = TorrentWorkerProcess.Start(WorkerBinary.Path!, dhtState, env, line => Log(output, name, line));
        return new Worker(process, output, name);
    }

    public string? Blob(string id)
    {
        lock (_seen) return _blobs.GetValueOrDefault(id);
    }

    public IReadOnlyList<JsonObject> Seen
    {
        get { lock (_seen) return _seen.ToArray(); }
    }

    /// <summary>Waits for an event that matches; returns it.</summary>
    public async Task<JsonObject> WaitForAsync(Func<JsonObject, bool> match, TimeSpan timeout, string what, int since = 0)
    {
        var deadline = DateTime.UtcNow + timeout;
        var index = since;
        while (true)
        {
            lock (_seen)
            {
                for (; index < _seen.Count; index++)
                    if (match(_seen[index])) return _seen[index];
            }
            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero) throw new TimeoutException($"{_name}: no {what} within {timeout.TotalSeconds:0} s.");
            if (Process.Exited.IsCompleted && !await _changed.WaitAsync(0)) throw new InvalidOperationException($"{_name}: the worker exited while waiting for {what}.");
            await _changed.WaitAsync(left < TimeSpan.FromMilliseconds(500) ? left : TimeSpan.FromMilliseconds(500));
        }
    }

    public Task<JsonObject> WaitForEventAsync(string name, string? id, TimeSpan timeout) =>
        WaitForAsync(e => e["event"]?.GetValue<string>() == name && (id is null || e["id"]?.GetValue<string>() == id), timeout, name);

    /// <summary>Waits for a progress row of torrent <paramref name="id"/> that matches.</summary>
    /// <summary>The number of events seen so far; pass it as <c>since</c> to wait only for later ones.</summary>
    public int Mark()
    {
        lock (_seen) return _seen.Count;
    }

    public async Task<JsonObject> WaitForProgressAsync(string id, Func<JsonObject, bool> match, TimeSpan timeout, string what, int since = 0)
    {
        JsonObject? row = null;
        await WaitForAsync(e =>
        {
            if (e["event"]?.GetValue<string>() != "progress") return false;
            foreach (var r in e["torrents"]!.AsArray())
                if (r!["id"]!.GetValue<string>() == id && match(r.AsObject())) { row = r.AsObject(); return true; }
            return false;
        }, timeout, what, since);
        return row!;
    }

    public async Task<int> ListenOnLoopbackAsync()
    {
        await Process.SendAsync("setListen", new JsonObject
        {
            ["port"] = 0, ["interfaces"] = new JsonArray("127.0.0.1"), ["dht"] = false, ["lsd"] = false, ["upnp"] = false, ["natpmp"] = false,
        });
        var stats = await WaitForAsync(e => e["event"]?.GetValue<string>() == "stats" && e["listenPort"] is not null, TimeSpan.FromSeconds(10), "listen port");
        return stats["listenPort"]!.GetValue<int>();
    }

    public async ValueTask DisposeAsync()
    {
        await Process.DisposeAsync();
        await _pump;
    }

    private async Task PumpAsync()
    {
        await foreach (var e in Process.Events.ReadAllAsync())
        {
            lock (_seen)
            {
                _seen.Add(e);
                if (e["event"]?.GetValue<string>() == "resumeBlob") _blobs[e["id"]!.GetValue<string>()] = e["data"]!.GetValue<string>();
            }
            if (e["event"]?.GetValue<string>() is not ("progress" or "resumeBlob") || Environment.GetEnvironmentVariable("INBOUND_TORRENT_TEST_VERBOSE") == "1") Log(_output, _name, "event " + Short(e));
            _changed.Release();
        }
        _changed.Release();
    }

    private static string Short(JsonObject e)
    {
        var s = e.ToJsonString();
        return s.Length > 300 ? s[..300] + "…" : s;
    }

    private static void Log(ITestOutputHelper output, string name, string line)
    {
        try { output.WriteLine($"[{name}] {line}"); }
        catch (InvalidOperationException) { } // the test has already finished
    }
}
