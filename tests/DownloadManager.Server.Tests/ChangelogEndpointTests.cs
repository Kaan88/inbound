using System.Net;
using DownloadManager.Contracts;

namespace DownloadManager.Server.Tests;

/// <summary>UPD.N1: the web UI's What's new reads the server's changelog with changelog.get, signed in only.</summary>
public sealed class ChangelogEndpointTests
{
    [Fact]
    public async Task ASignedInPageGetsTheServersChangelog()
    {
        await using var server = await ServerHarness.StartAsync();
        var changelog = await server.AskAsync<ChangelogDto>(MessageTypes.ChangelogGet, new { });
        Assert.NotEmpty(changelog.Versions);
        Assert.Equal(Changelog.Embedded.Select(version => version.Version), changelog.Versions.Select(version => version.Version));
        Assert.All(changelog.Versions.SelectMany(version => version.Sections).SelectMany(section => section.Items),
            item => Assert.All(item.Tags, tag => Assert.True(ChangelogTags.IsKnown(tag))));
    }

    [Fact]
    public async Task WithoutASessionItIsRefusedAndSignedInItAnswers()
    {
        await using var server = await AuthServer.StartAsync();
        using var anonymous = new AuthClient(server.Base);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.IpcAsync(MessageTypes.ChangelogGet, new { })).Status);

        using var client = await AuthServer.SetUpAsync(server);
        var (status, reply) = await client.IpcAsync(MessageTypes.ChangelogGet, new { });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(MessageTypes.ChangelogResult, reply!.Type);
        Assert.NotEmpty(reply.GetPayload<ChangelogDto>()!.Versions);
    }
}
