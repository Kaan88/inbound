using DownloadManager.Contracts;

namespace DownloadManager.Runtime.Ipc;

/// <summary>
/// Answers <c>webui.*</c> (<see cref="WebUiMessageTypes"/>) for the user at this PC: the desktop Host's
/// <see cref="WebUi.WebUiSupervisor"/>. The dispatcher sends it only requests from a session with the desktop role.
/// </summary>
public interface IWebUiCommands
{
    Task<IpcMessage> HandleAsync(IpcMessage request, CancellationToken cancellationToken);
}
