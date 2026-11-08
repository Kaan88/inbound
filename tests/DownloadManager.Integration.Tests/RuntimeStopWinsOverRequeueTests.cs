using DownloadManager.Contracts;
using DownloadManager.Core;

namespace DownloadManager.Integration.Tests;

/// <summary>A stop from the user wins over a lowered limit's requeue that is still winding down.</summary>
public sealed partial class RuntimeServiceTests
{
    /// <summary>Holds one job's checkpoint saves, so its runner can't exit until the test lets it.</summary>
    private sealed class HoldingCheckpoints(IDownloadCheckpointStore inner) : IDownloadCheckpointStore
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Guid HoldJob { get; set; }
        public Task Entered => _entered.Task;

        public void Release() => _release.TrySetResult();

        public Task<DownloadCheckpoint?> LoadAsync(Guid jobId, CancellationToken cancellationToken = default) => inner.LoadAsync(jobId, cancellationToken);

        public async Task SaveAsync(DownloadCheckpoint checkpoint, CancellationToken cancellationToken = default)
        {
            if (checkpoint.JobId == HoldJob && HoldJob != Guid.Empty)
            {
                _entered.TrySetResult();
                await _release.Task.ConfigureAwait(false);
            }

            await inner.SaveAsync(checkpoint, CancellationToken.None).ConfigureAwait(false);
        }

        public Task DeleteAsync(Guid jobId, CancellationToken cancellationToken = default) => inner.DeleteAsync(jobId, cancellationToken);
    }

    [Fact]
    public async Task StopAllWinsOverALoweredLimitsRequeueThatIsStillWindingDown()
    {
        const int size = 3 * Mb;
        HoldingCheckpoints? checkpoints = null;
        await using var ctx = await ServiceContext.CreateAsync(_server, maxConcurrent: 2, hooks: new ContextHooks
        {
            WrapCheckpoints = inner => checkpoints = new HoldingCheckpoints(inner),
        });
        var states = RecordStates(ctx);
        var older = await AddSlowAsync(ctx, "stopwins-0.bin", size, 951, 256 * 1024, 1);
        var newest = await AddSlowAsync(ctx, "stopwins-1.bin", size, 952, 256 * 1024, 1);
        await ctx.WaitFor(older.Id, j => j.State == "downloading" && j.ReceivedBytes > 128 * 1024, TimeSpan.FromSeconds(30));
        await ctx.WaitFor(newest.Id, j => j.State == "downloading" && j.ReceivedBytes > 128 * 1024, TimeSpan.FromSeconds(30));

        // The newest is sent back to the queue; its runner then stalls in a checkpoint save while winding down.
        checkpoints!.HoldJob = newest.Id;
        await SetLimitsAsync(ctx, simultaneous: 1);
        Assert.Equal("queued", ctx.Service.GetJob(newest.Id)!.State);
        await checkpoints.Entered.WaitAsync(TimeSpan.FromSeconds(30));

        await ctx.Service.PauseAllAsync();
        var seenAtStop = states[newest.Id].Count;
        Assert.Equal("paused", ctx.Service.GetJob(newest.Id)!.State);
        Assert.Equal("paused", ctx.Service.GetJob(older.Id)!.State);

        checkpoints.Release();
        await ctx.WaitFor(older.Id, j => j.State == "paused" && j.BytesPerSecond is null, TimeSpan.FromSeconds(30));

        // Stop All has freed every slot. The requeue must not take one: the job stays Paused and does not run again.
        await Task.Delay(TimeSpan.FromSeconds(2));
        var job = ctx.Service.GetJob(newest.Id)!;
        Assert.Equal("paused", job.State);
        Assert.Equal("paused", ctx.Service.GetJob(older.Id)!.State);
        var after = states[newest.Id].Skip(seenAtStop).ToList();
        Assert.DoesNotContain(after, state => state.EndsWith(":queued", StringComparison.Ordinal) ||
                                              state.EndsWith(":connecting", StringComparison.Ordinal) ||
                                              state.EndsWith(":downloading", StringComparison.Ordinal));
        var received = job.ReceivedBytes;
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        Assert.Equal(received, ctx.Service.GetJob(newest.Id)!.ReceivedBytes);
    }
}
