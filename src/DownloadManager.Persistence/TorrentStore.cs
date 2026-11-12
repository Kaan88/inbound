using System.Text.Json;
using DownloadManager.Core;

namespace DownloadManager.Persistence;

/// <summary>
/// <c>torrents/{id:N}.torrent</c> (the metadata), <c>.resume</c> (the worker's latest resume data) and <c>.files.json</c>
/// (the file list with each file's choice), written atomically like every other durable file.
/// </summary>
public sealed class TorrentStore(StoragePaths paths) : ITorrentStore
{
    /// <summary>The worker refuses metadata above 16 MiB; resume data carries the metadata plus piece state.</summary>
    private const long MaxBlobBytes = 64L * 1024 * 1024;

    private string PathOf(Guid jobId, string extension) => Path.Combine(paths.TorrentsDirectory, $"{jobId:N}{extension}");

    public Task SaveMetadataAsync(Guid jobId, byte[] torrent, CancellationToken cancellationToken)
        => AtomicFile.WriteBytesAsync(PathOf(jobId, ".torrent"), torrent, cancellationToken);

    public Task<byte[]?> LoadMetadataAsync(Guid jobId, CancellationToken cancellationToken)
        => ReadAsync(PathOf(jobId, ".torrent"), cancellationToken);

    public Task SaveResumeAsync(Guid jobId, byte[] resume, CancellationToken cancellationToken)
        => AtomicFile.WriteBytesAsync(PathOf(jobId, ".resume"), resume, cancellationToken);

    public Task<byte[]?> LoadResumeAsync(Guid jobId, CancellationToken cancellationToken)
        => ReadAsync(PathOf(jobId, ".resume"), cancellationToken);

    public Task SaveFilesAsync(Guid jobId, IReadOnlyList<TorrentFile> files, CancellationToken cancellationToken)
        => AtomicFile.WriteAsync(PathOf(jobId, ".files.json"), JsonSerializer.Serialize(files, PersistenceJson.Options), cancellationToken);

    public async Task<IReadOnlyList<TorrentFile>?> LoadFilesAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var bytes = await ReadAsync(PathOf(jobId, ".files.json"), cancellationToken).ConfigureAwait(false);
        if (bytes is null) return null;
        try
        {
            return JsonSerializer.Deserialize<List<TorrentFile>>(bytes, PersistenceJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public Task DeleteAsync(Guid jobId, CancellationToken cancellationToken)
    {
        foreach (var extension in new[] { ".torrent", ".resume", ".files.json" })
        {
            try
            {
                File.Delete(PathOf(jobId, extension));
            }
            catch (IOException)
            {
                // best effort; a leftover is ignored by a job that no longer exists
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return Task.CompletedTask;
    }

    private static async Task<byte[]?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxBlobBytes) return null;
            return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }
}
