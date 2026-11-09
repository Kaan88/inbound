using DownloadManager.Runtime.Remote;
using DownloadManager.Runtime.WebUi;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace DownloadManager.Server;

/// <summary>
/// <c>POST /api/upload/torrent</c>: a <c>.torrent</c> file from the web UI, as the request body. The server can only
/// add a torrent file by a path it may read, so the file is kept under a random name in <c>uploads</c> in the data
/// folder. The answer gives a reference to it (<c>upload:</c> and the random name), not its path, which would tell
/// where the server keeps its state; <c>remote.classify</c> and <c>remote.add</c> take the reference
/// (<see cref="RemoteJobCoordinator.ResolveReference"/>, <see cref="Resolve"/>). That folder is the one
/// place outside the roots a torrent file may be read from (<see cref="RemoteJobCoordinator.Roots"/>); nothing else
/// reads it. Files older than a day are removed, at start and with each upload. Signed-in users only (the sign-in gate).
/// </summary>
public sealed class TorrentUploads
{
    public const string FolderName = UploadReferences.FolderName;
    private static readonly TimeSpan KeepFor = TimeSpan.FromDays(1);
    private const int MaxFiles = 200;

    private TorrentUploads(string directory) => Directory = directory;

    public string Directory { get; }

    public const string ReferencePrefix = UploadReferences.Prefix;

    /// <summary>The path an upload reference stands for, or null when the text isn't one (<see cref="UploadReferences"/>).</summary>
    public string? Resolve(string text) => UploadReferences.Resolve(Directory, text);

    public static TorrentUploads Prepare(string dataFolder)
    {
        var uploads = new TorrentUploads(Path.Combine(dataFolder, FolderName));
        System.IO.Directory.CreateDirectory(uploads.Directory);
        uploads.Clean();
        return uploads;
    }

    public void Map(WebApplication web) => web.MapPost("/api/upload/torrent", async context =>
    {
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = TorrentMetadata.MaxFileBytes;
        if (context.Request.ContentLength is > TorrentMetadata.MaxFileBytes)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            await context.Response.WriteAsJsonAsync(new { ok = false, error = "This file is too large to be a .torrent file." }).ConfigureAwait(false);
            return;
        }

        Clean();
        var (name, reference) = UploadReferences.NewName();
        var path = UploadReferences.FileFor(Directory, name);
        try
        {
            await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await context.Request.Body.CopyToAsync(file, context.RequestAborted).ConfigureAwait(false);
                if (file.Length == 0) throw new InvalidDataException("empty");
                if (file.Length > TorrentMetadata.MaxFileBytes) throw new InvalidDataException("too large");
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or BadHttpRequestException or OperationCanceledException)
        {
            TryDelete(path);
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { ok = false, error = "The file could not be received." }).ConfigureAwait(false);
            return;
        }

        await context.Response.WriteAsJsonAsync(new { ok = true, upload = reference }).ConfigureAwait(false);
    });

    private void Clean()
    {
        try
        {
            var files = new DirectoryInfo(Directory).GetFiles("*.torrent").OrderByDescending(file => file.LastWriteTimeUtc).ToList();
            for (var i = 0; i < files.Count; i++)
            {
                if (i >= MaxFiles || DateTime.UtcNow - files[i].LastWriteTimeUtc > KeepFor) TryDelete(files[i].FullName);
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
