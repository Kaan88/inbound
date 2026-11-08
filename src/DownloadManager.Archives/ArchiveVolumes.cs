using System.Globalization;
using System.Text.RegularExpressions;

namespace DownloadManager.Archives;

public enum ArchiveVolumeScheme
{
    /// <summary><c>name.rar</c> with nothing that joins it.</summary>
    Single,

    /// <summary><c>name.part1.rar</c>, with any padding.</summary>
    PartNumbered,

    /// <summary><c>name.rar</c>, <c>name.r00</c> ... <c>name.r99</c>, <c>name.s00</c> ...</summary>
    OldStyle,

    /// <summary><c>name.001.rar</c>.</summary>
    ThreeDigit,

    /// <summary><c>name.7z.001</c>, <c>name.zip.001</c> and the like: one archive cut into pieces, which 7-Zip joins.</summary>
    Split,
}

/// <param name="BaseName">The name shared by the members, without the volume suffix.</param>
/// <param name="FirstVolume">Null when the first volume is one of the gaps.</param>
/// <param name="Members">The files present, in volume order.</param>
/// <param name="MissingVolumes">
/// Gaps up to the highest number seen. Names cannot reveal a missing last volume; only the
/// tool's whole-set listing can.
/// </param>
public sealed record ArchiveVolumeSet(
    string BaseName,
    ArchiveVolumeScheme Scheme,
    string? FirstVolume,
    IReadOnlyList<string> Members,
    IReadOnlyList<string> MissingVolumes);

/// <summary>
/// Groups the file names of one folder into archive sets: RAR sets, split name.7z.001 sets, and single 7z, zip and
/// tar files. Reads no files.
/// </summary>
public static partial class ArchiveVolumes
{
    // A name such as report.123.rar + report.124.rar is far likelier to be two separate
    // archives than a set missing 122 volumes, so a three-digit run needs to be nearly whole.
    private const int MaxThreeDigitGaps = 8;

    /// <param name="isWholeArchive">
    /// Says that a name's own header calls it a whole archive, so a numbered name doesn't make it part of a
    /// set. Null goes by names alone. Old-style names are left to the names: a lone .r00 has no header of its own to go by.
    /// </param>
    public static IReadOnlyList<ArchiveVolumeSet> Group(IEnumerable<string> fileNames, Func<string, bool>? isWholeArchive = null)
    {
        var names = fileNames
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var oldStyleBases = names
            .Select(name => OldStyleVolume().Match(name))
            .Where(match => match.Success && IsLetter(match, 'r'))
            .Select(match => match.Groups["base"].Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var oldStyle = new Dictionary<string, List<(int Index, string Name)>>(StringComparer.OrdinalIgnoreCase);
        var parts = new Dictionary<string, List<(string Digits, string Name)>>(StringComparer.OrdinalIgnoreCase);
        var threeDigit = new Dictionary<string, List<(int Number, string Name)>>(StringComparer.OrdinalIgnoreCase);
        var splits = new Dictionary<string, List<(int Number, string Name)>>(StringComparer.OrdinalIgnoreCase);
        var singles = new List<string>();
        var otherSingles = new List<(string Stem, string Name)>();

        foreach (var name in names)
        {
            if (SplitVolume().Match(name) is { Success: true } piece)
            {
                Add(splits, piece.Groups["base"].Value, (int.Parse(piece.Groups["number"].Value, CultureInfo.InvariantCulture), name));
                continue;
            }

            if (ArchiveNames.StemOfSingle(name) is { } stemOfOther)
            {
                otherSingles.Add((stemOfOther, name));
                continue;
            }

            if (OldStyleVolume().Match(name) is { Success: true } volume)
            {
                // Without an .rNN sibling, name.s01 is a season number or some other tool's file.
                var volumeBase = volume.Groups["base"].Value;
                if (!oldStyleBases.Contains(volumeBase)) continue;
                var letter = char.ToLowerInvariant(volume.Groups["letter"].Value[0]);
                var number = int.Parse(volume.Groups["number"].Value, CultureInfo.InvariantCulture);
                Add(oldStyle, volumeBase, (1 + ((letter - 'r') * 100) + number, name));
                continue;
            }

            if (!name.EndsWith(".rar", StringComparison.OrdinalIgnoreCase) || name.Length == ".rar".Length) continue;

            var stem = name[..^".rar".Length];
            if (oldStyleBases.Contains(stem))
            {
                Add(oldStyle, stem, (0, name));
            }
            else if (isWholeArchive?.Invoke(name) == true)
            {
                singles.Add(name);
            }
            else if (PartVolume().Match(name) is { Success: true } part && part.Groups["number"].Value.Any(digit => digit != '0'))
            {
                Add(parts, part.Groups["base"].Value, (part.Groups["number"].Value, name));
            }
            else if (ThreeDigitVolume().Match(name) is { Success: true } numbered)
            {
                Add(threeDigit, numbered.Groups["base"].Value, (int.Parse(numbered.Groups["number"].Value, CultureInfo.InvariantCulture), name));
            }
            else
            {
                singles.Add(name);
            }
        }

        var sets = new List<ArchiveVolumeSet>();
        foreach (var (baseName, members) in oldStyle) sets.Add(BuildOldStyle(baseName, members));
        foreach (var (baseName, members) in parts) sets.AddRange(BuildPartSets(baseName, members));
        foreach (var (baseName, members) in threeDigit) sets.AddRange(BuildThreeDigit(baseName, members));
        foreach (var (baseName, members) in splits) sets.Add(BuildSplit(baseName, members));
        foreach (var name in singles) sets.Add(new ArchiveVolumeSet(name[..^".rar".Length], ArchiveVolumeScheme.Single, name, [name], []));
        foreach (var (stem, name) in otherSingles) sets.Add(new ArchiveVolumeSet(stem, ArchiveVolumeScheme.Single, name, [name], []));

        return sets
            .OrderBy(set => set.BaseName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(set => set.Scheme)
            .ThenBy(set => set.Members[0], StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// TB.S3: the RAR recovery volumes (<c>.rev</c>) among these names that belong to the set called
    /// <paramref name="baseName"/>, such as <c>name.part01.rev</c>, <c>name.rev</c> or the older <c>name1_3_1.rev</c>. They
    /// rebuild missing volumes and hold nothing of their own, so they go when the set's archives are deleted. Only the
    /// name forms RAR writes are taken: <c>name2.rev</c> beside a set called <c>name</c> is not this set's, nor anything a
    /// longer set name in <paramref name="otherBaseNames"/> could claim. Reads no files.
    /// </summary>
    public static IReadOnlyList<string> RecoveryVolumes(string baseName, IEnumerable<string> fileNames, IEnumerable<string>? otherBaseNames = null)
    {
        if (string.IsNullOrEmpty(baseName)) return [];
        var others = (otherBaseNames ?? [])
            .Where(other => !string.Equals(other, baseName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return fileNames
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrEmpty(name) && IsRecoveryOf(baseName, name!) && !others.Any(other => IsRecoveryOf(other, name!)))
            .Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsRecoveryOf(string baseName, string name)
    {
        if (!name.EndsWith(".rev", StringComparison.OrdinalIgnoreCase) || name.Length <= baseName.Length + ".rev".Length - 1) return false;
        var stem = name[..^".rev".Length];
        return stem.StartsWith(baseName, StringComparison.OrdinalIgnoreCase) && RecoverySuffix().IsMatch(stem[baseName.Length..]);
    }

    /// <summary>The set a file belongs to, or null when the name is not part of any.</summary>
    public static ArchiveVolumeSet? FindSet(IEnumerable<string> fileNames, string fileName, Func<string, bool>? isWholeArchive = null) =>
        Group(fileNames, isWholeArchive).FirstOrDefault(set => set.Members.Contains(Path.GetFileName(fileName), StringComparer.OrdinalIgnoreCase));

    private static ArchiveVolumeSet BuildOldStyle(string baseName, List<(int Index, string Name)> members)
    {
        var ordered = members.OrderBy(member => member.Index).ToList();
        var present = ordered.Select(member => member.Index).ToHashSet();
        var missing = Enumerable.Range(0, ordered[^1].Index + 1)
            .Where(index => !present.Contains(index))
            .Select(index => baseName + OldStyleSuffix(index))
            .ToList();
        var first = ordered[0].Index == 0 ? ordered[0].Name : null;
        return new ArchiveVolumeSet(baseName, ArchiveVolumeScheme.OldStyle, first, ordered.Select(member => member.Name).ToList(), missing);
    }

    private static string OldStyleSuffix(int index)
    {
        if (index == 0) return ".rar";
        var offset = index - 1;
        return string.Create(CultureInfo.InvariantCulture, $".{(char)('r' + (offset / 100))}{offset % 100:00}");
    }

    /// <summary>
    /// part1 and part01 are different sets, because one archiver numbers all its volumes one way.
    /// A number with no leading zero says less: part10 fits a two-digit set, or an unpadded one
    /// that has grown past part9.
    /// </summary>
    private static IEnumerable<ArchiveVolumeSet> BuildPartSets(string baseName, List<(string Digits, string Name)> members)
    {
        var paddedWidths = members
            .Where(member => member.Digits[0] == '0')
            .Select(member => member.Digits.Length)
            .OrderBy(width => width)
            .ToList();

        var byWidth = new Dictionary<int, List<(int Number, string Name)>>();
        foreach (var (digits, name) in members)
        {
            int width;
            if (digits[0] == '0')
            {
                width = digits.Length;
            }
            else
            {
                var fits = paddedWidths.Where(candidate => candidate <= digits.Length).ToList();
                width = fits.Count > 0 ? fits[^1] : 1;
            }

            if (!byWidth.TryGetValue(width, out var list)) byWidth[width] = list = [];
            list.Add((int.Parse(digits, CultureInfo.InvariantCulture), name));
        }

        foreach (var (width, list) in byWidth)
        {
            var ordered = list.OrderBy(member => member.Number).ToList();
            var present = ordered.Select(member => member.Number).ToHashSet();
            var missing = Enumerable.Range(1, ordered[^1].Number)
                .Where(number => !present.Contains(number))
                .Select(number => string.Create(CultureInfo.InvariantCulture, $"{baseName}.part{number.ToString("D" + width, CultureInfo.InvariantCulture)}.rar"))
                .ToList();
            var first = ordered[0].Number == 1 ? ordered[0].Name : null;
            yield return new ArchiveVolumeSet(baseName, ArchiveVolumeScheme.PartNumbered, first, ordered.Select(member => member.Name).ToList(), missing);
        }
    }

    private static IEnumerable<ArchiveVolumeSet> BuildThreeDigit(string baseName, List<(int Number, string Name)> members)
    {
        var ordered = members.OrderBy(member => member.Number).ToList();
        var start = ordered[0].Number == 0 ? 0 : 1;
        var present = ordered.Select(member => member.Number).ToHashSet();
        var missing = ordered[^1].Number < start
            ? []
            : Enumerable.Range(start, ordered[^1].Number - start + 1)
                .Where(number => !present.Contains(number))
                .Select(number => string.Create(CultureInfo.InvariantCulture, $"{baseName}.{number:000}.rar"))
                .ToList();

        var loneFirstVolume = ordered.Count == 1 && ordered[0].Number <= 1;
        if ((ordered.Count > 1 && missing.Count <= MaxThreeDigitGaps) || loneFirstVolume)
        {
            var first = ordered[0].Number == start ? ordered[0].Name : null;
            yield return new ArchiveVolumeSet(baseName, ArchiveVolumeScheme.ThreeDigit, first, ordered.Select(member => member.Name).ToList(), missing);
            yield break;
        }

        foreach (var (_, name) in ordered)
        {
            yield return new ArchiveVolumeSet(name[..^".rar".Length], ArchiveVolumeScheme.Single, name, [name], []);
        }
    }

    /// <summary>
    /// Numbered from 001 by every tool that writes them. The names can't tell where a set ends; 7-Zip finds out when
    /// it joins the parts.
    /// </summary>
    private static ArchiveVolumeSet BuildSplit(string baseName, List<(int Number, string Name)> members)
    {
        var ordered = members.OrderBy(member => member.Number).ToList();
        var present = ordered.Select(member => member.Number).ToHashSet();
        var missing = Enumerable.Range(1, Math.Max(ordered[^1].Number, 1))
            .Where(number => !present.Contains(number))
            .Select(number => string.Create(CultureInfo.InvariantCulture, $"{baseName}.{number:000}"))
            .ToList();
        var first = ordered[0].Number == 1 ? ordered[0].Name : null;
        return new ArchiveVolumeSet(baseName, ArchiveVolumeScheme.Split, first, ordered.Select(member => member.Name).ToList(), missing);
    }

    private static bool IsLetter(Match match, char letter) =>
        char.ToLowerInvariant(match.Groups["letter"].Value[0]) == letter;

    private static void Add<T>(Dictionary<string, List<T>> groups, string key, T value)
    {
        if (!groups.TryGetValue(key, out var list)) groups[key] = list = [];
        list.Add(value);
    }

    [GeneratedRegex(@"^(?<base>.+)\.(?<letter>[r-z])(?<number>\d{2})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OldStyleVolume();

    [GeneratedRegex(@"^(?<base>.+)\.part(?<number>\d{1,4})\.rar$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PartVolume();

    [GeneratedRegex(@"^(?<base>.+)\.(?<number>\d{3})\.rar$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ThreeDigitVolume();

    [GeneratedRegex(@"^(?<base>.+\.(?:7z|zip|rar|tar))\.(?<number>\d{3})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SplitVolume();

    /// <summary>What follows a set's base name in a recovery volume's name: nothing, .partN, .N, .rNN, or RAR 3's N_N[_N].</summary>
    [GeneratedRegex(@"^(?:|\.part\d{1,4}|\.\d{1,4}|\.[r-z]\d{2}|\d{1,4}(?:_\d{1,4}){1,2})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RecoverySuffix();
}
