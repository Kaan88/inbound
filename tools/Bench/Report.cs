using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Bench;

/// <summary>A CSV file with a fixed header; values are written with the invariant culture.</summary>
internal sealed class Csv : IDisposable
{
    private readonly StreamWriter _writer;

    public Csv(string path, string[] columns)
    {
        _writer = new StreamWriter(path, false, new UTF8Encoding(false)) { AutoFlush = true };
        _writer.WriteLine(string.Join(',', columns));
        Columns = columns.Length;
    }

    public int Columns { get; }

    /// <summary>Every row written, for the summary.</summary>
    public List<object?[]> Rows { get; } = new();

    public void Row(params object?[] values)
    {
        if (values.Length != Columns) throw new InvalidOperationException($"CSV row has {values.Length} values, expected {Columns}.");
        Rows.Add(values);
        _writer.WriteLine(string.Join(',', values.Select(Format)));
    }

    private static string Format(object? v) => v switch
    {
        null => "",
        double d => double.IsNaN(d) ? "" : d.ToString("0.###", CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        string s when s.IndexOfAny([',', '"', '\n', '\r']) >= 0 => "\"" + s.Replace("\"", "\"\"") + "\"",
        _ => v.ToString() ?? "",
    };

    public void Dispose() => _writer.Dispose();
}

internal static class Stats
{
    public static double Median(IReadOnlyCollection<double> values)
    {
        if (values.Count == 0) return double.NaN;
        var s = values.OrderBy(v => v).ToArray();
        return s.Length % 2 == 1 ? s[s.Length / 2] : (s[s.Length / 2 - 1] + s[s.Length / 2]) / 2;
    }

    /// <summary>Nearest-rank percentile, p in 0..100.</summary>
    public static double Percentile(IReadOnlyCollection<double> values, double p)
    {
        if (values.Count == 0) return double.NaN;
        var s = values.OrderBy(v => v).ToArray();
        var rank = (int)Math.Ceiling(p / 100.0 * s.Length);
        return s[Math.Clamp(rank - 1, 0, s.Length - 1)];
    }

    public static double Mean(IReadOnlyCollection<double> values) => values.Count == 0 ? double.NaN : values.Average();
}

/// <summary>What machine and folders the numbers came from.</summary>
internal static class Env
{
    public static List<(string Key, string Value)> Describe(IEnumerable<(string Name, string? Path)> folders)
    {
        var rows = new List<(string, string)>
        {
            ("utc_start", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)),
            ("os", RuntimeInformation.OSDescription),
            ("architecture", RuntimeInformation.OSArchitecture.ToString()),
            ("runtime", RuntimeInformation.FrameworkDescription),
            ("logical_cpus", Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture)),
            ("server_gc", System.Runtime.GCSettings.IsServerGC.ToString()),
        };
        if (OperatingSystem.IsLinux())
        {
            rows.Add(("cpu_model", CpuModel()));
            rows.Add(("cpu_governor", Read("/sys/devices/system/cpu/cpu0/cpufreq/scaling_governor") ?? "n/a"));
            rows.Add(("loadavg_at_start", Read("/proc/loadavg") ?? "n/a"));
            rows.Add(("mem_total_kb", MemTotalKb()));
        }
        else
        {
            rows.Add(("total_available_memory_bytes", GC.GetGCMemoryInfo().TotalAvailableMemoryBytes.ToString(CultureInfo.InvariantCulture)));
        }

        foreach (var (name, path) in folders)
            if (path is not null) rows.Add(($"{name}_filesystem", Fs.Describe(path)));
        return rows;
    }

    public static string? LoadAverage() => OperatingSystem.IsLinux() ? Read("/proc/loadavg") : null;

    private static string? Read(string path)
    {
        try { return File.ReadAllText(path).Trim(); } catch (Exception) { return null; }
    }

    private static string CpuModel()
    {
        try
        {
            var line = File.ReadLines("/proc/cpuinfo").FirstOrDefault(l => l.StartsWith("model name", StringComparison.Ordinal));
            return line?.Split(':', 2)[1].Trim() ?? "n/a";
        }
        catch (Exception) { return "n/a"; }
    }

    private static string MemTotalKb()
    {
        try
        {
            var line = File.ReadLines("/proc/meminfo").First(l => l.StartsWith("MemTotal", StringComparison.Ordinal));
            return new string(line.Where(char.IsDigit).ToArray());
        }
        catch (Exception) { return "n/a"; }
    }
}

/// <summary>The kind of file system a folder is on, so a "disk" folder that is really tmpfs is visible in the results.</summary>
internal static class Fs
{
    public static string Describe(string path) => Type(path) ?? "unknown";

    /// <summary>"ext4", "tmpfs", "NTFS/Fixed", ... or null when unknown.</summary>
    public static string? Type(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (OperatingSystem.IsLinux())
            {
                var real = full;
                var dir = new DirectoryInfo(full);
                if (dir.Exists && dir.ResolveLinkTarget(true) is { } target) real = target.FullName;
                string? best = null, bestType = null;
                foreach (var line in File.ReadLines("/proc/self/mounts"))
                {
                    var parts = line.Split(' ');
                    if (parts.Length < 3) continue;
                    var mount = parts[1].Replace("\\040", " ");
                    var isPrefix = mount == "/" || real == mount || real.StartsWith(mount + "/", StringComparison.Ordinal);
                    if (isPrefix && (best is null || mount.Length >= best.Length)) { best = mount; bestType = parts[2]; }
                }

                return bestType;
            }

            var root = Path.GetPathRoot(full);
            if (root is null) return null;
            var drive = new DriveInfo(root);
            return $"{drive.DriveFormat}/{drive.DriveType}";
        }
        catch (Exception) { return null; }
    }

    public static bool IsMemoryBacked(string path) => Type(path) is "tmpfs" or "ramfs" or "devtmpfs";

    public static long FreeBytes(string path)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace; } catch (Exception) { return long.MaxValue; }
    }
}
