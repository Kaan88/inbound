namespace DownloadManager.Archives;

/// <summary>Which names are archives other than RAR. Reads no files.</summary>
public static class ArchiveNames
{
    private static readonly string[] CompressedTars = [".tar.gz", ".tgz", ".tar.bz2", ".tbz2", ".tbz", ".tar.xz", ".txz", ".tar.zst", ".tzst"];
    private static readonly string[] Plain = [".7z", ".zip", ".tar"];

    /// <summary>
    /// What a chooser of archive files on disk offers: every name extraction can start from (a RAR volume, a 7z, zip
    /// or tar, a compressed tar, the first piece of a split set). By the name alone; the file is not read.
    /// </summary>
    public static readonly IReadOnlyList<string> ChooserExtensions =
        [".rar", ".7z", ".zip", ".tar", ".gz", ".tgz", ".bz2", ".tbz2", ".tbz", ".xz", ".txz", ".zst", ".tzst", ".001"];

    /// <summary>Whether a chooser of archive files lists this name (<see cref="ChooserExtensions"/>).</summary>
    public static bool IsChooserName(string? fileName) =>
        !string.IsNullOrEmpty(fileName) && ChooserExtensions.Any(suffix => fileName.Length > suffix.Length &&
            fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

    /// <summary>The kinds a split name.ext.001 set is recognised for.</summary>
    internal static readonly string[] SplitInner = ["7z", "zip", "rar", "tar"];

    /// <summary>A tar inside gzip, bzip2, xz or zstd, which 7-Zip opens in two layers.</summary>
    public static bool IsCompressedTar(string? fileName) =>
        !string.IsNullOrEmpty(fileName) && CompressedTars.Any(suffix => fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

    /// <summary>The name without its archive suffix, for a 7z, zip, tar or compressed tar, or null for anything else.</summary>
    public static string? StemOfSingle(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return null;
        foreach (var suffix in CompressedTars.Concat(Plain))
        {
            if (fileName.Length > suffix.Length && fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return fileName[..^suffix.Length];
        }

        return null;
    }
}
