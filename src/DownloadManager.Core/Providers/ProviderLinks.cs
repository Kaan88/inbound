namespace DownloadManager.Core.Providers;

/// <summary>
/// Recognises direct provider links that users paste. These carry the account key in their
/// query, so the address itself is a credential.
/// </summary>
public static class ProviderLinks
{
    /// <summary>What the provider allows for one file.</summary>
    public const int MaxConnections = 4;

    private const string CdnLabel = "tb-cdn";
    private const string ApiDomain = "torbox.app";
    private const string TokenName = "token";
    private const string Mask = UrlRedactor.Mask;

    /// <summary>True for a link on a provider host that carries a real token.</summary>
    public static bool IsProviderLink(string? url)
        => TryGetToken(url, out var value) && value != Mask;

    /// <summary>True for a copy of a provider link whose token was already replaced, which cannot download anything.</summary>
    public static bool IsMasked(string? url)
        => TryGetToken(url, out var value) && value == Mask;

    /// <summary>Any http or https address on a provider's own host, with a token or without one.</summary>
    public static bool IsOnProviderHost(string? url)
        => !string.IsNullOrWhiteSpace(url) &&
           Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) &&
           uri.Scheme is "http" or "https" &&
           IsProviderHost(uri.Host);

    /// <summary>
    /// The address with its token, every other secret and any login removed. Safe to store, and
    /// for the Host to send: the real address is kept in the job's protected field, so nothing is lost by this.
    /// </summary>
    public static string MaskToken(string url)
    {
        var fragment = url.IndexOf('#');
        return UrlRedactor.Redact(fragment < 0 ? url : url[..fragment])
            .Replace("://" + Mask + "@", "://", StringComparison.Ordinal);
    }

    private static bool TryGetToken(string? url, out string value)
    {
        value = string.Empty;
        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            !IsProviderHost(uri.Host))
        {
            return false;
        }

        foreach (var pair in uri.Query.TrimStart('?').Split('&', ';'))
        {
            var equals = pair.IndexOf('=');
            if (equals > 0 && IsTokenName(pair[..equals]) && pair.Length > equals + 1)
            {
                value = pair[(equals + 1)..];
                return true;
            }
        }

        return false;
    }

    /// <summary>The API's own links matter most: requestdl takes the account key itself as its token.</summary>
    private static bool IsProviderHost(string host)
    {
        if (host.Equals(ApiDomain, StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith("." + ApiDomain, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Any label after the CDN's own: the company uses several top-level domains and regions.
        var labels = host.Split('.');
        var cdnLabel = Array.FindIndex(labels, label => label.Equals(CdnLabel, StringComparison.OrdinalIgnoreCase));
        return cdnLabel >= 0 && cdnLabel < labels.Length - 1;
    }

    private static bool IsTokenName(string name)
        => Uri.UnescapeDataString(name).Equals(TokenName, StringComparison.OrdinalIgnoreCase);
}
