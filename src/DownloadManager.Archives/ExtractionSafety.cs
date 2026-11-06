using System.Runtime.InteropServices;
using System.Text;
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
            if (segment.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0) return "character Windows does not allow";

            var dot = segment.IndexOf('.');
            var stem = (dot < 0 ? segment : segment[..dot]).TrimEnd(' ');
            if (DeviceNames.Contains(stem)) return "reserved device name";
        }

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

    /// <summary>Free bytes on the volume holding a path, or null when that cannot be told, as on a network share.</summary>
    public static long? AvailableBytes(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Walks what was really written. Refuses reparse points, hard links, unsafe names, anything
    /// the listing did not name and more bytes than it stated; a listed file that did not appear
    /// means the extraction cannot be trusted despite the exit code.
    /// </summary>
    public static StagedContent WalkStaging(string payloadRoot, ArchiveProbe probe, ExtractionLimits limits)
    {
        var listedFiles = probe.Entries
            .Where(entry => entry.Kind == ArchiveEntryKind.File)
            .Select(entry => entry.Path.Replace('/', '\\'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var listedFolders = probe.Entries
            .Where(entry => entry.Kind == ArchiveEntryKind.Directory)
            .Select(entry => entry.Path.Replace('/', '\\'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
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

                if (HardLinkCount(item.FullName) > 1)
                {
                    throw new ExtractionException(ExtractionFailureKind.UnsafePath, $"Extraction produced a hard link: {SafeForDisplay(relative)}");
                }

                total += ((FileInfo)item).Length;
                if (total > probe.TotalListedSize || files.Count >= limits.MaxEntries)
                {
                    throw new ExtractionException(ExtractionFailureKind.UnsafePath, "Extraction wrote more than the archive listed.");
                }

                files.Add(relative);
            }
        }

        var written = files.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (listedFiles.FirstOrDefault(file => !written.Contains(file)) is { } absent)
        {
            throw new ExtractionException(ExtractionFailureKind.Corrupted, $"A listed file was not extracted: {SafeForDisplay(absent)}");
        }

        directories.Sort(StringComparer.OrdinalIgnoreCase);
        files.Sort(StringComparer.OrdinalIgnoreCase);
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

    private static uint HardLinkCount(string path)
    {
        if (!OperatingSystem.IsWindows()) return 1;
        using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return GetFileInformationByHandle(handle, out var information) ? information.NumberOfLinks : 1;
    }

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
