using System.Net;
using System.Net.Sockets;
using System.Text;

namespace TestServer;

/// <summary>
/// Minimal local HTTP proxy with Basic proxy authentication. Supports plain
/// absolute-form HTTP forwarding and CONNECT tunneling for HTTPS so the engine's
/// proxy-auth and tunnel paths can be tested without external services.
/// </summary>
public sealed class TestProxyServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly string _username;
    private readonly string _password;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private long _authorized;
    private long _denied;
    private long _challenges;
    private long _tunnels;
    private bool _lastAuthorizationPresent;
    private string _lastTarget = string.Empty;

    private TestProxyServer(TcpListener listener, string username, string password)
    {
        _listener = listener;
        _username = username;
        _password = password;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        Address = $"127.0.0.1:{Port}";
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public static TestProxyServer Start(string username = "proxyuser", string password = "proxypass")
        => new(new TcpListener(IPAddress.Loopback, 0), username, password);

    public string Address { get; }
    public int Port { get; }
    public long Authorized => Interlocked.Read(ref _authorized);
    public long Denied => Interlocked.Read(ref _denied);
    public long Challenges => Interlocked.Read(ref _challenges);
    public long Tunnels => Interlocked.Read(ref _tunnels);
    public bool LastAuthorizationPresent => _lastAuthorizationPresent;
    public string LastTarget => _lastTarget;

    public void ResetStats()
    {
        Interlocked.Exchange(ref _authorized, 0);
        Interlocked.Exchange(ref _denied, 0);
        Interlocked.Exchange(ref _challenges, 0);
        Interlocked.Exchange(ref _tunnels, 0);
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
                var header = await ReadHeaderAsync(stream, cancellationToken).ConfigureAwait(false);
                if (header is null)
                {
                    return;
                }

                var lines = header.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length == 0)
                {
                    return;
                }

                var requestLine = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                if (requestLine.Length < 2)
                {
                    return;
                }

                var method = requestLine[0];
                var target = requestLine[1];
                var authorization = lines.FirstOrDefault(line =>
                    line.StartsWith("Proxy-Authorization:", StringComparison.OrdinalIgnoreCase));
                var authorized = IsAuthorized(authorization);
                _lastAuthorizationPresent = authorization is not null;
                _lastTarget = target;
                if (!authorized)
                {
                    if (authorization is null)
                    {
                        Interlocked.Increment(ref _challenges);
                    }
                    else
                    {
                        Interlocked.Increment(ref _denied);
                    }

                    await WriteAsync(stream,
                        "HTTP/1.1 407 Proxy Authentication Required\r\n" +
                        "Proxy-Authenticate: Basic realm=\"TestProxy\"\r\n" +
                        "Content-Length: 0\r\nConnection: close\r\n\r\n",
                        cancellationToken).ConfigureAwait(false);
                    return;
                }

                Interlocked.Increment(ref _authorized);

                if (method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
                {
                    Interlocked.Increment(ref _tunnels);
                    var (host, port) = SplitAuthority(target);
                    using var tunnel = new TcpClient();
                    await tunnel.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
                    await WriteAsync(stream, "HTTP/1.1 200 Connection Established\r\n\r\n", cancellationToken)
                        .ConfigureAwait(false);
                    var upstream = tunnel.GetStream();
                    await Task.WhenAny(
                        stream.CopyToAsync(upstream, cancellationToken),
                        upstream.CopyToAsync(stream, cancellationToken)).ConfigureAwait(false);
                    return;
                }

                if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) ||
                    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                {
                    await WriteAsync(stream,
                        "HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
                        cancellationToken).ConfigureAwait(false);
                    return;
                }

                using var upstreamClient = new TcpClient();
                await upstreamClient.ConnectAsync(uri.Host, uri.Port, cancellationToken).ConfigureAwait(false);
                var upstreamStream = upstreamClient.GetStream();

                var request = new StringBuilder();
                request.Append($"{method} {uri.PathAndQuery} HTTP/1.1\r\n");
                foreach (var line in lines.Skip(1))
                {
                    if (line.StartsWith("Proxy-Authorization:", StringComparison.OrdinalIgnoreCase) ||
                        line.StartsWith("Proxy-Connection:", StringComparison.OrdinalIgnoreCase) ||
                        line.StartsWith("Connection:", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    request.Append(line).Append("\r\n");
                }

                request.Append("Connection: close\r\n\r\n");
                await upstreamStream.WriteAsync(
                    Encoding.UTF8.GetBytes(request.ToString()), cancellationToken).ConfigureAwait(false);
                await upstreamStream.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The test client may close early; the next request opens a new connection.
            }
        }
    }

    private bool IsAuthorized(string? header)
    {
        if (header is null)
        {
            return false;
        }

        var value = header["Proxy-Authorization:".Length..].Trim();
        var expected = "Basic " + Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{_username}:{_password}"));
        return string.Equals(value, expected, StringComparison.Ordinal);
    }

    private static (string Host, int Port) SplitAuthority(string authority)
    {
        var separator = authority.LastIndexOf(':');
        if (separator <= 0 || !int.TryParse(authority[(separator + 1)..], out var port))
        {
            return (authority, 80);
        }

        var host = authority[..separator].Trim('[', ']');
        return (host, port);
    }

    private static async Task<string?> ReadHeaderAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        using var collected = new MemoryStream();
        while (collected.Length < 64 * 1024)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            collected.Write(buffer, 0, read);
            var bytes = collected.GetBuffer();
            var length = (int)collected.Length;
            for (var i = 3; i < length; i++)
            {
                if (bytes[i - 3] == '\r' && bytes[i - 2] == '\n' &&
                    bytes[i - 1] == '\r' && bytes[i] == '\n')
                {
                    return Encoding.UTF8.GetString(bytes, 0, i + 1);
                }
            }
        }

        return null;
    }

    private static async Task WriteAsync(Stream stream, string text, CancellationToken cancellationToken)
        => await stream.WriteAsync(Encoding.UTF8.GetBytes(text), cancellationToken).ConfigureAwait(false);

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
