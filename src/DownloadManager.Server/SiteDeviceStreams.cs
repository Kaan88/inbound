using DownloadManager.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;

namespace DownloadManager.Server;

/// <summary>
/// SITES.7, "to this device" and "from this device": <c>GET /api/sites/device?id=&amp;path=</c> sends a site's file to the
/// browser as a download, and <c>POST /api/sites/device?id=&amp;folder=&amp;replace=</c> takes the request body as a new
/// file on the site, its name in <c>X-File-Name</c> (percent-encoded, as the torrent upload does, so it stays out of the
/// address and the access logs). Both go through <c>sites.device.*</c> in pieces (<see cref="SiteDeviceLimits.ChunkBytes"/>),
/// with the same backend as <c>/api/ipc</c>: the server's own Host, or the app's pipe as the browser's web session. Nothing
/// is written to a disk here. Signed-in browsers and a paired desktop app's token, like the uploads.
/// </summary>
internal static class SiteDeviceStreams
{
    public const string Path = "/api/sites/device";
    public const string FileNameHeader = "X-File-Name";

    public static void Map(WebApplication web, IEnvelopeBackend backend, CancellationToken stopping)
    {
        web.MapGet(Path, context => SendAsync(context, backend, stopping));
        web.MapPost(Path, context => ReceiveAsync(context, backend, stopping));
    }

    private static async Task SendAsync(HttpContext context, IEnvelopeBackend backend, CancellationToken stopping)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, stopping);
        var token = lifetime.Token;
        var opened = await AskAsync<SiteDeviceStreamDto>(context, backend, SitesMessageTypes.DeviceReadOpen, new SiteDeviceOpenRequest
        {
            Id = context.Request.Query["id"].ToString(),
            Path = context.Request.Query["path"].ToString(),
        }, token).ConfigureAwait(false);
        if (opened is null) return;
        if (!opened.Ok || opened.StreamId is null)
        {
            await FailAsync(context, StatusCodes.Status400BadRequest, opened.Message ?? "The file could not be read.", opened.Problem).ConfigureAwait(false);
            return;
        }

        var done = false;
        try
        {
            context.Response.ContentType = "application/octet-stream";
            if (opened.Size >= 0) context.Response.ContentLength = opened.Size;
            var disposition = new ContentDispositionHeaderValue("attachment");
            disposition.SetHttpFileName(opened.Name ?? "file");
            context.Response.Headers.ContentDisposition = disposition.ToString();
            context.Response.Headers.CacheControl = "no-store";
            while (!done)
            {
                var chunk = await AskAsync<SiteDeviceChunkDto>(null, backend, SitesMessageTypes.DeviceRead,
                    new SiteDeviceChunkRequest { StreamId = opened.StreamId }, token).ConfigureAwait(false);
                if (chunk is not { Ok: true })
                {
                    // Headers are gone: a cut-off body is all a browser can be told (it shows the download as failed).
                    context.Abort();
                    return;
                }

                if (chunk.Data is { Length: > 0 } data) await context.Response.Body.WriteAsync(data, token).ConfigureAwait(false);
                done = chunk.Done;
            }
        }
        finally
        {
            if (!done) await CloseAsync(backend, opened.StreamId).ConfigureAwait(false);
        }
    }

    private static async Task ReceiveAsync(HttpContext context, IEnvelopeBackend backend, CancellationToken stopping)
    {
        // The site decides how big a file may be, not this server: no request size limit for this one path.
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = null;
        var size = context.Request.ContentLength;
        if (size is null)
        {
            await FailAsync(context, StatusCodes.Status411LengthRequired, "The file's size is missing.", null).ConfigureAwait(false);
            return;
        }

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, stopping);
        var token = lifetime.Token;
        var opened = await AskAsync<SiteDeviceStreamDto>(context, backend, SitesMessageTypes.DeviceWriteOpen, new SiteDeviceOpenRequest
        {
            Id = context.Request.Query["id"].ToString(),
            Folder = context.Request.Query["folder"].ToString(),
            Name = Uri.UnescapeDataString(context.Request.Headers[FileNameHeader].ToString()),
            Size = size.Value,
            Replace = string.Equals(context.Request.Query["replace"].ToString(), "1", StringComparison.Ordinal),
        }, token).ConfigureAwait(false);
        if (opened is null) return;
        if (!opened.Ok || opened.StreamId is null)
        {
            await FailAsync(context, opened.Problem == SiteProblems.Exists ? StatusCodes.Status409Conflict : StatusCodes.Status400BadRequest,
                opened.Message ?? "The file could not be sent.", opened.Problem).ConfigureAwait(false);
            return;
        }

        var finished = false;
        try
        {
            var buffer = new byte[SiteDeviceLimits.ChunkBytes];
            long sent = 0;
            while (true)
            {
                var filled = 0;
                int read;
                while (filled < buffer.Length && (read = await context.Request.Body.ReadAsync(buffer.AsMemory(filled), token).ConfigureAwait(false)) > 0)
                    filled += read;
                sent += filled;
                var last = filled < buffer.Length || sent == size.Value;
                var chunk = await AskAsync<SiteDeviceChunkDto>(null, backend, SitesMessageTypes.DeviceWrite,
                    new SiteDeviceChunkRequest { StreamId = opened.StreamId, Data = buffer[..filled], Last = last }, token).ConfigureAwait(false);
                if (chunk is not { Ok: true })
                {
                    await FailAsync(context, StatusCodes.Status502BadGateway, chunk?.Message ?? "The file could not be sent.", null).ConfigureAwait(false);
                    finished = chunk is not null; // the Host closed it with its answer
                    return;
                }

                if (last)
                {
                    finished = true;
                    await context.Response.WriteAsJsonAsync(new { ok = true, name = opened.Name }, token).ConfigureAwait(false);
                    return;
                }
            }
        }
        finally
        {
            if (!finished) await CloseAsync(backend, opened.StreamId).ConfigureAwait(false);
        }
    }

    /// <summary>One <c>sites.device.*</c> request. Null after a refusal that was already answered to <paramref name="context"/>.</summary>
    private static async Task<T?> AskAsync<T>(HttpContext? context, IEnvelopeBackend backend, string type, object payload, CancellationToken cancellationToken)
        where T : class
    {
        IpcMessage? reply;
        try
        {
            reply = await backend.SendOneAsync(IpcMessage.Create(type, payload), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is BackendUnavailableException or TimeoutException)
        {
            if (context is not null) await FailAsync(context, StatusCodes.Status503ServiceUnavailable, e.Message, null).ConfigureAwait(false);
            return null;
        }

        if (reply?.Type == MessageTypes.HostActionResult)
        {
            // Refused before Sites (not available, or this session may not).
            if (context is not null)
                await FailAsync(context, StatusCodes.Status403Forbidden, reply.GetPayload<HostActionResult>().Message ?? "Sites can't be used from here.", null).ConfigureAwait(false);
            return null;
        }

        return reply?.GetPayload<T>();
    }

    private static async Task CloseAsync(IEnvelopeBackend backend, string streamId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await backend.SendOneAsync(IpcMessage.Create(SitesMessageTypes.DeviceClose, new SiteDeviceChunkRequest { StreamId = streamId }), timeout.Token)
                .ConfigureAwait(false);
        }
        catch (Exception e) when (e is BackendUnavailableException or TimeoutException or OperationCanceledException)
        {
            // The Host closes an unused stream itself.
        }
    }

    private static async Task FailAsync(HttpContext context, int status, string message, string? problem)
    {
        if (context.Response.HasStarted) return;
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new { ok = false, error = message, problem }).ConfigureAwait(false);
    }
}
