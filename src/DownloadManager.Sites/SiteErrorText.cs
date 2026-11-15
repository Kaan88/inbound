namespace DownloadManager.Sites;

/// <summary>
/// Plain words for rclone's errors when a site is used (browse, Test). The texts were taken from rclone 1.76.0 runs on
/// Windows and the Go network errors it wraps on Linux; anything not recognised comes back as it was, already redacted by
/// <see cref="RcloneClient"/>.
/// </summary>
public static class SiteErrorText
{
    private const string CouldNotConnect = "couldn't connect SSH";

    public static string Friendly(string message)
    {
        var key = SiteKeyText.Friendly(message);
        if (!ReferenceEquals(key, message)) return key;

        // Only rclone's own connection failure is read, after its prefix, so a folder or user name with these words is not.
        var at = message.IndexOf(CouldNotConnect, StringComparison.OrdinalIgnoreCase);
        if (at >= 0)
        {
            var cause = message[(at + CouldNotConnect.Length)..];
            if (Has(cause, "unable to authenticate"))
                return "The server did not accept the sign-in. Check the user name, and the password or key, in Edit site.";
            if (Has(cause, "no such host"))
                return "The server's name could not be found. Check the server's address in Edit site.";
            if (Has(cause, "actively refused") || Has(cause, "connection refused"))
                return "The server refused the connection. Check the server's address and port, and that its SSH service is running.";
            if (Has(cause, "did not properly respond") || Has(cause, "i/o timeout") || Has(cause, "timed out")
                || Has(cause, "no route to host") || Has(cause, "network is unreachable"))
                return "The server did not answer. Check the server's address and port, and this PC's network.";
            return message;
        }

        // rclone ends a listing of a missing folder with this.
        if (message.TrimEnd().EndsWith("directory not found", StringComparison.OrdinalIgnoreCase))
            return "That folder is not on the server.";
        return message;
    }

    private static bool Has(string text, string part) => text.Contains(part, StringComparison.OrdinalIgnoreCase);
}
