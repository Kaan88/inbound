namespace DownloadManager.Contracts;

/// <summary>
/// SITES.11 (decision 7, "Open an address once"): a typed address read as a site. <c>sftp://</c>, <c>ftp://</c> (plain),
/// <c>ftps://</c> (explicit TLS), <c>http://</c> and <c>https://</c> (WebDAV), each with an optional <c>user@</c>, port
/// and folder. A password in the address, a <c>?</c> or <c>#</c> part, control characters and very long input are refused:
/// a password is asked for in a box of its own, never carried in an address that may end up in a history.
/// </summary>
public sealed record SiteAddress(string Kind, string Host, int Port, string User, string? Tls, string? Url, string Folder)
{
    public const int MaxLength = 2048;

    /// <summary>The site this address opens, or null with the reason.</summary>
    public static SiteAddress? Parse(string? text, out string? problem)
    {
        problem = null;
        var address = text?.Trim() ?? "";
        if (address.Length == 0)
        {
            problem = "Type an address, such as sftp://user@server/folder.";
            return null;
        }

        if (address.Length > MaxLength || address.Any(char.IsControl))
        {
            problem = "That is not an address.";
            return null;
        }

        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.DnsSafeHost))
        {
            problem = "Start the address with sftp://, ftp://, ftps://, https:// or http://, then the server's name.";
            return null;
        }

        var (kind, tls, defaultPort) = uri.Scheme switch
        {
            "sftp" => (SiteKindCatalog.Sftp, (string?)null, 22),
            "ftp" => (SiteKindCatalog.Ftp, SiteTlsModes.None, 21),
            "ftps" => (SiteKindCatalog.Ftp, SiteTlsModes.Explicit, 21),
            "http" or "https" => (SiteKindCatalog.WebDav, null, uri.Scheme == "https" ? 443 : 80),
            _ => ("", null, 0),
        };
        if (kind.Length == 0)
        {
            problem = "Only sftp://, ftp://, ftps://, https:// and http:// addresses can be opened.";
            return null;
        }

        var user = Uri.UnescapeDataString(uri.UserInfo);
        if (user.Contains(':', StringComparison.Ordinal))
        {
            problem = "Leave the password out of the address; you are asked for it when the server wants one.";
            return null;
        }

        // Read from the text too: for ftp:// the parser keeps a ? in the path.
        if (uri.Query.Length > 0 || uri.Fragment.Length > 0 || address.IndexOfAny(['?', '#']) >= 0)
        {
            problem = "The address can't have a ? or # part.";
            return null;
        }

        if (user.Any(char.IsControl) || user.Contains('@', StringComparison.Ordinal))
        {
            problem = "That is not a user name.";
            return null;
        }

        var port = uri.IsDefaultPort || uri.Port < 0 ? defaultPort : uri.Port;
        var folder = Uri.UnescapeDataString(uri.AbsolutePath);
        if (folder.Any(char.IsControl))
        {
            problem = "That is not a folder.";
            return null;
        }

        string? url = null;
        if (kind == SiteKindCatalog.WebDav)
        {
            // The whole address is the WebDAV server's (its path is part of it); the site opens at its root.
            url = new UriBuilder(uri) { UserName = "", Password = "" }.Uri.AbsoluteUri;
            if (SiteKindCatalog.AddressProblem(url, "address") is { } urlProblem)
            {
                problem = urlProblem;
                return null;
            }

            folder = "/";
        }

        return new SiteAddress(kind, uri.DnsSafeHost, port, user, tls, url, folder.Length == 0 ? "/" : folder);
    }

    /// <summary>Whether the site's traffic is encrypted, as <see cref="SiteKindCatalog.Encrypted"/>.</summary>
    public bool Encrypted => SiteKindCatalog.Encrypted(Kind, Tls, Url, null);
}
