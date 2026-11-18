using DownloadManager.Mobile.Links;

namespace DownloadManager.Mobile.Tests;

public sealed class CapturedDownloadTests
{
    private static CapturedDownload SignedIn() => new("https://files.example/file.zip", "../file.zip", pageUrl: "https://files.example/page?private=token#fragment",
        userAgent: "test-browser", cookie: "session=private-cookie", observedReferrer: "https://files.example/page?private=token");

    [Fact] public void RemoteProbeAndAdmissionBothRequireConsentBeforeExposingCookies()
    {
        var capture = SignedIn();
        Assert.Throws<InvalidOperationException>(() => capture.Probe(true, false));
        Assert.Throws<InvalidOperationException>(() => capture.Add(true, false, null, 4, false));
        Assert.Equal("session=private-cookie", capture.Probe(true, true).Headers["Cookie"]);
        Assert.Equal("session=private-cookie", capture.Add(false, false, null, 4, false).Headers["Cookie"]);
        Assert.DoesNotContain("private-cookie", capture.ToString());
    }
    [Fact] public void CookieCannotBeAttachedAcrossOriginsOrDowngrades()
    {
        Assert.Throws<ArgumentException>(() => new CapturedDownload("https://cdn.example/file", pageUrl: "https://files.example/page", cookie: "secret"));
        Assert.Throws<ArgumentException>(() => new CapturedDownload("http://files.example/file", pageUrl: "https://files.example/page", cookie: "secret"));
    }
    [Fact] public void DefaultCrossOriginReferrerOmitsSecretQueryAndDowngradeOmitsAll()
    {
        Assert.Equal("https://files.example/", CapturedDownload.SafeReferrer("https://files.example/private?key=secret#fragment", "https://cdn.example/file"));
        Assert.Null(CapturedDownload.SafeReferrer("https://files.example/private?key=secret", "http://cdn.example/file"));
        Assert.Equal("https://files.example/page?private=token", SignedIn().Referrer);
    }
    [Fact] public void BrowserObservedReferrerIsHonoredWithoutFragmentsOrDowngrades()
    {
        Assert.Equal("https://frames.example/source", CapturedDownload.SafeReferrer("https://page.example/full", "https://cdn.example/file", "https://frames.example/source#fragment"));
        Assert.Null(CapturedDownload.SafeReferrer(null, "http://cdn.example/file", "https://frames.example/source"));
    }
    [Fact] public void HeadersRejectControlCharactersAndRequestIdentityStaysStable()
    {
        Assert.Throws<ArgumentException>(() => new CapturedDownload("https://files.example/file", userAgent: "agent\r\nCookie: bad"));
        var capture = SignedIn();
        var first = capture.Add(false, false, "/downloads", 8, false);
        var second = capture.Add(false, false, "/downloads", 8, true);
        Assert.Equal(first.ClientRequestId, second.ClientRequestId);
        Assert.Equal("file.zip", first.FileName);
        Assert.Equal("https://files.example/page?private=token", first.PageUrl);
        Assert.Equal("later", second.StartMode);
    }
}
