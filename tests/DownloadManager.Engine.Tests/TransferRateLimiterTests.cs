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
        // Too fast fails at once. Too slow is measured again, up to three times: a busy machine (a shared CI runner
        // measured 2.5 MB/s once, CI.3) is slow now and then, while a limiter that can't reach the limit with small
        // reads is slow every time.
        var rate = 0d;
        for (var attempt = 0; attempt < 3 && rate < limit * 0.6; attempt++)
        {
            var limiter = new TransferRateLimiter(() => limit);
            long bytes = 0;
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < TimeSpan.FromSeconds(1.5))
            {
                await limiter.WaitAsync(read, CancellationToken.None);
                bytes += read;
            }

            rate = bytes / stopwatch.Elapsed.TotalSeconds;
            Assert.True(rate <= limit * 1.15, $"{rate:N0} bytes a second is over the limit of {limit:N0}");
        }

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
