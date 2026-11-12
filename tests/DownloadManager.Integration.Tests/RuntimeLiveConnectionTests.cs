using DownloadManager.Contracts;
using DownloadManager.Core;
using DownloadManager.Core.Providers;
using TestServer;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// UI.R21 "Apply to all" (owner, 2026-11-08): a changed connections-per-download reaches running provider (TorBox)
/// downloads and protected (pasted provider) links too, within the provider's per-file cap and the per-host limit,
/// with no link refresh asked for or counted.
/// </summary>
public sealed partial class RuntimeServiceTests
{
    private static async Task SetConnectionsAsync(ServiceContext ctx, int connections, int? perHost = null)
    {
        var settings = ctx.Service.GetSnapshot().Settings;
        settings.DefaultConnections = connections;
        if (perHost is { } h) settings.MaxConnectionsPerHost = h;
        var result = await ctx.Service.UpdateSettingsAsync(settings);
        Assert.True(result.Ok, result.Error);
    }

    [Fact]
    public async Task AChangedConnectionCountReachesARunningTorBoxDownloadWithinItsCap()
    {
        await using var rig = await TorBoxRig.CreateAsync(_server, connections: 2);
        await SetConnectionsAsync(rig.Ctx, 2, perHost: 32);
        const int size = 16 * Mb;
        var file = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Live/raise.bin", size, seed: 961);
        var item = rig.Mock.AddItem(RemoteItemKind.Torrent, "Live", [file]);
        // Per connection, so more connections are faster.
        rig.Mock.CdnBytesPerSecond = 256 * 1024;

        var added = await rig.AddAsync(item, file, maxConnections: 32);
        var running = await rig.Ctx.WaitFor(added.Id, j => j.State == "downloading" && j.ReceivedBytes > 512 * 1024, TimeSpan.FromSeconds(30));
        Assert.Equal(2, running.RequestedConnections);
        Assert.True(running.ActiveConnections <= 2, $"{running.ActiveConnections} connections before the change");
        var linkRequests = rig.Mock.Calls("GET torrents/requestdl");
        var links = rig.Mock.IssuedLinks.Count;
        Assert.Equal(1, linkRequests);

        // Raised past TorBox's per-file cap (16, TB.L1): the running transfer takes 16, not 32.
        await SetConnectionsAsync(rig.Ctx, 32);
        Assert.Equal(16, rig.Ctx.Service.GetJob(added.Id)!.RequestedConnections);
        await rig.Ctx.WaitFor(added.Id, j => j.ActiveConnections >= 6 || j.State == "completed", TimeSpan.FromSeconds(30));
        Assert.NotEqual("completed", rig.Ctx.Service.GetJob(added.Id)!.State);

        // Lowered while it runs: workers above 3 stop after their current range.
        await SetConnectionsAsync(rig.Ctx, 3);
        Assert.Equal(3, rig.Ctx.Service.GetJob(added.Id)!.RequestedConnections);
        await rig.Ctx.WaitFor(added.Id, j => j.ActiveConnections <= 3 || j.State == "completed", TimeSpan.FromSeconds(30));

        var completed = await rig.Ctx.WaitFor(added.Id, j => j.State is "completed" or "failed", TimeSpan.FromSeconds(90));
        Assert.Equal("completed", completed.State);
        Assert.Equal(Sha256Of(file.Content), await Hashing.Sha256FileAsync(completed.FinalFilePath));
        Assert.Equal(3, completed.RequestedConnections);
        Assert.InRange(rig.Mock.PeakCdnConnections, 3, 16);
        // A connection change is no link refresh: no new link asked for, none issued.
        Assert.Equal(linkRequests, rig.Mock.Calls("GET torrents/requestdl"));
        Assert.Equal(links, rig.Mock.IssuedLinks.Count);
        Assert.DoesNotContain(rig.Log.ToList(), line => line.Contains("asking for a fresh one", StringComparison.Ordinal));
        await rig.AssertNoLinkOrKeyLeakedAsync();
    }

    [Fact]
    public async Task ARaisedCountOnARunningTorBoxDownloadStaysWithinThePerHostLimit()
    {
        await using var rig = await TorBoxRig.CreateAsync(_server, connections: 2);
        await SetConnectionsAsync(rig.Ctx, 2, perHost: 4);
        const int size = 8 * Mb;
        var file = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Live/host.bin", size, seed: 962);
        var item = rig.Mock.AddItem(RemoteItemKind.Torrent, "Live", [file]);
        rig.Mock.CdnBytesPerSecond = 256 * 1024;

        var added = await rig.AddAsync(item, file, maxConnections: 32);
        await rig.Ctx.WaitFor(added.Id, j => j.State == "downloading" && j.ReceivedBytes > 256 * 1024, TimeSpan.FromSeconds(30));
        rig.Mock.ResetCdnPeak();

        await SetConnectionsAsync(rig.Ctx, 10);
        // The run's link host is held to 4, below both the new default and TorBox's cap.
        Assert.Equal(4, rig.Ctx.Service.GetJob(added.Id)!.RequestedConnections);
        await rig.Ctx.WaitFor(added.Id, j => j.ActiveConnections >= 3 || j.State == "completed", TimeSpan.FromSeconds(30));

        var completed = await rig.Ctx.WaitFor(added.Id, j => j.State is "completed" or "failed", TimeSpan.FromSeconds(90));
        Assert.Equal("completed", completed.State);
        Assert.Equal(Sha256Of(file.Content), await Hashing.Sha256FileAsync(completed.FinalFilePath));
        Assert.True(rig.Ctx.Engine.HostConnectionPeak <= 4, $"host peak {rig.Ctx.Engine.HostConnectionPeak}");
        Assert.True(rig.Mock.PeakCdnConnections <= 4, $"CDN peak {rig.Mock.PeakCdnConnections}");
        Assert.Equal(1, rig.Mock.Calls("GET torrents/requestdl"));
    }

    /// <summary>
    /// The change lands while the run still waits for its link: nothing to clamp against yet, so the record is left
    /// and the run takes the new default when the link comes. Still one link, and no refresh.
    /// </summary>
    [Fact]
    public async Task AChangeBeforeAProviderRunHasItsLinkIsTakenWhenTheLinkComes()
    {
        const int size = 4 * Mb;
        const string name = "live-before-link.bin";
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var broker = new ScriptedBroker(async (_, cancellationToken) =>
        {
            asked.TrySetResult();
            await gate.Task.WaitAsync(cancellationToken);
            return RemoteLinkResolution.Link($"{_server.BaseAddress}/file/{name}?size={size}&seed=963&bps=1048576");
        });
        await using var ctx = await ServiceContext.CreateAsync(_server, linkBroker: broker);
        await SetConnectionsAsync(ctx, 2, perHost: 8);
        var added = AddedJob(await ctx.Service.AddRemoteJobsAsync([RemoteFile(ctx, "1", name, size: size, maxConnections: 16)], "remote:job-1"));
        await asked.Task.WaitAsync(TimeSpan.FromSeconds(20));

        await SetConnectionsAsync(ctx, 6);
        gate.SetResult();
        var done = await ctx.WaitFor(added.Id, j => j.State is "completed" or "failed", TimeSpan.FromSeconds(60));
        Assert.Equal("completed", done.State);
        Assert.Equal(6, done.RequestedConnections);
        Assert.Equal(Sha256Of(963, size), await Hashing.Sha256FileAsync(done.FinalFilePath));
        Assert.Equal(1, broker.PrepareCalls);
        Assert.Equal(0, broker.RefreshCalls);
    }

    [Fact]
    public async Task AChangedConnectionCountReachesARunningProtectedLinkWithinTheProviderCap()
    {
        const int size = 8 * Mb;
        var handler = new CdnRedirectHandler(_server);
        await using var ctx = await ServiceContext.CreateAsync(_server, handler: handler);
        await SetConnectionsAsync(ctx, 2, perHost: 32);

        async Task<JobDto> AddPastedAsync(string name, int seed, int? connections)
        {
            var response = await ctx.Service.AddJobAsync(new AddJobRequest
            {
                Url = $"{PastedHost}/file/{name}?size={size}&seed={seed}&bps=262144&token={LinkSecret}",
                FileName = name,
                DestinationDirectory = ctx.Destination,
                Connections = connections,
                Source = "cli",
            });
            Assert.True(response.Accepted, response.Error);
            return response.Job!;
        }

        var followed = await AddPastedAsync("live-pasted.bin", 964, null);
        // Chosen by hand, so it keeps its count, as an ordinary download does (UI.B5).
        var chosen = await AddPastedAsync("live-pasted-chosen.bin", 965, 3);
        await ctx.WaitFor(followed.Id, j => j.State == "downloading" && j.ReceivedBytes > 256 * 1024, TimeSpan.FromSeconds(30));
        await ctx.WaitFor(chosen.Id, j => j.State == "downloading", TimeSpan.FromSeconds(30));

        await SetConnectionsAsync(ctx, 32);
        Assert.Equal(ProviderLinks.MaxConnections, ctx.Service.GetJob(followed.Id)!.RequestedConnections);
        Assert.Equal(3, ctx.Service.GetJob(chosen.Id)!.RequestedConnections);
        await ctx.WaitFor(followed.Id, j => j.ActiveConnections > 2 || j.State == "completed", TimeSpan.FromSeconds(30));

        foreach (var (job, seed) in new[] { (followed, 964), (chosen, 965) })
        {
            var done = await ctx.WaitFor(job.Id, j => j.State is "completed" or "failed", TimeSpan.FromSeconds(90));
            Assert.Equal("completed", done.State);
            Assert.Equal(Sha256Of(seed, size), await Hashing.Sha256FileAsync(done.FinalFilePath));
        }

        Assert.Equal(3, ctx.Service.GetJob(chosen.Id)!.RequestedConnections);
        Assert.All(handler.TokensSeen, token => Assert.Equal(LinkSecret, token));
        await AssertSecretNotOnDiskAsync(ctx, LinkSecret);
    }

    /// <summary>
    /// A pasted provider link saved by 0.5.3 holds 4, the old cap, whatever the default was. That 4 follows a changed
    /// default up to the new cap; a count below it was chosen and stays.
    /// </summary>
    [Fact]
    public async Task APastedLinkSavedAtTheOldCapOfFourFollowsAChangedDefault()
    {
        var handler = new CdnRedirectHandler(_server);
        await using var ctx = await ServiceContext.CreateAsync(_server, handler: handler);
        await SetConnectionsAsync(ctx, 8, perHost: 32);

        async Task<JobDto> AddPastedAsync(string name, int connections)
        {
            var response = await ctx.Service.AddJobAsync(new AddJobRequest
            {
                Url = $"{PastedHost}/file/{name}?size=1024&seed=1&token={LinkSecret}",
                FileName = name,
                DestinationDirectory = ctx.Destination,
                Connections = connections,
                StartMode = "later",
                Source = "cli",
            });
            Assert.True(response.Accepted, response.Error);
            return response.Job!;
        }

        var oldCap = await AddPastedAsync("old-cap.bin", 4);
        var chosen = await AddPastedAsync("chosen-two.bin", 2);
        Assert.Equal(4, oldCap.RequestedConnections);

        await SetConnectionsAsync(ctx, 12);
        Assert.Equal(12, ctx.Service.GetJob(oldCap.Id)!.RequestedConnections);
        Assert.Equal(2, ctx.Service.GetJob(chosen.Id)!.RequestedConnections);

        await SetConnectionsAsync(ctx, 32);
        Assert.Equal(ProviderLinks.MaxConnections, ctx.Service.GetJob(oldCap.Id)!.RequestedConnections);
        await AssertSecretNotOnDiskAsync(ctx, LinkSecret);
    }

    /// <summary>
    /// Only the per-host limit changes, the default stays: a running provider job's record follows the new effective
    /// count (min of default, host limit and cap), so what it shows is what it gets.
    /// </summary>
    [Fact]
    public async Task ALoweredPerHostLimitAloneReachesARunningTorBoxDownload()
    {
        await using var rig = await TorBoxRig.CreateAsync(_server, connections: 8);
        await SetConnectionsAsync(rig.Ctx, 8, perHost: 32);
        const int size = 8 * Mb;
        var file = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Live/per-host.bin", size, seed: 966);
        var item = rig.Mock.AddItem(RemoteItemKind.Torrent, "Live", [file]);
        rig.Mock.CdnBytesPerSecond = 128 * 1024;

        var added = await rig.AddAsync(item, file, maxConnections: 32);
        await rig.Ctx.WaitFor(added.Id, j => j.State == "downloading" && j.ActiveConnections >= 4, TimeSpan.FromSeconds(30));
        Assert.Equal(8, rig.Ctx.Service.GetJob(added.Id)!.RequestedConnections);

        var settings = rig.Ctx.Service.GetSnapshot().Settings;
        settings.MaxConnectionsPerHost = 3;
        Assert.True((await rig.Ctx.Service.UpdateSettingsAsync(settings)).Ok);
        Assert.Equal(3, rig.Ctx.Service.GetJob(added.Id)!.RequestedConnections);

        var completed = await rig.Ctx.WaitFor(added.Id, j => j.State is "completed" or "failed", TimeSpan.FromSeconds(120));
        Assert.Equal("completed", completed.State);
        Assert.Equal(3, completed.RequestedConnections);
        Assert.Equal(Sha256Of(file.Content), await Hashing.Sha256FileAsync(completed.FinalFilePath));
        Assert.Equal(1, rig.Mock.Calls("GET torrents/requestdl"));
    }

    /// <summary>
    /// The last run's link must not decide a new run's count: a change after a resume, before the new link comes,
    /// leaves the record alone (the earlier link's cap of 3 would have given 3) and the run takes it with its link.
    /// The link is dropped when the run is scheduled and again before the broker is asked; this test can only reach
    /// the second point, so it guards the resume path rather than the short gap before link preparation starts.
    /// </summary>
    [Fact]
    public async Task AChangeAfterAResumeIgnoresThePreviousRunsLink()
    {
        const int size = 8 * Mb;
        const string name = "live-stale-link.bin";
        var url = $"{_server.BaseAddress}/file/{name}?size={size}&seed=967&bps=262144";
        var calls = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondAsked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var broker = new ScriptedBroker(async (_, cancellationToken) =>
        {
            if (Interlocked.Increment(ref calls) == 1) return RemoteLinkResolution.Link(url, maxConnections: 3);
            secondAsked.TrySetResult();
            await gate.Task.WaitAsync(cancellationToken);
            return RemoteLinkResolution.Link(url);
        });
        await using var ctx = await ServiceContext.CreateAsync(_server, linkBroker: broker);
        await SetConnectionsAsync(ctx, 2, perHost: 8);
        var id = AddedJob(await ctx.Service.AddRemoteJobsAsync([RemoteFile(ctx, "1", name, size: size, maxConnections: 16)], "remote:job-1")).Id;
        await ctx.WaitFor(id, j => j.State == "downloading" && j.ReceivedBytes > 256 * 1024, TimeSpan.FromSeconds(30));
        Assert.Equal(2, ctx.Service.GetJob(id)!.RequestedConnections);

        Assert.True((await ctx.Service.PauseAsync(id)).Ok);
        await ctx.WaitFor(id, j => j.State == "paused", TimeSpan.FromSeconds(30));
        Assert.True((await ctx.Service.ResumeAsync(id)).Ok);
        await secondAsked.Task.WaitAsync(TimeSpan.FromSeconds(20));

        await SetConnectionsAsync(ctx, 6);
        Assert.Equal(2, ctx.Service.GetJob(id)!.RequestedConnections);
        gate.SetResult();

        var done = await ctx.WaitFor(id, j => j.State is "completed" or "failed", TimeSpan.FromSeconds(90));
        Assert.Equal("completed", done.State);
        Assert.Equal(6, done.RequestedConnections);
        Assert.Equal(Sha256Of(967, size), await Hashing.Sha256FileAsync(done.FinalFilePath));
        Assert.Equal(2, broker.PrepareCalls);
        Assert.Equal(0, broker.RefreshCalls);
    }
}
