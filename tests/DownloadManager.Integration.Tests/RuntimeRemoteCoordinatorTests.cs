using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DownloadManager.Contracts;
using DownloadManager.Core;
using DownloadManager.Core.Providers;
using DownloadManager.Persistence;
using DownloadManager.Runtime;
using DownloadManager.Runtime.Remote;
using TestServer;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// The real coordinator, service, client and engine against the mock over real HTTP. Time is manual
/// on the coordinator's side, so the 65 s spacing and the minute of grace take no real waiting. All
/// values are invented.
/// </summary>
public sealed partial class RuntimeServiceTests
{
    private const string CreateWeb = "POST webdl/createwebdownload";
    private const string CreateTorrent = "POST torrents/createtorrent";
    private const int Kb = 1024;

    private sealed class ManualTime : TimeProvider
    {
        private readonly Lock _sync = new();
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow()
        {
            lock (_sync) return _now;
        }

        public void Advance(TimeSpan by)
        {
            List<ManualTimer> due;
            lock (_sync)
            {
                _now += by;
                due = _timers.Where(timer => timer.Due is { } at && at <= _now).ToList();
                foreach (var timer in due) timer.Due = null;
            }

            foreach (var timer in due) timer.Fire();
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            lock (_sync) _timers.Add(timer);
            return timer;
        }

        private sealed class ManualTimer(ManualTime owner, TimerCallback callback, object? state) : ITimer
        {
            public DateTimeOffset? Due { get; set; }

            public void Fire() => callback(state);

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner._sync) Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
                return true;
            }

            public void Dispose()
            {
                lock (owner._sync)
                {
                    Due = null;
                    owner._timers.Remove(this);
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    /// <summary>A store whose process "dies" at a chosen write: that write either makes it to disk or doesn't, and none after it do.</summary>
    private sealed class KillableStore(IRemoteJobStore inner) : IRemoteJobStore
    {
        private int _writes;
        public int KillAtWrite { get; set; } = int.MaxValue;
        public bool PersistKillingWrite { get; set; }
        public bool Dead { get; set; }
        public int Writes => _writes;
        public Func<RemoteJobRecord, bool>? KillOn { get; set; }

        private async Task WriteAsync(Func<Task> write, RemoteJobRecord? record = null)
        {
            if (Dead) throw new IOException("Killed.");
            var kill = ++_writes >= KillAtWrite || (record is not null && KillOn?.Invoke(record) == true);
            if (kill)
            {
                Dead = true;
                if (PersistKillingWrite) await write();
                throw new IOException("Killed.");
            }

            await write();
        }

        public Task<IReadOnlyList<RemoteJobRecord>> LoadAllAsync(CancellationToken cancellationToken = default) => inner.LoadAllAsync(cancellationToken);
        public Task SaveAsync(RemoteJobRecord record, CancellationToken cancellationToken = default) => WriteAsync(() => inner.SaveAsync(record, cancellationToken), record);
        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => WriteAsync(() => inner.DeleteAsync(id, cancellationToken));
        public Task<RemoteAccountPacing> LoadPacingAsync(string providerId, CancellationToken cancellationToken = default) => inner.LoadPacingAsync(providerId, cancellationToken);
        public Task SavePacingAsync(string providerId, RemoteAccountPacing pacing, CancellationToken cancellationToken = default) => WriteAsync(() => inner.SavePacingAsync(providerId, pacing, cancellationToken));
    }

    /// <summary>A service that "dies" while local jobs are being created, having got through this many.</summary>
    private sealed class KillableHost(IRemoteJobHost inner) : IRemoteJobHost
    {
        public int? DieAfterJobs { get; set; }
        public Action? OnDeath { get; set; }
        public Func<Task>? BeforeAdd { get; set; }

        public string DefaultDownloadDirectory => inner.DefaultDownloadDirectory;

        public RemoteLocalSummary? GetLocalSummary(Guid packageId) => inner.GetLocalSummary(packageId);

        public int? CountLocalJobs(Guid remoteJobId) => inner.CountLocalJobs(remoteJobId);

        public async Task<RemoteJobsResult> AddRemoteJobsAsync(IReadOnlyList<RemoteJobRequest> requests, string packageKey, string? packageName = null, CancellationToken cancellationToken = default)
        {
            if (BeforeAdd is { } before) await before();
            if (DieAfterJobs is { } count)
            {
                if (count > 0) await inner.AddRemoteJobsAsync(requests.Take(count).ToList(), "unfinished:" + packageKey, packageName, cancellationToken);
                OnDeath?.Invoke();
                throw new IOException("Killed.");
            }

            return await inner.AddRemoteJobsAsync(requests, packageKey, packageName, cancellationToken);
        }
    }

    private sealed class RemoteRig : IAsyncDisposable
    {
        private readonly TestServerHost _server;
        private readonly List<ServiceContext> _contexts = [];

        private RemoteRig(TestServerHost server, TorBoxRig box)
        {
            _server = server;
            Box = box;
            Ctx = box.Ctx;
            Store = new KillableStore(new JsonRemoteJobStore(box.Ctx.Paths));
            Host = new KillableHost(box.Ctx.Service);
        }

        public TorBoxRig Box { get; }
        public ServiceContext Ctx { get; private set; }
        public ManualTime Time { get; } = new();
        public KillableStore Store { get; private set; }
        public KillableHost Host { get; private set; }
        public RemoteJobCoordinator Jobs { get; private set; } = null!;
        public MockTorBox Mock => Box.Mock;
        public string Destination => Ctx.Destination;
        public List<string> Log { get; } = [];

        public RemoteCoordinatorOptions Options { get; init; } = new();

        public static async Task<RemoteRig> CreateAsync(TestServerHost server, string? key = MockKey, int connections = 2, bool loops = false, RemoteCoordinatorOptions? options = null)
        {
            var box = await TorBoxRig.CreateAsync(server, key: key, connections: connections);
            var rig = new RemoteRig(server, box) { Options = options ?? new RemoteCoordinatorOptions() };
            rig.Mock.AddHoster("Pretend", ["pretend.invalid", "127.0.0.1", "localhost"], "/hoster/");
            rig.Mock.AddHoster("Pretend Down", ["down.invalid", "127.0.0.1", "localhost"], "/hoster-down/", available: false, note: "Down for maintenance");
            rig.Jobs = rig.NewCoordinator(loops);
            await rig.Jobs.StartAsync();
            return rig;
        }

        private RemoteJobCoordinator NewCoordinator(bool loops)
        {
            var jobs = new RemoteJobCoordinator(
                Box.Registry,
                Store,
                Host,
                Options with { RunLoops = loops, Jitter = () => 0.5 },
                Time,
                line =>
                {
                    lock (Log) Log.Add(line);
                });
            // As the Host does.
            Ctx.Service.DirectAddresses = jobs;
            return jobs;
        }

        /// <summary>
        /// The process is gone: nothing more gets written, the coordinator is dropped where it stands,
        /// the service stops. Then everything starts again from what is on disk.
        /// </summary>
        public async Task RestartAsync(bool loops = false)
        {
            await Jobs.StopAsync();
            await Ctx.Service.ShutdownAsync();
            var ctx = await ServiceContext.CreateAsync(_server, Ctx.Paths, Ctx.Destination, linkBroker: Box.Broker, linkSecrets: Box.Registry);
            _contexts.Add(ctx);
            Ctx = ctx;
            Store = new KillableStore(new JsonRemoteJobStore(ctx.Paths));
            Host = new KillableHost(ctx.Service);
            Jobs = NewCoordinator(loops);
            await Jobs.StartAsync();
        }

        /// <summary>Lets a coordinator with a chosen store and host start in place of the running one.</summary>
        public async Task RestartCoordinatorOnlyAsync()
        {
            await Jobs.StopAsync();
            Store = new KillableStore(new JsonRemoteJobStore(Ctx.Paths));
            Jobs = NewCoordinator(false);
            await Jobs.StartAsync();
        }

        public Task RunAsync() => Jobs.RunDueAsync();

        public async Task AdvanceAsync(int seconds)
        {
            Time.Advance(TimeSpan.FromSeconds(seconds));
            await Jobs.RunDueAsync();
        }

        public RemoteJobView View(Guid id) => Jobs.Find(id) ?? throw new InvalidOperationException("Not on the list.");

        public IReadOnlyList<JobDto> Children() => Ctx.Service.GetSnapshot().Jobs.Where(job => job.Url.StartsWith("remote://", StringComparison.Ordinal)).ToList();

        public async Task<RemoteJobView> AddAsync(RemoteAddRequest request)
        {
            var result = await Jobs.AddAsync(request with { Destination = request.Destination ?? Destination });
            Assert.True(result.Error is null, result.Error);
            return result.Job!;
        }

        /// <summary>A link on the mock's own address, standing in for a file on a hoster.</summary>
        public string HosterLink(string name) => Mock.HosterUrl($"file/{name}#invented-fragment-{name}");

        public static string Md5Of(string text) => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

        public (string Link, string Hash, MockTorBoxFile File) ScriptHoster(string name, params MockTorBoxState[] states)
        {
            var link = HosterLink(name);
            var hash = Md5Of(link);
            var file = MockTorBoxFile.Of(Mock.NextFileId(), $"{name}/{name}.bin", 64 * Kb, seed: 700 + name.Length);
            Mock.ScriptSubmission(hash, [file], states);
            return (link, hash, file);
        }

        public async Task AssertCompletedAsync(MockTorBoxFile file, params string[] relative)
        {
            var path = Path.Combine([Destination, .. relative]);
            var job = Assert.Single(Children(), candidate => candidate.FinalFilePath == path);
            // This harness stops the service politely, which pauses what was running. A real kill doesn't.
            if (job.State == "paused") Assert.True((await Ctx.Service.ResumeAsync(job.Id)).Ok);
            var done =await Ctx.WaitFor(job.Id, candidate => candidate.State is "completed" or "failed", TimeSpan.FromSeconds(30));
            AssertCompleted(done);
            Assert.Equal(Sha256Of(file.Content), await Hashing.Sha256FileAsync(path));
        }

        /// <summary>Everything written, logged, sent to clients or shown, against the given secrets, and against the key always.</summary>
        public async Task AssertNothingLeakedAsync(params string[] secrets)
        {
            var all = secrets.Append(MockKey).Append(MockKey.Replace("-", string.Empty, StringComparison.Ordinal))
                .Concat(Mock.IssuedLinks.Select(link => link.Uuid)).ToList();
            var snapshot = JsonSerializer.Serialize(Ctx.Service.GetSnapshot());
            var views = JsonSerializer.Serialize(Jobs.List());
            string lines;
            lock (Log) lines = string.Join('\n', Log);
            foreach (var secret in all)
            {
                await AssertSecretNotOnDiskAsync(Ctx, secret);
                Assert.DoesNotContain(secret, snapshot);
                Assert.DoesNotContain(secret, views);
                Assert.DoesNotContain(secret, lines);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Jobs.DisposeAsync();
            foreach (var ctx in _contexts) await ctx.DisposeAsync();
            await Box.DisposeAsync();
        }
    }

    private static async Task Eventually(Func<bool> condition, string what)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow > deadline) throw new TimeoutException($"Waited too long for: {what}");
            await Task.Delay(20);
        }
    }

    /// <summary>
    /// For a loop waiting on the manual clock. The clock keeps moving, because a loop that was still
    /// finishing a pass when it first moved has only just started waiting.
    /// </summary>
    private static async Task EventuallyWithTime(ManualTime time, Func<bool> condition, string what)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow > deadline) throw new TimeoutException($"Waited too long for: {what}");
            time.Advance(TimeSpan.FromSeconds(5));
            await Task.Delay(20);
        }
    }

    // ---- Scenario A: folder links -------------------------------------------------------------

    [Fact]
    public async Task ScenarioA_AFolderLinkShowsFilesAndDownloadsTheChosenOnesWithFoldersKept()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        MockTorBoxFile Add(string name, int seed) => MockTorBoxFile.Of(rig.Mock.NextFileId(), name, 96 * Kb, seed);
        var readme = Add("Pack/readme.txt", 11);
        var first = Add("Pack/sub/a/same.bin", 12);
        var second = Add("Pack/sub/b/same.bin", 13);
        var skipped = Add("Pack/skipped.bin", 14);
        var item = rig.Mock.AddItem(RemoteItemKind.Torrent, "Pack", [readme, first, second, skipped]);

        // The name on the link is noise, and tries something.
        var added = await rig.Jobs.AddAsync(new RemoteAddRequest
        {
            Input = $"https://torbox.app/download?id={item.Id}&type=torrents&name=Elsewhere%2F..%2F..%2Fwindows",
            Destination = rig.Destination,
        });
        Assert.Equal(RemoteInputKind.FolderLink, added.Kind);
        Assert.Equal(RemoteJobState.Waiting, added.Job!.State);
        Assert.Equal(item.Id.ToString(), added.Job.ItemId);

        await rig.RunAsync();
        var id = added.Job.Id;
        var waiting = rig.View(id);
        Assert.Equal(RemoteJobState.AwaitingSelection, waiting.State);
        Assert.Equal("Pack", waiting.Name);
        Assert.Empty(rig.Children());

        var listed = await rig.Jobs.GetFilesAsync(id);
        Assert.False(listed.Pending);
        Assert.Equal(4, listed.Files.Count);
        Assert.All(listed.Files, file => Assert.True(file.Selectable));
        Assert.Contains(listed.Files, file => file.RelativePath == Path.Combine("Pack", "sub", "a", "same.bin"));

        var chosen = await rig.Jobs.ChooseFilesAsync(id, all: false, [readme.Id.ToString(), first.Id.ToString(), second.Id.ToString()]);
        Assert.True(chosen.Ok, chosen.Error);
        await rig.RunAsync();

        var started = rig.View(id);
        Assert.Equal(RemoteJobState.LocalStarted, started.State);
        Assert.NotNull(started.PackageId);
        Assert.Contains(started.Status, new[] { "Downloading locally", "Waiting to download locally", "Completed" });
        Assert.Equal(3, rig.Children().Count);
        await rig.AssertCompletedAsync(readme, "Pack", "readme.txt");
        await rig.AssertCompletedAsync(first, "Pack", "sub", "a", "same.bin");
        await rig.AssertCompletedAsync(second, "Pack", "sub", "b", "same.bin");
        Assert.False(File.Exists(Path.Combine(rig.Destination, "Pack", "skipped.bin")));

        var package = Assert.Single(rig.Ctx.Service.GetSnapshot().Settings.DownloadGroups, group => group.Id == started.PackageId);
        Assert.Equal($"batch:remote:{id:N}", package.Key);
        Assert.Equal("Pack", package.Name);
        Assert.Equal(3, package.JobIds.Count);

        // Nothing submitted: the item was already there.
        Assert.Empty(rig.Mock.Submissions);
        Assert.Equal(0, rig.Mock.CdnRequestsWithCredentials);
        await rig.AssertNothingLeakedAsync();
    }

    [Theory]
    [InlineData("torrents", RemoteItemKind.Torrent)]
    [InlineData("web_downloads", RemoteItemKind.WebDownload)]
    [InlineData("usenet_downloads", RemoteItemKind.Usenet)]
    public async Task ScenarioA_EveryTypeGoesThroughTheSamePath(string type, RemoteItemKind kind)
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var file = MockTorBoxFile.Of(rig.Mock.NextFileId(), "single.bin", 32 * Kb, seed: 21);
        var item = rig.Mock.AddItem(kind, "single", [file]);

        var job = await rig.AddAsync(new RemoteAddRequest
        {
            Input = $"https://torbox.app/download?type={type}&extra=1&id={item.Id}",
            Selection = RemoteSelectionMode.All,
        });
        await rig.RunAsync();

        Assert.Equal(RemoteJobState.LocalStarted, rig.View(job.Id).State);
        await rig.AssertCompletedAsync(file, "single.bin");
        Assert.Empty(rig.Mock.Submissions);
    }

    [Fact]
    public async Task ScenarioA_AFolderLinkFromAnotherAccountGetsAPlainAnswerAndNothingElseHappens()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var job = await rig.AddAsync(new RemoteAddRequest { Input = "https://torbox.app/download?id=999999&type=torrents&name=Theirs" });
        await rig.RunAsync();

        var failed = rig.View(job.Id);
        Assert.Equal(RemoteJobState.Failed, failed.State);
        Assert.Equal(RemoteFailureKind.NotFound, failed.FailureKind);
        Assert.Contains("not on your TorBox account", failed.FailureMessage);
        Assert.Empty(rig.Children());
        Assert.Equal(1, rig.Mock.TotalApiCalls);
        Assert.Empty(rig.Mock.Submissions);
    }

    [Fact]
    public async Task ScenarioA_AnItemRemovedWhileWaitingIsCalledGoneOnlyAfterItsGrace()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var item = rig.Mock.AddItem(RemoteItemKind.Torrent, "Pack", [MockTorBoxFile.Of(rig.Mock.NextFileId(), "Pack/a.bin", Kb, 1)], MockTorBoxState.Downloading(), MockTorBoxState.Downloading());
        var job = await rig.AddAsync(new RemoteAddRequest { Input = $"https://torbox.app/download?id={item.Id}&type=torrents" });
        await rig.RunAsync();
        Assert.Equal(RemoteJobState.Waiting, rig.View(job.Id).State);

        rig.Mock.RemoveItem(item);
        await rig.AdvanceAsync(60);
        Assert.Equal(RemoteJobState.Waiting, rig.View(job.Id).State);
        Assert.True(rig.Mock.Calls("GET torrents/mylist") >= 2, "The list is read before giving up.");
        Assert.True(rig.Mock.Calls("GET queued/getqueued") >= 1, "The queue is read before giving up.");

        await rig.AdvanceAsync(30);
        Assert.Equal(RemoteJobState.Waiting, rig.View(job.Id).State);
        await rig.AdvanceAsync(31);
        var failed = rig.View(job.Id);
        Assert.Equal(RemoteJobState.Failed, failed.State);
        Assert.Equal(RemoteFailureKind.NotFound, failed.FailureKind);
        Assert.Empty(rig.Mock.Submissions);
    }

    /// <summary>Seen live: an expired item keeps its file list and never comes back, so it can't be waited for.</summary>
    [Fact]
    public async Task ScenarioA_AnExpiredItemFailsRatherThanWaitingForever()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var item = rig.Mock.AddItem(RemoteItemKind.Torrent, "Pack", [MockTorBoxFile.Of(0, "Pack/a.bin", Kb, 1)], MockTorBoxState.Expired);
        var job = await rig.AddAsync(new RemoteAddRequest { Input = $"https://torbox.app/download?id={item.Id}&type=torrents" });

        await rig.RunAsync();

        var failed = rig.View(job.Id);
        Assert.Equal(RemoteJobState.Failed, failed.State);
        Assert.Contains("expired", failed.FailureMessage);
        Assert.Empty(rig.Children());
        Assert.Empty(rig.Mock.Submissions);
    }

    [Fact]
    public async Task ScenarioA_AddingAnAttachedItemTwiceReturnsTheSameJob()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var item = rig.Mock.AddItem(RemoteItemKind.Torrent, "Pack", [MockTorBoxFile.Of(rig.Mock.NextFileId(), "Pack/a.bin", Kb, 1)]);
        var link = $"https://torbox.app/download?id={item.Id}&type=torrents";
        var first = await rig.AddAsync(new RemoteAddRequest { Input = link });
        var second = await rig.Jobs.AddAsync(new RemoteAddRequest { Input = link + "&name=Other", Destination = rig.Destination });
        Assert.True(second.Existing);
        Assert.Equal(first.Id, second.Job!.Id);
        Assert.Single(rig.Jobs.List());
    }

    [Fact]
    public async Task ScenarioA_InfectedEmptyAndUnsafeFilesAreNotDownloadedAndSaySo()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var good = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Pack/good.bin", 32 * Kb, 1);
        var infected = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Pack/bad.bin", 32 * Kb, 2, infected: true);
        var empty = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Pack/empty.bin", 0);
        var unsafePath = MockTorBoxFile.Of(rig.Mock.NextFileId(), "../../../evil.bin", 32 * Kb, 3);
        var item = rig.Mock.AddItem(RemoteItemKind.Torrent, "Pack", [good, infected, empty, unsafePath]);

        var job = await rig.AddAsync(new RemoteAddRequest { Input = $"https://torbox.app/download?id={item.Id}&type=torrents", Selection = RemoteSelectionMode.All });
        await rig.RunAsync();

        var started = rig.View(job.Id);
        Assert.Equal(RemoteJobState.LocalStarted, started.State);
        await rig.AssertCompletedAsync(good, "Pack", "good.bin");
        Assert.Single(rig.Children());
        Assert.Equal(3, started.Notes.Count);
        Assert.Contains(started.Notes, note => note.Contains("infected"));
        Assert.Contains(started.Notes, note => note.Contains("empty"));
        Assert.Contains(started.Notes, note => note.Contains("safe"));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(rig.Destination)!, "evil.bin")));
    }

    [Fact]
    public async Task ScenarioA_WhenEveryFileIsLeftOutTheJobSaysSoInsteadOfPretending()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var item = rig.Mock.AddItem(RemoteItemKind.Torrent, "Pack", [MockTorBoxFile.Of(rig.Mock.NextFileId(), "Pack/bad.bin", Kb, 2, infected: true)]);
        var job = await rig.AddAsync(new RemoteAddRequest { Input = $"https://torbox.app/download?id={item.Id}&type=torrents", Selection = RemoteSelectionMode.All });
        await rig.RunAsync();

        var failed = rig.View(job.Id);
        Assert.Equal(RemoteJobState.Failed, failed.State);
        Assert.Equal("None of its files can be downloaded.", failed.FailureMessage);
        Assert.Contains(failed.Notes, note => note.Contains("infected"));
        Assert.Empty(rig.Children());
    }

    [Fact]
    public async Task ChosenStartModeAndQueueReachTheLocalJobs()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var settings = rig.Ctx.Service.GetSnapshot().Settings;
        var file = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Pack/a.bin", 32 * Kb, 1);
        var item = rig.Mock.AddItem(RemoteItemKind.Torrent, "Pack", [file]);
        var job = await rig.AddAsync(new RemoteAddRequest
        {
            Input = $"https://torbox.app/download?id={item.Id}&type=torrents",
            Selection = RemoteSelectionMode.All,
            StartMode = "later",
            QueueId = settings.DefaultQueueId,
        });
        await rig.RunAsync();

        Assert.Equal(RemoteJobState.LocalStarted, rig.View(job.Id).State);
        var child = Assert.Single(rig.Children());
        Assert.Equal("later", child.StartMode);
        Assert.Equal(settings.DefaultQueueId, child.QueueId);
        Assert.Equal("queued", child.State);
        Assert.Equal("remote", child.Source);
        await Task.Delay(200);
        Assert.Equal("queued", rig.Ctx.Service.GetJob(child.Id)!.State);
        Assert.Empty(rig.Mock.IssuedLinks);
    }

    [Fact]
    public async Task WithNoDestinationTheFilesGoToTheDefaultFolder()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var file = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Pack/a.bin", 32 * Kb, 1);
        var item = rig.Mock.AddItem(RemoteItemKind.Torrent, "Pack", [file]);
        var added = await rig.Jobs.AddAsync(new RemoteAddRequest { Input = $"https://torbox.app/download?id={item.Id}&type=torrents", Selection = RemoteSelectionMode.All });
        Assert.Null(added.Job!.Destination);
        await rig.RunAsync();
        await rig.AssertCompletedAsync(file, "Pack", "a.bin");
    }

    [Theory]
    [InlineData("relative/path", "startNow", "full path")]
    [InlineData(null, "whenever", "start mode")]
    public async Task ABadRequestIsRefusedBeforeAnythingIsSaved(string? destination, string startMode, string error)
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var result = await rig.Jobs.AddAsync(new RemoteAddRequest { Input = "https://torbox.app/download?id=1&type=torrents", Destination = destination, StartMode = startMode });
        Assert.Contains(error, result.Error);
        Assert.Empty(rig.Jobs.List());
        Assert.Empty(Directory.GetFiles(rig.Ctx.Paths.RemoteJobsDirectory));
    }

    [Fact]
    public async Task OrdinaryAndUnsupportedInputsAreLeftAloneWithAnError()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var ordinary = await rig.Jobs.AddAsync(new RemoteAddRequest { Input = "https://example.invalid/file.zip" });
        Assert.Equal(RemoteInputKind.OrdinaryLink, ordinary.Kind);
        Assert.NotNull(ordinary.Error);
        var nonsense = await rig.Jobs.AddAsync(new RemoteAddRequest { Input = "words" });
        Assert.Equal(RemoteInputKind.Unsupported, nonsense.Kind);
        Assert.Empty(rig.Jobs.List());
    }

    // ---- Scenario B: cached hoster link -------------------------------------------------------

    [Fact]
    public async Task ScenarioB_ACachedHosterLinkGoesStraightOnWithoutTheHoster()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var (link, hash, file) = rig.ScriptHoster("cached", MockTorBoxState.CachedReady);
        rig.Mock.AddCached(hash);

        var classified = await rig.Jobs.ClassifyAsync(link);
        Assert.Equal(RemoteInputKind.HosterLink, classified.Kind);
        Assert.Equal("Pretend", classified.HosterName);
        Assert.True(classified.HosterAvailable);

        var job = await rig.AddAsync(new RemoteAddRequest { Input = link, Selection = RemoteSelectionMode.All });
        Assert.Equal(RemoteJobState.Intended, job.State);
        await rig.RunAsync();

        var started = rig.View(job.Id);
        Assert.Equal(RemoteJobState.LocalStarted, started.State);
        Assert.True(started.Cached);
        var submission = Assert.Single(rig.Mock.Submissions);
        Assert.True(submission.OnlyIfCached, "Without an approval, the provider must not be able to start work.");
        Assert.Equal(link, submission.Link);
        await rig.AssertCompletedAsync(file, "cached", "cached.bin");

        var child = Assert.Single(rig.Children());
        Assert.StartsWith("remote://torbox/webdl/", child.Url);
        Assert.Null(child.FinalUrl);
        Assert.Equal(0, rig.Mock.HosterRequests);
        await rig.AssertNothingLeakedAsync(link, "invented-fragment", "/hoster/");
    }

    [Fact]
    public async Task ScenarioB_ALinkAlreadyOnTheAccountIsReusedWithoutSpendingACreation()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var link = rig.HosterLink("existing");
        var file = MockTorBoxFile.Of(rig.Mock.NextFileId(), "existing/existing.bin", 32 * Kb, 9);
        var item = rig.Mock.AddItem(RemoteItemKind.WebDownload, "existing", [file]);
        item.Hash = RemoteRig.Md5Of(link);

        var job = await rig.AddAsync(new RemoteAddRequest { Input = link, Selection = RemoteSelectionMode.All });
        await rig.RunAsync();

        Assert.Equal(RemoteJobState.LocalStarted, rig.View(job.Id).State);
        Assert.Equal(item.Id.ToString(), rig.View(job.Id).ItemId);
        Assert.Empty(rig.Mock.Submissions);
        Assert.Equal(0, rig.Mock.Calls("POST webdl/checkcached"));
        await rig.AssertCompletedAsync(file, "existing", "existing.bin");

        // No slot was used, so another link needn't wait.
        var (other, hash, _) = rig.ScriptHoster("other", MockTorBoxState.CachedReady);
        rig.Mock.AddCached(hash);
        await rig.AddAsync(new RemoteAddRequest { Input = other, Selection = RemoteSelectionMode.All });
        await rig.RunAsync();
        Assert.Single(rig.Mock.Submissions);
    }

    [Fact]
    public async Task ScenarioB_AnAlternativeHashCountsAsTheSameItem()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var link = rig.HosterLink("alternative");
        var item = rig.Mock.AddItem(RemoteItemKind.WebDownload, "alternative", [MockTorBoxFile.Of(rig.Mock.NextFileId(), "alternative/a.bin", Kb, 9)]);
        item.AlternativeHashes.Add(RemoteRig.Md5Of(link));

        var job = await rig.AddAsync(new RemoteAddRequest { Input = link, Selection = RemoteSelectionMode.All });
        await rig.RunAsync();
        Assert.Equal(item.Id.ToString(), rig.View(job.Id).ItemId);
        Assert.Empty(rig.Mock.Submissions);
    }

    // ---- Scenario C: uncached hoster link -----------------------------------------------------

    [Fact]
    public async Task ScenarioC_AnUncachedLinkNeedsApprovalThenWalksTheStatesAndStartsLocallyWhenReady()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var (link, _, file) = rig.ScriptHoster(
            "uncached",
            MockTorBoxState.Queued,
            MockTorBoxState.Downloading(0.2, eta: 40),
            MockTorBoxState.Downloading(0.8, eta: 5000),
            MockTorBoxState.Stalled,
            MockTorBoxState.Downloading(0.9, eta: 2),
            MockTorBoxState.Ready);

        var job = await rig.AddAsync(new RemoteAddRequest { Input = link, Selection = RemoteSelectionMode.All });
        await rig.RunAsync();

        // Says what it needs, and nothing has been created.
        var pending = rig.View(job.Id);
        Assert.Equal(RemoteJobState.Intended, pending.State);
        Assert.True(pending.ApprovalNeeded);
        Assert.Contains("Not cached on TorBox", pending.Status);
        Assert.Null(pending.NextCheckAt);
        Assert.Empty(rig.Mock.Submissions);
        Assert.Equal(1, rig.Mock.Calls("POST webdl/checkcached"));

        // It waits, and nothing happens by itself.
        await rig.AdvanceAsync(3600);
        Assert.Empty(rig.Mock.Submissions);
        Assert.Equal(1, rig.Mock.Calls("POST webdl/checkcached"));

        Assert.True((await rig.Jobs.ApproveUncachedAsync(job.Id)).Ok);
        await rig.RunAsync();
        var submission = Assert.Single(rig.Mock.Submissions);
        Assert.False(submission.OnlyIfCached);

        async Task ExpectAsync(int seconds, RemoteStage stage, string words)
        {
            await rig.AdvanceAsync(seconds);
            var view = rig.View(job.Id);
            Assert.Equal(RemoteJobState.Waiting, view.State);
            Assert.Equal(stage, view.Stage);
            Assert.Contains(words, view.Status);
            Assert.Empty(rig.Children());
        }

        var first = rig.View(job.Id);
        Assert.Equal(RemoteStage.Queued, first.Stage);
        Assert.Equal("Added to TorBox queue", first.Status);
        Assert.Equal(rig.Time.GetUtcNow() + TimeSpan.FromSeconds(30), first.NextCheckAt);

        await ExpectAsync(30, RemoteStage.Processing, "Processing on TorBox (20%)");
        Assert.Equal(rig.Time.GetUtcNow() + TimeSpan.FromSeconds(40), rig.View(job.Id).NextCheckAt);
        await ExpectAsync(40, RemoteStage.Processing, "Processing on TorBox (80%)");
        // A long eta doesn't push the next look past a minute.
        Assert.Equal(rig.Time.GetUtcNow() + TimeSpan.FromSeconds(60), rig.View(job.Id).NextCheckAt);
        await ExpectAsync(60, RemoteStage.Stalled, "Stalled on TorBox");
        await ExpectAsync(30, RemoteStage.Processing, "Processing on TorBox (90%)");
        // A short one doesn't pull it under 15 s.
        Assert.Equal(rig.Time.GetUtcNow() + TimeSpan.FromSeconds(15), rig.View(job.Id).NextCheckAt);

        await rig.AdvanceAsync(15);
        var started = rig.View(job.Id);
        Assert.Equal(RemoteJobState.LocalStarted, started.State);
        Assert.Null(started.NextCheckAt);
        await rig.AssertCompletedAsync(file, "uncached", "uncached.bin");

        // Finished means finished: no more calls.
        var calls = rig.Mock.TotalApiCalls;
        await rig.AdvanceAsync(3600);
        Assert.Equal(calls, rig.Mock.TotalApiCalls);
        Assert.Single(rig.Mock.Submissions);
        Assert.Equal(0, rig.Mock.HosterRequests);
        await rig.AssertNothingLeakedAsync(link, "invented-fragment");
    }

    [Fact]
    public async Task ScenarioC_ApprovingWhenAddingNeedsNoSecondStep()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var (link, _, file) = rig.ScriptHoster("approved", MockTorBoxState.Ready);
        var job = await rig.AddAsync(new RemoteAddRequest { Input = link, Selection = RemoteSelectionMode.All, ApprovedUncached = true });
        await rig.RunAsync();
        Assert.Equal(RemoteJobState.LocalStarted, rig.View(job.Id).State);
        Assert.False(Assert.Single(rig.Mock.Submissions).OnlyIfCached);
        await rig.AssertCompletedAsync(file, "approved", "approved.bin");
    }

    [Fact]
    public async Task ScenarioC_AnItemTheProviderQueuesHasNoIdUntilItLeavesTheQueue()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var link = rig.HosterLink("queued");
        var hash = RemoteRig.Md5Of(link);
        var file = MockTorBoxFile.Of(rig.Mock.NextFileId(), "queued/queued.bin", 32 * Kb, 8);
        rig.Mock.ScriptSubmission(hash, [file], [MockTorBoxState.Ready], queued: true);

        var job = await rig.AddAsync(new RemoteAddRequest { Input = link, Selection = RemoteSelectionMode.All, ApprovedUncached = true });
        await rig.RunAsync();
        var waiting = rig.View(job.Id);
        Assert.Equal(RemoteJobState.Waiting, waiting.State);
        Assert.Equal(RemoteStage.Queued, waiting.Stage);
        Assert.Null(waiting.ItemId);

        // Well past the grace period, it's still in the queue and that's fine.
        await rig.AdvanceAsync(120);
        await rig.AdvanceAsync(120);
        Assert.Equal(RemoteJobState.Waiting, rig.View(job.Id).State);
        Assert.Single(rig.Mock.Submissions);

        var item = Assert.Single(rig.Mock.Submissions.Select(_ => rig.Mock.FindItem(RemoteItemKind.WebDownload, hash)));
        item!.InQueue = false;
        await rig.AdvanceAsync(30);
        Assert.Equal(RemoteJobState.LocalStarted, rig.View(job.Id).State);
        Assert.Equal(item.Id.ToString(), rig.View(job.Id).ItemId);
        await rig.AssertCompletedAsync(file, "queued", "queued.bin");
    }

    [Fact]
    public async Task ScenarioC_AnUnknownStateStaysVisibleAndNeverCountsAsReady()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var (link, _, _) = rig.ScriptHoster("odd", new MockTorBoxState("brand new word"), new MockTorBoxState("brand new word"));
        var job = await rig.AddAsync(new RemoteAddRequest { Input = link, Selection = RemoteSelectionMode.All, ApprovedUncached = true });
        await rig.RunAsync();
        await rig.AdvanceAsync(30);

        var view = rig.View(job.Id);
        Assert.Equal(RemoteJobState.Waiting, view.State);
        Assert.Equal(RemoteStage.Unknown, view.Stage);
        Assert.Equal("brand new word", view.RawState);
        Assert.Equal("TorBox reports: brand new word", view.Status);
        Assert.Empty(rig.Children());
    }

    [Fact]
    public async Task ScenarioC_WhenNothingChangesPollingSlowsDown()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var (link, _, _) = rig.ScriptHoster("idle", MockTorBoxState.Stalled);
        var job = await rig.AddAsync(new RemoteAddRequest { Input = link, Selection = RemoteSelectionMode.All, ApprovedUncached = true });
        await rig.RunAsync();
        for (var i = 0; i < 25; i++)
        {
            var next = rig.View(job.Id).NextCheckAt!.Value;
            rig.Time.Advance(next - rig.Time.GetUtcNow());
            await rig.RunAsync();
        }

        Assert.True(rig.View(job.Id).NextCheckAt - rig.Time.GetUtcNow() >= TimeSpan.FromSeconds(120));
        Assert.Single(rig.Mock.Submissions);
    }

    // ---- Spacing ------------------------------------------------------------------------------

    [Fact]
    public async Task WebDownloadsAreSpaced65SecondsPerAccountAndAcrossARestart()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var (first, _, _) = rig.ScriptHoster("one", MockTorBoxState.Downloading());
        var (second, _, _) = rig.ScriptHoster("twoo", MockTorBoxState.Downloading());
        var (third, _, _) = rig.ScriptHoster("three", MockTorBoxState.Downloading());
        foreach (var link in new[] { first, second, third })
        {
            await rig.AddAsync(new RemoteAddRequest { Input = link, ApprovedUncached = true, Selection = RemoteSelectionMode.All });
            rig.Time.Advance(TimeSpan.FromMilliseconds(1));
        }

        await rig.RunAsync();
        Assert.Single(rig.Mock.Submissions);

        await rig.AdvanceAsync(64);
        Assert.Single(rig.Mock.Submissions);

        // The reserved slot is on disk.
        await rig.RestartAsync();
        await rig.RunAsync();
        Assert.Single(rig.Mock.Submissions);

        await rig.AdvanceAsync(1);
        Assert.Equal(2, rig.Mock.Submissions.Count);
        await rig.AdvanceAsync(64);
        Assert.Equal(2, rig.Mock.Submissions.Count);
        await rig.AdvanceAsync(1);
        Assert.Equal(3, rig.Mock.Submissions.Count);
        Assert.Equal([first, second, third], rig.Mock.Submissions.Select(submission => submission.Link));
    }

    [Fact]
    public async Task TorrentsGoOneAtATimeWithoutTheSpacing()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var hashes = Enumerable.Range(1, 3).Select(i => new string((char)('a' + i), 40)).ToList();
        foreach (var hash in hashes)
        {
            await rig.AddAsync(new RemoteAddRequest { Input = "magnet:?xt=urn:btih:" + hash, ApprovedUncached = true });
            rig.Time.Advance(TimeSpan.FromMilliseconds(1));
        }

        await rig.RunAsync();
        Assert.Equal(3, rig.Mock.Submissions.Count);
        Assert.Equal(hashes.Select(hash => "magnet:?xt=urn:btih:" + hash), rig.Mock.Submissions.Select(submission => submission.Link));
    }

    // ---- Scenario D: failures -----------------------------------------------------------------

    [Theory]
    [InlineData("UNSUPPORTED_SITE", "That site is not supported.", RemoteJobState.Failed, RemoteFailureKind.Rejected)]
    [InlineData("LINK_OFFLINE", "The file is offline.", RemoteJobState.Failed, RemoteFailureKind.Rejected)]
    [InlineData("MONTHLY_LIMIT", "Your monthly limit is used up.", RemoteJobState.Failed, RemoteFailureKind.AccountLimit)]
    [InlineData("PLAN_RESTRICTED_FEATURE", "Your plan does not allow this.", RemoteJobState.Failed, RemoteFailureKind.Rejected)]
    [InlineData("DOWNLOAD_TOO_LARGE", "The file is too large for your plan.", RemoteJobState.Failed, RemoteFailureKind.Rejected)]
    [InlineData("ACTIVE_LIMIT", "Too many active downloads.", RemoteJobState.Blocked, RemoteFailureKind.AccountBusy)]
    [InlineData("COOLDOWN_LIMIT", "Your account is cooling down.", RemoteJobState.Blocked, RemoteFailureKind.AccountBusy)]
    [InlineData("BAD_TOKEN", "The key is bad.", RemoteJobState.Failed, RemoteFailureKind.Authentication)]
    public async Task ScenarioD_EachRefusalShowsTheRealCauseAndGoesNowhereNearTheHoster(string code, string detail, RemoteJobState state, RemoteFailureKind kind)
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var (link, _, _) = rig.ScriptHoster("refused", MockTorBoxState.Ready);
        rig.Mock.Fail(CreateWeb, MockFault.Error(code, detail));

        var job = await rig.AddAsync(new RemoteAddRequest { Input = link, Selection = RemoteSelectionMode.All, ApprovedUncached = true });
        await rig.RunAsync();

        var view = rig.View(job.Id);
        Assert.Equal(state, view.State);
        Assert.Equal(kind, view.FailureKind);
        Assert.Contains(detail, view.FailureMessage);
        Assert.Contains(detail, view.Status);
        Assert.Empty(rig.Children());
        Assert.Equal(0, rig.Mock.HosterRequests);
        Assert.Equal(1, rig.Mock.Calls(CreateWeb));
        Assert.Empty(rig.Ctx.Service.GetSnapshot().Jobs);
        await rig.AssertNothingLeakedAsync(link, "invented-fragment");
    }

    /// <summary>The real service's answers to a dead link and to a site it has turned off. Each is sent once, and stops with the service's own words.</summary>
    public static TheoryData<string, string> RealRefusals => new()
    {
        { nameof(MockFault.DeadWebLink), "error scanning this link" },
        { nameof(MockFault.InvalidHosterLink), "not a valid link" },
        { nameof(MockFault.SiteTurnedOff), "temporarily disabled" },
        { nameof(MockFault.SiteNotSupported), "not supported" },
        { nameof(MockFault.NotALink), "link you provided is invalid" },
    };

    [Theory]
    [MemberData(nameof(RealRefusals))]
    public async Task ScenarioD_ARealRefusalStopsAtOnceWithTheProvidersWordsAndIsNotSentAgain(string which, string words)
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var (link, _, _) = rig.ScriptHoster("refused-real", MockTorBoxState.Ready);
        rig.Mock.RefuseLink(link, (MockFault)typeof(MockFault).GetProperty(which)!.GetValue(null)!);

        var job = await rig.AddAsync(new RemoteAddRequest { Input = link, Selection = RemoteSelectionMode.All, ApprovedUncached = true });
        await rig.RunAsync();

        var view = rig.View(job.Id);
        Assert.Equal(RemoteJobState.Failed, view.State);
        Assert.Equal(RemoteFailureKind.Rejected, view.FailureKind);
        Assert.Contains(words, view.Status);

        for (var i = 0; i < 6; i++)
        {
            await rig.AdvanceAsync(65);
        }

        Assert.Equal(RemoteJobState.Failed, rig.View(job.Id).State);
        Assert.Equal(1, rig.Mock.Calls(CreateWeb));
        Assert.Empty(rig.Children());
        Assert.Equal(0, rig.Mock.HosterRequests);
        Assert.Empty(rig.Ctx.Service.GetSnapshot().Jobs);
        await rig.AssertNothingLeakedAsync(link, "invented-fragment");
    }

    [Fact]
    public async Task ScenarioD_ARetryAfterARefusalAsksAgainAndCanSucceed()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var (link, _, _) = rig.ScriptHoster("back-online", MockTorBoxState.Ready);
        rig.Mock.RefuseLink(link, MockFault.DeadWebLink);
        var job = await rig.AddAsync(new RemoteAddRequest { Input = link, Selection = RemoteSelectionMode.All, ApprovedUncached = true });
        await rig.RunAsync();
        Assert.Equal(RemoteJobState.Failed, rig.View(job.Id).State);

        rig.Mock.AcceptLink(link);
        await rig.Jobs.RetryAsync(job.Id);
        await rig.AdvanceAsync(65);

        Assert.Equal(RemoteJobState.LocalStarted, rig.View(job.Id).State);
        Assert.Equal(2, rig.Mock.Calls(CreateWeb));
    }

    [Fact]
    public async Task ScenarioD_ARefusalThatQuotesTheLinkDoesNotPutItAnywhere()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var (link, _, _) = rig.ScriptHoster("quoted", MockTorBoxState.Ready);
        rig.Mock.Fail(CreateWeb, MockFault.Error("LINK_OFFLINE", $"Could not fetch {link} for you"));
        var job = await rig.AddAsync(new RemoteAddRequest { Input = link, ApprovedUncached = true });
        await rig.RunAsync();

        Assert.Equal("TorBox refused this: Could not fetch [link] for you", rig.View(job.Id).FailureMessage);
        await rig.AssertNothingLeakedAsync(link, "invented-fragment");
    }

    [Fact]
    public async Task ScenarioD_AHosterThatIsDownStopsBeforeAnythingIsSentAndNamesTheReason()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var link = rig.Mock.HosterUrl("x") .Replace("/hoster/", "/hoster-down/", StringComparison.Ordinal);
        var classified = await rig.Jobs.ClassifyAsync(link);
        Assert.Equal(RemoteInputKind.HosterLink, classified.Kind);
        Assert.False(classified.HosterAvailable);

        var job = await rig.AddAsync(new RemoteAddRequest { Input = link, ApprovedUncached = true, Selection = RemoteSelectionMode.All });
        await rig.RunAsync();

        var failed = rig.View(job.Id);
        Assert.Equal(RemoteJobState.Failed, failed.State);
        Assert.Contains("Pretend Down is unavailable", failed.FailureMessage);
        Assert.Contains("Down for maintenance", failed.FailureMessage);
        Assert.Empty(rig.Mock.Submissions);
        Assert.Equal(0, rig.Mock.HosterRequests);
        Assert.Empty(rig.Ctx.Service.GetSnapshot().Jobs);
    }

    [Fact]
    public async Task ScenarioD_AHosterThatIsDownDoesNotMatterIfTheContentIsCached()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var link = rig.Mock.HosterUrl("x").Replace("/hoster/", "/hoster-down/", StringComparison.Ordinal);
        var hash = RemoteRig.Md5Of(link);
        rig.Mock.AddCached(hash);
        var file = MockTorBoxFile.Of(rig.Mock.NextFileId(), "x/x.bin", 32 * Kb, 5);
        rig.Mock.ScriptSubmission(hash, [file], [MockTorBoxState.CachedReady]);

        var job = await rig.AddAsync(new RemoteAddRequest { Input = link, Selection = RemoteSelectionMode.All });
        await rig.RunAsync();
        Assert.Equal(RemoteJobState.LocalStarted, rig.View(job.Id).State);
        await rig.AssertCompletedAsync(file, "x", "x.bin");
    }

    [Fact]
    public async Task ScenarioD_AnItemThatFailsOnTheProviderShowsItsReasonAndIsNeverSentAgain()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var (link, _, _) = rig.ScriptHoster("broken", MockTorBoxState.Queued, MockTorBoxState.Failed("Hoster is down"));
        var job = await rig.AddAsync(new RemoteAddRequest { Input = link, ApprovedUncached = true, Selection = RemoteSelectionMode.All });
        await rig.RunAsync();
        await rig.AdvanceAsync(30);

        var failed = rig.View(job.Id);
        Assert.Equal(RemoteJobState.Failed, failed.State);
        Assert.Contains("Hoster is down", failed.FailureMessage);
        Assert.Null(failed.NextCheckAt);

        var calls = rig.Mock.TotalApiCalls;
        await rig.AdvanceAsync(3600);
        Assert.Equal(calls, rig.Mock.TotalApiCalls);

        // The user can look again, which asks the provider and doesn't submit anything.
        Assert.True((await rig.Jobs.RetryAsync(job.Id)).Ok);
        await rig.RunAsync();
        Assert.Equal(RemoteJobState.Failed, rig.View(job.Id).State);
        Assert.Single(rig.Mock.Submissions);
        Assert.Equal(0, rig.Mock.HosterRequests);
        Assert.Empty(rig.Children());
    }

    [Fact]
    public async Task ScenarioD_AQuotaErrorStopsAllCreationUntilTheUserRetries()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var (first, _, _) = rig.ScriptHoster("first", MockTorBoxState.Ready);
        var (second, _, _) = rig.ScriptHoster("second", MockTorBoxState.Ready);
        rig.Mock.Fail(CreateWeb, MockFault.Error("MONTHLY_LIMIT", "Your monthly limit is used up."), times: 1);
        var one = await rig.AddAsync(new RemoteAddRequest { Input = first, ApprovedUncached = true, Selection = RemoteSelectionMode.All });
        rig.Time.Advance(TimeSpan.FromMilliseconds(1));
        var two = await rig.AddAsync(new RemoteAddRequest { Input = second, ApprovedUncached = true, Selection = RemoteSelectionMode.All });
        await rig.RunAsync();
        await rig.AdvanceAsync(65);

        Assert.Equal(RemoteJobState.Failed, rig.View(one.Id).State);
        Assert.Equal(RemoteJobState.Failed, rig.View(two.Id).State);
        Assert.Equal(RemoteFailureKind.AccountLimit, rig.View(two.Id).FailureKind);
        Assert.Contains("monthly limit", rig.View(two.Id).FailureMessage);
        Assert.Equal(1, rig.Mock.Calls(CreateWeb));

        // The stop outlasts a restart.
        await rig.RestartAsync();
        var (third, _, _) = rig.ScriptHoster("third", MockTorBoxState.Ready);
        var three = await rig.AddAsync(new RemoteAddRequest { Input = third, ApprovedUncached = true, Selection = RemoteSelectionMode.All });
        await rig.AdvanceAsync(65);
        Assert.Equal(RemoteJobState.Failed, rig.View(three.Id).State);
        Assert.Equal(1, rig.Mock.Calls(CreateWeb));

        // Retrying one lifts it, and that one goes through.
        Assert.True((await rig.Jobs.RetryAsync(one.Id)).Ok);
        await rig.AdvanceAsync(65);
        Assert.Equal(2, rig.Mock.Calls(CreateWeb));
        Assert.Equal(RemoteJobState.LocalStarted, rig.View(one.Id).State);
        Assert.Equal(RemoteJobState.Failed, rig.View(two.Id).State);
    }

    [Fact]
    public async Task ScenarioD_ABusyAccountRetriesByItselfWithGrowingWaits()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var (link, _, file) = rig.ScriptHoster("busy", MockTorBoxState.Ready);
        rig.Mock.Fail(CreateWeb, MockFault.Error("ACTIVE_LIMIT", "Too many active downloads."), times: 2);
        var job = await rig.AddAsync(new RemoteAddRequest { Input = link, ApprovedUncached = true, Selection = RemoteSelectionMode.All });
        await rig.RunAsync();

        var first = rig.View(job.Id);
        Assert.Equal(RemoteJobState.Blocked, first.State);
        Assert.Contains("tried again by itself", first.FailureMessage);
        Assert.Equal(rig.Time.GetUtcNow() + TimeSpan.FromSeconds(30), first.NextCheckAt);

        // The slot it used still counts, so the retry is at 65 s.
        await rig.AdvanceAsync(30);
        Assert.Equal(1, rig.Mock.Calls(CreateWeb));
        await rig.AdvanceAsync(35);
        Assert.Equal(2, rig.Mock.Calls(CreateWeb));
        Assert.Equal(RemoteJobState.Blocked, rig.View(job.Id).State);
        Assert.Equal(rig.Time.GetUtcNow() + TimeSpan.FromSeconds(60), rig.View(job.Id).NextCheckAt);

        await rig.AdvanceAsync(65);
        Assert.Equal(3, rig.Mock.Calls(CreateWeb));
        Assert.Equal(RemoteJobState.LocalStarted, rig.View(job.Id).State);
        await rig.AssertCompletedAsync(file, "busy", "busy.bin");
    }

    [Fact]
    public async Task ScenarioD_ALinkPasswordIsAskedForAndKeptProtected()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        const string password = "invented-link-password";
        var (link, _, file) = rig.ScriptHoster("locked", MockTorBoxState.Ready);
        rig.Mock.RequireLinkPassword(link, password);

        var job = await rig.AddAsync(new RemoteAddRequest { Input = link, ApprovedUncached = true, Selection = RemoteSelectionMode.All });
        await rig.RunAsync();

        var asking = rig.View(job.Id);
        Assert.Equal(RemoteJobState.Failed, asking.State);
        Assert.Equal(RemoteFailureKind.LinkPasswordRequired, asking.FailureKind);
        Assert.Contains("needs a password", asking.FailureMessage);
        Assert.False(asking.HasLinkPassword);

        // Wrong, and it doesn't stay.
        Assert.True((await rig.Jobs.RetryAsync(job.Id, "not-it")).Ok);
        Assert.True(rig.View(job.Id).HasLinkPassword);
        await rig.AdvanceAsync(65);
        Assert.Equal(RemoteFailureKind.LinkPasswordRequired, rig.View(job.Id).FailureKind);
        Assert.Contains("wasn't accepted", rig.View(job.Id).FailureMessage);
        Assert.False(rig.View(job.Id).HasLinkPassword);

        // Right.
        Assert.True((await rig.Jobs.RetryAsync(job.Id, password)).Ok);
        await AssertSecretNotOnDiskAsync(rig.Ctx, password);
        await rig.AdvanceAsync(65);
        Assert.Equal(RemoteJobState.LocalStarted, rig.View(job.Id).State);
        Assert.Equal(password, rig.Mock.Submissions[^1].Password);
        await rig.AssertCompletedAsync(file, "locked", "locked.bin");
        await rig.AssertNothingLeakedAsync(password, "not-it", link, "invented-fragment");
    }

    [Fact]
    public async Task ScenarioD_NoKeyStopsTheAttemptWithThatReason()
    {
        await using var rig = await RemoteRig.CreateAsync(_server, key: null);
        var link = rig.HosterLink("nokey");
        Assert.Equal(RemoteInputKind.OrdinaryLink, (await rig.Jobs.ClassifyAsync(link)).Kind);

        var refused = await rig.Jobs.AddAsync(new RemoteAddRequest { Input = "magnet:?xt=urn:btih:" + new string('a', 40) });
        Assert.Contains("No TorBox API key", refused.Error);
        Assert.Empty(rig.Jobs.List());
        Assert.Equal(0, rig.Mock.TotalApiCalls);
    }

    [Fact]
    public async Task ScenarioD_AnOutageIsWaitedOutWithBackoffAndNeverSubmitsAnything()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var (link, _, file) = rig.ScriptHoster("outage", MockTorBoxState.Ready);
        var job = await rig.AddAsync(new RemoteAddRequest { Input = link, ApprovedUncached = true, Selection = RemoteSelectionMode.All });

        // Answers that aren't answers: 500, 503, a dropped connection, a proxy's page.
        foreach (var fault in new[] { MockFault.Status5xx(), MockFault.Status5xx(503, null, "Unavailable"), MockFault.Abort, MockFault.NotJson() })
        {
            rig.Mock.Fail("*", fault);
            var next = rig.View(job.Id).NextCheckAt!.Value;
            rig.Time.Advance(next - rig.Time.GetUtcNow());
            await rig.RunAsync();
            var view = rig.View(job.Id);
            Assert.Equal(RemoteJobState.Intended, view.State);
            Assert.NotNull(view.FailureMessage);
            Assert.Empty(rig.Mock.Submissions);
            Assert.Equal(0, rig.Mock.Calls(CreateWeb));
        }

        // A growing wait: 30, 60, 120, 240 s.
        Assert.Equal(rig.Time.GetUtcNow() + TimeSpan.FromSeconds(240), rig.View(job.Id).NextCheckAt);

        rig.Mock.ClearFaults();
        await rig.AdvanceAsync(240);
        Assert.Equal(RemoteJobState.LocalStarted, rig.View(job.Id).State);
        Assert.Null(rig.View(job.Id).FailureMessage);
        Assert.Single(rig.Mock.Submissions);
        await rig.AssertCompletedAsync(file, "outage", "outage.bin");
    }

    [Fact]
    public async Task ScenarioD_FailedStatusChecksNeverResubmit()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var (link, _, _) = rig.ScriptHoster("polling", MockTorBoxState.Downloading(), MockTorBoxState.Downloading(), MockTorBoxState.Downloading(), MockTorBoxState.Ready);
        var job = await rig.AddAsync(new RemoteAddRequest { Input = link, ApprovedUncached = true, Selection = RemoteSelectionMode.All });
        await rig.RunAsync();
        Assert.Single(rig.Mock.Submissions);

        rig.Mock.Fail("GET webdl/mylist", MockFault.Status5xx());
        for (var i = 0; i < 8; i++)
        {
            await rig.AdvanceAsync(1000);
            Assert.Equal(RemoteJobState.Waiting, rig.View(job.Id).State);
        }

        Assert.Equal(rig.Time.GetUtcNow() + RemoteJobRules.LongestBackoff, rig.View(job.Id).NextCheckAt);
        Assert.Contains("having problems", rig.View(job.Id).Status);

        rig.Mock.ClearFaults();
        await rig.AdvanceAsync(1000);
        Assert.Equal(1, rig.Mock.Calls(CreateWeb));
        Assert.Null(rig.View(job.Id).FailureMessage);
    }

    [Fact]
    public async Task RetryAfterIsBelievedOnA429AndHoldsTheWholeAccountEvenAgainstCheckNow()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var (link, _, _) = rig.ScriptHoster("slow", MockTorBoxState.Downloading(), MockTorBoxState.Downloading());
        var job = await rig.AddAsync(new RemoteAddRequest { Input = link, ApprovedUncached = true });
        await rig.RunAsync();

        // A 200 carries a Retry-After too, and it counted for nothing: the next look is at the eta.
        Assert.Equal(rig.Time.GetUtcNow() + TimeSpan.FromSeconds(20), rig.View(job.Id).NextCheckAt);

        rig.Mock.Fail("GET webdl/mylist", MockFault.TooManyRequests(600), times: 1);
        await rig.AdvanceAsync(20);
        Assert.Equal(rig.Time.GetUtcNow() + TimeSpan.FromSeconds(600), rig.View(job.Id).NextCheckAt);

        var calls = rig.Mock.TotalApiCalls;
        await rig.Jobs.CheckNowAsync(job.Id);
        await rig.AdvanceAsync(599);
        Assert.Equal(calls, rig.Mock.TotalApiCalls);

        await rig.AdvanceAsync(1);
        Assert.True(rig.Mock.TotalApiCalls > calls);
    }

    [Fact]
    public async Task ARateLimitAnswerToASubmissionLeavesItToTryAgain()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var (link, _, _) = rig.ScriptHoster("limited", MockTorBoxState.Ready);
        rig.Mock.Fail(CreateWeb, MockFault.TooManyRequests(70), times: 1);
        var job = await rig.AddAsync(new RemoteAddRequest { Input = link, ApprovedUncached = true, Selection = RemoteSelectionMode.All });
        await rig.RunAsync();

        var waiting = rig.View(job.Id);
        Assert.Equal(RemoteJobState.Intended, waiting.State);
        Assert.Equal(RemoteFailureKind.RateLimited, waiting.FailureKind);
        Assert.Equal(1, rig.Mock.Calls(CreateWeb));

        await rig.AdvanceAsync(70);
        Assert.Equal(2, rig.Mock.Calls(CreateWeb));
        Assert.Equal(RemoteJobState.LocalStarted, rig.View(job.Id).State);
    }

    // ---- Scenario E: magnet -------------------------------------------------------------------

    [Fact]
    public async Task ScenarioE_AMagnetIsCheckedApprovedWaitedForChosenFromAndDownloaded()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var hash = new string('c', 40);
        var magnet = $"magnet:?xt=urn:btih:{hash}&dn=Pretty%20Name&tr=http%3A%2F%2Ftracker.invalid%2Fsecret-passkey";
        var wanted = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Pretty Name/sub/wanted.bin", 96 * Kb, 31);
        var other = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Pretty Name/other.bin", 32 * Kb, 32);
        rig.Mock.ScriptSubmission(hash, [wanted, other], [
            MockTorBoxState.MetaData,
            MockTorBoxState.MetaData,
            MockTorBoxState.Downloading(0.5),
            MockTorBoxState.Ready,
        ]);

        var classified = await rig.Jobs.ClassifyAsync(magnet);
        Assert.Equal(RemoteInputKind.Magnet, classified.Kind);
        Assert.Equal(hash, classified.Hash);

        // Told to wait for a choice, as "Let me choose files when TorBox is done" does. Left alone, it would take all.
        var job = await rig.AddAsync(new RemoteAddRequest { Input = magnet, Selection = RemoteSelectionMode.AskWhenReady });
        Assert.Equal("Pretty Name", job.Name);
        await rig.RunAsync();
        Assert.True(rig.View(job.Id).ApprovalNeeded);
        Assert.Empty(rig.Mock.Submissions);
        Assert.Equal(1, rig.Mock.Calls("POST torrents/checkcached"));

        // An approval of an item that has gone past that stage is refused plainly further down; this one applies.
        Assert.True((await rig.Jobs.ApproveUncachedAsync(job.Id)).Ok);
        await rig.RunAsync();
        Assert.Equal(magnet, Assert.Single(rig.Mock.Submissions).Link);

        // Metadata pending: there are no files to show, and none are made up.
        var view = rig.View(job.Id);
        Assert.Equal(RemoteStage.MetadataPending, view.Stage);
        Assert.Contains("list the files", view.Status);
        var early = await rig.Jobs.GetFilesAsync(job.Id);
        Assert.True(early.Pending);
        Assert.Empty(early.Files);

        await rig.AdvanceAsync(30);
        Assert.Equal(RemoteJobState.Waiting, rig.View(job.Id).State);
        Assert.Contains("Processing on TorBox", rig.View(job.Id).Status);
        await rig.AdvanceAsync(30);
        Assert.Equal(RemoteJobState.AwaitingSelection, rig.View(job.Id).State);
        Assert.Empty(rig.Children());

        var files = await rig.Jobs.GetFilesAsync(job.Id);
        Assert.False(files.Pending);
        Assert.Equal(2, files.Files.Count);
        Assert.True((await rig.Jobs.ChooseFilesAsync(job.Id, all: false, [wanted.Id.ToString()])).Ok);
        await rig.RunAsync();

        Assert.Equal(RemoteJobState.LocalStarted, rig.View(job.Id).State);
        var child = Assert.Single(rig.Children());
        Assert.Equal(Path.Combine(rig.Destination, "Pretty Name", "sub", "wanted.bin"), child.FinalFilePath);
        await rig.AssertCompletedAsync(wanted, "Pretty Name", "sub", "wanted.bin");
        Assert.False(File.Exists(Path.Combine(rig.Destination, "Pretty Name", "other.bin")));

        // More of the same item later: the first one isn't repeated.
        Assert.True((await rig.Jobs.ChooseFilesAsync(job.Id, all: true, null)).Ok);
        await rig.RunAsync();
        Assert.Equal(2, rig.Children().Count);
        await rig.AssertCompletedAsync(other, "Pretty Name", "other.bin");
        Assert.Single(rig.Mock.Submissions);
        await rig.AssertNothingLeakedAsync(magnet, "secret-passkey", "tracker.invalid");
    }

    [Fact]
    public async Task ScenarioE_ACachedMagnetNeedsNoApproval()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var hash = new string('d', 40);
        rig.Mock.AddCached(hash);
        var file = MockTorBoxFile.Of(rig.Mock.NextFileId(), "cached/a.bin", 32 * Kb, 33);
        rig.Mock.ScriptSubmission(hash, [file], [MockTorBoxState.CachedReady]);

        var job = await rig.AddAsync(new RemoteAddRequest { Input = "magnet:?xt=urn:btih:" + hash, Selection = RemoteSelectionMode.All });
        await rig.RunAsync();
        Assert.True(Assert.Single(rig.Mock.Submissions).OnlyIfCached);
        Assert.Equal(RemoteJobState.LocalStarted, rig.View(job.Id).State);
        await rig.AssertCompletedAsync(file, "cached", "a.bin");
    }

    [Fact]
    public async Task ScenarioE_TheSameMagnetTwiceIsOneJob()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var magnet = "magnet:?xt=urn:btih:" + new string('e', 40);
        var first = await rig.AddAsync(new RemoteAddRequest { Input = magnet });
        var again = await rig.Jobs.AddAsync(new RemoteAddRequest { Input = magnet.ToUpperInvariant().Replace("MAGNET:?XT=URN:BTIH:", "magnet:?xt=urn:btih:", StringComparison.Ordinal), Destination = rig.Destination });
        Assert.True(again.Existing);
        Assert.Equal(first.Id, again.Job!.Id);
    }

    [Fact]
    public async Task ScenarioE_AChoiceIsRequiredBeforeAnyLocalJob()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var item = rig.Mock.AddItem(RemoteItemKind.Torrent, "Pack",
            [MockTorBoxFile.Of(rig.Mock.NextFileId(), "Pack/a.bin", Kb, 1), MockTorBoxFile.Of(rig.Mock.NextFileId(), "Pack/b.bin", Kb, 2)]);
        var job = await rig.AddAsync(new RemoteAddRequest { Input = $"https://torbox.app/download?id={item.Id}&type=torrents" });
        await rig.RunAsync();

        var empty = await rig.Jobs.ChooseFilesAsync(job.Id, all: false, []);
        Assert.False(empty.Ok);
        Assert.Contains("at least one", empty.Error);
        await rig.AdvanceAsync(3600);
        Assert.Equal(RemoteJobState.AwaitingSelection, rig.View(job.Id).State);
        Assert.Empty(rig.Children());
    }

    // ---- Only a real choice is asked for ------------------------------------------------------

    public enum AskedInput
    {
        Magnet,
        TorrentFile,
        FolderLink,
    }

    /// <summary>
    /// An item that asks for a choice when it is ready, holding these files. An uncached magnet or .torrent asks only
    /// when told to, as with the add box's "Let me choose files when TorBox is done"; a folder link always may.
    /// </summary>
    private static async Task<RemoteJobView> AddAskingAsync(RemoteRig rig, AskedInput input, string name, MockTorBoxFile[] files)
    {
        switch (input)
        {
            case AskedInput.Magnet:
                var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(name))).ToLowerInvariant();
                rig.Mock.ScriptSubmission(hash, files, [MockTorBoxState.Ready]);
                return await rig.AddAsync(new RemoteAddRequest { Input = "magnet:?xt=urn:btih:" + hash, ApprovedUncached = true, Selection = RemoteSelectionMode.AskWhenReady });
            case AskedInput.TorrentFile:
                var (bytes, infoHash) = Torrent(name);
                rig.Mock.ScriptTorrentUpload(bytes, infoHash);
                rig.Mock.ScriptSubmission(infoHash, files, [MockTorBoxState.Ready]);
                return await rig.AddAsync(new RemoteAddRequest { TorrentFile = bytes, ApprovedUncached = true, Selection = RemoteSelectionMode.AskWhenReady });
            default:
                var item = rig.Mock.AddItem(RemoteItemKind.Torrent, name, files);
                return await rig.AddAsync(new RemoteAddRequest { Input = $"https://torbox.app/download?id={item.Id}&type=torrents" });
        }
    }

    [Theory]
    [InlineData(AskedInput.Magnet)]
    [InlineData(AskedInput.TorrentFile)]
    [InlineData(AskedInput.FolderLink)]
    public async Task OneFileThatCanBeDownloadedStartsWithoutAskingEvenBesideOnesThatCant(AskedInput input)
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var wanted = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Solo/solo.bin", 32 * Kb, 51);
        MockTorBoxFile[] files =
        [
            wanted,
            MockTorBoxFile.Of(rig.Mock.NextFileId(), "Solo/infected.bin", 8 * Kb, 52, infected: true),
            MockTorBoxFile.Of(rig.Mock.NextFileId(), "Solo/empty.txt", 0),
            MockTorBoxFile.Of(rig.Mock.NextFileId(), "../outside.bin", 8 * Kb, 53),
        ];

        var job = await AddAskingAsync(rig, input, "Solo", files);
        Assert.Equal(RemoteSelectionMode.AskWhenReady, rig.View(job.Id).SelectionMode);
        await rig.RunAsync();

        var started = rig.View(job.Id);
        Assert.Equal(RemoteJobState.LocalStarted, started.State);
        Assert.Single(rig.Children());
        await rig.AssertCompletedAsync(wanted, "Solo", "solo.bin");
        Assert.Contains(started.Notes, note => note.Contains("infected", StringComparison.Ordinal));
        Assert.Contains(started.Notes, note => note.Contains("empty", StringComparison.Ordinal));
        Assert.Contains(started.Notes, note => note.Contains("isn't safe", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(AskedInput.Magnet)]
    [InlineData(AskedInput.TorrentFile)]
    [InlineData(AskedInput.FolderLink)]
    public async Task TwoFilesThatCanBeDownloadedWaitForTheChoice(AskedInput input)
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        MockTorBoxFile[] files =
        [
            MockTorBoxFile.Of(rig.Mock.NextFileId(), "Duo/a.bin", 8 * Kb, 54),
            MockTorBoxFile.Of(rig.Mock.NextFileId(), "Duo/b.bin", 8 * Kb, 55),
        ];

        var job = await AddAskingAsync(rig, input, "Duo", files);
        await rig.RunAsync();

        Assert.Equal(RemoteJobState.AwaitingSelection, rig.View(job.Id).State);
        Assert.Empty(rig.Children());
    }

    [Theory]
    [InlineData(AskedInput.Magnet)]
    [InlineData(AskedInput.TorrentFile)]
    [InlineData(AskedInput.FolderLink)]
    public async Task NoFileThatCanBeDownloadedEndsInWordsThatDontTalkOfAChoice(AskedInput input)
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        MockTorBoxFile[] files =
        [
            MockTorBoxFile.Of(rig.Mock.NextFileId(), "Bad/infected.bin", 8 * Kb, 56, infected: true),
            MockTorBoxFile.Of(rig.Mock.NextFileId(), "Bad/empty.txt", 0),
        ];

        var job = await AddAskingAsync(rig, input, "Bad", files);
        await rig.RunAsync();

        var failed = rig.View(job.Id);
        Assert.Equal(RemoteJobState.Failed, failed.State);
        Assert.Equal("None of its files can be downloaded.", failed.FailureMessage);
        Assert.Equal(2, failed.Notes.Count);
        Assert.Empty(rig.Children());
    }

    [Fact]
    public async Task AChoiceThatEndsUpEmptyStillSaysChosen()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var infected = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Mix/infected.bin", 8 * Kb, 57, infected: true);
        var item = rig.Mock.AddItem(RemoteItemKind.Torrent, "Mix", [infected, MockTorBoxFile.Of(rig.Mock.NextFileId(), "Mix/fine.bin", 8 * Kb, 58)]);
        var job = await rig.AddAsync(new RemoteAddRequest
        {
            Input = $"https://torbox.app/download?id={item.Id}&type=torrents",
            Selection = RemoteSelectionMode.Chosen,
            FileIds = [infected.Id.ToString()],
        });
        await rig.RunAsync();

        Assert.Equal("None of the chosen files can be downloaded.", rig.View(job.Id).FailureMessage);
    }

    /// <summary>
    /// TB.F14: adding again what had been downloaded, after its downloads were taken off the list, answered
    /// with the old job, said "Local downloads created" and put nothing on the list.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddingAgainWhatLeftTheListStartsItAgain(bool magnet)
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var file = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Again/again.bin", 16 * Kb, 61);
        RemoteAddRequest request;
        if (magnet)
        {
            var hash = new string('7', 40);
            rig.Mock.AddCached(hash);
            rig.Mock.ScriptSubmission(hash, [file], [MockTorBoxState.CachedReady]);
            request = new RemoteAddRequest { Input = "magnet:?xt=urn:btih:" + hash, Destination = rig.Destination };
        }
        else
        {
            var item = rig.Mock.AddItem(RemoteItemKind.Torrent, "Again", [file]);
            request = new RemoteAddRequest { Input = $"https://torbox.app/download?id={item.Id}&type=torrents", Destination = rig.Destination };
        }

        var first = (await rig.Jobs.AddAsync(request)).Job!;
        await rig.RunAsync();
        Assert.Equal(RemoteJobState.LocalStarted, rig.View(first.Id).State);
        await rig.AssertCompletedAsync(file, "Again", "again.bin");

        // While a download it made is on the list, it is the same job.
        var same = await rig.Jobs.AddAsync(request);
        Assert.True(same.Existing);
        Assert.Equal(first.Id, same.Job!.Id);

        var child = Assert.Single(rig.Children());
        Assert.True((await rig.Ctx.Service.RemoveAsync(child.Id, deleteFile: true)).Ok);
        Assert.Equal("Its downloads are no longer on the list", rig.View(first.Id).Status);

        var again = await rig.Jobs.AddAsync(request);
        Assert.False(again.Existing);
        Assert.NotEqual(first.Id, again.Job!.Id);
        Assert.Null(rig.Jobs.Find(first.Id));
        await rig.RunAsync();

        Assert.Equal(RemoteJobState.LocalStarted, rig.View(again.Job.Id).State);
        Assert.Single(rig.Children());
        await rig.AssertCompletedAsync(file, "Again", "again.bin");

        // And it stays gone after a restart.
        await rig.RestartAsync();
        Assert.Equal([again.Job.Id], rig.Jobs.List().Select(job => job.Id));
    }

    [Fact]
    public async Task AJobLeftWaitingForAChoiceOfOneFileGoesOnWhenLookedAtAgain()
    {
        // Saved by an earlier version, which asked even for one file. Check Now is how such a row moves.
        await using var rig = await RemoteRig.CreateAsync(_server);
        var file = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Old/one.bin", 8 * Kb, 59);
        var item = rig.Mock.AddItem(RemoteItemKind.Torrent, "Old", [file, MockTorBoxFile.Of(rig.Mock.NextFileId(), "Old/two.bin", 8 * Kb, 60)]);
        var job = await rig.AddAsync(new RemoteAddRequest { Input = $"https://torbox.app/download?id={item.Id}&type=torrents" });
        await rig.RunAsync();
        Assert.Equal(RemoteJobState.AwaitingSelection, rig.View(job.Id).State);

        item.Files.RemoveAt(1);
        await rig.RestartAsync();
        Assert.Equal(RemoteJobState.AwaitingSelection, rig.View(job.Id).State);
        Assert.True((await rig.Jobs.CheckNowAsync(job.Id)).Ok);
        await rig.RunAsync();

        Assert.Equal(RemoteJobState.LocalStarted, rig.View(job.Id).State);
        await rig.AssertCompletedAsync(file, "Old", "one.bin");
    }

    // ---- .torrent files -----------------------------------------------------------------------

    private static (byte[] File, string InfoHash) Torrent(string name = "from-file", string secretTracker = "http://tracker.invalid/secret-passkey/announce")
    {
        var info = $"d6:lengthi1234e4:name{name.Length}:{name}12:piece lengthi16384e6:pieces20:aaaaaaaaaaaaaaaaaaaae";
        var file = Encoding.Latin1.GetBytes($"d8:announce{secretTracker.Length}:{secretTracker}4:info{info}e");
        return (file, Convert.ToHexString(SHA1.HashData(Encoding.Latin1.GetBytes(info))).ToLowerInvariant());
    }

    [Fact]
    public async Task ATorrentFileIsFoundByItsRawInfoHashAndUploadedOnlyWhenAbsent()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var (bytes, infoHash) = Torrent();
        rig.Mock.ScriptTorrentUpload(bytes, infoHash);
        var file = MockTorBoxFile.Of(rig.Mock.NextFileId(), "from-file/data.bin", 32 * Kb, 41);
        rig.Mock.ScriptSubmission(infoHash, [file], [MockTorBoxState.Ready]);

        var job = await rig.AddAsync(new RemoteAddRequest { TorrentFile = bytes, ApprovedUncached = true, Selection = RemoteSelectionMode.All });
        Assert.Equal("from-file", job.Name);
        await rig.RunAsync();

        Assert.Equal(bytes.Length, Assert.Single(rig.Mock.Submissions).FileBytes);
        Assert.Equal(RemoteJobState.LocalStarted, rig.View(job.Id).State);
        await rig.AssertCompletedAsync(file, "from-file", "data.bin");
        await rig.AssertNothingLeakedAsync("secret-passkey", "tracker.invalid");
    }

    [Fact]
    public async Task ATorrentFileAlreadyOnTheAccountIsNotUploadedAgain()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var (bytes, infoHash) = Torrent();
        var item = rig.Mock.AddItem(RemoteItemKind.Torrent, "from-file", [MockTorBoxFile.Of(rig.Mock.NextFileId(), "from-file/a.bin", Kb, 2)]);
        item.Hash = infoHash;

        var job = await rig.AddAsync(new RemoteAddRequest { TorrentFile = bytes, Selection = RemoteSelectionMode.All });
        await rig.RunAsync();
        Assert.Equal(item.Id.ToString(), rig.View(job.Id).ItemId);
        Assert.Empty(rig.Mock.Submissions);
    }

    [Fact]
    public async Task ATorrentFileByPathIsRead()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var (bytes, infoHash) = Torrent();
        var path = Path.Combine(rig.Destination, "picked.torrent");
        await File.WriteAllBytesAsync(path, bytes);
        rig.Mock.ScriptTorrentUpload(bytes, infoHash);

        var job = await rig.AddAsync(new RemoteAddRequest { Input = path, ApprovedUncached = true });
        await rig.RunAsync();
        Assert.Equal(bytes.Length, Assert.Single(rig.Mock.Submissions).FileBytes);
        Assert.Equal(RemoteItemKind.Torrent, job.Kind);
    }

    [Fact]
    public async Task AMalformedOrMissingTorrentFileIsRefusedAndNothingIsSaved()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var bad = await rig.Jobs.AddAsync(new RemoteAddRequest { TorrentFile = "d4:infoi1ee"u8.ToArray(), ApprovedUncached = true });
        Assert.Contains("not a valid .torrent", bad.Error);

        var missing = await rig.Jobs.AddAsync(new RemoteAddRequest { Input = Path.Combine(rig.Destination, "missing.torrent") });
        Assert.Contains("doesn't exist", missing.Error);

        Assert.Empty(rig.Jobs.List());
        Assert.Empty(rig.Mock.Submissions);
        Assert.Empty(Directory.GetFiles(rig.Ctx.Paths.RemoteJobsDirectory));
    }

    // ---- Local actions and remote delete ------------------------------------------------------

    [Fact]
    public async Task PausingCancellingAndRemovingLocallyNeverTouchTheRemoteItem()
    {
        await using var rig = await RemoteRig.CreateAsync(_server, connections: 1);
        rig.Mock.CdnBytesPerSecond = 256 * Kb;
        var files = Enumerable.Range(1, 3).Select(i => MockTorBoxFile.Of(rig.Mock.NextFileId(), $"Pack/{i}.bin", 2 * Mb, 50 + i)).ToList();
        var item = rig.Mock.AddItem(RemoteItemKind.Torrent, "Pack", files);
        var job = await rig.AddAsync(new RemoteAddRequest { Input = $"https://torbox.app/download?id={item.Id}&type=torrents", Selection = RemoteSelectionMode.All });
        await rig.RunAsync();
        var children = rig.Children();
        Assert.Equal(3, children.Count);

        Assert.True((await rig.Ctx.Service.PauseAsync(children[0].Id)).Ok);
        Assert.True((await rig.Ctx.Service.CancelAsync(children[1].Id)).Ok);
        Assert.True((await rig.Ctx.Service.RemoveAsync(children[2].Id, deleteFile: true)).Ok);
        Assert.True((await rig.Jobs.RemoveAsync(job.Id)).Ok);
        Assert.Empty(rig.Jobs.List());

        Assert.Empty(rig.Mock.Deleted);
        Assert.Equal(0, rig.Mock.Calls("POST torrents/controltorrent"));
        Assert.Empty(Directory.GetFiles(rig.Ctx.Paths.RemoteJobsDirectory));
    }

    [Fact]
    public async Task RemovingARecordLeavesNoSecretEvenIfTheDeleteNeverHappens()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var magnet = "magnet:?xt=urn:btih:" + new string('f', 40);
        var job = await rig.AddAsync(new RemoteAddRequest { Input = magnet, LinkPassword = "invented-password" });
        rig.Store.PersistKillingWrite = true;
        rig.Store.KillOn = record => record.State == RemoteJobState.Removed;
        await Assert.ThrowsAsync<IOException>(() => rig.Jobs.RemoveAsync(job.Id));

        var saved = await File.ReadAllTextAsync(Path.Combine(rig.Ctx.Paths.RemoteJobsDirectory, $"{job.Id:N}.json"));
        Assert.DoesNotContain("protectedInput", saved);
        Assert.DoesNotContain("protectedLinkPassword", saved);

        await rig.RestartAsync();
        Assert.Empty(rig.Jobs.List());
        Assert.Empty(Directory.GetFiles(rig.Ctx.Paths.RemoteJobsDirectory));
    }

    [Fact]
    public async Task RemoteDeleteNeedsAnExplicitConfirmationAndOnlyTouchesThatItem()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var mine = rig.Mock.AddItem(RemoteItemKind.Torrent, "Mine", [MockTorBoxFile.Of(rig.Mock.NextFileId(), "Mine/a.bin", Kb, 1)]);
        var other = rig.Mock.AddItem(RemoteItemKind.Torrent, "Other", [MockTorBoxFile.Of(rig.Mock.NextFileId(), "Other/a.bin", Kb, 2)]);
        var job = await rig.AddAsync(new RemoteAddRequest { Input = $"https://torbox.app/download?id={mine.Id}&type=torrents" });
        await rig.RunAsync();

        var unconfirmed = await rig.Jobs.RemoveRemoteItemAsync(job.Id, confirmed: false);
        Assert.False(unconfirmed.Ok);
        Assert.Contains("confirmed", unconfirmed.Error);
        Assert.Empty(rig.Mock.Deleted);
        Assert.NotNull(rig.Jobs.Find(job.Id));

        Assert.True((await rig.Jobs.RemoveRemoteItemAsync(job.Id, confirmed: true)).Ok);
        Assert.Equal(mine.Id, Assert.Single(rig.Mock.Deleted).Id);
        Assert.Null(rig.Jobs.Find(job.Id));
        Assert.NotNull(other);
    }

    [Fact]
    public async Task ACheckFailureDuringRemoteDeleteKeepsTheJobAndSaysWhy()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var item = rig.Mock.AddItem(RemoteItemKind.Torrent, "Mine", [MockTorBoxFile.Of(rig.Mock.NextFileId(), "Mine/a.bin", Kb, 1)]);
        var job = await rig.AddAsync(new RemoteAddRequest { Input = $"https://torbox.app/download?id={item.Id}&type=torrents" });
        rig.Mock.Fail("POST torrents/controltorrent", MockFault.Status5xx());
        var failed = await rig.Jobs.RemoveRemoteItemAsync(job.Id, confirmed: true);
        Assert.False(failed.Ok);
        Assert.Contains("having problems", failed.Error);
        Assert.NotNull(rig.Jobs.Find(job.Id));
    }

    // ---- Hoster list --------------------------------------------------------------------------

    [Fact]
    public async Task TheHosterListIsKeptForAnHourAndABadPatternIsOnlyThatHostersProblem()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        rig.Mock.AddHoster("Broken", ["broken.invalid"], "((((");
        rig.Mock.AddHoster("Slow", ["slow.invalid"], "^(a+)+$");

        var link = rig.HosterLink("list");
        Assert.Equal(RemoteInputKind.HosterLink, (await rig.Jobs.ClassifyAsync(link)).Kind);
        // Its pattern can't be used, and its domains still say what it is.
        Assert.Equal(RemoteInputKind.HosterLink, (await rig.Jobs.ClassifyAsync("https://broken.invalid/x")).Kind);
        Assert.Equal(RemoteInputKind.HosterLink, (await rig.Jobs.ClassifyAsync(link)).Kind);
        Assert.Equal(1, rig.Mock.Calls("GET webdl/hosters"));

        rig.Time.Advance(TimeSpan.FromMinutes(59));
        await rig.Jobs.ClassifyAsync(link);
        Assert.Equal(1, rig.Mock.Calls("GET webdl/hosters"));

        rig.Time.Advance(TimeSpan.FromMinutes(2));
        await rig.Jobs.ClassifyAsync(link);
        Assert.Equal(2, rig.Mock.Calls("GET webdl/hosters"));
    }

    [Fact]
    public async Task AFailedHosterRefreshKeepsTheListItHadAndPausesBeforeTryingAgain()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var link = rig.HosterLink("list");
        rig.Mock.Fail("GET webdl/hosters", MockFault.Status5xx());

        // Nothing to fall back on yet: it can only say so.
        var classified = await rig.Jobs.ClassifyAsync(link);
        Assert.Equal(RemoteInputKind.Undetermined, classified.Kind);
        Assert.Contains("couldn't be fetched", classified.Note);
        await rig.Jobs.ClassifyAsync(link);
        Assert.Equal(1, rig.Mock.Calls("GET webdl/hosters"));

        rig.Mock.ClearFaults();
        rig.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(RemoteInputKind.HosterLink, (await rig.Jobs.ClassifyAsync(link)).Kind);

        // Now it has one to fall back on.
        rig.Mock.Fail("GET webdl/hosters", MockFault.Abort);
        rig.Time.Advance(TimeSpan.FromHours(2));
        Assert.Equal(RemoteInputKind.HosterLink, (await rig.Jobs.ClassifyAsync(link)).Kind);
    }

    // ---- Retrying and checking ----------------------------------------------------------------

    [Fact]
    public async Task RetryingAnItemThatWentAwaySubmitsAgainThroughTheSameChecks()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var (link, hash, file) = rig.ScriptHoster("away", MockTorBoxState.Downloading(), MockTorBoxState.Downloading());
        var job = await rig.AddAsync(new RemoteAddRequest { Input = link, ApprovedUncached = true, Selection = RemoteSelectionMode.All });
        await rig.RunAsync();
        Assert.Equal(1, rig.Mock.Calls(CreateWeb));

        rig.Mock.RemoveItem(rig.Mock.FindItem(RemoteItemKind.WebDownload, hash)!);
        await rig.AdvanceAsync(30);
        await rig.AdvanceAsync(61);
        var gone = rig.View(job.Id);
        Assert.Equal(RemoteJobState.Failed, gone.State);
        Assert.Equal(RemoteFailureKind.NotFound, gone.FailureKind);
        Assert.Contains("no longer on your TorBox account", gone.FailureMessage);
        Assert.DoesNotContain("folder link", gone.FailureMessage);
        Assert.Equal(1, rig.Mock.Calls(CreateWeb));

        // It stays failed until asked. Then it is a new submission, of something confirmed to be gone.
        await rig.AdvanceAsync(3600);
        Assert.Equal(1, rig.Mock.Calls(CreateWeb));
        rig.Mock.ScriptSubmission(hash, [file], [MockTorBoxState.Ready]);
        Assert.True((await rig.Jobs.RetryAsync(job.Id)).Ok);
        await rig.RunAsync();
        Assert.Equal(2, rig.Mock.Calls(CreateWeb));
        Assert.Equal(RemoteJobState.LocalStarted, rig.View(job.Id).State);
    }

    [Fact]
    public async Task RetryingAFolderLinkOnlyLooksAgain()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var job = await rig.AddAsync(new RemoteAddRequest { Input = "https://torbox.app/download?id=424242&type=torrents" });
        await rig.RunAsync();
        Assert.Equal(RemoteJobState.Failed, rig.View(job.Id).State);

        Assert.True((await rig.Jobs.RetryAsync(job.Id)).Ok);
        await rig.RunAsync();
        Assert.Equal(RemoteJobState.Failed, rig.View(job.Id).State);
        Assert.Equal(2, rig.Mock.Calls("GET torrents/mylist"));
        Assert.Empty(rig.Mock.Submissions);
    }

    [Fact]
    public async Task ActionsThatDontApplyAreRefusedInWords()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var item = rig.Mock.AddItem(RemoteItemKind.Torrent, "Pack", [MockTorBoxFile.Of(rig.Mock.NextFileId(), "Pack/a.bin", 8 * Kb, 1)]);
        var job = await rig.AddAsync(new RemoteAddRequest { Input = $"https://torbox.app/download?id={item.Id}&type=torrents", Selection = RemoteSelectionMode.All });
        await rig.RunAsync();
        Assert.Equal(RemoteJobState.LocalStarted, rig.View(job.Id).State);

        Assert.Contains("already been sent", (await rig.Jobs.ApproveUncachedAsync(job.Id)).Error);
        Assert.Contains("failed or blocked", (await rig.Jobs.RetryAsync(job.Id)).Error);
        Assert.Contains("nothing to check", (await rig.Jobs.CheckNowAsync(job.Id)).Error);
        var nobody = Guid.NewGuid();
        Assert.Contains("not on the list", (await rig.Jobs.RetryAsync(nobody)).Error);
        Assert.Contains("not on the list", (await rig.Jobs.RemoveAsync(nobody)).Error);
        Assert.Contains("not on the list", (await rig.Jobs.GetFilesAsync(nobody)).Error);
        Assert.Contains("not on the list", (await rig.Jobs.RemoveRemoteItemAsync(nobody, confirmed: true)).Error);
    }

    [Fact]
    public async Task CheckNowLooksAgainAtWhatIsWaitingForTheUser()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var item = rig.Mock.AddItem(RemoteItemKind.Torrent, "Pack",
            [MockTorBoxFile.Of(rig.Mock.NextFileId(), "Pack/a.bin", 8 * Kb, 1), MockTorBoxFile.Of(rig.Mock.NextFileId(), "Pack/b.bin", 8 * Kb, 2)]);
        var job = await rig.AddAsync(new RemoteAddRequest { Input = $"https://torbox.app/download?id={item.Id}&type=torrents" });
        await rig.RunAsync();
        Assert.Equal(RemoteJobState.AwaitingSelection, rig.View(job.Id).State);

        var calls = rig.Mock.Calls("GET torrents/mylist");
        Assert.True((await rig.Jobs.CheckNowAsync(job.Id)).Ok);
        await rig.RunAsync();
        Assert.Equal(calls + 1, rig.Mock.Calls("GET torrents/mylist"));
        Assert.Equal(RemoteJobState.AwaitingSelection, rig.View(job.Id).State);
    }

    [Fact]
    public async Task ApprovalLooksAtTheCacheAgainWhenAskedToCheckNow()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var (link, hash, _) = rig.ScriptHoster("later", MockTorBoxState.CachedReady);
        var job = await rig.AddAsync(new RemoteAddRequest { Input = link, Selection = RemoteSelectionMode.All });
        await rig.RunAsync();
        Assert.True(rig.View(job.Id).ApprovalNeeded);

        rig.Mock.AddCached(hash);
        Assert.True((await rig.Jobs.CheckNowAsync(job.Id)).Ok);
        await rig.RunAsync();
        Assert.Equal(RemoteJobState.LocalStarted, rig.View(job.Id).State);
        Assert.True(Assert.Single(rig.Mock.Submissions).OnlyIfCached);
    }

    [Fact]
    public async Task AnAccountTooBigToSearchIsNotCalledAbsentAndNothingIsSent()
    {
        await using var rig = await RemoteRig.CreateAsync(_server, options: new RemoteCoordinatorOptions { ListPageSize = 2, MostListPages = 2 });
        for (var i = 0; i < 6; i++)
        {
            rig.Mock.AddItem(RemoteItemKind.WebDownload, $"item{i}", [MockTorBoxFile.Of(rig.Mock.NextFileId(), $"item{i}/a.bin", Kb, i + 1)]);
        }

        var (link, _, _) = rig.ScriptHoster("deep", MockTorBoxState.Ready);
        var job = await rig.AddAsync(new RemoteAddRequest { Input = link, ApprovedUncached = true });
        await rig.RunAsync();

        Assert.Equal(RemoteJobState.Intended, rig.View(job.Id).State);
        Assert.Contains("too many items", rig.View(job.Id).Status);
        Assert.Equal(2, rig.Mock.Calls("GET webdl/mylist"));
        Assert.Empty(rig.Mock.Submissions);
    }

    [Fact]
    public async Task ManyWaitingItemsShareOneReadOfTheList()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var ids = new List<Guid>();
        for (var i = 0; i < 4; i++)
        {
            var item = rig.Mock.AddItem(RemoteItemKind.Torrent, $"item{i}", [MockTorBoxFile.Of(rig.Mock.NextFileId(), $"item{i}/a.bin", Kb, i + 1)], MockTorBoxState.Downloading(), MockTorBoxState.Downloading(), MockTorBoxState.Downloading());
            ids.Add((await rig.AddAsync(new RemoteAddRequest { Input = $"https://torbox.app/download?id={item.Id}&type=torrents" })).Id);
            rig.Time.Advance(TimeSpan.FromMilliseconds(1));
        }

        rig.Time.Advance(TimeSpan.FromSeconds(1));
        await rig.RunAsync();
        Assert.All(ids, id => Assert.Equal(RemoteStage.Processing, rig.View(id).Stage));
        Assert.Equal(1, rig.Mock.Calls("GET torrents/mylist"));
    }

    [Fact]
    public async Task AnItemThatIsntChangingDoesNotRewriteItsFile()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var (link, _, _) = rig.ScriptHoster("quiet", MockTorBoxState.Stalled);
        await rig.AddAsync(new RemoteAddRequest { Input = link, ApprovedUncached = true });
        await rig.RunAsync();
        await rig.AdvanceAsync(31);
        var writes = rig.Store.Writes;
        for (var i = 0; i < 3; i++)
        {
            await rig.AdvanceAsync(31);
        }

        Assert.Equal(writes, rig.Store.Writes);
    }

    // ---- Scenario J: restart ------------------------------------------------------------------

    [Fact]
    public async Task ScenarioJ_KilledBeforeThePostSendsItOnceAfterTheGrace()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var (link, _, file) = rig.ScriptHoster("before", MockTorBoxState.Ready);
        var job = await rig.AddAsync(new RemoteAddRequest { Input = link, ApprovedUncached = true, Selection = RemoteSelectionMode.All });

        // On disk as Submitting, and the process goes before the request.
        rig.Store.PersistKillingWrite = true;
        rig.Store.KillOn = record => record.State == RemoteJobState.Submitting;
        await rig.RunAsync();
        Assert.True(rig.Store.Dead);
        Assert.Equal(0, rig.Mock.Calls(CreateWeb));

        await rig.RestartAsync();
        Assert.Equal(RemoteJobState.SubmissionUnknown, rig.View(job.Id).State);
        await rig.RunAsync();
        Assert.Equal(RemoteJobState.SubmissionUnknown, rig.View(job.Id).State);
        Assert.Equal(0, rig.Mock.Calls(CreateWeb));

        // Not yet confirmed absent for long enough.
        await rig.AdvanceAsync(59);
        Assert.Equal(0, rig.Mock.Calls(CreateWeb));

        await rig.AdvanceAsync(1);
        await rig.AdvanceAsync(5);
        Assert.Equal(1, rig.Mock.Calls(CreateWeb));
        Assert.Equal(RemoteJobState.LocalStarted, rig.View(job.Id).State);
        Assert.Single(rig.Children());
        await rig.AssertCompletedAsync(file, "before", "before.bin");
    }

    [Fact]
    public async Task ScenarioJ_ALostAnswerIsReconciledWithoutASecondSubmit()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var (link, _, file) = rig.ScriptHoster("lost", MockTorBoxState.Downloading(), MockTorBoxState.Ready);
        rig.Mock.Fail(CreateWeb, MockFault.AcceptThenAbort, times: 1);
        var job = await rig.AddAsync(new RemoteAddRequest { Input = link, ApprovedUncached = true, Selection = RemoteSelectionMode.All });
        await rig.RunAsync();

        // Without any restart: the list had it, so it just carries on.
        Assert.Equal(1, rig.Mock.Calls(CreateWeb));
        var view = rig.View(job.Id);
        Assert.Equal(RemoteJobState.Waiting, view.State);
        Assert.NotNull(view.ItemId);

        await rig.AdvanceAsync(30);
        Assert.Equal(RemoteJobState.LocalStarted, rig.View(job.Id).State);
        Assert.Equal(1, rig.Mock.Calls(CreateWeb));
        await rig.AssertCompletedAsync(file, "lost", "lost.bin");
    }

    [Fact]
    public async Task ScenarioJ_KilledWithTheAnswerLostFindsTheItemAfterTheRestart()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var (link, hash, file) = rig.ScriptHoster("lost2", MockTorBoxState.Ready);
        rig.Mock.Fail(CreateWeb, MockFault.AcceptThenAbort, times: 1);
        var job = await rig.AddAsync(new RemoteAddRequest { Input = link, ApprovedUncached = true, Selection = RemoteSelectionMode.All });

        // The write after the POST never reaches disk.
        rig.Store.KillOn = record => record.State == RemoteJobState.SubmissionUnknown;
        await rig.RunAsync();
        Assert.True(rig.Store.Dead);
        Assert.Equal(1, rig.Mock.Calls(CreateWeb));

        await rig.RestartAsync();
        Assert.Equal(RemoteJobState.SubmissionUnknown, rig.View(job.Id).State);
        await rig.RunAsync();
        Assert.Equal(RemoteJobState.LocalStarted, rig.View(job.Id).State);
        Assert.Equal(1, rig.Mock.Calls(CreateWeb));
        Assert.Equal(hash, rig.Mock.FindItem(RemoteItemKind.WebDownload, hash)!.Hash);
        await rig.AssertCompletedAsync(file, "lost2", "lost2.bin");
    }

    [Fact]
    public async Task ScenarioJ_ALostAnswerThatTheProviderNeverGotIsSentAgainOnlyWhenConfirmedAbsent()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var (link, _, _) = rig.ScriptHoster("never", MockTorBoxState.Ready);
        rig.Mock.Fail(CreateWeb, MockFault.Abort, times: 1);
        var job = await rig.AddAsync(new RemoteAddRequest { Input = link, ApprovedUncached = true, Selection = RemoteSelectionMode.All });
        await rig.RunAsync();
        Assert.Equal(RemoteJobState.SubmissionUnknown, rig.View(job.Id).State);
        Assert.Equal(1, rig.Mock.Calls(CreateWeb));

        // A failed look says nothing, however long it goes on, so nothing is sent.
        rig.Mock.Fail("GET webdl/mylist", MockFault.Status5xx());
        await rig.AdvanceAsync(90);
        await rig.AdvanceAsync(90);
        await rig.AdvanceAsync(180);
        Assert.Equal(RemoteJobState.SubmissionUnknown, rig.View(job.Id).State);
        Assert.Equal(1, rig.Mock.Calls(CreateWeb));

        // The queue is part of the answer too.
        rig.Mock.ClearFaults();
        rig.Mock.Fail("GET queued/getqueued", MockFault.Status5xx());
        await rig.AdvanceAsync(600);
        Assert.Equal(1, rig.Mock.Calls(CreateWeb));

        rig.Mock.ClearFaults();
        await rig.AdvanceAsync(900);
        Assert.Equal(2, rig.Mock.Calls(CreateWeb));
        Assert.Equal(RemoteJobState.LocalStarted, rig.View(job.Id).State);
    }

    [Fact]
    public async Task ScenarioJ_AServerErrorAnswerToThePostIsTreatedAsLost()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var (link, _, _) = rig.ScriptHoster("err", MockTorBoxState.Ready);
        rig.Mock.Fail(CreateWeb, MockFault.Status5xx(), times: 1);
        var job = await rig.AddAsync(new RemoteAddRequest { Input = link, ApprovedUncached = true, Selection = RemoteSelectionMode.All });
        await rig.RunAsync();
        var view = rig.View(job.Id);
        Assert.Equal(RemoteJobState.SubmissionUnknown, view.State);
        Assert.Contains("having problems", view.Status + view.FailureMessage);

        await rig.AdvanceAsync(60);
        await rig.AdvanceAsync(5);
        Assert.Equal(2, rig.Mock.Calls(CreateWeb));
        Assert.Equal(RemoteJobState.LocalStarted, rig.View(job.Id).State);
    }

    [Fact]
    public async Task ScenarioJ_AProviderThatCallsItADuplicateIsBelievedByLookingItUp()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var link = rig.HosterLink("duplicate");
        var hash = RemoteRig.Md5Of(link);
        // Just added elsewhere, so the first look finds nothing and the POST then says it's there.
        rig.Mock.Fail(CreateWeb, MockFault.Error("DUPLICATE_ITEM", "Already there.", 400), times: 1);
        var job = await rig.AddAsync(new RemoteAddRequest { Input = link, ApprovedUncached = true, Selection = RemoteSelectionMode.All });
        await rig.RunAsync();
        Assert.Equal(RemoteJobState.SubmissionUnknown, rig.View(job.Id).State);

        var item = rig.Mock.AddItem(RemoteItemKind.WebDownload, "duplicate", [MockTorBoxFile.Of(rig.Mock.NextFileId(), "duplicate/a.bin", 32 * Kb, 3)]);
        item.Hash = hash;
        await rig.AdvanceAsync(60);
        Assert.Equal(1, rig.Mock.Calls(CreateWeb));
        Assert.Equal(RemoteJobState.LocalStarted, rig.View(job.Id).State);
        Assert.Equal(item.Id.ToString(), rig.View(job.Id).ItemId);
    }

    /// <summary>A real outage is retried, since the item may have reached the provider. When that stops, what it said is what the user gets.</summary>
    [Fact]
    public async Task ScenarioJ_RepeatedProviderErrorsEndWithWhatTheProviderSaid()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var (link, _, _) = rig.ScriptHoster("outage", MockTorBoxState.Ready);
        rig.Mock.Fail(CreateWeb, MockFault.Status5xx(500, "UNKNOWN_ERROR", "The scripted reason."));
        var job = await rig.AddAsync(new RemoteAddRequest { Input = link, ApprovedUncached = true, Selection = RemoteSelectionMode.All });
        await rig.RunAsync();
        for (var i = 0; i < 20; i++)
        {
            await rig.AdvanceAsync(65);
        }

        var failed = rig.View(job.Id);
        Assert.Equal(RemoteJobState.Failed, failed.State);
        Assert.Contains("didn't list this", failed.Status);
        Assert.Contains("The scripted reason.", failed.Status);
        Assert.Equal(4, rig.Mock.Calls(CreateWeb));
    }

    [Fact]
    public async Task ScenarioJ_SomethingNeverListedStopsAfterALimitedNumberOfTries()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var (link, _, _) = rig.ScriptHoster("ghost", MockTorBoxState.Ready);
        rig.Mock.Fail(CreateWeb, MockFault.Abort);
        var job = await rig.AddAsync(new RemoteAddRequest { Input = link, ApprovedUncached = true, Selection = RemoteSelectionMode.All });
        await rig.RunAsync();
        for (var i = 0; i < 20; i++)
        {
            await rig.AdvanceAsync(65);
        }

        var failed = rig.View(job.Id);
        Assert.Equal(RemoteJobState.Failed, failed.State);
        Assert.Contains("didn't list this", failed.FailureMessage);
        Assert.Equal(4, rig.Mock.Calls(CreateWeb));

        var calls = rig.Mock.TotalApiCalls;
        await rig.AdvanceAsync(3600);
        Assert.Equal(calls, rig.Mock.TotalApiCalls);
    }

    [Fact]
    public async Task ScenarioJ_RestartWhileWaitingResumesMonitoringAndStillSubmitsOnce()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var (link, _, file) = rig.ScriptHoster("waiting", MockTorBoxState.Downloading(0.1), MockTorBoxState.Downloading(0.5), MockTorBoxState.Ready);
        var job = await rig.AddAsync(new RemoteAddRequest { Input = link, ApprovedUncached = true, Selection = RemoteSelectionMode.All });
        await rig.RunAsync();
        var before = rig.View(job.Id);
        Assert.Equal(RemoteJobState.Waiting, before.State);

        await rig.RestartAsync();
        var restored = rig.View(job.Id);
        Assert.Equal(before.ItemId, restored.ItemId);
        Assert.Equal(RemoteJobState.Waiting, restored.State);
        Assert.Equal(before.NextCheckAt, restored.NextCheckAt);

        await rig.AdvanceAsync(20);
        await rig.AdvanceAsync(20);
        Assert.Equal(RemoteJobState.LocalStarted, rig.View(job.Id).State);
        Assert.Equal(1, rig.Mock.Calls(CreateWeb));
        await rig.AssertCompletedAsync(file, "waiting", "waiting.bin");
    }

    [Fact]
    public async Task ScenarioJ_KilledAfterReadyBeforeChildrenCreatesThemOnce()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var (link, _, file) = rig.ScriptHoster("ready", MockTorBoxState.Ready);
        var job = await rig.AddAsync(new RemoteAddRequest { Input = link, ApprovedUncached = true, Selection = RemoteSelectionMode.All });
        rig.Host.DieAfterJobs = 0;
        rig.Host.OnDeath = () => rig.Store.Dead = true;
        await rig.RunAsync();
        Assert.Empty(rig.Children());

        await rig.RestartAsync();
        await rig.AdvanceAsync(30);
        Assert.Equal(RemoteJobState.LocalStarted, rig.View(job.Id).State);
        Assert.Single(rig.Children());
        Assert.Equal(1, rig.Mock.Calls(CreateWeb));
        await rig.AssertCompletedAsync(file, "ready", "ready.bin");
    }

    [Fact]
    public async Task ScenarioJ_KilledMidChildrenFinishesTheRestWithoutDuplicates()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var link = rig.HosterLink("mid");
        var hash = RemoteRig.Md5Of(link);
        var files = Enumerable.Range(1, 6).Select(i => MockTorBoxFile.Of(rig.Mock.NextFileId(), $"mid/{i}.bin", 32 * Kb, 60 + i)).ToList();
        rig.Mock.ScriptSubmission(hash, files, [MockTorBoxState.Ready]);
        var job = await rig.AddAsync(new RemoteAddRequest { Input = link, ApprovedUncached = true, Selection = RemoteSelectionMode.All });
        rig.Host.DieAfterJobs = 3;
        rig.Host.OnDeath = () => rig.Store.Dead = true;
        await rig.RunAsync();
        Assert.Equal(3, rig.Children().Count);
        var firstThree = rig.Children().Select(child => child.Id).ToHashSet();

        await rig.RestartAsync();
        Assert.Equal(3, rig.Children().Count);
        await rig.AdvanceAsync(30);

        var started = rig.View(job.Id);
        Assert.Equal(RemoteJobState.LocalStarted, started.State);
        var children = rig.Children();
        Assert.Equal(6, children.Count);
        Assert.Equal(6, children.Select(child => child.FinalFilePath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Superset(firstThree, children.Select(child => child.Id).ToHashSet());
        Assert.Equal(1, rig.Mock.Calls(CreateWeb));
        foreach (var file in files)
        {
            await rig.AssertCompletedAsync(file, "mid", file.ShortName);
        }
    }

    [Fact]
    public async Task ScenarioJ_ChildrenExistButLocalStartedWasNeverSavedIsHarmless()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var (link, _, _) = rig.ScriptHoster("almost", MockTorBoxState.Ready);
        var job = await rig.AddAsync(new RemoteAddRequest { Input = link, ApprovedUncached = true, Selection = RemoteSelectionMode.All });
        rig.Store.KillOn = record => record.State == RemoteJobState.LocalStarted;
        await rig.RunAsync();
        var child = Assert.Single(rig.Children());

        await rig.RestartAsync();
        await rig.AdvanceAsync(30);
        Assert.Equal(RemoteJobState.LocalStarted, rig.View(job.Id).State);
        Assert.Equal(child.Id, Assert.Single(rig.Children()).Id);
        Assert.Equal(1, rig.Mock.Calls(CreateWeb));
    }

    /// <summary>
    /// The whole road from add to local jobs, with the process dying at each write it makes in turn,
    /// where that write either reached the disk or didn't. Whatever happened, one submission and one
    /// set of children have to come out at the end.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ScenarioJ_KilledAtEveryDurableWrite(bool writeReachedDisk, bool answerLost)
    {
        async Task<int> OnceAsync(int killAt)
        {
            await using var rig = await RemoteRig.CreateAsync(_server);
            var link = rig.HosterLink("matrix");
            var hash = RemoteRig.Md5Of(link);
            var files = Enumerable.Range(1, 3).Select(i => MockTorBoxFile.Of(rig.Mock.NextFileId(), $"matrix/{i}.bin", 8 * Kb, 80 + i)).ToList();
            rig.Mock.ScriptSubmission(hash, files, [MockTorBoxState.Downloading(), MockTorBoxState.Ready]);
            if (answerLost)
            {
                rig.Mock.Fail(CreateWeb, MockFault.AcceptThenAbort, times: 1);
            }

            rig.Store.KillAtWrite = killAt;
            rig.Store.PersistKillingWrite = writeReachedDisk;
            var addFailed = false;
            try
            {
                await rig.AddAsync(new RemoteAddRequest { Input = link, ApprovedUncached = true, Selection = RemoteSelectionMode.All });
            }
            catch (IOException)
            {
                addFailed = true;
            }

            for (var i = 0; i < 6 && !rig.Store.Dead; i++)
            {
                await rig.AdvanceAsync(35);
            }

            var writes = rig.Store.Writes;
            var diedInTheRun = rig.Store.Dead;
            var submittedBeforeDeath = rig.Mock.Calls(CreateWeb);
            await rig.RestartAsync();

            if (addFailed && !writeReachedDisk)
            {
                // It never happened, as far as anyone can tell.
                Assert.Empty(rig.Jobs.List());
                Assert.Equal(0, rig.Mock.Calls(CreateWeb));
                return writes;
            }

            var job = Assert.Single(rig.Jobs.List());
            for (var i = 0; i < 12 && rig.View(job.Id).State != RemoteJobState.LocalStarted; i++)
            {
                await rig.AdvanceAsync(35);
            }

            var context = $"kill at {killAt}, reached disk {writeReachedDisk}, answer lost {answerLost}, died {diedInTheRun}, submitted first {submittedBeforeDeath}";
            Assert.True(rig.View(job.Id).State == RemoteJobState.LocalStarted, $"{rig.View(job.Id).State}: {rig.View(job.Id).FailureMessage} ({context})");
            Assert.True(rig.Mock.Calls(CreateWeb) == 1, $"{rig.Mock.Calls(CreateWeb)} submissions ({context})");
            Assert.True(rig.Mock.Submissions.Count == 1, context);
            var children = rig.Children();
            Assert.True(children.Count == 3, $"{children.Count} children ({context})");
            var package = Assert.Single(rig.Ctx.Service.GetSnapshot().Settings.DownloadGroups);
            Assert.Equal(3, package.JobIds.Count);
            foreach (var file in files)
            {
                await rig.AssertCompletedAsync(file, "matrix", file.ShortName);
            }

            return writes;
        }

        // Counts the writes of a run that isn't killed, then kills at each one. A kill past the end changes nothing.
        var total = await OnceAsync(int.MaxValue);
        Assert.InRange(total, 5, 30);
        for (var kill = 1; kill <= total; kill++)
        {
            await OnceAsync(kill);
        }
    }

    [Fact]
    public async Task ScenarioJ_LocalJobsStayWithThePackageAcrossAFullRestart()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var files = Enumerable.Range(1, 3).Select(i => MockTorBoxFile.Of(rig.Mock.NextFileId(), $"Pack/{i}.bin", 32 * Kb, 90 + i)).ToList();
        var item = rig.Mock.AddItem(RemoteItemKind.Torrent, "Pack", files);
        var job = await rig.AddAsync(new RemoteAddRequest { Input = $"https://torbox.app/download?id={item.Id}&type=torrents", Selection = RemoteSelectionMode.All });
        await rig.RunAsync();
        var packageId = rig.View(job.Id).PackageId;

        await rig.RestartAsync();
        var calls = rig.Mock.Calls("GET torrents/mylist");
        await rig.AdvanceAsync(3600);
        Assert.Equal(calls, rig.Mock.Calls("GET torrents/mylist"));
        Assert.Equal(RemoteJobState.LocalStarted, rig.View(job.Id).State);
        Assert.Equal(packageId, rig.View(job.Id).PackageId);
        Assert.Contains(rig.Ctx.Service.GetSnapshot().Settings.DownloadGroups, group => group.Id == packageId);
        foreach (var file in files)
        {
            await rig.AssertCompletedAsync(file, "Pack", file.ShortName);
        }
    }

    [Fact]
    public async Task AChangeThatCantBeSavedDoesNotCountAndNothingIsSent()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var (link, _, _) = rig.ScriptHoster("disk", MockTorBoxState.Ready);
        await rig.AddAsync(new RemoteAddRequest { Input = link, ApprovedUncached = true, Selection = RemoteSelectionMode.All });
        rig.Store.Dead = true;

        await rig.RunAsync();
        Assert.Equal(0, rig.Mock.Calls(CreateWeb));

        // Later the disk works again, and it picks up from there.
        rig.Store.Dead = false;
        rig.Store.KillAtWrite = int.MaxValue;
        await rig.AdvanceAsync(31);
        Assert.Equal(1, rig.Mock.Calls(CreateWeb));
    }

    [Fact]
    public async Task AnAnswerThatCantBeSavedFallsBackOnTheList()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var (link, _, _) = rig.ScriptHoster("answer", MockTorBoxState.Ready);
        var job = await rig.AddAsync(new RemoteAddRequest { Input = link, ApprovedUncached = true, Selection = RemoteSelectionMode.All });
        rig.Store.KillOn = record => record.State == RemoteJobState.Waiting;
        await rig.RunAsync();
        Assert.Equal(1, rig.Mock.Calls(CreateWeb));
        Assert.Equal(RemoteJobState.SubmissionUnknown, rig.View(job.Id).State);

        rig.Store.Dead = false;
        rig.Store.KillOn = null;
        await rig.AdvanceAsync(31);
        Assert.Equal(RemoteJobState.LocalStarted, rig.View(job.Id).State);
        Assert.Equal(1, rig.Mock.Calls(CreateWeb));
    }

    [Fact]
    public async Task AJobWhoseSavedInputCantBeReadSaysSoInsteadOfSendingNothing()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var (link, _, _) = rig.ScriptHoster("gone", MockTorBoxState.Ready);
        var job = await rig.AddAsync(new RemoteAddRequest { Input = link, ApprovedUncached = true, Selection = RemoteSelectionMode.All });
        await rig.Jobs.StopAsync();

        var path = Path.Combine(rig.Ctx.Paths.RemoteJobsDirectory, $"{job.Id:N}.json");
        var saved = await File.ReadAllTextAsync(path);
        var damaged = System.Text.RegularExpressions.Regex.Replace(saved, "\"protectedInput\": \"[^\"]+\"", "\"protectedInput\": \"dpapi:v1:AAAA\"");
        Assert.NotEqual(saved, damaged);
        await File.WriteAllTextAsync(path, damaged);

        await rig.RestartCoordinatorOnlyAsync();
        await rig.RunAsync();
        var failed = rig.View(job.Id);
        Assert.Equal(RemoteJobState.Failed, failed.State);
        Assert.Contains("can't be read", failed.FailureMessage);
        Assert.Empty(rig.Mock.Submissions);
    }

    // ---- The loop itself ----------------------------------------------------------------------

    [Fact]
    public async Task TheLoopWorksByItselfIsWokenByCheckNowAndStopsWhenThereIsNothingToDo()
    {
        await using var rig = await RemoteRig.CreateAsync(_server, loops: true);
        var (link, _, file) = rig.ScriptHoster("loop", MockTorBoxState.Downloading(0.1, eta: 50), MockTorBoxState.Downloading(0.5, eta: 50), MockTorBoxState.Ready);

        var job = await rig.AddAsync(new RemoteAddRequest { Input = link, ApprovedUncached = true, Selection = RemoteSelectionMode.All });
        await Eventually(() => rig.View(job.Id).Stage == RemoteStage.Processing, "the first look");
        var calls = rig.Mock.Calls("GET webdl/mylist");
        await Task.Delay(100);
        Assert.Equal(calls, rig.Mock.Calls("GET webdl/mylist"));

        // Check Now doesn't wait for the 50 s, which on this clock would never pass.
        Assert.True((await rig.Jobs.CheckNowAsync(job.Id)).Ok);
        await Eventually(() => rig.Mock.Calls("GET webdl/mylist") > calls, "Check Now");

        // The timer works too.
        await EventuallyWithTime(rig.Time, () => rig.View(job.Id).State == RemoteJobState.LocalStarted, "the timer");
        await rig.AssertCompletedAsync(file, "loop", "loop.bin");
        Assert.Equal(1, rig.Mock.Calls(CreateWeb));

        calls = rig.Mock.TotalApiCalls;
        rig.Time.Advance(TimeSpan.FromHours(1));
        await Task.Delay(100);
        Assert.Equal(calls, rig.Mock.TotalApiCalls);
    }

    [Fact]
    public async Task TheLoopResumesAfterARestartAndStopsWithTheCoordinator()
    {
        await using var rig = await RemoteRig.CreateAsync(_server, loops: true);
        var (link, _, _) = rig.ScriptHoster("again", MockTorBoxState.Downloading(0.1), MockTorBoxState.Downloading(0.5), MockTorBoxState.Ready);
        var job = await rig.AddAsync(new RemoteAddRequest { Input = link, ApprovedUncached = true, Selection = RemoteSelectionMode.All });
        await Eventually(() => rig.View(job.Id).Stage == RemoteStage.Processing, "the first look");

        await rig.RestartAsync(loops: true);
        Assert.Equal(RemoteJobState.Waiting, rig.View(job.Id).State);
        await EventuallyWithTime(rig.Time, () => rig.View(job.Id).Progress == 0.5 || rig.View(job.Id).State == RemoteJobState.LocalStarted, "monitoring to resume");
        Assert.Equal(1, rig.Mock.Calls(CreateWeb));

        await rig.Jobs.StopAsync();
        var calls = rig.Mock.TotalApiCalls;
        rig.Time.Advance(TimeSpan.FromHours(1));
        await Task.Delay(100);
        Assert.Equal(calls, rig.Mock.TotalApiCalls);
    }

    [Fact]
    public async Task SettingAKeyWakesWhatWasWaitingForIt()
    {
        await using var rig = await RemoteRig.CreateAsync(_server, loops: true);
        var item = rig.Mock.AddItem(RemoteItemKind.Torrent, "Pack", [MockTorBoxFile.Of(rig.Mock.NextFileId(), "Pack/a.bin", 8 * Kb, 1)]);
        await rig.Box.Account.ClearAsync();
        await Task.Delay(50);
        var job = await rig.Jobs.AddAsync(new RemoteAddRequest
        {
            ExistingItem = new RemoteItemRef("torbox", RemoteItemKind.Torrent, item.Id.ToString()),
            Destination = rig.Destination,
            Selection = RemoteSelectionMode.All,
        });
        await Eventually(() => rig.View(job.Job!.Id).FailureKind == RemoteFailureKind.Authentication, "the missing key to show");
        Assert.Contains("No TorBox API key", rig.View(job.Job!.Id).Status);
        var before = rig.Mock.TotalApiCalls;
        Assert.Equal(0, before);

        await rig.Box.Account.SetKeyAsync(MockKey);
        await Eventually(() => rig.View(job.Job!.Id).State == RemoteJobState.LocalStarted, "the key to be used");
    }

    [Fact]
    public async Task SomethingChosenFromTheProvidersListNeedsNoLink()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var file = MockTorBoxFile.Of(rig.Mock.NextFileId(), "Pack/a.bin", 32 * Kb, 1);
        var item = rig.Mock.AddItem(RemoteItemKind.Usenet, "Pack", [file]);
        var job = await rig.AddAsync(new RemoteAddRequest
        {
            ExistingItem = new RemoteItemRef("torbox", RemoteItemKind.Usenet, item.Id.ToString()),
            Selection = RemoteSelectionMode.All,
        });
        await rig.RunAsync();
        Assert.Equal(RemoteJobState.LocalStarted, rig.View(job.Id).State);
        await rig.AssertCompletedAsync(file, "Pack", "a.bin");
        Assert.Empty(rig.Mock.Submissions);
    }

    [Fact]
    public async Task EveryChangeIsReportedAndCarriesNoSecret()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var (link, _, _) = rig.ScriptHoster("event", MockTorBoxState.Ready);
        var seen = new List<RemoteJobView>();
        rig.Jobs.Changed += (_, view) =>
        {
            lock (seen) seen.Add(view);
        };
        await rig.AddAsync(new RemoteAddRequest { Input = link, ApprovedUncached = true, Selection = RemoteSelectionMode.All, LinkPassword = "invented-password" });
        await rig.RunAsync();

        Assert.Contains(seen, view => view.State == RemoteJobState.Intended);
        Assert.Contains(seen, view => view.State == RemoteJobState.Submitting);
        Assert.Contains(seen, view => view.State == RemoteJobState.LocalStarted);
        var text = JsonSerializer.Serialize(seen);
        Assert.DoesNotContain("invented-password", text);
        Assert.DoesNotContain("invented-fragment", text);
        Assert.DoesNotContain("/hoster/", text);
        Assert.DoesNotContain("protected", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AStoppedLookDoesntApplyToAJobRemovedMeanwhile()
    {
        await using var rig = await RemoteRig.CreateAsync(_server);
        var item = rig.Mock.AddItem(RemoteItemKind.Torrent, "Pack", [MockTorBoxFile.Of(rig.Mock.NextFileId(), "Pack/a.bin", Kb, 1)]);
        var job = await rig.AddAsync(new RemoteAddRequest { Input = $"https://torbox.app/download?id={item.Id}&type=torrents", Selection = RemoteSelectionMode.All });
        rig.Mock.Fail("GET torrents/mylist", MockFault.Delay(TimeSpan.FromMilliseconds(300)), times: 1);
        var running = rig.RunAsync();
        await Task.Delay(50);
        Assert.True((await rig.Jobs.RemoveAsync(job.Id)).Ok);
        await running;

        Assert.Empty(rig.Jobs.List());
        Assert.Empty(rig.Children());
        Assert.Empty(Directory.GetFiles(rig.Ctx.Paths.RemoteJobsDirectory));
    }
}
