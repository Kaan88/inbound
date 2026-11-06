using System.Runtime.InteropServices;

namespace DownloadManager.Archives;

/// <summary>NTFS alternate data streams. Only a file's default stream shows up in a listing or a file's length.</summary>
internal static class DataStreams
{
    private const string DefaultStream = "::$DATA";
    private const string ZoneIdentifier = ":Zone.Identifier:$DATA";
    private const string StreamSuffix = ":$DATA";
    private const int FindStreamInfoStandard = 0;
    private const int MaxExtendedPath = 260;

    /// <summary>
    /// The first stream that should not be there, or null. The zone mark is expected only when the archive itself has
    /// one, because then -om has just written it. Any other copy came out of the archive and says what the archive wants it to.
    /// </summary>
    public static string? FindUnexpected(string path, bool zoneAllowed)
    {
        foreach (var stream in Named(path))
        {
            if (!(zoneAllowed && string.Equals(stream, ZoneIdentifier, StringComparison.OrdinalIgnoreCase)))
            {
                return stream.EndsWith(StreamSuffix, StringComparison.OrdinalIgnoreCase) ? stream[..^StreamSuffix.Length] : stream;
            }
        }

        return null;
    }

    public static bool HasZoneIdentifier(string path) =>
        Named(path).Any(stream => string.Equals(stream, ZoneIdentifier, StringComparison.OrdinalIgnoreCase));

    /// <summary>Empty on a file system without streams, or if the path can't be read, which the rest of the walk will meet too.</summary>
    private static List<string> Named(string path)
    {
        var names = new List<string>();
        if (!OperatingSystem.IsWindows()) return names;

        var native = path.Length >= MaxExtendedPath && !path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\" + path : path;
        var handle = FindFirstStreamW(native, FindStreamInfoStandard, out var data, 0);
        if (handle == new IntPtr(-1)) return names;
        try
        {
            do
            {
                if (!string.Equals(data.Name, DefaultStream, StringComparison.OrdinalIgnoreCase)) names.Add(data.Name);
            }
            while (FindNextStreamW(handle, out data));
        }
        finally
        {
            FindClose(handle);
        }

        return names;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FindStreamData
    {
        public long Size;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxExtendedPath + 36)]
        public string Name;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstStreamW(string fileName, int infoLevel, out FindStreamData data, int flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextStreamW(IntPtr handle, out FindStreamData data);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr handle);
}
