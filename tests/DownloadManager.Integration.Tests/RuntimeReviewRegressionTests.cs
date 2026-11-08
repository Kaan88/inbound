using System.Text.Json;
using System.Text.Json.Nodes;
using DownloadManager.Contracts;
using DownloadManager.Core;
using DownloadManager.Core.Providers;
using DownloadManager.Persistence;
using DownloadManager.Runtime;

namespace DownloadManager.Integration.Tests;

public sealed partial class RuntimeServiceTests
{
    /// <summary>Runs a step at the moment the engine records the final rename, which it does just before making it.</summary>
    private sealed class FinalizeHookCheckpoints(IDownloadCheckpointStore inner) : IDownloadCheckpointStore
    {
        public Func<Task>? OnFinalizing { get; set; }

        public Task<DownloadCheckpoint?> LoadAsync(Guid jobId, CancellationToken cancellationToken = default) => inner.LoadAsync(jobId, cancellationToken);

        public async Task SaveAsync(DownloadCheckpoint checkpoint, CancellationToken cancellationToken = default)
        {
            if (checkpoint.FinalizingTo is not null && OnFinalizing is { } hook) await hook();
            await inner.SaveAsync(checkpoint, cancellationToken);
        }

        public Task DeleteAsync(Guid jobId, CancellationToken cancellationToken = default) => inner.DeleteAsync(jobId, cancellationToken);
    }

    /// <summary>Stops one job's first save, inside the part of a start that runs before anything else can.</summary>
    private sealed class BlockingJobStore(IJobStore inner) : IJobStore
    {
        private readonly ManualResetEventSlim _release = new();
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Guid BlockJob { get; set; }
        public DownloadState? BlockAnyJobIn { get; set; }
        public Task Entered => _entered.Task;

        public void Release() => _release.Set();

        public Task SaveAsync(DownloadJob job, CancellationToken cancellationToken = default)
        {
            if ((job.Id == BlockJob && job.State == DownloadState.Connecting) || job.State == BlockAnyJobIn)
            {
                _entered.TrySetResult();
                _release.Wait(TimeSpan.FromSeconds(30));
            }

            return inner.SaveAsync(job, cancellationToken);
        }

        public Task InitializeAsync(CancellationToken cancellationToken = default) => inner.InitializeAsync(cancellationToken);
        public Task<IReadOnlyList<DownloadJob>> LoadAllAsync(CancellationToken cancellationToken = default) => inner.LoadAllAsync(cancellationToken);
        public Task<DownloadJob?> LoadAsync(Guid jobId, CancellationToken cancellationToken = default) => inner.LoadAsync(jobId, cancellationToken);
        public Task DeleteAsync(Guid jobId, CancellationToken cancellationToken = default) => inner.DeleteAsync(jobId, cancellationToken);
        public Task<Guid?> FindClaimedRequestAsync(string extensionId, string clientRequestId, CancellationToken cancellationToken = default)
            => inner.FindClaimedRequestAsync(extensionId, clientRequestId, cancellationToken);
        public Task<bool> TryClaimRequestAsync(string extensionId, string clientRequestId, Guid jobId, CancellationToken cancellationToken = default)
            => inner.TryClaimRequestAsync(extensionId, clientRequestId, jobId, cancellationToken);
        public Task DeleteClaimAsync(string extensionId, string clientRequestId, CancellationToken cancellationToken = default)
            => inner.DeleteClaimAsync(extensionId, clientRequestId, cancellationToken);
    }

    private static string TestFolder(out StoragePaths paths)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "test-work", Guid.NewGuid().ToString("N"));
        paths = new StoragePaths(Path.Combine(root, "state"));
        var destination = Path.Combine(root, "downloads");
        Directory.CreateDirectory(destination);
        paths.EnsureCreated();
        return destination;
    }

    // ---- A finished file stays finished ----

    [Theory]
    [InlineData("pause")]
    [InlineData("cancel")]
    public async Task AStopArrivingJustBeforeTheRenameStillLeavesACompletedJob(string action)
    {
        const int size = 65536;
        var name = $"late-{action}.bin";
        FinalizeHookCheckpoints? checkpoints = null;
        await using var ctx = await ServiceContext.CreateAsync(_server, autoResume: false, hooks: new ContextHooks
        {
            WrapCheckpoints = inner => checkpoints = new FinalizeHookCheckpoints(inner),
        });
        var job = await AddToPackage(ctx, name, null, url: $"{_server.BaseAddress}/file/{name}?size={size}&seed=801");
        checkpoints!.OnFinalizing = async () =>
        {
            checkpoints.OnFinalizing = null;
            var stopped = action == "pause"
                ? await ctx.Service.PauseAsync(job.Id)
                : await ctx.Service.CancelAsync(job.Id, deleteFile: false);
            Assert.True(stopped.Ok, stopped.Error);
        };
        Assert.True((await ctx.Service.ResumeAsync(job.Id)).Ok);

        var completed = await ctx.WaitFor(job.Id, current => current.State == "completed");
        Assert.Equal(Sha256Of(801, size), await Hashing.Sha256FileAsync(completed.FinalFilePath));
        await WaitUntil(() => !Directory.GetFiles(ctx.Paths.CheckpointsDirectory, $"{job.Id:N}*").Any(), "the checkpoint to go");
    }

    private static async Task<DownloadJob> CrashAfterRenameAsync(ServiceContext ctx, Guid id, long? recordedBytes = null)
    {
        await ctx.Service.ShutdownAsync();
        var jobs = new JsonJobStore(ctx.Paths);
        var job = (await jobs.LoadAsync(id))!;
        var length = new FileInfo(job.FinalFilePath).Length;
        var checkpoint = new DownloadCheckpoint
        {
            JobId = id,
            Url = job.Url,
            TotalBytes = recordedBytes ?? length,
            FinalizingTo = job.FinalFilePath,
        };
        checkpoint.Segments.Add(new SegmentCheckpoint { Start = 0, End = (recordedBytes ?? length) - 1, Completed = recordedBytes ?? length });
        await new JsonCheckpointStore(ctx.Paths).SaveAsync(checkpoint);
        job.State = DownloadState.Downloading;
        job.CompletedAt = null;
        await jobs.SaveAsync(job);
        return job;
    }

    [Fact]
    public async Task AKillBetweenTheRenameAndTheSaveDoesNotDownloadAgain()
    {
        const string name = "killed-after-rename.bin";
        var destination = TestFolder(out var paths);
        var ctx = await ServiceContext.CreateAsync(_server, paths, destination);
        await using (ctx)
        {
            var added = await AddToPackage(ctx, name, null, start: "startNow", url: $"{_server.BaseAddress}/file/{name}?size=65536&seed=802");
            await ctx.WaitFor(added.Id, job => job.State == "completed");
            var crashed = await CrashAfterRenameAsync(ctx, added.Id);

            await using var restarted = await ServiceContext.CreateAsync(_server, paths, destination);
            var adopted = restarted.Service.GetJob(added.Id)!;
            Assert.Equal("completed", adopted.State);
            Assert.Equal(crashed.FinalFilePath, adopted.FinalFilePath);
            Assert.Equal(65536, adopted.ReceivedBytes);
            Assert.Null(await new JsonCheckpointStore(paths).LoadAsync(added.Id));
            await SettleEvents();
            Assert.Single(Directory.GetFiles(destination));
        }
    }

    [Fact]
    public async Task AFileOfAnotherSizeIsNotMistakenForTheFinishedOne()
    {
        const string name = "other-size.bin";
        var destination = TestFolder(out var paths);
        var ctx = await ServiceContext.CreateAsync(_server, paths, destination, autoResume: false);
        await using (ctx)
        {
            var added = await AddToPackage(ctx, name, null, start: "startNow", url: $"{_server.BaseAddress}/file/{name}?size=65536&seed=803");
            await ctx.WaitFor(added.Id, job => job.State == "completed");
            await CrashAfterRenameAsync(ctx, added.Id, recordedBytes: 1000);

            await using var restarted = await ServiceContext.CreateAsync(_server, paths, destination, autoResume: false);
            Assert.Equal("paused", restarted.Service.GetJob(added.Id)!.State);
        }
    }

    // ---- Verification failures are not retried unattended ----

    [Fact]
    public async Task AQueuedFileThatFailsItsChecksumIsNotRetriedByTheQueue()
    {
        const string name = "stale-hash.bin";
        const int size = 65536;
        await using var ctx = await ServiceContext.CreateAsync(_server, autoResume: false);
        var response = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/file/{name}?size={size}&seed=804",
            DestinationDirectory = ctx.Destination,
            FileName = name,
            Source = "cli",
            StartMode = "queue",
            ExpectedSha256 = new string('0', 64),
        });
        Assert.True(response.Accepted, response.Error);

        var failed = await ctx.WaitFor(response.Job!.Id, job => job.State == "failed");
        Assert.Equal("verificationFailed", failed.Failure?.Kind);
        Assert.Null(failed.QueueRetryAt);
        Assert.Equal(0, failed.QueueRetryCount);
        Assert.True(failed.CanResume);
        Assert.Equal(size, new FileInfo(failed.PartFilePath).Length);
    }

    // ---- Removal ----

    [Fact]
    public async Task RemovingAWholePackageNeverAnnouncesItAsFinished()
    {
        var events = new List<PackageCompletedEventArgs>();
        await using var ctx = await ServiceContext.CreateAsync(_server, autoResume: false, maxConcurrent: 5);
        Collect(ctx, events);
        // The running files come first, so the package has never been finished before the removal.
        var running = new List<JobDto>();
        foreach (var name in new[] { "whole-d.bin", "whole-e.bin" })
        {
            running.Add(await AddToPackage(ctx, name, "whole", start: "startNow",
                url: $"{_server.BaseAddress}/slow/{name}?size=4194304&seed=805&bps=262144"));
        }

        var finished = new List<JobDto>();
        foreach (var name in new[] { "whole-a.bin", "whole-b.bin", "whole-c.bin" })
        {
            finished.Add(await ctx.WaitFor((await AddToPackage(ctx, name, "whole", start: "startNow")).Id, job => job.State == "completed"));
        }

        var order = running.Select(job => job.Id).Concat(finished.Select(job => job.Id)).ToList();
        var removed = await ctx.Service.RemoveManyAsync(order, deleteFile: true);
        Assert.True(removed.Ok, removed.Error);
        await SettleEvents();
        Assert.Equal(0, CountOf(events));
        Assert.Empty(ctx.Service.GetSnapshot().Jobs);
        Assert.Empty(ctx.Service.GetSnapshot().Settings.DownloadGroups);
    }

    [Fact]
    public async Task ARemovalThatTimesOutLeavesTheJobInItsPackage()
    {
        var events = new List<PackageCompletedEventArgs>();
        using var handler = new StallFirstRequestHandler();
        await using var ctx = await ServiceContext.CreateAsync(_server, autoResume: false, handler: handler);
        Collect(ctx, events);
        var stuck = await AddToPackage(ctx, "stuck.bin", "stuck", start: "startNow");
        await handler.Entered;
        var done = await AddToPackage(ctx, "done-1.bin", "stuck", start: "startNow");
        await ctx.WaitFor(done.Id, job => job.State == "completed");
        var group = Package(ctx, "stuck");

        var removal = await ctx.Service.RemoveAsync(stuck.Id, deleteFile: false);
        Assert.False(removal.Ok);
        Assert.Contains("still stopping", removal.Error);
        Assert.Contains(stuck.Id, Package(ctx, "stuck").JobIds);
        Assert.Equal(new[] { stuck.Id, done.Id }, group.JobIds);
        await SettleEvents();
        Assert.Equal(0, CountOf(events));

        handler.Release();
        var retried = await ctx.Service.RemoveAsync(stuck.Id, deleteFile: false);
        Assert.True(retried.Ok, retried.Error);
        Assert.DoesNotContain(stuck.Id, ctx.Service.GetSnapshot().Settings.DownloadGroups.SelectMany(package => package.JobIds));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ARemovalACrashInterruptedIsFinishedAtTheNextStart(bool deleteFile)
    {
        var destination = TestFolder(out var paths);
        var ctx = await ServiceContext.CreateAsync(_server, paths, destination, autoResume: true);
        await using (ctx)
        {
            var done = await AddToPackage(ctx, "kept.bin", "interrupted", start: "startNow");
            await ctx.WaitFor(done.Id, job => job.State == "completed");
            var removedJob = await AddToPackage(ctx, "removed.bin", "interrupted");
            await ctx.Service.ShutdownAsync();

            var jobs = new JsonJobStore(paths);
            var stored = (await jobs.LoadAsync(removedJob.Id))!;
            stored.RemovalPending = true;
            stored.DeleteFileOnCancel = deleteFile;
            await jobs.SaveAsync(stored);
            await File.WriteAllTextAsync(stored.PartFilePath, "partial");

            await using var restarted = await ServiceContext.CreateAsync(_server, paths, destination, autoResume: true);
            Assert.Null(restarted.Service.GetJob(removedJob.Id));
            Assert.Null(await jobs.LoadAsync(removedJob.Id));
            Assert.Equal(!deleteFile, File.Exists(stored.PartFilePath));
            Assert.DoesNotContain(restarted.Service.GetSnapshot().Settings.DownloadGroups, group => group.JobIds.Contains(removedJob.Id));
            Assert.Equal("completed", restarted.Service.GetJob(done.Id)!.State);
        }
    }

    [Fact]
    public async Task ARemovalThatCannotBeSavedLeavesEverythingAsItWas()
    {
        var (ctx, store) = await CreateWithFlakyStoreAsync(_server);
        await using (ctx)
        {
            var job = await AddToPackage(ctx, "not-gone.bin", "failed-removal");
            await AddToPackage(ctx, "other.bin", "failed-removal");
            store.FailSaves = true;

            var removal = await ctx.Service.RemoveAsync(job.Id, deleteFile: true);
            Assert.False(removal.Ok);
            store.FailSaves = false;
            Assert.NotNull(ctx.Service.GetJob(job.Id));
            Assert.Contains(job.Id, Package(ctx, "failed-removal").JobIds);
            var stored = await new JsonJobStore(ctx.Paths).LoadAsync(job.Id);
            Assert.False(stored!.RemovalPending);
            Assert.True(stored.DeleteFileOnCancel);
        }
    }

    // ---- Cancellation ----

    [Fact]
    public async Task ACancelBetweenSchedulingAndStartingIsNotUndone()
    {
        BlockingJobStore? jobs = null;
        await using var ctx = await ServiceContext.CreateAsync(_server, autoResume: false, hooks: new ContextHooks
        {
            WrapJobs = inner => jobs = new BlockingJobStore(inner),
        });
        Assert.True((await ctx.Service.QueueActionAsync(new QueueActionRequest { QueueId = 0, Action = "stop" })).Ok);
        var first = await AddToPackage(ctx, "gap-first.bin", null, start: "startNow");
        var second = await AddToPackage(ctx, "gap-second.bin", null, start: "startNow");
        jobs!.BlockJob = first.Id;

        var starting = Task.Run(() => ctx.Service.QueueActionAsync(new QueueActionRequest { QueueId = 0, Action = "start" }));
        await jobs.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        // The first job's start is stuck in its save, so the second is chosen but not yet entered.
        var cancelling = ctx.Service.CancelAsync(second.Id, deleteFile: false);
        jobs.Release();
        Assert.True((await cancelling).Ok);
        await starting;

        await ctx.WaitFor(first.Id, job => job.State == "completed");
        await SettleEvents();
        var cancelled = ctx.Service.GetJob(second.Id)!;
        Assert.Equal("cancelled", cancelled.State);
        Assert.Null(cancelled.StartedAt);
        Assert.False(File.Exists(cancelled.FinalFilePath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFailureAClientCanSeeCanBeActedOnWhileItsRunIsStillSavingIt(bool refresh)
    {
        BlockingJobStore? jobs = null;
        await using var ctx = await ServiceContext.CreateAsync(_server, hooks: new ContextHooks
        {
            WrapJobs = inner => jobs = new BlockingJobStore(inner),
        });
        jobs!.BlockAnyJobIn = DownloadState.Failed;
        var added = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/no-such-route/window.bin",
            DestinationDirectory = ctx.Destination,
            Source = "cli",
        });
        Assert.True(added.Accepted, added.Error);

        await jobs.Entered.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal("failed", ctx.Service.GetJob(added.Job!.Id)!.State);

        var acting = refresh
            ? ctx.Service.RefreshUrlAsync(added.Job.Id, $"{_server.BaseAddress}/file/window.bin?size=4096&seed=71")
            : ctx.Service.ResumeAsync(added.Job.Id);
        await Task.Delay(150);
        Assert.False(acting.IsCompleted);
        jobs.Release();

        var answer = await acting;
        Assert.True(answer.Ok, answer.Error);
    }

    [Fact]
    public async Task ABrokerTimeoutIsAFailureAndNotACancellation()
    {
        var broker = new ScriptedBroker(_ => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout."));
        await using var ctx = await ServiceContext.CreateAsync(_server, linkBroker: broker);
        var result = await ctx.Service.AddRemoteJobsAsync([RemoteFile(ctx, "1", "timeout.bin")], "remote:job-1");

        var failed = await ctx.WaitFor(AddedJob(result).Id, job => job.State == "failed");
        Assert.Equal("unknown", failed.Failure?.Kind);
        Assert.Contains("timed out", failed.Failure?.Message);
        Assert.True(failed.CanResume);
    }

    [Fact]
    public async Task ATimeoutAskingForARefreshKeepsTheTransfersOwnFailure()
    {
        var broker = new ScriptedBroker(_ => RemoteLinkResolution.Link($"{_server.BaseAddress}/error/403"))
        {
            RefreshThrows = new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout."),
        };
        await using var ctx = await ServiceContext.CreateAsync(_server, linkBroker: broker);
        var result = await ctx.Service.AddRemoteJobsAsync([RemoteFile(ctx, "1", "refresh-timeout.bin")], "remote:job-1");

        var failed = await ctx.WaitFor(AddedJob(result).Id, job => job.State == "failed");
        Assert.Equal("forbidden", failed.Failure?.Kind);
    }

    [Fact]
    public async Task ABrokersFailureMessageLosesAnyAddressItQuotes()
    {
        var failure = new JobFailure(FailureKind.NotFound, $"Dead: https://hoster.test/file/{LinkSecret} and magnet:?xt=urn:btih:{LinkSecret}",
            false, true, DateTimeOffset.UtcNow);
        var broker = new ScriptedBroker(_ => RemoteLinkResolution.Fail(failure));
        await using var ctx = await ServiceContext.CreateAsync(_server, linkBroker: broker);
        var result = await ctx.Service.AddRemoteJobsAsync([RemoteFile(ctx, "1", "quoted.bin")], "remote:job-1");

        var failed = await ctx.WaitFor(AddedJob(result).Id, job => job.State == "failed");
        Assert.StartsWith("Dead:", failed.Failure?.Message);
        Assert.DoesNotContain(LinkSecret, failed.Failure?.Message);
        await SettleEvents();
        Assert.DoesNotContain(LinkSecret, await JobJsonAsync(ctx, failed.Id));
    }

    // ---- Limits and reserved names ----

    [Fact]
    public async Task ATooManyPackagesRefusalComesBeforeAnyJobExists()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server, autoResume: false);
        await ctx.Service.ShutdownAsync();
        var settingsStore = new JsonSettingsStore(ctx.Paths);
        var settings = await settingsStore.LoadAsync();
        settings.DownloadGroups.AddRange(Enumerable.Range(0, 1000).Select(index => new DownloadGroup
        {
            Name = $"Package {index}",
            Key = $"batch:{index}",
            CompletionTracked = true,
            JobIds = [Guid.NewGuid(), Guid.NewGuid()],
        }));
        await settingsStore.SaveAsync(settings);

        await using var restarted = await ServiceContext.CreateAsync(_server, ctx.Paths, ctx.Destination, autoResume: false);
        var refused = await restarted.Service.AddRemoteJobsAsync([RemoteFile(restarted, "1", "one-too-many.bin")], "remote:new");
        Assert.Contains("1000 packages", refused.Error);
        Assert.Empty(refused.Items);
        Assert.Empty(restarted.Service.GetSnapshot().Jobs);
    }

    [Theory]
    [InlineData("remote:job-1")]
    [InlineData("  REMOTE:job-1 ")]
    public async Task AClientCannotJoinAProvidersPackage(string key)
    {
        await using var ctx = await ServiceContext.CreateAsync(_server, autoResume: false);
        var response = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/file/forged-member.bin?size=1024&seed=1",
            FileName = "forged-member.bin",
            DestinationDirectory = ctx.Destination,
            Source = "cli",
            PackageKey = key,
        });
        Assert.False(response.Accepted);
        Assert.Equal("invalid_package", response.ErrorCode);
        Assert.Empty(ctx.Service.GetSnapshot().Jobs);
    }

    // ---- Package completion ----

    [Fact]
    public async Task APackageBuiltOneAddAtATimeCompletesOnceWithAllItsFiles()
    {
        var events = new List<PackageCompletedEventArgs>();
        await using var ctx = await ServiceContext.CreateAsync(_server, autoResume: false, hooks: new ContextHooks
        {
            BeforeInitialize = service => service.PackageSettleDelay = TimeSpan.FromSeconds(2),
        });
        Collect(ctx, events);

        var first = await AddToPackage(ctx, "settle.part1.rar", "settle", start: "startNow");
        await ctx.WaitFor(first.Id, job => job.State == "completed");
        var second = await AddToPackage(ctx, "settle.part2.rar", "settle", start: "startNow");
        await ctx.WaitFor(second.Id, job => job.State == "completed");
        Assert.Equal(0, CountOf(events));

        await WaitUntil(() => CountOf(events) == 1, "the completion after the quiet period");
        await Task.Delay(TimeSpan.FromSeconds(2.5));
        Assert.Equal(1, CountOf(events));
        Assert.Equal(new[] { first.Id, second.Id }, events[0].JobIds);
    }

    [Fact]
    public async Task AProviderBatchCompletesWithoutAQuietPeriod()
    {
        var events = new List<PackageCompletedEventArgs>();
        var broker = new ScriptedBroker(job => RemoteLinkResolution.Link($"{_server.BaseAddress}/file/{job.FileName}?size=1024&seed=806"));
        await using var ctx = await ServiceContext.CreateAsync(_server, autoResume: false, linkBroker: broker, hooks: new ContextHooks
        {
            BeforeInitialize = service => service.PackageSettleDelay = TimeSpan.FromHours(1),
        });
        Collect(ctx, events);
        var result = await ctx.Service.AddRemoteJobsAsync([RemoteFile(ctx, "1", "batch-a.bin"), RemoteFile(ctx, "2", "batch-b.bin")], "remote:batch");
        Assert.Null(result.Error);

        await WaitUntil(() => CountOf(events) == 1, "the batch's completion");
        Assert.Equal(2, events[0].JobIds.Count);
    }

    // ---- Keys in stored addresses ----

    [Fact]
    public async Task AnApiLinkIsTreatedAsTheSecretItIs()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server, autoResume: false);
        var link = $"https://api.torbox.app/v1/api/torrents/requestdl?token={LinkSecret}&torrent_id=1&file_id=2";
        var response = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = link,
            PageUrl = link,
            Referrer = link,
            FileName = "api-link.bin",
            DestinationDirectory = ctx.Destination,
            Source = "clipboardMonitor",
            StartMode = "later",
            Connections = 8,
        });
        Assert.True(response.Accepted, response.Error);
        Assert.DoesNotContain(LinkSecret, response.Job!.Url);
        Assert.DoesNotContain(LinkSecret, response.Job.PageUrl);
        Assert.Equal(Math.Min(8, ProviderLinks.MaxConnections), response.Job.RequestedConnections);
        Assert.Empty(ctx.Service.GetSnapshot().Settings.RecentUrls);
        Assert.DoesNotContain(LinkSecret, JsonSerializer.Serialize(ctx.Service.GetSnapshot()));
        await AssertSecretNotOnDiskAsync(ctx, LinkSecret);
        Assert.Contains("dpapi:v1:", await JobJsonAsync(ctx, response.Job.Id));
    }

    [Fact]
    public async Task SchedulerSynchronizationDoesNotStoreAProviderLinkItWasRedirectedTo()
    {
        const string name = "sync-redirected.bin";
        const int size = 65536;
        var handler = new CdnRedirectHandler(_server)
        {
            RedirectHost = "go.redirect.test",
            RedirectTo = $"{PastedHost}/file/{name}?size={size}&seed=807&token={LinkSecret}",
        };
        await using var ctx = await ServiceContext.CreateAsync(_server, handler: handler);
        var added = await AddToPackage(ctx, name, null, start: "startNow", url: "https://go.redirect.test/file");
        var completed = await ctx.WaitFor(added.Id, job => job.State == "completed");

        var settings = ctx.Service.GetSnapshot().Settings;
        settings.Schedules.Add(new ScheduleDto
        {
            Id = Guid.NewGuid(), Name = "Periodic sync", QueueId = 0, Enabled = true,
            Kind = "daily", Days = "Mon,Tue,Wed,Thu,Fri,Sat,Sun", StartTime = "10:00",
            RepeatEveryMinutes = 1,
        });
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);

        // A missing file counts as changed, so the job takes the path that stores the final address.
        File.Delete(completed.FinalFilePath);
        var run = new DateTimeOffset(2026, 11, 4, 10, 0, 0, TimeSpan.Zero);
        await ctx.Service.EvaluateSchedulesAsync(run);
        await ctx.Service.EvaluateSchedulesAsync(run.AddMinutes(1));

        var synced = await ctx.WaitFor(added.Id, job => job.State == "completed" && File.Exists(job.FinalFilePath), TimeSpan.FromSeconds(30));
        Assert.Null(synced.FinalUrl);
        await AssertSecretNotOnDiskAsync(ctx, LinkSecret);
    }

    [Fact]
    public async Task AJobSavedWithAPlainProviderLinkIsProtectedAtTheNextStart()
    {
        const string name = "from-the-old-build.bin";
        const int size = 65536;
        var destination = TestFolder(out var paths);
        var link = $"{PastedHost}/file/{name}?size={size}&seed=808&token={LinkSecret}";
        var job = new DownloadJob
        {
            Url = link,
            FinalUrl = link,
            PageUrl = link,
            Referrer = link,
            FileName = name,
            DestinationDirectory = destination,
            FinalFilePath = Path.Combine(destination, name),
            PartFilePath = Path.Combine(destination, name + ".part"),
            State = DownloadState.Paused,
            RequestedConnections = 8,
        };
        await new JsonJobStore(paths).SaveAsync(job);
        var checkpoint = new DownloadCheckpoint { JobId = job.Id, Url = link, FinalUrl = link };
        checkpoint.Segments.Add(new SegmentCheckpoint { Start = 0, End = long.MaxValue - 1, Completed = 0 });
        await new JsonCheckpointStore(paths).SaveAsync(checkpoint);

        var handler = new CdnRedirectHandler(_server);
        await using var ctx = await ServiceContext.CreateAsync(_server, paths, destination, autoResume: false, handler: handler);
        var restored = ctx.Service.GetJob(job.Id)!;
        Assert.DoesNotContain(LinkSecret, restored.Url);
        Assert.Null(restored.FinalUrl);
        Assert.DoesNotContain(LinkSecret, JsonSerializer.Serialize(ctx.Service.GetSnapshot()));
        await AssertSecretNotOnDiskAsync(ctx, LinkSecret);
        Assert.Contains("dpapi:v1:", await JobJsonAsync(ctx, job.Id));

        Assert.True((await ctx.Service.ResumeAsync(job.Id)).Ok);
        var completed = await ctx.WaitFor(job.Id, current => current.State == "completed");
        Assert.Equal(Sha256Of(808, size), await Hashing.Sha256FileAsync(completed.FinalFilePath));
        Assert.All(handler.TokensSeen, token => Assert.Equal(LinkSecret, token));
        Assert.NotEmpty(handler.TokensSeen);
        Assert.InRange(completed.RequestedConnections, 1, ProviderLinks.MaxConnections);
    }

    [Fact]
    public async Task RecentAddressesSavedWithAKeyLeaveTheDiskAtLoad()
    {
        TestFolder(out var paths);
        var store = new JsonSettingsStore(paths);
        await store.SaveAsync(new AppSettings());
        var settingsFile = paths.SettingsFile;
        var stored = JsonNode.Parse(await File.ReadAllTextAsync(settingsFile))!;
        stored["recentUrls"] = new JsonArray($"{PastedHost}/a.bin?token={LinkSecret}", "https://example.test/keep.bin");
        await File.WriteAllTextAsync(settingsFile, stored.ToJsonString());

        var loaded = await new JsonSettingsStore(paths).LoadAsync();
        Assert.Equal(new[] { "https://example.test/keep.bin" }, loaded.RecentUrls);
        var onDisk = await File.ReadAllTextAsync(settingsFile);
        Assert.DoesNotContain(LinkSecret, onDisk);
        Assert.Contains("keep.bin", onDisk);
    }
}
