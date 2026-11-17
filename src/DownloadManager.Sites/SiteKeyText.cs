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
            return "The private key has a passphrase. Enter it in Edit remote.";
        if (cause.Contains("decryption password incorrect", StringComparison.OrdinalIgnoreCase))
            return "The key passphrase is wrong. Enter it again in Edit remote.";
        return "The private key could not be read. Choose the key file again in Edit remote.";
    }
}
