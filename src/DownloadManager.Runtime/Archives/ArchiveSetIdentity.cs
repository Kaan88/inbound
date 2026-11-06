using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DownloadManager.Archives;
using DownloadManager.Contracts;

namespace DownloadManager.Runtime.Archives;

/// <summary>The stable identity of a set: which folder, and which first volume, whatever order things arrive in.</summary>
internal static partial class ArchiveSetIdentity
{
    /// <summary>
    /// Whether a name could belong to a RAR set. Decides nothing by itself: the volume grouping does
    /// that with the rest of the folder in view. Any other format is not claimed.
    /// </summary>
    public static bool LooksLikeVolume(string? fileName) =>
        !string.IsNullOrEmpty(fileName) &&
        (fileName.EndsWith(".rar", StringComparison.OrdinalIgnoreCase) || OldStyleVolumeName().IsMatch(fileName));

    /// <summary>The first volume's name, present or not. A set missing its first has it as its first gap.</summary>
    public static string FirstName(ArchiveVolumeSet set) =>
        set.FirstVolume ?? set.MissingVolumes.FirstOrDefault() ?? set.Members[0];

    public static string NormalizeDirectory(string directory) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));

    /// <summary>
    /// part1 and part01 mean one first volume unless both sets really exist. Without this, a set
    /// seen first as part10 alone would be keyed for an unpadded part1 and change when part01
    /// arrives, leaving a record waiting for a file that will never come.
    /// </summary>
    public static string KeyFor(string directory, ArchiveVolumeSet set, IReadOnlyList<ArchiveVolumeSet> allSets)
    {
        var name = FirstName(set);
        var hasTwin = allSets.Count(other => other.Scheme == ArchiveVolumeScheme.PartNumbered &&
            string.Equals(other.BaseName, set.BaseName, StringComparison.OrdinalIgnoreCase)) > 1;
        if (set.Scheme == ArchiveVolumeScheme.PartNumbered && !hasTwin)
        {
            name = PaddedPartNumber().Replace(name, ".part$1.rar");
        }

        return (NormalizeDirectory(directory) + "|" + name).ToUpperInvariant();
    }

    /// <summary>The same set gets the same id on every start, so its claim and its file are one thing.</summary>
    public static Guid IdFor(string key) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes(key)).AsSpan(0, 16));

    [GeneratedRegex(@"\.[r-z]\d{2}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OldStyleVolumeName();

    [GeneratedRegex(@"\.part0*(\d+)\.rar$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PaddedPartNumber();
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
        var onDisk = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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

        var jobsByName = new Dictionary<string, List<JobDto>>(StringComparer.OrdinalIgnoreCase);
        foreach (var job in allJobs)
        {
            if (string.IsNullOrEmpty(job.FinalFilePath)) continue;
            var parent = Path.GetDirectoryName(job.FinalFilePath);
            if (parent is null || !string.Equals(ArchiveSetIdentity.NormalizeDirectory(parent), folder, StringComparison.OrdinalIgnoreCase)) continue;
            var name = Path.GetFileName(job.FinalFilePath);
            if (!jobsByName.TryGetValue(name, out var list)) jobsByName[name] = list = [];
            list.Add(job);
        }

        var sets = ArchiveVolumes.Group(onDisk.Concat(jobsByName.Keys).Concat(expectedNames ?? []));
        return new ArchiveDirectoryView(folder, onDisk, jobsByName, sets);
    }

    public ArchiveVolumeSet? FindSet(string fileName) =>
        Sets.FirstOrDefault(set => set.Members.Contains(fileName, StringComparer.OrdinalIgnoreCase));

    public ArchiveVolumeSet? FindByKey(string key) =>
        Sets.FirstOrDefault(set => string.Equals(ArchiveSetIdentity.KeyFor(Directory, set, Sets), key, StringComparison.Ordinal));
}
