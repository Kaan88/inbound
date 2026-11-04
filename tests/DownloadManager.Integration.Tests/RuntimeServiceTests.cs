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
    public async Task DescriptionEditPersistsAcrossRestart()
    {
        var ctx = await ServiceContext.CreateAsync(_server);
        try
        {
            var response = await ctx.Service.AddJobAsync(new AddJobRequest
            {
                Url = $"{_server.BaseAddress}/file/described.bin?size=65536&seed=240",
                DestinationDirectory = ctx.Destination,
                FileName = "described.bin",
                Source = "cli",
                StartMode = "later",
            });
            var jobId = response.Job!.Id;
            var update = await ctx.Service.UpdateJobAsync(new JobUpdateRequest
            {
                JobId = jobId,
                Description = "  quarterly report  ",
            });
            Assert.True(update.Ok, update.Error);
            Assert.Equal("quarterly report", update.Job!.Description);

            var missing = await ctx.Service.UpdateJobAsync(new JobUpdateRequest
            {
                JobId = Guid.NewGuid(),
                Description = "nope",
            });
            Assert.False(missing.Ok);

            var second = await ServiceContext.CreateAsync(_server, ctx.Paths, ctx.Destination);
            try
            {
                var reloaded = second.Service.GetJob(jobId);
                Assert.NotNull(reloaded);
                Assert.Equal("quarterly report", reloaded!.Description);
            }
            finally
            {
                await second.DisposeAsync();
            }
        }
        finally
        {
            await ctx.DisposeAsync();
        }
    }

    [Fact]
    public async Task FtpRequiresLoginAndResumesFromPartialBytes()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var ftpBase = $"ftp://127.0.0.1:{_server.Ftp.Port}";

        var denied = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{ftpBase}/auth.bin",
            DestinationDirectory = ctx.Destination,
            FileName = "ftp-denied.bin",
            Source = "cli",
        });
        var deniedJob = await ctx.WaitFor(denied.Job!.Id, j => j.State == "failed",
            TimeSpan.FromSeconds(30));
        Assert.Equal("unauthorized", deniedJob.Failure?.Kind);

        var settings = ctx.Service.GetSnapshot().Settings;
        settings.SiteLogins.Add(new SiteLoginDto
        {
            Site = ftpBase,
            Username = "ftpuser",
            Password = "ftppass",
        });
        var update = await ctx.Service.UpdateSettingsAsync(settings);
        Assert.True(update.Ok, update.Error);

        var plain = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{ftpBase}/plain.bin",
            DestinationDirectory = ctx.Destination,
            FileName = "ftp-plain.bin",
            Source = "cli",
        });
        var plainCompleted = await ctx.WaitFor(plain.Job!.Id, j => j.State == "completed",
            TimeSpan.FromSeconds(30));
        Assert.Equal(65_536, plainCompleted.ReceivedBytes);
        Assert.Equal(
            await Hashing.Sha256FileAsync(Path.Combine(_server.Ftp.Root, "plain.bin")),
            await Hashing.Sha256FileAsync(plainCompleted.FinalFilePath));

        var slow = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{ftpBase}/slow.bin",
            DestinationDirectory = ctx.Destination,
            FileName = "ftp-slow.bin",
            Source = "cli",
        });
        await ctx.WaitFor(slow.Job!.Id, j => j.ReceivedBytes > 131_072, TimeSpan.FromSeconds(30));
        Assert.True((await ctx.Service.PauseAsync(slow.Job.Id)).Ok);
        var paused = await ctx.WaitFor(slow.Job.Id, j => j.State == "paused");
        Assert.True(paused.ReceivedBytes > 0);
        Assert.True(File.Exists(paused.PartFilePath));
        var restBefore = _server.Ftp.RestCount;

        Assert.True((await ctx.Service.ResumeAsync(slow.Job.Id)).Ok);
        var resumed = await ctx.WaitFor(slow.Job.Id, j => j.State == "completed",
            TimeSpan.FromSeconds(60));
        Assert.Equal(524_288, resumed.ReceivedBytes);
        Assert.Equal(
            await Hashing.Sha256FileAsync(Path.Combine(_server.Ftp.Root, "slow.bin")),
            await Hashing.Sha256FileAsync(resumed.FinalFilePath));
        Assert.True(_server.Ftp.RestCount > restBefore);
        Assert.True(_server.Ftp.BytesServed("slow.bin") < 524_288 * 3 / 2);
    }

    [Fact]
    public async Task GrabberProjectPersistsAndResumesWithoutRedownload()
    {
        var ctx = await ServiceContext.CreateAsync(_server);
        try
        {
            var mirror = Path.Combine(ctx.Destination, "project-mirror");
            var created = await ctx.Service.CreateGrabberProjectAsync(new GrabberProjectCreateRequest
            {
                Name = "Docs mirror",
                Request = new SiteGrabberRequest
                {
                    StartUrl = $"{_server.BaseAddress}/grab/",
                    DestinationDirectory = mirror,
                    MaxDepth = 2,
                    MaxPages = 10,
                    Extensions = new List<string> { "bin" },
                    ExcludePatterns = new List<string> { "*/files/b.bin*" },
                    StartMode = "now",
                },
            });
            Assert.True(created.Ok, created.Error);
            var projectId = created.Project!.Id;

            var first = await ctx.Service.RunGrabberProjectAsync(projectId);
            Assert.True(first.Ok, first.Error);
            Assert.Equal("completed", first.Project!.Status);
            Assert.Equal(3, first.Project.PagesCrawled);
            Assert.Equal(2, first.Project.FilesQueued);
            Assert.Equal(1, first.Project.FilesSkipped);

            var jobs = ctx.Service.GetSnapshot().Jobs
                .Where(job => job.Url.Contains("/grab/files/", StringComparison.Ordinal))
                .ToList();
            Assert.Equal(2, jobs.Count);
            foreach (var job in jobs)
            {
                await ctx.WaitFor(job.Id, j => j.State == "completed", TimeSpan.FromSeconds(30));
            }

            var aPath = Path.Combine(mirror, "grab", "files", "a.bin");
            var aHash = await Hashing.Sha256FileAsync(aPath);
            var aModified = File.GetLastWriteTimeUtc(aPath);

            var second = await ServiceContext.CreateAsync(_server, ctx.Paths, ctx.Destination);
            try
            {
                var persisted = Assert.Single(second.Service.GetGrabberProjects().Projects);
                Assert.Equal(projectId, persisted.Id);
                Assert.Equal("completed", persisted.Status);
                Assert.Equal(2, persisted.FilesQueued);

                var jobsBefore = second.Service.GetSnapshot().Jobs.Count;
                var resume = await second.Service.RunGrabberProjectAsync(projectId);
                Assert.True(resume.Ok, resume.Error);
                Assert.Equal(0, resume.Project!.FilesQueued);
                Assert.True(resume.Project.FilesSkipped >= 3);
                Assert.Equal(jobsBefore, second.Service.GetSnapshot().Jobs.Count);
                Assert.Equal(aHash, await Hashing.Sha256FileAsync(aPath));
                Assert.Equal(aModified, File.GetLastWriteTimeUtc(aPath));

                var removed = await second.Service.RemoveGrabberProjectAsync(projectId);
                Assert.True(removed.Ok, removed.Error);
                Assert.Empty(second.Service.GetGrabberProjects().Projects);
            }
            finally
            {
                await second.DisposeAsync();
            }

            var badProject = await ctx.Service.CreateGrabberProjectAsync(new GrabberProjectCreateRequest
            {
                Name = "Bad start",
                Request = new SiteGrabberRequest
                {
                    StartUrl = $"{_server.BaseAddress}/grab/missing-page",
                    DestinationDirectory = Path.Combine(ctx.Destination, "bad-mirror"),
                    MaxDepth = 0,
                    MaxPages = 1,
                },
            });
            Assert.True(badProject.Ok, badProject.Error);
            var badRun = await ctx.Service.RunGrabberProjectAsync(badProject.Project!.Id);
            Assert.False(badRun.Ok);
            Assert.Equal("failed", badRun.Project!.Status);
            Assert.False(string.IsNullOrWhiteSpace(badRun.Project.LastError));
        }
        finally
        {
            await ctx.DisposeAsync();
        }
    }

    [Fact]
    public async Task ScheduledGrabberProjectRunsOncePerDay()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var mirror = Path.Combine(ctx.Destination, "scheduled-mirror");
        var created = await ctx.Service.CreateGrabberProjectAsync(new GrabberProjectCreateRequest
        {
            Name = "Scheduled mirror",
            Request = new SiteGrabberRequest
            {
                StartUrl = $"{_server.BaseAddress}/grab/",
                DestinationDirectory = mirror,
                MaxDepth = 1,
                MaxPages = 5,
                Extensions = new List<string> { "bin" },
                StartMode = "now",
            },
        });
        Assert.True(created.Ok, created.Error);

        var settings = ctx.Service.GetSnapshot().Settings;
        settings.Schedules.Add(new ScheduleDto
        {
            Id = Guid.NewGuid(),
            Name = "Nightly grab",
            QueueId = 0,
            GrabberProjectId = created.Project!.Id,
            Enabled = true,
            Kind = "daily",
            StartTime = "00:00",
            Days = "Mon,Tue,Wed,Thu,Fri,Sat,Sun",
        });
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);

        var now = new DateTimeOffset(DateTime.Today.AddHours(12));
        var actions = await ctx.Service.EvaluateSchedulesAsync(now);
        Assert.Contains(actions, action => action.Action == "grabber" && action.Ok);

        var deadline = DateTime.UtcNow.AddSeconds(20);
        GrabberProjectDto project;
        do
        {
            await Task.Delay(200);
            project = Assert.Single(ctx.Service.GetGrabberProjects().Projects);
        } while ((project.Status is "pending" or "running") && DateTime.UtcNow < deadline);

        Assert.Equal("completed", project.Status);
        Assert.True(project.FilesQueued > 0);

        var second = await ctx.Service.EvaluateSchedulesAsync(now);
        Assert.DoesNotContain(second, action => action.Action == "grabber");
    }

    [Fact]
    public async Task SiteGrabberCrawlsScopeAndRewritesLinks()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var mirror = Path.Combine(ctx.Destination, "mirror");
        var result = await ctx.Service.RunSiteGrabberAsync(new SiteGrabberRequest
        {
            StartUrl = $"{_server.BaseAddress}/grab/",
            DestinationDirectory = mirror,
            MaxDepth = 2,
            MaxPages = 10,
            SameHostOnly = true,
            Extensions = new List<string> { "bin" },
            ExcludePatterns = new List<string> { "*/files/b.bin*" },
            SavePages = true,
            RewriteLinks = true,
            StartMode = "now",
        });

        Assert.True(result.Ok, result.Error);
        Assert.Equal(3, result.PagesCrawled);
        Assert.Equal(2, result.FilesQueued);
        Assert.Equal(1, result.FilesSkipped);

        var jobs = ctx.Service.GetSnapshot().Jobs
            .Where(job => job.Url.Contains("/grab/files/", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(2, jobs.Count);
        foreach (var job in jobs)
        {
            await ctx.WaitFor(job.Id, j => j.State == "completed", TimeSpan.FromSeconds(30));
        }

        var filesDirectory = Path.Combine(mirror, "grab", "files");
        var aPath = Path.Combine(filesDirectory, "a.bin");
        var cPath = Path.Combine(filesDirectory, "c.bin");
        Assert.True(File.Exists(aPath));
        Assert.True(File.Exists(cPath));
        Assert.False(File.Exists(Path.Combine(filesDirectory, "b.bin")));
        Assert.Equal(65_536, new FileInfo(aPath).Length);
        Assert.Equal(
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                TestContent.Generate(290, 65_536))).ToLowerInvariant(),
            await Hashing.Sha256FileAsync(aPath));
        Assert.Equal(
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                TestContent.Generate(293, 65_536))).ToLowerInvariant(),
            await Hashing.Sha256FileAsync(cPath));

        var index = await File.ReadAllTextAsync(Path.Combine(mirror, "grab", "index.html"));
        Assert.Contains("href=\"page1.html\"", index);
        Assert.Contains("files/a.bin", index);
        Assert.DoesNotContain("/grab/files/a.bin", index);
        Assert.Contains("https://example.com/external", index);
        Assert.True(File.Exists(Path.Combine(mirror, "grab", "page1.html")));
        Assert.True(File.Exists(Path.Combine(mirror, "grab", "page2.html")));
        Assert.False(File.Exists(Path.Combine(mirror, "grab", "page3.html")));
    }

    [Fact]
    public async Task CategoryDestinationRulesAndLastFolderPersist()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var musicDirectory = Path.Combine(ctx.Destination, "music");
        var settings = ctx.Service.GetSnapshot().Settings;
        settings.CategoryFolders["music"] = musicDirectory;
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);

        var response = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/file/song.mp3?size=65536&seed=260",
            FileName = "song.mp3",
            Source = "cli",
        });
        var completed = await ctx.WaitFor(response.Job!.Id, j => j.State == "completed");
        Assert.StartsWith(musicDirectory, completed.FinalFilePath, StringComparison.OrdinalIgnoreCase);
        var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            TestContent.Generate(260, 65_536))).ToLowerInvariant();
        Assert.Equal(expected, await Hashing.Sha256FileAsync(completed.FinalFilePath));

        var explicitDestination = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/file/song2.mp3?size=65536&seed=261",
            FileName = "song2.mp3",
            DestinationDirectory = ctx.Destination,
            Source = "cli",
        });
        var explicitCompleted = await ctx.WaitFor(explicitDestination.Job!.Id, j => j.State == "completed");
        Assert.Equal(
            Path.GetFullPath(ctx.Destination),
            Path.GetFullPath(Path.GetDirectoryName(explicitCompleted.FinalFilePath)!));

        var snapshot = ctx.Service.GetSnapshot().Settings;
        Assert.Equal(Path.GetFullPath(ctx.Destination), Path.GetFullPath(snapshot.LastDestinationDirectory!));
        Assert.Equal(musicDirectory, snapshot.CategoryFolders["music"]);

        var second = await ServiceContext.CreateAsync(_server, ctx.Paths, ctx.Destination);
        try
        {
            var reloaded = second.Service.GetSnapshot().Settings;
            Assert.Equal(Path.GetFullPath(ctx.Destination), Path.GetFullPath(reloaded.LastDestinationDirectory!));
            Assert.Equal(musicDirectory, reloaded.CategoryFolders["music"]);
        }
        finally
        {
            await second.DisposeAsync();
        }
    }

    [Fact]
    public async Task ConflictPoliciesHandleExistingFile()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        const string name = "conflict.bin";
        const int size = 65_536;
        var existingPath = Path.Combine(ctx.Destination, name);
        await File.WriteAllTextAsync(existingPath, "old content");
        var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            TestContent.Generate(250, size))).ToLowerInvariant();

        var rename = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/file/{name}?size={size}&seed=250",
            DestinationDirectory = ctx.Destination,
            FileName = name,
            ConflictPolicy = "rename",
            Source = "cli",
        });
        var renamed = await ctx.WaitFor(rename.Job!.Id, j => j.State == "completed");
        Assert.EndsWith("conflict (1).bin", renamed.FinalFilePath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("old content", await File.ReadAllTextAsync(existingPath));
        Assert.Equal(expected, await Hashing.Sha256FileAsync(renamed.FinalFilePath));

        var fail = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/file/{name}?size={size}&seed=250",
            DestinationDirectory = ctx.Destination,
            FileName = name,
            ConflictPolicy = "fail",
            Source = "cli",
        });
        Assert.False(fail.Accepted);
        Assert.Contains("already exists", fail.Error, StringComparison.OrdinalIgnoreCase);

        var overwrite = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/file/{name}?size={size}&seed=250",
            DestinationDirectory = ctx.Destination,
            FileName = name,
            ConflictPolicy = "overwrite",
            Source = "cli",
        });
        Assert.True(overwrite.Accepted, overwrite.Error);
        var overwritten = await ctx.WaitFor(overwrite.Job!.Id, j => j.State == "completed");
        Assert.Equal(existingPath, overwritten.FinalFilePath);
        Assert.Equal(expected, await Hashing.Sha256FileAsync(existingPath));
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
    public async Task Socks5SettingsApplyToRuntimeDownloads()
    {
        await using var socks = TestSocks5Server.Start();
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var settings = ctx.Service.GetSnapshot().Settings;
        settings.Proxy = new ProxyDto
        {
            Mode = "manual",
            Address = $"socks5://{socks.Address}",
            Username = "socksuser",
            Password = "sockspass",
        };
        var update = await ctx.Service.UpdateSettingsAsync(settings);
        Assert.True(update.Ok, update.Error);

        var response = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/file/runtime-socks5.bin?size=524288&seed=95",
            DestinationDirectory = ctx.Destination,
            FileName = "runtime-socks5.bin",
            Source = "cli",
        });
        Assert.True(response.Accepted, response.Error);
        var completed = await ctx.WaitFor(response.Job!.Id, j => j.State == "completed", TimeSpan.FromSeconds(30));
        Assert.True(socks.Authorized > 0);
        Assert.True(socks.Connects > 0);
        Assert.Equal(0, socks.Denied);
        var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            TestContent.Generate(95, 524_288))).ToLowerInvariant();
        Assert.Equal(expected, await Hashing.Sha256FileAsync(completed.FinalFilePath));
    }

    [Fact]
    public async Task StoredSiteLoginAuthenticatesAndSurvivesRestart()
    {
        _server.SetSiteCredentials("siteuser", "sitepass");
        _server.ResetSiteAuthStats();
        var ctx = await ServiceContext.CreateAsync(_server);
        try
        {
            // Without a stored login the secure endpoint must fail cleanly.
            var denied = await ctx.Service.AddJobAsync(new AddJobRequest
            {
                Url = $"{_server.BaseAddress}/site/secure/no-login.bin?size=65536&seed=97",
                DestinationDirectory = ctx.Destination,
                FileName = "no-login.bin",
                Source = "cli",
            });
            var deniedJob = await ctx.WaitFor(denied.Job!.Id, j => j.State == "failed");
            Assert.Equal("unauthorized", deniedJob.Failure?.Kind);
            Assert.True(_server.SiteAuthDenied > 0);
            var deniedBefore = _server.SiteAuthDenied;

            var settings = ctx.Service.GetSnapshot().Settings;
            settings.SiteLogins.Add(new SiteLoginDto
            {
                Id = Guid.NewGuid(),
                Site = _server.BaseAddress,
                Username = "siteuser",
                Password = "sitepass",
            });
            var update = await ctx.Service.UpdateSettingsAsync(settings);
            Assert.True(update.Ok, update.Error);

            var readBack = Assert.Single(ctx.Service.GetSnapshot().Settings.SiteLogins);
            Assert.Null(readBack.Password);
            Assert.True(readBack.HasPassword);
            var settingsJson = await File.ReadAllTextAsync(ctx.Paths.SettingsFile);
            Assert.DoesNotContain("sitepass", settingsJson);
            Assert.Contains("dpapi:v1:", settingsJson);

            var authorizedBefore = _server.SiteAuthAuthorized;
            var response = await ctx.Service.AddJobAsync(new AddJobRequest
            {
                Url = $"{_server.BaseAddress}/site/secure/site-login.bin?size=2097152&seed=98",
                DestinationDirectory = ctx.Destination,
                FileName = "site-login.bin",
                Source = "cli",
                Connections = 2,
            });
            Assert.True(response.Accepted, response.Error);
            var completed = await ctx.WaitFor(response.Job!.Id, j => j.State == "completed",
                TimeSpan.FromSeconds(30));
            var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                TestContent.Generate(98, 2_097_152))).ToLowerInvariant();
            Assert.Equal(expected, await Hashing.Sha256FileAsync(completed.FinalFilePath));
            Assert.True(_server.SiteAuthAuthorized >= authorizedBefore + 2);
            Assert.Equal(deniedBefore, _server.SiteAuthDenied);

            // A fresh service instance loads the DPAPI-protected login from disk.
            await using var second = await ServiceContext.CreateAsync(
                _server, ctx.Paths, ctx.Destination, autoResume: false);
            var secondLogin = Assert.Single(second.Service.GetSnapshot().Settings.SiteLogins);
            Assert.Equal("siteuser", secondLogin.Username);
            Assert.True(secondLogin.HasPassword);
            var response2 = await second.Service.AddJobAsync(new AddJobRequest
            {
                Url = $"{_server.BaseAddress}/site/secure/site-login2.bin?size=262144&seed=99",
                DestinationDirectory = ctx.Destination,
                FileName = "site-login2.bin",
                Source = "cli",
            });
            var completed2 = await second.WaitFor(response2.Job!.Id, j => j.State == "completed",
                TimeSpan.FromSeconds(30));
            var expected2 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                TestContent.Generate(99, 262_144))).ToLowerInvariant();
            Assert.Equal(expected2, await Hashing.Sha256FileAsync(completed2.FinalFilePath));
        }
        finally
        {
            await ctx.DisposeAsync();
        }
    }

    [Fact]
    public async Task RefreshDownloadAddressResumesAfterExpiredToken()
    {
        const string name = "refresh-token.bin";
        const int size = 1_048_576;
        const int seed = 96;
        _server.ResetExpiringStats();
        _server.SetVersion(name, 1);
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var response = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/expiring/{name}?token=1&size={size}&seed={seed}&bps=262144",
            DestinationDirectory = ctx.Destination,
            FileName = name,
            Source = "cli",
        });
        Assert.True(response.Accepted, response.Error);

        var partial = await ctx.WaitFor(response.Job!.Id, j => j.ReceivedBytes > 65_536);
        var pause = await ctx.Service.PauseAsync(partial.Id);
        Assert.True(pause.Ok, pause.Error);
        var paused = await ctx.WaitFor(partial.Id, j => j.State == "paused");
        var bytesBeforeExpiry = paused.ReceivedBytes;
        Assert.True(bytesBeforeExpiry > 0);
        Assert.True(File.Exists(paused.PartFilePath));

        // Rotate the server token: the old URL now returns 403 + X-Url-Expired.
        _server.SetVersion(name, 2);
        var resume = await ctx.Service.ResumeAsync(partial.Id);
        Assert.True(resume.Ok, resume.Error);
        var expired = await ctx.WaitFor(partial.Id, j => j.State == "failed");
        Assert.Equal("expiredUrl", expired.Failure?.Kind);
        Assert.True(expired.Failure?.IsResumable);
        Assert.True(expired.ReceivedBytes >= bytesBeforeExpiry);
        Assert.True(File.Exists(expired.PartFilePath));

        var badRefresh = await ctx.Service.RefreshUrlAsync(partial.Id, "not-a-url");
        Assert.False(badRefresh.Ok);

        var newUrl = $"{_server.BaseAddress}/expiring/{name}?token=2&size={size}&seed={seed}&bps=262144";
        var refreshed = await ctx.Service.RefreshUrlAsync(partial.Id, newUrl);
        Assert.True(refreshed.Ok, refreshed.Error);
        Assert.Contains("token=2", refreshed.Job!.Url);
        Assert.True(refreshed.Job.ReceivedBytes >= bytesBeforeExpiry);

        var completed = await ctx.WaitFor(partial.Id, j => j.State == "completed", TimeSpan.FromSeconds(30));
        var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            TestContent.Generate(seed, size))).ToLowerInvariant();
        Assert.Equal(expected, await Hashing.Sha256FileAsync(completed.FinalFilePath));
        Assert.True(_server.ExpiringExpired > 0);
        Assert.True(_server.ExpiringRangeRequests > 0);
    }

    [Fact]
    public async Task DownloadLaterWaitsForExplicitStart()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var response = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/file/later.bin?size=262144&seed=122",
            DestinationDirectory = ctx.Destination,
            FileName = "later.bin",
            Source = "cli",
            StartMode = "later",
        });
        Assert.True(response.Accepted, response.Error);
        await Task.Delay(300);
        Assert.Equal("queued", ctx.Service.GetJob(response.Job!.Id)!.State);
        Assert.False(File.Exists(Path.Combine(ctx.Destination, "later.bin")));

        Assert.True((await ctx.Service.QueueActionAsync(new QueueActionRequest
        {
            QueueId = 0,
            Action = "stop",
        })).Ok);
        Assert.True((await ctx.Service.QueueActionAsync(new QueueActionRequest
        {
            QueueId = 0,
            Action = "start",
        })).Ok);
        await Task.Delay(300);
        Assert.Equal("queued", ctx.Service.GetJob(response.Job.Id)!.State);

        Assert.True((await ctx.Service.ResumeAsync(response.Job.Id)).Ok);
        var completed = await ctx.WaitFor(response.Job.Id, j => j.State == "completed",
            TimeSpan.FromSeconds(30));
        var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            TestContent.Generate(122, 262_144))).ToLowerInvariant();
        Assert.Equal(expected, await Hashing.Sha256FileAsync(completed.FinalFilePath));
    }

    [Fact]
    public async Task SleepPreventionFollowsActiveDownloads()
    {
        var sleep = new RecordingSleepPrevention();
        await using var ctx = await ServiceContext.CreateAsync(_server, sleepPrevention: sleep);
        var response = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/slow/sleep.bin?size=1048576&seed=119&bps=131072",
            DestinationDirectory = ctx.Destination,
            FileName = "sleep.bin",
            Source = "cli",
        });
        await ctx.WaitFor(response.Job!.Id, j => j.ReceivedBytes > 65536);
        await WaitForSleepAsync(sleep, expected: true);
        Assert.Contains(true, sleep.States);

        Assert.True((await ctx.Service.PauseAsync(response.Job.Id)).Ok);
        await ctx.WaitFor(response.Job.Id, j => j.State == "paused");
        await WaitForSleepAsync(sleep, expected: false);

        Assert.True((await ctx.Service.ResumeAsync(response.Job.Id)).Ok);
        await ctx.WaitFor(response.Job.Id, j => j.State == "downloading");
        await WaitForSleepAsync(sleep, expected: true);

        await ctx.WaitFor(response.Job.Id, j => j.State == "completed", TimeSpan.FromSeconds(30));
        await WaitForSleepAsync(sleep, expected: false);
        Assert.True(sleep.Activations >= 2);
        Assert.True(sleep.Releases >= 2);
    }

    [Fact]
    public async Task SleepPreventionSettingCanDisableAndReenable()
    {
        var sleep = new RecordingSleepPrevention();
        await using var ctx = await ServiceContext.CreateAsync(_server, sleepPrevention: sleep);
        var settings = ctx.Service.GetSnapshot().Settings;
        settings.PreventSleepWhileDownloading = false;
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);

        var response = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/slow/sleep-off.bin?size=1048576&seed=120&bps=131072",
            DestinationDirectory = ctx.Destination,
            FileName = "sleep-off.bin",
            Source = "cli",
        });
        await ctx.WaitFor(response.Job!.Id, j => j.ReceivedBytes > 65536);
        await WaitForSleepAsync(sleep, expected: false);

        var enabled = ctx.Service.GetSnapshot().Settings;
        enabled.PreventSleepWhileDownloading = true;
        Assert.True((await ctx.Service.UpdateSettingsAsync(enabled)).Ok);
        await WaitForSleepAsync(sleep, expected: true);

        await ctx.Service.CancelAsync(response.Job.Id, deleteFile: true);
        await WaitForSleepAsync(sleep, expected: false);
    }

    [Fact]
    public async Task ScheduleHandlesRepeatedDstHour()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var settings = ctx.Service.GetSnapshot().Settings;
        settings.Queues.Add(new QueueDto
        {
            Id = 1,
            Name = "Night",
            MaxConcurrentDownloads = 1,
            IsRunning = false,
            Order = 1,
        });
        settings.Schedules.Add(new ScheduleDto
        {
            Id = Guid.NewGuid(),
            Name = "DST",
            QueueId = 1,
            Enabled = true,
            Kind = "daily",
            Days = "Sun",
            StartTime = "01:30",
            StopTime = "03:00",
        });
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);

        var firstHour = new DateTimeOffset(2026, 11, 1, 1, 30, 0, TimeSpan.FromHours(-4));
        Assert.Equal("start", Assert.Single(await ctx.Service.EvaluateSchedulesAsync(firstHour)).Action);
        Assert.True(ctx.Service.GetSnapshot().Settings.Queues.Single(q => q.Id == 1).IsRunning);

        Assert.True((await ctx.Service.QueueActionAsync(new QueueActionRequest
        {
            QueueId = 1,
            Action = "stop",
        })).Ok);

        var repeatedHour = new DateTimeOffset(2026, 11, 1, 1, 30, 0, TimeSpan.FromHours(-5));
        Assert.Empty(await ctx.Service.EvaluateSchedulesAsync(repeatedHour));
        Assert.False(ctx.Service.GetSnapshot().Settings.Queues.Single(q => q.Id == 1).IsRunning);

        var nextSunday = new DateTimeOffset(2026, 11, 8, 1, 30, 0, TimeSpan.FromHours(-5));
        Assert.Equal("start", Assert.Single(await ctx.Service.EvaluateSchedulesAsync(nextSunday)).Action);
    }

    [Fact]
    public async Task ScheduleHandlesOvernightStopAcrossDst()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var settings = ctx.Service.GetSnapshot().Settings;
        settings.Queues.Add(new QueueDto
        {
            Id = 1,
            Name = "Night",
            MaxConcurrentDownloads = 1,
            IsRunning = false,
            Order = 1,
        });
        settings.Schedules.Add(new ScheduleDto
        {
            Id = Guid.NewGuid(),
            Name = "Overnight",
            QueueId = 1,
            Enabled = true,
            Kind = "daily",
            Days = "Sat,Sun",
            StartTime = "23:00",
            StopTime = "07:00",
        });
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);

        var start = new DateTimeOffset(2026, 3, 7, 23, 0, 0, TimeSpan.FromHours(-5));
        Assert.Equal("start", Assert.Single(await ctx.Service.EvaluateSchedulesAsync(start)).Action);

        var stop = new DateTimeOffset(2026, 3, 8, 7, 0, 0, TimeSpan.FromHours(-4));
        Assert.Equal("stop", Assert.Single(await ctx.Service.EvaluateSchedulesAsync(stop)).Action);
        Assert.False(ctx.Service.GetSnapshot().Settings.Queues.Single(q => q.Id == 1).IsRunning);
    }

    [Fact]
    public async Task QueueQuotaPausesDownloadsAndBlocksNewStarts()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server, maxConcurrent: 2);
        var settings = ctx.Service.GetSnapshot().Settings;
        var queue = settings.Queues.Single(q => q.Id == 0);
        queue.QuotaEnabled = true;
        queue.QuotaBytes = 524_288;
        queue.QuotaWindowHours = 24;
        queue.QuotaWindowStart = DateTimeOffset.UtcNow;
        queue.QuotaBytesUsed = 0;
        queue.MaxConcurrentDownloads = 2;
        var update = await ctx.Service.UpdateSettingsAsync(settings);
        Assert.True(update.Ok, update.Error);

        const int size = 1_048_576;
        var first = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/slow/quota1.bin?size={size}&seed=110&bps=131072",
            DestinationDirectory = ctx.Destination,
            FileName = "quota1.bin",
            Source = "cli",
        });
        var paused = await ctx.WaitFor(first.Job!.Id, j => j.State == "paused", TimeSpan.FromSeconds(30));
        var afterQuota = ctx.Service.GetSnapshot().Settings.Queues.Single(q => q.Id == 0);
        Assert.True(afterQuota.QuotaBytesUsed >= afterQuota.QuotaBytes);
        Assert.False(afterQuota.IsRunning);
        Assert.True(paused.ReceivedBytes > 0);
        Assert.True(File.Exists(paused.PartFilePath));

        var second = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/file/quota2.bin?size=262144&seed=111",
            DestinationDirectory = ctx.Destination,
            FileName = "quota2.bin",
            Source = "cli",
        });
        await Task.Delay(400);
        Assert.Equal("queued", ctx.Service.GetJob(second.Job!.Id)!.State);

        // A fresh window (and a larger limit) lets the queue resume.
        var reset = ctx.Service.GetSnapshot().Settings;
        var resetQueue = reset.Queues.Single(q => q.Id == 0);
        resetQueue.QuotaWindowStart = DateTimeOffset.UtcNow.AddHours(-25);
        resetQueue.QuotaBytes = 10 * 1024 * 1024;
        Assert.True((await ctx.Service.UpdateSettingsAsync(reset)).Ok);
        var start = await ctx.Service.QueueActionAsync(new QueueActionRequest { QueueId = 0, Action = "start" });
        Assert.True(start.Ok, start.Error);

        await ctx.WaitFor(first.Job.Id, j => j.State == "completed", TimeSpan.FromSeconds(60));
        await ctx.WaitFor(second.Job.Id, j => j.State == "completed", TimeSpan.FromSeconds(60));
        var firstHash = await Hashing.Sha256FileAsync(Path.Combine(ctx.Destination, "quota1.bin"));
        var expectedFirst = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            TestContent.Generate(110, size))).ToLowerInvariant();
        Assert.Equal(expectedFirst, firstHash);
    }

    [Fact]
    public async Task CompletionActionsFireAfterQueueFinishes()
    {
        var executor = new RecordingCompletionExecutor();
        await using var ctx = await ServiceContext.CreateAsync(_server, maxConcurrent: 2, completionActions: executor);
        var settings = ctx.Service.GetSnapshot().Settings;
        settings.AllowSystemCompletionActions = true;
        settings.Schedules.Add(new ScheduleDto
        {
            Id = Guid.NewGuid(),
            Name = "Completion",
            QueueId = 0,
            Enabled = true,
            Kind = "daily",
            StartTime = "00:00",
            OpenFileWhenDone = true,
            ExitWhenDone = true,
        });
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);

        var first = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/file/action1.bin?size=131072&seed=112",
            DestinationDirectory = ctx.Destination,
            FileName = "action1.bin",
            Source = "cli",
        });
        var second = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/file/action2.bin?size=131072&seed=113",
            DestinationDirectory = ctx.Destination,
            FileName = "action2.bin",
            Source = "cli",
        });
        await ctx.WaitFor(first.Job!.Id, j => j.State == "completed", TimeSpan.FromSeconds(30));
        await ctx.WaitFor(second.Job!.Id, j => j.State == "completed", TimeSpan.FromSeconds(30));
        await Task.Delay(300);

        Assert.Single(executor.OpenedPaths);
        Assert.Equal(1, executor.ExitCount);
        Assert.Equal(0, executor.ShutdownCount);
    }

    [Fact]
    public async Task SystemCompletionActionsRequireOptInAndNoActiveDownloads()
    {
        var executor = new RecordingCompletionExecutor();
        await using var ctx = await ServiceContext.CreateAsync(_server, maxConcurrent: 2, completionActions: executor);
        var settings = ctx.Service.GetSnapshot().Settings;
        settings.Schedules.Add(new ScheduleDto
        {
            Id = Guid.NewGuid(),
            Name = "Exit",
            QueueId = 0,
            Enabled = true,
            Kind = "daily",
            StartTime = "00:00",
            ExitWhenDone = true,
        });
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);

        var first = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/file/exit1.bin?size=131072&seed=114",
            DestinationDirectory = ctx.Destination,
            FileName = "exit1.bin",
            Source = "cli",
        });
        await ctx.WaitFor(first.Job!.Id, j => j.State == "completed", TimeSpan.FromSeconds(30));
        await Task.Delay(200);
        Assert.Equal(0, executor.ExitCount);

        var enabled = ctx.Service.GetSnapshot().Settings;
        enabled.AllowSystemCompletionActions = true;
        enabled.Queues.Add(new QueueDto
        {
            Id = 1,
            Name = "Other",
            MaxConcurrentDownloads = 1,
            IsRunning = true,
            Order = 1,
        });
        Assert.True((await ctx.Service.UpdateSettingsAsync(enabled)).Ok);

        var slow = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/slow/other.bin?size=2097152&seed=115&bps=131072",
            DestinationDirectory = ctx.Destination,
            FileName = "other.bin",
            Source = "cli",
            QueueId = 1,
        });
        await ctx.WaitFor(slow.Job!.Id, j => j.ReceivedBytes > 65536);
        var blocked = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/file/exit2.bin?size=131072&seed=116",
            DestinationDirectory = ctx.Destination,
            FileName = "exit2.bin",
            Source = "cli",
        });
        await ctx.WaitFor(blocked.Job!.Id, j => j.State == "completed", TimeSpan.FromSeconds(30));
        await Task.Delay(200);
        Assert.Equal(0, executor.ExitCount);

        await ctx.Service.CancelAsync(slow.Job.Id, deleteFile: true);
        var finalJob = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/file/exit3.bin?size=131072&seed=117",
            DestinationDirectory = ctx.Destination,
            FileName = "exit3.bin",
            Source = "cli",
        });
        await ctx.WaitFor(finalJob.Job!.Id, j => j.State == "completed", TimeSpan.FromSeconds(30));
        await Task.Delay(300);
        Assert.Equal(1, executor.ExitCount);
    }

    [Fact]
    public async Task CompletionActionFailureDoesNotFailTheDownload()
    {
        var executor = new RecordingCompletionExecutor { ThrowOnOpen = true };
        await using var ctx = await ServiceContext.CreateAsync(_server, completionActions: executor);
        var settings = ctx.Service.GetSnapshot().Settings;
        settings.AllowSystemCompletionActions = true;
        settings.Schedules.Add(new ScheduleDto
        {
            Id = Guid.NewGuid(),
            Name = "Open",
            QueueId = 0,
            Enabled = true,
            Kind = "daily",
            StartTime = "00:00",
            OpenFileWhenDone = true,
            ShutdownWhenDone = true,
            ShutdownMode = "restart",
            ForceShutdown = true,
        });
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);

        var job = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/file/action-fail.bin?size=131072&seed=118",
            DestinationDirectory = ctx.Destination,
            FileName = "action-fail.bin",
            Source = "cli",
        });
        var completed = await ctx.WaitFor(job.Job!.Id, j => j.State == "completed", TimeSpan.FromSeconds(30));
        await Task.Delay(300);
        Assert.Equal("completed", completed.State);
        Assert.Equal(1, executor.OpenAttempts);
        Assert.Equal(1, executor.ShutdownCount);
        Assert.Equal("restart", executor.LastShutdownMode);
        Assert.True(executor.LastForce);
    }

    [Fact]
    public async Task LiveStreamsAreRejectedExplicitly()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        foreach (var (path, name) in new[]
                 {
                     ("/live/playlist.m3u8", "live.m3u8"),
                     ("/live/manifest.mpd", "live.mpd"),
                 })
        {
            var response = await ctx.Service.AddJobAsync(new AddJobRequest
            {
                Url = $"{_server.BaseAddress}{path}",
                DestinationDirectory = ctx.Destination,
                FileName = name,
                Source = "cli",
            });
            Assert.True(response.Accepted, response.Error);
            var failed = await ctx.WaitFor(response.Job!.Id, j => j.State == "failed",
                TimeSpan.FromSeconds(30));
            Assert.Equal("liveStream", failed.Failure?.Kind);
            Assert.Contains("Live", failed.Failure?.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(failed.Failure?.IsRetryable);
            Assert.False(failed.Failure?.IsResumable);
            Assert.False(failed.CanResume);
            Assert.True(failed.IsTerminal);
            Assert.False(File.Exists(failed.FinalFilePath));
            Assert.False(File.Exists(failed.PartFilePath));
        }
    }

    [Fact]
    public async Task DrmProtectedMediaIsIdentifiedAndNotBypassed()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        foreach (var (path, name) in new[]
                 {
                     ("/drm/hls.m3u8", "drm.m3u8"),
                     ("/drm/manifest.mpd", "drm.mpd"),
                 })
        {
            var response = await ctx.Service.AddJobAsync(new AddJobRequest
            {
                Url = $"{_server.BaseAddress}{path}",
                DestinationDirectory = ctx.Destination,
                FileName = name,
                Source = "cli",
            });
            Assert.True(response.Accepted, response.Error);
            var failed = await ctx.WaitFor(response.Job!.Id, j => j.State == "failed",
                TimeSpan.FromSeconds(30));
            Assert.Equal("drmProtected", failed.Failure?.Kind);
            Assert.Contains("DRM", failed.Failure?.Message);
            Assert.False(failed.Failure?.IsRetryable);
            Assert.False(failed.Failure?.IsResumable);
            Assert.False(failed.CanResume);
            Assert.True(failed.IsTerminal);
            Assert.False(File.Exists(failed.FinalFilePath));
            Assert.False(File.Exists(failed.PartFilePath));
        }
    }

    [Fact]
    public async Task HlsMasterAndMediaPlaylistsAssembleSegments()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        using var http = new HttpClient();
        using var expectedJson = System.Text.Json.JsonDocument.Parse(
            await http.GetStringAsync($"{_server.BaseAddress}/hls/expected"));
        var expectedHash = expectedJson.RootElement.GetProperty("sha256").GetString();
        var expectedBytes = expectedJson.RootElement.GetProperty("bytes").GetInt64();

        foreach (var playlist in new[] { "master.m3u8", "media.m3u8" })
        {
            var name = $"hls-{Path.GetFileNameWithoutExtension(playlist)}.m3u8";
            var response = await ctx.Service.AddJobAsync(new AddJobRequest
            {
                Url = $"{_server.BaseAddress}/hls/{playlist}",
                DestinationDirectory = ctx.Destination,
                FileName = name,
                Source = "cli",
            });
            Assert.True(response.Accepted, response.Error);
            var completed = await ctx.WaitFor(response.Job!.Id, j => j.State == "completed",
                TimeSpan.FromSeconds(30));
            Assert.EndsWith(".ts", completed.FinalFilePath, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(expectedBytes, completed.ReceivedBytes);
            Assert.Equal(expectedBytes, new FileInfo(completed.FinalFilePath).Length);
            Assert.Equal(expectedHash, await Hashing.Sha256FileAsync(completed.FinalFilePath));
        }
    }

    [Fact]
    public async Task DashManifestDownloadsInitAndMediaSegments()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        using var http = new HttpClient();
        using var expectedJson = System.Text.Json.JsonDocument.Parse(
            await http.GetStringAsync($"{_server.BaseAddress}/dash/expected"));
        var expectedHash = expectedJson.RootElement.GetProperty("sha256").GetString();
        var expectedBytes = expectedJson.RootElement.GetProperty("bytes").GetInt64();

        var response = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/dash/manifest.mpd",
            DestinationDirectory = ctx.Destination,
            FileName = "dash-test.mpd",
            Source = "cli",
        });
        Assert.True(response.Accepted, response.Error);
        var completed = await ctx.WaitFor(response.Job!.Id, j => j.State == "completed",
            TimeSpan.FromSeconds(30));
        Assert.EndsWith(".mp4", completed.FinalFilePath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(expectedBytes, completed.ReceivedBytes);
        Assert.Equal(expectedBytes, new FileInfo(completed.FinalFilePath).Length);
        Assert.Equal(expectedHash, await Hashing.Sha256FileAsync(completed.FinalFilePath));
    }

    [Fact]
    public async Task HlsResumesAtCompletedSegmentAfterInterruption()
    {
        _server.ResetHlsStats();
        await using var ctx = await ServiceContext.CreateAsync(_server);
        using var http = new HttpClient();
        using var expectedJson = System.Text.Json.JsonDocument.Parse(
            await http.GetStringAsync($"{_server.BaseAddress}/hls/expected"));
        var expectedHash = expectedJson.RootElement.GetProperty("sha256").GetString();

        var response = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/hls/recovery.m3u8",
            DestinationDirectory = ctx.Destination,
            FileName = "hls-recovery.m3u8",
            Source = "cli",
        });
        Assert.True(response.Accepted, response.Error);
        await ctx.WaitFor(response.Job!.Id, j => j.ReceivedBytes > 210_000, TimeSpan.FromSeconds(30));
        Assert.True((await ctx.Service.PauseAsync(response.Job.Id)).Ok);
        var paused = await ctx.WaitFor(response.Job.Id, j => j.State == "paused");
        Assert.True(paused.ReceivedBytes > 0);
        var seg0AfterPause = _server.HlsSegmentRequests("seg0.ts");
        var seg2AfterPause = _server.HlsSegmentRequests("seg2.ts");

        Assert.True((await ctx.Service.ResumeAsync(response.Job.Id)).Ok);
        var completed = await ctx.WaitFor(response.Job.Id, j => j.State == "completed",
            TimeSpan.FromSeconds(60));
        Assert.Equal(expectedHash, await Hashing.Sha256FileAsync(completed.FinalFilePath));
        Assert.Equal(1, _server.HlsSegmentRequests("seg0.ts")); // completed fragment not repeated
        Assert.True(_server.HlsSegmentRequests("seg1.ts") >= 2 ||
                    _server.HlsSegmentRequests("seg2.ts") >= 2);
        Assert.True(_server.HlsSegmentRequests("seg2.ts") >= seg2AfterPause);
        Assert.Equal(1, seg0AfterPause);
    }

    [Fact]
    public async Task DashResumesAtCompletedSegmentAfterInterruption()
    {
        _server.ResetHlsStats();
        await using var ctx = await ServiceContext.CreateAsync(_server);
        using var http = new HttpClient();
        using var expectedJson = System.Text.Json.JsonDocument.Parse(
            await http.GetStringAsync($"{_server.BaseAddress}/dash/expected"));
        var expectedHash = expectedJson.RootElement.GetProperty("sha256").GetString();

        var response = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/dash/recovery.mpd",
            DestinationDirectory = ctx.Destination,
            FileName = "dash-recovery.mpd",
            Source = "cli",
        });
        Assert.True(response.Accepted, response.Error);
        await ctx.WaitFor(response.Job!.Id, j => j.ReceivedBytes > 184_000, TimeSpan.FromSeconds(30));
        Assert.True((await ctx.Service.PauseAsync(response.Job.Id)).Ok);
        await ctx.WaitFor(response.Job.Id, j => j.State == "paused");
        var chunk1AfterPause = _server.DashSegmentRequests("chunk-0-00001.m4s");

        Assert.True((await ctx.Service.ResumeAsync(response.Job.Id)).Ok);
        var completed = await ctx.WaitFor(response.Job.Id, j => j.State == "completed",
            TimeSpan.FromSeconds(60));
        Assert.Equal(expectedHash, await Hashing.Sha256FileAsync(completed.FinalFilePath));
        Assert.Equal(1, _server.DashSegmentRequests("chunk-0-00001.m4s"));
        Assert.True(_server.DashSegmentRequests("chunk-0-00002.m4s") >= 2 ||
                    _server.DashSegmentRequests("chunk-0-00003.m4s") >= 2);
        Assert.Equal(1, chunk1AfterPause);
    }

    [Fact]
    public async Task HlsRefreshesExpiredSegmentTokens()
    {
        _server.ResetHlsStats();
        _server.ResetRotatingMedia();
        await using var ctx = await ServiceContext.CreateAsync(_server);
        using var http = new HttpClient();
        using var expectedJson = System.Text.Json.JsonDocument.Parse(
            await http.GetStringAsync($"{_server.BaseAddress}/hls/expected"));
        var expectedHash = expectedJson.RootElement.GetProperty("sha256").GetString();

        var response = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/hls-rotating/playlist.m3u8",
            DestinationDirectory = ctx.Destination,
            FileName = "hls-expiring.m3u8",
            Source = "cli",
        });
        Assert.True(response.Accepted, response.Error);
        var completed = await ctx.WaitFor(response.Job!.Id, j => j.State == "completed",
            TimeSpan.FromSeconds(30));
        Assert.Equal(expectedHash, await Hashing.Sha256FileAsync(completed.FinalFilePath));
        Assert.Equal(1, _server.HlsSegmentRequests("rotating-seg0.ts"));
        Assert.True(_server.HlsSegmentRequests("rotating-seg1.ts") >= 1);
    }

    [Fact]
    public async Task DashRefreshesExpiredSegmentTokens()
    {
        _server.ResetHlsStats();
        _server.ResetRotatingMedia();
        await using var ctx = await ServiceContext.CreateAsync(_server);
        using var http = new HttpClient();
        using var expectedJson = System.Text.Json.JsonDocument.Parse(
            await http.GetStringAsync($"{_server.BaseAddress}/dash/expected"));
        var expectedHash = expectedJson.RootElement.GetProperty("sha256").GetString();

        var response = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/dash-rotating/manifest.mpd",
            DestinationDirectory = ctx.Destination,
            FileName = "dash-expiring.mpd",
            Source = "cli",
        });
        Assert.True(response.Accepted, response.Error);
        var completed = await ctx.WaitFor(response.Job!.Id, j => j.State == "completed",
            TimeSpan.FromSeconds(30));
        Assert.Equal(expectedHash, await Hashing.Sha256FileAsync(completed.FinalFilePath));
        Assert.Equal(1, _server.DashSegmentRequests("rotating-chunk-0-00001.m4s"));
        Assert.True(_server.DashSegmentRequests("rotating-chunk-0-00002.m4s") >= 1);
    }

    [Fact]
    public async Task DashSeparateAudioVideoMuxesWhenFfmpegAvailable()
    {
        var ffmpeg = Environment.GetEnvironmentVariable("DOWNLOADMANAGER_FFMPEG");
        if (string.IsNullOrWhiteSpace(ffmpeg) || !File.Exists(ffmpeg))
        {
            return;
        }

        await using var ctx = await ServiceContext.CreateAsync(_server);
        using var http = new HttpClient();
        using var expectedJson = System.Text.Json.JsonDocument.Parse(
            await http.GetStringAsync($"{_server.BaseAddress}/dash-av/expected"));
        var expectedHash = expectedJson.RootElement.GetProperty("sha256").GetString();
        var expectedBytes = expectedJson.RootElement.GetProperty("bytes").GetInt64();

        var response = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/dash-av/manifest.mpd",
            DestinationDirectory = ctx.Destination,
            FileName = "dash-av.mpd",
            Source = "cli",
        });
        Assert.True(response.Accepted, response.Error);
        var completed = await ctx.WaitFor(response.Job!.Id, j => j.State == "completed",
            TimeSpan.FromSeconds(60));
        Assert.Null(completed.Failure);
        Assert.EndsWith(".mp4", completed.FinalFilePath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(expectedBytes, new FileInfo(completed.FinalFilePath).Length);
        Assert.Equal(expectedHash, await Hashing.Sha256FileAsync(completed.FinalFilePath));
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

    private static async Task WaitForSleepAsync(RecordingSleepPrevention sleep, bool expected)
    {
        for (var i = 0; i < 40; i++)
        {
            if (sleep.Active == expected)
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Equal(expected, sleep.Active);
    }

    private sealed class RecordingSleepPrevention : ISleepPrevention
    {
        public bool Active { get; private set; }
        public int Activations { get; private set; }
        public int Releases { get; private set; }
        public List<bool> States { get; } = new();

        public void SetActive(bool active)
        {
            Active = active;
            States.Add(active);
            if (active)
            {
                Activations++;
            }
            else
            {
                Releases++;
            }
        }

        public void Dispose()
        {
        }
    }

    private sealed class RecordingCompletionExecutor : ICompletionActionExecutor
    {
        public List<string> OpenedPaths { get; } = new();
        public int OpenAttempts { get; private set; }
        public int ExitCount { get; private set; }
        public int ShutdownCount { get; private set; }
        public string? LastShutdownMode { get; private set; }
        public bool LastForce { get; private set; }
        public bool ThrowOnOpen { get; init; }

        public Task OpenFileAsync(string path, CancellationToken cancellationToken)
        {
            OpenAttempts++;
            if (ThrowOnOpen)
            {
                throw new IOException("simulated open failure");
            }

            OpenedPaths.Add(path);
            return Task.CompletedTask;
        }

        public Task RequestExitAsync(CancellationToken cancellationToken)
        {
            ExitCount++;
            return Task.CompletedTask;
        }

        public Task RequestShutdownAsync(string mode, bool force, CancellationToken cancellationToken)
        {
            ShutdownCount++;
            LastShutdownMode = mode;
            LastForce = force;
            return Task.CompletedTask;
        }
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
            int maxConcurrent = 2,
            ICompletionActionExecutor? completionActions = null,
            ISleepPrevention? sleepPrevention = null)
        {
            var root = Path.Combine(AppContext.BaseDirectory, "test-work", Guid.NewGuid().ToString("N"));
            var paths = new StoragePaths(Path.Combine(root, "state"));
            var destination = Path.Combine(root, "downloads");
            Directory.CreateDirectory(destination);
            paths.EnsureCreated();
            return BuildAsync(server, paths, destination, autoResume, maxConcurrent, ownsPaths: true,
                completionActions, sleepPrevention);
        }

        public static Task<ServiceContext> CreateAsync(
            TestServerHost server,
            StoragePaths paths,
            string destination,
            bool autoResume = true,
            ICompletionActionExecutor? completionActions = null,
            ISleepPrevention? sleepPrevention = null)
            => BuildAsync(server, paths, destination, autoResume, 2, ownsPaths: false,
                completionActions, sleepPrevention);

        private static async Task<ServiceContext> BuildAsync(
            TestServerHost server,
            StoragePaths paths,
            string destination,
            bool autoResume,
            int maxConcurrent,
            bool ownsPaths,
            ICompletionActionExecutor? completionActions = null,
            ISleepPrevention? sleepPrevention = null)
        {
            var checkpoints = new JsonCheckpointStore(paths);
            var mediaCheckpoints = new JsonMediaCheckpointStore(paths);
            var grabberProjects = new JsonGrabberProjectStore(paths);
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
                checkpoints: checkpoints,
                mediaCheckpoints: mediaCheckpoints);
            var ftpEngine = new DownloadManager.Engine.FtpDownloadEngine();
            var service = new DownloadManagerService(
                paths,
                store,
                checkpoints,
                settingsStore,
                engine,
                enableSchedulerTimer: false,
                completionActions: completionActions,
                sleepPrevention: sleepPrevention,
                mediaCheckpoints: mediaCheckpoints,
                grabberProjects: grabberProjects,
                ftpEngine: ftpEngine);
            await service.InitializeAsync();

            var settings = service.GetSnapshot().Settings;
            settings.DefaultDownloadDirectory = destination;
            settings.MaxConcurrentDownloads = maxConcurrent;
            settings.DefaultConnections = 1;
            settings.AutoResumeUnfinishedOnStartup = autoResume;
            settings.FfmpegPath = Environment.GetEnvironmentVariable("DOWNLOADMANAGER_FFMPEG");
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
