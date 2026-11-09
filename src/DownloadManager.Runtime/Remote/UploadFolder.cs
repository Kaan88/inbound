using System.Security.Cryptography;

namespace DownloadManager.Runtime.Remote;

/// <summary>
/// Files a browser sent the Host (a <c>.torrent</c> file, or an <c>.rsdf</c>, <c>.ccf</c> or <c>.dlc</c> container), kept
/// under a random name in <c>uploads</c> in the data folder. Only a server's web endpoints write here: the Docker server
/// today (<c>TorrentUploads</c>); the Windows app's web bridge does not store uploads yet, so the desktop Host sets no
/// <see cref="RemoteJobCoordinator.ResolveReference"/> and its hello has no <c>uploads</c>. A client gets a reference
/// (<c>upload:</c> and the random name), never the path, which would tell where the state is kept; the provider and
/// container commands take it (<see cref="RemoteJobCoordinator.ResolveReference"/>). A reference names a file here by
/// construction, so it is read wherever the caller's own folders are: it can't name anything else. Files older than a
/// day go, at start and with each upload, and at most <see cref="MaxFiles"/> of each kind (torrents, containers) are
/// kept, so a burst of one kind can't take away the other's that an open dialog still names.
/// </summary>
public sealed class UploadFolder
{
    public const string FolderName = "uploads";
    public const string ReferencePrefix = "upload:";
    public const string TorrentExtension = ".torrent";

    /// <summary>The container kinds the container commands read. The extension tells RSDF apart.</summary>
    public static readonly IReadOnlyList<string> ContainerExtensions = [".rsdf", ".ccf", ".dlc"];

    /// <summary>Every kind kept here, in the order a reference is looked up.</summary>
    private static readonly string[] Extensions = [TorrentExtension, ".rsdf", ".ccf", ".dlc"];

    private const string PartialExtension = ".partial";
    private static readonly TimeSpan KeepFor = TimeSpan.FromDays(1);
    public const int MaxFiles = 200;

    /// <summary>What a caller says for a reference whose file has gone (cleaned up after a day, or never there).</summary>
    public const string GoneText = "The uploaded file is no longer there. Upload it again.";

    public UploadFolder(string directory) => Directory = directory;

    public string Directory { get; }

    /// <summary>The folder in <paramref name="dataFolder"/>, made if missing and cleaned.</summary>
    public static UploadFolder Prepare(string dataFolder)
    {
        var uploads = new UploadFolder(Path.Combine(dataFolder, FolderName));
        System.IO.Directory.CreateDirectory(uploads.Directory);
        uploads.Clean();
        return uploads;
    }

    /// <summary>
    /// Whether the text is a reference as this folder writes them (<c>upload:</c> and 32 lowercase hex digits), whether
    /// or not its file is still here. Anything else is not a reference at all, and is read as the text it is.
    /// </summary>
    public static bool IsReference(string? text) => NameOf(text?.Trim()) is not null;

    private static string? NameOf(string? text)
    {
        if (text is null || !text.StartsWith(ReferencePrefix, StringComparison.Ordinal)) return null;
        var name = text[ReferencePrefix.Length..];
        return name.Length == 32 && name.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') ? name : null;
    }

    /// <summary>
    /// The path of the file a reference stands for, whichever kind it is. Null when the text isn't a reference, or when
    /// its file is gone (<see cref="IsReference"/> tells those apart, for <see cref="GoneText"/>).
    /// </summary>
    public string? Resolve(string text)
    {
        if (NameOf(text) is not { } name) return null;
        foreach (var extension in Extensions)
        {
            var path = Path.Combine(Directory, name + extension);
            if (File.Exists(path)) return path;
        }

        return null;
    }

    /// <summary>
    /// Keeps what <paramref name="source"/> holds as a new upload of this kind and answers its reference. Written under a
    /// working name, flushed and then renamed, so a reference never names half a file.
    /// </summary>
    /// <param name="extension">One of <see cref="TorrentExtension"/> and <see cref="ContainerExtensions"/>.</param>
    /// <exception cref="UploadTooLargeException">More than <paramref name="maxBytes"/> was sent.</exception>
    /// <exception cref="InvalidDataException">Nothing was sent.</exception>
    public async Task<string> SaveAsync(Stream source, string extension, long maxBytes, CancellationToken cancellationToken)
    {
        if (!Extensions.Contains(extension, StringComparer.Ordinal)) throw new ArgumentException("Not a kind of file kept here.", nameof(extension));
        // Made here too, so an endpoint whose folder wasn't prepared (or was removed since) still keeps the file.
        System.IO.Directory.CreateDirectory(Directory);
        Clean();
        var name = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var partial = Path.Combine(Directory, name + PartialExtension);
        try
        {
            await using (var file = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > maxBytes) throw new UploadTooLargeException();
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }

                if (total == 0) throw new InvalidDataException("empty");
                await file.FlushAsync(cancellationToken).ConfigureAwait(false);
                file.Flush(flushToDisk: true);
            }

            File.Move(partial, Path.Combine(Directory, name + extension));
        }
        catch
        {
            TryDelete(partial);
            throw;
        }

        return ReferencePrefix + name;
    }

    /// <summary>Removes uploads older than a day and all but the newest <see cref="MaxFiles"/> of each kind. Best effort.</summary>
    public void Clean()
    {
        try
        {
            var files = new DirectoryInfo(Directory).EnumerateFiles()
                .Where(file => Extensions.Contains(file.Extension, StringComparer.Ordinal) || file.Extension == PartialExtension)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ToList();
            var keptTorrents = 0;
            var keptContainers = 0;
            foreach (var file in files)
            {
                var old = DateTime.UtcNow - file.LastWriteTimeUtc > KeepFor;
                // One being written right now is never in the count, so a burst of uploads can't remove it under its writer.
                if (file.Extension == PartialExtension) { if (old) TryDelete(file.FullName); continue; }
                var kept = file.Extension == TorrentExtension ? ++keptTorrents : ++keptContainers;
                if (old || kept > MaxFiles) TryDelete(file.FullName);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cleaning up is best effort.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort
        }
    }
}

/// <summary>An upload sent more than its kind may be (<see cref="UploadFolder.SaveAsync"/>). Nothing of it is kept.</summary>
public sealed class UploadTooLargeException() : IOException("The upload is larger than allowed.");
