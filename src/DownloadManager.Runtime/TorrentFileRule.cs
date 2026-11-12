using DownloadManager.Core;
using DownloadManager.Runtime.Remote;

namespace DownloadManager.Runtime;

/// <summary>
/// T10 (option A of docs/plans/t10-torrent-links.md): a download that turns out to be a .torrent file is offered as a
/// torrent. The browser extension does not change: it hands the link over as any other, the Host downloads it with the
/// browser's cookies, and this rule looks at it when it finishes. A download looks like a torrent when its content type
/// is <c>application/x-bittorrent</c> (the browser's or the server's) or its name or address ends in <c>.torrent</c>; only
/// then is its file read, and it counts only when it is at most <see cref="TorrentMetadata.MaxFileBytes"/> and reads as a
/// .torrent file. Other files are never opened, so the rule costs nothing for them.
/// </summary>
public static class TorrentFileRule
{
    public const string ContentType = "application/x-bittorrent";

    /// <summary>Whether a download with these facts looks like a .torrent file. Nothing is read.</summary>
    public static bool LooksLikeTorrent(string? contentType, string? handoffContentType, string? fileName, string? url)
        => IsTorrentType(contentType) || IsTorrentType(handoffContentType) || EndsInTorrent(fileName) || UrlEndsInTorrent(url);

    /// <summary>As above, for a job: its server's and its browser's content type, its name, and its address.</summary>
    public static bool LooksLikeTorrent(DownloadJob job)
        => job.Torrent is null && job.RemoteSource is null &&
           (LooksLikeTorrent(job.ContentType, job.HandoffContentType, job.FileName, job.Url) ||
            EndsInTorrent(Path.GetFileName(job.FinalFilePath)) || UrlEndsInTorrent(job.FinalUrl));

    /// <summary>
    /// Whether the finished file at <paramref name="path"/> is a .torrent file this app can add: at most
    /// <see cref="TorrentMetadata.MaxFileBytes"/>, and it reads as one with at least one file listed. A file that is
    /// missing, too big, or can't be read or parsed (an HTML error page saved as x.torrent) is not one.
    /// </summary>
    public static bool IsTorrentFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0 || info.Length > TorrentMetadata.MaxFileBytes) return false;
            var bytes = File.ReadAllBytes(path);
            return TorrentMetadata.TryParse(bytes, out var parsed, out _) && parsed is { Files.Count: > 0, FilesCut: false };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsTorrentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType)) return false;
        var semicolon = contentType.IndexOf(';');
        var media = (semicolon >= 0 ? contentType[..semicolon] : contentType).Trim();
        return string.Equals(media, ContentType, StringComparison.OrdinalIgnoreCase);
    }

    private static bool EndsInTorrent(string? name)
        => !string.IsNullOrWhiteSpace(name) && name.Trim().EndsWith(".torrent", StringComparison.OrdinalIgnoreCase);

    /// <summary>The address's path (not its query) ends in .torrent.</summary>
    private static bool UrlEndsInTorrent(string? url)
        => !string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) &&
           uri.Scheme is "http" or "https" && EndsInTorrent(Uri.UnescapeDataString(uri.AbsolutePath));
}
