using DownloadManager.Contracts;

namespace DownloadManager.Integration.Tests;

public sealed partial class RuntimeServiceTests
{
    [Fact]
    public async Task GroupsPersistMembershipTransferDetachAndDeleteWithoutRemovingJobs()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server, autoResume: false);
        var first = await AddDownload("group-first.bin", startNow: true);
        first = await ctx.WaitFor(first.Id, job => job.State == "completed");
        var second = await AddLater("group-second.bin");
        var third = await AddLater("group-third.bin");

        var staleOptions = ctx.Service.GetSnapshot().Settings;
        var created = await ctx.Service.GroupActionAsync(new GroupActionRequest
        {
            Action = "create", Name = "Batch one", JobIds = [first.Id],
        });
        Assert.True(created.Ok, created.Error);
        var firstGroupId = created.GroupId;

        var added = await ctx.Service.GroupActionAsync(new GroupActionRequest
        {
            Action = "add", GroupId = firstGroupId, JobIds = [second.Id],
        });
        Assert.True(added.Ok, added.Error);

        var secondGroup = await ctx.Service.GroupActionAsync(new GroupActionRequest
        {
            Action = "create", Name = "Batch two", JobIds = [third.Id],
        });
        Assert.True(secondGroup.Ok, secondGroup.Error);
        var secondGroupId = secondGroup.GroupId;

        var transferred = await ctx.Service.GroupActionAsync(new GroupActionRequest
        {
            Action = "add", GroupId = secondGroupId, JobIds = [second.Id],
        });
        Assert.True(transferred.Ok, transferred.Error);
        Assert.DoesNotContain(second.Id, Assert.Single(
            ctx.Service.GetSnapshot().Settings.DownloadGroups, group => group.Id == firstGroupId).JobIds);
        Assert.Contains(second.Id, Assert.Single(
            ctx.Service.GetSnapshot().Settings.DownloadGroups, group => group.Id == secondGroupId).JobIds);

        var badIds = await ctx.Service.GroupActionAsync(new GroupActionRequest
        {
            Action = "add", GroupId = firstGroupId, JobIds = [first.Id, Guid.NewGuid()],
        });
        Assert.False(badIds.Ok);
        Assert.Equal(new[] { first.Id }, Assert.Single(
            badIds.Groups, group => group.Id == firstGroupId).JobIds);

        var staleSave = await ctx.Service.UpdateSettingsAsync(staleOptions);
        Assert.True(staleSave.Ok, staleSave.Error);
        var persistedBeforeRestart = ctx.Service.GetSnapshot().Settings.DownloadGroups;
        Assert.Equal(new[] { first.Id }, Assert.Single(
            persistedBeforeRestart, group => group.Id == firstGroupId).JobIds);
        Assert.Equal(new[] { third.Id, second.Id }, Assert.Single(
            persistedBeforeRestart, group => group.Id == secondGroupId).JobIds);

        var detached = await ctx.Service.GroupActionAsync(new GroupActionRequest
        {
            Action = "detach", GroupId = secondGroupId, JobIds = [second.Id],
        });
        Assert.True(detached.Ok, detached.Error);
        // Detaching left one member, so the package dissolved; its jobs remain.
        Assert.DoesNotContain(ctx.Service.GetSnapshot().Settings.DownloadGroups,
            group => group.Id == secondGroupId);
        Assert.NotNull(ctx.Service.GetJob(first.Id));
        Assert.NotNull(ctx.Service.GetJob(second.Id));
        Assert.NotNull(ctx.Service.GetJob(third.Id));
        Assert.True(File.Exists(first.FinalFilePath));

        await ctx.Service.ShutdownAsync();
        await using var restarted = await ServiceContext.CreateAsync(_server, ctx.Paths, ctx.Destination,
            autoResume: false);
        var reloadedGroups = restarted.Service.GetSnapshot().Settings.DownloadGroups;
        Assert.Equal(new[] { first.Id }, Assert.Single(reloadedGroups, group => group.Id == firstGroupId).JobIds);
        Assert.NotNull(restarted.Service.GetJob(first.Id));
        Assert.NotNull(restarted.Service.GetJob(second.Id));
        Assert.NotNull(restarted.Service.GetJob(third.Id));
        Assert.True(File.Exists(first.FinalFilePath));
        Assert.Equal(first.ReceivedBytes, restarted.Service.GetJob(first.Id)!.ReceivedBytes);
        Assert.Equal(first.ReceivedBytes, restarted.Service.GetStatistics().CompletedBytes);

        async Task<JobDto> AddLater(string fileName)
            => await AddDownload(fileName, startNow: false);

        async Task<JobDto> AddDownload(string fileName, bool startNow)
        {
            var response = await ctx.Service.AddJobAsync(new AddJobRequest
            {
                Url = $"{_server.BaseAddress}/file/{fileName}?size=4096&seed=41",
                DestinationDirectory = ctx.Destination,
                FileName = fileName,
                Source = "cli",
                StartMode = startNow ? "startNow" : "later",
            });
            Assert.True(response.Accepted, response.Error);
            return response.Job!;
        }
    }

    [Fact]
    public async Task RemovingCompletedDownloadArchivesStatisticsAcrossRestartWithoutDoubleCounting()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server, autoResume: false);
        const int size = 96_000;
        var response = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/file/statistics.bin?size={size}&seed=113",
            DestinationDirectory = ctx.Destination,
            FileName = "statistics.bin",
            Source = "cli",
        });
        Assert.True(response.Accepted, response.Error);
        var completed = await ctx.WaitFor(response.Job!.Id, job => job.State == "completed");

        var beforeRemoval = ctx.Service.GetStatistics();
        Assert.Equal(size, beforeRemoval.RecordedBytes);
        Assert.Equal(size, beforeRemoval.CompletedBytes);
        Assert.Equal(1, beforeRemoval.CompletedCount);
        Assert.Equal(1, beforeRemoval.RetainedCount);
        Assert.Equal(0, beforeRemoval.ArchivedCount);

        var removal = await ctx.Service.RemoveAsync(completed.Id, deleteFile: false);
        Assert.True(removal.Ok, removal.Error);
        Assert.Null(ctx.Service.GetJob(completed.Id));
        Assert.True(File.Exists(completed.FinalFilePath));

        var afterRemoval = ctx.Service.GetStatistics();
        Assert.Equal(size, afterRemoval.RecordedBytes);
        Assert.Equal(size, afterRemoval.CompletedBytes);
        Assert.Equal(1, afterRemoval.CompletedCount);
        Assert.Equal(0, afterRemoval.RetainedCount);
        Assert.Equal(1, afterRemoval.ArchivedCount);

        await ctx.Service.ShutdownAsync();
        await using var restarted = await ServiceContext.CreateAsync(_server, ctx.Paths, ctx.Destination,
            autoResume: false);
        var afterRestart = restarted.Service.GetStatistics();
        Assert.Equal(size, afterRestart.RecordedBytes);
        Assert.Equal(size, afterRestart.CompletedBytes);
        Assert.Equal(1, afterRestart.CompletedCount);
        Assert.Equal(0, afterRestart.RetainedCount);
        Assert.Equal(1, afterRestart.ArchivedCount);
    }
}

public sealed partial class RuntimeServiceTests
{
    [Fact]
    public async Task SplitArchivePartsAndBatchesBecomePackagesAndSurviveRestart()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server, autoResume: false);
        var single = await Add("movie.part1.rar");
        Assert.DoesNotContain(ctx.Service.GetSnapshot().Settings.DownloadGroups, g => g.JobIds.Contains(single.Id));
        var second = await Add("movie.part2.rar");
        var unrelated = await Add("other.part1.rar");
        // Windows names ignore case, so a part written Movie belongs to the set there; Linux would keep it apart.
        var third = await Add(OperatingSystem.IsWindows() ? "Movie.part3.rar" : "movie.part3.rar");

        var package = Assert.Single(ctx.Service.GetSnapshot().Settings.DownloadGroups, g => g.JobIds.Contains(single.Id));
        Assert.Equal("movie", package.Name);
        Assert.Equal(new[] { single.Id, second.Id, third.Id }, package.JobIds);
        Assert.DoesNotContain(ctx.Service.GetSnapshot().Settings.DownloadGroups, g => g.JobIds.Contains(unrelated.Id));

        var a = await Add("readme.txt", packageKey: "paste-1", packageName: "Pasted links");
        var b = await Add("notes.pdf", packageKey: "paste-1", packageName: "ignored after first");
        var batch = Assert.Single(ctx.Service.GetSnapshot().Settings.DownloadGroups, g => g.JobIds.Contains(a.Id));
        Assert.Equal("Pasted links", batch.Name);
        Assert.Equal(new[] { a.Id, b.Id }, batch.JobIds);

        var removed = await ctx.Service.RemoveAsync(b.Id, deleteFile: false);
        Assert.True(removed.Ok, removed.Error);
        Assert.DoesNotContain(ctx.Service.GetSnapshot().Settings.DownloadGroups, g => g.Id == batch.Id);

        await ctx.Service.ShutdownAsync();
        await using var restarted = await ServiceContext.CreateAsync(_server, ctx.Paths, ctx.Destination, autoResume: false);
        var reloaded = Assert.Single(restarted.Service.GetSnapshot().Settings.DownloadGroups, g => g.Id == package.Id);
        Assert.Equal(package.JobIds, reloaded.JobIds);

        async Task<JobDto> Add(string fileName, string? packageKey = null, string? packageName = null)
        {
            var response = await ctx.Service.AddJobAsync(new AddJobRequest
            {
                Url = $"{_server.BaseAddress}/file/{fileName}?size=2048&seed=7",
                DestinationDirectory = ctx.Destination,
                FileName = fileName,
                Source = "cli",
                StartMode = "later",
                PackageKey = packageKey,
                PackageName = packageName,
            });
            Assert.True(response.Accepted, response.Error);
            return response.Job!;
        }
    }

    [Fact]
    public async Task UnnamedBatchPackageFollowsMemberFileNames()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        foreach (var name in new[] { "data1.doi", "data2.doi", "Setup.exe" })
        {
            var added = await ctx.Service.AddJobAsync(new AddJobRequest
            {
                Url = $"{_server.BaseAddress}/file/{name}?size=2048&seed=11", DestinationDirectory = ctx.Destination,
                FileName = name, Source = "batch", StartMode = "later", PackageKey = "auto-name",
            });
            Assert.True(added.Accepted, added.Error);
        }
        var package = Assert.Single(ctx.Service.GetSnapshot().Settings.DownloadGroups, g => g.Key == "batch:auto-name");
        Assert.Equal("data", package.Name);

        var renamed = await ctx.Service.GroupActionAsync(new GroupActionRequest { Action = "rename", GroupId = package.Id, Name = "Game" });
        Assert.True(renamed.Ok, renamed.Error);
        var more = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/file/data3.doi?size=2048&seed=12", DestinationDirectory = ctx.Destination,
            FileName = "data3.doi", Source = "batch", StartMode = "later", PackageKey = "auto-name",
        });
        Assert.True(more.Accepted, more.Error);
        Assert.Equal("Game", Assert.Single(ctx.Service.GetSnapshot().Settings.DownloadGroups, g => g.Key == "batch:auto-name").Name);
    }

    [Fact]
    public async Task RemovingSeveralRunningDownloadsStopsThemTogetherAndStartsNoneOfThem()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var ids = new List<Guid>();
        for (var i = 0; i < 4; i++)
        {
            var added = await ctx.Service.AddJobAsync(new AddJobRequest
            {
                Url = $"{_server.BaseAddress}/slow/remove-many-{i}.bin?size=8388608&seed={20 + i}&bps=131072",
                DestinationDirectory = ctx.Destination, Source = "cli", PackageKey = "remove-many",
            });
            Assert.True(added.Accepted, added.Error);
            ids.Add(added.Job!.Id);
        }
        await ctx.WaitFor(ids[0], job => job.State == "downloading" && job.ReceivedBytes > 0);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var removed = await ctx.Service.RemoveManyAsync(ids, deleteFile: true);
        Assert.True(removed.Ok, removed.Error);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"Removal took {stopwatch.Elapsed}.");
        Assert.DoesNotContain(ctx.Service.GetSnapshot().Jobs, job => ids.Contains(job.Id));
        Assert.DoesNotContain(ctx.Service.GetSnapshot().Settings.DownloadGroups, g => g.Key == "batch:remove-many");
        Assert.Empty(Directory.GetFiles(ctx.Destination, "remove-many-*", SearchOption.AllDirectories));
    }
}
