using System.Text.Json;
using DownloadManager.Core;
using DownloadManager.Persistence;
using TestServer;
using Xunit.Abstractions;

namespace DownloadManager.Engine.Tests;

[CollectionDefinition("http-server")]
public sealed class HttpServerCollection : ICollectionFixture<TestServerFixture>;

public sealed class TestServerFixture : IAsyncLifetime
{
    public TestServerHost Server { get; private set; } = null!;
    public TestProxyServer Proxy { get; private set; } = null!;
    public TestSocks5Server Socks5 { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Server = await TestServerHost.StartAsync();
        Proxy = TestProxyServer.Start();
        Socks5 = TestSocks5Server.Start();
    }

    public async Task DisposeAsync()
    {
        await Socks5.DisposeAsync();
        await Proxy.DisposeAsync();
        await Server.DisposeAsync();
    }
}

[Collection("http-server")]
public sealed class HttpDownloadEngineTests
{
    private readonly TestServerHost _server;
    private readonly TestProxyServer _proxy;
    private readonly TestSocks5Server _socks5;
    private readonly ITestOutputHelper _output;

    public HttpDownloadEngineTests(TestServerFixture fixture, ITestOutputHelper output)
    {
        _server = fixture.Server;
        _proxy = fixture.Proxy;
        _socks5 = fixture.Socks5;
        _output = output;
    }

    [Fact]
    public async Task DownloadsFileAndMatchesHash()
    {
        var size = 262_144;
        using var ctx = new EngineContext();
        var job = ctx.CreateJob($"{_server.BaseAddress}/file/basic.bin?size={size}&seed=7", "basic.bin");

        using var engine = CreateEngine();
        var result = await engine.ExecuteAsync(job, null, null, CancellationToken.None);

        Assert.True(result.Success, result.Failure?.Message);
        Assert.Equal(DownloadState.Completed, result.FinalState);
        Assert.True(File.Exists(result.FinalFilePath));
        var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            TestContent.Generate(7, size))).ToLowerInvariant();
        Assert.Equal(expected, await Hashing.Sha256FileAsync(result.FinalFilePath!));
    }

    [Fact]
    public async Task FollowsRedirects()
    {
        using var ctx = new EngineContext();
        var job = ctx.CreateJob($"{_server.BaseAddress}/redirect-file/redirect.bin?size=65536&count=3", "redirect.bin");

        using var engine = CreateEngine();
        var result = await engine.ExecuteAsync(job, null, null, CancellationToken.None);

        Assert.True(result.Success, result.Failure?.Message);
        Assert.Equal(65536, new FileInfo(result.FinalFilePath!).Length);
    }

    [Fact]
    public async Task SendsCookieRefererAndUserAgentToAuthenticatedEndpoint()
    {
        const int size = 262_144;
        _server.ResetAuthStats();
        using var ctx = new EngineContext();
        var job = ctx.CreateJob($"{_server.BaseAddress}/auth/file/auth.bin?size={size}&seed=321", "auth.bin");
        job.Referrer = $"{_server.BaseAddress}/auth/login?size={size}&seed=321";
        job.UserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
            "Chrome/156.0.0.0 Safari/537.36";
        job.RequestHeaders["Cookie"] = "dmsession=valid-auth-token";

        using var engine = CreateEngine();
        var result = await engine.ExecuteAsync(job, null, null, CancellationToken.None);

        Assert.True(result.Success, result.Failure?.Message);
        Assert.True(_server.AuthAuthorized > 0);
        Assert.Equal(0, _server.AuthDenied);
        var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            TestContent.Generate(321, size))).ToLowerInvariant();
        Assert.Equal(expected, await Hashing.Sha256FileAsync(result.FinalFilePath!));

        using var http = new HttpClient();
        var stats = await http.GetStringAsync($"{_server.BaseAddress}/auth/stats");
        Assert.Contains("\"lastCookiePresent\":true", stats);
        Assert.Contains("/auth/login", stats);
        Assert.Contains("Chrome/156", stats);
        Assert.DoesNotContain("valid-auth-token", stats);
    }

    [Fact]
    public async Task DownloadsThroughAuthenticatedHttpProxy()
    {
        const int size = 524_288;
        _proxy.ResetStats();
        using var ctx = new EngineContext();
        var job = ctx.CreateJob(
            $"{_server.BaseAddress}/file/proxy.bin?size={size}&seed=71",
            "proxy.bin");

        using var engine = CreateEngine(proxy: ProxyCredentials());
        var result = await engine.ExecuteAsync(job, null, null, CancellationToken.None);

        Assert.True(result.Success, result.Failure?.Message);
        Assert.True(_proxy.Authorized > 0);
        Assert.Equal(0, _proxy.Denied);
        Assert.True(_proxy.LastAuthorizationPresent);
        var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            TestContent.Generate(71, size))).ToLowerInvariant();
        Assert.Equal(expected, await Hashing.Sha256FileAsync(result.FinalFilePath!));
    }

    [Fact]
    public async Task DownloadsThroughAuthenticatedSocks5Proxy()
    {
        const int size = 524_288;
        _socks5.ResetStats();
        using var ctx = new EngineContext();
        var job = ctx.CreateJob(
            $"{_server.BaseAddress}/file/socks5.bin?size={size}&seed=75",
            "socks5.bin");
        job.RequestedConnections = 4;

        using var engine = CreateEngine(proxy: new ProxySettings
        {
            Mode = "manual",
            Address = $"socks5://{_socks5.Address}",
            Username = "socksuser",
            Password = "sockspass",
        });
        var result = await engine.ExecuteAsync(job, null, null, CancellationToken.None);

        Assert.True(result.Success, result.Failure?.Message);
        Assert.True(_socks5.Authorized > 0);
        Assert.True(_socks5.Connects > 0);
        Assert.Equal(0, _socks5.Denied);
        var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            TestContent.Generate(75, size))).ToLowerInvariant();
        Assert.Equal(expected, await Hashing.Sha256FileAsync(result.FinalFilePath!));
    }

    [Fact]
    public async Task Socks5RejectsWrongCredentials()
    {
        _socks5.ResetStats();
        using var ctx = new EngineContext();
        var job = ctx.CreateJob(
            $"{_server.BaseAddress}/file/socks5-bad.bin?size=65536&seed=76",
            "socks5-bad.bin");

        using var engine = CreateEngine(
            maxAttempts: 1,
            proxy: new ProxySettings
            {
                Mode = "manual",
                Address = $"socks5://{_socks5.Address}",
                Username = "socksuser",
                Password = "wrong-password",
            });
        var result = await engine.ExecuteAsync(job, null, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(_socks5.Denied > 0);
    }

    [Fact]
    public async Task ProxyRejectsWrongCredentials()
    {
        _proxy.ResetStats();
        using var ctx = new EngineContext();
        var job = ctx.CreateJob(
            $"{_server.BaseAddress}/file/proxy-bad.bin?size=65536&seed=72",
            "proxy-bad.bin");

        using var engine = CreateEngine(
            maxAttempts: 1,
            proxy: new ProxySettings
            {
                Mode = "manual",
                Address = _proxy.Address,
                Username = "proxyuser",
                Password = "wrong-password",
            });
        var result = await engine.ExecuteAsync(job, null, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(_proxy.Denied > 0);
        Assert.NotEqual(DownloadState.Completed, result.FinalState);
    }

    [Fact]
    public async Task CrossOriginRedirectDropsSensitiveHeaders()
    {
        await using var target = await TestServerHost.StartAsync();
        _proxy.ResetStats();
        using var ctx = new EngineContext();
        var redirect = Uri.EscapeDataString($"{target.BaseAddress}/sensitive/echo");
        var job = ctx.CreateJob(
            $"{_server.BaseAddress}/redirect?to={redirect}",
            "cross-origin.json");
        job.Referrer = $"{_server.BaseAddress}/page";
        job.RequestHeaders["Cookie"] = "session=secret";
        job.RequestHeaders["Authorization"] = "Bearer secret";

        using var engine = CreateEngine(proxy: ProxyCredentials());
        var result = await engine.ExecuteAsync(job, null, null, CancellationToken.None);

        Assert.True(result.Success, result.Failure?.Message);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(result.FinalFilePath!));
        var root = json.RootElement;
        Assert.False(root.GetProperty("cookie").GetBoolean());
        Assert.False(root.GetProperty("authorization").GetBoolean());
        Assert.False(root.GetProperty("referer").GetBoolean());
        Assert.True(_proxy.Authorized > 0);
    }

    [Fact]
    public async Task SameOriginRedirectKeepsSensitiveHeaders()
    {
        _proxy.ResetStats();
        using var ctx = new EngineContext();
        var job = ctx.CreateJob(
            $"{_server.BaseAddress}/redirect?to=%2Fsensitive%2Fecho",
            "same-origin.json");
        job.Referrer = $"{_server.BaseAddress}/page";
        job.RequestHeaders["Cookie"] = "session=secret";
        job.RequestHeaders["Authorization"] = "Bearer secret";

        using var engine = CreateEngine(proxy: ProxyCredentials());
        var result = await engine.ExecuteAsync(job, null, null, CancellationToken.None);

        Assert.True(result.Success, result.Failure?.Message);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(result.FinalFilePath!));
        var root = json.RootElement;
        Assert.True(root.GetProperty("cookie").GetBoolean());
        Assert.True(root.GetProperty("authorization").GetBoolean());
        Assert.True(root.GetProperty("referer").GetBoolean());
        Assert.True(_proxy.Authorized > 0);
    }

    private ProxySettings ProxyCredentials()
        => new()
        {
            Mode = "manual",
            Address = _proxy.Address,
            Username = "proxyuser",
            Password = "proxypass",
        };

    [Fact]
    public async Task DownloadsWithoutRangeSupport()
    {
        using var ctx = new EngineContext();
        var job = ctx.CreateJob($"{_server.BaseAddress}/norange/norange.bin?size=65536&seed=3", "norange.bin");

        using var engine = CreateEngine();
        var result = await engine.ExecuteAsync(job, null, null, CancellationToken.None);

        Assert.True(result.Success, result.Failure?.Message);
        Assert.Equal(
            await Hashing.Sha256FileAsync(WriteReference(ctx.Root, "reference.bin", TestContent.Generate(3, 65536))),
            await Hashing.Sha256FileAsync(result.FinalFilePath!));
    }

    [Fact]
    public async Task SegmentedDownloadUsesMultipleConnectionsAndMatchesHash()
    {
        const int size = 2 * 1024 * 1024;
        _server.ResetConcurrency();
        using var ctx = new EngineContext();
        var job = ctx.CreateJob($"{_server.BaseAddress}/file/segmented.bin?size={size}&seed=21&bps=1048576", "segmented.bin");
        job.RequestedConnections = 4;

        using var engine = CreateEngine();
        var result = await engine.ExecuteAsync(job, null, null, CancellationToken.None);

        Assert.True(result.Success, result.Failure?.Message);
        Assert.True(_server.PeakConcurrentRangeRequests >= 2,
            $"expected concurrent range requests, saw {_server.PeakConcurrentRangeRequests}");
        var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            TestContent.Generate(21, size))).ToLowerInvariant();
        Assert.Equal(expected, await Hashing.Sha256FileAsync(result.FinalFilePath!));
    }

    [Fact]
    public async Task SegmentedDownloadResumesFromCheckpoint()
    {
        const int size = 2 * 1024 * 1024;
        using var ctx = new EngineContext();
        var job = ctx.CreateJob($"{_server.BaseAddress}/file/segmented-resume.bin?size={size}&seed=22&bps=524288", "segmented-resume.bin");
        job.RequestedConnections = 4;
        var store = new JsonCheckpointStore(ctx.StoragePaths);
        using var cts = new CancellationTokenSource();
        var cancelled = false;

        using (var engine = CreateEngine(store))
        {
            var progress = new InlineProgress<TransferProgress>(p =>
            {
                if (!cancelled && p.ReceivedBytes >= 131_072)
                {
                    cancelled = true;
                    cts.Cancel();
                }
            });
            var first = await engine.ExecuteAsync(job, null, progress, cts.Token);
            Assert.False(first.Success);
        }

        var checkpoint = await store.LoadAsync(job.Id);
        Assert.NotNull(checkpoint);
        Assert.True(checkpoint!.Segments.Count > 1);
        Assert.True(checkpoint.CompletedBytes > 0);
        Assert.True(File.Exists(job.PartFilePath));

        using (var engine = CreateEngine(store))
        {
            var second = await engine.ExecuteAsync(job, checkpoint, null, CancellationToken.None);
            Assert.True(second.Success, second.Failure?.Message);
            var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                TestContent.Generate(22, size))).ToLowerInvariant();
            Assert.Equal(expected, await Hashing.Sha256FileAsync(second.FinalFilePath!));
        }
    }

    [Fact]
    public async Task SegmentedRequestFallsBackWhenServerIgnoresRanges()
    {
        const int size = 2 * 1024 * 1024;
        using var ctx = new EngineContext();
        var job = ctx.CreateJob($"{_server.BaseAddress}/norange/segmented-nr.bin?size={size}&seed=23&bps=2097152", "segmented-nr.bin");
        job.RequestedConnections = 4;

        using var engine = CreateEngine();
        var result = await engine.ExecuteAsync(job, null, null, CancellationToken.None);

        Assert.True(result.Success, result.Failure?.Message);
        var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            TestContent.Generate(23, size))).ToLowerInvariant();
        Assert.Equal(expected, await Hashing.Sha256FileAsync(result.FinalFilePath!));
    }

    [Fact]
    public async Task DownloadsUnknownLength()
    {
        using var ctx = new EngineContext();
        var job = ctx.CreateJob($"{_server.BaseAddress}/unknown/unknown.bin?size=65536&seed=4", "unknown.bin");

        using var engine = CreateEngine();
        var result = await engine.ExecuteAsync(job, null, null, CancellationToken.None);

        Assert.True(result.Success, result.Failure?.Message);
        Assert.Null(result.TotalBytes);
        Assert.Equal(65536, result.BytesWritten);
    }

    [Fact]
    public async Task SendsAuthenticationCookieAndCustomHeaders()
    {
        using var ctx = new EngineContext();
        var job = ctx.CreateJob($"{_server.BaseAddress}/header/required", "authed.txt");
        job.RequestHeaders["X-Test-Token"] = "expected";

        using var engine = CreateEngine();
        var result = await engine.ExecuteAsync(job, null, null, CancellationToken.None);

        Assert.True(result.Success, result.Failure?.Message);
        Assert.Equal("header ok", await File.ReadAllTextAsync(result.FinalFilePath!));
    }

    [Fact]
    public async Task ReportsBasicAuthenticationFailure()
    {
        using var ctx = new EngineContext();
        var job = ctx.CreateJob($"{_server.BaseAddress}/auth/basic?user=u&pass=p", "auth.txt");

        using var engine = CreateEngine();
        var result = await engine.ExecuteAsync(job, null, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(FailureKind.Unauthorized, result.Failure!.Kind);
        Assert.False(Directory.EnumerateFiles(ctx.Destination, "*.part").Any());
    }

    [Fact]
    public async Task ReportsHttpDnsTlsAndTimeoutFailureClasses()
    {
        using var ctx = new EngineContext();

        using (var engine = CreateEngine(maxAttempts: 1))
        {
            var result = await engine.ExecuteAsync(
                ctx.CreateJob($"{_server.BaseAddress}/error/404", "not-found.bin"),
                null, null, CancellationToken.None);
            Assert.False(result.Success);
            Assert.Equal(FailureKind.NotFound, result.Failure!.Kind);
            Assert.False(result.Failure.IsRetryable);
        }

        using (var engine = CreateEngine(maxAttempts: 2))
        {
            var result = await engine.ExecuteAsync(
                ctx.CreateJob($"{_server.BaseAddress}/error/500", "server-error.bin"),
                null, null, CancellationToken.None);
            Assert.False(result.Success);
            Assert.Equal(FailureKind.Http, result.Failure!.Kind);
            Assert.True(result.Failure.IsRetryable);
        }

        using (var engine = CreateEngine(maxAttempts: 1, connectTimeout: TimeSpan.FromSeconds(3)))
        {
            var result = await engine.ExecuteAsync(
                ctx.CreateJob("http://download-manager-no-such-host.invalid/file.bin", "dns.bin"),
                null, null, CancellationToken.None);
            Assert.False(result.Success);
            _output.WriteLine($"dns failure: {result.Failure!.Kind} {result.Failure.Message}");
            Assert.True(result.Failure.Kind is FailureKind.Dns or FailureKind.Network,
                $"unexpected DNS failure: {result.Failure.Kind} {result.Failure.Message}");
        }

        using (var engine = CreateEngine(maxAttempts: 1, connectTimeout: TimeSpan.FromSeconds(3)))
        {
            var port = new Uri(_server.BaseAddress).Port;
            var result = await engine.ExecuteAsync(
                ctx.CreateJob($"https://127.0.0.1:{port}/file/tls.bin?size=1024", "tls.bin"),
                null, null, CancellationToken.None);
            Assert.False(result.Success);
            _output.WriteLine($"tls failure: {result.Failure!.Kind} {result.Failure.Message}");
            Assert.True(result.Failure.Kind is FailureKind.Tls or FailureKind.Network,
                $"unexpected TLS failure: {result.Failure.Kind} {result.Failure.Message}");
        }

        using (var engine = CreateEngine(
                   maxAttempts: 1, readTimeout: TimeSpan.FromMilliseconds(250)))
        {
            var result = await engine.ExecuteAsync(
                ctx.CreateJob(
                    $"{_server.BaseAddress}/slow/timeout.bin?size=1048576&seed=12&bps=1024",
                    "timeout.bin"),
                null, null, CancellationToken.None);
            Assert.False(result.Success);
            Assert.Equal(FailureKind.Timeout, result.Failure!.Kind);
        }
    }

    [Fact]
    public async Task RetriesTransientServerErrors()
    {
        using var ctx = new EngineContext();
        const int size = 65_536;
        using var engine = CreateEngine(maxAttempts: 4);
        var result = await engine.ExecuteAsync(
            ctx.CreateJob(
                $"{_server.BaseAddress}/flaky/retry.bin?size={size}&seed=16&failures=2",
                "retry.bin"),
            null, null, CancellationToken.None);

        Assert.True(result.Success, result.Failure?.Message);
        Assert.Equal(size, new FileInfo(result.FinalFilePath!).Length);
        Assert.Equal(3, _server.FlakyRequests("retry.bin"));
        Assert.Equal(
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                TestContent.Generate(16, size))).ToLowerInvariant(),
            await Hashing.Sha256FileAsync(result.FinalFilePath!));
    }

    [Fact]
    public async Task PerDownloadSpeedLimitIsApplied()
    {
        using var ctx = new EngineContext();
        const int size = 524_288;
        var job = ctx.CreateJob(
            $"{_server.BaseAddress}/file/speed.bin?size={size}&seed=17",
            "speed.bin");
        job.SpeedLimitBytesPerSecond = 131_072;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        using var engine = CreateEngine();
        var result = await engine.ExecuteAsync(job, null, null, CancellationToken.None);
        stopwatch.Stop();

        Assert.True(result.Success, result.Failure?.Message);
        Assert.InRange(stopwatch.Elapsed.TotalSeconds, 3.2, 8.0);
        Assert.Equal(size, new FileInfo(result.FinalFilePath!).Length);
        Assert.Equal(
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                TestContent.Generate(17, size))).ToLowerInvariant(),
            await Hashing.Sha256FileAsync(result.FinalFilePath!));
    }

    [Fact]
    public async Task HtmlPageIsSavedOnlyForHtmlLikeTargets()
    {
        using var ctx = new EngineContext();
        var htmlJob = ctx.CreateJob($"{_server.BaseAddress}/html", "legit.html");
        using (var engine = CreateEngine())
        {
            var result = await engine.ExecuteAsync(htmlJob, null, null, CancellationToken.None);
            Assert.True(result.Success, result.Failure?.Message);
            var text = await File.ReadAllTextAsync(result.FinalFilePath!);
            Assert.Contains("downloadable html", text);
        }

        using (var engine = CreateEngine())
        {
            var result = await engine.ExecuteAsync(
                ctx.CreateJob($"{_server.BaseAddress}/html", "error-page.bin"),
                null, null, CancellationToken.None);
            Assert.False(result.Success);
            Assert.Equal(FailureKind.InvalidContent, result.Failure!.Kind);
            Assert.Contains("HTML page", result.Failure.Message);
            Assert.False(File.Exists(Path.Combine(ctx.Destination, "error-page.bin")));
        }
    }

    [Fact]
    public async Task ProgressReportsAreMonotonicAndComplete()
    {
        using var ctx = new EngineContext();
        const int size = 1_048_576;
        var reports = new List<TransferProgress>();
        using var engine = CreateEngine();
        var result = await engine.ExecuteAsync(
            ctx.CreateJob(
                $"{_server.BaseAddress}/slow/progress.bin?size={size}&seed=13&bps=262144",
                "progress.bin"),
            null,
            new InlineProgress<TransferProgress>(reports.Add),
            CancellationToken.None);

        Assert.True(result.Success, result.Failure?.Message);
        Assert.True(reports.Count >= 3, $"expected multiple progress reports, got {reports.Count}");
        for (var i = 1; i < reports.Count; i++)
        {
            Assert.True(reports[i].ReceivedBytes >= reports[i - 1].ReceivedBytes,
                "progress bytes decreased");
        }

        var last = reports[^1];
        Assert.Equal(size, last.ReceivedBytes);
        Assert.True(last.TotalBytes is null or size);
        Assert.True(reports.Any(report => report.BytesPerSecond > 0));
        Assert.All(reports, report =>
        {
            Assert.True(report.EstimatedRemaining is null ||
                        report.EstimatedRemaining >= TimeSpan.Zero);
            Assert.True(report.EstimatedRemaining is null ||
                        report.EstimatedRemaining < TimeSpan.FromMinutes(5));
        });
    }

    [Fact]
    public async Task TruncatedResponseNeverReportsSuccess()
    {
        using var ctx = new EngineContext();
        var job = ctx.CreateJob($"{_server.BaseAddress}/truncate/truncated.bin?size=65536&cut=1000", "truncated.bin");

        using var engine = CreateEngine(maxAttempts: 2);
        var result = await engine.ExecuteAsync(job, null, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotEqual(DownloadState.Completed, result.FinalState);
        Assert.False(File.Exists(Path.Combine(ctx.Destination, "truncated.bin")));
        _output.WriteLine($"failed as expected: {result.Failure?.Kind} {result.Failure?.Message}");
    }

    [Fact]
    public async Task AlwaysBadRangeFailsWithoutCorruption()
    {
        using var ctx = new EngineContext();
        var job = ctx.CreateJob($"{_server.BaseAddress}/badrange/bad.bin?size=65536", "bad.bin");

        using var engine = CreateEngine(maxAttempts: 2);
        var result = await engine.ExecuteAsync(job, null, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(FailureKind.InvalidContent, result.Failure!.Kind);
        Assert.False(File.Exists(Path.Combine(ctx.Destination, "bad.bin")));
    }

    [Fact]
    public async Task ResumeAfterCancellationMatchesHash()
    {
        var size = 524_288;
        using var ctx = new EngineContext();
        var job = ctx.CreateJob($"{_server.BaseAddress}/slow/resume.bin?size={size}&bps=262144", "resume.bin");
        var store = new JsonCheckpointStore(ctx.StoragePaths);
        using var cts = new CancellationTokenSource();
        var cancelled = false;

        using (var engine = CreateEngine(store))
        {
            var progress = new InlineProgress<TransferProgress>(p =>
            {
                if (!cancelled && p.ReceivedBytes >= 131_072)
                {
                    cancelled = true;
                    cts.Cancel();
                }
            });
            var first = await engine.ExecuteAsync(job, null, progress, cts.Token);
            Assert.False(first.Success);
            Assert.True(cancelled);
        }

        var checkpoint = await store.LoadAsync(job.Id);
        Assert.NotNull(checkpoint);
        Assert.True(checkpoint!.CompletedBytes > 0);
        _output.WriteLine($"cancelled at {checkpoint.CompletedBytes} bytes");

        using (var engine = CreateEngine(store))
        {
            var second = await engine.ExecuteAsync(job, checkpoint, null, CancellationToken.None);
            Assert.True(second.Success, second.Failure?.Message);
            var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                TestContent.Generate(1, size))).ToLowerInvariant();
            Assert.Equal(expected, await Hashing.Sha256FileAsync(second.FinalFilePath!));
        }

        Assert.Null(await store.LoadAsync(job.Id));
    }

    [Fact]
    public async Task ServerIgnoringRangeRestartsFromZero()
    {
        var size = 262_144;
        using var ctx = new EngineContext();
        var job = ctx.CreateJob($"{_server.BaseAddress}/norange/resume-nr.bin?size={size}&seed=9&bps=524288", "resume-nr.bin");
        var store = new JsonCheckpointStore(ctx.StoragePaths);
        using var cts = new CancellationTokenSource();
        var cancelled = false;

        using (var engine = CreateEngine(store))
        {
            var progress = new InlineProgress<TransferProgress>(p =>
            {
                if (!cancelled && p.ReceivedBytes > 65_536)
                {
                    cancelled = true;
                    cts.Cancel();
                }
            });
            var first = await engine.ExecuteAsync(job, null, progress, cts.Token);
            Assert.False(first.Success);
        }

        var checkpoint = await store.LoadAsync(job.Id);
        Assert.NotNull(checkpoint);
        using (var engine = CreateEngine(store))
        {
            var second = await engine.ExecuteAsync(job, checkpoint, null, CancellationToken.None);
            Assert.True(second.Success, second.Failure?.Message);
            var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                TestContent.Generate(9, size))).ToLowerInvariant();
            Assert.Equal(expected, await Hashing.Sha256FileAsync(second.FinalFilePath!));
        }
    }

    [Fact]
    public async Task ChangedRemoteContentRestartsSafely()
    {
        var size = 2_097_152;
        using var ctx = new EngineContext();
        var job = ctx.CreateJob($"{_server.BaseAddress}/changed/changing.bin?size={size}&bps=524288", "changing.bin");
        var store = new JsonCheckpointStore(ctx.StoragePaths);
        using var cts = new CancellationTokenSource();
        var cancelled = false;

        using (var engine = CreateEngine(store))
        {
            var progress = new InlineProgress<TransferProgress>(p =>
            {
                if (!cancelled && p.ReceivedBytes >= 262_144)
                {
                    cancelled = true;
                    cts.Cancel();
                }
            });
            var first = await engine.ExecuteAsync(job, null, progress, cts.Token);
            Assert.False(first.Success);
        }

        var checkpoint = await store.LoadAsync(job.Id);
        Assert.NotNull(checkpoint);
        _server.SetVersion("changing.bin", 2);

        using (var engine = CreateEngine(store))
        {
            var second = await engine.ExecuteAsync(job, checkpoint, null, CancellationToken.None);
            Assert.True(second.Success, second.Failure?.Message);
            var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                TestContent.Generate(2, size))).ToLowerInvariant();
            Assert.Equal(expected, await Hashing.Sha256FileAsync(second.FinalFilePath!));
        }
    }

    private HttpDownloadEngine CreateEngine(
        JsonCheckpointStore? store = null,
        int maxAttempts = 3,
        ProxySettings? proxy = null,
        TimeSpan? readTimeout = null,
        TimeSpan? connectTimeout = null)
        => new(
            new EngineOptions
            {
                MaxAttempts = maxAttempts,
                InitialRetryDelay = TimeSpan.FromMilliseconds(10),
                MaxRetryDelay = TimeSpan.FromMilliseconds(50),
                ReadTimeout = readTimeout ?? TimeSpan.FromSeconds(10),
                ConnectTimeout = connectTimeout ?? TimeSpan.FromSeconds(5),
                AllowLocalNetwork = true,
                Proxy = proxy ?? new ProxySettings(),
            },
            checkpoints: store);

    private static string WriteReference(string root, string name, byte[] bytes)
    {
        var path = Path.Combine(root, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private sealed class InlineProgress<T>(Action<T> action) : IProgress<T>
    {
        public void Report(T value) => action(value);
    }

    private sealed class EngineContext : IDisposable
    {
        public EngineContext()
        {
            Root = Path.Combine(AppContext.BaseDirectory, "test-work", Guid.NewGuid().ToString("N"));
            Destination = Path.Combine(Root, "downloads");
            StoragePaths = new StoragePaths(Path.Combine(Root, "state"));
            Directory.CreateDirectory(Destination);
            StoragePaths.EnsureCreated();
        }

        public string Root { get; }
        public string Destination { get; }
        public StoragePaths StoragePaths { get; }

        public DownloadJob CreateJob(string url, string fileName)
            => new()
            {
                Url = url,
                FileName = fileName,
                DestinationDirectory = Destination,
                FinalFilePath = Path.Combine(Destination, fileName),
                PartFilePath = Path.Combine(Destination, fileName) + ".part",
                RequestedConnections = 1,
                State = DownloadState.Queued,
            };

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
                // best effort
            }
        }
    }
}
