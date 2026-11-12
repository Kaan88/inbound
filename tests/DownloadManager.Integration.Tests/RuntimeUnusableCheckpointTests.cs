using DownloadManager.Core;
using DownloadManager.Persistence;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// Audit 1 L9: a checkpoint that is damaged or from a newer build fails the run once with its own message, keeps the
/// part, and is not retried automatically (a retry can't mend it).
/// </summary>
public sealed partial class RuntimeServiceTests
{
    [Theory]
    [InlineData("damaged", "is damaged")]
    [InlineData("newer", "newer version of the app")]
    public async Task AnUnusableCheckpointFailsTheRunWithItsReasonAndKeepsThePart(string kind, string expected)
    {
        const int size = 3 * Mb;
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var added = await AddSlowAsync(ctx, $"unusable-{kind}.bin", size, 961, 512 * 1024, 1);
        await ctx.WaitFor(added.Id, j => j.State == "downloading" && j.ReceivedBytes > 256 * 1024, TimeSpan.FromSeconds(30));
        Assert.True((await ctx.Service.PauseAsync(added.Id)).Ok);
        var saved = await SettledCheckpointAsync(ctx, added.Id);
        Assert.True(saved.CompletedBytes > 0);
        var job = ctx.Service.GetJob(added.Id)!;
        var partLength = new FileInfo(job.PartFilePath).Length;

        var path = Path.Combine(ctx.Paths.CheckpointsDirectory, $"{added.Id:N}.json");
        if (kind == "damaged")
        {
            await File.WriteAllTextAsync(path, "{ \"jobId\": ");
        }
        else
        {
            saved.SchemaVersion = DownloadCheckpoint.CurrentSchemaVersion + 1;
            await new JsonCheckpointStore(ctx.Paths).SaveAsync(saved);
        }

        var checkpointText = await File.ReadAllTextAsync(path);
        Assert.True((await ctx.Service.ResumeAsync(added.Id)).Ok);
        var failed = await ctx.WaitFor(added.Id, j => j.State is "failed" or "completed", TimeSpan.FromSeconds(30));
        Assert.Equal("failed", failed.State);
        Assert.Contains(expected, failed.Failure!.Message);
        Assert.False(failed.Failure.IsRetryable);

        // Not retried behind the user's back, and nothing was truncated or rewritten.
        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.Equal("failed", ctx.Service.GetJob(added.Id)!.State);
        Assert.Equal(partLength, new FileInfo(job.PartFilePath).Length);
        Assert.Equal(checkpointText, await File.ReadAllTextAsync(path));
    }
}
