using DownloadManager.Contracts;
using DownloadManager.Core;
using DownloadManager.Core.Providers;
using DownloadManager.Persistence;
using DownloadManager.Providers.TorBox;
using DownloadManager.Runtime;
using DownloadManager.Runtime.Remote;
using TestServer;

namespace DownloadManager.Integration.Tests;

/// <summary>The real broker and the real client against the mock, driven through the service.</summary>
public sealed partial class RuntimeServiceTests
{
    private const string MockKey = MockTorBox.DefaultApiKey;
    private const int Mb = 1024 * 1024;

    private sealed class TorBoxRig : IAsyncDisposable
    {
        private readonly HttpClient _http;
        private readonly string _root;

        private TorBoxRig(MockTorBox mock, ServiceContext ctx, RemoteAccountStore account, RemoteLinkBroker broker, RemoteProviderRegistry registry, HttpClient http, string root, List<string> log)
        {
            Mock = mock;
            Ctx = ctx;
            Account = account;
            Broker = broker;
            Registry = registry;
            _http = http;
            _root = root;
            Log = log;
        }

        public MockTorBox Mock { get; }
        public ServiceContext Ctx { get; }
        public RemoteAccountStore Account { get; }
        public RemoteLinkBroker Broker { get; }
        public RemoteProviderRegistry Registry { get; }
        public List<string> Log { get; }

        public static readonly RemoteLinkBrokerOptions Fast = new()
        {
            MaxAttempts = 3,
            InitialDelay = TimeSpan.FromMilliseconds(10),
            MaxDelay = TimeSpan.FromMilliseconds(50),
            LongestRetryAfter = TimeSpan.FromSeconds(3),
        };

        public static async Task<TorBoxRig> CreateAsync(
            TestServerHost server,
            string? key = MockKey,
            int connections = 4,
            RemoteLinkBrokerOptions? options = null,
            int maxConcurrent = 2,
            Action<string, StoragePaths>? beforeOpen = null,
            ContextHooks? hooks = null)
        {
            server.TorBox.Reset();
            var root = Path.Combine(AppContext.BaseDirectory, "test-work", Guid.NewGuid().ToString("N"));
            var paths = new StoragePaths(Path.Combine(root, "state"));
            var destination = Path.Combine(root, "downloads");
            Directory.CreateDirectory(destination);
            paths.EnsureCreated();
            beforeOpen?.Invoke("torbox", paths);

            var log = new List<string>();
            void Write(string line)
            {
                lock (log) log.Add(line);
            }

            var account = await RemoteAccountStore.OpenAsync(paths, TorBoxClient.ProviderId, Write);
            if (key is not null) await account.SetKeyAsync(key);
            var http = new HttpClient();
            var registry = new RemoteProviderRegistry();
            registry.Add(new TorBoxClient(http, account.KeySource, server.TorBox.ApiBase), account);
            var broker = new RemoteLinkBroker(registry, options ?? Fast, log: Write);
            var ctx = await ServiceContext.CreateAsync(server, paths, destination, linkBroker: broker, linkSecrets: registry, maxConcurrent: maxConcurrent, hooks: hooks);
            var settings = ctx.Service.GetSnapshot().Settings;
            settings.DefaultConnections = connections;
            Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);
            return new TorBoxRig(server.TorBox, ctx, account, broker, registry, http, root, log);
        }

        public RemoteJobRequest Request(MockTorBoxItem item, MockTorBoxFile file, int maxConnections = 4) => new()
        {
            Source = new RemoteSource("torbox", RemoteKinds.ToWord(item.Kind), item.Id.ToString(), file.Id.ToString(), file.Name, $"job-{item.Id}"),
            FileName = file.ShortName,
            DestinationRoot = Ctx.Destination,
            MaxConnections = maxConnections,
            ExpectedTotalBytes = file.Size,
            ExpectedMd5 = file.Md5,
        };

        public async Task<JobDto> AddAsync(MockTorBoxItem item, MockTorBoxFile file, int maxConnections = 4)
            => AddedJob(await Ctx.Service.AddRemoteJobsAsync([Request(item, file, maxConnections)], $"remote:job-{item.Id}", item.Name));

        /// <summary>Scans everything the app has written, plus every log line and the whole snapshot.</summary>
        public async Task AssertNoLinkOrKeyLeakedAsync()
        {
            var secrets = Mock.IssuedLinks.Select(link => link.Uuid)
                .Append(MockKey)
                .Append(MockKey.Replace("-", string.Empty, StringComparison.Ordinal));
            var snapshot = System.Text.Json.JsonSerializer.Serialize(Ctx.Service.GetSnapshot());
            string lines;
            lock (Log) lines = string.Join('\n', Log);
            foreach (var secret in secrets)
            {
                await AssertSecretNotOnDiskAsync(Ctx, secret);
                Assert.DoesNotContain(secret, snapshot);
                Assert.DoesNotContain(secret, lines);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Ctx.DisposeAsync();
            _http.Dispose();
            Mock.Reset();
            try { Directory.Delete(_root, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static string Sha256Of(byte[] content) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)).ToLowerInvariant();

    private static void AssertCompleted(JobDto job) =>
        Assert.True(job.State == "completed", $"{job.State}: {job.Failure?.Kind}: {job.Failure?.Message}");

    private static void AssertFailedAndKept(JobDto failed, string kind)
    {
        Assert.Equal("failed", failed.State);
        Assert.Equal(kind, failed.Failure?.Kind);
        Assert.True(failed.CanResume, "The job should be resumable.");
    }

    // ---- Scenario F ---------------------------------------------------------------------------

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task ScenarioF_ALinkLostMidTransferIsReplacedAndTheTransferResumes(int connections)
    {
        var loaded = new List<DownloadCheckpoint?>();
        await using var rig = await TorBoxRig.CreateAsync(_server, connections: connections, hooks: new ContextHooks
        {
            WrapCheckpoints = inner => new RecordingCheckpoints(inner, loaded),
        });
        const int size = 8 * Mb;
        var file = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Pack/sub/big.bin", size, seed: 901);
        var item = rig.Mock.AddItem(RemoteItemKind.Torrent, "Pack", [file]);
        rig.Mock.NextLinkDiesAfter(2 * Mb);

        var added = await rig.AddAsync(item, file);
        Assert.Equal($"remote://torbox/torrents/{item.Id}/{file.Id}", added.Url);
        Assert.Equal(Path.Combine(rig.Ctx.Destination, "Pack", "sub"), added.DestinationDirectory);

        var completed = await rig.Ctx.WaitFor(added.Id, j => j.State is "completed" or "failed", TimeSpan.FromSeconds(60));
        AssertCompleted(completed);
        Assert.Equal(Sha256Of(file.Content), await Hashing.Sha256FileAsync(completed.FinalFilePath));
        Assert.Equal(size, new FileInfo(completed.FinalFilePath).Length);

        var links = rig.Mock.IssuedLinks;
        Assert.Equal(2, links.Count);
        Assert.Equal(2, rig.Mock.Calls("GET torrents/requestdl"));

        // A new link, and only what was still missing was asked of it. Asked, not served: a response whose range another
        // connection has since taken half of keeps sending until it is closed, so under load what the server pushes
        // into its sockets can add up to more than the file, while no byte the first link gave is ever requested again.
        Assert.Equal(2, loaded.Count);
        var saved = loaded[1];
        Assert.NotNull(saved);
        var requested = RequestedBytes(links[1].Ranges, size);
        Assert.Equal(size - saved.CompletedBytes, requested);
        Assert.True(links[1].BytesServed >= 1);

        // The peak the server sees says nothing here: the old link's aborted answers can still be winding down on its
        // side as the new link's start, though the client closed them first. TheProvidersConnectionLimit... tests the cap.
        Assert.Equal(connections, completed.RequestedConnections);

        Assert.Equal(added.Url, completed.Url);
        Assert.Null(completed.FinalUrl);
        await rig.AssertNoLinkOrKeyLeakedAsync();
    }

    /// <summary>How many distinct bytes of the file these Range headers ask for, leaving out the 0-0 that only asks about the file.</summary>
    private static long RequestedBytes(IEnumerable<string> ranges, long size)
    {
        var spans = ranges
            .Where(range => range != "bytes=0-0")
            .Select(range => range["bytes=".Length..].Split('-'))
            .Select(parts => (Start: long.Parse(parts[0]), End: parts[1].Length == 0 ? size - 1 : long.Parse(parts[1])))
            .OrderBy(span => span.Start);
        long total = 0;
        long reached = 0;
        foreach (var (start, end) in spans)
        {
            var from = Math.Max(start, reached);
            if (end >= from) total += end - from + 1;
            reached = Math.Max(reached, end + 1);
        }

        return total;
    }

    /// <summary>Remembers what the service was given to continue from, at the moment it asked.</summary>
    private sealed class RecordingCheckpoints(IDownloadCheckpointStore inner, List<DownloadCheckpoint?> loaded) : IDownloadCheckpointStore
    {
        public async Task<DownloadCheckpoint?> LoadAsync(Guid jobId, CancellationToken cancellationToken = default)
        {
            var checkpoint = await inner.LoadAsync(jobId, cancellationToken);
            lock (loaded) loaded.Add(checkpoint);
            return checkpoint;
        }

        public Task SaveAsync(DownloadCheckpoint checkpoint, CancellationToken cancellationToken = default) => inner.SaveAsync(checkpoint, cancellationToken);

        public Task DeleteAsync(Guid jobId, CancellationToken cancellationToken = default) => inner.DeleteAsync(jobId, cancellationToken);
    }

    /// <summary>
    /// A scanner holding the checkpoint for a moment must not read as "there is no progress": without it, the part
    /// file holds scattered ranges nothing can vouch for, so the engine would throw away every byte and start over.
    /// </summary>
    [Fact]
    public async Task ACheckpointThatCantBeReadRightNowCostsNoBytes()
    {
        await using var rig = await TorBoxRig.CreateAsync(_server, connections: 4);
        const int size = 8 * Mb;
        var file = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Pack/held.bin", size, seed: 911);
        var item = rig.Mock.AddItem(RemoteItemKind.Torrent, "Pack", [file]);
        rig.Mock.CdnBytesPerSecond = 2 * Mb;

        var added = await rig.AddAsync(item, file);
        await rig.Ctx.WaitFor(added.Id, j => j.ReceivedBytes > Mb, TimeSpan.FromSeconds(30));
        Assert.True((await rig.Ctx.Service.PauseAsync(added.Id)).Ok);
        var saved = await SettledCheckpointAsync(rig.Ctx, added.Id);
        Assert.True(saved.CompletedBytes > 0);
        rig.Mock.CdnBytesPerSecond = null;
        var part = rig.Ctx.Service.GetJob(added.Id)!.PartFilePath;

        var checkpoint = Path.Combine(rig.Ctx.Paths.CheckpointsDirectory, $"{added.Id:N}.json");
        using (new FileStream(checkpoint, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.True((await rig.Ctx.Service.ResumeAsync(added.Id)).Ok);
            var failed = await rig.Ctx.WaitFor(added.Id, j => j.State is "failed" or "completed", TimeSpan.FromSeconds(30));
            Assert.Equal("failed", failed.State);
            Assert.True(failed.CanResume);
            Assert.Contains("couldn't be read", failed.Failure!.Message);
        }

        Assert.Equal(size, new FileInfo(part).Length);
        var issued = rig.Mock.IssuedLinks.Count;
        Assert.True((await rig.Ctx.Service.ResumeAsync(added.Id)).Ok);
        var completed = await rig.Ctx.WaitFor(added.Id, j => j.State is "completed" or "failed", TimeSpan.FromSeconds(30));

        AssertCompleted(completed);
        Assert.Equal(Sha256Of(file.Content), await Hashing.Sha256FileAsync(completed.FinalFilePath));
        Assert.True(rig.Mock.IssuedLinks.Count > issued);
        Assert.Equal(size - saved.CompletedBytes, RequestedBytes(rig.Mock.IssuedLinks[^1].Ranges, size));
    }

    [Fact]
    public async Task ASaveThatMeetsAFileHeldForAMomentStillLands()
    {
        await using var rig = await TorBoxRig.CreateAsync(_server);
        var store = new JsonCheckpointStore(rig.Ctx.Paths);
        var id = Guid.NewGuid();
        await store.SaveAsync(new DownloadCheckpoint { JobId = id, Url = "https://example.invalid/a", Segments = [] });
        var path = Path.Combine(rig.Ctx.Paths.CheckpointsDirectory, $"{id:N}.json");

        var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        _ = Task.Run(async () =>
        {
            await Task.Delay(60);
            await held.DisposeAsync();
        });
        await store.SaveAsync(new DownloadCheckpoint { JobId = id, Url = "https://example.invalid/b", Segments = [] });

        Assert.Equal("https://example.invalid/b", (await store.LoadAsync(id))!.Url);
    }

    [Fact]
    public async Task ScenarioF_ALinkDeadWhenResumedIsReplacedAfterAPause()
    {
        await using var rig = await TorBoxRig.CreateAsync(_server, connections: 4);
        const int size = 8 * Mb;
        var file = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Pack/big.bin", size, seed: 902);
        var item = rig.Mock.AddItem(RemoteItemKind.Torrent, "Pack", [file]);
        rig.Mock.CdnBytesPerSecond = 2 * Mb;

        var added = await rig.AddAsync(item, file);
        await rig.Ctx.WaitFor(added.Id, j => j.ReceivedBytes > Mb, TimeSpan.FromSeconds(30));
        Assert.True((await rig.Ctx.Service.PauseAsync(added.Id)).Ok);
        var saved = await SettledCheckpointAsync(rig.Ctx, added.Id);

        rig.Mock.ExpireLinks();
        rig.Mock.CdnBytesPerSecond = null;
        Assert.True((await rig.Ctx.Service.ResumeAsync(added.Id)).Ok);
        var completed = await rig.Ctx.WaitFor(added.Id, j => j.State is "completed" or "failed", TimeSpan.FromSeconds(30));

        AssertCompleted(completed);
        Assert.Equal(Sha256Of(file.Content), await Hashing.Sha256FileAsync(completed.FinalFilePath));
        Assert.Equal(2, rig.Mock.IssuedLinks.Count);
        // A resume asks for fresh links each run instead of using one that may have expired while paused.
        foreach (var range in rig.Mock.IssuedLinks[1].Ranges.Where(range => range != "bytes=0-0"))
        {
            var start = long.Parse(range["bytes=".Length..].Split('-')[0]);
            Assert.DoesNotContain(saved.Segments, segment => start >= segment.Start && start < segment.Start + segment.Completed);
        }

        await rig.AssertNoLinkOrKeyLeakedAsync();
    }

    [Fact]
    public async Task ScenarioF_ALinkIsFetchedFreshOnEveryRunAndNeverSaved()
    {
        await using var rig = await TorBoxRig.CreateAsync(_server, connections: 1);
        var file = MockTorBoxFile.Of(rig.Mock.NextFileId(), "x/a.bin", 2 * Mb, seed: 903);
        var item = rig.Mock.AddItem(RemoteItemKind.WebDownload, "x", [file]);
        rig.Mock.CdnBytesPerSecond = Mb;

        var added = await rig.AddAsync(item, file);
        await rig.Ctx.WaitFor(added.Id, j => j.ReceivedBytes > 256 * 1024, TimeSpan.FromSeconds(30));
        Assert.True((await rig.Ctx.Service.PauseAsync(added.Id)).Ok);
        await rig.Ctx.WaitFor(added.Id, j => j.State == "paused");
        Assert.Single(rig.Mock.IssuedLinks);
        Assert.DoesNotContain(rig.Mock.IssuedLinks[0].Uuid, await JobJsonAsync(rig.Ctx, added.Id));

        rig.Mock.CdnBytesPerSecond = null;
        Assert.True((await rig.Ctx.Service.ResumeAsync(added.Id)).Ok);
        var completed = await rig.Ctx.WaitFor(added.Id, j => j.State == "completed", TimeSpan.FromSeconds(30));
        Assert.Equal(2, rig.Mock.IssuedLinks.Count);
        Assert.Equal(Sha256Of(file.Content), await Hashing.Sha256FileAsync(completed.FinalFilePath));
        await rig.AssertNoLinkOrKeyLeakedAsync();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task ScenarioF_ContentChangedAtTheSameSizeIsCaughtByItsDateAndNoOldBytesRemain(int connections)
    {
        await using var rig = await TorBoxRig.CreateAsync(_server, connections: connections);
        const int size = 8 * Mb;
        var file = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Pack/same-size.bin", size, seed: 904);
        var item = rig.Mock.AddItem(RemoteItemKind.Torrent, "Pack", [file]);
        rig.Mock.NextLinkDiesAfter(2 * Mb);
        rig.Mock.LinkDied = () => file.Replace(size, seed: 905);

        var added = await rig.AddAsync(item, file);
        var done = await rig.Ctx.WaitFor(added.Id, j => j.State is "completed" or "failed", TimeSpan.FromSeconds(60));

        AssertCompleted(done);
        Assert.Equal(Sha256Of(905, size), await Hashing.Sha256FileAsync(done.FinalFilePath));
        Assert.True(rig.Mock.IssuedLinks[^1].BytesServed >= size, "Nothing from before the change may be kept.");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task ADifferentSizeFromTheProviderRestartsFromZero(int connections)
    {
        await using var rig = await TorBoxRig.CreateAsync(_server, connections: connections);
        const int oldSize = 8 * Mb;
        const int newSize = 3 * Mb;
        var file = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Pack/resized.bin", oldSize, seed: 906);
        var item = rig.Mock.AddItem(RemoteItemKind.Torrent, "Pack", [file]);
        rig.Mock.NextLinkDiesAfter(2 * Mb);
        rig.Mock.LinkDied = () => file.Replace(newSize, seed: 907);

        var added = await rig.AddAsync(item, file);
        var done = await rig.Ctx.WaitFor(added.Id, j => j.State is "completed" or "failed", TimeSpan.FromSeconds(60));

        AssertCompleted(done);
        Assert.Equal(newSize, done.TotalBytes);
        Assert.Equal(Sha256Of(907, newSize), await Hashing.Sha256FileAsync(done.FinalFilePath));
        Assert.Contains(rig.Log, line => line.Contains("changed on TorBox") && line.Contains(added.Id.ToString()));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task ADifferentMd5FromTheProviderRestartsFromZeroEvenWithTheSameDate(int connections)
    {
        await using var rig = await TorBoxRig.CreateAsync(_server, connections: connections);
        const int size = 8 * Mb;
        var file = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Pack/rehashed.bin", size, seed: 908, withMd5: true);
        var item = rig.Mock.AddItem(RemoteItemKind.Torrent, "Pack", [file]);
        rig.Mock.NextLinkDiesAfter(2 * Mb);
        var sameDate = file.LastModified;
        rig.Mock.LinkDied = () => file.Replace(size, seed: 909, withMd5: true, lastModified: sameDate);

        var added = await rig.AddAsync(item, file);
        var done = await rig.Ctx.WaitFor(added.Id, j => j.State is "completed" or "failed", TimeSpan.FromSeconds(60));

        AssertCompleted(done);
        Assert.Equal(Sha256Of(909, size), await Hashing.Sha256FileAsync(done.FinalFilePath));
        Assert.True(rig.Mock.IssuedLinks[^1].BytesServed >= size, "The old data should have been thrown away.");
    }

    [Fact]
    public async Task AWrongFileWithoutWarningFailsItsMd5AndIsNotKept()
    {
        await using var rig = await TorBoxRig.CreateAsync(_server, connections: 4);
        const int size = 2 * Mb;
        var file = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Pack/lying.bin", size, seed: 910, withMd5: true);
        var claimed = file.Md5;
        var item = rig.Mock.AddItem(RemoteItemKind.Torrent, "Pack", [file]);
        file.Content = TestContent.Generate(911, size);
        file.Md5 = claimed;

        var added = await rig.AddAsync(item, file);
        var failed = await rig.Ctx.WaitFor(added.Id, j => j.State == "failed", TimeSpan.FromSeconds(30));

        Assert.Equal("verificationFailed", failed.Failure?.Kind);
        Assert.False(File.Exists(failed.FinalFilePath));
        Assert.False(File.Exists(failed.PartFilePath));
    }

    [Fact]
    public async Task ChangingOneFileLeavesItsFinishedSiblingAlone()
    {
        await using var rig = await TorBoxRig.CreateAsync(_server, connections: 4, maxConcurrent: 1);
        var sibling = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Pack/first.bin", Mb, seed: 912);
        var changing = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Pack/second.bin", 8 * Mb, seed: 913);
        var item = rig.Mock.AddItem(RemoteItemKind.Torrent, "Pack", [sibling, changing]);

        var firstId = (await rig.AddAsync(item, sibling)).Id;
        var first = await rig.Ctx.WaitFor(firstId, j => j.State == "completed", TimeSpan.FromSeconds(30));
        var firstHash = await Hashing.Sha256FileAsync(first.FinalFilePath);
        var firstWritten = File.GetLastWriteTimeUtc(first.FinalFilePath);

        // The second file's link dies partway, and the provider now holds other content for it.
        rig.Mock.NextLinkDiesAfter(2 * Mb);
        rig.Mock.LinkDied = () => changing.Replace(5 * Mb, seed: 914);
        var secondId = (await rig.AddAsync(item, changing)).Id;

        var second = await rig.Ctx.WaitFor(secondId, j => j.State is "completed" or "failed", TimeSpan.FromSeconds(60));
        AssertCompleted(second);
        Assert.Equal(Sha256Of(914, 5 * Mb), await Hashing.Sha256FileAsync(second.FinalFilePath));

        var after = rig.Ctx.Service.GetJob(firstId)!;
        Assert.Equal("completed", after.State);
        Assert.Equal(firstHash, await Hashing.Sha256FileAsync(after.FinalFilePath));
        Assert.Equal(firstWritten, File.GetLastWriteTimeUtc(after.FinalFilePath));
        Assert.Equal(1, rig.Mock.IssuedLinks.Count(link => link.File == sibling));
        Assert.Single(rig.Ctx.Service.GetSnapshot().Settings.DownloadGroups);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task ASmallTextAnswerWith200IsNeverAppendedToTheFile(int connections)
    {
        await using var rig = await TorBoxRig.CreateAsync(_server, connections: connections);
        const int size = 8 * Mb;
        var file = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Pack/dead-200.bin", size, seed: 915);
        var item = rig.Mock.AddItem(RemoteItemKind.Torrent, "Pack", [file]);
        rig.Mock.NextLinkDiesAfter(2 * Mb);
        rig.Mock.LinkDied = () => rig.Mock.Fail(MockTorBox.CdnRoute, MockFault.DeadLink(), times: 2);

        var added = await rig.AddAsync(item, file);
        var done = await rig.Ctx.WaitFor(added.Id, j => j.State is "completed" or "failed", TimeSpan.FromSeconds(60));
        if (done.State == "failed")
        {
            // Two refreshes can run out while the answers are still bad. The bytes must survive that.
            Assert.True(done.CanResume, done.Failure?.Message);
            Assert.True((await rig.Ctx.Service.ResumeAsync(added.Id)).Ok);
            done = await rig.Ctx.WaitFor(added.Id, j => j.State == "completed", TimeSpan.FromSeconds(60));
        }

        Assert.Equal(size, new FileInfo(done.FinalFilePath).Length);
        Assert.Equal(Sha256Of(file.Content), await Hashing.Sha256FileAsync(done.FinalFilePath));
    }

    [Theory]
    [InlineData(401, "unauthorized")]
    [InlineData(403, "forbidden")]
    [InlineData(404, "notFound")]
    [InlineData(410, "expiredUrl")]
    public async Task ARefreshHappensAtMostTwicePerRunThenTheJobFailsResumably(int status, string kind)
    {
        await using var rig = await TorBoxRig.CreateAsync(_server, connections: 1);
        var file = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Pack/a.bin", 64 * 1024, seed: 916);
        var item = rig.Mock.AddItem(RemoteItemKind.WebDownload, "Pack", [file]);
        rig.Mock.Fail(MockTorBox.CdnRoute, MockFault.Empty(status));

        var added = await rig.AddAsync(item, file);
        var failed = await rig.Ctx.WaitFor(added.Id, j => j.State == "failed");

        AssertFailedAndKept(failed, kind);
        Assert.Contains("keep failing", failed.Failure!.Message);
        Assert.DoesNotContain("Refresh Download Address", failed.Failure.Message);
        Assert.Equal(3, rig.Mock.Calls("GET webdl/requestdl"));
        Assert.Equal(3, rig.Mock.Calls("GET webdl/mylist"));
        Assert.Equal(0, rig.Mock.Calls("POST webdl/createwebdownload"));

        rig.Mock.ClearFaults();
        Assert.True((await rig.Ctx.Service.ResumeAsync(added.Id)).Ok);
        var completed = await rig.Ctx.WaitFor(added.Id, j => j.State == "completed");
        Assert.Equal(Sha256Of(file.Content), await Hashing.Sha256FileAsync(completed.FinalFilePath));
    }

    /// <summary>
    /// Seen live: the CDN rewrites an HTML file sent whole, so it arrives longer than the file and with
    /// no Content-Length, and a provider's file then fails its check and loses its bytes. As a range it
    /// is untouched, so a provider's job asks for one from the first byte.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnHtmlFileTheCdnWouldRewriteArrivesExact(bool withMd5)
    {
        await using var rig = await TorBoxRig.CreateAsync(_server, connections: 1);
        var file = MockTorBoxFile.Of(0, "Pack/page.html", 256, seed: 935, withMd5: withMd5);
        file.MimeType = "text/html; charset=utf-8";
        var item = rig.Mock.AddItem(RemoteItemKind.Torrent, "Pack", [file]);

        var added = await rig.AddAsync(item, file);
        var completed = await rig.Ctx.WaitFor(added.Id, j => j.State == "completed");

        Assert.Equal(Sha256Of(file.Content), await Hashing.Sha256FileAsync(completed.FinalFilePath));
        Assert.All(rig.Mock.IssuedLinks, link => Assert.NotEmpty(link.Ranges));
    }

    /// <summary>A real item's file ids run from 0, so two items share them. Each job still fetches its own file.</summary>
    [Fact]
    public async Task TwoItemsWithTheSameFileIdsAreTwoDownloads()
    {
        await using var rig = await TorBoxRig.CreateAsync(_server, connections: 1);
        var one = MockTorBoxFile.Of(0, "One/a.bin", 64 * 1024, seed: 936);
        var two = MockTorBoxFile.Of(0, "Two/a.bin", 64 * 1024, seed: 937);
        var first = await rig.AddAsync(rig.Mock.AddItem(RemoteItemKind.Torrent, "One", [one]), one);
        var second = await rig.AddAsync(rig.Mock.AddItem(RemoteItemKind.Torrent, "Two", [two]), two);

        var firstDone = await rig.Ctx.WaitFor(first.Id, j => j.State == "completed");
        var secondDone = await rig.Ctx.WaitFor(second.Id, j => j.State == "completed");

        Assert.Equal(Sha256Of(one.Content), await Hashing.Sha256FileAsync(firstDone.FinalFilePath));
        Assert.Equal(Sha256Of(two.Content), await Hashing.Sha256FileAsync(secondDone.FinalFilePath));
    }

    [Fact]
    public async Task AnExpiredItemFailsWithoutRetryingAndKeepsWhatWasDownloaded()
    {
        await using var rig = await TorBoxRig.CreateAsync(_server, connections: 1);
        var file = MockTorBoxFile.Of(0, "Pack/a.bin", 64 * 1024, seed: 938);
        var item = rig.Mock.AddItem(RemoteItemKind.Torrent, "Pack", [file], MockTorBoxState.Expired);

        var added = await rig.AddAsync(item, file);
        var failed = await rig.Ctx.WaitFor(added.Id, j => j.State == "failed");

        Assert.Contains("expired", failed.Failure!.Message);
        Assert.False(failed.Failure.IsRetryable);
        Assert.Equal(0, rig.Mock.Calls("GET torrents/requestdl"));
    }

    [Theory]
    [InlineData(429)]
    [InlineData(503)]
    public async Task ThrottlingOrAnOutageAtTheCdnIsNotMistakenForAnExpiredLink(int status)
    {
        await using var rig = await TorBoxRig.CreateAsync(_server, connections: 1);
        var file = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Pack/a.bin", 64 * 1024, seed: 934);
        var item = rig.Mock.AddItem(RemoteItemKind.Torrent, "Pack", [file]);
        rig.Mock.Fail(MockTorBox.CdnRoute, status == 429 ? MockFault.TooManyRequests(1) : MockFault.Status5xx(status), times: 1);

        var added = await rig.AddAsync(item, file);
        var completed = await rig.Ctx.WaitFor(added.Id, j => j.State == "completed");
        Assert.Equal(Sha256Of(file.Content), await Hashing.Sha256FileAsync(completed.FinalFilePath));
        Assert.Equal(1, rig.Mock.Calls("GET torrents/requestdl"));
    }

    [Fact]
    public async Task TheProvidersConnectionLimitIsAppliedEvenToASavedHigherOne()
    {
        await using var rig = await TorBoxRig.CreateAsync(_server, connections: 8);
        const int size = 16 * Mb;
        var file = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Pack/capped.bin", size, seed: 917);
        var item = rig.Mock.AddItem(RemoteItemKind.Torrent, "Pack", [file]);
        rig.Mock.CdnBytesPerSecond = 2 * Mb;

        var added = await rig.AddAsync(item, file, maxConnections: 8);
        Assert.Equal(8, added.RequestedConnections);
        var completed = await rig.Ctx.WaitFor(added.Id, j => j.State == "completed", TimeSpan.FromSeconds(60));

        Assert.Equal(4, completed.RequestedConnections);
        Assert.InRange(rig.Mock.PeakCdnConnections, 2, 4);
    }

    [Fact]
    public async Task TheCdnNeverSeesTheBearerKeyOrAnyLogin()
    {
        await using var rig = await TorBoxRig.CreateAsync(_server, connections: 1);
        var file = MockTorBoxFile.Of(rig.Mock.NextFileId(), "x/a.bin", 64 * 1024, seed: 918);
        var item = rig.Mock.AddItem(RemoteItemKind.Torrent, "x", [file]);
        var added = await rig.AddAsync(item, file);
        await rig.Ctx.WaitFor(added.Id, j => j.State == "completed");
        Assert.True(rig.Mock.Calls(MockTorBox.CdnRoute) > 0);
        Assert.Equal(0, rig.Mock.CdnRequestsWithCredentials);
    }

    // ---- The real cause ------------------------------------------------------------------------

    [Fact]
    public async Task AnItemRemovedFromTheAccountSaysSoAndKeepsTheBytes()
    {
        await using var rig = await TorBoxRig.CreateAsync(_server, connections: 1);
        var file = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Pack/a.bin", 8 * Mb, seed: 919);
        var item = rig.Mock.AddItem(RemoteItemKind.Torrent, "Pack", [file]);
        rig.Mock.NextLinkDiesAfter(2 * Mb);
        rig.Mock.LinkDied = () => rig.Mock.RemoveItem(item);

        var added = await rig.AddAsync(item, file);
        var failed = await rig.Ctx.WaitFor(added.Id, j => j.State == "failed", TimeSpan.FromSeconds(30));

        AssertFailedAndKept(failed, "notFound");
        Assert.Contains("no longer on your TorBox account", failed.Failure!.Message);
        Assert.InRange(failed.ReceivedBytes, 1, 8 * Mb);
        Assert.True(File.Exists(failed.PartFilePath));
        Assert.Empty(rig.Mock.Submissions);
        Assert.Empty(rig.Mock.Deleted);
    }

    [Fact]
    public async Task AFileGoneFromItsItemSaysSo()
    {
        await using var rig = await TorBoxRig.CreateAsync(_server);
        var file = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Pack/a.bin", 1024);
        var other = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Pack/b.bin", 1024);
        var item = rig.Mock.AddItem(RemoteItemKind.Torrent, "Pack", [other]);

        var added = await rig.AddAsync(item, file);
        var failed = await rig.Ctx.WaitFor(added.Id, j => j.State == "failed");
        AssertFailedAndKept(failed, "notFound");
        Assert.Contains("no longer lists this file", failed.Failure!.Message);
        Assert.Equal(0, rig.Mock.Calls("GET torrents/requestdl"));
    }

    [Fact]
    public async Task AFileLaterMarkedInfectedIsNotDownloaded()
    {
        await using var rig = await TorBoxRig.CreateAsync(_server);
        var file = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Pack/a.bin", 1024, infected: true);
        var item = rig.Mock.AddItem(RemoteItemKind.Torrent, "Pack", [file]);

        var added = await rig.AddAsync(item, file);
        var failed = await rig.Ctx.WaitFor(added.Id, j => j.State == "failed");
        Assert.Contains("infected", failed.Failure?.Message);
        Assert.Equal(0, rig.Mock.Calls("GET torrents/requestdl"));
    }

    [Fact]
    public async Task AnItemThatFailedRemotelyGivesTheProvidersReason()
    {
        await using var rig = await TorBoxRig.CreateAsync(_server);
        var file = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Pack/a.bin", 1024);
        var item = rig.Mock.AddItem(RemoteItemKind.WebDownload, "Pack", [file], MockTorBoxState.Failed("Hoster is down"));

        var added = await rig.AddAsync(item, file);
        var failed = await rig.Ctx.WaitFor(added.Id, j => j.State == "failed");
        Assert.Contains("TorBox reports that this item failed: Hoster is down", failed.Failure?.Message);
        Assert.False(failed.Failure!.IsRetryable);
        Assert.Equal(0, rig.Mock.Calls("GET webdl/requestdl"));
    }

    [Fact]
    public async Task AnItemNoLongerReadyShowsItsStateAndGetsNoLinkRequest()
    {
        await using var rig = await TorBoxRig.CreateAsync(_server);
        var file = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Pack/a.bin", 1024);
        var item = rig.Mock.AddItem(RemoteItemKind.Torrent, "Pack", [file], new MockTorBoxState("downloading"));

        var added = await rig.AddAsync(item, file);
        var failed = await rig.Ctx.WaitFor(added.Id, j => j.State == "failed");
        Assert.Contains("state: downloading", failed.Failure?.Message);
        Assert.True(failed.Failure!.IsRetryable);
        Assert.Equal(0, rig.Mock.Calls("GET torrents/requestdl"));
    }

    [Theory]
    [InlineData("PLAN_RESTRICTED_FEATURE", "unknown")]
    [InlineData("MONTHLY_LIMIT", "forbidden")]
    [InlineData("DOWNLOAD_TOO_LARGE", "unknown")]
    public async Task AccountLimitsAreReportedInTheProvidersWordsWithoutRetrying(string code, string kind)
    {
        await using var rig = await TorBoxRig.CreateAsync(_server);
        var file = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Pack/a.bin", 1024);
        var item = rig.Mock.AddItem(RemoteItemKind.Torrent, "Pack", [file]);
        rig.Mock.Fail("GET torrents/requestdl", MockFault.Error(code, "Your plan does not allow this."));

        var added = await rig.AddAsync(item, file);
        var failed = await rig.Ctx.WaitFor(added.Id, j => j.State == "failed");
        AssertFailedAndKept(failed, kind);
        Assert.Contains("Your plan does not allow this.", failed.Failure!.Message);
        Assert.Equal(1, rig.Mock.Calls("GET torrents/requestdl"));
    }

    [Fact]
    public async Task ABusyAccountIsReportedAndCanBeRetried()
    {
        await using var rig = await TorBoxRig.CreateAsync(_server);
        var file = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Pack/a.bin", 1024);
        var item = rig.Mock.AddItem(RemoteItemKind.Torrent, "Pack", [file]);
        rig.Mock.Fail("GET torrents/requestdl", MockFault.Error("ACTIVE_LIMIT", "Too many active downloads."));

        var added = await rig.AddAsync(item, file);
        var failed = await rig.Ctx.WaitFor(added.Id, j => j.State == "failed");
        Assert.Contains("Too many active downloads.", failed.Failure?.Message);
        Assert.True(failed.Failure!.IsRetryable);
        Assert.Equal(1, rig.Mock.Calls("GET torrents/requestdl"));
    }

    [Fact]
    public async Task AWrongKeyIsReportedAsTheKey()
    {
        await using var rig = await TorBoxRig.CreateAsync(_server, key: "ffffffff-ffff-4fff-8fff-ffffffffffff");
        var file = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Pack/a.bin", 1024);
        var item = rig.Mock.AddItem(RemoteItemKind.Torrent, "Pack", [file]);

        var added = await rig.AddAsync(item, file);
        var failed = await rig.Ctx.WaitFor(added.Id, j => j.State == "failed");
        AssertFailedAndKept(failed, "unauthorized");
        Assert.Contains("refused the API key", failed.Failure!.Message);
        Assert.Equal(1, rig.Mock.TotalApiCalls);
    }

    [Fact]
    public async Task NoKeyAndAnUnreadableKeyEachSaySoWithoutCallingAnyone()
    {
        JobDto missing;
        string missingMessage;
        await using (var rig = await TorBoxRig.CreateAsync(_server, key: null))
        {
            Assert.Equal(RemoteKeyState.None, rig.Account.State);
            var file = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Pack/a.bin", 1024);
            var item = rig.Mock.AddItem(RemoteItemKind.Torrent, "Pack", [file]);
            var added = await rig.AddAsync(item, file);
            missing = await rig.Ctx.WaitFor(added.Id, j => j.State == "failed");
            AssertFailedAndKept(missing, "unauthorized");
            missingMessage = missing.Failure!.Message;
            Assert.Contains("No TorBox API key is set", missingMessage);
            Assert.Equal(0, rig.Mock.TotalApiCalls);

            // Setting one makes the same job work.
            await rig.Account.SetKeyAsync(MockKey);
            Assert.True((await rig.Ctx.Service.ResumeAsync(added.Id)).Ok);
            await rig.Ctx.WaitFor(added.Id, j => j.State == "completed");
        }

        await using (var rig = await TorBoxRig.CreateAsync(_server, key: null, beforeOpen: (provider, paths) =>
                     new RemoteAccountFile(paths, provider).SaveAsync(new StoredRemoteAccount { Provider = provider, ProtectedKey = "dpapi:v1:AAAA" }).GetAwaiter().GetResult()))
        {
            Assert.Equal(RemoteKeyState.Unreadable, rig.Account.State);
            var file = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Pack/a.bin", 1024);
            var item = rig.Mock.AddItem(RemoteItemKind.Torrent, "Pack", [file]);
            var added = await rig.AddAsync(item, file);
            var failed = await rig.Ctx.WaitFor(added.Id, j => j.State == "failed");
            AssertFailedAndKept(failed, "unauthorized");
            Assert.Contains("can't be read on this Windows account", failed.Failure!.Message);
            Assert.NotEqual(missingMessage, failed.Failure.Message);
            Assert.Equal(0, rig.Mock.TotalApiCalls);
        }
    }

    [Fact]
    public async Task ProviderWordsCannotCarryTheKeyIntoTheJob()
    {
        await using var rig = await TorBoxRig.CreateAsync(_server);
        var file = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Pack/a.bin", 1024);
        var item = rig.Mock.AddItem(RemoteItemKind.Torrent, "Pack", [file]);
        rig.Mock.Fail("GET torrents/requestdl", MockFault.Error("MONTHLY_LIMIT", $"Key {MockKey} used https://cdn.invalid/x?token={MockKey} up."));

        var added = await rig.AddAsync(item, file);
        var failed = await rig.Ctx.WaitFor(added.Id, j => j.State == "failed");
        Assert.DoesNotContain(MockKey, failed.Failure?.Message);
        Assert.DoesNotContain("cdn.invalid", failed.Failure?.Message);
        await rig.AssertNoLinkOrKeyLeakedAsync();
    }

    // ---- Temporary failures -------------------------------------------------------------------

    [Theory]
    [InlineData("GET torrents/mylist")]
    [InlineData("GET torrents/requestdl")]
    public async Task TemporaryFailuresAreRetriedInsideTheRun(string route)
    {
        await using var rig = await TorBoxRig.CreateAsync(_server);
        var file = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Pack/a.bin", 1024, seed: 920);
        var item = rig.Mock.AddItem(RemoteItemKind.Torrent, "Pack", [file]);
        rig.Mock.Fail(route, MockFault.Status5xx(503), times: 2);

        var added = await rig.AddAsync(item, file);
        var completed = await rig.Ctx.WaitFor(added.Id, j => j.State == "completed");
        Assert.Equal(3, rig.Mock.Calls(route));
        Assert.Equal(Sha256Of(file.Content), await Hashing.Sha256FileAsync(completed.FinalFilePath));
    }

    [Fact]
    public async Task ALostConnectionToTheApiIsRetriedToo()
    {
        await using var rig = await TorBoxRig.CreateAsync(_server);
        var file = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Pack/a.bin", 1024, seed: 921);
        var item = rig.Mock.AddItem(RemoteItemKind.Torrent, "Pack", [file]);
        rig.Mock.Fail("GET torrents/mylist", MockFault.Abort, times: 1);
        rig.Mock.Fail("GET torrents/requestdl", MockFault.NotJson(200), times: 1);

        var added = await rig.AddAsync(item, file);
        await rig.Ctx.WaitFor(added.Id, j => j.State == "completed");
        Assert.Equal(2, rig.Mock.Calls("GET torrents/mylist"));
        Assert.Equal(2, rig.Mock.Calls("GET torrents/requestdl"));
    }

    [Fact]
    public async Task AnOutageRunsOutThenFailsResumablyAndSubmitsNothing()
    {
        await using var rig = await TorBoxRig.CreateAsync(_server);
        var file = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Pack/a.bin", 1024, seed: 922);
        var item = rig.Mock.AddItem(RemoteItemKind.WebDownload, "Pack", [file]);
        rig.Mock.Fail("*", MockFault.Status5xx(500, "DATABASE_ERROR", "The database is down."));

        var added = await rig.AddAsync(item, file);
        var failed = await rig.Ctx.WaitFor(added.Id, j => j.State == "failed");

        AssertFailedAndKept(failed, "http");
        Assert.True(failed.Failure!.IsRetryable);
        Assert.Contains("TorBox is having problems: The database is down.", failed.Failure.Message);
        Assert.Equal(TorBoxRig.Fast.MaxAttempts, rig.Mock.Calls("GET webdl/mylist"));
        Assert.Equal(0, rig.Mock.Calls("POST webdl/createwebdownload"));

        rig.Mock.ClearFaults();
        Assert.True((await rig.Ctx.Service.ResumeAsync(added.Id)).Ok);
        await rig.Ctx.WaitFor(added.Id, j => j.State == "completed");
    }

    [Fact]
    public async Task A429WithRetryAfterIsWaitedOut()
    {
        await using var rig = await TorBoxRig.CreateAsync(_server);
        var file = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Pack/a.bin", 1024, seed: 923);
        var item = rig.Mock.AddItem(RemoteItemKind.Torrent, "Pack", [file]);
        rig.Mock.Fail("GET torrents/requestdl", MockFault.TooManyRequests(1), times: 1);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var added = await rig.AddAsync(item, file);
        await rig.Ctx.WaitFor(added.Id, j => j.State == "completed");
        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(900), $"Took {clock.Elapsed}");
        Assert.Equal(2, rig.Mock.Calls("GET torrents/requestdl"));
    }

    [Fact]
    public async Task ATooLongRetryAfterFailsNowInsteadOfHoldingTheSlot()
    {
        await using var rig = await TorBoxRig.CreateAsync(_server);
        var file = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Pack/a.bin", 1024, seed: 924);
        var item = rig.Mock.AddItem(RemoteItemKind.Torrent, "Pack", [file]);
        rig.Mock.Fail("GET torrents/requestdl", MockFault.TooManyRequests(3600));

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var added = await rig.AddAsync(item, file);
        var failed = await rig.Ctx.WaitFor(added.Id, j => j.State == "failed");
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), $"Took {clock.Elapsed}");
        Assert.Contains("fewer requests", failed.Failure?.Message);
        Assert.Equal(1, rig.Mock.Calls("GET torrents/requestdl"));
    }

    [Fact]
    public async Task PausingCutsABackoffShort()
    {
        var slow = new RemoteLinkBrokerOptions { MaxAttempts = 5, InitialDelay = TimeSpan.FromMinutes(5), MaxDelay = TimeSpan.FromMinutes(5) };
        await using var rig = await TorBoxRig.CreateAsync(_server, options: slow);
        var file = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Pack/a.bin", 1024, seed: 925);
        var item = rig.Mock.AddItem(RemoteItemKind.Torrent, "Pack", [file]);
        rig.Mock.Fail("GET torrents/mylist", MockFault.Status5xx(503));

        var added = await rig.AddAsync(item, file);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (rig.Mock.Calls("GET torrents/mylist") == 0 && DateTimeOffset.UtcNow < deadline) await Task.Delay(20);
        Assert.Equal(1, rig.Mock.Calls("GET torrents/mylist"));
        await Task.Delay(100);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        Assert.True((await rig.Ctx.Service.PauseAsync(added.Id)).Ok);
        await rig.Ctx.WaitFor(added.Id, j => j.State == "paused");
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"Took {clock.Elapsed}");
        Assert.Equal(1, rig.Mock.Calls("GET torrents/mylist"));
        Assert.Empty(rig.Mock.Deleted);
    }

    // ---- The broker on its own -----------------------------------------------------------------

    private static DownloadJob RemoteJob(string provider = "torbox", string kind = "torrents") => new()
    {
        Url = $"remote://{provider}/{kind}/1/2",
        RemoteSource = new RemoteSource(provider, kind, "1", "2", "a.bin", "job"),
    };

    [Theory]
    [InlineData(FailureKind.ExpiredUrl, 410, true)]
    [InlineData(FailureKind.Forbidden, 403, true)]
    [InlineData(FailureKind.Unauthorized, 401, true)]
    [InlineData(FailureKind.NotFound, 404, true)]
    [InlineData(FailureKind.Unauthorized, 407, false)]
    [InlineData(FailureKind.Http, 429, false)]
    [InlineData(FailureKind.Http, 503, false)]
    [InlineData(FailureKind.Network, null, false)]
    [InlineData(FailureKind.VerificationFailed, null, false)]
    [InlineData(FailureKind.RemoteChanged, null, false)]
    public async Task OnlyADeadLinkAsksForAFreshOne(FailureKind kind, int? status, bool expected)
    {
        await using var rig = await TorBoxRig.CreateAsync(_server);
        var failure = new JobFailure(kind, "x", false, true, DateTimeOffset.UtcNow, status);
        Assert.Equal(expected, await rig.Broker.ShouldRefreshAfterFailureAsync(RemoteJob(), failure, CancellationToken.None));
    }

    [Fact]
    public async Task AnUnknownProviderOrKindFailsPlainly()
    {
        await using var rig = await TorBoxRig.CreateAsync(_server);
        var failure = new JobFailure(FailureKind.Forbidden, "x", false, true, DateTimeOffset.UtcNow, 403);
        Assert.False(await rig.Broker.ShouldRefreshAfterFailureAsync(RemoteJob("nobody"), failure, CancellationToken.None));

        var unknown = await rig.Broker.PrepareRunAsync(RemoteJob("nobody"), CancellationToken.None);
        Assert.Contains("nobody", unknown.Failure?.Message);
        var badKind = await rig.Broker.PrepareRunAsync(RemoteJob(kind: "ftp"), CancellationToken.None);
        Assert.Contains("can't be read", badKind.Failure?.Message);
        var noSource = await rig.Broker.PrepareRunAsync(new DownloadJob(), CancellationToken.None);
        Assert.NotNull(noSource.Failure);
    }

    [Fact]
    public async Task AResolutionNeverPrintsItsLink()
    {
        await using var rig = await TorBoxRig.CreateAsync(_server);
        var file = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Pack/a.bin", 1024);
        var item = rig.Mock.AddItem(RemoteItemKind.Torrent, "Pack", [file]);
        var job = new DownloadJob
        {
            RemoteSource = new RemoteSource("torbox", "torrents", item.Id.ToString(), file.Id.ToString()),
            ExpectedTotalBytes = file.Size,
        };

        var resolution = await rig.Broker.PrepareRunAsync(job, CancellationToken.None);
        Assert.Contains(MockKey, resolution.Url);
        Assert.Equal("[link]", resolution.ToString());
        Assert.False(resolution.ResetLocalData);
        Assert.Equal(file.Size, resolution.ExpectedTotalBytes);
        Assert.Equal(4, resolution.MaxConnections);
    }

    [Theory]
    [InlineData(1000L, 1000L, null, null, false)]
    [InlineData(1000L, 999L, null, null, true)]
    [InlineData(null, 999L, null, null, false)]
    [InlineData(1000L, 0L, null, null, false)]
    [InlineData(1000L, 1000L, "AAAA", "aaaa", false)]
    [InlineData(1000L, 1000L, "aaaa", "bbbb", true)]
    [InlineData(1000L, 1000L, "aaaa", null, false)]
    [InlineData(1000L, 1000L, null, "aaaa", false)]
    public async Task IdentityIsSizeThenMd5(long? expectedSize, long reportedSize, string? expectedMd5, string? reportedMd5, bool changed)
    {
        await using var rig = await TorBoxRig.CreateAsync(_server);
        var file = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Pack/a.bin", 16);
        file.Size = reportedSize;
        file.Md5 = reportedMd5;
        var item = rig.Mock.AddItem(RemoteItemKind.Torrent, "Pack", [file]);
        var job = new DownloadJob
        {
            RemoteSource = new RemoteSource("torbox", "torrents", item.Id.ToString(), file.Id.ToString()),
            ExpectedTotalBytes = expectedSize,
            ExpectedMd5 = expectedMd5,
        };

        var resolution = await rig.Broker.PrepareRunAsync(job, CancellationToken.None);
        Assert.Null(resolution.Failure);
        Assert.Equal(changed, resolution.ResetLocalData);
        Assert.Equal(reportedMd5, resolution.ExpectedMd5);
    }

    // ---- A second provider ---------------------------------------------------------------------

    private sealed class SmallProvider(string link) : IRemoteProvider, IRemoteCatalog, IRemoteLinkResolver
    {
        public string Id => "small";
        public string DisplayName => "Small Provider";
        public RemoteCapabilities Capabilities => RemoteCapabilities.Catalog;
        public RemoteTransferPolicy TransferPolicy { get; } = new() { MaxConnectionsPerFile = 2 };
        public Task<RemoteAccountInfo> GetAccountAsync(CancellationToken cancellationToken = default) => Task.FromResult(new RemoteAccountInfo());
        public Task<IReadOnlyList<RemoteItem>> ListAsync(RemoteItemKind kind, int offset, int limit, bool fresh, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RemoteItem>>([]);

        public Task<RemoteItem?> GetAsync(RemoteItemRef item, bool fresh, CancellationToken cancellationToken = default) => Task.FromResult<RemoteItem?>(new RemoteItem
        {
            Ref = item,
            Stage = RemoteStage.Ready,
            Files = [new RemoteFile { FileId = "f", Path = "a.bin", Size = 4096 }],
        });

        public Task<RemoteDirectLink> ResolveLinkAsync(RemoteItemRef item, string fileId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new RemoteDirectLink(new Uri(link)));
    }

    private sealed class ListOnlyProvider : IRemoteProvider
    {
        public string Id => "listonly";
        public string DisplayName => "List Only";
        public RemoteCapabilities Capabilities => RemoteCapabilities.None;
        public RemoteTransferPolicy TransferPolicy { get; } = new();
        public Task<RemoteAccountInfo> GetAccountAsync(CancellationToken cancellationToken = default) => Task.FromResult(new RemoteAccountInfo());
    }

    [Fact]
    public async Task ASecondProviderNeedsOnlyItsInterfaces()
    {
        const int size = 4096;
        var registry = new RemoteProviderRegistry();
        registry.Add(new SmallProvider($"{_server.BaseAddress}/file/small.bin?size={size}&seed=926"));
        registry.Add(new ListOnlyProvider());
        await using var ctx = await ServiceContext.CreateAsync(_server, linkBroker: new RemoteLinkBroker(registry, TorBoxRig.Fast), linkSecrets: registry);
        var settings = ctx.Service.GetSnapshot().Settings;
        settings.DefaultConnections = 8;
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);

        var result = await ctx.Service.AddRemoteJobsAsync([new RemoteJobRequest
        {
            Source = new RemoteSource("small", "webdl", "1", "f", "a.bin", "job-1"),
            FileName = "a.bin",
            DestinationRoot = ctx.Destination,
            MaxConnections = 8,
        }], "remote:job-1");
        var completed = await ctx.WaitFor(AddedJob(result).Id, j => j.State == "completed");
        Assert.Equal(2, completed.RequestedConnections);
        Assert.Equal(Sha256Of(926, size), await Hashing.Sha256FileAsync(completed.FinalFilePath));

        var listOnly = await ctx.Service.AddRemoteJobsAsync([new RemoteJobRequest
        {
            Source = new RemoteSource("listonly", "webdl", "1", "f", "b.bin", "job-2"),
            FileName = "b.bin",
            DestinationRoot = ctx.Destination,
            MaxConnections = 4,
        }], "remote:job-2");
        var failed = await ctx.WaitFor(AddedJob(listOnly).Id, j => j.State == "failed");
        Assert.Contains("not available", failed.Failure?.Message);
    }

    // ---- Addresses that hold the stored key ----------------------------------------------------

    [Theory]
    [InlineData("query", true)]
    [InlineData("dashless", true)]
    [InlineData("upper", true)]
    [InlineData("encoded", true)]
    [InlineData("path", false)]
    [InlineData("userinfo", true)]
    public async Task AnAddressHoldingTheStoredKeyIsProtectedWhereverItIs(string form, bool giveName)
    {
        await using var rig = await TorBoxRig.CreateAsync(_server, connections: 2);
        const int size = 256 * 1024;
        var dashless = MockKey.Replace("-", string.Empty, StringComparison.Ordinal);
        var address = new Uri(_server.BaseAddress);
        var url = form switch
        {
            "query" => $"{_server.BaseAddress}/file/held.bin?size={size}&seed=927&ref={MockKey}",
            "dashless" => $"{_server.BaseAddress}/file/held.bin?size={size}&seed=927&ref={dashless}",
            "upper" => $"{_server.BaseAddress}/file/held.bin?size={size}&seed=927&ref={MockKey.ToUpperInvariant()}",
            "encoded" => $"{_server.BaseAddress}/file/held.bin?size={size}&seed=927&ref={MockKey.Replace("-", "%2D", StringComparison.Ordinal)}",
            "userinfo" => $"http://{dashless}@{address.Authority}/file/held.bin?size={size}&seed=927",
            // A route that sends no ETag: the file route would echo the name back in one.
            _ => $"{_server.BaseAddress}/typed/{MockKey}?size={size}&seed=927&type=application/octet-stream",
        };
        var response = await rig.Ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = url,
            PageUrl = $"{_server.BaseAddress}/page?ref={MockKey}",
            Referrer = $"{_server.BaseAddress}/page/{dashless}",
            FileName = giveName ? "held.bin" : null,
            DestinationDirectory = rig.Ctx.Destination,
            Source = "cli",
        });

        Assert.True(response.Accepted, response.Error);
        var added = response.Job!;
        Assert.False(added.IsRemote);
        Assert.Null(added.FinalUrl);
        Assert.InRange(added.RequestedConnections, 1, ProviderLinks.MaxConnections);
        foreach (var text in new[] { added.Url, added.PageUrl, added.FileName, added.FinalFilePath })
        {
            Assert.False(rig.Registry.Holds(text), $"The key is still in '{form}'.");
        }

        var completed = await rig.Ctx.WaitFor(added.Id, j => j.State == "completed", TimeSpan.FromSeconds(30));
        Assert.Equal(Sha256Of(927, size), await Hashing.Sha256FileAsync(completed.FinalFilePath));

        Assert.Empty(rig.Ctx.Service.GetSnapshot().Settings.RecentUrls);
        Assert.Contains("\"protectedUrl\": \"dpapi:v1:", await JobJsonAsync(rig.Ctx, added.Id));
        await rig.AssertNoLinkOrKeyLeakedAsync();
    }

    [Fact]
    public async Task AKeyInAFinalUrlGoesToTheProtectedFieldToo()
    {
        await using var rig = await TorBoxRig.CreateAsync(_server, connections: 1);
        var response = await rig.Ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/error/403",
            FinalUrl = $"{_server.BaseAddress}/file/final.bin?size=4096&seed=928&ref={MockKey}",
            FileName = "final.bin",
            DestinationDirectory = rig.Ctx.Destination,
            Source = "cli",
        });
        Assert.True(response.Accepted, response.Error);
        Assert.Null(response.Job!.FinalUrl);
        var completed = await rig.Ctx.WaitFor(response.Job.Id, j => j.State == "completed");
        Assert.Equal(Sha256Of(928, 4096), await Hashing.Sha256FileAsync(completed.FinalFilePath));
        Assert.Empty(rig.Ctx.Service.GetSnapshot().Settings.RecentUrls);
        await rig.AssertNoLinkOrKeyLeakedAsync();
    }

    [Fact]
    public async Task OnlyTheStoredKeyMakesAnAddressSpecial()
    {
        await using var rig = await TorBoxRig.CreateAsync(_server, key: null);
        var url = $"{_server.BaseAddress}/file/plain.bin?size=1024&seed=929&ref={MockKey}";
        var plain = await rig.Ctx.Service.AddJobAsync(new AddJobRequest { Url = url, FileName = "plain.bin", DestinationDirectory = rig.Ctx.Destination, Source = "cli" });
        Assert.Equal(url, plain.Job!.Url);
        Assert.Contains(url, rig.Ctx.Service.GetSnapshot().Settings.RecentUrls);

        await rig.Account.SetKeyAsync("ffffffff-ffff-4fff-8fff-ffffffffffff");
        var almost = $"{_server.BaseAddress}/file/plain2.bin?size=1024&seed=930&ref=aa{MockKey}bb";
        var other = await rig.Ctx.Service.AddJobAsync(new AddJobRequest { Url = almost, FileName = "plain2.bin", DestinationDirectory = rig.Ctx.Destination, Source = "cli" });
        Assert.Equal(almost, other.Job!.Url);
    }

    [Fact]
    public async Task StoringAKeyCleansAddressesAlreadySaved()
    {
        await using var rig = await TorBoxRig.CreateAsync(_server, key: null, connections: 1);
        const int size = 2 * Mb;
        var stopped = await rig.Ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/file/stopped.bin?size=4096&seed=931&ref={MockKey}",
            PageUrl = $"{_server.BaseAddress}/page?ref={MockKey}",
            FileName = "stopped.bin",
            DestinationDirectory = rig.Ctx.Destination,
            Source = "cli",
            StartMode = "later",
        });
        var running = await rig.Ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/slow/running.bin?size={size}&seed=932&bps={Mb}&ref={MockKey}",
            FileName = "running.bin",
            DestinationDirectory = rig.Ctx.Destination,
            Source = "cli",
        });
        await rig.Ctx.WaitFor(running.Job!.Id, j => j.ReceivedBytes > 0, TimeSpan.FromSeconds(30));
        Assert.Equal(2, rig.Ctx.Service.GetSnapshot().Settings.RecentUrls.Count);

        await rig.Account.SetKeyAsync(MockKey);
        await rig.Ctx.Service.ProtectStoredSecretsAsync();

        var job = rig.Ctx.Service.GetJob(stopped.Job!.Id)!;
        Assert.False(rig.Registry.Holds(job.Url));
        Assert.False(rig.Registry.Holds(job.PageUrl));
        Assert.Empty(rig.Ctx.Service.GetSnapshot().Settings.RecentUrls);
        Assert.False(rig.Registry.Holds(rig.Ctx.Service.GetJob(running.Job.Id)!.Url));

        // The running transfer goes on with the real address and finishes intact.
        var finished = await rig.Ctx.WaitFor(running.Job.Id, j => j.State == "completed", TimeSpan.FromSeconds(30));
        Assert.Equal(Sha256Of(932, size), await Hashing.Sha256FileAsync(finished.FinalFilePath));

        // The stopped one still downloads from its protected address.
        Assert.True((await rig.Ctx.Service.ResumeAsync(job.Id)).Ok);
        var resumed = await rig.Ctx.WaitFor(job.Id, j => j.State == "completed");
        Assert.Equal(Sha256Of(931, 4096), await Hashing.Sha256FileAsync(resumed.FinalFilePath));
        await rig.AssertNoLinkOrKeyLeakedAsync();
    }

    [Fact]
    public async Task AKeyStoredWhileTheHostWasDownIsCleanedAtStartup()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "test-work", Guid.NewGuid().ToString("N"));
        var paths = new StoragePaths(Path.Combine(root, "state"));
        var destination = Path.Combine(root, "downloads");
        Directory.CreateDirectory(destination);
        paths.EnsureCreated();
        var url = $"{_server.BaseAddress}/file/restart.bin?size=4096&seed=933&ref={MockKey}";

        Guid id;
        {
            await using var first = await ServiceContext.CreateAsync(_server, paths, destination);
            var added = await first.Service.AddJobAsync(new AddJobRequest { Url = url, FileName = "restart.bin", DestinationDirectory = destination, Source = "cli", StartMode = "later" });
            Assert.Contains(url, first.Service.GetSnapshot().Settings.RecentUrls);
            id = added.Job!.Id;
        }

        var account = await RemoteAccountStore.OpenAsync(paths, "torbox");
        await account.SetKeyAsync(MockKey);
        var registry = new RemoteProviderRegistry();
        registry.Add(new ListOnlyNamed("torbox"), account);

        await using var second = await ServiceContext.CreateAsync(_server, paths, destination, linkSecrets: registry);
        var restored = second.Service.GetJob(id)!;
        Assert.False(registry.Holds(restored.Url));
        Assert.Empty(second.Service.GetSnapshot().Settings.RecentUrls);
        await AssertSecretNotOnDiskAsync(second, MockKey);

        Assert.True((await second.Service.ResumeAsync(id)).Ok);
        var completed = await second.WaitFor(id, j => j.State == "completed");
        Assert.Equal(Sha256Of(933, 4096), await Hashing.Sha256FileAsync(completed.FinalFilePath));
    }

    private sealed class ListOnlyNamed(string id) : IRemoteProvider
    {
        public string Id => id;
        public string DisplayName => id;
        public RemoteCapabilities Capabilities => RemoteCapabilities.None;
        public RemoteTransferPolicy TransferPolicy { get; } = new();
        public Task<RemoteAccountInfo> GetAccountAsync(CancellationToken cancellationToken = default) => Task.FromResult(new RemoteAccountInfo());
    }
}
