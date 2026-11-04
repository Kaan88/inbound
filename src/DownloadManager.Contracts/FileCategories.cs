namespace DownloadManager.Contracts;

public static class FileCategories
{
    public static readonly string[] Names =
    {
        "compressed", "documents", "music", "programs", "video",
    };

    public static string For(string? fileName)
    {
        var extension = Path.GetExtension(fileName ?? string.Empty).TrimStart('.').ToLowerInvariant();
        return extension switch
        {
            "zip" or "7z" or "rar" or "gz" or "gzip" or "bz2" or "tar" or "xz" or "zst"
                or "iso" or "cab" or "ace" or "arj" or "lzh" => "compressed",
            "pdf" or "doc" or "docx" or "xls" or "xlsx" or "ppt" or "pptx" or "txt"
                or "rtf" or "epub" or "odt" or "ods" or "odp" or "csv" => "documents",
            "mp3" or "flac" or "wav" or "m4a" or "aac" or "ogg" or "wma" or "opus"
                or "aif" or "ra" => "music",
            "exe" or "msi" or "apk" or "msix" or "appx" or "bat" or "cmd" or "deb"
                or "rpm" or "dmg" or "pkg" => "programs",
            "mp4" or "mkv" or "avi" or "mov" or "wmv" or "webm" or "m4v" or "mpg"
                or "mpeg" or "flv" or "ts" or "3gp" or "rm" or "rmvb" => "video",
            _ => "other",
        };
    }

    public static string Normalize(string? category)
        => Names.FirstOrDefault(name => string.Equals(name, category, StringComparison.OrdinalIgnoreCase))
           ?? "other";
}
