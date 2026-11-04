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
    private readonly IDownloadCheckpointStore? _checkpoints;
    private readonly bool _ownsClient;
    private readonly object _clientSync = new();
    private readonly List<HttpClient> _retiredClients = new();
    private HttpClient _client;

    public HttpDownloadEngine(
        EngineOptions? options = null,
        HttpMessageHandler? handler = null,
        IDownloadCheckpointStore? checkpoints = null)
    {
        _options = options ?? new EngineOptions();
        _checkpoints = checkpoints;
        if (handler is null)
        {
            _client = CreateDefaultClient();
            _ownsClient = true;
        }
        else
        {
            _client = new HttpClient(handler, disposeHandler: false);
            ConfigureClient(_client);
        }
    }

    private HttpClient CreateDefaultClient()
    {
        var sockets = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = _options.ConnectTimeout,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            MaxConnectionsPerServer = 64,
            Proxy = new ConfigurableProxy(_options.Proxy),
            UseProxy = true,
            UseCookies = false,
        };

        var client = new HttpClient(sockets, disposeHandler: true);
        ConfigureClient(client);
        return client;
    }

    private static void ConfigureClient(HttpClient client)
    {
        client.Timeout = Timeout.InfiniteTimeSpan;
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DownloadManager/0.1");
    }

    private HttpClient CurrentClient()
    {
        lock (_clientSync)
        {
            return _client;
        }
    }

    public void UpdateProxy(ProxySettings settings)
    {
        var changed = !string.Equals(_options.Proxy.Mode, settings.Mode, StringComparison.OrdinalIgnoreCase) ||
                      !string.Equals(_options.Proxy.Address, settings.Address, StringComparison.Ordinal) ||
                      !string.Equals(_options.Proxy.Username, settings.Username, StringComparison.Ordinal) ||
                      !string.Equals(_options.Proxy.Password, settings.Password, StringComparison.Ordinal);
        if (!changed)
        {
            return;
        }

        _options.Proxy.Mode = settings.Mode;
        _options.Proxy.Address = settings.Address;
        _options.Proxy.Username = settings.Username;
        _options.Proxy.Password = settings.Password;

        if (!_ownsClient)
        {
            return;
        }

        // SocketsHttpHandler can pin the proxy scheme/credentials to an existing
        // connection. Rebuild the client so switching between HTTP and SOCKS5 (or
        // changing credentials) applies to new requests instead of reusing a stale
        // tunnel configuration.
        lock (_clientSync)
        {
            var previous = _client;
            _client = CreateDefaultClient();
            _retiredClients.Add(previous);
        }
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

        if (job.RequestedConnections > 1 &&
            job.SpeedLimitBytesPerSecond is not > 0 &&
            (checkpoint is null || checkpoint.Segments.Count > 1))
        {
            var segmented = await TryExecuteSegmentedAsync(job, checkpoint, progress, cancellationToken)
                .ConfigureAwait(false);
            if (segmented is not null)
            {
                return segmented;
            }

            // The segmented attempt resets the part/checkpoint when it cannot
            // safely continue, so the single-stream path starts from zero.
            checkpoint = null;
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

        if (status is 401 or 403 or 404 or 407 or 410)
        {
            throw new DownloadEngineException(
                ClassifyHttpFailure(status, response),
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
        CancellationToken cancellationToken,
        long? rangeEnd = null)
    {
        var currentUri = new Uri(job.Url, UriKind.Absolute);
        var originalOrigin = currentUri.GetLeftPart(UriPartial.Authority);
        for (var redirect = 0; redirect <= _options.MaxRedirects; redirect++)
        {
            using var request = BuildRequest(job, currentUri, offset,
                sameOrigin: currentUri.GetLeftPart(UriPartial.Authority) == originalOrigin,
                rangeEnd: rangeEnd);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.ConnectTimeout);

            HttpResponseMessage response;
            try
            {
                response = await CurrentClient()
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

    private HttpRequestMessage BuildRequest(
        DownloadJob job,
        Uri uri,
        long offset,
        bool sameOrigin,
        long? rangeEnd = null)
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

        if (offset > 0 || rangeEnd is not null)
        {
            request.Headers.Range = new RangeHeaderValue(offset, rangeEnd);
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

    private async Task<DownloadResult?> TryExecuteSegmentedAsync(
        DownloadJob job,
        DownloadCheckpoint? checkpoint,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        SegmentedPlan? plan;
        try
        {
            plan = await ProbeSegmentedAsync(job, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }

        if (plan is null || plan.Total < 1024 * 1024)
        {
            return null;
        }

        if (checkpoint is not null)
        {
            if (checkpoint.TotalBytes != plan.Total ||
                checkpoint.Segments.Count < 2 ||
                !SegmentsValid(checkpoint.Segments, plan.Total) ||
                !File.Exists(job.PartFilePath) ||
                new FileInfo(job.PartFilePath).Length != plan.Total ||
                ValidatorsConflict(checkpoint.ETag, checkpoint.LastModified, plan.ETag, plan.LastModified))
            {
                await ResetSegmentedAsync(job).ConfigureAwait(false);
                return null;
            }
        }
        else
        {
            try
            {
                if (File.Exists(job.PartFilePath))
                {
                    File.Delete(job.PartFilePath);
                }
            }
            catch (IOException)
            {
                return null;
            }
        }

        var segments = checkpoint is null
            ? BuildSegments(plan.Total, job.RequestedConnections)
            : checkpoint.Segments.Select(CloneSegment).ToList();
        if (segments.Count < 2)
        {
            return null;
        }

        job.ETag = plan.ETag ?? job.ETag;
        job.LastModified = plan.LastModified ?? job.LastModified;
        job.ContentType = plan.ContentType ?? job.ContentType;

        FileStream file;
        try
        {
            file = new FileStream(
                job.PartFilePath,
                checkpoint is null ? FileMode.Create : FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.ReadWrite,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.RandomAccess);
            if (checkpoint is null)
            {
                file.SetLength(plan.Total);
            }

            if (file.Length != plan.Total)
            {
                await file.DisposeAsync().ConfigureAwait(false);
                await ResetSegmentedAsync(job).ConfigureAwait(false);
                return null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DownloadResult.Fail(DownloadState.Failed, MapException(ex, out _));
        }

        var run = new SegmentedRun(job, plan, file, segments)
        {
            AggregateBytes = segments.Sum(s => s.Completed),
        };
        var reporter = new SegmentedReporter(progress, run);
        run.Reporter = reporter;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        run.Cancellation = linked;
        var tasks = segments.Select(segment => DownloadSegmentAsync(run, segment, linked.Token)).ToArray();
        DownloadResult? failure = null;

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // User cancellation or a segment aborting the rest.
        }
        catch (SegmentationUnsupportedException)
        {
            run.Unsupported = true;
        }
        catch (SegmentFailureException ex)
        {
            failure = DownloadResult.Fail(DownloadState.Failed, ex.Failure, run.AggregateBytes, plan.Total);
        }
        catch (Exception ex)
        {
            failure = DownloadResult.Fail(DownloadState.Failed,
                new JobFailure(FailureKind.Unknown, ex.Message, false, true, DateTimeOffset.UtcNow),
                run.AggregateBytes, plan.Total);
        }
        finally
        {
            if (run.Unsupported || failure is not null)
            {
                linked.Cancel();
            }

            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The first failure is already captured above.
            }

            await file.DisposeAsync().ConfigureAwait(false);
        }

        if (run.Unsupported)
        {
            await ResetSegmentedAsync(job).ConfigureAwait(false);
            return null;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            await SaveSegmentedCheckpointAsync(run, CancellationToken.None).ConfigureAwait(false);
            return DownloadResult.Fail(DownloadState.Cancelled,
                new JobFailure(FailureKind.Cancelled, "The download was cancelled.", false, true, DateTimeOffset.UtcNow),
                run.AggregateBytes, plan.Total);
        }

        if (failure is not null)
        {
            await SaveSegmentedCheckpointAsync(run, CancellationToken.None).ConfigureAwait(false);
            return failure;
        }

        if (run.AggregateBytes != plan.Total || !segments.All(s => s.Completed >= s.End - s.Start + 1))
        {
            await SaveSegmentedCheckpointAsync(run, CancellationToken.None).ConfigureAwait(false);
            return DownloadResult.Fail(DownloadState.Failed,
                new JobFailure(FailureKind.Network,
                    "The segmented transfer ended before all bytes arrived.",
                    true, true, DateTimeOffset.UtcNow),
                run.AggregateBytes, plan.Total);
        }

        var finalState = new TransferState(job, null)
        {
            PartPath = job.PartFilePath,
            Bytes = run.AggregateBytes,
            Total = plan.Total,
            ETag = plan.ETag,
            LastModified = plan.LastModified,
            ContentType = plan.ContentType,
        };
        var finalReporter = new TransferReporter(progress, finalState);
        finalReporter.Reset();
        return await FinalizeAsync(job, finalState, finalReporter, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SegmentedPlan?> ProbeSegmentedAsync(DownloadJob job, CancellationToken cancellationToken)
    {
        using var response = await SendWithRedirectsAsync(job, 0, cancellationToken, rangeEnd: 0)
            .ConfigureAwait(false);
        if ((int)response.StatusCode != 206)
        {
            return null;
        }

        var range = response.Content.Headers.ContentRange;
        if (range?.From != 0 || range.To is null || range.Length is not > 0)
        {
            return null;
        }

        return new SegmentedPlan(
            range.Length.Value,
            response.Headers.ETag?.Tag,
            response.Content.Headers.LastModified?.ToString("R"),
            response.Content.Headers.ContentType?.ToString());
    }

    private async Task DownloadSegmentAsync(
        SegmentedRun run,
        SegmentCheckpoint segment,
        CancellationToken cancellationToken)
    {
        var job = run.Job;
        var length = segment.End - segment.Start + 1;
        var attempt = 0;
        Interlocked.Increment(ref run.ActiveConnections);
        try
        {
            while (segment.Completed < length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                attempt++;
                try
                {
                    var offset = segment.Start + segment.Completed;
                    using var response = await SendWithRedirectsAsync(job, offset, cancellationToken, segment.End)
                        .ConfigureAwait(false);
                    var status = (int)response.StatusCode;

                    if (status == 200)
                    {
                        throw new SegmentationUnsupportedException();
                    }

                    if (status == 416)
                    {
                        if (segment.Completed == length)
                        {
                            break;
                        }

                        throw new SegmentationUnsupportedException();
                    }

                    if (status is 401 or 403 or 404 or 407 or 410)
                    {
                        throw new SegmentFailureException(
                            ClassifyHttpFailure(status, response, segment: true),
                            retryable: false);
                    }

                    if (status is 408 or 425 or 429 || status >= 500)
                    {
                        throw new SegmentFailureException(
                            new JobFailure(FailureKind.Http, $"The server returned HTTP {status} for a segment.",
                                true, true, DateTimeOffset.UtcNow),
                            retryable: true);
                    }

                    if (status != 206)
                    {
                        throw new SegmentFailureException(
                            new JobFailure(FailureKind.Http, $"Unexpected HTTP {status} for a segment.",
                                false, false, DateTimeOffset.UtcNow),
                            retryable: false);
                    }

                    var range = response.Content.Headers.ContentRange;
                    if (range?.From != offset || range.To is null || range.To > segment.End)
                    {
                        throw new SegmentationUnsupportedException();
                    }

                    if (range.Length != run.Plan.Total)
                    {
                        throw new SegmentFailureException(
                            new JobFailure(FailureKind.RemoteChanged,
                                "The remote file size changed during a segmented transfer.",
                                false, false, DateTimeOffset.UtcNow),
                            retryable: false);
                    }

                    var responseEtag = response.Headers.ETag?.Tag;
                    var responseLastModified = response.Content.Headers.LastModified?.ToString("R");
                    if (ValidatorsConflict(job.ETag, job.LastModified, responseEtag, responseLastModified))
                    {
                        throw new SegmentFailureException(
                            new JobFailure(FailureKind.RemoteChanged,
                                "The remote file changed during a segmented transfer.",
                                false, false, DateTimeOffset.UtcNow),
                            retryable: false);
                    }

                    var remaining = length - segment.Completed;
                    var contentLength = response.Content.Headers.ContentLength;
                    if (contentLength is > 0 && contentLength != remaining)
                    {
                        throw new SegmentFailureException(
                            new JobFailure(FailureKind.InvalidContent,
                                "The server returned a segment with an unexpected length.",
                                true, true, DateTimeOffset.UtcNow),
                            retryable: true);
                    }

                    var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    var buffer = new byte[_options.BufferSize];
                    var writeOffset = offset;
                    while (true)
                    {
                        var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken)
                            .AsTask()
                            .WaitAsync(_options.ReadTimeout, cancellationToken)
                            .ConfigureAwait(false);
                        if (read == 0)
                        {
                            break;
                        }

                        if (read > length - segment.Completed)
                        {
                            throw new SegmentFailureException(
                                new JobFailure(FailureKind.InvalidContent,
                                    "A segment response exceeded its range.",
                                    false, false, DateTimeOffset.UtcNow),
                                retryable: false);
                        }

                        await RandomAccess.WriteAsync(
                                run.File.SafeFileHandle,
                                buffer.AsMemory(0, read),
                                writeOffset,
                                cancellationToken)
                            .ConfigureAwait(false);
                        writeOffset += read;

                        lock (run.Sync)
                        {
                            segment.Completed += read;
                            run.AggregateBytes += read;
                        }

                        run.Reporter.Report(force: false, "Downloading");
                        await MaybeSaveSegmentedCheckpointAsync(run, cancellationToken).ConfigureAwait(false);
                    }

                    if (segment.Completed < length)
                    {
                        throw new SegmentFailureException(
                            new JobFailure(FailureKind.Network,
                                "A segment connection ended before its range completed.",
                                true, true, DateTimeOffset.UtcNow),
                            retryable: true);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (SegmentationUnsupportedException)
                {
                    throw;
                }
                catch (SegmentFailureException ex)
                {
                    if (!ex.Retryable || attempt >= _options.MaxAttempts)
                    {
                        throw;
                    }

                    await SaveSegmentedCheckpointAsync(run, CancellationToken.None).ConfigureAwait(false);
                    await Task.Delay(RetryPolicy.Delay(attempt, _options), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    var failure = MapException(ex, out var retryable);
                    if (!retryable || attempt >= _options.MaxAttempts)
                    {
                        throw new SegmentFailureException(failure, retryable);
                    }

                    await SaveSegmentedCheckpointAsync(run, CancellationToken.None).ConfigureAwait(false);
                    await Task.Delay(RetryPolicy.Delay(attempt, _options), cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            Interlocked.Decrement(ref run.ActiveConnections);
        }
    }

    private async Task MaybeSaveSegmentedCheckpointAsync(SegmentedRun run, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        lock (run.Sync)
        {
            if (now - run.LastCheckpoint < TimeSpan.FromSeconds(1))
            {
                return;
            }

            run.LastCheckpoint = now;
        }

        await SaveSegmentedCheckpointAsync(run, cancellationToken).ConfigureAwait(false);
    }

    private async Task SaveSegmentedCheckpointAsync(SegmentedRun run, CancellationToken cancellationToken)
    {
        if (_checkpoints is null)
        {
            return;
        }

        await run.CheckpointGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DownloadCheckpoint checkpoint;
            lock (run.Sync)
            {
                checkpoint = new DownloadCheckpoint
                {
                    JobId = run.Job.Id,
                    Url = run.Job.Url,
                    FinalUrl = run.Job.FinalUrl,
                    ETag = run.Plan.ETag,
                    LastModified = run.Plan.LastModified,
                    TotalBytes = run.Plan.Total,
                    ContentType = run.Plan.ContentType,
                    Segments = run.Segments.Select(CloneSegment).ToList(),
                };
            }

            await _checkpoints.SaveAsync(checkpoint, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            // A missing checkpoint only costs resume precision.
        }
        finally
        {
            run.CheckpointGate.Release();
        }
    }

    private async Task ResetSegmentedAsync(DownloadJob job)
    {
        try
        {
            if (File.Exists(job.PartFilePath))
            {
                using var stream = new FileStream(job.PartFilePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                stream.SetLength(0);
            }
        }
        catch (IOException)
        {
            // best effort; the next download will truncate it
        }

        if (_checkpoints is not null)
        {
            try
            {
                await _checkpoints.DeleteAsync(job.Id, CancellationToken.None).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // best effort
            }
        }
    }

    private static List<SegmentCheckpoint> BuildSegments(long total, int requestedConnections)
    {
        const long minimumSegment = 256 * 1024;
        var count = Math.Clamp(requestedConnections, 1, 32);
        count = (int)Math.Min(count, Math.Max(1, total / minimumSegment));
        if (count < 2)
        {
            return new List<SegmentCheckpoint>
            {
                new() { Start = 0, End = total - 1, Completed = 0 },
            };
        }

        var baseSize = total / count;
        var remainder = total % count;
        var segments = new List<SegmentCheckpoint>(count);
        long start = 0;
        for (var i = 0; i < count; i++)
        {
            var size = baseSize + (i < remainder ? 1 : 0);
            segments.Add(new SegmentCheckpoint
            {
                Start = start,
                End = start + size - 1,
                Completed = 0,
            });
            start += size;
        }

        return segments;
    }

    private static bool SegmentsValid(IReadOnlyList<SegmentCheckpoint> segments, long total)
    {
        long expectedStart = 0;
        foreach (var segment in segments.OrderBy(s => s.Start))
        {
            if (segment.Start != expectedStart || segment.End < segment.Start || segment.End >= total)
            {
                return false;
            }

            var length = segment.End - segment.Start + 1;
            if (segment.Completed < 0 || segment.Completed > length)
            {
                return false;
            }

            expectedStart = segment.End + 1;
        }

        return expectedStart == total;
    }

    private static SegmentCheckpoint CloneSegment(SegmentCheckpoint segment)
        => new()
        {
            Start = segment.Start,
            End = segment.End,
            Completed = segment.Completed,
        };

    private static bool ValidatorsConflict(TransferState state, string? responseEtag, string? responseLastModified)
        => ValidatorsConflict(state.ETag, state.LastModified, responseEtag, responseLastModified);

    private static bool ValidatorsConflict(
        string? expectedEtag,
        string? expectedLastModified,
        string? responseEtag,
        string? responseLastModified)
    {
        if (!string.IsNullOrWhiteSpace(expectedEtag) && !string.IsNullOrWhiteSpace(responseEtag) &&
            !string.Equals(expectedEtag, responseEtag, StringComparison.Ordinal))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(expectedLastModified) && !string.IsNullOrWhiteSpace(responseLastModified) &&
            !string.Equals(expectedLastModified, responseLastModified, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private sealed record SegmentedPlan(
        long Total,
        string? ETag,
        string? LastModified,
        string? ContentType);

    private sealed class SegmentedRun
    {
        public SegmentedRun(
            DownloadJob job,
            SegmentedPlan plan,
            FileStream file,
            List<SegmentCheckpoint> segments)
        {
            Job = job;
            Plan = plan;
            File = file;
            Segments = segments;
        }

        public DownloadJob Job { get; }
        public SegmentedPlan Plan { get; }
        public FileStream File { get; }
        public List<SegmentCheckpoint> Segments { get; }
        public object Sync { get; } = new();
        public long AggregateBytes { get; set; }
        public int ActiveConnections;
        public bool Unsupported { get; set; }
        public DateTimeOffset LastCheckpoint { get; set; } = DateTimeOffset.MinValue;
        public SemaphoreSlim CheckpointGate { get; } = new(1, 1);
        public CancellationTokenSource? Cancellation { get; set; }
        public SegmentedReporter Reporter { get; set; } = null!;
    }

    private sealed class SegmentedReporter
    {
        private readonly IProgress<TransferProgress>? _progress;
        private readonly SegmentedRun _run;
        private readonly SpeedEstimator _estimator = new();
        private DateTimeOffset _lastReport = DateTimeOffset.MinValue;

        public SegmentedReporter(IProgress<TransferProgress>? progress, SegmentedRun run)
        {
            _progress = progress;
            _run = run;
        }

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

            long bytes;
            int active;
            lock (_run.Sync)
            {
                bytes = _run.AggregateBytes;
                active = _run.ActiveConnections;
            }

            var (speed, eta) = _estimator.Update(bytes, _run.Plan.Total, now);
            _lastReport = now;
            try
            {
                _progress.Report(new TransferProgress(bytes, _run.Plan.Total, speed, eta, active, phase));
            }
            catch
            {
                // progress observers must not break the transfer
            }
        }
    }

    private sealed class SegmentationUnsupportedException : Exception
    {
    }

    private sealed class SegmentFailureException : Exception
    {
        public SegmentFailureException(JobFailure failure, bool retryable)
        {
            Failure = failure;
            Retryable = retryable;
        }

        public JobFailure Failure { get; }
        public bool Retryable { get; }
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
                    $"The partial file length ({partInfo.Length}) does not match the transfer state ({state.Bytes}).",
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

    private static JobFailure ClassifyHttpFailure(
        int status,
        HttpResponseMessage response,
        bool segment = false)
    {
        var expiredHeader = response.Headers.TryGetValues("X-Url-Expired", out var values) &&
                            values.Any(value => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase));
        var expired = status == 410 || (status == 403 && expiredHeader);
        var kind = expired
            ? FailureKind.ExpiredUrl
            : status is 401 or 407 ? FailureKind.Unauthorized
            : status == 403 ? FailureKind.Forbidden
            : FailureKind.NotFound;
        var message = expired
            ? "The download URL has expired. Use Refresh Download Address and try again."
            : status == 407
                ? (segment
                    ? "Proxy authentication failed for a segment (HTTP 407)."
                    : "Proxy authentication failed (HTTP 407).")
                : segment
                    ? $"The server returned HTTP {status} for a segment."
                    : $"The server returned HTTP {status}.";
        var resumable = status is 401 or 403 or 410;
        return new JobFailure(kind, message, false, resumable, DateTimeOffset.UtcNow);
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
        if (!_ownsClient)
        {
            return;
        }

        lock (_clientSync)
        {
            _client.Dispose();
            foreach (var retired in _retiredClients)
            {
                retired.Dispose();
            }

            _retiredClients.Clear();
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

internal sealed class ConfigurableProxy(ProxySettings settings) : IWebProxy
{
    public ICredentials? Credentials
    {
        get => string.IsNullOrWhiteSpace(settings.Username)
            ? null
            : new NetworkCredential(settings.Username, settings.Password);
        set
        {
            // Credentials come from the live settings object.
        }
    }

    public Uri? GetProxy(Uri destination)
    {
        var mode = settings.Mode?.Trim().ToLowerInvariant();
        if (mode == "system")
        {
            return HttpClient.DefaultProxy.GetProxy(destination);
        }

        if (mode != "manual" || string.IsNullOrWhiteSpace(settings.Address))
        {
            return null;
        }

        var address = settings.Address.Trim();
        if (!address.Contains("://", StringComparison.Ordinal))
        {
            address = "http://" + address;
        }

        return Uri.TryCreate(address, UriKind.Absolute, out var proxy) ? proxy : null;
    }

    public bool IsBypassed(Uri host) => GetProxy(host) is null;
}
