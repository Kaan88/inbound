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

    private readonly ConcurrentDictionary<string, DeviceStream> _streams = new(StringComparer.Ordinal);
    private Timer? _streamSweep;

    private sealed class DeviceStream(string siteId, RcloneLease lease) : IAsyncDisposable
    {
        public string SiteId { get; } = siteId;
        public RcloneLease Lease { get; } = lease;
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
            Lease.Dispose();
            Stop.Dispose();
        }
    }

    private async Task<SiteDeviceStreamDto> DeviceOpenAsync(SiteDeviceOpenRequest request, bool write, CancellationToken cancellationToken)
    {
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
            }
            else
            {
                var response = await lease.Client.OpenFileAsync(remote, remotePath, cancellationToken).ConfigureAwait(false);
                stream = new DeviceStream(site.Id, lease) { Response = response };
                stream.Body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is RcloneCallException or RcloneUnavailableException or SiteStoreException or OperationCanceledException)
        {
            if (stream is not null) await stream.DisposeAsync().ConfigureAwait(false);
            else lease.Dispose();
            if (e is OperationCanceledException) throw;
            return new SiteDeviceStreamDto
            {
                Problem = e is SiteSignInNeededException ? SiteProblems.SignInNeeded
                    : SiteErrorText.IsUntrustedCertificate(e.Message) ? SiteProblems.CertificateNotTrusted : null,
                Message = SiteErrorText.Friendly(e.Message),
            };
        }

        var id = Guid.NewGuid().ToString("N");
        _streams[id] = stream;
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
            stream.LastUsed = Environment.TickCount64;
            var buffer = new byte[SiteDeviceLimits.ChunkBytes];
            var filled = 0;
            int read;
            // A whole piece unless the file ends: fewer round trips for the caller.
            while (filled < buffer.Length && (read = await stream.Body.ReadAsync(buffer.AsMemory(filled), cancellationToken).ConfigureAwait(false)) > 0)
                filled += read;
            var done = filled < buffer.Length;
            if (done) await CloseStreamAsync(request.StreamId).ConfigureAwait(false);
            return new SiteDeviceChunkDto { Ok = true, Data = filled == buffer.Length ? buffer : buffer[..filled], Done = done };
        }
        catch (Exception e) when (e is IOException or HttpRequestException)
        {
            await CloseStreamAsync(request.StreamId).ConfigureAwait(false);
            return new SiteDeviceChunkDto { Message = "The server stopped sending the file." };
        }
        finally
        {
            stream.Gate.Release();
        }
    }

    private async Task<SiteDeviceChunkDto> DeviceWriteAsync(SiteDeviceChunkRequest request, CancellationToken cancellationToken)
    {
        if (!_streams.TryGetValue(request.StreamId, out var stream) || stream.Pipe is null)
            return new SiteDeviceChunkDto { Message = "That transfer is no longer open." };
        await stream.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            stream.LastUsed = Environment.TickCount64;
            var data = request.Data ?? [];
            if (data.Length > SiteDeviceLimits.ChunkBytes || stream.Written + data.Length > stream.Expected)
            {
                await CloseStreamAsync(request.StreamId).ConfigureAwait(false);
                return new SiteDeviceChunkDto { Message = "The file is larger than it said; it was not kept." };
            }

            if (stream.Upload is { IsCompleted: true } ended)
            {
                await CloseStreamAsync(request.StreamId).ConfigureAwait(false);
                return new SiteDeviceChunkDto { Message = UploadFailure(ended) };
            }

            if (data.Length > 0)
            {
                var flushed = await stream.Pipe.Writer.WriteAsync(data, cancellationToken).ConfigureAwait(false);
                stream.Written += data.Length;
                if (flushed.IsCompleted) return new SiteDeviceChunkDto { Message = "The server stopped taking the file." };
            }

            if (!request.Last) return new SiteDeviceChunkDto { Ok = true };
            if (stream.Written != stream.Expected)
            {
                await CloseStreamAsync(request.StreamId).ConfigureAwait(false);
                return new SiteDeviceChunkDto { Message = "The file ended before its size; it was not kept." };
            }

            await stream.Pipe.Writer.CompleteAsync().ConfigureAwait(false);
            string? failure = null;
            try
            {
                await stream.Upload!.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is RcloneCallException or RcloneUnavailableException)
            {
                failure = SiteErrorText.Friendly(e.Message);
            }

            _streams.TryRemove(request.StreamId, out _);
            await stream.DisposeAsync().ConfigureAwait(false);
            if (failure is not null) return new SiteDeviceChunkDto { Message = failure };
            ForgetListings(stream.SiteId);
            return new SiteDeviceChunkDto { Ok = true, Done = true };
        }
        finally
        {
            stream.Gate.Release();
        }
    }

    private static string UploadFailure(Task upload) => upload.Exception?.InnerException is { } e
        ? SiteErrorText.Friendly(e.Message)
        : "The server stopped taking the file.";

    private async Task<SiteResultDto> DeviceCloseAsync(string streamId)
    {
        await CloseStreamAsync(streamId).ConfigureAwait(false);
        return new SiteResultDto { Ok = true };
    }

    private async Task CloseStreamAsync(string streamId)
    {
        if (_streams.TryRemove(streamId, out var stream)) await stream.DisposeAsync().ConfigureAwait(false);
    }

    private void StartStreamSweep()
    {
        if (_streamSweep is not null) return;
        var sweep = new Timer(_ => _ = SweepStreamsAsync(), null, StreamIdle, StreamIdle);
        if (Interlocked.CompareExchange(ref _streamSweep, sweep, null) is not null) sweep.Dispose();
    }

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
        _streamSweep?.Dispose();
        foreach (var id in _streams.Keys) await CloseStreamAsync(id).ConfigureAwait(false);
    }
}
