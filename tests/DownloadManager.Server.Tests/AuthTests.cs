using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using DownloadManager.Contracts;
using DownloadManager.Server.Auth;

namespace DownloadManager.Server.Tests;

/// <summary>D2 sign-in: setup, sessions, waits after wrong tries, cookie flags, the origin check and sign-in off.</summary>
public sealed class AuthTests
{
    [Fact]
    public async Task SetupFromThisMachineGivesASessionAndOnlyHealthAndTheSignInPageAnswerWithoutOne()
    {
        await using var server = await AuthServer.StartAsync();
        using var anonymous = new AuthClient(server.Base);

        var status = await anonymous.JsonAsync(await anonymous.GetAsync("/api/auth/status"));
        Assert.True(status.GetProperty("signInRequired").GetBoolean());
        Assert.True(status.GetProperty("setupNeeded").GetBoolean());
        Assert.False(status.GetProperty("setupCodeNeeded").GetBoolean());
        Assert.False(status.GetProperty("signedIn").GetBoolean());
        // A stranger isn't told which version runs here.
        Assert.True(!status.TryGetProperty("version", out var version) || version.ValueKind == JsonValueKind.Null);

        // Without a session: health and the sign-in page answer, the API is refused and pages send you to sign in.
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/signin/")).StatusCode);
        // The theme script the sign-in page loads is public too (absent from the placeholder page, so 404 there).
        Assert.NotEqual(HttpStatusCode.Redirect, (await anonymous.GetAsync("/theme.js")).StatusCode);
        var page = await anonymous.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
        Assert.Equal("/signin/", page.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.Redirect, (await anonymous.GetAsync("/assets/app.js")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.IpcAsync(MessageTypes.HostPing, new { })).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/auth/tokens")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/auth/pairing/code")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/ws")).StatusCode);
        var socketRefused = await Assert.ThrowsAnyAsync<WebSocketException>(() => server.ConnectAsync());
        Assert.Contains("401", socketRefused.Message);

        // A short password is refused, then the first one is set and signs this browser in.
        Assert.Equal(HttpStatusCode.BadRequest, (await anonymous.PostAsync("/api/auth/setup", new { password = "short" })).StatusCode);
        using var client = await AuthServer.SetUpAsync(server);
        Assert.Equal(HttpStatusCode.OK, (await client.IpcAsync(MessageTypes.HostPing, new { })).Status);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/")).StatusCode);
        Assert.Equal(Protocol.HostVersion, (await client.JsonAsync(await client.GetAsync("/api/auth/status"))).GetProperty("version").GetString());
        await using (var socket = await server.ConnectAsync(sessionCookie: client.Session))
            Assert.Equal(MessageTypes.ServerHello, (await socket.ReadAsync())!.Type);

        // Setup happens once.
        Assert.Equal(HttpStatusCode.Conflict, (await anonymous.PostAsync("/api/auth/setup", new { password = "another password" })).StatusCode);

        // Only the hash is saved: PBKDF2-SHA256 with at least 600,000 iterations, never the password.
        var saved = await File.ReadAllTextAsync(Path.Combine(server.Data, AuthStore.FileName));
        Assert.DoesNotContain(AuthServer.Password, saved);
        using var file = JsonDocument.Parse(saved);
        var record = file.RootElement.GetProperty("password");
        Assert.Equal("pbkdf2-sha256", record.GetProperty("algorithm").GetString());
        Assert.True(record.GetProperty("iterations").GetInt32() >= 600_000);
        Assert.Equal(16, Convert.FromBase64String(record.GetProperty("salt").GetString()!).Length);
        Assert.Equal(32, Convert.FromBase64String(record.GetProperty("hash").GetString()!).Length);
    }

    [Fact]
    public async Task TheSessionCookieIsHttpOnlyStrictRandomAndSecureOnlyOverHttps()
    {
        await using var server = await AuthServer.StartAsync();
        using var client = await AuthServer.SetUpAsync(server);
        var cookie = client.LastSetCookie!;
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secure", cookie, StringComparison.OrdinalIgnoreCase);
        // 32 random bytes, base64url without padding.
        Assert.Equal(43, client.Session!.Length);

        // Two sign-ins never share an id.
        using var second = new AuthClient(server.Base);
        Assert.Equal(HttpStatusCode.OK, (await second.PostAsync("/api/auth/signin", new { password = AuthServer.Password })).StatusCode);
        Assert.NotEqual(client.Session, second.Session);

        // X-Forwarded-Proto is believed only from a proxy DOWNLOADMANAGER_TRUST_PROXY lists.
        using var forwarded = new AuthClient(server.Base);
        await forwarded.PostAsync("/api/auth/signin", new { password = AuthServer.Password },
            request => request.Headers.Add("X-Forwarded-Proto", "https"));
        Assert.DoesNotContain("secure", forwarded.LastSetCookie!, StringComparison.OrdinalIgnoreCase);

        await using var proxied = await AuthServer.StartAsync(change: options => options with { TrustedProxies = [System.Net.IPNetwork.Parse("127.0.0.1/32")] }, work: null);
        // Through a listed proxy the setup code is asked even from this machine.
        using var proxiedSetup = await AuthServer.SetUpAsync(proxied, setupCode: AuthServer.SetupCode(proxied));
        using var overHttps = new AuthClient(proxied.Base) { Origin = $"https://{proxied.Base.Authority}" };
        using var signedIn = await overHttps.PostAsync("/api/auth/signin", new { password = AuthServer.Password },
            request => request.Headers.Add("X-Forwarded-Proto", "https"));
        Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
        Assert.Contains("secure", overHttps.LastSetCookie!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ABrowserFromElsewhereNeedsTheSetupCodeFromTheLog()
    {
        await using var server = await AuthServer.StartAsync();
        using var remote = new AuthClient(server.Base);
        // A proxy's header marks a request as not from this machine, as a LAN address would.
        void FromElsewhere(HttpRequestMessage request) => request.Headers.Add("X-Forwarded-For", "192.168.1.20");

        var status = await remote.JsonAsync(await remote.GetAsync("/api/auth/status", FromElsewhere));
        Assert.True(status.GetProperty("setupCodeNeeded").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await remote.PostAsync("/api/auth/setup", new { password = AuthServer.Password }, FromElsewhere)).StatusCode);

        var log = string.Join("\n", Directory.GetFiles(Path.Combine(server.Data, "logs")).Select(File.ReadAllText));
        var marker = "setup code: ";
        var code = log[(log.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..].Split('\n')[0].Trim();
        Assert.Matches("^[A-Z2-9]{4}-[A-Z2-9]{4}$", code);

        // The wrong code made this address wait.
        Assert.Equal(HttpStatusCode.TooManyRequests,
            (await remote.PostAsync("/api/auth/setup", new { password = AuthServer.Password, setupCode = code }, FromElsewhere)).StatusCode);
        await Task.Delay(TimeSpan.FromSeconds(1.2));
        using var done = await remote.PostAsync("/api/auth/setup", new { password = AuthServer.Password, setupCode = code.ToLowerInvariant() }, FromElsewhere);
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        Assert.NotNull(remote.Session);
    }

    [Fact]
    public async Task WrongPasswordsMakeTheAddressWaitLongerEachTimeAndARightOneClearsIt()
    {
        var time = new ManualTime(DateTimeOffset.UtcNow);
        await using var server = await AuthServer.StartAsync(time);
        using var _ = await AuthServer.SetUpAsync(server);
        using var client = new AuthClient(server.Base);

        var wrong = await client.PostAsync("/api/auth/signin", new { password = "not the password" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(1, (await client.JsonAsync(wrong)).GetProperty("retryAfterSeconds").GetInt32());

        // While it waits, even the right password isn't checked.
        var waiting = await client.PostAsync("/api/auth/signin", new { password = AuthServer.Password });
        Assert.Equal(HttpStatusCode.TooManyRequests, waiting.StatusCode);
        Assert.NotNull(waiting.Headers.RetryAfter);
        Assert.Null(client.Session);

        time.Advance(TimeSpan.FromSeconds(1));
        var second = await client.PostAsync("/api/auth/signin", new { password = "still wrong" });
        Assert.Equal(2, (await client.JsonAsync(second)).GetProperty("retryAfterSeconds").GetInt32());
        time.Advance(TimeSpan.FromSeconds(2));
        var third = await client.PostAsync("/api/auth/signin", new { password = "still wrong" });
        Assert.Equal(4, (await client.JsonAsync(third)).GetProperty("retryAfterSeconds").GetInt32());

        time.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/auth/signin", new { password = AuthServer.Password })).StatusCode);
        Assert.Equal(0, server.App.Auth.Limiter.Failures("127.0.0.1"));

        // The wait grows to five minutes and no further.
        Assert.Equal(TimeSpan.FromSeconds(1), SignInLimiter.Delay(1));
        Assert.Equal(TimeSpan.FromSeconds(256), SignInLimiter.Delay(9));
        Assert.Equal(TimeSpan.FromMinutes(5), SignInLimiter.Delay(10));
        Assert.Equal(TimeSpan.FromMinutes(5), SignInLimiter.Delay(1000));
    }

    [Fact]
    public async Task ASessionEndsWhenIdleAndAtItsAbsoluteLimit()
    {
        var time = new ManualTime(DateTimeOffset.UtcNow);
        await using var server = await AuthServer.StartAsync(time, options => options with
        {
            SessionIdleTimeout = TimeSpan.FromHours(1),
            SessionAbsoluteTimeout = TimeSpan.FromHours(5),
        });
        using var client = await AuthServer.SetUpAsync(server);
        Assert.Contains("expires=", client.LastSetCookie!, StringComparison.OrdinalIgnoreCase);

        time.Advance(TimeSpan.FromMinutes(59));
        Assert.Equal(HttpStatusCode.OK, (await client.IpcAsync(MessageTypes.HostPing, new { })).Status);
        time.Advance(TimeSpan.FromMinutes(61));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.IpcAsync(MessageTypes.HostPing, new { })).Status);

        // Used every half hour it stays, until five hours after sign-in.
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/auth/signin", new { password = AuthServer.Password })).StatusCode);
        for (var i = 0; i < 9; i++)
        {
            time.Advance(TimeSpan.FromMinutes(30));
            Assert.Equal(HttpStatusCode.OK, (await client.IpcAsync(MessageTypes.HostPing, new { })).Status);
        }

        time.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.IpcAsync(MessageTypes.HostPing, new { })).Status);
    }

    [Fact]
    public async Task SignOutEndsTheSessionAndClosesItsWebSocket()
    {
        await using var server = await AuthServer.StartAsync();
        using var client = await AuthServer.SetUpAsync(server);
        var session = client.Session!;
        await using var socket = await server.ConnectAsync(sessionCookie: session);
        Assert.Equal(MessageTypes.ServerHello, (await socket.ReadAsync())!.Type);
        Assert.Equal(MessageTypes.JobsSnapshot, (await socket.ReadAsync())!.Type);

        using var signedOut = await client.PostAsync("/api/auth/signout");
        Assert.Equal(HttpStatusCode.OK, signedOut.StatusCode);
        Assert.Contains("expires=", client.LastSetCookie!, StringComparison.OrdinalIgnoreCase);
        // The server ends the socket: a close, or the connection dropped while a read was cancelled.
        try
        {
            Assert.Null(await socket.ReadAsync(TimeSpan.FromSeconds(10)));
        }
        catch (WebSocketException)
        {
        }

        Assert.NotEqual(WebSocketState.Open, socket.Socket.State);

        client.Session = session;
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.IpcAsync(MessageTypes.HostPing, new { })).Status);
    }

    [Fact]
    public async Task ChangingThePasswordSignsOutEveryOtherBrowser()
    {
        await using var server = await AuthServer.StartAsync();
        using var first = await AuthServer.SetUpAsync(server);
        using var second = new AuthClient(server.Base);
        await second.PostAsync("/api/auth/signin", new { password = AuthServer.Password });

        Assert.Equal(HttpStatusCode.Forbidden,
            (await first.PostAsync("/api/auth/password", new { current = "wrong one", password = "the new password" })).StatusCode);
        await Task.Delay(TimeSpan.FromSeconds(1.2));
        Assert.Equal(HttpStatusCode.OK,
            (await first.PostAsync("/api/auth/password", new { current = AuthServer.Password, password = "the new password" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await first.IpcAsync(MessageTypes.HostPing, new { })).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await second.IpcAsync(MessageTypes.HostPing, new { })).Status);
        Assert.Equal(HttpStatusCode.OK, (await second.PostAsync("/api/auth/signin", new { password = "the new password" })).StatusCode);
    }

    [Fact]
    public async Task APasswordFromTheEnvironmentNeedsNoSetupAndCantBeChangedFromTheWebUi()
    {
        var work = ServerHarness.NewWork();
        var file = Path.Combine(work, "password.txt");
        await File.WriteAllTextAsync(file, "from a docker secret\n");
        await using (var server = await AuthServer.StartAsync(change: options => options with { PasswordFile = file }, work: work))
        {
            using var client = new AuthClient(server.Base);
            var status = await client.JsonAsync(await client.GetAsync("/api/auth/status"));
            Assert.False(status.GetProperty("setupNeeded").GetBoolean());
            Assert.True(status.GetProperty("passwordFromEnvironment").GetBoolean());
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync("/api/auth/setup", new { password = "someone else's" })).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/auth/signin", new { password = "from a docker secret" })).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict,
                (await client.PostAsync("/api/auth/password", new { current = "from a docker secret", password = "a changed one" })).StatusCode);
        }

        // The variable wins over the file and replaces the saved hash on the next start.
        await using (var server = await AuthServer.StartAsync(change: options => options with { Password = new SecretValue("from the variable") }, work: work))
        {
            using var client = new AuthClient(server.Base);
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/auth/signin", new { password = "from the variable" })).StatusCode);
            Assert.DoesNotContain("from the variable", await File.ReadAllTextAsync(Path.Combine(server.Data, AuthStore.FileName)));
        }

        // A password too short to keep anyone out stops the start; the options never print it.
        var refused = await Assert.ThrowsAsync<ServerStartException>(() =>
            AuthServer.StartAsync(change: options => options with { Password = new SecretValue("short") }));
        Assert.Contains(ServerOptions.PasswordVariable, refused.Message);
        Assert.DoesNotContain("from the variable", new ServerOptions { Password = new SecretValue("from the variable") }.ToString());
        Directory.Delete(work, recursive: true);
    }

    [Fact]
    public async Task TheSignInCallsAndTheWebSocketRefuseAnotherSite()
    {
        await using var server = await AuthServer.StartAsync();
        using var client = await AuthServer.SetUpAsync(server);
        client.Origin = "http://evil.example";
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/auth/signin", new { password = AuthServer.Password })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/auth/signout")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/auth/pairing/code")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.IpcAsync(MessageTypes.JobsPauseAll, new { })).Status);
        var refused = await Assert.ThrowsAnyAsync<WebSocketException>(() => server.ConnectAsync("http://evil.example", client.Session));
        Assert.Contains("403", refused.Message);

        client.Origin = $"http://{server.Base.Authority}";
        Assert.Equal(HttpStatusCode.OK, (await client.IpcAsync(MessageTypes.HostPing, new { })).Status);
        await using var own = await server.ConnectAsync(client.Origin, client.Session);
        Assert.Equal(MessageTypes.ServerHello, (await own.ReadAsync())!.Type);
    }

    [Fact]
    public async Task WithSignInOffEverythingAnswersAndTheStatusSaysSo()
    {
        await using var server = await ServerHarness.StartAsync();
        using var client = new AuthClient(server.Base);
        var status = await client.JsonAsync(await client.GetAsync("/api/auth/status"));
        Assert.False(status.GetProperty("signInRequired").GetBoolean());
        Assert.True(status.GetProperty("signedIn").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await client.IpcAsync(MessageTypes.HostPing, new { })).Status);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/")).StatusCode);

        // Only "off" and its plain synonyms turn it off; a typo leaves it on.
        foreach (var off in new[] { "off", "OFF", "0", "false", "no" })
            Assert.False(ServerOptions.FromEnvironment(name => name == ServerOptions.AuthVariable ? off : null).SignInRequired);
        foreach (var on in new[] { "on", "1", "of", "disabled", "" })
            Assert.True(ServerOptions.FromEnvironment(name => name == ServerOptions.AuthVariable ? on : null).SignInRequired);
        Assert.True(ServerOptions.FromEnvironment(_ => null).SignInRequired);
    }

    [Fact]
    public async Task TorrentUploadsNeedASessionAndLandInTheUploadsFolder()
    {
        await using var server = await AuthServer.StartAsync();
        using var anonymous = new AuthClient(server.Base);
        using var refused = await anonymous.SendAsync(HttpMethod.Post, "/api/upload/torrent", null,
            request => request.Content = new ByteArrayContent([1, 2, 3]));
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);

        using var client = await AuthServer.SetUpAsync(server);
        using var uploaded = await client.SendAsync(HttpMethod.Post, "/api/upload/torrent", null,
            request => request.Content = new ByteArrayContent("d4:infod4:name1:aee"u8.ToArray()));
        Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
        var reference = (await client.JsonAsync(uploaded)).GetProperty("upload").GetString()!;
        var path = Path.Combine(server.Data, TorrentUploads.FolderName, reference[TorrentUploads.ReferencePrefix.Length..] + ".torrent");
        Assert.Equal(Path.Combine(server.Data, TorrentUploads.FolderName), Path.GetDirectoryName(path));
        Assert.True(File.Exists(path));
    }
}
