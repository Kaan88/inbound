using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace DownloadManager.Sites;

/// <summary>
/// SITES.11 (owner, 2026-11-22): reads a PuTTY private key file (.ppk, format version 2 or 3, with or without a
/// passphrase) and writes the same key as an unencrypted OpenSSH private key, which rclone's key_pem reads. The Host
/// converts when a site is saved and seals the result, so the passphrase is not needed afterwards. RSA, ECDSA (NIST
/// P-256, P-384, P-521) and Ed25519 keys are accepted; DSA and Ed448 are not, as rclone can't use them.
/// Format: PuTTY manual, appendix C. No text here ever quotes the key or the passphrase.
/// </summary>
public static class PuttyKeyFile
{
    public sealed record Result(string? OpenSshKey, string? Problem, string? KeyType)
    {
        public bool Ok => OpenSshKey is not null;
    }

    public const string WrongPassphrase = "The passphrase doesn't open this key. Check it and try again.";
    public const string NeedsPassphrase = "This PuTTY key has a passphrase. Enter it as the key passphrase.";
    public const string Damaged = "This PuTTY key file is damaged or incomplete. Save it again with PuTTYgen and choose it again.";

    // Argon2 limits for a file from outside: PuTTYgen's defaults are 8192 KiB, 13 to 34 passes and 1 lane.
    private const int MaxMemoryKiB = 256 * 1024;
    private const int MaxPasses = 256;
    private const int MaxLanes = 16;

    /// <summary>Whether <paramref name="text"/> starts like a PuTTY key file of any version.</summary>
    public static bool IsPuttyKey(string text) => text.TrimStart().StartsWith("PuTTY-User-Key-File-", StringComparison.Ordinal);

    /// <summary>The key type named in a PuTTY key file's first line (for example ssh-ed25519), or null.</summary>
    public static string? KeyTypeOf(string text)
    {
        var line = text.TrimStart().Split('\n', 2)[0].Trim();
        var colon = line.IndexOf(':');
        return line.StartsWith("PuTTY-User-Key-File-", StringComparison.Ordinal) && colon > 0 ? line[(colon + 1)..].Trim() : null;
    }

    public static Result ToOpenSsh(string text, string? passphrase)
    {
        try
        {
            return Convert(text, passphrase);
        }
        catch (FormatException)
        {
            return new Result(null, Damaged, null);
        }
        catch (ArgumentException)
        {
            return new Result(null, Damaged, null);
        }
        catch (OverflowException)
        {
            return new Result(null, Damaged, null);
        }
    }

    private static Result Convert(string text, string? passphrase)
    {
        var lines = text.Replace("\r", "", StringComparison.Ordinal).Split('\n');
        int at = 0;
        string NextLine() => at < lines.Length ? lines[at++] : throw new FormatException();
        (string Key, string Value) Header(string expected)
        {
            var line = NextLine();
            var colon = line.IndexOf(": ", StringComparison.Ordinal);
            if (colon < 0) throw new FormatException();
            var key = line[..colon];
            if (expected.Length > 0 && key != expected) throw new FormatException();
            return (key, line[(colon + 2)..].Trim());
        }

        while (at < lines.Length && lines[at].Trim().Length == 0) at++;
        var (first, algorithm) = Header("");
        int version = first switch
        {
            "PuTTY-User-Key-File-2" => 2,
            "PuTTY-User-Key-File-3" => 3,
            _ when first.StartsWith("PuTTY-User-Key-File-", StringComparison.Ordinal) => 0,
            _ => throw new FormatException(),
        };
        if (version == 0)
            return new Result(null, "This PuTTY key file's format is not supported. Save it again with a current PuTTYgen.", algorithm);
        var encryption = Header("Encryption").Value;
        if (encryption is not ("none" or "aes256-cbc")) return new Result(null, Damaged, algorithm);
        var comment = Header("Comment").Value;
        var publicBlob = Blob(Header("Public-Lines").Value, NextLine);

        int memory = 0, passes = 0, lanes = 0;
        byte[] salt = [];
        Argon2.Kind kind = Argon2.Kind.Id;
        if (version == 3 && encryption != "none")
        {
            kind = Header("Key-Derivation").Value switch
            {
                "Argon2d" => Argon2.Kind.D,
                "Argon2i" => Argon2.Kind.I,
                "Argon2id" => Argon2.Kind.Id,
                _ => throw new FormatException(),
            };
            memory = int.Parse(Header("Argon2-Memory").Value, System.Globalization.CultureInfo.InvariantCulture);
            passes = int.Parse(Header("Argon2-Passes").Value, System.Globalization.CultureInfo.InvariantCulture);
            lanes = int.Parse(Header("Argon2-Parallelism").Value, System.Globalization.CultureInfo.InvariantCulture);
            salt = System.Convert.FromHexString(Header("Argon2-Salt").Value);
            if (memory is < 8 or > MaxMemoryKiB || passes is < 1 or > MaxPasses || lanes is < 1 or > MaxLanes || memory < 8 * lanes)
                return new Result(null, "This PuTTY key asks for more work to open than Inbound allows. Save it again with PuTTYgen's default settings.", algorithm);
        }

        var privateBlob = Blob(Header("Private-Lines").Value, NextLine);
        var mac = System.Convert.FromHexString(Header("Private-MAC").Value);

        bool encrypted = encryption != "none";
        if (encrypted && string.IsNullOrEmpty(passphrase)) return new Result(null, NeedsPassphrase, algorithm);
        var secret = Encoding.UTF8.GetBytes(encrypted ? passphrase! : "");
        byte[] cipherKey = [], iv = new byte[16], macKey;
        try
        {
            if (version == 3)
            {
                if (encrypted)
                {
                    var derived = Argon2.Hash(kind, secret, salt, memory, passes, lanes, 80);
                    cipherKey = derived[..32];
                    iv = derived[32..48];
                    macKey = derived[48..];
                    CryptographicOperations.ZeroMemory(derived);
                }
                else macKey = [];
            }
            else
            {
                if (encrypted)
                {
                    var a = SHA1.HashData([0, 0, 0, 0, .. secret]);
                    var b = SHA1.HashData([0, 0, 0, 1, .. secret]);
                    cipherKey = [.. a, .. b[..12]];
                }
                macKey = SHA1.HashData([.. "putty-private-key-file-mac-key"u8, .. secret]);
            }

            if (encrypted)
            {
                if (privateBlob.Length == 0 || privateBlob.Length % 16 != 0) return new Result(null, Damaged, algorithm);
                using var aes = Aes.Create();
                aes.Key = cipherKey;
                privateBlob = aes.DecryptCbc(privateBlob, iv, PaddingMode.None);
            }

            var macData = new SshWriter();
            macData.String(Encoding.UTF8.GetBytes(algorithm));
            macData.String(Encoding.UTF8.GetBytes(encryption));
            macData.String(Encoding.UTF8.GetBytes(comment));
            macData.String(publicBlob);
            macData.String(privateBlob);
            var expected = version == 3 ? HMACSHA256.HashData(macKey, macData.ToArray()) : HMACSHA1.HashData(macKey, macData.ToArray());
            if (!CryptographicOperations.FixedTimeEquals(expected, mac))
                return new Result(null, encrypted ? WrongPassphrase : Damaged, algorithm);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
            CryptographicOperations.ZeroMemory(cipherKey);
        }

        try
        {
            return BuildOpenSsh(algorithm, comment, publicBlob, privateBlob);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateBlob);
        }
    }

    private static byte[] Blob(string count, Func<string> nextLine)
    {
        int lines = int.Parse(count, System.Globalization.CultureInfo.InvariantCulture);
        if (lines is < 0 or > 4096) throw new FormatException();
        var base64 = new StringBuilder();
        for (int i = 0; i < lines; i++) base64.Append(nextLine().Trim());
        return System.Convert.FromBase64String(base64.ToString());
    }

    private static Result BuildOpenSsh(string algorithm, string comment, byte[] publicBlob, byte[] privateBlob)
    {
        var pub = new SshReader(publicBlob);
        if (Encoding.UTF8.GetString(pub.String()) != algorithm) return new Result(null, Damaged, algorithm);
        var priv = new SshReader(privateBlob);
        var section = new SshWriter();
        Span<byte> check = stackalloc byte[4];
        RandomNumberGenerator.Fill(check);
        section.Raw(check);
        section.Raw(check);
        section.String(Encoding.UTF8.GetBytes(algorithm));
        string type;
        switch (algorithm)
        {
            case "ssh-rsa":
            {
                var e = pub.String(); var n = pub.String();
                var d = priv.String(); var p = priv.String(); var q = priv.String(); var iqmp = priv.String();
                // n = p * q: the private half belongs to the public one.
                if (Mpint(n) != Mpint(p) * Mpint(q)) return new Result(null, Damaged, algorithm);
                section.String(n); section.String(e); section.String(d); section.String(iqmp); section.String(p); section.String(q);
                type = "RSA";
                break;
            }
            case "ecdsa-sha2-nistp256" or "ecdsa-sha2-nistp384" or "ecdsa-sha2-nistp521":
            {
                var curve = pub.String(); var point = pub.String();
                var d = priv.String();
                if (Encoding.UTF8.GetString(curve) != algorithm["ecdsa-sha2-".Length..]) return new Result(null, Damaged, algorithm);
                section.String(curve); section.String(point); section.String(d);
                type = "ECDSA";
                break;
            }
            case "ssh-ed25519":
            {
                var publicKey = pub.String();
                // PuTTY stores the 32-byte private key as a little-endian number without its high zero bytes.
                var stored = priv.String();
                if (publicKey.Length != 32 || stored.Length > 32) return new Result(null, Damaged, algorithm);
                var seed = new byte[32];
                stored.CopyTo(seed, 0);
                section.String(publicKey);
                section.String([.. seed, .. publicKey]);
                CryptographicOperations.ZeroMemory(seed);
                type = "Ed25519";
                break;
            }
            default:
                return new Result(null, algorithm switch
                {
                    "ssh-dss" => "DSA keys can't be used. Use an RSA, ECDSA or Ed25519 key.",
                    "ssh-ed448" => "Ed448 keys can't be used. Use an RSA, ECDSA or Ed25519 key.",
                    _ => "This kind of key can't be used. Use an RSA, ECDSA or Ed25519 key.",
                }, algorithm);
        }

        section.String(Encoding.UTF8.GetBytes(comment));
        for (byte pad = 1; section.Length % 8 != 0; pad++) section.Raw([pad]);

        var file = new SshWriter();
        file.Raw("openssh-key-v1\0"u8);
        file.String("none"u8);
        file.String("none"u8);
        file.String([]);
        file.UInt32(1);
        file.String(publicBlob);
        var sectionBytes = section.ToArray();
        file.String(sectionBytes);
        CryptographicOperations.ZeroMemory(sectionBytes);
        var body = file.ToArray();
        var base64 = System.Convert.ToBase64String(body);
        CryptographicOperations.ZeroMemory(body);
        var pem = new StringBuilder("-----BEGIN OPENSSH PRIVATE KEY-----\n");
        for (int i = 0; i < base64.Length; i += 70) pem.Append(base64, i, Math.Min(70, base64.Length - i)).Append('\n');
        pem.Append("-----END OPENSSH PRIVATE KEY-----\n");
        return new Result(pem.ToString(), null, type);
    }

    private static BigInteger Mpint(byte[] value) => new(value, isUnsigned: false, isBigEndian: true);

    private sealed class SshReader(byte[] data)
    {
        private int _at;

        public byte[] String()
        {
            if (_at + 4 > data.Length) throw new FormatException();
            var length = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(_at));
            _at += 4;
            if (length > (uint)(data.Length - _at)) throw new FormatException();
            var value = data.AsSpan(_at, (int)length).ToArray();
            _at += (int)length;
            return value;
        }
    }

    private sealed class SshWriter
    {
        private readonly MemoryStream _stream = new();

        public long Length => _stream.Length;

        public void Raw(ReadOnlySpan<byte> bytes) => _stream.Write(bytes);

        public void UInt32(uint value)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(b, value);
            _stream.Write(b);
        }

        public void String(ReadOnlySpan<byte> bytes)
        {
            UInt32((uint)bytes.Length);
            _stream.Write(bytes);
        }

        public byte[] ToArray() => _stream.ToArray();
    }
}
