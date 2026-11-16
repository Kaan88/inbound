using DownloadManager.Server;
using Microsoft.Extensions.FileProviders;
using Xunit;

namespace DownloadManager.Server.Tests;

/// <summary>PERF.20: which compressed copy of a built web UI file is sent, and with which content type.</summary>
public sealed class StaticAssetCompressionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "inbound-assets-" + Guid.NewGuid().ToString("N"));
    private readonly PhysicalFileProvider _files;

    public StaticAssetCompressionTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "assets"));
        File.WriteAllText(Path.Combine(_root, "assets", "index-abc.js"), "x");
        File.WriteAllText(Path.Combine(_root, "assets", "index-abc.js.br"), "b");
        File.WriteAllText(Path.Combine(_root, "assets", "index-abc.js.gz"), "g");
        File.WriteAllText(Path.Combine(_root, "assets", "index-abc.css"), "x");
        File.WriteAllText(Path.Combine(_root, "assets", "index-abc.css.gz"), "g");
        _files = new PhysicalFileProvider(_root);
    }

    public void Dispose()
    {
        _files.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData("gzip, deflate, br, zstd", "br", true)]
    [InlineData("gzip, deflate", "br", false)]
    [InlineData("br;q=0, gzip", "br", false)]
    [InlineData("br;q=0.5", "br", true)]
    [InlineData("*", "gzip", true)]
    [InlineData("", "gzip", false)]
    [InlineData("GZIP", "gzip", true)]
    public void ReadsAcceptEncoding(string header, string encoding, bool expected)
        => Assert.Equal(expected, StaticAssetCompression.Accepts(header, encoding));

    [Fact]
    public void BrotliComesFirstThenGzipThenThePlainFile()
    {
        Assert.Equal(("br", ".br"), StaticAssetCompression.Choose("gzip, br", "/assets/index-abc.js", _files));
        Assert.Equal(("gzip", ".gz"), StaticAssetCompression.Choose("gzip", "/assets/index-abc.js", _files));
        // No Brotli copy of the style sheet: gzip, then nothing.
        Assert.Equal(("gzip", ".gz"), StaticAssetCompression.Choose("br, gzip", "/assets/index-abc.css", _files));
        Assert.Null(StaticAssetCompression.Choose("br", "/assets/index-abc.css", _files));
        Assert.Null(StaticAssetCompression.Choose("", "/assets/index-abc.js", _files));
        // A path that leaves the folder finds nothing.
        Assert.Null(StaticAssetCompression.Choose("br, gzip", "/assets/../../index-abc.js", _files));
    }

    [Fact]
    public void ACompressedCopyHasTheTypeOfItsFileOnlyUnderAssets()
    {
        var types = new StaticAssetCompression.ContentTypes();
        Assert.True(types.TryGetContentType("/assets/index-abc.js.br", out var js));
        Assert.Equal("text/javascript", js);
        Assert.True(types.TryGetContentType("/assets/index-abc.css.gz", out var css));
        Assert.Equal("text/css", css);
        Assert.True(types.TryGetContentType("/favicon.svg", out var svg));
        Assert.Equal("image/svg+xml", svg);
        // Outside /assets/ a compressed copy keeps its own (gzip) type.
        Assert.True(types.TryGetContentType("/favicon.svg.gz", out var gz));
        Assert.NotEqual("image/svg+xml", gz);
    }
}
