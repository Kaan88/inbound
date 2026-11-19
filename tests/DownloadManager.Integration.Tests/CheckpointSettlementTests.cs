using DownloadManager.Core;
using DownloadManager.Persistence;
using TestServer;

namespace DownloadManager.Integration.Tests;

public sealed partial class RuntimeServiceTests
{
    private sealed class NonZeroCheckpointGate(IDownloadCheckpointStore inner) : IDownloadCheckpointStore
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<DownloadCheckpoint?> LoadAsync(Guid id, CancellationToken ct = default) => inner.LoadAsync(id, ct);
        public Task DeleteAsync(Guid id, CancellationToken ct = default) => inner.DeleteAsync(id, ct);
        public async Task SaveAsync(DownloadCheckpoint checkpoint, CancellationToken ct = default)
        {
            if (checkpoint.CompletedBytes > 0) { Entered.TrySetResult(); await Release.Task; }
            await inner.SaveAsync(checkpoint, CancellationToken.None);
        }
    }

    [Fact]
    public async Task AStableInitialCheckpointIsNotMistakenForTheFinishedStopCheckpoint()
    {
        NonZeroCheckpointGate? gate = null;
        await using var ctx = await ServiceContext.CreateAsync(_server, hooks: new ContextHooks { WrapCheckpoints = inner => gate = new NonZeroCheckpointGate(inner) });
        Task<DownloadCheckpoint>? settling = null;
        try
        {
            var added = await AddSlowAsync(ctx, "held-stop-checkpoint.bin", 3 * Mb, 975, 512 * 1024, 1);
            await ctx.WaitFor(added.Id, j => j.State == "downloading" && j.ReceivedBytes > 256 * 1024, TimeSpan.FromSeconds(30));
            Assert.True((await ctx.Service.PauseAsync(added.Id)).Ok);
            await gate!.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var initial = await new JsonCheckpointStore(ctx.Paths).LoadAsync(added.Id);
            Assert.NotNull(initial); Assert.Equal(0, initial.CompletedBytes);
            settling = SettledCheckpointAsync(ctx, added.Id);
            // The old heuristic returned the unchanged initial file after two 300 ms reads, despite the
            // actual stopped-checkpoint save remaining blocked by this explicit gate.
            Assert.NotSame(settling, await Task.WhenAny(settling, Task.Delay(750)));
            gate.Release.TrySetResult();
            var saved = await settling;
            Assert.True(saved.CompletedBytes > 0);
            var part = await File.ReadAllBytesAsync(ctx.Service.GetJob(added.Id)!.PartFilePath);
            Assert.True(part.Length >= saved.CompletedBytes);
            Assert.Equal(TestContent.Generate(975, (int)saved.CompletedBytes), part.Take((int)saved.CompletedBytes).ToArray());
        }
        finally { gate?.Release.TrySetResult(); if (settling is not null) { try { await settling; } catch { } } }
    }
}
