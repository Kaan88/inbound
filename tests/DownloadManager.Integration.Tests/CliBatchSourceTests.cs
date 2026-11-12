using System.Collections.Concurrent;
using DownloadManager.Contracts;
using DownloadManager.Runtime;
using TestServer;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// UI.B14, the Host's side: what the Host does with the sources a CLI batch or import sends. These call the service
/// with each source and run no CLI code; that the CLI sends them is <see cref="CliBatchSourceHostTests"/>.
/// A batch or import is headless like a single CLI add, and unlike one, no completion window opens per job either.
/// Each has a source of its own, so the list still shows "Batch" and "App" as it did, and an import still keeps its
/// links out of the recent addresses.
/// </summary>
public sealed partial class RuntimeServiceTests
{
    [Theory]
    [InlineData("cliBatch")]
    [InlineData("cliImport")]
    public async Task AddsWithTheCliBatchAndImportSourcesOpenNoProgressOrCompletionWindow(string source)
    {
        await using var ctx = await ServiceContext.CreateAsync(_server, maxConcurrent: 3);
        var windows = new ConcurrentQueue<DownloadWindowRequest>();
        using var decisions = new DownloadWindows(ctx.Service, () => true, windows.Enqueue);

        var jobs = new List<JobDto>();
        for (var i = 0; i < 3; i++)
        {
            var added = await ctx.Service.AddJobAsync(new AddJobRequest
            {
                Url = $"{_server.BaseAddress}/file/{source}-{i}.bin?size=2048&seed={i}",
                DestinationDirectory = ctx.Destination,
                StartMode = "startNow",
                Source = source,
            });
            Assert.True(added.Accepted, added.Error);
            Assert.Equal(source, added.Job!.Source);
            jobs.Add(added.Job);
        }

        foreach (var job in jobs) await ctx.WaitFor(job.Id, j => j.State == "completed", TimeSpan.FromSeconds(30));
        await Task.Delay(200);
        Assert.Empty(windows);
        Assert.All(ctx.Service.GetSnapshot().Jobs, job => Assert.Equal(source, job.Source));
    }

    [Fact]
    public async Task TheAppsBatchStillOpensItsWindows()
    {
        // The source the app's own Add Links sends is unchanged: one progress window when it starts, one when it ends.
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var windows = new ConcurrentQueue<DownloadWindowRequest>();
        using var decisions = new DownloadWindows(ctx.Service, () => true, windows.Enqueue);
        var added = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/file/app-batch.bin?size=2048&seed=3",
            DestinationDirectory = ctx.Destination,
            StartMode = "startNow",
            Source = "batch",
        });
        Assert.True(added.Accepted, added.Error);
        await ctx.WaitFor(added.Job!.Id, j => j.State == "completed", TimeSpan.FromSeconds(30));
        await Task.Delay(200);
        Assert.Equal(2, windows.Count(window => window.Id == added.Job.Id));
    }

    [Fact]
    public void OnlyTheAppsSourcesOpenWindowsByThemselves()
    {
        foreach (var headless in new[] { "cli", "cliBatch", "cliImport", "remote" })
            Assert.False(DownloadWindows.OpensWindows(new JobDto { Source = headless }), headless);
        foreach (var shown in new[] { "batch", "import", "api", "chromeAuto", "chromeContextMenu", "clipboard", "clipboardMonitor" })
            Assert.True(DownloadWindows.OpensWindows(new JobDto { Source = shown }), shown);
    }

    [Fact]
    public async Task AnAddWithTheCliImportSourceKeepsItsLinksOutOfRecentAddressesAndOneWithTheBatchSourceDoesNot()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var imported = $"{_server.BaseAddress}/file/imported.bin?size=16";
        var batched = $"{_server.BaseAddress}/file/batched.bin?size=16";
        Assert.True((await ctx.Service.AddJobAsync(new AddJobRequest { Url = imported, DestinationDirectory = ctx.Destination, StartMode = "later", Source = "cliImport" })).Accepted);
        Assert.True((await ctx.Service.AddJobAsync(new AddJobRequest { Url = batched, DestinationDirectory = ctx.Destination, StartMode = "later", Source = "cliBatch" })).Accepted);

        var recent = ctx.Service.GetSnapshot().Settings.RecentUrls;
        Assert.DoesNotContain(imported, recent);
        Assert.Contains(batched, recent);
    }
}

/// <summary>UI.B14 through the real CLI and Host: add-batch and import send their own sources, a single add still "cli".</summary>
[Trait("Platform", "Windows")]
public sealed class CliBatchSourceHostTests : IAsyncLifetime
{
    private TestServerHost _server = null!;

    public async Task InitializeAsync() => _server = await TestServerHost.StartAsync();

    public async Task DisposeAsync() => await _server.DisposeAsync();

    [Fact]
    public async Task TheCliSendsBatchAndImportAsSourcesOfTheirOwn()
    {
        await using var host = await SandboxHost.StartAsync("https://example.invalid/v1/api/");
        await using var connection = await host.ConnectAsync();

        var single = await host.RunCliAsync(null, "add", $"{_server.BaseAddress}/file/single.bin?size=16", "--dest", host.Destination, "--start", "later");
        Assert.Equal(0, single.ExitCode);
        var batch = await host.RunCliAsync(null, "add-batch", $"{_server.BaseAddress}/file/batch[1-2].bin?size=16", "--dest", host.Destination, "--start", "later");
        Assert.Equal(0, batch.ExitCode);
        var listFile = Path.Combine(host.Root, "list.json");
        await File.WriteAllTextAsync(listFile, JobListFile.Serialize([new JobDto { Url = $"{_server.BaseAddress}/file/listed.bin?size=16", DestinationDirectory = host.Destination, StartMode = "later" }]));
        var imported = await host.RunCliAsync(null, "import", listFile, "--start", "later");
        Assert.Equal(0, imported.ExitCode);

        var jobs = (await connection.AskAsync<SnapshotDto>(MessageTypes.JobsQuery, new { })).Jobs;
        Assert.Equal(4, jobs.Count);
        Assert.Equal("cli", Assert.Single(jobs, job => job.Url.Contains("single.bin", StringComparison.Ordinal)).Source);
        Assert.All(jobs.Where(job => job.Url.Contains("/batch", StringComparison.Ordinal)), job => Assert.Equal("cliBatch", job.Source));
        Assert.Equal("cliImport", Assert.Single(jobs, job => job.Url.Contains("listed.bin", StringComparison.Ordinal)).Source);
    }
}
