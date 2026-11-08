using System.Runtime.InteropServices;
using System.Text;
using DownloadManager.Core;
using DownloadManager.Core.Archives;
using Microsoft.Win32.SafeHandles;

namespace DownloadManager.Archives;

public sealed record ExtractionLimits
{
    /// <summary>Refuse an archive listing more than this many bytes.</summary>
    public long MaxTotalBytes { get; init; } = 1L << 40;

    /// <summary>Refuse an archive listing more than this many entries.</summary>
    public int MaxEntries { get; init; } = 200_000;

    public long FreeSpaceMarginBytes { get; init; } = 256L * 1024 * 1024;

    /// <summary>Share of the listed size to keep free on top of <see cref="FreeSpaceMarginBytes"/>.</summary>
    public double FreeSpaceMarginFraction { get; init; } = 0.02;
}

/// <summary>What a staging walk found: paths relative to the payload folder, files sorted.</summary>
public sealed record StagedContent(IReadOnlyList<string> Directories, IReadOnlyList<string> Files);

/// <summary>
/// Checks that apply whichever tool extracts. Listing and written path can differ, because UnRAR
/// rewrites some names, so both the listing and what actually landed in staging are checked.
/// </summary>
public static partial class ExtractionSafety
{
    private static readonly HashSet<string> DeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", "CLOCK$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    /// <summary>Returns why an entry path must not be written, or null when it is acceptable.</summary>
    public static string? DescribeUnsafePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return "empty name";
        if (path.Any(character => char.IsControl(character))) return "control character in name";
        if (path[0] is '\\' or '/') return "rooted or network path";
        if (path.Contains(':')) return "drive or stream in name";

        var segments = path.Split('\\', '/');
        for (var index = 0; index < segments.Length; index++)
        {
            var segment = segments[index];
            if (segment.Length == 0) return "empty folder name";
            if (segment is "." or "..") return "path traversal";

            // UnRAR lists C:/x as C_\x, so a leading one-letter folder ending in _ is how a drive path looks here.
            if (index == 0 && segments.Length > 1 && segment.Length == 2 && char.IsAsciiLetter(segment[0]) && segment[1] == '_')
                return "drive path";

            // Windows drops these, so "a." and "a" would be the same file.
            if (segment[^1] is '.' or ' ') return "name ends in a dot or space";

            // Inside a destination this name means a crashed extraction of ours, and it must not come from outside.
            if (ArchiveWorkingFolders.IsReserved(segment)) return "name this app keeps for its own working folders";
            if (segment.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0) return "character Windows does not allow";

            var dot = segment.IndexOf('.');
            var stem = (dot < 0 ? segment : segment[..dot]).TrimEnd(' ');
            if (DeviceNames.Contains(stem)) return "reserved device name";
        }

        // Linux keeps a backslash as part of a name, so "a\b" is one file there and a folder and a file on Windows.
        // Which one the archive meant can't be told, and a guess could put a file where the listing didn't say. A zip
        // made on Windows is the exception: its listing reads the backslash as a folder separator first, so it never
        // reaches this. Checked last, so a traversal or a device name is still named as such.
        if (!OperatingSystem.IsWindows() && path.Contains('\\')) return "backslash in name";

        return null;
    }

    /// <summary>Refuses an archive from its listing alone, before anything is written.</summary>
    public static void ValidateListing(ArchiveProbe probe, ExtractionLimits limits)
    {
        if (probe.ListingHasUnknownLines)
        {
            throw new ExtractionException(ExtractionFailureKind.UnsafePath, "The archive listing has a line that could not be read, which can mean a name with a line break in it.");
        }

        if (probe.Entries.Count > limits.MaxEntries)
        {
            throw new ExtractionException(ExtractionFailureKind.UnsafePath, $"The archive lists {probe.Entries.Count} entries, over the limit of {limits.MaxEntries}.");
        }

        if (probe.TotalListedSize > limits.MaxTotalBytes)
        {
            throw new ExtractionException(ExtractionFailureKind.DiskSpace, $"The archive lists {probe.TotalListedSize} bytes, over the limit of {limits.MaxTotalBytes}.");
        }

        if (probe.HoldsDataStreams)
        {
            throw new ExtractionException(ExtractionFailureKind.UnsafePath, "The archive carries hidden data streams, which its listing doesn't show and the size limits can't count.");
        }

        if (probe.ListingHasRepeatedNames)
        {
            throw new ExtractionException(ExtractionFailureKind.UnsafePath, "The archive lists the same name twice, so one file would overwrite the other.");
        }

        foreach (var entry in probe.Entries)
        {
            if (entry.Kind is not (ArchiveEntryKind.File or ArchiveEntryKind.Directory))
            {
                var what = entry.Kind == ArchiveEntryKind.HardLink ? "a hard link" : entry.IsLink ? "a link" : "an entry that is not a file or folder";
                throw new ExtractionException(ExtractionFailureKind.UnsafePath, $"The archive holds {what}: {SafeForDisplay(entry.Path)}");
            }

            if (DescribeUnsafePath(entry.Path) is { } reason)
            {
                throw new ExtractionException(ExtractionFailureKind.UnsafePath, $"Unsafe entry ({reason}): {SafeForDisplay(entry.Path)}");
            }
        }

        // Linux keeps a.txt and A.TXT apart, so there the listing's own repeat check is enough. A share that folds case
        // anyway is caught after extraction: WalkStaging then counts fewer files than were listed.
        if (OperatingSystem.IsWindows()) RefuseNamesWindowsCannotKeepApart(probe);
    }

    /// <summary>
    /// Archives from other systems can hold a.txt and A.TXT. Windows keeps one name for both, UnRAR writes the
    /// second over the first and exits 0, and with archives deleted the first would then exist nowhere.
    /// </summary>
    private static void RefuseNamesWindowsCannotKeepApart(ArchiveProbe probe)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in probe.Entries)
        {
            var path = PathNames.ToNativeSeparators(entry.Path);
            if (entry.Kind == ArchiveEntryKind.Directory)
            {
                folders.Add(path);
            }
            else if (!files.Add(path))
            {
                throw new ExtractionException(ExtractionFailureKind.UnsafePath, $"The archive holds two files whose names differ only by capital letters, and Windows can keep only one of them: {SafeForDisplay(path)}");
            }

            for (var parent = Path.GetDirectoryName(path); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
            {
                folders.Add(parent);
            }
        }

        if (files.FirstOrDefault(folders.Contains) is { } both)
        {
            throw new ExtractionException(ExtractionFailureKind.UnsafePath, $"The archive holds a file and a folder that Windows would see as the same name: {SafeForDisplay(both)}");
        }
    }

    /// <summary>A folder as the start of a path, for checking that another path is inside it. A drive's root already ends in its separator.</summary>
    internal static string WithSeparator(string folder) =>
        Path.EndsInDirectorySeparator(folder) ? folder : folder + Path.DirectorySeparatorChar;

    internal static string FreeName(string target)
    {
        var directory = Path.GetDirectoryName(target)!;
        var stem = Path.GetFileNameWithoutExtension(target);
        var extension = Path.GetExtension(target);
        for (var number = 1; number < 100_000; number++)
        {
            var candidate = Path.Combine(directory, $"{stem} ({number}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }

        throw new IOException("No free name for the file.");
    }

    public static long RequiredFreeBytes(long listedBytes, ExtractionLimits limits) =>
        listedBytes + limits.FreeSpaceMarginBytes + (long)(listedBytes * limits.FreeSpaceMarginFraction);

    public static void CheckFreeSpace(long listedBytes, long? availableBytes, ExtractionLimits limits)
    {
        if (availableBytes is not { } available) return;
        var required = RequiredFreeBytes(listedBytes, limits);
        if (available < required)
        {
            throw new ExtractionException(ExtractionFailureKind.DiskSpace, $"Extraction needs {required} free bytes including a margin, and {available} are available.");
        }
    }

    /// <summary>Free bytes on the volume holding a path (its own mount on Linux), or null when that cannot be told, as on a network share.</summary>
    public static long? AvailableBytes(string path) => Volumes.AvailableBytes(path);

    /// <summary>
    /// Off Windows, moves what 7-Zip wrote under a zip's Windows names (<c>dir\a.txt</c>, one name there) into the
    /// folders those names mean (<c>dir/a.txt</c>), as <see cref="ArchiveProbe.NameFixes"/> lists them, so that
    /// <see cref="WalkStaging"/> then finds what the listing names and checks it as usual. Every target was checked with
    /// the listing already. Nothing is followed: a link anywhere in staging stops this before a folder is made or a file
    /// moved. A target outside the payload folder is refused, and so is a file whose target is already there; a folder joins one.
    /// </summary>
    public static void ApplyNameFixes(string payloadRoot, IReadOnlyList<ArchiveNameFix> fixes)
    {
        if (fixes.Count == 0) return;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(payloadRoot));
        var prefix = root + Path.DirectorySeparatorChar;

        string Inside(string relative)
        {
            var full = Path.GetFullPath(Path.Combine(root, relative));
            if (!full.StartsWith(prefix, StringComparison.Ordinal))
            {
                throw new ExtractionException(ExtractionFailureKind.UnsafePath, $"An entry's folders lead outside the extraction folder: {SafeForDisplay(relative)}");
            }

            return full;
        }

        try
        {
            var everything = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false };
            foreach (var item in new DirectoryInfo(root).EnumerateFileSystemInfos("*", everything))
            {
                if ((item.Attributes & FileAttributes.ReparsePoint) != 0 || item.LinkTarget is not null)
                {
                    throw new ExtractionException(ExtractionFailureKind.UnsafePath, $"Extraction produced a link: {SafeForDisplay(Path.GetRelativePath(root, item.FullName))}");
                }
            }

            foreach (var fix in fixes.Where(fix => !fix.IsFolder))
            {
                var written = Inside(fix.Written);
                // One that didn't come out is named by the walk, as a listed file that was not extracted.
                if (!File.Exists(written)) continue;
                var target = Inside(fix.Path);
                if (File.Exists(target) || Directory.Exists(target))
                {
                    throw new ExtractionException(ExtractionFailureKind.UnsafePath, $"Two entries in the archive name the same file: {SafeForDisplay(fix.Path)}");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(written, target);
            }

            foreach (var fix in fixes.Where(fix => fix.IsFolder))
            {
                var written = Inside(fix.Written);
                Directory.CreateDirectory(Inside(fix.Path));
                // Empty by now: 7-Zip writes dir\a.txt beside dir\, not in it. One that still holds something throws.
                if (Directory.Exists(written)) Directory.Delete(written, recursive: false);
            }

            // A file listed as "a\b/c.txt" with no entry for "a\b" leaves 7-Zip's own folder "a\b" empty once c.txt has
            // moved. Deepest first, and only empty ones: anything still in one is refused by the walk.
            var leftovers = new DirectoryInfo(root).EnumerateDirectories("*", everything)
                .Where(folder => folder.Name.Contains('\\'))
                .Select(folder => folder.FullName)
                .OrderByDescending(folder => folder.Length)
                .ToList();
            foreach (var folder in leftovers)
            {
                if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder, recursive: false);
            }
        }
        catch (IOException exception)
        {
            throw new ExtractionException(ExtractionFailureKind.UnsafePath, $"An entry with backslashes in its name could not be put in its folders: {exception.GetType().Name}.");
        }
        catch (UnauthorizedAccessException exception)
        {
            throw MapIoFailure(exception, "putting entries in their folders");
        }
    }

    /// <summary>
    /// Walks what was really written. Refuses reparse points, hard links, unsafe names, hidden data
    /// streams, anything the listing did not name and more bytes than it stated; a listed file that did
    /// not appear means the extraction cannot be trusted despite the exit code.
    /// </summary>
    public static StagedContent WalkStaging(string payloadRoot, ArchiveProbe probe, ExtractionLimits limits)
    {
        var zoneAllowed = DataStreams.HasZoneIdentifier(probe.FirstVolume);
        var listedFiles = probe.Entries
            .Where(entry => entry.Kind == ArchiveEntryKind.File)
            .Select(entry => PathNames.ToNativeSeparators(entry.Path))
            .ToHashSet(PathNames.Comparer);
        var listedFolders = probe.Entries
            .Where(entry => entry.Kind == ArchiveEntryKind.Directory)
            .Select(entry => PathNames.ToNativeSeparators(entry.Path))
            .ToHashSet(PathNames.Comparer);
        foreach (var file in listedFiles)
        {
            for (var parent = Path.GetDirectoryName(file); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
            {
                listedFolders.Add(parent);
            }
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(payloadRoot));
        var directories = new List<string>();
        var files = new List<string>();
        long total = 0;
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));

        while (pending.Count > 0)
        {
            foreach (var item in pending.Pop().EnumerateFileSystemInfos())
            {
                var relative = Path.GetRelativePath(root, item.FullName);
                if ((item.Attributes & FileAttributes.ReparsePoint) != 0 || item.LinkTarget is not null)
                {
                    throw new ExtractionException(ExtractionFailureKind.UnsafePath, $"Extraction produced a link: {SafeForDisplay(relative)}");
                }

                if (DescribeUnsafePath(relative) is { } reason)
                {
                    throw new ExtractionException(ExtractionFailureKind.UnsafePath, $"Extraction produced an unsafe name ({reason}): {SafeForDisplay(relative)}");
                }

                // UnRAR restores NTFS streams without listing them, so nothing above counts or names them.
                if (DataStreams.FindUnexpected(item.FullName, zoneAllowed) is { } stream)
                {
                    throw new ExtractionException(ExtractionFailureKind.UnsafePath, $"A file in the archive carries a hidden data stream ({SafeForDisplay(stream)}): {SafeForDisplay(relative)}");
                }

                if (item is DirectoryInfo directory)
                {
                    if (!listedFolders.Contains(relative))
                    {
                        throw new ExtractionException(ExtractionFailureKind.UnsafePath, $"Extraction produced a folder the listing did not name: {SafeForDisplay(relative)}");
                    }

                    directories.Add(relative);
                    pending.Push(directory);
                    continue;
                }

                if (!listedFiles.Contains(relative))
                {
                    throw new ExtractionException(ExtractionFailureKind.UnsafePath, $"Extraction produced a file the listing did not name: {SafeForDisplay(relative)}");
                }

                switch (HardLinkCount(item.FullName))
                {
                    case > 1:
                        throw new ExtractionException(ExtractionFailureKind.UnsafePath, $"Extraction produced a hard link: {SafeForDisplay(relative)}");
                    case null:
                        // Not counted is not one: the check stays closed rather than being skipped without a word.
                        throw new ExtractionException(ExtractionFailureKind.UnsafePath,
                            $"This system can't tell whether an extracted file is a hard link ({RuntimeInformation.ProcessArchitecture}), so nothing was moved out of the staging folder: {SafeForDisplay(relative)}");
                }

                total += ((FileInfo)item).Length;
                if (total > probe.TotalListedSize || files.Count >= limits.MaxEntries)
                {
                    throw new ExtractionException(ExtractionFailureKind.UnsafePath, "Extraction wrote more than the archive listed.");
                }

                files.Add(relative);
            }
        }

        var written = files.ToHashSet(PathNames.Comparer);
        if (listedFiles.FirstOrDefault(file => !written.Contains(file)) is { } absent)
        {
            throw new ExtractionException(ExtractionFailureKind.Corrupted, $"A listed file was not extracted: {SafeForDisplay(absent)}");
        }

        // On Windows the checks above fold capital letters, so one file on disk could answer for two listed names. On
        // Linux a share that folds case does the same: two listed names, one file written.
        var listedCount = probe.Entries.Count(entry => entry.Kind == ArchiveEntryKind.File);
        if (files.Count < listedCount)
        {
            throw new ExtractionException(ExtractionFailureKind.Corrupted, $"The archive lists {listedCount} files, and {files.Count} came out of it.");
        }

        directories.Sort(PathNames.Comparer);
        files.Sort(PathNames.Comparer);
        return new StagedContent(directories, files);
    }

    /// <summary>
    /// Removes characters that can make a name read as something else on screen: bidi controls,
    /// which UnRAR keeps in file names, and other control characters.
    /// </summary>
    public static string SafeForDisplay(string name)
    {
        var builder = new StringBuilder(Math.Min(name.Length, 260));
        foreach (var character in name.Take(260))
        {
            var isBidi = character is '\u061C' or '\u200E' or '\u200F' or (>= '\u202A' and <= '\u202E') or (>= '\u2066' and <= '\u2069');
            builder.Append(isBidi || char.IsControl(character) ? '_' : character);
        }

        return builder.ToString();
    }

    internal static ExtractionException MapIoFailure(Exception exception, string action)
    {
        const int ErrorHandleDiskFull = 39;
        const int ErrorDiskFull = 112;
        var code = exception.HResult & 0xFFFF;
        var diskFull = exception is IOException && code is ErrorHandleDiskFull or ErrorDiskFull;
        return new ExtractionException(
            diskFull ? ExtractionFailureKind.DiskSpace : ExtractionFailureKind.Permission,
            diskFull ? $"The disk filled up while {action}." : $"Windows refused access while {action}: {exception.GetType().Name} (0x{exception.HResult:X8}).");
    }

    /// <returns>Null when the count can't be told off Windows. Windows keeps its earlier rule: a failed call counts as one.</returns>
    private static uint? HardLinkCount(string path)
    {
        if (OperatingSystem.IsLinux()) return UnixHardLinkCount(path);
        if (!OperatingSystem.IsWindows()) return null;
        using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return GetFileInformationByHandle(handle, out var information) ? information.NumberOfLinks : 1;
    }

    /// <summary>
    /// .NET has no way to ask for a link count, and on Linux a tar can carry hard links the listing might not admit to,
    /// so this asks the C library: <c>lstat</c>, or on a glibc older than 2.33, which doesn't export that, the
    /// <c>__lxstat</c> it is built on. Null when neither can be called, the call fails, or the architecture's
    /// <c>struct stat</c> isn't one this knows (<see cref="LinkCountIn"/>): the caller then refuses, rather than taking
    /// an unchecked file for a single link.
    /// </summary>
    internal static uint? UnixHardLinkCount(string path)
    {
        var buffer = new byte[256];
        int result;
        try
        {
            result = lstat(path, buffer);
        }
        catch (EntryPointNotFoundException)
        {
            try
            {
                // _STAT_VER: 1 on x64, 0 on arm64 (the generic layout); the buffer layout is the same as lstat's.
                result = __lxstat(RuntimeInformation.ProcessArchitecture == Architecture.X64 ? 1 : 0, path, buffer);
            }
            catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
            {
                return null;
            }
        }
        catch (DllNotFoundException)
        {
            return null;
        }

        return result == 0 ? LinkCountIn(buffer, RuntimeInformation.ProcessArchitecture) : null;
    }

    /// <summary>
    /// <c>st_nlink</c> from a <c>struct stat</c>: x64 has a 64-bit one at 16, arm64 (the generic layout) a 32-bit one at
    /// 20. Null for any other architecture, whose layout hasn't been checked.
    /// </summary>
    internal static uint? LinkCountIn(byte[] stat, Architecture architecture) => architecture switch
    {
        Architecture.X64 => (uint)Math.Min(BitConverter.ToUInt64(stat, 16), uint.MaxValue),
        Architecture.Arm64 => BitConverter.ToUInt32(stat, 20),
        _ => null,
    };

    // Big enough for struct stat on every Linux architecture (x64 144 bytes, arm64 128).
    [DllImport("libc", SetLastError = true)]
    private static extern int lstat(string path, byte[] buffer);

    [DllImport("libc", SetLastError = true)]
    private static extern int __lxstat(int version, string path, byte[] buffer);

    // Pack 4 because the native FILETIME fields are two DWORDs, not an 8-aligned long.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);
}
