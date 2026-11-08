using System.Net;
using System.Text.Json;
using DownloadManager.Contracts;
using DownloadManager.Server.Auth;

namespace DownloadManager.Server.Tests;

/// <summary>D2 extension pairing: one-time codes, tokens kept as hashes, the extension's role only, revocation, CORS.</summary>
public sealed class PairingTests
{
    internal const string ChromeOrigin = "chrome-extension://" + BrowserBridge.UnpackedExtensionId;

    internal static async Task<string> NewCodeAsync(AuthClient signedIn)
    {
        using var response = await signedIn.PostAsync("/api/auth/pairing/code");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var code = (await signedIn.JsonAsync(response)).GetProperty("code").GetString()!;
        Assert.Matches("^[ABCDEFGHJKLMNPQRSTUVWXYZ23456789]{8}$", code);
        return code;
    }

    internal static async Task<string> PairAsync(ServerHarness server, string code, string name = "Chrome on the desk")
    {
        using var extension = new AuthClient(server.Base) { Origin = ChromeOrigin };
        using var response = await extension.PostAsync("/api/pair", new { code, name });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var token = (await extension.JsonAsync(response)).GetProperty("token").GetString()!;
        Assert.StartsWith(Pairing.TokenPrefix, token);
        return token;
    }

    [Fact]
    public async Task ACodeWorksOnceAndOnlyItsHashIsKept()
    {
        await using var server = await AuthServer.StartAsync();
        using var user = await AuthServer.SetUpAsync(server);
        var code = await NewCodeAsync(user);

        using var extension = new AuthClient(server.Base) { Origin = ChromeOrigin };
        Assert.Equal(HttpStatusCode.Unauthorized, (await extension.PostAsync("/api/pair", new { code = "AAAAAAAA" })).StatusCode);
        await Task.Delay(TimeSpan.FromSeconds(1.2));
        // Dashes, spaces and lower case are forgiven.
        var token = await PairAsync(server, code[..4].ToLowerInvariant() + "-" + code[4..]);
        await Task.Delay(TimeSpan.FromSeconds(0.1));
        Assert.Equal(HttpStatusCode.Unauthorized, (await extension.PostAsync("/api/pair", new { code })).StatusCode);

        var saved = await File.ReadAllTextAsync(Path.Combine(server.Data, AuthStore.FileName));
        Assert.DoesNotContain(token, saved);
        Assert.DoesNotContain(token[Pairing.TokenPrefix.Length..], saved);

        using var list = await user.GetAsync("/api/auth/tokens");
        var tokens = (await user.JsonAsync(list)).GetProperty("tokens");
        Assert.Equal(1, tokens.GetArrayLength());
        Assert.Equal("Chrome on the desk", tokens[0].GetProperty("name").GetString());
        Assert.False(tokens[0].TryGetProperty("hash", out _));
    }

    [Fact]
    public void ACodeExpiresAfterTenMinutesAndAThousandWrongTriesEndIt()
    {
        var work = ServerHarness.NewWork();
        var time = new ManualTime(DateTimeOffset.UtcNow);
        var pairing = new Pairing(AuthStore.Open(work), time);

        var (code, expires) = pairing.NewCode();
        Assert.Equal(time.GetUtcNow() + TimeSpan.FromMinutes(10), expires);
        time.Advance(TimeSpan.FromMinutes(10));
        Assert.Null(pairing.Redeem(code, "late"));

        (code, _) = pairing.NewCode();
        // A few wrong tries (anyone's, from anywhere) don't cancel the owner's code; the waits are per address.
        Assert.Equal(1000, Pairing.MaxWrongTries);
        for (var i = 0; i < Pairing.MaxWrongTries - 1; i++) Assert.Null(pairing.Redeem("ZZZZZZZZ" == code ? "YYYYYYYY" : "ZZZZZZZZ", "guess"));
        Assert.NotNull(pairing.Redeem(code, "after 999 wrong"));

        (code, _) = pairing.NewCode();
        for (var i = 0; i < Pairing.MaxWrongTries; i++) Assert.Null(pairing.Redeem("ZZZZZZZZ" == code ? "YYYYYYYY" : "ZZZZZZZZ", "guess"));
        Assert.Null(pairing.Redeem(code, "after 1000 wrong"));

        // A new code replaces the one before.
        var (first, _) = pairing.NewCode();
        var (second, _) = pairing.NewCode();
        if (first != second) Assert.Null(pairing.Redeem(first, "old"));
        Assert.NotNull(pairing.Redeem(second, "new"));
        Directory.Delete(work, recursive: true);
    }

    [Fact]
    public async Task ATokenOpensTheExtensionsRoleOnlyAndRevokingItEndsIt()
    {
        await using var server = await AuthServer.StartAsync();
        using var user = await AuthServer.SetUpAsync(server);
        var token = await PairAsync(server, await NewCodeAsync(user));
        using var extension = new AuthClient(server.Base) { Origin = ChromeOrigin };

        var ping = await extension.IpcAsync(MessageTypes.HostPing, new { }, token);
        Assert.Equal(HttpStatusCode.OK, ping.Status);
        Assert.True(ping.Reply!.GetPayload<HostActionResult>().Ok);

        // The snapshot comes without its settings.
        var snapshot = await extension.IpcAsync(MessageTypes.JobsQuery, new { }, token);
        Assert.Equal(MessageTypes.JobsSnapshot, snapshot.Reply!.Type);
        Assert.False(snapshot.Reply.Payload!.Value.TryGetProperty("settings", out _));
        Assert.True(snapshot.Reply.Payload!.Value.TryGetProperty("jobs", out _));

        var folders = await extension.IpcAsync(MessageTypes.SettingsDownloadFolders, new { }, token);
        Assert.Equal(MessageTypes.SettingsDownloadFoldersResult, folders.Reply!.Type);
        var browse = await extension.IpcAsync(MessageTypes.FoldersBrowse, new { }, token);
        Assert.Equal(MessageTypes.FoldersBrowseResult, browse.Reply!.Type);
        Assert.True(browse.Reply.GetPayload<FolderBrowseResult>().Ok);

        // Everything outside the role is refused before it reaches the Host.
        foreach (var (type, payload) in new (string, object)[]
                 {
                     (MessageTypes.JobAdd, new { url = "http://127.0.0.1:1/x" }),
                     (MessageTypes.SettingsGet, new { }),
                     (MessageTypes.SettingsUpdate, new { settings = new { } }),
                     (MessageTypes.FoldersCreate, new { parent = server.Root, name = "made" }),
                     (MessageTypes.JobsPauseAll, new { }),
                     (ArchiveMessageTypes.List, new { }),
                     (MessageTypes.RemoteAccountSetKey, new { key = "k" }),
                     (MessageTypes.JobRemove, new { jobIds = new[] { Guid.NewGuid() }, deleteFile = true }),
                     (MessageTypes.JobCancel, new { jobId = Guid.NewGuid(), deleteFile = true }),
                 })
        {
            var refused = await extension.IpcAsync(type, payload, token);
            Assert.Equal(HttpStatusCode.Forbidden, refused.Status);
            Assert.False(refused.Reply!.GetPayload<HostActionResult>().Ok);
        }

        Assert.False(Directory.Exists(Path.Combine(server.Root, "made")));

        // A token is not a session: it opens nothing else.
        var withToken = new AuthClient(server.Base);
        using (var page = await withToken.GetAsync("/api/auth/tokens", request => request.Headers.Add("Authorization", "Bearer " + token)))
            Assert.Equal(HttpStatusCode.Unauthorized, page.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await extension.IpcAsync(MessageTypes.HostPing, new { }, token + "x")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await extension.IpcAsync(MessageTypes.HostPing, new { }, "ibt_madeup")).Status);

        // The last use is noted, and revoking ends the token at once.
        var tokens = (await user.JsonAsync(await user.GetAsync("/api/auth/tokens"))).GetProperty("tokens");
        Assert.Equal(JsonValueKind.String, tokens[0].GetProperty("lastUsedAt").ValueKind);
        var id = tokens[0].GetProperty("id").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await user.SendAsync(HttpMethod.Delete, $"/api/auth/tokens/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await extension.IpcAsync(MessageTypes.HostPing, new { }, token)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await user.SendAsync(HttpMethod.Delete, $"/api/auth/tokens/{id}")).StatusCode);
    }

    [Fact]
    public async Task ChangingThePasswordInTheEnvironmentUnpairsEveryBrowser()
    {
        var work = ServerHarness.NewWork();
        string token;
        await using (var server = await AuthServer.StartAsync(change: options => options with { Password = new SecretValue("the first env password") }, work: work))
        {
            using var user = new AuthClient(server.Base);
            Assert.Equal(HttpStatusCode.OK, (await user.PostAsync("/api/auth/signin", new { password = "the first env password" })).StatusCode);
            token = await PairAsync(server, await NewCodeAsync(user));
        }

        // The same password on the next start keeps the pairing.
        await using (var server = await AuthServer.StartAsync(change: options => options with { Password = new SecretValue("the first env password") }, work: work))
        {
            using var extension = new AuthClient(server.Base) { Origin = ChromeOrigin };
            Assert.Equal(HttpStatusCode.OK, (await extension.IpcAsync(MessageTypes.HostPing, new { }, token)).Status);
        }

        // A rotated secret ends it, as a change on the Account page does.
        await using (var server = await AuthServer.StartAsync(change: options => options with { Password = new SecretValue("the rotated env password") }, work: work))
        {
            using var extension = new AuthClient(server.Base) { Origin = ChromeOrigin };
            Assert.Equal(HttpStatusCode.Unauthorized, (await extension.IpcAsync(MessageTypes.HostPing, new { }, token)).Status);
            Assert.Empty(AuthStore.Open(server.Data).Tokens());
        }

        Directory.Delete(work, recursive: true);
    }

    [Fact]
    public async Task ExtensionOriginsGetCorsOnTheirTwoPathsAndFirefoxOnlyWithAToken()
    {
        await using var server = await AuthServer.StartAsync();
        using var user = await AuthServer.SetUpAsync(server);
        var token = await PairAsync(server, await NewCodeAsync(user));

        // The preflight: our extension's origin, POST, the token header, never cookies.
        using var preflight = new AuthClient(server.Base) { Origin = ChromeOrigin };
        using var options = await preflight.SendAsync(HttpMethod.Options, "/api/ipc", null, request =>
        {
            request.Headers.Add("Access-Control-Request-Method", "POST");
            request.Headers.Add("Access-Control-Request-Headers", "authorization, content-type");
        });
        Assert.Equal(HttpStatusCode.NoContent, options.StatusCode);
        Assert.Equal(ChromeOrigin, options.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.False(options.Headers.Contains("Access-Control-Allow-Credentials"));

        using var firefox = new AuthClient(server.Base) { Origin = "moz-extension://5f2c8a8e-1111-2222-3333-444455556666" };
        Assert.Equal(HttpStatusCode.OK, (await firefox.IpcAsync(MessageTypes.HostPing, new { }, token)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await firefox.IpcAsync(MessageTypes.HostPing, new { })).Status);

        // Another extension's origin, and our extension anywhere but its two paths, meet the ordinary origin check.
        using var other = new AuthClient(server.Base) { Origin = "chrome-extension://abcdefghijklmnopabcdefghijklmnop" };
        Assert.Equal(HttpStatusCode.Forbidden, (await other.IpcAsync(MessageTypes.HostPing, new { }, token)).Status);
        using var elsewhere = new AuthClient(server.Base) { Origin = ChromeOrigin };
        Assert.Equal(HttpStatusCode.Forbidden, (await elsewhere.PostAsync("/api/auth/signin", new { password = AuthServer.Password })).StatusCode);
        var socket = await Assert.ThrowsAnyAsync<System.Net.WebSockets.WebSocketException>(() => server.ConnectAsync(ChromeOrigin, user.Session));
        Assert.Contains("403", socket.Message);
    }

    [Fact]
    public void TheServerExtensionRoleIsTheNativeRolePlusFolderBrowsing()
    {
        foreach (var type in new[]
                 {
                     MessageTypes.HandoffRequest, MessageTypes.HostPing, MessageTypes.JobsQuery, MessageTypes.JobPause,
                     MessageTypes.JobResume, MessageTypes.JobRetry, MessageTypes.JobCancel, MessageTypes.JobRemove,
                     MessageTypes.SettingsDownloadFolders,
                 })
        {
            Assert.True(NativeBridgePolicy.AllowFrom(BridgeRole.BrowserExtension, type));
            Assert.True(NativeBridgePolicy.AllowFrom(BridgeRole.ServerExtension, type));
        }

        Assert.True(NativeBridgePolicy.AllowFrom(BridgeRole.ServerExtension, MessageTypes.FoldersBrowse));
        Assert.False(NativeBridgePolicy.AllowFrom(BridgeRole.BrowserExtension, MessageTypes.FoldersBrowse));
        Assert.False(NativeBridgePolicy.AllowFrom(BridgeRole.ServerExtension, MessageTypes.FoldersCreate));
        Assert.True(NativeBridgePolicy.AllowTo(BridgeRole.ServerExtension, MessageTypes.FoldersBrowseResult));
        Assert.False(NativeBridgePolicy.AllowTo(BridgeRole.ServerExtension, MessageTypes.GroupsChanged));

        // One download per action, as from the native host.
        var many = IpcMessage.Create(MessageTypes.JobRemove, new { jobIds = new[] { Guid.NewGuid(), Guid.NewGuid() } });
        Assert.False(NativeBridgePolicy.AllowFrom(BridgeRole.ServerExtension, many));
        var one = IpcMessage.Create(MessageTypes.JobRemove, new { jobId = Guid.NewGuid(), deleteFile = true });
        Assert.True(NativeBridgePolicy.AllowFrom(BridgeRole.ServerExtension, one));
    }
}
