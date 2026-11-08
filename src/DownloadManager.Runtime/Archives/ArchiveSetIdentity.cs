using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DownloadManager.Archives;
using DownloadManager.Contracts;
using DownloadManager.Core.Archives;
using DownloadManager.Core;

namespace DownloadManager.Runtime.Archives;

/// <summary>The stable identity of a set: which folder, and which first volume, whatever order things arrive in.</summary>
internal static partial class ArchiveSetIdentity
{
    /// <summary>
    /// Whether a name could belong to an archive set: RAR, a split name.7z.001, or a single 7z, zip or tar. Decides
    /// nothing by itself: the volume grouping does that with the rest of the folder in view.
    /// </summary>
    public static bool LooksLikeVolume(string? fileName) =>
        !string.IsNullOrEmpty(fileName) &&
        (fileName.EndsWith(".rar", StringComparison.OrdinalIgnoreCase) || OldStyleVolumeName().IsMatch(fileName) ||
         SplitPieceName().IsMatch(fileName) || ArchiveNames.StemOfSingle(fileName) is not null);

    /// <summary>The first volume's name, present or not. A set missing its first has it as its first gap.</summary>
    public static string FirstName(ArchiveVolumeSet set) =>
        set.FirstVolume ?? set.MissingVolumes.FirstOrDefault() ?? set.Members[0];

    public static string NormalizeDirectory(string directory) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));

    /// <summary>
    /// A set is its folder and its first volume's real name, so part1 and part01 are two sets from the
    /// start, and a record cannot turn into the other's when the other shows up. Only a name worked out
    /// from a later volume can be wrong, and <see cref="ArchiveDirectoryView.FindFor"/> deals with that.
    /// </summary>
    public static string KeyFor(string directory, ArchiveVolumeSet set) => KeyFor(directory, FirstName(set));

    /// <remarks>Folded to capitals on Windows only, where a.rar and A.RAR are one file. On Linux they are two sets.</remarks>
    public static string KeyFor(string directory, string firstVolumeName)
    {
        var key = NormalizeDirectory(directory) + "|" + firstVolumeName;
        return OperatingSystem.IsWindows() ? key.ToUpperInvariant() : key;
    }

    /// <summary>The same set gets the same id on every start, so its claim and its file are one thing.</summary>
    public static Guid IdFor(string key) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes(key)).AsSpan(0, 16));

    /// <summary>The base of a name that is part number 1 of a set, in any padding, or null.</summary>
    public static string? BaseOfFirstPart(string name) =>
        FirstPartName().Match(name) is { Success: true } first ? first.Groups["base"].Value : null;

    [GeneratedRegex(@"\.[r-z]\d{2}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OldStyleVolumeName();

    [GeneratedRegex(@"^(?<base>.+)\.part0*1\.rar$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FirstPartName();

    [GeneratedRegex(@"\.(?:7z|zip|rar|tar)\.\d{3}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SplitPieceName();
}

/// <summary>What one folder holds right now: files on disk, and files that downloads there will bring.</summary>
internal sealed class ArchiveDirectoryView
{
    private readonly HashSet<string> _onDisk;
    private readonly Dictionary<string, List<JobDto>> _jobsByName;

    private ArchiveDirectoryView(string directory, HashSet<string> onDisk, Dictionary<string, List<JobDto>> jobsByName, IReadOnlyList<ArchiveVolumeSet> sets)
    {
        Directory = directory;
        _onDisk = onDisk;
        _jobsByName = jobsByName;
        Sets = sets;
    }

    public string Directory { get; }

    public IReadOnlyList<ArchiveVolumeSet> Sets { get; }

    public bool IsOnDisk(string name) => _onDisk.Contains(name);

    public IReadOnlyList<JobDto> JobsNamed(string name) =>
        _jobsByName.TryGetValue(name, out var jobs) ? jobs : [];

    /// <param name="expectedNames">Names to treat as part of the folder though nothing has brought them yet.</param>
    public static ArchiveDirectoryView Read(string directory, IEnumerable<JobDto> allJobs, IEnumerable<string>? expectedNames = null)
    {
        var folder = ArchiveSetIdentity.NormalizeDirectory(directory);
        var onDisk = new HashSet<string>(PathNames.Comparer);
        try
        {
            if (System.IO.Directory.Exists(folder))
            {
                foreach (var file in System.IO.Directory.EnumerateFiles(folder)) onDisk.Add(Path.GetFileName(file));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // An unreachable folder reads as empty, so its set waits rather than fails.
            onDisk.Clear();
        }

        var jobsByName = new Dictionary<string, List<JobDto>>(PathNames.Comparer);
        foreach (var job in allJobs)
        {
            if (string.IsNullOrEmpty(job.FinalFilePath)) continue;
            var parent = Path.GetDirectoryName(job.FinalFilePath);
            if (parent is null || !PathNames.Same(ArchiveSetIdentity.NormalizeDirectory(parent), folder)) continue;
            var name = Path.GetFileName(job.FinalFilePath);
            if (!jobsByName.TryGetValue(name, out var list)) jobsByName[name] = list = [];
            list.Add(job);
        }

        var sets = ArchiveVolumes.Group(
            onDisk.Concat(jobsByName.Keys).Concat(expectedNames ?? []),
            name => onDisk.Contains(name) && VolumeHeaders.SaysWholeArchive(Path.Combine(folder, name)));
        return new ArchiveDirectoryView(folder, onDisk, jobsByName, sets);
    }

    public ArchiveVolumeSet? FindSet(string fileName) =>
        Sets.FirstOrDefault(set => set.Members.Contains(fileName, PathNames.Comparer));

    /// <summary>
    /// A set whose first volume really exists goes first: a later volume with nothing before it works out
    /// the same first name, and a whole archive of that name must not wait for it.
    /// </summary>
    public ArchiveVolumeSet? FindByKey(string key)
    {
        ArchiveVolumeSet? worked = null;
        foreach (var set in Sets)
        {
            if (!string.Equals(ArchiveSetIdentity.KeyFor(Directory, set), key, StringComparison.Ordinal)) continue;
            if (set.FirstVolume is not null) return set;
            worked ??= set;
        }

        return worked;
    }

    /// <summary>
    /// The set a record is about. Its key says, unless the record's first volume was only ever worked out: part10
    /// alone suggests an unpadded part1, and if part01 arrives instead, that is the file the record was waiting for.
    /// Never when the part1 it named turned out to exist, because then these are two sets.
    /// </summary>
    public ArchiveVolumeSet? FindFor(ArchiveSetRecord record)
    {
        if (FindByKey(record.Key) is { } set) return set;
        if (record.Attempts > 0 || record.State == ArchiveSetState.Completed) return null;
        if (ArchiveSetIdentity.BaseOfFirstPart(record.FirstVolumeName) is not { } baseName) return null;
        if (IsOnDisk(record.FirstVolumeName) || JobsNamed(record.FirstVolumeName).Count > 0) return null;

        return Sets.FirstOrDefault(candidate =>
            candidate is { Scheme: ArchiveVolumeScheme.PartNumbered, FirstVolume: { } first } &&
            ArchiveSetIdentity.BaseOfFirstPart(first) is { } candidateBase &&
            // A base, not a file name: bases match ignoring case, as ArchiveVolumes groups them.
            string.Equals(candidateBase, baseName, StringComparison.OrdinalIgnoreCase) &&
            (record.Volumes.Count == 0 || record.Volumes.Any(volume => candidate.Members.Contains(volume, PathNames.Comparer))));
    }
}

/// <summary>
/// What each file's header says about being a volume. A folder is read again on every job event and a set can
/// have a thousand volumes, so a file is only opened again when its size or time has changed.
/// </summary>
internal static class VolumeHeaders
{
    private const int MostRemembered = 4096;
    private static readonly ConcurrentDictionary<string, (long Length, long Ticks, bool Whole)> Known = new(PathNames.Comparer);

    public static bool SaysWholeArchive(string path)
    {
        try
        {
            var info = new FileInfo(path);
            var (length, ticks) = (info.Length, info.LastWriteTimeUtc.Ticks);
            if (Known.TryGetValue(path, out var seen) && seen.Length == length && seen.Ticks == ticks) return seen.Whole;

            // Only a RAR header says whether it is a volume; the grouping asks only about .rar names.
            var whole = path.EndsWith(".rar", StringComparison.OrdinalIgnoreCase) && RarVolumeHeader.Read(path) == RarVolumeHeaderKind.NotAVolume;
            if (Known.Count >= MostRemembered) Known.Clear();
            Known[path] = (length, ticks, whole);
            return whole;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
