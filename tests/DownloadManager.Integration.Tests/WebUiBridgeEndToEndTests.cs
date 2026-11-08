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

        // Ticked again and the Host restarted: it starts the bridge by itself, and stops it when it exits.
        await pipe.AskAsync<WebUiStatusDto>(WebUiMessageTypes.Set, new WebUiSetRequest { Enabled = true }, TimeSpan.FromSeconds(60));
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
