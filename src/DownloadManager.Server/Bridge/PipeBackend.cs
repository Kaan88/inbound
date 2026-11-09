using System.IO.Pipes;
using System.Net.WebSockets;
using System.Text.Json;
using DownloadManager.Contracts;
using DownloadManager.Core.Providers;

namespace DownloadManager.Server.Bridge;

/// <summary>
/// The web bridge's backend (D8): each browser WebSocket and each <c>/api/ipc</c> call is one connection to the desktop
/// Host's hello pipe, whose <c>client.hello</c> always asks for the <see cref="ClientRoles.Web"/> role, whatever the
/// browser sent. The Host decides what that role may do; the bridge only carries envelopes, one per WebSocket message
/// and one per pipe frame, in order both ways. The pipe is this Windows user's only, and the client side checks that
/// the Host's end belongs to this user too (<see cref="PipeOwner"/>).
/// </summary>
internal sealed class PipeBackend(string helloPipe, ServerLog log) : IEnvelopeBackend
{
    /// <summary>
    /// At most this many browser calls hold a pipe connection at once. The hello pipe has 64 instances, shared with the
    /// browser extension's native host and the CLI; a page that opens sockets in a loop must not take them all.
    /// </summary>
    internal const int MaxConnections = 32;

    internal static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    private const string HostAway = "The app on this PC isn't answering. Try again in a moment.";

    private readonly SemaphoreSlim _connections = new(MaxConnections, MaxConnections);

    public async Task RunSocketAsync(WebSocket socket, Action? onMessage, bool clientSpeaksFirst, CancellationToken cancellationToken)
    {
        if (!_connections.Wait(0))
        {
            log.Warn($"A WebSocket was refused: {MaxConnections} browser connections are open already.");
            await CloseAsync(socket, WebSocketCloseStatus.EndpointUnavailable, "Too many connections. Close a tab and try again.").ConfigureAwait(false);
            return;
        }

        NamedPipeClientStream? pipe = null;
        try
        {
            var hello = new ClientHelloDto();
            IpcMessage? forwardFirst = null;
            if (clientSpeaksFirst)
            {
                IpcMessage? first;
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    deadline.CancelAfter(WebSocketTransport.ClientHelloTimeout);
                    try
                    {
                        first = await WebSocketTransport.ReadAsync(socket, deadline.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        log.Warn($"A WebSocket client sent no {MessageTypes.ClientHello} within {WebSocketTransport.ClientHelloTimeout.TotalSeconds:0} s; closed.");
                        return;
                    }
                }

                if (first is null) return;
                onMessage?.Invoke();
                if (first.V != Protocol.Version)
                {
                    // The Host answers it after its hello, with server.incompatible, and then closes, as the server does.
                    hello = new ClientHelloDto { Snapshot = false, Events = false };
                    forwardFirst = first;
                }
                else if (first.Type != MessageTypes.ClientHello)
                {
                    await CloseAsync(socket, WebSocketCloseStatus.PolicyViolation, $"{MessageTypes.ClientHello} expected first.").ConfigureAwait(false);
                    return;
                }
                else
                {
                    try
                    {
                        hello = first.Payload is null ? new ClientHelloDto() : first.GetPayload<ClientHelloDto>();
                    }
                    catch (Exception ex) when (ex is JsonException or InvalidDataException)
                    {
                        await CloseAsync(socket, WebSocketCloseStatus.InvalidPayloadData, "Not an IPC envelope.").ConfigureAwait(false);
                        return;
                    }
                }
            }

            pipe = await OpenAsync(new ClientHelloDto { Snapshot = hello.Snapshot, Events = hello.Events }, cancellationToken).ConfigureAwait(false);
            if (pipe is null)
            {
                await CloseAsync(socket, WebSocketCloseStatus.InternalServerError, HostAway).ConfigureAwait(false);
                return;
            }

            if (forwardFirst is not null) await IpcFraming.WriteAsync(pipe, forwardFirst, cancellationToken).ConfigureAwait(false);
            // The pumps get a token of their own: a WebSocket whose receive is cancelled is aborted, without a close frame.
            using var pumps = new CancellationTokenSource();
            var toBrowser = PipeToSocketAsync(pipe, socket, pumps.Token);
            var toHost = SocketToPipeAsync(socket, pipe, onMessage, pumps.Token);
            var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task ended;
            await using (cancellationToken.Register(() => stopped.TrySetResult()).ConfigureAwait(false))
                ended = await Task.WhenAny(toBrowser, toHost, stopped.Task).ConfigureAwait(false);

            // Either side ending ends the other: a closed tab closes the Host's session; a Host that closed it (the web UI
            // turned off, an exit), a sign-out or the bridge stopping closes the tab's socket. Then the close is sent first,
            // and the browser's answer ends the receive.
            if (ended != toHost)
            {
                await CloseAsync(socket, WebSocketCloseStatus.EndpointUnavailable, ended == toBrowser ? "The app closed the connection." : null)
                    .ConfigureAwait(false);
                await Task.WhenAny(toHost, Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None)).ConfigureAwait(false);
            }

            await pumps.CancelAsync().ConfigureAwait(false);
            pipe.Dispose();
            try
            {
                await Task.WhenAll(toBrowser, toHost).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or WebSocketException or ObjectDisposedException or InvalidDataException)
            {
                // The side that didn't end first was stopped; a browser's bad envelope is answered below.
            }

            if (toHost.Exception?.InnerException is InvalidDataException bad)
            {
                log.Warn($"A WebSocket client sent something that is not an envelope: {bad.Message}");
                await CloseAsync(socket, WebSocketCloseStatus.InvalidPayloadData, "Not an IPC envelope.").ConfigureAwait(false);
                return;
            }

            await CloseAsync(socket, WebSocketCloseStatus.NormalClosure, null).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Signed out, the token removed, the bridge stopping, or the browser went away.
        }
        catch (Exception ex) when (ex is WebSocketException or IOException)
        {
            log.Info($"WebSocket client disconnected: {ex.GetType().Name}");
        }
        catch (InvalidDataException ex)
        {
            log.Warn($"A WebSocket client sent something that is not an envelope: {ex.Message}");
            await CloseAsync(socket, WebSocketCloseStatus.InvalidPayloadData, "Not an IPC envelope.").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.Error($"WebSocket client failed: {UrlRedactor.RedactText(ex.ToString())}");
        }
        finally
        {
            pipe?.Dispose();
            _connections.Release();
            await CloseAsync(socket, WebSocketCloseStatus.NormalClosure, null).ConfigureAwait(false);
        }
    }

    public async Task<IpcMessage?> SendOneAsync(IpcMessage request, CancellationToken cancellationToken)
    {
        if (!_connections.Wait(0)) throw new BackendUnavailableException("Too many requests at once. Try again in a moment.");
        try
        {
            await using var pipe = await OpenAsync(new ClientHelloDto { Snapshot = false, Events = false }, cancellationToken).ConfigureAwait(false)
                                   ?? throw new BackendUnavailableException(HostAway);
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            wait.CancelAfter(TimeSpan.FromMinutes(5));
            try
            {
                var hello = await IpcFraming.ReadAsync(pipe, wait.Token).ConfigureAwait(false);
                if (hello?.Type != MessageTypes.ServerHello) throw new BackendUnavailableException(HostAway);
                await IpcFraming.WriteAsync(pipe, request, wait.Token).ConfigureAwait(false);
                while (await IpcFraming.ReadAsync(pipe, wait.Token).ConfigureAwait(false) is { } frame)
                {
                    if (frame.ReplyTo == request.Id || frame.Type == MessageTypes.ServerIncompatible) return frame;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("The app on this PC did not answer within 5 minutes.");
            }
            catch (IOException)
            {
                // Closed under the request: the Host stopped, or it closed the web sessions.
            }
            catch (InvalidDataException ex)
            {
                // A frame the bridge can't read: not this app's Host, or a broken connection.
                log.Warn($"The app's pipe sent something that isn't a frame: {ex.Message}");
            }

            throw new BackendUnavailableException(HostAway);
        }
        finally
        {
            _connections.Release();
        }
    }

    /// <summary>The hello pipe with a web <c>client.hello</c> sent; null when the Host isn't there.</summary>
    private async Task<NamedPipeClientStream?> OpenAsync(ClientHelloDto wanted, CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeClientStream(".", helloPipe, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough);
        try
        {
            await pipe.ConnectAsync(ConnectTimeout, cancellationToken).ConfigureAwait(false);
            // Not PipeOptions.CurrentUserOnly: that compares the owner with the token's default owner, which in an
            // elevated bridge is the Administrators group, while the Host's pipes are always owned by the user itself.
            if (OperatingSystem.IsWindows()) PipeOwner.Verify(pipe);
            var hello = new ClientHelloDto { Snapshot = wanted.Snapshot, Events = wanted.Events, Role = ClientRoles.Web };
            await IpcFraming.WriteAsync(pipe, IpcMessage.Create(MessageTypes.ClientHello, hello), cancellationToken).ConfigureAwait(false);
            return pipe;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            log.Warn($"The app's pipe could not be opened: {ex.GetType().Name}");
            await pipe.DisposeAsync().ConfigureAwait(false);
            return null;
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>The Host's frames to the browser, until the Host closes the connection.</summary>
    private static async Task PipeToSocketAsync(Stream pipe, WebSocket socket, CancellationToken cancellationToken)
    {
        while (await IpcFraming.ReadAsync(pipe, cancellationToken).ConfigureAwait(false) is { } frame)
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(frame, IpcJson.Options);
            await socket.SendAsync(json, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The browser's envelopes to the Host, until the browser closes the socket.</summary>
    private static async Task SocketToPipeAsync(WebSocket socket, Stream pipe, Action? onMessage, CancellationToken cancellationToken)
    {
        while (await WebSocketTransport.ReadAsync(socket, cancellationToken).ConfigureAwait(false) is { } message)
        {
            onMessage?.Invoke();
            await IpcFraming.WriteAsync(pipe, message, cancellationToken).ConfigureAwait(false);
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
}
