namespace DownloadManager.Contracts;

/// <summary>
/// Converts between the stored proxy address (<c>host:port</c> for HTTP,
/// <c>socks4|socks4a|socks5://host:port</c> for SOCKS) and the pieces shown in
/// the proxy settings UI.
/// </summary>
public static class ProxyAddress
{
    /// <summary>Display names, index-aligned with <see cref="Schemes"/>.</summary>
    public static IReadOnlyList<string> ProtocolNames { get; } =
        ["HTTP / HTTPS", "SOCKS4", "SOCKS4a", "SOCKS5"];

    public static IReadOnlyList<string> Schemes { get; } = ["http", "socks4", "socks4a", "socks5"];

    /// <summary>Splits a stored address into a protocol index and a host:port string.</summary>
    public static (int ProtocolIndex, string HostPort) Split(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return (0, string.Empty);
        }

        var text = address.Trim();
        var separator = text.IndexOf("://", StringComparison.Ordinal);
        if (separator < 0)
        {
            return (0, text);
        }

        var scheme = text[..separator].ToLowerInvariant();
        var index = -1;
        for (var i = 0; i < Schemes.Count; i++)
        {
            if (Schemes[i] == scheme || (scheme == "https" && i == 0))
            {
                index = i;
                break;
            }
        }

        return (index < 0 ? 0 : index, text[(separator + 3)..].TrimEnd('/'));
    }

    /// <summary>
    /// Builds the stored address, or returns <c>null</c> when the host is empty or the
    /// port is missing or outside 1-65535. A scheme typed into the host box is ignored;
    /// the protocol index decides the scheme.
    /// </summary>
    public static string? Compose(int protocolIndex, string? hostPort)
    {
        var text = (hostPort ?? string.Empty).Trim();
        var separator = text.IndexOf("://", StringComparison.Ordinal);
        if (separator >= 0)
        {
            text = text[(separator + 3)..];
        }

        text = text.TrimEnd('/');
        if (text.Length == 0 || text.Contains('/') || text.Contains(' ') ||
            !Uri.TryCreate("http://" + text, UriKind.Absolute, out var uri) ||
            string.IsNullOrEmpty(uri.Host))
        {
            return null;
        }

        // Require an explicit port: a bare host would silently become port 80.
        var portText = text.StartsWith('[')
            ? (text.LastIndexOf("]:", StringComparison.Ordinal) is var v and >= 0 ? text[(v + 2)..] : string.Empty)
            : (text.Count(c => c == ':') == 1 ? text[(text.LastIndexOf(':') + 1)..] : string.Empty);
        if (!int.TryParse(portText, out var port) || port is < 1 or > 65535)
        {
            return null;
        }

        var index = protocolIndex is >= 0 and < 4 ? protocolIndex : 0;
        return index == 0 ? text : $"{Schemes[index]}://{text}";
    }
}
