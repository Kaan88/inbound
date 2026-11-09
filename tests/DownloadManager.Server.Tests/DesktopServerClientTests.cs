using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using DownloadManager.Contracts;
using DownloadManager.Server.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace DownloadManager.Server.Tests;

/// <summary>
/// Design D9, section 5: the desktop app's own client for another server (<see cref="RemoteServerHttp"/>,
/// <see cref="WebSocketIpcClient"/>, <see cref="ServerConnector"/>), against a real test server and, for what a real
/// server never does, an in-process fake: pairing with the password, the hello, framing, and a refused token saying to
/// pair again.
/// </summary>
public sealed class DesktopServerClientTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private static ServerTarget Target(Uri address, string token, bool plainHttpConfirmed = false) =>
        ServerTarget.Remote(Guid.NewGuid().ToString("N"), "Test server", address, token, plainHttpConfirmed);

    private static async Task<(ServerHarness Server, ServerTarget Target)> PairedAsync()
    {
        var server = await AuthServer.StartAsync();
        using (await AuthServer.SetUpAsync(server)) { }
        var paired = await RemoteServerHttp.PairAsync(server.Base, AuthServer.Password, "Desk PC", plainHttpConfirmed: false);
        Assert.True(paired.Ok, paired.Error);
        return (server, Target(server.Base, paired.Token!));
    }

    [Fact]
    public async Task PairingWithThePasswordGivesARemoteClientTokenAndTheServersKind()
    {
        await using var server = await AuthServer.StartAsync();
        using (await AuthServer.SetUpAsync(server)) { }

        var paired = await RemoteServerHttp.PairAsync(server.Base, AuthServer.Password, "Desk PC", plainHttpConfirmed: false);

        Assert.True(paired.Ok, paired.Error);
        Assert.StartsWith(Pairing.TokenPrefix, paired.Token);
        Assert.False(string.IsNullOrEmpty(paired.TokenId));
        Assert.Equal(HostKinds.Server, paired.HostKind);
        Assert.DoesNotContain(paired.Token!, paired.ToString());
    }

    [Fact]
    public async Task AWrongPasswordSavesNothingAndSaysWhy()
    {
        await using var server = await AuthServer.StartAsync();
        using (await AuthServer.SetUpAsync(server)) { }

        var paired = await RemoteServerHttp.PairAsync(server.Base, "not the password", "Desk PC", plainHttpConfirmed: false);

        Assert.False(paired.Ok);
        Assert.Null(paired.Token);
        Assert.False(string.IsNullOrWhiteSpace(paired.Error));
        Assert.DoesNotContain("not the password", paired.Error);
    }

    [Fact]
    public async Task TheSocketSendsTheHelloFirstAndGetsTheSnapshotWhenItAsks()
    {
        var (server, target) = await PairedAsync();
        await using var _ = server;

        await using var client = await WebSocketIpcClient.ConnectAsync(target, new ClientHelloDto { Snapshot = true, Events = true });
        Assert.True(client.SnapshotFollowsHello);
        var hello = await ReadAsync(client);
        Assert.Equal(MessageTypes.ServerHello, hello.Type);
        Assert.Equal(HostKinds.Server, hello.GetPayload<HostHelloDto>().HostKind);
        Assert.Equal(MessageTypes.JobsSnapshot, (await ReadAsync(client)).Type);

        var ping = IpcMessage.Create(MessageTypes.HostPing, new { });
        await client.SendAsync(ping);
        var reply = await ReadUntilAsync(client, message => message.ReplyTo == ping.Id);
        Assert.True(reply.GetPayload<HostActionResult>().Ok);
    }

    [Fact]
    public async Task ASocketThatAskedForNoSnapshotGetsTheHelloAndThenAnswers()
    {
        var (server, target) = await PairedAsync();
        await using var _ = server;

        await using var client = (IIpcConnection)await ServerConnector.ConnectAsync(target, new ClientHelloDto { Snapshot = false, Events = true });
        Assert.False(client.SnapshotFollowsHello);
        Assert.Equal(MessageTypes.ServerHello, (await ReadAsync(client)).Type);

        var ping = IpcMessage.Create(MessageTypes.HostPing, new { });
        await client.SendAsync(ping);
        var next = await ReadAsync(client);
        Assert.Equal(ping.Id, next.ReplyTo);
    }

    [Fact]
    public async Task AShortCallIsOnePostWithTheToken()
    {
        var (server, target) = await PairedAsync();
        await using var _ = server;

        var reply = await ServerConnector.SendOnceAsync(target, IpcMessage.Create(MessageTypes.SettingsGet, new { }), Patience);

        Assert.Equal(MessageTypes.SettingsResult, reply.Type);
        Assert.Equal(Path.GetFullPath(server.Root), reply.GetPayload<SettingsResult>().Settings.DefaultDownloadDirectory);
    }

    [Fact]
    public async Task AfterUnpairingTheTokenIsRefusedAndEveryCallSaysToPairAgain()
    {
        var (server, target) = await PairedAsync();
        await using var _ = server;

        Assert.True(await RemoteServerHttp.UnpairAsync(target));

        await Assert.ThrowsAsync<RemotePairingEndedException>(() => WebSocketIpcClient.ConnectAsync(target, new ClientHelloDto()));
        await Assert.ThrowsAsync<RemotePairingEndedException>(() => RemoteServerHttp.SendIpcAsync(target, IpcMessage.Create(MessageTypes.HostPing, new { })));
        // Removing it again from this PC still works: the server has already forgotten it.
        Assert.True(await RemoteServerHttp.UnpairAsync(target));
    }

    [Fact]
    public async Task AnInventedTokenSaysToPairAgain()
    {
        await using var server = await AuthServer.StartAsync();
        using (await AuthServer.SetUpAsync(server)) { }
        var target = Target(server.Base, Pairing.TokenPrefix + "invented-for-this-test-0000000000000000000");

        var refused = await Assert.ThrowsAsync<RemotePairingEndedException>(() => WebSocketIpcClient.ConnectAsync(target, new ClientHelloDto()));
        Assert.Contains("Pair it again", refused.Message);
    }

    [Fact]
    public async Task NoTokenOrPasswordGoesToPlainHttpElsewhereUntilTheUserSaysSo()
    {
        // A documentation address (RFC 5737): nothing is listening, and nothing is sent.
        var address = new Uri("http://192.0.2.10:8080/");
        var target = Target(address, Pairing.TokenPrefix + "invented");

        await Assert.ThrowsAsync<PlainHttpNotConfirmedException>(() => WebSocketIpcClient.ConnectAsync(target, new ClientHelloDto(), TimeSpan.FromSeconds(1)));
        await Assert.ThrowsAsync<PlainHttpNotConfirmedException>(() => RemoteServerHttp.SendIpcAsync(target, IpcMessage.Create(MessageTypes.HostPing, new { })));
        var paired = await RemoteServerHttp.PairAsync(address, "invented", "Desk PC", plainHttpConfirmed: false);
        Assert.False(paired.Ok);
        Assert.Equal(new PlainHttpNotConfirmedException().Message, paired.Error);
        var (reference, error) = await RemoteServerHttp.UploadTorrentAsync(target, [1, 2, 3]);
        Assert.Null(reference);
        Assert.Equal(new PlainHttpNotConfirmedException().Message, error);
    }

    [Fact]
    public async Task APairedAppUploadsATorrentAndAContainerWithItsTokenAndTheServerReadsThemByReference()
    {
        var (server, target) = await PairedAsync();
        await using var _ = server;
        var uploads = Path.Combine(server.Data, TorrentUploads.FolderName);

        var (torrent, torrentError) = await RemoteServerHttp.UploadTorrentAsync(target, Encoding.ASCII.GetBytes("d4:infod4:name1:aee"));
        Assert.Null(torrentError);
        Assert.Matches("^upload:[0-9a-f]{32}$", torrent);
        Assert.True(File.Exists(Path.Combine(uploads, torrent![TorrentUploads.ReferencePrefix.Length..] + ".torrent")));

        var (container, containerError) = await RemoteServerHttp.UploadContainerAsync(target, "not really a container"u8.ToArray(), "My links ü.RSDF");
        Assert.Null(containerError);
        Assert.True(File.Exists(Path.Combine(uploads, container![TorrentUploads.ReferencePrefix.Length..] + ".rsdf")));
        var reply = await RemoteServerHttp.SendIpcAsync(target,
            IpcMessage.Create(MessageTypes.RemoteContainerPreview, new RemoteContainerPreviewRequest { Path = container, FileName = "My links.rsdf" }));
        var preview = reply.GetPayload<RemoteContainerPreviewResult>();
        Assert.Equal("rsdf", preview.Format);
        Assert.DoesNotContain("outside", preview.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        // The server's own rules still hold for a token: only container kinds, and a name it can tell the kind by.
        var (wrong, wrongError) = await RemoteServerHttp.UploadContainerAsync(target, [1, 2, 3], "links.txt");
        Assert.Null(wrong);
        Assert.Equal("Only .rsdf, .ccf and .dlc files can be imported.", wrongError);
    }

    [Fact]
    public async Task AnUploadWithARemovedTokenSaysPairAgainAndAnExtensionsTokenUploadsNothing()
    {
        var (server, target) = await PairedAsync();
        await using var _ = server;
        var uploads = Path.Combine(server.Data, TorrentUploads.FolderName);

        // An invented token of ours: refused at the gate, and the app hears to pair again (not "paste the magnet link").
        var invented = ServerTarget.Remote("x", "Test server", server.Base, Pairing.TokenPrefix + new string('b', 43), plainHttpConfirmed: false);
        var (none, error) = await RemoteServerHttp.UploadTorrentAsync(invented, [1, 2, 3]);
        Assert.Null(none);
        Assert.Equal(new RemotePairingEndedException().Message, error);

        // The extension's token keeps to /api/ipc and its own unpair, as before: no upload of either kind.
        var (extension, _) = server.App.Auth.Pairing.Issue(TokenRoles.Extension, "Chrome");
        using var browser = new AuthClient(server.Base) { Origin = PairingTests.ChromeOrigin };
        foreach (var path in new[] { ServerAuth.TorrentUploadPath, ServerAuth.ContainerUploadPath })
        {
            using var refused = await browser.SendAsync(HttpMethod.Post, path, null, request =>
            {
                request.Content = new ByteArrayContent([1, 2, 3]);
                request.Headers.Add(TorrentUploads.FileNameHeader, "links.dlc");
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", extension);
            });
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }

        Assert.Empty(Directory.Exists(uploads) ? Directory.GetFiles(uploads) : []);
        Assert.Null((await RemoteServerHttp.UploadTorrentAsync(target, [1, 2, 3])).Error);
    }

    [Fact]
    public async Task ABrowserBehindAProxyStillUploadsWithItsCookieWhateverAuthorizationTheProxySends()
    {
        await using var server = await AuthServer.StartAsync();
        using var client = await AuthServer.SetUpAsync(server);

        using var uploaded = await client.SendAsync(HttpMethod.Post, ServerAuth.TorrentUploadPath, null, request =>
        {
            request.Content = new ByteArrayContent([1, 2, 3]);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", "cHJveHk6c2VjcmV0");
        });
        Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
    }

    [Fact]
    public async Task AMessageInSeveralFramesIsOneEnvelopeAndTheTokenGoesAsABearer()
    {
        string? authorization = null, query = null, firstType = null;
        await using var fake = await FakeServer.StartAsync(async (context, socket) =>
        {
            authorization = context.Request.Headers.Authorization.ToString();
            query = context.Request.QueryString.Value;
            firstType = (await ReceiveAsync(socket))?.Type;
            var hello = JsonSerializer.SerializeToUtf8Bytes(IpcMessage.Create(MessageTypes.ServerHello, new HostHelloDto { ProtocolVersion = Protocol.Version }), IpcJson.Options);
            var third = hello.Length / 3;
            await socket.SendAsync(hello.AsMemory(0, third), WebSocketMessageType.Text, endOfMessage: false, default);
            await socket.SendAsync(hello.AsMemory(third, third), WebSocketMessageType.Text, endOfMessage: false, default);
            await socket.SendAsync(hello.AsMemory(2 * third), WebSocketMessageType.Text, endOfMessage: true, default);
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, default);
        });
        var target = Target(fake.Address, "invented-bearer-token");

        await using var client = await WebSocketIpcClient.ConnectAsync(target, new ClientHelloDto { Snapshot = false });
        Assert.Equal(MessageTypes.ServerHello, (await ReadAsync(client)).Type);
        Assert.Null(await client.ReadAsync(new CancellationTokenSource(Patience).Token));

        Assert.Equal("Bearer invented-bearer-token", authorization);
        Assert.Equal("?hello=1", query);
        Assert.Equal(MessageTypes.ClientHello, firstType);
    }

    [Fact]
    public async Task ABinaryOrOversizedMessageIsRefused()
    {
        await using var binary = await FakeServer.StartAsync(async (_, socket) =>
        {
            await ReceiveAsync(socket);
            await socket.SendAsync(new byte[] { 1, 2, 3 }, WebSocketMessageType.Binary, endOfMessage: true, default);
            await ReceiveAsync(socket);
        });
        await using (var client = await WebSocketIpcClient.ConnectAsync(Target(binary.Address, "invented"), new ClientHelloDto()))
            await Assert.ThrowsAsync<InvalidDataException>(() => client.ReadAsync(new CancellationTokenSource(Patience).Token));

        await using var large = await FakeServer.StartAsync(async (_, socket) =>
        {
            await ReceiveAsync(socket);
            var chunk = new byte[1024 * 1024];
            Array.Fill(chunk, (byte)' ');
            for (var i = 0; i < Protocol.MaxFrameBytes / chunk.Length + 1; i++)
                await socket.SendAsync(chunk, WebSocketMessageType.Text, endOfMessage: false, default);
            await socket.SendAsync(chunk, WebSocketMessageType.Text, endOfMessage: true, default);
            await ReceiveAsync(socket);
        });
        await using (var client = await WebSocketIpcClient.ConnectAsync(Target(large.Address, "invented"), new ClientHelloDto()))
            await Assert.ThrowsAsync<InvalidDataException>(() => client.ReadAsync(new CancellationTokenSource(Patience).Token));
    }

    [Fact]
    public async Task ARefusedUpgradeIsToldApartFromOtherRefusals()
    {
        await using var unauthorized = await FakeServer.StartAsync(status: HttpStatusCode.Unauthorized);
        await Assert.ThrowsAsync<RemotePairingEndedException>(() => WebSocketIpcClient.ConnectAsync(Target(unauthorized.Address, "invented"), new ClientHelloDto()));

        await using var forbidden = await FakeServer.StartAsync(status: HttpStatusCode.Forbidden);
        var refused = await Assert.ThrowsAsync<RemoteServerException>(() => WebSocketIpcClient.ConnectAsync(Target(forbidden.Address, "invented"), new ClientHelloDto()));
        Assert.IsNotType<RemotePairingEndedException>(refused);
        Assert.DoesNotContain("invented", refused.Message);
    }

    [Fact]
    public async Task ARedirectIsNeverFollowedSoTheTokenGoesNowhereElse()
    {
        var elsewhere = new List<string>();
        await using var other = await FakeHttp.StartAsync(context =>
        {
            lock (elsewhere) elsewhere.Add($"{context.Request.Path} {context.Request.Headers.Authorization}");
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        });
        await using var redirecting = await FakeHttp.StartAsync(context =>
        {
            context.Response.StatusCode = StatusCodes.Status302Found;
            context.Response.Headers.Location = new Uri(other.Address, context.Request.Path + context.Request.QueryString).ToString();
            return Task.CompletedTask;
        });
        var target = Target(redirecting.Address, "invented-redirect-token");

        var upgrade = await Assert.ThrowsAnyAsync<RemoteServerException>(() => WebSocketIpcClient.ConnectAsync(target, new ClientHelloDto(), TimeSpan.FromSeconds(5)));
        Assert.IsNotType<RemotePairingEndedException>(upgrade);
        await Assert.ThrowsAnyAsync<RemoteServerException>(() => RemoteServerHttp.SendIpcAsync(target, IpcMessage.Create(MessageTypes.HostPing, new { })));
        Assert.False(await RemoteServerHttp.UnpairAsync(target));
        var paired = await RemoteServerHttp.PairAsync(redirecting.Address, "invented-password", "Desk PC", plainHttpConfirmed: false);
        Assert.False(paired.Ok);

        lock (elsewhere) Assert.Empty(elsewhere);
    }

    [Fact]
    public async Task ATokenOfAnotherKindFromAnOlderServerIsForgottenThere()
    {
        string? forgotten = null;
        await using var older = await FakeHttp.StartAsync(async context =>
        {
            if (context.Request.Path == "/api/pair" && HttpMethods.IsPost(context.Request.Method))
            {
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync("""{ "id": "t1", "token": "ibt_invented-extension-token", "role": "extension" }""");
                return;
            }

            if (context.Request.Path == "/api/pair/self" && HttpMethods.IsDelete(context.Request.Method))
            {
                forgotten = context.Request.Headers.Authorization.ToString();
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return;
            }

            context.Response.StatusCode = StatusCodes.Status404NotFound;
        });

        var paired = await RemoteServerHttp.PairAsync(older.Address, "invented-password", "Desk PC", plainHttpConfirmed: false);

        Assert.False(paired.Ok);
        Assert.Null(paired.Token);
        Assert.Equal("Bearer ibt_invented-extension-token", forgotten);
    }

    private static async Task<IpcMessage> ReadAsync(IIpcConnection client)
    {
        using var deadline = new CancellationTokenSource(Patience);
        return await client.ReadAsync(deadline.Token) ?? throw new Xunit.Sdk.XunitException("The server closed the socket.");
    }

    private static async Task<IpcMessage> ReadUntilAsync(IIpcConnection client, Func<IpcMessage, bool> match)
    {
        while (true)
        {
            var message = await ReadAsync(client);
            if (match(message)) return message;
        }
    }

    private static async Task<IpcMessage?> ReceiveAsync(WebSocket socket)
    {
        var buffer = new byte[64 * 1024];
        using var memory = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, default);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            memory.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) return JsonSerializer.Deserialize<IpcMessage>(memory.ToArray(), IpcJson.Options);
        }
    }

    /// <summary>A plain HTTP endpoint on loopback that answers every request as the test says.</summary>
    private sealed class FakeHttp(WebApplication app, Uri address) : IAsyncDisposable
    {
        public Uri Address { get; } = address;

        public static async Task<FakeHttp> StartAsync(RequestDelegate answer)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            var app = builder.Build();
            app.Run(answer);
            await app.StartAsync();
            return new FakeHttp(app, new Uri(app.Urls.First()));
        }

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    /// <summary>A WebSocket endpoint on loopback that does what the test says, or refuses the upgrade with a status.</summary>
    private sealed class FakeServer(WebApplication app, Uri address) : IAsyncDisposable
    {
        public Uri Address { get; } = address;

        public static async Task<FakeServer> StartAsync(Func<HttpContext, WebSocket, Task>? talk = null, HttpStatusCode? status = null)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            var app = builder.Build();
            app.UseWebSockets();
            app.Run(async context =>
            {
                if (status is { } refused || !context.WebSockets.IsWebSocketRequest)
                {
                    context.Response.StatusCode = (int)(status ?? HttpStatusCode.BadRequest);
                    return;
                }

                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                try
                {
                    await talk!(context, socket);
                }
                catch (WebSocketException)
                {
                    // The client went away first.
                }
            });
            await app.StartAsync();
            return new FakeServer(app, new Uri(app.Urls.First()));
        }

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
