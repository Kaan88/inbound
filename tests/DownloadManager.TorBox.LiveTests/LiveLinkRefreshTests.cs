using System.Security.Cryptography;
using DownloadManager.Contracts;
using DownloadManager.Core;
using DownloadManager.Core.Providers;
using DownloadManager.Persistence;
using DownloadManager.Providers.TorBox;
using DownloadManager.Runtime;
using DownloadManager.Runtime.Remote;

namespace DownloadManager.TorBox.LiveTests;

/// <summary>
/// A direct link the real CDN refuses, and a real fresh one from the real API, through the real
/// service, broker and engine. What isn't real: this class swaps the token of the links it is told to
/// spoil, because a real link wasn't seen to expire in hours of watching, and a provider job keeps no
/// link on disk to tamper with. Only reads the account: <c>requestdl</c> for a small file already there.
/// </summary>
[Trait("Category", LiveAccount.ReadTrait)]
public sealed class LiveLinkRefreshTests(ITestOutputHelper output)
{
    private const long SmallestFile = 512 * 1024;
    private const long LargestFile = 8 * 1024 * 1024;

    private void Say(string text) => output.WriteLine(LiveAccount.Scrub(text));

    private sealed class SpoilingBroker(IRemoteLinkBroker inner) : IRemoteLinkBroker
    {
        private readonly object _sync = new();
        private readonly List<JobFailure> _failures = [];
        private int _spoilNext;
        private int _prepared;

        public int Prepared => Volatile.Read(ref _prepared);

        public List<JobFailure> Failures
        {
            get
            {
                lock (_sync) return [.. _failures];
            }
        }

        public void SpoilNext(int count) => Volatile.Write(ref _spoilNext, count);

        public async Task<RemoteLinkResolution> PrepareRunAsync(DownloadJob job, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _prepared);
            var real = await inner.PrepareRunAsync(job, cancellationToken).ConfigureAwait(false);
            if (real.Url is null || Interlocked.Decrement(ref _spoilNext) < 0)
            {
                Interlocked.Exchange(ref _spoilNext, 0);
                return real;
            }

            var spoiled = LiveAccount.WithQuery(new Uri(real.Url), "token=" + Guid.NewGuid());
            return RemoteLinkResolution.Link(spoiled.AbsoluteUri, real.ResetLocalData, real.ExpectedTotalBytes, real.ExpectedMd5, real.MaxConnections);
        }

        public Task<bool> ShouldRefreshAfterFailureAsync(DownloadJob job, JobFailure failure, CancellationToken cancellationToken)
        {
            lock (_sync) _failures.Add(failure);
            return inner.ShouldRefreshAfterFailureAsync(job, failure, cancellationToken);
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly string _root;

        private Rig(string root, DownloadManagerService service, SpoilingBroker broker, List<string> log, string destination)
        {
            _root = root;
            Service = service;
            Broker = broker;
            Log = log;
            Destination = destination;
        }

        public DownloadManagerService Service { get; }
        public SpoilingBroker Broker { get; }
        public List<string> Log { get; }
        public string Destination { get; }

        public static async Task<Rig> StartAsync(long? speedLimit)
        {
            var root = Path.Combine(AppContext.BaseDirectory, "test-work", Guid.NewGuid().ToString("N"));
            var paths = new StoragePaths(Path.Combine(root, "state"));
            var destination = Path.Combine(root, "downloads");
            Directory.CreateDirectory(destination);
            paths.EnsureCreated();

            var log = new List<string>();
            void Write(string line)
            {
                lock (log) log.Add(line);
            }

            var account = await RemoteAccountStore.OpenAsync(paths, TorBoxClient.ProviderId, Write);
            await account.SetKeyAsync(LiveAccount.Key!);
            var registry = new RemoteProviderRegistry();
            registry.Add(LiveAccount.Client(), account);
            var broker = new SpoilingBroker(new RemoteLinkBroker(registry, log: Write));

            var checkpoints = new JsonCheckpointStore(paths);
            var engine = new HttpDownloadEngine(new EngineOptions(), checkpoints: checkpoints, mediaCheckpoints: new JsonMediaCheckpointStore(paths));
            var service = new DownloadManagerService(
                paths,
                new JsonJobStore(paths),
                checkpoints,
                new JsonSettingsStore(paths),
                engine,
                log: Write,
                enableSchedulerTimer: false,
                mediaCheckpoints: new JsonMediaCheckpointStore(paths),
                linkBroker: broker,
                linkSecrets: registry);
            await service.InitializeAsync();

            var settings = service.GetSnapshot().Settings;
            settings.DefaultDownloadDirectory = destination;
            settings.MaxConcurrentDownloads = 1;
            settings.DefaultConnections = 8;
            settings.AutoResumeUnfinishedOnStartup = false;
            settings.GlobalSpeedLimitBytesPerSecond = speedLimit;
            Assert.True((await service.UpdateSettingsAsync(settings)).Ok);
            return new Rig(root, service, broker, log, destination);
        }

        public async Task<JobDto> AddAsync(RemoteItem item, RemoteFile file)
        {
            var source = new RemoteSource(
                TorBoxClient.ProviderId,
                RemoteKinds.ToWord(item.Ref.Kind),
                item.Ref.ItemId,
                file.FileId,
                "file.bin",
                "live-" + Guid.NewGuid().ToString("N"));
            var result = await Service.AddRemoteJobsAsync(
                [new RemoteJobRequest
                {
                    Source = source,
                    FileName = "file.bin",
                    DestinationRoot = Destination,
                    MaxConnections = 4,
                    ExpectedTotalBytes = file.Size,
                    ExpectedMd5 = file.Md5,
                }],
                "remote:" + source.RemoteJobId,
                "live");
            Assert.Null(result.Error);
            return Assert.Single(result.Items).Job!;
        }

        public async Task<JobDto> WaitForAsync(Guid id, Func<JobDto, bool> until, int seconds)
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(seconds);
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (Service.GetJob(id) is { } job && until(job)) return job;
                await Task.Delay(50);
            }

            var last = Service.GetJob(id);
            Assert.Fail($"Waited {seconds} s. State {last?.State}, {last?.ReceivedBytes} bytes, {LiveAccount.Scrub(last?.Failure?.Message)}");
            return last!;
        }

        public string[] LogLines()
        {
            lock (Log) return [.. Log];
        }

        public async ValueTask DisposeAsync()
        {
            await Service.ShutdownAsync();
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Under the build output. Nothing in it holds a secret in the clear.
            }
        }
    }

    private static async Task<(RemoteItem Item, RemoteFile File)?> SmallFileWithMd5Async()
    {
        var files = await LiveAccount.FetchableFilesAsync();
        return files.Where(pair => !string.IsNullOrWhiteSpace(pair.File.Md5) && pair.File.Size is >= SmallestFile and <= LargestFile)
            .Select(pair => ((RemoteItem Item, RemoteFile File)?)pair)
            .LastOrDefault();
    }

    private static string Md5Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(MD5.HashData(stream)).ToLowerInvariant();
    }

    private void SayLog(Rig rig)
    {
        foreach (var line in rig.LogLines().Where(line => line.Contains("link", StringComparison.OrdinalIgnoreCase))) Say("  log: " + line);
    }

    /// <summary>The whole log and the job's files on disk hold no sign of a link or the key.</summary>
    private static void AssertNoSecretLeft(Rig rig)
    {
        var key = LiveAccount.Key!;
        var lines = string.Join('\n', rig.LogLines());
        Assert.DoesNotContain(key, lines, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tb-cdn", lines, StringComparison.OrdinalIgnoreCase);
    }

    [LiveFact]
    public async Task ARefusedLinkAtTheStartGetsAFreshOneAndTheBytesAreRight()
    {
        if (await SmallFileWithMd5Async() is not { } pick)
        {
            Say("No file between 512 KiB and 8 MiB has an md5 on this account, so nothing could be compared.");
            return;
        }

        await using var rig = await Rig.StartAsync(speedLimit: null);
        rig.Broker.SpoilNext(1);
        var added = await rig.AddAsync(pick.Item, pick.File);
        var done = await rig.WaitForAsync(added.Id, job => job.State is "completed" or "failed", 180);

        Say($"state {done.State}, {done.ReceivedBytes} of {pick.File.Size}, links asked for {rig.Broker.Prepared}, refusals {string.Join(",", rig.Broker.Failures.Select(failure => $"{failure.Kind}/{failure.HttpStatus}"))}, connections at most {done.RequestedConnections}");
        SayLog(rig);
        Assert.Equal("completed", done.State);
        Assert.Equal(2, rig.Broker.Prepared);
        Assert.Equal(403, Assert.Single(rig.Broker.Failures).HttpStatus);
        Assert.True(done.RequestedConnections <= 4);
        Assert.Equal(pick.File.Size, new FileInfo(done.FinalFilePath).Length);
        Assert.Equal(pick.File.Md5, Md5Of(done.FinalFilePath), ignoreCase: true);
        AssertNoSecretLeft(rig);
    }

    [LiveFact]
    public async Task ARefusedLinkAfterAStopResumesOnAFreshOneWithoutStartingAgain()
    {
        if (await SmallFileWithMd5Async() is not { } pick)
        {
            Say("No file between 512 KiB and 8 MiB has an md5 on this account, so nothing could be compared.");
            return;
        }

        // Slow enough to stop partway on any file in the range.
        await using var rig = await Rig.StartAsync(speedLimit: Math.Max(64 * 1024, pick.File.Size / 20));
        var added = await rig.AddAsync(pick.Item, pick.File);
        await rig.WaitForAsync(added.Id, job => job.ReceivedBytes > pick.File.Size / 4, 120);
        Assert.True((await rig.Service.PauseAsync(added.Id)).Ok);
        var stopped = await rig.WaitForAsync(added.Id, job => job.State == "paused", 30);
        Say($"stopped at {stopped.ReceivedBytes} of {pick.File.Size}, links asked for so far {rig.Broker.Prepared}");
        Assert.InRange(stopped.ReceivedBytes, 1, pick.File.Size - 1);

        rig.Broker.SpoilNext(1);
        var lowest = long.MaxValue;
        void Watch(object? sender, JobChangeEventArgs change)
        {
            if (change.Job.Id == added.Id && change.Job.ReceivedBytes > 0) lowest = Math.Min(lowest, change.Job.ReceivedBytes);
        }

        rig.Service.JobChanged += Watch;
        Assert.True((await rig.Service.ResumeAsync(added.Id)).Ok);
        var done = await rig.WaitForAsync(added.Id, job => job.State is "completed" or "failed", 180);
        rig.Service.JobChanged -= Watch;

        Say($"state {done.State}, lowest count after the resume {lowest}, links asked for {rig.Broker.Prepared}, refusals {string.Join(",", rig.Broker.Failures.Select(failure => $"{failure.Kind}/{failure.HttpStatus}"))}, connections at most {done.RequestedConnections}");
        SayLog(rig);
        Assert.Equal("completed", done.State);
        Assert.Equal(3, rig.Broker.Prepared);
        Assert.Equal(403, Assert.Single(rig.Broker.Failures).HttpStatus);
        Assert.True(lowest >= stopped.ReceivedBytes * 9 / 10, $"Went back to {lowest} from {stopped.ReceivedBytes}.");
        Assert.True(done.RequestedConnections <= 4);
        Assert.Equal(pick.File.Size, new FileInfo(done.FinalFilePath).Length);
        Assert.Equal(pick.File.Md5, Md5Of(done.FinalFilePath), ignoreCase: true);
        AssertNoSecretLeft(rig);
    }

    [LiveFact]
    public async Task LinksThatKeepBeingRefusedStopTheJobWithItsBytesKept()
    {
        if (await SmallFileWithMd5Async() is not { } pick)
        {
            Say("No file between 512 KiB and 8 MiB has an md5 on this account, so nothing could be compared.");
            return;
        }

        await using var rig = await Rig.StartAsync(speedLimit: null);
        rig.Broker.SpoilNext(99);
        var added = await rig.AddAsync(pick.Item, pick.File);
        var failed = await rig.WaitForAsync(added.Id, job => job.State is "completed" or "failed", 120);

        Say($"state {failed.State}, links asked for {rig.Broker.Prepared}, words: {failed.Failure?.Message}");
        Assert.Equal("failed", failed.State);
        Assert.Equal(3, rig.Broker.Prepared);
        Assert.Contains("keep failing", failed.Failure!.Message, StringComparison.Ordinal);
        Assert.True(failed.CanResume);

        rig.Broker.SpoilNext(0);
        Assert.True((await rig.Service.ResumeAsync(added.Id)).Ok);
        var done = await rig.WaitForAsync(added.Id, job => job.State is "completed" or "failed", 180);
        Assert.Equal("completed", done.State);
        Assert.Equal(pick.File.Md5, Md5Of(done.FinalFilePath), ignoreCase: true);
        AssertNoSecretLeft(rig);
    }
}
