using System.Net;
using System.Text;
using System.Text.Json;
using DownloadManager.Mobile.Updates;

namespace DownloadManager.Mobile.Tests;

public sealed class GithubAndroidFeedTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(respond(request)); }
    }

    [Theory]
    [InlineData("http://github.com/Kaan88/inbound/releases/download/a/a.apk")]
    [InlineData("https://github.com/another/repo/releases/download/a/a.apk")]
    [InlineData("https://github.com/Kaan88/inbound/releases/download/a/a.apk?token=secret")]
    [InlineData("https://github.com:444/Kaan88/inbound/releases/download/a/a.apk")]
    [InlineData("https://user:password@github.com/Kaan88/inbound/releases/download/a/a.apk")]
    public void NonReleaseAddressesAreRefused(string url) => Assert.False(AndroidUpdateTrust.IsReleaseUrl(url));

    [Fact]
    public async Task UnpublishedAndroidFeedIsNotAnError()
    {
        using var handler = new Handler(_ => new(HttpStatusCode.NotFound));
        using var http = new HttpClient(handler);
        Assert.Null(await new GithubAndroidFeed(http).ReadAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("http://release-assets.githubusercontent.com/download")]
    [InlineData("https://example.com/download")]
    [InlineData("https://github.com/another/repo/releases/download/a/manifest.json")]
    public async Task RedirectsOutsideTheHttpsReleaseBoundaryAreRefused(string redirect)
    {
        using var handler = new Handler(_ => { var r = new HttpResponseMessage(HttpStatusCode.Redirect); r.Headers.Location = new(redirect); return r; });
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => new GithubAndroidFeed(http).ReadAsync(CancellationToken.None));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task ARealisticGithubCdnRedirectReadsCamelCaseMetadata()
    {
        using var handler = new Handler(request =>
        {
            if (request.RequestUri!.Host == "github.com")
            { var r = new HttpResponseMessage(HttpStatusCode.Redirect); r.Headers.Location = new("https://release-assets.githubusercontent.com/file?signature=opaque"); return r; }
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"versionCode\":3,\"versionName\":\"0.0.3\"}") };
        });
        using var http = new HttpClient(handler);
        var result = await new GithubAndroidFeed(http).ReadAsync(CancellationToken.None);
        Assert.NotNull(result); Assert.Equal(3, result.VersionCode); Assert.Equal("0.0.3", result.VersionName);
    }

    [Fact]
    public async Task OversizedMetadataIsBoundedBeforeParsing()
    {
        using var handler = new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[65537]) });
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => new GithubAndroidFeed(http).ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RedirectLoopsAreBounded()
    {
        using var handler = new Handler(_ => { var r = new HttpResponseMessage(HttpStatusCode.Redirect); r.Headers.Location = new(AndroidUpdateTrust.FeedUrl); return r; });
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => new GithubAndroidFeed(http).ReadAsync(CancellationToken.None));
        Assert.InRange(handler.Calls, 1, 6);
    }

    [Theory] [InlineData(2)] [InlineData(4)]
    public async Task PayloadLengthDifferentFromSignedSizeIsRefused(long actual)
    {
        using var handler = new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[actual]) });
        using var http = new HttpClient(handler);
        var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".apk.part");
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => new GithubAndroidFeed(http).DownloadAsync(new()
            { ApkUrl = "https://github.com/Kaan88/inbound/releases/download/android-v0.0.3/a.apk", Size = 3 }, file, _ => { }, CancellationToken.None));
            Assert.False(File.Exists(file));
        }
        finally { File.Delete(file); }
    }
}
