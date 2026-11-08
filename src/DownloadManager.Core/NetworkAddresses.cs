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
