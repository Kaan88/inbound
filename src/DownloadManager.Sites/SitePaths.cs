namespace DownloadManager.Sites;

/// <summary>
/// Paths on a site's server (SITES.3): absolute, <c>/</c>-separated, as rclone sees them with the remote's root at
/// <c>/</c> (<see cref="SiteRemote"/>). Only <c>/</c> separates: a backslash is part of a name on an SFTP server.
/// </summary>
public static class SitePaths
{
    public const int MaxLength = 4096;
    public const int MaxStars = 100;

    /// <summary>
    /// <c>/a/b</c> for <c>a/b</c>, <c>/a//b/</c> or <c>/a/b</c>; <c>/</c> for an empty path. Null for one with a <c>.</c> or
    /// <c>..</c> segment, a control character, or longer than <see cref="MaxLength"/>.
    /// </summary>
    public static string? Normalize(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "/";
        if (path.Length > MaxLength || path.Any(char.IsControl)) return null;
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(s => s is "." or "..")) return null;
        return "/" + string.Join('/', segments);
    }

    /// <summary>
    /// A path that may be starred: normalised, not the root, and without format or line/paragraph separator characters
    /// (right-to-left overrides and the like), which could make a starred name in the sidebar read as another.
    /// </summary>
    public static bool IsStarrable(string? path) =>
        path is { Length: > 1 } && Normalize(path) == path
        && !path.Any(c => char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.Format
            or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator);

    /// <summary>Why <paramref name="name"/> can't be one item's name on the server; see <see cref="Contracts.SiteNameRules"/>.</summary>
    public static string? NameProblem(string? name) => Contracts.SiteNameRules.Problem(name);

    /// <summary>The <c>remote</c> argument for rclone: the path without its leading slash (<c>""</c> for the root).</summary>
    public static string ToRemote(string normalized) => normalized.TrimStart('/');
}
