using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DownloadManager.Contracts;
using DownloadManager.Server.Auth;

namespace DownloadManager.Server.Tests;

/// <summary>
/// Design D9, section 4: token roles, a desktop app pairing with the password (<see cref="TokenRoles.RemoteClient"/>),
/// its token on <c>/api/ws</c> and <c>/api/ipc</c>, revoking closing its sockets, the host-name rule for apps that
/// aren't browsers, the WebSocket's optional <c>client.hello</c>, and <c>hostKind</c>.
/// </summary>
public sealed class RemoteClientTests
{
    private static async Task<(HttpStatusCode Status, JsonElement Body)> PairWithPasswordAsync(ServerHarness server, string? password,
        string name = "Desk PC", object? extra = null, string? origin = null)
    {
        using var client = new AuthClient(server.Base) { Origin = origin };
        using var response = await client.PostAsync("/api/pair", extra ?? new { password, name, role = TokenRoles.RemoteClient });
        return (response.StatusCode, await client.JsonAsync(response));
    }

    internal static async Task<string> PairRemoteClientAsync(ServerHarness server, string name = "Desk PC")
    {
        var (status, body) = await PairWithPasswordAsync(server, AuthServer.Password, name);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(TokenRoles.RemoteClient, body.GetProperty("role").GetString());
        var token = body.GetProperty("token").GetString()!;
        Assert.StartsWith(Pairing.TokenPrefix, token);
        return token;
    }

    /// <summary>
    /// A WebSocket to <paramref name="path"/> as an app that isn't a browser: a bearer token and no <c>Origin</c> unless
    /// given. <paramref name="hostName"/> is the name in the address (and the Host header); the connection still goes to
    /// this machine, as a name in the hosts file would.
    /// </summary>
    internal static async Task<WsClient> ConnectAsync(ServerHarness server, string? bearer, string path = "/api/ws",
        string? origin = null, string? hostName = null, string? sessionCookie = null)
    {
        var socket = new ClientWebSocket();
        if (bearer is not null) socket.Options.SetRequestHeader("Authorization", "Bearer " + bearer);
        if (origin is not null) socket.Options.SetRequestHeader("Origin", origin);
        // As a header: with an invoker given, the socket's own cookie container may not be set.
        if (sessionCookie is not null) socket.Options.SetRequestHeader("Cookie", $"{ServerAuth.CookieName}={sessionCookie}");

        var port = server.Base.Port;
        var address = new Uri($"ws://{hostName ?? server.Base.Host}:{port}{path}");
        using var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, cancellationToken) =>
            {
                var tcp = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await tcp.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), cancellationToken);
                    return new NetworkStream(tcp, ownsSocket: true);
                }
                catch
                {
                    tcp.Dispose();
                    throw;
                }
            },
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await socket.ConnectAsync(address, new HttpMessageInvoker(handler, disposeHandler: false), timeout.Token);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        return new WsClient(socket);
    }

    /// <summary>The status a refused WebSocket upgrade was answered with.</summary>
    private static async Task<HttpStatusCode?> RefusedUpgradeAsync(Func<Task<WsClient>> connect)
    {
        try
        {
            await using var opened = await connect();
            return null;
        }
        catch (WebSocketException ex)
        {
            // "The server returned status code '403' when status code '101' was expected."
            foreach (var code in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.BadRequest })
                if (ex.Message.Contains($"'{(int)code}'", StringComparison.Ordinal)) return code;
            throw;
        }
    }

    /// <summary>Reads until the server closes the socket; false when it is still open after <paramref name="within"/>.</summary>
    private static async Task<bool> ClosesWithinAsync(WsClient client, TimeSpan within)
    {
        var deadline = DateTime.UtcNow + within;
        try
        {
            while (DateTime.UtcNow < deadline)
                if (await client.ReadAsync(deadline - DateTime.UtcNow) is null) return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (WebSocketException)
        {
            return true;
        }

        return false;
    }

    private static async Task<string> TokenIdAsync(AuthClient user, string name)
    {
        using var list = await user.GetAsync("/api/auth/tokens");
        return (await user.JsonAsync(list)).GetProperty("tokens").EnumerateArray()
            .Single(token => token.GetProperty("name").GetString() == name).GetProperty("id").GetString()!;
    }

    // ---- 1. Roles in auth.json --------------------------------------------------------------------------------

    [Fact]
    public async Task AnAuthFileFromBeforeRolesReadsAsExtensionTokensAndAnUnknownRoleOpensNothing()
    {
        var work = ServerHarness.NewWork();
        var data = Path.Combine(work, "data");
        Directory.CreateDirectory(data);
        const string old = "ibt_written-by-0.6.2-without-a-role";
        const string newer = "ibt_written-by-a-later-version";
        static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        // As 0.6.2 wrote it: no role field at all.
        await File.WriteAllTextAsync(Path.Combine(data, AuthStore.FileName), $$"""
            {
              "version": 1,
              "tokens": [
                { "id": "0a0b0c0d0e0f", "name": "Old extension", "hash": "{{Hash(old)}}", "createdAt": "2026-11-01T10:00:00+00:00" },
                { "id": "1a1b1c1d1e1f", "name": "From the future", "hash": "{{Hash(newer)}}", "createdAt": "2026-11-02T10:00:00+00:00", "role": "somethingNew" }
              ]
            }
            """);

        var pairing = new Pairing(AuthStore.Open(data), TimeProvider.System);
        var found = pairing.Authenticate(old);
        Assert.NotNull(found);
        Assert.Equal(TokenRoles.Extension, found!.Role);
        Assert.Null(pairing.Authenticate(newer));
        // Still listed (and so removable), under the role it has.
        Assert.Equal(new[] { TokenRoles.Extension, "somethingNew" }, pairing.List().Select(token => token.Role));

        // Saving again keeps the old record without a role, so the file stays as an older version reads it.
        pairing.Issue(TokenRoles.RemoteClient, "Desk PC");
        var saved = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(data, AuthStore.FileName))).RootElement.GetProperty("tokens");
        Assert.False(saved[0].TryGetProperty("role", out _));
        Assert.Equal(TokenRoles.RemoteClient, saved[2].GetProperty("role").GetString());
        Directory.Delete(work, recursive: true);
    }

    [Fact]
    public async Task AnExtensionTokenFromAnOldFileKeepsTheExtensionsRightsOnARealServer()
    {
        var work = ServerHarness.NewWork();
        string token;
        await using (var server = await AuthServer.StartAsync(work: work))
        {
            using var user = await AuthServer.SetUpAsync(server);
            token = await PairingTests.PairAsync(server, await PairingTests.NewCodeAsync(user));
        }

        // The extension's record has no role in the file, as before roles existed.
        var file = await File.ReadAllTextAsync(Path.Combine(work, "data", AuthStore.FileName));
        Assert.DoesNotContain("\"role\"", file);

        await using (var server = await AuthServer.StartAsync(work: work))
        {
            using var extension = new AuthClient(server.Base) { Origin = PairingTests.ChromeOrigin };
            Assert.Equal(HttpStatusCode.OK, (await extension.IpcAsync(MessageTypes.HostPing, new { }, token)).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await extension.IpcAsync(MessageTypes.SettingsGet, new { }, token)).Status);
            Assert.Equal(HttpStatusCode.Forbidden, await RefusedUpgradeAsync(() => ConnectAsync(server, token)));
        }

        Directory.Delete(work, recursive: true);
    }

    // ---- 2. Pairing with the password -------------------------------------------------------------------------

    [Fact]
    public async Task ThePasswordPairsADesktopAppAndItsTokenHasASignedInBrowsersRights()
    {
        await using var server = await AuthServer.StartAsync();
        using var user = await AuthServer.SetUpAsync(server);
        var token = await PairRemoteClientAsync(server);
        // The password is not kept anywhere new, and only the token's hash is.
        var saved = await File.ReadAllTextAsync(Path.Combine(server.Data, AuthStore.FileName));
        Assert.DoesNotContain(token, saved);
        Assert.DoesNotContain(AuthServer.Password, saved);

        // POST /api/ipc: what the extension may not do, a signed-in browser may.
        using var app = new AuthClient(server.Base);
        var settings = await app.IpcAsync(MessageTypes.SettingsGet, new { }, token);
        Assert.Equal(HttpStatusCode.OK, settings.Status);
        Assert.NotEqual(MessageTypes.HostActionResult, settings.Reply!.Type);
        var snapshot = await app.IpcAsync(MessageTypes.JobsQuery, new { }, token);
        Assert.True(snapshot.Reply!.Payload!.Value.TryGetProperty("settings", out _));
        var made = await app.IpcAsync(MessageTypes.FoldersCreate, new { parent = server.Root, name = "made by the desk" }, token);
        Assert.Equal(HttpStatusCode.OK, made.Status);
        Assert.True(Directory.Exists(Path.Combine(server.Root, "made by the desk")));

        // GET /api/ws with the token: the hello and the snapshot with its settings, then answers.
        await using var socket = await ConnectAsync(server, token);
        Assert.Equal(MessageTypes.ServerHello, (await socket.ReadAsync())!.Type);
        var first = await socket.ReadAsync();
        Assert.Equal(MessageTypes.JobsSnapshot, first!.Type);
        Assert.Equal(Path.GetFullPath(server.Root), first.GetPayload<SnapshotDto>().Settings.DefaultDownloadDirectory);
        Assert.True((await socket.AskAsync<HostActionResult>(MessageTypes.HostPing, new { })).Ok);

        // The token is still not a session: the sign-in and token pages want the cookie.
        using (var page = await app.GetAsync("/api/auth/tokens", request => request.Headers.Add("Authorization", "Bearer " + token)))
            Assert.Equal(HttpStatusCode.Unauthorized, page.StatusCode);

        // The list shows each token's role.
        await PairingTests.PairAsync(server, await PairingTests.NewCodeAsync(user), "Chrome");
        using var list = await user.GetAsync("/api/auth/tokens");
        var roles = (await user.JsonAsync(list)).GetProperty("tokens").EnumerateArray()
            .ToDictionary(item => item.GetProperty("name").GetString()!, item => item.GetProperty("role").GetString());
        Assert.Equal(TokenRoles.RemoteClient, roles["Desk PC"]);
        Assert.Equal(TokenRoles.Extension, roles["Chrome"]);
    }

    [Fact]
    public async Task ACodeOnlyEverMakesAnExtensionTokenAndUnknownRolesAreRefused()
    {
        var time = new ManualTime(DateTimeOffset.UtcNow);
        await using var server = await AuthServer.StartAsync(time);
        using var user = await AuthServer.SetUpAsync(server);
        var code = await PairingTests.NewCodeAsync(user);

        // A code with the desktop role is not a password: refused, and counted as a wrong one.
        var (status, body) = await PairWithPasswordAsync(server, null, extra: new { code, name = "sneaky", role = TokenRoles.RemoteClient });
        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal(1, body.GetProperty("retryAfterSeconds").GetInt32());
        time.Advance(TimeSpan.FromSeconds(2));

        (status, _) = await PairWithPasswordAsync(server, AuthServer.Password, extra: new { code, password = AuthServer.Password, role = "admin" });
        Assert.Equal(HttpStatusCode.BadRequest, status);

        // The code is still good, and makes what it was made for. A password beside it changes nothing.
        using var extension = new AuthClient(server.Base) { Origin = PairingTests.ChromeOrigin };
        using var paired = await extension.PostAsync("/api/pair", new { code, name = "Chrome", password = AuthServer.Password, role = TokenRoles.Extension });
        Assert.Equal(HttpStatusCode.OK, paired.StatusCode);
        var result = await extension.JsonAsync(paired);
        Assert.Equal(TokenRoles.Extension, result.GetProperty("role").GetString());
        var token = result.GetProperty("token").GetString()!;
        Assert.Equal(HttpStatusCode.Forbidden, (await extension.IpcAsync(MessageTypes.SettingsGet, new { }, token)).Status);
        Assert.Equal(TokenRoles.Extension, server.App.Auth.Pairing.List().Single().Role);
    }

    [Fact]
    public async Task AWrongPairingPasswordWaitsAndLocksOutLikeAWrongSignIn()
    {
        var time = new ManualTime(DateTimeOffset.UtcNow);
        await using var server = await AuthServer.StartAsync(time);
        using (await AuthServer.SetUpAsync(server)) { }

        var (status, body) = await PairWithPasswordAsync(server, "not the password");
        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal(1, body.GetProperty("retryAfterSeconds").GetInt32());
        // While the address waits, even the right password isn't checked, and the sign-in page waits too.
        (status, _) = await PairWithPasswordAsync(server, AuthServer.Password);
        Assert.Equal((HttpStatusCode)429, status);
        using var browser = new AuthClient(server.Base);
        Assert.Equal((HttpStatusCode)429, (await browser.PostAsync("/api/auth/signin", new { password = AuthServer.Password })).StatusCode);

        // Each wrong one doubles the wait, sign-ins and pairings together.
        time.Advance(TimeSpan.FromSeconds(1.5));
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.PostAsync("/api/auth/signin", new { password = "wrong again" })).StatusCode);
        time.Advance(TimeSpan.FromSeconds(2.5));
        (status, body) = await PairWithPasswordAsync(server, "and again");
        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal(4, body.GetProperty("retryAfterSeconds").GetInt32());
        time.Advance(TimeSpan.FromSeconds(3));
        (status, _) = await PairWithPasswordAsync(server, AuthServer.Password);
        Assert.Equal((HttpStatusCode)429, status);
        Assert.Empty(server.App.Auth.Pairing.List());

        // After the wait the right one pairs, and clears the count.
        time.Advance(TimeSpan.FromSeconds(1.5));
        await PairRemoteClientAsync(server);
        (status, body) = await PairWithPasswordAsync(server, "one more wrong");
        Assert.Equal(1, body.GetProperty("retryAfterSeconds").GetInt32());
    }

    [Fact]
    public async Task PasswordPairingIsRefusedWithSignInOffOrBeforeAPasswordIsSet()
    {
        await using (var open = await ServerHarness.StartAsync())
        {
            var (status, _) = await PairWithPasswordAsync(open, AuthServer.Password);
            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.Empty(open.App.Auth.Pairing.List());
        }

        await using var fresh = await AuthServer.StartAsync();
        var (setup, _) = await PairWithPasswordAsync(fresh, AuthServer.Password);
        Assert.Equal(HttpStatusCode.Conflict, setup);
        Assert.Empty(fresh.App.Auth.Pairing.List());
    }

    // ---- 3. The extension's token keeps its place -------------------------------------------------------------

    [Fact]
    public async Task AnExtensionTokenIsRefusedOnTheWebSocketWithSignInOnOrOff()
    {
        await using (var server = await AuthServer.StartAsync())
        {
            using var user = await AuthServer.SetUpAsync(server);
            var token = await PairingTests.PairAsync(server, await PairingTests.NewCodeAsync(user));
            Assert.Equal(HttpStatusCode.Forbidden, await RefusedUpgradeAsync(() => ConnectAsync(server, token)));
            // Its cookie beside it doesn't help: the token decides.
            Assert.Equal(HttpStatusCode.Forbidden, await RefusedUpgradeAsync(() => ConnectAsync(server, token, sessionCookie: user.Session)));
            Assert.Equal(HttpStatusCode.Unauthorized, await RefusedUpgradeAsync(() => ConnectAsync(server, "ibt_madeup")));
            // Without a token and a session it is the sign-in it always was; a session alone opens it.
            Assert.Equal(HttpStatusCode.Unauthorized, await RefusedUpgradeAsync(() => ConnectAsync(server, null)));
            await using var signedIn = await ConnectAsync(server, null, sessionCookie: user.Session);
            Assert.Equal(MessageTypes.ServerHello, (await signedIn.ReadAsync())!.Type);
        }

        await using var open = await ServerHarness.StartAsync();
        using var anyone = new AuthClient(open.Base);
        var openToken = await PairingTests.PairAsync(open, await PairingTests.NewCodeAsync(anyone));
        Assert.Equal(HttpStatusCode.Forbidden, await RefusedUpgradeAsync(() => ConnectAsync(open, openToken)));
    }

    // ---- 4. Revoking closes the sockets -----------------------------------------------------------------------

    [Fact]
    public async Task RevokingATokenClosesItsWebSocketsAndOnlyItsOwn()
    {
        await using var server = await AuthServer.StartAsync();
        using var user = await AuthServer.SetUpAsync(server);
        var desk = await PairRemoteClientAsync(server, "Desk PC");
        var laptop = await PairRemoteClientAsync(server, "Laptop");

        await using var deskSocket = await ConnectAsync(server, desk);
        await using var deskSecond = await ConnectAsync(server, desk);
        await using var laptopSocket = await ConnectAsync(server, laptop);
        foreach (var socket in new[] { deskSocket, deskSecond, laptopSocket })
            await socket.ReadUntilAsync(message => message.Type == MessageTypes.JobsSnapshot);

        using var revoked = await user.SendAsync(HttpMethod.Delete, $"/api/auth/tokens/{await TokenIdAsync(user, "Desk PC")}");
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        Assert.True(await ClosesWithinAsync(deskSocket, TimeSpan.FromSeconds(10)));
        Assert.True(await ClosesWithinAsync(deskSecond, TimeSpan.FromSeconds(10)));
        Assert.Equal(HttpStatusCode.Unauthorized, await RefusedUpgradeAsync(() => ConnectAsync(server, desk)));

        // The other token's socket stays and answers.
        Assert.True((await laptopSocket.AskAsync<HostActionResult>(MessageTypes.HostPing, new { })).Ok);

        // The app's own Unpair closes its sockets too.
        await using var laptopSecond = await ConnectAsync(server, laptop);
        await laptopSecond.ReadUntilAsync(message => message.Type == MessageTypes.JobsSnapshot);
        using var app = new AuthClient(server.Base);
        using var unpaired = await app.SendAsync(HttpMethod.Delete, ServerAuth.PairSelfPath, null,
            request => request.Headers.Add("Authorization", "Bearer " + laptop));
        Assert.Equal(HttpStatusCode.NoContent, unpaired.StatusCode);
        Assert.True(await ClosesWithinAsync(laptopSocket, TimeSpan.FromSeconds(10)));
        Assert.True(await ClosesWithinAsync(laptopSecond, TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task APasswordChangeThatUnpairsClosesTheSocketsAndOneThatKeepsThemDoesNot()
    {
        await using var server = await AuthServer.StartAsync();
        using var user = await AuthServer.SetUpAsync(server);
        var token = await PairRemoteClientAsync(server);
        await using var socket = await ConnectAsync(server, token);
        await socket.ReadUntilAsync(message => message.Type == MessageTypes.JobsSnapshot);

        using (var kept = await user.PostAsync("/api/auth/password", new { current = AuthServer.Password, password = "second password", keepPairedBrowsers = true }))
            Assert.Equal(HttpStatusCode.OK, kept.StatusCode);
        Assert.True((await socket.AskAsync<HostActionResult>(MessageTypes.HostPing, new { })).Ok);

        using (var changed = await user.PostAsync("/api/auth/password", new { current = "second password", password = "third password" }))
        {
            Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
            Assert.Equal(1, (await user.JsonAsync(changed)).GetProperty("unpaired").GetInt32());
        }

        Assert.True(await ClosesWithinAsync(socket, TimeSpan.FromSeconds(10)));
        using var app = new AuthClient(server.Base);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.IpcAsync(MessageTypes.HostPing, new { }, token)).Status);
    }

    [Fact]
    public void ALifetimeAskedForAfterTheTokenWentIsEndedAlready()
    {
        var work = ServerHarness.NewWork();
        var pairing = new Pairing(AuthStore.Open(work), TimeProvider.System);
        var (_, record) = pairing.Issue(TokenRoles.RemoteClient, "Desk PC");
        var lifetime = pairing.Lifetime(record.Id);
        Assert.False(lifetime.IsCancellationRequested);
        Assert.True(pairing.Revoke(record.Id));
        Assert.True(lifetime.IsCancellationRequested);
        Assert.True(pairing.Lifetime(record.Id).IsCancellationRequested);
        Assert.True(pairing.Lifetime("never-was").IsCancellationRequested);

        // Pushed out by newer ones (the oldest unused goes first) ends it as well.
        var (_, oldest) = pairing.Issue(TokenRoles.RemoteClient, "oldest");
        var oldestLifetime = pairing.Lifetime(oldest.Id);
        for (var i = 0; i < Pairing.MaxTokens; i++) pairing.Issue(TokenRoles.Extension, $"newer {i}");
        Assert.True(oldestLifetime.IsCancellationRequested);
        Directory.Delete(work, recursive: true);
    }

    // ---- 5. The host-name rule -------------------------------------------------------------------------------

    [Fact]
    public async Task AnAppWithAValidTokenAndNoOriginMayUseAnyNameAndABrowsersPageStillMayNot()
    {
        await using var server = await AuthServer.StartAsync();
        using (await AuthServer.SetUpAsync(server)) { }
        const string name = "homeserver";
        var self = $"http://{name}:{server.Base.Port}";

        async Task<HttpStatusCode> SendAsync(HttpMethod method, string path, string? bearer, string? origin, object? body = null)
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Host = $"{name}:{server.Base.Port}";
            if (bearer is not null) request.Headers.Add("Authorization", "Bearer " + bearer);
            if (origin is not null) request.Headers.Add("Origin", origin);
            if (body is not null) request.Content = JsonContent.Create(body, body.GetType(), options: body is IpcMessage ? IpcJson.Options : null);
            using var response = await server.Http.SendAsync(request);
            return response.StatusCode;
        }

        // Pairing by name, without an Origin: the password is the credential.
        Assert.Equal(HttpStatusCode.OK, await SendAsync(HttpMethod.Post, "/api/pair", null, null,
            new { password = AuthServer.Password, name = "by name", role = TokenRoles.RemoteClient }));
        var token = await PairRemoteClientAsync(server);
        var ping = IpcMessage.Create(MessageTypes.HostPing, new { });

        Assert.Equal(HttpStatusCode.OK, await SendAsync(HttpMethod.Post, "/api/ipc", token, null, ping));
        await using (var socket = await ConnectAsync(server, token, hostName: name))
            Assert.Equal(MessageTypes.ServerHello, (await socket.ReadAsync())!.Type);

        // With an Origin it is a browser, and both rules apply as before: another site is refused, and so is a page
        // that made the name its own (rebinding), token or not.
        Assert.Equal(HttpStatusCode.Forbidden, await SendAsync(HttpMethod.Post, "/api/ipc", token, "http://evil.example", ping));
        Assert.Equal(HttpStatusCode.Forbidden, await SendAsync(HttpMethod.Post, "/api/ipc", token, self, ping));
        Assert.Equal(HttpStatusCode.Forbidden, await SendAsync(HttpMethod.Post, "/api/pair", null, self,
            new { password = AuthServer.Password, role = TokenRoles.RemoteClient }));
        Assert.Equal(HttpStatusCode.Forbidden, await RefusedUpgradeAsync(() => ConnectAsync(server, token, origin: self, hostName: name)));
        Assert.Equal(HttpStatusCode.Forbidden, await RefusedUpgradeAsync(() => ConnectAsync(server, token, origin: "http://evil.example", hostName: name)));

        // Without an Origin, only a valid token on its own paths counts.
        Assert.Equal(HttpStatusCode.Forbidden, await SendAsync(HttpMethod.Post, "/api/ipc", null, null, ping));
        Assert.Equal(HttpStatusCode.Forbidden, await SendAsync(HttpMethod.Post, "/api/ipc", "ibt_madeup", null, ping));
        Assert.Equal(HttpStatusCode.Forbidden, await SendAsync(HttpMethod.Get, "/api/auth/tokens", token, null));
        Assert.Equal(HttpStatusCode.Forbidden, await SendAsync(HttpMethod.Get, "/api/auth/status", token, null));
        Assert.Equal(HttpStatusCode.Forbidden, await SendAsync(HttpMethod.Get, "/api/pair", null, null));
        Assert.Equal(HttpStatusCode.Forbidden, await RefusedUpgradeAsync(() => ConnectAsync(server, null, hostName: name)));
        Assert.Equal(HttpStatusCode.Forbidden, await RefusedUpgradeAsync(() => ConnectAsync(server, "ibt_madeup", hostName: name)));
    }

    [Fact]
    public async Task WithSignInOffANameIsStillRefusedWithoutAToken()
    {
        // With sign-in off a rebinding page would need nothing else, so this is the wall that matters.
        await using var server = await ServerHarness.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/ipc")
        {
            Content = JsonContent.Create(IpcMessage.Create(MessageTypes.JobsPauseAll, new { }), options: IpcJson.Options),
        };
        request.Headers.Host = $"homeserver:{server.Base.Port}";
        request.Headers.Add("Authorization", "Bearer ibt_not-a-real-one");
        using var response = await server.Http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, await RefusedUpgradeAsync(() => ConnectAsync(server, null, hostName: "homeserver")));
    }

    // ---- 6. client.hello on the WebSocket ---------------------------------------------------------------------

    [Fact]
    public async Task WithTheHelloFlagTheClientSaysFirstWhetherItWantsTheSnapshotAndTheEvents()
    {
        await using var server = await ServerHarness.StartAsync();
        await using var quiet = await ConnectAsync(server, null, "/api/ws?hello=1");
        await quiet.SendAsync(IpcMessage.Create(MessageTypes.ClientHello, new ClientHelloDto { Snapshot = false, Events = false }));
        Assert.Equal(MessageTypes.ServerHello, (await quiet.ReadAsync())!.Type);

        await using var listening = await ConnectAsync(server, null, "/api/ws?hello=1");
        await listening.SendAsync(IpcMessage.Create(MessageTypes.ClientHello, new ClientHelloDto { Snapshot = false, Events = true }));
        Assert.Equal(MessageTypes.ServerHello, (await listening.ReadAsync())!.Type);

        // Without the flag nothing changed: hello, snapshot.
        await using var plain = await server.ConnectAsync();
        Assert.Equal(MessageTypes.ServerHello, (await plain.ReadAsync())!.Type);
        Assert.Equal(MessageTypes.JobsSnapshot, (await plain.ReadAsync())!.Type);

        // A download added elsewhere is an event: the listening client gets it, the quiet one doesn't.
        var added = await plain.AskAsync<AddJobResponse>(MessageTypes.JobAdd, new AddJobRequest { Url = "http://127.0.0.1:1/never.bin", StartMode = "later" });
        Assert.True(added.Accepted, added.Error);
        var heard = await listening.ReadUntilAsync(message => message.Type == MessageTypes.JobEvent);
        Assert.Equal(added.Job!.Id, heard.GetPayload<JobEventDto>().Job.Id);

        // The quiet client's next message is the answer to its own question, with nothing before it.
        var ping = IpcMessage.Create(MessageTypes.HostPing, new { });
        await quiet.SendAsync(ping);
        Assert.Equal(ping.Id, (await quiet.ReadAsync())!.ReplyTo);
        Assert.Equal(new[] { MessageTypes.ServerHello, MessageTypes.HostActionResult }, quiet.Received.Select(message => message.Type));

        // The snapshot when asked for, and an empty hello means both.
        await using var full = await ConnectAsync(server, null, "/api/ws?hello=1");
        await full.SendAsync(IpcMessage.Create(MessageTypes.ClientHello));
        Assert.Equal(MessageTypes.ServerHello, (await full.ReadAsync())!.Type);
        Assert.Equal(MessageTypes.JobsSnapshot, (await full.ReadAsync())!.Type);
    }

    [Fact]
    public async Task WithTheHelloFlagAnythingElseFirstClosesTheSocketAndAnotherVersionIsTold()
    {
        await using var server = await ServerHarness.StartAsync();

        await using var rude = await ConnectAsync(server, null, "/api/ws?hello=1");
        await rude.SendAsync(IpcMessage.Create(MessageTypes.HostPing, new { }));
        Assert.Null(await rude.ReadAsync());
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, rude.Socket.CloseStatus);

        await using var older = await ConnectAsync(server, null, "/api/ws?hello=1");
        var hello = IpcMessage.Create(MessageTypes.ClientHello, new ClientHelloDto()) with { V = Protocol.Version + 1 };
        await older.SendAsync(hello);
        Assert.Equal(MessageTypes.ServerHello, (await older.ReadAsync())!.Type);
        var refused = await older.ReadAsync();
        Assert.Equal(MessageTypes.ServerIncompatible, refused!.Type);
        Assert.Null(await older.ReadAsync());
    }

    [Fact]
    public async Task ADesktopAppsTokenWorksWithTheHelloFlag()
    {
        await using var server = await AuthServer.StartAsync();
        using (await AuthServer.SetUpAsync(server)) { }
        var token = await PairRemoteClientAsync(server);
        await using var socket = await ConnectAsync(server, token, "/api/ws?hello=1");
        await socket.SendAsync(IpcMessage.Create(MessageTypes.ClientHello, new ClientHelloDto { Snapshot = false, Events = true }));
        Assert.Equal(MessageTypes.ServerHello, (await socket.ReadAsync())!.Type);
        Assert.True((await socket.AskAsync<HostActionResult>(MessageTypes.HostPing, new { })).Ok);
        Assert.DoesNotContain(socket.Received, message => message.Type == MessageTypes.JobsSnapshot);
    }

    // ---- 7. hostKind -----------------------------------------------------------------------------------------

    [Fact]
    public async Task TheSignInStatusSaysWhichKindOfServerThisIs()
    {
        await using (var server = await AuthServer.StartAsync())
        {
            // Before sign-in too: the sign-in page's texts depend on it.
            using var anonymous = new AuthClient(server.Base);
            using var status = await anonymous.GetAsync("/api/auth/status");
            Assert.Equal("server", (await anonymous.JsonAsync(status)).GetProperty("hostKind").GetString());
        }

        await using var windows = await ServerHarness.StartAsync(options => options with { HostKind = "windows" });
        using var client = new AuthClient(windows.Base);
        using var answer = await client.GetAsync("/api/auth/status");
        Assert.Equal("windows", (await client.JsonAsync(answer)).GetProperty("hostKind").GetString());
    }
}
