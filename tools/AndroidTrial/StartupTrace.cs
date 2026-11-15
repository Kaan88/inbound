using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using SystemClock = Android.OS.SystemClock;

namespace AndroidTrial;

/// <summary>
/// ANDROID.2 start-up measurement: named marks in milliseconds since the process was forked (Zygote fork time, the same base as
/// <c>am start -W</c> uses). Cheap enough to stay on; written into the result JSON as "trace" by HostScenario.
/// </summary>
internal static class StartupTrace
{
    private static readonly ConcurrentQueue<(string Name, double Ms)> Marks = new();
    private static readonly ConcurrentDictionary<string, (int Count, double TotalMs)> Totals = new();

    public static double Now() => (SystemClock.ElapsedRealtimeNanos() - Android.OS.Process.StartElapsedRealtime * 1_000_000L) / 1_000_000.0;

    public static void Mark(string name)
    {
        Marks.Enqueue((name, Now()));
        // Methods the runtime compiled so far (JIT, or Mono's interpreter/AOT-miss path) and the time spent doing it, if the runtime reports it.
        try { Jit.Enqueue((name, System.Runtime.JitInfo.GetCompiledMethodCount(), System.Runtime.JitInfo.GetCompilationTime().TotalMilliseconds)); }
        catch (Exception) { /* not reported on this runtime */ }
    }

    private static readonly ConcurrentQueue<(string Name, long Methods, double Ms)> Jit = new();

    /// <summary>Adds one timed call to a running total (a store's Save, the checkpoint loads...).</summary>
    public static void Add(string name, double ms) => Totals.AddOrUpdate(name, (1, ms), (_, t) => (t.Count + 1, t.TotalMs + ms));

    public static async Task<T> TimeAsync<T>(string name, Func<Task<T>> body)
    {
        var begin = Now();
        Marks.Enqueue((name + ".begin", begin));
        try { return await body().ConfigureAwait(false); }
        finally { var end = Now(); Marks.Enqueue((name + ".end", end)); Add(name, end - begin); }
    }

    public static async Task TimeAsync(string name, Func<Task> body)
    {
        await TimeAsync<int>(name, async () => { await body().ConfigureAwait(false); return 0; }).ConfigureAwait(false);
    }

    public static JsonObject ToJson()
    {
        var marks = new JsonObject();
        foreach (var (name, ms) in Marks) marks[name] = Math.Round(ms, 1);
        var totals = new JsonObject();
        foreach (var (name, t) in Totals) totals[name] = new JsonObject { ["count"] = t.Count, ["totalMs"] = Math.Round(t.TotalMs, 1) };
        var jit = new JsonObject();
        foreach (var (name, methods, ms) in Jit) jit[name] = new JsonObject { ["methods"] = methods, ["ms"] = Math.Round(ms, 1) };
        return new JsonObject { ["marks"] = marks, ["totals"] = totals, ["jit"] = jit };
    }
}
