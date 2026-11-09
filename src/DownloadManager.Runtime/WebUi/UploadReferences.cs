namespace DownloadManager.Runtime.WebUi;

/// <summary>
/// The one definition of an upload's reference (D2, D8): a <c>.torrent</c> file a browser sends is kept in
/// <see cref="FolderName"/> in the data folder under a random name of 32 lower-case hex digits, and the browser gets
/// <see cref="Prefix"/> and that name back. The server's <c>TorrentUploads</c> makes them; the server's coordinator and
/// the desktop Host (for its web bridge) turn them back into the file's path. Nothing else is one.
/// </summary>
public static class UploadReferences
{
    public const string FolderName = "uploads";
    public const string Prefix = "upload:";
    public const string Extension = ".torrent";

    /// <summary>A new random name, and the reference the browser gets for it.</summary>
    public static (string Name, string Reference) NewName()
    {
        var name = Guid.NewGuid().ToString("N");
        return (name, Prefix + name);
    }

    /// <summary>The file a name is kept in, inside <paramref name="uploadFolder"/>.</summary>
    public static string FileFor(string uploadFolder, string name) => Path.Combine(uploadFolder, name + Extension);

    /// <summary>The path a reference stands for inside <paramref name="uploadFolder"/>, or null when the text isn't one.</summary>
    public static string? Resolve(string uploadFolder, string text)
    {
        if (!text.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        var name = text[Prefix.Length..];
        return name.Length == 32 && name.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') ? FileFor(uploadFolder, name) : null;
    }
}
