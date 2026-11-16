namespace DownloadManager.Contracts;

/// <summary>
/// SITES.5: the names a new folder or a rename may give an item on a site's server. Shared by the Host (which decides)
/// and the windows (which say it before sending).
/// </summary>
public static class SiteNameRules
{
    public const int MaxLength = 255;

    /// <summary>
    /// Why <paramref name="name"/> can't be one item's name in a folder on the server, or null when it can: not empty or
    /// spaces only, no <c>/</c>, not <c>.</c> or <c>..</c>, no control characters, at most <see cref="MaxLength"/> characters.
    /// A backslash is allowed: it is part of a name on an SFTP server.
    /// </summary>
    public static string? Problem(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Type a name.";
        if (name.Length > MaxLength) return $"That name is too long ({MaxLength} characters at most).";
        if (name is "." or "..") return "That name can't be used.";
        if (name.Contains('/')) return "A name can't hold a slash (/).";
        if (name.Any(char.IsControl)) return "That name holds a character that can't be used.";
        return null;
    }
}
