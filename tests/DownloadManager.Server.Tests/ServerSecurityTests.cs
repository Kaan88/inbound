using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using DownloadManager.Contracts;

namespace DownloadManager.Server.Tests;

public sealed class ServerSecurityTests
{
    [Fact]
    public async Task AWebSocketFromAnotherSiteIsRefusedAndOneFromItsOwnOriginIsNot()
    {
        await using var server = await ServerHarness.StartAsync();

        var refused = await Assert.ThrowsAnyAsync<WebSocketException>(() => server.ConnectAsync("http://evil.example"));
        Assert.Contains("403", refused.Message);

        await using var own = await server.ConnectAsync($"http://{server.Base.Authority}");
        Assert.Equal(MessageTypes.ServerHello, (await own.ReadAsync())!.Type);
    }

    [Fact]
    public async Task APostFromAnotherSiteIsRefused()
    {
        await using var server = await ServerHarness.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/ipc")
        {
            Content = JsonContent.Create(IpcMessage.Create(MessageTypes.JobsPauseAll, new { }), options: IpcJson.Options),
        };
        request.Headers.Add("Origin", "http://evil.example");
        using var response = await server.Http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        // Another port on the same machine is another origin too.
        using var otherPort = new HttpRequestMessage(HttpMethod.Post, "/api/ipc")
        {
            Content = JsonContent.Create(IpcMessage.Create(MessageTypes.HostPing, new { }), options: IpcJson.Options),
        };
        otherPort.Headers.Add("Origin", $"http://{server.Base.Host}:{server.Base.Port + 1}");
        using var otherPortResponse = await server.Http.SendAsync(otherPort);
        Assert.Equal(HttpStatusCode.Forbidden, otherPortResponse.StatusCode);
    }

    [Fact]
    public async Task ABrowserThatReachesTheServerByAnUnlistedNameIsRefusedAsRebinding()
    {
        await using var server = await ServerHarness.StartAsync();

        Assert.Equal(HttpStatusCode.Forbidden, await PostWithHostAsync(server, "rebound.example"));
        Assert.Equal(HttpStatusCode.OK, await PostWithHostAsync(server, "localhost"));
        Assert.Equal(HttpStatusCode.OK, await PostWithHostAsync(server, server.Base.Host));

        await using var named = await ServerHarness.StartAsync(options => options with { AllowedHostNames = ["nas.lan"] });
        Assert.Equal(HttpStatusCode.OK, await PostWithHostAsync(named, "nas.lan"));
    }

    [Fact]
    public async Task HealthHasNoOriginCheck()
    {
        await using var server = await ServerHarness.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Add("Origin", "http://evil.example");
        using var response = await server.Http.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task WithSignInOffListeningBeyondLoopbackIsRefusedWithoutTheExplicitVariable()
    {
        foreach (var url in new[] { "http://0.0.0.0:8080", "http://*:8080", "http://+:8080", "http://[::]:8080", "http://192.168.1.5:8080", "http://nas:8080" })
        {
            var options = new ServerOptions { DataDirectory = Path.Combine(ServerHarness.NewWork(), "data"), Urls = [url], SignInRequired = false };
            var refused = await Assert.ThrowsAsync<ServerStartException>(() => ServerApp.StartAsync(options, logToConsole: false));
            Assert.Contains(ServerOptions.AllowUnauthenticatedVariable, refused.Message);
            Assert.Contains(ServerOptions.AuthVariable, refused.Message);
            Assert.Null((options with { AllowUnauthenticated = true }).DescribeUnsafeBinding());
            // With sign-in on (D2), any address may be used.
            Assert.Null((options with { SignInRequired = true }).DescribeUnsafeBinding());
        }

        foreach (var url in new[] { "http://127.0.0.1:8080", "http://localhost:8080", "http://[::1]:8080", "http://127.0.0.2:1" })
            Assert.Null(new ServerOptions { Urls = [url], SignInRequired = false }.DescribeUnsafeBinding());

        // The default listens on every address with sign-in on, which is allowed; turning sign-in off needs the variable.
        Assert.Null(ServerOptions.FromEnvironment(_ => null).DescribeUnsafeBinding());
        Assert.NotNull(ServerOptions.FromEnvironment(name => name == ServerOptions.AuthVariable ? "off" : null).DescribeUnsafeBinding());
        Assert.Null(ServerOptions.FromEnvironment(name => name switch
        {
            ServerOptions.AuthVariable => "off",
            ServerOptions.AllowUnauthenticatedVariable => "1",
            _ => null,
        }).DescribeUnsafeBinding());
    }

    [Fact]
    public void TheEnvironmentGivesDataRootsAndAddresses()
    {
        var variables = new Dictionary<string, string>
        {
            [ServerOptions.DataVariable] = "/srv/inbound",
            [ServerOptions.RootsVariable] = "/media/a;/media/b",
            ["ASPNETCORE_URLS"] = "http://127.0.0.1:9000",
            [OriginCheck.HostsVariable] = "nas.lan, inbound.home",
        };
        var options = ServerOptions.FromEnvironment(name => variables.GetValueOrDefault(name));
        Assert.Equal("/srv/inbound", options.DataDirectory);
        Assert.Equal(["/media/a", "/media/b"], options.Roots);
        Assert.Equal(["http://127.0.0.1:9000"], options.Urls);
        Assert.Equal(["nas.lan", "inbound.home"], options.AllowedHostNames);

        variables[ServerOptions.UrlsVariable] = "http://127.0.0.1:9001";
        Assert.Equal(["http://127.0.0.1:9001"], ServerOptions.FromEnvironment(name => variables.GetValueOrDefault(name)).Urls);

        var defaults = ServerOptions.FromEnvironment(_ => null);
        Assert.Equal(ServerOptions.DefaultData, defaults.DataDirectory);
        Assert.Equal([ServerOptions.DefaultRoot], defaults.Roots);
        Assert.Equal([ServerOptions.DefaultUrls], defaults.Urls);
    }

    private static async Task<HttpStatusCode> PostWithHostAsync(ServerHarness server, string host)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/ipc")
        {
            Content = JsonContent.Create(IpcMessage.Create(MessageTypes.HostPing, new { }), options: IpcJson.Options),
        };
        request.Headers.Host = $"{host}:{server.Base.Port}";
        request.Headers.Add("Origin", $"http://{host}:{server.Base.Port}");
        using var response = await server.Http.SendAsync(request);
        return response.StatusCode;
    }
}
