using System.Net;
using System.Net.Sockets;
using System.Text;

namespace TestServer;

/// <summary>
/// Minimal SOCKS5 server with username/password authentication (RFC 1929) and
/// CONNECT support. Enough to exercise the engine's SOCKS5 path locally.
/// </summary>
public sealed class TestSocks5Server : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly string _username;
    private readonly string _password;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private long _authorized;
    private long _denied;
    private long _connects;
    private bool _lastAuthPresent;
    private string _lastTarget = string.Empty;

    private TestSocks5Server(TcpListener listener, string username, string password)
    {
        _listener = listener;
        _username = username;
        _password = password;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        Address = $"127.0.0.1:{Port}";
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public static TestSocks5Server Start(string username = "socksuser", string password = "sockspass")
        => new(new TcpListener(IPAddress.Loopback, 0), username, password);

    public string Address { get; }
    public int Port { get; }
    public long Authorized => Interlocked.Read(ref _authorized);
    public long Denied => Interlocked.Read(ref _denied);
    public long Connects => Interlocked.Read(ref _connects);
    public bool LastAuthPresent => _lastAuthPresent;
    public string LastTarget => _lastTarget;

    public void ResetStats()
    {
        Interlocked.Exchange(ref _authorized, 0);
        Interlocked.Exchange(ref _denied, 0);
        Interlocked.Exchange(ref _connects, 0);
        _lastAuthPresent = false;
        _lastTarget = string.Empty;
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, int> _portMap = new();

    /// <summary>
    /// Requests for <paramref name="from"/> are forwarded to <paramref name="to"/>. A test points a download at a port
    /// that counts direct connections; only a request that really went through this proxy reaches the server.
    /// </summary>
    public void MapPort(int from, int to) => _portMap[from] = to;

    private int Mapped(int port) => _portMap.TryGetValue(port, out var to) ? to : port;

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

                var version = await ReadByteAsync(stream, cancellationToken).ConfigureAwait(false);
                var methodCount = await ReadByteAsync(stream, cancellationToken).ConfigureAwait(false);
                var methods = new byte[methodCount];
                await ReadExactAsync(stream, methods, cancellationToken).ConfigureAwait(false);
                if (version != 5 || !methods.Contains((byte)2))
                {
                    Interlocked.Increment(ref _denied);
                    await stream.WriteAsync(new byte[] { 5, 0xFF }, cancellationToken).ConfigureAwait(false);
                    return;
                }

                await stream.WriteAsync(new byte[] { 5, 2 }, cancellationToken).ConfigureAwait(false);

                var authVersion = await ReadByteAsync(stream, cancellationToken).ConfigureAwait(false);
                var userLength = await ReadByteAsync(stream, cancellationToken).ConfigureAwait(false);
                var userBytes = new byte[userLength];
                await ReadExactAsync(stream, userBytes, cancellationToken).ConfigureAwait(false);
                var passwordLength = await ReadByteAsync(stream, cancellationToken).ConfigureAwait(false);
                var passwordBytes = new byte[passwordLength];
                await ReadExactAsync(stream, passwordBytes, cancellationToken).ConfigureAwait(false);

                var username = Encoding.UTF8.GetString(userBytes);
                var password = Encoding.UTF8.GetString(passwordBytes);
                _lastAuthPresent = userLength > 0 || passwordLength > 0;
                if (authVersion != 1 ||
                    !string.Equals(username, _username, StringComparison.Ordinal) ||
                    !string.Equals(password, _password, StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref _denied);
                    await stream.WriteAsync(new byte[] { 1, 1 }, cancellationToken).ConfigureAwait(false);
                    return;
                }

                Interlocked.Increment(ref _authorized);
                await stream.WriteAsync(new byte[] { 1, 0 }, cancellationToken).ConfigureAwait(false);

                var requestVersion = await ReadByteAsync(stream, cancellationToken).ConfigureAwait(false);
                var command = await ReadByteAsync(stream, cancellationToken).ConfigureAwait(false);
                await ReadByteAsync(stream, cancellationToken).ConfigureAwait(false); // reserved
                var addressType = await ReadByteAsync(stream, cancellationToken).ConfigureAwait(false);
                if (requestVersion != 5 || command != 1)
                {
                    await ReplyAsync(stream, 7, cancellationToken).ConfigureAwait(false);
                    return;
                }

                string host;
                switch (addressType)
                {
                    case 1:
                    {
                        var address = new byte[4];
                        await ReadExactAsync(stream, address, cancellationToken).ConfigureAwait(false);
                        host = new IPAddress(address).ToString();
                        break;
                    }
                    case 3:
                    {
                        var length = await ReadByteAsync(stream, cancellationToken).ConfigureAwait(false);
                        var name = new byte[length];
                        await ReadExactAsync(stream, name, cancellationToken).ConfigureAwait(false);
                        host = Encoding.UTF8.GetString(name);
                        break;
                    }
                    case 4:
                    {
                        var address = new byte[16];
                        await ReadExactAsync(stream, address, cancellationToken).ConfigureAwait(false);
                        host = new IPAddress(address).ToString();
                        break;
                    }
                    default:
                        await ReplyAsync(stream, 8, cancellationToken).ConfigureAwait(false);
                        return;
                }

                var portHigh = await ReadByteAsync(stream, cancellationToken).ConfigureAwait(false);
                var portLow = await ReadByteAsync(stream, cancellationToken).ConfigureAwait(false);
                var port = (portHigh << 8) | portLow;
                _lastTarget = $"{host}:{port}";

                using var upstream = new TcpClient();
                try
                {
                    await upstream.ConnectAsync(host, Mapped(port), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    await ReplyAsync(stream, 5, cancellationToken).ConfigureAwait(false);
                    return;
                }

                Interlocked.Increment(ref _connects);
                await ReplyAsync(stream, 0, cancellationToken).ConfigureAwait(false);
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
        var reply = new byte[] { 5, code, 0, 1, 0, 0, 0, 0, 0, 0 };
        await stream.WriteAsync(reply, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte> ReadByteAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[1];
        await ReadExactAsync(stream, buffer, cancellationToken).ConfigureAwait(false);
        return buffer[0];
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("The SOCKS5 client closed the connection.");
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
