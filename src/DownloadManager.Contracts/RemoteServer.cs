using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DownloadManager.Contracts;

/// <summary>
/// One connection to a Host that carries envelopes: the named pipe on this PC (<see cref="IpcClient"/>) or a WebSocket to
/// another server (<see cref="WebSocketIpcClient"/>). Design D9, section 5.
/// </summary>
public interface IIpcConnection : IAsyncDisposable
{
    bool IsConnected { get; }

    /// <summary>Whether <c>jobs.snapshot</c> follows <c>server.hello</c> on this connection.</summary>
    bool SnapshotFollowsHello { get; }

    Task SendAsync(IpcMessage message, CancellationToken cancellationToken = default);

    /// <summary>The next envelope, or null when the other side closed the connection.</summary>
    Task<IpcMessage?> ReadAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Which Host a connection goes to: this PC (<see cref="Local"/>, over the pipe) or a saved server, by its id, address
/// and paired token. The token is never part of <see cref="ToString"/>.
/// </summary>
public sealed class ServerTarget
{
    private ServerTarget(string? id, string name, Uri? address, string? token, bool plainHttpConfirmed)
    {
        Id = id;
        Name = name;
        Address = address;
        Token = token;
        PlainHttpConfirmed = plainHttpConfirmed;
    }

    /// <summary>The Host on this PC, over the named pipe.</summary>
    public static ServerTarget Local { get; } = new(null, "This PC", null, null, plainHttpConfirmed: false);

    public static ServerTarget Remote(string id, string name, Uri address, string token, bool plainHttpConfirmed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrEmpty(token);
        return new ServerTarget(id, name, address, token, plainHttpConfirmed);
    }

    /// <summary>Null for this PC.</summary>
    public string? Id { get; }

    public string Name { get; }

    /// <summary>The server's base address (<see cref="ServerAddress.TryNormalize"/>); null for this PC.</summary>
    public Uri? Address { get; }

    /// <summary>The paired <c>remoteClient</c> token. Sent only as <c>Authorization: Bearer</c> to <see cref="Address"/>.</summary>
    public string? Token { get; }

    /// <summary>The user agreed, for this server, to send its token over plain <c>http://</c> to another machine.</summary>
    public bool PlainHttpConfirmed { get; }

    public bool IsLocal => Id is null;

    public override string ToString() => IsLocal ? Name : $"{Name} ({Address})";
}

/// <summary>What a remote call failed on, in words for the user. Never holds a token or a password.</summary>
public class RemoteServerException(string message) : IOException(message);

/// <summary>The server refused the token (401): the pairing was removed, revoked or ended by a password change.</summary>
public sealed class RemotePairingEndedException() : RemoteServerException(
    "This server no longer accepts this PC. Pair it again in File > Servers.");

/// <summary>Plain <c>http://</c> to another machine, and the user hasn't agreed to that for this server.</summary>
public sealed class PlainHttpNotConfirmedException() : RemoteServerException(
    "This server's address starts with http://, so its sign-in would cross the network unencrypted. Allow that for this server in File > Servers first.");

/// <summary>Server addresses as the user types them, and the rule for sending a token over plain HTTP.</summary>
public static class ServerAddress
{
    /// <summary>The port an address without one gets, the same as the Docker server's and the web UI's.</summary>
    public const int DefaultPort = 8080;

    /// <summary>
    /// <c>homeserver</c>, <c>homeserver:8080</c>, <c>http://192.168.1.5:8080/downloads#x</c> and the like become
    /// <c>http://homeserver:8080/</c>: http unless https is typed, port 8080 when no scheme and no port are typed,
    /// host in lower case, path, query and fragment left out. A user name or password in the address, another scheme or
    /// an empty host is refused.
    /// </summary>
    public static bool TryNormalize(string? text, out Uri address, out string error)
    {
        address = null!;
        error = string.Empty;
        var trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            error = "Type the server's address, such as homeserver:8080.";
            return false;
        }

        var typedScheme = trimmed.Contains("://", StringComparison.Ordinal);
        if (!Uri.TryCreate(typedScheme ? trimmed : "http://" + trimmed, UriKind.Absolute, out var parsed) || parsed.Host.Length == 0)
        {
            error = "That isn't an address. Type it as homeserver:8080 or http://192.168.1.5:8080.";
            return false;
        }

        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
        {
            error = "Use an address that starts with http:// or https://.";
            return false;
        }

        if (parsed.UserInfo.Length > 0)
        {
            error = "Leave the user name and password out of the address. The server's password is asked for separately.";
            return false;
        }

        var port = !typedScheme && parsed.IsDefaultPort ? DefaultPort : parsed.Port;
        address = new UriBuilder(parsed.Scheme, parsed.IdnHost.ToLowerInvariant(), port).Uri;
        return true;
    }

    /// <summary><c>localhost</c> or a loopback IP address: a token sent there never leaves this PC.</summary>
    public static bool IsLoopback(Uri address)
    {
        var host = address.IdnHost.Trim('[', ']');
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip);
    }

    /// <summary>
    /// Design D9, section 4: a token (or the password, when pairing) goes to a non-loopback <c>http://</c> address only
    /// once the user has confirmed that for this server.
    /// </summary>
    public static bool NeedsPlainHttpConfirmation(Uri address) =>
        address.Scheme == Uri.UriSchemeHttp && !IsLoopback(address);

    public static bool MaySend(Uri address, bool plainHttpConfirmed) => plainHttpConfirmed || !NeedsPlainHttpConfirmation(address);

    /// <summary>The WebSocket on which the client speaks first (<c>/api/ws?hello=1</c>).</summary>
    public static Uri WebSocketAddress(Uri address) => new UriBuilder(address)
    {
        Scheme = address.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
        Path = "/api/ws",
        Query = "hello=1",
    }.Uri;

    public static Uri Endpoint(Uri address, string path) => new UriBuilder(address) { Path = path, Query = string.Empty }.Uri;

    internal static void EnsureMaySend(ServerTarget target)
    {
        if (target.IsLocal || target.Address is null || string.IsNullOrEmpty(target.Token))
            throw new InvalidOperationException("A remote target needs an address and a token.");
        if (!MaySend(target.Address, target.PlainHttpConfirmed)) throw new PlainHttpNotConfirmedException();
    }
}

/// <summary>
/// The wait before the next try to reach a server: 2, 4, 8, 16, 32 seconds, then <see cref="Maximum"/> (about a minute)
/// until a connection works again. Design D9, section 5.
/// </summary>
public sealed class ReconnectBackoff
{
    private readonly TimeSpan _first;
    private TimeSpan _next;

    public ReconnectBackoff(TimeSpan first, TimeSpan maximum)
    {
        if (first <= TimeSpan.Zero || maximum < first) throw new ArgumentOutOfRangeException(nameof(first));
        _first = first;
        _next = first;
        Maximum = maximum;
    }

    public ReconnectBackoff() : this(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(60)) { }

    public TimeSpan Maximum { get; }

    /// <summary>The wait to use now; each call doubles the following one, up to the maximum.</summary>
    public TimeSpan Next()
    {
        var wait = _next;
        _next = TimeSpan.FromTicks(Math.Min(_next.Ticks * 2, Maximum.Ticks));
        return wait;
    }

    /// <summary>A connection worked: the next failure waits the first time again.</summary>
    public void Reset() => _next = _first;
}

/// <summary>Opens the connection a <see cref="ServerTarget"/> names.</summary>
public static class ServerConnector
{
    /// <summary>
    /// This PC: the main pipe without <paramref name="hello"/>, the hello pipe with it (as <see cref="IpcClient"/>). A
    /// server: the WebSocket with <paramref name="hello"/> sent first (the default asks for the snapshot and events).
    /// </summary>
    public static async Task<IIpcConnection> ConnectAsync(ServerTarget target, ClientHelloDto? hello = null, TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (target.IsLocal)
        {
            return hello is null
                ? await IpcClient.ConnectAsync(timeout, cancellationToken).ConfigureAwait(false)
                : await IpcClient.ConnectAsync(hello, timeout, cancellationToken).ConfigureAwait(false);
        }

        return await WebSocketIpcClient.ConnectAsync(target, hello ?? new ClientHelloDto(), timeout, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// One request and its reply. This PC: a hello-pipe connection that asks for nothing else. A server: one
    /// <c>POST /api/ipc</c>, not a new socket.
    /// </summary>
    public static async Task<IpcMessage> SendOnceAsync(ServerTarget target, IpcMessage request, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        if (!target.IsLocal) return await RemoteServerHttp.SendIpcAsync(target, request, deadline.Token).ConfigureAwait(false);

        await using var client = await IpcClient.ConnectAsync(new ClientHelloDto { Snapshot = false, Events = false }, timeout, deadline.Token).ConfigureAwait(false);
        await client.SendAsync(request, deadline.Token).ConfigureAwait(false);
        while (await client.ReadAsync(deadline.Token).ConfigureAwait(false) is { } reply)
        {
            if (reply.ReplyTo == request.Id) return reply;
            if (reply.Type == MessageTypes.ServerIncompatible) return reply;
        }

        throw new IOException("The Host closed the connection before it answered.");
    }
}

/// <summary>
/// The WebSocket to another server's <c>/api/ws?hello=1</c>, with a paired <c>remoteClient</c> token. Each text message
/// is one envelope (the WebSocket frames it), at most <see cref="Protocol.MaxFrameBytes"/>. The first message is
/// <c>client.hello</c>; what follows is as on the hello pipe.
/// </summary>
public sealed class WebSocketIpcClient : IIpcConnection
{
    private readonly ClientWebSocket _socket;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private int _disposed;

    private WebSocketIpcClient(ClientWebSocket socket, bool snapshot)
    {
        _socket = socket;
        SnapshotFollowsHello = snapshot;
    }

    public bool IsConnected => _socket.State == WebSocketState.Open;

    public bool SnapshotFollowsHello { get; }

    /// <param name="invoker">For tests: the HTTP stack the upgrade goes through. Redirects are never followed.</param>
    public static async Task<WebSocketIpcClient> ConnectAsync(ServerTarget target, ClientHelloDto hello, TimeSpan? timeout = null,
        CancellationToken cancellationToken = default, HttpMessageInvoker? invoker = null)
    {
        ServerAddress.EnsureMaySend(target);
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + target.Token);
        socket.Options.CollectHttpResponseDetails = true;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(10));
        try
        {
            await socket.ConnectAsync(ServerAddress.WebSocketAddress(target.Address!), invoker, deadline.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or HttpRequestException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            var status = socket.HttpStatusCode;
            socket.Dispose();
            throw RemoteServerHttp.Describe(status, ex is OperationCanceledException);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        var client = new WebSocketIpcClient(socket, hello.Snapshot);
        try
        {
            await client.SendAsync(IpcMessage.Create(MessageTypes.ClientHello, hello), deadline.Token).ConfigureAwait(false);
            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task SendAsync(IpcMessage message, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(message, IpcJson.Options);
        if (json.Length > Protocol.MaxFrameBytes) throw new InvalidDataException($"IPC frame is too large ({json.Length} bytes).");
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _socket.SendAsync(json, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
        }
        catch (WebSocketException ex)
        {
            throw new IOException("The connection to the server was lost.", ex);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public async Task<IpcMessage?> ReadAsync(CancellationToken cancellationToken = default)
    {
        var buffer = new ArrayBufferWriter<byte>(16 * 1024);
        try
        {
            while (true)
            {
                var result = await _socket.ReceiveAsync(buffer.GetMemory(16 * 1024), cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) return null;
                if (result.MessageType != WebSocketMessageType.Text) throw new InvalidDataException("The server sent a binary message.");
                buffer.Advance(result.Count);
                if (buffer.WrittenCount > Protocol.MaxFrameBytes) throw new InvalidDataException($"A message over {Protocol.MaxFrameBytes} bytes.");
                if (result.EndOfMessage) break;
            }
        }
        catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
        {
            return null;
        }
        catch (WebSocketException ex)
        {
            throw new IOException("The connection to the server was lost.", ex);
        }

        try
        {
            return JsonSerializer.Deserialize<IpcMessage>(buffer.WrittenSpan, IpcJson.Options)
                   ?? throw new InvalidDataException("The server sent an empty message.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The server sent something that isn't an envelope.", ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException)
        {
            // Closing is best effort; the socket goes either way.
        }
        finally
        {
            _socket.Dispose();
            _sendGate.Dispose();
        }
    }
}

/// <summary>What <c>POST /api/pair</c> gave a desktop app. The token is shown to nobody and kept encrypted by the caller.</summary>
public sealed class RemotePairResult
{
    public bool Ok => Error is null;
    public string? Error { get; init; }
    public string? Token { get; init; }
    public string? TokenId { get; init; }

    /// <summary>The server's <c>hostKind</c> (<see cref="HostKinds"/>), when its sign-in status said.</summary>
    public string? HostKind { get; init; }

    public override string ToString() => Ok ? $"paired ({TokenId})" : $"failed: {Error}";
}

/// <summary>
/// The server's plain HTTP calls a desktop app makes: pairing with the password, its own Unpair, one-shot
/// <c>/api/ipc</c> and the <c>.torrent</c> upload. Redirects are never followed (a token goes only to the address the user
/// typed) and no cookie is kept.
/// </summary>
public static class RemoteServerHttp
{
    /// <summary>The token role a desktop app asks for and must get back.</summary>
    public const string RemoteClientRole = "remoteClient";

    private static readonly Lazy<HttpClient> Shared = new(() => new HttpClient(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
        ConnectTimeout = TimeSpan.FromSeconds(10),
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    });

    /// <summary>For tests: the client every call here uses instead of the shared one.</summary>
    public static HttpClient? ClientForTests { get; set; }

    private static HttpClient Http => ClientForTests ?? Shared.Value;

    /// <summary>
    /// <c>POST /api/pair</c> with <c>{password, name, role: "remoteClient"}</c> and no <c>Origin</c>. Answers with the token
    /// only when the server says it made a <c>remoteClient</c> one; anything else is an error in words.
    /// </summary>
    public static async Task<RemotePairResult> PairAsync(Uri address, string password, string name, bool plainHttpConfirmed,
        CancellationToken cancellationToken = default)
    {
        if (!ServerAddress.MaySend(address, plainHttpConfirmed)) return new RemotePairResult { Error = new PlainHttpNotConfirmedException().Message };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, ServerAddress.Endpoint(address, "/api/pair"))
            {
                Content = JsonContent(new PairForm { Password = password, Name = name, Role = RemoteClientRole }),
            };
            using var response = await Http.SendAsync(request, deadline.Token).ConfigureAwait(false);
            var body = await ReadJsonAsync(response, deadline.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.OK && body is { } ok)
            {
                var role = Text(ok, "role");
                var token = Text(ok, "token");
                if (role != RemoteClientRole || string.IsNullOrEmpty(token))
                    return new RemotePairResult { Error = "The server didn't give this PC the access it asked for, so nothing was saved. Update the server and try again." };
                return new RemotePairResult { Token = token, TokenId = Text(ok, "id"), HostKind = await HostKindAsync(address, deadline.Token).ConfigureAwait(false) };
            }

            return new RemotePairResult { Error = PairingError(response, body) };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new RemotePairResult { Error = $"The server at {address} didn't answer in time." };
        }
        catch (HttpRequestException)
        {
            return new RemotePairResult { Error = $"Couldn't reach a server at {address}. Check the address and that the server is running." };
        }
    }

    private static string PairingError(HttpResponseMessage response, JsonElement? body)
    {
        var said = body is { } json ? Text(json, "error") : null;
        if (said is { Length: > 300 }) said = said[..300];
        switch (response.StatusCode)
        {
            case HttpStatusCode.TooManyRequests:
                var wait = response.Headers.RetryAfter?.Delta;
                return wait is { } delta
                    ? $"Too many wrong passwords. Wait {Math.Max(1, (int)Math.Ceiling(delta.TotalSeconds))} seconds and try again."
                    : "Too many wrong passwords. Wait a moment and try again.";
            case HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed:
                return "That address doesn't answer like an Inbound server, or the server is too old to be managed from here.";
        }

        if (body is null) return "That address doesn't answer like an Inbound server.";
        return said ?? $"The server refused the pairing ({(int)response.StatusCode}).";
    }

    /// <summary>The <c>hostKind</c> in the server's public sign-in status, or null.</summary>
    private static async Task<string?> HostKindAsync(Uri address, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await Http.GetAsync(ServerAddress.Endpoint(address, "/api/auth/status"), cancellationToken).ConfigureAwait(false);
            return await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false) is { } body ? Text(body, "hostKind") : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>
    /// <c>DELETE /api/pair/self</c>: the server forgets this token. True when it did, or already had (401); false when the
    /// server couldn't be reached or refused, in which case the user removes the pairing in the server's web UI.
    /// </summary>
    public static async Task<bool> UnpairAsync(ServerTarget target, CancellationToken cancellationToken = default)
    {
        try
        {
            ServerAddress.EnsureMaySend(target);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            using var request = new HttpRequestMessage(HttpMethod.Delete, ServerAddress.Endpoint(target.Address!, "/api/pair/self"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", target.Token);
            using var response = await Http.SendAsync(request, deadline.Token).ConfigureAwait(false);
            return response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.OK or HttpStatusCode.Unauthorized;
        }
        catch (Exception ex) when (ex is HttpRequestException or RemoteServerException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>One envelope to <c>POST /api/ipc</c> and its reply. A refused token throws <see cref="RemotePairingEndedException"/>.</summary>
    public static async Task<IpcMessage> SendIpcAsync(ServerTarget target, IpcMessage message, CancellationToken cancellationToken = default)
    {
        ServerAddress.EnsureMaySend(target);
        using var request = new HttpRequestMessage(HttpMethod.Post, ServerAddress.Endpoint(target.Address!, "/api/ipc"))
        {
            Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(message, IpcJson.Options)),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", target.Token);
        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new RemoteServerException($"Couldn't reach {target.Name}. {ex.HttpRequestError switch { HttpRequestError.NameResolutionError => "Its name isn't known on this network.", HttpRequestError.ConnectionError => "It may be off or on another network.", _ => string.Empty }}".TrimEnd());
        }

        using (response)
        {
            if (response.StatusCode != HttpStatusCode.OK) throw Describe(response.StatusCode, timedOut: false);
            var bytes = await ReadLimitedAsync(response, cancellationToken).ConfigureAwait(false);
            try
            {
                return JsonSerializer.Deserialize<IpcMessage>(bytes, IpcJson.Options) ?? throw new InvalidDataException("The server sent an empty reply.");
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("The server's reply isn't an envelope.", ex);
            }
        }
    }

    /// <summary>
    /// <c>POST /api/upload/torrent</c> with the file's bytes; the <c>upload:</c> reference the server's <c>remote.classify</c>
    /// and <c>remote.add</c> take. A server that doesn't take uploads from a paired app says so in the error.
    /// </summary>
    public static async Task<(string? Reference, string? Error)> UploadTorrentAsync(ServerTarget target, byte[] content, CancellationToken cancellationToken = default)
    {
        try
        {
            ServerAddress.EnsureMaySend(target);
            using var request = new HttpRequestMessage(HttpMethod.Post, ServerAddress.Endpoint(target.Address!, "/api/upload/torrent"))
            {
                Content = new ByteArrayContent(content),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-bittorrent");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", target.Token);
            using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.OK && body is { } ok && Text(ok, "upload") is { } reference) return (reference, null);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
                return (null, $"{target.Name} doesn't take .torrent files from this app. Paste the magnet link instead, or add the file in the server's web UI.");
            return (null, body is { } failed && Text(failed, "error") is { } said ? said : $"{target.Name} couldn't take the file.");
        }
        catch (RemoteServerException ex)
        {
            return (null, ex.Message);
        }
        catch (HttpRequestException)
        {
            return (null, $"Couldn't reach {target.Name}.");
        }
    }

    /// <summary>An HTTP status that ended a remote call, in words.</summary>
    internal static RemoteServerException Describe(HttpStatusCode status, bool timedOut) => status switch
    {
        HttpStatusCode.Unauthorized => new RemotePairingEndedException(),
        HttpStatusCode.Forbidden => new RemoteServerException("The server refused this app's connection. Check that the address is the server's own, then pair again."),
        HttpStatusCode.TooManyRequests => new RemoteServerException("The server is busy. It will be tried again shortly."),
        (HttpStatusCode)0 when timedOut => new RemoteServerException("The server didn't answer in time."),
        (HttpStatusCode)0 => new RemoteServerException("Couldn't reach the server."),
        _ => new RemoteServerException($"The server answered with an error ({(int)status})."),
    };

    private static async Task<byte[]> ReadLimitedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > Protocol.MaxFrameBytes) throw new InvalidDataException("The server's reply is too large.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var memory = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            memory.Write(buffer, 0, read);
            if (memory.Length > Protocol.MaxFrameBytes) throw new InvalidDataException("The server's reply is too large.");
        }

        return memory.ToArray();
    }

    private static async Task<JsonElement?> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentType?.MediaType != "application/json") return null;
        try
        {
            var bytes = await ReadLimitedAsync(response, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(bytes);
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement body, string name) =>
        body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static ByteArrayContent JsonContent<T>(T value)
    {
        var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(value, IpcJson.Options));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        return content;
    }

    private sealed class PairForm
    {
        public string Password { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string Role { get; init; } = string.Empty;
    }
}
