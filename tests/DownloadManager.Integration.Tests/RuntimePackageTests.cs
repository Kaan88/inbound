using DownloadManager.Contracts;
using DownloadManager.Core;
using DownloadManager.Persistence;
using DownloadManager.Runtime;

namespace DownloadManager.Integration.Tests;

public sealed partial class RuntimeServiceTests
{
    /// <summary>A settings store whose saves can be made to fail, as a full or locked disk would.</summary>
    private sealed class FlakySettingsStore(ISettingsStore inner) : ISettingsStore
    {
        private int _saves;
        private int _failedSaves;
        public volatile bool FailSaves;
        public int Saves => Volatile.Read(ref _saves);
        public int FailedSaves => Volatile.Read(ref _failedSaves);

        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) => inner.LoadAsync(cancellationToken);

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _saves);
            if (FailSaves)
            {
                Interlocked.Increment(ref _failedSaves);
                throw new IOException("The disk refused the write.");
            }

            return inner.SaveAsync(settings, cancellationToken);
        }
    }

    /// <summary>Holds the first request past cancellation, like a transfer that is slow to wind down.</summary>
    private sealed class StallFirstRequestHandler() : DelegatingHandler(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false })
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _requests;

        public Task Entered => _entered.Task;

        public void Release() => _release.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _requests) == 1)
            {
                _entered.TrySetResult();
                await _release.Task.ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }

            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<(ServiceContext Context, FlakySettingsStore Store)> CreateWithFlakyStoreAsync(
        TestServer.TestServerHost server,
        List<string>? log = null,
        int maxConcurrent = 2)
    {
        FlakySettingsStore? flaky = null;
        var ctx = await ServiceContext.CreateAsync(server, autoResume: false, maxConcurrent: maxConcurrent, hooks: new ContextHooks
        {
            WrapSettings = store => flaky = new FlakySettingsStore(store),
            Log = log is null ? null : message => { lock (log) log.Add(message); },
        });
        return (ctx, flaky!);
    }

    private async Task<JobDto> AddToPackage(
        ServiceContext ctx,
        string name,
        string? key,
        string start = "later",
        string? url = null,
        string? owner = null,
        string? requestId = null,
        int? priority = null)
    {
        var response = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = url ?? $"{_server.BaseAddress}/file/{name}?size=2048&seed=7",
            DestinationDirectory = ctx.Destination,
            FileName = name,
            Source = "cli",
            StartMode = start,
            PackageKey = key,
            OwnerExtensionId = owner,
            ClientRequestId = requestId,
            Priority = priority,
        });
        Assert.True(response.Accepted, response.Error);
        return response.Job!;
    }

    private static DownloadGroupDto Package(ServiceContext ctx, string key)
        => Assert.Single(ctx.Service.GetSnapshot().Settings.DownloadGroups, group => group.Key == "batch:" + key);

    private static string StateOf(ServiceContext ctx, Guid id) => ctx.Service.GetJob(id)!.State;

    private static List<JobDto> Children(ServiceContext ctx, DownloadGroupDto group)
    {
        var jobs = ctx.Service.GetSnapshot().Jobs;
        return group.JobIds.Select(id => jobs.Single(job => job.Id == id)).ToList();
    }

    private static async Task WaitUntil(Func<bool> condition, string what, TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
        while (!condition())
        {
            if (DateTimeOffset.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for {what}.");
            await Task.Delay(25);
        }
    }

    /// <summary>Lets a late, unwanted event arrive so a count of it means something.</summary>
    private static Task SettleEvents() => Task.Delay(300);

    private static ContextHooks CollectCompletions(List<PackageCompletedEventArgs> into) => new()
    {
        BeforeInitialize = service => service.PackageCompleted += (_, completed) => { lock (into) into.Add(completed); },
    };

    private static void Collect(ServiceContext ctx, List<PackageCompletedEventArgs> into)
        => ctx.Service.PackageCompleted += (_, completed) => { lock (into) into.Add(completed); };

    private static int CountOf(List<PackageCompletedEventArgs> events)
    {
        lock (events) return events.Count;
    }

    // ---- Parent pause, resume, cancel ----

    [Fact]
    public async Task PackageControlsHandleMixedChildrenAndResumeLeavesAFileCancelledOnItsOwnAlone()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server, autoResume: false);
        var done = await AddToPackage(ctx, "done.bin", "mixed", start: "startNow");
        done = await ctx.WaitFor(done.Id, job => job.State == "completed");
        var failed = await AddToPackage(ctx, "failed.bin", "mixed", start: "startNow", url: $"{_server.BaseAddress}/error/404");
        await ctx.WaitFor(failed.Id, job => job.State == "failed");
        var cancelled = await AddToPackage(ctx, "cancelled.bin", "mixed");
        Assert.True((await ctx.Service.CancelAsync(cancelled.Id, deleteFile: false)).Ok);
        var paused = await AddToPackage(ctx, "paused.bin", "mixed");
        Assert.True((await ctx.Service.PauseAsync(paused.Id)).Ok);
        var waiting = await AddToPackage(ctx, "waiting.bin", "mixed");
        var group = Package(ctx, "mixed");

        var pause = await ctx.Service.GroupActionAsync(new GroupActionRequest { Action = "pause", GroupId = group.Id });
        Assert.True(pause.Ok, pause.Error);
        Assert.Equal(1, pause.Changed);
        Assert.Equal("paused", StateOf(ctx, waiting.Id));
        Assert.Equal("completed", StateOf(ctx, done.Id));
        Assert.Equal("failed", StateOf(ctx, failed.Id));
        Assert.Equal("cancelled", StateOf(ctx, cancelled.Id));

        var resume = await ctx.Service.GroupActionAsync(new GroupActionRequest { Action = "resume", GroupId = group.Id });
        Assert.True(resume.Ok, resume.Error);
        Assert.Equal(3, resume.Changed);
        Assert.Equal(1, resume.LeftCancelled);
        await ctx.WaitFor(paused.Id, job => job.State == "completed");
        await ctx.WaitFor(waiting.Id, job => job.State == "completed");
        await ctx.WaitFor(failed.Id, job => job.State == "failed");
        Assert.Equal("cancelled", StateOf(ctx, cancelled.Id));
        Assert.Null(ctx.Service.GetJob(cancelled.Id)!.CancelledByPackageId);
        Assert.Equal(done.CompletedAt, ctx.Service.GetJob(done.Id)!.CompletedAt);

        var cancel = await ctx.Service.GroupActionAsync(new GroupActionRequest { Action = "cancel", GroupId = group.Id });
        Assert.True(cancel.Ok, cancel.Error);
        Assert.Equal(1, cancel.Changed);
        Assert.Equal(group.Id, ctx.Service.GetJob(failed.Id)!.CancelledByPackageId);
        Assert.Equal("completed", StateOf(ctx, done.Id));
        Assert.True(File.Exists(done.FinalFilePath));

        // Not every file was stopped by that cancel, so Resume has nothing it may revive.
        var noUndo = await ctx.Service.GroupActionAsync(new GroupActionRequest { Action = "resume", GroupId = group.Id });
        Assert.True(noUndo.Ok, noUndo.Error);
        Assert.Equal(0, noUndo.Changed);
        Assert.Equal(2, noUndo.LeftCancelled);
        Assert.Equal("cancelled", StateOf(ctx, failed.Id));
    }

    [Fact]
    public async Task ResumeUndoesAPackageCancelThatStoppedEveryFile()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server, autoResume: false);
        var first = await AddToPackage(ctx, "undo-1.bin", "undo");
        var second = await AddToPackage(ctx, "undo-2.bin", "undo");
        var group = Package(ctx, "undo");

        var cancel = await ctx.Service.GroupActionAsync(new GroupActionRequest { Action = "cancel", GroupId = group.Id });
        Assert.True(cancel.Ok, cancel.Error);
        Assert.Equal(2, cancel.Changed);
        Assert.All(Children(ctx, group), child => Assert.Equal(group.Id, child.CancelledByPackageId));

        var again = await ctx.Service.GroupActionAsync(new GroupActionRequest { Action = "cancel", GroupId = group.Id });
        Assert.Equal(0, again.Changed);

        var resume = await ctx.Service.GroupActionAsync(new GroupActionRequest { Action = "resume", GroupId = group.Id });
        Assert.True(resume.Ok, resume.Error);
        Assert.Equal(2, resume.Changed);
        Assert.Equal(0, resume.LeftCancelled);
        await ctx.WaitFor(first.Id, job => job.State == "completed" && job.CancelledByPackageId is null);
        await ctx.WaitFor(second.Id, job => job.State == "completed" && job.CancelledByPackageId is null);
    }

    [Fact]
    public async Task AnIndividualCancelBlocksUndoEvenWhenEveryFileEndsUpCancelled()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server, autoResume: false);
        var before = await AddToPackage(ctx, "before.bin", "before");
        var other = await AddToPackage(ctx, "other.bin", "before");
        Assert.True((await ctx.Service.CancelAsync(before.Id, deleteFile: false)).Ok);
        var beforeGroup = Package(ctx, "before");
        Assert.True((await ctx.Service.GroupActionAsync(new GroupActionRequest { Action = "cancel", GroupId = beforeGroup.Id })).Ok);
        var resume = await ctx.Service.GroupActionAsync(new GroupActionRequest { Action = "resume", GroupId = beforeGroup.Id });
        Assert.Equal(0, resume.Changed);
        Assert.Equal(2, resume.LeftCancelled);
        Assert.Equal("cancelled", StateOf(ctx, before.Id));
        Assert.Equal("cancelled", StateOf(ctx, other.Id));

        // Cancelling a file by itself after the package replaces the package's cancel.
        var after = await AddToPackage(ctx, "after.bin", "after");
        await AddToPackage(ctx, "after-2.bin", "after");
        var afterGroup = Package(ctx, "after");
        Assert.True((await ctx.Service.GroupActionAsync(new GroupActionRequest { Action = "cancel", GroupId = afterGroup.Id })).Ok);
        Assert.True((await ctx.Service.CancelAsync(after.Id, deleteFile: false)).Ok);
        Assert.Equal(0, (await ctx.Service.GroupActionAsync(new GroupActionRequest { Action = "resume", GroupId = afterGroup.Id })).Changed);

        // A cancel made in another package does not count for this one.
        var mover = await AddToPackage(ctx, "mover.bin", "from");
        await AddToPackage(ctx, "stays.bin", "from");
        var fromGroup = Package(ctx, "from");
        Assert.True((await ctx.Service.GroupActionAsync(new GroupActionRequest { Action = "cancel", GroupId = fromGroup.Id })).Ok);
        var created = await ctx.Service.GroupActionAsync(new GroupActionRequest { Action = "create", Name = "Target", JobIds = [mover.Id] });
        Assert.True(created.Ok, created.Error);
        Assert.Equal(0, (await ctx.Service.GroupActionAsync(new GroupActionRequest { Action = "resume", GroupId = created.GroupId })).Changed);
    }

    [Fact]
    public async Task CancellingARunningPackageStartsNoOtherFileAndResumeContinuesFromPartialData()
    {
        const int size = 1_048_576;
        await using var ctx = await ServiceContext.CreateAsync(_server, autoResume: false, maxConcurrent: 1);
        var running = await AddToPackage(ctx, "running.bin", "running", start: "startNow",
            url: $"{_server.BaseAddress}/slow/running.bin?size={size}&seed=31&bps=524288");
        var waiting = new List<JobDto>();
        for (var index = 0; index < 8; index++)
        {
            waiting.Add(await AddToPackage(ctx, $"next-{index}.bin", "running", start: "startNow"));
        }

        await ctx.WaitFor(running.Id, job => job.State == "downloading" && job.ReceivedBytes > 0);
        var group = Package(ctx, "running");

        var cancel = await ctx.Service.GroupActionAsync(new GroupActionRequest { Action = "cancel", GroupId = group.Id });
        Assert.True(cancel.Ok, cancel.Error);
        Assert.Equal(9, cancel.Changed);
        Assert.All(waiting, job => Assert.Null(ctx.Service.GetJob(job.Id)!.StartedAt));

        var resume = await ctx.Service.GroupActionAsync(new GroupActionRequest { Action = "resume", GroupId = group.Id });
        Assert.True(resume.Ok, resume.Error);
        Assert.Equal(9, resume.Changed);
        var finished = await ctx.WaitFor(running.Id, job => job.State == "completed", TimeSpan.FromSeconds(30));
        foreach (var job in waiting)
        {
            await ctx.WaitFor(job.Id, current => current.State == "completed");
        }

        Assert.Equal(size, new FileInfo(finished.FinalFilePath).Length);
    }

    [Fact]
    public async Task ResumeWaitsForACancelledTransferThatIsStillWindingDown()
    {
        using var handler = new StallFirstRequestHandler();
        await using var ctx = await ServiceContext.CreateAsync(_server, autoResume: false, handler: handler);
        var job = await AddToPackage(ctx, "slow-stop.bin", "slow-stop", start: "startNow");
        await handler.Entered;
        var group = Package(ctx, "slow-stop");

        var cancel = await ctx.Service.GroupActionAsync(new GroupActionRequest { Action = "cancel", GroupId = group.Id });
        Assert.True(cancel.Ok, cancel.Error);
        Assert.Equal("cancelled", StateOf(ctx, job.Id));

        var resuming = ctx.Service.GroupActionAsync(new GroupActionRequest { Action = "resume", GroupId = group.Id });
        await Task.Delay(150);
        Assert.False(resuming.IsCompleted);
        handler.Release();
        var resume = await resuming;
        Assert.True(resume.Ok, resume.Error);
        Assert.Equal(1, resume.Changed);
        await ctx.WaitFor(job.Id, current => current.State == "completed");
    }

    // ---- Membership ----

    [Fact]
    public async Task ReAddAfterACrashBetweenJobSaveAndPackageSaveRestoresMembership()
    {
        var (ctx, store) = await CreateWithFlakyStoreAsync(_server);
        await using (ctx)
        {
            var first = await AddToPackage(ctx, "crash-1.bin", "crash", owner: "tester", requestId: "1");
            store.FailSaves = true;
            var second = await AddToPackage(ctx, "crash-2.bin", "crash", owner: "tester", requestId: "2");
            Assert.DoesNotContain(second.Id, Package(ctx, "crash").JobIds);

            // Gone with the process: the job file is durable, the unsaved membership is not.
            await ctx.Service.ShutdownAsync();
            await using var restarted = await ServiceContext.CreateAsync(_server, ctx.Paths, ctx.Destination, autoResume: false);
            Assert.DoesNotContain(second.Id, Package(restarted, "crash").JobIds);

            var again = await AddToPackage(restarted, "crash-2.bin", "crash", owner: "tester", requestId: "2");
            Assert.Equal(second.Id, again.Id);
            Assert.Equal(new[] { first.Id, second.Id }, Package(restarted, "crash").JobIds);

            // Already a member: repeating it changes nothing.
            await AddToPackage(restarted, "crash-2.bin", "crash", owner: "tester", requestId: "2");
            Assert.Equal(new[] { first.Id, second.Id }, Package(restarted, "crash").JobIds);
            await restarted.Service.ShutdownAsync();
            await using var again2 = await ServiceContext.CreateAsync(_server, ctx.Paths, ctx.Destination, autoResume: false);
            Assert.Equal(new[] { first.Id, second.Id }, Package(again2, "crash").JobIds);
        }
    }

    [Fact]
    public async Task AFailedPackageSaveIsLoggedAndTheNextAddSavesIt()
    {
        var log = new List<string>();
        var (ctx, store) = await CreateWithFlakyStoreAsync(_server, log);
        await using (ctx)
        {
            var first = await AddToPackage(ctx, "retry-1.bin", "retry");
            store.FailSaves = true;
            var second = await AddToPackage(ctx, "retry-2.bin", "retry");
            Assert.Equal(new[] { first.Id }, Package(ctx, "retry").JobIds);
            lock (log) Assert.Contains(log, line => line.Contains("Could not save packages") && line.Contains("refused"));

            store.FailSaves = false;
            await AddToPackage(ctx, "unrelated.bin", null);
            Assert.Equal(new[] { first.Id, second.Id }, Package(ctx, "retry").JobIds);
        }
    }

    [Fact]
    public async Task RemovingAJobOnlyDissolvesItsOwnPackage()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server, autoResume: false);
        var doomed = await AddToPackage(ctx, "doomed.bin", "own");
        var survivor = await AddToPackage(ctx, "survivor.bin", "own");
        var ownId = Package(ctx, "own").Id;
        var firstOfBatch = await AddToPackage(ctx, "batch-1.bin", "still-adding");

        var removed = await ctx.Service.RemoveAsync(doomed.Id, deleteFile: false);
        Assert.True(removed.Ok, removed.Error);
        Assert.DoesNotContain(ctx.Service.GetSnapshot().Settings.DownloadGroups, group => group.Id == ownId);
        Assert.NotNull(ctx.Service.GetJob(survivor.Id));
        Assert.Equal(new[] { firstOfBatch.Id }, Package(ctx, "still-adding").JobIds);

        var secondOfBatch = await AddToPackage(ctx, "batch-2.bin", "still-adding");
        Assert.Equal(new[] { firstOfBatch.Id, secondOfBatch.Id }, Package(ctx, "still-adding").JobIds);
    }

    // ---- Queue ----

    [Fact]
    public async Task PackageQueueActionsCoverEveryChildAndTheColumnShowsSharedOrMixed()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server, autoResume: false);
        var created = await ctx.Service.QueueActionAsync(new QueueActionRequest { Action = "create", Name = "Second", MaxConcurrentDownloads = 2 });
        Assert.True(created.Ok, created.Error);
        var second = created.Settings.Queues.Single(queue => queue.Name == "Second");

        var done = await AddToPackage(ctx, "q-done.bin", "queues", start: "startNow");
        await ctx.WaitFor(done.Id, job => job.State == "completed");
        var waiting = await AddToPackage(ctx, "q-waiting.bin", "queues");
        var group = Package(ctx, "queues");
        Assert.Equal("1", PackageRules.QueueText(Children(ctx, group)));

        var move = await ctx.Service.GroupActionAsync(new GroupActionRequest { Action = "queue", GroupId = group.Id, QueueId = second.Id });
        Assert.True(move.Ok, move.Error);
        Assert.Equal(2, move.Changed);
        Assert.All(Children(ctx, group), child =>
        {
            Assert.True(child.IsQueued);
            Assert.Equal(second.Id, child.QueueId);
            Assert.Equal("Second", child.QueueName);
        });
        Assert.Equal((second.Id + 1).ToString(), PackageRules.QueueText(Children(ctx, group)));
        Assert.Equal(second.Id, PackageRules.SharedQueueId(Children(ctx, group)));

        Assert.True((await ctx.Service.DetachFromQueueAsync(waiting.Id)).Ok);
        Assert.Equal(PackageRules.MixedQueueMarker, PackageRules.QueueText(Children(ctx, group)));
        Assert.Null(PackageRules.SharedQueueId(Children(ctx, group)));

        var detach = await ctx.Service.GroupActionAsync(new GroupActionRequest { Action = "unqueue", GroupId = group.Id });
        Assert.True(detach.Ok, detach.Error);
        Assert.All(Children(ctx, group), child => Assert.False(child.IsQueued));
        Assert.Equal(string.Empty, PackageRules.QueueText(Children(ctx, group)));

        var unknown = await ctx.Service.GroupActionAsync(new GroupActionRequest { Action = "queue", GroupId = group.Id, QueueId = 999 });
        Assert.False(unknown.Ok);
        var missing = await ctx.Service.GroupActionAsync(new GroupActionRequest { Action = "queue", GroupId = group.Id });
        Assert.False(missing.Ok);
    }

    // ---- Priority ----

    [Fact]
    public async Task PriorityDecidesWhichFileTheSchedulerStartsFirstAndSurvivesARestart()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server, autoResume: false, maxConcurrent: 1);
        Assert.True((await ctx.Service.QueueActionAsync(new QueueActionRequest { QueueId = 0, Action = "stop" })).Ok);
        var lowered = await AddToPackage(ctx, "lowered.bin", null, start: "startNow");
        var plain = await AddToPackage(ctx, "plain.bin", null, start: "startNow");
        var raised = await AddToPackage(ctx, "raised.bin", null, start: "startNow");
        var addedHigh = await AddToPackage(ctx, "added-high.bin", null, start: "startNow", priority: JobPriorities.High);
        Assert.Equal(JobPriorities.High, addedHigh.Priority);

        Assert.True((await ctx.Service.UpdateJobAsync(new JobUpdateRequest { JobId = raised.Id, Priority = JobPriorities.High })).Ok);
        Assert.True((await ctx.Service.UpdateJobAsync(new JobUpdateRequest { JobId = lowered.Id, Priority = JobPriorities.Low })).Ok);

        await ctx.Service.ShutdownAsync();
        await using var restarted = await ServiceContext.CreateAsync(_server, ctx.Paths, ctx.Destination, autoResume: false, maxConcurrent: 1);
        Assert.Equal(JobPriorities.High, restarted.Service.GetJob(raised.Id)!.Priority);
        Assert.Equal(JobPriorities.Low, restarted.Service.GetJob(lowered.Id)!.Priority);

        var order = new List<Guid>();
        restarted.Service.JobChanged += (_, change) =>
        {
            if (change.Job.State != "connecting") return;
            lock (order) if (!order.Contains(change.Job.Id)) order.Add(change.Job.Id);
        };
        Assert.True((await restarted.Service.QueueActionAsync(new QueueActionRequest { QueueId = 0, Action = "start" })).Ok);
        foreach (var job in new[] { lowered, plain, raised, addedHigh })
        {
            await restarted.WaitFor(job.Id, current => current.State == "completed");
        }

        lock (order) Assert.Equal(new[] { raised.Id, addedHigh.Id, plain.Id, lowered.Id }, order);
    }

    [Fact]
    public async Task PackagePriorityRaisesItsUnfinishedFilesPastEarlierOnes()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server, autoResume: false, maxConcurrent: 1);
        var done = await AddToPackage(ctx, "p-done.bin", "prio", start: "startNow");
        await ctx.WaitFor(done.Id, job => job.State == "completed");
        Assert.True((await ctx.Service.QueueActionAsync(new QueueActionRequest { QueueId = 0, Action = "stop" })).Ok);
        var earlier = await AddToPackage(ctx, "p-earlier.bin", null, start: "startNow");
        var child = await AddToPackage(ctx, "p-child.bin", "prio", start: "startNow");
        var group = Package(ctx, "prio");

        var raise = await ctx.Service.GroupActionAsync(new GroupActionRequest { Action = "priority", GroupId = group.Id, Priority = JobPriorities.High });
        Assert.True(raise.Ok, raise.Error);
        Assert.Equal(1, raise.Changed);
        Assert.Equal(JobPriorities.Normal, ctx.Service.GetJob(done.Id)!.Priority);
        Assert.Equal(JobPriorities.High, ctx.Service.GetJob(child.Id)!.Priority);

        // One child can still differ from the package.
        Assert.True((await ctx.Service.UpdateJobAsync(new JobUpdateRequest { JobId = child.Id, Priority = JobPriorities.Normal })).Ok);
        Assert.True((await ctx.Service.UpdateJobAsync(new JobUpdateRequest { JobId = child.Id, Priority = JobPriorities.High })).Ok);

        var order = new List<Guid>();
        ctx.Service.JobChanged += (_, change) =>
        {
            if (change.Job.State != "connecting") return;
            lock (order) if (!order.Contains(change.Job.Id)) order.Add(change.Job.Id);
        };
        Assert.True((await ctx.Service.QueueActionAsync(new QueueActionRequest { QueueId = 0, Action = "start" })).Ok);
        await ctx.WaitFor(child.Id, job => job.State == "completed");
        await ctx.WaitFor(earlier.Id, job => job.State == "completed");
        lock (order) Assert.Equal(new[] { child.Id, earlier.Id }, order);
    }

    [Fact]
    public async Task InvalidOrPointlessPrioritiesAreRefused()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server, autoResume: false);
        var rejected = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/file/bad-priority.bin?size=2048&seed=7",
            DestinationDirectory = ctx.Destination,
            FileName = "bad-priority.bin",
            Priority = 5,
        });
        Assert.False(rejected.Accepted);
        Assert.Equal("invalid_priority", rejected.ErrorCode);

        var done = await AddToPackage(ctx, "finished.bin", "bad", start: "startNow");
        await ctx.WaitFor(done.Id, job => job.State == "completed");
        Assert.False((await ctx.Service.UpdateJobAsync(new JobUpdateRequest { JobId = done.Id, Priority = JobPriorities.High })).Ok);

        var waiting = await AddToPackage(ctx, "waiting-p.bin", "bad");
        Assert.False((await ctx.Service.UpdateJobAsync(new JobUpdateRequest { JobId = waiting.Id, Priority = 7 })).Ok);
        Assert.Equal(JobPriorities.Normal, ctx.Service.GetJob(waiting.Id)!.Priority);
        Assert.False((await ctx.Service.GroupActionAsync(new GroupActionRequest { Action = "priority", GroupId = Package(ctx, "bad").Id, Priority = 7 })).Ok);
    }

    // ---- Expected sizes and bulk add ----

    [Fact]
    public async Task AQueuedPackageShowsTotalsFromExpectedSizesBeforeAnyFileConnects()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server, autoResume: false);
        var result = await ctx.Service.AddRemoteJobsAsync(
        [
            RemoteFile(ctx, "1", "a.bin", size: 1000) with { StartMode = "later" },
            RemoteFile(ctx, "2", "b.bin", size: 500) with { StartMode = "later" },
        ], "remote:sizes", "Sizes");
        Assert.Null(result.Error);

        var summary = DownloadGroupSummary.Create(Children(ctx, Package(ctx, "remote:sizes")));
        Assert.Equal("queued", summary.State);
        Assert.Equal(1500, summary.TotalBytes);
        Assert.Equal(0d, summary.Progress);

        await ctx.Service.ShutdownAsync();
        await using var restarted = await ServiceContext.CreateAsync(_server, ctx.Paths, ctx.Destination, autoResume: false);
        Assert.Equal(1500, DownloadGroupSummary.Create(Children(restarted, Package(restarted, "remote:sizes"))).TotalBytes);
    }

    [Fact]
    public async Task ABatchOfProviderFilesCommitsItsPackageOnce()
    {
        var (ctx, store) = await CreateWithFlakyStoreAsync(_server);
        await using (ctx)
        {
            var requests = Enumerable.Range(1, 50)
                .Select(index => RemoteFile(ctx, index.ToString(), $"bulk-{index}.bin", size: 100) with { StartMode = "later" })
                .ToList();
            var before = store.Saves;
            var result = await ctx.Service.AddRemoteJobsAsync(requests, "remote:bulk", "Bulk");
            Assert.Null(result.Error);
            Assert.Equal(1, store.Saves - before);
            Assert.Equal(50, Package(ctx, "remote:bulk").JobIds.Count);

            // Repeating the batch finds nothing new to save.
            before = store.Saves;
            var repeat = await ctx.Service.AddRemoteJobsAsync(requests, "remote:bulk", "Bulk");
            Assert.Null(repeat.Error);
            Assert.All(repeat.Items, item => Assert.True(item.Duplicate));
            Assert.Equal(0, store.Saves - before);
        }
    }

    [Fact]
    public async Task AddsThatWouldOverfillAPackageFailBeforeAnyJobIsCreated()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server, autoResume: false);
        var tooMany = Enumerable.Range(1, 1001)
            .Select(index => RemoteFile(ctx, index.ToString(), $"over-{index}.bin"))
            .ToList();
        var oversized = await ctx.Service.AddRemoteJobsAsync(tooMany, "remote:over");
        Assert.Contains("at most 1000", oversized.Error);
        Assert.Empty(ctx.Service.GetSnapshot().Jobs);

        await ctx.Service.ShutdownAsync();
        var settingsStore = new JsonSettingsStore(ctx.Paths);
        var settings = await settingsStore.LoadAsync();
        settings.DownloadGroups.Add(new DownloadGroup
        {
            Name = "Nearly full",
            Key = "batch:full",
            CompletionTracked = true,
            JobIds = Enumerable.Range(0, 999).Select(_ => Guid.NewGuid()).ToList(),
        });
        await settingsStore.SaveAsync(settings);
        await using var restarted = await ServiceContext.CreateAsync(_server, ctx.Paths, ctx.Destination, autoResume: false);

        var two = await restarted.Service.AddRemoteJobsAsync(
            [RemoteFile(restarted, "1", "one.bin") with { StartMode = "later" }, RemoteFile(restarted, "2", "two.bin") with { StartMode = "later" }], "full");
        Assert.Contains("at most 1000", two.Error);
        Assert.Empty(restarted.Service.GetSnapshot().Jobs);

        var one = await restarted.Service.AddRemoteJobsAsync([RemoteFile(restarted, "1", "one.bin") with { StartMode = "later" }], "full");
        Assert.Null(one.Error);
        Assert.Equal(1000, Package(restarted, "full").JobIds.Count);

        // A full package still accepts a repeat of what it holds, and refuses anything new.
        var repeat = await restarted.Service.AddRemoteJobsAsync([RemoteFile(restarted, "1", "one.bin") with { StartMode = "later" }], "full");
        Assert.Null(repeat.Error);
        Assert.True(Assert.Single(repeat.Items).Duplicate);

        var refused = await restarted.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/file/too-late.bin?size=2048&seed=7",
            DestinationDirectory = restarted.Destination,
            FileName = "too-late.bin",
            StartMode = "later",
            PackageKey = "full",
        });
        Assert.False(refused.Accepted);
        Assert.Equal("package_full", refused.ErrorCode);
        Assert.Single(restarted.Service.GetSnapshot().Jobs);
    }

    // ---- Completion ----

    [Fact]
    public async Task PackageCompletedFiresOncePerCompletionAndARestartDoesNotRepeatIt()
    {
        var events = new List<PackageCompletedEventArgs>();
        await using var ctx = await ServiceContext.CreateAsync(_server, autoResume: false);
        Collect(ctx, events);
        var first = await AddToPackage(ctx, "c-1.bin", "complete", start: "startNow");
        await ctx.WaitFor(first.Id, job => job.State == "completed");
        await WaitUntil(() => CountOf(events) == 1, "the one-file completion");
        var second = await AddToPackage(ctx, "c-2.bin", "complete", start: "startNow");
        await ctx.WaitFor(second.Id, job => job.State == "completed");
        await WaitUntil(() => CountOf(events) == 2, "the second completion");
        await SettleEvents();
        Assert.Equal(2, CountOf(events));
        var group = Package(ctx, "complete");
        var announced = events[1];
        Assert.Equal(group.Id, announced.PackageId);
        Assert.Equal(new[] { first.Id, second.Id }, announced.JobIds);
        Assert.Equal(ctx.Service.GetJob(second.Id)!.CompletedAt, announced.CompletedAt);
        Assert.True(ctx.Service.IsPackageComplete(group.Id));

        await ctx.Service.ShutdownAsync();
        var afterRestart = new List<PackageCompletedEventArgs>();
        await using var restarted = await ServiceContext.CreateAsync(_server, ctx.Paths, ctx.Destination, autoResume: false,
            hooks: CollectCompletions(afterRestart));
        await SettleEvents();
        Assert.Equal(0, CountOf(afterRestart));

        // What a subscriber that starts later sweeps: the same completion, with the same identity.
        var swept = Assert.Single(restarted.Service.GetCompletedPackages());
        Assert.Equal(announced.PackageId, swept.PackageId);
        Assert.Equal(announced.CompletedAt, swept.CompletedAt);

        var third = await AddToPackage(restarted, "c-3.bin", "complete");
        Assert.Empty(restarted.Service.GetCompletedPackages());
        Assert.False(restarted.Service.IsPackageComplete(group.Id));
        Assert.True((await restarted.Service.ResumeAsync(third.Id)).Ok);
        await restarted.WaitFor(third.Id, job => job.State == "completed");
        await WaitUntil(() => CountOf(afterRestart) == 1, "the new completion");
        Assert.True(afterRestart[0].CompletedAt > announced.CompletedAt);
    }

    [Fact]
    public async Task ACrashBeforeThePackageSaveIsAnnouncedOnceAtTheNextStart()
    {
        var (ctx, store) = await CreateWithFlakyStoreAsync(_server);
        var events = new List<PackageCompletedEventArgs>();
        await using (ctx)
        {
            Collect(ctx, events);
            var first = await AddToPackage(ctx, "gap-1.bin", "gap");
            var second = await AddToPackage(ctx, "gap-2.bin", "gap");
            store.FailSaves = true;
            Assert.True((await ctx.Service.ResumeAsync(first.Id)).Ok);
            Assert.True((await ctx.Service.ResumeAsync(second.Id)).Ok);
            await ctx.WaitFor(first.Id, job => job.State == "completed");
            await ctx.WaitFor(second.Id, job => job.State == "completed");
            await WaitUntil(() => store.FailedSaves > 0, "the refused package save");
            await SettleEvents();
            Assert.Equal(0, CountOf(events));
            Assert.Empty(ctx.Service.GetCompletedPackages());

            await ctx.Service.ShutdownAsync();
            var atStart = new List<PackageCompletedEventArgs>();
            await using (var restarted = await ServiceContext.CreateAsync(_server, ctx.Paths, ctx.Destination, autoResume: false,
                hooks: CollectCompletions(atStart)))
            {
                Assert.Equal(1, CountOf(atStart));
                Assert.Single(restarted.Service.GetCompletedPackages());
            }

            var later = new List<PackageCompletedEventArgs>();
            await using var again = await ServiceContext.CreateAsync(_server, ctx.Paths, ctx.Destination, autoResume: false,
                hooks: CollectCompletions(later));
            await SettleEvents();
            Assert.Equal(0, CountOf(later));
        }
    }

    [Fact]
    public async Task PackagesFinishedBeforeCompletionsWereTrackedAreNotAnnounced()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server, autoResume: false);
        var first = await AddToPackage(ctx, "old-1.bin", "old", start: "startNow");
        var second = await AddToPackage(ctx, "old-2.bin", "old", start: "startNow");
        await ctx.WaitFor(first.Id, job => job.State == "completed");
        await ctx.WaitFor(second.Id, job => job.State == "completed");
        await WaitUntil(() => ctx.Service.GetCompletedPackages().Count == 1, "the completion");
        await ctx.Service.ShutdownAsync();

        var settingsStore = new JsonSettingsStore(ctx.Paths);
        var settings = await settingsStore.LoadAsync();
        var saved = settings.DownloadGroups.Single(group => group.Key == "batch:old");
        saved.CompletedAt = null;
        saved.CompletionTracked = false;
        await settingsStore.SaveAsync(settings);

        var events = new List<PackageCompletedEventArgs>();
        await using var restarted = await ServiceContext.CreateAsync(_server, ctx.Paths, ctx.Destination, autoResume: false,
            hooks: CollectCompletions(events));
        await SettleEvents();
        Assert.Equal(0, CountOf(events));
        Assert.Single(restarted.Service.GetCompletedPackages());
        var reloaded = (await settingsStore.LoadAsync()).DownloadGroups.Single(group => group.Key == "batch:old");
        Assert.True(reloaded.CompletionTracked);
        Assert.NotNull(reloaded.CompletedAt);

        // From here on the package behaves like any other.
        var third = await AddToPackage(restarted, "old-3.bin", "old", start: "startNow");
        await restarted.WaitFor(third.Id, job => job.State == "completed");
        await WaitUntil(() => CountOf(events) == 1, "a real completion");
    }

    [Fact]
    public async Task RemovingTheLastUnfinishedFileCompletesThePackageAndRemovedIdsDoNotCountAsFinished()
    {
        var events = new List<PackageCompletedEventArgs>();
        await using var ctx = await ServiceContext.CreateAsync(_server, autoResume: false);
        Collect(ctx, events);
        // Added before any of them runs, so the package always has unfinished work until the removal.
        var first = await AddToPackage(ctx, "rm-1.bin", "rm");
        var second = await AddToPackage(ctx, "rm-2.bin", "rm");
        var stuck = await AddToPackage(ctx, "rm-stuck.bin", "rm", url: $"{_server.BaseAddress}/error/404");
        foreach (var job in new[] { stuck, first, second }) Assert.True((await ctx.Service.ResumeAsync(job.Id)).Ok);
        await ctx.WaitFor(first.Id, job => job.State == "completed");
        await ctx.WaitFor(second.Id, job => job.State == "completed");
        await ctx.WaitFor(stuck.Id, job => job.State == "failed");
        var group = Package(ctx, "rm");
        Assert.False(ctx.Service.IsPackageComplete(group.Id));
        await SettleEvents();
        Assert.Equal(0, CountOf(events));

        Assert.True((await ctx.Service.RemoveAsync(stuck.Id, deleteFile: false)).Ok);
        await WaitUntil(() => CountOf(events) == 1, "the completion after removal");
        await SettleEvents();
        Assert.Equal(1, CountOf(events));
        Assert.True(ctx.Service.IsPackageComplete(group.Id));

        // Removing a finished file from a finished package is not a new completion.
        var third = await AddToPackage(ctx, "rm-3.bin", "rm", start: "startNow");
        await ctx.WaitFor(third.Id, job => job.State == "completed");
        await WaitUntil(() => CountOf(events) == 2, "the completion with the third file");
        Assert.True((await ctx.Service.RemoveAsync(third.Id, deleteFile: false)).Ok);
        await SettleEvents();
        Assert.Equal(2, CountOf(events));

        await ctx.Service.ShutdownAsync();
        var settingsStore = new JsonSettingsStore(ctx.Paths);
        var settings = await settingsStore.LoadAsync();
        settings.DownloadGroups.Add(new DownloadGroup { Name = "Ghosts", Key = "batch:ghosts", CompletionTracked = true, JobIds = [Guid.NewGuid(), Guid.NewGuid()] });
        settings.DownloadGroups.Add(new DownloadGroup { Name = "Part ghost", Key = "batch:part-ghost", CompletionTracked = true, JobIds = [Guid.NewGuid(), first.Id] });
        settings.DownloadGroups.Single(candidate => candidate.Id == group.Id).JobIds.Remove(first.Id);
        await settingsStore.SaveAsync(settings);
        await using var restarted = await ServiceContext.CreateAsync(_server, ctx.Paths, ctx.Destination, autoResume: false);
        Assert.False(restarted.Service.IsPackageComplete(Package(restarted, "ghosts").Id));
        Assert.True(restarted.Service.IsPackageComplete(Package(restarted, "part-ghost").Id));
    }

    // ---- Mixed outcomes ----

    [Fact]
    public async Task APartialFailureLeavesThePackageFailedAndIncompleteWithItsGoodFilesKept()
    {
        var events = new List<PackageCompletedEventArgs>();
        await using var ctx = await ServiceContext.CreateAsync(_server, autoResume: false);
        Collect(ctx, events);
        var bad = await AddToPackage(ctx, "partial-bad.bin", "partial", start: "startNow", url: $"{_server.BaseAddress}/error/404");
        await ctx.WaitFor(bad.Id, job => job.State == "failed");
        var good = await AddToPackage(ctx, "partial-good.bin", "partial", start: "startNow");
        good = await ctx.WaitFor(good.Id, job => job.State == "completed");
        var group = Package(ctx, "partial");

        var summary = DownloadGroupSummary.Create(Children(ctx, group));
        Assert.Equal("failed", summary.State);
        Assert.Equal(1, summary.CompletedCount);
        Assert.Equal(2, summary.MemberCount);
        Assert.False(ctx.Service.IsPackageComplete(group.Id));
        await SettleEvents();
        Assert.Equal(0, CountOf(events));
        Assert.Empty(ctx.Service.GetCompletedPackages());
        Assert.True(File.Exists(good.FinalFilePath));
    }

    [Fact]
    public async Task RetryingAChildOnlyTouchesThatChildAndCompletesThePackage()
    {
        var events = new List<PackageCompletedEventArgs>();
        await using var ctx = await ServiceContext.CreateAsync(_server, autoResume: false);
        Collect(ctx, events);
        var flakyName = $"retry-{Guid.NewGuid():N}.bin";
        var sibling = await AddToPackage(ctx, "retry-sibling.bin", "child-retry");
        var flaky = await AddToPackage(ctx, flakyName, "child-retry", start: "startNow",
            url: $"{_server.BaseAddress}/flaky/{flakyName}?failures=3&size=4096&seed=5");
        await ctx.WaitFor(flaky.Id, job => job.State == "failed");
        Assert.True((await ctx.Service.ResumeAsync(sibling.Id)).Ok);
        sibling = await ctx.WaitFor(sibling.Id, job => job.State == "completed");
        var group = Package(ctx, "child-retry");
        Assert.Equal("failed", DownloadGroupSummary.Create(Children(ctx, group)).State);
        Assert.Equal(0, CountOf(events));

        Assert.True((await ctx.Service.ResumeAsync(flaky.Id)).Ok);
        var finished = await ctx.WaitFor(flaky.Id, job => job.State == "completed");
        Assert.Equal(4096, new FileInfo(finished.FinalFilePath).Length);
        Assert.Equal(sibling.CompletedAt, ctx.Service.GetJob(sibling.Id)!.CompletedAt);
        Assert.Equal("completed", DownloadGroupSummary.Create(Children(ctx, group)).State);
        await WaitUntil(() => CountOf(events) == 1, "the completion after the retry");
        await SettleEvents();
        Assert.Equal(1, CountOf(events));
    }

    [Fact]
    public async Task APackageWithMixedChildrenComesBackTheSameAfterARestart()
    {
        const int size = 2_097_152;
        await using var ctx = await ServiceContext.CreateAsync(_server, autoResume: false);
        var done = await AddToPackage(ctx, "r-done.bin", "restart", start: "startNow");
        done = await ctx.WaitFor(done.Id, job => job.State == "completed");
        var failed = await AddToPackage(ctx, "r-failed.bin", "restart", start: "startNow", url: $"{_server.BaseAddress}/error/404");
        await ctx.WaitFor(failed.Id, job => job.State == "failed");
        var cancelled = await AddToPackage(ctx, "r-cancelled.bin", "restart");
        Assert.True((await ctx.Service.CancelAsync(cancelled.Id, deleteFile: false)).Ok);
        var waiting = await AddToPackage(ctx, "r-waiting.bin", "restart");
        var running = await AddToPackage(ctx, "r-running.bin", "restart", start: "startNow",
            url: $"{_server.BaseAddress}/slow/r-running.bin?size={size}&seed=61&bps=524288");
        var progressed = await ctx.WaitFor(running.Id, job => job.State == "downloading" && job.ReceivedBytes > 0);
        var group = Package(ctx, "restart");

        // Every file in this one was stopped by the package, and that must survive too.
        var undone1 = await AddToPackage(ctx, "r-undo-1.bin", "restart-undo");
        var undone2 = await AddToPackage(ctx, "r-undo-2.bin", "restart-undo");
        var undoGroup = Package(ctx, "restart-undo");
        Assert.True((await ctx.Service.GroupActionAsync(new GroupActionRequest { Action = "cancel", GroupId = undoGroup.Id })).Ok);

        await ctx.Service.ShutdownAsync();
        await using var restarted = await ServiceContext.CreateAsync(_server, ctx.Paths, ctx.Destination, autoResume: false);

        Assert.Equal(group.JobIds, Package(restarted, "restart").JobIds);
        var children = Children(restarted, Package(restarted, "restart"));
        Assert.Equal("completed", children.Single(job => job.Id == done.Id).State);
        Assert.Equal(done.ReceivedBytes, children.Single(job => job.Id == done.Id).ReceivedBytes);
        Assert.Equal("failed", children.Single(job => job.Id == failed.Id).State);
        Assert.Equal("cancelled", children.Single(job => job.Id == cancelled.Id).State);
        Assert.Equal("queued", children.Single(job => job.Id == waiting.Id).State);
        var interrupted = children.Single(job => job.Id == running.Id);
        Assert.Equal("paused", interrupted.State);
        Assert.True(interrupted.ReceivedBytes >= progressed.ReceivedBytes);

        var summary = DownloadGroupSummary.Create(children);
        Assert.Equal("failed", summary.State);
        Assert.Equal(1, summary.CompletedCount);
        Assert.False(restarted.Service.IsPackageComplete(group.Id));

        var resume = await restarted.Service.GroupActionAsync(new GroupActionRequest { Action = "resume", GroupId = group.Id });
        Assert.True(resume.Ok, resume.Error);
        Assert.Equal(2, resume.Changed);
        Assert.Equal(1, resume.LeftCancelled);
        var finished = await restarted.WaitFor(running.Id, job => job.State == "completed", TimeSpan.FromSeconds(30));
        Assert.Equal(size, new FileInfo(finished.FinalFilePath).Length);
        Assert.Equal("cancelled", StateOf(restarted, cancelled.Id));

        var undo = await restarted.Service.GroupActionAsync(new GroupActionRequest { Action = "resume", GroupId = undoGroup.Id });
        Assert.True(undo.Ok, undo.Error);
        Assert.Equal(2, undo.Changed);
        await restarted.WaitFor(undone1.Id, job => job.State == "completed");
        await restarted.WaitFor(undone2.Id, job => job.State == "completed");
    }
}
