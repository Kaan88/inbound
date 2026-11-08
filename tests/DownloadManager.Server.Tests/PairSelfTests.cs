using System.Net;
using DownloadManager.Contracts;

namespace DownloadManager.Server.Tests;

/// <summary><c>DELETE /api/pair/self</c>: the extension's Unpair revokes the token it is sent with, and nothing else.</summary>
public sealed class PairSelfTests
{
    private const string FirefoxOrigin = "moz-extension://5f2c8a8e-1111-2222-3333-444455556666";

    private static Task<HttpResponseMessage> UnpairAsync(AuthClient client, string? bearer, string path = "/api/pair/self") =>
        client.SendAsync(HttpMethod.Delete, path, null, request =>
        {
            if (bearer is not null) request.Headers.Add("Authorization", "Bearer " + bearer);
        });

    [Fact]
    public async Task ATokenRevokesItselfOnlyAndAnswers204()
    {
        await using var server = await AuthServer.StartAsync();
        using var user = await AuthServer.SetUpAsync(server);
        var mine = await PairingTests.PairAsync(server, await PairingTests.NewCodeAsync(user), "this one");
        var other = await PairingTests.PairAsync(server, await PairingTests.NewCodeAsync(user), "the other one");

        using var list = await user.GetAsync("/api/auth/tokens");
        var otherId = (await user.JsonAsync(list)).GetProperty("tokens").EnumerateArray()
            .Single(token => token.GetProperty("name").GetString() == "the other one").GetProperty("id").GetString()!;

        // An id in the query is ignored: what goes is the calling token's own record.
        using var extension = new AuthClient(server.Base) { Origin = PairingTests.ChromeOrigin };
        using var unpaired = await UnpairAsync(extension, mine, "/api/pair/self?id=" + otherId);
        Assert.Equal(HttpStatusCode.NoContent, unpaired.StatusCode);
        Assert.Equal(PairingTests.ChromeOrigin, unpaired.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.False(unpaired.Headers.Contains("Access-Control-Allow-Credentials"));

        Assert.Equal(HttpStatusCode.Unauthorized, (await extension.IpcAsync(MessageTypes.HostPing, new { }, mine)).Status);
        Assert.Equal(HttpStatusCode.OK, (await extension.IpcAsync(MessageTypes.HostPing, new { }, other)).Status);
        using var after = await user.GetAsync("/api/auth/tokens");
        var left = (await user.JsonAsync(after)).GetProperty("tokens");
        Assert.Equal(1, left.GetArrayLength());
        Assert.Equal(otherId, left[0].GetProperty("id").GetString());

        // Once gone, the same token is just unknown.
        Assert.Equal(HttpStatusCode.Unauthorized, (await UnpairAsync(extension, mine)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await UnpairAsync(extension, "ibt_not-a-token")).StatusCode);
    }

    [Fact]
    public async Task OnlyATokenCanUnpairAndOnlyFromOurExtensionsOrNoOrigin()
    {
        await using var server = await AuthServer.StartAsync();
        using var user = await AuthServer.SetUpAsync(server);
        var token = await PairingTests.PairAsync(server, await PairingTests.NewCodeAsync(user));

        // A signed-in browser has no token to revoke; the endpoint never takes one from the session.
        Assert.Equal(HttpStatusCode.Unauthorized, (await UnpairAsync(user, bearer: null)).StatusCode);
        using var anonymous = new AuthClient(server.Base);
        Assert.Equal(HttpStatusCode.Unauthorized, (await UnpairAsync(anonymous, bearer: null)).StatusCode);

        // Another site's page, or another extension, meets the origin check even with the token.
        using var page = new AuthClient(server.Base) { Origin = "http://evil.example" };
        Assert.Equal(HttpStatusCode.Forbidden, (await UnpairAsync(page, token)).StatusCode);
        using var stranger = new AuthClient(server.Base) { Origin = "chrome-extension://abcdefghijklmnopabcdefghijklmnop" };
        Assert.Equal(HttpStatusCode.Forbidden, (await UnpairAsync(stranger, token)).StatusCode);
        // Our extension with POST on this path is not the Unpair call.
        using var post = new AuthClient(server.Base) { Origin = PairingTests.ChromeOrigin };
        Assert.Equal(HttpStatusCode.Forbidden, (await post.SendAsync(HttpMethod.Post, "/api/pair/self", new { },
            request => request.Headers.Add("Authorization", "Bearer " + token))).StatusCode);

        // Firefox's preflight for it: DELETE with the token header, no credentials.
        using var firefox = new AuthClient(server.Base) { Origin = FirefoxOrigin };
        using var preflight = await firefox.SendAsync(HttpMethod.Options, "/api/pair/self", null, request =>
        {
            request.Headers.Add("Access-Control-Request-Method", "DELETE");
            request.Headers.Add("Access-Control-Request-Headers", "authorization");
        });
        Assert.Equal(HttpStatusCode.NoContent, preflight.StatusCode);
        Assert.Equal(FirefoxOrigin, preflight.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Equal("DELETE", preflight.Headers.GetValues("Access-Control-Allow-Methods").Single());
        Assert.False(preflight.Headers.Contains("Access-Control-Allow-Credentials"));

        Assert.Equal(HttpStatusCode.NoContent, (await UnpairAsync(firefox, token)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await firefox.IpcAsync(MessageTypes.HostPing, new { }, token)).Status);
    }

    [Fact]
    public async Task UnpairWorksWithSignInOffToo()
    {
        await using var server = await ServerHarness.StartAsync();
        using var user = new AuthClient(server.Base);
        var token = await PairingTests.PairAsync(server, await PairingTests.NewCodeAsync(user));
        using var extension = new AuthClient(server.Base) { Origin = PairingTests.ChromeOrigin };
        Assert.Equal(HttpStatusCode.NoContent, (await UnpairAsync(extension, token)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await UnpairAsync(extension, token)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await UnpairAsync(user, bearer: null)).StatusCode);
    }
}
