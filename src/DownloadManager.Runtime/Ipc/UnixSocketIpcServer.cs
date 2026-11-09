using System.Net.Sockets;
using System.Runtime.Versioning;
using DownloadManager.Contracts;

namespace DownloadManager.Runtime.Ipc;

/// <summary>
/// The Linux Host's transport: the Windows Host's two pipes (<see cref="PipeNames.HostPipe"/> and the hello pipe) as Unix
/// sockets in this user's own folder (<see cref="UnixSockets"/>). Only a process of this user is served; the folder is
/// closed to others and each connection's peer user ID is checked as well.
/// Start it only while holding the single-instance lock: it replaces socket files a Host that has gone left behind.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class UnixSocketIpcServer : IAsyncDisposable
{
    /// <summary>How many clients can be connected at once on each socket; as on the Windows pipes, a guard against a runaway.</summary>
    public const int MaxClients = 64;

    private readonly IpcConnectionHandler _connections;
    private readonly IHostLog _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<(Socket Listener, string Path, Task Loop)> _listeners = [];

    public UnixSocketIpcServer(HostCommandDispatcher dispatcher, IHostLog log)
    {
        _log = log;
        _connections = new IpcConnectionHandler(dispatcher, log);
    }

    /// <summary>The hello socket first, so wherever the main socket is the hello socket is too (as on Windows).</summary>
    public void Start()
    {
        UnixSockets.EnsureDirectory();
        Listen(PipeNames.HostHelloPipe(), clientSpeaksFirst: true);
        Listen(PipeNames.HostPipe(), clientSpeaksFirst: false);
    }

    private void Listen(string name, bool clientSpeaksFirst)
    {
        var path = UnixSockets.PathFor(name);
        // Left by a Host that has gone: this process holds the single-instance lock, so no live Host is using it.
        if (File.Exists(path)) File.Delete(path);
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(path));
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            listener.Listen(MaxClients);
        }
        catch
        {
            listener.Dispose();
            throw;
        }

        _listeners.Add((listener, path, AcceptLoopAsync(listener, clientSpeaksFirst, _cts.Token)));
        _log.Info($"IPC server listening on '{path}'.");
    }

    private async Task AcceptLoopAsync(Socket listener, bool clientSpeaksFirst, CancellationToken cancellationToken)
    {
        var open = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            Socket? socket = null;
            try
            {
                socket = await listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
                if (UnixSockets.PeerUserId(socket) != UnixSockets.EffectiveUserId())
                {
                    _log.Warn("A process of another user connected to the app's socket; closed.");
                    socket.Dispose();
                    continue;
                }

                if (Interlocked.Increment(ref open) > MaxClients)
                {
                    Interlocked.Decrement(ref open);
                    _log.Warn($"More than {MaxClients} clients on one socket; a connection was closed.");
                    socket.Dispose();
                    continue;
                }

                var stream = new NetworkStream(socket, ownsSocket: true);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _connections.HandleAsync(stream, clientSpeaksFirst, cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref open);
                    }
                }, CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                socket?.Dispose();
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                socket?.Dispose();
                _log.Error($"Socket accept failed: {ex.Message}");
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

    public async Task StopAsync()
    {
        if (_cts.IsCancellationRequested) return;
        _cts.Cancel();
        _connections.CompleteAll();
        foreach (var (listener, path, loop) in _listeners)
        {
            listener.Dispose();
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // expected
            }

            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // best effort; the next Host replaces it
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}
