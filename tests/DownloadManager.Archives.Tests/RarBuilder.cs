using System.Security.Cryptography;
using System.Text;

namespace DownloadManager.Archives.Tests;

/// <summary>
/// Hand-built stored archives, from public format notes. Rar 7.x can no longer write RAR4 or
/// old-style volume names, and it refuses to write hostile paths, so these cover what it cannot.
/// </summary>
internal static class RarBuilder
{
    private const uint DosTime = 0x5A213C00;
    private static readonly byte[] Rar4Marker = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00];
    private static readonly byte[] Rar5Marker = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00];
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static byte[] Rar5(params (string Name, byte[] Data)[] files) =>
        Rar5Raw([.. files.Select(file => Rar5File(file.Name, file.Data))]);

    public static byte[] Rar5Raw(params byte[][] fileBlocks) => Concat([Rar5Marker, Rar5Header(1, 0), .. fileBlocks, Rar5Header(5, 0)]);

    public static byte[] Rar5File(string name, byte[] data, byte[]? extra = null, uint attributes = 0x20, ulong compressionInfo = 0)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var body = new List<byte>();
        body.AddRange(VInt(2));
        body.AddRange(VInt(0x02UL | (extra is null ? 0UL : 0x01UL)));
        if (extra is not null) body.AddRange(VInt((ulong)extra.Length));
        body.AddRange(VInt((ulong)data.Length));
        body.AddRange(VInt(0x04));
        body.AddRange(VInt((ulong)data.Length));
        body.AddRange(VInt(attributes));
        body.AddRange(BitConverter.GetBytes(Crc(data)));
        body.AddRange(VInt(compressionInfo));
        body.AddRange(VInt(0));
        body.AddRange(VInt((ulong)nameBytes.Length));
        body.AddRange(nameBytes);
        if (extra is not null) body.AddRange(extra);
        return Concat([Rar5Block([.. body]), data]);
    }

    /// <summary>A redirection record: 2 Windows symlink, 3 junction, 4 hard link.</summary>
    public static byte[] Rar5Link(string name, int kind, string target, uint attributes = 0x400)
    {
        var targetBytes = Encoding.UTF8.GetBytes(target);
        byte[] record = [.. VInt(5), .. VInt((ulong)kind), .. VInt(0), .. VInt((ulong)targetBytes.Length), .. targetBytes];
        byte[] extra = [.. VInt((ulong)record.Length), .. record];
        return Rar5File(name, [], extra, attributes);
    }

    public static byte[] Rar4(params (string Name, byte[] Data)[] files) =>
        Concat([Rar4Marker, Rar4Main(), .. files.Select(file => Rar4File(file.Name, file.Data)), Rar4End()]);

    public static byte[] Rar4WithBadCrc(string name, byte[] data)
    {
        var damaged = (byte[])data.Clone();
        damaged[damaged.Length / 2] ^= 0xFF;
        return Concat([Rar4Marker, Rar4Main(), Rar4File(name, damaged, fileCrc: Crc(data)), Rar4End()]);
    }

    /// <summary>A file split over several volumes, with the flags that say so.</summary>
    public static IReadOnlyList<byte[]> Rar4Volumes(string entryName, byte[] data, int volumes, bool newNumbering)
    {
        const ushort VolumeFlag = 0x0001;
        const ushort NewNumberingFlag = 0x0010;
        const ushort FirstVolumeFlag = 0x0100;
        var size = data.Length / volumes;
        var result = new List<byte[]>();
        for (var index = 0; index < volumes; index++)
        {
            var slice = index == volumes - 1 ? data[(size * index)..] : data[(size * index)..(size * (index + 1))];
            ushort mainFlags = VolumeFlag;
            if (index == 0) mainFlags |= FirstVolumeFlag;
            if (newNumbering) mainFlags |= NewNumberingFlag;
            ushort fileFlags = 0x8000;
            if (index > 0) fileFlags |= 0x0001;
            if (index < volumes - 1) fileFlags |= 0x0002;
            var fileCrc = index == volumes - 1 ? Crc(data) : Crc(slice);
            result.Add(Concat([
                Rar4Marker,
                Rar4Main(mainFlags),
                Rar4File(entryName, slice, fileCrc, fileFlags, data.Length),
                Rar4End((ushort)(0x4000 | (index < volumes - 1 ? 0x0001 : 0))),
            ]));
        }

        return result;
    }

    public static byte[] Rar4Encrypted(string name, byte[] data, string password)
    {
        byte[] salt = [0, 1, 2, 3, 4, 5, 6, 7];
        var cipher = Encrypt(password, salt, data);
        return Concat([Rar4Marker, Rar4Main(), Rar4File(name, cipher, Crc(data), unpackedSize: data.Length, salt: salt), Rar4End()]);
    }

    public static byte[] Rar4HeaderEncrypted(string name, byte[] data, string password)
    {
        const ushort EncryptedHeadersFlag = 0x0080;
        byte[] salt = [0, 1, 2, 3, 4, 5, 6, 7];
        var cipher = Encrypt(password, salt, data);
        var header = Rar4FileHeader(name, cipher.Length, data.Length, Crc(data), 0x8000, salt);

        byte[] Block(byte[] plain)
        {
            var blockSalt = RandomNumberGenerator.GetBytes(8);
            return Concat([blockSalt, Encrypt(password, blockSalt, plain)]);
        }

        return Concat([Rar4Marker, Rar4Main(EncryptedHeadersFlag), Block(header), cipher, Block(Rar4End())]);
    }

    private static byte[] Rar4Main(ushort flags = 0)
    {
        var body = new List<byte> { 0x73 };
        body.AddRange(BitConverter.GetBytes(flags));
        body.AddRange(BitConverter.GetBytes((ushort)13));
        body.AddRange(new byte[6]);
        return Rar4Header([.. body]);
    }

    private static byte[] Rar4End(ushort flags = 0x4000)
    {
        var body = new List<byte> { 0x7B };
        body.AddRange(BitConverter.GetBytes(flags));
        body.AddRange(BitConverter.GetBytes((ushort)7));
        return Rar4Header([.. body]);
    }

    private static byte[] Rar4File(string name, byte[] data, uint? fileCrc = null, ushort flags = 0x8000, int? unpackedSize = null, byte[]? salt = null) =>
        Concat([Rar4FileHeader(name, data.Length, unpackedSize ?? data.Length, fileCrc ?? Crc(data), flags, salt), data]);

    private static byte[] Rar4FileHeader(string name, int packedSize, int unpackedSize, uint fileCrc, ushort flags, byte[]? salt)
    {
        var nameBytes = Encoding.ASCII.GetBytes(name);
        if (salt is not null) flags |= 0x0404;
        var size = (ushort)(32 + nameBytes.Length + (salt?.Length ?? 0));
        var body = new List<byte> { 0x74 };
        body.AddRange(BitConverter.GetBytes(flags));
        body.AddRange(BitConverter.GetBytes(size));
        body.AddRange(BitConverter.GetBytes(packedSize));
        body.AddRange(BitConverter.GetBytes(unpackedSize));
        body.Add(2);
        body.AddRange(BitConverter.GetBytes(fileCrc));
        body.AddRange(BitConverter.GetBytes(DosTime));
        body.Add(29);
        body.Add(0x30);
        body.AddRange(BitConverter.GetBytes((ushort)nameBytes.Length));
        body.AddRange(BitConverter.GetBytes(0x20));
        body.AddRange(nameBytes);
        if (salt is not null) body.AddRange(salt);
        return Rar4Header([.. body]);
    }

    private static byte[] Rar4Header(byte[] body) => Concat([BitConverter.GetBytes((ushort)(Crc(body) & 0xFFFF)), body]);

    private static byte[] Rar5Header(ulong type, ulong flags) => Rar5Block([.. VInt(type), .. VInt(0), .. VInt(flags)]);

    private static byte[] Rar5Block(byte[] body)
    {
        var size = VInt((ulong)body.Length);
        return Concat([BitConverter.GetBytes(Crc([.. size, .. body])), size, body]);
    }

    private static byte[] VInt(ulong value)
    {
        var bytes = new List<byte>();
        while (true)
        {
            var low = (byte)(value & 0x7F);
            value >>= 7;
            if (value == 0)
            {
                bytes.Add(low);
                return [.. bytes];
            }

            bytes.Add((byte)(low | 0x80));
        }
    }

    /// <summary>The RAR 3.x key derivation and AES-128-CBC with zero padding.</summary>
    private static byte[] Encrypt(string password, byte[] salt, byte[] plain)
    {
        const int Rounds = 0x40000;
        var raw = Concat([Encoding.Unicode.GetBytes(password), salt]);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        var iv = new byte[16];
        Span<byte> counter = stackalloc byte[3];
        Span<byte> digest = stackalloc byte[20];
        for (var round = 0; round < Rounds; round++)
        {
            sha.AppendData(raw);
            counter[0] = (byte)round;
            counter[1] = (byte)(round >> 8);
            counter[2] = (byte)(round >> 16);
            sha.AppendData(counter);
            if (round % (Rounds / 16) == 0)
            {
                sha.GetCurrentHash(digest);
                iv[round / (Rounds / 16)] = digest[19];
            }
        }

        sha.GetHashAndReset(digest);
        var key = new byte[16];
        for (var word = 0; word < 16; word += 4)
        {
            for (var index = 0; index < 4; index++) key[word + index] = digest[word + 3 - index];
        }

        var padded = new byte[(plain.Length + 15) / 16 * 16];
        plain.CopyTo(padded, 0);
        using var aes = Aes.Create();
        aes.Key = key;
        return aes.EncryptCbc(padded, iv, PaddingMode.None);
    }

    private static uint Crc(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in data) crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint index = 0; index < 256; index++)
        {
            var value = index;
            for (var bit = 0; bit < 8; bit++) value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
            table[index] = value;
        }

        return table;
    }

    private static byte[] Concat(byte[][] parts) => [.. parts.SelectMany(part => part)];
}
