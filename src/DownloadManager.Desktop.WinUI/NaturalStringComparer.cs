namespace DownloadManager.Desktop.WinUI;

/// <summary>Case-insensitive comparison that orders digit runs numerically (part2 before part10).</summary>
internal sealed class NaturalStringComparer : IComparer<string?>
{
    public static readonly NaturalStringComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;
        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
            {
                int si = i, sj = j;
                while (i < x.Length && char.IsAsciiDigit(x[i])) i++;
                while (j < y.Length && char.IsAsciiDigit(y[j])) j++;
                var a = x[si..i].TrimStart('0');
                var b = y[sj..j].TrimStart('0');
                if (a.Length != b.Length) return a.Length.CompareTo(b.Length);
                var digits = string.CompareOrdinal(a, b);
                if (digits != 0) return digits;
                continue;
            }
            var c = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
            if (c != 0) return c;
            i++; j++;
        }
        return (x.Length - i).CompareTo(y.Length - j);
    }
}
