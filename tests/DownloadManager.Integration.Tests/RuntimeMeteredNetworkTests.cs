using System.Collections.Concurrent;
using DownloadManager.Contracts;
using DownloadManager.Core;
using DownloadManager.Persistence;
using TestServer;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// Android step 2, "Wi-Fi only" (S0): with <c>DownloadOnlyOnUnmeteredNetwork</c> on and a metered network reported, downloads
/// wait in the queue, running ones go back to it with their data, and none is paused by the user or failed.
/// </summary>
public sealed partial class RuntimeServiceTests
{
    private static async Task WifiOnlyAsync(ServiceContext ctx, bool on = true)
    {
        var settings = ctx.Service.GetSnapshot().Settings;
        settings.DownloadOnlyOnUnmeteredNetwork = on;
        var result = await ctx.Service.UpdateSettingsAsync(settings);
        Assert.True(result.Ok, result.Error);
    }

    [Fact]
    public async Task MeteredNetworkChangesNothingWhileTheSettingIsOff()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        await ctx.Service.SetNetworkMeteredAsync(true);
        Assert.False(ctx.Service.WaitingForMeteredNetwork);
        Assert.False(ctx.Service.GetStatus().WaitingForMeteredNetwork);

        var job = await AddSlowAsync(ctx, "metered-off.bin", 256 * 1024, 941, 4 * Mb, 1);
        var done = await ctx.WaitFor(job.Id, j => j.State is "completed" or "failed", TimeSpan.FromSeconds(30));
        Assert.Equal("completed", done.State);
        Assert.Equal(Sha256Of(941, 256 * 1024), await Hashing.Sha256FileAsync(done.FinalFilePath));
    }

    [Fact]
    public async Task MeteredNetworkHoldsAQueuedJobUntilTheNetworkIsNotMetered()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var waits = new ConcurrentQueue<bool>();
        ctx.Service.MeteredWaitChanged += (_, waiting) => waits.Enqueue(waiting);
        await WifiOnlyAsync(ctx);
        await ctx.Service.SetNetworkMeteredAsync(true);
        Assert.True(ctx.Service.WaitingForMeteredNetwork);
        Assert.True(ctx.Service.GetStatus().WaitingForMeteredNetwork);

        var job = await AddSlowAsync(ctx, "metered-hold.bin", 256 * 1024, 942, 4 * Mb, 1);
        // A start would show Connecting at once; give it a generous moment to prove it does not happen.
        await Task.Delay(500);
        var held = ctx.Service.GetJob(job.Id)!;
        Assert.Equal("queued", held.State);
        Assert.Equal(0, held.ReceivedBytes);
        Assert.False(held.PausedByUser);

        await ctx.Service.SetNetworkMeteredAsync(false);
        Assert.False(ctx.Service.WaitingForMeteredNetwork);
        var done = await ctx.WaitFor(job.Id, j => j.State is "completed" or "failed", TimeSpan.FromSeconds(30));
        Assert.Equal("completed", done.State);
        Assert.Equal(Sha256Of(942, 256 * 1024), await Hashing.Sha256FileAsync(done.FinalFilePath));
        Assert.Equal(new[] { true, false }, waits.ToArray());
    }

    [Fact]
    public async Task RunningJobGoesBackToQueuedOnAMeteredNetworkAndResumesFromItsCheckpoint()
    {
        const int size = 3 * Mb;
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var states = RecordStates(ctx);
        await WifiOnlyAsync(ctx);
        var job = await AddSlowAsync(ctx, "metered-run.bin", size, 943, 512 * 1024, 2);
        await ctx.WaitFor(job.Id, j => j.State == "downloading" && j.ReceivedBytes > 256 * 1024, TimeSpan.FromSeconds(30));

        await ctx.Service.SetNetworkMeteredAsync(true);
        var queued = ctx.Service.GetJob(job.Id)!;
        Assert.Equal("queued", queued.State);
        Assert.False(queued.PausedByUser);

        var checkpoint = await SettledCheckpointAsync(ctx, job.Id);
        Assert.True(checkpoint.CompletedBytes > 256 * 1024, $"kept {checkpoint.CompletedBytes} bytes.");
        // The run has wound down and nothing took its place: still Queued, not Paused or Failed.
        Assert.Equal("queued", ctx.Service.GetJob(job.Id)!.State);
        await Task.Delay(300);
        Assert.Equal("queued", ctx.Service.GetJob(job.Id)!.State);
        Assert.True(File.Exists(ctx.Service.GetJob(job.Id)!.PartFilePath));

        await ctx.Service.SetNetworkMeteredAsync(false);
        var done = await ctx.WaitFor(job.Id, j => j.State is "completed" or "failed", TimeSpan.FromSeconds(60));
        Assert.Equal("completed", done.State);
        Assert.Equal(Sha256Of(943, size), await Hashing.Sha256FileAsync(done.FinalFilePath));
        var seen = states[job.Id].ToList();
        Assert.DoesNotContain(seen, state => state.EndsWith(":paused", StringComparison.Ordinal) || state.EndsWith(":failed", StringComparison.Ordinal));
        Assert.Contains("queued:queued", seen);
    }

    [Fact]
    public async Task AJobPausedByTheUserStaysPausedThroughMeteredAndBack()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        await WifiOnlyAsync(ctx);
        var job = await AddSlowAsync(ctx, "metered-paused.bin", 3 * Mb, 944, 512 * 1024, 2);
        await ctx.WaitFor(job.Id, j => j.State == "downloading" && j.ReceivedBytes > 128 * 1024, TimeSpan.FromSeconds(30));
        Assert.True((await ctx.Service.PauseAsync(job.Id)).Ok);
        await ctx.WaitFor(job.Id, j => j.State == "paused", TimeSpan.FromSeconds(30));
        await SettledCheckpointAsync(ctx, job.Id);

        await ctx.Service.SetNetworkMeteredAsync(true);
        await ctx.Service.SetNetworkMeteredAsync(false);
        await Task.Delay(500);
        var after = ctx.Service.GetJob(job.Id)!;
        Assert.Equal("paused", after.State);
        Assert.True(after.PausedByUser);
    }

    [Fact]
    public async Task UseDataLiftsTheRuleUntilTheNextTimeTheNetworkIsNotMetered()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var waits = new ConcurrentQueue<bool>();
        ctx.Service.MeteredWaitChanged += (_, waiting) => waits.Enqueue(waiting);
        await WifiOnlyAsync(ctx);
        await ctx.Service.SetNetworkMeteredAsync(true);
        var first = await AddSlowAsync(ctx, "metered-use-1.bin", 256 * 1024, 945, 4 * Mb, 1);
        await Task.Delay(300);
        Assert.Equal("queued", ctx.Service.GetJob(first.Id)!.State);

        ctx.Service.AllowMeteredUntilUnmetered();
        Assert.False(ctx.Service.WaitingForMeteredNetwork);
        var done = await ctx.WaitFor(first.Id, j => j.State is "completed" or "failed", TimeSpan.FromSeconds(30));
        Assert.Equal("completed", done.State);
        Assert.Equal(Sha256Of(945, 256 * 1024), await Hashing.Sha256FileAsync(done.FinalFilePath));

        // Still metered: a new job runs too while the lift lasts.
        var second = await AddSlowAsync(ctx, "metered-use-2.bin", 256 * 1024, 946, 4 * Mb, 1);
        Assert.Equal("completed", (await ctx.WaitFor(second.Id, j => j.State is "completed" or "failed", TimeSpan.FromSeconds(30))).State);

        // Not metered ends the lift; metered again, the rule is back.
        await ctx.Service.SetNetworkMeteredAsync(false);
        await ctx.Service.SetNetworkMeteredAsync(true);
        Assert.True(ctx.Service.WaitingForMeteredNetwork);
        var third = await AddSlowAsync(ctx, "metered-use-3.bin", 256 * 1024, 947, 4 * Mb, 1);
        await Task.Delay(500);
        Assert.Equal("queued", ctx.Service.GetJob(third.Id)!.State);
        Assert.Equal(new[] { true, false, true }, waits.ToArray());
    }

    [Fact]
    public async Task UseDataTappedOnANetworkThatIsNotMeteredDoesNotCarryOverToTheNextMeteredOne()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        await WifiOnlyAsync(ctx);
        await ctx.Service.SetNetworkMeteredAsync(false);
        ctx.Service.AllowMeteredUntilUnmetered(); // a late tap, after Wi-Fi came back
        await ctx.Service.SetNetworkMeteredAsync(true);
        Assert.True(ctx.Service.WaitingForMeteredNetwork);
        var job = await AddSlowAsync(ctx, "metered-late-tap.bin", 256 * 1024, 949, 4 * Mb, 1);
        await Task.Delay(300);
        Assert.Equal("queued", ctx.Service.GetJob(job.Id)!.State);
    }

    [Fact]
    public async Task TurningTheSettingOffReleasesHeldJobs()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        await WifiOnlyAsync(ctx);
        await ctx.Service.SetNetworkMeteredAsync(true);
        var job = await AddSlowAsync(ctx, "metered-off-later.bin", 256 * 1024, 948, 4 * Mb, 1);
        await Task.Delay(300);
        Assert.Equal("queued", ctx.Service.GetJob(job.Id)!.State);
        await WifiOnlyAsync(ctx, on: false);
        Assert.Equal("completed", (await ctx.WaitFor(job.Id, j => j.State is "completed" or "failed", TimeSpan.FromSeconds(30))).State);
    }

    [Fact]
    public async Task TheSettingRoundTripsAndAnOldSettingsFileLoadsWithItOff()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        Assert.False(ctx.Service.GetSnapshot().Settings.DownloadOnlyOnUnmeteredNetwork);
        await WifiOnlyAsync(ctx);
        Assert.True(ctx.Service.GetSnapshot().Settings.DownloadOnlyOnUnmeteredNetwork);
        Assert.True((await new JsonSettingsStore(ctx.Paths).LoadAsync()).DownloadOnlyOnUnmeteredNetwork);

        await File.WriteAllTextAsync(ctx.Paths.SettingsFile, """{ "schemaVersion": 1, "maxConcurrentDownloads": 5 }""");
        var old = await new JsonSettingsStore(ctx.Paths).LoadAsync();
        Assert.False(old.DownloadOnlyOnUnmeteredNetwork);
        Assert.Equal(5, old.MaxConcurrentDownloads);
    }
}
