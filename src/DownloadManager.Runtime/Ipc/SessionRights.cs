using DownloadManager.Contracts;
using DownloadManager.Runtime.Archives;
using DownloadManager.Runtime.Remote;

namespace DownloadManager.Runtime.Ipc;

/// <summary>
/// What one connected client may do. Every session has one for its whole life, given when it opens
/// (<see cref="HostCommandDispatcher.OpenSessionAsync(IClientSession, ClientHelloDto?, CancellationToken)"/>): the
/// platform's own rights for the desktop window, the CLI and the browser's native host (and the server's clients), or
/// a web session's (<see cref="WebSessionProfile"/>), which asked for fewer in its <c>client.hello</c>.
/// </summary>
public sealed class SessionRights
{
    /// <summary>A <see cref="ClientRoles"/> value.</summary>
    public required string Role { get; init; }

    /// <summary>What the machine does for this session; the hello lists it.</summary>
    public required PlatformCapabilities Capabilities { get; init; }

    /// <summary>
    /// Set for a client that isn't this machine's user and shares the service with one who is (a web session): its
    /// folders and the addresses it may reach, passed with each call it makes. Null: the platform's own limits.
    /// </summary>
    public CallerScope? Scope { get; init; }

    /// <summary>Answers its <c>archive.*</c>, held to its folders. Null refuses them.</summary>
    public ArchiveIpcDispatcher? Archives { get; init; }

    /// <summary>Answers its <c>folders.*</c> where <see cref="PlatformCapabilities.SupportsFolderBrowse"/> is on.</summary>
    public FolderBrowser? Folders { get; init; }

    /// <summary>Answers its <c>remote.*</c>, held to its folders. Null leaves <c>remote</c> out of the hello.</summary>
    public RemoteCommands? Remote { get; init; }

    /// <summary>Answers <c>torrent.*</c> (the Host has the worker and the kill switch is off); the hello lists <c>torrents</c>.</summary>
    public bool Torrents { get; init; }
}
