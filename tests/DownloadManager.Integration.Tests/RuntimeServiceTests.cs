using DownloadManager.Contracts;
using DownloadManager.Core;
using DownloadManager.Persistence;
using DownloadManager.Runtime;
using TestServer;

namespace DownloadManager.Integration.Tests;

[CollectionDefinition("runtime-http")]
public sealed class RuntimeHttpCollection : ICollectionFixture<RuntimeServerFixture>;

public sealed class RuntimeServerFixture : IAsyncLifetime
{
    public TestServerHost Server { get; private set; } = null!;
    public Task InitializeAsync() => Start();
    public async Task DisposeAsync() => await Server.DisposeAsync();

    private async Task Start() => Server = await TestServerHost.StartAsync();
}

[Collection("runtime-http")]
public sealed class RuntimeServiceTests
{
    private readonly TestServerHost _server;

    public RuntimeServiceTests(RuntimeServerFixture fixture)
    {
        _server = fixture.Server;
    }

    [Fact]
    public async Task AddWaitAndVerifyHash()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        const int size = 262_144;
        var response = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/file/service.bin?size={size}&seed=12",
            DestinationDirectory = ctx.Destination,
            FileName = "service.bin",
            Source = "cli",
        });

        Assert.True(response.Accepted, response.Error);
        var job = await ctx.WaitFor(response.Job!.Id, j => j.State == "completed");
        Assert.Equal(size, job.ReceivedBytes);
        var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            TestContent.Generate(12, size))).ToLowerInvariant();
        Assert.Equal(expected, await Hashing.Sha256FileAsync(job.FinalFilePath));
    }

    [Fact]
    public async Task DuplicateHandoffCreatesOneJob()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var request = new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/file/dup.bin?size=65536&seed=2",
            DestinationDirectory = ctx.Destination,
            FileName = "dup.bin",
            Source = "chromeAuto",
            OwnerExtensionId = "test-extension",
            ClientRequestId = "request-1",
        };

        var first = await ctx.Service.AddJobAsync(request);
        var second = await ctx.Service.AddJobAsync(request);

        Assert.True(first.Accepted, first.Error);
        Assert.True(second.Accepted, second.Error);
        Assert.True(second.Duplicate);
        Assert.Equal(first.Job!.Id, second.Job!.Id);
        Assert.Single(ctx.Service.GetSnapshot().Jobs.Where(j => j.ClientRequestId == "request-1"));
    }

    [Fact]
    public async Task PauseAndResumePreservesBytes()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        const int size = 1_048_576;
        var response = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/slow/pause.bin?size={size}&seed=5&bps=262144",
            DestinationDirectory = ctx.Destination,
            FileName = "pause.bin",
            Source = "cli",
        });

        var job = await ctx.WaitFor(response.Job!.Id, j => j.ReceivedBytes > 65_536);
        var pause = await ctx.Service.PauseAsync(job.Id);
        Assert.True(pause.Ok, pause.Error);
        await ctx.WaitFor(job.Id, j => j.State == "paused");
        var paused = ctx.Service.GetJob(job.Id)!;
        Assert.True(paused.ReceivedBytes > 0);

        var resume = await ctx.Service.ResumeAsync(job.Id);
        Assert.True(resume.Ok, resume.Error);
        var completed = await ctx.WaitFor(job.Id, j => j.State == "completed");
        Assert.Equal(size, completed.ReceivedBytes);
        var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            TestContent.Generate(5, size))).ToLowerInvariant();
        Assert.Equal(expected, await Hashing.Sha256FileAsync(completed.FinalFilePath));
    }

    [Fact]
    public async Task RestartServiceAndResumeFromDisk()
    {
        var ctx = await ServiceContext.CreateAsync(_server, autoResume: false);
        try
        {
            const int size = 1_048_576;
            var response = await ctx.Service.AddJobAsync(new AddJobRequest
            {
                Url = $"{_server.BaseAddress}/slow/restart.bin?size={size}&seed=6&bps=262144",
                DestinationDirectory = ctx.Destination,
                FileName = "restart.bin",
                Source = "cli",
            });
            var job = await ctx.WaitFor(response.Job!.Id, j => j.ReceivedBytes > 65_536);
            await ctx.Service.PauseAsync(job.Id);
            await ctx.WaitFor(job.Id, j => j.State == "paused");
            var pausedBytes = ctx.Service.GetJob(job.Id)!.ReceivedBytes;
            await Task.Delay(300);

            await using var second = await ServiceContext.CreateAsync(_server, ctx.Paths, ctx.Destination, autoResume: false);
            var reloaded = second.Service.GetJob(job.Id);
            Assert.NotNull(reloaded);
            Assert.Equal("paused", reloaded!.State);
            Assert.True(reloaded.ReceivedBytes > 0);
            Assert.True(reloaded.ReceivedBytes >= pausedBytes);

            var resume = await second.Service.ResumeAsync(job.Id);
            Assert.True(resume.Ok, resume.Error);
            var completed = await second.WaitFor(job.Id, j => j.State == "completed");
            var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                TestContent.Generate(6, size))).ToLowerInvariant();
            Assert.Equal(expected, await Hashing.Sha256FileAsync(completed.FinalFilePath));
        }
        finally
        {
            await ctx.DisposeAsync();
        }
    }

    [Fact]
    public async Task ConcurrencyLimitIsObserved()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server, maxConcurrent: 1);
        var first = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/slow/c1.bin?size=524288&seed=7&bps=524288",
            DestinationDirectory = ctx.Destination,
            FileName = "c1.bin",
            Source = "cli",
        });
        var second = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/slow/c2.bin?size=524288&seed=8&bps=524288",
            DestinationDirectory = ctx.Destination,
            FileName = "c2.bin",
            Source = "cli",
        });

        await Task.Delay(250);
        var snapshot = ctx.Service.GetSnapshot();
        var running = snapshot.Jobs.Count(j => j.State is "connecting" or "downloading");
        Assert.InRange(running, 0, 1);

        await ctx.WaitFor(first.Job!.Id, j => j.State == "completed", TimeSpan.FromSeconds(30));
        await ctx.WaitFor(second.Job!.Id, j => j.State == "completed", TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task CancelDeletesPartialPayload()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var response = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/slow/cancel.bin?size=1048576&seed=9&bps=262144",
            DestinationDirectory = ctx.Destination,
            FileName = "cancel.bin",
            Source = "cli",
        });

        await ctx.WaitFor(response.Job!.Id, j => j.ReceivedBytes > 65536);
        var cancel = await ctx.Service.CancelAsync(response.Job.Id, deleteFile: true);
        Assert.True(cancel.Ok, cancel.Error);
        await Task.Delay(500);
        Assert.False(File.Exists(Path.Combine(ctx.Destination, "cancel.bin")));
        Assert.Empty(Directory.EnumerateFiles(ctx.Paths.PartialsDirectory, "*.part"));
    }

    private sealed class ServiceContext : IAsyncDisposable
    {
        private readonly HttpDownloadEngine _engine;
        private readonly bool _ownsPaths;

        private ServiceContext(
            StoragePaths paths,
            string destination,
            DownloadManagerService service,
            HttpDownloadEngine engine,
            bool ownsPaths)
        {
            Paths = paths;
            Destination = destination;
            Service = service;
            _engine = engine;
            _ownsPaths = ownsPaths;
        }

        public StoragePaths Paths { get; }
        public string Destination { get; }
        public DownloadManagerService Service { get; }
        public bool AutoResume { get; private set; }

        public static Task<ServiceContext> CreateAsync(
            TestServerHost server,
            bool autoResume = true,
            int maxConcurrent = 2)
        {
            var root = Path.Combine(AppContext.BaseDirectory, "test-work", Guid.NewGuid().ToString("N"));
            var paths = new StoragePaths(Path.Combine(root, "state"));
            var destination = Path.Combine(root, "downloads");
            Directory.CreateDirectory(destination);
            paths.EnsureCreated();
            return BuildAsync(server, paths, destination, autoResume, maxConcurrent, ownsPaths: true);
        }

        public static Task<ServiceContext> CreateAsync(
            TestServerHost server,
            StoragePaths paths,
            string destination,
            bool autoResume = true)
            => BuildAsync(server, paths, destination, autoResume, 2, ownsPaths: false);

        private static async Task<ServiceContext> BuildAsync(
            TestServerHost server,
            StoragePaths paths,
            string destination,
            bool autoResume,
            int maxConcurrent,
            bool ownsPaths)
        {
            var checkpoints = new JsonCheckpointStore(paths);
            var store = new JsonJobStore(paths);
            var settingsStore = new JsonSettingsStore(paths);
            var engine = new HttpDownloadEngine(
                new EngineOptions
                {
                    MaxAttempts = 3,
                    InitialRetryDelay = TimeSpan.FromMilliseconds(20),
                    MaxRetryDelay = TimeSpan.FromMilliseconds(100),
                    ReadTimeout = TimeSpan.FromSeconds(15),
                    AllowLocalNetwork = true,
                },
                checkpoints: checkpoints);
            var service = new DownloadManagerService(paths, store, checkpoints, settingsStore, engine);
            await service.InitializeAsync();

            var settings = service.GetSnapshot().Settings;
            settings.DefaultDownloadDirectory = destination;
            settings.MaxConcurrentDownloads = maxConcurrent;
            settings.DefaultConnections = 1;
            settings.AutoResumeUnfinishedOnStartup = autoResume;
            var update = await service.UpdateSettingsAsync(settings);
            if (!update.Ok)
            {
                throw new InvalidOperationException(update.Error);
            }

            return new ServiceContext(paths, destination, service, engine, ownsPaths);
        }

        public async Task<JobDto> WaitFor(Guid id, Func<JobDto, bool> predicate, TimeSpan? timeout = null)
        {
            var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
            while (DateTimeOffset.UtcNow < deadline)
            {
                var job = Service.GetJob(id);
                if (job is not null && predicate(job))
                {
                    return job;
                }

                await Task.Delay(50);
            }

            var current = Service.GetJob(id);
            throw new TimeoutException($"Job did not reach the expected state. Current: {current?.State} {current?.Failure?.Message}");
        }

        public async ValueTask DisposeAsync()
        {
            await Service.ShutdownAsync();
            _engine.Dispose();
            if (_ownsPaths)
            {
                try
                {
                    var root = Path.GetDirectoryName(Paths.Root);
                    if (root is not null && Directory.Exists(root))
                    {
                        Directory.Delete(root, recursive: true);
                    }
                }
                catch (IOException)
                {
                    // best effort
                }
            }
        }
    }
}
