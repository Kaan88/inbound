using System.Net;
using System.Net.Sockets;

namespace DownloadManager.Core;

/// <summary>
/// Addresses on this PC or its local network: loopback, private ranges, link-local, shared (CGNAT) and unique-local
/// ones, unspecified, broadcast and multicast. A download a browser on another device added must not reach them
/// (<see cref="DownloadJob.PublicNetworkOnly"/>), or a web page could use this PC to reach its router or a service
/// that only listens locally.
/// </summary>
public static class NetworkAddresses
{
    /// <summary>
    /// What the address's text alone says: <c>localhost</c> and names under <c>.localhost</c> or <c>.local</c>, and an IP
    /// address that <see cref="IsLocal"/>. Any other name can still lead here once resolved, so a connection made for such
    /// a download checks the addresses it resolved too.
    /// </summary>
    public static bool IsLocalTarget(Uri uri)
    {
        if (uri.IsLoopback) return true;
        var host = uri.IdnHost.TrimEnd('.');
        if (host.Length == 0) return true;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
            return true;
        return IPAddress.TryParse(host.Trim('[', ']'), out var address) && IsLocal(address);
    }

    /// <summary>
    /// UI.B27: an http or https address that only this PC or its local network can reach, by its text alone: what
    /// <see cref="IsLocalTarget"/> says (loopback, private, link-local and unique-local ranges, <c>localhost</c>,
    /// <c>.local</c>), a name of one label (<c>nas</c>), and names under <c>.lan</c> and <c>.home.arpa</c>. No service
    /// on the internet can fetch from one, so adding one never asks a provider about it: it is an ordinary download.
    /// False for anything that isn't an absolute http or https address.
    /// </summary>
    public static bool IsLocalWebAddress(string? text)
        => Uri.TryCreate(text?.Trim(), UriKind.Absolute, out var uri) && IsLocalWebAddress(uri);

    /// <inheritdoc cref="IsLocalWebAddress(string?)"/>
    public static bool IsLocalWebAddress(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https")) return false;
        if (IsLocalTarget(uri)) return true;
        if (uri.HostNameType != UriHostNameType.Dns) return false;
        var host = uri.IdnHost.TrimEnd('.');
        return !host.Contains('.') ||
               host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase) ||
               host.Equals("home.arpa", StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(".home.arpa", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether this address is not on the public internet.</summary>
    public static bool IsLocal(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] is 0 or 10 or 127 ||
                   (b[0] == 100 && b[1] is >= 64 and <= 127) ||
                   (b[0] == 169 && b[1] == 254) ||
                   (b[0] == 172 && b[1] is >= 16 and <= 31) ||
                   (b[0] == 192 && b[1] == 168) ||
                   (b[0] == 192 && b[1] == 0 && b[2] == 0) ||
                   (b[0] == 198 && b[1] is 18 or 19) ||
                   b[0] >= 224;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.Equals(IPAddress.IPv6Any) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal ||
                address.IsIPv6Multicast || address.IsIPv6UniqueLocal)
                return true;
            var b = address.GetAddressBytes();
            // 64:ff9b::/96 carries an IPv4 address; judge that one.
            if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b && b[4..12].All(part => part == 0))
                return IsLocal(new IPAddress(b[12..]));
            return false;
        }

        return true;
    }
}
