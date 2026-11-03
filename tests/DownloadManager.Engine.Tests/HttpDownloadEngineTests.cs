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

    public async Task InitializeAsync() => Server = await TestServerHost.StartAsync();

    public async Task DisposeAsync() => await Server.DisposeAsync();
}

[Collection("http-server")]
public sealed class HttpDownloadEngineTests
{
    private readonly TestServerHost _server;
    private readonly ITestOutputHelper _output;

    public HttpDownloadEngineTests(TestServerFixture fixture, ITestOutputHelper output)
    {
        _server = fixture.Server;
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

    private HttpDownloadEngine CreateEngine(JsonCheckpointStore? store = null, int maxAttempts = 3)
        => new(
            new EngineOptions
            {
                MaxAttempts = maxAttempts,
                InitialRetryDelay = TimeSpan.FromMilliseconds(10),
                MaxRetryDelay = TimeSpan.FromMilliseconds(50),
                ReadTimeout = TimeSpan.FromSeconds(10),
                ConnectTimeout = TimeSpan.FromSeconds(5),
                AllowLocalNetwork = true,
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
