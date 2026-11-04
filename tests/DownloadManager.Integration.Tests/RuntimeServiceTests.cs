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

    [Fact]
    public async Task StoppingQueuePausesOnlyThatQueue()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server, maxConcurrent: 3);
        var settings = ctx.Service.GetSnapshot().Settings;
        settings.Queues.Add(new QueueDto
        {
            Id = 1,
            Name = "Night",
            MaxConcurrentDownloads = 2,
            IsRunning = true,
            Order = 1,
        });
        var update = await ctx.Service.UpdateSettingsAsync(settings);
        Assert.True(update.Ok, update.Error);

        const int size = 1_048_576;
        var main = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/slow/q0.bin?size={size}&seed=41&bps=262144",
            DestinationDirectory = ctx.Destination,
            FileName = "q0.bin",
            Source = "cli",
            QueueId = 0,
        });
        var night = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/slow/q1.bin?size={size}&seed=42&bps=262144",
            DestinationDirectory = ctx.Destination,
            FileName = "q1.bin",
            Source = "cli",
            QueueId = 1,
        });

        Assert.True(main.Accepted, main.Error);
        Assert.True(night.Accepted, night.Error);
        Assert.Equal(1, night.Job!.QueueId);
        Assert.Equal("Night", night.Job.QueueName);

        await ctx.WaitFor(main.Job!.Id, j => j.ReceivedBytes > 65_536);
        await ctx.WaitFor(night.Job.Id, j => j.ReceivedBytes > 65_536);

        var stop = await ctx.Service.QueueActionAsync(new QueueActionRequest { QueueId = 1, Action = "stop" });
        Assert.True(stop.Ok, stop.Error);
        var paused = await ctx.WaitFor(night.Job.Id, j => j.State == "paused");
        Assert.Equal(1, paused.QueueId);

        // The other queue keeps running and completes.
        await ctx.WaitFor(main.Job.Id, j => j.State == "completed", TimeSpan.FromSeconds(30));
        Assert.Equal(1, ctx.Service.GetJob(night.Job.Id)!.QueueId);

        var start = await ctx.Service.QueueActionAsync(new QueueActionRequest { QueueId = 1, Action = "start" });
        Assert.True(start.Ok, start.Error);
        var completed = await ctx.WaitFor(night.Job.Id, j => j.State == "completed", TimeSpan.FromSeconds(30));
        var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            TestContent.Generate(42, size))).ToLowerInvariant();
        Assert.Equal(expected, await Hashing.Sha256FileAsync(completed.FinalFilePath));
    }

    [Fact]
    public async Task PerQueueConcurrencyIsRespected()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server, maxConcurrent: 3);
        var settings = ctx.Service.GetSnapshot().Settings;
        settings.Queues =
        [
            new QueueDto { Id = 0, Name = "Main", MaxConcurrentDownloads = 1, IsRunning = true, Order = 0 },
            new QueueDto { Id = 1, Name = "Night", MaxConcurrentDownloads = 1, IsRunning = true, Order = 1 },
        ];
        var update = await ctx.Service.UpdateSettingsAsync(settings);
        Assert.True(update.Ok, update.Error);

        var jobs = new List<JobDto>();
        for (var i = 0; i < 3; i++)
        {
            var response = await ctx.Service.AddJobAsync(new AddJobRequest
            {
                Url = $"{_server.BaseAddress}/slow/pq{i}.bin?size=524288&seed={50 + i}&bps=131072",
                DestinationDirectory = ctx.Destination,
                FileName = $"pq{i}.bin",
                Source = "cli",
                QueueId = i == 2 ? 1 : 0,
            });
            Assert.True(response.Accepted, response.Error);
            jobs.Add(response.Job!);
        }

        await Task.Delay(700);
        var snapshot = ctx.Service.GetSnapshot();
        var running = snapshot.Jobs
            .Where(j => j.State is "connecting" or "downloading")
            .GroupBy(j => j.QueueId)
            .ToList();
        Assert.All(running, group => Assert.InRange(group.Count(), 1, 1));
        Assert.Equal(2, running.Sum(group => group.Count()));

        foreach (var job in jobs)
        {
            await ctx.WaitFor(job.Id, j => j.State == "completed", TimeSpan.FromSeconds(60));
        }

        Assert.Equal(0, jobs[0].QueueId);
        Assert.Equal(1, jobs[2].QueueId);
    }

    [Fact]
    public async Task QueueStateAndMembershipSurviveRestart()
    {
        var ctx = await ServiceContext.CreateAsync(_server, autoResume: false);
        try
        {
            var settings = ctx.Service.GetSnapshot().Settings;
            settings.Queues.Add(new QueueDto
            {
                Id = 1,
                Name = "Night",
                MaxConcurrentDownloads = 2,
                IsRunning = false,
                Order = 1,
            });
            var update = await ctx.Service.UpdateSettingsAsync(settings);
            Assert.True(update.Ok, update.Error);

            var response = await ctx.Service.AddJobAsync(new AddJobRequest
            {
                Url = $"{_server.BaseAddress}/slow/persist.bin?size=524288&seed=60&bps=262144",
                DestinationDirectory = ctx.Destination,
                FileName = "persist.bin",
                Source = "cli",
                QueueId = 1,
            });
            Assert.True(response.Accepted, response.Error);
            await Task.Delay(250);
            Assert.Equal("queued", ctx.Service.GetJob(response.Job!.Id)!.State);

            await using var second = await ServiceContext.CreateAsync(_server, ctx.Paths, ctx.Destination, autoResume: false);
            var reloadedSettings = second.Service.GetSnapshot().Settings;
            var night = Assert.Single(reloadedSettings.Queues, q => q.Id == 1);
            Assert.Equal("Night", night.Name);
            Assert.Equal(2, night.MaxConcurrentDownloads);
            Assert.False(night.IsRunning);

            var reloaded = second.Service.GetJob(response.Job.Id);
            Assert.NotNull(reloaded);
            Assert.Equal(1, reloaded!.QueueId);
            Assert.Equal("Night", reloaded.QueueName);
            Assert.Equal("queued", reloaded.State);

            var start = await second.Service.QueueActionAsync(new QueueActionRequest { QueueId = 1, Action = "start" });
            Assert.True(start.Ok, start.Error);
            var completed = await second.WaitFor(response.Job.Id, j => j.State == "completed", TimeSpan.FromSeconds(30));
            var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                TestContent.Generate(60, 524_288))).ToLowerInvariant();
            Assert.Equal(expected, await Hashing.Sha256FileAsync(completed.FinalFilePath));
        }
        finally
        {
            await ctx.DisposeAsync();
        }
    }

    [Fact]
    public async Task MovingJobToStoppedQueueUpdatesMembershipAndPauses()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server, maxConcurrent: 3);
        var settings = ctx.Service.GetSnapshot().Settings;
        settings.Queues.Add(new QueueDto
        {
            Id = 1,
            Name = "Night",
            MaxConcurrentDownloads = 2,
            IsRunning = false,
            Order = 1,
        });
        var update = await ctx.Service.UpdateSettingsAsync(settings);
        Assert.True(update.Ok, update.Error);

        var done = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/file/move-done.bin?size=65536&seed=91",
            DestinationDirectory = ctx.Destination,
            FileName = "move-done.bin",
            Source = "cli",
            QueueId = 0,
        });
        await ctx.WaitFor(done.Job!.Id, j => j.State == "completed", TimeSpan.FromSeconds(30));
        var moveDone = await ctx.Service.MoveToQueueAsync(done.Job.Id, 1);
        Assert.True(moveDone.Ok, moveDone.Error);
        Assert.Equal(1, moveDone.Job!.QueueId);
        Assert.Equal("Night", moveDone.Job.QueueName);

        const int size = 1_048_576;
        var active = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/slow/move-active.bin?size={size}&seed=92&bps=131072",
            DestinationDirectory = ctx.Destination,
            FileName = "move-active.bin",
            Source = "cli",
            QueueId = 0,
        });
        Assert.True(active.Accepted, active.Error);
        await ctx.WaitFor(active.Job!.Id, j => j.ReceivedBytes > 65_536);

        var moveActive = await ctx.Service.MoveToQueueAsync(active.Job.Id, 1);
        Assert.True(moveActive.Ok, moveActive.Error);
        var paused = await ctx.WaitFor(active.Job.Id, j => j.State == "paused");
        Assert.Equal(1, paused.QueueId);
        Assert.Equal("Night", paused.QueueName);

        var start = await ctx.Service.QueueActionAsync(new QueueActionRequest { QueueId = 1, Action = "start" });
        Assert.True(start.Ok, start.Error);
        var completed = await ctx.WaitFor(active.Job.Id, j => j.State == "completed", TimeSpan.FromSeconds(30));
        var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            TestContent.Generate(92, size))).ToLowerInvariant();
        Assert.Equal(expected, await Hashing.Sha256FileAsync(completed.FinalFilePath));
    }

    [Fact]
    public async Task DailyScheduleStartsAndStopsOnlyItsQueue()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server, maxConcurrent: 3);
        var settings = ctx.Service.GetSnapshot().Settings;
        settings.Queues.Add(new QueueDto
        {
            Id = 1,
            Name = "Night",
            MaxConcurrentDownloads = 2,
            IsRunning = false,
            Order = 1,
        });
        var scheduleId = Guid.NewGuid();
        settings.Schedules.Add(new ScheduleDto
        {
            Id = scheduleId,
            Name = "Work hours",
            QueueId = 1,
            Enabled = true,
            Kind = "daily",
            Days = "Wed",
            StartTime = "09:00",
            StopTime = "10:00",
        });
        var update = await ctx.Service.UpdateSettingsAsync(settings);
        Assert.True(update.Ok, update.Error);

        var wednesday = new DateTimeOffset(2026, 11, 4, 8, 59, 0, TimeSpan.Zero);
        Assert.Empty(await ctx.Service.EvaluateSchedulesAsync(wednesday));
        Assert.False(ctx.Service.GetSnapshot().Settings.Queues.Single(q => q.Id == 1).IsRunning);

        var started = await ctx.Service.EvaluateSchedulesAsync(wednesday.AddMinutes(1));
        var startAction = Assert.Single(started);
        Assert.Equal("start", startAction.Action);
        Assert.True(startAction.Ok, startAction.Error);
        var afterStart = ctx.Service.GetSnapshot().Settings;
        Assert.True(afterStart.Queues.Single(q => q.Id == 1).IsRunning);
        Assert.True(afterStart.Queues.Single(q => q.Id == 0).IsRunning);
        Assert.Equal(new DateOnly(2026, 11, 4), afterStart.Schedules.Single(s => s.Id == scheduleId).LastStartDate);

        var duplicate = await ctx.Service.EvaluateSchedulesAsync(wednesday.AddMinutes(30));
        Assert.DoesNotContain(duplicate, a => a.Action == "start");

        var job = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/slow/scheduled.bin?size=2097152&seed=93&bps=131072",
            DestinationDirectory = ctx.Destination,
            FileName = "scheduled.bin",
            Source = "cli",
            QueueId = 1,
        });
        Assert.True(job.Accepted, job.Error);
        await ctx.WaitFor(job.Job!.Id, j => j.ReceivedBytes > 65_536);

        var stopped = await ctx.Service.EvaluateSchedulesAsync(
            new DateTimeOffset(2026, 11, 4, 10, 0, 0, TimeSpan.Zero));
        var stopAction = Assert.Single(stopped);
        Assert.Equal("stop", stopAction.Action);
        Assert.True(stopAction.Ok, stopAction.Error);
        var afterStop = ctx.Service.GetSnapshot().Settings;
        Assert.False(afterStop.Queues.Single(q => q.Id == 1).IsRunning);
        Assert.Equal(new DateOnly(2026, 11, 4), afterStop.Schedules.Single(s => s.Id == scheduleId).LastStopDate);
        Assert.Equal("paused", (await ctx.WaitFor(job.Job.Id, j => j.State == "paused")).State);

        // Thursday is not selected: no start even though the clock says 09:00.
        var thursday = await ctx.Service.EvaluateSchedulesAsync(
            new DateTimeOffset(2026, 11, 5, 9, 0, 0, TimeSpan.Zero));
        Assert.Empty(thursday);
        Assert.False(ctx.Service.GetSnapshot().Settings.Queues.Single(q => q.Id == 1).IsRunning);
    }

    [Fact]
    public async Task OneTimeScheduleOnlyRunsOnItsDate()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var settings = ctx.Service.GetSnapshot().Settings;
        settings.Queues.Add(new QueueDto
        {
            Id = 1,
            Name = "Night",
            MaxConcurrentDownloads = 2,
            IsRunning = false,
            Order = 1,
        });
        var scheduleId = Guid.NewGuid();
        settings.Schedules.Add(new ScheduleDto
        {
            Id = scheduleId,
            Name = "One shot",
            QueueId = 1,
            Enabled = true,
            Kind = "oneTime",
            StartDate = new DateOnly(2026, 11, 4),
            StartTime = "09:00",
            StopTime = "10:00",
        });
        var update = await ctx.Service.UpdateSettingsAsync(settings);
        Assert.True(update.Ok, update.Error);

        Assert.Empty(await ctx.Service.EvaluateSchedulesAsync(
            new DateTimeOffset(2026, 11, 5, 9, 0, 0, TimeSpan.Zero)));
        Assert.False(ctx.Service.GetSnapshot().Settings.Queues.Single(q => q.Id == 1).IsRunning);

        var started = await ctx.Service.EvaluateSchedulesAsync(
            new DateTimeOffset(2026, 11, 4, 9, 0, 0, TimeSpan.Zero));
        Assert.Equal("start", Assert.Single(started).Action);

        var stopped = await ctx.Service.EvaluateSchedulesAsync(
            new DateTimeOffset(2026, 11, 4, 10, 0, 0, TimeSpan.Zero));
        Assert.Equal("stop", Assert.Single(stopped).Action);
        Assert.False(ctx.Service.GetSnapshot().Settings.Queues.Single(q => q.Id == 1).IsRunning);

        var cleared = ctx.Service.GetSnapshot().Settings;
        cleared.Schedules.Clear();
        var clearResult = await ctx.Service.UpdateSettingsAsync(cleared);
        Assert.True(clearResult.Ok, clearResult.Error);
        Assert.Empty(ctx.Service.GetSnapshot().Settings.Schedules);
    }

    [Fact]
    public async Task ScheduleRunStateSurvivesRestart()
    {
        var ctx = await ServiceContext.CreateAsync(_server, autoResume: false);
        try
        {
            var settings = ctx.Service.GetSnapshot().Settings;
            settings.Queues.Add(new QueueDto
            {
                Id = 1,
                Name = "Night",
                MaxConcurrentDownloads = 2,
                IsRunning = false,
                Order = 1,
            });
            var scheduleId = Guid.NewGuid();
            settings.Schedules.Add(new ScheduleDto
            {
                Id = scheduleId,
                Name = "Daily",
                QueueId = 1,
                Enabled = true,
                Kind = "daily",
                Days = "Mon,Tue,Wed,Thu,Fri,Sat,Sun",
                StartTime = "09:00",
                StopTime = "10:00",
            });
            Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);

            var mondayMorning = new DateTimeOffset(2026, 11, 9, 9, 0, 0, TimeSpan.Zero);
            Assert.Equal("start",
                Assert.Single(await ctx.Service.EvaluateSchedulesAsync(mondayMorning)).Action);

            // Simulate the user stopping the queue later that morning.
            Assert.True((await ctx.Service.QueueActionAsync(new QueueActionRequest
            {
                QueueId = 1,
                Action = "stop",
            })).Ok);

            await using var second = await ServiceContext.CreateAsync(
                _server, ctx.Paths, ctx.Destination, autoResume: false);
            var reloaded = second.Service.GetSnapshot().Settings.Schedules.Single(s => s.Id == scheduleId);
            Assert.Equal(new DateOnly(2026, 11, 9), reloaded.LastStartDate);

            // Same day: the persisted last-run date prevents a second start.
            var sameDay = await second.Service.EvaluateSchedulesAsync(
                new DateTimeOffset(2026, 11, 9, 9, 30, 0, TimeSpan.Zero));
            Assert.Empty(sameDay);
            Assert.False(second.Service.GetSnapshot().Settings.Queues.Single(q => q.Id == 1).IsRunning);

            // Next day: the daily schedule is eligible again.
            var nextDay = await second.Service.EvaluateSchedulesAsync(
                new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero));
            Assert.Equal("start", Assert.Single(nextDay).Action);
            Assert.True(second.Service.GetSnapshot().Settings.Queues.Single(q => q.Id == 1).IsRunning);
        }
        finally
        {
            await ctx.DisposeAsync();
        }
    }

    [Fact]
    public async Task ProxySettingsApplyToRuntimeDownloads()
    {
        await using var proxy = TestProxyServer.Start();
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var settings = ctx.Service.GetSnapshot().Settings;
        settings.Proxy = new ProxyDto
        {
            Mode = "manual",
            Address = proxy.Address,
            Username = "proxyuser",
            Password = "proxypass",
        };
        var update = await ctx.Service.UpdateSettingsAsync(settings);
        Assert.True(update.Ok, update.Error);
        Assert.Equal("manual", ctx.Service.GetSnapshot().Settings.Proxy.Mode);

        var response = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/file/runtime-proxy.bin?size=524288&seed=94",
            DestinationDirectory = ctx.Destination,
            FileName = "runtime-proxy.bin",
            Source = "cli",
        });
        Assert.True(response.Accepted, response.Error);
        var completed = await ctx.WaitFor(response.Job!.Id, j => j.State == "completed", TimeSpan.FromSeconds(30));
        Assert.True(proxy.Authorized > 0);
        Assert.Equal(0, proxy.Denied);
        var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            TestContent.Generate(94, 524_288))).ToLowerInvariant();
        Assert.Equal(expected, await Hashing.Sha256FileAsync(completed.FinalFilePath));
    }

    [Fact]
    public async Task PartialPayloadLivesOnDestinationVolume()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        const int size = 1_048_576;
        var response = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/slow/partloc.bin?size={size}&seed=90&bps=131072",
            DestinationDirectory = ctx.Destination,
            FileName = "partloc.bin",
            Source = "cli",
        });
        Assert.True(response.Accepted, response.Error);

        var job = await ctx.WaitFor(response.Job!.Id, j => j.ReceivedBytes > 65_536);
        Assert.StartsWith(ctx.Destination, job.PartFilePath, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(job.PartFilePath));
        Assert.Empty(Directory.EnumerateFiles(ctx.Paths.PartialsDirectory, "*.part"));

        var completed = await ctx.WaitFor(response.Job.Id, j => j.State == "completed", TimeSpan.FromSeconds(30));
        Assert.False(File.Exists(completed.PartFilePath));
        Assert.Equal(size, completed.ReceivedBytes);
        var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            TestContent.Generate(90, size))).ToLowerInvariant();
        Assert.Equal(expected, await Hashing.Sha256FileAsync(completed.FinalFilePath));
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
            var service = new DownloadManagerService(
                paths,
                store,
                checkpoints,
                settingsStore,
                engine,
                enableSchedulerTimer: false);
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
