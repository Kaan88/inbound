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
/// <item><c>DOWNLOADMANAGER_TRUST_PROXY=1</c>: believe <c>X-Forwarded-For</c> and <c>X-Forwarded-Proto</c> (only behind a reverse proxy that sets them).</item>
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

    /// <summary>Take the client address and scheme from a reverse proxy's <c>X-Forwarded-*</c> headers.</summary>
    public bool TrustProxy { get; init; }

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
            TrustProxy = read(TrustProxyVariable)?.Trim() is "1" or "true" or "yes",
            DiscardUnreadableSecrets = read(DownloadManager.Persistence.AesGcmSecretProtector.DiscardVariable)?.Trim() is "1" or "true" or "yes",
            AllowedHostNames = OriginCheck.ParseHosts(read(OriginCheck.HostsVariable)),
        };
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
