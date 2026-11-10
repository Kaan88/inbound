using System.Collections.Concurrent;
using DownloadManager.Contracts;
using DownloadManager.Core.Providers;

namespace DownloadManager.Runtime.Ipc;

/// <summary>
/// One local client connection, whatever carries it: the Windows Host's named pipes and the Linux Host's Unix sockets
/// both hand their connected stream here. Reads frames in order, writes the session's queue, and on the hello transport
/// reads the client's <c>client.hello</c> first. What a frame means belongs to <see cref="HostCommandDispatcher"/>.
/// </summary>
public sealed class IpcConnectionHandler(HostCommandDispatcher dispatcher, IHostLog log)
{
    /// <summary>How long the hello transport waits for a client's <c>client.hello</c> before closing the connection.</summary>
    public static readonly TimeSpan ClientHelloTimeout = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<Guid, StreamClientSession> _clients = new();

    /// <summary>Ends every connection's writer; the reads end with the server's token.</summary>
    public void CompleteAll()
    {
        foreach (var client in _clients.Values)
        {
            client.Complete();
        }
    }

    /// <param name="stream">The connected stream. Disposed when the connection ends.</param>
    /// <param name="clientSpeaksFirst">
    /// The hello transport (PERF.7): the client's first frame is <c>client.hello</c>, read before the Host sends anything,
    /// and says whether this connection gets the snapshot and the events. On the main transport everyone gets both.
    /// </param>
    public async Task HandleAsync(Stream stream, bool clientSpeaksFirst, CancellationToken serverToken)
    {
        var client = new StreamClientSession(stream, log);
        _clients[client.Id] = client;
        Task? writerTask = null;
        try
        {
            ClientHelloDto? options = null;
            IpcMessage? incompatible = null;
            if (clientSpeaksFirst)
            {
                IpcMessage? first;
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(serverToken))
                {
                    // A client that connects and never says hello would hold a connection until it went away.
                    deadline.CancelAfter(ClientHelloTimeout);
                    try
                    {
                        first = await IpcFraming.ReadAsync(client.Stream, deadline.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!serverToken.IsCancellationRequested)
                    {
                        log.Warn($"A client on the hello pipe sent no {MessageTypes.ClientHello} within {ClientHelloTimeout.TotalSeconds:0} s; closed.");
                        return;
                    }
                }

                if (first is null)
                {
                    return;
                }

                if (first.V != Protocol.Version)
                {
                    // Answered after the hello, which carries this Host's version, and then closed; nothing else is sent.
                    incompatible = first;
                    options = new ClientHelloDto { Snapshot = false, Events = false };
                }
                else if (first.Type != MessageTypes.ClientHello)
                {
                    var type = first.Type ?? "(no type)";
                    log.Warn($"A client on the hello pipe sent '{(type.Length > 64 ? type[..64] : type)}' first instead of {MessageTypes.ClientHello}; closed.");
                    return;
                }
                else
                {
                    options = first.Payload is null ? new ClientHelloDto() : first.GetPayload<ClientHelloDto>();
                    if (!dispatcher.OffersRole(options.Role))
                    {
                        var role = options.Role ?? string.Empty;
                        log.Warn($"A client on the hello pipe asked for the role '{(role.Length > 32 ? role[..32] : role)}', which this Host doesn't offer; closed.");
                        return;
                    }
                }
            }

            await dispatcher.OpenSessionAsync(client, options, serverToken).ConfigureAwait(false);
            // Close() (HostCommandDispatcher.CloseSessions) ends the reads as well as the writes.
            using var session = CancellationTokenSource.CreateLinkedTokenSource(serverToken, client.Closing);
            serverToken = session.Token;
            writerTask = client.RunWriterAsync(serverToken);
            if (incompatible is not null && dispatcher.RejectIfIncompatible(client, incompatible))
            {
                return;
            }

            Task<IpcMessage?>? pendingRead = null;
            while (!serverToken.IsCancellationRequested)
            {
                var message = await (pendingRead ?? IpcFraming.ReadAsync(client.Stream, serverToken)).ConfigureAwait(false);
                pendingRead = null;
                if (message is null)
                {
                    break;
                }

                if (dispatcher.RejectIfIncompatible(client, message))
                {
                    break;
                }

                if (HostCommandDispatcher.NeedsLifetime(message.Type))
                {
                    // The next frame is read meanwhile: an EOF there means the client gone, which ends the operation.
                    pendingRead = IpcFraming.ReadAsync(client.Stream, serverToken);
                    await IpcOperationLifetime.RunAsync(token => dispatcher.DispatchAsync(client, message, token), pendingRead, serverToken).ConfigureAwait(false);
                }
                else await dispatcher.DispatchAsync(client, message, serverToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (IOException ex)
        {
            log.Info($"IPC client disconnected: {ex.Message}");
        }
        catch (Exception ex)
        {
            log.Error($"IPC client failed: {UrlRedactor.RedactText(ex.ToString())}");
        }
        finally
        {
            dispatcher.CloseSession(client);
            _clients.TryRemove(client.Id, out _);
            client.Complete();
            try
            {
                if (writerTask is not null) await writerTask.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // writer termination is best effort
            }

            await client.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class StreamClientSession(Stream stream, IHostLog log) : QueuedClientSession(log)
    {
        private readonly CancellationTokenSource _closing = new();

        public Stream Stream => stream;

        /// <summary>
        /// Cancelled by <see cref="Close"/>: the Host ends this connection at once, the writer too, so a client that has
        /// stopped reading can't hold it open. What was still queued is not sent.
        /// </summary>
        public CancellationToken Closing => _closing.Token;

        public override void Close()
        {
            base.Close();
            try { _closing.Cancel(); }
            catch (ObjectDisposedException) { /* already gone */ }
        }

        protected override Task WriteFrameAsync(IpcMessage message, CancellationToken cancellationToken)
            => IpcFraming.WriteAsync(stream, message, cancellationToken);

        public override async ValueTask DisposeAsync()
        {
            try
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
            catch (IOException)
            {
                // best effort
            }

            _closing.Dispose();
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }
}
