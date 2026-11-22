using System.Runtime.InteropServices;
using System.Text;

namespace DownloadManager.Core;

/// <summary>
/// MAC.6: what Safari and Chrome give a downloaded file on a Mac, the counterpart of Windows' mark of the web:
/// <c>com.apple.quarantine</c>, so Gatekeeper checks a downloaded app, disk image or installer before it first opens,
/// and <c>com.apple.metadata:kMDItemWhereFroms</c>, which Finder's Get Info shows as "Where from" and Spotlight
/// searches. As on Windows, only the site's scheme and host are written: the full address can carry a token.
/// </summary>
public static class MacFileMarks
{
    public const string QuarantineName = "com.apple.quarantine";
    public const string WhereFromsName = "com.apple.metadata:kMDItemWhereFroms";
    public const string Agent = "Inbound";

    /// <summary>Marks the file; best effort (a file system without extended attributes keeps it unmarked, logged).</summary>
    public static void TryWrite(string path, string? site, Action<string>? log = null)
    {
        if (!OperatingSystem.IsMacOS()) return;
        try
        {
            Set(path, QuarantineName, Encoding.ASCII.GetBytes(Quarantine(DateTimeOffset.UtcNow, Guid.NewGuid())));
            if (site is not null) Set(path, WhereFromsName, WhereFroms([site]));
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or DllNotFoundException or EntryPointNotFoundException)
        {
            log?.Invoke($"The download could not be marked as from the internet: {ex.Message}");
        }
    }

    /// <summary>The quarantine value: flags (0081: downloaded, not yet opened), the time in hex, the agent, an id.</summary>
    public static string Quarantine(DateTimeOffset at, Guid id) =>
        $"0081;{at.ToUnixTimeSeconds():x8};{Agent};{id.ToString("D").ToUpperInvariant()}";

    /// <summary>A file's quarantine value, or null when it has none (or this isn't a Mac).</summary>
    public static string? ReadQuarantine(string path)
    {
        if (!OperatingSystem.IsMacOS()) return null;
        var buffer = new byte[512];
        var length = GetXattr(path, QuarantineName, buffer, (nuint)buffer.Length, 0, 0);
        return length > 0 ? Encoding.ASCII.GetString(buffer, 0, (int)length) : null;
    }

    /// <summary>Puts a quarantine value on a file (an extracted one gets its archive's, as the Archive Utility does).</summary>
    public static void WriteQuarantine(string path, string value)
    {
        if (OperatingSystem.IsMacOS()) Set(path, QuarantineName, Encoding.ASCII.GetBytes(value));
    }

    /// <summary>
    /// The binary property list of an array of ASCII strings, the form <c>kMDItemWhereFroms</c> takes: "bplist00", the
    /// array and its strings, a table of their offsets, and the 32-byte trailer.
    /// </summary>
    public static byte[] WhereFroms(IReadOnlyList<string> values)
    {
        if (values.Count is 0 or > 14) throw new ArgumentOutOfRangeException(nameof(values));
        using var stream = new MemoryStream();
        stream.Write("bplist00"u8);
        var offsets = new List<long> { stream.Position };
        stream.WriteByte((byte)(0xA0 | values.Count));
        for (var i = 1; i <= values.Count; i++) stream.WriteByte((byte)i);
        foreach (var value in values)
        {
            if (value.Any(c => c > 0x7E || c < 0x20)) throw new ArgumentException("Only printable ASCII is written.", nameof(values));
            offsets.Add(stream.Position);
            var bytes = Encoding.ASCII.GetBytes(value);
            if (bytes.Length < 15)
            {
                stream.WriteByte((byte)(0x50 | bytes.Length));
            }
            else
            {
                stream.WriteByte(0x5F);
                stream.WriteByte(0x11); // a 2-byte length
                stream.WriteByte((byte)(bytes.Length >> 8));
                stream.WriteByte((byte)bytes.Length);
            }

            stream.Write(bytes);
        }

        var table = stream.Position;
        foreach (var offset in offsets)
        {
            stream.WriteByte((byte)(offset >> 8));
            stream.WriteByte((byte)offset);
        }

        Span<byte> trailer = stackalloc byte[32];
        trailer[6] = 2; // offset size
        trailer[7] = 1; // object reference size
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(trailer[8..], (ulong)offsets.Count);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(trailer[16..], 0); // the array is the top object
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(trailer[24..], (ulong)table);
        stream.Write(trailer);
        return stream.ToArray();
    }

    private static void Set(string path, string name, byte[] value)
    {
        if (SetXattr(path, name, value, (nuint)value.Length, 0, 0) != 0)
            throw new IOException($"setxattr {name} failed (errno {Marshal.GetLastPInvokeError()}).");
    }

    // Darwin: setxattr(path, name, value, size, position, options) and getxattr(path, name, value, size, position, options).
    [DllImport("libc", EntryPoint = "setxattr", SetLastError = true)]
    private static extern int SetXattr([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        byte[] value, nuint size, uint position, int options);

    [DllImport("libc", EntryPoint = "getxattr", SetLastError = true)]
    private static extern nint GetXattr([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        byte[] value, nuint size, uint position, int options);
}
