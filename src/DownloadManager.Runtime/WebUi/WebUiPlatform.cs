using System.Net;
using DownloadManager.Contracts;

namespace DownloadManager.Runtime.WebUi;

/// <summary>Starts the web bridge (<c>DownloadManager.Server.exe --bridge</c>). The desktop Host's starts a process; tests give a fake.</summary>
public interface IWebBridgeLauncher
{
    /// <summary>The full path of the bridge program, which the firewall rule names. It may not exist (not installed).</summary>
    string BridgePath { get; }

    /// <summary>Starts it with these options (<see cref="WebBridgeStartDto"/>). Throws when it can't be started at all.</summary>
    IWebBridgeProcess Start(WebBridgeStartDto start);
}

/// <summary>One run of the bridge.</summary>
public interface IWebBridgeProcess
{
    int ProcessId { get; }

    /// <summary>Completes when the bridge says it listens. Never completes when it ends first.</summary>
    Task Ready { get; }

    /// <summary>Completes when it has ended, with its exit code and the last thing it said on standard error.</summary>
    Task<WebBridgeExitInfo> Exited { get; }

    /// <summary>Asks it to stop (closes its standard input), and ends it when it hasn't within <paramref name="grace"/>.</summary>
    Task StopAsync(TimeSpan grace);
}

public sealed record WebBridgeExitInfo(int ExitCode, string? Message);

/// <summary>Reads the Windows Firewall's rules; never changes them (the window adds the rule, with one UAC prompt).</summary>
public interface IFirewallRules
{
    /// <summary><see cref="WebUiFirewallStates"/> for this program and port.</summary>
    string Check(string program, int port, string ruleName);
}

/// <summary>Who listens on a TCP port on 127.0.0.1 (or every IPv4 address).</summary>
public interface IListenerCheck
{
    ListenerOwner Owner(int processId, int port);
}

/// <summary>What <see cref="IListenerCheck.Owner"/> found.</summary>
public enum ListenerOwner
{
    /// <summary>Nothing listens there.</summary>
    None,

    /// <summary>Only the process asked about.</summary>
    Ours,

    /// <summary>Another process, alone or besides it.</summary>
    Other,
}

/// <summary>The web UI's sign-in password, kept where the bridge reads it (<c>auth.json</c>).</summary>
public interface IWebUiPasswords
{
    bool HasPassword();

    /// <summary>Why this password can't be used (the server's sign-in rules), or null.</summary>
    string? Problem(string? password);

    /// <summary>Whether this is the password in force already (then setting it again changes nothing).</summary>
    Task<bool> IsCurrentAsync(string password, CancellationToken cancellationToken);

    /// <summary>
    /// Saves it (only while the bridge is stopped: the bridge keeps the file in memory). The browsers signed in with the
    /// old one sign in again; the paired desktop apps pair again, and the paired browser extensions too unless
    /// <paramref name="keepPairedBrowsers"/> (as on the web UI's own password change).
    /// </summary>
    Task SetAsync(string password, bool keepPairedBrowsers, CancellationToken cancellationToken);

    /// <summary>
    /// Ends every browser's sign-in (removes the saved sessions; only while the bridge is stopped). Paired apps stay
    /// paired: their tokens were made with the password, which is still the one in force.
    /// </summary>
    void EndSignIns();
}

/// <summary>This PC's addresses another device on the network could use.</summary>
public interface INetworkAddresses
{
    IReadOnlyList<IPAddress> Current();
}

/// <summary>What the supervisor needs from the machine.</summary>
public sealed record WebUiPlatform(
    IWebBridgeLauncher Launcher,
    IFirewallRules Firewall,
    IListenerCheck Listener,
    IWebUiPasswords Passwords,
    INetworkAddresses Network);
