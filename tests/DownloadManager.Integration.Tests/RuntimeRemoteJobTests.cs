using DownloadManager.Contracts;
using DownloadManager.Core;
using DownloadManager.Core.Providers;
using DownloadManager.Persistence;
using DownloadManager.Runtime;
using TestServer;

namespace DownloadManager.Integration.Tests;

public sealed partial class RuntimeServiceTests
{
    private const string LinkSecret = "not-a-real-key-7f3a";
    private const string PastedHost = "https://store-1.weur.tb-cdn.test";

    private static RemoteJobRequest RemoteFile(
        ServiceContext ctx,
        string fileId,
        string fileName,
        string? relativePath = null,
        long? size = null,
        string remoteJobId = "job-1",
        int maxConnections = 4)
        => new()
        {
            Source = new RemoteSource("torbox", "torrents", "77", fileId, relativePath ?? fileName, remoteJobId),
            FileName = fileName,
            DestinationRoot = ctx.Destination,
            MaxConnections = maxConnections,
            ExpectedTotalBytes = size,
        };

    private static JobDto AddedJob(RemoteJobsResult result, int index = 0)
    {
        var item = result.Items[index];
        Assert.True(item.Accepted, item.Error);
        return item.Job!;
    }

    private static async Task<string> JobJsonAsync(ServiceContext ctx, Guid id)
        => await File.ReadAllTextAsync(Path.Combine(ctx.Paths.JobsDirectory, $"{id:N}.json"));

    /// <summary>Every state file, so a leak anywhere fails the test.</summary>
    private static async Task AssertSecretNotOnDiskAsync(ServiceContext ctx, string secret)
    {
        foreach (var file in Directory.GetFiles(ctx.Paths.Root, "*", SearchOption.AllDirectories))
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    Assert.DoesNotContain(secret, await File.ReadAllTextAsync(file));
                    break;
                }
                // A store can be replacing the file right now, and the file after that will be read instead.
                catch (FileNotFoundException)
                {
                    break;
                }
                catch (IOException) when (attempt < 20)
                {
                    await Task.Delay(50);
                }
            }
        }
    }

    [Fact]
    public async Task PublicAddKeepsRejectingProviderReferences()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var response = await ctx.Service.AddJobAsync(new AddJobRequest { Url = "remote://torbox/torrents/1/2", FileName = "a.bin" });
        Assert.False(response.Accepted);
        Assert.Equal("invalid_url", response.ErrorCode);
    }

    [Fact]
    public async Task PublicAddCannotTakeAProviderRequestIdOrSource()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var forged = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/file/forged.bin?size=1024&seed=1",
            FileName = "forged.bin",
            OwnerExtensionId = "provider:torbox",
            ClientRequestId = "job-1/1",
            DestinationDirectory = ctx.Destination,
        });
        Assert.False(forged.Accepted);
        Assert.Equal("invalid_owner", forged.ErrorCode);

        var sourced = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/file/sourced.bin?size=1024&seed=1",
            FileName = "sourced.bin",
            Source = "remote",
            DestinationDirectory = ctx.Destination,
        });
        Assert.True(sourced.Accepted, sourced.Error);
        Assert.Equal("api", sourced.Job!.Source);
    }

    [Fact]
    public async Task RemoteJobWithoutABrokerFailsClearly()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var result = await ctx.Service.AddRemoteJobsAsync([RemoteFile(ctx, "1", "a.bin")], "remote:job-1");
        var failed = await ctx.WaitFor(AddedJob(result).Id, j => j.State == "failed");
        Assert.Contains("torbox", failed.Failure?.Message);
        Assert.True(failed.CanResume);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task RemoteJobDownloadsThroughItsBrokersLink(int maxConnections)
    {
        const int size = 2 * 1024 * 1024;
        const int seed = 701;
        var name = $"remote-{maxConnections}.bin";
        var link = $"{_server.BaseAddress}/file/{name}?size={size}&seed={seed}&sig={LinkSecret}";
        var broker = new ScriptedBroker(_ => RemoteLinkResolution.Link(link));
        await using var ctx = await ServiceContext.CreateAsync(_server, linkBroker: broker);
        var settings = ctx.Service.GetSnapshot().Settings;
        settings.DefaultConnections = 8;
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);

        var result = await ctx.Service.AddRemoteJobsAsync(
            [RemoteFile(ctx, "1", name, $"Show/S1/{name}", size, maxConnections: maxConnections)], "remote:job-1");
        var added = AddedJob(result);
        Assert.Equal($"remote://torbox/torrents/77/1", added.Url);
        Assert.Equal(Path.Combine(ctx.Destination, "Show", "S1"), added.DestinationDirectory);
        Assert.Equal(size, added.TotalBytes);
        Assert.True(added.IsRemote);
        Assert.Equal("torbox", added.RemoteProvider);
        Assert.Equal("remote", added.Source);
        Assert.Equal("provider:torbox", added.OwnerExtensionId);
        Assert.Equal("job-1/1", added.ClientRequestId);
        Assert.Equal(maxConnections, added.RequestedConnections);
        Assert.True(added.CanChangeSpeedLimit);

        var completed = await ctx.WaitFor(added.Id, j => j.State == "completed", TimeSpan.FromSeconds(30));
        Assert.Equal(Sha256Of(seed, size), await Hashing.Sha256FileAsync(completed.FinalFilePath));
        Assert.Equal("remote://torbox/torrents/77/1", completed.Url);
        Assert.Null(completed.FinalUrl);
        Assert.Equal(1, broker.PrepareCalls);
        await AssertSecretNotOnDiskAsync(ctx, LinkSecret);
        Assert.DoesNotContain(LinkSecret, System.Text.Json.JsonSerializer.Serialize(ctx.Service.GetSnapshot()));
        Assert.Empty(ctx.Service.GetSnapshot().Settings.RecentUrls);
    }

    [Fact]
    public async Task DeadLinksAreReplacedAtMostTwiceAndKeepProgress()
    {
        const string name = "remote-refresh.bin";
        const int size = 8 * 1024 * 1024;
        const int seed = 702;
        _server.ResetExpiringStats();
        _server.SetVersion(name, 1);
        var serving = $"{_server.BaseAddress}/expiring/{name}?size={size}&seed={seed}";
        var calls = 0;
        // The first run gets a good link. The next run's first two are already dead, and its third works.
        var broker = new ScriptedBroker(_ => RemoteLinkResolution.Link(++calls switch
        {
            1 => $"{serving}&token=1&bps=524288",
            2 or 3 => $"{serving}&token=1",
            _ => $"{serving}&token=2",
        }));
        await using var ctx = await ServiceContext.CreateAsync(_server, linkBroker: broker);
        var settings = ctx.Service.GetSnapshot().Settings;
        settings.DefaultConnections = 4;
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);
        var result = await ctx.Service.AddRemoteJobsAsync([RemoteFile(ctx, "1", name, size: size)], "remote:job-1");
        var id = AddedJob(result).Id;

        await ctx.WaitFor(id, j => j.ReceivedBytes > 1024 * 1024, TimeSpan.FromSeconds(30));
        Assert.True((await ctx.Service.PauseAsync(id)).Ok);
        var saved = await SettledCheckpointAsync(ctx, id);
        Assert.True(saved.Segments.Count > 1, "The test needs a segmented checkpoint.");

        _server.SetVersion(name, 2);
        _server.ResetExpiringStats();
        Assert.True((await ctx.Service.ResumeAsync(id)).Ok);

        var completed = await ctx.WaitFor(id, j => j.State == "completed", TimeSpan.FromSeconds(30));
        Assert.Equal(Sha256Of(seed, size), await Hashing.Sha256FileAsync(completed.FinalFilePath));
        Assert.Equal(4, broker.PrepareCalls);
        Assert.Equal(2, broker.RefreshCalls);
        Assert.All(broker.RefreshKinds, kind => Assert.Equal(FailureKind.ExpiredUrl, kind));
        var ranges = _server.ExpiringRanges().Where(range => range != "bytes=0-0").ToList();
        Assert.NotEmpty(ranges);
        foreach (var range in ranges)
        {
            var start = long.Parse(range["bytes=".Length..].Split('-')[0]);
            Assert.DoesNotContain(saved.Segments, segment => start >= segment.Start && start < segment.Start + segment.Completed);
        }
    }

    [Fact]
    public async Task RefreshesStopAfterTwoThenTheJobFails()
    {
        const string name = "remote-always-dead.bin";
        _server.SetVersion(name, 2);
        var broker = new ScriptedBroker(_ => RemoteLinkResolution.Link($"{_server.BaseAddress}/expiring/{name}?token=1"));
        await using var ctx = await ServiceContext.CreateAsync(_server, linkBroker: broker);
        var result = await ctx.Service.AddRemoteJobsAsync([RemoteFile(ctx, "1", name)], "remote:job-1");

        var failed = await ctx.WaitFor(AddedJob(result).Id, j => j.State == "failed");
        Assert.Equal("expiredUrl", failed.Failure?.Kind);
        Assert.Equal(3, broker.PrepareCalls);
        Assert.Equal(2, broker.RefreshCalls);
    }

    [Theory]
    [InlineData("error/401", "unauthorized")]
    [InlineData("error/403", "forbidden")]
    [InlineData("error/404", "notFound")]
    [InlineData("error/410", "expiredUrl")]
    public async Task EveryDeadLinkKindAsksTheBroker(string path, string kind)
    {
        var broker = new ScriptedBroker(_ => RemoteLinkResolution.Link($"{_server.BaseAddress}/{path}")) { Refresh = false };
        await using var ctx = await ServiceContext.CreateAsync(_server, linkBroker: broker);
        var result = await ctx.Service.AddRemoteJobsAsync([RemoteFile(ctx, "1", "dead.bin")], "remote:job-1");

        var failed = await ctx.WaitFor(AddedJob(result).Id, j => j.State == "failed");
        Assert.Equal(kind, failed.Failure?.Kind);
        Assert.Equal(1, broker.RefreshCalls);
        Assert.Equal(1, broker.PrepareCalls);
    }

    [Fact]
    public async Task OtherFailuresDoNotAskForAFreshLink()
    {
        var broker = new ScriptedBroker(_ => RemoteLinkResolution.Link($"{_server.BaseAddress}/error/500"));
        await using var ctx = await ServiceContext.CreateAsync(_server, linkBroker: broker);
        var result = await ctx.Service.AddRemoteJobsAsync([RemoteFile(ctx, "1", "server-error.bin")], "remote:job-1");

        await ctx.WaitFor(AddedJob(result).Id, j => j.State == "failed");
        Assert.Equal(0, broker.RefreshCalls);
    }

    [Fact]
    public async Task BrokerFailureIsShownAndKeepsTheBytes()
    {
        var failure = new JobFailure(FailureKind.NotFound, "The item is gone from the account.", false, false, DateTimeOffset.UtcNow);
        var broker = new ScriptedBroker(_ => RemoteLinkResolution.Fail(failure));
        await using var ctx = await ServiceContext.CreateAsync(_server, linkBroker: broker);
        var result = await ctx.Service.AddRemoteJobsAsync([RemoteFile(ctx, "1", "gone.bin", size: 1000)], "remote:job-1");

        var failed = await ctx.WaitFor(AddedJob(result).Id, j => j.State == "failed");
        Assert.Equal("notFound", failed.Failure?.Kind);
        Assert.Equal("The item is gone from the account.", failed.Failure?.Message);
        Assert.Equal(1000, failed.TotalBytes);
    }

    [Fact]
    public async Task BrokerExceptionBecomesAFailureWithoutItsSecret()
    {
        var broker = new ScriptedBroker(_ => throw new InvalidOperationException($"bad call to https://api.test/x?token={LinkSecret}"));
        await using var ctx = await ServiceContext.CreateAsync(_server, linkBroker: broker);
        var result = await ctx.Service.AddRemoteJobsAsync([RemoteFile(ctx, "1", "throws.bin")], "remote:job-1");

        var failed = await ctx.WaitFor(AddedJob(result).Id, j => j.State == "failed");
        Assert.DoesNotContain(LinkSecret, failed.Failure?.Message);
    }

    [Fact]
    public async Task PausingCancelsASlowBrokerAndResumeAsksAgain()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var link = $"{_server.BaseAddress}/file/slow-broker.bin?size=1024&seed=703";
        var broker = new ScriptedBroker(async (_, cancellationToken) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return RemoteLinkResolution.Link(link);
        });
        await using var ctx = await ServiceContext.CreateAsync(_server, linkBroker: broker);
        var result = await ctx.Service.AddRemoteJobsAsync([RemoteFile(ctx, "1", "slow-broker.bin")], "remote:job-1");
        var id = AddedJob(result).Id;

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True((await ctx.Service.PauseAsync(id)).Ok);
        await ctx.WaitFor(id, j => j.State == "paused");

        release.TrySetResult();
        Assert.True((await ctx.Service.ResumeAsync(id)).Ok, "Resume was refused.");
        await ctx.WaitFor(id, j => j.State == "completed");
        Assert.Equal(2, broker.PrepareCalls);
    }

    [Fact]
    public async Task ChangedIdentityDeletesLocalDataFirst()
    {
        const string name = "remote-changed.bin";
        const int oldSize = 4 * 1024 * 1024;
        const int newSize = 2 * 1024 * 1024;
        const int seed = 704;
        var calls = 0;
        var broker = new ScriptedBroker(_ => ++calls == 1
            ? RemoteLinkResolution.Link($"{_server.BaseAddress}/file/{name}?size={oldSize}&seed={seed}&bps=524288")
            : RemoteLinkResolution.Link($"{_server.BaseAddress}/file/{name}?size={newSize}&seed={seed}", resetLocalData: true, expectedTotalBytes: newSize));
        await using var ctx = await ServiceContext.CreateAsync(_server, linkBroker: broker);
        var result = await ctx.Service.AddRemoteJobsAsync([RemoteFile(ctx, "1", name, size: oldSize)], "remote:job-1");
        var id = AddedJob(result).Id;

        await ctx.WaitFor(id, j => j.ReceivedBytes > 256 * 1024, TimeSpan.FromSeconds(30));
        Assert.True((await ctx.Service.PauseAsync(id)).Ok);
        await SettledCheckpointAsync(ctx, id);
        Assert.True((await ctx.Service.ResumeAsync(id)).Ok);

        var completed = await ctx.WaitFor(id, j => j.State == "completed", TimeSpan.FromSeconds(30));
        Assert.Equal(newSize, completed.TotalBytes);
        Assert.Equal(Sha256Of(seed, newSize), await Hashing.Sha256FileAsync(completed.FinalFilePath));
    }

    [Fact]
    public async Task ConnectionCapIsReclampedAtEachRun()
    {
        const string name = "remote-reclamp.bin";
        const int size = 8 * 1024 * 1024;
        _server.ResetFileConcurrency();
        var broker = new ScriptedBroker(_ => RemoteLinkResolution.Link(
            $"{_server.BaseAddress}/file/{name}?size={size}&seed=705&bps=8388608", maxConnections: 2));
        await using var ctx = await ServiceContext.CreateAsync(_server, linkBroker: broker);
        var settings = ctx.Service.GetSnapshot().Settings;
        settings.DefaultConnections = 8;
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);

        var result = await ctx.Service.AddRemoteJobsAsync([RemoteFile(ctx, "1", name, maxConnections: 8)], "remote:job-1");
        var id = AddedJob(result).Id;
        Assert.Equal(8, result.Items[0].Job!.RequestedConnections);

        var completed = await ctx.WaitFor(id, j => j.State == "completed", TimeSpan.FromSeconds(30));
        Assert.Equal(2, completed.RequestedConnections);
        Assert.InRange(_server.PeakConcurrentFileRequests, 1, 2);
    }

    [Fact]
    public async Task RemoteJobsAreRestoredAfterARestart()
    {
        const string name = "remote-restart.bin";
        const int size = 2 * 1024 * 1024;
        var calls = 0;
        var root = Path.Combine(AppContext.BaseDirectory, "test-work", Guid.NewGuid().ToString("N"));
        var paths = new StoragePaths(Path.Combine(root, "state"));
        var destination = Path.Combine(root, "downloads");
        Directory.CreateDirectory(destination);
        paths.EnsureCreated();
        var broker = new ScriptedBroker(_ => RemoteLinkResolution.Link(
            $"{_server.BaseAddress}/file/{name}?size={size}&seed=706{(++calls == 1 ? "&bps=262144" : "")}"));

        Guid id;
        {
            await using var first = await ServiceContext.CreateAsync(_server, paths, destination, linkBroker: broker);
            var result = await first.Service.AddRemoteJobsAsync([new RemoteJobRequest
            {
                Source = new RemoteSource("torbox", "torrents", "77", "1", name, "job-1"),
                FileName = name,
                DestinationRoot = destination,
                MaxConnections = 4,
                ExpectedTotalBytes = size,
                ExpectedMd5 = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(TestContent.Generate(706, size))),
            }], "remote:job-1");
            id = AddedJob(result).Id;
            await first.WaitFor(id, j => j.ReceivedBytes > 0);
        }

        await using var second = await ServiceContext.CreateAsync(_server, paths, destination, linkBroker: broker);
        var restored = second.Service.GetJob(id)!;
        Assert.True(restored.IsRemote);
        Assert.True((await second.Service.ResumeAsync(id)).Ok);
        var completed = await second.WaitFor(id, j => j.State == "completed", TimeSpan.FromSeconds(30));
        Assert.Equal(Sha256Of(706, size), await Hashing.Sha256FileAsync(completed.FinalFilePath));
        Assert.True(calls >= 2);

        var again = await second.Service.AddRemoteJobsAsync([new RemoteJobRequest
        {
            Source = new RemoteSource("torbox", "torrents", "77", "1", name, "job-1"),
            FileName = name,
            DestinationRoot = destination,
            MaxConnections = 4,
        }], "remote:job-1");
        Assert.True(again.Items[0].Duplicate);
        Assert.Equal(id, again.Items[0].Job!.Id);
    }

    [Fact]
    public async Task BatchAddCommitsSettingsOnceAndIsIdempotent()
    {
        var broker = new ScriptedBroker(_ => RemoteLinkResolution.Fail(
            new JobFailure(FailureKind.NotFound, "Not in this test.", false, true, DateTimeOffset.UtcNow)));
        await using var ctx = await ServiceContext.CreateAsync(_server, linkBroker: broker);
        var commits = 0;
        ctx.Service.SettingsChanged += (_, _) => Interlocked.Increment(ref commits);
        var files = Enumerable.Range(1, 5)
            .Select(i => RemoteFile(ctx, i.ToString(), $"file{i}.bin", $"Pack/file{i}.bin", 100 + i))
            .ToList();

        var first = await ctx.Service.AddRemoteJobsAsync(files, "remote:job-1", "My pack");
        Assert.Null(first.Error);
        Assert.Equal(5, first.Items.Count(item => item.Accepted && !item.Duplicate));
        Assert.Equal(1, commits);
        var package = Assert.Single(ctx.Service.GetSnapshot().Settings.DownloadGroups);
        Assert.Equal(first.PackageId, package.Id);
        Assert.Equal("My pack", package.Name);
        Assert.Equal(5, package.JobIds.Count);
        Assert.Equal(Enumerable.Range(1, 5).Select(i => 100L + i), first.Items.Select(item => item.Job!.TotalBytes!.Value));
        Assert.Empty(ctx.Service.GetSnapshot().Settings.RecentUrls);

        var retry = await ctx.Service.AddRemoteJobsAsync(files, "remote:job-1", "My pack");
        Assert.All(retry.Items, item => Assert.True(item.Duplicate));
        Assert.Equal(first.Items.Select(item => item.Job!.Id), retry.Items.Select(item => item.Job!.Id));
        Assert.Equal(1, commits);
        Assert.Equal(5, Assert.Single(ctx.Service.GetSnapshot().Settings.DownloadGroups).JobIds.Count);
        Assert.Equal(5, ctx.Service.GetSnapshot().Jobs.Count);
    }

    [Fact]
    public async Task RetryRepairsMembershipWhenTheFirstPackageSaveWasLost()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var files = new[] { RemoteFile(ctx, "1", "a.bin"), RemoteFile(ctx, "2", "b.bin") };
        var first = await ctx.Service.AddRemoteJobsAsync(files, "remote:job-1");
        var ids = first.Items.Select(item => item.Job!.Id).ToList();

        // As if the package never reached disk: the jobs and their claims exist, the package doesn't.
        var deleted = await ctx.Service.GroupActionAsync(new GroupActionRequest { Action = "delete", GroupId = first.PackageId!.Value });
        Assert.True(deleted.Ok, deleted.Error);
        Assert.Empty(ctx.Service.GetSnapshot().Settings.DownloadGroups);

        var retry = await ctx.Service.AddRemoteJobsAsync(files, "remote:job-1");
        Assert.All(retry.Items, item => Assert.True(item.Duplicate));
        Assert.Equal(ids, Assert.Single(ctx.Service.GetSnapshot().Settings.DownloadGroups).JobIds);
    }

    [Theory]
    [InlineData("../escape.bin")]
    [InlineData("a/../../escape.bin")]
    [InlineData("/rooted/escape.bin")]
    [InlineData("C:/Windows/escape.bin")]
    [InlineData("..\\escape.bin")]
    public async Task PathsThatLeaveTheRootAreRefusedAndOthersStillAdd(string relativePath)
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var result = await ctx.Service.AddRemoteJobsAsync(
            [RemoteFile(ctx, "1", "escape.bin", relativePath), RemoteFile(ctx, "2", "fine.bin", "ok/fine.bin")],
            "remote:job-1");

        Assert.False(result.Items[0].Accepted);
        Assert.Equal("invalid_path", result.Items[0].ErrorCode);
        Assert.True(result.Items[1].Accepted, result.Items[1].Error);
        Assert.StartsWith(ctx.Destination + Path.DirectorySeparatorChar, result.Items[1].Job!.FinalFilePath);
        Assert.Single(ctx.Service.GetSnapshot().Jobs);
    }

    [Fact]
    public async Task BadReferencesAreRefused()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var bad = new[]
        {
            new RemoteSource("tor/box", "torrents", "1", "2", "a.bin", "job"),
            new RemoteSource("torbox", "tor rents", "1", "2", "a.bin", "job"),
            new RemoteSource("torbox", "torrents", "", "2", "a.bin", "job"),
            new RemoteSource("torbox", "torrents", "1", "", "a.bin", "job"),
            new RemoteSource("torbox", "torrents", "1", "2", "a.bin", null),
        };
        var result = await ctx.Service.AddRemoteJobsAsync(
            bad.Select(source => new RemoteJobRequest
            {
                Source = source, FileName = "a.bin", DestinationRoot = ctx.Destination, MaxConnections = 4,
            }).ToList(),
            "remote:job-1");

        Assert.All(result.Items, item => Assert.Equal("invalid_reference", item.ErrorCode));
        Assert.Empty(ctx.Service.GetSnapshot().Jobs);
        Assert.Empty(ctx.Service.GetSnapshot().Settings.DownloadGroups);
    }

    [Fact]
    public async Task RefreshAddressIsRefusedForRemoteJobs()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var result = await ctx.Service.AddRemoteJobsAsync([RemoteFile(ctx, "1", "a.bin")], "remote:job-1");
        var failed = await ctx.WaitFor(AddedJob(result).Id, j => j.State == "failed");

        var refreshed = await ctx.Service.RefreshUrlAsync(failed.Id, $"{_server.BaseAddress}/file/a.bin");
        Assert.False(refreshed.Ok);
        Assert.Equal("remote://torbox/torrents/77/1", ctx.Service.GetJob(failed.Id)!.Url);
    }

    [Fact]
    public async Task SpeedLimitCanChangeOnRemoteJobs()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var result = await ctx.Service.AddRemoteJobsAsync([RemoteFile(ctx, "1", "a.bin")], "remote:job-1");
        var failed = await ctx.WaitFor(AddedJob(result).Id, j => j.State == "failed");
        Assert.True(failed.CanChangeSpeedLimit);
    }

    [Fact]
    public async Task SchedulerSynchronizationSkipsRemoteAndProtectedJobsWithoutAnError()
    {
        const int size = 65536;
        var name = "sync-remote.bin";
        var link = $"{_server.BaseAddress}/file/{name}?size={size}&seed=707&sig={LinkSecret}";
        var handler = new CdnRedirectHandler(_server);
        await using var ctx = await ServiceContext.CreateAsync(_server,
            linkBroker: new ScriptedBroker(_ => RemoteLinkResolution.Link(link)), handler: handler);
        var remote = AddedJob(await ctx.Service.AddRemoteJobsAsync([RemoteFile(ctx, "1", name)], "remote:job-1"));
        var pasted = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{PastedHost}/file/sync-pasted.bin?size={size}&seed=708&token={LinkSecret}",
            FileName = "sync-pasted.bin",
            DestinationDirectory = ctx.Destination,
            Source = "cli",
        });
        Assert.True(pasted.Accepted, pasted.Error);
        await ctx.WaitFor(remote.Id, j => j.State == "completed");
        await ctx.WaitFor(pasted.Job!.Id, j => j.State == "completed");

        var settings = ctx.Service.GetSnapshot().Settings;
        settings.Schedules.Add(new ScheduleDto
        {
            Id = Guid.NewGuid(), Name = "Periodic sync", QueueId = 0, Enabled = true,
            Kind = "daily", Days = "Mon,Tue,Wed,Thu,Fri,Sat,Sun", StartTime = "10:00",
            RepeatEveryMinutes = 1,
        });
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);

        var run = new DateTimeOffset(2026, 11, 4, 10, 0, 0, TimeSpan.Zero);
        Assert.True(Assert.Single(await ctx.Service.EvaluateSchedulesAsync(run)).Ok);
        var outcome = Assert.Single(await ctx.Service.EvaluateSchedulesAsync(run.AddMinutes(1)));
        Assert.True(outcome.Ok, outcome.Error);
        Assert.Equal("completed", ctx.Service.GetJob(remote.Id)!.State);
        Assert.Equal("completed", ctx.Service.GetJob(pasted.Job!.Id)!.State);
        await AssertSecretNotOnDiskAsync(ctx, LinkSecret);
    }

    [Fact]
    public async Task PastedProviderLinkIsMaskedProtectedAndCapped()
    {
        const string name = "pasted.bin";
        const int size = 2 * 1024 * 1024;
        var handler = new CdnRedirectHandler(_server);
        await using var ctx = await ServiceContext.CreateAsync(_server, handler: handler);
        var settings = ctx.Service.GetSnapshot().Settings;
        settings.DefaultConnections = 8;
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);

        var response = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{PastedHost}/file/{name}?size={size}&seed=711&token={LinkSecret}",
            PageUrl = $"{PastedHost}/file/{name}?token={LinkSecret}",
            Referrer = $"{PastedHost}/file/{name}?token={LinkSecret}",
            FileName = name,
            DestinationDirectory = ctx.Destination,
            Source = "clipboardMonitor",
        });
        Assert.True(response.Accepted, response.Error);
        var added = response.Job!;
        Assert.Contains($"token={UrlRedactor.Mask}", added.Url);
        Assert.DoesNotContain(LinkSecret, added.Url);
        Assert.DoesNotContain(LinkSecret, added.PageUrl);
        Assert.Null(added.FinalUrl);
        Assert.Equal(ProviderLinks.MaxConnections, added.RequestedConnections);
        Assert.False(added.IsRemote);

        var completed = await ctx.WaitFor(added.Id, j => j.State == "completed", TimeSpan.FromSeconds(30));
        Assert.Equal(Sha256Of(711, size), await Hashing.Sha256FileAsync(completed.FinalFilePath));
        Assert.All(handler.TokensSeen, token => Assert.Equal(LinkSecret, token));
        Assert.NotEmpty(handler.TokensSeen);

        Assert.Empty(ctx.Service.GetSnapshot().Settings.RecentUrls);
        await AssertSecretNotOnDiskAsync(ctx, LinkSecret);
        Assert.Contains("\"protectedUrl\": \"dpapi:v1:", await JobJsonAsync(ctx, added.Id));
        Assert.DoesNotContain(LinkSecret, System.Text.Json.JsonSerializer.Serialize(ctx.Service.GetSnapshot()));
    }

    [Fact]
    public async Task ProviderLinkInFinalUrlIsProtectedToo()
    {
        const string name = "pasted-final.bin";
        var handler = new CdnRedirectHandler(_server);
        await using var ctx = await ServiceContext.CreateAsync(_server, handler: handler);
        var response = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/error/403",
            FinalUrl = $"{PastedHost}/file/{name}?size=65536&seed=712&token={LinkSecret}",
            FileName = name,
            DestinationDirectory = ctx.Destination,
            Source = "chromeContextMenu",
        });
        Assert.True(response.Accepted, response.Error);
        Assert.Null(response.Job!.FinalUrl);

        var completed = await ctx.WaitFor(response.Job.Id, j => j.State == "completed");
        Assert.Equal(Sha256Of(712, 65536), await Hashing.Sha256FileAsync(completed.FinalFilePath));
        await AssertSecretNotOnDiskAsync(ctx, LinkSecret);
    }

    [Fact]
    public async Task MaskedAddressesCannotBeAdded()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var response = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{PastedHost}/file/a.bin?token={UrlRedactor.Mask}",
            FileName = "a.bin",
            DestinationDirectory = ctx.Destination,
        });
        Assert.False(response.Accepted);
        Assert.Equal("masked_url", response.ErrorCode);
    }

    [Fact]
    public async Task PastedLinkSurvivesARestartAndIsCappedAgain()
    {
        const string name = "pasted-restart.bin";
        const int size = 2 * 1024 * 1024;
        var root = Path.Combine(AppContext.BaseDirectory, "test-work", Guid.NewGuid().ToString("N"));
        var paths = new StoragePaths(Path.Combine(root, "state"));
        var destination = Path.Combine(root, "downloads");
        Directory.CreateDirectory(destination);
        paths.EnsureCreated();

        var handler = new CdnRedirectHandler(_server);
        Guid id;
        {
            await using var first = await ServiceContext.CreateAsync(_server, paths, destination, handler: handler);
            var response = await first.Service.AddJobAsync(new AddJobRequest
            {
                Url = $"{PastedHost}/file/{name}?size={size}&seed=713&bps=524288&token={LinkSecret}",
                FileName = name,
                DestinationDirectory = destination,
                Source = "cli",
                Connections = 4,
            });
            Assert.True(response.Accepted, response.Error);
            id = response.Job!.Id;
            await first.WaitFor(id, j => j.ReceivedBytes > 0);
        }

        await using var second = await ServiceContext.CreateAsync(_server, paths, destination, handler: handler);
        Assert.True((await second.Service.ResumeAsync(id)).Ok);
        var completed = await second.WaitFor(id, j => j.State == "completed", TimeSpan.FromSeconds(30));
        Assert.Equal(Sha256Of(713, size), await Hashing.Sha256FileAsync(completed.FinalFilePath));
        Assert.All(handler.TokensSeen, token => Assert.Equal(LinkSecret, token));
        await AssertSecretNotOnDiskAsync(second, LinkSecret);
    }

    [Fact]
    public async Task UnreadableSavedAddressFailsClearlyAndKeepsTheCiphertext()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "test-work", Guid.NewGuid().ToString("N"));
        var paths = new StoragePaths(Path.Combine(root, "state"));
        var destination = Path.Combine(root, "downloads");
        Directory.CreateDirectory(destination);
        paths.EnsureCreated();
        const string unreadable = "dpapi:v1:AAAA";
        var job = new DownloadJob
        {
            Url = $"{PastedHost}/file/a.bin?token={UrlRedactor.Mask}",
            ProtectedUrl = unreadable,
            FileName = "a.bin",
            DestinationDirectory = destination,
            FinalFilePath = Path.Combine(destination, "a.bin"),
            PartFilePath = Path.Combine(destination, "a.bin.part"),
            State = DownloadState.Queued,
        };
        await new JsonJobStore(paths).SaveAsync(job);

        await using var ctx = await ServiceContext.CreateAsync(_server, paths, destination);
        var failed = await ctx.WaitFor(job.Id, j => j.State == "failed");
        Assert.Contains("could not be read", failed.Failure?.Message);
        Assert.Contains(unreadable, await JobJsonAsync(ctx, job.Id));
    }

    [Fact]
    public async Task RefreshHandlesPastedLinks()
    {
        const string name = "pasted-refresh.bin";
        const int size = 65536;
        const string secondSecret = "another-fake-key-99c1";
        var handler = new CdnRedirectHandler(_server) { RefuseToken = LinkSecret };
        await using var ctx = await ServiceContext.CreateAsync(_server, handler: handler);
        var response = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{PastedHost}/file/{name}?size={size}&seed=714&token={LinkSecret}",
            FileName = name,
            DestinationDirectory = ctx.Destination,
            Source = "cli",
        });
        Assert.True(response.Accepted, response.Error);
        var id = response.Job!.Id;
        var failed = await ctx.WaitFor(id, j => j.State == "failed");

        // The dialog starts with the masked address; sending it back changes nothing, and fails the same way.
        var same = await ctx.Service.RefreshUrlAsync(id, failed.Url);
        Assert.True(same.Ok, same.Error);
        await ctx.WaitFor(id, j => j.State == "failed");
        Assert.Contains("dpapi:v1:", await JobJsonAsync(ctx, id));

        var otherMasked = await ctx.Service.RefreshUrlAsync(id, $"{PastedHost}/other.bin?token={UrlRedactor.Mask}");
        Assert.False(otherMasked.Ok);

        // A new pasted link replaces the secret.
        var fresh = await ctx.Service.RefreshUrlAsync(id, $"{PastedHost}/file/{name}?size={size}&seed=714&token={secondSecret}");
        Assert.True(fresh.Ok, fresh.Error);
        Assert.DoesNotContain(secondSecret, fresh.Job!.Url);
        var completed = await ctx.WaitFor(id, j => j.State == "completed");
        Assert.Equal(Sha256Of(714, size), await Hashing.Sha256FileAsync(completed.FinalFilePath));
        await AssertSecretNotOnDiskAsync(ctx, secondSecret);
        await AssertSecretNotOnDiskAsync(ctx, LinkSecret);
    }

    [Fact]
    public async Task RefreshToAnOrdinaryLinkDropsTheProtectedAddress()
    {
        const string name = "pasted-to-plain.bin";
        const int size = 65536;
        var handler = new CdnRedirectHandler(_server) { RefuseToken = LinkSecret };
        await using var ctx = await ServiceContext.CreateAsync(_server, handler: handler);
        var response = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{PastedHost}/file/{name}?size={size}&seed=715&token={LinkSecret}",
            FileName = name,
            DestinationDirectory = ctx.Destination,
            Source = "cli",
        });
        var id = response.Job!.Id;
        await ctx.WaitFor(id, j => j.State == "failed");

        var refreshed = await ctx.Service.RefreshUrlAsync(id, $"{_server.BaseAddress}/file/{name}?size={size}&seed=715");
        Assert.True(refreshed.Ok, refreshed.Error);
        var completed = await ctx.WaitFor(id, j => j.State == "completed");
        Assert.Equal(Sha256Of(715, size), await Hashing.Sha256FileAsync(completed.FinalFilePath));
        Assert.DoesNotContain("protectedUrl", await JobJsonAsync(ctx, id));
    }

    [Fact]
    public async Task OrdinaryAddsStillRememberRecentUrls()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var url = $"{_server.BaseAddress}/file/recent.bin?size=1024&seed=716&token=harmless-elsewhere";
        var response = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = url,
            FileName = "recent.bin",
            DestinationDirectory = ctx.Destination,
            Source = "cli",
        });
        Assert.True(response.Accepted, response.Error);
        Assert.Equal(url, response.Job!.Url);
        Assert.Contains(url, ctx.Service.GetSnapshot().Settings.RecentUrls);
    }

    private sealed class ScriptedBroker(Func<DownloadJob, CancellationToken, Task<RemoteLinkResolution>> prepare) : IRemoteLinkBroker
    {
        private int _prepareCalls;
        private int _refreshCalls;

        public ScriptedBroker(Func<DownloadJob, RemoteLinkResolution> prepare)
            : this((job, _) => Task.FromResult(prepare(job)))
        {
        }

        public bool Refresh { get; init; } = true;
        public Exception? RefreshThrows { get; init; }
        public int PrepareCalls => Volatile.Read(ref _prepareCalls);
        public int RefreshCalls => Volatile.Read(ref _refreshCalls);
        public List<FailureKind> RefreshKinds { get; } = [];

        public Task<RemoteLinkResolution> PrepareRunAsync(DownloadJob job, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _prepareCalls);
            return prepare(job, cancellationToken);
        }

        public Task<bool> ShouldRefreshAfterFailureAsync(DownloadJob job, JobFailure failure, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _refreshCalls);
            lock (RefreshKinds) RefreshKinds.Add(failure.Kind);
            return RefreshThrows is null ? Task.FromResult(Refresh) : Task.FromException<bool>(RefreshThrows);
        }
    }

    /// <summary>Stands in for the CDN's DNS: sends its host to the test server and records the token it was given.</summary>
    private sealed class CdnRedirectHandler(TestServerHost server) : DelegatingHandler(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false })
    {
        private readonly Lock _sync = new();
        private readonly List<string> _tokens = [];

        public string? RefuseToken { get; init; }

        /// <summary>Requests to this host answer with a redirect to <see cref="RedirectTo"/>.</summary>
        public string? RedirectHost { get; init; }
        public string? RedirectTo { get; init; }

        public IReadOnlyList<string> TokensSeen
        {
            get
            {
                lock (_sync) return _tokens.ToList();
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (uri.Host == RedirectHost)
            {
                var redirect = new HttpResponseMessage(System.Net.HttpStatusCode.Found) { RequestMessage = request };
                redirect.Headers.Location = new Uri(RedirectTo!);
                return redirect;
            }

            if (uri.Host.Contains(".tb-cdn.", StringComparison.OrdinalIgnoreCase))
            {
                var token = System.Web.HttpUtility.ParseQueryString(uri.Query)["token"] ?? string.Empty;
                lock (_sync) _tokens.Add(token);
                if (token == RefuseToken)
                {
                    return new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden) { RequestMessage = request };
                }

                var target = new Uri(server.BaseAddress);
                request.RequestUri = new UriBuilder(uri) { Scheme = target.Scheme, Host = target.Host, Port = target.Port }.Uri;
                var answer = await base.SendAsync(request, cancellationToken);
                // To the engine, the answer came from the CDN's address.
                request.RequestUri = uri;
                return answer;
            }

            return await base.SendAsync(request, cancellationToken);
        }
    }
}
