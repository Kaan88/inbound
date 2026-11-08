using System.Buffers.Binary;

using System.Text;

namespace DownloadManager.Archives.Tests;

/// <summary>
/// A hand-built spanned (split) zip set, from PKWARE's APPNOTE section 8.5: name.z01, name.z02 ... and name.zip last.
/// The first segment starts with the split signature 0x08074b50. Each local header records nothing of its disk; the
/// central directory, all in name.zip, gives each entry's disk and its offset inside that disk, and the end record
/// names the last disk. Stored entries, one per segment, so no record spans two files. Nothing on this PC writes these
/// from a command line (WinZip is GUI only here), so the tests make their own.
/// </summary>
internal static class SpannedZipBuilder
{
    private const uint SplitSignature = 0x08074b50;
    private const uint LocalSignature = 0x04034b50;
    private const uint CentralSignature = 0x02014b50;
    private const uint EndSignature = 0x06054b50;
    private const ushort DosTime = 0x6000;
    private const ushort DosDate = 0x5A68;

    /// <summary>
    /// Writes <c>baseName.z01</c> ... and <c>baseName.zip</c> into <paramref name="folder"/>, one entry per segment.
    /// Returns the paths in disk order, name.zip last.
    /// </summary>
    public static IReadOnlyList<string> Write(string folder, string baseName, params (string Name, byte[] Data)[] entries)
    {
        if (entries.Length < 2) throw new ArgumentException("A spanned set needs at least two segments.", nameof(entries));
        var segments = new List<MemoryStream>();
        var central = new MemoryStream();
        for (var disk = 0; disk < entries.Length; disk++)
        {
            var segment = new MemoryStream();
            if (disk == 0) Write32(segment, SplitSignature);
            var (name, data) = entries[disk];
            var nameBytes = Encoding.UTF8.GetBytes(name);
            var crc = RarBuilder.Crc(data);
            var offset = (uint)segment.Position;

            Write32(segment, LocalSignature);
            Write16(segment, 20);
            Write16(segment, 0);
            Write16(segment, 0);
            Write16(segment, DosTime);
            Write16(segment, DosDate);
            Write32(segment, crc);
            Write32(segment, (uint)data.Length);
            Write32(segment, (uint)data.Length);
            Write16(segment, (ushort)nameBytes.Length);
            Write16(segment, 0);
            segment.Write(nameBytes);
            segment.Write(data);

            Write32(central, CentralSignature);
            Write16(central, 20);
            Write16(central, 20);
            Write16(central, 0);
            Write16(central, 0);
            Write16(central, DosTime);
            Write16(central, DosDate);
            Write32(central, crc);
            Write32(central, (uint)data.Length);
            Write32(central, (uint)data.Length);
            Write16(central, (ushort)nameBytes.Length);
            Write16(central, 0);
            Write16(central, 0);
            Write16(central, (ushort)disk);
            Write16(central, 0);
            Write32(central, 0x20);
            Write32(central, offset);
            central.Write(nameBytes);
            segments.Add(segment);
        }

        var last = segments[^1];
        var lastDisk = (ushort)(segments.Count - 1);
        var centralOffset = (uint)last.Position;
        last.Write(central.ToArray());
        Write32(last, EndSignature);
        Write16(last, lastDisk);
        Write16(last, lastDisk);
        Write16(last, (ushort)entries.Length);
        Write16(last, (ushort)entries.Length);
        Write32(last, (uint)central.Length);
        Write32(last, centralOffset);
        Write16(last, 0);

        Directory.CreateDirectory(folder);
        var paths = new List<string>();
        for (var disk = 0; disk < segments.Count; disk++)
        {
            var suffix = disk == segments.Count - 1 ? ".zip" : $".z{disk + 1:00}";
            var path = Path.Combine(folder, baseName + suffix);
            File.WriteAllBytes(path, segments[disk].ToArray());
            paths.Add(path);
        }

        return paths;
    }

    private static void Write16(Stream stream, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void Write32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }
}
