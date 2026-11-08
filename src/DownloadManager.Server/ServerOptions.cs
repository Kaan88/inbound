using System.Net;
using DownloadManager.Runtime.Ipc;

namespace DownloadManager.Server;

/// <summary>
/// What the server reads from its environment. In a container these are the image's variables:
/// <list type="bullet">
/// <item><c>DOWNLOADMANAGER_DATA</c>: state (jobs, settings, logs, the secret key file). Default <c>/data</c>.</item>
/// <item><c>DOWNLOADMANAGER_ROOTS</c>: the folders a client may browse and create folders in, separated by <c>:</c> or <c>;</c>. Default <c>/downloads</c>.</item>
/// <item><c>DOWNLOADMANAGER_URLS</c>, else <c>ASPNETCORE_URLS</c>: where to listen, separated by <c>;</c>. Default <c>http://0.0.0.0:8080</c>.</item>
/// <item><c>DOWNLOADMANAGER_ALLOW_UNAUTHENTICATED=1</c>: needed to listen on anything but loopback, because there is no sign-in yet (D2).</item>
/// <item><c>DOWNLOADMANAGER_HOSTS</c>: host names a browser may use for the server, besides IP addresses and <c>localhost</c>.</item>
/// </list>
/// </summary>
public sealed record ServerOptions
{
    public const string DataVariable = "DOWNLOADMANAGER_DATA";
    public const string RootsVariable = "DOWNLOADMANAGER_ROOTS";
    public const string UrlsVariable = "DOWNLOADMANAGER_URLS";
    public const string AllowUnauthenticatedVariable = "DOWNLOADMANAGER_ALLOW_UNAUTHENTICATED";

    public const string DefaultData = "/data";
    public const string DefaultRoot = "/downloads";
    public const string DefaultUrls = "http://0.0.0.0:8080";

    public string DataDirectory { get; init; } = DefaultData;
    public IReadOnlyList<string> Roots { get; init; } = [DefaultRoot];
    public IReadOnlyList<string> Urls { get; init; } = [DefaultUrls];
    public bool AllowUnauthenticated { get; init; }

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
        return new ServerOptions
        {
            DataDirectory = string.IsNullOrWhiteSpace(data) ? DefaultData : data.Trim(),
            Roots = FolderBrowser.ParseRoots(read(RootsVariable), DefaultRoot),
            Urls = string.IsNullOrWhiteSpace(urls)
                ? [DefaultUrls]
                : urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            AllowUnauthenticated = read(AllowUnauthenticatedVariable)?.Trim() is "1" or "true" or "yes",
            AllowedHostNames = OriginCheck.ParseHosts(read(OriginCheck.HostsVariable)),
        };
    }

    /// <summary>
    /// Why the server must not start with these addresses, or null. Until D2 adds sign-in anyone who reaches the port
    /// controls the downloads and every saved login, so an address other than loopback needs the explicit variable.
    /// </summary>
    public string? DescribeUnsafeBinding()
    {
        if (AllowUnauthenticated) return null;
        var exposed = Urls.Where(url => !IsLoopback(url)).ToList();
        return exposed.Count == 0
            ? null
            : $"Refusing to listen on {string.Join(", ", exposed)}: this server has no sign-in yet, so anyone who can reach it " +
              $"controls it. Listen on 127.0.0.1 only, or set {AllowUnauthenticatedVariable}=1 on a network you trust.";
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
