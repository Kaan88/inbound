using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using DownloadManager.Core;
using DownloadManager.Persistence;
using TestServer;

namespace DownloadManager.Engine.Tests;

/// <summary>Audit 1 fixes in the HTTP engine: weak ETags, free space, resume offset, failing segments, site logins.</summary>
[Collection("http-server")]
public sealed class EngineAuditFixTests(TestServerFixture fixture) : IDisposable
{
    private readonly TestServerHost _server = fixture.Server;
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "test-work", "audit-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private DownloadJob Job(string url, string fileName, int connections = 1)
    {
        var destination = Path.Combine(_root, "downloads");
        Directory.CreateDirectory(destination);
        return new DownloadJob
        {
            Url = url,
            FileName = fileName,
            DestinationDirectory = destination,
            FinalFilePath = Path.Combine(destination, fileName),
            PartFilePath = Path.Combine(destination, fileName) + ".part",
            RequestedConnections = connections,
            State = DownloadState.Queued,
        };
    }

    private StoragePaths Paths()
    {
        var paths = new StoragePaths(Path.Combine(_root, "state"));
        paths.EnsureCreated();
        return paths;
    }

    private static EngineOptions Options(int maxAttempts = 3) => new()
    {
        MaxAttempts = maxAttempts,
        InitialRetryDelay = TimeSpan.FromMilliseconds(10),
        MaxRetryDelay = TimeSpan.FromMilliseconds(50),
        ReadTimeout = TimeSpan.FromSeconds(10),
        ConnectTimeout = TimeSpan.FromSeconds(5),
        AllowLocalNetwork = true,
    };

    private static string Sha(byte[] bytes)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

    [Fact]
    public async Task WeakEtagKeepsItsMarkerAndNeverGoesInIfRange()
    {
        // The server compares If-Range exactly, as nginx and Go do, and answers a mismatch with the whole file.
        var content = TestContent.Generate(901, 2 * 1024 * 1024);
        using var handler = new StubFileHandler(content) { ETag = "W/\"abc\"" };
        using var engine = new HttpDownloadEngine(Options(), handler);
        var job = Job("http://weak-etag.test/file.bin", "weak.bin", connections: 4);

        var result = await engine.ExecuteAsync(job, null, null, CancellationToken.None);

        Assert.True(result.Success, result.Failure?.Message);
        Assert.Equal(Sha(content), await Hashing.Sha256FileAsync(result.FinalFilePath!));
        Assert.Equal("W/\"abc\"", job.ETag);
        Assert.Empty(handler.IfRangeSent);
        Assert.Equal(0, handler.FullBodyResponses);
        Assert.True(handler.PartialResponses > 2, $"partial responses {handler.PartialResponses}");
    }

    [Fact]
    public void WeakAndStrongFormsOfOneTagAreTheSameEntity()
    {
        Assert.Equal(HttpDownloadEngine.OpaqueEtag("\"abc\""), HttpDownloadEngine.OpaqueEtag("W/\"abc\""));
        Assert.NotEqual(HttpDownloadEngine.OpaqueEtag("\"abc\""), HttpDownloadEngine.OpaqueEtag("W/\"abd\""));
    }

    [Fact]
    public async Task NotEnoughFreeSpaceIsAFailureOnTheSegmentedPathToo()
    {
        const int size = 2 * 1024 * 1024;
        using var engine = new HttpDownloadEngine(Options(), freeSpace: new FixedFreeSpace(1024));
        var job = Job($"{_server.BaseAddress}/file/audit-disk.bin?size={size}&seed=902", "audit-disk.bin", connections: 4);

        var result = await engine.ExecuteAsync(job, null, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(FailureKind.DiskFull, result.Failure!.Kind);
        Assert.False(result.Failure.IsRetryable);
    }

    [Fact]
    public async Task SingleStreamResumesFromTheCheckpointNotThePartLength()
    {
        // The part holds 1,500 bytes, the last 500 of them never flushed (zeros); the checkpoint counts 1,000.
        var content = TestContent.Generate(903, 64 * 1024);
        using var handler = new StubFileHandler(content) { ETag = "\"v1\"" };
        using var engine = new HttpDownloadEngine(Options(), handler, new JsonCheckpointStore(Paths()));
        var job = Job("http://resume-offset.test/file.bin", "resume-offset.bin");
        var part = new byte[1500];
        Array.Copy(content, part, 1000);
        await File.WriteAllBytesAsync(job.PartFilePath, part);
        var checkpoint = new DownloadCheckpoint { JobId = job.Id, Url = job.Url, TotalBytes = content.Length, ETag = "\"v1\"" };
        checkpoint.Segments.Add(new SegmentCheckpoint { Start = 0, End = content.Length - 1, Completed = 1000 });

        var result = await engine.ExecuteAsync(job, checkpoint, null, CancellationToken.None);

        Assert.True(result.Success, result.Failure?.Message);
        Assert.Equal("bytes=1000-", handler.Ranges.First());
        Assert.Equal(Sha(content), await Hashing.Sha256FileAsync(result.FinalFilePath!));
    }

    [Fact]
    public async Task OneSegmentGivingUpStopsTheOthers()
    {
        // Every range but the first comes back with another ETag ("the file changed"), and all ranges are slow:
        // without the stop, the other workers would fetch their whole ranges before the run ends.
        var content = TestContent.Generate(904, 8 * 1024 * 1024);
        using var handler = new StubFileHandler(content)
        {
            ETag = "\"v1\"",
            ChunkDelay = TimeSpan.FromMilliseconds(20),
            ChunkSize = 16 * 1024,
            ChangedEtagAfterRequests = 2,
        };
        using var engine = new HttpDownloadEngine(Options(maxAttempts: 1), handler);
        var job = Job("http://segment-fault.test/file.bin", "segment-fault.bin", connections: 4);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = await engine.ExecuteAsync(job, null, null, CancellationToken.None);
        stopwatch.Stop();

        Assert.False(result.Success);
        Assert.Equal(FailureKind.RemoteChanged, result.Failure!.Kind);
        // Each range is 2 MB at about 800 KB/s; the first worker alone would fetch all of its 2 MB unstopped.
        Assert.True(handler.BytesServed < 1024 * 1024, $"served {handler.BytesServed} bytes in {stopwatch.Elapsed}");
    }

    [Theory]
    [InlineData("example.test", true)]
    [InlineData("example.test:8080", true)]
    [InlineData("example.test:8080/files", true)]
    [InlineData("http://example.test:8080", true)]
    [InlineData("example.test:9090", false)]
    [InlineData("https://example.test:8080", false)]
    public async Task SiteLoginMatchesHostPortFormsAndHonoursTheScheme(string site, bool sent)
    {
        var content = TestContent.Generate(905, 4096);
        using var handler = new StubFileHandler(content);
        using var engine = new HttpDownloadEngine(Options(), handler);
        engine.UpdateSiteLogins([new SiteLoginCredential(site, "user", "pass")]);
        var job = Job("http://example.test:8080/files/a.bin", "site-" + Guid.NewGuid().ToString("N") + ".bin");

        var result = await engine.ExecuteAsync(job, null, null, CancellationToken.None);

        Assert.True(result.Success, result.Failure?.Message);
        Assert.Equal(sent, handler.AuthorizationSent.Any(value => value.StartsWith("Basic ", StringComparison.Ordinal)));
    }

    private sealed class FixedFreeSpace(long available) : IFreeSpaceProvider
    {
        public long? GetAvailableFreeSpace(string directory) => available;
    }

    /// <summary>Serves one byte array with ranges; compares If-Range exactly and answers a mismatch with 200.</summary>
    private sealed class StubFileHandler(byte[] content) : HttpMessageHandler
    {
        private int _requests;
        private long _served;

        public string? ETag { get; init; }
        public TimeSpan ChunkDelay { get; init; }
        public int ChunkSize { get; init; } = 64 * 1024;
        /// <summary>Ranged requests after this many get another ETag.</summary>
        public int? ChangedEtagAfterRequests { get; init; }
        public ConcurrentQueue<string> IfRangeSent { get; } = new();
        public ConcurrentQueue<string?> Ranges { get; } = new();
        public ConcurrentQueue<string> AuthorizationSent { get; } = new();
        public int FullBodyResponses;
        public int PartialResponses;
        public long BytesServed => Interlocked.Read(ref _served);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var number = Interlocked.Increment(ref _requests);
            if (request.Headers.Authorization is { } auth) AuthorizationSent.Enqueue(auth.ToString());
            if (request.Headers.IfRange is { } ifRange) IfRangeSent.Enqueue(ifRange.ToString());
            var range = request.Headers.Range?.Ranges.FirstOrDefault();
            if (range is not null) Ranges.Enqueue(request.Headers.Range!.ToString());
            var etag = ChangedEtagAfterRequests is { } after && number > after && range?.From > 0 ? "\"v2\"" : ETag;

            var whole = range is null ||
                        (request.Headers.IfRange?.EntityTag is { } tag && tag.ToString() != ETag);
            HttpResponseMessage response;
            if (whole)
            {
                Interlocked.Increment(ref FullBodyResponses);
                response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Body(0, content.Length - 1, cancellationToken) };
                response.Content.Headers.ContentLength = content.Length;
            }
            else
            {
                Interlocked.Increment(ref PartialResponses);
                var from = range!.From ?? 0;
                var to = Math.Min(range.To ?? content.Length - 1, content.Length - 1);
                response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = Body(from, to, cancellationToken) };
                response.Content.Headers.ContentLength = to - from + 1;
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, content.Length);
            }

            response.Headers.AcceptRanges.Add("bytes");
            if (etag is not null) response.Headers.ETag = EntityTagHeaderValue.Parse(etag);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }

        private HttpContent Body(long from, long to, CancellationToken cancellationToken)
            => ChunkDelay <= TimeSpan.Zero
                ? new ByteArrayContentCounting(this, content, (int)from, (int)(to - from + 1))
                : new StreamContent(new SlowStream(this, content, from, to, ChunkSize, ChunkDelay));

        private sealed class ByteArrayContentCounting : ByteArrayContent
        {
            public ByteArrayContentCounting(StubFileHandler owner, byte[] content, int offset, int count)
                : base(content, offset, count) => Interlocked.Add(ref owner._served, count);
        }

        private sealed class SlowStream(StubFileHandler owner, byte[] content, long from, long to, int chunk, TimeSpan delay) : Stream
        {
            private long _position = from;
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count).GetAwaiter().GetResult();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (_position > to) return 0;
                await Task.Delay(delay, cancellationToken);
                var count = (int)Math.Min(Math.Min(buffer.Length, chunk), to - _position + 1);
                content.AsMemory((int)_position, count).CopyTo(buffer);
                _position += count;
                Interlocked.Add(ref owner._served, count);
                return count;
            }
        }
    }
}
