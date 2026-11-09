using DownloadManager.Runtime.WebUi;

namespace DownloadManager.Host;

/// <summary>
/// The web bridge's uploads (D8): a <c>.torrent</c> file a browser sends is kept in <c>uploads</c> in the data folder
/// under a random name, and the browser gets <c>upload:</c> and that name back (the server's <c>TorrentUploads</c>,
/// which the bridge runs; the format is <see cref="UploadReferences"/>). The Host turns such a reference into the
/// file's path for <c>remote.classify</c> and <c>remote.add</c>; anything else is left as it is. The bridge cleans the folder.
/// </summary>
public static class HostUploads
{
    /// <summary>The path an upload reference stands for, or null when the text isn't one.</summary>
    public static string? Resolve(string dataDirectory, string text) =>
        UploadReferences.Resolve(Path.Combine(dataDirectory, UploadReferences.FolderName), text);
}
