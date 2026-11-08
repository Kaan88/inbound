using System.Buffers.Binary;
using System.Net;
using System.Runtime.InteropServices;
using DownloadManager.Runtime.WebUi;

namespace DownloadManager.Host;

/// <summary>
/// Which process listens on a TCP port, from Windows' own table (<c>GetExtendedTcpTable</c>, no rights needed): before
/// "Open in browser" gets its ticket, the Host checks that the bridge it started is the one listening on
/// <c>127.0.0.1</c> (or on every IPv4 address) at that port, and that no other process is.
/// </summary>
public sealed class TcpListenerOwner : IListenerCheck
{
    private const int AfInet = 2;
    private const int TcpTableOwnerPidListener = 3;
    private const int ErrorInsufficientBuffer = 122;

    public bool IsListening(int processId, int port)
    {
        var owners = ListenersOn(port, [IPAddress.Loopback, IPAddress.Any]);
        return owners.Count > 0 && owners.All(owner => owner == processId);
    }

    /// <summary>The processes listening on <paramref name="port"/> at one of these IPv4 addresses.</summary>
    internal static List<int> ListenersOn(int port, IReadOnlyCollection<IPAddress> addresses)
    {
        var owners = new List<int>();
        if (!OperatingSystem.IsWindows()) return owners;
        var size = 0;
        var result = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerPidListener, 0);
        for (var attempt = 0; attempt < 5 && result == ErrorInsufficientBuffer; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                result = GetExtendedTcpTable(buffer, ref size, false, AfInet, TcpTableOwnerPidListener, 0);
                if (result != 0) continue;
                var count = Marshal.ReadInt32(buffer);
                // MIB_TCPROW_OWNER_PID: state, local address, local port, remote address, remote port, pid (6 DWORDs).
                const int rowSize = 24;
                for (var i = 0; i < count; i++)
                {
                    var row = buffer + 4 + i * rowSize;
                    var localAddress = new IPAddress(unchecked((uint)Marshal.ReadInt32(row, 4)));
                    // The port is in network order in the low two bytes.
                    var raw = unchecked((uint)Marshal.ReadInt32(row, 8));
                    var localPort = BinaryPrimitives.ReverseEndianness((ushort)(raw & 0xFFFF));
                    if (localPort != port || !addresses.Contains(localAddress)) continue;
                    owners.Add(Marshal.ReadInt32(row, 20));
                }

                return owners;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        throw new InvalidOperationException($"The TCP table could not be read ({result}).");
    }

    [DllImport("iphlpapi.dll", SetLastError = false)]
    private static extern int GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, int reserved);
}
