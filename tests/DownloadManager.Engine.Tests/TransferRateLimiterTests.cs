using System.Diagnostics;
using DownloadManager.Core;

namespace DownloadManager.Engine.Tests;

public sealed class TransferRateLimiterTests
{
    /// <summary>A TLS connection hands over a record at a time, about 16 KB, however large the buffer is.</summary>
    [Fact]
    public async Task SmallReadsStillReachTheLimit()
    {
        const long limit = 8_000_000;
        const int read = 16 * 1024;
        var limiter = new TransferRateLimiter(() => limit);
        long bytes = 0;
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(1.5))
        {
            await limiter.WaitAsync(read, CancellationToken.None);
            bytes += read;
        }

        var rate = bytes / stopwatch.Elapsed.TotalSeconds;
        Assert.InRange(rate, limit * 0.6, limit * 1.15);
    }

    [Fact]
    public async Task TheCreditDoesNotOutrunTheLimit()
    {
        const long limit = 1_000_000;
        const int read = 16 * 1024;
        var limiter = new TransferRateLimiter(() => limit);
        await limiter.WaitAsync(read, CancellationToken.None);
        await Task.Delay(TimeSpan.FromSeconds(1));
        long bytes = 0;
        var stopwatch = Stopwatch.StartNew();
        while (bytes < 400_000)
        {
            await limiter.WaitAsync(read, CancellationToken.None);
            bytes += read;
        }

        // Idle time earns at most a tenth of a second's worth.
        Assert.True(stopwatch.Elapsed.TotalSeconds > 0.25, $"{bytes} bytes took {stopwatch.Elapsed.TotalSeconds:0.000} s");
    }
}
