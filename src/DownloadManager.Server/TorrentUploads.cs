using DownloadManager.Containers;
using DownloadManager.Runtime.Remote;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace DownloadManager.Server;

/// <summary>
/// The web UI's file uploads, as the request body: <c>POST /api/upload/torrent</c> for a <c>.torrent</c> file and
/// <c>POST /api/upload/container</c> for an <c>.rsdf</c>, <c>.ccf</c> or <c>.dlc</c> container, its file name in the
/// <c>X-File-Name</c> header (percent-encoded, so it stays out of the address and the access logs). The server can
/// only read such a file by a path, so it is kept in <see cref="UploadFolder"/> (<c>uploads</c> in the data folder) and
/// the answer gives a reference to it (<c>upload:</c> and a random name), not its path, which would tell where the
/// server keeps its state. <c>remote.classify</c> and <c>remote.add</c> take a torrent's reference, and
/// <c>remote.container.preview</c> a container's (<see cref="RemoteJobCoordinator.ResolveReference"/>, <see cref="Resolve"/>).
/// Nothing else reads that folder. Signed-in users only (the sign-in gate): a paired app's token isn't read here.
/// </summary>
public sealed class TorrentUploads
{
    /// <summary>The container's own file name, percent-encoded (<c>encodeURIComponent</c>). Only its extension is used.</summary>
    public const string FileNameHeader = "X-File-Name";
    public const string FolderName = UploadFolder.FolderName;
    public const string ReferencePrefix = UploadFolder.ReferencePrefix;

    private readonly UploadFolder _folder;

    private TorrentUploads(UploadFolder folder) => _folder = folder;

    public string Directory => _folder.Directory;

    /// <summary>The path an upload reference stands for, or null when the text isn't one.</summary>
    public string? Resolve(string text) => _folder.Resolve(text);

    public static TorrentUploads Prepare(string dataFolder) => new(UploadFolder.Prepare(dataFolder));

    public void Map(WebApplication web)
    {
        web.MapPost("/api/upload/torrent", context =>
            ReceiveAsync(context, UploadFolder.TorrentExtension, TorrentMetadata.MaxFileBytes, "This file is too large to be a .torrent file."));

        web.MapPost("/api/upload/container", context =>
        {
            // The kind is the file's own extension: an .rsdf file is told apart by it. Only the name's extension is used.
            var extension = Path.GetExtension(DecodedName(context.Request.Headers[FileNameHeader].ToString())).ToLowerInvariant();
            if (!UploadFolder.ContainerExtensions.Contains(extension))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return context.Response.WriteAsJsonAsync(new { ok = false, error = "Only .rsdf, .ccf and .dlc files can be imported." });
            }

            return ReceiveAsync(context, extension, ContainerLimits.Default.MaxInputBytes, "This file is too large to be a container.");
        });
    }

    private async Task ReceiveAsync(HttpContext context, string extension, long maxBytes, string tooLarge)
    {
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = maxBytes;
        if (context.Request.ContentLength > maxBytes)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            await context.Response.WriteAsJsonAsync(new { ok = false, error = tooLarge }).ConfigureAwait(false);
            return;
        }

        string upload;
        try
        {
            upload = await _folder.SaveAsync(context.Request.Body, extension, maxBytes, context.RequestAborted).ConfigureAwait(false);
        }
        // Past the limit while reading: ours, or Kestrel's for a body sent without a length (chunked).
        catch (Exception ex) when (ex is UploadTooLargeException or BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge })
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            await context.Response.WriteAsJsonAsync(new { ok = false, error = tooLarge }).ConfigureAwait(false);
            return;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or BadHttpRequestException or OperationCanceledException or UnauthorizedAccessException)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { ok = false, error = "The file could not be received." }).ConfigureAwait(false);
            return;
        }

        await context.Response.WriteAsJsonAsync(new { ok = true, upload }).ConfigureAwait(false);
    }

    /// <summary>The header's name, decoded. A sequence that isn't valid percent-encoding is left as sent.</summary>
    private static string DecodedName(string header) => Uri.UnescapeDataString(header);
}
