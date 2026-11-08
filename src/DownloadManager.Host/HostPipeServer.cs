using System.Collections.Concurrent;
using System.IO.Pipes;
using DownloadManager.Contracts;
using DownloadManager.Core.Providers;
using DownloadManager.Runtime.Ipc;
using DownloadManager.Tray;

namespace DownloadManager.Host;

/// <summary>
/// The desktop Host's transport: a per-user named pipe with a current-user-only ACL, one session per connection.
/// What a frame means and what is broadcast belongs to <see cref="HostCommandDispatcher"/>; this only accepts,
/// reads frames in order and writes the session's queue.
/// </summary>
public sealed class HostPipeServer
{
    private readonly HostCommandDispatcher _dispatcher;
    private readonly FileLogger _log;
    private readonly ConcurrentDictionary<Guid, PipeClientSession> _clients = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;
    private Task? _helloAcceptLoop;

    public HostPipeServer(HostCommandDispatcher dispatcher, FileLogger log)
    {
        _dispatcher = dispatcher;
        _log = log;
    }

    /// <summary>
    /// Starts accepting on both pipes. The dispatcher is started first, so the first client misses no broadcast. The
    /// hello pipe's first instance is created first, so wherever the main pipe is there the hello pipe is too, and a
    /// client that finds only the main pipe knows the Host is older (<see cref="IpcClient.ConnectAsync(ClientHelloDto, TimeSpan?, CancellationToken)"/>).
    /// </summary>
    public void Start()
    {
        _helloAcceptLoop = AcceptLoopAsync(PipeNames.HostHelloPipe(), clientSpeaksFirst: true, _cts.Token);
        _acceptLoop = AcceptLoopAsync(PipeNames.HostPipe(), clientSpeaksFirst: false, _cts.Token);
        _log.Info($"IPC server listening on pipe '{PipeNames.HostPipe()}'.");
        _log.Info($"IPC server listening for client hellos on '{PipeNames.HostHelloPipe()}'.");
    }

    public async Task StopAsync()
    {
        _cts.Cancel();
        foreach (var client in _clients.Values)
        {
            client.Complete();
        }

        foreach (var loop in new[] { _acceptLoop, _helloAcceptLoop })
        {
            if (loop is null) continue;
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // expected
            }
        }
    }

    private async Task AcceptLoopAsync(string pipeName, bool clientSpeaksFirst, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = CreatePipe(pipeName);
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                var client = new PipeClientSession(pipe, _log);
                _ = HandleClientAsync(client, clientSpeaksFirst, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                pipe?.Dispose();
                break;
            }
            catch (Exception ex)
            {
                pipe?.Dispose();
                _log.Error($"Named-pipe accept failed: {ex.Message}");
                try
                {
                    await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    /// <summary>
    /// How many clients can be connected at once. Every progress or completion window is a window process with
    /// a connection of its own, besides the main window, the browser bridge and the CLI. At 8, the main window
    /// and seven open progress windows left the browser bridge and the CLI unable to connect at all (PERF.2
    /// soak). The pipe is the current user's only, so this guards against a runaway, not another user. Each of the
    /// two pipes has its own limit.
    /// </summary>
    internal const int MaxClients = 64;

    /// <summary>How long the hello pipe waits for a client's <c>client.hello</c> before closing the connection.</summary>
    internal static readonly TimeSpan ClientHelloTimeout = TimeSpan.FromSeconds(5);

    private static NamedPipeServerStream CreatePipe(string pipeName)
    {
        var security = PipeSecurityFactory.CreateForCurrentUser();

        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            MaxClients,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            64 * 1024,
            64 * 1024,
            security);
    }

    /// <param name="clientSpeaksFirst">
    /// The hello pipe (PERF.7): the client's first frame is <c>client.hello</c>, read before the Host sends anything, and
    /// says whether this connection gets the snapshot and the events. On the main pipe everyone gets both, as always.
    /// </param>
    private async Task HandleClientAsync(PipeClientSession client, bool clientSpeaksFirst, CancellationToken serverToken)
    {
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
                    // A client that connects and never says hello would hold a pipe instance until it went away.
                    deadline.CancelAfter(ClientHelloTimeout);
                    try
                    {
                        first = await IpcFraming.ReadAsync(client.Stream, deadline.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!serverToken.IsCancellationRequested)
                    {
                        _log.Warn($"A client on the hello pipe sent no {MessageTypes.ClientHello} within {ClientHelloTimeout.TotalSeconds:0} s; closed.");
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
                    _log.Warn($"A client on the hello pipe sent '{(type.Length > 64 ? type[..64] : type)}' first instead of {MessageTypes.ClientHello}; closed.");
                    return;
                }
                else
                {
                    options = first.Payload is null ? new ClientHelloDto() : first.GetPayload<ClientHelloDto>();
                }
            }

            await _dispatcher.OpenSessionAsync(client, options, serverToken).ConfigureAwait(false);
            writerTask = client.RunWriterAsync(serverToken);
            if (incompatible is not null && _dispatcher.RejectIfIncompatible(client, incompatible))
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

                if (_dispatcher.RejectIfIncompatible(client, message))
                {
                    break;
                }

                if (HostCommandDispatcher.NeedsLifetime(message.Type))
                {
                    // The next frame is read meanwhile: an EOF there means the client gone, which ends the operation.
                    pendingRead = IpcFraming.ReadAsync(client.Stream, serverToken);
                    await IpcOperationLifetime.RunAsync(token => _dispatcher.DispatchAsync(client, message, token), pendingRead, serverToken).ConfigureAwait(false);
                }
                else await _dispatcher.DispatchAsync(client, message, serverToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (IOException ex)
        {
            _log.Info($"IPC client disconnected: {ex.Message}");
        }
        catch (Exception ex)
        {
            _log.Error($"IPC client failed: {UrlRedactor.RedactText(ex.ToString())}");
        }
        finally
        {
            _dispatcher.CloseSession(client);
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

    private sealed class PipeClientSession(NamedPipeServerStream pipe, IHostLog log) : QueuedClientSession(log)
    {
        public Stream Stream => pipe;

        protected override Task WriteFrameAsync(IpcMessage message, CancellationToken cancellationToken)
            => IpcFraming.WriteAsync(pipe, message, cancellationToken);

        public override async ValueTask DisposeAsync()
        {
            try
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
            }
            catch (IOException)
            {
                // best effort
            }

            await base.DisposeAsync().ConfigureAwait(false);
        }
    }
}
