namespace DownloadManager.Core;

/// <summary>
/// Decides whether a saved <see cref="SiteLogin.Site"/> applies to a request address. The site is a host,
/// host:port, host/path prefix, or a full http(s) URL; the host may start with <c>*.</c> to take in its subdomains.
/// </summary>
/// <remarks>
/// SEC.L1 (review 1 N4/L12): a site saved without a scheme is sent over https only, to exactly that host. A site
/// saved with http:// is the user's explicit choice and is sent over http or https to that host. Subdomains match
/// only when the site says so with <c>*.example.com</c>, which also takes in example.com itself.
/// </remarks>
public static class SiteLoginMatcher
{
    private static readonly HashSet<string> SiteSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "http", "https",
    };

    public static bool Matches(string? site, Uri uri)
    {
        // Logins are only ever sent over http or https (FTP was cut in 0.7.2).
        if (string.IsNullOrWhiteSpace(site) || uri.Scheme is not ("http" or "https")) return false;
        site = site.Trim();

        string? scheme = null;
        var rest = site;
        var schemeEnd = site.IndexOf("://", StringComparison.Ordinal);
        // Text before "://" is a scheme only when it holds no '/', '?', '#' or ':'; otherwise the "://" sits in the
        // path or query of a scheme-less site ("example.com/get?u=https://x") and the whole site is host[:port][/path].
        if (schemeEnd > 0 && site.AsSpan(0, schemeEnd).IndexOfAny("/?#:") < 0)
        {
            scheme = site[..schemeEnd];
            // Only a scheme this app downloads over counts; a login saved as ftp://… or the like never matches.
            if (!SiteSchemes.Contains(scheme)) return false;
            rest = site[(schemeEnd + 3)..];
        }

        var queryStart = rest.IndexOfAny(['?', '#']);
        if (queryStart >= 0) rest = rest[..queryStart];
        var separator = rest.IndexOf('/');
        var authority = separator < 0 ? rest : rest[..separator];
        var path = separator < 0 ? "/" : rest[separator..];
        var wildcard = authority.StartsWith("*.", StringComparison.Ordinal);
        if (wildcard) authority = authority[2..];
        if (authority.Length == 0 || authority.Contains('*', StringComparison.Ordinal) ||
            !Uri.TryCreate($"{scheme ?? "https"}://{authority}", UriKind.Absolute, out var authorityUri))
        {
            return false;
        }

        var host = authorityUri.Host;
        int? port = HasExplicitPort(authority) ? authorityUri.Port : null;

        // No scheme: https only. http://: http or its secure form. https://: https only.
        var schemeAllowed = scheme is null
            ? uri.Scheme == "https"
            : string.Equals(scheme, uri.Scheme, StringComparison.OrdinalIgnoreCase) ||
              (string.Equals(scheme, "http", StringComparison.OrdinalIgnoreCase) && uri.Scheme == "https");
        if (!schemeAllowed) return false;

        var hostMatches = string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase) ||
                          (wildcard && uri.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase));
        if (!hostMatches) return false;

        // A port is checked only when the site names one.
        if (port is not null && uri.Port != port.Value) return false;

        if (path is "" or "/") return true;

        var normalizedPath = path.TrimEnd('/');
        return uri.AbsolutePath.StartsWith(normalizedPath, StringComparison.OrdinalIgnoreCase) &&
               (uri.AbsolutePath.Length == normalizedPath.Length ||
                uri.AbsolutePath[normalizedPath.Length] == '/');
    }

    private static bool HasExplicitPort(string authority)
    {
        // "[::1]:8080" or "host:8080"; the colons inside an IPv6 literal are not a port.
        var close = authority.LastIndexOf(']');
        return authority.IndexOf(':', close + 1) >= 0;
    }
}
