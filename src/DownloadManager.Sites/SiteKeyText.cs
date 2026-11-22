using DownloadManager.Contracts;

namespace DownloadManager.Sites;

/// <summary>
/// Plain words for a private key that cannot be used: one refused when the site is saved, before it is sealed, and
/// rclone's own "failed to parse private key" errors when it is used. The texts never quote the key.
/// </summary>
public static class SiteKeyText
{
    private const string ParseFailed = "failed to parse private key";

    /// <summary>Why <paramref name="key"/> is not a private key file's text, or null when it looks like one.</summary>
    public static string? Problem(string key) => SiteKeyRules.Problem(key);

    /// <summary>
    /// Whether <paramref name="key"/> is PEM text that opens without a passphrase: an OpenSSH key with cipher "none", an
    /// unencrypted PKCS#8 key, or a PKCS#1/SEC1 key without an ENCRYPTED header. rclone refuses a passphrase for such a key
    /// ("ssh: key is not password protected").
    /// </summary>
    public static bool IsUnencrypted(string key)
    {
        var text = key.Trim();
        const string OpenSshBegin = "-----BEGIN OPENSSH PRIVATE KEY-----";
        if (text.StartsWith(OpenSshBegin, StringComparison.Ordinal))
        {
            var end = text.IndexOf("-----END ", StringComparison.Ordinal);
            if (end < 0) return false;
            try
            {
                var data = Convert.FromBase64String(string.Concat(text[OpenSshBegin.Length..end].Split((char[])['\r', '\n', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries)));
                // "openssh-key-v1\0", then the cipher name as an SSH string.
                if (data.Length < 19 || !data.AsSpan(0, 15).SequenceEqual("openssh-key-v1\0"u8)) return false;
                var length = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(15));
                return length == 4 && data.Length >= 23 && data.AsSpan(19, 4).SequenceEqual("none"u8);
            }
            catch (FormatException)
            {
                return false;
            }
        }

        if (text.StartsWith("-----BEGIN PRIVATE KEY-----", StringComparison.Ordinal)) return true;
        return (text.StartsWith("-----BEGIN RSA PRIVATE KEY-----", StringComparison.Ordinal) || text.StartsWith("-----BEGIN EC PRIVATE KEY-----", StringComparison.Ordinal))
            && !text.Contains("ENCRYPTED", StringComparison.Ordinal);
    }

    /// <summary>Plain words for rclone's key errors; any other message comes back as it was.</summary>
    /// <remarks>
    /// rclone wraps every key error in "failed to parse private key file: ...". Only text after that is read, so a folder
    /// or server message that happens to contain the same words is not taken for a key problem.
    /// </remarks>
    public static string Friendly(string message)
    {
        var at = message.IndexOf(ParseFailed, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return message;
        var cause = message[(at + ParseFailed.Length)..];
        if (cause.Contains("this private key is passphrase protected", StringComparison.OrdinalIgnoreCase))
            return "The private key has a passphrase. Enter it in Edit site.";
        if (cause.Contains("decryption password incorrect", StringComparison.OrdinalIgnoreCase))
            return "The key passphrase is wrong. Enter it again in Edit site.";
        return "The private key could not be read. Choose the key file again in Edit site.";
    }
}
