using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;

namespace DownloadManager.Core;

public sealed partial class HttpDownloadEngine : IConnectionTelemetrySource
{
    private readonly ConcurrentDictionary<Guid, LiveConnection> _connections = new();

    public IReadOnlyList<ConnectionTelemetry> GetConnections(Guid jobId) => _connections.Values
        .Where(connection => connection.JobId == jobId).Select(connection => connection.Snapshot())
        .OrderBy(connection => connection.StartedAt).ToList();

    private async Task<HttpResponseMessage> SendTrackedAsync(DownloadJob job, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Every request of such a download, a playlist's segments and each redirect included, before anything is sent;
        // and every request of any download while "allow local network" is off.
        if ((job.PublicNetworkOnly || !_options.AllowLocalNetwork) &&
            request.RequestUri is { } target && NetworkAddresses.IsLocalTarget(target))
        {
            throw new DownloadEngineException(
                new JobFailure(FailureKind.Forbidden, LocalNetworkRefused, false, false, DateTimeOffset.UtcNow),
                isRetryable: false);
        }

        var connection = new LiveConnection(this, job.Id, request);
        _connections[connection.Id] = connection;
        try
        {
            var response = await CurrentClient(job).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            connection.Headers(response);
            response.Content = new TrackedContent(response.Content, connection);
            return response;
        }
        catch { connection.Dispose(); throw; }
    }

    private sealed class LiveConnection : IDisposable
    {
        private readonly HttpDownloadEngine _owner;
        private readonly object _sync = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly Queue<(double Seconds, long Bytes)> _history = new();
        private readonly string _host;
        private readonly long? _start;
        private readonly long? _end;
        private readonly DateTimeOffset _started = DateTimeOffset.UtcNow;
        private string _state = "Connecting";
        private long _bytes;
        private double _lastBytesAt;
        private int? _status;
        private string? _version;
        public Guid Id { get; } = Guid.NewGuid();
        public Guid JobId { get; }

        public LiveConnection(HttpDownloadEngine owner, Guid jobId, HttpRequestMessage request)
        {
            _owner = owner; JobId = jobId;
            // Host and port only. Never expose request paths, user info, tokens or headers.
            _host = request.RequestUri?.Authority ?? string.Empty;
            var range = request.Headers.Range?.Ranges.FirstOrDefault();
            _start = range?.From; _end = range?.To;
            _history.Enqueue((0, 0));
        }
        public void Headers(HttpResponseMessage response)
        { lock (_sync) { _status = (int)response.StatusCode; _version = response.Version.ToString(); _state = "Open"; } }
        public void Reading() { lock (_sync) _state = "Reading"; }
        public void Read(int count)
        {
            lock (_sync)
            {
                _bytes += Math.Max(0, count);
                var now = _clock.Elapsed.TotalSeconds;
                if (count > 0) _lastBytesAt = now;
                if (now - _history.Last().Seconds >= 0.2) _history.Enqueue((now, _bytes));
                while (_history.Count > 2 && now - _history.Peek().Seconds > 2) _history.Dequeue();
                _state = count == 0 ? "End of stream" : "Open";
            }
        }
        public ConnectionTelemetry Snapshot()
        {
            lock (_sync)
            {
                var now = _clock.Elapsed.TotalSeconds;
                while (_history.Count > 2 && now - _history.Peek().Seconds > 2) _history.Dequeue();
                var first = _history.Peek();
                var rate = now - _lastBytesAt > 2 ? 0 : Math.Max(0, (_bytes - first.Bytes) / Math.Max(0.001, now - first.Seconds));
                return new ConnectionTelemetry(Id, _host, _state, _start, _end, _bytes, rate, _status, _version, _started);
            }
        }
        public void Dispose() => _owner._connections.TryRemove(Id, out _);
    }

    private sealed class TrackedContent : HttpContent
    {
        private readonly HttpContent _inner;
        private readonly LiveConnection _connection;
        public TrackedContent(HttpContent inner, LiveConnection connection)
        {
            _inner = inner; _connection = connection;
            foreach (var header in inner.Headers) Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        protected override bool TryComputeLength(out long length)
        { length = _inner.Headers.ContentLength ?? 0; return _inner.Headers.ContentLength.HasValue; }
        protected override async Task<Stream> CreateContentReadStreamAsync() =>
            new TrackedStream(await _inner.ReadAsStreamAsync().ConfigureAwait(false), _connection);
        protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
            new TrackedStream(await _inner.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), _connection);
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            using var input = new TrackedStream(await _inner.ReadAsStreamAsync().ConfigureAwait(false), _connection);
            await input.CopyToAsync(stream).ConfigureAwait(false);
        }
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            using var input = new TrackedStream(await _inner.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), _connection);
            await input.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
        }
        protected override void Dispose(bool disposing)
        {
            try { if (disposing) _inner.Dispose(); }
            finally { _connection.Dispose(); base.Dispose(disposing); }
        }
    }

    private sealed class TrackedStream(Stream inner, LiveConnection connection) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count)
        { connection.Reading(); var read = inner.Read(buffer, offset, count); connection.Read(read); return read; }
        public override int Read(Span<byte> buffer)
        { connection.Reading(); var read = inner.Read(buffer); connection.Read(read); return read; }
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        { connection.Reading(); var read = await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false); connection.Read(read); return read; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { connection.Reading(); var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false); connection.Read(read); return read; }
        protected override void Dispose(bool disposing)
        { try { if (disposing) inner.Dispose(); } finally { connection.Dispose(); base.Dispose(disposing); } }
        public override async ValueTask DisposeAsync()
        { try { await inner.DisposeAsync().ConfigureAwait(false); } finally { connection.Dispose(); GC.SuppressFinalize(this); } }
    }
}
