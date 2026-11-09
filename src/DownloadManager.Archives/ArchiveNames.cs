namespace DownloadManager.Archives;

/// <summary>Which names are archives other than RAR. Reads no files.</summary>
public static class ArchiveNames
{
    private static readonly string[] CompressedTars = [".tar.gz", ".tgz", ".tar.bz2", ".tbz2", ".tbz", ".tar.xz", ".txz", ".tar.zst", ".tzst"];
    private static readonly string[] Plain = [".7z", ".zip", ".tar"];

    /// <summary>
    /// Whether a chooser of archive files on disk lists this name: one extraction starts from, by the same rules. A RAR
    /// volume (any <c>.rar</c>), a 7z, zip or tar, a compressed tar (<see cref="StemOfSingle"/>), or the first piece of a
    /// split 7z, zip, RAR or tar set (<c>name.7z.001</c>). Not a lone <c>.gz</c> or <c>.xz</c> that holds no tar, any
    /// other <c>.001</c>, nor the later pieces (<c>.002</c>, <c>.r00</c>, <c>.z01</c>), which the set's first one stands
    /// for. By the name alone; the file is not read. A name that is only the extension (<c>.zip</c>) is not listed.
    /// </summary>
    public static bool IsChooserName(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return false;
        if (fileName.Length > ".rar".Length && fileName.EndsWith(".rar", StringComparison.OrdinalIgnoreCase)) return true;
        if (StemOfSingle(fileName) is { Length: > 0 }) return true;
        return SplitInner.Select(inner => "." + inner + ".001")
            .Any(suffix => fileName.Length > suffix.Length && fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }

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
