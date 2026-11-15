using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using DownloadManager.Sites;

namespace DownloadManager.Sites.Tests;

/// <summary>A DSA key of the tests' own that writes OpenPGP clearsigned text the way rclone's release list is written.</summary>
internal sealed class TestSigningKey : IDisposable
{
    public const byte Sha256Digest = 8;

    private readonly DSA _dsa = DSA.Create(2048);

    public string Fingerprint { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(20));

    public string Verify(string clearsigned) =>
        RcloneReleaseSignature.Verify(clearsigned, _dsa.ExportParameters(false), Fingerprint);

    public RcloneSignedText VerifySigned(string clearsigned) =>
        RcloneReleaseSignature.VerifySigned(clearsigned, _dsa.ExportParameters(false), Fingerprint);

    public string Sign(string text, byte digestId, string? issuer = null, byte[]? extraHashed = null, DateTimeOffset? signedAt = null)
    {
        var hashed = new List<byte>();
        hashed.AddRange([5, 2, .. BitConverter.GetBytes(BinaryPrimitives.ReverseEndianness((uint)(signedAt ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds()))]);
        hashed.AddRange([22, 33, 4, .. Convert.FromHexString(issuer ?? Fingerprint)]);
        if (extraHashed is not null) hashed.AddRange([(byte)extraHashed.Length, .. extraHashed]);

        var head = new List<byte> { 4, 0x01, 17, digestId, (byte)(hashed.Count >> 8), (byte)hashed.Count };
        head.AddRange(hashed);
        var canonical = Encoding.UTF8.GetBytes(string.Join("\r\n", text.Split('\n')));
        var trailer = new byte[6] { 4, 0xFF, 0, 0, 0, 0 };
        BinaryPrimitives.WriteUInt32BigEndian(trailer.AsSpan(2), (uint)head.Count);
        var digest = SHA256.HashData([.. canonical, .. head, .. trailer]);
        var qLength = _dsa.ExportParameters(false).Q!.Length;
        var signature = _dsa.CreateSignature(digest.AsSpan(0, qLength).ToArray(), DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        var body = new List<byte>(head) { 0, 0, digest[0], digest[1] };
        body.AddRange(Mpi(signature[..qLength]));
        body.AddRange(Mpi(signature[qLength..]));
        var packet = new List<byte> { 0xC2 };
        if (body.Count < 192) packet.Add((byte)body.Count);
        else packet.AddRange([(byte)(((body.Count - 192) >> 8) + 192), (byte)(body.Count - 192)]);
        packet.AddRange(body);

        var escaped = text.Split('\n').Select(line => line.StartsWith('-') ? "- " + line : line);
        return "-----BEGIN PGP SIGNED MESSAGE-----\nHash: SHA256\n\n" + string.Join('\n', escaped) +
            "\n-----BEGIN PGP SIGNATURE-----\n\n" + Convert.ToBase64String([.. packet]) + "\n-----END PGP SIGNATURE-----\n";
    }

    private static byte[] Mpi(byte[] value)
    {
        var start = 0;
        while (start < value.Length - 1 && value[start] == 0) start++;
        var trimmed = value[start..];
        var bits = (trimmed.Length - 1) * 8 + (8 - System.Numerics.BitOperations.LeadingZeroCount((uint)trimmed[0]) + 24);
        return [(byte)(bits >> 8), (byte)bits, .. trimmed];
    }

    public void Dispose() => _dsa.Dispose();
}
