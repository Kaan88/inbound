using System.Collections.Concurrent;
using System.Net;
using DownloadManager.Contracts;
using DownloadManager.Core.Providers;
using DownloadManager.Runtime;
using TestServer;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// Direct provider links pasted into Add Links together, as the owner did: three small files in one package, one
/// of them empty, and one whose server didn't answer the add's question for a name. Each link carries the key.
/// </summary>
public sealed partial class RuntimeServiceTests
{
    private const string FileIdNamed = "0b6d3a52-91c4-4f7e-a1f0-5d2c8e7b9a10";
    private const string FileIdEmpty = "6f1e2d3c-4b5a-4968-8776-a5b4c3d2e1f0";
    private const string FileIdUnasked = "a9b8c7d6-e5f4-4321-9a8b-7c6d5e4f3a2b";

    /// <summary>The CDN for these tests: the named and unasked files say their names, and the unasked one won't answer the add's question.</summary>
    private sealed class BatchCdnHandler(TestServerHost server) : DelegatingHandler(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false })
    {
        public ConcurrentQueue<string> Seen { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (!uri.Host.Contains(".tb-cdn.", StringComparison.OrdinalIgnoreCase)) return await base.SendAsync(request, cancellationToken);
            var id = uri.Segments[^1];
            var range = request.Headers.Range?.ToString() ?? "none";
            Seen.Enqueue($"{request.Method} {id} {range}");

            // The add asks with a HEAD, or a range of one byte where HEAD isn't allowed.
            if (id == FileIdUnasked && (request.Method == HttpMethod.Head || range == "bytes=0-0"))
            {
                return new HttpResponseMessage(HttpStatusCode.TooManyRequests) { RequestMessage = request };
            }

            var target = new Uri(server.BaseAddress);
            var path = id switch
            {
                FileIdNamed => "/filename/named?disposition=" + Uri.EscapeDataString("attachment; filename=\"Read me.txt\""),
                FileIdUnasked => "/filename/unasked?disposition=" + Uri.EscapeDataString("attachment; filename=\"Second file.txt\""),
                _ => "/file/empty.txt?size=0",
            };
            var forwarded = new HttpRequestMessage(request.Method == HttpMethod.Head ? HttpMethod.Get : request.Method, new Uri(target, path));
            foreach (var header in request.Headers) forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value);
            var answer = await base.SendAsync(forwarded, cancellationToken);

            // To the engine, the answer came from the CDN's address.
            answer.RequestMessage = request;
            if (request.Method == HttpMethod.Head) answer.Content = CopyHeaders(answer.Content);
            return answer;
        }

        private static HttpContent CopyHeaders(HttpContent from)
        {
            var empty = new ByteArrayContent([]);
            foreach (var header in from.Headers) empty.Headers.TryAddWithoutValidation(header.Key, header.Value);
            return empty;
        }
    }

    private static string CdnLink(string fileId) => $"{PastedHost}/dld/{fileId}?token={LinkSecret}";

    private static async Task<List<JobDto>> AddAsOneBatchAsync(ServiceContext ctx, params string[] fileIds)
    {
        // What Add Links sends: one add per link, in a package of their own.
        var key = Guid.NewGuid().ToString("N");
        var jobs = new List<JobDto>();
        foreach (var id in fileIds)
        {
            var added = await ctx.Service.AddJobAsync(new AddJobRequest
            {
                Url = CdnLink(id),
                DestinationDirectory = ctx.Destination,
                StartMode = "now",
                ConflictPolicy = "rename",
                Source = "batch",
                PackageKey = key,
            });
            Assert.True(added.Accepted, added.Error);
            jobs.Add(added.Job!);
        }

        return jobs;
    }

    [Fact]
    public async Task ThreeDirectLinksInOnePackageOpenOneWindowAndNoFileOfItsOwnCompletionWindow()
    {
        var handler = new BatchCdnHandler(_server);
        await using var ctx = await ServiceContext.CreateAsync(_server, handler: handler, maxConcurrent: 3);
        // As the Host has it: small files finish before the next add, and the package waits a moment for more.
        ctx.Service.PackageSettleDelay = TimeSpan.FromSeconds(2);
        var windows = new ConcurrentQueue<DownloadWindowRequest>();
        using var decisions = new DownloadWindows(ctx.Service, () => true, windows.Enqueue);
        var packageDone = new TaskCompletionSource<PackageCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.Service.PackageCompleted += (_, e) => packageDone.TrySetResult(e);

        var jobs = await AddAsOneBatchAsync(ctx, FileIdNamed, FileIdEmpty, FileIdUnasked);
        foreach (var job in jobs) await ctx.WaitFor(job.Id, j => j.State == "completed", TimeSpan.FromSeconds(30));
        var completed = await packageDone.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var package = Assert.Single(ctx.Service.GetDownloadGroups());
        Assert.Equal(package.Id, completed.PackageId);
        Assert.Equal(3, completed.JobIds.Count);

        // One window when the first file started, and the package's completion: never a file's own.
        await Task.Delay(200);
        Assert.All(windows, window => Assert.True(window.IsPackage, "A file of the package opened a window of its own."));
        Assert.All(windows, window => Assert.Equal(package.Id, window.Id));
        Assert.Equal(2, windows.Count);
    }

    [Fact]
    public async Task EachDirectLinkOfABatchEndsWithItsServersNameAndTheEmptyFileCompletes()
    {
        var handler = new BatchCdnHandler(_server);
        var log = new ConcurrentQueue<string>();
        await using var ctx = await ServiceContext.CreateAsync(_server, handler: handler, maxConcurrent: 3,
            hooks: new ContextHooks { Log = log.Enqueue });

        var jobs = await AddAsOneBatchAsync(ctx, FileIdNamed, FileIdEmpty, FileIdUnasked);
        var done = new List<JobDto>();
        foreach (var job in jobs) done.Add(await ctx.WaitFor(job.Id, j => j.IsTerminal, TimeSpan.FromSeconds(30)));

        Assert.All(done, job => Assert.Equal("completed", job.State));
        Assert.Equal("Read me.txt", done[0].FileName);
        Assert.Equal(0, new FileInfo(done[1].FinalFilePath).Length);
        // The add couldn't learn this one's name, and the transfer's own answer gave it.
        Assert.Equal("Second file.txt", done[2].FileName);
        Assert.Equal("Second file.txt", Path.GetFileName(done[2].FinalFilePath));
        Assert.True(File.Exists(done[2].FinalFilePath));
        Assert.Contains(log, line => line.Contains("name for a new download wasn't read", StringComparison.Ordinal) && line.Contains("HTTP 429", StringComparison.Ordinal));
        Assert.DoesNotContain(log, line => line.Contains(LinkSecret, StringComparison.Ordinal) || line.Contains(FileIdUnasked, StringComparison.Ordinal));
    }

    /// <summary>TB.F12: the token, and the account's file id with it, reach none of the places a link is shown, copied or kept.</summary>
    [Fact]
    public async Task APastedDirectLinksTokenReachesNoSurface()
    {
        var handler = new BatchCdnHandler(_server);
        var log = new ConcurrentQueue<string>();
        await using var ctx = await ServiceContext.CreateAsync(_server, handler: handler,
            hooks: new ContextHooks { Log = log.Enqueue });

        var added = (await AddAsOneBatchAsync(ctx, FileIdNamed, FileIdEmpty)).ToList();
        var done = new List<JobDto>();
        foreach (var job in added) done.Add(await ctx.WaitFor(job.Id, j => j.State == "completed", TimeSpan.FromSeconds(30)));
        var snapshot = ctx.Service.GetSnapshot();

        foreach (var job in done)
        {
            // What the Host sends every client: a masked copy, flagged so it is shown as its site alone.
            Assert.True(job.AddressHidden);
            Assert.DoesNotContain(LinkSecret, job.Url);
            Assert.Equal("store-1.weur.tb-cdn.test", ShownAddress.For(job.Url, job.AddressHidden));
            Assert.Equal("Site", ShownAddress.Label(job.Url, job.AddressHidden));
        }

        var everything = System.Text.Json.JsonSerializer.Serialize(snapshot);
        Assert.DoesNotContain(LinkSecret, everything);
        Assert.Empty(snapshot.Settings.RecentUrls);

        // An export can't carry it, and a masked copy couldn't download anything if it did.
        var exported = JobListFile.Serialize(snapshot.Jobs);
        Assert.DoesNotContain(LinkSecret, exported);
        Assert.DoesNotContain(FileIdNamed, exported);
        Assert.DoesNotContain("tb-cdn", exported);

        Assert.DoesNotContain(log, line => line.Contains(LinkSecret, StringComparison.Ordinal));
        await AssertSecretNotOnDiskAsync(ctx, LinkSecret);
    }

    [Fact]
    public async Task AnOrdinaryLinkIsStillShownAndExportedAsItIs()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var link = $"{_server.BaseAddress}/file/plain.bin?size=1024&seed=9";
        var added = await ctx.Service.AddJobAsync(new AddJobRequest { Url = link, DestinationDirectory = ctx.Destination, Source = "batch" });
        Assert.True(added.Accepted, added.Error);

        var job = await ctx.WaitFor(added.Job!.Id, j => j.State == "completed");
        Assert.False(job.AddressHidden);
        Assert.Equal(link, ShownAddress.For(job.Url, job.AddressHidden));
        Assert.Contains("plain.bin", JobListFile.Serialize([job]));
    }
}
