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

    /// <summary>
    /// SITES.10 (5): the folder on this computer a saved remote is linked to; null when there is no such remote or it has
    /// none. The dispatcher keeps an unchanged linked folder on a save (or a test of an edit) even when it is outside the
    /// session's folders, and holds a new or changed one to them.
    /// </summary>
    Task<string?> LinkedFolderAsync(string siteId, CancellationToken cancellationToken);
}
