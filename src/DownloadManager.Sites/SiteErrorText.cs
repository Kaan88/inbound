namespace DownloadManager.Sites;

/// <summary>
/// Plain words for rclone's errors when a site is used (browse, Test). The texts were taken from rclone 1.76.0 runs on
/// Windows and the Go network errors it wraps on Linux; anything not recognised comes back as it was, already redacted by
/// <see cref="RcloneClient"/>.
/// </summary>
public static class SiteErrorText
{
    private const string CouldNotConnect = "couldn't connect SSH";
    private const string FtpConnection = "failed to make FTP connection to";

    /// <summary>
    /// SITES.6b: Go's "unknown authority" (a self-signed or private certificate), the one failure that trusting the
    /// certificate fixes; a wrong name or an expired certificate is not this.
    /// </summary>
    public static bool IsUntrustedCertificate(string message) =>
        Has(message, "x509: certificate signed by unknown authority") || Has(message, "x509: certificate is not trusted");

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

        // SITES.6: FTP, WebDAV and S3. Only rclone's and Go's own fixed phrases are read.
        if (IsUntrustedCertificate(message))
            return "The server's certificate is not trusted on this PC, so nothing was sent. Test the site to see the certificate and choose whether to trust it.";
        if (Has(message, "x509:") || Has(message, "failed to verify certificate"))
            return "The server's certificate could not be checked (wrong name, or out of date), so nothing was sent. Check the address, or ask the server's owner about its certificate.";
        var ftp = message.IndexOf(FtpConnection, StringComparison.OrdinalIgnoreCase);
        if (ftp >= 0 && message[(ftp + FtpConnection.Length)..] is var ftpCause && ftpCause.Contains(": 530 ", StringComparison.Ordinal))
            return "The server did not accept the sign-in. Check the user name and password in Edit site.";
        if (Has(message, "401 Unauthorized"))
            return "The server did not accept the sign-in. Check the user name and password in Edit site.";
        if (Has(message, "api error SignatureDoesNotMatch") || Has(message, "api error InvalidAccessKeyId"))
            return "The service did not accept the access key ID or the secret access key. Check them in Edit site.";
        if (Has(message, "api error AccessDenied"))
            return "The service refused this: the access key is not allowed to do it.";
        var dial = message.IndexOf("dial tcp", StringComparison.OrdinalIgnoreCase);
        if (dial >= 0)
        {
            var cause = message[dial..];
            if (Has(cause, "no such host")) return "The server's name could not be found. Check the server's address in Edit site.";
            if (Has(cause, "actively refused") || Has(cause, "connection refused"))
                return "The server refused the connection. Check the server's address and port, and that the server is running.";
            if (Has(cause, "did not properly respond") || Has(cause, "i/o timeout") || Has(cause, "timed out")
                || Has(cause, "no route to host") || Has(cause, "network is unreachable"))
                return "The server did not answer. Check the server's address and port, and this PC's network.";
        }

        // rclone ends a listing of a missing folder with this.
        if (message.TrimEnd().EndsWith("directory not found", StringComparison.OrdinalIgnoreCase))
            return "That folder is not on the server.";
        return message;
    }

    private static bool Has(string text, string part) => text.Contains(part, StringComparison.OrdinalIgnoreCase);
}
