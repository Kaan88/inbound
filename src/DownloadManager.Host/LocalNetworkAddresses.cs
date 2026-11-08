using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using DownloadManager.Runtime.WebUi;

namespace DownloadManager.Host;

/// <summary>
/// This PC's addresses another device on the same network can type: IPv4 on network adapters that are up, without
/// loopback, tunnels and link-local (169.254.x.x) addresses. IPv6 is left out: the firewall rule's "local subnet" and
/// the addresses people type at home are IPv4.
/// </summary>
public sealed class LocalNetworkAddresses : INetworkAddresses
{
    public IReadOnlyList<IPAddress> Current()
    {
        var addresses = new List<IPAddress>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up ||
                adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            foreach (var unicast in adapter.GetIPProperties().UnicastAddresses)
            {
                var address = unicast.Address;
                if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address)) continue;
                var bytes = address.GetAddressBytes();
                if (bytes[0] == 169 && bytes[1] == 254) continue;
                if (!addresses.Contains(address)) addresses.Add(address);
            }
        }

        return addresses;
    }
}
