using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace DownloadManager.Core;

public sealed partial class HttpDownloadEngine : IDownloadEngine, IFileMetadataReader, IDisposable
{
    private static readonly HashSet<string> RestrictedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Range", "If-Range", "Content-Length", "Transfer-Encoding",
        "Connection", "Proxy-Connection", "Upgrade", "Expect"
    };

    private readonly EngineOptions _options;
    private readonly IDownloadCheckpointStore? _checkpoints;
    private readonly IFreeSpaceProvider _freeSpace;
    private readonly FairHostConnectionLimiter _hostLimiter;
    private readonly object _runsSync = new();
    private readonly Dictionary<Guid, SegmentedRun> _segmentedRuns = new();
    private readonly bool _ownsClient;
    private readonly object _clientSync = new();
    private readonly List<HttpClient> _retiredClients = new();
    private readonly object _siteLoginSync = new();
    private List<SiteLoginCredential> _siteLogins = new();
    private readonly IMediaMuxer _muxer;
    private readonly IMediaCheckpointStore? _mediaCheckpoints;
    private string? _ffmpegPath;
    private HttpClient _client;

    public HttpDownloadEngine(
        EngineOptions? options = null,
        HttpMessageHandler? handler = null,
        IDownloadCheckpointStore? checkpoints = null,
        IMediaMuxer? muxer = null,
        IMediaCheckpointStore? mediaCheckpoints = null,
        IFreeSpaceProvider? freeSpace = null)
    {
        _options = options ?? new EngineOptions();
        _checkpoints = checkpoints;
        _mediaCheckpoints = mediaCheckpoints;
        _freeSpace = freeSpace ?? DriveFreeSpaceProvider.Instance;
        _hostLimiter = new FairHostConnectionLimiter(_options.MaxConnectionsPerHost);
        _hostLimiter.WaitingChanged += jobId => HostWaitChanged?.Invoke(jobId);
        _ffmpegPath = _options.FfmpegPath;
        _muxer = muxer ?? new FfmpegMediaMuxer(() => _ffmpegPath);
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
        var sockets = CreateProxiedHandler(_options.Proxy, _options.ConnectTimeout);
        sockets.AutomaticDecompression = DecompressionMethods.None;
        sockets.PooledConnectionLifetime = TimeSpan.FromMinutes(10);
        sockets.MaxConnectionsPerServer = 64;

        var client = new HttpClient(sockets, disposeHandler: true);
        ConfigureClient(client);
        return client;
    }

    /// <summary>
    /// A handler for other HTTP clients in the app, such as a provider's API client, so their
    /// traffic takes the same route as downloads. It reads the engine's live proxy settings,
    /// so a change applies to new connections.
    /// </summary>
    public SocketsHttpHandler CreateProxiedHandler() => CreateProxiedHandler(_options.Proxy, _options.ConnectTimeout);

    /// <summary>Follows no redirects and keeps no cookies; callers change what they need.</summary>
    public static SocketsHttpHandler CreateProxiedHandler(ProxySettings proxy, TimeSpan connectTimeout)
        => new()
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = connectTimeout,
            // Short, because an open connection keeps the proxy it was opened through.
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            Proxy = new ConfigurableProxy(proxy),
            UseProxy = true,
            UseCookies = false,
        };

    private static void ConfigureClient(HttpClient client)
    {
        client.Timeout = Timeout.InfiniteTimeSpan;
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DownloadManager/0.1");
    }

    public void UpdateSiteLogins(IReadOnlyList<SiteLoginCredential> logins)
    {
        lock (_siteLoginSync)
        {
            _siteLogins = logins.ToList();
        }
    }

    public void UpdateFfmpegPath(string? path)
    {
        _ffmpegPath = string.IsNullOrWhiteSpace(path) ? null : path.Trim();
    }

    public void UpdateHostConnectionLimit(int limit)
        => _hostLimiter.UpdateLimit(limit);

    /// <summary>All transfers draw on this one budget as well as their own, so N downloads share the cap.</summary>
    public void UpdateSharedSpeedLimit(long? bytesPerSecond) => SpeedBudget.Update(bytesPerSecond);

    public long SharedSpeedLimit => SpeedBudget.BytesPerSecond;

    /// <summary>The shared cap's budget. Give it to the FTP engine too, so the cap covers both (PERF.6).</summary>
    public SharedSpeedBudget SpeedBudget { get; } = new();

    private SharedSpeedBudget SharedRateLimiter => SpeedBudget;

    public int HostConnectionLimit => _hostLimiter.Limit;

    public int HostConnectionPeak => _hostLimiter.Peak;

    /// <summary>Connections out right now, across every host.</summary>
    public int HostConnectionsActive => _hostLimiter.Active;

    public event Action<Guid>? HostWaitChanged;

    public string? WaitingForHost(Guid jobId) => _hostLimiter.WaitingHost(jobId);

    public void ConnectionsChanged(Guid jobId)
    {
        SegmentedRun? run;
        lock (_runsSync)
        {
            _segmentedRuns.TryGetValue(jobId, out run);
        }

        run?.Wake();
    }

    private HttpClient CurrentClient()
    {
        lock (_clientSync)
        {
            return _client;
        }
    }

    /// <summary>The proxy settings changed. <see cref="CreateProxiedHandler()"/> handlers made before this keep their open connections.</summary>
    public event Action? ProxyChanged;

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
        ProxyChanged?.Invoke();

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
        if (!TryCreateUri(job.EffectiveUrl, out var uri, out var uriError))
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

        if (IsHlsPlaylist(job))
        {
            return await ExecuteWithMediaRefreshAsync(
                () => ExecuteHlsAsync(job, progress, cancellationToken)).ConfigureAwait(false);
        }

        if (IsDashManifest(job))
        {
            return await ExecuteWithMediaRefreshAsync(
                () => ExecuteDashAsync(job, progress, cancellationToken)).ConfigureAwait(false);
        }

        // All this says is that an earlier first request failed: no bytes rest on it, and it
        // must not stop the next run, perhaps with a fresh link, from using segments.
        if (checkpoint is { Segments.Count: <= 1, CompletedBytes: 0 } &&
            (!File.Exists(job.PartFilePath) || new FileInfo(job.PartFilePath).Length == 0))
        {
            checkpoint = null;
        }

        // Segmented progress sits at scattered offsets in a preallocated file, so only the
        // segmented path can continue it, even if the job is now limited to one connection.
        if (checkpoint is { Segments.Count: > 1 } ||
            (checkpoint is null && job.RequestedConnections > 1))
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
        var attempt = 0;
        var bestBytes = state.Bytes;

        reporter.Report(force: true, "Connecting");

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempt++;

            try
            {
                var result = await TransferOnceAsync(job, state, reporter, cancellationToken)
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
                    // Finalizing can fail on a locked file or an existing destination. The retry
                    // has to know these bytes are all there, or it throws away a whole good file.
                    await SaveCheckpointAsync(job, state, CancellationToken.None).ConfigureAwait(false);
                    return await FinalizeAsync(job, state, reporter, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await SaveStoppedCheckpointAsync(job, state).ConfigureAwait(false);
                return DownloadResult.Fail(DownloadState.Cancelled,
                    new JobFailure(FailureKind.Cancelled, "The download was cancelled.", false, true, DateTimeOffset.UtcNow),
                    state.Bytes, state.Total);
            }
            catch (DownloadEngineException ex)
            {
                if (ex.DiscardLocalData)
                {
                    await DiscardLocalDataAsync(job).ConfigureAwait(false);
                    return DownloadResult.Fail(DownloadState.Failed, ex.Failure);
                }

                await SaveStoppedCheckpointAsync(job, state).ConfigureAwait(false);

                if (!ex.IsRetryable || attempt >= _options.MaxAttempts)
                {
                    return DownloadResult.Fail(DownloadState.Failed, ex.Failure, state.Bytes, state.Total);
                }

                if (state.Bytes > bestBytes)
                {
                    bestBytes = state.Bytes;
                    attempt = 1;
                }

                var delay = RetryPolicy.Delay(attempt, _options, ex.RetryAfter);
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
                await SaveStoppedCheckpointAsync(job, state).ConfigureAwait(false);
                return DownloadResult.Fail(DownloadState.Failed, failure with { IsRetryable = retryable },
                    state.Bytes, state.Total);
            }
        }
    }

    private async Task<AttemptResult> TransferOnceAsync(
        DownloadJob job,
        TransferState state,
        TransferReporter reporter,
        CancellationToken cancellationToken)
    {
        reporter.Report(force: true, state.Bytes > 0 ? "Resuming" : "Connecting");

        // One stream can't hand its range to anyone, so it never yields; a fair share still gets it in first.
        using var hostSlot = await _hostLimiter.AcquireAsync(HostOf(job), job.Id, yieldable: false, cancellationToken)
            .ConfigureAwait(false);
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
            throw new DownloadEngineException(
                new JobFailure(FailureKind.Http, $"The server returned HTTP {status}.", true, true, DateTimeOffset.UtcNow, status),
                isRetryable: true,
                RetryAfterOf(response));
        }

        // Some servers say "nothing past the end" with a 206 and no range, rather than a 416.
        if (status == 416 || (status == 206 && response.Content.Headers.ContentRange is { HasRange: false, HasLength: true }))
        {
            // The bytes are all here only if the checkpoint recorded them and the server's
            // own size agrees; the part file's length alone proves nothing.
            var serverTotal = response.Content.Headers.ContentRange?.Length;
            if (state.Total is > 0 && state.Bytes == state.Total && state.RecordedBytes == state.Total &&
                (serverTotal is null || serverTotal == state.Total))
            {
                return new AttemptResult(Complete: true, RestartFromZero: false);
            }

            // A range from zero of an empty file has no byte to send, and the server says its total is 0.
            // A provider's file is always asked for that way, so restarting would only ask the same again.
            if (state.Bytes == 0 && serverTotal == 0 && state.Total is null or 0 && job.ExpectedTotalBytes is null or 0)
            {
                await using (new FileStream(state.PartPath, FileMode.Create, FileAccess.Write, FileShare.Read)) { }
                state.Total = 0;
                state.RecordedBytes = 0;
                return new AttemptResult(Complete: true, RestartFromZero: false);
            }

            if (state.Restarts++ < 2)
            {
                return new AttemptResult(Complete: false, RestartFromZero: true);
            }

            throw new DownloadEngineException(
                new JobFailure(FailureKind.RangeUnsupported,
                    "The server rejected the resume range and restarting did not help.",
                    false, false, DateTimeOffset.UtcNow, status),
                isRetryable: false);
        }

        if (status is < 200 or >= 300)
        {
            throw new DownloadEngineException(
                new JobFailure(FailureKind.Http, $"The server returned HTTP {status}.", false, false, DateTimeOffset.UtcNow, status),
                isRetryable: false);
        }

        // Before anything below can restart the transfer: an error page says nothing about
        // whether the file changed, so it must not cost the bytes already downloaded.
        if (LooksLikeErrorBody(job, response) is { } errorBody)
        {
            throw new DownloadEngineException(errorBody, isRetryable: false);
        }

        if (NotThePromisedFile(job, response, hasLocalData: state.Bytes > 0) is { } notThePromisedFile)
        {
            throw new DownloadEngineException(notThePromisedFile, isRetryable: false);
        }

        if (status == 200 && state.Bytes > 0)
        {
            job.ResumeSupported = false;
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

        if (job.ExpectedTotalBytes is > 0 && responseTotal is > 0 && responseTotal != job.ExpectedTotalBytes)
        {
            // A range from zero stands in for asking for the whole file, with nothing saved to judge
            // it against, so it gets the same answer as a whole file of the wrong size: a fresh link.
            if (status == 206 && state.Bytes == 0)
            {
                throw new DownloadEngineException(LinkAnsweredAnotherSize(job, responseTotal, status), isRetryable: false);
            }

            throw new DownloadEngineException(ExpectedSizeMismatch(job, responseTotal.Value), isRetryable: false, discardLocalData: true);
        }

        EnsureFreeSpace(job, responseTotal, state.Bytes);

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
        if (state.Bytes == 0) AdoptServerName(job, response);
        if (status == 206 || response.Headers.AcceptRanges.Contains("bytes")) job.ResumeSupported = true;
        else if (response.Headers.AcceptRanges.Contains("none")) job.ResumeSupported = false;
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
                catch (IOException ex) when (!cancellationToken.IsCancellationRequested)
                {
                    // This only reads the network, so it can't be a disk problem. The bytes so far
                    // are good, and the next attempt asks for the rest.
                    throw new DownloadEngineException(
                        new JobFailure(FailureKind.Network, "The connection was lost while reading.", true, true, DateTimeOffset.UtcNow),
                        isRetryable: true, inner: ex);
                }

                if (read == 0)
                {
                    break;
                }

                await state.RateLimiter.WaitAsync(read, cancellationToken).ConfigureAwait(false);
                await SharedRateLimiter.WaitAsync(read, cancellationToken).ConfigureAwait(false);
                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                state.Bytes += read;
                bytesThisResponse += read;

                reporter.Report(force: false, "Downloading");

                var now = DateTimeOffset.UtcNow;
                if (now - lastCheckpointAt >= _options.CheckpointInterval || Math.Abs(state.Bytes - checkpointBytes) >= CheckpointEveryBytes)
                {
                    await FlushToDiskAsync(file, cancellationToken).ConfigureAwait(false);
                    await SaveCheckpointAsync(job, state, cancellationToken).ConfigureAwait(false);
                    lastCheckpointAt = now;
                    checkpointBytes = state.Bytes;
                }
            }

            await FlushToDiskAsync(file, cancellationToken).ConfigureAwait(false);
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

    private const long CheckpointEveryBytes = 256 * 1024 * 1024;

    private static async Task FlushToDiskAsync(FileStream file, CancellationToken cancellationToken)
    {
        await file.FlushAsync(cancellationToken).ConfigureAwait(false);
        await Task.Run(() => file.Flush(flushToDisk: true), cancellationToken).ConfigureAwait(false);
    }

    private SiteLoginCredential? MatchSiteLogin(Uri uri)
    {
        List<SiteLoginCredential> snapshot;
        lock (_siteLoginSync)
        {
            snapshot = _siteLogins.ToList();
        }

        return snapshot
            .Where(login => SiteMatches(login.Site, uri))
            .OrderByDescending(login => login.Site.Length)
            .FirstOrDefault();
    }

    private static bool SiteMatches(string site, Uri uri)
    {
        if (string.IsNullOrWhiteSpace(site))
        {
            return false;
        }

        site = site.Trim();
        string host;
        int? port = null;
        string path;
        if (Uri.TryCreate(site, UriKind.Absolute, out var siteUri))
        {
            host = siteUri.Host;
            port = siteUri.IsDefaultPort ? null : siteUri.Port;
            path = siteUri.AbsolutePath;
        }
        else
        {
            var separator = site.IndexOf('/');
            var authority = separator < 0 ? site : site[..separator];
            path = separator < 0 ? "/" : site[separator..];
            if (!Uri.TryCreate($"http://{authority}", UriKind.Absolute, out var authorityUri))
            {
                return false;
            }

            host = authorityUri.Host;
            port = authority.Contains(':', StringComparison.Ordinal) ? authorityUri.Port : null;
        }

        if (!string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase) &&
            !uri.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (port is not null && uri.Port != port.Value)
        {
            return false;
        }

        if (path is "" or "/")
        {
            return true;
        }

        var normalizedPath = path.TrimEnd('/');
        return uri.AbsolutePath.StartsWith(normalizedPath, StringComparison.OrdinalIgnoreCase) &&
               (uri.AbsolutePath.Length == normalizedPath.Length ||
                uri.AbsolutePath[normalizedPath.Length] == '/');
    }

    private static async Task<DownloadResult> ExecuteWithMediaRefreshAsync(
        Func<Task<DownloadResult>> attempt)
    {
        var result = await attempt().ConfigureAwait(false);
        if (!result.Success && result.Failure?.Kind == FailureKind.ExpiredUrl)
        {
            // The playlist/manifest is re-fetched by the second attempt; the
            // path-based media signature keeps completed fragments resumable.
            result = await attempt().ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>A masked or provider reference in Url cannot fetch a playlist; the resolved address can.</summary>
    private static Uri MediaEntryUri(DownloadJob job)
        => new(string.IsNullOrWhiteSpace(job.ResolvedUrl) ? job.Url : job.ResolvedUrl, UriKind.Absolute);

    private static bool IsHlsPlaylist(DownloadJob job)
    {
        if (Uri.TryCreate(job.Url, UriKind.Absolute, out var uri) &&
            uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var hint = job.Description ?? string.Empty;
        return hint.Contains("mpegurl", StringComparison.OrdinalIgnoreCase) ||
               hint.Contains("m3u8", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<DownloadResult> ExecuteHlsAsync(
        DownloadJob job,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            var playlistUri = MediaEntryUri(job);
            var playlist = await FetchHlsTextAsync(job, playlistUri, cancellationToken).ConfigureAwait(false);
            if (playlist.Text.Contains("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase))
            {
                var selection = SelectHlsMaster(playlist.Text, playlist.FinalUri);
                if (selection is null)
                {
                    return DownloadResult.Fail(DownloadState.Failed,
                        new JobFailure(FailureKind.InvalidContent,
                            "The HLS master playlist has no usable variant.",
                            false, false, DateTimeOffset.UtcNow));
                }

                playlist = await FetchHlsTextAsync(job, selection.Variant, cancellationToken)
                    .ConfigureAwait(false);
                if (selection.Audio is not null)
                {
                    var audioPlaylist = await FetchHlsTextAsync(job, selection.Audio, cancellationToken)
                        .ConfigureAwait(false);
                    return await DownloadHlsMuxedAsync(
                        job,
                        playlist.Text,
                        playlist.FinalUri,
                        audioPlaylist.Text,
                        audioPlaylist.FinalUri,
                        progress,
                        cancellationToken).ConfigureAwait(false);
                }
            }

            return await DownloadHlsMediaAsync(job, playlist.Text, playlist.FinalUri, progress, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return DownloadResult.Fail(DownloadState.Cancelled,
                new JobFailure(FailureKind.Cancelled, "The HLS download was cancelled.", false, true, DateTimeOffset.UtcNow));
        }
        catch (DownloadEngineException ex)
        {
            return DownloadResult.Fail(DownloadState.Failed, ex.Failure);
        }
        catch (Exception ex)
        {
            return DownloadResult.Fail(DownloadState.Failed, MapException(ex, out _));
        }
    }

    private async Task<(string Text, Uri FinalUri)> FetchHlsTextAsync(
        DownloadJob job,
        Uri uri,
        CancellationToken cancellationToken)
    {
        for (var redirect = 0; redirect <= _options.MaxRedirects; redirect++)
        {
            using var request = BuildRequest(job, uri, 0, SameAuthority(job.Url, uri));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.ConnectTimeout);
            using var response = await SendTrackedAsync(job, request, timeout.Token)
                .ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status is 301 or 302 or 303 or 307 or 308)
            {
                var location = response.Headers.Location ??
                               throw new DownloadEngineException(new JobFailure(
                                   FailureKind.InvalidContent, "HLS redirect had no Location header.",
                                   false, false, DateTimeOffset.UtcNow), isRetryable: false);
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                continue;
            }

            if (status is 401 or 403 or 404 or 407 or 410)
            {
                throw new DownloadEngineException(ClassifyHttpFailure(status, response), isRetryable: false);
            }

            if (status != 200)
            {
                throw new DownloadEngineException(new JobFailure(
                    FailureKind.Http,
                    $"The HLS playlist returned HTTP {status}.",
                    status >= 500,
                    false,
                    DateTimeOffset.UtcNow), isRetryable: status >= 500);
            }

            var text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            if (text.Length > 4 * 1024 * 1024)
            {
                throw new DownloadEngineException(new JobFailure(
                    FailureKind.InvalidContent, "The HLS playlist is too large.",
                    false, false, DateTimeOffset.UtcNow), isRetryable: false);
            }

            return (text, uri);
        }

        throw new DownloadEngineException(new JobFailure(
            FailureKind.InvalidContent, "Too many HLS playlist redirects.",
            false, false, DateTimeOffset.UtcNow), isRetryable: false);
    }

    private static HlsMasterSelection? SelectHlsMaster(string playlist, Uri baseUri)
    {
        long bestBandwidth = -1;
        Uri? bestVariant = null;
        string? bestAudioGroup = null;
        var audioRenditions = new List<HlsAudioRendition>();
        var lines = playlist.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith("#EXT-X-MEDIA:", StringComparison.OrdinalIgnoreCase))
            {
                var type = ExtractAttribute(line, "TYPE");
                if (!string.Equals(type, "AUDIO", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var groupId = ExtractAttribute(line, "GROUP-ID") ?? string.Empty;
                var uriText = ExtractAttribute(line, "URI");
                Uri? uri = null;
                if (!string.IsNullOrWhiteSpace(uriText))
                {
                    Uri.TryCreate(baseUri, uriText, out uri);
                }

                audioRenditions.Add(new HlsAudioRendition(
                    groupId,
                    uri,
                    IsYes(ExtractAttribute(line, "DEFAULT")),
                    IsYes(ExtractAttribute(line, "AUTOSELECT"))));
                continue;
            }

            if (!line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var bandwidth = 0L;
            foreach (var attribute in line["#EXT-X-STREAM-INF:".Length..].Split(','))
            {
                var pair = attribute.Split('=', 2);
                if (pair.Length == 2 && pair[0].Trim().Equals("BANDWIDTH", StringComparison.OrdinalIgnoreCase))
                {
                    long.TryParse(pair[1].Trim(), out bandwidth);
                }
            }

            var audioGroup = ExtractAttribute(line, "AUDIO");
            for (var j = i + 1; j < lines.Length; j++)
            {
                var candidate = lines[j].Trim();
                if (candidate.Length == 0 || candidate.StartsWith('#'))
                {
                    continue;
                }

                if (Uri.TryCreate(baseUri, candidate, out var resolved) && bandwidth >= bestBandwidth)
                {
                    bestBandwidth = bandwidth;
                    bestVariant = resolved;
                    bestAudioGroup = audioGroup;
                }

                break;
            }
        }

        if (bestVariant is null)
        {
            return null;
        }

        Uri? audio = null;
        if (!string.IsNullOrWhiteSpace(bestAudioGroup))
        {
            audio = audioRenditions
                .Where(rendition =>
                    string.Equals(rendition.GroupId, bestAudioGroup, StringComparison.Ordinal) &&
                    rendition.Uri is not null)
                .OrderByDescending(rendition => rendition.IsDefault)
                .ThenByDescending(rendition => rendition.AutoSelect)
                .Select(rendition => rendition.Uri)
                .FirstOrDefault();
        }

        return new HlsMasterSelection(bestVariant, audio);
    }

    private static bool IsYes(string? value)
        => string.Equals(value, "YES", StringComparison.OrdinalIgnoreCase);

    private async Task<DownloadResult> DownloadHlsMediaAsync(
        DownloadJob job,
        string playlist,
        Uri playlistUri,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        var media = await PrepareHlsMediaAsync(job, playlist, playlistUri, cancellationToken)
            .ConfigureAwait(false);
        var isFragmentedMp4 = media.InitUri is not null ||
                              media.Segments.Any(segment =>
                                  segment.Uri.AbsolutePath.EndsWith(".m4s", StringComparison.OrdinalIgnoreCase));
        return await AssembleMediaSegmentsAsync(
            job,
            media.InitUri is null ? null : new MediaSegment(media.InitUri, null, null),
            media.Segments,
            isFragmentedMp4 ? ".mp4" : ".ts",
            isFragmentedMp4 ? "video/mp4" : "video/mp2t",
            "Downloading HLS",
            "hls",
            ComputeMediaSignature("hls", media.InitUri, media.Segments),
            progress,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<HlsMediaPlaylist> PrepareHlsMediaAsync(
        DownloadJob job,
        string playlist,
        Uri playlistUri,
        CancellationToken cancellationToken)
    {
        Uri? initUri = null;
        var parsedSegments = new List<ParsedHlsSegment>();
        var hasEndList = false;
        long mediaSequence = 0;
        Uri? currentKeyUri = null;
        byte[]? currentIv = null;
        foreach (var rawLine in playlist.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("#EXT-X-TARGETDURATION", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("#EXT-X-VERSION", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("#EXT-X-PLAYLIST-TYPE", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("#EXT-X-DISCONTINUITY", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE", StringComparison.OrdinalIgnoreCase))
            {
                var value = line[(line.IndexOf(':') + 1)..].Trim();
                long.TryParse(value, out mediaSequence);
                continue;
            }

            if (line.StartsWith("#EXT-X-SESSION-KEY:", StringComparison.OrdinalIgnoreCase))
            {
                throw new DownloadEngineException(new JobFailure(
                    FailureKind.DrmProtected,
                    "This HLS stream is DRM-protected (session key). Protected content cannot be downloaded or decrypted.",
                    false, false, DateTimeOffset.UtcNow), isRetryable: false);
            }

            if (line.StartsWith("#EXT-X-KEY:", StringComparison.OrdinalIgnoreCase))
            {
                var method = (ExtractAttribute(line, "METHOD") ?? string.Empty).Trim();
                var keyFormat = (ExtractAttribute(line, "KEYFORMAT") ?? string.Empty).Trim();
                var keyUriText = ExtractAttribute(line, "URI") ?? string.Empty;
                var isDrm = method.Contains("SAMPLE-AES", StringComparison.OrdinalIgnoreCase) ||
                            keyUriText.StartsWith("skd://", StringComparison.OrdinalIgnoreCase) ||
                            keyFormat.Contains("apple", StringComparison.OrdinalIgnoreCase) ||
                            keyFormat.Contains("widevine", StringComparison.OrdinalIgnoreCase) ||
                            keyFormat.Contains("playready", StringComparison.OrdinalIgnoreCase);
                if (isDrm)
                {
                    throw new DownloadEngineException(new JobFailure(
                        FailureKind.DrmProtected,
                        "This HLS stream is DRM-protected (SAMPLE-AES/session key). Protected content cannot be downloaded or decrypted.",
                        false, false, DateTimeOffset.UtcNow), isRetryable: false);
                }

                if (method.Equals("NONE", StringComparison.OrdinalIgnoreCase))
                {
                    currentKeyUri = null;
                    currentIv = null;
                    continue;
                }

                if (!method.Equals("AES-128", StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrEmpty(keyFormat) &&
                     !keyFormat.Equals("identity", StringComparison.OrdinalIgnoreCase)))
                {
                    throw new DownloadEngineException(new JobFailure(
                        FailureKind.InvalidContent,
                        $"Unsupported HLS encryption method '{method}' or key format '{keyFormat}'.",
                        false, false, DateTimeOffset.UtcNow), isRetryable: false);
                }

                if (string.IsNullOrWhiteSpace(keyUriText) ||
                    !Uri.TryCreate(playlistUri, keyUriText, out var keyUri))
                {
                    throw new DownloadEngineException(new JobFailure(
                        FailureKind.InvalidContent,
                        "The HLS AES-128 key URI is missing or invalid.",
                        false, false, DateTimeOffset.UtcNow), isRetryable: false);
                }

                currentKeyUri = keyUri;
                currentIv = ParseHlsIv(ExtractAttribute(line, "IV"));
                continue;
            }

            if (line.StartsWith("#EXT-X-BYTERANGE", StringComparison.OrdinalIgnoreCase))
            {
                throw new DownloadEngineException(new JobFailure(
                    FailureKind.InvalidContent,
                    "HLS byte-range segments are not supported yet.",
                    false, false, DateTimeOffset.UtcNow), isRetryable: false);
            }

            if (line.StartsWith("#EXT-X-MAP:", StringComparison.OrdinalIgnoreCase))
            {
                var uriValue = ExtractAttribute(line, "URI");
                if (uriValue is not null && Uri.TryCreate(playlistUri, uriValue, out var mapUri))
                {
                    initUri = mapUri;
                }

                continue;
            }

            if (line.Equals("#EXT-X-ENDLIST", StringComparison.OrdinalIgnoreCase))
            {
                hasEndList = true;
                continue;
            }

            if (line.StartsWith('#'))
            {
                continue;
            }

            if (Uri.TryCreate(playlistUri, line, out var segmentUri))
            {
                var sequence = mediaSequence + parsedSegments.Count;
                var iv = currentIv ?? SequenceIv(sequence);
                parsedSegments.Add(new ParsedHlsSegment(segmentUri, currentKeyUri, iv));
            }
        }

        if (parsedSegments.Count == 0)
        {
            throw new DownloadEngineException(new JobFailure(
                FailureKind.InvalidContent, "The HLS media playlist has no segments.",
                false, false, DateTimeOffset.UtcNow), isRetryable: false);
        }

        if (!hasEndList)
        {
            throw new DownloadEngineException(new JobFailure(
                FailureKind.LiveStream,
                "Live HLS playlists are not supported. A VOD playlist with #EXT-X-ENDLIST is required.",
                false, false, DateTimeOffset.UtcNow), isRetryable: false);
        }

        var keyCache = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var segments = new List<MediaSegment>(parsedSegments.Count);
        foreach (var parsed in parsedSegments)
        {
            byte[]? key = null;
            if (parsed.KeyUri is not null)
            {
                var keyId = parsed.KeyUri.GetLeftPart(UriPartial.Path);
                if (!keyCache.TryGetValue(keyId, out key))
                {
                    key = await DownloadHlsKeyAsync(job, parsed.KeyUri, cancellationToken)
                        .ConfigureAwait(false);
                    keyCache[keyId] = key;
                }
            }

            segments.Add(new MediaSegment(
                parsed.Uri,
                key,
                parsed.KeyUri is null ? null : parsed.Iv));
        }

        return new HlsMediaPlaylist(initUri, segments);
    }

    private async Task<DownloadResult> DownloadHlsMuxedAsync(
        DownloadJob job,
        string videoPlaylist,
        Uri videoUri,
        string audioPlaylist,
        Uri audioUri,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        var video = await PrepareHlsMediaAsync(job, videoPlaylist, videoUri, cancellationToken)
            .ConfigureAwait(false);
        var audio = await PrepareHlsMediaAsync(job, audioPlaylist, audioUri, cancellationToken)
            .ConfigureAwait(false);
        var videoSignature = ComputeMediaSignature("hls-video", video.InitUri, video.Segments);
        var audioSignature = ComputeMediaSignature("hls-audio", audio.InitUri, audio.Segments);

        var baseName = Path.GetFileNameWithoutExtension(job.FileName);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = "stream";
        }

        var directory = string.IsNullOrWhiteSpace(job.DestinationDirectory)
            ? Path.GetDirectoryName(job.FinalFilePath) ?? AppSettings.GetDefaultDownloadDirectory()
            : job.DestinationDirectory;
        job.FinalFilePath = FileNameResolver.ResolveUniquePath(
            directory, baseName + ".mp4", job.ConflictPolicy);
        job.FileName = Path.GetFileName(job.FinalFilePath);
        var videoPath = job.PartFilePath + ".video";
        var audioPath = job.PartFilePath + ".audio";
        var muxPath = job.PartFilePath + ".mux.mp4";
        var received = 0L;
        try
        {
            received += await DownloadMediaTrackToFileAsync(
                job,
                video.InitUri is null ? null : new MediaSegment(video.InitUri, null, null),
                video.Segments,
                videoSignature,
                videoPath,
                received,
                "Downloading HLS video",
                "hls-video",
                progress,
                cancellationToken).ConfigureAwait(false);
            received += await DownloadMediaTrackToFileAsync(
                job,
                audio.InitUri is null ? null : new MediaSegment(audio.InitUri, null, null),
                audio.Segments,
                audioSignature,
                audioPath,
                received,
                "Downloading HLS audio",
                "hls-audio",
                progress,
                cancellationToken).ConfigureAwait(false);

            progress?.Report(new TransferProgress(received, null, 0, null, 0, "Muxing audio/video"));
            var mux = await _muxer.MuxAsync(videoPath, audioPath, muxPath, cancellationToken)
                .ConfigureAwait(false);
            if (!mux.Success)
            {
                return DownloadResult.Fail(DownloadState.Failed, new JobFailure(
                    FailureKind.MuxingFailed,
                    mux.Error ?? "Audio/video muxing failed.",
                    false, true, DateTimeOffset.UtcNow), received, received);
            }

            if (!string.IsNullOrWhiteSpace(job.ExpectedSha256))
            {
                var actual = await Hashing.Sha256FileAsync(muxPath, cancellationToken)
                    .ConfigureAwait(false);
                if (!actual.Equals(job.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    return DownloadResult.Fail(DownloadState.Failed, new JobFailure(
                        FailureKind.VerificationFailed,
                        $"The muxed output SHA-256 did not match (expected {job.ExpectedSha256}, got {actual}).",
                        false, true, DateTimeOffset.UtcNow), received, received);
                }
            }

            File.Move(muxPath, job.FinalFilePath,
                overwrite: job.ConflictPolicy is ConflictPolicy.Overwrite);
            if (_mediaCheckpoints is not null)
            {
                await _mediaCheckpoints.DeleteAsync(job.Id, "hls-video", cancellationToken)
                    .ConfigureAwait(false);
                await _mediaCheckpoints.DeleteAsync(job.Id, "hls-audio", cancellationToken)
                    .ConfigureAwait(false);
            }

            var finalBytes = new FileInfo(job.FinalFilePath).Length;
            progress?.Report(new TransferProgress(finalBytes, finalBytes, 0, TimeSpan.Zero, 0, "Completed"));
            return DownloadResult.Ok(finalBytes, finalBytes, job.FinalFilePath, null, null, "video/mp4");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return DownloadResult.Fail(DownloadState.Cancelled, new JobFailure(
                FailureKind.Cancelled, "The HLS download was cancelled.", false, true,
                DateTimeOffset.UtcNow), received, received);
        }
        catch (DownloadEngineException ex)
        {
            return DownloadResult.Fail(DownloadState.Failed, ex.Failure, received, received);
        }
        catch (Exception ex)
        {
            return DownloadResult.Fail(DownloadState.Failed, MapException(ex, out _), received, received);
        }
        finally
        {
            DeleteFileQuietly(videoPath);
            DeleteFileQuietly(audioPath);
            DeleteFileQuietly(muxPath);
        }
    }

    private async Task<DownloadResult> AssembleMediaSegmentsAsync(
        DownloadJob job,
        MediaSegment? initSegment,
        IReadOnlyList<MediaSegment> segments,
        string extension,
        string mimeType,
        string phase,
        string checkpointTrack,
        string checkpointSignature,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        var baseName = Path.GetFileNameWithoutExtension(job.FileName);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = "stream";
        }

        var directory = string.IsNullOrWhiteSpace(job.DestinationDirectory)
            ? Path.GetDirectoryName(job.FinalFilePath) ?? AppSettings.GetDefaultDownloadDirectory()
            : job.DestinationDirectory;
        job.FinalFilePath = FileNameResolver.ResolveUniquePath(
            directory, baseName + extension, job.ConflictPolicy);
        job.FileName = Path.GetFileName(job.FinalFilePath);

        var completedSegments = 0;
        var received = 0L;
        if (_mediaCheckpoints is not null)
        {
            var existing = await _mediaCheckpoints.LoadAsync(job.Id, checkpointTrack, cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                var partLength = File.Exists(job.PartFilePath)
                    ? new FileInfo(job.PartFilePath).Length
                    : -1;
                if (string.Equals(existing.Signature, checkpointSignature, StringComparison.Ordinal) &&
                    partLength >= existing.CompletedBytes &&
                    existing.CompletedBytes >= 0)
                {
                    completedSegments = Math.Min(existing.CompletedSegments, segments.Count);
                    received = existing.CompletedBytes;
                }
                else
                {
                    await _mediaCheckpoints.DeleteAsync(job.Id, checkpointTrack, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }

        var stopwatch = Stopwatch.StartNew();
        var progressState = new HlsProgressState();
        try
        {
            await using (var part = new FileStream(
                             job.PartFilePath,
                             received > 0 ? FileMode.Open : FileMode.Create,
                             FileAccess.Write,
                             FileShare.Read,
                             _options.BufferSize,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                if (received > 0)
                {
                    part.SetLength(received);
                    part.Position = received;
                }

                if (initSegment is not null && completedSegments == 0)
                {
                    received += await DownloadHlsSegmentAsync(
                        job, initSegment, part, received, progress,
                        stopwatch, progressState, phase, cancellationToken)
                        .ConfigureAwait(false);
                }

                for (var i = completedSegments; i < segments.Count; i++)
                {
                    received += await DownloadHlsSegmentAsync(
                        job, segments[i], part, received, progress, stopwatch, progressState, phase, cancellationToken)
                        .ConfigureAwait(false);
                    await part.FlushAsync(cancellationToken).ConfigureAwait(false);
                    if (_mediaCheckpoints is not null)
                    {
                        await _mediaCheckpoints.SaveAsync(new MediaCheckpoint
                        {
                            JobId = job.Id,
                            Track = checkpointTrack,
                            Signature = checkpointSignature,
                            CompletedSegments = i + 1,
                            CompletedBytes = received,
                        }, cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(job.ExpectedSha256))
            {
                var actual = await Hashing.Sha256FileAsync(job.PartFilePath, cancellationToken).ConfigureAwait(false);
                if (!actual.Equals(job.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    return DownloadResult.Fail(DownloadState.Failed, new JobFailure(
                        FailureKind.VerificationFailed,
                        $"The media output SHA-256 did not match (expected {job.ExpectedSha256}, got {actual}).",
                        false, true, DateTimeOffset.UtcNow), received, received);
                }
            }

            File.Move(job.PartFilePath, job.FinalFilePath,
                overwrite: job.ConflictPolicy is ConflictPolicy.Overwrite);
            if (_mediaCheckpoints is not null)
            {
                await _mediaCheckpoints.DeleteAsync(job.Id, checkpointTrack, cancellationToken)
                    .ConfigureAwait(false);
            }

            progress?.Report(new TransferProgress(
                received, received, received / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds),
                TimeSpan.Zero, 1, "Completed"));
            return DownloadResult.Ok(received, received, job.FinalFilePath, null, null, mimeType);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return DownloadResult.Fail(DownloadState.Cancelled, new JobFailure(
                FailureKind.Cancelled, "The media download was cancelled.", false, true, DateTimeOffset.UtcNow),
                received, received);
        }
        catch (DownloadEngineException ex)
        {
            return DownloadResult.Fail(DownloadState.Failed, ex.Failure, received, received);
        }
        catch (Exception ex)
        {
            return DownloadResult.Fail(DownloadState.Failed, MapException(ex, out _), received, received);
        }
    }

    private async Task<long> DownloadHlsSegmentAsync(
        DownloadJob job,
        MediaSegment segment,
        Stream output,
        long alreadyReceived,
        IProgress<TransferProgress>? progress,
        Stopwatch stopwatch,
        HlsProgressState progressState,
        string phase,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var segmentUri = segment.Uri;
                var rangeStart = segment.RangeStart ?? 0;
                var rangeEnd = segment.RangeEnd;
                var expectedRangeLength = rangeEnd is null
                    ? (long?)null
                    : rangeEnd.Value - rangeStart + 1;
                using var request = BuildRequest(
                    job, segmentUri, rangeStart, SameAuthority(job.Url, segmentUri), rangeEnd,
                    includeValidators: false);
                // A media job fetches one fragment at a time, so this is one connection per job under the
                // host's cap. Taken before the timeout starts: waiting for a turn is not a slow server.
                using var hostSlot = await _hostLimiter.AcquireAsync(segmentUri.Host, job.Id, yieldable: false, cancellationToken)
                    .ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(_options.ReadTimeout);
                using var response = await SendTrackedAsync(job, request, timeout.Token)
                    .ConfigureAwait(false);
                var status = (int)response.StatusCode;
                if (status is 401 or 403 or 404 or 407 or 410)
                {
                    throw new DownloadEngineException(ClassifyHttpFailure(status, response), isRetryable: false);
                }

                if (status is < 200 or >= 300)
                {
                    if (status >= 500 && attempt < _options.MaxAttempts)
                    {
                        // Not this job's connection while it only waits.
                        hostSlot.Dispose();
                        await Task.Delay(RetryPolicy.Delay(attempt, _options), cancellationToken)
                            .ConfigureAwait(false);
                        continue;
                    }

                    throw new DownloadEngineException(new JobFailure(
                        FailureKind.Http, $"HLS segment returned HTTP {status}.",
                        status >= 500, false, DateTimeOffset.UtcNow), isRetryable: status >= 500);
                }

                var buffer = new byte[_options.BufferSize];
                var segmentBytes = 0L;
                await using var segmentStream = await response.Content.ReadAsStreamAsync(timeout.Token)
                    .ConfigureAwait(false);
                if (status == 200 && rangeStart > 0)
                {
                    var remaining = rangeStart;
                    while (remaining > 0)
                    {
                        var read = await segmentStream.ReadAsync(
                            buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), timeout.Token)
                            .ConfigureAwait(false);
                        if (read == 0)
                        {
                            throw new DownloadEngineException(new JobFailure(
                                FailureKind.InvalidContent,
                                "The media server ignored the byte range and ended early.",
                                false, false, DateTimeOffset.UtcNow), isRetryable: false);
                        }

                        remaining -= read;
                    }
                }

                if (segment.Key is null)
                {
                    while (true)
                    {
                        var read = await segmentStream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                        if (read == 0)
                        {
                            break;
                        }

                        if (expectedRangeLength is { } wanted)
                        {
                            var allowed = (int)Math.Min(read, wanted - segmentBytes);
                            if (allowed <= 0)
                            {
                                break;
                            }

                            read = allowed;
                        }

                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                            .ConfigureAwait(false);
                        segmentBytes += read;
                        var now = DateTimeOffset.UtcNow;
                        if (now - progressState.LastReport >= TimeSpan.FromMilliseconds(300))
                        {
                            progressState.LastReport = now;
                            var bytes = alreadyReceived + segmentBytes;
                            var speed = bytes / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds);
                            progress?.Report(new TransferProgress(
                                bytes, null, speed, null, 1, phase));
                        }
                    }
                }
                else
                {
                    using var encrypted = new MemoryStream();
                    while (true)
                    {
                        var read = await segmentStream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                        if (read == 0)
                        {
                            break;
                        }

                        if (expectedRangeLength is { } wanted)
                        {
                            var allowed = (int)Math.Min(read, wanted - encrypted.Length);
                            if (allowed <= 0)
                            {
                                break;
                            }

                            read = allowed;
                        }

                        encrypted.Write(buffer, 0, read);

                        // The segment is decrypted only once it is whole, so report while it arrives too: a long
                        // segment would otherwise go quiet for its whole download. The count is what is surely
                        // plaintext (padding is at most one block), so it never runs ahead of the final figure.
                        var now = DateTimeOffset.UtcNow;
                        if (now - progressState.LastReport >= TimeSpan.FromMilliseconds(300))
                        {
                            progressState.LastReport = now;
                            var arrived = alreadyReceived + Math.Max(0, encrypted.Length - 16);
                            var arrivalSpeed = arrived / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds);
                            progress?.Report(new TransferProgress(arrived, null, arrivalSpeed, null, 1, phase));
                        }
                    }

                    var plain = DecryptHlsSegment(
                        encrypted.ToArray(), segment.Key, segment.Iv ?? new byte[16]);
                    await output.WriteAsync(plain, cancellationToken).ConfigureAwait(false);
                    segmentBytes = plain.LongLength;
                    progressState.LastReport = DateTimeOffset.UtcNow;
                    var bytes = alreadyReceived + segmentBytes;
                    var speed = bytes / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds);
                    progress?.Report(new TransferProgress(bytes, null, speed, null, 1, phase));
                }

                if (segment.Key is null &&
                    expectedRangeLength is { } wantedTotal &&
                    segmentBytes != wantedTotal)
                {
                    throw new DownloadEngineException(new JobFailure(
                        FailureKind.InvalidContent,
                        $"The media byte range returned {segmentBytes} of {wantedTotal} bytes.",
                        false, false, DateTimeOffset.UtcNow), isRetryable: false);
                }

                return segmentBytes;
            }
            catch (Exception ex) when (
                attempt < _options.MaxAttempts &&
                (ex is IOException or HttpRequestException or TaskCanceledException) &&
                !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(RetryPolicy.Delay(attempt, _options), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<byte[]> DownloadHlsKeyAsync(
        DownloadJob job,
        Uri keyUri,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var request = BuildRequest(job, keyUri, 0, SameAuthority(job.Url, keyUri));
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(_options.ReadTimeout);
                using var response = await SendTrackedAsync(job, request, timeout.Token)
                    .ConfigureAwait(false);
                var status = (int)response.StatusCode;
                if (status is 401 or 403 or 404 or 407 or 410)
                {
                    throw new DownloadEngineException(
                        ClassifyHttpFailure(status, response), isRetryable: false);
                }

                if (status is < 200 or >= 300)
                {
                    if (status >= 500 && attempt < _options.MaxAttempts)
                    {
                        await Task.Delay(RetryPolicy.Delay(attempt, _options), cancellationToken)
                            .ConfigureAwait(false);
                        continue;
                    }

                    throw new DownloadEngineException(new JobFailure(
                        FailureKind.Http, $"HLS key returned HTTP {status}.",
                        status >= 500, false, DateTimeOffset.UtcNow), isRetryable: status >= 500);
                }

                var key = await response.Content.ReadAsByteArrayAsync(timeout.Token)
                    .ConfigureAwait(false);
                if (key.Length != 16)
                {
                    throw new DownloadEngineException(new JobFailure(
                        FailureKind.InvalidContent,
                        $"The HLS AES-128 key must be 16 bytes (got {key.Length}).",
                        false, false, DateTimeOffset.UtcNow), isRetryable: false);
                }

                return key;
            }
            catch (Exception ex) when (
                attempt < _options.MaxAttempts &&
                (ex is IOException or HttpRequestException or TaskCanceledException) &&
                !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(RetryPolicy.Delay(attempt, _options), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private static byte[] DecryptHlsSegment(byte[] ciphertext, byte[] key, byte[] iv)
    {
        if (key.Length != 16 || iv.Length != 16)
        {
            throw new DownloadEngineException(new JobFailure(
                FailureKind.InvalidContent,
                "The HLS AES-128 key or IV has an invalid length.",
                false, false, DateTimeOffset.UtcNow), isRetryable: false);
        }

        try
        {
            using var aes = Aes.Create();
            aes.KeySize = 128;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.Key = key;
            aes.IV = iv;
            using var decryptor = aes.CreateDecryptor();
            return decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
        }
        catch (CryptographicException ex)
        {
            throw new DownloadEngineException(new JobFailure(
                FailureKind.InvalidContent,
                $"HLS AES-128 decryption failed: {ex.Message}",
                false, false, DateTimeOffset.UtcNow), isRetryable: false);
        }
    }

    private static byte[]? ParseHlsIv(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..];
        }

        if (text.Length == 0 || text.Length > 32)
        {
            throw new DownloadEngineException(new JobFailure(
                FailureKind.InvalidContent, "The HLS AES-128 IV is invalid.",
                false, false, DateTimeOffset.UtcNow), isRetryable: false);
        }

        if (text.Length % 2 != 0)
        {
            text = "0" + text;
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromHexString(text);
        }
        catch (FormatException)
        {
            throw new DownloadEngineException(new JobFailure(
                FailureKind.InvalidContent, "The HLS AES-128 IV is not hexadecimal.",
                false, false, DateTimeOffset.UtcNow), isRetryable: false);
        }

        if (bytes.Length == 16)
        {
            return bytes;
        }

        var padded = new byte[16];
        Buffer.BlockCopy(bytes, 0, padded, 16 - bytes.Length, bytes.Length);
        return padded;
    }

    private static byte[] SequenceIv(long sequence)
    {
        var iv = new byte[16];
        for (var i = 15; i >= 0 && sequence > 0; i--)
        {
            iv[i] = (byte)(sequence & 0xFF);
            sequence >>= 8;
        }

        return iv;
    }

    private static string? ExtractAttribute(string line, string name)
    {
        var marker = name + "=";
        var index = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        var value = line[(index + marker.Length)..].Trim();
        if (value.StartsWith('"'))
        {
            var end = value.IndexOf('"', 1);
            return end > 0 ? value[1..end] : null;
        }

        var comma = value.IndexOf(',');
        return comma >= 0 ? value[..comma] : value;
    }

    private static bool SameAuthority(string original, Uri current)
        => Uri.TryCreate(original, UriKind.Absolute, out var originalUri) &&
           string.Equals(
               originalUri.GetLeftPart(UriPartial.Authority),
               current.GetLeftPart(UriPartial.Authority),
               StringComparison.OrdinalIgnoreCase);

    private static bool IsDashManifest(DownloadJob job)
    {
        if (Uri.TryCreate(job.Url, UriKind.Absolute, out var uri) &&
            uri.AbsolutePath.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var hint = job.Description ?? string.Empty;
        return hint.Contains("dash+xml", StringComparison.OrdinalIgnoreCase) ||
               hint.Contains(".mpd", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<DownloadResult> ExecuteDashAsync(
        DownloadJob job,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            var manifestUri = MediaEntryUri(job);
            var manifest = await FetchHlsTextAsync(job, manifestUri, cancellationToken).ConfigureAwait(false);
            var document = XDocument.Parse(manifest.Text);
            var root = document.Root ?? throw new InvalidDataException("The DASH manifest is empty.");
            var ns = root.Name.Namespace;

            if (string.Equals((string?)root.Attribute("type"), "dynamic", StringComparison.OrdinalIgnoreCase))
            {
                return DownloadResult.Fail(DownloadState.Failed, new JobFailure(
                    FailureKind.LiveStream,
                    "Live DASH manifests are not supported. A static MPD is required.",
                    false, false, DateTimeOffset.UtcNow));
            }

            var protections = root.Descendants(ns + "ContentProtection").ToList();
            if (protections.Count > 0)
            {
                var schemes = string.Join(", ", protections
                    .Select(p => (string?)p.Attribute("schemeIdUri"))
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Distinct(StringComparer.OrdinalIgnoreCase));
                return DownloadResult.Fail(DownloadState.Failed, new JobFailure(
                    FailureKind.DrmProtected,
                    string.IsNullOrWhiteSpace(schemes)
                        ? "This DASH stream is DRM-protected. Protected content cannot be downloaded or decrypted."
                        : $"This DASH stream is DRM-protected ({schemes}). Protected content cannot be downloaded or decrypted.",
                    false, false, DateTimeOffset.UtcNow));
            }

            var period = root.Elements(ns + "Period").FirstOrDefault();
            if (period is null)
            {
                return DownloadResult.Fail(DownloadState.Failed, new JobFailure(
                    FailureKind.InvalidContent, "The DASH manifest has no Period.",
                    false, false, DateTimeOffset.UtcNow));
            }

            var adaptationSets = period.Elements(ns + "AdaptationSet").ToList();
            var videoAdaptation = adaptationSets.FirstOrDefault(a =>
                (string?)a.Attribute("contentType") == "video" ||
                ((string?)a.Attribute("mimeType"))?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true);
            var audioAdaptation = adaptationSets.FirstOrDefault(a =>
                (string?)a.Attribute("contentType") == "audio" ||
                ((string?)a.Attribute("mimeType"))?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true);
            if (videoAdaptation is null && audioAdaptation is null)
            {
                return DownloadResult.Fail(DownloadState.Failed, new JobFailure(
                    FailureKind.InvalidContent, "The DASH manifest has no usable AdaptationSet.",
                    false, false, DateTimeOffset.UtcNow));
            }

            DashTrack? videoTrack = null;
            DashTrack? audioTrack = null;
            if (videoAdaptation is not null)
            {
                var representation = SelectDashRepresentation(videoAdaptation, ns);
                if (representation is null)
                {
                    return DownloadResult.Fail(DownloadState.Failed, new JobFailure(
                        FailureKind.InvalidContent, "The DASH video AdaptationSet has no Representation.",
                        false, false, DateTimeOffset.UtcNow));
                }

                var videoResult = await BuildDashTrackAsync(
                    job, root, period, videoAdaptation, representation, ns, manifest.FinalUri,
                    cancellationToken).ConfigureAwait(false);
                videoTrack = videoResult.Track;
                if (videoTrack is null)
                {
                    return DownloadResult.Fail(DownloadState.Failed, new JobFailure(
                        FailureKind.InvalidContent,
                        videoResult.Error ?? "The DASH video track is invalid.",
                        false, false, DateTimeOffset.UtcNow));
                }
            }

            if (audioAdaptation is not null)
            {
                var representation = SelectDashRepresentation(audioAdaptation, ns);
                if (representation is not null)
                {
                    var audioResult = await BuildDashTrackAsync(
                        job, root, period, audioAdaptation, representation, ns, manifest.FinalUri,
                        cancellationToken).ConfigureAwait(false);
                    audioTrack = audioResult.Track;
                    if (audioTrack is null && videoTrack is null)
                    {
                        return DownloadResult.Fail(DownloadState.Failed, new JobFailure(
                            FailureKind.InvalidContent,
                            audioResult.Error ?? "The DASH audio track is invalid.",
                            false, false, DateTimeOffset.UtcNow));
                    }
                }
            }

            if (videoTrack is not null && audioTrack is not null)
            {
                return await DownloadAndMuxDashTracksAsync(
                    job, videoTrack, audioTrack, progress, cancellationToken).ConfigureAwait(false);
            }

            var singleTrack = videoTrack ?? audioTrack!;
            var singleTrackName = videoTrack is not null ? "dash-video" : "dash-audio";
            return await AssembleMediaSegmentsAsync(
                job,
                singleTrack.Init,
                singleTrack.Segments,
                ".mp4",
                videoTrack is not null ? "video/mp4" : "audio/mp4",
                "Downloading DASH",
                singleTrackName,
                singleTrack.Signature,
                progress, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return DownloadResult.Fail(DownloadState.Cancelled, new JobFailure(
                FailureKind.Cancelled, "The DASH download was cancelled.", false, true, DateTimeOffset.UtcNow));
        }
        catch (DownloadEngineException ex)
        {
            return DownloadResult.Fail(DownloadState.Failed, ex.Failure);
        }
        catch (System.Xml.XmlException ex)
        {
            return DownloadResult.Fail(DownloadState.Failed, new JobFailure(
                FailureKind.InvalidContent, $"The DASH manifest is invalid: {ex.Message}",
                false, false, DateTimeOffset.UtcNow));
        }
        catch (Exception ex)
        {
            return DownloadResult.Fail(DownloadState.Failed, MapException(ex, out _));
        }
    }

    private static XElement? SelectDashRepresentation(XElement adaptation, XNamespace ns)
        => adaptation.Elements(ns + "Representation")
            .OrderByDescending(r => ParseLong((string?)r.Attribute("bandwidth")))
            .FirstOrDefault();

    private async Task<(DashTrack? Track, string? Error)> BuildDashTrackAsync(
        DownloadJob job,
        XElement root,
        XElement period,
        XElement adaptation,
        XElement representation,
        XNamespace ns,
        Uri manifestUri,
        CancellationToken cancellationToken)
    {
        var baseUri = ResolveDashBase(manifestUri, root, ns);
        baseUri = ResolveDashBase(baseUri, period, ns);
        baseUri = ResolveDashBase(baseUri, adaptation, ns);
        baseUri = ResolveDashBase(baseUri, representation, ns);

        var segmentBase = representation.Elements(ns + "SegmentBase").FirstOrDefault()
                          ?? adaptation.Elements(ns + "SegmentBase").FirstOrDefault()
                          ?? period.Elements(ns + "SegmentBase").FirstOrDefault();
        if (segmentBase is not null)
        {
            var baseUrlElement = representation.Element(ns + "BaseURL")
                                 ?? adaptation.Element(ns + "BaseURL")
                                 ?? period.Element(ns + "BaseURL")
                                 ?? root.Element(ns + "BaseURL");
            if (baseUrlElement is null)
            {
                return (null, "This DASH SegmentBase has no BaseURL.");
            }

            var initElement = segmentBase.Element(ns + "Initialization");
            MediaSegment? baseInit = null;
            if (initElement is not null)
            {
                var initSource = (string?)initElement.Attribute("sourceURL");
                var initUri = string.IsNullOrWhiteSpace(initSource)
                    ? baseUri
                    : ResolveDashUri(baseUri, initSource);
                if (initUri is null)
                {
                    return (null, "The DASH SegmentBase initialization URL is invalid.");
                }

                var initRange = ParseDashRange((string?)initElement.Attribute("range"));
                baseInit = new MediaSegment(initUri, null, null, initRange?.Start, initRange?.End);
            }

            var indexText = (string?)segmentBase.Attribute("indexRange");
            if (string.IsNullOrWhiteSpace(indexText))
            {
                var whole = new List<MediaSegment> { new(baseUri, null, null) };
                return (new DashTrack(baseInit, whole,
                    ComputeMediaSignature("dash", baseInit, whole)), null);
            }

            var indexRange = ParseDashRange(indexText);
            if (indexRange is null)
            {
                return (null, "The DASH SegmentBase indexRange is invalid.");
            }

            byte[] indexBytes;
            try
            {
                indexBytes = await DownloadDashRangeAsync(
                    job,
                    new MediaSegment(baseUri, null, null, indexRange.Value.Start, indexRange.Value.End),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (DownloadEngineException ex)
            {
                return (null, ex.Failure.Message);
            }

            if (!TryParseSidx(indexBytes, indexRange.Value.End, out var ranges, out var sidxError))
            {
                return (null, sidxError);
            }

            var rangedSegments = ranges
                .Select(range => new MediaSegment(baseUri, null, null, range.Start, range.End))
                .ToList();
            return (new DashTrack(baseInit, rangedSegments,
                ComputeMediaSignature("dash", baseInit, rangedSegments)), null);
        }

        var segmentList = representation.Elements(ns + "SegmentList").FirstOrDefault()
                          ?? adaptation.Elements(ns + "SegmentList").FirstOrDefault()
                          ?? period.Elements(ns + "SegmentList").FirstOrDefault();
        if (segmentList is not null)
        {
            var initElement = segmentList.Element(ns + "Initialization");
            MediaSegment? listInit = null;
            if (initElement is not null)
            {
                var initSource = (string?)initElement.Attribute("sourceURL") ??
                                 (string?)segmentList.Attribute("initialization");
                var initUri = string.IsNullOrWhiteSpace(initSource)
                    ? baseUri
                    : ResolveDashUri(baseUri, initSource);
                if (initUri is null)
                {
                    return (null, "The DASH SegmentList initialization URL is invalid.");
                }

                var initRange = ParseDashRange((string?)initElement.Attribute("range"));
                listInit = new MediaSegment(initUri, null, null, initRange?.Start, initRange?.End);
            }

            var listed = new List<MediaSegment>();
            foreach (var segment in segmentList.Elements(ns + "SegmentURL"))
            {
                var media = (string?)segment.Attribute("media");
                if (string.IsNullOrWhiteSpace(media))
                {
                    continue;
                }

                var uri = ResolveDashUri(baseUri, media);
                if (uri is null)
                {
                    continue;
                }

                var range = ParseDashRange((string?)segment.Attribute("mediaRange"));
                listed.Add(new MediaSegment(uri, null, null, range?.Start, range?.End));
            }

            if (listed.Count == 0)
            {
                return (null, "The DASH SegmentList has no media segments.");
            }

            return (new DashTrack(listInit, listed,
                ComputeMediaSignature("dash", listInit, listed)), null);
        }

        var template = representation.Elements(ns + "SegmentTemplate").FirstOrDefault()
                       ?? adaptation.Elements(ns + "SegmentTemplate").FirstOrDefault()
                       ?? period.Elements(ns + "SegmentTemplate").FirstOrDefault();
        if (template is null)
        {
            return (null, "This DASH manifest uses an unsupported indexing mode.");
        }

        var mediaTemplate = (string?)template.Attribute("media");
        if (string.IsNullOrWhiteSpace(mediaTemplate))
        {
            return (null, "The DASH SegmentTemplate has no media template.");
        }

        var representationId = (string?)representation.Attribute("id") ?? "0";
        var bandwidth = ParseLong((string?)representation.Attribute("bandwidth"));
        var timescale = Math.Max(1, ParseLong((string?)template.Attribute("timescale"), 1));
        var duration = ParseLong((string?)template.Attribute("duration"));
        var startNumber = ParseLong((string?)template.Attribute("startNumber"), 1);
        var initTemplate = (string?)template.Attribute("initialization");
        MediaSegment? init = null;
        if (!string.IsNullOrWhiteSpace(initTemplate))
        {
            var initUri = ResolveDashUri(baseUri, ExpandDashTemplate(
                initTemplate, representationId, bandwidth, null, null));
            if (initUri is not null)
            {
                init = new MediaSegment(initUri, null, null);
            }
        }

        var segments = new List<MediaSegment>();
        var timeline = template.Element(ns + "SegmentTimeline");
        if (timeline is not null)
        {
            long time = 0;
            var number = startNumber;
            foreach (var s in timeline.Elements(ns + "S"))
            {
                time = ParseLong((string?)s.Attribute("t"), time);
                var step = ParseLong((string?)s.Attribute("d"));
                if (step <= 0)
                {
                    continue;
                }

                var repeat = ParseLong((string?)s.Attribute("r"));
                for (var i = 0; i <= repeat; i++)
                {
                    var uri = ResolveDashUri(baseUri, ExpandDashTemplate(
                        mediaTemplate, representationId, bandwidth, number, time));
                    if (uri is not null)
                    {
                        segments.Add(new MediaSegment(uri, null, null));
                    }

                    time += step;
                    number++;
                }
            }
        }
        else
        {
            if (duration <= 0)
            {
                return (null, "The DASH SegmentTemplate has no duration or SegmentTimeline.");
            }

            TimeSpan totalDuration;
            try
            {
                totalDuration = XmlConvert.ToTimeSpan(
                    (string?)root.Attribute("mediaPresentationDuration") ?? string.Empty);
            }
            catch (FormatException)
            {
                return (null, "The DASH manifest has no valid mediaPresentationDuration.");
            }

            var segmentSeconds = duration / (double)timescale;
            var count = (int)Math.Ceiling(totalDuration.TotalSeconds / Math.Max(0.001, segmentSeconds));
            for (var i = 0; i < count; i++)
            {
                var number = startNumber + i;
                var time = (long)i * duration;
                var uri = ResolveDashUri(baseUri, ExpandDashTemplate(
                    mediaTemplate, representationId, bandwidth, number, time));
                if (uri is not null)
                {
                    segments.Add(new MediaSegment(uri, null, null));
                }
            }
        }

        if (segments.Count == 0)
        {
            return (null, "The DASH manifest produced no segments.");
        }

        return (new DashTrack(init, segments,
            ComputeMediaSignature("dash", init, segments)), null);
    }

    private async Task<byte[]> DownloadDashRangeAsync(
        DownloadJob job,
        MediaSegment segment,
        CancellationToken cancellationToken)
    {
        var start = segment.RangeStart ?? 0;
        var end = segment.RangeEnd;
        var expectedLength = end is null ? (long?)null : end.Value - start + 1;
        using var request = BuildRequest(
            job, segment.Uri, start, SameAuthority(job.Url, segment.Uri), end,
            includeValidators: false);
        // As for HLS fragments: one at a time per job, under the host's cap.
        using var hostSlot = await _hostLimiter.AcquireAsync(segment.Uri.Host, job.Id, yieldable: false, cancellationToken)
            .ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.ReadTimeout);
        using var response = await SendTrackedAsync(job, request, timeout.Token)
            .ConfigureAwait(false);
        var status = (int)response.StatusCode;
        if (status is 401 or 403 or 404 or 407 or 410)
        {
            throw new DownloadEngineException(
                ClassifyHttpFailure(status, response), isRetryable: false);
        }

        if (status is not (200 or 206))
        {
            throw new DownloadEngineException(new JobFailure(
                FailureKind.Http, $"DASH byte range returned HTTP {status}.",
                status >= 500, false, DateTimeOffset.UtcNow), isRetryable: status >= 500);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token)
            .ConfigureAwait(false);
        var buffer = new byte[81_920];
        if (status == 200 && start > 0)
        {
            var remaining = start;
            while (remaining > 0)
            {
                var read = await stream.ReadAsync(
                    buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), timeout.Token)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    throw new DownloadEngineException(new JobFailure(
                        FailureKind.InvalidContent,
                        "The DASH server ignored the byte range and ended early.",
                        false, false, DateTimeOffset.UtcNow), isRetryable: false);
                }

                remaining -= read;
            }
        }

        using var memory = new MemoryStream();
        long total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (expectedLength is { } wanted)
            {
                var allowed = (int)Math.Min(read, wanted - total);
                if (allowed <= 0)
                {
                    break;
                }

                memory.Write(buffer, 0, allowed);
                total += allowed;
                if (total >= wanted)
                {
                    break;
                }
            }
            else
            {
                memory.Write(buffer, 0, read);
                total += read;
            }
        }

        if (expectedLength is { } length && total != length)
        {
            throw new DownloadEngineException(new JobFailure(
                FailureKind.InvalidContent,
                $"The DASH byte range returned {total} of {length} expected bytes.",
                false, false, DateTimeOffset.UtcNow), isRetryable: false);
        }

        return memory.ToArray();
    }

    private static (long Start, long End)? ParseDashRange(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var separator = text.IndexOf('-');
        if (separator <= 0 ||
            !long.TryParse(text[..separator].Trim(), out var start) ||
            !long.TryParse(text[(separator + 1)..].Trim(), out var end) ||
            start < 0 || end < start)
        {
            return null;
        }

        return (start, end);
    }

    private static bool TryParseSidx(
        byte[] data,
        long indexEnd,
        out List<(long Start, long End)> ranges,
        out string? error)
    {
        ranges = new List<(long Start, long End)>();
        error = null;
        if (data.Length < 12 ||
            !string.Equals(
                System.Text.Encoding.ASCII.GetString(data, 4, 4),
                "sidx",
                StringComparison.Ordinal))
        {
            error = "The DASH SegmentBase index range does not contain a SIDX box.";
            return false;
        }

        var headerSize = ReadUInt32BE(data, 0) == 1 ? 16 : 8;
        if (data.Length < headerSize + 4)
        {
            error = "The DASH SIDX box is truncated.";
            return false;
        }

        var position = headerSize;
        var version = data[position];
        position += 4;
        position += 4; // reference_ID
        position += 4; // timescale
        ulong firstOffset;
        if (version == 0)
        {
            position += 4; // earliest_presentation_time
            firstOffset = ReadUInt32BE(data, position);
            position += 4;
        }
        else
        {
            position += 8; // earliest_presentation_time (64-bit)
            firstOffset = ReadUInt64BE(data, position);
            position += 8;
        }

        position += 2; // reserved
        if (position + 2 > data.Length)
        {
            error = "The DASH SIDX box is truncated.";
            return false;
        }

        var count = (data[position] << 8) | data[position + 1];
        position += 2;
        long next = indexEnd + 1 + (long)firstOffset;
        for (var i = 0; i < count; i++)
        {
            if (position + 12 > data.Length)
            {
                error = "The DASH SIDX reference table is truncated.";
                return false;
            }

            var sizeWord = ReadUInt32BE(data, position);
            var isIndex = (sizeWord & 0x80000000) != 0;
            var size = sizeWord & 0x7FFFFFFF;
            position += 12;
            if (isIndex)
            {
                error = "Hierarchical DASH SIDX references are not supported yet.";
                return false;
            }

            if (size <= 0)
            {
                error = "The DASH SIDX contains an empty reference.";
                return false;
            }

            ranges.Add((next, next + size - 1));
            next += size;
        }

        if (ranges.Count == 0)
        {
            error = "The DASH SIDX contains no media references.";
            return false;
        }

        return true;
    }

    private static uint ReadUInt32BE(byte[] data, int offset)
        => (uint)((data[offset] << 24) | (data[offset + 1] << 16) |
                  (data[offset + 2] << 8) | data[offset + 3]);

    private static ulong ReadUInt64BE(byte[] data, int offset)
        => ((ulong)ReadUInt32BE(data, offset) << 32) | ReadUInt32BE(data, offset + 4);

    private async Task<DownloadResult> DownloadAndMuxDashTracksAsync(
        DownloadJob job,
        DashTrack videoTrack,
        DashTrack audioTrack,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        var baseName = Path.GetFileNameWithoutExtension(job.FileName);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = "stream";
        }

        var directory = string.IsNullOrWhiteSpace(job.DestinationDirectory)
            ? Path.GetDirectoryName(job.FinalFilePath) ?? AppSettings.GetDefaultDownloadDirectory()
            : job.DestinationDirectory;
        job.FinalFilePath = FileNameResolver.ResolveUniquePath(
            directory, baseName + ".mp4", job.ConflictPolicy);
        job.FileName = Path.GetFileName(job.FinalFilePath);
        var videoPath = job.PartFilePath + ".video";
        var audioPath = job.PartFilePath + ".audio";
        var muxPath = job.PartFilePath + ".mux.mp4";
        var received = 0L;
        try
        {
            received += await DownloadMediaTrackToFileAsync(
                job,
                videoTrack.Init,
                videoTrack.Segments,
                videoTrack.Signature,
                videoPath,
                received,
                "Downloading DASH video",
                "dash-video",
                progress,
                cancellationToken).ConfigureAwait(false);
            received += await DownloadMediaTrackToFileAsync(
                job,
                audioTrack.Init,
                audioTrack.Segments,
                audioTrack.Signature,
                audioPath,
                received,
                "Downloading DASH audio",
                "dash-audio",
                progress,
                cancellationToken).ConfigureAwait(false);

            progress?.Report(new TransferProgress(received, null, 0, null, 0, "Muxing audio/video"));
            var mux = await _muxer.MuxAsync(videoPath, audioPath, muxPath, cancellationToken)
                .ConfigureAwait(false);
            if (!mux.Success)
            {
                return DownloadResult.Fail(DownloadState.Failed, new JobFailure(
                    FailureKind.MuxingFailed,
                    mux.Error ?? "Audio/video muxing failed.",
                    false, true,
                    DateTimeOffset.UtcNow), received, received);
            }

            if (!string.IsNullOrWhiteSpace(job.ExpectedSha256))
            {
                var actual = await Hashing.Sha256FileAsync(muxPath, cancellationToken).ConfigureAwait(false);
                if (!actual.Equals(job.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    return DownloadResult.Fail(DownloadState.Failed, new JobFailure(
                        FailureKind.VerificationFailed,
                        $"The muxed output SHA-256 did not match (expected {job.ExpectedSha256}, got {actual}).",
                        false, true, DateTimeOffset.UtcNow), received, received);
                }
            }

            File.Move(muxPath, job.FinalFilePath,
                overwrite: job.ConflictPolicy is ConflictPolicy.Overwrite);
            if (_mediaCheckpoints is not null)
            {
                await _mediaCheckpoints.DeleteAsync(job.Id, "dash-video", cancellationToken).ConfigureAwait(false);
                await _mediaCheckpoints.DeleteAsync(job.Id, "dash-audio", cancellationToken).ConfigureAwait(false);
            }

            var finalBytes = new FileInfo(job.FinalFilePath).Length;
            DeleteFileQuietly(videoPath);
            DeleteFileQuietly(audioPath);
            progress?.Report(new TransferProgress(finalBytes, finalBytes, 0, TimeSpan.Zero, 0, "Completed"));
            return DownloadResult.Ok(finalBytes, finalBytes, job.FinalFilePath, null, null, "video/mp4");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return DownloadResult.Fail(DownloadState.Cancelled, new JobFailure(
                FailureKind.Cancelled, "The DASH download was cancelled.", false, true, DateTimeOffset.UtcNow),
                received, received);
        }
        catch (DownloadEngineException ex)
        {
            return DownloadResult.Fail(DownloadState.Failed, ex.Failure, received, received);
        }
        catch (Exception ex)
        {
            return DownloadResult.Fail(DownloadState.Failed, MapException(ex, out _), received, received);
        }
        finally
        {
            DeleteFileQuietly(videoPath);
            DeleteFileQuietly(audioPath);
            DeleteFileQuietly(muxPath);
        }
    }

    private async Task<long> DownloadMediaTrackToFileAsync(
        DownloadJob job,
        MediaSegment? initSegment,
        IReadOnlyList<MediaSegment> segments,
        string signature,
        string path,
        long receivedBase,
        string phase,
        string checkpointTrack,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        var completedSegments = 0;
        var received = 0L;
        if (_mediaCheckpoints is not null)
        {
            var existing = await _mediaCheckpoints.LoadAsync(job.Id, checkpointTrack, cancellationToken)
                .ConfigureAwait(false);
            var length = File.Exists(path) ? new FileInfo(path).Length : -1;
            if (existing is not null &&
                string.Equals(existing.Signature, signature, StringComparison.Ordinal) &&
                length >= existing.CompletedBytes)
            {
                completedSegments = Math.Min(existing.CompletedSegments, segments.Count);
                received = existing.CompletedBytes;
            }
            else if (existing is not null)
            {
                await _mediaCheckpoints.DeleteAsync(job.Id, checkpointTrack, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        var stopwatch = Stopwatch.StartNew();
        var state = new HlsProgressState();
        await using (var file = new FileStream(
                         path, received > 0 ? FileMode.Open : FileMode.Create,
                         FileAccess.Write, FileShare.Read,
                         _options.BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            if (received > 0)
            {
                file.SetLength(received);
                file.Position = received;
            }

            if (initSegment is not null && completedSegments == 0)
            {
                received += await DownloadHlsSegmentAsync(
                    job, initSegment, file, receivedBase + received,
                    progress, stopwatch, state, phase, cancellationToken).ConfigureAwait(false);
            }

            for (var i = completedSegments; i < segments.Count; i++)
            {
                received += await DownloadHlsSegmentAsync(
                    job, segments[i], file, receivedBase + received,
                    progress, stopwatch, state, phase, cancellationToken).ConfigureAwait(false);
                await file.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (_mediaCheckpoints is not null)
                {
                    await _mediaCheckpoints.SaveAsync(new MediaCheckpoint
                    {
                        JobId = job.Id,
                        Track = checkpointTrack,
                        Signature = signature,
                        CompletedSegments = i + 1,
                        CompletedBytes = received,
                    }, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        return received;
    }

    private static void DeleteFileQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort cleanup
        }
    }

    private static string ComputeMediaSignature(string track, Uri? init, IReadOnlyList<Uri> segments)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendSignature(hash, track);
        // Ignore query strings: media tokens commonly rotate while the segment
        // identity (scheme/host/path) stays the same.
        AppendSignature(hash, init?.GetLeftPart(UriPartial.Path) ?? string.Empty);
        foreach (var segment in segments)
        {
            AppendSignature(hash, segment.GetLeftPart(UriPartial.Path));
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string ComputeMediaSignature(
        string track,
        Uri? init,
        IReadOnlyList<MediaSegment> segments)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendSignature(hash, track);
        AppendSignature(hash, init?.GetLeftPart(UriPartial.Path) ?? string.Empty);
        foreach (var segment in segments)
        {
            AppendSignature(hash, segment.Uri.GetLeftPart(UriPartial.Path));
            if (segment.Key is not null)
            {
                AppendSignature(hash, Convert.ToHexString(segment.Key));
            }

            if (segment.Iv is not null)
            {
                AppendSignature(hash, Convert.ToHexString(segment.Iv));
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string ComputeMediaSignature(
        string track,
        MediaSegment? init,
        IReadOnlyList<MediaSegment> segments)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendSignature(hash, track);
        if (init is not null)
        {
            AppendMediaSegmentSignature(hash, init);
        }

        foreach (var segment in segments)
        {
            AppendMediaSegmentSignature(hash, segment);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendMediaSegmentSignature(IncrementalHash hash, MediaSegment segment)
    {
        AppendSignature(hash, segment.Uri.GetLeftPart(UriPartial.Path));
        if (segment.Key is not null)
        {
            AppendSignature(hash, Convert.ToHexString(segment.Key));
        }

        if (segment.Iv is not null)
        {
            AppendSignature(hash, Convert.ToHexString(segment.Iv));
        }

        if (segment.RangeStart is { } start && segment.RangeEnd is { } end)
        {
            AppendSignature(hash, $"{start}-{end}");
        }
    }

    private static void AppendSignature(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        hash.AppendData(bytes);
        hash.AppendData([0]);
    }

    private sealed record DashTrack(
        MediaSegment? Init,
        List<MediaSegment> Segments,
        string Signature);

    private sealed record ParsedHlsSegment(Uri Uri, Uri? KeyUri, byte[] Iv);

    private sealed record MediaSegment(
        Uri Uri,
        byte[]? Key,
        byte[]? Iv,
        long? RangeStart = null,
        long? RangeEnd = null);

    private sealed record HlsMasterSelection(Uri Variant, Uri? Audio);

    private sealed record HlsAudioRendition(
        string GroupId,
        Uri? Uri,
        bool IsDefault,
        bool AutoSelect);

    private sealed record HlsMediaPlaylist(Uri? InitUri, List<MediaSegment> Segments);

    private static Uri ResolveDashBase(Uri current, XElement element, XNamespace ns)
    {
        var baseUrl = element.Element(ns + "BaseURL")?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return current;
        }

        return Uri.TryCreate(current, baseUrl, out var resolved) ? resolved : current;
    }

    private static Uri? ResolveDashUri(Uri baseUri, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative))
        {
            return null;
        }

        return Uri.TryCreate(baseUri, relative.Trim(), out var uri) ? uri : null;
    }

    private static string ExpandDashTemplate(
        string template,
        string representationId,
        long bandwidth,
        long? number,
        long? time)
    {
        var result = template
            .Replace("$RepresentationID$", representationId, StringComparison.Ordinal)
            .Replace("$Bandwidth$", bandwidth.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        if (number is { } value)
        {
            result = System.Text.RegularExpressions.Regex.Replace(
                result,
                @"\$Number(?:%0?(\d+)d)?\$",
                match => value.ToString(
                    match.Groups[1].Success ? "D" + match.Groups[1].Value : "D",
                    CultureInfo.InvariantCulture));
        }

        if (time is { } timeValue)
        {
            result = System.Text.RegularExpressions.Regex.Replace(
                result,
                @"\$Time(?:%0?(\d+)d)?\$",
                match => timeValue.ToString(
                    match.Groups[1].Success ? "D" + match.Groups[1].Value : "D",
                    CultureInfo.InvariantCulture));
        }

        return result;
    }

    private static long ParseLong(string? value, long fallback = 0)
        => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;

    public async Task<FileMetadata> ReadMetadataAsync(DownloadJob request, CancellationToken cancellationToken)
    {
        try { return await ReadMetadataCoreAsync(request, cancellationToken).ConfigureAwait(false); }
        catch (DownloadEngineException ex) { throw new HttpRequestException(ex.Message, ex); }
    }

    private async Task<FileMetadata> ReadMetadataCoreAsync(DownloadJob request, CancellationToken cancellationToken)
    {
        if (!TryCreateUri(request.Url, out var uri, out var error)) throw new ArgumentException(error);
        // One short request, not a transfer connection: it must not queue behind a running
        // segmented download to the same host, or names for the rest of a batch time out.
        var response = await SendWithRedirectsAsync(request, 0, cancellationToken, method: HttpMethod.Head).ConfigureAwait(false);
        try
        {
            if (response.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented)
            {
                response.Dispose();
                response = await SendWithRedirectsAsync(request, 0, cancellationToken, rangeEnd: 0).ConfigureAwait(false);
            }
            response.EnsureSuccessStatusCode();
            var finalUri = response.RequestMessage?.RequestUri ?? uri;
            var disposition = response.Content.Headers.TryGetValues("Content-Disposition", out var values)
                ? values.FirstOrDefault() : null;
            var size = response.StatusCode == HttpStatusCode.PartialContent
                ? response.Content.Headers.ContentRange?.Length : response.Content.Headers.ContentLength;
            return new FileMetadata(FileNameResolver.Resolve(disposition, finalUri), size,
                response.Content.Headers.ContentType?.MediaType, finalUri.AbsoluteUri,
                response.Headers.ETag?.ToString(), response.Content.Headers.LastModified?.ToString("R"));
        }
        finally { response.Dispose(); }
    }

    /// <summary>
    /// The add asks the server for a name before the job exists. When that went unanswered the job has its
    /// address's last part, which for a CDN link is only an id. The transfer's own first answer can still say
    /// the real name, as a browser would take it. Asked once: a name the user gives later is theirs.
    /// </summary>
    private static void AdoptServerName(DownloadJob job, HttpResponseMessage response)
    {
        if (!job.NameFromAddress) return;
        job.NameFromAddress = false;
        var disposition = response.Content.Headers.TryGetValues("Content-Disposition", out var values) ? values.FirstOrDefault() : null;
        if (FileNameResolver.FromContentDisposition(disposition) is not { } given) return;
        var name = FileNameResolver.Sanitize(given);

        // A name that repeats part of the query is no name: on a direct link, that is where the key is.
        var query = Uri.TryCreate(job.EffectiveUrl, UriKind.Absolute, out var address) ? Uri.UnescapeDataString(address.Query) : string.Empty;
        if (name == "download" || name == job.FileName || (name.Length >= 8 && query.Contains(name, StringComparison.OrdinalIgnoreCase))) return;

        job.FileName = name;
        var directory = Path.GetDirectoryName(job.FinalFilePath);
        // The part file keeps its own name; the finished file takes this one, and a clash is settled when it lands.
        job.FinalFilePath = string.IsNullOrEmpty(directory) ? string.Empty : Path.Combine(directory, name);
    }

    private async Task<HttpResponseMessage> SendWithRedirectsAsync(
        DownloadJob job,
        long offset,
        CancellationToken cancellationToken,
        long? rangeEnd = null,
        HttpMethod? method = null)
    {
        // Browser handoff can already have resolved a landing page to a CDN URL.
        // Keep credentials bound to the original origin even when skipping that page.
        var originalOrigin = new Uri(job.Url, UriKind.Absolute).GetLeftPart(UriPartial.Authority);
        if (!TryCreateUri(job.EffectiveUrl, out var currentUri, out var targetError))
        {
            throw new DownloadEngineException(
                new JobFailure(FailureKind.InvalidContent, targetError, false, false, DateTimeOffset.UtcNow),
                isRetryable: false);
        }
        for (var redirect = 0; redirect <= _options.MaxRedirects; redirect++)
        {
            if (!_options.AllowLocalNetwork && IsLocalTarget(currentUri))
            {
                throw new DownloadEngineException(
                    new JobFailure(FailureKind.Forbidden, "Local network destinations are disabled.", false, false, DateTimeOffset.UtcNow),
                    isRetryable: false);
            }
            using var request = BuildRequest(job, currentUri, offset,
                sameOrigin: currentUri.GetLeftPart(UriPartial.Authority) == originalOrigin,
                rangeEnd: rangeEnd);
            if (method is not null) request.Method = method;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.ConnectTimeout);

            HttpResponseMessage response;
            try
            {
                response = await SendTrackedAsync(job, request, timeout.Token)
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

            response.RequestMessage ??= request;
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

                // A resolved link holds an account key, which a plain http request would send in the open.
                if (job.ResolvedUrl is not null && currentUri.Scheme == Uri.UriSchemeHttps && next.Scheme == Uri.UriSchemeHttp && !next.IsLoopback)
                {
                    throw new DownloadEngineException(
                        new JobFailure(FailureKind.Http,
                            "The download server redirected to an unencrypted address. The link holds your account key, so it wasn't followed. Anything already downloaded was kept.",
                            false, true, DateTimeOffset.UtcNow),
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
        long? rangeEnd = null,
        bool includeValidators = true)
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

        if (sameOrigin && !request.Headers.Contains("Authorization"))
        {
            var login = MatchSiteLogin(uri);
            if (login is not null)
            {
                var token = Convert.ToBase64String(
                    Encoding.UTF8.GetBytes($"{login.Username}:{login.Password}"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
            }
        }

        if (!string.IsNullOrWhiteSpace(job.Referrer) && sameOrigin &&
            Uri.TryCreate(job.Referrer, UriKind.Absolute, out var referrer))
        {
            request.Headers.Referrer = referrer;
        }

        // A provider vouches for exact bytes, and a whole-file answer can arrive rewritten: TorBox's CDN
        // adds to an HTML file sent whole, and sends the same file untouched as a range, even a range from zero.
        var wantsExactBytes = !string.IsNullOrWhiteSpace(job.ResolvedUrl) || job.ExpectedTotalBytes is > 0;
        if (offset == 0 && rangeEnd is null && wantsExactBytes)
        {
            request.Headers.Range = new RangeHeaderValue(0, null);
        }
        else if (offset > 0 || rangeEnd is not null)
        {
            request.Headers.Range = new RangeHeaderValue(offset, rangeEnd);
            if (includeValidators)
            {
                if (!string.IsNullOrWhiteSpace(job.ETag) &&
                    !job.ETag.StartsWith("W/", StringComparison.Ordinal))
                {
                    request.Headers.IfRange = new RangeConditionHeaderValue(job.ETag);
                }
                else if (DateTimeOffset.TryParse(job.LastModified, out var lastModified))
                {
                    request.Headers.IfRange = new RangeConditionHeaderValue(lastModified);
                }
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
        var probe = await ProbeSegmentedAsync(job, checkpoint, progress, cancellationToken).ConfigureAwait(false);
        if (probe.Failure is { } probeFailure)
        {
            // A failed probe says nothing about the file, so saved progress stays exactly as it
            // is for the next attempt. With none saved, the single-stream path reports the
            // failure itself, except an error page, which it would only fetch a second time.
            return checkpoint is not null || probe.IsErrorBody
                ? DownloadResult.Fail(DownloadState.Failed, probeFailure, checkpoint?.CompletedBytes ?? 0, checkpoint?.TotalBytes)
                : null;
        }

        var plan = probe.Plan;
        if (plan is not null && job.ExpectedTotalBytes is > 0 && plan.Total != job.ExpectedTotalBytes)
        {
            await DiscardLocalDataAsync(job).ConfigureAwait(false);
            return DownloadResult.Fail(DownloadState.Failed, ExpectedSizeMismatch(job, plan.Total));
        }

        if (plan is null || plan.Total < 1024 * 1024)
        {
            // The server now answers in a way these segments cannot continue from.
            if (checkpoint is not null)
            {
                await ResetSegmentedAsync(job).ConfigureAwait(false);
            }

            return null;
        }

        var existingBytes = checkpoint is not null && File.Exists(job.PartFilePath)
            ? new FileInfo(job.PartFilePath).Length
            : 0;
        EnsureFreeSpace(job, plan.Total, existingBytes);

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
                SparseFile.TryMarkSparse(file.SafeFileHandle);
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
        // Registered before the count is read: a change that lands after the read finds the run and wakes it, and one
        // that lands before is in the count, so neither is lost.
        lock (_runsSync)
        {
            _segmentedRuns[job.Id] = run;
        }

        var workerCount = Math.Max(1, Math.Min(run.TargetWorkers, Math.Max(1, segments.Count)));

        DownloadResult? failure = null;

        try
        {
            await SuperviseSegmentWorkersAsync(run, workerCount, linked.Token).ConfigureAwait(false);
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
            lock (_runsSync)
            {
                if (_segmentedRuns.TryGetValue(job.Id, out var current) && ReferenceEquals(current, run))
                {
                    _segmentedRuns.Remove(job.Id);
                }
            }

            if (run.Unsupported || failure is not null)
            {
                linked.Cancel();
            }

            try
            {
                await Task.WhenAll(run.Workers()).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The first failure is already captured above.
            }

            // No worker is writing now, and the saves below come after the handle is gone.
            run.FileDurable = TryFlushToDisk(file);
            run.FileClosed = true;
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

        // A failed verification keeps the bytes, and a retry should find every range complete.
        await SaveSegmentedCheckpointAsync(run, CancellationToken.None).ConfigureAwait(false);
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

    private async Task<SegmentedProbe> ProbeSegmentedAsync(
        DownloadJob job,
        DownloadCheckpoint? checkpoint,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var probe = await ProbeSegmentedOnceAsync(job, checkpoint is not null, cancellationToken).ConfigureAwait(false);

            // Without saved progress the single-stream path takes over and does its own retrying.
            if (probe.Failure is null || !probe.IsRetryable || checkpoint is null || attempt >= _options.MaxAttempts)
            {
                return probe;
            }

            var delay = RetryPolicy.Delay(attempt, _options, probe.RetryAfter);
            try
            {
                progress?.Report(new TransferProgress(checkpoint.CompletedBytes, checkpoint.TotalBytes, 0, null, 0,
                    $"Retrying in {Math.Max(1, Math.Ceiling(delay.TotalSeconds))}s"));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Progress observers must not be able to break a transfer.
            }

            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<SegmentedProbe> ProbeSegmentedOnceAsync(DownloadJob job, bool hasLocalData, CancellationToken cancellationToken)
    {
        try
        {
            using var hostSlot = await _hostLimiter.AcquireAsync(HostOf(job), job.Id, yieldable: false, cancellationToken)
                .ConfigureAwait(false);
            using var response = await SendWithRedirectsAsync(job, 0, cancellationToken, rangeEnd: 0)
                .ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status is 401 or 403 or 404 or 407 or 410)
            {
                return new SegmentedProbe { Failure = ClassifyHttpFailure(status, response) };
            }

            if (status is 408 or 425 or 429 || status >= 500)
            {
                return new SegmentedProbe
                {
                    Failure = new JobFailure(FailureKind.Http, $"The server returned HTTP {status}.", true, true, DateTimeOffset.UtcNow, status),
                    IsRetryable = true,
                    RetryAfter = RetryAfterOf(response),
                };
            }

            if (status is < 200 or >= 300)
            {
                return new SegmentedProbe
                {
                    Failure = new JobFailure(FailureKind.Http, $"The server returned HTTP {status}.", false, true, DateTimeOffset.UtcNow, status),
                };
            }

            if (LooksLikeErrorBody(job, response) is { } errorBody)
            {
                return new SegmentedProbe { Failure = errorBody, IsErrorBody = true };
            }

            if (NotThePromisedFile(job, response, hasLocalData) is { } notThePromisedFile)
            {
                return new SegmentedProbe { Failure = notThePromisedFile, IsErrorBody = true };
            }

            if (status != 206)
            {
                job.ResumeSupported = false;
                return new SegmentedProbe();
            }

            var range = response.Content.Headers.ContentRange;
            if (range?.From != 0 || range.To is null || range.Length is not > 0)
            {
                return new SegmentedProbe();
            }

            job.ResumeSupported = true;
            if (!hasLocalData) AdoptServerName(job, response);
            return new SegmentedProbe
            {
                Plan = new SegmentedPlan(
                    range.Length.Value,
                    response.Headers.ETag?.Tag,
                    response.Content.Headers.LastModified?.ToString("R"),
                    response.Content.Headers.ContentType?.ToString()),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var failure = MapException(ex, out var retryable);
            return new SegmentedProbe { Failure = failure, IsRetryable = retryable };
        }
    }

    /// <summary>
    /// Runs the workers, and adds some when the job's connection count goes up while it runs (UI.R21). The new
    /// ones take an unclaimed range or split a running one through <see cref="TryStealSegment"/>, the path a
    /// worker that ran out of work already takes, so no checkpoint shape is new. A lower count lets workers above
    /// it stop once their current range is done (see <see cref="RunSegmentWorkerAsync"/>); none is cut short.
    /// </summary>
    private async Task SuperviseSegmentWorkersAsync(SegmentedRun run, int initialWorkers, CancellationToken cancellationToken)
    {
        for (var i = 0; i < initialWorkers; i++)
        {
            run.StartWorker(() => RunSegmentWorkerAsync(run, cancellationToken));
        }

        while (true)
        {
            var current = run.Workers();
            var all = Task.WhenAll(current);
            var woken = run.WakeTask();
            if (await Task.WhenAny(all, woken).ConfigureAwait(false) == all)
            {
                if (run.Workers().Length == current.Length)
                {
                    // Throws the first worker's failure, as before.
                    await all.ConfigureAwait(false);
                    return;
                }

                continue;
            }

            run.ResetWake();
            if (cancellationToken.IsCancellationRequested || current.Any(task => task.IsFaulted))
            {
                continue;
            }

            int toAdd;
            lock (run.Sync)
            {
                toAdd = HasWorkLocked(run) ? run.TargetWorkers - run.LiveWorkers : 0;
            }

            for (var i = 0; i < toAdd; i++)
            {
                run.StartWorker(() => RunSegmentWorkerAsync(run, cancellationToken));
            }
        }
    }

    /// <summary>
    /// Called under <see cref="SegmentedRun.Sync"/> after the run's work shrank (a claim, a split, written bytes).
    /// When a worker is waiting for a connection and nothing is left that it could take, returns the source to
    /// cancel so that wait ends: otherwise the connection may go to another job first, and this job, with every
    /// byte fetched, would wait for one it no longer needs.
    /// </summary>
    private static CancellationTokenSource? IdleWaitersToReleaseLocked(SegmentedRun run)
        => run.PendingAcquires > 0 && !HasWorkLocked(run) ? run.SwapNoWorkLocked() : null;

    /// <summary>A range nobody is on, or one long enough that splitting it is worth a connection.</summary>
    private static bool HasWorkLocked(SegmentedRun run)
    {
        foreach (var segment in run.Segments)
        {
            var remaining = segment.End - (segment.Start + segment.Completed) + 1;
            if (remaining <= 0) continue;
            if (!run.Claimed.Contains(segment) || remaining >= MinimumStealBytes) return true;
        }

        return false;
    }

    private static SegmentCheckpoint? ClaimUnclaimedSegment(SegmentedRun run)
    {
        lock (run.Sync)
        {
            foreach (var candidate in run.Segments)
            {
                if (run.Claimed.Contains(candidate))
                {
                    continue;
                }

                if (candidate.Completed < candidate.End - candidate.Start + 1)
                {
                    run.Claimed.Add(candidate);
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// One worker: a connection first, then a range. Taking the connection first matters (UI.B7): a worker that
    /// waits for one holds no range, so this job's other workers can take that range meanwhile.
    /// </summary>
    private async Task RunSegmentWorkerAsync(
        SegmentedRun run,
        CancellationToken cancellationToken)
    {
        var retired = false;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CancellationToken noWork;
                lock (run.Sync)
                {
                    // The connection count went down: the workers above it stop between ranges.
                    if (run.LiveWorkers > run.TargetWorkers)
                    {
                        run.LiveWorkers--;
                        retired = true;
                        return;
                    }

                    if (!HasWorkLocked(run))
                    {
                        // Counted out under the lock that saw no work: until then the other workers' retire check
                        // above still counted this one, so a worker that gave a range back and retired against
                        // that stale count could leave the range with no worker at all.
                        run.LiveWorkers--;
                        retired = true;
                        return;
                    }

                    run.PendingAcquires++;
                    noWork = run.NoWorkToken;
                }

                HostSlot slot;
                try
                {
                    using var acquireToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, noWork);
                    slot = await _hostLimiter.AcquireAsync(run.Host, run.Job.Id, yieldable: true, acquireToken.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (noWork.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    // The other workers took or finished everything meanwhile: look again, which normally ends here.
                    continue;
                }
                finally
                {
                    lock (run.Sync)
                    {
                        run.PendingAcquires--;
                    }
                }

                // The count may have gone down while this worker waited for its connection: it stops here, before
                // taking a range, rather than after one more.
                lock (run.Sync)
                {
                    if (run.LiveWorkers > run.TargetWorkers)
                    {
                        run.LiveWorkers--;
                        retired = true;
                    }
                }

                if (retired)
                {
                    slot.Dispose();
                    return;
                }

                SegmentCheckpoint? work;
                CancellationTokenSource? idle;
                try
                {
                    // One lock (both helpers re-enter it) for the claim and, when nothing was left, the count.
                    lock (run.Sync)
                    {
                        work = ClaimUnclaimedSegment(run) ?? TryStealSegment(run);
                        idle = IdleWaitersToReleaseLocked(run);
                        if (work is null)
                        {
                            run.LiveWorkers--;
                            retired = true;
                        }
                    }
                }
                catch
                {
                    slot.Dispose();
                    throw;
                }

                idle?.Cancel();
                if (work is null)
                {
                    slot.Dispose();
                    return;
                }

                await DownloadSegmentAsync(run, work, slot, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            // Every exit counts this worker out exactly once: the locked paths above set retired when they did.
            if (!retired)
            {
                lock (run.Sync)
                {
                    run.LiveWorkers--;
                }
            }

            WorkerExitedForTests?.Invoke(run.Job.Id, retired);
        }
    }

    /// <summary>
    /// Test seam: called as each segment worker exits, with whether it was already counted out under the lock
    /// that decided it should stop (false only for exceptions and cancellation). Null outside tests.
    /// </summary>
    internal static Action<Guid, bool>? WorkerExitedForTests { get; set; }

    private const long MinimumStealBytes = 256 * 1024;

    private static SegmentCheckpoint? TryStealSegment(SegmentedRun run)
    {
        lock (run.Sync)
        {
            SegmentCheckpoint? victim = null;
            long bestRemaining = 0;
            foreach (var segment in run.Segments)
            {
                if (!run.Claimed.Contains(segment))
                {
                    continue;
                }

                var remaining = segment.End - (segment.Start + segment.Completed) + 1;
                if (remaining > bestRemaining)
                {
                    bestRemaining = remaining;
                    victim = segment;
                }
            }

            if (victim is null || bestRemaining < MinimumStealBytes)
            {
                return null;
            }

            var nextByte = victim.Start + victim.Completed;
            var oldEnd = victim.End;
            var split = nextByte + bestRemaining / 2;
            if (split <= nextByte || split > oldEnd)
            {
                return null;
            }

            victim.End = split - 1;
            var stolen = new SegmentCheckpoint
            {
                Start = split,
                End = oldEnd,
                Completed = 0,
            };
            run.Segments.Add(stolen);
            run.Claimed.Add(stolen);
            return stolen;
        }
    }

    /// <summary>
    /// Downloads one range, starting on <paramref name="slot"/>, which this method now owns. When the limiter
    /// asks for the connection back, the worker stops at the last byte it wrote and lets go of the range: the
    /// range's <see cref="SegmentCheckpoint.Completed"/> prefix is everything before that byte, so what is left
    /// is the range from there on, which any worker of this job continues. That is the split at the write
    /// offset; it needs no new segment, so every checkpoint written before, during or after it is one an
    /// ordinary pause could have left.
    /// </summary>
    private async Task DownloadSegmentAsync(
        SegmentedRun run,
        SegmentCheckpoint segment,
        HostSlot? slot,
        CancellationToken cancellationToken)
    {
        var job = run.Job;
        var attempt = 0;
        try
        {
            while (true)
            {
                long length;
                long offset;
                long requestEnd;
                long completedBefore;
                lock (run.Sync)
                {
                    length = segment.End - segment.Start + 1;
                    if (segment.Completed >= length)
                    {
                        break;
                    }

                    completedBefore = segment.Completed;
                    offset = segment.Start + segment.Completed;
                    requestEnd = segment.End;
                }

                cancellationToken.ThrowIfCancellationRequested();
                attempt++;
                // A retry waits for a connection like anyone else, still holding its range.
                slot ??= await _hostLimiter.AcquireAsync(run.Host, job.Id, yieldable: true, cancellationToken)
                    .ConfigureAwait(false);
                Interlocked.Increment(ref run.ActiveConnections);
                var yielded = false;
                // Set while this worker writes to its own file or checkpoint: a failure there is a real one even
                // with a yield pending, since a yield never cancels those (they keep the job's token).
                var localWork = false;
                TimeSpan? retryDelay = null;
                try
                {
                    // Everything that only waits on the network may be cut short for a yield. The file write and
                    // the checkpoint keep the job's own token: a yield never leaves a write half done.
                    using var attemptToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, slot.YieldToken);
                    var token = attemptToken.Token;
                    using var response = await SendWithRedirectsAsync(
                        job, offset, token, requestEnd).ConfigureAwait(false);
                    var status = (int)response.StatusCode;

                    if (status is 200 or 206 && LooksLikeErrorBody(job, response) is { } errorBody)
                    {
                        throw new SegmentFailureException(errorBody, retryable: false);
                    }

                    if (NotThePromisedFile(job, response, hasLocalData: true) is { } notThePromisedFile)
                    {
                        throw new SegmentFailureException(notThePromisedFile, retryable: false);
                    }

                    if (status == 200)
                    {
                        throw new SegmentationUnsupportedException();
                    }

                    // The same past-the-end answer as the single-stream path knows, in both its forms.
                    if (status == 416 || (status == 206 && response.Content.Headers.ContentRange is { HasRange: false, HasLength: true }))
                    {
                        lock (run.Sync)
                        {
                            length = segment.End - segment.Start + 1;
                        }

                        if (segment.Completed >= length)
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
                            new JobFailure(FailureKind.Http,
                                $"The server returned HTTP {status} for a segment.",
                                true, true, DateTimeOffset.UtcNow, status),
                            retryable: true,
                            RetryAfterOf(response));
                    }

                    if (status != 206)
                    {
                        throw new SegmentFailureException(
                            new JobFailure(FailureKind.Http,
                                $"Unexpected HTTP {status} for a segment.",
                                false, false, DateTimeOffset.UtcNow, status),
                            retryable: false);
                    }

                    var range = response.Content.Headers.ContentRange;
                    if (range?.From != offset || range.To is null || range.To < offset)
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

                    long remaining;
                    lock (run.Sync)
                    {
                        remaining = segment.End - offset + 1;
                    }

                    if (remaining <= 0)
                    {
                        break;
                    }

                    var contentLength = response.Content.Headers.ContentLength;
                    if (contentLength is > 0 && contentLength < remaining)
                    {
                        throw new SegmentFailureException(
                            new JobFailure(FailureKind.InvalidContent,
                                "The server returned a segment with an unexpected length.",
                                true, true, DateTimeOffset.UtcNow),
                            retryable: true);
                    }

                    var stream = await response.Content.ReadAsStreamAsync(token)
                        .ConfigureAwait(false);
                    var buffer = new byte[_options.BufferSize];
                    var writeOffset = offset;
                    var truncated = false;
                    while (true)
                    {
                        var read = await stream.ReadAsync(buffer.AsMemory(), token)
                            .AsTask()
                            .WaitAsync(_options.ReadTimeout, token)
                            .ConfigureAwait(false);
                        if (read == 0)
                        {
                            break;
                        }

                        int allowed;
                        lock (run.Sync)
                        {
                            var remainingToWrite = segment.End - writeOffset + 1;
                            allowed = (int)Math.Min(read, Math.Max(0, remainingToWrite));
                        }

                        if (allowed <= 0)
                        {
                            truncated = true;
                            break;
                        }

                        if (allowed < read)
                        {
                            truncated = true;
                        }

                        // Bytes read but not yet written are simply fetched again after a yield here.
                        await run.RateLimiter.WaitAsync(allowed, token).ConfigureAwait(false);
                        await SharedRateLimiter.WaitAsync(allowed, token).ConfigureAwait(false);
                        localWork = true;
                        await RandomAccess.WriteAsync(
                                run.File.SafeFileHandle,
                                buffer.AsMemory(0, allowed),
                                writeOffset,
                                cancellationToken)
                            .ConfigureAwait(false);
                        writeOffset += allowed;

                        CancellationTokenSource? idle;
                        lock (run.Sync)
                        {
                            segment.Completed += allowed;
                            run.AggregateBytes += allowed;
                            // This range just got too short to split: a worker waiting to split it can stop.
                            idle = IdleWaitersToReleaseLocked(run);
                        }

                        idle?.Cancel();

                        run.Reporter.Report(force: false, "Downloading");
                        await MaybeSaveSegmentedCheckpointAsync(run, cancellationToken)
                            .ConfigureAwait(false);
                        localWork = false;

                        if (truncated)
                        {
                            break;
                        }

                        if (slot.YieldRequested)
                        {
                            throw new OperationCanceledException(token);
                        }
                    }

                    lock (run.Sync)
                    {
                        length = segment.End - segment.Start + 1;
                    }

                    if (!truncated && segment.Completed < length)
                    {
                        throw new SegmentFailureException(
                            new JobFailure(FailureKind.Network,
                                "A segment connection ended before its range completed.",
                                true, true, DateTimeOffset.UtcNow),
                            retryable: true);
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && slot.YieldRequested)
                {
                    yielded = true;
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
                    // Before the limit is checked, so an attempt that got bytes on the last try allowed still retries.
                    if (SegmentProgressed(run, segment, completedBefore)) attempt = 0;
                    if (!ex.Retryable || attempt >= _options.MaxAttempts)
                    {
                        throw;
                    }

                    retryDelay = RetryPolicy.Delay(attempt, _options, ex.RetryAfter);
                }
                catch (Exception ex)
                {
                    // A yield that cut a request short can surface as another failure; it is still only a yield.
                    // A failed write or checkpoint save is not one of those, so it is never taken for a yield.
                    if (!localWork && !cancellationToken.IsCancellationRequested && slot.YieldRequested)
                    {
                        yielded = true;
                    }
                    else
                    {
                        var failure = MapException(ex, out var retryable);
                        if (SegmentProgressed(run, segment, completedBefore)) attempt = 0;
                        if (!retryable || attempt >= _options.MaxAttempts)
                        {
                            throw new SegmentFailureException(failure, retryable);
                        }

                        retryDelay = RetryPolicy.Delay(attempt, _options);
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref run.ActiveConnections);
                    slot.Dispose();
                    slot = null;
                }

                if (yielded)
                {
                    lock (run.Sync)
                    {
                        run.Claimed.Remove(segment);
                    }

                    run.Reporter.Report(force: true, "Downloading");
                    // The connection is already back with the limiter, so the job that wanted it isn't kept
                    // waiting on this flush. Saving now records the bytes this connection got before it went.
                    await SaveSegmentedCheckpointAsync(run, CancellationToken.None).ConfigureAwait(false);
                    return;
                }

                if (retryDelay is { } delay)
                {
                    await SaveSegmentedCheckpointAsync(run, CancellationToken.None)
                        .ConfigureAwait(false);
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            slot?.Dispose();
        }
    }

    /// <summary>
    /// Whether this attempt got the segment new bytes. A failure after progress starts the count of attempts over,
    /// and is checked before the limit, so an attempt that got bytes doesn't count against it even on the last try
    /// allowed: a server that cuts every response short still delivers the segment a piece at a time, and only
    /// attempts that get nothing use the retry budget up (UI.B15).
    /// </summary>
    private static bool SegmentProgressed(SegmentedRun run, SegmentCheckpoint segment, long completedBefore)
    {
        lock (run.Sync)
        {
            return segment.Completed > completedBefore;
        }
    }

    private async Task MaybeSaveSegmentedCheckpointAsync(SegmentedRun run, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        lock (run.Sync)
        {
            if (now - run.LastCheckpoint < _options.CheckpointInterval)
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

            // Every byte this checkpoint counts is written, and the flush makes it durable. The
            // file is sparse, so a checkpoint that landed first would mark zeros as downloaded,
            // and with no md5 from the provider nothing later would notice.
            if (!run.FileClosed)
            {
                await Task.Run(() => RandomAccess.FlushToDisk(run.File.SafeFileHandle), cancellationToken).ConfigureAwait(false);
            }
            else if (!run.FileDurable)
            {
                return;
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

    private static bool TryFlushToDisk(FileStream file)
    {
        try
        {
            RandomAccess.FlushToDisk(file.SafeFileHandle);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
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

    /// <summary>Deletes the part and checkpoint, when they can no longer become the requested file.</summary>
    private async Task DiscardLocalDataAsync(DownloadJob job)
    {
        try
        {
            if (File.Exists(job.PartFilePath))
            {
                File.Delete(job.PartFilePath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Without a checkpoint the next attempt truncates or replaces the file anyway.
        }

        // Identity is what is being discarded; old validators would only conflict with the new file.
        job.ETag = null;
        job.LastModified = null;

        if (_checkpoints is not null)
        {
            try
            {
                await _checkpoints.DeleteAsync(job.Id, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A stale checkpoint fails validation against the missing part file.
            }
        }
    }

    private static JobFailure ExpectedSizeMismatch(DownloadJob job, long actual)
        => new(FailureKind.RemoteChanged,
            $"The server's file is {actual:N0} bytes, not the expected {job.ExpectedTotalBytes:N0}. " +
            "The partial file was deleted, so a retry starts from the beginning.",
            false, false, DateTimeOffset.UtcNow);

    /// <summary>
    /// A failure when a whole-file answer can't be the file the job was promised. A 206 with another
    /// total describes a different file, but a 200 is any document at all, usually a dead link's
    /// short message, so it says nothing about the saved bytes and must not cost them. It counts
    /// as a dead link so that a provider's job gets a fresh one.
    /// </summary>
    private static JobFailure? NotThePromisedFile(DownloadJob job, HttpResponseMessage response, bool hasLocalData)
    {
        if (job.ExpectedTotalBytes is not > 0 || response.StatusCode != HttpStatusCode.OK)
        {
            return null;
        }

        var length = response.Content.Headers.ContentLength;
        if (length == job.ExpectedTotalBytes || (length is null && !hasLocalData))
        {
            return null;
        }

        return LinkAnsweredAnotherSize(job, length, (int)response.StatusCode);
    }

    private static JobFailure LinkAnsweredAnotherSize(DownloadJob job, long? length, int status)
        => new(FailureKind.ExpiredUrl,
            $"The link answered with {(length is null ? "a response of unknown size" : $"{length:N0} bytes")} " +
            $"instead of the expected {job.ExpectedTotalBytes:N0}-byte file. Anything already downloaded was kept.",
            false, true, DateTimeOffset.UtcNow, status);

    /// <summary>For a file whose bytes a provider vouched for: keeping them can only reach the same result.</summary>
    private async Task<DownloadResult> FailVerificationAsync(DownloadJob job, string problem, long? total)
    {
        await DiscardLocalDataAsync(job).ConfigureAwait(false);
        return DownloadResult.Fail(DownloadState.Failed,
            new JobFailure(FailureKind.VerificationFailed, problem + " The partial file was deleted, so a retry starts from the beginning.",
                false, false, DateTimeOffset.UtcNow),
            0, total);
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

    /// <summary>A plan; a failure; or neither, when the server simply won't do ranged requests.</summary>
    private sealed record SegmentedProbe
    {
        public SegmentedPlan? Plan { get; init; }
        public JobFailure? Failure { get; init; }
        public bool IsRetryable { get; init; }
        public bool IsErrorBody { get; init; }
        public TimeSpan? RetryAfter { get; init; }
    }

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
            RateLimiter = new TransferRateLimiter(() => job.SpeedLimitBytesPerSecond);
            Host = HostOf(job);
        }

        public DownloadJob Job { get; }
        public SegmentedPlan Plan { get; }
        public FileStream File { get; }
        public List<SegmentCheckpoint> Segments { get; }
        public TransferRateLimiter RateLimiter { get; }
        public HashSet<SegmentCheckpoint> Claimed { get; } = new();
        public string Host { get; }
        public object Sync { get; } = new();
        public long AggregateBytes { get; set; }
        public int ActiveConnections;
        public bool Unsupported { get; set; }
        public DateTimeOffset LastCheckpoint { get; set; } = DateTimeOffset.MinValue;
        public SemaphoreSlim CheckpointGate { get; } = new(1, 1);
        public CancellationTokenSource? Cancellation { get; set; }
        public SegmentedReporter Reporter { get; set; } = null!;
        public bool FileClosed { get; set; }
        /// <summary>Whether the last flush before closing worked. If not, an older checkpoint is the honest one.</summary>
        public bool FileDurable { get; set; }

        private readonly List<Task> _workers = new();
        private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationTokenSource _noWork = new();

        /// <summary>Workers waiting for a connection while holding no range. Read and written under <see cref="Sync"/>.</summary>
        public int PendingAcquires { get; set; }

        /// <summary>Cancelled once nothing is left that a waiting worker could take. Read under <see cref="Sync"/>.</summary>
        public CancellationToken NoWorkToken => _noWork.Token;

        /// <summary>
        /// Swaps in a fresh token and returns the old source for the caller to cancel after letting go of
        /// <see cref="Sync"/>: cancelling runs the limiter's abandon, which must not run under this lock.
        /// </summary>
        public CancellationTokenSource SwapNoWorkLocked()
        {
            var stale = _noWork;
            _noWork = new CancellationTokenSource();
            return stale;
        }

        /// <summary>Workers started and not yet retired or finished. Read and written under <see cref="Sync"/>.</summary>
        public int LiveWorkers { get; set; }

        /// <summary>The job's connection count, read live: the user can change it while the job runs.</summary>
        public int TargetWorkers => Math.Clamp(Job.RequestedConnections, 1, 32);

        /// <summary>Counted before the worker's task exists, so a worker that finishes at once can't take the count below zero.</summary>
        public void StartWorker(Func<Task> worker)
        {
            lock (Sync)
            {
                LiveWorkers++;
            }

            var task = worker();
            lock (Sync)
            {
                _workers.Add(task);
            }
        }

        public Task[] Workers()
        {
            lock (Sync)
            {
                return _workers.ToArray();
            }
        }

        public Task WakeTask()
        {
            lock (Sync)
            {
                return _wake.Task;
            }
        }

        public void ResetWake()
        {
            lock (Sync)
            {
                if (_wake.Task.IsCompleted)
                {
                    _wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }
            }
        }

        public void Wake()
        {
            lock (Sync)
            {
                _wake.TrySetResult();
            }
        }
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
        public SegmentFailureException(JobFailure failure, bool retryable, TimeSpan? retryAfter = null)
        {
            Failure = failure;
            Retryable = retryable;
            RetryAfter = retryAfter;
        }

        public JobFailure Failure { get; }
        public bool Retryable { get; }
        public TimeSpan? RetryAfter { get; }
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

        if (job.ExpectedTotalBytes is > 0 && state.Bytes != job.ExpectedTotalBytes)
        {
            return await FailVerificationAsync(job,
                $"The server sent {state.Bytes:N0} bytes, not the expected {job.ExpectedTotalBytes:N0}.",
                job.ExpectedTotalBytes).ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(job.ExpectedSha256))
        {
            var actual = await Hashing.Sha256FileAsync(job.PartFilePath, cancellationToken).ConfigureAwait(false);
            if (!actual.Equals(job.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                var problem = $"SHA-256 mismatch. Expected {job.ExpectedSha256}, got {actual}.";
                if (job.RemoteSource is not null)
                {
                    return await FailVerificationAsync(job, problem, state.Total).ConfigureAwait(false);
                }

                // A hash the user typed can simply be wrong, and the file may be gone from the
                // server by the time they notice. Only they can decide to throw these bytes away.
                return DownloadResult.Fail(DownloadState.Failed,
                    new JobFailure(FailureKind.VerificationFailed,
                        problem + $" The downloaded data was kept in {Path.GetFileName(job.PartFilePath)}. Use Redownload to fetch the file again.",
                        false, true, DateTimeOffset.UtcNow),
                    state.Bytes, state.Total);
            }
        }

        if (!string.IsNullOrWhiteSpace(job.ExpectedMd5))
        {
            var actual = await Hashing.Md5FileAsync(job.PartFilePath, cancellationToken).ConfigureAwait(false);
            if (!actual.Equals(job.ExpectedMd5, StringComparison.OrdinalIgnoreCase))
            {
                return await FailVerificationAsync(job,
                    $"MD5 mismatch. Expected {job.ExpectedMd5}, got {actual}.", state.Total).ConfigureAwait(false);
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

            await SaveCheckpointAsync(job, state, CancellationToken.None, finalPath).ConfigureAwait(false);
            File.Move(job.PartFilePath, finalPath,
                overwrite: job.ConflictPolicy is ConflictPolicy.Overwrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DownloadResult.Fail(DownloadState.Failed, MapException(ex, out _), state.Bytes, state.Total);
        }

        if (_checkpoints is not null && !job.KeepCheckpointAfterFinalize)
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

    /// <summary>For after the part file was closed by an interruption, with bytes the OS may still hold.</summary>
    private async Task SaveStoppedCheckpointAsync(DownloadJob job, TransferState state)
    {
        try
        {
            await Task.Run(() =>
            {
                if (!File.Exists(job.PartFilePath))
                {
                    return;
                }

                using var part = File.OpenHandle(job.PartFilePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                RandomAccess.FlushToDisk(part);
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // These bytes aren't known to be durable, so the earlier checkpoint stands.
            return;
        }

        await SaveCheckpointAsync(job, state, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task SaveCheckpointAsync(DownloadJob job, TransferState state, CancellationToken cancellationToken, string? finalizingTo = null)
    {
        state.RecordedBytes = state.Bytes;
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
            FinalizingTo = finalizingTo,
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

    private static string HostOf(DownloadJob job)
        => Uri.TryCreate(job.EffectiveUrl, UriKind.Absolute, out var uri) ? uri.Host : string.Empty;

    /// <summary>The wait a throttled server asks for. Other statuses' headers are not a promise about capacity.</summary>
    private static TimeSpan? RetryAfterOf(HttpResponseMessage response)
    {
        if ((int)response.StatusCode is not (429 or 503))
        {
            return null;
        }

        var header = response.Headers.RetryAfter;
        return header?.Delta ?? (header?.Date is { } date ? date - DateTimeOffset.UtcNow : (TimeSpan?)null);
    }

    /// <summary>
    /// A failure when a 200 or 206 is really an error document rather than the file. Ordinary
    /// downloads of pages and JSON stay allowed: the target's name says what the user wants.
    /// </summary>
    private static JobFailure? LooksLikeErrorBody(DownloadJob job, HttpResponseMessage response)
    {
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        var extension = Path.GetExtension(job.FileName).TrimStart('.').ToLowerInvariant();
        var html = IsHtmlErrorPage(extension, mediaType);
        var json = !html && IsJsonErrorBody(job, extension, mediaType, response);
        if (!html && !json)
        {
            return null;
        }

        var total = response.StatusCode == HttpStatusCode.PartialContent
            ? response.Content.Headers.ContentRange?.Length
            : response.Content.Headers.ContentLength;
        if (job.ExpectedTotalBytes is > 0)
        {
            // Exactly the promised size is the promised file, whatever type the server calls it.
            if (total == job.ExpectedTotalBytes)
            {
                return null;
            }
        }
        else if (total >= LargestErrorBody && (json || !BinaryExtensions.Contains(extension)))
        {
            // Error documents are small. A large one is a real page or a mislabelled file, unless
            // the name says it can't be HTML at all.
            return null;
        }

        return new JobFailure(
            FailureKind.InvalidContent,
            html
                ? "The server returned an HTML page instead of the requested file. " +
                  "Rename the target to .html to save it as a page."
                : "The server returned a JSON response instead of the requested file.",
            false, false, DateTimeOffset.UtcNow, (int)response.StatusCode);
    }

    private static bool IsHtmlErrorPage(string extension, string? mediaType)
        => mediaType is not null &&
           (mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase) ||
            mediaType.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase)) &&
           extension is not ("html" or "htm" or "xhtml" or "txt" or "xml" or "json" or "mhtml" or "mht");

    private static bool IsJsonErrorBody(DownloadJob job, string extension, string? mediaType, HttpResponseMessage response)
    {
        if (mediaType is null ||
            (!mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) &&
             !mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        // JSON has many legitimate shapes and names, so unlike HTML it is only called an error
        // when the target is known not to be JSON at all.
        return job.ExpectedTotalBytes is > 0
            ? extension is not ("json" or "jsonl" or "ndjson" or "geojson" or "txt" or "log" or "xml" or "html" or "htm")
            : BinaryExtensions.Contains(extension) && !IsCompressedJson(job.FileName, extension, response);
    }

    /// <summary>Object stores label a gzipped JSON file as JSON, which is true of what is inside it.</summary>
    private static bool IsCompressedJson(string fileName, string extension, HttpResponseMessage response)
    {
        if (extension is not ("gz" or "br" or "zst"))
        {
            return false;
        }

        var inner = Path.GetExtension(Path.GetFileNameWithoutExtension(fileName)).TrimStart('.');
        return inner.Equals("json", StringComparison.OrdinalIgnoreCase) ||
               inner.Equals("jsonl", StringComparison.OrdinalIgnoreCase) ||
               inner.Equals("ndjson", StringComparison.OrdinalIgnoreCase) ||
               response.Content.Headers.ContentEncoding.Any(encoding => !encoding.Equals("identity", StringComparison.OrdinalIgnoreCase));
    }

    private const long LargestErrorBody = 1024 * 1024;

    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.Ordinal)
    {
        "bin", "iso", "img", "exe", "msi", "dmg", "deb", "rpm", "apk",
        "zip", "rar", "7z", "gz", "tar", "bz2", "xz", "cab",
        "mp4", "mkv", "avi", "mov", "webm", "wmv", "flv", "m4v", "ts",
        "mp3", "flac", "wav", "m4a", "aac", "ogg", "opus",
        "jpg", "jpeg", "png", "gif", "webp", "pdf",
    };

    private void EnsureFreeSpace(DownloadJob job, long? total, long alreadyBytes)
    {
        if (total is not > 0)
        {
            return;
        }

        var directory = Path.GetDirectoryName(job.PartFilePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = job.DestinationDirectory;
        }

        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        var available = _freeSpace.GetAvailableFreeSpace(directory);
        if (available is null)
        {
            return;
        }

        var remaining = Math.Max(0, total.Value - Math.Max(0, alreadyBytes));
        var required = remaining + _options.MinimumFreeSpaceReserveBytes;
        if (available.Value < required)
        {
            throw new DownloadEngineException(new JobFailure(
                FailureKind.DiskFull,
                $"Not enough free space at '{directory}': need {required:N0} bytes, " +
                $"{available.Value:N0} available.",
                false, false, DateTimeOffset.UtcNow), isRetryable: false);
        }
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
        return new JobFailure(kind, message, false, resumable, DateTimeOffset.UtcNow, status);
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
            RateLimiter = new TransferRateLimiter(() => job.SpeedLimitBytesPerSecond);
            Total = checkpoint?.TotalBytes ?? job.TotalBytes;
            ETag = checkpoint?.ETag ?? job.ETag;
            LastModified = checkpoint?.LastModified ?? job.LastModified;
            ContentType = checkpoint?.ContentType ?? job.ContentType;
            CheckpointPresent = checkpoint is not null && checkpoint.JobId == job.Id;
            RecordedBytes = CheckpointPresent ? checkpoint!.CompletedBytes : 0;
        }

        public long Bytes { get; set; }
        /// <summary>The bytes known to be good: from the checkpoint this began with, then as each is saved.</summary>
        public long RecordedBytes { get; set; }
        public TransferRateLimiter RateLimiter { get; }
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
            RecordedBytes = 0;
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

    private sealed class HlsProgressState
    {
        public DateTimeOffset LastReport { get; set; } = DateTimeOffset.MinValue;
    }

    private sealed record AttemptResult(bool Complete, bool RestartFromZero);

    private sealed class DownloadEngineException : Exception
    {
        public DownloadEngineException(
            JobFailure failure,
            bool isRetryable,
            TimeSpan? retryAfter = null,
            Exception? inner = null,
            bool discardLocalData = false)
            : base(failure.Message, inner)
        {
            Failure = failure;
            IsRetryable = isRetryable;
            RetryAfter = retryAfter;
            DiscardLocalData = discardLocalData;
        }

        public JobFailure Failure { get; }
        public bool IsRetryable { get; }
        public TimeSpan? RetryAfter { get; }
        public bool DiscardLocalData { get; }
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
