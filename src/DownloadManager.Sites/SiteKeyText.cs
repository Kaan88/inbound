namespace DownloadManager.Sites;

/// <summary>
/// Plain words for a private key that cannot be used: one refused when the site is saved, before it is sealed, and
/// rclone's own "failed to parse private key" errors when it is used. The texts never quote the key.
/// </summary>
public static class SiteKeyText
{
    /// <summary>Why <paramref name="key"/> is not a private key file's text, or null when it looks like one.</summary>
    /// <remarks>
    /// rclone's key_pem takes PEM text: OpenSSH, PKCS#1 or PKCS#8, each between a <c>-----BEGIN ... PRIVATE KEY-----</c>
    /// line and its END line. A public key, a PuTTY key or a few pasted lines would only fail later, at a test.
    /// </remarks>
    public static string? Problem(string key)
    {
        var text = key.Trim();
        if (text.StartsWith("PuTTY-User-Key-File-", StringComparison.Ordinal))
            return "PuTTY keys (.ppk) can't be used. Save the key in OpenSSH format with PuTTYgen (Conversions › Export OpenSSH key) and choose that file.";
        var begin = text.IndexOf("-----BEGIN ", StringComparison.Ordinal);
        if (begin < 0 || text.IndexOf("PRIVATE KEY-----", begin, StringComparison.Ordinal) < 0
            || text.IndexOf("-----END ", begin, StringComparison.Ordinal) < 0)
        {
            return text.StartsWith("ssh-", StringComparison.Ordinal) || text.StartsWith("ecdsa-", StringComparison.Ordinal)
                ? "That is a public key (the .pub file). Choose the private key, the file of the same name without .pub."
                : "That is not a private key. Choose the key file, or paste all of it, from its -----BEGIN line to its -----END line.";
        }

        return null;
    }

    /// <summary>Plain words for rclone's key errors; any other message comes back as it was.</summary>
    public static string Friendly(string message)
    {
        if (message.Contains("this private key is passphrase protected", StringComparison.OrdinalIgnoreCase))
            return "The private key has a passphrase. Enter it in Edit site.";
        if (message.Contains("decryption password incorrect", StringComparison.OrdinalIgnoreCase))
            return "The key passphrase is wrong. Enter it again in Edit site.";
        if (message.Contains("failed to parse private key", StringComparison.OrdinalIgnoreCase))
            return "The private key could not be read. Choose the key file again in Edit site.";
        return message;
    }
}
