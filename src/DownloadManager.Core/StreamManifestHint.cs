namespace DownloadManager.Core;

/// <summary>
/// Whether a download is an HLS playlist or a DASH manifest. Stream downloading was cut in 0.7.2: such a download
/// fails at its start with <see cref="NotDownloadedMessage"/> instead of saving the playlist text as the file.
/// The address decides first; then the content type the
/// browser hand-off gave (<see cref="DownloadJob.HandoffContentType"/>), never <see cref="DownloadJob.ContentType"/>,
/// which a plain transfer overwrites with the server's answer: a .m3u list served as audio/x-mpegurl must not turn into
/// a playlist on its next run. The description is the user's text, so it counts only
/// when the whole of it is such a MIME type: hand-offs from before the content type was carried put it there, and
/// "taken from the m3u8 page" must not turn a plain file into a playlist.
/// </summary>
public static class StreamManifestHint
{
    public const string NotDownloadedMessage = "Streaming playlists aren't downloaded; use a video downloader.";

    /// <summary>Final: the same address gives the same playlist, so a retry only repeats it.</summary>
    public static JobFailure NotDownloadedFailure(int? httpStatus = null)
        => new(FailureKind.Unsupported, NotDownloadedMessage, false, false, DateTimeOffset.UtcNow, httpStatus);

    /// <summary>
    /// A server's answer that is an HLS playlist or a DASH manifest. A plain ".m3u" list (internet radio, a music
    /// playlist) is often served with the same HLS type and is still saved as the file it is: when the job's name,
    /// its address or the server's own name (Content-Disposition) ends in .m3u, the HLS type does not count.
    /// </summary>
    public static bool IsPlaylistResponse(string? contentType, params string?[] names)
        => IsDashMediaType(contentType) || IsHlsMediaType(contentType) && !IsPlainM3u(names);

    /// <summary>
    /// The address decides first (.m3u8). A type from the hand-off or the description counts only when neither the
    /// job's name nor its address is a plain .m3u list: a browser hands such a list over as audio/x-mpegurl, and the
    /// extension catches it, so failing it here would lose the file in both places. Same rule as
    /// <see cref="IsPlaylistResponse"/>.
    /// </summary>
    public static bool IsHls(DownloadJob job)
        => PathEndsWith(job.Url, ".m3u8") ||
           (IsHlsMediaType(job.HandoffContentType) || IsHlsMediaType(DescriptionMediaType(job.Description))) &&
           !IsPlainM3u(job.FileName, job.Url, job.FinalUrl);

    public static bool IsDash(DownloadJob job)
        => PathEndsWith(job.Url, ".mpd") || IsDashMediaType(job.HandoffContentType) || IsDashMediaType(DescriptionMediaType(job.Description));

    public static bool IsHlsMediaType(string? contentType)
        => MediaTypeOf(contentType) is { } type && type.Contains("mpegurl", StringComparison.OrdinalIgnoreCase);

    public static bool IsDashMediaType(string? contentType)
        => MediaTypeOf(contentType) is { } type && type.Equals("application/dash+xml", StringComparison.OrdinalIgnoreCase);

    /// <summary>Any of these names or addresses ends in ".m3u" (an address by its path, without the query).</summary>
    private static bool IsPlainM3u(params string?[] names)
        => names.Any(name => !string.IsNullOrWhiteSpace(name) &&
                             (PathEndsWith(name, ".m3u") || name.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase)));

    private static bool PathEndsWith(string? url, string extension)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
           uri.AbsolutePath.EndsWith(extension, StringComparison.OrdinalIgnoreCase);

    /// <summary>The description when all of it is one MIME type (parameters allowed), else null.</summary>
    private static string? DescriptionMediaType(string? description)
    {
        var type = MediaTypeOf(description);
        if (type is null) return null;
        var slash = type.IndexOf('/');
        if (slash <= 0 || slash == type.Length - 1 || type.IndexOf('/', slash + 1) >= 0) return null;
        return type.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '.' or '+' or '-' or '_') ? type : null;
    }

    private static string? MediaTypeOf(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var semicolon = value.IndexOf(';');
        var type = (semicolon >= 0 ? value[..semicolon] : value).Trim();
        return type.Length == 0 ? null : type;
    }
}
