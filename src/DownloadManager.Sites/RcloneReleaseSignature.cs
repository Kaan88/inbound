using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace DownloadManager.Sites;

public sealed class RcloneReleaseException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A verified signed text and the time in its signature.</summary>
public sealed record RcloneSignedText(string Text, DateTimeOffset SignedAt);

/// <summary>
/// rclone's signed <c>SHA256SUMS</c> (remote-sites.md, "rclone updates"): an OpenPGP clearsigned file, signed with rclone's
/// release key, which is built in here. Only what that file uses is read: a v4 signature over canonical text by the one
/// DSA key, with SHA-1 or a SHA-2 digest. Anything else is refused, never skipped.
/// <para>The key is DSA-1024 and the file is signed with SHA-1 (seen for 1.76.0), weak today, so the caller does not
/// rely on this alone: the same file must also come identical from two hosts, and each zip must match its line.</para>
/// </summary>
public static class RcloneReleaseSignature
{
    /// <summary>rclone's release signing key (https://rclone.org/release_signing/), Nick Craig-Wood.</summary>
    public const string KeyFingerprint = "FBF737ECE9F8AB18604BD2AC93935E02FF3B54FA";

    // The key's primary public-key packet body (v4, DSA), as published on keys.openpgp.org. Its SHA-1 fingerprint is
    // checked against KeyFingerprint before the key is used, so a changed byte here stops every check.
    private const string KeyPacket =
        "BDuy3V0RBADVQOAF5aFiCxD3t2h6iAF2WMiaMlgZ6kX2i/u7addNkzX71VU97NpI0SnsP5YWt+gEedST6OmFbtLfZWCR4KWn5XnNdjCMNhxaH6Wc" +
        "cVqNm4ALPIqT59uVjkgf8RISmmoNJ1d+2wMWjQTUfwOEmoIgH6n+2MYNUKuctBrwAACflwCg1I1QO/prv/5hczdpQCs+fL87DxsD/Rt7pIXvsIOZ" +
        "yQWbIhSvNpGalJuMkW5Jx92UjsE91Ipo3Xr6SGRPgW9+NxAZAsiZfCX/19knAyNrN9blwL0rcPDnkhdGwK69kfjF+wq+QbogRGodbKhqY4v+cMNk" +
        "KiemBuTQiWPkpKjifwNsD1fNjNKfDP3pJ64Yz7a4fuzVX1YwBACpKVuEen34lmcX6ziY4jq8rKibKBs4JjQCRO24kYoHDULVe+RS9krQWY5be0fo" +
        "Dhru4dsKccefK099G+WEzKVCKxupstWkTT/iJwajR8mIqd4AhD0wO9W3MCfVOv8ykMDZ7qBWk1DHc87Ep3W1o8t8wq74ifV+HjhhWg8QAylXgw==";

    private const string BeginMessage = "-----BEGIN PGP SIGNED MESSAGE-----";
    private const string BeginSignature = "-----BEGIN PGP SIGNATURE-----";
    private const string EndSignature = "-----END PGP SIGNATURE-----";

    private static readonly Lazy<DSAParameters> Key = new(() => ParseKey(Convert.FromBase64String(KeyPacket), KeyFingerprint));

    /// <summary>The signed text (dash-escaping removed, line ends as <c>\n</c>) when the signature is good; throws otherwise.</summary>
    public static string Verify(string clearsigned) => VerifySigned(clearsigned).Text;

    /// <summary>The signed text and when it was signed (from the signature itself, so it can be relied on).</summary>
    public static RcloneSignedText VerifySigned(string clearsigned) => VerifySigned(clearsigned, Key.Value, KeyFingerprint);

    internal static string Verify(string clearsigned, DSAParameters key, string fingerprint) => VerifySigned(clearsigned, key, fingerprint).Text;

    internal static RcloneSignedText VerifySigned(string clearsigned, DSAParameters key, string fingerprint)
    {
        var lines = clearsigned.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var i = 0;
        while (i < lines.Length && lines[i].Length == 0) i++;
        if (i >= lines.Length || lines[i].TrimEnd() != BeginMessage) throw Bad("it is not a signed file");
        i++;

        // Armor headers ("Hash: SHA1") up to the blank line; the signature packet says which digest counts.
        while (i < lines.Length && lines[i].TrimEnd().Length > 0)
        {
            if (!lines[i].StartsWith("Hash:", StringComparison.Ordinal)) throw Bad("it has an unknown header");
            i++;
        }

        if (i >= lines.Length) throw Bad("it ends early");
        i++;

        var text = new List<string>();
        while (i < lines.Length && lines[i].TrimEnd() != BeginSignature)
        {
            var line = lines[i];
            if (line.StartsWith("- ", StringComparison.Ordinal)) line = line[2..];
            else if (line.StartsWith('-')) throw Bad("a line is not dash-escaped");
            text.Add(line);
            i++;
        }

        if (i >= lines.Length) throw Bad("it has no signature");
        i++;
        while (i < lines.Length && lines[i].TrimEnd().Length > 0 && lines[i].Contains(':')) i++; // armor headers
        while (i < lines.Length && lines[i].TrimEnd().Length == 0) i++;

        var base64 = new StringBuilder();
        string? checksum = null;
        for (; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line == EndSignature) break;
            if (line.StartsWith('=')) checksum = line[1..];
            else base64.Append(line);
        }

        if (i >= lines.Length) throw Bad("its signature is cut off");
        byte[] packet;
        try
        {
            packet = Convert.FromBase64String(base64.ToString());
        }
        catch (FormatException e)
        {
            throw Bad("its signature is not base64", e);
        }

        if (checksum is not null && checksum != Crc24(packet)) throw Bad("its signature's checksum is wrong");

        // The signed text: trailing spaces and tabs removed from each line, lines joined with CR LF, and the line end
        // before the signature not included (RFC 4880, 7.1).
        var canonical = string.Join("\r\n", text.Select(line => line.TrimEnd(' ', '\t')));
        var signedAt = VerifyPacket(packet, Encoding.UTF8.GetBytes(canonical), key, fingerprint);
        return new RcloneSignedText(string.Join('\n', text), signedAt);
    }

    /// <summary>The file names and SHA-256 values of a verified <c>SHA256SUMS</c> text.</summary>
    public static IReadOnlyDictionary<string, string> ParseSums(string signedText)
    {
        var sums = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in signedText.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var space = line.IndexOf(' ');
            if (space != 64) throw new RcloneReleaseException("rclone's checksum list has a line that is not \"sha256  name\".");
            var hash = line[..64];
            var name = line[64..].TrimStart(' ', '*');
            if (!hash.All(Uri.IsHexDigit) || name.Length == 0 || !sums.TryAdd(name, hash.ToLowerInvariant()))
                throw new RcloneReleaseException("rclone's checksum list has a line that is not \"sha256  name\".");
        }

        return sums;
    }

    private static DateTimeOffset VerifyPacket(byte[] data, byte[] text, DSAParameters key, string fingerprint)
    {
        var (tag, body) = ReadPacket(data);
        if (tag != 2) throw Bad("its signature is not a signature packet");
        var reader = new Reader(body);
        if (reader.Byte() != 4) throw Bad("its signature is not version 4");
        var type = reader.Byte();
        if (type != 0x01) throw Bad("its signature is not over text");
        if (reader.Byte() != 17) throw Bad("its signature is not DSA");
        var hashAlgorithm = reader.Byte() switch
        {
            2 => HashAlgorithmName.SHA1,
            8 => HashAlgorithmName.SHA256,
            9 => HashAlgorithmName.SHA384,
            10 => HashAlgorithmName.SHA512,
            _ => throw Bad("its signature uses an unknown digest"),
        };

        var hashedLength = reader.UInt16();
        var hashed = reader.Bytes(hashedLength);
        var hashedEnd = reader.Position;
        var unhashed = reader.Bytes(reader.UInt16());
        var left16 = reader.Bytes(2);
        var r = reader.Mpi();
        var s = reader.Mpi();
        if (!reader.AtEnd) throw Bad("its signature has extra bytes");

        var issuer = false;
        var keyId = Convert.FromHexString(fingerprint[^16..]);
        var fingerprintBytes = Convert.FromHexString(fingerprint);
        DateTimeOffset? created = null;
        foreach (var (subType, critical, value, inHashed) in Subpackets(hashed, true).Concat(Subpackets(unhashed, false)))
        {
            switch (subType)
            {
                case 2 when inHashed && value.Length == 4:
                    created = DateTimeOffset.FromUnixTimeSeconds(BinaryPrimitives.ReadUInt32BigEndian(value));
                    break;
                case 3 when inHashed && value.Length == 4:
                    var seconds = BinaryPrimitives.ReadUInt32BigEndian(value);
                    if (seconds != 0 && created is { } at && at.AddSeconds(seconds) < DateTimeOffset.UtcNow) throw Bad("its signature has expired");
                    break;
                case 16 when value.Length == 8:
                    if (!value.AsSpan().SequenceEqual(keyId)) throw Bad("it is signed by another key");
                    issuer = true;
                    break;
                case 33 when value.Length == 21 && value[0] == 4:
                    if (!value.AsSpan(1).SequenceEqual(fingerprintBytes)) throw Bad("it is signed by another key");
                    issuer = true;
                    break;
                default:
                    if (critical) throw Bad("its signature has a critical part this check does not know");
                    break;
            }
        }

        if (!issuer) throw Bad("its signature does not name its key");
        if (created is null) throw Bad("its signature has no creation time");

        // The digest covers the text, the signature's hashed part, and a trailer (RFC 4880, 5.2.4).
        using var hash = IncrementalHash.CreateHash(hashAlgorithm);
        hash.AppendData(text);
        hash.AppendData(body, 0, hashedEnd);
        Span<byte> trailer = stackalloc byte[6];
        trailer[0] = 4;
        trailer[1] = 0xFF;
        BinaryPrimitives.WriteUInt32BigEndian(trailer[2..], (uint)hashedEnd);
        hash.AppendData(trailer);
        var digest = hash.GetHashAndReset();
        if (digest[0] != left16[0] || digest[1] != left16[1]) throw Bad("its signature does not match the text");

        // DSA signs the leftmost bits of the digest, as many as q has.
        var qLength = key.Q!.Length;
        var signature = new byte[2 * qLength];
        if (!PadInto(r, signature.AsSpan(0, qLength)) || !PadInto(s, signature.AsSpan(qLength))) throw Bad("its signature is malformed");
        using var dsa = DSA.Create();
        dsa.ImportParameters(key);
        if (!dsa.VerifySignature(digest.AsSpan(0, Math.Min(digest.Length, qLength)), signature, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            throw Bad("its signature does not match the text");
        return created.Value;
    }

    internal static DSAParameters ParseKey(byte[] body, string fingerprint)
    {
        Span<byte> prefix = [0x99, (byte)(body.Length >> 8), (byte)body.Length];
        var computed = Convert.ToHexString(SHA1.HashData([.. prefix, .. body]));
        if (computed != fingerprint) throw new RcloneReleaseException("The built-in rclone release key is damaged.");
        var reader = new Reader(body);
        if (reader.Byte() != 4) throw new RcloneReleaseException("The built-in rclone release key is not version 4.");
        reader.Bytes(4); // creation time
        if (reader.Byte() != 17) throw new RcloneReleaseException("The built-in rclone release key is not DSA.");
        var p = reader.Mpi();
        var q = reader.Mpi();
        var g = Pad(reader.Mpi(), p.Length);
        var y = Pad(reader.Mpi(), p.Length);
        return new DSAParameters { P = p, Q = q, G = g, Y = y };
    }

    internal static (int Tag, byte[] Body) ReadPacket(byte[] data)
    {
        if (data.Length < 2 || (data[0] & 0x80) == 0) throw Bad("its signature is not an OpenPGP packet");
        int tag, headerLength, length;
        if ((data[0] & 0x40) != 0)
        {
            tag = data[0] & 0x3F;
            var first = data[1];
            if (first < 192) (headerLength, length) = (2, first);
            else if (first < 224 && data.Length >= 3) (headerLength, length) = (3, ((first - 192) << 8) + data[2] + 192);
            else if (first == 255 && data.Length >= 6) (headerLength, length) = (6, checked((int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(2))));
            else throw Bad("its signature uses a packet form this check does not read");
        }
        else
        {
            tag = (data[0] >> 2) & 0x0F;
            (headerLength, length) = (data[0] & 3) switch
            {
                0 => (2, data[1]),
                1 when data.Length >= 3 => (3, BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(1))),
                2 when data.Length >= 5 => (5, checked((int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(1)))),
                _ => throw Bad("its signature uses a packet form this check does not read"),
            };
        }

        if (headerLength + length != data.Length) throw Bad("its signature is not exactly one packet");
        return (tag, data[headerLength..]);
    }

    private static IEnumerable<(int Type, bool Critical, byte[] Value, bool Hashed)> Subpackets(byte[] area, bool hashed)
    {
        var list = new List<(int, bool, byte[], bool)>();
        var i = 0;
        while (i < area.Length)
        {
            int length;
            var first = area[i];
            if (first < 192) (length, i) = (first, i + 1);
            else if (first < 255 && i + 1 < area.Length) (length, i) = (((first - 192) << 8) + area[i + 1] + 192, i + 2);
            else if (first == 255 && i + 4 < area.Length) (length, i) = (checked((int)BinaryPrimitives.ReadUInt32BigEndian(area.AsSpan(i + 1))), i + 5);
            else throw Bad("its signature has a malformed part");
            if (length < 1 || i + length > area.Length) throw Bad("its signature has a malformed part");
            var type = area[i];
            list.Add((type & 0x7F, (type & 0x80) != 0, area[(i + 1)..(i + length)], hashed));
            i += length;
        }

        return list;
    }

    private static bool PadInto(byte[] value, Span<byte> target)
    {
        var start = 0;
        while (start < value.Length - 1 && value[start] == 0) start++;
        var trimmed = value.AsSpan(start);
        if (trimmed.Length > target.Length) return false;
        target.Clear();
        trimmed.CopyTo(target[(target.Length - trimmed.Length)..]);
        return true;
    }

    private static byte[] Pad(byte[] value, int length)
    {
        if (value.Length >= length) return value;
        var padded = new byte[length];
        value.CopyTo(padded, length - value.Length);
        return padded;
    }

    private static string Crc24(byte[] data)
    {
        var crc = 0xB704CEu;
        foreach (var b in data)
        {
            crc ^= (uint)b << 16;
            for (var bit = 0; bit < 8; bit++)
            {
                crc <<= 1;
                if ((crc & 0x1000000) != 0) crc ^= 0x1864CFBu;
            }
        }

        crc &= 0xFFFFFF;
        return Convert.ToBase64String([(byte)(crc >> 16), (byte)(crc >> 8), (byte)crc]);
    }

    private static RcloneReleaseException Bad(string why, Exception? inner = null) =>
        new($"rclone's checksum list can't be trusted: {why}.", inner);

    private sealed class Reader(byte[] data)
    {
        public int Position { get; private set; }

        public bool AtEnd => Position == data.Length;

        public byte Byte() => Position < data.Length ? data[Position++] : throw Bad("its signature is cut off");

        public int UInt16() => (Byte() << 8) | Byte();

        public byte[] Bytes(int count)
        {
            if (count < 0 || Position + count > data.Length) throw Bad("its signature is cut off");
            var bytes = data[Position..(Position + count)];
            Position += count;
            return bytes;
        }

        public byte[] Mpi()
        {
            var bits = UInt16();
            return Bytes((bits + 7) / 8);
        }
    }
}
