namespace DownloadManager.Core;

/// <summary>
/// Decides whether a saved <see cref="SiteLogin.Site"/> applies to a request address. The site is a host,
/// host:port, host/path prefix, or a full http(s)/ftp(s) URL. Shared by the HTTP and FTP engines.
/// </summary>
public static class SiteLoginMatcher
{
    private static readonly HashSet<string> SiteSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "http", "https", "ftp", "ftps",
    };

    /// <param name="allowSubdomains">True when a login for example.com also applies to a.example.com.</param>
    public static bool Matches(string? site, Uri uri, bool allowSubdomains)
    {
        if (string.IsNullOrWhiteSpace(site)) return false;
        site = site.Trim();

        string? scheme = null;
        string host;
        int? port = null;
        string path;
        // "example.test:8080" parses as an absolute URI with the scheme "example.test", so only a
        // scheme this app downloads over counts as one; anything else is host[:port][/path].
        if (Uri.TryCreate(site, UriKind.Absolute, out var siteUri) && SiteSchemes.Contains(siteUri.Scheme) &&
            site.Contains("://", StringComparison.Ordinal))
        {
            scheme = siteUri.Scheme;
            host = siteUri.Host;
            port = siteUri.IsDefaultPort ? null : siteUri.Port;
            path = siteUri.AbsolutePath;
        }
        else
        {
            var separator = site.IndexOf('/');
            var authority = separator < 0 ? site : site[..separator];
            path = separator < 0 ? "/" : site[separator..];
            if (authority.Length == 0 ||
                !Uri.TryCreate($"http://{authority}", UriKind.Absolute, out var authorityUri))
            {
                return false;
            }

            host = authorityUri.Host;
            port = authority.Contains(':', StringComparison.Ordinal) ? authorityUri.Port : null;
        }

        // A login saved for a scheme is sent over that scheme only, or over its secure form
        // (http to https, ftp to ftps); never from https down to http.
        if (scheme is not null && !string.Equals(scheme, uri.Scheme, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(scheme + "s", uri.Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var hostMatches = string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase) ||
                          (allowSubdomains && uri.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase));
        if (!hostMatches) return false;

        if (port is not null && uri.Port != port.Value) return false;

        if (path is "" or "/") return true;

        var normalizedPath = path.TrimEnd('/');
        return uri.AbsolutePath.StartsWith(normalizedPath, StringComparison.OrdinalIgnoreCase) &&
               (uri.AbsolutePath.Length == normalizedPath.Length ||
                uri.AbsolutePath[normalizedPath.Length] == '/');
    }
}
