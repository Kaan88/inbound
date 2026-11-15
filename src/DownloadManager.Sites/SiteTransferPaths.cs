using System.Text.RegularExpressions;

namespace DownloadManager.Sites;

/// <summary>
/// Names and paths for site transfers. A batch item's path is relative, <c>/</c>-separated, and every segment must be a
/// name this PC can hold: a server may have a file called <c>..\x</c> or <c>a:b</c>, which on Windows would point outside
/// the target folder or into a stream. Such a file is refused, never renamed behind the user's back.
/// </summary>
public static partial class SiteTransferPaths
{
    private static readonly string[] Reserved =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
        "CONIN$", "CONOUT$",
    ];

    /// <summary>Why <paramref name="name"/> (one segment) can't be a file or folder name on this PC, or null.</summary>
    public static string? LocalNameProblem(string name) => LocalNameProblem(name, OperatingSystem.IsWindows());

    internal static string? LocalNameProblem(string name, bool windows)
    {
        const string cant = "can't be used as a name on this PC";
        if (name.Length == 0 || name is "." or "..") return $"\"{name}\" {cant}.";
        if (name.Length > 255) return "The name is longer than this PC allows.";
        if (name.Any(c => c == '/' || c == '\0')) return $"The name {cant}.";
        if (!windows) return null;
        if (name.Any(c => c < 32 || c is '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|'))
            return $"\"{name}\" {cant}: it has a character Windows does not allow (\\ : * ? \" < > |).";
        if (name.EndsWith('.') || name.EndsWith(' ')) return $"\"{name}\" {cant}: Windows does not allow a name ending in a dot or a space.";
        var stem = name.Split('.')[0].TrimEnd(' ');
        if (Reserved.Contains(stem, StringComparer.OrdinalIgnoreCase)) return $"\"{name}\" {cant}: Windows keeps that name for a device.";
        return null;
    }

    /// <summary>Why a relative item path can't be placed under a folder on this PC, or null.</summary>
    public static string? LocalPathProblem(string relative)
    {
        if (string.IsNullOrEmpty(relative)) return "The file has no name.";
        foreach (var segment in relative.Split('/'))
            if (LocalNameProblem(segment) is { } problem) return problem;
        return null;
    }

    /// <summary>A relative path's segments are all plain names (no empty, <c>.</c> or <c>..</c> segment, no control character).</summary>
    public static bool IsRelative(string? relative) =>
        !string.IsNullOrEmpty(relative) && relative.Length <= SitePaths.MaxLength && !relative.Any(char.IsControl)
        && relative.Split('/').All(s => s.Length > 0 && s is not "." and not "..");

    /// <summary>The full path on this PC of a relative item path under <paramref name="folder"/>. Call after <see cref="LocalPathProblem"/>.</summary>
    public static string Local(string folder, string relative) =>
        Path.Combine([folder, .. relative.Split('/')]);

    /// <summary>The absolute server path of a relative item path under <paramref name="folder"/>.</summary>
    public static string Remote(string folder, string relative) =>
        folder == "/" ? "/" + relative : folder + "/" + relative;

    /// <summary><c>report (2).pdf</c> for <c>report.pdf</c> and 2; the number goes before the last extension.</summary>
    public static string Numbered(string relative, int number)
    {
        var slash = relative.LastIndexOf('/');
        var folder = slash < 0 ? "" : relative[..(slash + 1)];
        var name = relative[(slash + 1)..];
        var dot = name.LastIndexOf('.');
        return dot <= 0 ? $"{folder}{name} ({number})" : $"{folder}{name[..dot]} ({number}){name[dot..]}";
    }

    /// <summary>
    /// rclone's temporary name for a file it is writing: <c>name.&lt;token&gt;.partial</c> next to the target, the token
    /// eight lowercase hex digits, new for each copy (seen with rclone 1.76.0, six copies: <c>big.bin.63642737.partial</c>,
    /// <c>c1.bin.b1dc66bb.partial</c>). Kept that narrow so a user's own <c>report.old.partial</c> is never taken for one.
    /// </summary>
    public static bool IsPartialOf(string candidate, string targetName) =>
        candidate.Length > targetName.Length + 1
        && candidate.StartsWith(targetName + ".", StringComparison.Ordinal)
        && PartialTail().IsMatch(candidate.AsSpan(targetName.Length));

    [GeneratedRegex(@"^\.[0-9a-f]{8}\.partial$", RegexOptions.CultureInvariant)]
    private static partial Regex PartialTail();
}
