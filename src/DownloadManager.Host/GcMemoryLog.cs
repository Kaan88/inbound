using System.Diagnostics;
using System.Globalization;
using System.Runtime;

namespace DownloadManager.Host;

/// <summary>
/// Measurement aid for soak and GC-setting runs (PERF.2, PERF.3), off unless the environment variable
/// <c>DOWNLOADMANAGER_GC_LOG</c> names a CSV file. Once a minute it appends the managed heap and the
/// process memory as the runtime sees them, which an outside sampler cannot read. With the variable unset
/// nothing is created and no timer runs, so it costs an installed Host nothing.
/// </summary>
internal sealed class GcMemoryLog : IDisposable
{
    public const string Variable = "DOWNLOADMANAGER_GC_LOG";
    public const string Header =
        "utc,heapBytes,totalAllocatedBytes,committedBytes,fragmentedBytes,gen0,gen1,gen2,pauseTimePercent,privateBytes,handles,threads,isServerGc,latencyMode,conserveMemory";

    private readonly string _path;
    private readonly System.Threading.Timer _timer;

    private GcMemoryLog(string path, TimeSpan interval)
    {
        _path = path;
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            File.AppendAllText(path, Header + Environment.NewLine);
        }

        _timer = new System.Threading.Timer(_ => Write(), null, TimeSpan.Zero, interval);
    }

    public static GcMemoryLog? StartFromEnvironment(Action<string> log)
    {
        var path = Environment.GetEnvironmentVariable(Variable);
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var full = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            log($"GC memory log on: {full}");
            return new GcMemoryLog(full, TimeSpan.FromMinutes(1));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            log($"GC memory log could not start: {ex.GetType().Name}");
            return null;
        }
    }

    public static string FormatLine()
    {
        var info = GC.GetGCMemoryInfo(GCKind.Any);
        using var process = Process.GetCurrentProcess();
        var conserve = AppContext.GetData("System.GC.ConserveMemory")
            ?? Environment.GetEnvironmentVariable("DOTNET_GCConserveMemory") ?? "";
        return string.Join(',',
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            GC.GetTotalMemory(false).ToString(CultureInfo.InvariantCulture),
            GC.GetTotalAllocatedBytes(false).ToString(CultureInfo.InvariantCulture),
            info.TotalCommittedBytes.ToString(CultureInfo.InvariantCulture),
            info.FragmentedBytes.ToString(CultureInfo.InvariantCulture),
            GC.CollectionCount(0).ToString(CultureInfo.InvariantCulture),
            GC.CollectionCount(1).ToString(CultureInfo.InvariantCulture),
            GC.CollectionCount(2).ToString(CultureInfo.InvariantCulture),
            info.PauseTimePercentage.ToString("0.###", CultureInfo.InvariantCulture),
            process.PrivateMemorySize64.ToString(CultureInfo.InvariantCulture),
            process.HandleCount.ToString(CultureInfo.InvariantCulture),
            process.Threads.Count.ToString(CultureInfo.InvariantCulture),
            GCSettings.IsServerGC ? "1" : "0",
            GCSettings.LatencyMode.ToString(),
            Convert.ToString(conserve, CultureInfo.InvariantCulture));
    }

    private void Write()
    {
        try
        {
            File.AppendAllText(_path, FormatLine() + Environment.NewLine);
        }
        catch (IOException)
        {
            // A sampler reading the file at the same moment; the next minute writes again.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public void Dispose() => _timer.Dispose();
}
