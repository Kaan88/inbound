using System.Globalization;
using DownloadManager.Contracts;
using DownloadManager.Runtime.WebUi;

namespace DownloadManager.Host;

/// <summary>
/// The web UI's firewall check on a Linux desktop. Windows asks for a rule per program, and the window adds it with one
/// UAC prompt; a Linux desktop has no such rule to look for (most have no firewall on by default, and firewalld and ufw
/// filter by port and zone, not by program). So there is nothing for the app to add, and the check says so. A port that
/// a firewall of the user's own blocks still blocks; the web UI's page names the address to try from another device.
/// </summary>
internal sealed class LinuxFirewall : IFirewallRules
{
    public string Check(string program, int port, string ruleName) => WebUiFirewallStates.Ready;
}

/// <summary>
/// Who listens on a TCP port, from the kernel's own tables (<c>/proc/net/tcp</c> and <c>tcp6</c>): the listening
/// sockets on that port, matched against the sockets the bridge holds (<c>/proc/&lt;pid&gt;/fd</c>). A socket the bridge
/// doesn't hold is another process's: the web UI then says the port is taken.
/// </summary>
internal sealed class ProcListenerCheck : IListenerCheck
{
    private const string Listen = "0A";

    public ListenerOwner Owner(int processId, int port)
    {
        var inodes = ListeningInodes(port);
        if (inodes.Count == 0) return ListenerOwner.None;
        var ours = SocketInodesOf(processId);
        return inodes.All(ours.Contains) ? ListenerOwner.Ours : ListenerOwner.Other;
    }

    /// <summary>The inode of each socket listening on <paramref name="port"/> on loopback or every address.</summary>
    private static HashSet<string> ListeningInodes(int port)
    {
        var inodes = new HashSet<string>(StringComparer.Ordinal);
        var hexPort = port.ToString("X4", CultureInfo.InvariantCulture);
        foreach (var table in new[] { "/proc/net/tcp", "/proc/net/tcp6" })
        {
            string[] lines;
            try
            {
                lines = File.ReadAllLines(table);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            // sl local_address rem_address st tx:rx tr:when retrnsmt uid timeout inode ...
            foreach (var line in lines.Skip(1))
            {
                var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length < 10 || fields[3] != Listen) continue;
                var local = fields[1].Split(':');
                if (local.Length != 2 || !string.Equals(local[1], hexPort, StringComparison.OrdinalIgnoreCase)) continue;
                inodes.Add(fields[9]);
            }
        }

        return inodes;
    }

    private static HashSet<string> SocketInodesOf(int processId)
    {
        var inodes = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var fd in Directory.EnumerateFileSystemEntries($"/proc/{processId}/fd"))
            {
                var target = new FileInfo(fd).LinkTarget;
                // "socket:[12345]"
                if (target is not null && target.StartsWith("socket:[", StringComparison.Ordinal) && target.EndsWith(']'))
                    inodes.Add(target[8..^1]);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The process is gone, or not ours to look at: none of the sockets are its.
        }

        return inodes;
    }
}
