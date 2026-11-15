using DownloadManager.Contracts;

namespace DownloadManager.Runtime.Ipc;

/// <summary>
/// Answers <c>sites.*</c> (<see cref="SitesMessageTypes"/>; SITES.2): <see cref="Sites.SitesCommands"/>. The dispatcher sends
/// it only requests from the user at this PC (the desktop role without a web scope).
/// </summary>
public interface ISitesCommands
{
    Task<IpcMessage> HandleAsync(IpcMessage request, CancellationToken cancellationToken);
}
