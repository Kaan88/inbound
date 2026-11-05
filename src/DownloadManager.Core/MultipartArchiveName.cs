using System.Text.RegularExpressions;

namespace DownloadManager.Core;

/// <summary>
/// Recognises the parts of a split archive so they can be shown as one package:
/// <c>name.part1.rar</c>/<c>name.part01.rar</c>, <c>name.rar</c> + <c>name.r00</c>,
/// <c>name.7z.001</c>, <c>name.zip.001</c>, <c>name.zip</c> + <c>name.z01</c> and plain <c>name.001</c>.
/// </summary>
public static partial class MultipartArchiveName
{
    /// <summary>Returns the shared base name (for example <c>movie</c> or <c>backup.7z</c>) when
    /// <paramref name="fileName"/> looks like one part of a split archive.</summary>
    public static bool TryGetBaseName(string? fileName, out string baseName)
    {
        baseName = string.Empty;
        if (string.IsNullOrWhiteSpace(fileName)) return false;
        var name = Path.GetFileName(fileName.Trim());

        foreach (var pattern in Patterns)
        {
            var match = pattern.Match(name);
            if (!match.Success) continue;
            var value = match.Groups["base"].Value.TrimEnd('.', ' ');
            if (value.Length == 0) return false;
            baseName = value;
            return true;
        }

        return false;
    }

    /// <summary>A case-insensitive key that is equal for every part of the same archive in one folder.</summary>
    public static string? KeyFor(string? fileName, string? directory)
    {
        if (!TryGetBaseName(fileName, out var baseName)) return null;
        var folder = string.IsNullOrWhiteSpace(directory) ? string.Empty
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        return "multipart:" + folder.ToUpperInvariant() + "|" + baseName.ToUpperInvariant();
    }

    /// <summary>
    /// Old-style RAR and split ZIP sets start with an unnumbered <c>name.rar</c>/<c>name.zip</c>.
    /// That file alone is not a part, but it joins the package once a numbered sibling exists.
    /// </summary>
    public static string? FirstPartKeyFor(string? fileName, string? directory)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        var name = Path.GetFileName(fileName.Trim());
        var extension = Path.GetExtension(name);
        if (!extension.Equals(".rar", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)) return null;
        if (PartRar().IsMatch(name)) return null;
        var baseName = Path.GetFileNameWithoutExtension(name);
        if (baseName.Length == 0) return null;
        var folder = string.IsNullOrWhiteSpace(directory) ? string.Empty
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        return "multipart:" + folder.ToUpperInvariant() + "|" + baseName.ToUpperInvariant();
    }

    private static readonly Regex[] Patterns =
    [
        PartRar(),      // name.part1.rar, name.part001.rar
        OldRar(),       // name.rar together with name.r00 … name.r99
        SevenZipOrZip(),// name.7z.001, name.zip.001
        ZipSplit(),     // name.z01 (with name.zip)
        Numbered(),     // name.001 (HJSplit and similar)
    ];

    [GeneratedRegex(@"^(?<base>.+)\.part\d{1,4}\.rar$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PartRar();

    [GeneratedRegex(@"^(?<base>.+)\.r\d{2,3}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OldRar();

    [GeneratedRegex(@"^(?<base>.+\.(?:7z|zip|rar|tar|gz))\.\d{3}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SevenZipOrZip();

    [GeneratedRegex(@"^(?<base>.+)\.z\d{2}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ZipSplit();

    [GeneratedRegex(@"^(?<base>.+)\.\d{3}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Numbered();
}
