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
