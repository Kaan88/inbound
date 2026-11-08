using TestServer;

namespace DownloadManager.Engine.Tests;

/// <summary>
/// PERF.2: the soak gave each segmented file its own seed, and the TestServer kept every one in memory
/// (1 GB after two hours, growing 8 MB a minute). The content cache must stay under its cap and still hand
/// out the right bytes.
/// </summary>
public sealed class TestServerContentCacheTests
{
    [Fact]
    public async Task ManyDifferentFilesKeepTheCacheUnderItsCap()
    {
        await using var server = await TestServerHost.StartAsync();
        const int size = 16 * 1024 * 1024;

        for (var seed = 1; seed <= 24; seed++)
        {
            var content = server.ContentFor($"file-{seed}.bin", size, seed);
            Assert.Equal(TestContent.Generate(seed, size)[..4096], content[..4096]);
            Assert.InRange(server.CachedContentBytes, size, TestServerHost.ContentCacheBytes);
        }

        // The same size and seed under another name is the same cached copy.
        Assert.Same(server.ContentFor("a.bin", size, 24), server.ContentFor("b.bin", size, 24));
    }
}
