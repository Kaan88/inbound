namespace DownloadManager.Contracts;

/// <summary>
/// The one rule for a pasted or chosen private key, shared by the Host (refused on save, before it is sealed) and the
/// windows (refused before it is sent), so each says the same thing. The texts never quote the key.
/// </summary>
public static class SiteKeyRules
{
    /// <summary>Why <paramref name="key"/> is not a private key file's text, or null when it looks like one.</summary>
    /// <remarks>
    /// rclone's key_pem takes PEM text: OpenSSH, PKCS#1 or PKCS#8, each between a <c>-----BEGIN ... PRIVATE KEY-----</c>
    /// line and its END line. A public key or a few pasted lines would only fail later, at a test. A PuTTY key (.ppk,
    /// SITES.11) is taken whole: the Host turns it into OpenSSH text when the site is saved, so it is only checked for
    /// its private part here; whether it opens is the Host's to say.
    /// </remarks>
    public static string? Problem(string key)
    {
        var text = key.Trim();
        if (text.StartsWith("PuTTY-User-Key-File-", StringComparison.Ordinal))
            return text.Contains("Private-Lines:", StringComparison.Ordinal) && text.Contains("Private-MAC:", StringComparison.Ordinal)
                ? null
                : "That PuTTY key file is cut short. Choose the .ppk file, or paste all of it.";
        if (text.StartsWith("---- BEGIN SSH2 PUBLIC KEY", StringComparison.Ordinal))
            return "That is a public key (the .pub file). Choose the private key, the file of the same name without .pub.";
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
}
