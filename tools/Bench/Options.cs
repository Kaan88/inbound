using System.Globalization;

namespace Bench;

/// <summary>"--name value" pairs and bare "--flag"s after the command word.</summary>
internal sealed class Options
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public Options(IEnumerable<string> args)
    {
        var list = args.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            if (!list[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Unexpected argument '{list[i]}'.");
            var name = list[i][2..];
            if (i + 1 < list.Count && !list[i + 1].StartsWith("--", StringComparison.Ordinal)) _values[name] = list[++i];
            else _values[name] = "true";
        }
    }

    public bool Has(string name) => _values.ContainsKey(name);
    public string? Get(string name) => _values.GetValueOrDefault(name);
    public string Get(string name, string fallback) => _values.GetValueOrDefault(name) ?? fallback;
    public int Int(string name, int fallback) => _values.TryGetValue(name, out var v) ? int.Parse(v, CultureInfo.InvariantCulture) : fallback;

    /// <summary>A byte count: 123, 64M, 64MiB, 8G, 8GiB (binary multiples).</summary>
    public long Bytes(string name, long fallback) => _values.TryGetValue(name, out var v) ? ParseBytes(v) : fallback;

    public static long ParseBytes(string text)
    {
        var t = text.Trim().ToUpperInvariant().Replace("IB", "").Replace("B", "");
        long mult = 1;
        if (t.EndsWith('K')) { mult = 1L << 10; t = t[..^1]; }
        else if (t.EndsWith('M')) { mult = 1L << 20; t = t[..^1]; }
        else if (t.EndsWith('G')) { mult = 1L << 30; t = t[..^1]; }
        return (long)(double.Parse(t, CultureInfo.InvariantCulture) * mult);
    }

    public int[] IntList(string name, int[] fallback)
        => _values.TryGetValue(name, out var v)
            ? v.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray()
            : fallback;
}
