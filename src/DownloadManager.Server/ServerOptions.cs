using System.Net;

namespace DownloadManager.Server;

/// <summary>
/// What the server reads from its environment. In a container these are the image's variables:
/// <list type="bullet">
/// <item><c>DOWNLOADMANAGER_DATA</c>: state (jobs, settings, logs, the secret key file). Default <c>/data</c>.</item>
/// <item><c>DOWNLOADMANAGER_ROOTS</c>: the folders a client may browse and create folders in, separated by <c>:</c> or <c>;</c>. Default <c>/downloads</c>.</item>
/// <item><c>DOWNLOADMANAGER_URLS</c>, else <c>ASPNETCORE_URLS</c>: where to listen, separated by <c>;</c>. Default <c>http://0.0.0.0:8080</c>.</item>
/// <item><c>DOWNLOADMANAGER_AUTH</c>: <c>off</c> turns sign-in off (D2). It is on otherwise, for a new install too.</item>
/// <item><c>DOWNLOADMANAGER_PASSWORD</c> or <c>DOWNLOADMANAGER_PASSWORD_FILE</c>: the sign-in password; without them it is set on the setup page.</item>
/// <item><c>DOWNLOADMANAGER_TRUST_PROXY</c>: the reverse proxy's address or network (<c>172.30.0.10</c>, <c>172.30.0.0/24</c>; several
/// separated by <c>,</c> or <c>;</c>). <c>X-Forwarded-For</c> and <c>X-Forwarded-Proto</c> are believed from those addresses only.</item>
/// <item><c>DOWNLOADMANAGER_ALLOW_UNAUTHENTICATED=1</c>: with sign-in off, needed to listen on anything but loopback.</item>
/// <item><c>DOWNLOADMANAGER_HOSTS</c>: host names a browser may use for the server, besides IP addresses and <c>localhost</c>.</item>
/// <item><c>DOWNLOADMANAGER_DISCARD_UNREADABLE_SECRETS=1</c>: start although saved secrets were sealed with another key file (they are lost).</item>
/// </list>
/// Where it listens comes from these variables only: Kestrel's own <c>Kestrel:Endpoints</c> configuration is ignored, so
/// it can't open a port the loopback rule below didn't see.
/// </summary>
public sealed record ServerOptions
{
    public const string DataVariable = "DOWNLOADMANAGER_DATA";
    public const string RootsVariable = "DOWNLOADMANAGER_ROOTS";
    public const string UrlsVariable = "DOWNLOADMANAGER_URLS";
    public const string AllowUnauthenticatedVariable = "DOWNLOADMANAGER_ALLOW_UNAUTHENTICATED";
    public const string AuthVariable = "DOWNLOADMANAGER_AUTH";
    public const string PasswordVariable = "DOWNLOADMANAGER_PASSWORD";
    public const string PasswordFileVariable = "DOWNLOADMANAGER_PASSWORD_FILE";
    public const string TrustProxyVariable = "DOWNLOADMANAGER_TRUST_PROXY";

    public const string DefaultData = "/data";
    public const string DefaultRoot = "/downloads";
    public const string DefaultUrls = "http://0.0.0.0:8080";

    public string DataDirectory { get; init; } = DefaultData;
    public IReadOnlyList<string> Roots { get; init; } = [DefaultRoot];
    public IReadOnlyList<string> Urls { get; init; } = [DefaultUrls];
    public bool AllowUnauthenticated { get; init; }

    /// <summary>Sign-in (D2). On unless <c>DOWNLOADMANAGER_AUTH=off</c>.</summary>
    public bool SignInRequired { get; init; } = true;

    /// <summary>The password from <c>DOWNLOADMANAGER_PASSWORD</c>; it prints as hidden.</summary>
    public SecretValue? Password { get; init; }

    /// <summary>A file holding the password (a Docker secret), from <c>DOWNLOADMANAGER_PASSWORD_FILE</c>.</summary>
    public string? PasswordFile { get; init; }

    /// <summary>
    /// The reverse proxies whose <c>X-Forwarded-*</c> headers give the client's address and scheme. Only a connection
    /// from one of these is believed: believing any client would let it choose a new address for every wrong password
    /// (no wait would ever apply) and claim to be this machine (no setup code).
    /// </summary>
    public IReadOnlyList<IPNetwork> TrustedProxies { get; init; } = [];

    /// <summary>Why <c>DOWNLOADMANAGER_TRUST_PROXY</c> can't be used as given, or null. The server refuses to start then.</summary>
    public string? TrustProxyProblem { get; init; }

    public bool TrustProxy => TrustedProxies.Count > 0;

    /// <summary>Whether a connection from this address is one of <see cref="TrustedProxies"/>.</summary>
    public bool IsTrustedProxy(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return TrustedProxies.Any(network => network.Contains(address));
    }

    /// <summary>A session ends after this long without a request; an open WebSocket keeps it alive.</summary>
    public TimeSpan SessionIdleTimeout { get; init; } = TimeSpan.FromHours(24);

    /// <summary>A session ends this long after sign-in, whatever happens.</summary>
    public TimeSpan SessionAbsoluteTimeout { get; init; } = TimeSpan.FromDays(7);

    /// <summary>The clock for sessions, waits and pairing codes; tests give their own.</summary>
    internal TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>Start even when saved secrets can't be opened with the key file; they are then lost.</summary>
    public bool DiscardUnreadableSecrets { get; init; }

    /// <summary>Names a browser may use for this server besides an IP address and <c>localhost</c> (<see cref="OriginCheck"/>).</summary>
    public IReadOnlyList<string> AllowedHostNames { get; init; } = [];

    /// <summary>
    /// Which kind of server this is, in <c>/api/auth/status</c>, so the web UI's texts fit it: <c>server</c> for this
    /// headless server (Docker). The Windows app's web bridge (D8) will say <c>windows</c>. Not read from the environment.
    /// </summary>
    public string HostKind { get; init; } = "server";

    /// <summary>Where downloads go until a client changes it: the first root.</summary>
    public string DefaultDownloadDirectory => Roots.Count > 0 ? Roots[0] : DefaultRoot;

    public static ServerOptions FromEnvironment(Func<string, string?>? read = null)
    {
        read ??= Environment.GetEnvironmentVariable;
        var data = read(DataVariable);
        var urls = read(UrlsVariable);
        if (string.IsNullOrWhiteSpace(urls)) urls = read("ASPNETCORE_URLS");
        var password = read(PasswordVariable);
        var passwordFile = read(PasswordFileVariable);
        var proxies = ParseTrustedProxies(read(TrustProxyVariable), out var proxyProblem);
        return new ServerOptions
        {
            DataDirectory = string.IsNullOrWhiteSpace(data) ? DefaultData : data.Trim(),
            Roots = DownloadManager.Runtime.PathRoots.Parse(read(RootsVariable), DefaultRoot),
            Urls = string.IsNullOrWhiteSpace(urls)
                ? [DefaultUrls]
                : urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            AllowUnauthenticated = read(AllowUnauthenticatedVariable)?.Trim() is "1" or "true" or "yes",
            // Only these words turn it off; anything else, a typo included, leaves sign-in on.
            SignInRequired = read(AuthVariable)?.Trim().ToLowerInvariant() is not ("off" or "0" or "false" or "no"),
            Password = string.IsNullOrEmpty(password) ? null : new SecretValue(password),
            PasswordFile = string.IsNullOrWhiteSpace(passwordFile) ? null : passwordFile.Trim(),
            TrustedProxies = proxies,
            TrustProxyProblem = proxyProblem,
            DiscardUnreadableSecrets = read(DownloadManager.Persistence.AesGcmSecretProtector.DiscardVariable)?.Trim() is "1" or "true" or "yes",
            AllowedHostNames = OriginCheck.ParseHosts(read(OriginCheck.HostsVariable)),
        };
    }

    /// <summary>
    /// The proxies <c>DOWNLOADMANAGER_TRUST_PROXY</c> names: IP addresses and networks in CIDR form. Empty, <c>0</c>,
    /// <c>off</c> and the like trust none. <c>1</c> (trust whoever connects) and a network of every address are refused,
    /// as is anything that doesn't parse, with <paramref name="problem"/> saying so.
    /// </summary>
    public static List<IPNetwork> ParseTrustedProxies(string? value, out string? problem)
    {
        problem = null;
        var networks = new List<IPNetwork>();
        var text = value?.Trim() ?? string.Empty;
        if (text.Length == 0 || text.ToLowerInvariant() is "0" or "off" or "false" or "no") return networks;
        if (text.ToLowerInvariant() is "1" or "on" or "true" or "yes")
        {
            problem = $"{TrustProxyVariable} takes the reverse proxy's address or network (for example 172.30.0.10 or 172.30.0.0/24), " +
                      "not \"" + text + "\": believing X-Forwarded-For from any client would let it skip the sign-in waits and the setup code.";
            return [];
        }

        foreach (var entry in text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            IPNetwork network;
            if (IPAddress.TryParse(entry, out var single) && !entry.Contains('/'))
            {
                if (single.IsIPv4MappedToIPv6) single = single.MapToIPv4();
                network = new IPNetwork(single, single.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128);
            }
            else if (!IPNetwork.TryParse(entry, out network))
            {
                problem = $"{TrustProxyVariable}: \"{entry}\" is neither an IP address nor a network such as 172.30.0.0/24.";
                return [];
            }

            if (network.PrefixLength == 0)
            {
                problem = $"{TrustProxyVariable}: \"{entry}\" is every address, so any client could claim any address. Give the proxy's own address or network.";
                return [];
            }

            networks.Add(network);
        }

        return networks;
    }

    /// <summary>
    /// Why the server must not start with these addresses, or null. With sign-in off anyone who reaches the port
    /// controls the downloads and every saved login, so an address other than loopback then needs the explicit
    /// variable. With sign-in on, any address is allowed.
    /// </summary>
    public string? DescribeUnsafeBinding() => DescribeUnsafeBinding(Urls);

    /// <summary>The same rule for the addresses Kestrel reports it really listens on, checked again once it has started.</summary>
    public string? DescribeUnsafeBinding(IEnumerable<string> urls)
    {
        if (SignInRequired || AllowUnauthenticated) return null;
        var exposed = urls.Where(url => !IsLoopback(url)).ToList();
        return exposed.Count == 0
            ? null
            : $"Refusing to listen on {string.Join(", ", exposed)}: sign-in is off ({AuthVariable}=off), so anyone who can reach " +
              $"this server controls it. Turn sign-in on, listen on 127.0.0.1 only, or set {AllowUnauthenticatedVariable}=1 on a network you trust.";
    }

    /// <summary>
    /// <c>localhost</c>, <c>127.x</c> and <c>[::1]</c>. A wildcard (<c>*</c>, <c>+</c>, <c>0.0.0.0</c>, <c>[::]</c>), a
    /// name or another address is not; neither is anything that doesn't parse, so a typo can't open the port.
    /// </summary>
    public static bool IsLoopback(string url)
    {
        // Kestrel accepts http://*:8080 and http://+:8080, which Uri doesn't parse; they are wildcards.
        var host = url;
        var scheme = host.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) host = host[(scheme + 3)..];
        var slash = host.IndexOf('/');
        if (slash >= 0) host = host[..slash];
        if (host.StartsWith('['))
        {
            var close = host.IndexOf(']');
            if (close < 0) return false;
            host = host[1..close];
        }
        else
        {
            var colon = host.LastIndexOf(':');
            if (colon >= 0) host = host[..colon];
        }

        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
    }
}

/// <summary>A secret given in the environment. It prints as hidden, so a logged options record never shows it.</summary>
public sealed class SecretValue(string value)
{
    public string Value { get; } = value;

    public override string ToString() => "[hidden]";
}
