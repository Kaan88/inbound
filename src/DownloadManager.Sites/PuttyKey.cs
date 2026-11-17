using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DownloadManager.Sites;

/// <summary>
/// SITES.14: a PuTTY private key file (.ppk, format 2 or 3) turned into the OpenSSH private key text rclone's
/// <c>key_pem</c> takes. RSA, Ed25519 and ECDSA (P-256, P-384, P-521). The file's MAC is checked, so a damaged file or a
/// wrong passphrase is refused rather than turned into a broken key. A format 2 key with a passphrase is opened here
/// (AES-256-CBC, SHA-1 derived key); a format 3 key with a passphrase needs Argon2, which .NET does not have, so it is
/// refused with the PuTTYgen steps. The OpenSSH text has no passphrase of its own: it is sealed like any key, and the
/// passphrase is not kept. Nothing here writes a file, and no message quotes the key.
/// </summary>
public static class PuttyKey
{
    private const string Header = "PuTTY-User-Key-File-";

    public const string ExportSteps = "Convert it with PuTTYgen › Conversions › Export OpenSSH key, and choose that file.";

    /// <summary>Whether <paramref name="text"/> is a PuTTY key file's text.</summary>
    public static bool IsPutty(string? text) => text is not null && text.TrimStart().StartsWith(Header, StringComparison.Ordinal);

    /// <summary>Whether the PuTTY key is protected with a passphrase (its <c>Encryption</c> is not <c>none</c>).</summary>
    public static bool IsEncrypted(string text)
        => Fields(text, out _) is { } fields && fields.TryGetValue("Encryption", out var cipher) && cipher != "none";

    /// <summary>
    /// The OpenSSH private key text for <paramref name="text"/>, or the reason it can't be made. <paramref name="passphrase"/>
    /// is used only when the key has one.
    /// </summary>
    public static (string? OpenSsh, string? Problem) ToOpenSsh(string text, string? passphrase)
    {
        try
        {
            return (Convert(text, passphrase), null);
        }
        catch (PuttyKeyException e)
        {
            return (null, e.Message);
        }
        catch (Exception e) when (e is FormatException or CryptographicException or ArgumentException or OverflowException or IndexOutOfRangeException)
        {
            return (null, "The PuTTY key file could not be read. " + ExportSteps);
        }
    }

    private sealed class PuttyKeyException(string message) : Exception(message);

    private static string Convert(string text, string? passphrase)
    {
        var fields = Fields(text, out var version) ?? throw new PuttyKeyException("The PuTTY key file could not be read. " + ExportSteps);
        if (version is not (2 or 3))
            throw new PuttyKeyException("This PuTTY key file is too old to read. " + ExportSteps);
        var algorithm = Need(fields, "Algorithm");
        var cipher = Need(fields, "Encryption");
        var comment = fields.GetValueOrDefault("Comment") ?? "";
        var publicBlob = System.Convert.FromBase64String(Need(fields, "Public-Lines"));
        var privateBlob = System.Convert.FromBase64String(Need(fields, "Private-Lines"));
        var mac = System.Convert.FromHexString(Need(fields, "Private-MAC"));
        if (algorithm is not ("ssh-rsa" or "ssh-ed25519" or "ecdsa-sha2-nistp256" or "ecdsa-sha2-nistp384" or "ecdsa-sha2-nistp521"))
            throw new PuttyKeyException(algorithm == "ssh-dss"
                ? "DSA keys can't be used: the servers rclone talks to refuse them. Make an Ed25519 or RSA key."
                : $"PuTTY keys of type {algorithm} can't be used here. " + ExportSteps);

        var encrypted = cipher != "none";
        if (encrypted && cipher != "aes256-cbc") throw new PuttyKeyException("This PuTTY key's encryption is not known. " + ExportSteps);
        if (encrypted && string.IsNullOrEmpty(passphrase))
            throw new PuttyKeyException("The PuTTY key has a passphrase. Type it in Key passphrase.");
        var secret = encrypted ? Encoding.UTF8.GetBytes(passphrase!) : [];

        byte[] macKey;
        if (version == 2)
        {
            if (encrypted)
            {
                var key = new byte[32];
                SHA1.HashData([0, 0, 0, 0, .. secret]).AsSpan(0, 20).CopyTo(key);
                SHA1.HashData([0, 0, 0, 1, .. secret]).AsSpan(0, 12).CopyTo(key.AsSpan(20));
                privateBlob = Decrypt(privateBlob, key, new byte[16]);
            }

            macKey = SHA1.HashData([.. "putty-private-key-file-mac-key"u8, .. secret]);
        }
        else
        {
            // Format 3: the key, IV and MAC key come from Argon2, which .NET doesn't have. Unencrypted, the MAC key is empty.
            if (encrypted)
                throw new PuttyKeyException("PuTTY keys of format 3 with a passphrase can't be opened here. " + ExportSteps);
            macKey = [];
        }

        var macData = new SshWriter();
        macData.String(Encoding.ASCII.GetBytes(algorithm));
        macData.String(Encoding.ASCII.GetBytes(cipher));
        macData.String(Encoding.UTF8.GetBytes(comment));
        macData.String(publicBlob);
        macData.String(privateBlob);
        var expected = version == 2 ? HMACSHA1.HashData(macKey, macData.ToArray()) : HMACSHA256.HashData(macKey, macData.ToArray());
        if (!CryptographicOperations.FixedTimeEquals(expected, mac))
            throw new PuttyKeyException(encrypted ? "The key passphrase is wrong." : "The PuTTY key file is damaged (its check failed). " + ExportSteps);

        return OpenSshText(algorithm, comment, publicBlob, privateBlob);
    }

    private static byte[] Decrypt(byte[] data, byte[] key, byte[] iv)
    {
        if (data.Length % 16 != 0) throw new PuttyKeyException("The PuTTY key file is damaged. " + ExportSteps);
        using var aes = Aes.Create();
        aes.Key = key;
        return aes.DecryptCbc(data, iv, PaddingMode.None);
    }

    private static string Need(Dictionary<string, string> fields, string name)
        => fields.TryGetValue(name, out var value) ? value : throw new PuttyKeyException("The PuTTY key file is incomplete. " + ExportSteps);

    /// <summary>
    /// The file's fields, with <c>Public-Lines</c> and <c>Private-Lines</c> holding their joined base64, and the first
    /// line's algorithm as <c>Algorithm</c>. Null when the text is not a PuTTY key file.
    /// </summary>
    private static Dictionary<string, string>? Fields(string text, out int version)
    {
        version = 0;
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        var index = 0;
        while (index < lines.Length && lines[index].Trim().Length == 0) index++;
        if (index >= lines.Length) return null;
        var first = lines[index].Trim();
        if (!first.StartsWith(Header, StringComparison.Ordinal)) return null;
        var colon = first.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0 || !int.TryParse(first[Header.Length..colon], NumberStyles.None, CultureInfo.InvariantCulture, out version)) return null;
        fields["Algorithm"] = first[(colon + 1)..].Trim();
        for (index++; index < lines.Length; index++)
        {
            // Header lines are not trimmed: the comment is part of the MAC, so its own spaces count, and PuTTYgen
            // writes an empty comment as "Comment: " (or "Comment:" once an editor strips the trailing space).
            var line = lines[index];
            if (line.Trim().Length == 0) continue;
            colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0) return null;
            var name = line[..colon];
            var value = line[(colon + 1)..];
            if (name == "Comment") { if (value.StartsWith(' ')) value = value[1..]; }
            else value = value.Trim();
            if (name is "Public-Lines" or "Private-Lines")
            {
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count > 1000 || index + count >= lines.Length)
                    return null;
                var joined = new StringBuilder();
                for (var at = 0; at < count; at++) joined.Append(lines[++index].Trim());
                value = joined.ToString();
            }

            fields[name] = value;
        }

        return fields;
    }

    /// <summary>The <c>openssh-key-v1</c> file, unencrypted, for the key in PuTTY's public and private blobs.</summary>
    private static string OpenSshText(string algorithm, string comment, byte[] publicBlob, byte[] privateBlob)
    {
        var pub = new SshReader(publicBlob);
        var priv = new SshReader(privateBlob);
        if (pub.Text() != algorithm) throw new PuttyKeyException("The PuTTY key file does not match its own type. " + ExportSteps);

        var key = new SshWriter();
        key.String(Encoding.ASCII.GetBytes(algorithm));
        switch (algorithm)
        {
            case "ssh-rsa":
            {
                var e = pub.Bytes();
                var n = pub.Bytes();
                var d = priv.Bytes();
                var p = priv.Bytes();
                var q = priv.Bytes();
                var iqmp = priv.Bytes();
                foreach (var part in new[] { n, e, d, iqmp, p, q }) key.String(part);
                break;
            }
            case "ssh-ed25519":
            {
                var publicKey = pub.Bytes();
                var seed = priv.Bytes();
                if (publicKey.Length != 32 || seed.Length is not (32 or 64))
                    throw new PuttyKeyException("The PuTTY key file is damaged. " + ExportSteps);
                key.String(publicKey);
                key.String([.. seed.AsSpan(0, 32), .. publicKey]);
                break;
            }
            default:
            {
                var curve = pub.Bytes();
                var point = pub.Bytes();
                key.String(curve);
                key.String(point);
                key.String(priv.Bytes());
                break;
            }
        }

        key.String(Encoding.UTF8.GetBytes(comment));

        var check = RandomNumberGenerator.GetBytes(4);
        var section = new SshWriter();
        section.Raw(check);
        section.Raw(check);
        section.Raw(key.ToArray());
        for (byte pad = 1; section.Length % 8 != 0; pad++) section.Raw([pad]);

        var file = new SshWriter();
        file.Raw("openssh-key-v1\0"u8.ToArray());
        file.String("none"u8.ToArray());
        file.String("none"u8.ToArray());
        file.String([]);
        file.Raw([0, 0, 0, 1]);
        file.String(publicBlob);
        file.String(section.ToArray());

        var body = System.Convert.ToBase64String(file.ToArray());
        var pem = new StringBuilder("-----BEGIN OPENSSH PRIVATE KEY-----\n");
        for (var at = 0; at < body.Length; at += 70) pem.Append(body, at, Math.Min(70, body.Length - at)).Append('\n');
        return pem.Append("-----END OPENSSH PRIVATE KEY-----\n").ToString();
    }

    private sealed class SshReader(byte[] data)
    {
        private int _at;

        public byte[] Bytes()
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(_at, 4));
            if (length > data.Length - _at - 4) throw new PuttyKeyException("The PuTTY key file is damaged. " + ExportSteps);
            var value = data.AsSpan(_at + 4, (int)length).ToArray();
            _at += 4 + (int)length;
            return value;
        }

        public string Text() => Encoding.ASCII.GetString(Bytes());
    }

    private sealed class SshWriter
    {
        private readonly MemoryStream _stream = new();

        public long Length => _stream.Length;

        public void Raw(byte[] bytes) => _stream.Write(bytes);

        public void String(byte[] bytes)
        {
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(length, (uint)bytes.Length);
            _stream.Write(length);
            _stream.Write(bytes);
        }

        public byte[] ToArray() => _stream.ToArray();
    }
}
