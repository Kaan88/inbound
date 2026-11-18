using DownloadManager.Contracts;
using DownloadManager.Persistence;
using DownloadManager.Core;

namespace DownloadManager.Integration.Tests;

public sealed partial class RuntimeServiceTests
{
    [Fact]
    public async Task BrowserRefreshReplacesStaleCredentialsAndPersistsTheFreshContext()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var added = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/file/browser-refresh.bin?size=4096&seed=21", FileName = "browser-refresh.bin",
            Headers = new() { ["Cookie"] = "old=login", ["Authorization"] = "old-authorization", ["X-Old"] = "stale" },
            UserAgent = "old-agent", Referrer = "https://old.example/page",
        });
        Assert.True(added.Accepted, added.Error);
        await ctx.WaitFor(added.Job!.Id, j => j.State == "completed");
        var result = await ctx.Service.RefreshUrlAsync(added.Job.Id,
            $"{_server.BaseAddress}/file/browser-refresh.bin?size=4096&seed=21&fresh=1", browserContext: new BrowserRequestContext
            {
                Headers = new() { ["Cookie"] = "fresh=login" }, UserAgent = "fresh-agent",
                PageUrl = $"{_server.BaseAddress}/page#fragment", Referrer = $"{_server.BaseAddress}/page#fragment",
            });
        Assert.True(result.Ok, result.Error);
        Assert.Equal("completed", result.Job!.State);
        var persisted = (await new JsonJobStore(ctx.Paths).LoadAsync(added.Job.Id))!;
        Assert.Equal("fresh=login", Assert.Single(persisted.RequestHeaders).Value);
        Assert.Equal("fresh-agent", persisted.UserAgent);
        Assert.Equal($"{_server.BaseAddress}/page", persisted.PageUrl);
        Assert.Equal($"{_server.BaseAddress}/page", persisted.Referrer);
    }

    [Theory]
    [InlineData("cookie-other-origin")]
    [InlineData("authorization")]
    [InlineData("header-injection")]
    [InlineData("invalid-page")]
    [InlineData("agent-injection")]
    public async Task InvalidBrowserRefreshLeavesAddressAndCredentialsUnchanged(string invalid)
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var original = $"{_server.BaseAddress}/file/browser-invalid.bin?size=4096&seed=22";
        var added = await ctx.Service.AddJobAsync(new AddJobRequest { Url = original, FileName = "browser-invalid.bin", Headers = new() { ["Cookie"] = "old=login" } });
        Assert.True(added.Accepted, added.Error);
        await ctx.WaitFor(added.Job!.Id, j => j.State == "completed");
        var context = new BrowserRequestContext { PageUrl = $"{_server.BaseAddress}/page", Headers = new() { ["Cookie"] = "fresh=login" } };
        switch (invalid)
        {
            case "cookie-other-origin": context.PageUrl = "https://other.example/page"; break;
            case "authorization": context.Headers = new() { ["Authorization"] = "new" }; break;
            case "header-injection": context.Headers["Cookie"] = "x=y\r\nX-Injected: value"; break;
            case "invalid-page": context.PageUrl = "file:///private"; break;
            case "agent-injection": context.UserAgent = "agent\nCookie: extra"; break;
        }
        var result = await ctx.Service.RefreshUrlAsync(added.Job.Id, original + "&fresh=1", browserContext: context);
        Assert.False(result.Ok);
        Assert.Equal(original, ctx.Service.GetJob(added.Job.Id)!.Url);
        var persisted = (await new JsonJobStore(ctx.Paths).LoadAsync(added.Job.Id))!;
        Assert.Equal("old=login", persisted.RequestHeaders["Cookie"]);
        Assert.Equal(original, persisted.Url);
    }

    [Fact]
    public async Task BrowserRefreshCanRecoverAnAuthenticatedFailure()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var url = $"{_server.BaseAddress}/auth/file/browser-auth.bin?size=65536&seed=23";
        var added = await ctx.Service.AddJobAsync(new AddJobRequest { Url = url, FileName = "browser-auth.bin" });
        Assert.True(added.Accepted, added.Error);
        await ctx.WaitFor(added.Job!.Id, j => j.State == "failed");
        var result = await ctx.Service.RefreshUrlAsync(added.Job.Id, url, browserContext: new BrowserRequestContext
        {
            Headers = new() { ["Cookie"] = "dmsession=valid-auth-token" },
            PageUrl = $"{_server.BaseAddress}/auth/login", Referrer = $"{_server.BaseAddress}/auth/login", UserAgent = "Mozilla/5.0 Chrome/120.0 InboundBrowserTest",
        });
        Assert.True(result.Ok, result.Error);
        var completed = await ctx.WaitFor(added.Job.Id, j => j.State == "completed");
        var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(TestServer.TestContent.Generate(23, 65536))).ToLowerInvariant();
        Assert.Equal(expected, await Hashing.Sha256FileAsync(completed.FinalFilePath));
    }
}
