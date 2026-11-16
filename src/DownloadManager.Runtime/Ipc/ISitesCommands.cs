using DownloadManager.Contracts;

namespace DownloadManager.Runtime.Ipc;

/// <summary>
/// Answers <c>sites.*</c> (<see cref="SitesMessageTypes"/>; SITES.2): <see cref="Sites.SitesCommands"/>. The dispatcher sends
/// it requests from the user at this PC and, since SITES.7, from web sessions, held to their folders.
/// </summary>
public interface ISitesCommands
{
    Task<IpcMessage> HandleAsync(IpcMessage request, CancellationToken cancellationToken);

    /// <summary>
    /// SITES.7: the folder on this computer a transfer batch reads or writes; null when there is no such batch. The
    /// dispatcher checks it before a scoped session resumes or retries the batch.
    /// </summary>
    string? TransferLocalFolder(string transferId);
}
