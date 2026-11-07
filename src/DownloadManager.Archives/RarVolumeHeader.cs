namespace DownloadManager.Archives;

public enum RarVolumeHeaderKind
{
    /// <summary>Encrypted headers, not a RAR file, or a file that could not be read. The name has to decide.</summary>
    Unknown,

    /// <summary>The header says this is a whole archive, whatever its name suggests.</summary>
    NotAVolume,

    Volume,
}

/// <summary>
/// Reads the "this is a volume" flag from the start of a RAR file. A name such as report.001.rar only
/// suggests a set. UnRAR goes by this flag, so two separate archives with such names are two archives.
/// </summary>
public static class RarVolumeHeader
{
    private const int BytesNeeded = 64;
    private const byte Rar4MainHeader = 0x73;
    private const ulong Rar5MainHeader = 1;
    private const ulong Rar5HasExtraArea = 1;
    private const ulong Rar5HasDataArea = 2;

    private static ReadOnlySpan<byte> Rar4Signature => [(byte)'R', (byte)'a', (byte)'r', (byte)'!', 0x1A, 0x07, 0x00];

    private static ReadOnlySpan<byte> Rar5Signature => [(byte)'R', (byte)'a', (byte)'r', (byte)'!', 0x1A, 0x07, 0x01, 0x00];

    public static RarVolumeHeaderKind Read(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1);
            Span<byte> head = stackalloc byte[BytesNeeded];
            return Parse(head[..stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false)]);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return RarVolumeHeaderKind.Unknown;
        }
    }

    public static RarVolumeHeaderKind Parse(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith(Rar5Signature)) return ParseRar5(head[Rar5Signature.Length..]);
        if (!head.StartsWith(Rar4Signature)) return RarVolumeHeaderKind.Unknown;

        // CRC (2), type (1), flags (2). Bit 0 of the main header's flags marks a volume.
        var main = head[Rar4Signature.Length..];
        if (main.Length < 5 || main[2] != Rar4MainHeader) return RarVolumeHeaderKind.Unknown;
        return (main[3] & 1) != 0 ? RarVolumeHeaderKind.Volume : RarVolumeHeaderKind.NotAVolume;
    }

    private static RarVolumeHeaderKind ParseRar5(ReadOnlySpan<byte> rest)
    {
        // CRC32 (4), then variable-length: header size, type, header flags, and only with their flags
        // an extra-area size and a data-area size, then the main header's own archive flags.
        if (rest.Length < 4) return RarVolumeHeaderKind.Unknown;
        rest = rest[4..];
        if (!TryReadVint(ref rest, out _) || !TryReadVint(ref rest, out var type) || !TryReadVint(ref rest, out var flags)) return RarVolumeHeaderKind.Unknown;

        // An encryption header comes first when the headers are encrypted, so there is nothing to read yet.
        if (type != Rar5MainHeader) return RarVolumeHeaderKind.Unknown;
        if ((flags & Rar5HasExtraArea) != 0 && !TryReadVint(ref rest, out _)) return RarVolumeHeaderKind.Unknown;
        if ((flags & Rar5HasDataArea) != 0 && !TryReadVint(ref rest, out _)) return RarVolumeHeaderKind.Unknown;
        if (!TryReadVint(ref rest, out var archiveFlags)) return RarVolumeHeaderKind.Unknown;
        return (archiveFlags & 1) != 0 ? RarVolumeHeaderKind.Volume : RarVolumeHeaderKind.NotAVolume;
    }

    private static bool TryReadVint(ref ReadOnlySpan<byte> rest, out ulong value)
    {
        value = 0;
        for (var index = 0; index < rest.Length && index < 10; index++)
        {
            value |= (ulong)(rest[index] & 0x7F) << (7 * index);
            if ((rest[index] & 0x80) == 0)
            {
                rest = rest[(index + 1)..];
                return true;
            }
        }

        return false;
    }
}
