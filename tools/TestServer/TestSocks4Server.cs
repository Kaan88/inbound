using System.Net;
using System.Net.Sockets;
using System.Text;

namespace TestServer;

/// <summary>
/// Minimal SOCKS4 / SOCKS4a server (CONNECT only). SOCKS4 has no password, so
/// the only credential is the USERID field; when a required user id is set,
/// other values are refused with code 91. SOCKS4a requests (destination
/// 0.0.0.x with a trailing hostname) are resolved here and counted separately.
/// </summary>
public sealed class TestSocks4Server : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly string? _requiredUserId;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private long _connects;
    private long _hostnameConnects;
    private long _denied;
    private string _lastUserId = string.Empty;
    private string _lastTarget = string.Empty;

    private TestSocks4Server(TcpListener listener, string? requiredUserId)
    {
        _listener = listener;
        _requiredUserId = requiredUserId;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        Address = $"127.0.0.1:{Port}";
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public static TestSocks4Server Start(string? requiredUserId = "socks4user")
        => new(new TcpListener(IPAddress.Loopback, 0), requiredUserId);

    public string Address { get; }
    public int Port { get; }
    public long Connects => Interlocked.Read(ref _connects);

    /// <summary>CONNECTs whose destination was a hostname (SOCKS4a).</summary>
    public long HostnameConnects => Interlocked.Read(ref _hostnameConnects);

    public long Denied => Interlocked.Read(ref _denied);
    public string LastUserId => _lastUserId;
    public string LastTarget => _lastTarget;

    public void ResetStats()
    {
        Interlocked.Exchange(ref _connects, 0);
        Interlocked.Exchange(ref _hostnameConnects, 0);
        Interlocked.Exchange(ref _denied, 0);
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                return;
            }

            _ = HandleClientAsync(client, cancellationToken);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var header = new byte[8];
                await ReadExactAsync(stream, header, cancellationToken).ConfigureAwait(false);
                var version = header[0];
                var command = header[1];
                var port = (header[2] << 8) | header[3];
                var addressBytes = header[4..8];
                var userId = await ReadNullTerminatedAsync(stream, cancellationToken).ConfigureAwait(false);
                _lastUserId = userId;

                if (version != 4 || command != 1)
                {
                    Interlocked.Increment(ref _denied);
                    await ReplyAsync(stream, 91, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (_requiredUserId is not null &&
                    !string.Equals(userId, _requiredUserId, StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref _denied);
                    await ReplyAsync(stream, 93, cancellationToken).ConfigureAwait(false);
                    return;
                }

                string host;
                var isSocks4a = addressBytes[0] == 0 && addressBytes[1] == 0 && addressBytes[2] == 0 &&
                                addressBytes[3] != 0;
                if (isSocks4a)
                {
                    host = await ReadNullTerminatedAsync(stream, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    host = new IPAddress(addressBytes).ToString();
                }

                _lastTarget = $"{host}:{port}";
                using var upstream = new TcpClient();
                try
                {
                    // "localhost" must resolve to IPv4 because the test servers bind 127.0.0.1.
                    var target = isSocks4a && string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
                        ? IPAddress.Loopback.ToString()
                        : host;
                    await upstream.ConnectAsync(target, port, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref _denied);
                    await ReplyAsync(stream, 91, cancellationToken).ConfigureAwait(false);
                    return;
                }

                Interlocked.Increment(ref _connects);
                if (isSocks4a)
                {
                    Interlocked.Increment(ref _hostnameConnects);
                }

                await ReplyAsync(stream, 90, cancellationToken).ConfigureAwait(false);
                var upstreamStream = upstream.GetStream();
                await Task.WhenAny(
                    stream.CopyToAsync(upstreamStream, cancellationToken),
                    upstreamStream.CopyToAsync(stream, cancellationToken)).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Test client may close early; a new connection starts cleanly.
            }
        }
    }

    private static async Task ReplyAsync(Stream stream, byte code, CancellationToken cancellationToken)
    {
        var reply = new byte[] { 0, code, 0, 0, 0, 0, 0, 0 };
        await stream.WriteAsync(reply, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ReadNullTerminatedAsync(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>();
        var buffer = new byte[1];
        while (bytes.Count < 512)
        {
            await ReadExactAsync(stream, buffer, cancellationToken).ConfigureAwait(false);
            if (buffer[0] == 0)
            {
                break;
            }

            bytes.Add(buffer[0]);
        }

        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("The SOCKS4 client closed the connection.");
            }

            offset += read;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        try
        {
            await _acceptLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // expected
        }

        _cts.Dispose();
    }
}
