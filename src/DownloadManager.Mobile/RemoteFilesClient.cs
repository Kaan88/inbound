using DownloadManager.Contracts;

namespace DownloadManager.Mobile;

/// <summary>
/// Remote Files requests on the caller's selected, already-open Host connection.
/// The caller owns the connection and its lifetime, capability checks and host selection.
/// Browse can be slow; callers should provide a suitable connection and cancellation token.
/// </summary>
public sealed class RemoteFilesClient
{
    private readonly IHostConnection _connection;

    public RemoteFilesClient(IHostConnection connection) =>
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    public Task<SitesStatusDto> GetStatusAsync(CancellationToken cancellationToken = default) =>
        RequestAsync<SitesStatusDto>(IpcMessage.Create(SitesMessageTypes.Get, new EmptyPayload()), SitesMessageTypes.Status, cancellationToken);

    public Task<SitesListDto> ListAsync(CancellationToken cancellationToken = default) =>
        RequestAsync<SitesListDto>(IpcMessage.Create(SitesMessageTypes.ListRequest, new EmptyPayload()), SitesMessageTypes.List, cancellationToken);

    /// <summary>Returns one page, including any Host-reported problem and listing token.</summary>
    public Task<SiteBrowseResultDto> BrowseAsync(SiteBrowseRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RequestAsync<SiteBrowseResultDto>(IpcMessage.Create(SitesMessageTypes.Browse, request), SitesMessageTypes.BrowseResult, cancellationToken);
    }

    public Task<SitesStatusDto> SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default) =>
        RequestAsync<SitesStatusDto>(IpcMessage.Create(SitesMessageTypes.SetEnabled, new SitesSetEnabledRequest { Enabled = enabled }), SitesMessageTypes.Status, cancellationToken);

    /// <summary>Secrets in the request are sent to the selected Host and are not retained by this client.</summary>
    public Task<SiteResultDto> SaveAsync(SiteSaveRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RequestAsync<SiteResultDto>(IpcMessage.Create(SitesMessageTypes.Save, request), SitesMessageTypes.Result, cancellationToken);
    }

    public Task<SiteResultDto> RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return RequestAsync<SiteResultDto>(IpcMessage.Create(SitesMessageTypes.Remove, new SiteIdRequest { Id = id }), SitesMessageTypes.Result, cancellationToken);
    }

    /// <summary>The local folder is a path on the selected Host, not necessarily this device.</summary>
    public Task<SiteTransferResultDto> StartTransferAsync(SiteTransferStartRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var command = request.SafeSingleDownload ? SitesMessageTypes.SafeDownloadStart : SitesMessageTypes.TransferStart;
        return RequestAsync<SiteTransferResultDto>(IpcMessage.Create(command, request), SitesMessageTypes.TransferResult, cancellationToken);
    }

    public Task<SiteChangeResultDto> DeleteFilesAsync(SiteChangeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RequestAsync<SiteChangeResultDto>(IpcMessage.Create(SitesMessageTypes.DeleteFiles, request), SitesMessageTypes.ChangeResult, cancellationToken);
    }

    /// <summary>
    /// Streams a device-owned file on the caller's selected Host. The caller owns the source and must
    /// establish the Host's destination and conflict guarantees before exposing this operation.
    /// An uncertain result is never retried here.
    /// </summary>
    public async Task<SiteResultDto> WriteDeviceFileAsync(
        SiteDeviceOpenRequest destination, Stream source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(destination.Size);
        if (!source.CanRead) throw new ArgumentException("A readable file is required.", nameof(source));

        long remaining = destination.Size;
        if (destination.Replace || string.IsNullOrWhiteSpace(destination.Listing))
            throw new ArgumentException("A confirmed no-replace destination listing is required.", nameof(destination));
        var opened = await RequestAsync<SiteDeviceStreamDto>(IpcMessage.Create(SitesMessageTypes.DeviceCreateOpen, destination),
            SitesMessageTypes.DeviceStream, cancellationToken).ConfigureAwait(false);
        if (!opened.Ok)
            return new SiteResultDto { Message = opened.Message, Problem = opened.Problem };
        if (opened.StreamId is not { Length: > 0 } id)
            throw new InvalidDataException("The Host did not identify the opened upload.");

        bool completed = false;
        try
        {
            var buffer = new byte[(int)Math.Min(remaining, SiteDeviceLimits.ChunkBytes)];
            var extra = new byte[1];
            do
            {
                var length = (int)Math.Min(remaining, buffer.Length);
                await source.ReadExactlyAsync(buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
                remaining -= length;
                if (remaining == 0 && await source.ReadAsync(extra.AsMemory(), cancellationToken).ConfigureAwait(false) != 0)
                    throw new InvalidDataException("The file grew after it was selected.");

                var request = new SiteDeviceChunkRequest
                {
                    StreamId = id, Data = length == buffer.Length ? buffer : buffer[..length], Last = remaining == 0
                };
                var piece = await RequestAsync<SiteDeviceChunkDto>(IpcMessage.Create(SitesMessageTypes.DeviceWrite, request),
                    SitesMessageTypes.DeviceChunk, cancellationToken).ConfigureAwait(false);
                if (!piece.Ok) return new SiteResultDto { Message = piece.Message };
                if (piece.Done != request.Last) throw new InvalidDataException("The Host did not confirm the upload at the expected byte count.");
            } while (remaining > 0);
            completed = true;
            return new SiteResultDto { Ok = true };
        }
        finally
        {
            if (!completed)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    await RequestAsync<SiteResultDto>(IpcMessage.Create(SitesMessageTypes.DeviceClose,
                        new SiteDeviceChunkRequest { StreamId = id }), SitesMessageTypes.Result, cleanup.Token).ConfigureAwait(false);
                }
                catch { /* Preserve the original refusal or error. The Host also sweeps abandoned streams. */ }
            }
        }
    }

    public Task<SiteSignInResultDto> SignInSiteAsync(string id, string? token = null, CancellationToken cancellationToken = default) =>
        RequestAsync<SiteSignInResultDto>(IpcMessage.Create(SitesMessageTypes.SignIn, new SiteSignInRequest { Id = id, Token = token }),
            SitesMessageTypes.SignInResult, cancellationToken);

    public Task<SiteResultDto> CancelSiteSignInAsync(string id, CancellationToken cancellationToken = default) =>
        RequestAsync<SiteResultDto>(IpcMessage.Create(SitesMessageTypes.SignInCancel, new SiteIdRequest { Id = id }),
            SitesMessageTypes.Result, cancellationToken);

    public Task<SiteProbeResultDto> ProbeSftpAsync(string host, int port, CancellationToken cancellationToken = default) =>
        RequestAsync<SiteProbeResultDto>(IpcMessage.Create(SitesMessageTypes.Probe, new SiteProbeRequest { Host = host, Port = port }),
            SitesMessageTypes.ProbeResult, cancellationToken);

    public Task<SiteResultDto> TrustSftpEndpointAsync(string id, string host, int port, string entry, CancellationToken cancellationToken = default) =>
        RequestAsync<SiteResultDto>(IpcMessage.Create(SitesMessageTypes.TrustSftpEndpoint,
            new SiteTrustRequest { Id = id, ExpectedHost = host, ExpectedPort = port, Entries = [entry] }),
            SitesMessageTypes.Result, cancellationToken);

    public Task<SiteTransfersDto> ListTransfersAsync(CancellationToken cancellationToken = default) =>
        RequestAsync<SiteTransfersDto>(IpcMessage.Create(SitesMessageTypes.TransferList, new EmptyPayload()), SitesMessageTypes.TransferListResult, cancellationToken);

    public Task<SiteTransferResultDto> GetTransferAsync(string id, CancellationToken cancellationToken = default) =>
        TransferRequestAsync(SitesMessageTypes.TransferGet, id, cancellationToken);

    public Task<SiteTransferResultDto> PauseTransferAsync(string id, CancellationToken cancellationToken = default) =>
        TransferRequestAsync(SitesMessageTypes.TransferPause, id, cancellationToken);

    public Task<SiteTransferResultDto> ResumeTransferAsync(string id, CancellationToken cancellationToken = default) =>
        TransferRequestAsync(SitesMessageTypes.TransferResume, id, cancellationToken);

    public Task<SiteTransferResultDto> CancelTransferAsync(string id, CancellationToken cancellationToken = default) =>
        TransferRequestAsync(SitesMessageTypes.TransferCancel, id, cancellationToken);

    public Task<SiteTransferResultDto> RetryTransferAsync(string id, CancellationToken cancellationToken = default) =>
        TransferRequestAsync(SitesMessageTypes.TransferRetry, id, cancellationToken);

    /// <summary>Clears only the specified batch; bulk clearing is intentionally not exposed.</summary>
    public Task<SiteTransferResultDto> ClearTransferAsync(string id, CancellationToken cancellationToken = default) =>
        TransferRequestAsync(SitesMessageTypes.TransferClear, id, cancellationToken);

    private Task<SiteTransferResultDto> TransferRequestAsync(string type, string id, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return RequestAsync<SiteTransferResultDto>(IpcMessage.Create(type, new SiteTransferIdRequest { Id = id }), SitesMessageTypes.TransferResult, cancellationToken);
    }

    private async Task<T> RequestAsync<T>(IpcMessage request, string expectedType, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var reply = await _connection.RequestAsync(request, cancellationToken).ConfigureAwait(false);
        if (reply.Type == MessageTypes.HostActionResult)
        {
            var refusal = reply.GetPayload<HostActionResult>();
            throw new InvalidOperationException(refusal.Message ?? "The Host did not accept the Remote Files request.");
        }
        if (reply.Type != expectedType)
            throw new InvalidDataException($"The Host answered with '{reply.Type}' instead of '{expectedType}'.");
        return reply.GetPayload<T>();
    }
}
