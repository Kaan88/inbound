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
/// <para>
/// A client that asked for it (<c>/api/ws?hello=1</c>) speaks first, as on the desktop's hello pipe (PERF.7): its
/// first message is <c>client.hello</c> with <see cref="ClientHelloDto"/> (snapshot, events), within
/// <see cref="ClientHelloTimeout"/>. A first message on another protocol version gets the hello and
/// <c>server.incompatible</c>; anything else closes the socket.
/// </para>
/// </summary>
internal static class WebSocketTransport
{
    /// <summary>How long a client that said it speaks first has for its <c>client.hello</c>, as on the hello pipe.</summary>
    internal static readonly TimeSpan ClientHelloTimeout = TimeSpan.FromSeconds(5);

    /// <param name="onMessage">Called for each message the client sends (the session's activity).</param>
    /// <param name="clientSpeaksFirst">The client sends <c>client.hello</c> before the server says anything.</param>
    public static async Task RunAsync(WebSocket socket, HostCommandDispatcher dispatcher, IHostLog log, CancellationToken cancellationToken,
        Action? onMessage = null, bool clientSpeaksFirst = false)
    {
        await using var client = new WebSocketClientSession(socket, log);
        Task? writer = null;
        try
        {
            ClientHelloDto? options = null;
            IpcMessage? incompatible = null;
            if (clientSpeaksFirst)
            {
                IpcMessage? first;
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    // A client that connects and never says hello would hold a socket open for nothing.
                    deadline.CancelAfter(ClientHelloTimeout);
                    try
                    {
                        first = await ReadAsync(socket, deadline.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        log.Warn($"A WebSocket client sent no {MessageTypes.ClientHello} within {ClientHelloTimeout.TotalSeconds:0} s; closed.");
                        return;
                    }
                }

                if (first is null) return;
                onMessage?.Invoke();
                if (first.V != Protocol.Version)
                {
                    // Answered after the hello, which carries this server's version, and then closed.
                    incompatible = first;
                    options = new ClientHelloDto { Snapshot = false, Events = false };
                }
                else if (first.Type != MessageTypes.ClientHello)
                {
                    var type = first.Type ?? "(no type)";
                    log.Warn($"A WebSocket client sent '{(type.Length > 64 ? type[..64] : type)}' first instead of {MessageTypes.ClientHello}; closed.");
                    await CloseAsync(socket, WebSocketCloseStatus.PolicyViolation, $"{MessageTypes.ClientHello} expected first.").ConfigureAwait(false);
                    return;
                }
                else
                {
                    try
                    {
                        options = first.Payload is null ? new ClientHelloDto() : first.GetPayload<ClientHelloDto>();
                    }
                    catch (JsonException ex)
                    {
                        throw new InvalidDataException($"The {MessageTypes.ClientHello} is not one ({ex.GetType().Name}).");
                    }
                }
            }

            await dispatcher.OpenSessionAsync(client, options, cancellationToken).ConfigureAwait(false);
            writer = client.RunWriterAsync(cancellationToken);
            if (incompatible is not null && dispatcher.RejectIfIncompatible(client, incompatible)) return;

            Task<IpcMessage?>? pendingRead = null;
            while (!cancellationToken.IsCancellationRequested)
            {
                var message = await (pendingRead ?? ReadAsync(socket, cancellationToken)).ConfigureAwait(false);
                pendingRead = null;
                if (message is null) break;
                onMessage?.Invoke();
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
        /// <summary>Ends the connection at once (a full queue, L19): the reader stops and the client connects again for a new snapshot.</summary>
        public override void Close()
        {
            base.Close();
            try { socket.Abort(); }
            catch (ObjectDisposedException) { /* already gone */ }
        }

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
