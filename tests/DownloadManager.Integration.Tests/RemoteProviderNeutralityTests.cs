using System.Reflection;
using System.Text.Json;
using DownloadManager.Contracts;
using DownloadManager.Core;
using DownloadManager.Core.Providers;
using DownloadManager.Persistence;
using DownloadManager.Providers.TorBox;
using DownloadManager.Runtime;
using DownloadManager.Runtime.Remote;
using TestServer;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// A second provider that is nothing but the Core interfaces, next to the real one in the same
/// registry. Everything above the registry is untouched by it: if one of these needed a change in
/// Runtime to pass, adding a provider would not be just an implementation, a capability declaration
/// and a resolver.
/// </summary>
public sealed partial class RuntimeServiceTests
{
    private const string FakeProviderId = "fakebox";
    private const string FakeKey = "fakebox-invented-key-12345";

    /// <summary>Deliberately has no queue, no remover and no knowledge of any other provider.</summary>
    private sealed class FakeBox(string fileServer) :
        IRemoteProvider, IRemoteCatalog, IRemoteCacheChecker, IRemoteSubmitter, IRemoteLinkResolver, IRemoteHosterDirectory, IRemoteFolderLinkParser
    {
        private readonly Lock _sync = new();
        private readonly List<RemoteItem> _items = [];
        private readonly Dictionary<string, RemoteFile> _pendingFiles = [];
        private int _nextId = 700;

        public string Id => FakeProviderId;
        public string DisplayName => "Fake Box";
        public RemoteCapabilities Capabilities =>
            RemoteCapabilities.Account | RemoteCapabilities.Catalog | RemoteCapabilities.WebDownloads | RemoteCapabilities.CacheCheck | RemoteCapabilities.HosterList;
        public RemoteTransferPolicy TransferPolicy { get; } = new() { MaxConnectionsPerFile = 2 };

        public int Submissions { get; private set; }
        public int LinksIssued { get; private set; }
        public string? LastSubmittedLink { get; private set; }

        public RemoteItem AddReady(string name, params RemoteFile[] files)
        {
            lock (_sync)
            {
                var item = new RemoteItem
                {
                    Ref = new RemoteItemRef(Id, RemoteItemKind.WebDownload, (++_nextId).ToString()),
                    Name = name,
                    Stage = RemoteStage.Ready,
                    Files = files,
                    Progress = 1,
                };
                _items.Add(item);
                return item;
            }
        }

        public void PrepareSubmission(string link, RemoteFile file)
        {
            lock (_sync) _pendingFiles[PredictHash(RemoteItemKind.WebDownload, link)!] = file;
        }

        public void FinishAll()
        {
            lock (_sync)
            {
                for (var i = 0; i < _items.Count; i++)
                {
                    _items[i] = _items[i] with { Stage = RemoteStage.Ready, Progress = 1 };
                }
            }
        }

        public Task<RemoteAccountInfo> GetAccountAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new RemoteAccountInfo { DisplayPlan = "Fake plan", Premium = true });

        public Task<IReadOnlyList<RemoteItem>> ListAsync(RemoteItemKind kind, int offset, int limit, bool fresh, CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                return Task.FromResult<IReadOnlyList<RemoteItem>>(_items.Where(item => item.Ref.Kind == kind).Skip(offset).Take(limit).ToList());
            }
        }

        public Task<RemoteItem?> GetAsync(RemoteItemRef item, bool fresh, CancellationToken cancellationToken = default)
        {
            lock (_sync) return Task.FromResult(_items.FirstOrDefault(candidate => candidate.Ref == item));
        }

        public Task<IReadOnlyDictionary<string, RemoteCacheHit>> CheckCachedAsync(RemoteItemKind kind, IReadOnlyCollection<string> hashes, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<string, RemoteCacheHit>>(new Dictionary<string, RemoteCacheHit>());

        public string? PredictHash(RemoteItemKind kind, string link) => "fake-" + Convert.ToHexString(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(link)));

        public Task<RemoteSubmissionResult> SubmitAsync(RemoteSubmission submission, CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                Submissions++;
                LastSubmittedLink = submission.Link;
                var hash = PredictHash(submission.Kind, submission.Link!)!;
                var item = new RemoteItem
                {
                    Ref = new RemoteItemRef(Id, submission.Kind, (++_nextId).ToString()),
                    Name = "Fake item",
                    Hash = hash,
                    Stage = RemoteStage.Processing,
                    RawState = "working on it",
                    Progress = 0.4,
                    Files = [_pendingFiles[hash]],
                };
                _items.Add(item);
                return Task.FromResult(new RemoteSubmissionResult(item.Ref, hash, Queued: false));
            }
        }

        public Task<RemoteDirectLink> ResolveLinkAsync(RemoteItemRef item, string fileId, CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                LinksIssued++;
                var file = _items.Single(candidate => candidate.Ref == item).Files.Single(candidate => candidate.FileId == fileId);
                // Holds its key as a query value, as the real one does, under a name nothing special-cases.
                var seed = int.Parse(fileId);
                return Task.FromResult(new RemoteDirectLink(
                    new Uri($"{fileServer}/file/{Path.GetFileName(file.Path)}?size={file.Size}&seed={seed}&fakeauth={FakeKey}")));
            }
        }

        public Task<IReadOnlyList<RemoteHoster>> GetHostersAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<RemoteHoster>>([new RemoteHoster { Name = "Fake Hoster", Domains = ["hoster.fake.invalid"], Available = true }]);

        public RemoteFolderLinkParse? ParseFolderLink(string? text)
        {
            const string shape = "https://share.fake.invalid/item/";
            return text is not null && text.StartsWith(shape, StringComparison.Ordinal)
                ? new RemoteFolderLinkParse(new RemoteFolderReference(RemoteItemKind.WebDownload, text[shape.Length..]), null)
                : null;
        }
    }

    private sealed class NeutralRig : IAsyncDisposable
    {
        public required ServiceContext Ctx { get; init; }
        public required RemoteJobCoordinator Jobs { get; init; }
        public required RemoteCommands Commands { get; init; }
        public required FakeBox Fake { get; init; }
        public required RemoteProviderRegistry Registry { get; init; }
        public required ManualTime Time { get; init; }
        public required List<string> Log { get; init; }
        public required HttpClient Http { get; init; }

        public async Task AdvanceAsync(int seconds)
        {
            Time.Advance(TimeSpan.FromSeconds(seconds));
            await Jobs.RunDueAsync();
        }

        public async ValueTask DisposeAsync()
        {
            Commands.Dispose();
            await Jobs.DisposeAsync();
            await Ctx.DisposeAsync();
            Http.Dispose();
        }
    }

    /// <summary>The real TorBox client sits in the registry first, with no key and pointing at the mock, to show it stays out of the way.</summary>
    private async Task<NeutralRig> CreateNeutralRigAsync()
    {
        _server.TorBox.Reset();
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

        var http = new HttpClient();
        var registry = new RemoteProviderRegistry();
        var torBoxAccount = await RemoteAccountStore.OpenAsync(paths, TorBoxClient.ProviderId, Write);
        registry.Add(new TorBoxClient(http, torBoxAccount.KeySource, _server.TorBox.ApiBase), torBoxAccount);

        var fake = new FakeBox(_server.BaseAddress);
        var fakeAccount = await RemoteAccountStore.OpenAsync(paths, FakeProviderId, Write);
        await fakeAccount.SetKeyAsync(FakeKey);
        registry.Add(fake, fakeAccount);

        var broker = new RemoteLinkBroker(registry, TorBoxRig.Fast, log: Write);
        var ctx = await ServiceContext.CreateAsync(_server, paths, destination, linkBroker: broker, linkSecrets: registry);
        var settings = ctx.Service.GetSnapshot().Settings;
        settings.DefaultConnections = 8;
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);

        var time = new ManualTime();
        var jobs = new RemoteJobCoordinator(registry, new JsonRemoteJobStore(paths), ctx.Service, new RemoteCoordinatorOptions { RunLoops = false, Jitter = () => 0.5 }, time, Write);
        await jobs.StartAsync();
        return new NeutralRig
        {
            Ctx = ctx,
            Jobs = jobs,
            Commands = new RemoteCommands(registry, jobs, time, Write),
            Fake = fake,
            Registry = registry,
            Time = time,
            Log = log,
            Http = http,
        };
    }

    private static RemoteFile FakeFile(int id, string path, int size) => new() { FileId = id.ToString(), Path = path, Size = size };

    private async Task AssertFakeJobCompletesAsync(NeutralRig rig, JobDto job, int seed, int size)
    {
        var done = await rig.Ctx.WaitFor(job.Id, candidate => candidate.State is "completed" or "failed", TimeSpan.FromSeconds(30));
        AssertCompleted(done);
        Assert.Equal(Sha256Of(seed, size), await Hashing.Sha256FileAsync(done.FinalFilePath));
    }

    /// <summary>Null only for a save's own temporary file. Anything else that can't be read is a reason to fail.</summary>
    private static string? ReadIfOpenable(string file)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch (IOException) when (file.EndsWith(".tmp", StringComparison.Ordinal))
            {
                return null;
            }
            catch (IOException) when (attempt < 10)
            {
                Thread.Sleep(20);
            }
        }
    }

    private static void AssertFakeSecretsNowhere(NeutralRig rig, ServiceContext ctx, params string[] more)
    {
        var snapshot = JsonSerializer.Serialize(ctx.Service.GetSnapshot());
        string lines;
        lock (rig.Log) lines = string.Join('\n', rig.Log);
        foreach (var secret in more.Append(FakeKey))
        {
            Assert.DoesNotContain(secret, snapshot);
            Assert.DoesNotContain(secret, lines);
            foreach (var file in Directory.GetFiles(ctx.Paths.Root, "*", SearchOption.AllDirectories))
            {
                // The service is still running, so a save's temporary file can be here, held open or already gone.
                if (ReadIfOpenable(file) is { } text) Assert.DoesNotContain(secret, text);
            }
        }
    }

    [Fact]
    public async Task ASecondProvider_ScenarioA_RunsThroughRuntimeUnchanged()
    {
        await using var rig = await CreateNeutralRigAsync();
        var item = rig.Fake.AddReady("Shared", FakeFile(11, "Shared/sub/a.bin", 48 * Kb), FakeFile(12, "Shared/b.bin", 16 * Kb), FakeFile(13, "Shared/left-out.bin", 8 * Kb));

        var classified = await rig.Commands.ClassifyAsync(new RemoteClassifyRequest { Text = $"https://share.fake.invalid/item/{item.Ref.ItemId}" });
        Assert.Equal("folderLink", classified.Kind);
        Assert.Equal(FakeProviderId, classified.Provider);
        Assert.Equal("Fake Box", classified.ProviderName);

        var added = await rig.Commands.AddAsync(new RemoteAddRequestDto
        {
            Input = $"https://share.fake.invalid/item/{item.Ref.ItemId}",
            Destination = rig.Ctx.Destination,
        });
        Assert.True(added.Ok, added.Error);
        var id = added.Job!.Id;
        Assert.Equal(FakeProviderId, added.Job.Provider);

        await rig.Jobs.RunDueAsync();
        Assert.Equal("awaitingSelection", rig.Commands.List().Jobs.Single().State);

        var files = await rig.Commands.GetFilesAsync(new RemoteJobIdRequest { JobId = id });
        Assert.True(files.Ok, files.Error);
        Assert.Equal(3, files.Files.Count);
        Assert.Contains(files.Files, file => file.RelativePath == Path.Combine("Shared", "sub", "a.bin"));

        Assert.True((await rig.Commands.SelectAsync(new RemoteSelectRequest { JobId = id, FileIds = ["11", "12"] })).Ok);
        await rig.Jobs.RunDueAsync();
        Assert.Equal("localStarted", rig.Commands.List().Jobs.Single().State);

        var children = rig.Ctx.Service.GetSnapshot().Jobs.Where(job => job.Url.StartsWith($"remote://{FakeProviderId}/webdl/{item.Ref.ItemId}/", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, children.Count);
        Assert.All(children, child =>
        {
            Assert.Equal("remote", child.Source);
            // Its own policy value, with nothing to say about the other provider's.
            Assert.InRange(child.RequestedConnections, 1, 2);
        });
        await AssertFakeJobCompletesAsync(rig, children.Single(job => job.FileName == "a.bin"), 11, 48 * Kb);
        await AssertFakeJobCompletesAsync(rig, children.Single(job => job.FileName == "b.bin"), 12, 16 * Kb);
        Assert.False(File.Exists(Path.Combine(rig.Ctx.Destination, "Shared", "left-out.bin")));
        Assert.True(File.Exists(Path.Combine(rig.Ctx.Destination, "Shared", "sub", "a.bin")));

        Assert.Equal(0, rig.Fake.Submissions);
        Assert.Equal(0, _server.TorBox.TotalApiCalls);
        AssertFakeSecretsNowhere(rig, rig.Ctx, "fakeauth");
    }

    [Fact]
    public async Task ASecondProvider_ScenarioC_RunsThroughRuntimeUnchanged()
    {
        await using var rig = await CreateNeutralRigAsync();
        const string link = "https://hoster.fake.invalid/file/abc#invented-fake-fragment";
        rig.Fake.PrepareSubmission(link, FakeFile(21, "Fake item/fake.bin", 32 * Kb));

        var classified = await rig.Commands.ClassifyAsync(new RemoteClassifyRequest { Text = link });
        Assert.Equal("hosterLink", classified.Kind);
        Assert.Equal(FakeProviderId, classified.Provider);
        Assert.Equal("Fake Hoster", classified.HosterName);
        Assert.Equal("hoster.fake.invalid", classified.DisplayHost);

        var added = await rig.Commands.AddAsync(new RemoteAddRequestDto { Input = link, Destination = rig.Ctx.Destination, Selection = "all" });
        Assert.True(added.Ok, added.Error);
        var id = added.Job!.Id;

        // Not cached, so it asks, in the provider's own name, and sends nothing.
        await rig.Jobs.RunDueAsync();
        var asking = rig.Commands.List().Jobs.Single();
        Assert.True(asking.ApprovalNeeded);
        Assert.Contains("Fake Box", asking.Status);
        Assert.DoesNotContain("TorBox", asking.Status, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, rig.Fake.Submissions);

        Assert.True((await rig.Commands.RetryAsync(new RemoteRetryRequest { JobId = id, ApproveUncached = true })).Ok);
        await rig.Jobs.RunDueAsync();
        Assert.Equal(1, rig.Fake.Submissions);
        Assert.Equal(link, rig.Fake.LastSubmittedLink);

        var working = rig.Commands.List().Jobs.Single();
        Assert.Equal("waiting", working.State);
        Assert.Equal("processing", working.Stage);
        Assert.Equal("working on it", working.RawState);
        Assert.Contains("Fake Box", working.Status);

        // A restart picks it up as it does any other. There is no queue to ask, and nothing needs one.
        await rig.AdvanceAsync(30);
        Assert.Equal("waiting", rig.Commands.List().Jobs.Single().State);
        rig.Fake.FinishAll();
        await rig.AdvanceAsync(30);
        Assert.Equal("localStarted", rig.Commands.List().Jobs.Single().State);
        Assert.Equal(1, rig.Fake.Submissions);

        var child = Assert.Single(rig.Ctx.Service.GetSnapshot().Jobs, job => job.Url.StartsWith($"remote://{FakeProviderId}/", StringComparison.Ordinal));
        await AssertFakeJobCompletesAsync(rig, child, 21, 32 * Kb);
        Assert.Equal(0, _server.TorBox.TotalApiCalls);
        AssertFakeSecretsNowhere(rig, rig.Ctx, link, "invented-fake-fragment", "fakeauth");

        // No remover declared: the same message gets a plain refusal, not a crash.
        var refused = await rig.Commands.DeleteRemoteAsync(new RemoteDeleteRequest { JobId = id, Confirmed = true });
        Assert.False(refused.Ok);
        Assert.Contains("Fake Box", refused.Error);
    }

    [Fact]
    public async Task ASecondProvider_IsReadThroughTheSameMessagesAsTheFirst()
    {
        await using var rig = await CreateNeutralRigAsync();
        rig.Fake.AddReady("Listed", FakeFile(31, "Listed/a.bin", Kb));

        var accounts = await rig.Commands.GetAccountAsync(new RemoteAccountRequest { Refresh = true });
        Assert.True(accounts.Ok, accounts.Error);
        Assert.Equal(2, accounts.Accounts.Count);
        var fake = Assert.Single(accounts.Accounts, account => account.Provider == FakeProviderId);
        Assert.Equal("Fake plan", fake.Plan);
        Assert.True(fake.HasKey);
        Assert.Equal(["webdl"], fake.ItemKinds);
        var torBox = Assert.Single(accounts.Accounts, account => account.Provider == TorBoxClient.ProviderId);
        Assert.False(torBox.HasKey);
        Assert.Equal(0, _server.TorBox.TotalApiCalls);

        // With two, a change has to say which, and each has its own kinds.
        Assert.Contains("which provider", (await rig.Commands.SetKeyAsync(new RemoteSetKeyRequest { Key = "another-key" })).Error);
        Assert.Contains("which provider", (await rig.Commands.GetItemsAsync(new RemoteItemRequest { Kind = "webdl" })).Error);
        var page = await rig.Commands.GetItemsAsync(new RemoteItemRequest { Provider = FakeProviderId, Kind = "webdl" });
        Assert.Equal("Listed", Assert.Single(page.Items).Name);
        Assert.Contains("has no torrents", (await rig.Commands.GetItemsAsync(new RemoteItemRequest { Provider = FakeProviderId, Kind = "torrents" })).Error);
        Assert.Contains("no provider called", (await rig.Commands.GetAccountAsync(new RemoteAccountRequest { Provider = "nobody" })).Error);

        // The first provider's unset key doesn't speak for the second's links, or the other way round.
        var other = await rig.Commands.GetItemsAsync(new RemoteItemRequest { Provider = TorBoxClient.ProviderId, Kind = "torrents" });
        Assert.False(other.Ok);
        Assert.Contains("TorBox API key", other.Error);
    }

    [Fact]
    public void RuntimeAndContractsNameNoConcreteProvider()
    {
        Assert.DoesNotContain(typeof(RemoteJobCoordinator).Assembly.GetReferencedAssemblies(), name => name.Name!.Contains("Providers", StringComparison.Ordinal));
        Assert.DoesNotContain(typeof(RemoteCommands).Assembly.GetTypes(), type => type.FullName!.Contains("TorBox", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(typeof(RemoteJobDto).Assembly.GetTypes(), type => type.FullName!.Contains("TorBox", StringComparison.OrdinalIgnoreCase));

        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "DownloadManager.slnx")))
        {
            root = root.Parent!;
        }

        foreach (var project in new[] { "DownloadManager.Runtime", "DownloadManager.Contracts" })
        {
            var sources = new DirectoryInfo(Path.Combine(root.FullName, "src", project)).GetFiles("*.cs", SearchOption.AllDirectories)
                .Where(file => !file.FullName.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                               !file.FullName.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                .ToList();
            Assert.NotEmpty(sources);
            foreach (var source in sources)
            {
                Assert.False(File.ReadAllText(source.FullName).Contains("torbox", StringComparison.OrdinalIgnoreCase), $"{source.Name} names a concrete provider.");
            }
        }
    }
}
