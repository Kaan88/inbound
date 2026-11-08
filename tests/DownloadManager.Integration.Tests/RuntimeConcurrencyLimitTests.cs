using System.Collections.Concurrent;
using DownloadManager.Contracts;
using DownloadManager.Core;
using DownloadManager.Core.Providers;
using DownloadManager.Persistence;
using TestServer;

namespace DownloadManager.Integration.Tests;

/// <summary>UI.B7 and UI.R21: the simultaneous-downloads limit and the per-host share apply to running downloads.</summary>
public sealed partial class RuntimeServiceTests
{

    private async Task<JobDto> AddSlowAsync(ServiceContext ctx, string name, int size, int seed, int bps, int connections, string route = "file")
    {
        var response = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/{route}/{name}?size={size}&seed={seed}&bps={bps}",
            DestinationDirectory = ctx.Destination,
            FileName = name,
            Connections = connections,
            Source = "cli",
        });
        Assert.True(response.Accepted, response.Error);
        // CreatedAt orders the newest first when a limit drops; keep the adds apart.
        await Task.Delay(20);
        return response.Job!;
    }

    private static async Task SetLimitsAsync(ServiceContext ctx, int? simultaneous = null, int? perHost = null, int? connections = null)
    {
        var settings = ctx.Service.GetSnapshot().Settings;
        if (simultaneous is { } s) settings.MaxConcurrentDownloads = s;
        if (perHost is { } h) settings.MaxConnectionsPerHost = h;
        if (connections is { } c) settings.DefaultConnections = c;
        var result = await ctx.Service.UpdateSettingsAsync(settings);
        Assert.True(result.Ok, result.Error);
    }

    /// <summary>Every state each job was announced in, in order.</summary>
    private static ConcurrentDictionary<Guid, ConcurrentQueue<string>> RecordStates(ServiceContext ctx)
    {
        var states = new ConcurrentDictionary<Guid, ConcurrentQueue<string>>();
        ctx.Service.JobChanged += (_, change) => states.GetOrAdd(change.Job.Id, _ => new()).Enqueue($"{change.ChangeType}:{change.Job.State}");
        return states;
    }

    [Fact]
    public async Task LoweringSimultaneousDownloadsRequeuesTheNewestAndKeepsTheirData()
    {
        const int size = 3 * Mb;
        await using var ctx = await ServiceContext.CreateAsync(_server, maxConcurrent: 3);
        var states = RecordStates(ctx);
        var jobs = new List<JobDto>();
        for (var i = 0; i < 3; i++) jobs.Add(await AddSlowAsync(ctx, $"lower-{i}.bin", size, 901 + i, 256 * 1024, 2));
        foreach (var job in jobs) await ctx.WaitFor(job.Id, j => j.State == "downloading" && j.ReceivedBytes > 256 * 1024, TimeSpan.FromSeconds(30));

        var seenBefore = jobs.ToDictionary(job => job.Id, job => states[job.Id].Count);
        await SetLimitsAsync(ctx, simultaneous: 1);

        // At once, before the transfers have wound down, the newest two show Queued; the oldest goes on.
        Assert.Equal("queued", ctx.Service.GetJob(jobs[2].Id)!.State);
        Assert.Equal("queued", ctx.Service.GetJob(jobs[1].Id)!.State);
        Assert.Equal("downloading", ctx.Service.GetJob(jobs[0].Id)!.State);

        var kept = new Dictionary<Guid, DownloadCheckpoint>();
        foreach (var job in jobs.Skip(1))
        {
            var checkpoint = await SettledCheckpointAsync(ctx, job.Id);
            Assert.True(checkpoint.CompletedBytes > 256 * 1024, $"{job.FileName} kept {checkpoint.CompletedBytes} bytes.");
            Assert.True(File.Exists(ctx.Service.GetJob(job.Id)!.PartFilePath));
            Assert.Equal("queued", ctx.Service.GetJob(job.Id)!.State);
            kept[job.Id] = checkpoint;
        }

        for (var i = 0; i < 3; i++)
        {
            var done = await ctx.WaitFor(jobs[i].Id, j => j.State is "completed" or "failed", TimeSpan.FromSeconds(60));
            Assert.Equal("completed", done.State);
            Assert.Equal(Sha256Of(901 + i, size), await Hashing.Sha256FileAsync(done.FinalFilePath));
        }

        // Requeued, never paused or failed, and only one ran at a time after the change.
        foreach (var job in jobs.Skip(1))
        {
            var seen = states[job.Id].ToList();
            Assert.DoesNotContain(seen, state => state.StartsWith("paused", StringComparison.Ordinal) || state.EndsWith(":paused", StringComparison.Ordinal));
            Assert.DoesNotContain(seen, state => state.EndsWith(":failed", StringComparison.Ordinal));
            Assert.Contains("queued:queued", seen);

            // From the requeue until the run has wound down (both announced as "queued"), a late progress
            // report must not show it running again.
            var after = seen.Skip(seenBefore[job.Id]).ToList();
            var first = after.IndexOf("queued:queued");
            var second = after.IndexOf("queued:queued", first + 1);
            Assert.True(first >= 0 && second > first, string.Join(", ", after));
            Assert.DoesNotContain(after.Skip(first).Take(second - first),
                state => state.EndsWith(":downloading", StringComparison.Ordinal) || state.EndsWith(":connecting", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task NonResumableJobIsNotPreempted()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server, maxConcurrent: 2);
        var states = RecordStates(ctx);
        var older = await AddSlowAsync(ctx, "keep-older.bin", 2 * Mb, 911, 512 * 1024, 2);
        var newest = await AddSlowAsync(ctx, "keep-norange.bin", 2 * Mb, 912, 512 * 1024, 1, route: "norange");
        await ctx.WaitFor(older.Id, j => j.State == "downloading" && j.ReceivedBytes > 128 * 1024, TimeSpan.FromSeconds(30));
        await ctx.WaitFor(newest.Id, j => j.State == "downloading" && j.ReceivedBytes > 128 * 1024 && j.ResumeSupported == false, TimeSpan.FromSeconds(30));

        await SetLimitsAsync(ctx, simultaneous: 1);

        // The newest can't resume, so it finishes above the limit; it is the excess, so the older one isn't stopped instead.
        Assert.Equal("downloading", ctx.Service.GetJob(newest.Id)!.State);
        Assert.Equal("downloading", ctx.Service.GetJob(older.Id)!.State);
        var doneNewest = await ctx.WaitFor(newest.Id, j => j.State is "completed" or "failed", TimeSpan.FromSeconds(30));
        var doneOlder = await ctx.WaitFor(older.Id, j => j.State is "completed" or "failed", TimeSpan.FromSeconds(30));
        Assert.Equal(Sha256Of(912, 2 * Mb), await Hashing.Sha256FileAsync(doneNewest.FinalFilePath));
        Assert.Equal(Sha256Of(911, 2 * Mb), await Hashing.Sha256FileAsync(doneOlder.FinalFilePath));
        Assert.DoesNotContain(states[newest.Id], state => state.StartsWith("queued", StringComparison.Ordinal));
        Assert.DoesNotContain(states[older.Id], state => state.StartsWith("queued", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PreemptedProviderJobResumesFromItsCheckpointWithAFreshLink()
    {
        const int size = 4 * Mb;
        const string name = "preempt-provider.bin";
        _server.ResetExpiringStats();
        _server.SetVersion(name, 1);
        // Each run asks once; the second run's link is a new one.
        var calls = 0;
        var broker = new ScriptedBroker(job => job.RemoteSource!.FileId == "1"
            ? RemoteLinkResolution.Link($"{_server.BaseAddress}/file/preempt-first.bin?size={2 * Mb}&seed=921&bps=262144")
            : RemoteLinkResolution.Link($"{_server.BaseAddress}/expiring/{name}?size={size}&seed=922&bps=262144&token=1&run={Interlocked.Increment(ref calls)}"));
        await using var ctx = await ServiceContext.CreateAsync(_server, linkBroker: broker, maxConcurrent: 2);
        await SetLimitsAsync(ctx, connections: 4);
        var states = RecordStates(ctx);
        var first = AddedJob(await ctx.Service.AddRemoteJobsAsync([RemoteFile(ctx, "1", "preempt-first.bin", size: 2 * Mb)], "remote:job-1"));
        await Task.Delay(20);
        var second = AddedJob(await ctx.Service.AddRemoteJobsAsync([RemoteFile(ctx, "2", name, size: size, remoteJobId: "job-2")], "remote:job-2"));
        await ctx.WaitFor(first.Id, j => j.State == "downloading", TimeSpan.FromSeconds(30));
        await ctx.WaitFor(second.Id, j => j.State == "downloading" && j.ReceivedBytes > 512 * 1024, TimeSpan.FromSeconds(30));
        Assert.Equal(1, calls);

        await SetLimitsAsync(ctx, simultaneous: 1);
        Assert.Equal("queued", ctx.Service.GetJob(second.Id)!.State);
        var saved = await SettledCheckpointAsync(ctx, second.Id);
        Assert.True(saved.Segments.Count > 1, "The test needs a segmented checkpoint.");
        Assert.True(saved.CompletedBytes > 512 * 1024);
        var rangesBefore = _server.ExpiringRanges().Count;

        var done = await ctx.WaitFor(second.Id, j => j.State is "completed" or "failed", TimeSpan.FromSeconds(60));
        Assert.Equal("completed", done.State);
        Assert.Equal(Sha256Of(922, size), await Hashing.Sha256FileAsync(done.FinalFilePath));
        Assert.Equal(2, calls);
        // Asking for a fresh link on the new run is not a dead-link refresh, so the refresh budget is untouched.
        Assert.Equal(0, broker.RefreshCalls);
        foreach (var range in _server.ExpiringRanges().Skip(rangesBefore).Where(range => range != "bytes=0-0"))
        {
            var start = long.Parse(range["bytes=".Length..].Split('-')[0]);
            Assert.DoesNotContain(saved.Segments, segment => start >= segment.Start && start < segment.Start + segment.Completed);
        }

        Assert.Contains("queued:queued", states[second.Id]);
        await ctx.WaitFor(first.Id, j => j.State == "completed", TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// The user's report: two segmented jobs hold every connection to the host, the limit goes up, and the
    /// newly started jobs sat on "Connecting". Now they get connections and bytes while the first two still run.
    /// </summary>
    [Fact]
    public async Task RaisingTheLimitStartsTransfersNotJustConnections()
    {
        const int size = 6 * Mb;
        await using var ctx = await ServiceContext.CreateAsync(_server, maxConcurrent: 2);
        await SetLimitsAsync(ctx, perHost: 8, connections: 4);
        var jobs = new List<JobDto>();
        for (var i = 0; i < 4; i++) jobs.Add(await AddSlowAsync(ctx, $"raise-{i}.bin", size, 931 + i, 256 * 1024, 4));
        await ctx.WaitFor(jobs[0].Id, j => j.State == "downloading" && j.ActiveConnections == 4, TimeSpan.FromSeconds(30));
        await ctx.WaitFor(jobs[1].Id, j => j.State == "downloading" && j.ActiveConnections == 4, TimeSpan.FromSeconds(30));
        Assert.Equal("queued", ctx.Service.GetJob(jobs[2].Id)!.State);
        Assert.Equal(8, ctx.Engine.HostConnectionsActive);

        await SetLimitsAsync(ctx, simultaneous: 4);
        foreach (var job in jobs.Skip(2))
        {
            await ctx.WaitFor(job.Id, j => j.ReceivedBytes > 0, TimeSpan.FromSeconds(20));
        }

        Assert.NotEqual("completed", ctx.Service.GetJob(jobs[0].Id)!.State);
        Assert.NotEqual("completed", ctx.Service.GetJob(jobs[1].Id)!.State);
        for (var i = 0; i < 4; i++)
        {
            var done = await ctx.WaitFor(jobs[i].Id, j => j.State is "completed" or "failed", TimeSpan.FromSeconds(90));
            Assert.Equal("completed", done.State);
            Assert.Equal(Sha256Of(931 + i, size), await Hashing.Sha256FileAsync(done.FinalFilePath));
        }

        Assert.True(ctx.Engine.HostConnectionPeak <= 8, $"peak {ctx.Engine.HostConnectionPeak}");
    }

    /// <summary>A job that waits for a free connection says so, with the host's name and nothing of the address.</summary>
    [Fact]
    public async Task AJobWithNoConnectionShowsWhichHostItWaitsFor()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server, maxConcurrent: 3);
        await SetLimitsAsync(ctx, perHost: 2);
        // One stream each never yields, so the third waits for real.
        var a = await AddSlowAsync(ctx, "wait-a.bin", Mb, 941, 256 * 1024, 1);
        var b = await AddSlowAsync(ctx, "wait-b.bin", Mb, 942, 256 * 1024, 1);
        await ctx.WaitFor(a.Id, j => j.State == "downloading", TimeSpan.FromSeconds(30));
        await ctx.WaitFor(b.Id, j => j.State == "downloading", TimeSpan.FromSeconds(30));
        var c = await AddSlowAsync(ctx, "wait-c.bin", 64 * 1024, 943, 0, 1);

        var host = new Uri(_server.BaseAddress).Host;
        var waiting = await ctx.WaitFor(c.Id, j => j.WaitingForHost is not null, TimeSpan.FromSeconds(20));
        Assert.Equal(host, waiting.WaitingForHost);
        Assert.Equal($"Waiting for a connection to {host}", waiting.StatusNote);
        Assert.DoesNotContain("wait-c", waiting.StatusNote);
        Assert.Equal("connecting", waiting.State);

        var done = await ctx.WaitFor(c.Id, j => j.State == "completed", TimeSpan.FromSeconds(30));
        Assert.Null(done.WaitingForHost);
        Assert.Null(done.StatusNote);
        // Runtime only: nothing about the wait is saved with the job.
        Assert.DoesNotContain("WaitingForHost", await JobJsonAsync(ctx, c.Id), StringComparison.OrdinalIgnoreCase);
    }
}
