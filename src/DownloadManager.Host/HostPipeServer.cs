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
    private readonly IpcConnectionHandler _connections;
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;
    private Task? _helloAcceptLoop;

    public HostPipeServer(HostCommandDispatcher dispatcher, FileLogger log)
    {
        _dispatcher = dispatcher;
        _log = log;
        _connections = new IpcConnectionHandler(dispatcher, log);
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
        _connections.CompleteAll();

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
                _ = _connections.HandleAsync(pipe, clientSpeaksFirst, cancellationToken);
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
                    // A pipe taken by another account stays taken until that account lets go; don't fill the log meanwhile.
                    var wait = ex is UnauthorizedAccessException ? TimeSpan.FromSeconds(10) : TimeSpan.FromMilliseconds(500);
                    await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
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
    internal static readonly TimeSpan ClientHelloTimeout = IpcConnectionHandler.ClientHelloTimeout;

    private static NamedPipeServerStream CreatePipe(string pipeName)
    {
        var security = PipeSecurityFactory.CreateForCurrentUser();

        var pipe = NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            MaxClients,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            64 * 1024,
            64 * 1024,
            security);
        try
        {
            // When the pipe already exists, this instance has that pipe's security, not ours: refuse to serve a pipe
            // another account made first, or that account could connect to it.
            PipeOwner.VerifyServer(pipe);
            return pipe;
        }
        catch
        {
            pipe.Dispose();
            throw;
        }
    }
}
