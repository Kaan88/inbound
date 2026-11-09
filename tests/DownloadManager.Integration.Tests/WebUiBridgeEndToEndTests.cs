using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using DownloadManager.Contracts;
using DownloadManager.Host;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// D8 end to end: the real Host in a sandbox profile starts the real bridge, which listens on this PC only (the test
/// program has no firewall rule; the rules are only read), signs a browser in with the window's ticket, and carries its
/// WebSocket to the Host in the web role. Turning the box off stops the bridge and ends the browser's socket.
/// </summary>
[Trait("Platform", "Windows")]
public sealed class WebUiBridgeEndToEndTests
{
    private const string NoApi = "https://example.invalid/v1/api/";

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private static async Task<IpcMessage?> ReadAsync(ClientWebSocket socket, TimeSpan? wait = null)
    {
        using var timeout = new CancellationTokenSource(wait ?? TimeSpan.FromSeconds(15));
        var buffer = new byte[256 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, timeout.Token);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) return JsonSerializer.Deserialize<IpcMessage>(message.ToArray(), IpcJson.Options);
        }
    }

    private static async Task<IpcMessage> AskAsync(ClientWebSocket socket, string type, object payload)
    {
        var request = IpcMessage.Create(type, payload);
        await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(request, IpcJson.Options), WebSocketMessageType.Text, true, CancellationToken.None);
        while (await ReadAsync(socket) is { } frame)
        {
            if (frame.ReplyTo == request.Id) return frame;
        }

        throw new InvalidOperationException("The socket closed before the answer.");
    }

    private static async Task<WebUiStatusDto> UntilStateAsync(HostConnection pipe, string state)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            var status = await pipe.AskAsync<WebUiStatusDto>(WebUiMessageTypes.Get, new { });
            if (status.State == state) return status;
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"The web UI stayed {status.State}: {status.Error}");
            await Task.Delay(100);
        }
    }

    private static StringContent Envelope(string type, object payload) =>
        new(JsonSerializer.Serialize(IpcMessage.Create(type, payload), IpcJson.Options));

    private static async Task<IpcMessage> IpcAsync(HttpClient http, string type, object payload, string? bearer = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/ipc") { Content = Envelope(type, payload) };
        if (bearer is not null) request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
        using var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonSerializer.Deserialize<IpcMessage>(await response.Content.ReadAsStringAsync(), IpcJson.Options)!;
    }

    private static async Task<(HttpStatusCode Status, string? Reference)> UploadAsync(HttpClient http, string path, byte[] body, string? fileName = null, string? bearer = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new ByteArrayContent(body) };
        if (fileName is not null) request.Headers.Add("X-File-Name", Uri.EscapeDataString(fileName));
        if (bearer is not null) request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
        using var response = await http.SendAsync(request);
        if (response.StatusCode != HttpStatusCode.OK) return (response.StatusCode, null);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (response.StatusCode, json.RootElement.GetProperty("upload").GetString());
    }

    [Fact]
    public async Task UploadsThroughTheBridgeLandInTheHostsUploadFolderAndTheHostReadsThemByReference()
    {
        // Gap a between the bridge and the Host streams: the bridge serves both uploads into <Host data>\uploads, the
        // Host resolves upload:<id> for the torrent and the container commands, and its hello says "uploads". A paired
        // desktop app's token uploads too (gap b).
        await using var host = await SandboxHost.StartAsync(NoApi);
        await using var pipe = await host.ConnectAsync();
        var port = FreePort();
        Assert.True((await pipe.AskAsync<WebUiStatusDto>(WebUiMessageTypes.SetPassword, new WebUiPasswordRequest { Password = "upload test password" })).HasPassword);
        Assert.Equal(WebUiStates.Running,
            (await pipe.AskAsync<WebUiStatusDto>(WebUiMessageTypes.Set, new WebUiSetRequest { Enabled = true, Port = port }, TimeSpan.FromSeconds(60))).State);
        var ticket = await pipe.AskAsync<WebUiTicketDto>(WebUiMessageTypes.OpenTicket, new { });
        var cookies = new CookieContainer();
        using var http = new HttpClient(new HttpClientHandler { CookieContainer = cookies, AllowAutoRedirect = false }) { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        using (var signedIn = await http.GetAsync(ticket.Url)) Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);

        using (var socket = new ClientWebSocket())
        {
            socket.Options.Cookies = cookies;
            await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/api/ws"), CancellationToken.None);
            var hello = (await ReadAsync(socket))!.GetPayload<HostHelloDto>();
            Assert.Contains("remote", hello.Capabilities);
            Assert.Contains("uploads", hello.Capabilities);
        }

        var uploads = Path.Combine(host.DataDirectory, "uploads");
        var torrent = "d4:infod6:lengthi4e4:name5:a.bin12:piece lengthi16384e6:pieces20:aaaaaaaaaaaaaaaaaaaaee"u8.ToArray();
        var (torrentStatus, torrentReference) = await UploadAsync(http, "/api/upload/torrent", torrent);
        Assert.Equal(HttpStatusCode.OK, torrentStatus);
        Assert.True(File.Exists(Path.Combine(uploads, torrentReference!["upload:".Length..] + ".torrent")));
        // The test Host has no provider key, so a .torrent file can't be used yet; but it is read as that file. A reference
        // with no file behind it is told apart.
        const string Gone = "The uploaded file is no longer there. Upload it again.";
        var classified = (await IpcAsync(http, MessageTypes.RemoteClassify, new RemoteClassifyRequest { Text = torrentReference })).GetPayload<RemoteClassifyResult>();
        Assert.NotEqual(Gone, classified.Reason);
        Assert.Equal(Gone, (await IpcAsync(http, MessageTypes.RemoteClassify, new RemoteClassifyRequest { Text = "upload:" + new string('0', 32) }))
            .GetPayload<RemoteClassifyResult>().Reason);

        var (containerStatus, containerReference) = await UploadAsync(http, "/api/upload/container", "not really a container"u8.ToArray(), "links.rsdf");
        Assert.Equal(HttpStatusCode.OK, containerStatus);
        Assert.True(File.Exists(Path.Combine(uploads, containerReference!["upload:".Length..] + ".rsdf")));
        var preview = (await IpcAsync(http, MessageTypes.RemoteContainerPreview, new RemoteContainerPreviewRequest { Path = containerReference, FileName = "links.rsdf" }))
            .GetPayload<RemoteContainerPreviewResult>();
        Assert.Equal("rsdf", preview.Format);
        Assert.DoesNotContain("outside", preview.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(Gone, preview.Error);
        // A reference whose file is gone says so; one never made too.
        var gone = (await IpcAsync(http, MessageTypes.RemoteContainerPreview, new RemoteContainerPreviewRequest { Path = "upload:" + new string('0', 32) }))
            .GetPayload<RemoteContainerPreviewResult>();
        Assert.Equal(Gone, gone.Error);

        // A desktop app paired with the password uploads with its token, and nothing else of its sign-in is needed.
        using var anonymous = new HttpClient(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false }) { BaseAddress = http.BaseAddress };
        using var pairing = await anonymous.PostAsync("/api/pair", new StringContent(
            JsonSerializer.Serialize(new { password = "upload test password", name = "Desk PC", role = "remoteClient" }), System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, pairing.StatusCode);
        string token;
        using (var paired = JsonDocument.Parse(await pairing.Content.ReadAsStringAsync())) token = paired.RootElement.GetProperty("token").GetString()!;
        Assert.Equal(HttpStatusCode.Unauthorized, (await UploadAsync(anonymous, "/api/upload/torrent", torrent)).Status);
        var (byToken, tokenReference) = await UploadAsync(anonymous, "/api/upload/torrent", torrent, bearer: token);
        Assert.Equal(HttpStatusCode.OK, byToken);
        Assert.NotEqual(Gone, (await IpcAsync(anonymous, MessageTypes.RemoteClassify, new RemoteClassifyRequest { Text = tokenReference! }, token))
            .GetPayload<RemoteClassifyResult>().Reason);
        Assert.Equal(HttpStatusCode.OK, (await UploadAsync(anonymous, "/api/upload/container", "x"u8.ToArray(), "links.ccf", token)).Status);

        await pipe.AskAsync<WebUiStatusDto>(WebUiMessageTypes.Set, new WebUiSetRequest { Enabled = false }, TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task TheWindowTurnsTheWebUiOnSignsABrowserInWithATicketAndOffAgain()
    {
        await using var host = await SandboxHost.StartAsync(NoApi);
        await using var pipe = await host.ConnectAsync();
        var port = FreePort();

        var off = await pipe.AskAsync<WebUiStatusDto>(WebUiMessageTypes.Get, new { });
        Assert.False(off.Enabled);
        Assert.Equal(WebUiStates.Off, off.State);
        Assert.False(off.HasPassword);
        Assert.Equal(Path.GetFullPath(SandboxHost.BridgeExecutable), off.BridgePath);
        Assert.Equal("Set a password first. Other devices sign in with it.",
            (await pipe.AskAsync<WebUiStatusDto>(WebUiMessageTypes.Set, new WebUiSetRequest { Enabled = true })).Error);
        Assert.DoesNotContain(host.LogLines(), line => line.Contains("Web bridge started", StringComparison.Ordinal));

        Assert.True((await pipe.AskAsync<WebUiStatusDto>(WebUiMessageTypes.SetPassword, new WebUiPasswordRequest { Password = "end to end password" })).HasPassword);
        var on = await pipe.AskAsync<WebUiStatusDto>(WebUiMessageTypes.Set, new WebUiSetRequest { Enabled = true, Port = port }, TimeSpan.FromSeconds(60));
        Assert.Null(on.Error);
        Assert.Equal(WebUiStates.Running, on.State);
        // No rule for the test bridge; a low-integrity test Host is refused the read and says unknown. Either way: this PC only.
        Assert.Equal(ProcessIntegrity.IsBelowMedium() ? WebUiFirewallStates.Unknown : WebUiFirewallStates.Missing, on.Firewall);
        Assert.True(on.LocalOnly);
        Assert.Empty(on.Addresses);
        Assert.True(File.Exists(Path.Combine(host.DataDirectory, "webui.json")));
        Assert.Contains(pipe.Events, frame => frame.Type == WebUiMessageTypes.Changed);

        var ticket = await pipe.AskAsync<WebUiTicketDto>(WebUiMessageTypes.OpenTicket, new { });
        Assert.Null(ticket.Error);
        var cookies = new CookieContainer();
        using var http = new HttpClient(new HttpClientHandler { CookieContainer = cookies, AllowAutoRedirect = false }) { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        using (var signedIn = await http.GetAsync(ticket.Url))
        {
            Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
            Assert.Equal("/", signedIn.Headers.Location?.OriginalString);
        }

        Assert.NotEmpty(cookies.GetCookies(http.BaseAddress!));
        // Once only.
        using (var again = new HttpClient(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false }))
        using (var used = await again.GetAsync(ticket.Url))
        {
            Assert.Equal("/signin/", used.Headers.Location?.OriginalString);
            Assert.False(used.Headers.Contains("Set-Cookie"));
        }

        // No setup page on Windows.
        using (var setup = await http.PostAsync("/api/auth/setup", new StringContent("{\"password\":\"someone else's\"}")))
            Assert.Equal(HttpStatusCode.NotFound, setup.StatusCode);

        using var socket = new ClientWebSocket();
        socket.Options.Cookies = cookies;
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/api/ws"), CancellationToken.None);
        var hello = (await ReadAsync(socket))!;
        Assert.Equal(MessageTypes.ServerHello, hello.Type);
        var helloDto = hello.GetPayload<HostHelloDto>();
        Assert.Equal(HostKinds.Windows, helloDto.HostKind);
        Assert.Contains("folders", helloDto.Capabilities);
        Assert.DoesNotContain("openUi", helloDto.Capabilities);
        Assert.Equal(MessageTypes.JobsSnapshot, (await ReadAsync(socket))!.Type);

        // A browser can't touch the web UI's own settings, even signed in.
        var refused = await AskAsync(socket, WebUiMessageTypes.Set, new WebUiSetRequest { Enabled = false });
        Assert.Equal("The web UI can only be set up in the app on this PC.", refused.GetPayload<HostActionResult>().Message);
        var browse = (await AskAsync(socket, MessageTypes.FoldersBrowse, new FolderBrowseRequest())).GetPayload<FolderBrowseResult>();
        Assert.Contains(browse.Roots, root => string.Equals(Path.TrimEndingDirectorySeparator(root), Path.TrimEndingDirectorySeparator(host.Destination), StringComparison.OrdinalIgnoreCase));

        using (var ping = await http.PostAsync("/api/ipc", new StringContent(JsonSerializer.Serialize(IpcMessage.Create(MessageTypes.HostPing, new { }), IpcJson.Options))))
        {
            Assert.Equal(HttpStatusCode.OK, ping.StatusCode);
            Assert.Equal("pong", JsonSerializer.Deserialize<IpcMessage>(await ping.Content.ReadAsStringAsync(), IpcJson.Options)!.GetPayload<HostActionResult>().Message);
        }

        // Off: the bridge stops, the browser's socket ends with a close frame, the port is free. The browser keeps
        // reading meanwhile, as a page does (a socket nobody reads loses the close frame to the connection's reset).
        var closed = Task.Run(async () =>
        {
            while (await ReadAsync(socket, TimeSpan.FromSeconds(30)) is not null)
            {
                // events until it closes
            }
        });
        var stopped = await pipe.AskAsync<WebUiStatusDto>(WebUiMessageTypes.Set, new WebUiSetRequest { Enabled = false }, TimeSpan.FromSeconds(60));
        Assert.Equal(WebUiStates.Off, stopped.State);
        await closed.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(WebSocketCloseStatus.EndpointUnavailable, socket.CloseStatus);

        Assert.Empty(TcpListenerOwner.ListenersOn(port, [IPAddress.Loopback, IPAddress.Any]));

        // Ticked again: the browser signed in before must sign in again (turning it off ended every sign-in).
        Assert.Equal(WebUiStates.Running,
            (await pipe.AskAsync<WebUiStatusDto>(WebUiMessageTypes.Set, new WebUiSetRequest { Enabled = true }, TimeSpan.FromSeconds(60))).State);
        Assert.NotEmpty(cookies.GetCookies(http.BaseAddress!));
        using (var stale = await http.PostAsync("/api/ipc", new StringContent(JsonSerializer.Serialize(IpcMessage.Create(MessageTypes.HostPing, new { }), IpcJson.Options))))
            Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);

        // And the Host restarted: it starts the bridge by itself, and stops it when it exits.
        await pipe.DisposeAsync();
        await host.StopAsync();
        Assert.Empty(TcpListenerOwner.ListenersOn(port, [IPAddress.Loopback, IPAddress.Any]));
        await host.RestartAsync();
        await using var later = await host.ConnectAsync();
        await UntilStateAsync(later, WebUiStates.Running);
        Assert.NotEmpty(TcpListenerOwner.ListenersOn(port, [IPAddress.Loopback]));
        await later.DisposeAsync();
        await host.StopAsync();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (TcpListenerOwner.ListenersOn(port, [IPAddress.Loopback, IPAddress.Any]).Count > 0 && DateTime.UtcNow < deadline) await Task.Delay(100);
        Assert.Empty(TcpListenerOwner.ListenersOn(port, [IPAddress.Loopback, IPAddress.Any]));
    }
}
