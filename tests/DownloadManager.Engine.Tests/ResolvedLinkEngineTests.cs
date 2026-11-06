using System.Net;
using System.Net.Http.Headers;
using DownloadManager.Core;
using DownloadManager.Persistence;

namespace DownloadManager.Engine.Tests;

/// <summary>A job on a link resolved for this run, which is how a provider's files go, and answers as the real CDN gives them.</summary>
public sealed class ResolvedLinkEngineTests : IDisposable
{
    private const int Size = 423;
    private const string Link = "https://store.example.invalid/dld/abc?token=invented-key";

    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "test-work", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests) Requests.Add(request.RequestUri!.ToString());
            return Task.FromResult(respond(request));
        }
    }

    private DownloadJob Job(string name)
    {
        var folder = Path.Combine(_root, "downloads");
        Directory.CreateDirectory(folder);
        return new DownloadJob
        {
            Url = "remote://torbox/torrents/1/1",
            ResolvedUrl = Link,
            DestinationDirectory = folder,
            FileName = name,
            FinalFilePath = Path.Combine(folder, name),
            PartFilePath = Path.Combine(folder, name + ".part"),
            RequestedConnections = 1,
            ExpectedTotalBytes = Size,
            TotalBytes = Size,
        };
    }

    private HttpDownloadEngine Engine(Handler handler, IDownloadCheckpointStore checkpoints) => new(
        new EngineOptions { MaxAttempts = 1, InitialRetryDelay = TimeSpan.FromMilliseconds(1) },
        handler,
        checkpoints);

    /// <summary>A whole part file, and a checkpoint that says so: where a kill after the last byte and before the rename leaves a job.</summary>
    private static DownloadCheckpoint Finished(DownloadJob job, long recorded = Size, long onDisk = Size)
    {
        File.WriteAllBytes(job.PartFilePath, new byte[onDisk]);
        return new DownloadCheckpoint
        {
            JobId = job.Id,
            Url = job.Url,
            TotalBytes = Size,
            Segments = [new SegmentCheckpoint { Start = 0, End = Size - 1, Completed = recorded }],
        };
    }

    private static HttpResponseMessage PastTheEnd(HttpStatusCode status)
    {
        var response = new HttpResponseMessage(status) { Content = new ByteArrayContent([]) };
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(Size);
        return response;
    }

    private static HttpResponseMessage Whole()
        => new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[Size]) };

    [Theory]
    [InlineData(HttpStatusCode.PartialContent)] // What the real CDN does, seen live.
    [InlineData(HttpStatusCode.RequestedRangeNotSatisfiable)]
    public async Task AResumeOfAFinishedFileFinishesItWhicheverWayTheServerSaysSo(HttpStatusCode status)
    {
        var job = Job("finished.bin");
        var checkpoint = Finished(job);
        using var handler = new Handler(_ => PastTheEnd(status));
        var paths = new StoragePaths(Path.Combine(_root, "state"));
        paths.EnsureCreated();
        using var engine = Engine(handler, new JsonCheckpointStore(paths));

        var result = await engine.ExecuteAsync(job, checkpoint, null, CancellationToken.None);

        Assert.True(result.Success, result.Failure?.Message);
        Assert.True(File.Exists(job.FinalFilePath));
        Assert.False(File.Exists(job.PartFilePath));
    }

    [Fact]
    public async Task AWholePartFileTheCheckpointDoesntVouchForIsFetchedAgain()
    {
        var job = Job("unvouched.bin");
        var checkpoint = Finished(job, recorded: 100);
        var calls = 0;
        using var handler = new Handler(request => Interlocked.Increment(ref calls) == 1 ? PastTheEnd(HttpStatusCode.PartialContent) : Whole());
        var paths = new StoragePaths(Path.Combine(_root, "state"));
        paths.EnsureCreated();
        using var engine = Engine(handler, new JsonCheckpointStore(paths));

        var result = await engine.ExecuteAsync(job, checkpoint, null, CancellationToken.None);

        Assert.True(result.Success, result.Failure?.Message);
        Assert.Equal(2, calls);
        Assert.Equal(Size, new FileInfo(job.FinalFilePath).Length);
    }

    [Fact]
    public async Task APartialAnswerWithNoRangeAtAllIsStillRefused()
    {
        var job = Job("norange.bin");
        var checkpoint = Finished(job);
        using var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent([1]) });
        var paths = new StoragePaths(Path.Combine(_root, "state"));
        paths.EnsureCreated();
        using var engine = Engine(handler, new JsonCheckpointStore(paths));

        var result = await engine.ExecuteAsync(job, checkpoint, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(FailureKind.InvalidContent, result.Failure!.Kind);
    }

    [Fact]
    public async Task ARedirectToPlainHttpIsNotFollowedWithAResolvedLink()
    {
        var job = Job("downgraded.bin");
        using var handler = new Handler(request =>
        {
            var redirect = new HttpResponseMessage(HttpStatusCode.Found);
            redirect.Headers.Location = new Uri("http://store.example.invalid/dld/abc?token=invented-key");
            return redirect;
        });
        var paths = new StoragePaths(Path.Combine(_root, "state"));
        paths.EnsureCreated();
        using var engine = Engine(handler, new JsonCheckpointStore(paths));

        var result = await engine.ExecuteAsync(job, null, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("unencrypted", result.Failure!.Message);
        Assert.DoesNotContain("invented-key", result.Failure.Message);
        Assert.True(result.Failure.IsResumable);
        Assert.NotEqual(FailureKind.Forbidden, result.Failure.Kind);
        Assert.All(handler.Requests, request => Assert.StartsWith("https:", request));
    }

    [Fact]
    public async Task ARedirectThatKeepsEncryptionIsFollowed()
    {
        var job = Job("moved.bin");
        using var handler = new Handler(request =>
        {
            if (request.RequestUri!.Host == "store.example.invalid")
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri("https://edge.example.invalid/dld/abc?token=invented-key");
                return redirect;
            }

            return Whole();
        });
        var paths = new StoragePaths(Path.Combine(_root, "state"));
        paths.EnsureCreated();
        using var engine = Engine(handler, new JsonCheckpointStore(paths));

        var result = await engine.ExecuteAsync(job, null, null, CancellationToken.None);

        Assert.True(result.Success, result.Failure?.Message);
        Assert.Contains(handler.Requests, request => request.StartsWith("https://edge.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnOrdinaryDownloadMayStillBeRedirectedToPlainHttp()
    {
        var job = Job("ordinary.bin");
        job.ResolvedUrl = null;
        job.Url = "https://site.example.invalid/file.bin";
        using var handler = new Handler(request =>
        {
            if (request.RequestUri!.Scheme == "https")
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri("http://site.example.invalid/file.bin");
                return redirect;
            }

            return Whole();
        });
        var paths = new StoragePaths(Path.Combine(_root, "state"));
        paths.EnsureCreated();
        using var engine = Engine(handler, new JsonCheckpointStore(paths));

        var result = await engine.ExecuteAsync(job, null, null, CancellationToken.None);

        Assert.True(result.Success, result.Failure?.Message);
    }
}
