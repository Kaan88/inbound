using DownloadManager.Contracts;
using DownloadManager.Core;

namespace DownloadManager.Integration.Tests;

/// <summary>Moving a running download into a full queue: the same rule as a lowered limit (owner, 2026-11-08).</summary>
public sealed partial class RuntimeServiceTests
{
    private static async Task<QueueDto> CreateRunningQueueAsync(ServiceContext ctx, string name, int max)
    {
        var created = await ctx.Service.QueueActionAsync(new QueueActionRequest { Action = "create", Name = name, MaxConcurrentDownloads = max });
        Assert.True(created.Ok, created.Error);
        var queue = created.Settings.Queues.Single(q => q.Name == name);
        var started = await ctx.Service.QueueActionAsync(new QueueActionRequest { QueueId = queue.Id, Action = "start" });
        Assert.True(started.Ok, started.Error);
        return queue;
    }

    [Fact]
    public async Task MovingARunningDownloadIntoAFullQueueRequeuesTheNewest()
    {
        const int size = 3 * Mb;
        await using var ctx = await ServiceContext.CreateAsync(_server, maxConcurrent: 3);
        var states = RecordStates(ctx);
        var full = await CreateRunningQueueAsync(ctx, "Full", 1);
        var older = await AddSlowAsync(ctx, "full-older.bin", size, 961, 256 * 1024, 2);
        var newer = await AddSlowAsync(ctx, "full-newer.bin", size, 962, 256 * 1024, 2);
        await ctx.WaitFor(older.Id, j => j.State == "downloading" && j.ReceivedBytes > 128 * 1024, TimeSpan.FromSeconds(30));
        await ctx.WaitFor(newer.Id, j => j.State == "downloading" && j.ReceivedBytes > 128 * 1024, TimeSpan.FromSeconds(30));

        var first = await ctx.Service.MoveToQueueAsync(older.Id, full.Id);
        Assert.True(first.Ok, first.Error);
        Assert.Equal("downloading", ctx.Service.GetJob(older.Id)!.State);

        var second = await ctx.Service.MoveToQueueAsync(newer.Id, full.Id);
        Assert.True(second.Ok, second.Error);
        // The queue holds one; the newest goes back to Queued at once, keeping its data, and the reply says so.
        Assert.Equal("queued", second.Job!.State);
        Assert.Equal("queued", ctx.Service.GetJob(newer.Id)!.State);
        Assert.Equal("downloading", ctx.Service.GetJob(older.Id)!.State);
        var kept = await SettledCheckpointAsync(ctx, newer.Id);
        Assert.True(kept.CompletedBytes > 128 * 1024, $"kept {kept.CompletedBytes} bytes");

        foreach (var (job, seed) in new[] { (older, 961), (newer, 962) })
        {
            var done = await ctx.WaitFor(job.Id, j => j.State is "completed" or "failed", TimeSpan.FromSeconds(60));
            Assert.Equal("completed", done.State);
            Assert.Equal(Sha256Of(seed, size), await Hashing.Sha256FileAsync(done.FinalFilePath));
        }

        Assert.Contains("queued:queued", states[newer.Id]);
        Assert.DoesNotContain(states[newer.Id], state => state.EndsWith(":paused", StringComparison.Ordinal) || state.EndsWith(":failed", StringComparison.Ordinal));
        Assert.DoesNotContain(states[older.Id], state => state.StartsWith("queued", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ANonResumableDownloadMovedIntoAFullQueueKeepsRunning()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server, maxConcurrent: 3);
        var full = await CreateRunningQueueAsync(ctx, "FullNoRange", 1);
        var older = await AddSlowAsync(ctx, "fullnr-older.bin", 2 * Mb, 971, 512 * 1024, 2);
        var newest = await AddSlowAsync(ctx, "fullnr-norange.bin", 2 * Mb, 972, 512 * 1024, 1, route: "norange");
        await ctx.WaitFor(older.Id, j => j.State == "downloading" && j.ReceivedBytes > 128 * 1024, TimeSpan.FromSeconds(30));
        await ctx.WaitFor(newest.Id, j => j.State == "downloading" && j.ReceivedBytes > 128 * 1024 && j.ResumeSupported == false, TimeSpan.FromSeconds(30));

        Assert.True((await ctx.Service.MoveToQueueAsync(older.Id, full.Id)).Ok);
        var moved = await ctx.Service.MoveToQueueAsync(newest.Id, full.Id);
        Assert.True(moved.Ok, moved.Error);

        // It can't stop without losing its bytes, so it finishes above the limit, and the older one isn't stopped instead.
        Assert.Equal("downloading", ctx.Service.GetJob(newest.Id)!.State);
        Assert.Equal("downloading", ctx.Service.GetJob(older.Id)!.State);
        Assert.Equal("completed", (await ctx.WaitFor(newest.Id, j => j.State is "completed" or "failed", TimeSpan.FromSeconds(30))).State);
        Assert.Equal("completed", (await ctx.WaitFor(older.Id, j => j.State is "completed" or "failed", TimeSpan.FromSeconds(30))).State);
    }
}
