using DownloadManager.Contracts;
using DownloadManager.Core;
using DownloadManager.Core.Providers;
using DownloadManager.Persistence;
using TestServer;

namespace DownloadManager.Integration.Tests;

/// <summary>MAC.6: while the Mac saves data or power, nothing runs; afterwards everything goes on from where it was.</summary>
public sealed partial class RuntimeServiceTests
{
    [Fact]
    public async Task TheSavingHoldQueuesWhatRunsStartsNothingAndThenGoesOn()
    {
        const int size = 2 * Mb;
        await using var ctx = await ServiceContext.CreateAsync(_server, maxConcurrent: 1);
        var running = await AddSlowAsync(ctx, "hold-running.bin", size, 931, 256 * 1024, 2);
        var waiting = await AddSlowAsync(ctx, "hold-waiting.bin", size, 932, 256 * 1024, 2);
        await ctx.WaitFor(running.Id, j => j.State == "downloading" && j.ReceivedBytes > 128 * 1024, TimeSpan.FromSeconds(30));

        await ctx.Service.SetSavingHoldAsync(true);
        Assert.True(ctx.Service.SavingHold);
        Assert.Equal("queued", ctx.Service.GetJob(running.Id)!.State);
        var checkpoint = await SettledCheckpointAsync(ctx, running.Id);
        Assert.True(checkpoint.CompletedBytes > 128 * 1024);

        // The freed slot isn't given to the next one.
        await Task.Delay(1500);
        Assert.Equal("queued", ctx.Service.GetJob(waiting.Id)!.State);
        Assert.Equal("queued", ctx.Service.GetJob(running.Id)!.State);

        await ctx.Service.SetSavingHoldAsync(false);
        foreach (var (job, seed) in new[] { (running, 931), (waiting, 932) })
        {
            var done = await ctx.WaitFor(job.Id, j => j.State is "completed" or "failed", TimeSpan.FromSeconds(60));
            Assert.Equal("completed", done.State);
            Assert.Equal(Sha256Of(seed, size), await Hashing.Sha256FileAsync(done.FinalFilePath));
        }
    }
}
