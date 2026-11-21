using DownloadManager.Contracts;

namespace DownloadManager.Mobile.TorBox;

/// <summary>
/// TorBox requests on the caller's selected, already-open Host connection (the phone's own Host or a paired one). The Host does the
/// provider calls; this only sends the <c>remote.*</c> messages and reads the replies. The API key goes out in <see cref="SetKeyAsync"/>
/// and is never kept, logged or read back: a reply only says whether a key is saved.
/// </summary>
public sealed class TorBoxClient
{
    public const string ProviderId = "torbox";

    /// <summary>What the hello lists when the Host answers <c>remote.*</c>.</summary>
    public const string Capability = "remote";

    private readonly IHostConnection _connection;

    public TorBoxClient(IHostConnection connection) =>
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    public static bool Offered(HostHelloDto? hello) => hello?.Capabilities.Contains(Capability) == true;

    public Task<RemoteAccountResult> AccountAsync(bool refresh = false, CancellationToken cancellationToken = default) =>
        RequestAsync<RemoteAccountResult>(MessageTypes.RemoteAccountGet, new RemoteAccountRequest { Provider = ProviderId, Refresh = refresh },
            MessageTypes.RemoteAccountResult, cancellationToken);

    public Task<RemoteAccountResult> SetKeyAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return RequestAsync<RemoteAccountResult>(MessageTypes.RemoteAccountSetKey, new RemoteSetKeyRequest { Provider = ProviderId, Key = key.Trim() },
            MessageTypes.RemoteAccountResult, cancellationToken);
    }

    public Task<RemoteAccountResult> ClearKeyAsync(CancellationToken cancellationToken = default) =>
        RequestAsync<RemoteAccountResult>(MessageTypes.RemoteAccountClearKey, new RemoteAccountRequest { Provider = ProviderId },
            MessageTypes.RemoteAccountResult, cancellationToken);

    /// <summary>A magnet is checked against TorBox's cache and, when it is cached, comes back with its files.</summary>
    public Task<RemoteClassifyResult> ClassifyAsync(string magnet, CancellationToken cancellationToken = default) =>
        RequestAsync<RemoteClassifyResult>(MessageTypes.RemoteClassify, new RemoteClassifyRequest { Text = magnet, WithFiles = true },
            MessageTypes.RemoteClassifyResult, cancellationToken);

    public Task<RemoteAddResultDto> AddAsync(RemoteAddRequestDto request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RequestAsync<RemoteAddResultDto>(MessageTypes.RemoteAdd, request, MessageTypes.RemoteAddResult, cancellationToken);
    }

    public Task<RemoteListResult> ListAsync(CancellationToken cancellationToken = default) =>
        RequestAsync<RemoteListResult>(MessageTypes.RemoteList, new EmptyPayload(), MessageTypes.RemoteListResult, cancellationToken);

    /// <summary>The account's items of one kind (torrents, webdl) with their sizes and file counts, which a job does not carry.</summary>
    public Task<RemoteItemResult> ItemsAsync(string kind, CancellationToken cancellationToken = default) =>
        RequestAsync<RemoteItemResult>(MessageTypes.RemoteItemGet,
            new RemoteItemRequest { Provider = ProviderId, Kind = kind, Limit = 250 }, MessageTypes.RemoteItemResult, cancellationToken);

    public Task<RemoteFilesResultDto> FilesAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        RequestAsync<RemoteFilesResultDto>(MessageTypes.RemoteFiles, new RemoteJobIdRequest { JobId = jobId },
            MessageTypes.RemoteFilesResult, cancellationToken);

    /// <summary>Chooses the files of a job that is waiting for them; the Host then adds them to its downloads.</summary>
    public Task<RemoteActionResultDto> SelectAsync(Guid jobId, IReadOnlyList<string> fileIds, bool all, CancellationToken cancellationToken = default) =>
        RequestAsync<RemoteActionResultDto>(MessageTypes.RemoteSelect,
            new RemoteSelectRequest { JobId = jobId, All = all, FileIds = all ? null : fileIds.ToList() },
            MessageTypes.RemoteActionResult, cancellationToken);

    /// <summary>Takes the job off the list. The item on TorBox and any downloads stay.</summary>
    public Task<RemoteActionResultDto> RemoveAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        RequestAsync<RemoteActionResultDto>(MessageTypes.RemoteRemove, new RemoteJobIdRequest { JobId = jobId },
            MessageTypes.RemoteActionResult, cancellationToken);

    /// <summary>Deletes the item from TorBox for good. Ask the person first.</summary>
    public Task<RemoteActionResultDto> DeleteFromTorBoxAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        RequestAsync<RemoteActionResultDto>(MessageTypes.RemoteDeleteRemote, new RemoteDeleteRequest { JobId = jobId, Confirmed = true },
            MessageTypes.RemoteActionResult, cancellationToken);

    public Task<RemoteActionResultDto> CheckNowAsync(CancellationToken cancellationToken = default) =>
        RequestAsync<RemoteActionResultDto>(MessageTypes.RemoteCheckNow, new RemoteCheckNowRequest { Provider = ProviderId },
            MessageTypes.RemoteActionResult, cancellationToken);

    private async Task<T> RequestAsync<T>(string type, object payload, string expectedType, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var reply = await _connection.RequestAsync(Create(type, payload), cancellationToken).ConfigureAwait(false);
        if (reply.Type == MessageTypes.HostActionResult)
            throw new InvalidOperationException(reply.GetPayload<HostActionResult>().Message ?? TorBoxText.HostRefused);
        if (reply.Type != expectedType)
            throw new InvalidDataException($"The Host answered with '{reply.Type}' instead of '{expectedType}'.");
        return reply.GetPayload<T>();
    }

    // IpcMessage.Create is generic over source-generated types: one branch per payload keeps it free of reflection.
    private static IpcMessage Create(string type, object payload) => payload switch
    {
        RemoteAccountRequest p => IpcMessage.Create(type, p),
        RemoteSetKeyRequest p => IpcMessage.Create(type, p),
        RemoteClassifyRequest p => IpcMessage.Create(type, p),
        RemoteAddRequestDto p => IpcMessage.Create(type, p),
        EmptyPayload p => IpcMessage.Create(type, p),
        RemoteItemRequest p => IpcMessage.Create(type, p),
        RemoteJobIdRequest p => IpcMessage.Create(type, p),
        RemoteSelectRequest p => IpcMessage.Create(type, p),
        RemoteDeleteRequest p => IpcMessage.Create(type, p),
        RemoteCheckNowRequest p => IpcMessage.Create(type, p),
        _ => throw new ArgumentException("Not a TorBox request.", nameof(payload)),
    };
}
