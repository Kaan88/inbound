using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Text.Json.Nodes;
using DownloadManager.Contracts;
using DownloadManager.Sites;

namespace DownloadManager.Runtime.Sites;

/// <summary>
/// SITES.7: "to this device" and "from this device" for the web UI (remote-sites.md, "Web UI"). A file goes between a
/// site and the browser in pieces of <see cref="SiteDeviceLimits.ChunkBytes"/> over <c>sites.device.*</c>, so the same
/// messages work in the server's own process and through the Windows app's pipe (the bridge). rclone reads the file
/// (<c>--rc-serve</c>) or takes it (<c>operations/uploadfile</c>) as a stream: nothing is written to a disk on the way.
/// A stream holds an rclone lease while it is open, and one unused for <see cref="StreamIdle"/> is closed.
/// </summary>
public sealed partial class SitesCommands
{
    internal static TimeSpan StreamIdle { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>The most one piece may wait on rclone; a stalled server ends the stream instead of holding it.</summary>
    internal static TimeSpan PieceTime { get; set; } = TimeSpan.FromMinutes(2);

    private readonly ConcurrentDictionary<string, DeviceStream> _streams = new(StringComparer.Ordinal);
    private readonly bool _allowWebDavUploadProbe;
    private const long MaxUploadProbeBytes = 64 * 1024;
    private Timer? _streamSweep;
    private volatile bool _disposing;

    private sealed record ConditionalCreate(SiteRecord Site, Uri? DavTarget, string Folder, string Name, MemoryStream Stage, int Session);
    private int _uploadProbeSession;

    private sealed class DeviceStream(string siteId, RcloneLease? lease = null) : IAsyncDisposable
    {
        public string SiteId { get; } = siteId;
        public ConditionalCreate? Create { get; init; }
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public long LastUsed = Environment.TickCount64;

        // Read
        public HttpResponseMessage? Response { get; init; }
        public Stream? Body { get; set; }

        // Write
        public Pipe? Pipe { get; init; }
        public Task? Upload { get; set; }
        public long Expected { get; init; }
        public long Written { get; set; }
        public CancellationTokenSource Stop { get; } = new();

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Stop.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
            }

            if (Pipe is not null) await Pipe.Writer.CompleteAsync(new OperationCanceledException("closed")).ConfigureAwait(false);
            if (Upload is not null)
            {
                try
                {
                    await Upload.ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Cancelled on purpose; the upload's own answer was given or is not wanted.
                }
            }

            if (Body is not null) await Body.DisposeAsync().ConfigureAwait(false);
            Response?.Dispose();
            if (Create is not null) await Create.Stage.DisposeAsync().ConfigureAwait(false);
            lease?.Dispose();
            Stop.Dispose();
        }
    }

    private async Task<SiteDeviceStreamDto> DeviceCreateOpenAsync(SiteDeviceOpenRequest request, CancellationToken cancellationToken)
    {
        if (!_allowWebDavUploadProbe)
            return new SiteDeviceStreamDto { Message = "This Host does not support uploads that guarantee no replacement." };
        int session = Volatile.Read(ref _uploadProbeSession);
        if (_disposing || !_config.Enabled || request.Replace || request.Size < 0 || request.Size > MaxUploadProbeBytes
            || !SiteStore.IsId(request.Id) || SitePaths.Normalize(request.Folder) is not { } folder
            || request.Name is not { } name || await _sites.GetAsync(request.Id, cancellationToken).ConfigureAwait(false) is not { } site)
            return new SiteDeviceStreamDto { Message = "The upload destination or file size cannot be used." };
        if (CachedListing(site.Id, folder, request.Listing) is not { } listing || listing.SiteRevision != site.Revision)
            return new SiteDeviceStreamDto { Problem = SiteProblems.ListingChanged, Message = "Refresh the destination folder and confirm it again." };
        bool s3Fixture = site.Kind == SiteKinds.S3 && site.Provider == "Other" && folder != "/"
            && SitePaths.NameProblem(name) is null && !name.Contains('\\')
            && Uri.TryCreate(site.Endpoint, UriKind.Absolute, out var endpoint)
            && endpoint.Scheme == Uri.UriSchemeHttp && endpoint.Host == "127.0.0.1";
        if (!WebDavConditionalUpload.TryTarget(site, folder, name, out var target) && !s3Fixture)
            return new SiteDeviceStreamDto { Message = "This location does not support uploads without replacing an existing file." };

        var id = Guid.NewGuid().ToString("N");
        var stage = new MemoryStream((int)request.Size);
        _streams[id] = new DeviceStream(site.Id) { Create = new ConditionalCreate(site, target, folder, name, stage, session), Expected = request.Size };
        if (_streams.Count > SiteDeviceLimits.MostStreams || _disposing || !_config.Enabled)
        {
            await CloseStreamAsync(id).ConfigureAwait(false);
            return new SiteDeviceStreamDto { Message = "No upload slot is available." };
        }
        StartStreamSweep();
        return new SiteDeviceStreamDto { Ok = true, StreamId = id, Name = name, Size = request.Size };
    }

    private async Task<SiteDeviceStreamDto> DeviceOpenAsync(SiteDeviceOpenRequest request, bool write, CancellationToken cancellationToken)
    {
        if (_disposing) return new SiteDeviceStreamDto { Message = "Inbound is closing." };
        if (!SiteStore.IsId(request.Id) || await _sites.GetAsync(request.Id, cancellationToken).ConfigureAwait(false) is not { } site)
            return new SiteDeviceStreamDto { Message = "That site no longer exists." };
        if (_streams.Count >= SiteDeviceLimits.MostStreams)
            return new SiteDeviceStreamDto { Message = "Too many files are being sent to or from devices at once. Try again when one has finished." };

        string? remotePath, folder = null, name = null;
        if (write)
        {
            folder = SitePaths.Normalize(request.Folder ?? "/");
            name = request.Name;
            if (folder is null) return new SiteDeviceStreamDto { Message = "That folder can't be used." };
            if (SitePaths.NameProblem(name) is { } problem) return new SiteDeviceStreamDto { Message = problem };
            if (request.Size < 0) return new SiteDeviceStreamDto { Message = "The file's size is missing." };
            remotePath = folder == "/" ? "/" + name : folder + "/" + name;
        }
        else
        {
            remotePath = SitePaths.Normalize(request.Path);
            if (remotePath is null or "/") return new SiteDeviceStreamDto { Message = "That file can't be read." };
            name = remotePath[(remotePath.LastIndexOf('/') + 1)..];
        }

        RcloneLease lease;
        try
        {
            lease = await AcquireAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (RcloneUnavailableException e)
        {
            return new SiteDeviceStreamDto { Message = e.Message };
        }

        DeviceStream? stream = null;
        try
        {
            var remote = await SiteRemote.NamedAsync(site, lease.Client, cancellationToken).ConfigureAwait(false);
            if (write)
            {
                if (!request.Replace && await ExistsAsync(lease.Client, remote, remotePath, cancellationToken).ConfigureAwait(false))
                {
                    lease.Dispose();
                    return new SiteDeviceStreamDto { Problem = SiteProblems.Exists, Message = $"{name} is already in that folder.", Name = name };
                }

                var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: SiteDeviceLimits.ChunkBytes * 4L, resumeWriterThreshold: SiteDeviceLimits.ChunkBytes * 2L));
                stream = new DeviceStream(site.Id, lease) { Pipe = pipe, Expected = request.Size };
                var upload = pipe.Reader.AsStream();
                var stop = stream.Stop.Token;
                stream.Upload = Task.Run(() => lease.Client.UploadFileAsync(remote, folder!, name!, upload, stop), CancellationToken.None);
                // An upload that ends early (the server refused it) completes the reader, so a piece waiting for room in
                // the pipe returns at once instead of waiting forever.
                _ = stream.Upload.ContinueWith(ended => pipe.Reader.Complete(ended.Exception?.InnerException), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            else
            {
                var response = await lease.Client.OpenFileAsync(remote, remotePath, cancellationToken).ConfigureAwait(false);
                stream = new DeviceStream(site.Id, lease) { Response = response };
                stream.Body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception e)
        {
            // Every failure gives the lease back: a stream not yet listed is never swept.
            if (stream is not null) await stream.DisposeAsync().ConfigureAwait(false);
            else lease.Dispose();
            if (e is OperationCanceledException) throw;
            if (e is not (RcloneCallException or RcloneUnavailableException or SiteStoreException or HttpRequestException or IOException)) throw;
            return new SiteDeviceStreamDto
            {
                Problem = e is SiteSignInNeededException ? SiteProblems.SignInNeeded
                    : SiteErrorText.IsUntrustedCertificate(e.Message) ? SiteProblems.CertificateNotTrusted : null,
                Message = e is HttpRequestException or IOException ? "The connection to rclone was lost. Try again." : SiteErrorText.Friendly(e.Message),
            };
        }

        var id = Guid.NewGuid().ToString("N");
        _streams[id] = stream;
        // Two opens can pass the count above at once, and one can finish after shutdown began: the later one gives way.
        if (_streams.Count > SiteDeviceLimits.MostStreams || _disposing)
        {
            await CloseStreamAsync(id).ConfigureAwait(false);
            return new SiteDeviceStreamDto { Message = _disposing ? "Inbound is closing." : "Too many files are being sent to or from devices at once. Try again when one has finished." };
        }

        StartStreamSweep();
        return new SiteDeviceStreamDto
        {
            Ok = true, StreamId = id, Name = name,
            Size = write ? request.Size : stream.Response!.Content.Headers.ContentLength ?? -1,
        };
    }

    private static async Task<bool> ExistsAsync(RcloneClient client, string remote, string path, CancellationToken cancellationToken)
    {
        var stat = await client.CallAsync("operations/stat", new JsonObject
        {
            ["fs"] = remote + ":/",
            ["remote"] = SitePaths.ToRemote(path),
        }, cancellationToken).ConfigureAwait(false);
        return stat["item"] is JsonObject;
    }

    private async Task<SiteDeviceChunkDto> DeviceReadAsync(SiteDeviceChunkRequest request, CancellationToken cancellationToken)
    {
        if (!_streams.TryGetValue(request.StreamId, out var stream) || stream.Body is null)
            return new SiteDeviceChunkDto { Message = "That transfer is no longer open." };
        await stream.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Closed while this waited: its token source is gone too.
            if (!_streams.ContainsKey(request.StreamId)) return new SiteDeviceChunkDto { Message = "That transfer is no longer open." };
            using var piece = PieceToken(stream, cancellationToken);
            stream.LastUsed = Environment.TickCount64;
            var buffer = new byte[SiteDeviceLimits.ChunkBytes];
            var filled = 0;
            int read;
            // A whole piece unless the file ends: fewer round trips for the caller.
            while (filled < buffer.Length && (read = await stream.Body.ReadAsync(buffer.AsMemory(filled), piece.Token).ConfigureAwait(false)) > 0)
                filled += read;
            var done = filled < buffer.Length;
            stream.LastUsed = Environment.TickCount64;
            if (done) await CloseStreamAsync(request.StreamId).ConfigureAwait(false);
            return new SiteDeviceChunkDto { Ok = true, Data = filled == buffer.Length ? buffer : buffer[..filled], Done = done };
        }
        catch (Exception e) when (e is IOException or HttpRequestException or ObjectDisposedException
                                      || (e is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // Closed while the piece was read, rclone stalled past PieceTime, or the server cut the file off.
            await CloseStreamAsync(request.StreamId).ConfigureAwait(false);
            return new SiteDeviceChunkDto { Message = "The server stopped sending the file." };
        }
        catch (OperationCanceledException)
        {
            await CloseStreamAsync(request.StreamId).ConfigureAwait(false);
            throw;
        }
        finally
        {
            stream.Gate.Release();
        }
    }

    private async Task<SiteDeviceChunkDto> DeviceWriteAsync(SiteDeviceChunkRequest request, CancellationToken cancellationToken)
    {
        if (!_streams.TryGetValue(request.StreamId, out var stream) || (stream.Pipe is null && stream.Create is null))
            return new SiteDeviceChunkDto { Message = "That transfer is no longer open." };
        await stream.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var finished = false;
        try
        {
            if (!_streams.ContainsKey(request.StreamId)) return new SiteDeviceChunkDto { Message = "That transfer is no longer open." };
            using var piece = PieceToken(stream, cancellationToken);
            stream.LastUsed = Environment.TickCount64;
            var data = request.Data ?? [];
            if (data.Length > SiteDeviceLimits.ChunkBytes || stream.Written + data.Length > stream.Expected)
            {
                await CloseStreamAsync(request.StreamId).ConfigureAwait(false);
                return new SiteDeviceChunkDto { Message = "The file is larger than it said, so the upload was stopped." };
            }
            if (stream.Create is { } create)
            {
                try
                {
                    await create.Stage.WriteAsync(data.AsMemory(), piece.Token).ConfigureAwait(false);
                    stream.Written += data.Length;
                    if (!request.Last)
                    {
                        finished = true;
                        return new SiteDeviceChunkDto { Ok = true };
                    }
                    if (stream.Written != stream.Expected)
                        return new SiteDeviceChunkDto { Message = "The file ended before its declared size." };
                    if (!_config.Enabled || create.Session != Volatile.Read(ref _uploadProbeSession))
                        return new SiteDeviceChunkDto { Message = "The destination changed. Refresh the folder and confirm it again." };
                    using var s3Lease = create.DavTarget is null ? await AcquireAsync(piece.Token).ConfigureAwait(false) : null;
                    await _gate.WaitAsync(piece.Token).ConfigureAwait(false);
                    try
                    {
                        if (!_config.Enabled || create.Session != _uploadProbeSession
                            || await _sites.GetAsync(stream.SiteId, piece.Token).ConfigureAwait(false) is not { } current
                            || current.Revision != create.Site.Revision)
                            return new SiteDeviceChunkDto { Message = "The destination changed. Refresh the folder and confirm it again." };
                        create.Stage.Position = 0;
                        try
                        {
                            if (create.DavTarget is { } dav)
                                await WebDavConditionalUpload.SendAsync(create.Site, dav, create.Stage, stream.Expected, piece.Token).ConfigureAwait(false);
                            else
                                await SiteRemote.CreateS3FileAsync(create.Site, s3Lease!.Client, create.Folder, create.Name, create.Stage, piece.Token).ConfigureAwait(false);
                        }
                        finally { ForgetListings(stream.SiteId); } // The endpoint may have accepted a PUT whose response was lost.
                    }
                    finally { _gate.Release(); }
                    return new SiteDeviceChunkDto { Ok = true, Done = true };
                }
                catch (IOException e) when (e is WebDavConditionalUpload.PublicationException)
                {
                    return new SiteDeviceChunkDto { Message = e.Message };
                }
                catch (RcloneCallException e) when (e.Message.Contains("StatusCode: 412", StringComparison.Ordinal)
                    && e.Message.Contains("PreconditionFailed", StringComparison.Ordinal))
                {
                    return new SiteDeviceChunkDto { Message = "A file already exists at the destination. It was not replaced." };
                }
                catch (Exception e) when (e is IOException or HttpRequestException or SiteStoreException or RcloneCallException or RcloneUnavailableException)
                {
                    return new SiteDeviceChunkDto { Message = "The Host could not confirm that the upload created a new file." };
                }
            }

            if (stream.Upload is { IsCompleted: true } ended)
            {
                await CloseStreamAsync(request.StreamId).ConfigureAwait(false);
                return new SiteDeviceChunkDto { Message = UploadFailure(ended) };
            }

            if (data.Length > 0)
            {
                var flushed = await stream.Pipe!.Writer.WriteAsync(data, piece.Token).ConfigureAwait(false);
                stream.Written += data.Length;
                if (flushed.IsCompleted)
                {
                    // The upload ended while this piece waited for room: its own reason, and the stream is gone.
                    var reason = stream.Upload is { IsCompleted: true } stopped ? UploadFailure(stopped) : "The server stopped taking the file.";
                    await CloseStreamAsync(request.StreamId).ConfigureAwait(false);
                    return new SiteDeviceChunkDto { Message = reason };
                }
            }

            if (!request.Last)
            {
                finished = true;
                stream.LastUsed = Environment.TickCount64;
                return new SiteDeviceChunkDto { Ok = true };
            }

            if (stream.Written != stream.Expected)
            {
                await CloseStreamAsync(request.StreamId).ConfigureAwait(false);
                return new SiteDeviceChunkDto { Message = "The file ended before its size, so the upload was stopped." };
            }

            await stream.Pipe!.Writer.CompleteAsync().ConfigureAwait(false);
            string? failure = null;
            try
            {
                // No PieceTime here: the server may take a while to store a large file once it has all of it.
                await stream.Upload!.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is RcloneCallException or RcloneUnavailableException or HttpRequestException or IOException)
            {
                failure = e is RcloneCallException or RcloneUnavailableException ? SiteErrorText.Friendly(e.Message) : "The connection to rclone was lost.";
            }

            await CloseStreamAsync(request.StreamId).ConfigureAwait(false);
            if (failure is not null) return new SiteDeviceChunkDto { Message = failure };
            ForgetListings(stream.SiteId);
            return new SiteDeviceChunkDto { Ok = true, Done = true };
        }
        catch (Exception e) when (e is ObjectDisposedException or InvalidOperationException
                                      || (e is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // Closed while this piece moved, or rclone took no bytes for PieceTime.
            return new SiteDeviceChunkDto { Message = "The server stopped taking the file." };
        }
        finally
        {
            // Anything but a piece taken (or an answer that already closed it) ends the stream: an aborted request
            // does not hold a slot and an rclone lease until the sweep.
            if (!finished) await CloseStreamAsync(request.StreamId).ConfigureAwait(false);
            stream.Gate.Release();
        }
    }

    private static string UploadFailure(Task upload) => upload.Exception?.InnerException is { } e
        ? SiteErrorText.Friendly(e.Message)
        : "The server stopped taking the file.";

    private static CancellationTokenSource PieceToken(DeviceStream stream, CancellationToken cancellationToken)
    {
        var piece = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stream.Stop.Token);
        piece.CancelAfter(PieceTime);
        return piece;
    }

    private async Task<SiteResultDto> DeviceCloseAsync(string streamId)
    {
        await CloseFromOutsideAsync(streamId).ConfigureAwait(false);
        return new SiteResultDto { Ok = true };
    }

    /// <summary>Ends a stream when the caller holds its gate (or no piece can be moving).</summary>
    private async Task CloseStreamAsync(string streamId)
    {
        if (_streams.TryRemove(streamId, out var stream)) await stream.DisposeAsync().ConfigureAwait(false);
        if (_streams.IsEmpty) StopStreamSweep();
    }

    /// <summary>
    /// Ends a stream from outside a piece: a piece in flight is cancelled first and finishes (it sees the stream gone),
    /// so nothing it reads or writes is disposed under it.
    /// </summary>
    private async Task CloseFromOutsideAsync(string streamId)
    {
        if (!_streams.TryRemove(streamId, out var stream)) return;
        try
        {
            await stream.Stop.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }

        var gated = await stream.Gate.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        try
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            if (gated) stream.Gate.Release();
        }

        if (_streams.IsEmpty) StopStreamSweep();
    }

    private void StartStreamSweep()
    {
        if (_streamSweep is not null) return;
        var sweep = new Timer(_ => _ = SweepStreamsAsync(), null, StreamIdle, StreamIdle);
        if (Interlocked.CompareExchange(ref _streamSweep, sweep, null) is not null) sweep.Dispose();
    }

    /// <summary>No timer while no stream is open (idle wake-ups); the next open starts it again.</summary>
    private void StopStreamSweep()
    {
        Interlocked.Exchange(ref _streamSweep, null)?.Dispose();
        // A stream opened while the timer was stopped starts its own; one listed in between gets a new timer here.
        if (!_streams.IsEmpty && !_disposing) StartStreamSweep();
    }

    internal bool StreamSweepRunning => _streamSweep is not null;

    /// <summary>A browser that went away leaves its stream: closed after <see cref="StreamIdle"/>.</summary>
    internal async Task SweepStreamsAsync()
    {
        var now = Environment.TickCount64;
        foreach (var (id, stream) in _streams)
        {
            if (now - Interlocked.Read(ref stream.LastUsed) < StreamIdle.TotalMilliseconds) continue;
            if (!stream.Gate.Wait(0)) continue; // a piece is moving: not idle
            try
            {
                await CloseStreamAsync(id).ConfigureAwait(false);
            }
            finally
            {
                stream.Gate.Release();
            }
        }
    }

    private async Task CloseAllStreamsAsync()
    {
        _disposing = true;
        Interlocked.Exchange(ref _streamSweep, null)?.Dispose();
        foreach (var id in _streams.Keys) await CloseFromOutsideAsync(id).ConfigureAwait(false);
    }
}
