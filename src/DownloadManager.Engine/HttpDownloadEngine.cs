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

public sealed class HttpDownloadEngine : IDownloadEngine, IDisposable
{
    private static readonly HashSet<string> RestrictedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Range", "If-Range", "Content-Length", "Transfer-Encoding",
        "Connection", "Proxy-Connection", "Upgrade", "Expect"
    };

    private readonly EngineOptions _options;
    private readonly IDownloadCheckpointStore? _checkpoints;
    private readonly IFreeSpaceProvider _freeSpace;
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

        if (IsHtmlErrorPage(job, response.Content.Headers.ContentType?.MediaType))
        {
            throw new DownloadEngineException(
                new JobFailure(
                    FailureKind.InvalidContent,
                    "The server returned an HTML page instead of the requested file. " +
                    "Rename the target to .html to save it as a page.",
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
            var playlistUri = new Uri(job.Url, UriKind.Absolute);
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
            using var response = await CurrentClient()
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
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
                        FailureKind.InvalidContent,
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
                        FailureKind.InvalidContent,
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
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(_options.ReadTimeout);
                using var response = await CurrentClient()
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
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
                using var response = await CurrentClient()
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
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
            var manifestUri = new Uri(job.Url, UriKind.Absolute);
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
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.ReadTimeout);
        using var response = await CurrentClient()
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
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
                        FailureKind.InvalidContent,
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

        if (offset > 0 || rangeEnd is not null)
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

            File.Move(job.PartFilePath, finalPath,
                overwrite: job.ConflictPolicy is ConflictPolicy.Overwrite);
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

    private static bool IsHtmlErrorPage(DownloadJob job, string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType) ||
            (!mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase) &&
             !mediaType.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var extension = Path.GetExtension(job.FileName).TrimStart('.').ToLowerInvariant();
        return extension is not ("html" or "htm" or "xhtml" or "txt" or "xml" or "json" or "mhtml" or "mht");
    }

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

    private sealed class HlsProgressState
    {
        public DateTimeOffset LastReport { get; set; } = DateTimeOffset.MinValue;
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
