using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using DownloadManager.Contracts;
using DownloadManager.Server.Auth;
using DownloadManager.Server.Bridge;

namespace DownloadManager.Server.Tests;

/// <summary>
/// A stand-in for the desktop Host's hello pipe: it keeps every <c>client.hello</c> it is sent, says hello as a Windows
/// Host, sends a snapshot when asked, answers each request with <c>test.echo</c> (and <c>folders.browse</c> with its
/// result), and closes a connection that sends <c>test.close</c>, as the Host does when the web UI is turned off.
/// </summary>
internal sealed class FakeHostPipe : IAsyncDisposable
{
    public const string Echo = "test.echo";
    public const string Close = "test.close";

    private readonly CancellationTokenSource _stop = new();
    private readonly Task _accepting;
    private readonly ConcurrentBag<NamedPipeServerStream> _open = [];

    public FakeHostPipe()
    {
        Name = "dm-bridge-test-" + Guid.NewGuid().ToString("N");
        _accepting = AcceptAsync();
    }

    public string Name { get; }
    public ConcurrentQueue<ClientHelloDto> Hellos { get; } = new();
    public ConcurrentQueue<string> Requests { get; } = new();

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(Name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.WaitForConnectionAsync(_stop.Token);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync();
                return;
            }

            _open.Add(pipe);
            _ = ServeAsync(pipe);
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe)
    {
        try
        {
            var first = await IpcFraming.ReadAsync(pipe, _stop.Token);
            if (first?.Type != MessageTypes.ClientHello) return;
            var hello = first.GetPayload<ClientHelloDto>();
            Hellos.Enqueue(hello);
            await IpcFraming.WriteAsync(pipe, IpcMessage.Create(MessageTypes.ServerHello, new HostHelloDto
            {
                HostVersion = "test",
                ProtocolVersion = Protocol.Version,
                HostKind = HostKinds.Windows,
                Capabilities = ["jobs", "status", "folders"],
            }), _stop.Token);
            if (hello.Snapshot) await IpcFraming.WriteAsync(pipe, IpcMessage.Create(MessageTypes.JobsSnapshot, new { jobs = Array.Empty<object>() }), _stop.Token);
            while (await IpcFraming.ReadAsync(pipe, _stop.Token) is { } request)
            {
                Requests.Enqueue(request.Type);
                if (request.Type == Close) return;
                var reply = request.Type == MessageTypes.FoldersBrowse
                    ? IpcMessage.Create(MessageTypes.FoldersBrowseResult, new FolderBrowseResult { Ok = true, Roots = [@"C:\Downloads"] }, request.Id)
                    : IpcMessage.Create(Echo, new HostActionResult { Ok = true, Message = request.Type }, request.Id);
                await IpcFraming.WriteAsync(pipe, reply, _stop.Token);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or InvalidDataException)
        {
            // The bridge went away, or the test ended.
        }
        finally
        {
            await pipe.DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        foreach (var pipe in _open) await pipe.DisposeAsync();
        try { await _accepting; } catch (OperationCanceledException) { }
        _stop.Dispose();
    }
}

/// <summary>
/// D8, the web bridge (<c>DownloadManager.Server.exe --bridge</c>): no setup page, no start without a password, the
/// window's one-time ticket, and each browser connection carried to the Host's hello pipe in the web role, whatever the
/// browser asked for. The Host is <see cref="FakeHostPipe"/>; the real one is in the integration tests.
/// </summary>
public sealed class BridgeTests : IAsyncDisposable
{
    private readonly string _data = Path.Combine(AppContext.BaseDirectory, "test-work", "bridge-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _key = WebUiTicket.NewKey();
    private readonly ManualTime _time = new(DateTimeOffset.UtcNow);
    private readonly FakeHostPipe _host = new();
    private BridgeApp? _bridge;

    public BridgeTests() => Directory.CreateDirectory(_data);

    public async ValueTask DisposeAsync()
    {
        if (_bridge is not null) await _bridge.DisposeAsync();
        await _host.DisposeAsync();
        try { Directory.Delete(_data, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
    }

    private WebBridgeStartDto Start(string? pipe = null, int port = 0) => new()
    {
        DataDirectory = _data,
        HelloPipe = pipe ?? _host.Name,
        Port = port,
        LocalOnly = true,
        TicketKey = Convert.ToBase64String(_key),
    };

    private WebBridgeStartDto Change(Action<WebBridgeStartDto> change, int port = 0)
    {
        var start = Start(port: port);
        change(start);
        return start;
    }

    private async Task SetPasswordAsync()
    {
        var store = AuthStore.Open(_data);
        store.SetPassword(await PasswordHash.CreateAsync(AuthServer.Password, DateTimeOffset.UtcNow, "app"));
    }

    private async Task<Uri> StartBridgeAsync(string? pipe = null)
    {
        await SetPasswordAsync();
        _bridge = await BridgeApp.StartAsync(Start(pipe), anyPort: true, time: _time);
        return _bridge.Addresses.First(address => address.Scheme == "http");
    }

    private static async Task<AuthClient> SignInAsync(Uri address)
    {
        var client = new AuthClient(address);
        using var response = await client.PostAsync("/api/auth/signin", new { password = AuthServer.Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(client.Session);
        return client;
    }

    private static async Task<WsClient> ConnectAsync(Uri address, string? session, string query = "", string? bearer = null)
    {
        var socket = new ClientWebSocket();
        if (session is not null)
        {
            socket.Options.Cookies = new CookieContainer();
            socket.Options.Cookies.Add(address, new Cookie(ServerAuth.CookieName, session));
        }

        if (bearer is not null) socket.Options.SetRequestHeader("Authorization", "Bearer " + bearer);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await socket.ConnectAsync(new UriBuilder(address) { Scheme = "ws", Path = "/api/ws", Query = query }.Uri, timeout.Token);
        return new WsClient(socket);
    }

    private string Ticket(DateTimeOffset? at = null, byte[]? key = null) =>
        $"{ServerAuth.TicketPath}?t={WebUiTicket.Create(key ?? _key, at ?? _time.GetUtcNow())}";

    [Fact]
    public async Task ItRefusesToStartWithoutAStoredPassword()
    {
        var refused = await Assert.ThrowsAsync<ServerStartException>(() => BridgeApp.StartAsync(Start(), anyPort: true, time: _time));
        Assert.Equal("No password is set for the web UI. Set one in the app on this PC (Options, Web UI).", refused.Message);
        Assert.Empty(_host.Hellos);
    }

    [Fact]
    public void ItRefusesOptionsItCantUse()
    {
        Assert.Throws<ServerStartException>(() => BridgeApp.OptionsFor(Change(start => start.TicketKey = Convert.ToBase64String(new byte[16])), anyPort: true));
        Assert.Throws<ServerStartException>(() => BridgeApp.OptionsFor(Change(start => start.TicketKey = "not base64!"), anyPort: true));
        Assert.Throws<ServerStartException>(() => BridgeApp.OptionsFor(Change(start => start.HelloPipe = @"..\other"), anyPort: true));
        Assert.Throws<ServerStartException>(() => BridgeApp.OptionsFor(Change(start => start.DataDirectory = "relative"), anyPort: true));
        Assert.Equal("The port must be between 1024 and 65535.", Assert.Throws<ServerStartException>(() => BridgeApp.OptionsFor(Start(port: 80))).Message);
        Assert.Throws<ServerStartException>(() => BridgeApp.OptionsFor(Start(port: 0)));

        var local = BridgeApp.OptionsFor(Start(port: 18080));
        Assert.True(local.Bridge);
        Assert.True(local.SignInRequired);
        Assert.Equal(HostKinds.Windows, local.HostKind);
        Assert.Empty(local.Roots);
        Assert.All(local.Urls, url => Assert.True(url is "http://127.0.0.1:18080" or "http://[::1]:18080", url));
        var everywhere = BridgeApp.OptionsFor(Change(start => { start.LocalOnly = false; start.Hosts = ["mypc"]; }, 18080));
        Assert.Contains("http://0.0.0.0:18080", everywhere.Urls);
        Assert.Equal(["mypc"], everywhere.AllowedHostNames);
    }

    [Fact]
    public async Task ThereIsNoSetupPageAndTheStatusSaysWindows()
    {
        var address = await StartBridgeAsync();
        using var anyone = new AuthClient(address);
        using var status = await anyone.GetAsync("/api/auth/status");
        var body = await anyone.JsonAsync(status);
        Assert.Equal(HostKinds.Windows, body.GetProperty("hostKind").GetString());
        Assert.False(body.GetProperty("setupNeeded").GetBoolean());
        using var setup = await anyone.PostAsync("/api/auth/setup", new { password = "a password of my own" });
        Assert.NotEqual(HttpStatusCode.OK, setup.StatusCode);
        Assert.Null(anyone.Session);
        Assert.True(await PasswordHash.VerifyAsync(AuthServer.Password, AuthStore.Open(_data).Password));
        // Nothing reached the Host without a sign-in.
        var (ipcStatus, _) = await anyone.IpcAsync(MessageTypes.HostPing, new { });
        Assert.Equal(HttpStatusCode.Unauthorized, ipcStatus);
        Assert.Empty(_host.Hellos);
    }

    [Fact]
    public async Task ASignedInBrowsersSocketReachesTheHostInTheWebRole()
    {
        var address = await StartBridgeAsync();
        using var user = await SignInAsync(address);
        await using var socket = await ConnectAsync(address, user.Session);
        var hello = (await socket.ReadAsync())!;
        Assert.Equal(MessageTypes.ServerHello, hello.Type);
        Assert.Equal(HostKinds.Windows, hello.GetPayload<HostHelloDto>().HostKind);
        Assert.Equal(MessageTypes.JobsSnapshot, (await socket.ReadAsync())!.Type);
        var echo = await socket.AskAsync<HostActionResult>(MessageTypes.SettingsGet, new { });
        Assert.Equal(MessageTypes.SettingsGet, echo.Message);
        var seen = Assert.Single(_host.Hellos);
        Assert.Equal(ClientRoles.Web, seen.Role);
        Assert.True(seen.Snapshot);
        Assert.True(seen.Events);
    }

    [Fact]
    public async Task ASocketThatSpeaksFirstStillGetsTheWebRole()
    {
        var address = await StartBridgeAsync();
        using var user = await SignInAsync(address);
        await using var socket = await ConnectAsync(address, user.Session, "hello=1");
        await socket.SendAsync(IpcMessage.Create(MessageTypes.ClientHello, new ClientHelloDto { Role = ClientRoles.Desktop, Snapshot = false, Events = false }));
        Assert.Equal(MessageTypes.ServerHello, (await socket.ReadAsync())!.Type);
        Assert.Equal(MessageTypes.HostPing, (await socket.AskAsync<HostActionResult>(MessageTypes.HostPing, new { })).Message);
        var seen = Assert.Single(_host.Hellos);
        Assert.Equal(ClientRoles.Web, seen.Role);
        Assert.False(seen.Snapshot);
        Assert.False(seen.Events);
        Assert.DoesNotContain(socket.Received, frame => frame.Type == MessageTypes.JobsSnapshot);
    }

    [Fact]
    public async Task OneIpcCallIsOneQuietWebConnection()
    {
        var address = await StartBridgeAsync();
        using var user = await SignInAsync(address);
        var (status, reply) = await user.IpcAsync(MessageTypes.JobsPauseAll, new { });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(FakeHostPipe.Echo, reply!.Type);
        Assert.Equal(MessageTypes.JobsPauseAll, reply.GetPayload<HostActionResult>().Message);
        var seen = Assert.Single(_host.Hellos);
        Assert.Equal(ClientRoles.Web, seen.Role);
        Assert.False(seen.Snapshot);
        Assert.False(seen.Events);
    }

    [Fact]
    public async Task AnExtensionsTokenKeepsTheExtensionsCommandsAndADesktopAppsTokenHasABrowsersRights()
    {
        var address = await StartBridgeAsync();
        var (extension, _) = _bridge!.Auth.Pairing.Issue(TokenRoles.Extension, "Chrome");
        var (desktop, _) = _bridge.Auth.Pairing.Issue(TokenRoles.RemoteClient, "Desk PC");
        using var app = new AuthClient(address);

        var (refused, refusal) = await app.IpcAsync(MessageTypes.SettingsGet, new { }, extension);
        Assert.Equal(HttpStatusCode.Forbidden, refused);
        Assert.Equal($"A paired browser can't send '{MessageTypes.SettingsGet}'.", refusal!.GetPayload<HostActionResult>().Message);
        Assert.DoesNotContain(MessageTypes.SettingsGet, _host.Requests);
        var (browsed, browse) = await app.IpcAsync(MessageTypes.FoldersBrowse, new FolderBrowseRequest(), extension);
        Assert.Equal(HttpStatusCode.OK, browsed);
        Assert.Equal(MessageTypes.FoldersBrowseResult, browse!.Type);

        var (allowed, echo) = await app.IpcAsync(MessageTypes.SettingsGet, new { }, desktop);
        Assert.Equal(HttpStatusCode.OK, allowed);
        Assert.Equal(MessageTypes.SettingsGet, echo!.GetPayload<HostActionResult>().Message);
        await using var socket = await ConnectAsync(address, null, bearer: desktop);
        Assert.Equal(MessageTypes.ServerHello, (await socket.ReadAsync())!.Type);
        Assert.All(_host.Hellos, hello => Assert.Equal(ClientRoles.Web, hello.Role));
    }

    [Fact]
    public async Task ATicketSignsInABrowserOnThisPcOnceWithinAMinute()
    {
        var address = await StartBridgeAsync();
        var ticket = Ticket();

        using (var browser = new AuthClient(address))
        using (var signedIn = await browser.GetAsync(ticket))
        {
            Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
            Assert.Equal("/", signedIn.Headers.Location?.OriginalString);
            Assert.NotNull(browser.Session);
            var (status, _) = await browser.IpcAsync(MessageTypes.HostPing, new { });
            Assert.Equal(HttpStatusCode.OK, status);
        }

        // Each failure below makes this address wait (1 s, 2 s, ...), so the clock moves past each wait.
        async Task RefusedAsync(string path, Action<HttpRequestMessage>? change = null, HttpStatusCode expected = HttpStatusCode.Redirect)
        {
            using var browser = new AuthClient(address);
            using var response = await browser.GetAsync(path, change);
            Assert.Equal(expected, response.StatusCode);
            if (expected == HttpStatusCode.Redirect) Assert.Equal(ServerAuth.SignInPath, response.Headers.Location?.OriginalString);
            Assert.Null(browser.Session);
            _time.Advance(TimeSpan.FromMinutes(6));
        }

        await RefusedAsync(ticket);
        await RefusedAsync(Ticket(key: WebUiTicket.NewKey()));
        await RefusedAsync(ServerAuth.TicketPath + "?t=nothing");
        await RefusedAsync(ServerAuth.TicketPath);
        var old = Ticket();
        _time.Advance(TimeSpan.FromSeconds(61));
        await RefusedAsync(old);
        await RefusedAsync(Ticket(), request => request.Headers.Add("X-Forwarded-For", "192.168.1.30"), HttpStatusCode.Forbidden);
        await RefusedAsync(Ticket(), request => request.Headers.Add("Forwarded", "for=192.168.1.30"), HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ATicketCountsAsASignInTryWhenItIsWrong()
    {
        var address = await StartBridgeAsync();
        using var browser = new AuthClient(address);
        using (var wrong = await browser.GetAsync(ServerAuth.TicketPath + "?t=wrong")) Assert.Equal(HttpStatusCode.Redirect, wrong.StatusCode);
        using (var waiting = await browser.GetAsync(Ticket())) Assert.Equal(HttpStatusCode.TooManyRequests, waiting.StatusCode);
        Assert.Null(browser.Session);
    }

    [Fact]
    public async Task AHostThatIsntThereIsSaidPlainly()
    {
        var address = await StartBridgeAsync(pipe: "dm-bridge-test-nobody-" + Guid.NewGuid().ToString("N"));
        using var user = await SignInAsync(address);
        using var response = await user.SendAsync(HttpMethod.Post, "/api/ipc", IpcMessage.Create(MessageTypes.HostPing, new { }));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("The app on this PC isn't answering. Try again in a moment.", (await user.JsonAsync(response)).GetProperty("error").GetString());

        await using var socket = await ConnectAsync(address, user.Session);
        Assert.Null(await socket.ReadAsync(TimeSpan.FromSeconds(20)));
        Assert.Equal(WebSocketCloseStatus.InternalServerError, socket.Socket.CloseStatus);
    }

    [Fact]
    public async Task TheHostClosingTheConnectionClosesTheBrowsersSocket()
    {
        var address = await StartBridgeAsync();
        using var user = await SignInAsync(address);
        await using var socket = await ConnectAsync(address, user.Session);
        Assert.Equal(MessageTypes.ServerHello, (await socket.ReadAsync())!.Type);
        await socket.SendAsync(IpcMessage.Create(FakeHostPipe.Close, new { }));
        while (await socket.ReadAsync() is not null)
        {
            // the snapshot, then the close
        }

        Assert.Equal(WebSocketCloseStatus.EndpointUnavailable, socket.Socket.CloseStatus);
    }

    [Fact]
    public async Task SigningOutOrTheBridgeStoppingClosesTheSocketWithACloseFrame()
    {
        var address = await StartBridgeAsync();
        using var user = await SignInAsync(address);
        await using (var socket = await ConnectAsync(address, user.Session))
        {
            Assert.Equal(MessageTypes.ServerHello, (await socket.ReadAsync())!.Type);
            using var other = new AuthClient(address) { Session = user.Session };
            using (await other.PostAsync("/api/auth/signout")) { }
            while (await socket.ReadAsync() is not null)
            {
                // the snapshot, then the close
            }

            Assert.Equal(WebSocketCloseStatus.EndpointUnavailable, socket.Socket.CloseStatus);
        }

        using var again = await SignInAsync(address);
        await using var open = await ConnectAsync(address, again.Session);
        Assert.Equal(MessageTypes.ServerHello, (await open.ReadAsync())!.Type);
        var stopping = _bridge!.StopAsync();
        while (await open.ReadAsync() is not null)
        {
            // the snapshot, then the close
        }

        Assert.Equal(WebSocketCloseStatus.EndpointUnavailable, open.Socket.CloseStatus);
        await stopping.WaitAsync(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task APortInUseIsSaidWithItsExitCode()
    {
        await SetPasswordAsync();
        using var taken = new TcpListener(IPAddress.Loopback, 0);
        taken.Start();
        var port = ((IPEndPoint)taken.LocalEndpoint).Port;
        var refused = await Assert.ThrowsAsync<BridgeBindException>(() => BridgeApp.StartAsync(Start(port: port), time: _time));
        Assert.Equal(WebBridgeExit.PortInUse, refused.ExitCode);
        Assert.Equal($"Port {port} is in use by another program. Choose another port.", refused.Message);
    }

    [Fact]
    public async Task TheBridgeProgramReadsItsOptionsFromItsInputAndStopsWhenTheInputCloses()
    {
        await SetPasswordAsync();
        using var taken = new TcpListener(IPAddress.Loopback, 0);
        taken.Start();
        var port = ((IPEndPoint)taken.LocalEndpoint).Port;
        taken.Stop();
        var input = new BlockingInput(System.Text.Json.JsonSerializer.Serialize(Start(port: port), IpcJson.Options));
        var output = new StringWriter();
        var error = new StringWriter();
        var run = BridgeApp.RunAsync(input, output, error);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (!output.ToString().Contains(WebBridgeExit.ReadyLine, StringComparison.Ordinal) && !run.IsCompleted && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        Assert.False(run.IsCompleted, error.ToString());
        using (var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") })
            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/health")).StatusCode);
        input.End();
        Assert.Equal(WebBridgeExit.Stopped, await run.WaitAsync(TimeSpan.FromSeconds(20)));

        var bad = await BridgeApp.RunAsync(new StringReader("{ not json"), new StringWriter(), error = new StringWriter());
        Assert.Equal(WebBridgeExit.Refused, bad);
        Assert.NotEmpty(error.ToString().Trim());
    }

    /// <summary>Standard input as the Host gives it: the options line, then nothing until the Host closes it.</summary>
    private sealed class BlockingInput(string line) : TextReader
    {
        private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _sent;

        public void End() => _ended.TrySetResult();

        public override string? ReadLine()
        {
            if (!_sent)
            {
                _sent = true;
                return line;
            }

            _ended.Task.GetAwaiter().GetResult();
            return null;
        }

        public override async Task<string?> ReadLineAsync()
        {
            if (!_sent)
            {
                _sent = true;
                return line;
            }

            await _ended.Task;
            return null;
        }

        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            if (!_sent)
            {
                _sent = true;
                return line;
            }

            await _ended.Task.WaitAsync(cancellationToken);
            return null;
        }

        public override int Read() => ReadLine() is null ? -1 : '\n';

        public override async Task<string> ReadToEndAsync()
        {
            await _ended.Task;
            return string.Empty;
        }
    }
}
