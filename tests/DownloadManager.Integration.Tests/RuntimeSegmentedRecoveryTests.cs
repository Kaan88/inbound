using DownloadManager.Contracts;
using DownloadManager.Core;
using DownloadManager.Persistence;
using TestServer;

namespace DownloadManager.Integration.Tests;

public sealed partial class RuntimeServiceTests
{
    [Fact]
    public async Task SegmentedJobKeepsItsProgressWhenTheLinkIsDeadAtResume()
    {
        const string name = "dead-at-resume.bin";
        const int size = 8 * 1024 * 1024;
        const int seed = 411;
        _server.ResetExpiringStats();
        _server.SetVersion(name, 1);
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var response = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/expiring/{name}?token=1&size={size}&seed={seed}&bps=524288",
            DestinationDirectory = ctx.Destination,
            FileName = name,
            Source = "cli",
            Connections = 4,
        });
        Assert.True(response.Accepted, response.Error);
        var id = response.Job!.Id;

        await ctx.WaitFor(id, j => j.ReceivedBytes > 1024 * 1024, TimeSpan.FromSeconds(30));
        Assert.True((await ctx.Service.PauseAsync(id)).Ok);
        var saved = await SettledCheckpointAsync(ctx, id);
        Assert.True(saved.Segments.Count > 1, "The test needs a segmented checkpoint.");
        Assert.True(saved.CompletedBytes > 0);

        // Dead link: twice, because the second attempt is the one the defect broke.
        _server.SetVersion(name, 2);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            Assert.True((await ctx.Service.ResumeAsync(id)).Ok);
            var failed = await ctx.WaitFor(id, j => j.State == "failed", TimeSpan.FromSeconds(30));
            Assert.Equal("expiredUrl", failed.Failure?.Kind);
            Assert.Equal(403, failed.Failure?.HttpStatus);
            Assert.Equal(saved.CompletedBytes, failed.ReceivedBytes);
            Assert.Equal(size, failed.TotalBytes);

            var kept = await new JsonCheckpointStore(ctx.Paths).LoadAsync(id);
            Assert.NotNull(kept);
            Assert.True(kept!.Segments.Count > 1, "A failed probe replaced the segmented checkpoint.");
            Assert.Equal(saved.CompletedBytes, kept.CompletedBytes);
            Assert.Equal(size, new FileInfo(failed.PartFilePath).Length);
        }

        // Good link, without a Host restart.
        _server.ResetExpiringStats();
        var refreshed = await ctx.Service.RefreshUrlAsync(id, $"{_server.BaseAddress}/expiring/{name}?token=2&size={size}&seed={seed}");
        Assert.True(refreshed.Ok, refreshed.Error);
        var completed = await ctx.WaitFor(id, j => j.State == "completed", TimeSpan.FromSeconds(30));
        Assert.Equal(Sha256Of(seed, size), await Hashing.Sha256FileAsync(completed.FinalFilePath));

        // Everything but the probe starts on a byte the checkpoint did not already have.
        var ranges = _server.ExpiringRanges().Where(range => range != "bytes=0-0").ToList();
        Assert.NotEmpty(ranges);
        foreach (var range in ranges)
        {
            var start = long.Parse(range["bytes=".Length..].Split('-')[0]);
            Assert.DoesNotContain(saved.Segments, segment => start >= segment.Start && start < segment.Start + segment.Completed);
        }
    }

    /// <summary>The checkpoint once a paused job's transfer has wound down and stopped writing it.</summary>
    private static async Task<DownloadCheckpoint> SettledCheckpointAsync(ServiceContext ctx, Guid id)
    {
        var store = new JsonCheckpointStore(ctx.Paths);
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        long? previous = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(300);
            var checkpoint = await store.LoadAsync(id);
            if (checkpoint is not null && checkpoint.CompletedBytes == previous) return checkpoint;
            previous = checkpoint?.CompletedBytes;
        }

        throw new TimeoutException("The checkpoint kept changing after the job was paused.");
    }

    private static string Sha256Of(int seed, int size)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(TestContent.Generate(seed, size))).ToLowerInvariant();
}
