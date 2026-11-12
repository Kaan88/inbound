namespace DownloadManager.Core;

/// <summary>
/// Whether a download is an HLS playlist or a DASH manifest. The address decides first; then the content type the
/// browser hand-off gave (<see cref="DownloadJob.HandoffContentType"/>), never <see cref="DownloadJob.ContentType"/>,
/// which a plain transfer overwrites with the server's answer: a .m3u list served as audio/x-mpegurl must not turn into
/// a playlist on its next run. The description is the user's text, so it counts only
/// when the whole of it is such a MIME type: hand-offs from before the content type was carried put it there, and
/// "taken from the m3u8 page" must not turn a plain file into a playlist.
/// </summary>
public static class StreamManifestHint
{
    public static bool IsHls(DownloadJob job)
        => PathEndsWith(job.Url, ".m3u8") || IsHlsMediaType(job.HandoffContentType) || IsHlsMediaType(DescriptionMediaType(job.Description));

    public static bool IsDash(DownloadJob job)
        => PathEndsWith(job.Url, ".mpd") || IsDashMediaType(job.HandoffContentType) || IsDashMediaType(DescriptionMediaType(job.Description));

    /// <summary>The media types alone (content type, or a description that is only a MIME type), without the address.</summary>
    public static bool HintsStream(string? contentType, string? description)
        => IsHlsMediaType(contentType) || IsDashMediaType(contentType) ||
           DescriptionMediaType(description) is { } fromDescription &&
           (IsHlsMediaType(fromDescription) || IsDashMediaType(fromDescription));

    public static bool IsHlsMediaType(string? contentType)
        => MediaTypeOf(contentType) is { } type && type.Contains("mpegurl", StringComparison.OrdinalIgnoreCase);

    public static bool IsDashMediaType(string? contentType)
        => MediaTypeOf(contentType) is { } type && type.Equals("application/dash+xml", StringComparison.OrdinalIgnoreCase);

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
