using DownloadManager.Core;

namespace DownloadManager.Torrents;

/// <summary>What a worker <c>error</c> event means for the job, in plain words.</summary>
/// <param name="PausesJob">The torrent pauses (the disk is full) instead of failing; Resume tries again.</param>
public sealed record TorrentErrorMeaning(FailureKind Kind, string Message, bool Retryable, bool PausesJob);

/// <summary>
/// Maps the worker's <c>torrent</c>, <c>file</c> and <c>metadata</c> errors to what the job shows. The worker's
/// <c>reason</c> (<c>diskFull</c>, <c>permission</c>, <c>pathTooLong</c>) decides when it is there; a worker older than
/// it sends only the system's message, which is read for the same cases in English. Tracker failures never reach here:
/// the worker does not report them, and a torrent with an unreachable tracker keeps looking for peers.
/// </summary>
public static class TorrentErrors
{
    public const string DiskFullText = "The disk is full";
    public const string PermissionText = "A file of the torrent can't be written here: permission denied. Choose another folder or check its permissions.";
    public const string PathTooLongText = "A file path of the torrent is too long for this folder. Choose a shorter folder.";

    /// <summary>Null for an error that is not about one torrent (<c>listen</c>, <c>protocol</c>) or a kind the job ignores.</summary>
    public static TorrentErrorMeaning? Classify(string? kind, string? reason, string? message)
    {
        if (kind is not ("torrent" or "file" or "metadata")) return null;
        var text = string.IsNullOrWhiteSpace(message) ? "unknown error" : message.Trim();
        if (kind == "metadata")
            return new TorrentErrorMeaning(FailureKind.InvalidContent, $"The torrent's file list can't be used: {text}", false, false);

        switch (reason ?? ReasonOf(text))
        {
            case "diskFull":
                return new TorrentErrorMeaning(FailureKind.DiskFull, DiskFullText, false, true);
            case "permission":
                return new TorrentErrorMeaning(FailureKind.Permission, PermissionText, false, false);
            case "pathTooLong":
                return new TorrentErrorMeaning(FailureKind.DestinationUnavailable, PathTooLongText, false, false);
        }

        return kind == "file"
            ? new TorrentErrorMeaning(FailureKind.Permission, $"A file of the torrent can't be written: {text}", true, false)
            : new TorrentErrorMeaning(FailureKind.Unknown, $"The torrent stopped: {text}", true, false);
    }

    /// <summary>The reason in an older worker's English system message, or null.</summary>
    internal static string? ReasonOf(string message)
    {
        bool Has(string words) => message.Contains(words, StringComparison.OrdinalIgnoreCase);
        if (Has("no space") || Has("not enough space") || Has("disk is full") || Has("disk full")) return "diskFull";
        if (Has("access is denied") || Has("permission denied") || Has("operation not permitted") || Has("read-only file system"))
            return "permission";
        if (Has("name too long") || Has("filename or extension is too long") || Has("path too long")) return "pathTooLong";
        return null;
    }
}
