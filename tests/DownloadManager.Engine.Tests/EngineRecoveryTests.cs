using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using DownloadManager.Core;
using DownloadManager.Persistence;
using TestServer;

namespace DownloadManager.Engine.Tests;

/// <summary>What the engine does when a link or a file turns out not to be what it expected.</summary>
[Collection("http-server")]
public sealed class EngineRecoveryTests(TestServerFixture fixture) : IDisposable
{
    private const int Megabyte = 1024 * 1024;

    private readonly TestServerHost _server = fixture.Server;
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "test-work", Guid.NewGuid().ToString("N"));
    private readonly List<HttpDownloadEngine> _engines = [];

    private string Destination => Path.Combine(_root, "downloads");
    private StoragePaths Paths => new(Path.Combine(_root, "state"));
    private JsonCheckpointStore Checkpoints => new(Paths);

    public void Dispose()
    {
        foreach (var engine in _engines) engine.Dispose();
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    private HttpDownloadEngine Engine(int maxAttempts = 3, TimeSpan? maxRetryAfter = null, int? hostLimit = null,
        HttpMessageHandler? handler = null)
    {
        Directory.CreateDirectory(Destination);
        Paths.EnsureCreated();
        var engine = new HttpDownloadEngine(
            new EngineOptions
            {
                MaxAttempts = maxAttempts,
                InitialRetryDelay = TimeSpan.FromMilliseconds(10),
                MaxRetryDelay = TimeSpan.FromMilliseconds(50),
                MaxRetryAfterDelay = maxRetryAfter ?? TimeSpan.FromMinutes(5),
                ReadTimeout = TimeSpan.FromSeconds(10),
                ConnectTimeout = TimeSpan.FromSeconds(5),
                AllowLocalNetwork = true,
                MaxConnectionsPerHost = hostLimit ?? 8,
            },
            handler: handler,
            checkpoints: Checkpoints);
        _engines.Add(engine);
        return engine;
    }

    private DownloadJob Job(string url, string fileName, int connections = 1)
    {
        var final = Path.Combine(Destination, fileName);
        return new DownloadJob
        {
            Url = url,
            FileName = fileName,
            DestinationDirectory = Destination,
            FinalFilePath = final,
            PartFilePath = final + $".{Guid.NewGuid():N}.part",
            RequestedConnections = connections,
        };
    }

    private static string Sha256Of(int seed, int size)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(TestContent.Generate(seed, size))).ToLowerInvariant();

    private static string Md5Of(int seed, int size)
        => Convert.ToHexString(System.Security.Cryptography.MD5.HashData(TestContent.Generate(seed, size))).ToLowerInvariant();

    /// <summary>Downloads until partway through a segmented transfer, then stops as a pause would.</summary>
    private async Task<DownloadCheckpoint> PartialSegmentedAsync(DownloadJob job)
    {
        using var stop = new CancellationTokenSource();
        var progress = new SyncProgress(p =>
        {
            if (p.ReceivedBytes > Megabyte / 2) stop.Cancel();
        });
        var stopped = await Engine().ExecuteAsync(job, null, progress, stop.Token);
        Assert.Equal(DownloadState.Cancelled, stopped.FinalState);
        var checkpoint = await Checkpoints.LoadAsync(job.Id);
        Assert.NotNull(checkpoint);
        Assert.True(checkpoint!.Segments.Count > 1, "The test needs a segmented checkpoint.");
        Assert.True(checkpoint.CompletedBytes > 0);
        return checkpoint;
    }

    private async Task AssertCheckpointKeptAsync(DownloadJob job, DownloadCheckpoint expected)
    {
        var kept = await Checkpoints.LoadAsync(job.Id);
        Assert.NotNull(kept);
        Assert.Equal(expected.Segments.Count, kept!.Segments.Count);
        Assert.Equal(expected.CompletedBytes, kept.CompletedBytes);
    }

    private static int ClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public async Task NoNetworkAtResumeKeepsTheSegmentedCheckpoint()
    {
        const int size = 6 * Megabyte;
        var good = $"{_server.BaseAddress}/file/no-network.bin?size={size}&seed=501&bps=524288";
        var job = Job(good, "no-network.bin", connections: 4);
        var saved = await PartialSegmentedAsync(job);

        job.FinalUrl = $"http://127.0.0.1:{ClosedPort()}/file/no-network.bin";
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var failed = await Engine().ExecuteAsync(job, await Checkpoints.LoadAsync(job.Id), null, CancellationToken.None);
            Assert.False(failed.Success);
            Assert.Equal(FailureKind.Network, failed.Failure?.Kind);
            Assert.True(failed.Failure?.IsResumable);
            Assert.Equal(saved.CompletedBytes, failed.BytesWritten);
            Assert.Equal(size, failed.TotalBytes);
            await AssertCheckpointKeptAsync(job, saved);
            Assert.Equal(size, new FileInfo(job.PartFilePath).Length);
        }

        job.FinalUrl = null;
        var result = await Engine().ExecuteAsync(job, await Checkpoints.LoadAsync(job.Id), null, CancellationToken.None);
        Assert.True(result.Success, result.Failure?.Message);
        Assert.Equal(Sha256Of(501, size), await Hashing.Sha256FileAsync(result.FinalFilePath!));
    }

    [Theory]
    [InlineData(404, FailureKind.NotFound)]
    [InlineData(401, FailureKind.Unauthorized)]
    [InlineData(500, FailureKind.Http)]
    public async Task ErrorStatusAtResumeKeepsTheSegmentedCheckpoint(int status, FailureKind kind)
    {
        const int size = 6 * Megabyte;
        var name = $"status-{status}.bin";
        var job = Job($"{_server.BaseAddress}/file/{name}?size={size}&seed=502&bps=524288", name, connections: 4);
        var saved = await PartialSegmentedAsync(job);

        job.FinalUrl = $"{_server.BaseAddress}/error/{status}";
        var failed = await Engine().ExecuteAsync(job, await Checkpoints.LoadAsync(job.Id), null, CancellationToken.None);
        Assert.Equal(kind, failed.Failure?.Kind);
        Assert.Equal(status, failed.Failure?.HttpStatus);
        await AssertCheckpointKeptAsync(job, saved);

        job.FinalUrl = null;
        var kept = await Checkpoints.LoadAsync(job.Id);
        using var recorder = new RecordingHandler();
        var result = await Engine(handler: recorder).ExecuteAsync(job, kept, null, CancellationToken.None);
        Assert.True(result.Success, result.Failure?.Message);
        Assert.Equal(Sha256Of(502, size), await Hashing.Sha256FileAsync(result.FinalFilePath!));
        // The hash alone would also match a full re-download.
        recorder.AssertResumedFrom(kept!);
    }

    [Fact]
    public async Task SegmentedProgressContinuesWithOneConnection()
    {
        const int size = 3 * Megabyte;
        var job = Job($"{_server.BaseAddress}/file/one-connection.bin?size={size}&seed=503&bps=524288", "one-connection.bin", connections: 4);
        await PartialSegmentedAsync(job);

        job.RequestedConnections = 1;
        job.Url = $"{_server.BaseAddress}/file/one-connection.bin?size={size}&seed=503";
        var saved = await Checkpoints.LoadAsync(job.Id);
        using var recorder = new RecordingHandler();
        var result = await Engine(handler: recorder).ExecuteAsync(job, saved, null, CancellationToken.None);
        Assert.True(result.Success, result.Failure?.Message);
        Assert.Equal(Sha256Of(503, size), await Hashing.Sha256FileAsync(result.FinalFilePath!));
        recorder.AssertResumedFrom(saved!);
    }

    [Fact]
    public async Task EmptySingleSegmentCheckpointDoesNotDisableSegments()
    {
        const int size = 2 * Megabyte;
        _server.ResetConcurrency();
        var job = Job($"{_server.BaseAddress}/file/first-failed.bin?size={size}&seed=504&bps=2097152", "first-failed.bin", connections: 4);
        var checkpoint = new DownloadCheckpoint { JobId = job.Id, Url = job.Url };
        checkpoint.Segments.Add(new SegmentCheckpoint { Start = 0, End = long.MaxValue - 1, Completed = 0 });

        var result = await Engine().ExecuteAsync(job, checkpoint, null, CancellationToken.None);
        Assert.True(result.Success, result.Failure?.Message);
        Assert.True(_server.PeakConcurrentRangeRequests > 1);
    }

    private async Task<(DownloadJob Job, DownloadCheckpoint Checkpoint)> SingleStreamCheckpointAsync(
        string name, byte[] partContent, long completed, long? total, string url)
    {
        Directory.CreateDirectory(Destination);
        Paths.EnsureCreated();
        var job = Job(url, name);
        await File.WriteAllBytesAsync(job.PartFilePath, partContent);
        var checkpoint = new DownloadCheckpoint { JobId = job.Id, Url = job.Url, TotalBytes = total };
        checkpoint.Segments.Add(new SegmentCheckpoint { Start = 0, End = (total ?? long.MaxValue) - 1, Completed = completed });
        return (job, checkpoint);
    }

    [Fact]
    public async Task RangeNotSatisfiableWithAllBytesPresentCompletes()
    {
        const int size = 128 * 1024;
        var content = TestContent.Generate(511, size);
        var (job, checkpoint) = await SingleStreamCheckpointAsync("complete-416.bin", content, size, size,
            $"{_server.BaseAddress}/file/complete-416.bin?size={size}&seed=511");

        using var recorder = new RecordingHandler();
        var result = await Engine(handler: recorder).ExecuteAsync(job, checkpoint, null, CancellationToken.None);
        Assert.True(result.Success, result.Failure?.Message);
        // Completed from the bytes already there: only the range past the end was asked for.
        Assert.All(recorder.TransferRanges(), range => Assert.Equal($"bytes={size}-", range));
        Assert.Equal(Sha256Of(511, size), await Hashing.Sha256FileAsync(result.FinalFilePath!));
    }

    [Fact]
    public async Task RangeNotSatisfiableWhenTheFileShrankRestartsFromZero()
    {
        const int size = 128 * 1024;
        var (job, checkpoint) = await SingleStreamCheckpointAsync("shrunk-416.bin", new byte[size * 2], size * 2, size * 2,
            $"{_server.BaseAddress}/file/shrunk-416.bin?size={size}&seed=512");

        var result = await Engine().ExecuteAsync(job, checkpoint, null, CancellationToken.None);
        Assert.True(result.Success, result.Failure?.Message);
        Assert.Equal(Sha256Of(512, size), await Hashing.Sha256FileAsync(result.FinalFilePath!));
    }

    [Fact]
    public async Task RangeNotSatisfiableWithoutRecordedBytesDoesNotTrustThePartFile()
    {
        // The state the old probe defect left behind: a preallocated file of holes, and a
        // checkpoint that records nothing. Finishing here would deliver a file of zeros.
        const int size = 128 * 1024;
        var (job, checkpoint) = await SingleStreamCheckpointAsync("holes-416.bin", new byte[size], 0, size,
            $"{_server.BaseAddress}/file/holes-416.bin?size={size}&seed=513");

        var result = await Engine().ExecuteAsync(job, checkpoint, null, CancellationToken.None);
        Assert.True(result.Success, result.Failure?.Message);
        Assert.Equal(Sha256Of(513, size), await Hashing.Sha256FileAsync(result.FinalFilePath!));
    }

    [Theory]
    [InlineData(429, 0, 1)]
    [InlineData(503, 0, 1)]
    // An HTTP date is only accurate to the second, so it asks for more to prove a wait of one.
    [InlineData(429, 1, 2)]
    public async Task SingleStreamWaitsForRetryAfter(int status, int asDate, int after)
    {
        var name = $"throttled-single-{status}-{asDate}.bin";
        var job = Job($"{_server.BaseAddress}/throttled/{name}?status={status}&after={after}&date={asDate}&size=65536&seed=521", name);

        var clock = Stopwatch.StartNew();
        var result = await Engine().ExecuteAsync(job, null, null, CancellationToken.None);

        Assert.True(result.Success, result.Failure?.Message);
        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(900), $"Retried after {clock.Elapsed}.");
    }

    [Fact]
    public async Task SegmentWaitsForRetryAfter()
    {
        const int size = 2 * Megabyte;
        const string name = "throttled-segment.bin";
        var job = Job($"{_server.BaseAddress}/throttled/{name}?skip=1&failures=1&after=1&size={size}&seed=522", name, connections: 4);

        var clock = Stopwatch.StartNew();
        var result = await Engine().ExecuteAsync(job, null, null, CancellationToken.None);

        Assert.True(result.Success, result.Failure?.Message);
        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(900), $"Retried after {clock.Elapsed}.");
        Assert.Equal(Sha256Of(522, size), await Hashing.Sha256FileAsync(result.FinalFilePath!));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task RetryAfterIsBounded(int connections)
    {
        const int size = 2 * Megabyte;
        var name = $"throttled-bound-{connections}.bin";
        var skip = connections > 1 ? 1 : 0;
        var job = Job($"{_server.BaseAddress}/throttled/{name}?skip={skip}&failures=1&after=3600&size={size}&seed=523", name, connections);

        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var result = await Engine(maxRetryAfter: TimeSpan.FromMilliseconds(200)).ExecuteAsync(job, null, null, limit.Token);
        Assert.True(result.Success, result.Failure?.Message);
    }

    [Fact]
    public async Task RetryAfterDoesNotChangeTheRetryCount()
    {
        const string name = "throttled-count.bin";
        var job = Job($"{_server.BaseAddress}/throttled/{name}?failures=99&after=0&size=65536&seed=524", name);

        var result = await Engine(maxAttempts: 3).ExecuteAsync(job, null, null, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal(429, result.Failure?.HttpStatus);
        Assert.Equal(3, _server.ThrottledRequests(name));
    }

    [Theory]
    [InlineData(1, "md5")]
    [InlineData(4, "md5")]
    [InlineData(1, "sha256")]
    [InlineData(4, "sha256")]
    public async Task AProviderFileThatFailsItsHashIsDeletedAndARetryStartsClean(int connections, string algorithm)
    {
        const int size = 2 * Megabyte;
        var name = $"mismatch-{connections}-{algorithm}.bin";
        _server.SetVersion(name, 1);
        var job = Job($"{_server.BaseAddress}/changed/{name}?size={size}", name, connections);
        job.RemoteSource = new RemoteSource("torbox", "torrents", "1", "2");
        if (algorithm == "sha256") job.ExpectedSha256 = Sha256Of(2, size);
        else job.ExpectedMd5 = Md5Of(2, size);

        var failed = await Engine().ExecuteAsync(job, null, null, CancellationToken.None);
        Assert.False(failed.Success);
        Assert.Equal(FailureKind.VerificationFailed, failed.Failure?.Kind);
        Assert.Contains("mismatch", failed.Failure?.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("deleted", failed.Failure?.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, failed.BytesWritten);
        Assert.False(File.Exists(job.PartFilePath));
        Assert.Null(await Checkpoints.LoadAsync(job.Id));
        Assert.False(File.Exists(job.FinalFilePath));

        _server.SetVersion(name, 2);
        var retried = await Engine().ExecuteAsync(job, await Checkpoints.LoadAsync(job.Id), null, CancellationToken.None);
        Assert.True(retried.Success, retried.Failure?.Message);
        Assert.Equal(Sha256Of(2, size), await Hashing.Sha256FileAsync(retried.FinalFilePath!));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task AHashTheUserGaveThatFailsKeepsTheBytes(int connections)
    {
        const int size = 2 * Megabyte;
        var name = $"typed-hash-{connections}.bin";
        var job = Job($"{_server.BaseAddress}/file/{name}?size={size}&seed=532", name, connections);
        job.ExpectedSha256 = new string('0', 64);

        var failed = await Engine().ExecuteAsync(job, null, null, CancellationToken.None);
        Assert.False(failed.Success);
        Assert.Equal(FailureKind.VerificationFailed, failed.Failure?.Kind);
        Assert.Contains("mismatch", failed.Failure?.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("kept", failed.Failure?.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(failed.Failure?.IsResumable);
        Assert.Equal(size, failed.BytesWritten);
        Assert.Equal(size, new FileInfo(job.PartFilePath).Length);
        Assert.False(File.Exists(job.FinalFilePath));

        // The hash was the mistake: with the right one the same bytes finish.
        job.ExpectedSha256 = Sha256Of(532, size);
        var retried = await Engine().ExecuteAsync(job, await Checkpoints.LoadAsync(job.Id), null, CancellationToken.None);
        Assert.True(retried.Success, retried.Failure?.Message);
        Assert.Equal(Sha256Of(532, size), await Hashing.Sha256FileAsync(retried.FinalFilePath!));
    }

    [Fact]
    public async Task AFinishedFileSurvivesAFailedRename()
    {
        const int size = 128 * 1024;
        const string name = "in-the-way.bin";
        var engine = Engine();
        var job = Job($"{_server.BaseAddress}/file/{name}?size={size}&seed=571", name);
        job.ConflictPolicy = ConflictPolicy.Fail;
        await File.WriteAllTextAsync(job.FinalFilePath, "already here");

        var failed = await engine.ExecuteAsync(job, null, null, CancellationToken.None);
        Assert.Equal(FailureKind.DestinationUnavailable, failed.Failure?.Kind);
        Assert.Equal(size, new FileInfo(job.PartFilePath).Length);
        Assert.Equal(size, (await Checkpoints.LoadAsync(job.Id))?.CompletedBytes);

        // The address now serves different bytes, so a retry that fetched anything would show.
        File.Delete(job.FinalFilePath);
        job.Url = $"{_server.BaseAddress}/file/other-{name}?size={size}&seed=572";
        var retried = await engine.ExecuteAsync(job, await Checkpoints.LoadAsync(job.Id), null, CancellationToken.None);
        Assert.True(retried.Success, retried.Failure?.Message);
        Assert.Equal(Sha256Of(571, size), await Hashing.Sha256FileAsync(retried.FinalFilePath!));
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(4, false)]
    [InlineData(1, true)]
    [InlineData(4, true)]
    public async Task TheFinalRenameIsRecordedForTheOwnerToClear(int connections, bool ownerClears)
    {
        const int size = 2 * Megabyte;
        var name = $"marker-{connections}-{ownerClears}.bin";
        var job = Job($"{_server.BaseAddress}/file/{name}?size={size}&seed=574", name, connections);
        job.KeepCheckpointAfterFinalize = ownerClears;

        var result = await Engine().ExecuteAsync(job, null, null, CancellationToken.None);
        Assert.True(result.Success, result.Failure?.Message);
        var checkpoint = await Checkpoints.LoadAsync(job.Id);
        if (ownerClears)
        {
            Assert.Equal(result.FinalFilePath, checkpoint?.FinalizingTo);
            Assert.Equal(size, checkpoint?.CompletedBytes);
        }
        else
        {
            Assert.Null(checkpoint);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task MatchingHashesKeepTheFile(int connections)
    {
        const int size = 2 * Megabyte;
        var name = $"hash-ok-{connections}.bin";
        var job = Job($"{_server.BaseAddress}/file/{name}?size={size}&seed=531", name, connections);
        job.ExpectedSha256 = Sha256Of(531, size).ToUpperInvariant();
        job.ExpectedMd5 = Md5Of(531, size).ToUpperInvariant();

        var result = await Engine().ExecuteAsync(job, null, null, CancellationToken.None);
        Assert.True(result.Success, result.Failure?.Message);
        Assert.Equal(size, new FileInfo(result.FinalFilePath!).Length);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task ResolvedAddressIsFetchedAndNeverStored(int connections)
    {
        const int size = 2 * Megabyte;
        var name = $"resolved-{connections}.bin";
        var address = $"{_server.BaseAddress}/file/{name}?size={size}&seed=541&sig=link-secret";
        var job = Job($"remote://torbox/torrents/7/{connections}", name, connections);
        job.ResolvedUrl = address;
        job.ExpectedTotalBytes = size;
        job.ExpectedMd5 = Md5Of(541, size);

        var result = await Engine().ExecuteAsync(job, null, null, CancellationToken.None);
        Assert.True(result.Success, result.Failure?.Message);
        Assert.Equal(Sha256Of(541, size), await Hashing.Sha256FileAsync(result.FinalFilePath!));
        Assert.Null(job.FinalUrl);
        Assert.Equal($"remote://torbox/torrents/7/{connections}", job.Url);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task CheckpointNeverHoldsTheResolvedAddress(int connections)
    {
        const int size = 2 * Megabyte;
        var name = $"resolved-checkpoint-{connections}.bin";
        var job = Job($"remote://torbox/torrents/7/{connections}", name, connections);
        job.ResolvedUrl = $"{_server.BaseAddress}/slow/{name}?size={size}&seed=542&bps=524288&sig=link-secret";

        using var stop = new CancellationTokenSource();
        var progress = new SyncProgress(p => { if (p.ReceivedBytes > 128 * 1024) stop.Cancel(); });
        await Engine().ExecuteAsync(job, null, progress, stop.Token);

        var checkpoint = await File.ReadAllTextAsync(Directory.GetFiles(Paths.CheckpointsDirectory, $"{job.Id:N}*").Single());
        Assert.DoesNotContain("link-secret", checkpoint);
        Assert.DoesNotContain("/slow/", checkpoint);
        Assert.Contains("remote://torbox", checkpoint);
    }

    [Fact]
    public async Task CredentialsStayOnTheOriginalOrigin()
    {
        var job = Job("remote://torbox/torrents/7/8", "echo.json");
        job.ResolvedUrl = $"{_server.BaseAddress}/sensitive/echo";
        job.Referrer = $"{_server.BaseAddress}/page";
        job.RequestHeaders["Cookie"] = "session=secret";
        job.RequestHeaders["Authorization"] = "Bearer secret";

        var result = await Engine().ExecuteAsync(job, null, null, CancellationToken.None);
        Assert.True(result.Success, result.Failure?.Message);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(result.FinalFilePath!));
        foreach (var name in new[] { "cookie", "authorization", "referer" })
            Assert.False(json.RootElement.GetProperty(name).GetBoolean(), name);
    }

    [Fact]
    public async Task HostLimiterUsesTheHostOfTheRealAddress()
    {
        // 127.0.0.1 and localhost reach the same server but are different hosts to the limiter.
        // Keyed on the reference's host, a limit of one would make these two take turns.
        var engine = Engine(hostLimit: 1);
        var port = new Uri(_server.BaseAddress).Port;
        var first = Job("remote://torbox/torrents/7/1", "limiter-a.bin");
        first.ResolvedUrl = $"http://127.0.0.1:{port}/slow/limiter-a.bin?size=262144&seed=543&bps=524288";
        var second = Job("remote://torbox/torrents/7/2", "limiter-b.bin");
        second.ResolvedUrl = $"http://localhost:{port}/slow/limiter-b.bin?size=262144&seed=544&bps=524288";

        var results = await Task.WhenAll(
            engine.ExecuteAsync(first, null, null, CancellationToken.None),
            engine.ExecuteAsync(second, null, null, CancellationToken.None));

        Assert.All(results, result => Assert.True(result.Success, result.Failure?.Message));
        Assert.Equal(2, engine.HostConnectionPeak);
    }

    [Fact]
    public async Task WrongRangeTotalAgainstExpectedIsRemoteChangedAndRestartsClean()
    {
        const int size = 2 * Megabyte;
        const string name = "wrong-size-ranged.bin";
        var job = Job($"{_server.BaseAddress}/file/{name}?size={size}&seed=551", name, connections: 4);
        job.ExpectedTotalBytes = size + 1;

        var failed = await Engine().ExecuteAsync(job, null, null, CancellationToken.None);
        Assert.False(failed.Success);
        Assert.Equal(FailureKind.RemoteChanged, failed.Failure?.Kind);
        Assert.False(File.Exists(job.PartFilePath));
        Assert.Null(await Checkpoints.LoadAsync(job.Id));

        job.ExpectedTotalBytes = size;
        var retried = await Engine().ExecuteAsync(job, await Checkpoints.LoadAsync(job.Id), null, CancellationToken.None);
        Assert.True(retried.Success, retried.Failure?.Message);
        Assert.Equal(Sha256Of(551, size), await Hashing.Sha256FileAsync(retried.FinalFilePath!));
    }

    [Fact]
    public async Task WrongWholeFileAgainstExpectedAsksForAFreshLink()
    {
        const int size = 2 * Megabyte;
        const string name = "wrong-size-whole.bin";
        var job = Job($"{_server.BaseAddress}/file/{name}?size={size}&seed=554", name);
        job.ExpectedTotalBytes = size + 1;

        var failed = await Engine().ExecuteAsync(job, null, null, CancellationToken.None);
        Assert.False(failed.Success);
        Assert.Equal(FailureKind.ExpiredUrl, failed.Failure?.Kind);
        Assert.False(File.Exists(job.PartFilePath));

        job.ExpectedTotalBytes = size;
        var retried = await Engine().ExecuteAsync(job, await Checkpoints.LoadAsync(job.Id), null, CancellationToken.None);
        Assert.True(retried.Success, retried.Failure?.Message);
        Assert.Equal(Sha256Of(554, size), await Hashing.Sha256FileAsync(retried.FinalFilePath!));
    }

    [Theory]
    [InlineData(1, "norange")]
    [InlineData(4, "norange")]
    [InlineData(1, "unknown")]
    [InlineData(4, "unknown")]
    public async Task ADeadLinkAnsweringWithAShortDocumentKeepsSavedProgress(int connections, string route)
    {
        const int size = 6 * Megabyte;
        var name = $"dead-200-{connections}-{route}.bin";
        var job = Job($"{_server.BaseAddress}/file/{name}?size={size}&seed=575&bps=524288", name, connections);
        job.ExpectedTotalBytes = size;
        using (var stop = new CancellationTokenSource())
        {
            var progress = new SyncProgress(p => { if (p.ReceivedBytes > Megabyte / 2) stop.Cancel(); });
            Assert.Equal(DownloadState.Cancelled, (await Engine().ExecuteAsync(job, null, progress, stop.Token)).FinalState);
        }

        var saved = await Checkpoints.LoadAsync(job.Id);
        Assert.True(saved!.CompletedBytes > 0);
        var partLength = new FileInfo(job.PartFilePath).Length;

        job.FinalUrl = $"{_server.BaseAddress}/{route}/dead.txt?size=7&seed=576";
        var failed = await Engine().ExecuteAsync(job, await Checkpoints.LoadAsync(job.Id), null, CancellationToken.None);
        Assert.False(failed.Success);
        Assert.Equal(FailureKind.ExpiredUrl, failed.Failure?.Kind);
        Assert.True(failed.Failure?.IsResumable);
        Assert.Contains("kept", failed.Failure?.Message);
        await AssertCheckpointKeptAsync(job, saved);
        Assert.Equal(partLength, new FileInfo(job.PartFilePath).Length);

        job.FinalUrl = null;
        job.Url = $"{_server.BaseAddress}/file/{name}?size={size}&seed=575";
        var result = await Engine().ExecuteAsync(job, await Checkpoints.LoadAsync(job.Id), null, CancellationToken.None);
        Assert.True(result.Success, result.Failure?.Message);
        Assert.Equal(Sha256Of(575, size), await Hashing.Sha256FileAsync(result.FinalFilePath!));
    }

    [Fact]
    public async Task WrongSizeAgainstSavedProgressDiscardsIt()
    {
        const int size = 6 * Megabyte;
        var job = Job($"{_server.BaseAddress}/file/wrong-size-resume.bin?size={size}&seed=552&bps=524288", "wrong-size-resume.bin", connections: 4);
        await PartialSegmentedAsync(job);

        job.ExpectedTotalBytes = size - 1;
        var failed = await Engine().ExecuteAsync(job, await Checkpoints.LoadAsync(job.Id), null, CancellationToken.None);
        Assert.Equal(FailureKind.RemoteChanged, failed.Failure?.Kind);
        Assert.False(File.Exists(job.PartFilePath));
        Assert.Null(await Checkpoints.LoadAsync(job.Id));
    }

    [Fact]
    public async Task UnknownLengthThatEndsShortOfExpectedFailsAtFinalize()
    {
        const int size = 65536;
        var job = Job($"{_server.BaseAddress}/unknown/short.bin?size={size}&seed=553", "short.bin");
        job.ExpectedTotalBytes = size + 100;

        var failed = await Engine().ExecuteAsync(job, null, null, CancellationToken.None);
        Assert.Equal(FailureKind.VerificationFailed, failed.Failure?.Kind);
        Assert.False(File.Exists(job.PartFilePath));
        Assert.False(File.Exists(job.FinalFilePath));
    }

    [Theory]
    [InlineData("text/html", 0, 1)]
    [InlineData("text/html", 1, 4)]
    [InlineData("application/json", 0, 1)]
    [InlineData("application/json", 0, 4)]
    [InlineData("application/json", 1, 4)]
    public async Task ErrorDocumentIsNotAcceptedForABinaryTarget(string type, int ranged, int connections)
    {
        var name = $"error-doc-{type.Replace('/', '-')}-{ranged}-{connections}.bin";
        var job = Job($"{_server.BaseAddress}/errordoc/{name}?type={type}&ranged={ranged}", name, connections);

        var failed = await Engine().ExecuteAsync(job, null, null, CancellationToken.None);
        Assert.False(failed.Success);
        Assert.Equal(FailureKind.InvalidContent, failed.Failure?.Kind);
        Assert.False(File.Exists(job.FinalFilePath));
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("application/json")]
    public async Task ErrorDocumentAtResumeKeepsTheSegmentedCheckpoint(string type)
    {
        const int size = 6 * Megabyte;
        var name = $"portal-{type.Replace('/', '-')}.bin";
        _server.SetVersion(name, 1);
        var url = $"{_server.BaseAddress}/errorpage/{name}?size={size}&seed=561&type={type}";
        var job = Job(url + "&bps=524288", name, connections: 4);
        var saved = await PartialSegmentedAsync(job);

        _server.SetVersion(name, 2);
        var failed = await Engine().ExecuteAsync(job, await Checkpoints.LoadAsync(job.Id), null, CancellationToken.None);
        Assert.Equal(FailureKind.InvalidContent, failed.Failure?.Kind);
        Assert.Equal(saved.CompletedBytes, failed.BytesWritten);
        await AssertCheckpointKeptAsync(job, saved);

        _server.SetVersion(name, 1);
        job.Url = url;
        var result = await Engine().ExecuteAsync(job, await Checkpoints.LoadAsync(job.Id), null, CancellationToken.None);
        Assert.True(result.Success, result.Failure?.Message);
        Assert.Equal(Sha256Of(561, size), await Hashing.Sha256FileAsync(result.FinalFilePath!));
    }

    [Theory]
    [InlineData("text/html", "page.html", 1)]
    [InlineData("text/html", "page.html", 4)]
    [InlineData("application/json", "data.json", 4)]
    [InlineData("application/json", "export", 4)]
    [InlineData("application/json", "records.dat", 4)]
    public async Task PagesAndJsonThatUsersWantStillDownload(string type, string fileName, int connections)
    {
        const int size = 2 * Megabyte;
        var name = $"{connections}-{fileName}";
        var job = Job($"{_server.BaseAddress}/typed/{name}?type={type}&size={size}&seed=562", name, connections);
        job.FileName = fileName;
        job.FinalFilePath = Path.Combine(Destination, $"{connections}-{fileName}");

        var result = await Engine().ExecuteAsync(job, null, null, CancellationToken.None);
        Assert.True(result.Success, result.Failure?.Message);
        Assert.Equal(Sha256Of(562, size), await Hashing.Sha256FileAsync(result.FinalFilePath!));
    }

    [Theory]
    [InlineData("text/html", "report.php", 2 * Megabyte, null)]
    [InlineData("text/html", "manual", 2 * Megabyte, null)]
    [InlineData("text/html", "data.dump", 2 * Megabyte, null)]
    [InlineData("application/json", "mislabelled.zip", 2 * Megabyte, null)]
    [InlineData("application/json", "records.json.gz", 2 * Megabyte, "gzip")]
    [InlineData("application/json", "records.json.gz", 64 * 1024, null)]
    [InlineData("application/json", "export.gz", 64 * 1024, "gzip")]
    public async Task LargeOrCompressedFilesLabelledAsDocumentsStillDownload(string type, string fileName, int size, string? encoding)
    {
        foreach (var connections in new[] { 1, 4 })
        {
            var name = $"{connections}-{size}-{encoding}-{fileName}";
            var job = Job($"{_server.BaseAddress}/typed/{name}?type={type}&size={size}&seed=564&encoding={encoding}", name, connections);
            job.FileName = fileName;

            var result = await Engine().ExecuteAsync(job, null, null, CancellationToken.None);
            Assert.True(result.Success, result.Failure?.Message);
            Assert.Equal(Sha256Of(564, size), await Hashing.Sha256FileAsync(result.FinalFilePath!));
        }
    }

    [Theory]
    [InlineData("text/html", "archive.zip", 2 * Megabyte)]
    [InlineData("text/html", "report.php", 64 * 1024)]
    [InlineData("application/json", "small.gz", 64 * 1024)]
    public async Task DocumentsThatCouldBeErrorPagesStillFail(string type, string fileName, int size)
    {
        foreach (var connections in new[] { 1, 4 })
        {
            var name = $"{connections}-{size}-{fileName}";
            var job = Job($"{_server.BaseAddress}/typed/{name}?type={type}&size={size}&seed=565", name, connections);
            job.FileName = fileName;

            var failed = await Engine().ExecuteAsync(job, null, null, CancellationToken.None);
            Assert.False(failed.Success);
            Assert.Equal(FailureKind.InvalidContent, failed.Failure?.Kind);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task ExactlyThePromisedSizeIsThePromisedFileWhateverItsType(int connections)
    {
        const int size = 2 * Megabyte;
        var name = $"promised-{connections}.bin";
        var job = Job($"{_server.BaseAddress}/typed/{name}?type=text/html&size={size}&seed=563", name, connections);
        job.ExpectedTotalBytes = size;

        var result = await Engine().ExecuteAsync(job, null, null, CancellationToken.None);
        Assert.True(result.Success, result.Failure?.Message);
    }

    [Fact]
    public async Task ProxiedHandlerTakesTheEngineProxyRoute()
    {
        await using var proxy = TestProxyServer.Start();
        var engine = Engine();
        engine.UpdateProxy(new ProxySettings { Mode = "manual", Address = proxy.Address, Username = "proxyuser", Password = "proxypass" });
        using var client = new HttpClient(engine.CreateProxiedHandler());

        var response = await client.GetStringAsync($"{_server.BaseAddress}/health");
        Assert.Equal("ok", response);
        Assert.True(proxy.Authorized > 0);

        // New connections follow a settings change, without rebuilding the client.
        var before = proxy.Authorized;
        engine.UpdateProxy(new ProxySettings { Mode = "none" });
        using var direct = new HttpClient(engine.CreateProxiedHandler());
        Assert.Equal("ok", await direct.GetStringAsync($"{_server.BaseAddress}/health"));
        Assert.Equal(before, proxy.Authorized);
    }

    /// <summary>
    /// UI.B15: a server that cuts every response short still sends some of the file each time. The single-stream
    /// path starts its count of attempts over after progress; a segment kept counting, so in the PERF.2 soak every
    /// segmented download from such a server failed near the end while the single-stream one finished.
    /// </summary>
    [Fact]
    public async Task ASegmentThatKeepsGettingBytesIsNotOutOfAttempts()
    {
        const int size = 4 * Megabyte;
        const string name = "cut-segments.bin";
        var job = Job($"{_server.BaseAddress}/truncate/{name}?size={size}&cut={128 * 1024}&seed=731", name, connections: 4);

        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var result = await Engine(maxAttempts: 3).ExecuteAsync(job, null, null, limit.Token);

        Assert.True(result.Success, result.Failure?.Message);
        Assert.Equal(Sha256Of(731, size), await Hashing.Sha256FileAsync(result.FinalFilePath!));
    }

    /// <summary>
    /// UI.B15: the reset comes before the limit is checked, so with one attempt allowed, every attempt that gets
    /// bytes still retries. Checked after the limit, the first cut response failed the download.
    /// </summary>
    [Fact]
    public async Task ASegmentThatGetsBytesOnItsLastAllowedAttemptStillRetries()
    {
        const int size = 2 * Megabyte;
        const string name = "cut-last-attempt.bin";
        var job = Job($"{_server.BaseAddress}/truncate/{name}?size={size}&cut={256 * 1024}&seed=733", name, connections: 4);

        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var result = await Engine(maxAttempts: 1).ExecuteAsync(job, null, null, limit.Token);

        Assert.True(result.Success, result.Failure?.Message);
        Assert.Equal(Sha256Of(733, size), await Hashing.Sha256FileAsync(result.FinalFilePath!));
    }

    /// <summary>The reset needs new bytes: a segment that gets none still runs out of attempts.</summary>
    [Fact]
    public async Task ASegmentThatGetsNothingStillRunsOutOfAttempts()
    {
        const string name = "segment-never.bin";
        var job = Job($"{_server.BaseAddress}/throttled/{name}?skip=1&failures=1000&status=500&after=0&size={2 * Megabyte}&seed=732", name, connections: 4);

        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var result = await Engine(maxAttempts: 3).ExecuteAsync(job, null, null, limit.Token);

        Assert.False(result.Success);
    }

    private sealed class SyncProgress(Action<TransferProgress> report) : IProgress<TransferProgress>
    {
        public void Report(TransferProgress value) => report(value);
    }
}
