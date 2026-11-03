using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;

namespace DownloadManager.Core;

public sealed class HttpDownloadEngine : IDownloadEngine, IDisposable
{
    private static readonly HashSet<string> RestrictedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Range", "If-Range", "Content-Length", "Transfer-Encoding",
        "Connection", "Proxy-Connection", "Upgrade", "Expect"
    };

    private readonly EngineOptions _options;
    private readonly HttpClient _client;
    private readonly IDownloadCheckpointStore? _checkpoints;
    private readonly bool _ownsClient;

    public HttpDownloadEngine(
        EngineOptions? options = null,
        HttpMessageHandler? handler = null,
        IDownloadCheckpointStore? checkpoints = null)
    {
        _options = options ?? new EngineOptions();
        _checkpoints = checkpoints;
        if (handler is null)
        {
            var sockets = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.None,
                ConnectTimeout = _options.ConnectTimeout,
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                MaxConnectionsPerServer = 64,
            };
            _client = new HttpClient(sockets, disposeHandler: true);
            _ownsClient = true;
        }
        else
        {
            _client = new HttpClient(handler, disposeHandler: false);
        }

        _client.Timeout = Timeout.InfiniteTimeSpan;
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("DownloadManager/0.1");
    }

    public async Task<DownloadResult> ExecuteAsync(
        DownloadJob job,
        DownloadCheckpoint? checkpoint,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!TryCreateUri(job.Url, out var uri, out var uriError))
        {
            return DownloadResult.Fail(DownloadState.Failed,
                new JobFailure(FailureKind.InvalidContent, uriError, false, false, DateTimeOffset.UtcNow));
        }

        if (!_options.AllowLocalNetwork && IsLocalTarget(uri))
        {
            return DownloadResult.Fail(DownloadState.Failed,
                new JobFailure(FailureKind.Forbidden, "Local network destinations are disabled.", false, false, DateTimeOffset.UtcNow));
        }

        if (string.IsNullOrWhiteSpace(job.PartFilePath))
        {
            var directory = string.IsNullOrWhiteSpace(job.DestinationDirectory)
                ? AppSettings.GetDefaultDownloadDirectory()
                : job.DestinationDirectory;
            job.FileName = FileNameResolver.Sanitize(job.FileName);
            job.FinalFilePath = FileNameResolver.ResolveUniquePath(directory, job.FileName, job.ConflictPolicy);
            job.PartFilePath = job.FinalFilePath + ".part";
        }

        var directoryName = Path.GetDirectoryName(job.PartFilePath);
        if (!string.IsNullOrWhiteSpace(directoryName))
        {
            try
            {
                Directory.CreateDirectory(directoryName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return DownloadResult.Fail(DownloadState.Failed, MapException(ex, out _));
            }
        }

        var state = new TransferState(job, checkpoint)
        {
            PartPath = job.PartFilePath,
        };
        state.InitializeFromDisk();

        var reporter = new TransferReporter(progress, state);
        reporter.Reset();
        var stopwatch = Stopwatch.StartNew();
        var attempt = 0;
        var bestBytes = state.Bytes;

        reporter.Report(force: true, "Connecting");

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempt++;

            try
            {
                var result = await TransferOnceAsync(job, state, reporter, stopwatch, cancellationToken)
                    .ConfigureAwait(false);

                if (result.RestartFromZero)
                {
                    state.RestartFromZero();
                    attempt = 0;
                    bestBytes = 0;
                    reporter.Report(force: true, "Restarting");
                    continue;
                }

                if (result.Complete)
                {
                    return await FinalizeAsync(job, state, reporter, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await SaveCheckpointAsync(job, state, CancellationToken.None).ConfigureAwait(false);
                return DownloadResult.Fail(DownloadState.Cancelled,
                    new JobFailure(FailureKind.Cancelled, "The download was cancelled.", false, true, DateTimeOffset.UtcNow),
                    state.Bytes, state.Total);
            }
            catch (DownloadEngineException ex)
            {
                await SaveCheckpointAsync(job, state, CancellationToken.None).ConfigureAwait(false);

                if (!ex.IsRetryable || attempt >= _options.MaxAttempts)
                {
                    return DownloadResult.Fail(DownloadState.Failed, ex.Failure, state.Bytes, state.Total);
                }

                if (state.Bytes > bestBytes)
                {
                    bestBytes = state.Bytes;
                    attempt = 1;
                }

                var delay = RetryPolicy.Delay(attempt, _options);
                reporter.Report(force: true,
                    $"Retrying in {Math.Max(1, Math.Ceiling(delay.TotalSeconds))}s");
                try
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return DownloadResult.Fail(DownloadState.Cancelled,
                        new JobFailure(FailureKind.Cancelled, "The download was cancelled.", false, true, DateTimeOffset.UtcNow),
                        state.Bytes, state.Total);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var failure = MapException(ex, out var retryable);
                await SaveCheckpointAsync(job, state, CancellationToken.None).ConfigureAwait(false);
                return DownloadResult.Fail(DownloadState.Failed, failure with { IsRetryable = retryable },
                    state.Bytes, state.Total);
            }
        }
    }

    private async Task<AttemptResult> TransferOnceAsync(
        DownloadJob job,
        TransferState state,
        TransferReporter reporter,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        reporter.Report(force: true, state.Bytes > 0 ? "Resuming" : "Connecting");

        using var response = await SendWithRedirectsAsync(job, state.Bytes, cancellationToken).ConfigureAwait(false);
        var status = (int)response.StatusCode;

        if (status is 401 or 403 or 404)
        {
            throw new DownloadEngineException(
                new JobFailure(
                    status == 401 ? FailureKind.Unauthorized : status == 403 ? FailureKind.Forbidden : FailureKind.NotFound,
                    $"The server returned HTTP {status}.",
                    false,
                    false,
                    DateTimeOffset.UtcNow),
                isRetryable: false);
        }

        if (status is 408 or 425 or 429 || status >= 500)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta;
            throw new DownloadEngineException(
                new JobFailure(FailureKind.Http, $"The server returned HTTP {status}.", true, true, DateTimeOffset.UtcNow),
                isRetryable: true,
                retryAfter);
        }

        if (status is < 200 or >= 300)
        {
            throw new DownloadEngineException(
                new JobFailure(FailureKind.Http, $"The server returned HTTP {status}.", false, false, DateTimeOffset.UtcNow),
                isRetryable: false);
        }

        if (status == 416)
        {
            if (state.Total is > 0 && state.Bytes == state.Total)
            {
                return new AttemptResult(Complete: true, RestartFromZero: false);
            }

            if (state.Restarts++ < 2)
            {
                return new AttemptResult(Complete: false, RestartFromZero: true);
            }

            throw new DownloadEngineException(
                new JobFailure(FailureKind.RangeUnsupported,
                    "The server rejected the resume range and restarting did not help.",
                    false, false, DateTimeOffset.UtcNow),
                isRetryable: false);
        }

        if (status == 200 && state.Bytes > 0)
        {
            // The server ignored our Range request. Never append response bytes to
            // an existing partial file: restart from zero.
            if (state.Restarts++ < 2)
            {
                return new AttemptResult(Complete: false, RestartFromZero: true);
            }

            throw new DownloadEngineException(
                new JobFailure(FailureKind.RangeUnsupported,
                    "The server ignores resume ranges and restarting did not help.",
                    false, false, DateTimeOffset.UtcNow),
                isRetryable: false);
        }

        long? responseTotal;
        if (status == 206)
        {
            var range = response.Content.Headers.ContentRange;
            if (range?.From is null || range.To is null)
            {
                throw new DownloadEngineException(
                    new JobFailure(FailureKind.InvalidContent,
                        "The server returned a partial response without a valid Content-Range.",
                        false, false, DateTimeOffset.UtcNow),
                    isRetryable: false);
            }

            if (range.From.Value != state.Bytes)
            {
                if (state.Restarts++ < 2)
                {
                    return new AttemptResult(Complete: false, RestartFromZero: true);
                }

                throw new DownloadEngineException(
                    new JobFailure(FailureKind.InvalidContent,
                        "The server returned an unexpected range start.",
                        false, false, DateTimeOffset.UtcNow),
                    isRetryable: false);
            }

            responseTotal = range.Length;
        }
        else
        {
            responseTotal = response.Content.Headers.ContentLength;
        }

        if (state.Total is > 0 && responseTotal is > 0 && state.Total != responseTotal)
        {
            if (state.Restarts++ < 2)
            {
                return new AttemptResult(Complete: false, RestartFromZero: true);
            }

            throw new DownloadEngineException(
                new JobFailure(FailureKind.RemoteChanged,
                    "The remote file size changed; the partial data cannot be resumed.",
                    false, false, DateTimeOffset.UtcNow),
                isRetryable: false);
        }

        var responseEtag = response.Headers.ETag?.Tag;
        var responseLastModified = response.Content.Headers.LastModified?.ToString("R");
        if (state.Bytes > 0 && ValidatorsConflict(state, responseEtag, responseLastModified))
        {
            if (state.Restarts++ < 2)
            {
                return new AttemptResult(Complete: false, RestartFromZero: true);
            }

            throw new DownloadEngineException(
                new JobFailure(FailureKind.RemoteChanged,
                    "The remote file changed; restarting from zero is required.",
                    false, false, DateTimeOffset.UtcNow),
                isRetryable: false);
        }

        state.Total = responseTotal ?? state.Total;
        state.ETag = responseEtag ?? state.ETag;
        state.LastModified = responseLastModified ?? state.LastModified;
        state.ContentType = response.Content.Headers.ContentType?.ToString();
        await SaveCheckpointAsync(job, state, cancellationToken).ConfigureAwait(false);

        var contentLength = response.Content.Headers.ContentLength;
        var bytesThisResponse = 0L;

        await using (var file = new FileStream(
                         job.PartFilePath,
                         FileMode.OpenOrCreate,
                         FileAccess.Write,
                         FileShare.Read,
                         _options.BufferSize,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            if (state.Bytes == 0)
            {
                file.SetLength(0);
            }
            else if (file.Length < state.Bytes)
            {
                throw new DownloadEngineException(
                    new JobFailure(FailureKind.InvalidContent,
                        "The partial file is shorter than its checkpoint.",
                        false, false, DateTimeOffset.UtcNow),
                    isRetryable: false);
            }

            file.Seek(state.Bytes, SeekOrigin.Begin);
            var buffer = new byte[_options.BufferSize];
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var lastCheckpointAt = DateTimeOffset.UtcNow;
            var checkpointBytes = state.Bytes;

            while (true)
            {
                int read;
                try
                {
                    read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken)
                        .AsTask()
                        .WaitAsync(_options.ReadTimeout, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (TimeoutException ex)
                {
                    throw new DownloadEngineException(
                        new JobFailure(FailureKind.Timeout, "The connection stalled while reading.", true, true, DateTimeOffset.UtcNow),
                        isRetryable: true, inner: ex);
                }

                if (read == 0)
                {
                    break;
                }

                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                state.Bytes += read;
                bytesThisResponse += read;

                reporter.Report(force: false, "Downloading");
                await ThrottleAsync(state.Bytes, stopwatch.Elapsed, job.SpeedLimitBytesPerSecond, cancellationToken)
                    .ConfigureAwait(false);

                var now = DateTimeOffset.UtcNow;
                if (now - lastCheckpointAt >= TimeSpan.FromSeconds(1) || Math.Abs(state.Bytes - checkpointBytes) >= 8 * 1024 * 1024)
                {
                    await SaveCheckpointAsync(job, state, cancellationToken).ConfigureAwait(false);
                    lastCheckpointAt = now;
                    checkpointBytes = state.Bytes;
                }
            }

            await file.FlushAsync(cancellationToken).ConfigureAwait(false);
            file.Flush(flushToDisk: false);
        }

        if (contentLength is > 0 && bytesThisResponse < contentLength)
        {
            throw new DownloadEngineException(
                new JobFailure(FailureKind.Network,
                    "The connection ended before all response bytes arrived.",
                    true, true, DateTimeOffset.UtcNow),
                isRetryable: true);
        }

        if (state.Total is > 0)
        {
            if (state.Bytes > state.Total)
            {
                throw new DownloadEngineException(
                    new JobFailure(FailureKind.InvalidContent,
                        "The server sent more bytes than the declared file size.",
                        false, false, DateTimeOffset.UtcNow),
                    isRetryable: false);
            }

            if (state.Bytes < state.Total)
            {
                throw new DownloadEngineException(
                    new JobFailure(FailureKind.Network,
                        "The transfer ended before the declared file size was reached.",
                        true, true, DateTimeOffset.UtcNow),
                    isRetryable: true);
            }
        }

        return new AttemptResult(Complete: true, RestartFromZero: false);
    }

    private async Task<HttpResponseMessage> SendWithRedirectsAsync(
        DownloadJob job,
        long offset,
        CancellationToken cancellationToken)
    {
        var currentUri = new Uri(job.Url, UriKind.Absolute);
        var originalOrigin = currentUri.GetLeftPart(UriPartial.Authority);
        for (var redirect = 0; redirect <= _options.MaxRedirects; redirect++)
        {
            using var request = BuildRequest(job, currentUri, offset, sameOrigin: currentUri.GetLeftPart(UriPartial.Authority) == originalOrigin);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.ConnectTimeout);

            HttpResponseMessage response;
            try
            {
                response = await _client
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new DownloadEngineException(
                    new JobFailure(FailureKind.Timeout, "The connection attempt timed out.", true, true, DateTimeOffset.UtcNow),
                    isRetryable: true);
            }
            catch (HttpRequestException ex)
            {
                throw ToEngineException(ex);
            }

            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect
                or HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null)
                {
                    throw new DownloadEngineException(
                        new JobFailure(FailureKind.Http, "The redirect response has no Location header.", false, false, DateTimeOffset.UtcNow),
                        isRetryable: false);
                }

                var next = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
                if (next.Scheme is not ("http" or "https"))
                {
                    throw new DownloadEngineException(
                        new JobFailure(FailureKind.Forbidden, "The redirect target uses an unsupported scheme.", false, false, DateTimeOffset.UtcNow),
                        isRetryable: false);
                }

                currentUri = next;
                continue;
            }

            return response;
        }

        throw new DownloadEngineException(
            new JobFailure(FailureKind.Http, "Too many redirects.", false, false, DateTimeOffset.UtcNow),
            isRetryable: false);
    }

    private HttpRequestMessage BuildRequest(DownloadJob job, Uri uri, long offset, bool sameOrigin)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.AcceptEncoding.Clear();
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue(_options.AcceptEncoding));

        if (!string.IsNullOrWhiteSpace(job.UserAgent))
        {
            request.Headers.UserAgent.Clear();
            request.Headers.TryAddWithoutValidation("User-Agent", job.UserAgent);
        }

        foreach (var (key, value) in job.RequestHeaders)
        {
            if (RestrictedHeaders.Contains(key) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (!sameOrigin && (key.Equals("Cookie", StringComparison.OrdinalIgnoreCase) ||
                               key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
                               key.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            request.Headers.TryAddWithoutValidation(key, value);
        }

        if (!string.IsNullOrWhiteSpace(job.Referrer) && sameOrigin &&
            Uri.TryCreate(job.Referrer, UriKind.Absolute, out var referrer))
        {
            request.Headers.Referrer = referrer;
        }

        if (offset > 0)
        {
            request.Headers.Range = new RangeHeaderValue(offset, null);
            if (!string.IsNullOrWhiteSpace(job.ETag) && !job.ETag.StartsWith("W/", StringComparison.Ordinal))
            {
                request.Headers.IfRange = new RangeConditionHeaderValue(job.ETag);
            }
            else if (DateTimeOffset.TryParse(job.LastModified, out var lastModified))
            {
                request.Headers.IfRange = new RangeConditionHeaderValue(lastModified);
            }
        }

        return request;
    }

    private async Task<DownloadResult> FinalizeAsync(
        DownloadJob job,
        TransferState state,
        TransferReporter reporter,
        CancellationToken cancellationToken)
    {
        reporter.Report(force: true, "Verifying");

        if (state.Total is > 0 && state.Bytes != state.Total)
        {
            return DownloadResult.Fail(DownloadState.Failed,
                new JobFailure(FailureKind.InvalidContent,
                    "The final byte count does not match the remote size.",
                    false, true, DateTimeOffset.UtcNow),
                state.Bytes, state.Total);
        }

        var partInfo = new FileInfo(job.PartFilePath);
        if (!partInfo.Exists)
        {
            return DownloadResult.Fail(DownloadState.Failed,
                new JobFailure(FailureKind.DestinationUnavailable,
                    "The partial file disappeared before completion.", false, false, DateTimeOffset.UtcNow),
                state.Bytes, state.Total);
        }

        if (partInfo.Length != state.Bytes)
        {
            return DownloadResult.Fail(DownloadState.Failed,
                new JobFailure(FailureKind.InvalidContent,
                    "The partial file length does not match the transfer state.",
                    false, true, DateTimeOffset.UtcNow),
                state.Bytes, state.Total);
        }

        if (!string.IsNullOrWhiteSpace(job.ExpectedSha256))
        {
            var actual = await Hashing.Sha256FileAsync(job.PartFilePath, cancellationToken).ConfigureAwait(false);
            if (!actual.Equals(job.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                return DownloadResult.Fail(DownloadState.Failed,
                    new JobFailure(FailureKind.InvalidContent,
                        $"SHA-256 mismatch. Expected {job.ExpectedSha256}, got {actual}.",
                        false, true, DateTimeOffset.UtcNow),
                    state.Bytes, state.Total);
            }
        }

        var directory = string.IsNullOrWhiteSpace(job.DestinationDirectory)
            ? Path.GetDirectoryName(job.PartFilePath) ?? AppSettings.GetDefaultDownloadDirectory()
            : job.DestinationDirectory;
        var finalPath = job.FinalFilePath;
        if (string.IsNullOrWhiteSpace(finalPath))
        {
            finalPath = FileNameResolver.ResolveUniquePath(directory, job.FileName, job.ConflictPolicy);
        }

        try
        {
            if (File.Exists(finalPath))
            {
                if (job.ConflictPolicy == ConflictPolicy.Overwrite)
                {
                    File.Delete(finalPath);
                }
                else if (job.ConflictPolicy == ConflictPolicy.Rename)
                {
                    finalPath = FileNameResolver.ResolveUniquePath(directory, Path.GetFileName(finalPath), ConflictPolicy.Rename);
                }
                else
                {
                    return DownloadResult.Fail(DownloadState.Failed,
                        new JobFailure(FailureKind.DestinationUnavailable,
                            $"The destination file already exists: {finalPath}",
                            false, false, DateTimeOffset.UtcNow),
                        state.Bytes, state.Total);
                }
            }

            File.Move(job.PartFilePath, finalPath, overwrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DownloadResult.Fail(DownloadState.Failed, MapException(ex, out _), state.Bytes, state.Total);
        }

        if (_checkpoints is not null)
        {
            try
            {
                await _checkpoints.DeleteAsync(job.Id, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // A stale checkpoint is harmless; the part file is gone.
            }
        }

        return DownloadResult.Ok(state.Bytes, state.Total, finalPath, state.ETag, state.LastModified, state.ContentType);
    }

    private async Task SaveCheckpointAsync(DownloadJob job, TransferState state, CancellationToken cancellationToken)
    {
        if (_checkpoints is null)
        {
            return;
        }

        var checkpoint = new DownloadCheckpoint
        {
            JobId = job.Id,
            Url = job.Url,
            FinalUrl = job.FinalUrl,
            ETag = state.ETag,
            LastModified = state.LastModified,
            TotalBytes = state.Total,
            ContentType = state.ContentType,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        checkpoint.Segments.Add(new SegmentCheckpoint
        {
            Start = 0,
            End = state.Total is > 0 ? state.Total.Value - 1 : long.MaxValue - 1,
            Completed = state.Bytes,
        });

        try
        {
            await _checkpoints.SaveAsync(checkpoint, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            // The payload itself is still written; a missing/stale checkpoint only
            // costs resume precision and is reconciled at startup.
        }
    }

    private static bool ValidatorsConflict(TransferState state, string? responseEtag, string? responseLastModified)
    {
        if (!string.IsNullOrWhiteSpace(state.ETag) && !string.IsNullOrWhiteSpace(responseEtag) &&
            !string.Equals(state.ETag, responseEtag, StringComparison.Ordinal))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(state.LastModified) && !string.IsNullOrWhiteSpace(responseLastModified) &&
            !string.Equals(state.LastModified, responseLastModified, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static async Task ThrottleAsync(long totalBytes, TimeSpan elapsed, long? limit, CancellationToken cancellationToken)
    {
        if (limit is not > 0 || elapsed <= TimeSpan.Zero)
        {
            return;
        }

        var allowedSeconds = totalBytes / (double)limit.Value;
        var delay = TimeSpan.FromSeconds(allowedSeconds) - elapsed;
        if (delay > TimeSpan.FromMilliseconds(15))
        {
            if (delay > TimeSpan.FromSeconds(2))
            {
                delay = TimeSpan.FromSeconds(2);
            }

            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class TransferReporter
    {
        private readonly IProgress<TransferProgress>? _progress;
        private readonly TransferState _state;
        private readonly SpeedEstimator _estimator = new();
        private DateTimeOffset _lastReport = DateTimeOffset.MinValue;

        public TransferReporter(IProgress<TransferProgress>? progress, TransferState state)
        {
            _progress = progress;
            _state = state;
        }

        public void Reset() => _estimator.Reset(_state.Bytes);

        public void Report(bool force, string phase)
        {
            if (_progress is null)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            if (!force && now - _lastReport < TimeSpan.FromMilliseconds(250))
            {
                return;
            }

            var (speed, eta) = _estimator.Update(_state.Bytes, _state.Total, now);
            _lastReport = now;
            try
            {
                _progress.Report(new TransferProgress(_state.Bytes, _state.Total, speed, eta, 1, phase));
            }
            catch
            {
                // Progress observers must not be able to break a transfer.
            }
        }
    }

    private static bool TryCreateUri(string value, out Uri uri, out string error)
    {
        uri = null!;
        error = string.Empty;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed))
        {
            error = "The URL is not absolute.";
            return false;
        }

        if (parsed.Scheme is not ("http" or "https"))
        {
            error = "Only http and https URLs are supported.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(parsed.Host))
        {
            error = "The URL has no host.";
            return false;
        }

        uri = parsed;
        return true;
    }

    private static bool IsLocalTarget(Uri uri)
    {
        if (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!IPAddress.TryParse(uri.Host, out var address))
        {
            return false;
        }

        if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal)
        {
            return true;
        }

        var bytes = address.GetAddressBytes();
        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork =>
                bytes[0] == 10 ||
                (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
                (bytes[0] == 192 && bytes[1] == 168) ||
                (bytes[0] == 169 && bytes[1] == 254),
            AddressFamily.InterNetworkV6 => bytes[0] == 0xfd || bytes[0] == 0xfe,
            _ => false,
        };
    }

    private static JobFailure MapException(Exception ex, out bool retryable)
    {
        switch (ex)
        {
            case DownloadEngineException dee:
                retryable = dee.IsRetryable;
                return dee.Failure;
            case HttpRequestException hre:
                retryable = hre.HttpRequestError is HttpRequestError.ConnectionError
                    or HttpRequestError.NameResolutionError
                    or HttpRequestError.SecureConnectionError
                    or HttpRequestError.ProxyTunnelError
                    or HttpRequestError.HttpProtocolError;
                return new JobFailure(
                    hre.HttpRequestError switch
                    {
                        HttpRequestError.NameResolutionError => FailureKind.Dns,
                        HttpRequestError.SecureConnectionError => FailureKind.Tls,
                        HttpRequestError.ConnectionError => FailureKind.Network,
                        HttpRequestError.HttpProtocolError => FailureKind.InvalidContent,
                        _ => FailureKind.Network,
                    },
                    hre.Message,
                    retryable,
                    true,
                    DateTimeOffset.UtcNow);
            case TimeoutException:
                retryable = true;
                return new JobFailure(FailureKind.Timeout, "The operation timed out.", true, true, DateTimeOffset.UtcNow);
            case IOException io:
                var error = io.HResult & 0xFFFF;
                var kind = error switch
                {
                    0x70 or 0x27 => FailureKind.DiskFull,
                    0x05 => FailureKind.Permission,
                    0x20 or 0x21 => FailureKind.DestinationUnavailable,
                    _ => FailureKind.Network,
                };
                retryable = kind is FailureKind.Network;
                return new JobFailure(kind, io.Message, retryable, kind is FailureKind.Network, DateTimeOffset.UtcNow);
            case UnauthorizedAccessException:
                retryable = false;
                return new JobFailure(FailureKind.Permission, ex.Message, false, false, DateTimeOffset.UtcNow);
            case OperationCanceledException:
                retryable = true;
                return new JobFailure(FailureKind.Cancelled, "The operation was cancelled.", false, true, DateTimeOffset.UtcNow);
            default:
                retryable = false;
                return new JobFailure(FailureKind.Unknown, ex.Message, false, false, DateTimeOffset.UtcNow);
        }
    }

    private static DownloadEngineException ToEngineException(HttpRequestException ex)
    {
        var failure = MapException(ex, out var retryable);
        return new DownloadEngineException(failure, retryable, inner: ex);
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }

    private sealed class TransferState
    {
        public TransferState(DownloadJob job, DownloadCheckpoint? checkpoint)
        {
            Total = checkpoint?.TotalBytes ?? job.TotalBytes;
            ETag = checkpoint?.ETag ?? job.ETag;
            LastModified = checkpoint?.LastModified ?? job.LastModified;
            ContentType = checkpoint?.ContentType ?? job.ContentType;
            CheckpointPresent = checkpoint is not null && checkpoint.JobId == job.Id;
        }

        public long Bytes { get; set; }
        public long? Total { get; set; }
        public string? ETag { get; set; }
        public string? LastModified { get; set; }
        public string? ContentType { get; set; }
        public int Restarts { get; set; }
        public bool CheckpointPresent { get; }

        public void InitializeFromDisk()
        {
            if (!File.Exists(PartPath))
            {
                Bytes = 0;
                if (!CheckpointPresent)
                {
                    Total = null;
                    ETag = null;
                    LastModified = null;
                }

                return;
            }

            var length = new FileInfo(PartPath).Length;
            if (!CheckpointPresent)
            {
                // Without metadata we cannot safely prove what the bytes belong to.
                Bytes = 0;
                Total = null;
                ETag = null;
                LastModified = null;
                return;
            }

            Bytes = length;
            if (Total is > 0 && Bytes > Total)
            {
                Bytes = Total.Value;
            }
        }

        public void RestartFromZero()
        {
            Bytes = 0;
            Total = null;
            ETag = null;
            LastModified = null;
            ContentType = null;
            try
            {
                if (File.Exists(PartPath))
                {
                    using var stream = new FileStream(PartPath, FileMode.Open, FileAccess.Write, FileShare.Read);
                    stream.SetLength(0);
                }
            }
            catch (IOException)
            {
                // The next write/open will surface the real error.
            }
        }

        // Set by ExecuteAsync's caller below; kept here to make state self-contained.
        public string PartPath { get; set; } = string.Empty;
    }

    private sealed record AttemptResult(bool Complete, bool RestartFromZero);

    private sealed class DownloadEngineException : Exception
    {
        public DownloadEngineException(JobFailure failure, bool isRetryable, TimeSpan? retryAfter = null, Exception? inner = null)
            : base(failure.Message, inner)
        {
            Failure = failure;
            IsRetryable = isRetryable;
        }

        public JobFailure Failure { get; }
        public bool IsRetryable { get; }
    }
}
