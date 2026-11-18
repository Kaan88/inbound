using DownloadManager.Contracts;
using DownloadManager.Mobile;
using DownloadManager.Mobile.Links;

namespace DownloadManager.Mobile.Tests;

public sealed class BrowserRefreshTests
{
    private static CapturedDownload Capture() => new("https://files.example/new.zip?token=one", pageUrl: "https://files.example/page", userAgent: "test-agent", cookie: "session=fixture", observedReferrer: "https://files.example/page");
    private static async Task<(FakeHostConnection Host, DownloadsModel Model)> Open(params string[] capabilities)
    {
        var host = new FakeHostConnection(); var model = new DownloadsModel(host, action => action(), TimeSpan.Zero);
        await model.StartAsync(CancellationToken.None);
        host.Push(IpcMessage.Create(MessageTypes.ServerHello, new HostHelloDto { Capabilities = capabilities.ToList() }));
        return (host, model);
    }
    [Fact] public void PageUrlWithoutObservedReferrerDoesNotInventOne()
    {
        var capture = new CapturedDownload("https://files.example/file", pageUrl: "https://files.example/page");
        Assert.Null(capture.Referrer);
        Assert.Null(capture.Refresh(Guid.NewGuid(), false, false).BrowserContext!.Referrer);
    }
    [Fact] public void RemoteRefreshRequiresConsentBeforeCookieIsExposed()
    {
        var capture = Capture(); var id = Guid.NewGuid();
        Assert.Throws<InvalidOperationException>(() => capture.Refresh(id, remote: true, cookieConsent: false));
        Assert.Equal("session=fixture", capture.Refresh(id, remote: true, cookieConsent: true).BrowserContext!.Headers["Cookie"]);
        Assert.Equal("session=fixture", capture.Refresh(id, remote: false, cookieConsent: false).BrowserContext!.Headers["Cookie"]);
    }
    [Fact] public async Task OldHostRefusesBrowserContextWithoutSendingCommand()
    {
        var (host, model) = await Open();
        var result = await model.RefreshUrlAsync(Capture().Refresh(Guid.NewGuid(), false, false));
        Assert.False(result.Ok); Assert.Empty(host.Sent);
    }
    [Fact] public async Task LegacyUrlOnlyRefreshWorksWithoutCapability()
    {
        var (host, model) = await Open(); var id = Guid.NewGuid();
        host.Reply = frame => IpcMessage.Create(MessageTypes.JobActionResult, new JobActionResponse { Ok = true }, frame.Id);
        var result = await model.RefreshUrlAsync(new JobRefreshUrlRequest { JobId = id, Url = "https://files.example/new.zip" });
        Assert.True(result.Ok); var sent = Assert.Single(host.Sent); Assert.Equal(MessageTypes.JobRefreshUrl, sent.Type);
        var request = sent.GetPayload<JobRefreshUrlRequest>(); Assert.Equal(id, request.JobId); Assert.Null(request.BrowserContext);
    }
    [Fact] public async Task AdvertisedHostReceivesExactTypedContextAndAckDeterminesResult()
    {
        var (host, model) = await Open("browserRefresh"); var capture = Capture(); var id = Guid.NewGuid();
        var accepted = false;
        host.Reply = frame => IpcMessage.Create(MessageTypes.JobActionResult, new JobActionResponse { Ok = accepted, Error = "refused" }, frame.Id);
        var request = capture.Refresh(id, true, true);
        Assert.False((await model.RefreshUrlAsync(request)).Ok);
        var sent = Assert.Single(host.Sent); Assert.Equal(MessageTypes.JobRefreshUrl, sent.Type);
        var actual = sent.GetPayload<JobRefreshUrlRequest>();
        Assert.Equal(id, actual.JobId); Assert.Equal(request.Url, actual.Url);
        Assert.NotNull(actual.BrowserContext); Assert.Equal("session=fixture", actual.BrowserContext.Headers["Cookie"]);
        Assert.Equal("test-agent", actual.BrowserContext.UserAgent); Assert.Equal("https://files.example/page", actual.BrowserContext.Referrer);
        Assert.Equal("https://files.example/page", actual.BrowserContext.PageUrl);
        accepted = true; Assert.True((await model.RefreshUrlAsync(request)).Ok);
    }
}
