using DownloadManager.Contracts;
using DownloadManager.Core;
using DownloadManager.Core.Providers;
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
public sealed partial class RuntimeServiceTests
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
        Assert.Single(ctx.Service.GetSnapshot().Jobs, j => j.ClientRequestId == "request-1");
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
    public async Task CrashReconciliationClampsMetadataToPartFile()
    {
        var ctx = await ServiceContext.CreateAsync(_server, autoResume: false);
        try
        {
            var response = await ctx.Service.AddJobAsync(new AddJobRequest
            {
                Url = $"{_server.BaseAddress}/file/clamp.bin?size=4096&seed=15",
                DestinationDirectory = ctx.Destination,
                FileName = "clamp.bin",
                StartMode = "later",
                Source = "cli",
            });
            Assert.True(response.Accepted, response.Error);
            var jobId = response.Job!.Id;
            var partPath = response.Job.PartFilePath;
            await File.WriteAllBytesAsync(partPath, new byte[1024]);
            var checkpoints = new JsonCheckpointStore(ctx.Paths);
            await checkpoints.SaveAsync(new DownloadCheckpoint
            {
                JobId = jobId,
                Url = response.Job.Url,
                TotalBytes = 4096,
                Segments =
                {
                    new SegmentCheckpoint
                    {
                        Start = 0,
                        End = 4095,
                        Completed = 3000,
                    },
                },
            });

            var shortened = await ServiceContext.CreateAsync(
                _server, ctx.Paths, ctx.Destination, autoResume: false);
            try
            {
                var job = shortened.Service.GetJob(jobId);
                Assert.NotNull(job);
                Assert.Equal(1024, job!.ReceivedBytes);
                Assert.Equal(4096, job.TotalBytes);
            }
            finally
            {
                await shortened.DisposeAsync();
            }

            File.Delete(partPath);
            var missing = await ServiceContext.CreateAsync(
                _server, ctx.Paths, ctx.Destination, autoResume: false);
            try
            {
                var job = missing.Service.GetJob(jobId);
                Assert.NotNull(job);
                Assert.Equal(0, job!.ReceivedBytes);
            }
            finally
            {
                await missing.DisposeAsync();
            }
        }
        finally
        {
            await ctx.DisposeAsync();
        }
    }

    [Fact]
    public async Task CrashReconciliationTruncatesLongPartFile()
    {
        var ctx = await ServiceContext.CreateAsync(_server, autoResume: false);
        try
        {
            var response = await ctx.Service.AddJobAsync(new AddJobRequest
            {
                Url = $"{_server.BaseAddress}/file/long-clamp.bin?size=8192&seed=21",
                DestinationDirectory = ctx.Destination,
                FileName = "long-clamp.bin",
                StartMode = "later",
                Source = "cli",
            });
            Assert.True(response.Accepted, response.Error);
            var jobId = response.Job!.Id;
            var partPath = response.Job.PartFilePath;
            await File.WriteAllBytesAsync(partPath, new byte[5000]);
            var checkpoints = new JsonCheckpointStore(ctx.Paths);
            await checkpoints.SaveAsync(new DownloadCheckpoint
            {
                JobId = jobId,
                Url = response.Job.Url,
                TotalBytes = 8192,
                Segments =
                {
                    new SegmentCheckpoint
                    {
                        Start = 0,
                        End = 8191,
                        Completed = 3000,
                    },
                },
            });

            var second = await ServiceContext.CreateAsync(
                _server, ctx.Paths, ctx.Destination, autoResume: false);
            try
            {
                var job = second.Service.GetJob(jobId);
                Assert.NotNull(job);
                Assert.Equal(3000, job!.ReceivedBytes);
                Assert.Equal(3000, new FileInfo(partPath).Length);
                Assert.Empty(second.Service.GetSnapshot().Status.OrphanPartFiles);
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
    public async Task CrashReconciliationPreservesSegmentedPartFileAndResumes()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server, autoResume: false);
        const int size = 4 * 1024 * 1024;
        const int keptPerSegment = 65536;
        var expected = TestContent.Generate(721, size);
        var response = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/file/segmented-crash.bin?size={size}&seed=721",
            DestinationDirectory = ctx.Destination,
            FileName = "segmented-crash.bin",
            Connections = 2,
            StartMode = "later",
            Source = "cli",
        });
        Assert.True(response.Accepted, response.Error);
        var jobId = response.Job!.Id;
        var partPath = response.Job.PartFilePath;
        await using (var part = new FileStream(partPath, FileMode.Create, FileAccess.Write))
        {
            part.SetLength(size);
            await part.WriteAsync(expected.AsMemory(0, keptPerSegment));
            part.Position = size / 2;
            await part.WriteAsync(expected.AsMemory(size / 2, keptPerSegment));
        }
        await new JsonCheckpointStore(ctx.Paths).SaveAsync(new DownloadCheckpoint
        {
            JobId = jobId,
            Url = response.Job.Url,
            TotalBytes = size,
            Segments =
            {
                new SegmentCheckpoint { Start = 0, End = size / 2 - 1, Completed = keptPerSegment },
                new SegmentCheckpoint { Start = size / 2, End = size - 1, Completed = keptPerSegment },
            },
        });

        await using var restarted = await ServiceContext.CreateAsync(
            _server, ctx.Paths, ctx.Destination, autoResume: false);
        Assert.Equal(2 * keptPerSegment, restarted.Service.GetJob(jobId)!.ReceivedBytes);
        Assert.Equal(size, new FileInfo(partPath).Length);
        var savedBytes = await File.ReadAllBytesAsync(partPath);
        Assert.Equal(expected.AsSpan(size / 2, keptPerSegment).ToArray(),
            savedBytes.AsSpan(size / 2, keptPerSegment).ToArray());
        var resume = await restarted.Service.ResumeAsync(jobId);
        Assert.True(resume.Ok, resume.Error);
        var completed = await restarted.WaitFor(jobId, job => job.State == "completed");
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(expected)).ToLowerInvariant(),
            await Hashing.Sha256FileAsync(completed.FinalFilePath));
    }

    [Fact]
    public async Task OrphanPartFilesCanBeQuarantinedOrDeleted()
    {
        var ctx = await ServiceContext.CreateAsync(_server, autoResume: false);
        try
        {
            var attached = await ctx.Service.AddJobAsync(new AddJobRequest
            {
                Url = $"{_server.BaseAddress}/file/attached.bin?size=8192&seed=310",
                DestinationDirectory = ctx.Destination,
                FileName = "attached.bin",
                StartMode = "later",
                Source = "cli",
            });
            Assert.True(attached.Accepted, attached.Error);
            var attachedPart = attached.Job!.PartFilePath;
            await File.WriteAllBytesAsync(attachedPart, new byte[20]);
            var first = Path.Combine(ctx.Destination, $"orphan-a.{Guid.NewGuid():N}.part");
            var second = Path.Combine(ctx.Destination, $"orphan-b.{Guid.NewGuid():N}.part");
            await File.WriteAllBytesAsync(first, new byte[10]);

            var quarantined = await ctx.Service.CleanOrphanPartFilesAsync(false, CancellationToken.None);
            Assert.True(quarantined.Ok);
            Assert.Contains(first, quarantined.Processed, StringComparer.OrdinalIgnoreCase);
            Assert.False(File.Exists(first));
            var quarantine = Path.Combine(ctx.Destination, DownloadManagerService.OrphanQuarantineFolderName);
            Assert.True(File.Exists(Path.Combine(quarantine, Path.GetFileName(first))));
            Assert.DoesNotContain(first, ctx.Service.GetSnapshot().Status.OrphanPartFiles,
                StringComparer.OrdinalIgnoreCase);

            // Same name again: the quarantine keeps both copies.
            await File.WriteAllBytesAsync(first, new byte[11]);
            var collided = await ctx.Service.CleanOrphanPartFilesAsync(false, CancellationToken.None);
            Assert.True(collided.Ok);
            Assert.Equal(2, Directory.GetFiles(quarantine).Length);

            // A locked orphan is reported as failed, not silently skipped.
            var locked = Path.Combine(ctx.Destination, $"orphan-locked.{Guid.NewGuid():N}.part");
            await File.WriteAllBytesAsync(locked, new byte[5]);
            using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var failed = await ctx.Service.CleanOrphanPartFilesAsync(true, CancellationToken.None);
                Assert.False(failed.Ok);
                Assert.Contains(locked, failed.Failed, StringComparer.OrdinalIgnoreCase);
                Assert.False(string.IsNullOrWhiteSpace(failed.Error));
            }

            File.Delete(locked);
            await File.WriteAllBytesAsync(second, new byte[10]);
            var deleted = await ctx.Service.CleanOrphanPartFilesAsync(true, CancellationToken.None);
            Assert.True(deleted.Ok);
            Assert.Single(deleted.Processed);
            Assert.False(File.Exists(second));
            Assert.Empty(ctx.Service.GetSnapshot().Status.OrphanPartFiles);
            // Quarantined files are not rescanned or deleted, and the job is untouched.
            Assert.True(File.Exists(Path.Combine(quarantine, Path.GetFileName(first))));
            Assert.True(File.Exists(attachedPart));
            Assert.DoesNotContain(attachedPart, quarantined.Processed.Concat(deleted.Processed),
                StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            await ctx.DisposeAsync();
        }
    }

    [Fact]
    public async Task OrphanPartFilesAreReported()
    {
        var ctx = await ServiceContext.CreateAsync(_server, autoResume: false);
        try
        {
            var orphanPath = Path.Combine(
                ctx.Destination, $"orphan.{Guid.NewGuid():N}.part");
            await File.WriteAllBytesAsync(orphanPath, new byte[123]);
            var second = await ServiceContext.CreateAsync(
                _server, ctx.Paths, ctx.Destination, autoResume: false);
            try
            {
                var status = second.Service.GetSnapshot().Status;
                Assert.Contains(
                    Path.GetFullPath(orphanPath),
                    status.OrphanPartFiles.Select(Path.GetFullPath));
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
    public async Task ConcurrentUpdateInstallIsRejectedAndCancellationReleasesGate()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var installRoot = Path.Combine(ctx.Paths.Root, "fake-install");
        Directory.CreateDirectory(installRoot);
        await File.WriteAllTextAsync(Path.Combine(installRoot, "install.json"),
            "{\"version\":\"1.0.0\",\"channel\":\"stable\"}");
        var settings = ctx.Service.GetSnapshot().Settings;
        // Exceed the fixture's 64 KiB first write. A complete first chunk could
        // otherwise return the HTTP connection to the pool while the fixture
        // still delays completion, blocking a later request on that connection.
        settings.UpdateManifestUrl = $"{_server.BaseAddress}/slow/update-manifest.bin?size=131072&seed=410&bps=1";
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);
        var previous = Environment.GetEnvironmentVariable("DOWNLOADMANAGER_INSTALL_ROOT");
        using var cancellation = new CancellationTokenSource();
        try
        {
            Environment.SetEnvironmentVariable("DOWNLOADMANAGER_INSTALL_ROOT", installRoot);
            var first = ctx.Service.InstallUpdateAsync(cancellationToken: cancellation.Token);
            var second = await ctx.Service.InstallUpdateAsync();
            Assert.False(second.Ok);
            Assert.Equal("busy", second.Status);
            Assert.Contains("already in progress", second.Error);
            cancellation.Cancel();
            try { await first; }
            catch (OperationCanceledException) { }
            Environment.SetEnvironmentVariable("DOWNLOADMANAGER_INSTALL_ROOT", null);
            var afterCancellation = await ctx.Service.InstallUpdateAsync();
            Assert.Equal("notInstalled", afterCancellation.Status);
        }
        finally
        {
            cancellation.Cancel();
            Environment.SetEnvironmentVariable("DOWNLOADMANAGER_INSTALL_ROOT", previous);
        }
    }

    [Fact]
    public async Task UpdateCheckExcludesPortableAndVerifiesSignedDownload()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var portable = await ctx.Service.CheckForUpdatesAsync();
        Assert.Equal("notInstalled", portable.Status);

        var root = Path.Combine(
            AppContext.BaseDirectory, "test-work", Guid.NewGuid().ToString("N"), "install");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "install.json"),
            "{\"version\":\"1.0.0\",\"channel\":\"stable\"}");

        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var package = TestContent.Generate(400, 8192);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(package))
            .ToLowerInvariant();
        try
        {
            var manifest = new UpdateManifest
            {
                Version = "1.0.1",
                Channel = "stable",
                InstallerUrl = $"{_server.BaseAddress}/update/package",
                Size = package.Length,
                Sha256 = hash,
            };
            manifest.Signature = Convert.ToBase64String(rsa.SignData(
                System.Text.Encoding.UTF8.GetBytes(UpdateManifestVerifier.Canonical(manifest)),
                System.Security.Cryptography.HashAlgorithmName.SHA256,
                System.Security.Cryptography.RSASignaturePadding.Pkcs1));
            var manifestBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
                manifest, IpcJson.Options);
            _server.SetUpdatePayload(manifestBytes, package);

            var settings = ctx.Service.GetSnapshot().Settings;
            settings.UpdateManifestUrl = $"{_server.BaseAddress}/update/manifest";
            settings.UpdatePublicKey = rsa.ExportSubjectPublicKeyInfoPem();
            settings.UpdateChannel = "stable";
            Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);

            var previous = Environment.GetEnvironmentVariable("DOWNLOADMANAGER_INSTALL_ROOT");
            Environment.SetEnvironmentVariable("DOWNLOADMANAGER_INSTALL_ROOT", root);
            try
            {
                var check = await ctx.Service.CheckForUpdatesAsync();
                Assert.True(check.Ok, check.Error);
                Assert.Equal("updateAvailable", check.Status);
                Assert.Equal("1.0.0", check.InstalledVersion);
                Assert.Equal("1.0.1", check.AvailableVersion);

                var download = await ctx.Service.DownloadUpdateAsync();
                Assert.True(download.Ok, download.Error);
                Assert.NotNull(download.Path);
                Assert.True(File.Exists(download.Path));
                Assert.Equal(package.Length, download.Size);
                Assert.Equal(hash, download.Sha256);

                var active = await ctx.Service.AddJobAsync(new AddJobRequest
                {
                    Url = $"{_server.BaseAddress}/slow/update-active.bin?size=1048576&seed=410&bps=131072",
                    DestinationDirectory = ctx.Destination,
                    FileName = "update-active.bin",
                    Source = "cli",
                });
                await ctx.WaitFor(active.Job!.Id, j => j.ReceivedBytes > 65536);
                var blocked = await ctx.Service.InstallUpdateAsync();
                Assert.False(blocked.Ok);
                Assert.Equal("busy", blocked.Status);
                await ctx.Service.CancelAsync(active.Job.Id, deleteFile: true);

                // The signed fixture is deliberately not a ZIP: a staging
                // failure must release admission so normal downloads continue.
                var invalidPackage = await ctx.Service.InstallUpdateAsync(new UpdateInstallRequest { Force = true });
                Assert.False(invalidPackage.Ok);
                Assert.Equal("failed", invalidPackage.Status);
                var following = await ctx.Service.AddJobAsync(new AddJobRequest
                {
                    Url = $"{_server.BaseAddress}/file/after-update-failure.bin?size=4096&seed=411",
                    Source = "cli",
                });
                Assert.True(following.Accepted, following.Error);
                await ctx.WaitFor(following.Job!.Id, job => job.State == "completed");

                var deferred = await ctx.Service.DeferUpdateAsync("1.0.1");
                Assert.True(deferred.Ok, deferred.Error);
                Assert.Equal("1.0.1",
                    ctx.Service.GetSnapshot().Settings.SkippedUpdateVersion);
            }
            finally
            {
                Environment.SetEnvironmentVariable("DOWNLOADMANAGER_INSTALL_ROOT", previous);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MoveCompletedDownloadRenamesAndUpdatesPaths()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var response = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/file/move-source.bin?size=65536&seed=241",
            DestinationDirectory = ctx.Destination,
            FileName = "move-source.bin",
            Source = "cli",
        });
        Assert.True(response.Accepted, response.Error);
        var completed = await ctx.WaitFor(response.Job!.Id, job => job.State == "completed");
        var newPath = Path.Combine(ctx.Destination, "moved", "renamed.bin");
        Assert.True(File.Exists(completed.FinalFilePath));

        var update = await ctx.Service.UpdateJobAsync(new JobUpdateRequest
        {
            JobId = completed.Id,
            NewFilePath = newPath,
        });
        Assert.True(update.Ok, update.Error);
        Assert.Equal("renamed.bin", update.Job!.FileName);
        Assert.Equal(newPath, update.Job.FinalFilePath);
        Assert.Equal(Path.GetDirectoryName(newPath), update.Job.DestinationDirectory);
        Assert.True(File.Exists(newPath));
        Assert.False(File.Exists(completed.FinalFilePath));

        var duplicate = await ctx.Service.UpdateJobAsync(new JobUpdateRequest
        {
            JobId = completed.Id,
            NewFilePath = newPath,
        });
        Assert.True(duplicate.Ok, duplicate.Error);
        Assert.Equal(newPath, duplicate.Job!.FinalFilePath);
    }

    [Fact]
    public async Task RecentUrlsAreRecordedDeduplicatedAndPersisted()
    {
        var ctx = await ServiceContext.CreateAsync(_server);
        try
        {
            var firstUrl = $"{_server.BaseAddress}/file/history-a.bin?size=1024&seed=550";
            var secondUrl = $"{_server.BaseAddress}/file/history-b.bin?size=1024&seed=551";
            foreach (var (url, name) in new[]
                     {
                         (firstUrl, "history-a.bin"),
                         (secondUrl, "history-b.bin"),
                     })
            {
                var response = await ctx.Service.AddJobAsync(new AddJobRequest
                {
                    Url = url,
                    DestinationDirectory = ctx.Destination,
                    FileName = name,
                    StartMode = "later",
                    Source = "cli",
                });
                Assert.True(response.Accepted, response.Error);
            }

            var refresh = await ctx.Service.AddJobAsync(new AddJobRequest
            {
                Url = firstUrl,
                DestinationDirectory = ctx.Destination,
                FileName = "history-a-again.bin",
                StartMode = "later",
                Source = "cli",
            });
            Assert.True(refresh.Accepted, refresh.Error);

            var recent = ctx.Service.GetSnapshot().Settings.RecentUrls;
            Assert.Equal(new[] { firstUrl, secondUrl }, recent);

            var second = await ServiceContext.CreateAsync(_server, ctx.Paths, ctx.Destination);
            try
            {
                Assert.Equal(recent, second.Service.GetSnapshot().Settings.RecentUrls);
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
    public async Task SettingsUpdatePreservesUnreturnedSecrets()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var settings = ctx.Service.GetSnapshot().Settings;
        settings.SiteLogins.Add(new SiteLoginDto
        {
            Site = "http://example.com",
            Username = "user",
            Password = "site-secret",
        });
        settings.Proxy = new ProxyDto
        {
            Mode = "none",
            Address = "http://proxy.example:8080",
            Username = "proxy-user",
            Password = "proxy-secret",
        };
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);

        var snapshot = ctx.Service.GetSnapshot().Settings;
        Assert.True(snapshot.Proxy.HasPassword);
        Assert.Null(snapshot.Proxy.Password);
        var login = Assert.Single(snapshot.SiteLogins);
        Assert.True(login.HasPassword);
        Assert.Null(login.Password);

        snapshot.MaxConcurrentDownloads = 5;
        Assert.True((await ctx.Service.UpdateSettingsAsync(snapshot)).Ok);
        var after = ctx.Service.GetSnapshot().Settings;
        Assert.True(after.Proxy.HasPassword);
        Assert.True(Assert.Single(after.SiteLogins).HasPassword);
        Assert.Equal(5, after.MaxConcurrentDownloads);
    }

    [Fact]
    public async Task DefaultQueueFollowsTheOverallDownloadLimitUnlessSetApart()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var settings = ctx.Service.GetSnapshot().Settings;
        var main = settings.Queues.Single(queue => queue.Id == settings.DefaultQueueId);
        Assert.Equal(settings.MaxConcurrentDownloads, main.MaxConcurrentDownloads);

        settings.MaxConcurrentDownloads = 6;
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);
        settings = ctx.Service.GetSnapshot().Settings;
        Assert.Equal(6, settings.Queues.Single(queue => queue.Id == settings.DefaultQueueId).MaxConcurrentDownloads);

        // Set apart in the Scheduler: the overall limit no longer moves it.
        settings.Queues.Single(queue => queue.Id == settings.DefaultQueueId).MaxConcurrentDownloads = 2;
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);
        settings = ctx.Service.GetSnapshot().Settings;
        settings.MaxConcurrentDownloads = 8;
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);
        settings = ctx.Service.GetSnapshot().Settings;
        Assert.Equal(8, settings.MaxConcurrentDownloads);
        Assert.Equal(2, settings.Queues.Single(queue => queue.Id == settings.DefaultQueueId).MaxConcurrentDownloads);
    }

    [Fact]
    public async Task ChangedDefaultConnectionsReachOnlyUnstartedDownloads()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var settings = ctx.Service.GetSnapshot().Settings;
        settings.DefaultConnections = 4;
        settings.MaxConnectionsPerHost = 16;
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);

        async Task<JobDto> AddAsync(string name, string startMode, int? connections)
        {
            var response = await ctx.Service.AddJobAsync(new AddJobRequest
            {
                Url = $"{_server.BaseAddress}/file/{name}?size=65536&seed=40",
                DestinationDirectory = ctx.Destination,
                FileName = name,
                Connections = connections,
                StartMode = startMode,
                Source = "cli",
            });
            Assert.True(response.Accepted, response.Error);
            return response.Job!;
        }

        var waiting = await AddAsync("conn-default.bin", "later", null);
        var chosen = await AddAsync("conn-chosen.bin", "later", 2);
        var finished = await AddAsync("conn-done.bin", "startNow", null);
        await ctx.WaitFor(finished.Id, job => job.State == "completed", TimeSpan.FromSeconds(20));
        Assert.Equal(4, waiting.RequestedConnections);

        settings = ctx.Service.GetSnapshot().Settings;
        settings.DefaultConnections = 6;
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);

        var jobs = ctx.Service.GetSnapshot().Jobs.ToDictionary(job => job.Id);
        Assert.Equal(6, jobs[waiting.Id].RequestedConnections);
        Assert.Equal(2, jobs[chosen.Id].RequestedConnections);
        Assert.Equal(4, jobs[finished.Id].RequestedConnections);
    }

    [Fact]
    public async Task GlobalSpeedLimitIsSharedByAllDownloads()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server, maxConcurrent: 2);
        var settings = ctx.Service.GetSnapshot().Settings;
        settings.GlobalSpeedLimitBytesPerSecond = 131_072;
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);
        const int size = 262_144;
        var jobs = new List<JobDto>();
        foreach (var seed in new[] { 18, 19 })
        {
            var response = await ctx.Service.AddJobAsync(new AddJobRequest
            {
                Url = $"{_server.BaseAddress}/file/global-speed-{seed}.bin?size={size}&seed={seed}",
                DestinationDirectory = ctx.Destination,
                FileName = $"global-speed-{seed}.bin",
                Connections = 1,
                Source = "cli",
            });
            Assert.True(response.Accepted, response.Error);
            // Not copied into the download: it stays one cap for all of them, and a change reaches running ones.
            Assert.Null(response.Job!.SpeedLimitBytesPerSecond);
            jobs.Add(response.Job);
        }

        // 512 KB in all at 128 KB/s together: about 4 s, where a copy per download would take about 2 s.
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        foreach (var job in jobs)
        {
            var completed = await ctx.WaitFor(job.Id, j => j.State == "completed", TimeSpan.FromSeconds(20));
            Assert.Equal(size, completed.ReceivedBytes);
        }
        stopwatch.Stop();
        Assert.InRange(stopwatch.Elapsed.TotalSeconds, 3.2, 8.0);
    }

    [Fact]
    public async Task PerHostAndGlobalConnectionLimitsApplyAcrossParallelJobs()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server, maxConcurrent: 2);
        const int size = 2 * 1024 * 1024;

        async Task<int> MonitorAsync(IReadOnlyList<JobDto> batch)
        {
            var completed = new HashSet<Guid>();
            var peakRunning = 0;
            var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
            while (completed.Count < batch.Count && DateTimeOffset.UtcNow < deadline)
            {
                var snapshot = ctx.Service.GetSnapshot();
                peakRunning = Math.Max(peakRunning, snapshot.Jobs.Count(job =>
                    job.State is "connecting" or "downloading" or "retrying"));
                foreach (var job in batch)
                {
                    var current = snapshot.Jobs.FirstOrDefault(candidate => candidate.Id == job.Id);
                    if (current?.State == "completed")
                    {
                        completed.Add(job.Id);
                    }
                }

                await Task.Delay(50);
            }

            Assert.Equal(batch.Count, completed.Count);
            return peakRunning;
        }

        async Task AssertHashesAsync(IReadOnlyList<JobDto> batch, int seedBase)
        {
            for (var i = 0; i < batch.Count; i++)
            {
                var final = ctx.Service.GetJob(batch[i].Id);
                Assert.NotNull(final);
                Assert.Equal("completed", final!.State);
                var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    TestContent.Generate(seedBase + i, size))).ToLowerInvariant();
                Assert.Equal(expected, await Hashing.Sha256FileAsync(final.FinalFilePath));
            }
        }

        var settings = ctx.Service.GetSnapshot().Settings;
        settings.MaxConnectionsPerHost = 8;
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);
        Assert.Equal(8, ctx.Engine.HostConnectionLimit);

        _server.ResetFileConcurrency();
        var globalBatch = new List<JobDto>();
        for (var i = 0; i < 3; i++)
        {
            var response = await ctx.Service.AddJobAsync(new AddJobRequest
            {
                Url = $"{_server.BaseAddress}/file/a5-global-{i}.bin?size={size}&seed={40 + i}&bps=1048576",
                DestinationDirectory = ctx.Destination,
                FileName = $"a5-global-{i}.bin",
                Connections = 1,
                Source = "cli",
            });
            Assert.True(response.Accepted, response.Error);
            globalBatch.Add(response.Job!);
        }

        var globalPeakRunning = await MonitorAsync(globalBatch);
        var globalPeakRequests = _server.PeakConcurrentFileRequests;
        Assert.Equal(2, globalPeakRunning);
        Assert.Equal(2, globalPeakRequests);
        await AssertHashesAsync(globalBatch, 40);

        settings = ctx.Service.GetSnapshot().Settings;
        settings.MaxConcurrentDownloads = 3;
        settings.MaxConnectionsPerHost = 2;
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);
        Assert.Equal(2, ctx.Engine.HostConnectionLimit);

        _server.ResetFileConcurrency();
        var hostBatch = new List<JobDto>();
        for (var i = 0; i < 3; i++)
        {
            var response = await ctx.Service.AddJobAsync(new AddJobRequest
            {
                Url = $"{_server.BaseAddress}/file/a5-host-{i}.bin?size={size}&seed={50 + i}&bps=1048576",
                DestinationDirectory = ctx.Destination,
                FileName = $"a5-host-{i}.bin",
                Connections = 1,
                Source = "cli",
            });
            Assert.True(response.Accepted, response.Error);
            hostBatch.Add(response.Job!);
        }

        var hostPeakRunning = await MonitorAsync(hostBatch);
        var hostPeakRequests = _server.PeakConcurrentFileRequests;
        Assert.Equal(3, hostPeakRunning);
        Assert.Equal(2, hostPeakRequests);
        await AssertHashesAsync(hostBatch, 50);

        var capped = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/file/a5-cap.bin?size=65536&seed=99",
            DestinationDirectory = ctx.Destination,
            FileName = "a5-cap.bin",
            Connections = 4,
            StartMode = "later",
            Source = "cli",
        });
        Assert.True(capped.Accepted, capped.Error);
        Assert.Equal(2, capped.Job!.RequestedConnections);
        await ctx.Service.CancelAsync(capped.Job.Id, deleteFile: true);

        Console.WriteLine(
            $"A5 globalPeakRunning={globalPeakRunning} globalPeakRequests={globalPeakRequests} " +
            $"hostPeakRunning={hostPeakRunning} hostPeakRequests={hostPeakRequests} " +
            $"perJobCap={capped.Job.RequestedConnections} hostLimiterLimit={ctx.Engine.HostConnectionLimit} " +
            $"hostLimiterPeak={ctx.Engine.HostConnectionPeak}");
    }

    [Fact]
    public async Task ConnectionProfilesCapRequestedConnections()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var settings = ctx.Service.GetSnapshot().Settings;
        settings.MaxConnectionsPerHost = 4;
        settings.ConnectionProfiles.Add(new ConnectionProfileDto
        {
            Pattern = "127.0.0.1",
            MaxConnections = 2,
        });
        settings.ConnectionProfiles.Add(new ConnectionProfileDto
        {
            Pattern = "*.example.com",
            MaxConnections = 1,
        });
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);

        var matched = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/file/profile.bin?size=65536&seed=330",
            DestinationDirectory = ctx.Destination,
            FileName = "profile.bin",
            Connections = 8,
            StartMode = "later",
            Source = "cli",
        });
        Assert.Equal(2, matched.Job!.RequestedConnections);

        var unmatched = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"http://localhost:{new Uri(_server.BaseAddress).Port}/file/profile2.bin?size=65536&seed=331",
            DestinationDirectory = ctx.Destination,
            FileName = "profile2.bin",
            Connections = 8,
            StartMode = "later",
            Source = "cli",
        });
        Assert.Equal(4, unmatched.Job!.RequestedConnections);

        var wildcard = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = "https://cdn.example.com/file.bin",
            DestinationDirectory = ctx.Destination,
            FileName = "profile3.bin",
            Connections = 8,
            StartMode = "later",
            Source = "cli",
        });
        Assert.Equal(1, wildcard.Job!.RequestedConnections);
    }

    [Fact]
    public async Task SegmentDetailsMatchTheEngineCheckpoint()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var response = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/slow/segments.bin?size=2097152&seed=430&bps=131072",
            DestinationDirectory = ctx.Destination,
            FileName = "segments.bin",
            Connections = 4,
            Source = "cli",
        });
        await ctx.WaitFor(response.Job!.Id, j => j.ReceivedBytes > 262144, TimeSpan.FromSeconds(30));

        var segments = await ctx.Service.GetSegmentsAsync(response.Job.Id);
        Assert.True(segments.Count >= 2);
        Assert.All(segments, segment =>
        {
            Assert.True(segment.End > segment.Start);
            Assert.InRange(segment.Fraction, 0, 1);
            Assert.True(segment.Completed >= 0);
        });
        Assert.True(segments.Sum(segment => segment.Completed) <=
                    ctx.Service.GetJob(response.Job.Id)!.ReceivedBytes);

        await ctx.Service.CancelAsync(response.Job.Id, deleteFile: true);
    }

    [Fact]
    public async Task PostDownloadHookRunsOnceWithExactFilePath()
    {
        var hook = new RecordingPostDownloadHook();
        await using var ctx = await ServiceContext.CreateAsync(_server, postDownloadHook: hook);
        var command = @"C:\Program Files\Scanner\scan.exe";
        var settings = ctx.Service.GetSnapshot().Settings;
        settings.PostDownloadCommand = command;
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);

        var response = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/file/hook.bin?size=65536&seed=310",
            DestinationDirectory = ctx.Destination,
            FileName = "hook file (1).bin",
            Source = "cli",
        });
        var completed = await ctx.WaitFor(response.Job!.Id, j => j.State == "completed",
            TimeSpan.FromSeconds(30));
        await Task.Delay(200);

        var call = Assert.Single(hook.Calls);
        Assert.Equal(command, call.Command);
        Assert.Equal(completed.FinalFilePath, call.FilePath);
        Assert.True(File.Exists(call.FilePath));
    }

    [Fact]
    public async Task PostDownloadHookFailureDoesNotFailDownload()
    {
        var hook = new RecordingPostDownloadHook { Throw = true };
        await using var ctx = await ServiceContext.CreateAsync(_server, postDownloadHook: hook);
        var settings = ctx.Service.GetSnapshot().Settings;
        settings.PostDownloadCommand = "C:\\Scanner\\missing.exe";
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);

        var response = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/file/hook-fail.bin?size=65536&seed=311",
            DestinationDirectory = ctx.Destination,
            FileName = "hook-fail.bin",
            Source = "cli",
        });
        var completed = await ctx.WaitFor(response.Job!.Id, j => j.State == "completed",
            TimeSpan.FromSeconds(30));
        await Task.Delay(200);
        Assert.Equal("completed", completed.State);
        Assert.Single(hook.Calls);
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
    public async Task ProgressCancelAfterCompletionKeepsSavedFile()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var added = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/file/cancel-complete.bin?size=65536&seed=422",
        });
        var completed = await ctx.WaitFor(added.Job!.Id, j => j.State == "completed");
        var hash = await Hashing.Sha256FileAsync(completed.FinalFilePath);
        var cancelled = await ctx.Service.CancelAsync(completed.Id, deleteFile: true, onlyIfUnfinished: true);
        Assert.False(cancelled.Ok);
        Assert.Equal("completed", ctx.Service.GetJob(completed.Id)!.State);
        Assert.Equal(hash, await Hashing.Sha256FileAsync(completed.FinalFilePath));
    }

    [Fact]
    public async Task TransferOptionsPersistIndependentlyAndRejectInvalidUpdates()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var first = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/file/options.bin?size=65536&seed=419", StartMode = "later",
        });
        var second = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/file/other-options.bin?size=65536&seed=420", StartMode = "later",
        });
        var id = first.Job!.Id;
        var update = await ctx.Service.UpdateJobAsync(new JobUpdateRequest
        {
            JobId = id, SpeedLimitBytesPerSecond = 32768, ShowCompletionDialog = false,
        });
        Assert.True(update.Ok, update.Error);
        Assert.Equal(32768, update.Job!.SpeedLimitBytesPerSecond);
        Assert.False(update.Job.ShowCompletionDialog);
        Assert.True(update.Job.CanChangeSpeedLimit);
        Assert.Null(ctx.Service.GetJob(second.Job!.Id)!.SpeedLimitBytesPerSecond);
        Assert.Null(ctx.Service.GetJob(second.Job.Id)!.ShowCompletionDialog);
        Assert.True(ctx.Service.GetSnapshot().Settings.ShowCompletionDialog);
        var invalid = await ctx.Service.UpdateJobAsync(new JobUpdateRequest
        {
            JobId = id, SpeedLimitBytesPerSecond = -1, ShowCompletionDialog = true,
        });
        Assert.False(invalid.Ok);
        Assert.False(ctx.Service.GetJob(id)!.ShowCompletionDialog);
        Assert.Equal(32768, ctx.Service.GetJob(id)!.SpeedLimitBytesPerSecond);
        await ctx.Service.ShutdownAsync();
        await using var restarted = await ServiceContext.CreateAsync(_server, ctx.Paths, ctx.Destination);
        Assert.Equal(32768, restarted.Service.GetJob(id)!.SpeedLimitBytesPerSecond);
        Assert.False(restarted.Service.GetJob(id)!.ShowCompletionDialog);
        var cleared = await restarted.Service.UpdateJobAsync(new JobUpdateRequest
        {
            JobId = id, SpeedLimitBytesPerSecond = 0,
        });
        Assert.True(cleared.Ok);
        Assert.Null(cleared.Job!.SpeedLimitBytesPerSecond);
        Assert.False(cleared.Job.ShowCompletionDialog);
        var media = await restarted.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/sample.m3u8", StartMode = "later",
        });
        Assert.False(media.Job!.CanChangeSpeedLimit);
        Assert.False((await restarted.Service.UpdateJobAsync(new JobUpdateRequest
        {
            JobId = media.Job.Id, SpeedLimitBytesPerSecond = 1000,
        })).Ok);
    }

    [Fact]
    public async Task CustomCategoriesClassifyPersistAndRespectExplicitChoicesAndBrowserDefaults()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var categoryId = Guid.NewGuid().ToString("N");
        var folder = Path.Combine(ctx.Destination, "design");
        var settings = ctx.Service.GetSnapshot().Settings;
        settings.CustomCategories.Add(new CategoryDefinitionDto { Id = categoryId, Name = "Design files",
            DestinationDirectory = folder, Extensions = new() { ".DMART" } });
        settings.CategoryFolders["documents"] = Path.Combine(ctx.Destination, "documents");
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);
        var added = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/file/design.dmart?size=65536&seed=901", Source = "cli",
        });
        Assert.True(added.Accepted, added.Error);
        var completed = await ctx.WaitFor(added.Job!.Id, job => job.State == "completed");
        Assert.Equal(categoryId, completed.CategoryId);
        Assert.Equal(folder, completed.DestinationDirectory);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(TestContent.Generate(901, 65536))).ToLowerInvariant(),
            await Hashing.Sha256FileAsync(completed.FinalFilePath));
        var chosen = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/file/chosen.dmart?size=65536&seed=902", CategoryId = "documents", StartMode = "later",
        });
        Assert.True(chosen.Accepted, chosen.Error);
        Assert.Equal("documents", chosen.Job!.CategoryId);
        Assert.Equal(settings.CategoryFolders["documents"], chosen.Job.DestinationDirectory);
        var browser = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/file/browser.dmart?size=65536&seed=903", Source = "chromeAuto", StartMode = "later",
        });
        Assert.True(browser.Accepted, browser.Error);
        Assert.Equal(categoryId, browser.Job!.CategoryId);
        Assert.Equal(ctx.Destination, browser.Job.DestinationDirectory);
        var explicitFolder = Path.Combine(ctx.Destination, "one-off");
        var remembered = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/file/remember.dmart?size=65536&seed=904", CategoryId = categoryId.ToUpperInvariant(),
            DestinationDirectory = explicitFolder, RememberDestination = true, StartMode = "later",
        });
        Assert.True(remembered.Accepted, remembered.Error);
        var renamed = ctx.Service.GetSnapshot().Settings;
        renamed.CustomCategories.Single().Name = "Renamed designs";
        Assert.True((await ctx.Service.UpdateSettingsAsync(renamed)).Ok);
        await using var restarted = await ServiceContext.CreateAsync(_server, ctx.Paths, ctx.Destination);
        var snapshot = restarted.Service.GetSnapshot();
        var definition = Assert.Single(snapshot.Settings.CustomCategories);
        Assert.Equal("Renamed designs", definition.Name);
        Assert.Equal(explicitFolder, definition.DestinationDirectory);
        Assert.Equal(new[] { "dmart" }, definition.Extensions);
        Assert.Equal(categoryId, snapshot.Jobs.Single(job => job.Id == completed.Id).CategoryId);
        Assert.Equal("documents", snapshot.Jobs.Single(job => job.Id == chosen.Job.Id).CategoryId);
        Assert.True(File.Exists(completed.FinalFilePath));
        var invalid = await restarted.Service.AddJobAsync(new AddJobRequest { Url = $"{_server.BaseAddress}/file/invalid", CategoryId = Guid.NewGuid().ToString("N") });
        Assert.False(invalid.Accepted);
        Assert.Equal("invalid_category", invalid.ErrorCode);
    }

    [Fact]
    public async Task FoldersByCategoryCanBeTurnedOff()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var settings = ctx.Service.GetSnapshot().Settings;
        Assert.True(settings.UseCategoryFolders);
        settings.DefaultDownloadDirectory = ctx.Destination;
        settings.CategoryFolders["documents"] = Path.Combine(ctx.Destination, "Docs");
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);

        async Task<string> FolderForAsync(string name)
        {
            var response = await ctx.Service.AddJobAsync(new AddJobRequest
            {
                Url = $"{_server.BaseAddress}/file/{name}?size=1024&seed=61", FileName = name, StartMode = "later", Source = "cli",
            });
            Assert.True(response.Accepted, response.Error);
            return response.Job!.DestinationDirectory;
        }

        Assert.Equal(Path.Combine(ctx.Destination, "Docs"), await FolderForAsync("on.pdf"));
        settings = ctx.Service.GetSnapshot().Settings;
        settings.UseCategoryFolders = false;
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);
        Assert.False(ctx.Service.GetSnapshot().Settings.UseCategoryFolders);
        Assert.Equal(ctx.Destination, await FolderForAsync("off.pdf"));
        // The folders themselves are kept for when it is turned back on.
        Assert.Equal(Path.Combine(ctx.Destination, "Docs"), ctx.Service.GetSnapshot().Settings.CategoryFolders["documents"]);
    }

    [Fact]
    public async Task CategoryFolderChangesOnlyWhenTheAddAsksToRememberIt()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var settings = ctx.Service.GetSnapshot().Settings;
        var chosen = Path.Combine(ctx.Destination, "Docs from Options");
        settings.CategoryFolders["documents"] = chosen;
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);

        async Task AddAsync(string name, bool? remember, string folder)
        {
            var response = await ctx.Service.AddJobAsync(new AddJobRequest
            {
                Url = $"{_server.BaseAddress}/file/{name}?size=1024&seed=60", FileName = name, CategoryId = "documents",
                DestinationDirectory = folder, RememberDestination = remember, StartMode = "later",
            });
            Assert.True(response.Accepted, response.Error);
        }

        // A re-add, an import or another client that doesn't say: the Options folder stays.
        await AddAsync("unsaid.pdf", null, Path.Combine(ctx.Destination, "elsewhere"));
        await AddAsync("declined.pdf", false, Path.Combine(ctx.Destination, "elsewhere"));
        Assert.Equal(chosen, ctx.Service.GetSnapshot().Settings.CategoryFolders["documents"]);

        // Remember ticked in Add URL: it becomes the category's folder.
        var remembered = Path.Combine(ctx.Destination, "remembered");
        await AddAsync("asked.pdf", true, remembered);
        Assert.Equal(remembered, ctx.Service.GetSnapshot().Settings.CategoryFolders["documents"]);
    }

    [Fact]
    public async Task CategoryValidationRejectsAmbiguousRulesAndStaleRemovalWithoutChangingDefinitions()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var stale = ctx.Service.GetSnapshot().Settings;
        var settings = ctx.Service.GetSnapshot().Settings;
        settings.CustomCategories.Add(new CategoryDefinitionDto { Id = Guid.NewGuid().ToString("N"), Name = "Assets", Extensions = new() { "asset" } });
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);
        Assert.False((await ctx.Service.UpdateSettingsAsync(stale)).Ok);
        var duplicate = ctx.Service.GetSnapshot().Settings;
        duplicate.CustomCategories.Add(new CategoryDefinitionDto { Id = Guid.NewGuid().ToString("N"), Name = "Other assets", Extensions = new() { ".ASSET" } });
        Assert.False((await ctx.Service.UpdateSettingsAsync(duplicate)).Ok);
        Assert.Single(ctx.Service.GetSnapshot().Settings.CustomCategories);
        var duplicateId = ctx.Service.GetSnapshot().Settings;
        duplicateId.CustomCategories.Add(new CategoryDefinitionDto { Id = duplicateId.CustomCategories[0].Id.ToUpperInvariant(), Name = "Duplicate identifier" });
        Assert.False((await ctx.Service.UpdateSettingsAsync(duplicateId)).Ok);
        var invalid = ctx.Service.GetSnapshot().Settings;
        invalid.CustomCategories[0].Extensions = new() { "../bad" };
        Assert.False((await ctx.Service.UpdateSettingsAsync(invalid)).Ok);
        Assert.Equal(new[] { "asset" }, Assert.Single(ctx.Service.GetSnapshot().Settings.CustomCategories).Extensions);
    }

    [Fact]
    public async Task BuiltInCategoryFoldersSeedFollowDefaultFolderAndRespectClearedOrCustom()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "test-work", Guid.NewGuid().ToString("N"));
        var paths = new StoragePaths(Path.Combine(root, "state"));
        try
        {
            var store = new JsonSettingsStore(paths);
            var first = Path.Combine(root, "dl1");
            await store.SaveAsync(new AppSettings { DefaultDownloadDirectory = first });
            var seeded = await store.LoadAsync();
            Assert.Equal(Path.Combine(first, "Compressed"), seeded.CategoryFolders["compressed"]);
            Assert.Equal(Path.Combine(first, "Video"), seeded.CategoryFolders["video"]);
            Assert.False(seeded.CategoryFolders.ContainsKey("other"));

            seeded.CategoryFolders.Remove("music");
            seeded.CategoryFolders["video"] = Path.Combine(root, "movies");
            var second = Path.Combine(root, "dl2");
            seeded.DefaultDownloadDirectory = second;
            await store.SaveAsync(seeded);
            var moved = await store.LoadAsync();
            Assert.Equal(Path.Combine(second, "Compressed"), moved.CategoryFolders["compressed"]);
            Assert.Equal(Path.Combine(root, "movies"), moved.CategoryFolders["video"]);
            Assert.False(moved.CategoryFolders.ContainsKey("music"));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task RenamingUpdatesAutomaticCategoryAndKeepsExplicitSelection()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var settings = ctx.Service.GetSnapshot().Settings;
        var id = Guid.NewGuid().ToString("N");
        settings.CustomCategories.Add(new CategoryDefinitionDto { Id = id, Name = "Artwork", Extensions = new() { "dmart" } });
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);
        var automatic = await ctx.Service.AddJobAsync(new AddJobRequest { Url = $"{_server.BaseAddress}/file/opaque?size=1024&seed=905" });
        Assert.True(automatic.Accepted, automatic.Error);
        var done = await ctx.WaitFor(automatic.Job!.Id, job => job.State == "completed");
        var renamed = await ctx.Service.UpdateJobAsync(new JobUpdateRequest { JobId = done.Id, NewFilePath = Path.Combine(ctx.Destination, "artwork.dmart") });
        Assert.True(renamed.Ok, renamed.Error);
        Assert.Equal(id, renamed.Job!.CategoryId);
        var explicitChoice = await ctx.Service.AddJobAsync(new AddJobRequest { Url = $"{_server.BaseAddress}/file/opaque2?size=1024&seed=906", CategoryId = "documents" });
        Assert.True(explicitChoice.Accepted, explicitChoice.Error);
        var selected = await ctx.WaitFor(explicitChoice.Job!.Id, job => job.State == "completed");
        var selectedRenamed = await ctx.Service.UpdateJobAsync(new JobUpdateRequest { JobId = selected.Id, NewFilePath = Path.Combine(ctx.Destination, "chosen.dmart") });
        Assert.True(selectedRenamed.Ok, selectedRenamed.Error);
        Assert.Equal("documents", selectedRenamed.Job!.CategoryId);
    }

    [Fact]
    public async Task ConcurrentCategoryCreationCannotSilentlyDiscardAnotherSavedDefinition()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var first = ctx.Service.GetSnapshot().Settings;
        var second = ctx.Service.GetSnapshot().Settings;
        first.CustomCategories.Add(new CategoryDefinitionDto { Id = Guid.NewGuid().ToString("N"), Name = "First" });
        second.CustomCategories.Add(new CategoryDefinitionDto { Id = Guid.NewGuid().ToString("N"), Name = "Second" });
        var results = await Task.WhenAll(ctx.Service.UpdateSettingsAsync(first), ctx.Service.UpdateSettingsAsync(second));
        Assert.Single(results, result => result.Ok);
        Assert.Single(results, result => !result.Ok);
        var kept = Assert.Single(ctx.Service.GetSnapshot().Settings.CustomCategories);
        await using var restarted = await ServiceContext.CreateAsync(_server, ctx.Paths, ctx.Destination);
        Assert.Equal(kept.Id, Assert.Single(restarted.Service.GetSnapshot().Settings.CustomCategories).Id);
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

    [Theory]
    [InlineData("chromeAuto")]
    [InlineData("chromeContextMenu")]
    public async Task BrowserDownloadsUseDefaultInsteadOfLastFolderAcrossRestart(string source)
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var settings = ctx.Service.GetSnapshot().Settings;
        var previous = Path.Combine(ctx.Destination, "previous");
        var oneOff = Path.Combine(ctx.Destination, "one-off");
        settings.LastDestinationDirectory = previous;
        settings.CategoryFolders["music"] = Path.Combine(ctx.Destination, "category-music");
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);

        var explicitChoice = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/file/one.mp3?size=4096&seed=281",
            DestinationDirectory = oneOff,
            Source = source,
            StartMode = "queue",
        });
        Assert.Equal(oneOff, explicitChoice.Job!.DestinationDirectory);
        Assert.Equal(previous, ctx.Service.GetSnapshot().Settings.LastDestinationDirectory);
        var next = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/file/two.mp3?size=4096&seed=282",
            Source = source,
            StartMode = "queue",
        });
        Assert.Equal(ctx.Destination, next.Job!.DestinationDirectory);

        await ctx.Service.ShutdownAsync();
        await using var restarted = await ServiceContext.CreateAsync(_server, ctx.Paths, ctx.Destination);
        var afterRestart = await restarted.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/file/three.mp3?size=4096&seed=283",
            Source = source,
            StartMode = "queue",
        });
        Assert.Equal(ctx.Destination, afterRestart.Job!.DestinationDirectory);
        Assert.Equal(previous, restarted.Service.GetSnapshot().Settings.LastDestinationDirectory);
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
    public async Task PausePreservesPartialAndCancelDeletesIt()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var response = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/slow/pause-cancel.bin?size=4194304&seed=14&bps=262144",
            DestinationDirectory = ctx.Destination,
            FileName = "pause-cancel.bin",
            Source = "cli",
        });
        Assert.True(response.Accepted, response.Error);
        await ctx.WaitFor(response.Job!.Id, j => j.ReceivedBytes > 65_536);
        Assert.True((await ctx.Service.PauseAsync(response.Job.Id)).Ok);
        var paused = await ctx.WaitFor(response.Job.Id, j => j.State == "paused");
        Assert.True(paused.ReceivedBytes > 0);
        Assert.True(File.Exists(paused.PartFilePath));
        Assert.False(File.Exists(paused.FinalFilePath));

        Assert.True((await ctx.Service.ResumeAsync(response.Job.Id)).Ok);
        await ctx.WaitFor(response.Job.Id,
            j => j.ReceivedBytes > paused.ReceivedBytes + 32_768,
            TimeSpan.FromSeconds(30));
        Assert.True((await ctx.Service.CancelAsync(response.Job.Id, deleteFile: true)).Ok);
        var cancelled = await ctx.WaitFor(response.Job.Id, j => j.State == "cancelled");
        // The state changes at once, and the transfer still holds the file until it winds down.
        await WaitUntil(() => !File.Exists(cancelled.PartFilePath), "the partial file to go");
        Assert.False(File.Exists(cancelled.FinalFilePath));
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
    public async Task QueueFileOrderControlsSchedulingSurvivesRestartAndRejectsStaleLists()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server, autoResume: false);
        var settings = ctx.Service.GetSnapshot().Settings;
        settings.Queues[0].IsRunning = false;
        settings.Queues[0].MaxConcurrentDownloads = 1;
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);
        var ids = new List<Guid>();
        for (var index = 0; index < 3; index++)
        {
            var response = await ctx.Service.AddJobAsync(new AddJobRequest
            {
                Url = $"{_server.BaseAddress}/slow/order-{index}.bin?size=262144&seed={120 + index}&bps=262144",
                DestinationDirectory = ctx.Destination,
                FileName = $"order-{index}.bin", QueueId = 0,
            });
            Assert.True(response.Accepted, response.Error);
            ids.Add(response.Job!.Id);
        }
        ids.Reverse();
        var reorder = await ctx.Service.QueueActionAsync(new QueueActionRequest
            { QueueId = 0, Action = "reorderfiles", JobOrder = ids });
        Assert.True(reorder.Ok, reorder.Error);
        foreach (var invalid in new[] { ids.Take(2).ToList(), new List<Guid> { ids[0], ids[0], ids[2] },
                     new List<Guid> { ids[0], ids[1], Guid.NewGuid() } })
        {
            var rejected = await ctx.Service.QueueActionAsync(new QueueActionRequest
                { QueueId = 0, Action = "reorderfiles", JobOrder = invalid });
            Assert.False(rejected.Ok);
            Assert.Equal(ids, ctx.Service.GetSnapshot().Settings.Queues[0].JobOrder);
        }
        // A stale general Options snapshot must not overwrite Host-owned order.
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);
        await using var restarted = await ServiceContext.CreateAsync(_server, ctx.Paths, ctx.Destination, autoResume: false);
        Assert.Equal(ids, restarted.Service.GetSnapshot().Settings.Queues[0].JobOrder);
        Assert.True((await restarted.Service.QueueActionAsync(new QueueActionRequest { QueueId = 0, Action = "start" })).Ok);
        await restarted.WaitFor(ids[0], job => job.ReceivedBytes > 0);
        Assert.Null(restarted.Service.GetJob(ids[1])!.StartedAt);
        Assert.Null(restarted.Service.GetJob(ids[2])!.StartedAt);
        foreach (var id in ids) await restarted.WaitFor(id, job => job.State == "completed");
        var finished = ids.Select(id => restarted.Service.GetJob(id)!).ToList();
        Assert.True(finished[0].CompletedAt <= finished[1].StartedAt);
        Assert.True(finished[1].CompletedAt <= finished[2].StartedAt);
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
    public async Task QueueSettingsCreateEditAndPersistWithoutChangingOtherQueues()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "test-work", Guid.NewGuid().ToString("N"));
        var paths = new StoragePaths(Path.Combine(root, "state"));
        var destination = Path.Combine(root, "downloads");
        Directory.CreateDirectory(destination);
        paths.EnsureCreated();
        var windowStart = DateTimeOffset.UtcNow;
        try
        {
            await using (var ctx = await ServiceContext.CreateAsync(
                             _server, paths, destination, autoResume: false))
            {
                var settings = ctx.Service.GetSnapshot().Settings;
                settings.Queues.Add(new QueueDto
                {
                    Id = 1,
                    Name = "Night",
                    MaxConcurrentDownloads = 2,
                    IsRunning = false,
                    Order = 1,
                    QuotaEnabled = true,
                    QuotaBytes = 2_000_000,
                    QuotaWindowHours = 8,
                    QuotaWindowStart = windowStart,
                    QuotaBytesUsed = 123_456,
                });
                settings.Queues.Add(new QueueDto
                {
                    Id = 2,
                    Name = "Sync",
                    MaxConcurrentDownloads = 4,
                    IsRunning = true,
                    Order = 2,
                    QuotaEnabled = true,
                    QuotaBytes = 5_000_000,
                    QuotaWindowHours = 24,
                    QuotaWindowStart = windowStart.AddHours(-1),
                    QuotaBytesUsed = 789_012,
                });

                var created = await ctx.Service.UpdateSettingsAsync(settings);
                Assert.True(created.Ok, created.Error);

                var beforeQueueCreation = ctx.Service.GetSnapshot().Settings.Queues;
                var invalidCreate = await ctx.Service.QueueActionAsync(new QueueActionRequest
                {
                    Action = "create",
                    Name = "  ",
                    MaxConcurrentDownloads = 2,
                });
                Assert.False(invalidCreate.Ok);
                Assert.Equal(beforeQueueCreation.Select(queue => queue.Id),
                    ctx.Service.GetSnapshot().Settings.Queues.Select(queue => queue.Id));

                var createdBrowser = await ctx.Service.QueueActionAsync(new QueueActionRequest
                {
                    Action = "create",
                    Name = "Browser",
                    MaxConcurrentDownloads = 1,
                });
                Assert.True(createdBrowser.Ok, createdBrowser.Error);
                var browserQueue = Assert.Single(createdBrowser.Settings!.Queues, queue => queue.Name == "Browser");
                Assert.Equal(3, browserQueue.Id);
                Assert.False(browserQueue.IsRunning);
                Assert.False(browserQueue.QuotaEnabled);

                var createdNight = await ctx.Service.QueueActionAsync(new QueueActionRequest
                {
                    Action = "create",
                    Name = "Night backup",
                    MaxConcurrentDownloads = 2,
                    QuotaEnabled = true,
                    QuotaBytes = 1_500_000,
                    QuotaWindowHours = 6,
                });
                Assert.True(createdNight.Ok, createdNight.Error);
                var nightBackup = Assert.Single(createdNight.Settings!.Queues, queue => queue.Name == "Night backup");
                Assert.Equal(4, nightBackup.Id);
                Assert.False(nightBackup.IsRunning);
                Assert.True(nightBackup.QuotaEnabled);
                Assert.Equal(1_500_000, nightBackup.QuotaBytes);
                Assert.Equal(6, nightBackup.QuotaWindowHours);

                var queuesAfterCreate = ctx.Service.GetSnapshot().Settings.Queues;
                Assert.Equal("Night", Assert.Single(queuesAfterCreate, queue => queue.Id == 1).Name);
                Assert.Equal(123_456, Assert.Single(queuesAfterCreate, queue => queue.Id == 1).QuotaBytesUsed);
                Assert.Equal(windowStart, Assert.Single(queuesAfterCreate, queue => queue.Id == 1).QuotaWindowStart);
                Assert.Equal("Sync", Assert.Single(queuesAfterCreate, queue => queue.Id == 2).Name);
                Assert.Equal(789_012, Assert.Single(queuesAfterCreate, queue => queue.Id == 2).QuotaBytesUsed);

                var editedSettings = ctx.Service.GetSnapshot().Settings;
                var night = Assert.Single(editedSettings.Queues, queue => queue.Id == 1);
                night.Name = "Night edited";
                night.MaxConcurrentDownloads = 5;
                night.QuotaBytes = 3_000_000;
                night.QuotaWindowHours = 12;
                // Leave usage, window start, and stopped state untouched while editing.
                var rejected = await ctx.Service.QueueActionAsync(new QueueActionRequest
                {
                    QueueId = 1, Action = "policy", Name = "Invalid", MaxConcurrentDownloads = 0,
                });
                Assert.False(rejected.Ok);
                Assert.Equal("Night", ctx.Service.GetSnapshot().Settings.Queues.Single(q => q.Id == 1).Name);
                var edited = await ctx.Service.QueueActionAsync(new QueueActionRequest
                {
                    QueueId = 1, Action = "policy", Name = night.Name,
                    MaxConcurrentDownloads = night.MaxConcurrentDownloads,
                    QuotaEnabled = true, QuotaBytes = night.QuotaBytes, QuotaWindowHours = night.QuotaWindowHours,
                });
                Assert.True(edited.Ok, edited.Error);
            }

            await using var restarted = await ServiceContext.CreateAsync(
                _server, paths, destination, autoResume: false);
            var queues = restarted.Service.GetSnapshot().Settings.Queues;
            var persistedNight = Assert.Single(queues, queue => queue.Id == 1);
            Assert.Equal("Night edited", persistedNight.Name);
            Assert.Equal(5, persistedNight.MaxConcurrentDownloads);
            Assert.False(persistedNight.IsRunning);
            Assert.True(persistedNight.QuotaEnabled);
            Assert.Equal(3_000_000, persistedNight.QuotaBytes);
            Assert.Equal(12, persistedNight.QuotaWindowHours);
            Assert.Equal(123_456, persistedNight.QuotaBytesUsed);
            Assert.Equal(windowStart, persistedNight.QuotaWindowStart);

            var unchangedSync = Assert.Single(queues, queue => queue.Id == 2);
            Assert.Equal("Sync", unchangedSync.Name);
            Assert.Equal(4, unchangedSync.MaxConcurrentDownloads);
            Assert.True(unchangedSync.IsRunning);
            Assert.True(unchangedSync.QuotaEnabled);
            Assert.Equal(5_000_000, unchangedSync.QuotaBytes);
            Assert.Equal(24, unchangedSync.QuotaWindowHours);
            Assert.Equal(789_012, unchangedSync.QuotaBytesUsed);
            Assert.Equal(windowStart.AddHours(-1), unchangedSync.QuotaWindowStart);

            var persistedBrowser = Assert.Single(queues, queue => queue.Id == 3);
            Assert.Equal("Browser", persistedBrowser.Name);
            Assert.Equal(1, persistedBrowser.MaxConcurrentDownloads);
            Assert.False(persistedBrowser.IsRunning);
            Assert.False(persistedBrowser.QuotaEnabled);

            var persistedNightBackup = Assert.Single(queues, queue => queue.Id == 4);
            Assert.Equal("Night backup", persistedNightBackup.Name);
            Assert.Equal(2, persistedNightBackup.MaxConcurrentDownloads);
            Assert.False(persistedNightBackup.IsRunning);
            Assert.True(persistedNightBackup.QuotaEnabled);
            Assert.Equal(1_500_000, persistedNightBackup.QuotaBytes);
            Assert.Equal(6, persistedNightBackup.QuotaWindowHours);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task QueueActionsPersistOrderAndProtectDefaultOrAttachedQueues()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "test-work", Guid.NewGuid().ToString("N"));
        var paths = new StoragePaths(Path.Combine(root, "state"));
        var destination = Path.Combine(root, "downloads");
        Directory.CreateDirectory(destination);
        paths.EnsureCreated();
        try
        {
            await using (var ctx = await ServiceContext.CreateAsync(
                             _server, paths, destination, autoResume: false))
            {
                var jobsQueue = await ctx.Service.QueueActionAsync(new QueueActionRequest
                {
                    Action = "create", Name = "Jobs", MaxConcurrentDownloads = 1,
                });
                var emptyQueue = await ctx.Service.QueueActionAsync(new QueueActionRequest
                {
                    Action = "create", Name = "Empty", MaxConcurrentDownloads = 2,
                });
                var orderedQueue = await ctx.Service.QueueActionAsync(new QueueActionRequest
                {
                    Action = "create", Name = "Ordered", MaxConcurrentDownloads = 3,
                });
                Assert.True(jobsQueue.Ok, jobsQueue.Error);
                Assert.True(emptyQueue.Ok, emptyQueue.Error);
                Assert.True(orderedQueue.Ok, orderedQueue.Error);
                Assert.Equal(new[] { 0, 1, 2, 3 },
                    ctx.Service.GetSnapshot().Settings.Queues.Select(queue => queue.Id));

                var added = await ctx.Service.AddJobAsync(new AddJobRequest
                {
                    Url = $"{_server.BaseAddress}/file/queue-delete-guard.bin?size=4096&seed=433",
                    DestinationDirectory = destination,
                    FileName = "queue-delete-guard.bin",
                    Source = "cli",
                    QueueId = 1,
                    StartMode = "later",
                });
                Assert.True(added.Accepted, added.Error);

                var rename = await ctx.Service.QueueActionAsync(new QueueActionRequest
                {
                    QueueId = 1, Action = "policy", Name = "Renamed jobs", MaxConcurrentDownloads = 1,
                });
                Assert.True(rename.Ok, rename.Error);
                Assert.Equal("Renamed jobs", ctx.Service.GetJob(added.Job!.Id)!.QueueName);

                var deleteDefault = await ctx.Service.QueueActionAsync(new QueueActionRequest
                {
                    QueueId = 0, Action = "delete",
                });
                Assert.False(deleteDefault.Ok);
                Assert.Contains(ctx.Service.GetSnapshot().Settings.Queues, queue => queue.Id == 0);

                var deleteAttached = await ctx.Service.QueueActionAsync(new QueueActionRequest
                {
                    QueueId = 1, Action = "delete",
                });
                Assert.False(deleteAttached.Ok);
                Assert.Contains(ctx.Service.GetSnapshot().Settings.Queues, queue => queue.Id == 1);
                Assert.Equal(1, ctx.Service.GetJob(added.Job!.Id)!.QueueId);

                Assert.True((await ctx.Service.QueueActionAsync(new QueueActionRequest
                {
                    QueueId = 3, Action = "moveup",
                })).Ok);
                Assert.True((await ctx.Service.QueueActionAsync(new QueueActionRequest
                {
                    QueueId = 1, Action = "movedown",
                })).Ok);
                var ordered = ctx.Service.GetSnapshot().Settings.Queues;
                Assert.Equal(new[] { 0, 3, 1, 2 }, ordered.Select(queue => queue.Id));
                Assert.Equal(new[] { 0, 1, 2, 3 }, ordered.Select(queue => queue.Order));

                var deleteEmpty = await ctx.Service.QueueActionAsync(new QueueActionRequest
                {
                    QueueId = 2, Action = "delete",
                });
                Assert.True(deleteEmpty.Ok, deleteEmpty.Error);
                Assert.DoesNotContain(ctx.Service.GetSnapshot().Settings.Queues, queue => queue.Id == 2);
                Assert.Equal(1, ctx.Service.GetJob(added.Job.Id)!.QueueId);
            }

            await using var restarted = await ServiceContext.CreateAsync(
                _server, paths, destination, autoResume: false);
            var afterRestart = restarted.Service.GetSnapshot();
            Assert.Equal(new[] { 0, 3, 1 }, afterRestart.Settings.Queues.Select(queue => queue.Id));
            Assert.Equal(new[] { 0, 1, 2 }, afterRestart.Settings.Queues.Select(queue => queue.Order));
            Assert.DoesNotContain(afterRestart.Settings.Queues, queue => queue.Id == 2);
            var retainedJob = Assert.Single(afterRestart.Jobs, job => job.FileName == "queue-delete-guard.bin");
            Assert.Equal(1, retainedJob.QueueId);
            Assert.Equal("Renamed jobs", retainedJob.QueueName);
            Assert.Equal("queued", retainedJob.State);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
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
    public async Task ProxyPasswordCanBeClearedWithoutChangingAddressOrUser()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var settings = ctx.Service.GetSnapshot().Settings;
        settings.Proxy = new ProxyDto
        {
            Mode = "manual", Address = "127.0.0.1:3128", Username = "keepuser", Password = "clear-me-789",
        };
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);
        Assert.True(ctx.Service.GetSnapshot().Settings.Proxy.HasPassword);

        // Empty password without the flag keeps the saved one (existing behaviour).
        settings = ctx.Service.GetSnapshot().Settings;
        settings.Proxy.Password = null;
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);
        Assert.True(ctx.Service.GetSnapshot().Settings.Proxy.HasPassword);

        settings = ctx.Service.GetSnapshot().Settings;
        settings.Proxy.Password = null;
        settings.Proxy.ClearPassword = true;
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);
        var cleared = ctx.Service.GetSnapshot().Settings.Proxy;
        Assert.False(cleared.HasPassword);
        Assert.Equal("keepuser", cleared.Username);
        Assert.Equal("127.0.0.1:3128", cleared.Address);
        var json = await File.ReadAllTextAsync(ctx.Paths.SettingsFile);
        Assert.DoesNotContain("clear-me-789", json);
        Assert.DoesNotContain("dpapi:v1:", json);   // no protected blob left behind (no site logins here)
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
    public async Task ProxyPasswordIsDpapiProtectedOnDiskAndSurvivesRestart()
    {
        await using var proxy = TestProxyServer.Start();
        var ctx = await ServiceContext.CreateAsync(_server);
        try
        {
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

            var json = await File.ReadAllTextAsync(ctx.Paths.SettingsFile);
            Assert.DoesNotContain("proxypass", json);
            Assert.Contains("dpapi:v1:", json);
            Assert.DoesNotContain("\"password\"", json, StringComparison.OrdinalIgnoreCase);

            // A fresh service instance decrypts the blob and authenticates to the proxy.
            await using var second = await ServiceContext.CreateAsync(
                _server, ctx.Paths, ctx.Destination, autoResume: false);
            Assert.True(second.Service.GetSnapshot().Settings.Proxy.HasPassword);
            Assert.Null(second.Service.GetSnapshot().Settings.Proxy.Password);
            var response = await second.Service.AddJobAsync(new AddJobRequest
            {
                Url = $"{_server.BaseAddress}/file/proxy-restart.bin?size=262144&seed=101",
                DestinationDirectory = ctx.Destination,
                FileName = "proxy-restart.bin",
                Source = "cli",
            });
            Assert.True(response.Accepted, response.Error);
            var completed = await second.WaitFor(response.Job!.Id, j => j.State == "completed",
                TimeSpan.FromSeconds(30));
            Assert.True(proxy.Authorized > 0);
            Assert.Equal(0, proxy.Denied);
            var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                TestContent.Generate(101, 262_144))).ToLowerInvariant();
            Assert.Equal(expected, await Hashing.Sha256FileAsync(completed.FinalFilePath));
        }
        finally
        {
            await ctx.DisposeAsync();
        }
    }

    [Fact]
    public async Task UndecryptableProxyBlobYieldsNoPasswordWithoutCrashing()
    {
        var ctx = await ServiceContext.CreateAsync(_server);
        try
        {
            var settings = ctx.Service.GetSnapshot().Settings;
            settings.Proxy = new ProxyDto
            {
                Mode = "manual",
                Address = "http://proxy.example:8080",
                Username = "proxyuser",
                Password = "proxypass",
            };
            Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);

            var node = System.Text.Json.Nodes.JsonNode.Parse(
                await File.ReadAllTextAsync(ctx.Paths.SettingsFile))!.AsObject();
            node["proxy"]!.AsObject()["passwordProtected"] = "dpapi:v1:AAAAAAAA";
            await File.WriteAllTextAsync(ctx.Paths.SettingsFile, node.ToJsonString());

            await using var second = await ServiceContext.CreateAsync(
                _server, ctx.Paths, ctx.Destination, autoResume: false);
            var proxySettings = second.Service.GetSnapshot().Settings.Proxy;
            Assert.Equal("proxyuser", proxySettings.Username);
            Assert.False(proxySettings.HasPassword);
        }
        finally
        {
            await ctx.DisposeAsync();
        }
    }

    [Fact]
    public async Task Socks4SettingsApplyToRuntimeDownloads()
    {
        await using var socks = TestSocks4Server.Start();
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var settings = ctx.Service.GetSnapshot().Settings;
        settings.Proxy = new ProxyDto
        {
            Mode = "manual",
            Address = $"socks4://{socks.Address}",
            Username = "socks4user",
        };
        var update = await ctx.Service.UpdateSettingsAsync(settings);
        Assert.True(update.Ok, update.Error);

        var response = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/file/runtime-socks4.bin?size=524288&seed=103",
            DestinationDirectory = ctx.Destination,
            FileName = "runtime-socks4.bin",
            Source = "cli",
        });
        Assert.True(response.Accepted, response.Error);
        var completed = await ctx.WaitFor(response.Job!.Id, j => j.State == "completed", TimeSpan.FromSeconds(30));
        Assert.True(socks.Connects > 0);
        Assert.Equal(0, socks.Denied);
        Assert.Equal("socks4user", socks.LastUserId);
        var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            TestContent.Generate(103, 524_288))).ToLowerInvariant();
        Assert.Equal(expected, await Hashing.Sha256FileAsync(completed.FinalFilePath));
    }

    [Fact]
    public async Task UpdateOutcomeIsToldOnlyWhileItStillApplies()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var root = Path.Combine(AppContext.BaseDirectory, "test-work", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "install.json"), """{"version":"0.3.2","channel":"stable"}""");
        var updates = Path.Combine(ctx.Paths.Root, "updates");
        Directory.CreateDirectory(updates);
        var result = Path.Combine(updates, "install-result.json");
        void Write(string status, string version, string at) => File.WriteAllText(result,
            $$"""{"status":"{{status}}","version":"{{version}}","error":"The file is in use.","at":"{{at}}"}""");

        var previous = Environment.GetEnvironmentVariable("DOWNLOADMANAGER_INSTALL_ROOT");
        Environment.SetEnvironmentVariable("DOWNLOADMANAGER_INSTALL_ROOT", root);
        try
        {
            // Setup has installed this version since the update to it failed: old news.
            Write("failed", "0.3.2", "2026-11-06T01:00:00+03:00");
            Assert.Null(ctx.Service.TakeUpdateInstallOutcome());
            Assert.Null(ctx.Service.GetSnapshot().UpdateProgress);

            Write("failed", "0.3.3", "2026-11-06T02:00:00+03:00");
            var failed = ctx.Service.TakeUpdateInstallOutcome();
            Assert.NotNull(failed);
            Assert.False(failed.Succeeded);
            Assert.Null(ctx.Service.TakeUpdateInstallOutcome());
            // Still there when the update window is opened later.
            Assert.Equal("failed", ctx.Service.GetSnapshot().UpdateProgress?.Stage);
            Assert.Equal("The file is in use.", ctx.Service.GetSnapshot().UpdateProgress?.Error);

            Write("succeeded", "0.3.2", "2026-11-06T03:00:00+03:00");
            Assert.True(ctx.Service.TakeUpdateInstallOutcome()?.Succeeded);

            // "Succeeded" while that version is not what is installed tells nothing true.
            Write("succeeded", "0.3.3", "2026-11-06T04:00:00+03:00");
            Assert.Null(ctx.Service.TakeUpdateInstallOutcome());

            // Nor does an older success left by an earlier version of the helper.
            Write("succeeded", "0.3.1", "2026-11-06T05:00:00+03:00");
            Assert.Null(ctx.Service.TakeUpdateInstallOutcome());
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOWNLOADMANAGER_INSTALL_ROOT", previous);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RefreshingToAnotherSiteLeavesTheSavedLoginBehind()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        const string path = "/slow/refresh-login.bin?size=4194304&seed=97&bps=131072";
        var response = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}{path}",
            DestinationDirectory = ctx.Destination,
            FileName = "refresh-login.bin",
            Source = "cli",
            Referrer = "https://page.test/list",
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Cookie"] = "session=secret" },
        });
        Assert.True(response.Accepted, response.Error);
        var id = response.Job!.Id;
        var jobFile = Path.Combine(ctx.Paths.JobsDirectory, $"{id:N}.json");
        async Task PauseAsync()
        {
            await ctx.WaitFor(id, job => job.ReceivedBytes > 0);
            Assert.True((await ctx.Service.PauseAsync(id)).Ok);
            await ctx.WaitFor(id, job => job.State == "paused");
        }

        // A refresh is refused until the paused transfer has fully stopped.
        async Task RefreshAsync(string url)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            var refreshed = await ctx.Service.RefreshUrlAsync(id, url);
            while (!refreshed.Ok && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
                refreshed = await ctx.Service.RefreshUrlAsync(id, url);
            }

            Assert.True(refreshed.Ok, refreshed.Error);
        }

        await PauseAsync();
        Assert.Contains("Cookie", await File.ReadAllTextAsync(jobFile));

        // The same site: a fresh link to the same file keeps the login.
        await RefreshAsync($"{_server.BaseAddress}{path}&fresh=1");
        await PauseAsync();
        var kept = await File.ReadAllTextAsync(jobFile);
        Assert.Contains("Cookie", kept);
        Assert.Contains("page.test", kept);

        // Another site, here the same server under another name: nothing of the login goes with it.
        var elsewhere = _server.BaseAddress.ToString().Replace("127.0.0.1", "localhost");
        Assert.NotEqual(_server.BaseAddress.ToString(), elsewhere);
        await RefreshAsync($"{elsewhere}{path}&fresh=2");
        await PauseAsync();
        var moved = await File.ReadAllTextAsync(jobFile);
        Assert.DoesNotContain("Cookie", moved);
        Assert.DoesNotContain("page.test", moved);

        await ctx.Service.CancelAsync(id, deleteFile: true);
    }

    [Fact]
    public async Task UpdatePackagesAreRemovedOnceTheirOutcomeIsKnown()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var root = Path.Combine(AppContext.BaseDirectory, "test-work", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "install.json"), """{"version":"0.3.2","channel":"stable"}""");
        var updates = Path.Combine(ctx.Paths.Root, "updates");
        var stage = Path.Combine(updates, "stage-0.3.1");
        Directory.CreateDirectory(Path.Combine(stage, "app"));
        File.WriteAllText(Path.Combine(stage, "app", "file.dll"), "x");
        var zip = Path.Combine(updates, "installer-0.3.1.zip");
        File.WriteAllText(zip, "x");
        var result = Path.Combine(updates, "install-result.json");

        var previous = Environment.GetEnvironmentVariable("DOWNLOADMANAGER_INSTALL_ROOT");
        Environment.SetEnvironmentVariable("DOWNLOADMANAGER_INSTALL_ROOT", root);
        try
        {
            // Nothing known yet: the helper may still be using them.
            Assert.Null(ctx.Service.TakeUpdateInstallOutcome());
            Assert.True(Directory.Exists(stage));
            Assert.True(File.Exists(zip));

            File.WriteAllText(result, """{"status":"succeeded","version":"0.3.2","error":"","at":"2026-11-06T01:00:00+03:00"}""");
            Assert.NotNull(ctx.Service.TakeUpdateInstallOutcome());
            Assert.False(Directory.Exists(stage));
            Assert.False(File.Exists(zip));
            Assert.True(File.Exists(result));
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOWNLOADMANAGER_INSTALL_ROOT", previous);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AnUpdateAlreadyDownloadedAndIntactIsNotFetchedAgain()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var root = await OfferUpdateWithHelperAsync(ctx, "exit 3");
        var previous = Environment.GetEnvironmentVariable("DOWNLOADMANAGER_INSTALL_ROOT");
        Environment.SetEnvironmentVariable("DOWNLOADMANAGER_INSTALL_ROOT", root);
        try
        {
            var first = await ctx.Service.DownloadUpdateAsync();
            Assert.True(first.Ok, first.Error);
            var written = File.GetLastWriteTimeUtc(first.Path!);
            await Task.Delay(50);

            var again = await ctx.Service.DownloadUpdateAsync();
            Assert.True(again.Ok, again.Error);
            Assert.Equal(written, File.GetLastWriteTimeUtc(again.Path!));
            Assert.Equal(first.Sha256, again.Sha256);

            // A damaged copy does not pass for the real one.
            File.WriteAllBytes(first.Path!, new byte[first.Size!.Value]);
            var repaired = await ctx.Service.DownloadUpdateAsync();
            Assert.True(repaired.Ok, repaired.Error);
            Assert.Equal(first.Sha256, await Hashing.Sha256FileAsync(repaired.Path!));
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOWNLOADMANAGER_INSTALL_ROOT", previous);
            Directory.Delete(Path.GetDirectoryName(root)!, recursive: true);
        }
    }

    // Offers version 1.0.1 of a package holding only this helper script. Returns the install root.
    private async Task<string> OfferUpdateWithHelperAsync(ServiceContext ctx, string helperScript)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "test-work", Guid.NewGuid().ToString("N"), "install");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "install.json"), """{"version":"1.0.0","channel":"stable"}""");

        using var zipped = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(zipped, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        using (var writer = new StreamWriter(zip.CreateEntry("apply-update.ps1").Open()))
        {
            await writer.WriteAsync(helperScript);
        }

        var package = zipped.ToArray();
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var manifest = new UpdateManifest
        {
            Version = "1.0.1",
            Channel = "stable",
            InstallerUrl = $"{_server.BaseAddress}/update/package",
            Size = package.Length,
            Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(package)).ToLowerInvariant(),
        };
        manifest.Signature = Convert.ToBase64String(rsa.SignData(
            System.Text.Encoding.UTF8.GetBytes(UpdateManifestVerifier.Canonical(manifest)),
            System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1));
        _server.SetUpdatePayload(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(manifest, IpcJson.Options), package);

        var settings = ctx.Service.GetSnapshot().Settings;
        settings.UpdateManifestUrl = $"{_server.BaseAddress}/update/manifest";
        settings.UpdatePublicKey = rsa.ExportSubjectPublicKeyInfoPem();
        settings.UpdateChannel = "stable";
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);
        return root;
    }

    [Fact]
    public async Task UpdateHelperThatExitsAtOnceLeavesTheHostRunning()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var root = await OfferUpdateWithHelperAsync(ctx, "exit 3");
        var previous = Environment.GetEnvironmentVariable("DOWNLOADMANAGER_INSTALL_ROOT");
        Environment.SetEnvironmentVariable("DOWNLOADMANAGER_INSTALL_ROOT", root);
        try
        {
            var result = await ctx.Service.InstallUpdateAsync();

            // Not "scheduled", which is what makes the Host shut down.
            Assert.False(result.Ok);
            Assert.Equal("launchFailed", result.Status);
            Assert.False(string.IsNullOrWhiteSpace(result.Error));
            Assert.Equal("failed", ctx.Service.GetSnapshot().UpdateProgress?.Stage);

            // Told now, so not again at the next start, and not retried on its own.
            Assert.Null(ctx.Service.TakeUpdateInstallOutcome());
            Assert.True(ctx.Service.UpdateFailedRecently("1.0.1"));

            // The Host is still in business.
            var following = await ctx.Service.AddJobAsync(new AddJobRequest
            {
                Url = $"{_server.BaseAddress}/file/after-helper-exit.bin?size=4096&seed=412",
                Source = "cli",
            });
            Assert.True(following.Accepted, following.Error);
            await ctx.WaitFor(following.Job!.Id, job => job.State == "completed");
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOWNLOADMANAGER_INSTALL_ROOT", previous);
            Directory.Delete(Path.GetDirectoryName(root)!, recursive: true);
        }
    }

    [Fact]
    public async Task UpdateHelperStillWaitingForTheHostIsScheduled()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var root = await OfferUpdateWithHelperAsync(ctx, "Start-Sleep -Seconds 120");
        var previous = Environment.GetEnvironmentVariable("DOWNLOADMANAGER_INSTALL_ROOT");
        Environment.SetEnvironmentVariable("DOWNLOADMANAGER_INSTALL_ROOT", root);
        try
        {
            var result = await ctx.Service.InstallUpdateAsync();

            Assert.True(result.Ok, result.Error);
            Assert.Equal("scheduled", result.Status);
            Assert.Equal("installing", ctx.Service.GetSnapshot().UpdateProgress?.Stage);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOWNLOADMANAGER_INSTALL_ROOT", previous);
            var pending = Path.Combine(ctx.Paths.Root, "updates", "install-pending.json");
            if (File.Exists(pending))
            {
                using var document = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(pending));
                try
                {
                    using var helper = System.Diagnostics.Process.GetProcessById(
                        document.RootElement.GetProperty("helperProcessId").GetInt32());
                    helper.Kill();
                    await helper.WaitForExitAsync();
                }
                catch (ArgumentException) { }
            }

            Directory.Delete(Path.GetDirectoryName(root)!, recursive: true);
        }
    }

    [Fact]
    public async Task AddWithoutFileNameUsesServerContentDispositionName()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var disposition = Uri.EscapeDataString("attachment; filename=\"Game.part01.rar\"; filename*=UTF-8''Game.part01.rar");
        var url = $"{_server.BaseAddress}/filename/696b84a7-c512-4aab-8825-a28864a1bd57?disposition={disposition}";
        var added = await ctx.Service.AddJobAsync(new AddJobRequest { Url = url, Source = "batch" });
        Assert.True(added.Accepted, added.Error);
        Assert.Equal("Game.part01.rar", added.Job!.FileName);
        Assert.Equal("compressed", added.Job.CategoryId);
        var completed = await ctx.WaitFor(added.Job.Id, job => job.State == "completed");
        Assert.Equal("Game.part01.rar", Path.GetFileName(completed.FinalFilePath));
    }

    [Fact]
    public async Task FileMetadataPreviewDoesNotCreateJobsPayloadOrHistoryAndSuggestedNameCanBeDownloaded()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var disposition = Uri.EscapeDataString("attachment; filename=server-report.zip");
        var url = $"{_server.BaseAddress}/filename/opaque?disposition={disposition}";
        var before = ctx.Service.GetSnapshot().Settings.RecentUrls.ToArray();
        var metadata = await ctx.Service.ReadFileMetadataAsync(new FileMetadataRequest { Url = url });
        Assert.True(metadata.Ok, metadata.Error);
        Assert.Equal("server-report.zip", metadata.FileName);
        Assert.Equal(1024, metadata.TotalBytes);
        Assert.Equal("application/octet-stream", metadata.ContentType);
        Assert.Empty(ctx.Service.GetSnapshot().Jobs);
        Assert.Equal(before, ctx.Service.GetSnapshot().Settings.RecentUrls);
        Assert.Empty(Directory.GetFiles(ctx.Destination, "*", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(ctx.Paths.JobsDirectory, "*.json"));
        var added = await ctx.Service.AddJobAsync(new AddJobRequest { Url = url, FileName = metadata.FileName });
        Assert.True(added.Accepted, added.Error);
        var completed = await ctx.WaitFor(added.Job!.Id, job => job.State == "completed");
        Assert.Equal("server-report.zip", completed.FileName);
        Assert.Equal("compressed", completed.CategoryId);
        Assert.True(File.Exists(completed.FinalFilePath));
        Assert.Equal(1024, new FileInfo(completed.FinalFilePath).Length);
    }

    [Fact]
    public async Task FileMetadataPreviewUsesPerRequestOrSavedAuthenticationAndRejectsMalformedHeaders()
    {
        _server.SetSiteCredentials("preview-user", "preview-pass");
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var url = $"{_server.BaseAddress}/site/secure/preview.bin?size=65536&seed=910";
        var denied = await ctx.Service.ReadFileMetadataAsync(new FileMetadataRequest { Url = url });
        Assert.False(denied.Ok);
        var request = new FileMetadataRequest { Url = url, Headers = new() { ["Authorization"] = "Basic " +
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("preview-user:preview-pass")) } };
        var authorized = await ctx.Service.ReadFileMetadataAsync(request);
        Assert.True(authorized.Ok, authorized.Error);
        Assert.Equal(65536, authorized.TotalBytes);
        var settings = ctx.Service.GetSnapshot().Settings;
        Assert.Empty(settings.SiteLogins);
        settings.SiteLogins.Add(new SiteLoginDto { Id = Guid.NewGuid(), Site = _server.BaseAddress, Username = "preview-user", Password = "preview-pass" });
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);
        var saved = await ctx.Service.ReadFileMetadataAsync(new FileMetadataRequest { Url = url });
        Assert.True(saved.Ok, saved.Error);
        var invalid = await ctx.Service.ReadFileMetadataAsync(new FileMetadataRequest { Url = url, Headers = new() { ["X-Test"] = "invalid\r\nheader" } });
        Assert.False(invalid.Ok);
        Assert.Empty(ctx.Service.GetSnapshot().Jobs);
        Assert.Empty(Directory.GetFiles(ctx.Paths.JobsDirectory, "*.json"));
        Assert.False((await ctx.Service.ReadFileMetadataAsync(new FileMetadataRequest { Url = "file:///private.txt" })).Ok);
    }

    [Fact]
    public async Task PerDownloadAuthenticationIsProtectedAndWorksAfterRestart()
    {
        _server.SetSiteCredentials("manualuser", "manualpass");
        var token = "Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("manualuser:manualpass"));
        await using var ctx = await ServiceContext.CreateAsync(_server, autoResume: false);
        var response = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/site/secure/manual-auth.bin?size=131072&seed=143",
            DestinationDirectory = ctx.Destination, FileName = "manual-auth.bin", StartMode = "later",
            Headers = new(StringComparer.OrdinalIgnoreCase)
            {
                ["Authorization"] = token, ["Cookie"] = "test-session=private-cookie-value",
                ["X-Test"] = "ordinary-header",
            },
        });
        Assert.True(response.Accepted, response.Error);
        var jobPath = Path.Combine(ctx.Paths.JobsDirectory, $"{response.Job!.Id:N}.json");
        var json = await File.ReadAllTextAsync(jobPath);
        Assert.DoesNotContain(token, json);
        Assert.DoesNotContain("private-cookie-value", json);
        Assert.Contains("dpapi:v1:", json);
        var store = new JsonJobStore(ctx.Paths);
        var reloaded = await store.LoadAsync(response.Job.Id);
        Assert.Equal(token, reloaded!.RequestHeaders["authorization"]);
        Assert.Equal("test-session=private-cookie-value", reloaded.RequestHeaders["cookie"]);
        Assert.Equal("ordinary-header", reloaded.RequestHeaders["X-Test"]);
        var snapshotJson = System.Text.Json.JsonSerializer.Serialize(ctx.Service.GetSnapshot());
        Assert.DoesNotContain(token, snapshotJson);
        Assert.DoesNotContain("private-cookie-value", snapshotJson);
        await using var restarted = await ServiceContext.CreateAsync(_server, ctx.Paths, ctx.Destination, autoResume: false);
        Assert.Empty(restarted.Service.GetSnapshot().Settings.SiteLogins);
        Assert.True((await restarted.Service.ResumeAsync(response.Job.Id)).Ok);
        var completed = await restarted.WaitFor(response.Job.Id, job => job.State == "completed");
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(TestContent.Generate(143, 131072))).ToLowerInvariant(),
            await Hashing.Sha256FileAsync(completed.FinalFilePath));
    }

    [Fact]
    public async Task HeaderPersistenceProtectsSecretsAndDropsUndecryptableValues()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server, autoResume: false);
        var store = new JsonJobStore(ctx.Paths);
        var job = new DownloadJob { Url = $"{_server.BaseAddress}/file/legacy-header.bin", FileName = "legacy-header.bin",
            RequestHeaders = new(StringComparer.OrdinalIgnoreCase) { ["cOoKiE"] = "legacy-cookie-secret", ["Authorization"] = "Basic legacy-secret" } };
        var path = Path.Combine(ctx.Paths.JobsDirectory, $"{job.Id:N}.json");
        await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(job));
        var loaded = await store.LoadAsync(job.Id);
        Assert.Equal("legacy-cookie-secret", loaded!.RequestHeaders["Cookie"]);
        await store.SaveAsync(loaded);
        Assert.Equal("legacy-cookie-secret", loaded.RequestHeaders["Cookie"]); // saving never mutates the live request
        var protectedJson = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain("legacy-cookie-secret", protectedJson);
        Assert.DoesNotContain("legacy-secret", protectedJson);
        var node = System.Text.Json.Nodes.JsonNode.Parse(protectedJson)!;
        node["requestHeadersProtected"]!["Authorization"] = "dpapi:v1:AAAA";
        await File.WriteAllTextAsync(path, node.ToJsonString());
        var all = await store.LoadAllAsync();
        var damaged = Assert.Single(all, item => item.Id == job.Id);
        Assert.False(damaged.RequestHeaders.ContainsKey("Authorization"));
        Assert.Equal("legacy-cookie-secret", damaged.RequestHeaders["Cookie"]);
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
    public async Task DownloadLaterWaitsForExplicitQueueStart()
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
    public async Task HlsAes128AndRotatingKeysDecryptToExpectedOutput()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        using var http = new HttpClient();
        using var expectedJson = System.Text.Json.JsonDocument.Parse(
            await http.GetStringAsync($"{_server.BaseAddress}/hls/expected"));
        var expectedHash = expectedJson.RootElement.GetProperty("sha256").GetString();
        var expectedBytes = expectedJson.RootElement.GetProperty("bytes").GetInt64();

        foreach (var playlist in new[] { "aes.m3u8", "aes-rotate.m3u8" })
        {
            var response = await ctx.Service.AddJobAsync(new AddJobRequest
            {
                Url = $"{_server.BaseAddress}/hls/{playlist}",
                DestinationDirectory = ctx.Destination,
                FileName = $"hls-{Path.GetFileNameWithoutExtension(playlist)}.m3u8",
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
    public async Task HlsAlternateAudioMuxesWhenFfmpegAvailable()
    {
        var ffmpeg = Environment.GetEnvironmentVariable("DOWNLOADMANAGER_FFMPEG");
        if (string.IsNullOrWhiteSpace(ffmpeg) || !File.Exists(ffmpeg))
        {
            return;
        }

        await using var ctx = await ServiceContext.CreateAsync(_server);
        using var http = new HttpClient();
        using var expectedJson = System.Text.Json.JsonDocument.Parse(
            await http.GetStringAsync($"{_server.BaseAddress}/hls/av-expected"));
        var expectedHash = expectedJson.RootElement.GetProperty("sha256").GetString();
        var expectedBytes = expectedJson.RootElement.GetProperty("bytes").GetInt64();

        var response = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/hls/av-master.m3u8",
            DestinationDirectory = ctx.Destination,
            FileName = "hls-av.m3u8",
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
    public async Task DashTimelineAndSegmentListMatchExpectedOutput()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        using var http = new HttpClient();
        using var expectedJson = System.Text.Json.JsonDocument.Parse(
            await http.GetStringAsync($"{_server.BaseAddress}/dash/expected"));
        var expectedHash = expectedJson.RootElement.GetProperty("sha256").GetString();
        var expectedBytes = expectedJson.RootElement.GetProperty("bytes").GetInt64();

        foreach (var manifest in new[] { "timeline.mpd", "segmentlist.mpd" })
        {
            var response = await ctx.Service.AddJobAsync(new AddJobRequest
            {
                Url = $"{_server.BaseAddress}/dash/{manifest}",
                DestinationDirectory = ctx.Destination,
                FileName = $"dash-{Path.GetFileNameWithoutExtension(manifest)}.mpd",
                Source = "cli",
            });
            Assert.True(response.Accepted, response.Error);
            var completed = await ctx.WaitFor(response.Job!.Id, j => j.State == "completed",
                TimeSpan.FromSeconds(30));
            Assert.Null(completed.Failure);
            Assert.Equal(expectedBytes, completed.ReceivedBytes);
            Assert.Equal(expectedBytes, new FileInfo(completed.FinalFilePath).Length);
            Assert.Equal(expectedHash, await Hashing.Sha256FileAsync(completed.FinalFilePath));
        }
    }

    [Fact]
    public async Task DashSegmentBaseAndSegmentListRangesMatchExpectedOutput()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        using var http = new HttpClient();
        using var expectedJson = System.Text.Json.JsonDocument.Parse(
            await http.GetStringAsync($"{_server.BaseAddress}/dash/expected"));
        var expectedHash = expectedJson.RootElement.GetProperty("sha256").GetString();
        var expectedBytes = expectedJson.RootElement.GetProperty("bytes").GetInt64();

        foreach (var manifest in new[] { "segmentbase.mpd", "segmentlist-range.mpd" })
        {
            _server.ResetHlsStats();
            var response = await ctx.Service.AddJobAsync(new AddJobRequest
            {
                Url = $"{_server.BaseAddress}/dash-range/{manifest}",
                DestinationDirectory = ctx.Destination,
                FileName = $"dash-{Path.GetFileNameWithoutExtension(manifest)}.mpd",
                Source = "cli",
            });
            Assert.True(response.Accepted, response.Error);
            var completed = await ctx.WaitFor(response.Job!.Id, j => j.State == "completed",
                TimeSpan.FromSeconds(30));
            Assert.Null(completed.Failure);
            Assert.Equal(expectedBytes, completed.ReceivedBytes);
            Assert.Equal(expectedBytes, new FileInfo(completed.FinalFilePath).Length);
            Assert.Equal(expectedHash, await Hashing.Sha256FileAsync(completed.FinalFilePath));
            Assert.True(_server.DashRangeRequests("single.mp4") >= 4,
                $"{manifest} used {_server.DashRangeRequests("single.mp4")} range requests.");
        }
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

    private sealed class RecordingPostDownloadHook : IPostDownloadHook
    {
        public List<(string Command, string FilePath)> Calls { get; } = new();
        public bool Throw { get; init; }

        public Task RunAsync(string command, string filePath, CancellationToken cancellationToken)
        {
            Calls.Add((command, filePath));
            if (Throw)
            {
                throw new IOException("simulated hook failure");
            }

            return Task.CompletedTask;
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

    private sealed record ContextHooks
    {
        public Action<string>? Log { get; init; }
        public Func<ISettingsStore, ISettingsStore>? WrapSettings { get; init; }
        public Func<IJobStore, IJobStore>? WrapJobs { get; init; }
        public Func<IDownloadCheckpointStore, IDownloadCheckpointStore>? WrapCheckpoints { get; init; }
        /// <summary>For what happens during startup, which a test can't subscribe to afterwards.</summary>
        public Action<DownloadManagerService>? BeforeInitialize { get; init; }
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
        public HttpDownloadEngine Engine => _engine;
        public bool AutoResume { get; private set; }

        public static Task<ServiceContext> CreateAsync(
            TestServerHost server,
            bool autoResume = true,
            int maxConcurrent = 2,
            ICompletionActionExecutor? completionActions = null,
            ISleepPrevention? sleepPrevention = null,
            IPostDownloadHook? postDownloadHook = null,
            IRemoteLinkBroker? linkBroker = null,
            IRemoteSecretMatcher? linkSecrets = null,
            HttpMessageHandler? handler = null,
            ContextHooks? hooks = null)
        {
            var root = Path.Combine(AppContext.BaseDirectory, "test-work", Guid.NewGuid().ToString("N"));
            var paths = new StoragePaths(Path.Combine(root, "state"));
            var destination = Path.Combine(root, "downloads");
            Directory.CreateDirectory(destination);
            paths.EnsureCreated();
            return BuildAsync(server, paths, destination, autoResume, maxConcurrent, ownsPaths: true,
                completionActions, sleepPrevention, postDownloadHook, linkBroker, linkSecrets, handler, hooks);
        }

        public static Task<ServiceContext> CreateAsync(
            TestServerHost server,
            StoragePaths paths,
            string destination,
            bool autoResume = true,
            ICompletionActionExecutor? completionActions = null,
            ISleepPrevention? sleepPrevention = null,
            IPostDownloadHook? postDownloadHook = null,
            IRemoteLinkBroker? linkBroker = null,
            IRemoteSecretMatcher? linkSecrets = null,
            HttpMessageHandler? handler = null,
            ContextHooks? hooks = null,
            int maxConcurrent = 2)
            => BuildAsync(server, paths, destination, autoResume, maxConcurrent, ownsPaths: false,
                completionActions, sleepPrevention, postDownloadHook, linkBroker, linkSecrets, handler, hooks);

        private static async Task<ServiceContext> BuildAsync(
            TestServerHost server,
            StoragePaths paths,
            string destination,
            bool autoResume,
            int maxConcurrent,
            bool ownsPaths,
            ICompletionActionExecutor? completionActions = null,
            ISleepPrevention? sleepPrevention = null,
            IPostDownloadHook? postDownloadHook = null,
            IRemoteLinkBroker? linkBroker = null,
            IRemoteSecretMatcher? linkSecrets = null,
            HttpMessageHandler? handler = null,
            ContextHooks? hooks = null)
        {
            IDownloadCheckpointStore checkpoints = new JsonCheckpointStore(paths);
            if (hooks?.WrapCheckpoints is { } wrapCheckpoints) checkpoints = wrapCheckpoints(checkpoints);
            var mediaCheckpoints = new JsonMediaCheckpointStore(paths);
            IJobStore store = new JsonJobStore(paths);
            if (hooks?.WrapJobs is { } wrapJobs) store = wrapJobs(store);
            ISettingsStore settingsStore = new JsonSettingsStore(paths);
            if (hooks?.WrapSettings is { } wrapSettings) settingsStore = wrapSettings(settingsStore);
            var engine = new HttpDownloadEngine(
                new EngineOptions
                {
                    MaxAttempts = 3,
                    InitialRetryDelay = TimeSpan.FromMilliseconds(20),
                    MaxRetryDelay = TimeSpan.FromMilliseconds(100),
                    ReadTimeout = TimeSpan.FromSeconds(15),
                    AllowLocalNetwork = true,
                },
                handler: handler,
                checkpoints: checkpoints,
                mediaCheckpoints: mediaCheckpoints);
            var ftpEngine = new DownloadManager.Engine.FtpDownloadEngine(engine.SpeedBudget);
            var service = new DownloadManagerService(
                paths,
                store,
                checkpoints,
                settingsStore,
                engine,
                log: hooks?.Log,
                enableSchedulerTimer: false,
                completionActions: completionActions,
                sleepPrevention: sleepPrevention,
                mediaCheckpoints: mediaCheckpoints,
                ftpEngine: ftpEngine,
                postDownloadHook: postDownloadHook,
                linkBroker: linkBroker,
                linkSecrets: linkSecrets);
            // Most tests have nothing to wait for; the ones about the quiet period set their own.
            service.PackageSettleDelay = TimeSpan.Zero;
            hooks?.BeforeInitialize?.Invoke(service);
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
