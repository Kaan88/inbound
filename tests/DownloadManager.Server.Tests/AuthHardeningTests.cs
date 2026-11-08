using System.Net;
using System.Text.Json;
using DownloadManager.Contracts;
using DownloadManager.Server.Auth;

namespace DownloadManager.Server.Tests;

/// <summary>
/// D2 review fixes: waits that a burst of parallel guesses can't skip, proxies believed only from their own address,
/// bounded unauthenticated bodies (chunked too), auth.json changes undone when they can't be saved, a password change
/// that unpairs extensions, the host-name check without an Origin, and upload references instead of paths.
/// </summary>
public sealed class AuthHardeningTests
{
    private static void From(HttpRequestMessage request, string address) => request.Headers.Add("X-Forwarded-For", address);

    [Fact]
    public async Task ABurstOfParallelWrongPasswordsGetsOneCheckAndTheRestWait()
    {
        // The clock stands still, so every try after the first falls inside the first one's wait.
        var time = new ManualTime(DateTimeOffset.UtcNow);
        await using var server = await AuthServer.StartAsync(time);
        using var _ = await AuthServer.SetUpAsync(server);
        using var client = new AuthClient(server.Base);

        var tries = await Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
        {
            using var response = await client.PostAsync("/api/auth/signin", new { password = $"guess number {i}" });
            return response.StatusCode;
        }));
        Assert.Equal(1, tries.Count(status => status == HttpStatusCode.Unauthorized));
        Assert.Equal(7, tries.Count(status => status == HttpStatusCode.TooManyRequests));
        Assert.Equal(1, server.App.Auth.Limiter.Failures("127.0.0.1"));

        // Pairing codes the same way.
        using var user = new AuthClient(server.Base);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.OK, (await user.PostAsync("/api/auth/signin", new { password = AuthServer.Password })).StatusCode);
        await PairingTests.NewCodeAsync(user);
        using var extension = new AuthClient(server.Base) { Origin = PairingTests.ChromeOrigin };
        var pairs = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            using var response = await extension.PostAsync("/api/pair", new { code = "ZZZZ2222" });
            return response.StatusCode;
        }));
        Assert.Equal(1, pairs.Count(status => status == HttpStatusCode.Unauthorized));
        Assert.Equal(5, pairs.Count(status => status == HttpStatusCode.TooManyRequests));
    }

    [Fact]
    public void ATryCountsAsWrongFromItsStartAndIPv6CountsByItsNetwork()
    {
        var time = new ManualTime(DateTimeOffset.UtcNow);
        var limiter = new SignInLimiter(time);
        Assert.Null(limiter.Begin("a", out var first));
        Assert.Equal(TimeSpan.FromSeconds(1), first);
        // A second try while the first is still being checked has to wait.
        Assert.Equal(TimeSpan.FromSeconds(1), limiter.Begin("a", out _));
        Assert.Equal(1, limiter.Failures("a"));
        limiter.Succeeded("a");
        Assert.Null(limiter.Wait("a"));

        Assert.Equal("2001:db8:1:2::/64", SignInLimiter.AddressOf(IPAddress.Parse("2001:db8:1:2:aaaa:bbbb:cccc:dddd")));
        Assert.Equal(SignInLimiter.AddressOf(IPAddress.Parse("2001:db8:1:2::1")), SignInLimiter.AddressOf(IPAddress.Parse("2001:db8:1:2:ffff::9")));
        Assert.NotEqual(SignInLimiter.AddressOf(IPAddress.Parse("2001:db8:1:2::1")), SignInLimiter.AddressOf(IPAddress.Parse("2001:db8:1:3::1")));
        Assert.Equal("::1", SignInLimiter.AddressOf(IPAddress.IPv6Loopback));
        Assert.Equal("192.168.1.20", SignInLimiter.AddressOf(IPAddress.Parse("::ffff:192.168.1.20")));
    }

    [Fact]
    public void TrustProxyTakesTheProxysAddressAndRefusesOneForEveryone()
    {
        Assert.False(ServerOptions.FromEnvironment(_ => null).TrustProxy);
        foreach (var off in new[] { "", "0", "off", "no" })
        {
            var options = ServerOptions.FromEnvironment(name => name == ServerOptions.TrustProxyVariable ? off : null);
            Assert.False(options.TrustProxy);
            Assert.Null(options.TrustProxyProblem);
        }

        // "1" would believe whoever connects; it, every-address networks and nonsense stop the start.
        foreach (var bad in new[] { "1", "true", "yes", "0.0.0.0/0", "::/0", "proxy", "10.0.0.5/40" })
        {
            var options = ServerOptions.FromEnvironment(name => name == ServerOptions.TrustProxyVariable ? bad : null);
            Assert.True(options.TrustProxyProblem is not null, bad);
            Assert.False(options.TrustProxy, bad);
            Assert.Contains(ServerOptions.TrustProxyVariable, options.TrustProxyProblem);
        }

        var listed = ServerOptions.FromEnvironment(name => name == ServerOptions.TrustProxyVariable ? "172.30.0.10, 10.1.0.0/16;fd00::/8" : null);
        Assert.Null(listed.TrustProxyProblem);
        Assert.Equal(3, listed.TrustedProxies.Count);
        Assert.True(listed.IsTrustedProxy(IPAddress.Parse("172.30.0.10")));
        Assert.True(listed.IsTrustedProxy(IPAddress.Parse("::ffff:172.30.0.10")));
        Assert.False(listed.IsTrustedProxy(IPAddress.Parse("172.30.0.11")));
        Assert.True(listed.IsTrustedProxy(IPAddress.Parse("10.1.200.3")));
        Assert.True(listed.IsTrustedProxy(IPAddress.Parse("fd12::1")));
    }

    [Fact]
    public async Task TrustProxyOneStopsTheStart()
    {
        var refused = await Assert.ThrowsAsync<ServerStartException>(() => AuthServer.StartAsync(change: options => options with
        {
            TrustProxyProblem = ServerOptions.FromEnvironment(name => name == ServerOptions.TrustProxyVariable ? "1" : null).TrustProxyProblem,
        }));
        Assert.Contains(ServerOptions.TrustProxyVariable, refused.Message);
    }

    [Fact]
    public async Task AClientThatIsNotTheListedProxyCantChooseItsAddress()
    {
        // The listed proxy is elsewhere; this client (127.0.0.1) reaches the port directly.
        var time = new ManualTime(DateTimeOffset.UtcNow);
        await using var server = await AuthServer.StartAsync(time, options => options with { TrustedProxies = [IPNetwork.Parse("10.9.0.0/24")] });
        using var client = new AuthClient(server.Base);

        // Claiming to be this machine doesn't skip the setup code.
        var status = await client.JsonAsync(await client.GetAsync("/api/auth/status", request => From(request, "127.0.0.1")));
        Assert.True(status.GetProperty("setupCodeNeeded").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PostAsync("/api/auth/setup", new { password = AuthServer.Password }, request => From(request, "127.0.0.1"))).StatusCode);

        // A new claimed address on every try still waits as the one address it is.
        Assert.Equal(HttpStatusCode.TooManyRequests,
            (await client.PostAsync("/api/auth/setup", new { password = AuthServer.Password }, request => From(request, "10.9.9.2"))).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests,
            (await client.PostAsync("/api/auth/setup", new { password = AuthServer.Password }, request => From(request, "10.9.9.3"))).StatusCode);
    }

    [Fact]
    public async Task ThroughTheListedProxyTheClientsAddressCountsAndTheSetupCodeIsAlwaysAsked()
    {
        var time = new ManualTime(DateTimeOffset.UtcNow);
        // This client plays the proxy.
        await using var server = await AuthServer.StartAsync(time, options => options with { TrustedProxies = [IPNetwork.Parse("127.0.0.1/32")] });

        // Through a proxy the browser can be anywhere, whatever it says: the setup code is asked.
        using var anyone = new AuthClient(server.Base);
        Assert.True((await anyone.JsonAsync(await anyone.GetAsync("/api/auth/status", request => From(request, "127.0.0.1"))))
            .GetProperty("setupCodeNeeded").GetBoolean());
        Assert.True((await anyone.JsonAsync(await anyone.GetAsync("/api/auth/status"))).GetProperty("setupCodeNeeded").GetBoolean());

        var log = string.Join("\n", Directory.GetFiles(Path.Combine(server.Data, "logs")).Select(File.ReadAllText));
        var marker = "setup code: ";
        var code = log[(log.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..].Split('\n')[0].Trim();
        using var owner = new AuthClient(server.Base);
        Assert.Equal(HttpStatusCode.OK,
            (await owner.PostAsync("/api/auth/setup", new { password = AuthServer.Password, setupCode = code }, request => From(request, "192.168.1.5"))).StatusCode);

        // The forwarded address is the one that waits: one client's wrong tries don't hold up another.
        using var guesser = new AuthClient(server.Base);
        Assert.Equal(HttpStatusCode.Unauthorized, (await guesser.PostAsync("/api/auth/signin", new { password = "wrong one" }, request => From(request, "192.168.1.66"))).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await guesser.PostAsync("/api/auth/signin", new { password = "wrong two" }, request => From(request, "192.168.1.66"))).StatusCode);
        using var other = new AuthClient(server.Base);
        Assert.Equal(HttpStatusCode.OK, (await other.PostAsync("/api/auth/signin", new { password = AuthServer.Password }, request => From(request, "192.168.1.7"))).StatusCode);
    }

    [Fact]
    public async Task AChunkedSignInBodyIsCutOffAtSixteenKilobytes()
    {
        await using var server = await AuthServer.StartAsync();
        using var _ = await AuthServer.SetUpAsync(server);
        using var http = new HttpClient { BaseAddress = server.Base, Timeout = TimeSpan.FromSeconds(30) };

        foreach (var path in new[] { "/api/auth/signin", "/api/pair" })
        {
            // No length given: the body comes chunked, 64 KB of it.
            var big = "{\"password\":\"" + new string('a', 64 * 1024) + "\"}";
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new ChunkedJson(System.Text.Encoding.UTF8.GetBytes(big)) };
            using var response = await http.SendAsync(request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        // Nothing was counted against the address: the body was refused before any check.
        Assert.Equal(0, server.App.Auth.Limiter.Failures("127.0.0.1"));

        // A small chunked body still works.
        using var smallRequest = new HttpRequestMessage(HttpMethod.Post, "/api/auth/signin")
        {
            Content = new ChunkedJson(JsonSerializer.SerializeToUtf8Bytes(new { password = AuthServer.Password })),
        };
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(smallRequest)).StatusCode);
    }

    [Fact]
    public void AChangeThatCantBeSavedIsUndoneInMemoryToo()
    {
        var work = ServerHarness.NewWork();
        var store = AuthStore.Open(work);
        var first = new PasswordRecord { Iterations = 600_000, Salt = "c2FsdA==", Hash = "aGFzaA==" };
        store.SetPassword(first);
        store.AddToken(new TokenRecord { Id = "kept", Name = "Kept", Hash = "00", CreatedAt = DateTimeOffset.UtcNow }, 50);

        // A folder where the temporary file goes makes every save fail.
        Directory.CreateDirectory(Path.Combine(work, AuthStore.FileName + ".tmp"));
        var second = new PasswordRecord { Iterations = 600_000, Salt = "c2FsdA==", Hash = "b3RoZXI=" };
        Assert.ThrowsAny<Exception>(() => store.SetPassword(second));
        Assert.Same(first, store.Password);
        Assert.ThrowsAny<Exception>(() => store.AddToken(new TokenRecord { Id = "new", Name = "New", Hash = "11", CreatedAt = DateTimeOffset.UtcNow }, 50));
        Assert.ThrowsAny<Exception>(() => store.RemoveToken("kept"));
        Assert.ThrowsAny<Exception>(() => store.RemoveAllTokens());
        Assert.Equal(["kept"], store.Tokens().Select(token => token.Id));

        // What is in force is what the file says.
        Directory.Delete(Path.Combine(work, AuthStore.FileName + ".tmp"));
        var reopened = AuthStore.Open(work);
        Assert.Equal(first.Hash, reopened.Password!.Hash);
        Assert.Equal(["kept"], reopened.Tokens().Select(token => token.Id));
        Directory.Delete(work, recursive: true);
    }

    [Fact]
    public async Task ChangingThePasswordUnpairsTheExtensionsUnlessAskedToKeepThem()
    {
        await using var server = await AuthServer.StartAsync();
        using var user = await AuthServer.SetUpAsync(server);
        var token = await PairingTests.PairAsync(server, await PairingTests.NewCodeAsync(user));
        using var extension = new AuthClient(server.Base) { Origin = PairingTests.ChromeOrigin };
        Assert.Equal(HttpStatusCode.OK, (await extension.IpcAsync(MessageTypes.HostPing, new { }, token)).Status);

        using var kept = await user.PostAsync("/api/auth/password", new { current = AuthServer.Password, password = "second password", keepPairedBrowsers = true });
        Assert.Equal(HttpStatusCode.OK, kept.StatusCode);
        Assert.Equal(0, (await user.JsonAsync(kept)).GetProperty("unpaired").GetInt32());
        Assert.Equal(HttpStatusCode.OK, (await extension.IpcAsync(MessageTypes.HostPing, new { }, token)).Status);

        using var changed = await user.PostAsync("/api/auth/password", new { current = "second password", password = "third password" });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Equal(1, (await user.JsonAsync(changed)).GetProperty("unpaired").GetInt32());
        Assert.Equal(HttpStatusCode.Unauthorized, (await extension.IpcAsync(MessageTypes.HostPing, new { }, token)).Status);
        Assert.Empty(server.App.Auth.Pairing.List());
    }

    [Fact]
    public async Task WithoutAnOriginAnApiRequestByAnUnlistedNameIsRefusedToo()
    {
        // Sign-in off: a rebinding page's same-origin GET carries no Origin and needs no session.
        await using var server = await ServerHarness.StartAsync();
        async Task<HttpStatusCode> GetAsync(string path, string host)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Host = $"{host}:{server.Base.Port}";
            using var response = await server.Http.SendAsync(request);
            return response.StatusCode;
        }

        Assert.Equal(HttpStatusCode.Forbidden, await GetAsync("/api/auth/tokens", "rebound.example"));
        Assert.Equal(HttpStatusCode.Forbidden, await GetAsync("/api/auth/status", "rebound.example"));
        Assert.Equal(HttpStatusCode.OK, await GetAsync("/api/auth/status", "localhost"));
        Assert.Equal(HttpStatusCode.OK, await GetAsync("/api/auth/status", server.Base.Host));
        // Health and the pages aren't under /api.
        Assert.Equal(HttpStatusCode.OK, await GetAsync("/health", "rebound.example"));

        await using var named = await ServerHarness.StartAsync(options => options with { AllowedHostNames = ["nas.lan"] });
        using var listed = new HttpRequestMessage(HttpMethod.Get, "/api/auth/status");
        listed.Headers.Host = $"nas.lan:{named.Base.Port}";
        Assert.Equal(HttpStatusCode.OK, (await named.Http.SendAsync(listed)).StatusCode);
    }

    [Fact]
    public async Task AnUploadIsAnsweredWithAReferenceNotThePathAndTheReferenceIsWhatRemoteCallsTake()
    {
        await using var server = await AuthServer.StartAsync();
        using var client = await AuthServer.SetUpAsync(server);
        using var uploaded = await client.SendAsync(HttpMethod.Post, "/api/upload/torrent", null,
            request => request.Content = new ByteArrayContent("d4:infod4:name1:aee"u8.ToArray()));
        Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
        var text = await uploaded.Content.ReadAsStringAsync();
        Assert.DoesNotContain(server.Data.Replace("\\", "\\\\"), text);
        Assert.DoesNotContain("\"path\"", text);
        var reference = (await client.JsonAsync(uploaded)).GetProperty("upload").GetString()!;
        Assert.Matches("^upload:[0-9a-f]{32}$", reference);
        Assert.True(File.Exists(Path.Combine(server.Data, TorrentUploads.FolderName, reference["upload:".Length..] + ".torrent")));

        // The server reads it as that .torrent file (here: no provider account to send it to), while text that only
        // looks like a reference is no link at all.
        var (status, reply) = await client.IpcAsync(MessageTypes.RemoteClassify, new { text = reference });
        Assert.Equal(HttpStatusCode.OK, status);
        var reason = reply!.Payload!.Value.GetProperty("reason").GetString()!;
        var (_, unknown) = await client.IpcAsync(MessageTypes.RemoteClassify, new { text = "upload:not-one-of-ours" });
        var unknownReason = unknown!.Payload!.Value.GetProperty("reason").GetString()!;
        Assert.Contains("magnets", unknownReason);
        Assert.DoesNotContain("magnets", reason);
        Assert.NotEqual(unknownReason, reason);
    }
}

/// <summary>A JSON body whose length isn't told, so it goes chunked.</summary>
internal sealed class ChunkedJson : HttpContent
{
    private readonly byte[] _bytes;

    public ChunkedJson(byte[] bytes)
    {
        _bytes = bytes;
        Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
    }

    protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) => stream.WriteAsync(_bytes).AsTask();

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }
}
