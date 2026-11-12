using System.Diagnostics;
using System.Globalization;
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
    /// <summary>How often the part file is flushed to disk and the checkpoint saved, as for HTTP.</summary>
    private static readonly TimeSpan CheckpointInterval = TimeSpan.FromSeconds(5);
    private const long CheckpointEveryBytes = 8L * 1024 * 1024;

    private readonly object _loginSync = new();
    private readonly SharedSpeedBudget _sharedSpeed;
    private readonly IDownloadCheckpointStore? _checkpoints;
    private readonly TimeSpan _idleTimeout;
    private List<SiteLoginCredential> _siteLogins = new();

    /// <param name="sharedSpeed">
    /// PERF.6: the budget of the shared speed cap. Pass the HTTP engine's <see cref="HttpDownloadEngine.SpeedBudget"/>
    /// so one cap covers HTTP and FTP downloads together. Without one, FTP downloads share a cap of their own.
    /// </param>
    /// <param name="checkpoints">
    /// Where the count of durable bytes is kept. With it, a resume starts from what was flushed to disk, not from the
    /// part file's length, which after a crash can include bytes that read back as zeros. Pass the HTTP engine's store.
    /// </param>
    /// <param name="idleTimeout">How long a data connection may send nothing before the transfer is given up (60 s).</param>
    public FtpDownloadEngine(
        SharedSpeedBudget? sharedSpeed = null,
        IDownloadCheckpointStore? checkpoints = null,
        TimeSpan? idleTimeout = null)
    {
        _sharedSpeed = sharedSpeed ?? new SharedSpeedBudget();
        _checkpoints = checkpoints;
        _idleTimeout = idleTimeout is { } idle && idle > TimeSpan.Zero ? idle : TimeSpan.FromSeconds(60);
    }

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

        // The platform FTP client opens its data connection wherever the server's PASV answer says, which could be any
        // address on this PC's network, so a download that must stay off it doesn't use FTP at all. The Host refuses
        // such an address when it is added; this is the engine's own wall.
        if (job.PublicNetworkOnly)
        {
            return DownloadResult.Fail(DownloadState.Failed, new JobFailure(
                FailureKind.Forbidden, "FTP downloads added from another device are not supported.", false, false, DateTimeOffset.UtcNow));
        }

        var credentials = ResolveCredentials(uri);
        var total = await GetFileSizeAsync(uri, credentials, cancellationToken).ConfigureAwait(false);
        var lastModified = await GetLastModifiedAsync(uri, credentials, cancellationToken).ConfigureAwait(false);
        long offset = ResumeOffset(job, checkpoint, total, lastModified);
        if (offset == 0)
        {
            DeleteFileQuietly(job.PartFilePath);
        }

        if (total is not null && total.Value == offset && offset > 0)
        {
            return await CompleteAsync(job, offset, total, lastModified, cancellationToken).ConfigureAwait(false);
        }

        var received = offset;
        var durable = offset;
        // PERF.6: the download's own cap, read live, then the cap every download shares.
        var ownLimit = new TransferRateLimiter(() => job.SpeedLimitBytesPerSecond);
        var stopwatch = Stopwatch.StartNew();
        var lastReport = DateTimeOffset.MinValue;
        var restarted = false;
        // After a refused REST, what the part still holds until the from-zero transfer has really begun.
        long kept = 0;
        long Reported() => received == 0 ? kept : received;

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

                // The checkpoint names what the part holds before a byte of this transfer lands in it. Only now that
                // the server is sending: after a refused REST, a from-zero request that fails too must not cost the
                // bytes kept so far (the reply can't say which command it refused, so a 500 may be anything).
                await SaveCheckpointAsync(job, offset, total, lastModified).ConfigureAwait(false);
                kept = 0;
                if (offset == 0 && total is null && response.ContentLength >= 0)
                {
                    total = response.ContentLength;
                }

                if (offset > 0)
                {
                    // The server took REST: a later stop (a lowered limit's requeue among them) keeps the data.
                    job.ResumeSupported = true;
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
                    var lastCheckpointAt = DateTimeOffset.UtcNow;
                    while (true)
                    {
                        int read;
                        try
                        {
                            // ReadWriteTimeout covers synchronous reads only; a stalled data connection would
                            // otherwise wait for ever.
                            read = await source.ReadAsync(buffer, cancellationToken).AsTask()
                                .WaitAsync(_idleTimeout, cancellationToken).ConfigureAwait(false);
                        }
                        catch (TimeoutException)
                        {
                            request.Abort();
                            await FlushToDiskAsync(destination).ConfigureAwait(false);
                            durable = received;
                            await SaveCheckpointAsync(job, durable, total, lastModified).ConfigureAwait(false);
                            return DownloadResult.Fail(DownloadState.Failed, new JobFailure(
                                FailureKind.Timeout, "The FTP data connection stalled.", true, true, DateTimeOffset.UtcNow),
                                received, total);
                        }

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
                        if (now - lastCheckpointAt >= CheckpointInterval || received - durable >= CheckpointEveryBytes)
                        {
                            // Flush first, then count: the checkpoint never names bytes the disk may not have.
                            await FlushToDiskAsync(destination).ConfigureAwait(false);
                            durable = received;
                            await SaveCheckpointAsync(job, durable, total, lastModified).ConfigureAwait(false);
                            lastCheckpointAt = now;
                        }

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

                    await FlushToDiskAsync(destination).ConfigureAwait(false);
                    durable = received;
                    await SaveCheckpointAsync(job, durable, total, lastModified).ConfigureAwait(false);
                }

                break;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                request?.Abort();
                durable = await SaveStoppedCheckpointAsync(job, received, durable, total, lastModified).ConfigureAwait(false);
                return DownloadResult.Fail(DownloadState.Cancelled, new JobFailure(
                    FailureKind.Cancelled, "The FTP download was cancelled.", false, true, DateTimeOffset.UtcNow),
                    Reported(), total);
            }
            catch (WebException ex) when (
                !restarted &&
                offset > 0 &&
                IsRestartUnsupported(ex))
            {
                // Some servers reject REST. Restart from zero once; the part is truncated only once that transfer is
                // underway, and the checkpoint still names its bytes until then.
                job.ResumeSupported = false;
                kept = durable;
                offset = 0;
                received = 0;
                durable = 0;
                total = await GetFileSizeAsync(uri, credentials, cancellationToken).ConfigureAwait(false);
                restarted = true;
            }
            catch (WebException ex)
            {
                await SaveStoppedCheckpointAsync(job, received, durable, total, lastModified).ConfigureAwait(false);
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
                    DateTimeOffset.UtcNow), Reported(), total);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && IsLocalFileFailure(ex))
            {
                return DownloadResult.Fail(DownloadState.Failed, LocalFileFailure(ex), Reported(), total);
            }
            catch (Exception ex)
            {
                await SaveStoppedCheckpointAsync(job, received, durable, total, lastModified).ConfigureAwait(false);
                return DownloadResult.Fail(DownloadState.Failed, new JobFailure(
                    FailureKind.Network, ex.Message, true, true, DateTimeOffset.UtcNow), Reported(), total);
            }
        }

        if (total is not null && received > total.Value)
        {
            return DownloadResult.Fail(DownloadState.Failed, new JobFailure(
                FailureKind.InvalidContent,
                $"The FTP transfer sent {received} bytes for a {total.Value}-byte file.",
                false,
                false,
                DateTimeOffset.UtcNow), received, total);
        }

        if (total is not null && received < total.Value)
        {
            // The connection ended early. What arrived is flushed and counted, so a retry continues from it.
            return DownloadResult.Fail(DownloadState.Failed, new JobFailure(
                FailureKind.Network,
                $"The FTP transfer ended at {received} of {total.Value} bytes.",
                true,
                true,
                DateTimeOffset.UtcNow), received, total);
        }

        return await CompleteAsync(job, received, total, lastModified, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Where this run continues from: the bytes the checkpoint counts (and the part holds), provided the remote file
    /// still has the size and modification time it had then. A part with no checkpoint at all comes from a version
    /// that kept none (or a direct caller without a store) and is continued from its length, as before.
    /// </summary>
    private long ResumeOffset(DownloadJob job, DownloadCheckpoint? checkpoint, long? total, string? lastModified)
    {
        if (!File.Exists(job.PartFilePath)) return 0;
        var length = new FileInfo(job.PartFilePath).Length;
        long offset;
        if (checkpoint is not null && checkpoint.JobId == job.Id && checkpoint.Segments.Count <= 1)
        {
            if (checkpoint.TotalBytes is > 0 && total is > 0 && checkpoint.TotalBytes != total)
            {
                return 0; // the remote file was replaced by one of another size
            }

            if (!string.IsNullOrWhiteSpace(checkpoint.LastModified) && !string.IsNullOrWhiteSpace(lastModified) &&
                !string.Equals(checkpoint.LastModified, lastModified, StringComparison.Ordinal))
            {
                return 0; // the remote file was replaced since these bytes came
            }

            offset = Math.Min(length, Math.Max(0, checkpoint.CompletedBytes));
        }
        else if (checkpoint is not null)
        {
            return 0; // not this job's, or a segmented layout FTP never writes
        }
        else
        {
            offset = length;
        }

        return total is not null && offset > total.Value ? 0 : offset;
    }

    private async Task<DownloadResult> CompleteAsync(
        DownloadJob job, long bytes, long? total, string? lastModified, CancellationToken cancellationToken)
    {
        string finalPath;
        try
        {
            var part = new FileInfo(job.PartFilePath);
            if (!part.Exists)
            {
                return DownloadResult.Fail(DownloadState.Failed, new JobFailure(
                    FailureKind.DestinationUnavailable, "The partial file disappeared before completion.",
                    false, false, DateTimeOffset.UtcNow), bytes, total);
            }

            if (part.Length != bytes)
            {
                // A part longer than what was counted holds bytes nobody vouches for; drop them first.
                await using (var stream = new FileStream(job.PartFilePath, FileMode.Open, FileAccess.Write, FileShare.Read))
                {
                    if (stream.Length < bytes)
                    {
                        return DownloadResult.Fail(DownloadState.Failed, new JobFailure(
                            FailureKind.InvalidContent, "The partial file is shorter than its checkpoint.",
                            false, false, DateTimeOffset.UtcNow), bytes, total);
                    }

                    stream.SetLength(bytes);
                }
            }

            if (!string.IsNullOrWhiteSpace(job.ExpectedSha256))
            {
                var actual = await Hashing.Sha256FileAsync(job.PartFilePath, cancellationToken).ConfigureAwait(false);
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

            // The same clash rules as the HTTP finalizer.
            var directory = string.IsNullOrWhiteSpace(job.DestinationDirectory)
                ? Path.GetDirectoryName(job.PartFilePath) ?? AppSettings.GetDefaultDownloadDirectory()
                : job.DestinationDirectory;
            finalPath = string.IsNullOrWhiteSpace(job.FinalFilePath)
                ? FileNameResolver.ResolveUniquePath(directory, job.FileName, job.ConflictPolicy)
                : job.FinalFilePath;
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
                    return DownloadResult.Fail(DownloadState.Failed, new JobFailure(
                        FailureKind.DestinationUnavailable,
                        $"The destination file already exists: {finalPath}",
                        false, false, DateTimeOffset.UtcNow), bytes, total);
                }
            }

            await SaveCheckpointAsync(job, bytes, total, lastModified, finalPath).ConfigureAwait(false);
            File.Move(job.PartFilePath, finalPath, overwrite: job.ConflictPolicy is ConflictPolicy.Overwrite);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return DownloadResult.Fail(DownloadState.Cancelled, new JobFailure(
                FailureKind.Cancelled, "The FTP download was cancelled.", false, true, DateTimeOffset.UtcNow),
                bytes, total);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DownloadResult.Fail(DownloadState.Failed, LocalFileFailure(ex), bytes, total);
        }

        if (_checkpoints is not null && !job.KeepCheckpointAfterFinalize)
        {
            try
            {
                await _checkpoints.DeleteAsync(job.Id, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A stale checkpoint is harmless; the part file is gone.
            }
        }

        return DownloadResult.Ok(bytes, total ?? bytes, finalPath, null, lastModified, "application/octet-stream");
    }

    private async Task SaveCheckpointAsync(
        DownloadJob job, long bytes, long? total, string? lastModified, string? finalizingTo = null)
    {
        if (_checkpoints is null) return;
        var checkpoint = new DownloadCheckpoint
        {
            JobId = job.Id,
            Url = job.Url,
            LastModified = lastModified,
            TotalBytes = total,
            ContentType = "application/octet-stream",
            UpdatedAt = DateTimeOffset.UtcNow,
            FinalizingTo = finalizingTo,
        };
        checkpoint.Segments.Add(new SegmentCheckpoint
        {
            Start = 0,
            End = total is > 0 ? total.Value - 1 : long.MaxValue - 1,
            Completed = bytes,
        });

        try
        {
            await _checkpoints.SaveAsync(checkpoint, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A stale checkpoint only costs resume precision: it counts fewer bytes than the part holds.
        }
    }

    /// <summary>After the part was closed by an interruption: flush what the OS holds, then count it.</summary>
    private async Task<long> SaveStoppedCheckpointAsync(
        DownloadJob job, long received, long durable, long? total, string? lastModified)
    {
        if (received == durable || _checkpoints is null) return durable;
        try
        {
            await Task.Run(() =>
            {
                using var part = File.OpenHandle(job.PartFilePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                if (RandomAccess.GetLength(part) < received) throw new IOException("The part file is shorter than the transfer.");
                RandomAccess.FlushToDisk(part);
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return durable; // these bytes aren't known to be on disk, so the earlier checkpoint stands
        }

        await SaveCheckpointAsync(job, received, total, lastModified).ConfigureAwait(false);
        return received;
    }

    private static async Task FlushToDiskAsync(FileStream stream)
    {
        await stream.FlushAsync().ConfigureAwait(false);
        await Task.Run(() => stream.Flush(flushToDisk: true)).ConfigureAwait(false);
    }

    /// <summary>A failure writing this PC's own file, which no retry against the server fixes.</summary>
    private static bool IsLocalFileFailure(Exception ex)
        => ex is UnauthorizedAccessException ||
           (ex is IOException io && (io.HResult & 0xFFFF) is 0x70 or 0x27 or 0x05 or 0x20 or 0x21);

    private static JobFailure LocalFileFailure(Exception ex)
    {
        var kind = ex switch
        {
            UnauthorizedAccessException => FailureKind.Permission,
            IOException io => (io.HResult & 0xFFFF) switch
            {
                0x70 or 0x27 => FailureKind.DiskFull,
                0x05 => FailureKind.Permission,
                _ => FailureKind.DestinationUnavailable,
            },
            _ => FailureKind.Unknown,
        };
        return new JobFailure(kind, ex.Message, false, true, DateTimeOffset.UtcNow);
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

    /// <summary>The remote file's MDTM time, to tell a replaced file from the one a part was fetched from.</summary>
    private async Task<string?> GetLastModifiedAsync(
        Uri uri,
        NetworkCredential credentials,
        CancellationToken cancellationToken)
    {
        try
        {
            var request = CreateRequest(uri, credentials, WebRequestMethods.Ftp.GetDateTimestamp);
            using var response = (FtpWebResponse)await request
                .GetResponseAsync()
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            return response.LastModified == default
                ? null
                : response.LastModified.ToUniversalTime().ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
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
        => SiteLoginMatcher.Matches(site, uri, allowSubdomains: false);

    private static bool IsRestartUnsupported(WebException exception)
    {
        if (exception.Response is not FtpWebResponse response)
        {
            return false;
        }

        // 500/502/504: REST is not understood or not implemented; 501/550: the offset is refused.
        return (int)response.StatusCode is 500 or 501 or 502 or 504 or 550;
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
