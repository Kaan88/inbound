using System.Net;
using System.Net.Http.Headers;
using DownloadManager.Core;

namespace DownloadManager.Engine.Tests;

public sealed class FileMetadataTests
{
    [Fact]
    public async Task HeadPreviewResolvesServerNameAndNeverReadsResponseBody()
    {
        var content = new UnreadableContent(123456);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileNameStar = "report ü.zip" };
        using var handler = new Handler(request =>
        {
            Assert.Equal(HttpMethod.Head, request.Method);
            Assert.Null(request.Headers.Range);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        using var engine = new HttpDownloadEngine(handler: handler);
        var metadata = await engine.ReadMetadataAsync(new DownloadJob { Url = "https://example.com/opaque" }, default);
        Assert.Equal("report ü.zip", metadata.FileName);
        Assert.Equal(123456, metadata.TotalBytes);
        Assert.Equal("application/zip", metadata.ContentType);
        Assert.True(content.Disposed);
        Assert.False(content.Read);
    }

    [Theory]
    [InlineData(HttpStatusCode.PartialContent, 999999)]
    [InlineData(HttpStatusCode.OK, 1)]
    public async Task UnsupportedHeadFallsBackToOneByteRangeAndUsesFullRepresentationLength(HttpStatusCode status, long expected)
    {
        var calls = 0;
        var content = new UnreadableContent(1);
        if (status == HttpStatusCode.PartialContent) content.Headers.ContentRange = new ContentRangeHeaderValue(0, 0, 999999);
        using var handler = new Handler(request =>
        {
            if (++calls == 1)
            {
                Assert.Equal(HttpMethod.Head, request.Method);
                return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
            }
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("bytes=0-0", request.Headers.Range?.ToString());
            return new HttpResponseMessage(status) { Content = content };
        });
        using var engine = new HttpDownloadEngine(handler: handler);
        var metadata = await engine.ReadMetadataAsync(new DownloadJob { Url = "https://example.com/file.bin" }, default);
        Assert.Equal(2, calls);
        Assert.Equal(expected, metadata.TotalBytes);
        Assert.False(content.Read);
        Assert.True(content.Disposed);
    }

    [Theory]
    [InlineData("https://other.example/file.bin", false)]
    [InlineData("https://example.com/next/file.bin", true)]
    public async Task PreviewRedirectsPreserveCredentialBoundaries(string target, bool sameOrigin)
    {
        var calls = 0;
        using var handler = new Handler(request =>
        {
            Assert.Equal(HttpMethod.Head, request.Method);
            if (++calls == 1)
            {
                Assert.True(request.Headers.Contains("Authorization"));
                var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
                redirect.Headers.Location = new Uri(target);
                return redirect;
            }
            Assert.Equal(sameOrigin, request.Headers.Contains("Authorization"));
            Assert.Equal(sameOrigin, request.Headers.Contains("Cookie"));
            Assert.Equal(sameOrigin, request.Headers.Referrer is not null);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnreadableContent(50) };
        });
        using var engine = new HttpDownloadEngine(handler: handler);
        var metadata = await engine.ReadMetadataAsync(new DownloadJob { Url = "https://example.com/start",
            Referrer = "https://example.com/page", RequestHeaders = new() { ["Authorization"] = "Basic test", ["Cookie"] = "test=1" } }, default);
        Assert.Equal(target, metadata.FinalUrl);
    }

    [Fact]
    public async Task PreviewRejectsLocalRedirectAndDoesNotRetryUnauthorizedHeadWithGet()
    {
        var calls = 0;
        using var redirectHandler = new Handler(request =>
        {
            calls++;
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("http://127.0.0.1/private");
            return response;
        });
        using var engine = new HttpDownloadEngine(new EngineOptions { AllowLocalNetwork = false }, handler: redirectHandler);
        await Assert.ThrowsAsync<HttpRequestException>(() => engine.ReadMetadataAsync(new DownloadJob { Url = "https://example.com/start" }, default));
        Assert.Equal(1, calls);
        calls = 0;
        using var deniedHandler = new Handler(request => { calls++; return new HttpResponseMessage(HttpStatusCode.Forbidden); });
        using var denied = new HttpDownloadEngine(handler: deniedHandler);
        await Assert.ThrowsAsync<HttpRequestException>(() => denied.ReadMetadataAsync(new DownloadJob { Url = "https://example.com/private" }, default));
        Assert.Equal(1, calls);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }

    private sealed class UnreadableContent(long length) : HttpContent
    {
        public bool Read { get; private set; }
        public bool Disposed { get; private set; }
        protected override bool TryComputeLength(out long value) { value = length; return true; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        { Read = true; throw new InvalidOperationException("Metadata must not consume the response body."); }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
