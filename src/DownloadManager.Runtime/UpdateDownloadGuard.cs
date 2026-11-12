using System.Net;

namespace DownloadManager.Runtime;

/// <summary>
/// UPD.N2: the checks the update download made on its own single stream, kept now that the engine fetches the package
/// (in segments when the server allows). Every request, each redirect and each range included, must go to an HTTPS
/// address (loopback is allowed for testing), and no response may carry more than the signed size. A refusal throws an
/// exception the engine doesn't retry, and <see cref="Refusal"/> keeps its message for the result.
/// </summary>
internal sealed class UpdateDownloadGuard(long signedSize, HttpMessageHandler inner) : DelegatingHandler(inner)
{
    public const string NotHttps = "The installer redirected to a non-HTTPS host.";
    public const string TooLarge = "The installer is larger than the signed size.";

    private string? _refusal;

    /// <summary>The first refusal's message, or null when nothing was refused.</summary>
    public string? Refusal => Volatile.Read(ref _refusal);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is not { } uri || (uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback))
        {
            throw Refuse(NotHttps);
        }

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return response;

        var declared = response.StatusCode == HttpStatusCode.PartialContent
            ? response.Content.Headers.ContentRange?.Length ?? response.Content.Headers.ContentLength
            : response.Content.Headers.ContentLength;
        if (declared > signedSize)
        {
            response.Dispose();
            throw Refuse(TooLarge);
        }

        response.Content = new CappedContent(response.Content, signedSize, this);
        return response;
    }

    private InvalidOperationException Refuse(string message)
    {
        Interlocked.CompareExchange(ref _refusal, message, null);
        return new InvalidOperationException(message);
    }

    /// <summary>A response without a length (or with a wrong one) still stops one byte past the signed size.</summary>
    private sealed class CappedContent : HttpContent
    {
        private readonly HttpContent _inner;
        private readonly long _limit;
        private readonly UpdateDownloadGuard _guard;

        public CappedContent(HttpContent inner, long limit, UpdateDownloadGuard guard)
        {
            _inner = inner;
            _limit = limit;
            _guard = guard;
            foreach (var header in inner.Headers) Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _inner.Headers.ContentLength ?? 0;
            return _inner.Headers.ContentLength.HasValue;
        }

        protected override async Task<Stream> CreateContentReadStreamAsync()
            => new CappedStream(await _inner.ReadAsStreamAsync().ConfigureAwait(false), _limit, _guard);

        protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
            => new CappedStream(await _inner.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), _limit, _guard);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await using var input = await CreateContentReadStreamAsync().ConfigureAwait(false);
            await input.CopyToAsync(stream).ConfigureAwait(false);
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            await using var input = await CreateContentReadStreamAsync(cancellationToken).ConfigureAwait(false);
            await input.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
        }

        protected override void Dispose(bool disposing)
        {
            try { if (disposing) _inner.Dispose(); }
            finally { base.Dispose(disposing); }
        }
    }

    private sealed class CappedStream(Stream inner, long limit, UpdateDownloadGuard guard) : Stream
    {
        private long _read;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

        public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => Count(await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => Count(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

        private int Count(int read)
        {
            _read += read;
            if (_read > limit) throw guard.Refuse(TooLarge);
            return read;
        }

        protected override void Dispose(bool disposing)
        {
            try { if (disposing) inner.Dispose(); }
            finally { base.Dispose(disposing); }
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }
    }
}
