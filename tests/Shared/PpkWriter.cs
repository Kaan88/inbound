using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace DownloadManager.Tests.Shared;

/// <summary>
/// SITES.14: PuTTY key files (.ppk) written as PuTTYgen writes them (format 2, with or without a passphrase, and format 3
/// without), from keys a test makes. No real key is in the repository.
/// </summary>
public static class PpkWriter
{
    /// <summary>A .ppk as PuTTYgen writes it.</summary>
    public static string Ppk(int version, string algorithm, string comment, byte[] publicBlob, byte[] privateBlob, string? passphrase)
    {
        var encrypted = passphrase is not null;
        var cipher = encrypted ? "aes256-cbc" : "none";
        var secret = encrypted ? Encoding.UTF8.GetBytes(passphrase!) : [];
        if (encrypted)
        {
            // Padded to the cipher's block with bytes of the SHA-1 of the blob, as PuTTY does.
            var padding = (16 - privateBlob.Length % 16) % 16;
            privateBlob = [.. privateBlob, .. SHA1.HashData(privateBlob).AsSpan(0, padding)];
        }

        var macData = Blob(W(algorithm), W(cipher), W(comment), S(publicBlob), S(privateBlob));
        var mac = version == 2
            ? HMACSHA1.HashData(SHA1.HashData([.. "putty-private-key-file-mac-key"u8, .. secret]), macData)
            : HMACSHA256.HashData(Array.Empty<byte>(), macData);
        var stored = privateBlob;
        if (encrypted)
        {
            var key = (byte[])[.. SHA1.HashData([0, 0, 0, 0, .. secret]), .. SHA1.HashData([0, 0, 0, 1, .. secret])];
            using var aes = Aes.Create();
            aes.Key = key[..32];
            stored = aes.EncryptCbc(privateBlob, new byte[16], PaddingMode.None);
        }

        var text = new StringBuilder();
        text.Append($"PuTTY-User-Key-File-{version}: {algorithm}\r\nEncryption: {cipher}\r\nComment: {comment}\r\n");
        Lines(text, "Public-Lines", publicBlob);
        Lines(text, "Private-Lines", stored);
        text.Append($"Private-MAC: {Convert.ToHexString(mac).ToLowerInvariant()}\r\n");
        return text.ToString();
    }

    /// <summary>An Ed25519 test key's .ppk (a key made with ssh-keygen for tests only).</summary>
    public static string TestEd25519(int version = 2, string? passphrase = null) =>
        Ppk(version, "ssh-ed25519", "test", Blob(W("ssh-ed25519"), S(EdPublic)), Blob(S(EdSeed)), passphrase);

    public static readonly byte[] EdPublic = Convert.FromHexString("22be70dfbd4a29b4a4881093059bb036ac59a8ae3320d0b8a9fae62192e9eb57");
    public static readonly byte[] EdSeed = Convert.FromHexString("4e1a7b4723c75f982c994da3853da086262c0f0648a70a5d7664114a48c072f8");

    private static void Lines(StringBuilder text, string name, byte[] blob)
    {
        var base64 = Convert.ToBase64String(blob);
        var lines = Enumerable.Range(0, (base64.Length + 63) / 64).Select(i => base64.Substring(i * 64, Math.Min(64, base64.Length - i * 64))).ToList();
        text.Append($"{name}: {lines.Count}\r\n");
        foreach (var line in lines) text.Append(line).Append("\r\n");
    }

    public static byte[] W(string text) => S(Encoding.UTF8.GetBytes(text));

    public static byte[] S(byte[] bytes)
    {
        var result = new byte[4 + bytes.Length];
        BinaryPrimitives.WriteUInt32BigEndian(result, (uint)bytes.Length);
        bytes.CopyTo(result, 4);
        return result;
    }

    public static byte[] Mp(byte[] unsigned)
    {
        var trimmed = unsigned.SkipWhile(b => b == 0).ToArray();
        return S(trimmed.Length > 0 && trimmed[0] >= 0x80 ? [0, .. trimmed] : trimmed);
    }

    public static byte[] Blob(params byte[][] parts) => parts.SelectMany(part => part).ToArray();
}
