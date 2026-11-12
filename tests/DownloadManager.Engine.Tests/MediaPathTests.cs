using System.Diagnostics;
using System.Net;
using System.Text;
using DownloadManager.Core;
using DownloadManager.Persistence;
using TestServer;

namespace DownloadManager.Engine.Tests;

/// <summary>
/// The HLS/DASH media path against TestServer's generated streams (<c>/media/...</c>): retries, timeouts, keys,
/// redirects, limits, speed caps and kept tracks (audit 1: H1, H2, M2-M5, M8, M10, L3, L5, L6).
/// </summary>
[Collection("http-server")]
public sealed class MediaPathTests : IDisposable
{
    private readonly TestServerHost _server;
    private readonly string _root;
    private readonly string _destination;

    public MediaPathTests(TestServerFixture fixture)
    {
        _server = fixture.Server;
        _server.ResetMediaStats();
        _root = Path.Combine(AppContext.BaseDirectory, "test-work", Guid.NewGuid().ToString("N"));
        _destination = Path.Combine(_root, "downloads");
        Directory.CreateDirectory(_destination);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private static EngineOptions FastRetries(TimeSpan? readTimeout = null, int maxAttempts = 5) => new()
    {
        InitialRetryDelay = TimeSpan.FromMilliseconds(10),
        MaxRetryDelay = TimeSpan.FromMilliseconds(50),
        ReadTimeout = readTimeout ?? TimeSpan.FromSeconds(30),
        MaxAttempts = maxAttempts,
    };

    private DownloadJob Job(string url, string fileName = "stream.ts") => new()
    {
        Url = url,
        FileName = fileName,
        DestinationDirectory = _destination,
        FinalFilePath = Path.Combine(_destination, fileName),
        PartFilePath = Path.Combine(_destination, fileName) + ".part",
        RequestedConnections = 1,
        State = DownloadState.Queued,
    };

    private static byte[] Expected(int count, int size)
        => Enumerable.Range(0, count).SelectMany(i => TestServerHost.MediaFragment(i, size)).ToArray();

    [Fact]
    public async Task RetriedFragmentReplacesTheBytesOfTheDroppedAttempt()
    {
        using var engine = new HttpDownloadEngine(FastRetries());
        var job = Job($"{_server.BaseAddress}/media/hls/playlist.m3u8?n=2&size=3000&cut=0&cutAt=1000");

        var result = await engine.ExecuteAsync(job, null, null, CancellationToken.None);

        Assert.True(result.Success, result.Failure?.Message);
        Assert.Equal(2, _server.MediaRequests("seg0"));
        Assert.Equal(Expected(2, 3000), await File.ReadAllBytesAsync(result.FinalFilePath!));
    }

    [Fact]
    public async Task RetriedEncryptedFragmentIsDecryptedOnce()
    {
        using var engine = new HttpDownloadEngine(FastRetries());
        var job = Job($"{_server.BaseAddress}/media/hls/playlist.m3u8?n=2&size=3000&keys=query&cut=1&cutAt=1500");

        var result = await engine.ExecuteAsync(job, null, null, CancellationToken.None);

        Assert.True(result.Success, result.Failure?.Message);
        Assert.Equal(2, _server.MediaRequests("seg1"));
        Assert.Equal(Expected(2, 3000), await File.ReadAllBytesAsync(result.FinalFilePath!));
    }

    [Fact]
    public async Task SlowFragmentCompletesWhenEachReadArrivesInTime()
    {
        // 8 chunks 300 ms apart: 2.1 s in all, against a 1.5 s read timeout that applies per read.
        using var engine = new HttpDownloadEngine(FastRetries(TimeSpan.FromSeconds(1.5)));
        var job = Job($"{_server.BaseAddress}/media/hls/playlist.m3u8?n=1&size=8000&chunk=1000&delayMs=300");

        var result = await engine.ExecuteAsync(job, null, null, CancellationToken.None);

        Assert.True(result.Success, result.Failure?.Message);
        Assert.Equal(1, _server.MediaRequests("seg0"));
        Assert.Equal(Expected(1, 8000), await File.ReadAllBytesAsync(result.FinalFilePath!));
    }

    [Fact]
    public async Task StalledFragmentFailsAsARetryableTimeoutNotACancel()
    {
        using var engine = new HttpDownloadEngine(FastRetries(TimeSpan.FromMilliseconds(400), maxAttempts: 2));
        var job = Job($"{_server.BaseAddress}/media/hls/playlist.m3u8?n=1&size=4000&chunk=1000&delayMs=5000");

        var result = await engine.ExecuteAsync(job, null, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(DownloadState.Failed, result.FinalState);
        Assert.Equal(FailureKind.Timeout, result.Failure!.Kind);
        Assert.True(result.Failure.IsRetryable);
        Assert.Equal(2, _server.MediaRequests("seg0"));
    }

    [Fact]
    public async Task KeysThatDifferOnlyInTheQueryAreEachFetched()
    {
        using var engine = new HttpDownloadEngine(FastRetries());
        var job = Job($"{_server.BaseAddress}/media/hls/playlist.m3u8?n=3&size=2000&keys=query");

        var result = await engine.ExecuteAsync(job, null, null, CancellationToken.None);

        Assert.True(result.Success, result.Failure?.Message);
        Assert.Equal(1, _server.MediaRequests("key1"));
        Assert.Equal(1, _server.MediaRequests("key2"));
        Assert.Equal(1, _server.MediaRequests("key3"));
        Assert.Equal(Expected(3, 2000), await File.ReadAllBytesAsync(result.FinalFilePath!));
    }

    [Fact]
    public async Task QuotedKeyAddressDoesNotHideTheIvAttribute()
    {
        // URI="key?id=1&div=3",IV=0x...: a search for "IV=" anywhere would find it inside the address.
        using var engine = new HttpDownloadEngine(FastRetries());
        var job = Job($"{_server.BaseAddress}/media/hls/playlist.m3u8?n=2&size=2000&keys=query&ivattr=1");

        var result = await engine.ExecuteAsync(job, null, null, CancellationToken.None);

        Assert.True(result.Success, result.Failure?.Message);
        Assert.Equal(Expected(2, 2000), await File.ReadAllBytesAsync(result.FinalFilePath!));
    }

    [Fact]
    public async Task FragmentsAndKeysBehindARedirectAreFollowed()
    {
        using var engine = new HttpDownloadEngine(FastRetries());
        var job = Job($"{_server.BaseAddress}/media/hls/playlist.m3u8?n=2&size=2000&keys=query&redirect=1");

        var result = await engine.ExecuteAsync(job, null, null, CancellationToken.None);

        Assert.True(result.Success, result.Failure?.Message);
        Assert.Equal(4, _server.MediaRequests("redirect")); // two fragments, two keys
        Assert.Equal(Expected(2, 2000), await File.ReadAllBytesAsync(result.FinalFilePath!));
    }

    [Fact]
    public async Task JobSpeedLimitAppliesToFragments()
    {
        using var engine = new HttpDownloadEngine(FastRetries());
        var job = Job($"{_server.BaseAddress}/media/hls/playlist.m3u8?n=3&size=20000");
        job.SpeedLimitBytesPerSecond = 20_000;

        var stopwatch = Stopwatch.StartNew();
        var result = await engine.ExecuteAsync(job, null, null, CancellationToken.None);
        stopwatch.Stop();

        Assert.True(result.Success, result.Failure?.Message);
        // 60,000 bytes at 20,000 B/s, less the small starting credit.
        Assert.True(stopwatch.Elapsed >= TimeSpan.FromSeconds(2), $"took {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task SharedSpeedLimitAppliesToFragments()
    {
        using var engine = new HttpDownloadEngine(FastRetries());
        engine.UpdateSharedSpeedLimit(20_000);
        var job = Job($"{_server.BaseAddress}/media/hls/playlist.m3u8?n=3&size=20000");

        var stopwatch = Stopwatch.StartNew();
        var result = await engine.ExecuteAsync(job, null, null, CancellationToken.None);
        stopwatch.Stop();

        Assert.True(result.Success, result.Failure?.Message);
        Assert.True(stopwatch.Elapsed >= TimeSpan.FromSeconds(2), $"took {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task OversizedPlaylistIsRefusedWithoutReadingItAll()
    {
        using var engine = new HttpDownloadEngine(FastRetries());
        var job = Job($"{_server.BaseAddress}/media/big.m3u8?mb=256");

        var result = await engine.ExecuteAsync(job, null, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(FailureKind.InvalidContent, result.Failure!.Kind);
        Assert.Contains("too large", result.Failure.Message, StringComparison.OrdinalIgnoreCase);
        // Stopped near the 4 MB limit; socket buffers let the server get somewhat further.
        Assert.True(_server.MediaBytesServed < 64L * 1024 * 1024, $"served {_server.MediaBytesServed}");
    }

    [Fact]
    public async Task OversizedKeyIsRefused()
    {
        using var engine = new HttpDownloadEngine(FastRetries());
        var job = Job($"{_server.BaseAddress}/media/hls/playlist.m3u8?n=1&size=2000&keys=query&keysize=1048576");

        var result = await engine.ExecuteAsync(job, null, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(FailureKind.InvalidContent, result.Failure!.Kind);
        Assert.Contains("16 bytes", result.Failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("huge-repeat")]
    [InlineData("huge-duration")]
    public async Task DashManifestWithTooManySegmentsIsRefusedQuickly(string manifest)
    {
        using var engine = new HttpDownloadEngine(FastRetries());
        var job = Job($"{_server.BaseAddress}/media/dash/{manifest}.mpd", "stream.mp4");

        var stopwatch = Stopwatch.StartNew();
        var result = await engine.ExecuteAsync(job, null, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(FailureKind.InvalidContent, result.Failure!.Kind);
        Assert.Contains("too many segments", result.Failure.Message, StringComparison.Ordinal);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"took {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task DashTimelineRepeatToTheEndIsExpanded()
    {
        using var engine = new HttpDownloadEngine(FastRetries());
        var job = Job($"{_server.BaseAddress}/media/dash/repeat.mpd", "stream.mp4");

        var result = await engine.ExecuteAsync(job, null, null, CancellationToken.None);

        Assert.True(result.Success, result.Failure?.Message);
        var expected = Enumerable.Range(1, 3).SelectMany(n => TestServerHost.DashFragment("chunk", n, 3000)).ToArray();
        Assert.Equal(expected, await File.ReadAllBytesAsync(result.FinalFilePath!));
        Assert.Equal(0, _server.MediaRequests("chunk4"));
    }

    [Fact]
    public async Task DashWithSeveralPeriodsIsRefused()
    {
        using var engine = new HttpDownloadEngine(FastRetries());
        var job = Job($"{_server.BaseAddress}/media/dash/multiperiod.mpd", "stream.mp4");

        var result = await engine.ExecuteAsync(job, null, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(FailureKind.InvalidContent, result.Failure!.Kind);
        Assert.Contains("more than one Period", result.Failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, _server.MediaRequests("chunk1"));
    }

    [Fact]
    public async Task ShortSidxIsReportedAsTruncated()
    {
        using var engine = new HttpDownloadEngine(FastRetries());
        var job = Job($"{_server.BaseAddress}/media/dash/sidx-short.mpd", "stream.mp4");

        var result = await engine.ExecuteAsync(job, null, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(FailureKind.InvalidContent, result.Failure!.Kind);
        Assert.Contains("truncated", result.Failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MuxFailureKeepsBothTracksForTheNextRun()
    {
        var paths = new StoragePaths(Path.Combine(_root, "state"));
        paths.EnsureCreated();
        var checkpoints = new JsonMediaCheckpointStore(paths);
        var muxer = new FailOnceMuxer();
        using var engine = new HttpDownloadEngine(FastRetries(), muxer: muxer, mediaCheckpoints: checkpoints);
        var job = Job($"{_server.BaseAddress}/media/dash/av.mpd", "film.mp4");

        var first = await engine.ExecuteAsync(job, null, null, CancellationToken.None);

        Assert.False(first.Success);
        Assert.Equal(FailureKind.MuxingFailed, first.Failure!.Kind);
        Assert.True(first.Failure.IsResumable);
        Assert.True(File.Exists(job.PartFilePath + ".video"));
        Assert.True(File.Exists(job.PartFilePath + ".audio"));
        Assert.False(File.Exists(job.PartFilePath + ".mux.mp4"));
        var requestsAfterFirst = new[] { "v1", "v2", "a1", "a2" }.Sum(_server.MediaRequests);
        Assert.Equal(4, requestsAfterFirst);

        var second = await engine.ExecuteAsync(job, null, null, CancellationToken.None);

        Assert.True(second.Success, second.Failure?.Message);
        Assert.Equal(4, new[] { "v1", "v2", "a1", "a2" }.Sum(_server.MediaRequests));
        var video = TestServerHost.DashFragment("v", 1, 3000).Concat(TestServerHost.DashFragment("v", 2, 3000));
        var audio = TestServerHost.DashFragment("a", 1, 3000).Concat(TestServerHost.DashFragment("a", 2, 3000));
        Assert.Equal(video.Concat(audio).ToArray(), await File.ReadAllBytesAsync(second.FinalFilePath!));
        Assert.False(File.Exists(job.PartFilePath + ".video"));
        Assert.False(File.Exists(job.PartFilePath + ".audio"));
    }

    [Fact]
    public async Task DescriptionTextDoesNotMakeAPlainFileAPlaylist()
    {
        using var engine = new HttpDownloadEngine(FastRetries());
        var job = Job($"{_server.BaseAddress}/file/notes.bin?size=4096&seed=3", "notes.bin");
        job.Description = "notes: taken from the m3u8 page, backup of site.mpd config";

        var result = await engine.ExecuteAsync(job, null, null, CancellationToken.None);

        Assert.True(result.Success, result.Failure?.Message);
        Assert.Equal(TestContent.Generate(3, 4096), await File.ReadAllBytesAsync(result.FinalFilePath!));
    }

    [Fact]
    public async Task HandoffContentTypeMakesAnAddressWithoutExtensionAPlaylist()
    {
        using var engine = new HttpDownloadEngine(FastRetries());
        var job = Job($"{_server.BaseAddress}/media/hls/playlist?n=2&size=1000", "stream");
        job.HandoffContentType = "application/vnd.apple.mpegurl";

        var result = await engine.ExecuteAsync(job, null, null, CancellationToken.None);

        Assert.True(result.Success, result.Failure?.Message);
        Assert.Equal(Expected(2, 1000), await File.ReadAllBytesAsync(result.FinalFilePath!));
    }

    /// <summary>
    /// A plain download whose server answers with an mpegurl type (a .m3u channel list) gets that type in ContentType
    /// from its own transfer or checkpoint. Its next run must stay a plain download, not turn into a playlist.
    /// </summary>
    [Fact]
    public async Task ServerContentTypeFromAnEarlierRunDoesNotMakeAPlaylist()
    {
        var url = $"{_server.BaseAddress}/media/hls/playlist?n=2&size=1000";
        using var client = new HttpClient();
        using var response = await client.GetAsync(url);
        Assert.Contains("mpegurl", response.Content.Headers.ContentType!.MediaType!, StringComparison.OrdinalIgnoreCase);
        var playlistBytes = await response.Content.ReadAsByteArrayAsync();

        using var engine = new HttpDownloadEngine(FastRetries());
        var job = Job(url, "channels");
        job.ContentType = "audio/x-mpegurl";

        var result = await engine.ExecuteAsync(job, null, null, CancellationToken.None);

        Assert.True(result.Success, result.Failure?.Message);
        Assert.Equal(playlistBytes, await File.ReadAllBytesAsync(result.FinalFilePath!));
        Assert.False(StreamManifestHint.IsHls(job));
    }

    [Fact]
    public async Task LocalSegmentIsRefusedWhenLocalNetworkIsOff()
    {
        var handler = new PublicPlaylistHandler();
        using var engine = new HttpDownloadEngine(FastRetries() with { AllowLocalNetwork = false }, handler);
        var job = Job("http://media.example/playlist.m3u8");

        var result = await engine.ExecuteAsync(job, null, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(FailureKind.Forbidden, result.Failure!.Kind);
        Assert.Equal(new[] { "http://media.example/playlist.m3u8" }, handler.Requested);
    }

    [Theory]
    [InlineData("application/vnd.apple.mpegurl", null, true, false)]
    [InlineData(null, "application/x-mpegURL", true, false)]
    [InlineData(null, "application/dash+xml; charset=utf-8", false, true)]
    [InlineData(null, "notes: taken from the m3u8 page", false, false)]
    [InlineData(null, "backup of site.mpd config", false, false)]
    [InlineData("video/mp2t", "mpegurl", false, false)]
    public void StreamHintReadsOnlyMediaTypes(string? contentType, string? description, bool hls, bool dash)
    {
        var job = new DownloadJob { Url = "https://example.test/watch?id=1", HandoffContentType = contentType, Description = description };

        Assert.Equal(hls, StreamManifestHint.IsHls(job));
        Assert.Equal(dash, StreamManifestHint.IsDash(job));
    }

    [Theory]
    [InlineData("audio/x-mpegurl")]
    [InlineData("application/vnd.apple.mpegurl")]
    [InlineData("application/dash+xml")]
    public void ServerContentTypeIsNotAStreamHint(string serverType)
    {
        var job = new DownloadJob { Url = "https://example.test/list", ContentType = serverType };

        Assert.False(StreamManifestHint.IsHls(job));
        Assert.False(StreamManifestHint.IsDash(job));
    }

    [Fact]
    public void MuxTimeoutGrowsWithTheInput()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), FfmpegMediaMuxer.MuxTimeout(0));
        Assert.Equal(TimeSpan.FromMinutes(6), FfmpegMediaMuxer.MuxTimeout(100L * 1024 * 1024));
        Assert.Equal(TimeSpan.FromMinutes(5 + 8), FfmpegMediaMuxer.MuxTimeout(4000L * 1024 * 1024));
    }

    /// <summary>Fails the first mux, then concatenates video and audio as the "muxed" output.</summary>
    private sealed class FailOnceMuxer : IMediaMuxer
    {
        private int _calls;

        public async Task<MediaMuxResult> MuxAsync(string videoPath, string audioPath, string outputPath, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                return new MediaMuxResult(false, "FFmpeg is required for audio/video muxing.");
            }

            await using var output = File.Create(outputPath);
            foreach (var input in new[] { videoPath, audioPath })
            {
                await using var stream = File.OpenRead(input);
                await stream.CopyToAsync(output, cancellationToken);
            }

            return new MediaMuxResult(true, null);
        }
    }

    /// <summary>A public-looking playlist whose only fragment is on a loopback address. Records what was asked.</summary>
    private sealed class PublicPlaylistHandler : HttpMessageHandler
    {
        public List<string> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requested) Requested.Add(request.RequestUri!.AbsoluteUri);
            var body = "#EXTM3U\n#EXT-X-TARGETDURATION:2\n#EXTINF:2,\nhttp://127.0.0.1:9/seg0.ts\n#EXT-X-ENDLIST\n";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/vnd.apple.mpegurl"),
                RequestMessage = request,
            });
        }
    }
}
