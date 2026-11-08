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

    public HostPipeServer(HostCommandDispatcher dispatcher, FileLogger log)
    {
        _dispatcher = dispatcher;
        _log = log;
    }

    /// <summary>Starts accepting. The dispatcher is started first, so the first client misses no broadcast.</summary>
    public void Start()
    {
        _acceptLoop = AcceptLoopAsync(_cts.Token);
        _log.Info($"IPC server listening on pipe '{PipeNames.HostPipe()}'.");
    }

    public async Task StopAsync()
    {
        _cts.Cancel();
        foreach (var client in _clients.Values)
        {
            client.Complete();
        }

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // expected
            }
        }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = CreatePipe();
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                var client = new PipeClientSession(pipe, _log);
                _ = HandleClientAsync(client, cancellationToken);
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
    /// soak). The pipe is the current user's only, so this guards against a runaway, not another user.
    /// </summary>
    internal const int MaxClients = 64;

    private static NamedPipeServerStream CreatePipe()
    {
        var security = PipeSecurityFactory.CreateForCurrentUser();

        return NamedPipeServerStreamAcl.Create(
            PipeNames.HostPipe(),
            PipeDirection.InOut,
            MaxClients,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            64 * 1024,
            64 * 1024,
            security);
    }

    private async Task HandleClientAsync(PipeClientSession client, CancellationToken serverToken)
    {
        _clients[client.Id] = client;
        Task? writerTask = null;
        try
        {
            await _dispatcher.OpenSessionAsync(client, serverToken).ConfigureAwait(false);
            writerTask = client.RunWriterAsync(serverToken);

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
