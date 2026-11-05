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
        var deleted = await ctx.Service.GroupActionAsync(new GroupActionRequest
        {
            Action = "delete", GroupId = secondGroupId,
        });
        Assert.True(deleted.Ok, deleted.Error);
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
