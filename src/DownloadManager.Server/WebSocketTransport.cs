using System.Buffers;
using System.Net.WebSockets;
using System.Text.Json;
using DownloadManager.Contracts;
using DownloadManager.Core.Providers;
using DownloadManager.Runtime.Ipc;

namespace DownloadManager.Server;

/// <summary>
/// The server's WebSocket transport for <c>/api/ws</c>: the same JSON envelopes as the desktop pipe, one envelope per
/// text message instead of a length prefix, with the same <see cref="Protocol.MaxFrameBytes"/> limit. A client gets the
/// hello and the snapshot first, then events, and its commands are answered in order, exactly as on the pipe.
/// </summary>
internal static class WebSocketTransport
{
    public static async Task RunAsync(WebSocket socket, HostCommandDispatcher dispatcher, IHostLog log, CancellationToken cancellationToken)
    {
        await using var client = new WebSocketClientSession(socket, log);
        Task? writer = null;
        try
        {
            await dispatcher.OpenSessionAsync(client, cancellationToken).ConfigureAwait(false);
            writer = client.RunWriterAsync(cancellationToken);

            Task<IpcMessage?>? pendingRead = null;
            while (!cancellationToken.IsCancellationRequested)
            {
                var message = await (pendingRead ?? ReadAsync(socket, cancellationToken)).ConfigureAwait(false);
                pendingRead = null;
                if (message is null) break;
                if (dispatcher.RejectIfIncompatible(client, message)) break;

                if (HostCommandDispatcher.NeedsLifetime(message.Type))
                {
                    // As on the pipe: the next message is read meanwhile, and a close there ends the operation.
                    pendingRead = ReadAsync(socket, cancellationToken);
                    await IpcOperationLifetime.RunAsync(token => dispatcher.DispatchAsync(client, message, token), pendingRead, cancellationToken).ConfigureAwait(false);
                }
                else await dispatcher.DispatchAsync(client, message, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down, or the client went away
        }
        catch (Exception ex) when (ex is WebSocketException or IOException)
        {
            log.Info($"WebSocket client disconnected: {ex.GetType().Name}");
        }
        catch (InvalidDataException ex)
        {
            log.Warn($"WebSocket client sent something that is not an envelope: {ex.Message}");
            await CloseAsync(socket, WebSocketCloseStatus.InvalidPayloadData, "Not an IPC envelope.").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.Error($"WebSocket client failed: {UrlRedactor.RedactText(ex.ToString())}");
        }
        finally
        {
            dispatcher.CloseSession(client);
            client.Complete();
            try
            {
                if (writer is not null) await writer.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // writer termination is best effort
            }

            await CloseAsync(socket, WebSocketCloseStatus.NormalClosure, null).ConfigureAwait(false);
        }
    }

    /// <summary>One whole text message as an envelope; null when the client closes. Larger than a frame is refused.</summary>
    internal static async Task<IpcMessage?> ReadAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new ArrayBufferWriter<byte>(16 * 1024);
        while (true)
        {
            var memory = buffer.GetMemory(16 * 1024);
            var result = await socket.ReceiveAsync(memory, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text) throw new InvalidDataException("Binary messages are not part of the protocol.");
            buffer.Advance(result.Count);
            if (buffer.WrittenCount > Protocol.MaxFrameBytes) throw new InvalidDataException($"Message over {Protocol.MaxFrameBytes} bytes.");
            if (!result.EndOfMessage) continue;

            try
            {
                return JsonSerializer.Deserialize<IpcMessage>(buffer.WrittenSpan, IpcJson.Options)
                       ?? throw new InvalidDataException("The message was empty.");
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"The message is not JSON ({ex.GetType().Name}).");
            }
        }
    }

    private static async Task CloseAsync(WebSocket socket, WebSocketCloseStatus status, string? reason)
    {
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await socket.CloseOutputAsync(status, reason, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException)
        {
            // The client is gone already.
        }
    }

    private sealed class WebSocketClientSession(WebSocket socket, IHostLog log) : QueuedClientSession(log)
    {
        protected override async Task WriteFrameAsync(IpcMessage message, CancellationToken cancellationToken)
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(message, IpcJson.Options);
            if (json.Length > Protocol.MaxFrameBytes) throw new InvalidDataException($"IPC frame is too large ({json.Length} bytes).");
            try
            {
                await socket.SendAsync(json, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
            }
            catch (WebSocketException ex)
            {
                // The writer loop and the dispatcher read a broken client as IOException, as on the pipe.
                throw new IOException("The WebSocket client went away.", ex);
            }
        }
    }
}

/// <summary>
/// <c>POST /api/ipc</c>: one request envelope, one reply. No hello, no snapshot and no events, so a short-lived client
/// (the browser extension, later) needn't hold a connection. The session collects the reply the dispatcher queues.
/// </summary>
internal sealed class OneShotSession(string requestId) : IClientSession
{
    private readonly TaskCompletionSource<IpcMessage> _reply = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Guid Id { get; } = Guid.NewGuid();

    public Task<IpcMessage> Reply => _reply.Task;

    public bool Enqueue(IpcMessage message)
    {
        // Only the answer to this request: a session that isn't opened gets no broadcasts, but be sure.
        if (message.ReplyTo == requestId || message.Type == MessageTypes.ServerIncompatible) _reply.TrySetResult(message);
        return true;
    }

    public Task SendDirectAsync(IpcMessage message, CancellationToken cancellationToken)
    {
        Enqueue(message);
        return Task.CompletedTask;
    }
}
