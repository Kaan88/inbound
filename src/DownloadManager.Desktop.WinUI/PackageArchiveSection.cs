using System.Text.RegularExpressions;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// UI.R23 (6): whether the package window shows its archive password section. Free of WinUI so it can be tested.
/// The names follow the Host's own volume check (RAR, name.r00, name.7z.001, name.z01, single 7z, zip, tar and
/// compressed tars); the Host still decides what is a set.
/// </summary>
internal static partial class PackageArchiveSection
{
    private static readonly string[] Suffixes =
        [".rar", ".7z", ".zip", ".tar", ".tar.gz", ".tgz", ".tar.bz2", ".tbz2", ".tbz", ".tar.xz", ".txz", ".tar.zst", ".tzst"];

    public static bool LooksLikeArchive(string? fileName) =>
        !string.IsNullOrEmpty(fileName) &&
        (Suffixes.Any(suffix => fileName.Length > suffix.Length && fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) ||
         OtherVolumeName().IsMatch(fileName));

    /// <summary>
    /// Shown when the package has an archive set, a password on record, or a file named like an archive. A package
    /// with no files yet, or a file whose name isn't known yet, keeps it too, as before: it may still bring archives.
    /// </summary>
    public static bool Show(IReadOnlyCollection<string?> memberNames, int archiveSets, bool hasPassword) =>
        archiveSets > 0 || hasPassword || memberNames.Count == 0 ||
        memberNames.Any(name => string.IsNullOrWhiteSpace(name) || LooksLikeArchive(name));

    // name.r00 (old-style RAR), name.7z.001 (split) and name.z01 (spanned zip).
    [GeneratedRegex(@"(\.[r-z]\d{2}|\.(?:7z|zip|rar|tar)\.\d{3}|\.z\d{2,3})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OtherVolumeName();
}
