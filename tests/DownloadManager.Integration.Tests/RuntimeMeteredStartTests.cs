using System.Collections.Concurrent;
using DownloadManager.Contracts;
using DownloadManager.Core;
using DownloadManager.Persistence;
using DownloadManager.Runtime;
using DownloadManager.Runtime.Ipc;
using TestServer;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// Android step 2, S0 follow-ups: the network reported before initialize, the hello capability, ordered announcements,
/// "Use data" only lifting a rule that holds, the no-resume flag, and the "stay paused" start option (StartReconcile).
/// </summary>
public sealed partial class RuntimeServiceTests
{
    /// <summary>A service built by hand on existing state, initialised by the test (settings are not rewritten after it).</summary>
    private sealed class Booted : IAsyncDisposable
    {
        private readonly HttpDownloadEngine _engine;

        public Booted(StoragePaths paths, bool schedulerTimer = false)
        {
            var checkpoints = new JsonCheckpointStore(paths);
            _engine = new HttpDownloadEngine(
                new EngineOptions { MaxAttempts = 3, InitialRetryDelay = TimeSpan.FromMilliseconds(20), MaxRetryDelay = TimeSpan.FromMilliseconds(100), AllowLocalNetwork = true },
                checkpoints: checkpoints);
            Service = new DownloadManagerService(paths, new JsonJobStore(paths), checkpoints, new JsonSettingsStore(paths), _engine,
                log: Log.Enqueue, enableSchedulerTimer: schedulerTimer);
            Service.PackageSettleDelay = TimeSpan.Zero;
            Service.MeteredWaitChanged += (_, waiting) => Waits.Enqueue(waiting);
        }

        public DownloadManagerService Service { get; }
        public ConcurrentQueue<bool> Waits { get; } = new();
        public ConcurrentQueue<string> Log { get; } = new();
        public JobDto Job(Guid id) => Service.GetJob(id)!;

        public async Task UntilAsync(Func<bool> condition, string what, int seconds = 30)
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(seconds);
            while (!condition())
            {
                if (DateTimeOffset.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for {what}.");
                await Task.Delay(30);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Service.ShutdownAsync();
            _engine.Dispose();
        }
    }

    /// <summary>
    /// A paused job with data on disk in a first service (kept alive by the caller), rewritten on disk by <paramref name="shape"/> as
    /// a crash or an exit would have left it.
    /// </summary>
    private async Task<(ServiceContext Ctx, Guid Id, long Bytes)> OnDiskJobAsync(Action<DownloadJob> shape, bool wifiOnly, string name, int seed, string extraQuery = "")
    {
        var ctx = await ServiceContext.CreateAsync(_server);
        if (wifiOnly) await WifiOnlyAsync(ctx);
        var job = await AddSlowAsync(ctx, name, 3 * Mb, seed, 512 * 1024, 1, extraQuery: extraQuery);
        await ctx.WaitFor(job.Id, j => j.State == "downloading" && j.ReceivedBytes > 128 * 1024, TimeSpan.FromSeconds(30));
        Assert.True((await ctx.Service.PauseAsync(job.Id)).Ok);
        await ctx.WaitFor(job.Id, j => j.State == "paused", TimeSpan.FromSeconds(30));
        var checkpoint = await SettledCheckpointAsync(ctx, job.Id);
        var store = new JsonJobStore(ctx.Paths);
        var stored = (await store.LoadAsync(job.Id))!;
        shape(stored);
        await store.SaveAsync(stored);
        return (ctx, job.Id, checkpoint.CompletedBytes);
    }

    private static void Interrupted(DownloadJob job, DownloadState state = DownloadState.Downloading)
    {
        job.State = state;
        job.PausedByUser = false;
        job.PausedByExit = false;
    }

    // ----- part 1, item 1: the network before initialize -----

    [Fact]
    public async Task AMeteredReportBeforeInitializeOnlyStoresItAndTheFirstScheduleIsHeld()
    {
        var (ctx, id, bytes) = await OnDiskJobAsync(job => Interrupted(job), wifiOnly: true, "pre-init.bin", 961);
        await using var _ = ctx;
        await using var b = new Booted(ctx.Paths);

        await b.Service.SetNetworkMeteredAsync(true);
        // Nothing was loaded, saved or announced: the job on disk is as the crash left it.
        Assert.Empty(b.Waits);
        var onDisk = (await new JsonJobStore(ctx.Paths).LoadAsync(id))!;
        Assert.Equal(DownloadState.Downloading, onDisk.State);

        await b.Service.InitializeAsync();
        var held = b.Job(id);
        Assert.Equal("queued", held.State);
        Assert.False(held.PausedByUser);
        Assert.Equal(bytes, held.ReceivedBytes);
        Assert.True(b.Service.WaitingForMeteredNetwork);
        Assert.Equal(new[] { true }, b.Waits.ToArray());
        await Task.Delay(500);
        Assert.Equal("queued", b.Job(id).State);
        Assert.Equal(bytes, b.Job(id).ReceivedBytes);

        await b.Service.SetNetworkMeteredAsync(false);
        await b.UntilAsync(() => b.Job(id).State is "completed" or "failed", "the job to finish");
        Assert.Equal("completed", b.Job(id).State);
        Assert.Equal(Sha256Of(961, 3 * Mb), await Hashing.Sha256FileAsync(b.Job(id).FinalFilePath));
        Assert.Equal(new[] { true, false }, b.Waits.ToArray());
    }

    // ----- part 1, item 2: the capability -----

    [Fact]
    public void TheHelloNamesMeteredNetworkRuleOnlyWhereThePlatformReportsNetworks()
    {
        SessionRights Rights(bool metered) => new()
        {
            Role = ClientRoles.Desktop,
            Capabilities = new PlatformCapabilities { SupportsTray = true, SupportsShutdown = true, SupportsMeteredNetworkRule = metered },
        };

        var without = HostCommandDispatcher.CapabilitiesOf(Rights(false));
        var with = HostCommandDispatcher.CapabilitiesOf(Rights(true));
        Assert.DoesNotContain("meteredNetworkRule", without);
        Assert.Equal("meteredNetworkRule", with[^1]);
        Assert.Equal(without, with.Take(with.Count - 1).ToList());
    }

    // ----- part 1, item 3: ordered announcements -----

    [Fact]
    public async Task ConcurrentSettingsSavesAndNetworkReportsDeliverInOrderAndEndOnTheFinalState()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var delivered = new ConcurrentQueue<bool>();
        ctx.Service.MeteredWaitChanged += (_, waiting) => { delivered.Enqueue(waiting); Thread.Yield(); };
        // A listener that throws must not reach the caller.
        ctx.Service.MeteredWaitChanged += (_, _) => throw new InvalidOperationException("listener failure");

        for (var round = 0; round < 12; round++)
        {
            var tasks = new List<Task>();
            for (var i = 0; i < 8; i++)
            {
                var on = (i + round) % 2 == 0;
                var metered = (i + round) % 3 != 0;
                tasks.Add(Task.Run(async () =>
                {
                    var settings = ctx.Service.GetSnapshot().Settings;
                    settings.DownloadOnlyOnUnmeteredNetwork = on;
                    await ctx.Service.UpdateSettingsAsync(settings);
                }));
                tasks.Add(Task.Run(() => ctx.Service.SetNetworkMeteredAsync(metered)));
            }

            await Task.WhenAll(tasks);
            var sequence = delivered.ToArray();
            Assert.NotEmpty(sequence);
            // Each delivery is a change from the one before, and the last one delivered is the state now.
            for (var i = 1; i < sequence.Length; i++) Assert.NotEqual(sequence[i - 1], sequence[i]);
            Assert.Equal(ctx.Service.WaitingForMeteredNetwork, sequence[^1]);
        }
    }

    [Fact]
    public async Task AListenerThatThrowsOnTheMeteredWaitDoesNotFailTheSettingsSave()
    {
        var logged = new ConcurrentQueue<string>();
        await using var ctx = await ServiceContext.CreateAsync(_server, hooks: new ContextHooks { Log = logged.Enqueue });
        ctx.Service.MeteredWaitChanged += (_, _) => throw new InvalidOperationException("boom");
        await ctx.Service.SetNetworkMeteredAsync(true);
        await WifiOnlyAsync(ctx); // the announce happens inside this save; Ok is asserted there
        Assert.True(ctx.Service.WaitingForMeteredNetwork);
        Assert.Contains(logged, line => line.Contains("metered-wait listener failed") && line.Contains("boom"));
    }

    // ----- part 1, item 4: "Use data" only lifts a rule that holds -----

    [Fact]
    public async Task UseDataWhileTheSettingIsOffDoesNotLiftTheRuleWhenItIsTurnedOn()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        await ctx.Service.SetNetworkMeteredAsync(true);
        ctx.Service.AllowMeteredUntilUnmetered(); // the rule doesn't hold: nothing to lift
        await WifiOnlyAsync(ctx);
        Assert.True(ctx.Service.WaitingForMeteredNetwork);
    }

    [Fact]
    public async Task ChangingTheSettingEndsAUseDataLift()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        await WifiOnlyAsync(ctx);
        await ctx.Service.SetNetworkMeteredAsync(true);
        ctx.Service.AllowMeteredUntilUnmetered();
        Assert.False(ctx.Service.WaitingForMeteredNetwork);
        ctx.Service.AllowMeteredUntilUnmetered(); // a second tap changes nothing
        Assert.False(ctx.Service.WaitingForMeteredNetwork);

        await WifiOnlyAsync(ctx, on: false);
        await WifiOnlyAsync(ctx, on: true);
        Assert.True(ctx.Service.WaitingForMeteredNetwork);
    }

    // ----- part 1, item 5: no-resume jobs keep running and say so -----

    [Fact]
    public async Task ANonResumableJobKeepsRunningOnMeteredDataAndIsFlaggedUntilTheRuleLetsGo()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server, maxConcurrent: 2);
        var updates = new ConcurrentQueue<(bool Flag, string State)>();
        ctx.Service.JobChanged += (_, change) => updates.Enqueue((change.Job.RunningOnMeteredWithoutResume, change.Job.State));
        await WifiOnlyAsync(ctx);
        var fixedJob = await AddSlowAsync(ctx, "nr-keep.bin", 3 * Mb, 962, 512 * 1024, 1, route: "norange");
        var resumable = await AddSlowAsync(ctx, "nr-resumable.bin", 3 * Mb, 963, 512 * 1024, 1);
        await ctx.WaitFor(fixedJob.Id, j => j.State == "downloading" && j.ReceivedBytes > 128 * 1024 && j.ResumeSupported == false, TimeSpan.FromSeconds(30));
        await ctx.WaitFor(resumable.Id, j => j.State == "downloading" && j.ReceivedBytes > 128 * 1024, TimeSpan.FromSeconds(30));
        Assert.False(ctx.Service.GetJob(fixedJob.Id)!.RunningOnMeteredWithoutResume);

        await ctx.Service.SetNetworkMeteredAsync(true);
        var kept = ctx.Service.GetJob(fixedJob.Id)!;
        Assert.Equal("downloading", kept.State);
        Assert.True(kept.RunningOnMeteredWithoutResume);
        Assert.Contains(updates, u => u.Flag && u.State == "downloading");
        var stopped = ctx.Service.GetJob(resumable.Id)!;
        Assert.Equal("queued", stopped.State);
        Assert.False(stopped.RunningOnMeteredWithoutResume);

        ctx.Service.AllowMeteredUntilUnmetered();
        Assert.False(ctx.Service.GetJob(fixedJob.Id)!.RunningOnMeteredWithoutResume);
        var done = await ctx.WaitFor(fixedJob.Id, j => j.State is "completed" or "failed", TimeSpan.FromSeconds(60));
        Assert.Equal("completed", done.State);
        Assert.False(done.RunningOnMeteredWithoutResume);
        Assert.Equal(Sha256Of(962, 3 * Mb), await Hashing.Sha256FileAsync(done.FinalFilePath));
    }

    [Fact]
    public async Task StopNonResumableOnMeteredStopsFlaggedJobsWhichRestartFromZeroOnAnUnmeteredNetwork()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server, maxConcurrent: 2);
        await WifiOnlyAsync(ctx);
        var job = await AddSlowAsync(ctx, "nr-stop.bin", 3 * Mb, 968, 512 * 1024, 1, route: "norange");
        await ctx.WaitFor(job.Id, j => j.State == "downloading" && j.ReceivedBytes > 128 * 1024 && j.ResumeSupported == false, TimeSpan.FromSeconds(30));

        // The rule doesn't hold: nothing is stopped.
        Assert.Equal(0, await ctx.Service.StopNonResumableOnMeteredAsync());
        Assert.Equal("downloading", ctx.Service.GetJob(job.Id)!.State);

        await ctx.Service.SetNetworkMeteredAsync(true);
        Assert.True(ctx.Service.GetJob(job.Id)!.RunningOnMeteredWithoutResume);
        Assert.Equal(1, await ctx.Service.StopNonResumableOnMeteredAsync());
        var stopped = ctx.Service.GetJob(job.Id)!;
        Assert.Equal("queued", stopped.State);
        Assert.False(stopped.PausedByUser);
        Assert.False(stopped.RunningOnMeteredWithoutResume);
        await Task.Delay(600);
        Assert.Equal("queued", ctx.Service.GetJob(job.Id)!.State);
        Assert.Equal(0, await ctx.Service.StopNonResumableOnMeteredAsync());

        await ctx.Service.SetNetworkMeteredAsync(false);
        var done = await ctx.WaitFor(job.Id, j => j.State is "completed" or "failed", TimeSpan.FromSeconds(60));
        Assert.Equal("completed", done.State);
        Assert.Equal(Sha256Of(968, 3 * Mb), await Hashing.Sha256FileAsync(done.FinalFilePath));
    }

    [Fact]
    public async Task AJobKeptRunningBecauseTheServerHadNotSaidWhetherItResumesIsStoppedOnceTheServerSaysYes()
    {
        // On disk: a paused job with data whose server's answer about resuming is not known. The resume's headers come 3 s late.
        var (ctx, id, _) = await OnDiskJobAsync(job => { Interrupted(job); job.ResumeSupported = null; }, wifiOnly: true,
            "resume-known.bin", 969, extraQuery: "&rangedelay=3000");
        await using var _ = ctx;
        await using var b = new Booted(ctx.Paths);
        await b.Service.InitializeAsync();
        await b.UntilAsync(() => b.Job(id) is { State: "starting" or "connecting" or "downloading" } && b.Job(id).ReceivedBytes > 0, "the resume to start");
        Assert.Null(b.Job(id).ResumeSupported);

        await b.Service.SetNetworkMeteredAsync(true);
        Assert.True(b.Job(id).RunningOnMeteredWithoutResume);
        Assert.NotEqual("queued", b.Job(id).State);

        // The server answers 206: the job is applied the held rule like any resumable one.
        await b.UntilAsync(() => b.Job(id).State == "queued", "the job to go back to the queue", seconds: 20);
        var queued = b.Job(id);
        Assert.False(queued.RunningOnMeteredWithoutResume);
        Assert.False(queued.PausedByUser);
        Assert.True(queued.ResumeSupported);
        await Task.Delay(500);
        Assert.Equal("queued", b.Job(id).State);

        await b.Service.SetNetworkMeteredAsync(false);
        await b.UntilAsync(() => b.Job(id).State is "completed" or "failed", "the job to finish");
        Assert.Equal("completed", b.Job(id).State);
        Assert.Equal(Sha256Of(969, 3 * Mb), await Hashing.Sha256FileAsync(b.Job(id).FinalFilePath));
    }

    // ----- part 1, item 6: a queue retry coming due while held -----

    [Fact]
    public async Task AQueueRetryDueWhileHeldQueuesTheJobButDoesNotStartIt()
    {
        var (ctx, id, bytes) = await OnDiskJobAsync(job =>
        {
            Interrupted(job, DownloadState.Failed);
            job.Failure = new JobFailure(FailureKind.Network, "dropped", true, true, DateTimeOffset.UtcNow);
            job.IsQueued = true;
            job.QueueProcessing = true;
            job.StartMode = StartMode.Queue;
            job.QueueRetryCount = 1;
            job.QueueRetryAt = DateTimeOffset.UtcNow.AddSeconds(-5);
        }, wifiOnly: true, "retry-held.bin", 964);
        await using var _ = ctx;
        await using var b = new Booted(ctx.Paths, schedulerTimer: true);
        await b.Service.SetNetworkMeteredAsync(true);
        await b.Service.InitializeAsync();

        await b.UntilAsync(() => b.Job(id) is { QueueRetryAt: null, State: "queued" }, "the due retry to be queued");
        await Task.Delay(600);
        Assert.Equal("queued", b.Job(id).State);
        Assert.Equal(bytes, b.Job(id).ReceivedBytes);

        await b.Service.SetNetworkMeteredAsync(false);
        await b.UntilAsync(() => b.Job(id).State is "completed" or "failed", "the retried job to finish");
        Assert.Equal("completed", b.Job(id).State);
    }

    // ----- part 2: the "stay paused" start option -----

    [Fact]
    public async Task WithoutTheOptionAnInterruptedJobStillResumesAtStart()
    {
        var (ctx, id, _) = await OnDiskJobAsync(job => Interrupted(job), wifiOnly: false, "default-resume.bin", 965);
        await using var _ = ctx;
        await using var b = new Booted(ctx.Paths);
        await b.Service.InitializeAsync();
        await b.UntilAsync(() => b.Job(id).State is "completed" or "failed", "the job to finish");
        Assert.Equal("completed", b.Job(id).State);
        Assert.Equal(Sha256Of(965, 3 * Mb), await Hashing.Sha256FileAsync(b.Job(id).FinalFilePath));
    }

    [Theory]
    [InlineData("downloading")]
    [InlineData("retrywaiting")]
    [InlineData("exit")]
    public async Task StoppedByTheUserTheInterruptedJobStaysPausedAsAUserPause(string shape)
    {
        var (ctx, id, bytes) = await OnDiskJobAsync(job =>
        {
            Interrupted(job, shape == "retrywaiting" ? DownloadState.RetryWaiting : DownloadState.Downloading);
            if (shape == "exit") { job.State = DownloadState.Paused; job.PausedByExit = true; }
        }, wifiOnly: false, $"stopped-{shape}.bin", 966);
        await using var _ = ctx;
        await using var b = new Booted(ctx.Paths);
        await b.Service.InitializeAsync(lastRunStoppedByUser: true);

        var job = b.Job(id);
        Assert.Equal("paused", job.State);
        Assert.True(job.PausedByUser);
        Assert.Equal(bytes, job.ReceivedBytes);
        await Task.Delay(600);
        Assert.Equal("paused", b.Job(id).State);
        Assert.Equal(bytes, b.Job(id).ReceivedBytes);
        var stored = (await new JsonJobStore(ctx.Paths).LoadAsync(id))!;
        Assert.Equal(DownloadState.Paused, stored.State);
        Assert.True(stored.PausedByUser);
        Assert.False(stored.PausedByExit);

        // It is a user pause like any other: Resume goes on from its data.
        Assert.True((await b.Service.ResumeAsync(id)).Ok);
        await b.UntilAsync(() => b.Job(id).State is "completed" or "failed", "the resumed job to finish");
        Assert.Equal(Sha256Of(966, 3 * Mb), await Hashing.Sha256FileAsync(b.Job(id).FinalFilePath));
    }

    [Fact]
    public async Task StoppedByTheUserAQueuedJobIsPausedTooAndResumes()
    {
        // Owner, 2026-11-17: "Pause them too".
        var (ctx, id, _) = await OnDiskJobAsync(job => { Interrupted(job, DownloadState.Queued); job.StartMode = StartMode.Queue; }, wifiOnly: false, "stopped-queued.bin", 967);
        await using var _ = ctx;
        await using var b = new Booted(ctx.Paths);
        await b.Service.InitializeAsync(lastRunStoppedByUser: true);
        await Task.Delay(300);
        Assert.Equal("paused", b.Job(id).State);
        Assert.True(b.Job(id).PausedByUser);
        await b.Service.ResumeAsync(id);
        await b.UntilAsync(() => b.Job(id).State is "completed" or "failed", "the resumed job to finish");
        Assert.Equal("completed", b.Job(id).State);
    }

    [Fact]
    public async Task StoppedByTheUserAJobScheduledForLaterKeepsItsSchedule()
    {
        var (ctx, id, _) = await OnDiskJobAsync(job => { Interrupted(job, DownloadState.Queued); job.StartMode = StartMode.Later; }, wifiOnly: false, "stopped-later.bin", 968);
        await using var _ = ctx;
        await using var b = new Booted(ctx.Paths);
        await b.Service.InitializeAsync(lastRunStoppedByUser: true);
        Assert.Equal("queued", b.Job(id).State);
        Assert.False(b.Job(id).PausedByUser);
    }

    /// <summary>The pure rule and the real reconcile give the same answer for each stored shape.</summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task StartReconcileAgreesWithTheRealReconcile(bool autoResumeSetting, bool userStopped)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "test-work", Guid.NewGuid().ToString("N"));
        var paths = new StoragePaths(Path.Combine(root, "state"));
        var destination = Path.Combine(root, "downloads");
        Directory.CreateDirectory(destination);
        paths.EnsureCreated();
        try
        {
            // Held on a metered network, so what would resume is Queued and nothing runs to move it on.
            var settings = new AppSettings { DefaultDownloadDirectory = destination, AutoResumeUnfinishedOnStartup = autoResumeSetting, DownloadOnlyOnUnmeteredNetwork = true };
            await new JsonSettingsStore(paths).SaveAsync(settings);
            var store = new JsonJobStore(paths);
            DownloadJob Make(string name, DownloadState state, Action<DownloadJob>? tweak = null)
            {
                var job = new DownloadJob
                {
                    Url = $"{_server.BaseAddress}/file/{name}?size=1000&seed=1",
                    FileName = name,
                    DestinationDirectory = destination,
                    FinalFilePath = Path.Combine(destination, name),
                    State = state,
                    IsQueued = true,
                };
                job.PartFilePath = $"{job.FinalFilePath}.{job.Id:N}.part";
                tweak?.Invoke(job);
                return job;
            }

            var jobs = new List<DownloadJob>
            {
                Make("queued.bin", DownloadState.Queued),
                Make("later.bin", DownloadState.Queued, j => j.StartMode = StartMode.Later),
                Make("connecting.bin", DownloadState.Connecting),
                Make("downloading.bin", DownloadState.Downloading),
                Make("downloading-no-auto.bin", DownloadState.Downloading, j => j.AutoResumeAfterRestart = false),
                Make("retrywaiting.bin", DownloadState.RetryWaiting),
                Make("verifying.bin", DownloadState.Verifying),
                Make("paused-user.bin", DownloadState.Paused, j => j.PausedByUser = true),
                Make("paused-plain.bin", DownloadState.Paused),
                Make("paused-exit.bin", DownloadState.Paused, j => j.PausedByExit = true),
                Make("paused-exit-no-auto.bin", DownloadState.Paused, j => { j.PausedByExit = true; j.AutoResumeAfterRestart = false; }),
                Make("paused-exit-user.bin", DownloadState.Paused, j => { j.PausedByExit = true; j.PausedByUser = true; }),
                Make("paused-exit-later.bin", DownloadState.Paused, j => { j.PausedByExit = true; j.StartMode = StartMode.Later; }),
                Make("failed.bin", DownloadState.Failed, j => j.Failure = new JobFailure(FailureKind.Network, "x", true, true, DateTimeOffset.UtcNow)),
            };
            foreach (var job in jobs) await store.SaveAsync(job);
            var before = (await store.LoadAllAsync()).ToDictionary(j => j.Id);

            await using var b = new Booted(paths);
            await b.Service.SetNetworkMeteredAsync(true);
            await b.Service.InitializeAsync(userStopped);
            foreach (var job in jobs)
            {
                var stored = before[job.Id];
                var expected = StartReconcile.WouldResumeAtStart(stored, settings, userStopped);
                var after = b.Job(job.Id);
                var actual = after.State == "queued" && after.StartMode != "later";
                Assert.True(expected == actual, $"{job.FileName} (auto {autoResumeSetting}, stopped {userStopped}): rule {expected}, reconcile {after.State}/{after.StartMode}.");
                // Held, so nothing ran: what the rule decided is what is there.
                var decided = StartReconcile.Decide(stored, settings, userStopped);
                Assert.Equal(decided.State.ToString().ToLowerInvariant(), after.State.ToLowerInvariant());
                Assert.Equal(decided.PausedByUser, after.PausedByUser);
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
}
