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
    /// one, because then extraction has just written it. Any other copy came out of the archive and says what the archive wants it to.
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

    /// <summary>The ZoneId in a file's mark of the web, or null when it has none or it can't be read.</summary>
    public static int? ReadZoneId(string path)
    {
        if (!OperatingSystem.IsWindows() || !HasZoneIdentifier(path)) return null;
        try
        {
            foreach (var line in File.ReadAllLines(path + ":Zone.Identifier"))
            {
                var text = line.Trim();
                if (text.StartsWith("ZoneId=", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(text["ZoneId=".Length..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var zone))
                {
                    return zone;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Unreadable counts as none.
        }

        return null;
    }

    /// <summary>
    /// Marks a file as coming from a zone, without the addresses the archive's own mark may carry. Windows refuses to
    /// write a stream of a read-only file, and 7-Zip restores that attribute from the archive, so it is lifted for the
    /// write and put back.
    /// </summary>
    public static void WriteZone(string path, int zoneId)
    {
        var attributes = File.GetAttributes(path);
        var readOnly = (attributes & FileAttributes.ReadOnly) != 0;
        if (readOnly) File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        try
        {
            File.WriteAllText(path + ":Zone.Identifier", "[ZoneTransfer]" + Environment.NewLine + $"ZoneId={zoneId}" + Environment.NewLine);
        }
        finally
        {
            if (readOnly) File.SetAttributes(path, attributes);
        }
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
