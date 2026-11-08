using DownloadManager.Contracts;
using DownloadManager.Core.Providers;

namespace DownloadManager.Runtime.Ipc;

public sealed partial class HostCommandDispatcher
{
    /// <summary>
    /// These wait on the network, so they run under a lifetime that ends when their client goes
    /// away. Commands run one at a time per client: a frontend sends them on their own connection.
    /// </summary>
    private static bool WaitsOnProvider(string type) => type is
        MessageTypes.RemoteAccountGet or MessageTypes.RemoteAccountValidate or MessageTypes.RemoteClassify or MessageTypes.RemoteHosters or
        MessageTypes.RemoteAdd or MessageTypes.RemoteItemGet or MessageTypes.RemoteFiles or MessageTypes.RemoteSelect or MessageTypes.RemoteDeleteRemote or MessageTypes.RemoteStartQueued or
        MessageTypes.RemoteContainerPreview or MessageTypes.RemoteContainerAdd or MessageTypes.RemoteContainerSendToJd or
        // Classifies a changed address before it applies it. A client that gave up has said it wasn't changed.
        MessageTypes.JobRefreshUrl;

    private async Task<bool> TryDispatchRemoteAsync(IClientSession client, SessionRights rights, IpcMessage message, CancellationToken cancellationToken)
    {
        if (rights.Remote is not { } remote || !message.Type.StartsWith("remote.", StringComparison.Ordinal))
        {
            return false;
        }

        // The provider's key is this PC's user's own: a web session can use the account, never set or remove its key.
        if (rights.Scope is not null && message.Type is MessageTypes.RemoteAccountSetKey or MessageTypes.RemoteAccountClearKey)
        {
            await Reply(client, message, MessageTypes.RemoteAccountResult,
                new RemoteAccountResult { Error = ProviderKeyUnavailable }).ConfigureAwait(false);
            return true;
        }

        var replyType = message.Type switch
        {
            MessageTypes.RemoteAccountGet or MessageTypes.RemoteAccountSetKey or
            MessageTypes.RemoteAccountClearKey or MessageTypes.RemoteAccountValidate => MessageTypes.RemoteAccountResult,
            MessageTypes.RemoteClassify => MessageTypes.RemoteClassifyResult,
            MessageTypes.RemoteHosters => MessageTypes.RemoteHostersResult,
            MessageTypes.RemoteAdd => MessageTypes.RemoteAddResult,
            MessageTypes.RemoteList => MessageTypes.RemoteListResult,
            MessageTypes.RemoteItemGet => MessageTypes.RemoteItemResult,
            MessageTypes.RemoteFiles => MessageTypes.RemoteFilesResult,
            MessageTypes.RemoteSelect or MessageTypes.RemoteRetry or MessageTypes.RemoteCheckNow or
            MessageTypes.RemoteRemove or MessageTypes.RemoteDeleteRemote or MessageTypes.RemoteArchivePassword or
            MessageTypes.RemoteStartQueued => MessageTypes.RemoteActionResult,
            MessageTypes.RemoteContainerPreview => MessageTypes.RemoteContainerPreviewResult,
            MessageTypes.RemoteContainerAdd => MessageTypes.RemoteContainerAddResult,
            MessageTypes.RemoteContainerSendToJd => MessageTypes.RemoteContainerSendResult,
            _ => null,
        };
        if (replyType is null)
        {
            return false;
        }

        try
        {
            object reply = message.Type switch
            {
                MessageTypes.RemoteAccountGet => await remote.GetAccountAsync(Payload<RemoteAccountRequest>(message), cancellationToken).ConfigureAwait(false),
                MessageTypes.RemoteAccountSetKey => await remote.SetKeyAsync(message.GetPayload<RemoteSetKeyRequest>(), cancellationToken).ConfigureAwait(false),
                MessageTypes.RemoteAccountClearKey => await remote.ClearKeyAsync(Payload<RemoteAccountRequest>(message), cancellationToken).ConfigureAwait(false),
                MessageTypes.RemoteAccountValidate => await remote.ValidateAsync(Payload<RemoteAccountRequest>(message), cancellationToken).ConfigureAwait(false),
                MessageTypes.RemoteClassify => await remote.ClassifyAsync(message.GetPayload<RemoteClassifyRequest>(), cancellationToken).ConfigureAwait(false),
                MessageTypes.RemoteHosters => await remote.GetHostersAsync(cancellationToken).ConfigureAwait(false),
                MessageTypes.RemoteAdd => await remote.AddAsync(message.GetPayload<RemoteAddRequestDto>(), cancellationToken).ConfigureAwait(false),
                MessageTypes.RemoteList => remote.List(),
                MessageTypes.RemoteItemGet => await remote.GetItemsAsync(message.GetPayload<RemoteItemRequest>(), cancellationToken).ConfigureAwait(false),
                MessageTypes.RemoteFiles => await remote.GetFilesAsync(message.GetPayload<RemoteJobIdRequest>(), cancellationToken).ConfigureAwait(false),
                MessageTypes.RemoteSelect => await remote.SelectAsync(message.GetPayload<RemoteSelectRequest>(), cancellationToken).ConfigureAwait(false),
                MessageTypes.RemoteRetry => await remote.RetryAsync(message.GetPayload<RemoteRetryRequest>(), cancellationToken).ConfigureAwait(false),
                MessageTypes.RemoteCheckNow => await remote.CheckNowAsync(Payload<RemoteCheckNowRequest>(message), cancellationToken).ConfigureAwait(false),
                MessageTypes.RemoteRemove => await remote.RemoveAsync(message.GetPayload<RemoteJobIdRequest>(), cancellationToken).ConfigureAwait(false),
                MessageTypes.RemoteStartQueued => await remote.StartQueuedAsync(message.GetPayload<RemoteJobIdRequest>(), cancellationToken).ConfigureAwait(false),
                MessageTypes.RemoteArchivePassword => await remote.SetArchivePasswordAsync(message.GetPayload<RemoteArchivePasswordRequest>(), cancellationToken).ConfigureAwait(false),
                MessageTypes.RemoteContainerPreview => await remote.ContainerPreviewAsync(message.GetPayload<RemoteContainerPreviewRequest>(), cancellationToken).ConfigureAwait(false),
                MessageTypes.RemoteContainerAdd => await remote.ContainerAddAsync(message.GetPayload<RemoteContainerAddRequest>(), cancellationToken).ConfigureAwait(false),
                MessageTypes.RemoteContainerSendToJd => await remote.ContainerSendToJdAsync(message.GetPayload<RemoteContainerSendRequest>(), cancellationToken).ConfigureAwait(false),
                _ => await remote.DeleteRemoteAsync(message.GetPayload<RemoteDeleteRequest>(), cancellationToken).ConfigureAwait(false),
            };

            await ReplyWithinFrameAsync(client, message, replyType, reply).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The client went away or the Host is stopping. Nobody is waiting for an answer.
        }
        catch (Exception ex)
        {
            // Never the exception's words: its text could quote what the request carried.
            _log.Error($"IPC command '{message.Type}' failed: {ex.GetType().Name}.");
            await Reply(client, message, replyType, new { Ok = false, Error = $"That could not be done ({ex.GetType().Name}). See the log for when it happened." }).ConfigureAwait(false);
        }

        return true;
    }

    private static T Payload<T>(IpcMessage message) where T : new()
        => message.Payload is null ? new T() : message.GetPayload<T>();

    /// <summary>A frame over the limit would end the connection, and a very large item shouldn't do that to a frontend.</summary>
    private async Task ReplyWithinFrameAsync(IClientSession client, IpcMessage request, string type, object reply)
    {
        var message = IpcMessage.Create(type, reply, request.Id);
        if (message.Payload is { } payload && payload.GetRawText().Length >= Protocol.MaxFrameBytes - 1024)
        {
            _log.Warn($"IPC reply '{type}' was too large to send.");
            await Reply(client, request, type, new { Ok = false, Error = "This answer is too large to send. For a list, ask for a smaller page." }).ConfigureAwait(false);
            return;
        }

        client.Enqueue(message);
    }

    private void OnRemoteChanged(object? sender, RemoteChangedDto change) => BroadcastRemoteChanged(change);

    public void BroadcastRemoteChanged(RemoteChangedDto change)
        => Broadcast(IpcMessage.Create(MessageTypes.RemoteChanged, change));
}
