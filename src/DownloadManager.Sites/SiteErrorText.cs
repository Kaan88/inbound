namespace DownloadManager.Sites;

/// <summary>What <see cref="SiteErrorText.UploadFailure"/> found.</summary>
public enum UploadProblem
{
    None,
    LocalUnreadable,
    Refused,
    NotTaken,

    /// <summary>An FTP server's 4xx reply to the write ("stor: 450 …"): said in its own words, and tried again as usual.</summary>
    ServerDeclined,
}

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

    /// <summary>SITES.6c: the provider refused the stored token for good (revoked, expired refresh token); signing in again helps.</summary>
    public static bool IsSignInRefused(string message) =>
        Has(message, "invalid_grant") || Has(message, "token expired and there's no refresh token");

    /// <summary>
    /// Audit 2026-11-15: the server refuses this site as a whole (a wrong password or key, a refused or revoked sign-in,
    /// a certificate this PC doesn't trust), for every kind of site. Every file of a transfer would be refused the same
    /// way, and a server may lock the account after many tries, so a transfer stops instead of trying each file.
    /// </summary>
    public static bool RefusesTheSite(string message)
    {
        if (IsSignInRefused(message) || IsUntrustedCertificate(message)) return true;
        var at = message.IndexOf(CouldNotConnect, StringComparison.OrdinalIgnoreCase);
        if (at >= 0 && Has(message[(at + CouldNotConnect.Length)..], "unable to authenticate")) return true;
        var ftp = message.IndexOf(FtpConnection, StringComparison.OrdinalIgnoreCase);
        if (ftp >= 0 && message[(ftp + FtpConnection.Length)..].Contains(": 530 ", StringComparison.Ordinal)) return true;
        return Has(message, "401 Unauthorized") || Has(message, "api error SignatureDoesNotMatch") || Has(message, "api error InvalidAccessKeyId");
    }

    /// <summary>
    /// Audit 2026-11-15: the server could not be reached at all (SFTP, FTP, or a connection any kind makes over TCP), as
    /// opposed to a failure of one file. A transfer waits for the server to come back instead of using up each file's tries.
    /// </summary>
    /// <summary>
    /// SITES.11 R2 review: the connection itself was never made (no TCP connection, or the TLS handshake timed out), so no
    /// password or key reached the server. Narrower than <see cref="CantReach"/>: a handshake that failed after connecting
    /// (a ban, a reset) is left out, because a client may try this again on its own.
    /// </summary>
    public static bool DidNotAnswer(string message)
    {
        if (RefusesTheSite(message)) return false;
        if (Has(message, "TLS handshake timeout")) return true;
        var dial = message.IndexOf("dial tcp", StringComparison.OrdinalIgnoreCase);
        if (dial < 0) return false;
        var cause = message[dial..];
        return Has(cause, "no such host") || Has(cause, "actively refused") || Has(cause, "connection refused")
            || Has(cause, "did not properly respond") || Has(cause, "i/o timeout") || Has(cause, "timed out")
            || Has(cause, "no route to host") || Has(cause, "network is unreachable")
            || Has(cause, "unreachable network") || Has(cause, "unreachable host");
    }

    public static bool CantReach(string message)
    {
        if (RefusesTheSite(message)) return false;
        if (Has(message, CouldNotConnect) || Has(message, FtpConnection) || Has(message, "TLS handshake timeout")) return true;
        var dial = message.IndexOf("dial tcp", StringComparison.OrdinalIgnoreCase);
        if (dial < 0) return false;
        var cause = message[dial..];
        // "unreachable network" and "unreachable host" are Windows' words for no route; Linux says the other two.
        return Has(cause, "no such host") || Has(cause, "actively refused") || Has(cause, "connection refused")
            || Has(cause, "did not properly respond") || Has(cause, "i/o timeout") || Has(cause, "timed out")
            || Has(cause, "no route to host") || Has(cause, "network is unreachable")
            || Has(cause, "unreachable network") || Has(cause, "unreachable host");
    }

    public static string Friendly(string message)
    {
        if (IsSignInRefused(message)) return "The service no longer accepts this remote's sign-in. Sign in again in Options > Remote files.";
        var key = SiteKeyText.Friendly(message);
        if (!ReferenceEquals(key, message)) return key;

        // Only rclone's own connection failure is read, after its prefix, so a folder or user name with these words is not.
        var at = message.IndexOf(CouldNotConnect, StringComparison.OrdinalIgnoreCase);
        if (at >= 0)
        {
            var cause = message[(at + CouldNotConnect.Length)..];
            if (Has(cause, "unable to authenticate"))
                return "The server did not accept the sign-in. Check the user name, and the password or key, in Edit remote.";
            if (Has(cause, "no such host"))
                return "The server's name could not be found. Check the server's address in Edit remote.";
            if (Has(cause, "actively refused") || Has(cause, "connection refused"))
                return "The server refused the connection. Check the server's address and port, and that its SSH service is running.";
            if (Has(cause, "did not properly respond") || Has(cause, "i/o timeout") || Has(cause, "timed out")
                || Has(cause, "no route to host") || Has(cause, "network is unreachable"))
                return "The server did not answer. Check the server's address and port, and this PC's network.";
            return message;
        }

        // SITES.6: FTP, WebDAV and S3. Only rclone's and Go's own fixed phrases are read.
        if (IsUntrustedCertificate(message))
            return "The server's certificate is not trusted on this PC, so nothing was sent. Test the remote to see the certificate and choose whether to trust it.";
        if (Has(message, "x509:") || Has(message, "failed to verify certificate"))
            return "The server's certificate could not be checked (wrong name, or out of date), so nothing was sent. Check the address, or ask the server's owner about its certificate.";
        var ftp = message.IndexOf(FtpConnection, StringComparison.OrdinalIgnoreCase);
        if (ftp >= 0 && message[(ftp + FtpConnection.Length)..] is var ftpCause && ftpCause.Contains(": 530 ", StringComparison.Ordinal))
            return "The server did not accept the sign-in. Check the user name and password in Edit remote.";
        if (Has(message, "401 Unauthorized"))
            return "The server did not accept the sign-in. Check the user name and password in Edit remote.";
        if (Has(message, "api error SignatureDoesNotMatch") || Has(message, "api error InvalidAccessKeyId"))
            return "The service did not accept the access key ID or the secret access key. Check them in Edit remote.";
        if (Has(message, "api error AccessDenied"))
            return "The service refused this: the access key is not allowed to do it.";
        var dial = message.IndexOf("dial tcp", StringComparison.OrdinalIgnoreCase);
        if (dial >= 0)
        {
            var cause = message[dial..];
            if (Has(cause, "no such host")) return "The server's name could not be found. Check the server's address in Edit remote.";
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

    /// <summary>
    /// SITES.11: why an upload's file failed, when it is one that trying again won't fix: this PC's file could not be read,
    /// the server refused the write (a read-only share, a sign-in that may only read), or a WebDAV server answered 404 to
    /// the copy (the folder is gone, or the server won't write). Only rclone's, Go's and the protocols' own phrases are
    /// read, and the file's and folders' own names (<paramref name="names"/>, as paths relative to the batch) are taken
    /// out first, so a file called "403 Forbidden.txt" is not one. Phrases from rclone 1.76.0 against read-only servers;
    /// FTP's 450 is not read (rclone's own server says it for other things too).
    /// </summary>
    public static UploadProblem UploadFailure(string message, string localFolder, params string?[] names)
    {
        // A file another program holds open (Windows' sharing violation) may be free at the next try: the usual tries.
        if (Has(message, "being used by another process") || Has(message, "The process cannot access the file")) return UploadProblem.None;
        if (Has(message, "failed to open source object") || LocalUnreadable(message, localFolder)) return UploadProblem.LocalUnreadable;
        var text = WithoutNames(message, names);
        if (Has(text, "403 Forbidden") || Has(text, "405 Method Not Allowed")
            || Has(text, "SSH_FX_PERMISSION_DENIED") || Has(text, "SSH_FX_WRITE_PROTECT")
            || Has(text, "sftp: \"Read only file system\"")
            || Has(text, ": permission denied") || Has(text, ": read-only file system"))
            return UploadProblem.Refused;
        // FTP: the reply code right after rclone's "stor: " (also "update stor: ").
        var stor = text.LastIndexOf("stor: ", StringComparison.OrdinalIgnoreCase);
        if (stor >= 0 && text[(stor + 6)..] is var reply)
        {
            if (reply.StartsWith("550 ", StringComparison.Ordinal) || reply.StartsWith("553 ", StringComparison.Ordinal)) return UploadProblem.Refused;
            // 4xx is FTP's "not now": the server's own words, and the file's usual tries.
            if (reply.Length >= 4 && reply[0] == '4' && char.IsAsciiDigit(reply[1]) && char.IsAsciiDigit(reply[2]) && reply[3] == ' ')
                return UploadProblem.ServerDeclined;
        }

        if (Has(text, "Not Found: 404")
            && (Has(text, "operations/copyfile") || Has(text, "operations/movefile") || Has(text, "operations/mkdir") || Has(text, "moveto")))
            return UploadProblem.NotTaken;
        return UploadProblem.None;
    }

    /// <summary>The words for <see cref="UploadFailure"/>; <see cref="UploadProblem.None"/> is <see cref="Friendly"/>.</summary>
    public static string UploadText(UploadProblem problem, string message, string siteName, string folder, string fileName) => problem switch
    {
        UploadProblem.LocalUnreadable => $"This PC's file could not be read: {Leaf(fileName)}.",
        UploadProblem.Refused => $"{siteName} refused the write to {folder}. Check that this sign-in may write there.",
        UploadProblem.NotTaken => $"{siteName} did not take the file in {folder}: the folder is gone or can't be written.",
        UploadProblem.ServerDeclined => $"{siteName} did not take the file in {folder}: {ServerWords(message)}",
        _ => Friendly(message),
    };

    // Go's "open <path>: permission denied" (or Windows' "Access is denied.") about a path in the batch's folder on this PC.
    private static bool LocalUnreadable(string message, string localFolder)
    {
        var folder = localFolder.Replace('\\', '/').TrimEnd('/');
        if (folder.Length == 0) return false;
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(message,
                     "open (?<path>[^\r\n]+?): (permission denied|Access is denied)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            var path = match.Groups["path"].Value.Trim('"').Replace('\\', '/');
            if (path.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    // The names (and each folder of them) where they stand as a path part: after a slash, a backslash or a quote, and before
    // one of those, a colon or the end (so a folder "Read" leaves sftp's "Read only file system" alone).
    private static string WithoutNames(string message, string?[] names)
    {
        var parts = names.Where(name => !string.IsNullOrEmpty(name))
            .SelectMany(name => name!.Split('/', '\\').Append(name!))
            .Where(part => part.Length > 0).Distinct(StringComparer.Ordinal).OrderByDescending(part => part.Length);
        foreach (var part in parts)
        {
            message = System.Text.RegularExpressions.Regex.Replace(message, "(?<=[/\\\\\"])" + System.Text.RegularExpressions.Regex.Escape(part) + "(?=[/\\\\\":]|$)", "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        return message;
    }

    // An FTP reply after rclone's "stor: ": the words in its quotes, else what follows the code.
    private static string ServerWords(string message)
    {
        var stor = message.LastIndexOf("stor: ", StringComparison.OrdinalIgnoreCase);
        var reply = stor >= 0 ? message[(stor + 6)..].Trim() : message.Trim();
        var open = reply.IndexOf('"');
        var close = reply.LastIndexOf('"');
        if (open >= 0 && close > open) return reply[(open + 1)..close];
        return reply.Length > 4 ? reply[4..].Trim() : reply;
    }

    private static string Leaf(string path)
    {
        var trimmed = path.TrimEnd('/', '\\');
        var cut = trimmed.LastIndexOfAny(['/', '\\']);
        return cut < 0 ? trimmed : trimmed[(cut + 1)..];
    }

    private static bool Has(string text, string part) => text.Contains(part, StringComparison.OrdinalIgnoreCase);
}
