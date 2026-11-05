using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using DownloadManager.Core;

namespace DownloadManager.Engine.Tests;

public sealed class HttpConnectionTelemetryTests
{
    [Fact]
    public async Task ResumeTelemetryTracksRangeHostStatusAndExactBodyBytesWithoutChangingCredentialsOrPayload()
    {
        var suffix = new byte[] { 4, 5, 6, 7, 8 };
        var prefix = new byte[] { 1, 2, 3 };
        var content = new GatedReadStream(suffix, firstReadBytes: 2);
        using var handler = new TestHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("bytes=3-", request.Headers.Range?.ToString());
            Assert.Equal("session=keep", request.Headers.GetValues("Cookie").Single());
            Assert.Equal("Bearer keep", request.Headers.Authorization?.ToString());
            Assert.Equal(new Uri("https://example.test:8443/page"), request.Headers.Referrer);
            Assert.Equal("TelemetryTest/1.0", request.Headers.UserAgent.ToString());

            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new StreamContent(content),
            };
            response.Content.Headers.ContentLength = suffix.Length;
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(3, 7, 8);
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            return response;
        });
        using var engine = new HttpDownloadEngine(new EngineOptions { MaxAttempts = 1 }, handler);
        using var workspace = new TestWorkspace();
        var job = workspace.CreateJob();
        job.RequestHeaders["Cookie"] = "session=keep";
        job.RequestHeaders["Authorization"] = "Bearer keep";
        job.Referrer = "https://example.test:8443/page";
        job.UserAgent = "TelemetryTest/1.0";
        await File.WriteAllBytesAsync(job.PartFilePath, prefix);
        var checkpoint = new DownloadCheckpoint
        {
            JobId = job.Id,
            Url = job.Url,
            TotalBytes = 8,
            Segments = [new SegmentCheckpoint { Start = 0, End = 7, Completed = prefix.Length }],
        };

        var transfer = engine.ExecuteAsync(job, checkpoint, null, CancellationToken.None);
        await content.FirstReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var live = Assert.Single(engine.GetConnections(job.Id));
        Assert.Equal("example.test:8443", live.Host);
        Assert.Equal("Reading", live.State);
        Assert.Equal(3, live.RangeStart);
        Assert.Null(live.RangeEnd);
        Assert.Equal(206, live.StatusCode);
        Assert.Equal(0, live.ReceivedBytes);

        content.ReleaseFirstRead();
        await content.SecondReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        live = Assert.Single(engine.GetConnections(job.Id));
        Assert.Equal(2, live.ReceivedBytes);

        content.ReleaseSecondRead();
        var result = await transfer.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.Success, result.Failure?.Message);
        Assert.Empty(engine.GetConnections(job.Id));
        Assert.Equal(prefix.Concat(suffix).ToArray(), await File.ReadAllBytesAsync(job.FinalFilePath));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(prefix.Concat(suffix).ToArray())).ToLowerInvariant(),
            await Hashing.Sha256FileAsync(job.FinalFilePath));
    }

    [Fact]
    public async Task CancellingBlockedResponseReadRemovesTelemetry()
    {
        var content = new GatedReadStream([1, 2, 3, 4], firstReadBytes: 2);
        using var handler = new TestHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(content),
        });
        using var engine = new HttpDownloadEngine(new EngineOptions { MaxAttempts = 1 }, handler);
        using var workspace = new TestWorkspace();
        using var cancellation = new CancellationTokenSource();
        var job = workspace.CreateJob();

        var transfer = engine.ExecuteAsync(job, null, null, cancellation.Token);
        await content.FirstReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(engine.GetConnections(job.Id));

        cancellation.Cancel();
        var result = await transfer.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(DownloadState.Cancelled, result.FinalState);
        Assert.Empty(engine.GetConnections(job.Id));
    }

    [Fact]
    public async Task ResponseStreamFailureRemovesTelemetry()
    {
        using var handler = new TestHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new FaultingReadStream()),
        });
        using var engine = new HttpDownloadEngine(new EngineOptions { MaxAttempts = 1 }, handler);
        using var workspace = new TestWorkspace();
        var job = workspace.CreateJob();

        var result = await engine.ExecuteAsync(job, null, null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(result.Success);
        Assert.Empty(engine.GetConnections(job.Id));
    }

    [Fact]
    public async Task HandlerFailureDoesNotLeaveConnectingTelemetry()
    {
        using var handler = new TestHandler(_ => throw new HttpRequestException("controlled handler failure"));
        using var engine = new HttpDownloadEngine(new EngineOptions { MaxAttempts = 1 }, handler);
        using var workspace = new TestWorkspace();
        var job = workspace.CreateJob();

        var result = await engine.ExecuteAsync(job, null, null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(result.Success);
        Assert.Empty(engine.GetConnections(job.Id));
    }

    private sealed class TestHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }

    private sealed class GatedReadStream(byte[] bytes, int firstReadBytes) : Stream
    {
        private readonly TaskCompletionSource<bool> _firstReadGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _secondReadGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _position;
        private int _readCount;

        public TaskCompletionSource<bool> FirstReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> SecondReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void ReleaseFirstRead() => _firstReadGate.TrySetResult(true);
        public void ReleaseSecondRead() => _secondReadGate.TrySetResult(true);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position >= bytes.Length) return 0;
            var readNumber = Interlocked.Increment(ref _readCount);
            if (readNumber == 1)
            {
                FirstReadStarted.TrySetResult(true);
                await _firstReadGate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            else if (readNumber == 2)
            {
                SecondReadStarted.TrySetResult(true);
                await _secondReadGate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            var limit = readNumber == 1 ? firstReadBytes : bytes.Length - _position;
            var count = Math.Min(buffer.Length, Math.Min(limit, bytes.Length - _position));
            bytes.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }
    }

    private sealed class FaultingReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("controlled response read failure");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromException<int>(new IOException("controlled response read failure"));
    }

    private sealed class TestWorkspace : IDisposable
    {
        private readonly string _root = Path.Combine(AppContext.BaseDirectory, "test-work", "telemetry-" + Guid.NewGuid().ToString("N"));
        public TestWorkspace() => Directory.CreateDirectory(_root);
        public DownloadJob CreateJob() => new()
        {
            Id = Guid.NewGuid(),
            Url = "https://example.test:8443/private/file.bin?token=secret",
            FileName = "telemetry.bin",
            DestinationDirectory = _root,
            FinalFilePath = Path.Combine(_root, "telemetry.bin"),
            PartFilePath = Path.Combine(_root, "telemetry.bin.part"),
            RequestedConnections = 1,
            State = DownloadState.Queued,
        };
        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { }
        }
    }
}
