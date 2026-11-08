using System.Diagnostics;
using System.Net;
using DownloadManager.Core;

#pragma warning disable SYSLIB0014

namespace DownloadManager.Engine;

/// <summary>
/// FTP download support using the platform FTP client. It supports passive
/// binary transfers, authenticated site logins, and REST-based resume.
/// </summary>
public sealed class FtpDownloadEngine : IDownloadEngine
{
    private readonly object _loginSync = new();
    private readonly SharedSpeedBudget _sharedSpeed;
    private List<SiteLoginCredential> _siteLogins = new();

    /// <param name="sharedSpeed">
    /// PERF.6: the budget of the shared speed cap. Pass the HTTP engine's <see cref="HttpDownloadEngine.SpeedBudget"/>
    /// so one cap covers HTTP and FTP downloads together. Without one, FTP downloads share a cap of their own.
    /// </param>
    public FtpDownloadEngine(SharedSpeedBudget? sharedSpeed = null) => _sharedSpeed = sharedSpeed ?? new SharedSpeedBudget();

    /// <summary>Updates the cap all transfers share; on a budget shared with the HTTP engine, both see it.</summary>
    public void UpdateSharedSpeedLimit(long? bytesPerSecond) => _sharedSpeed.Update(bytesPerSecond);

    public long SharedSpeedLimit => _sharedSpeed.BytesPerSecond;

    public async Task<DownloadResult> ExecuteAsync(
        DownloadJob job,
        DownloadCheckpoint? checkpoint,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(job.Url, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, "ftp", StringComparison.OrdinalIgnoreCase))
        {
            return DownloadResult.Fail(DownloadState.Failed, new JobFailure(
                FailureKind.InvalidContent, "Not an FTP URL.", false, false, DateTimeOffset.UtcNow));
        }

        var credentials = ResolveCredentials(uri);
        long offset = File.Exists(job.PartFilePath) ? new FileInfo(job.PartFilePath).Length : 0;
        var total = await GetFileSizeAsync(uri, credentials, cancellationToken).ConfigureAwait(false);
        if (total is not null && offset > total.Value)
        {
            DeleteFileQuietly(job.PartFilePath);
            offset = 0;
        }

        if (total is not null && total.Value == offset && offset > 0)
        {
            return Complete(job, offset, total, job.PartFilePath);
        }

        var received = offset;
        // PERF.6: the download's own cap, read live, then the cap every download shares.
        var ownLimit = new TransferRateLimiter(() => job.SpeedLimitBytesPerSecond);
        var stopwatch = Stopwatch.StartNew();
        var lastReport = DateTimeOffset.MinValue;
        var restarted = false;

        while (true)
        {
            FtpWebRequest? request = null;
            try
            {
                request = CreateRequest(uri, credentials, WebRequestMethods.Ftp.DownloadFile);
                if (offset > 0)
                {
                    request.ContentOffset = offset;
                }

                using var response = (FtpWebResponse)await request
                    .GetResponseAsync()
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (offset == 0 && total is null && response.ContentLength >= 0)
                {
                    total = response.ContentLength;
                }

                await using var source = response.GetResponseStream();
                await using (var destination = new FileStream(
                                 job.PartFilePath,
                                 offset > 0 ? FileMode.Open : FileMode.Create,
                                 FileAccess.Write,
                                 FileShare.Read,
                                 81_920,
                                 FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    if (offset > 0)
                    {
                        destination.SetLength(offset);
                        destination.Position = offset;
                    }

                    var buffer = new byte[81_920];
                    while (true)
                    {
                        var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                        if (read == 0)
                        {
                            break;
                        }

                        await ownLimit.WaitAsync(read, cancellationToken).ConfigureAwait(false);
                        await _sharedSpeed.WaitAsync(read, cancellationToken).ConfigureAwait(false);
                        await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                            .ConfigureAwait(false);
                        received += read;
                        var now = DateTimeOffset.UtcNow;
                        if (now - lastReport >= TimeSpan.FromMilliseconds(300))
                        {
                            lastReport = now;
                            var seconds = Math.Max(0.001, stopwatch.Elapsed.TotalSeconds);
                            var speed = (received - offset) / seconds;
                            var remaining = total is { } totalBytes
                                ? TimeSpan.FromSeconds(Math.Max(0, totalBytes - received) / Math.Max(1, speed))
                                : (TimeSpan?)null;
                            progress?.Report(new TransferProgress(
                                received, total, speed, remaining, 1, "Downloading FTP"));
                        }
                    }
                }

                break;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                request?.Abort();
                return DownloadResult.Fail(DownloadState.Cancelled, new JobFailure(
                    FailureKind.Cancelled, "The FTP download was cancelled.", false, true, DateTimeOffset.UtcNow),
                    received, total);
            }
            catch (WebException ex) when (
                !restarted &&
                offset > 0 &&
                IsRestartUnsupported(ex))
            {
                // Some servers reject REST. Restart from zero once.
                DeleteFileQuietly(job.PartFilePath);
                offset = 0;
                received = 0;
                total = await GetFileSizeAsync(uri, credentials, cancellationToken).ConfigureAwait(false);
                restarted = true;
            }
            catch (WebException ex)
            {
                var response = ex.Response as FtpWebResponse;
                var status = response is null ? 0 : (int)response.StatusCode;
                var kind = status switch
                {
                    530 or 532 => FailureKind.Unauthorized,
                    550 => FailureKind.NotFound,
                    _ when ex.Status == WebExceptionStatus.Timeout => FailureKind.Timeout,
                    _ when ex.Status == WebExceptionStatus.TrustFailure => FailureKind.Tls,
                    _ => FailureKind.Network,
                };
                return DownloadResult.Fail(DownloadState.Failed, new JobFailure(
                    kind,
                    status > 0 ? $"FTP server returned {status}: {ex.Message}" : ex.Message,
                    kind is FailureKind.Network or FailureKind.Timeout,
                    kind is FailureKind.Network or FailureKind.Timeout,
                    DateTimeOffset.UtcNow), received, total);
            }
            catch (Exception ex)
            {
                return DownloadResult.Fail(DownloadState.Failed, new JobFailure(
                    FailureKind.Network, ex.Message, true, true, DateTimeOffset.UtcNow), received, total);
            }
        }

        if (total is not null && received != total.Value)
        {
            return DownloadResult.Fail(DownloadState.Failed, new JobFailure(
                FailureKind.InvalidContent,
                $"The FTP transfer ended at {received} of {total.Value} bytes.",
                false,
                true,
                DateTimeOffset.UtcNow), received, total);
        }

        return Complete(job, received, total, job.PartFilePath);
    }

    private DownloadResult Complete(DownloadJob job, long bytes, long? total, string partPath)
    {
        if (!string.IsNullOrWhiteSpace(job.ExpectedSha256))
        {
            var actual = Hashing.Sha256FileAsync(partPath).GetAwaiter().GetResult();
            if (!actual.Equals(job.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                return DownloadResult.Fail(DownloadState.Failed, new JobFailure(
                    FailureKind.VerificationFailed,
                    $"The FTP output SHA-256 did not match (expected {job.ExpectedSha256}, got {actual}).",
                    false,
                    false,
                    DateTimeOffset.UtcNow), bytes, total);
            }
        }

        File.Move(partPath, job.FinalFilePath,
            overwrite: job.ConflictPolicy is ConflictPolicy.Overwrite);
        return DownloadResult.Ok(bytes, total ?? bytes, job.FinalFilePath, null, null, "application/octet-stream");
    }

    private async Task<long?> GetFileSizeAsync(
        Uri uri,
        NetworkCredential credentials,
        CancellationToken cancellationToken)
    {
        try
        {
            var request = CreateRequest(uri, credentials, WebRequestMethods.Ftp.GetFileSize);
            using var response = (FtpWebResponse)await request
                .GetResponseAsync()
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            return response.ContentLength >= 0 ? response.ContentLength : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static FtpWebRequest CreateRequest(
        Uri uri,
        NetworkCredential credentials,
        string method)
    {
        var request = (FtpWebRequest)WebRequest.Create(uri);
        request.Method = method;
        request.Credentials = credentials;
        request.UseBinary = true;
        request.UsePassive = true;
        request.KeepAlive = false;
        request.Proxy = null;
        request.Timeout = 30_000;
        request.ReadWriteTimeout = 60_000;
        return request;
    }

    private NetworkCredential ResolveCredentials(Uri uri)
    {
        if (!string.IsNullOrWhiteSpace(uri.UserInfo))
        {
            var parts = uri.UserInfo.Split(':', 2);
            return new NetworkCredential(
                Uri.UnescapeDataString(parts[0]),
                parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty);
        }

        SiteLoginCredential? login;
        lock (_loginSync)
        {
            login = _siteLogins
                .Where(candidate => SiteMatches(candidate.Site, uri))
                .OrderByDescending(candidate => candidate.Site.Length)
                .FirstOrDefault();
        }

        return login is null
            ? new NetworkCredential("anonymous", "anonymous@example.com")
            : new NetworkCredential(login.Username, login.Password);
    }

    private static bool SiteMatches(string site, Uri uri)
    {
        if (string.IsNullOrWhiteSpace(site))
        {
            return false;
        }

        if (!Uri.TryCreate(site, UriKind.Absolute, out var siteUri))
        {
            siteUri = null;
        }

        if (siteUri is not null)
        {
            if (!string.Equals(siteUri.Host, uri.Host, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!siteUri.IsDefaultPort && siteUri.Port != uri.Port)
            {
                return false;
            }

            var path = siteUri.AbsolutePath.TrimEnd('/');
            return path.Length == 0 ||
                   uri.AbsolutePath.StartsWith(path, StringComparison.OrdinalIgnoreCase);
        }

        var separator = site.IndexOf('/');
        var authority = separator < 0 ? site : site[..separator];
        return string.Equals(uri.Authority, authority, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRestartUnsupported(WebException exception)
    {
        if (exception.Response is not FtpWebResponse response)
        {
            return false;
        }

        return (int)response.StatusCode is 501 or 550;
    }

    public void UpdateProxy(ProxySettings settings)
    {
        // FtpWebRequest does not apply the HTTP proxy engine configuration.
    }

    public void UpdateSiteLogins(IReadOnlyList<SiteLoginCredential> logins)
    {
        lock (_loginSync)
        {
            _siteLogins = logins.ToList();
        }
    }

    public void UpdateFfmpegPath(string? path)
    {
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
            // best effort
        }
    }
}
