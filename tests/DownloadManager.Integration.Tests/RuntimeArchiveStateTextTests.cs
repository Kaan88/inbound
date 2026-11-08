using System.Text;
using DownloadManager.Contracts;

namespace DownloadManager.Integration.Tests;

/// <summary>UI.R23 (1): a set's words say what its unfinished volume downloads are doing, not "Downloading" for all.</summary>
[Collection("runtime-archive")]
public sealed class RuntimeArchiveStateTextTests(ArchiveFileServer fixture)
{
    private static readonly byte[] Bytes = Encoding.ASCII.GetBytes("not a real archive, only its name counts here");

    private readonly ArchiveFileServer _server = fixture;

    [Fact]
    public async Task ADownloadLaterVolumeIsNotStartedThenPausedAndNeverWaitingToDownload()
    {
        await using var run = await ArchiveHarness.StartAsync(_server);

        // "Later" is skipped by the scheduler, so it won't start without the user.
        var job = await run.DownloadAsync("held.part1.rar", Bytes, startMode: "later");
        var later = await run.WaitForSetAsync(set => set.HeldVolumes.Contains("held.part1.rar"));
        Assert.Equal("downloading", later.State);
        Assert.Equal("Not started: held.part1.rar", later.StateText);
        Assert.Empty(later.QueuedVolumes);
        Assert.Empty(later.PausedVolumes);

        var paused = await run.Service.PauseAsync(job.Id);
        Assert.True(paused.Ok, paused.Error);
        var held = await run.WaitForSetAsync(set => set.PausedVolumes.Contains("held.part1.rar"));
        Assert.Equal("Paused: held.part1.rar", held.StateText);
        Assert.DoesNotContain("Downloading", held.StateText);
        Assert.Empty(held.QueuedVolumes);
        Assert.Empty(held.HeldVolumes);
        Assert.Equal(["held.part1.rar"], held.DownloadingVolumes);
    }

    [Fact]
    public async Task AVolumeInAStoppedQueueIsNotStartedUntilTheQueueStarts()
    {
        await using var run = await ArchiveHarness.StartAsync(_server);
        var queueId = run.Service.GetSettings().DefaultQueueId;
        var stopped = await run.Service.QueueActionAsync(new QueueActionRequest { QueueId = queueId, Action = "stop" });
        Assert.True(stopped.Ok, stopped.Error);

        var job = await run.DownloadAsync("stopped.part1.rar", Bytes, startMode: "queue");
        var waiting = await run.WaitForSetAsync(set => set.HeldVolumes.Contains("stopped.part1.rar"));
        Assert.Equal("Not started: stopped.part1.rar", waiting.StateText);
        Assert.Empty(waiting.QueuedVolumes);

        // Starting the queue comes as a settings change; the set is counted again from it.
        var started = await run.Service.QueueActionAsync(new QueueActionRequest { QueueId = queueId, Action = "start" });
        Assert.True(started.Ok, started.Error);
        await run.WaitForJobAsync(job.Id, "completed");
        var done = await run.WaitForSetAsync(set => set.Volumes.Contains("stopped.part1.rar"));
        Assert.Empty(done.HeldVolumes);
        Assert.DoesNotContain("Not started", done.StateText);
    }

    [Fact]
    public async Task ASetSavedWithoutTheNewListsIsCountedAgainAtTheNextStart()
    {
        await using var run = await ArchiveHarness.StartAsync(_server);
        var job = await run.DownloadAsync("old.part1.rar", Bytes, startMode: "later");
        await run.WaitForSetAsync(set => set.HeldVolumes.Contains("old.part1.rar"));
        var paused = await run.Service.PauseAsync(job.Id);
        Assert.True(paused.Ok, paused.Error);
        await run.WaitForSetAsync(set => set.PausedVolumes.Contains("old.part1.rar"));
        await run.Archives.ShutdownAsync();

        // What 0.6.1 wrote: the same set with no PausedVolumes, QueuedVolumes or HeldVolumes.
        var file = Directory.EnumerateFiles(Path.Combine(run.Paths.Root, "archive", "sets"), "*.json").Single();
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        Assert.True(json.Remove("pausedVolumes"));
        json.Remove("queuedVolumes");
        json.Remove("heldVolumes");
        await File.WriteAllTextAsync(file, json.ToJsonString());

        await run.StartCoordinatorAsync();
        var recounted = await run.WaitForSetAsync(set => set.PausedVolumes.Contains("old.part1.rar"));
        Assert.Equal("Paused: old.part1.rar", recounted.StateText);
    }

    /// <summary>UI.R23 (6): a package with a password and no archive left still says so, so its window can clear it.</summary>
    [Fact]
    public async Task APackagePasswordWithNoArchiveIsListedUntilCleared()
    {
        await using var run = await ArchiveHarness.StartAsync(_server);
        var job = await run.DownloadAsync("movie.mkv", Bytes, packageKey: "pkg");
        await run.DownloadAsync("movie.srt", Bytes, packageKey: "pkg");
        Guid? packageId = null;
        for (var deadline = DateTimeOffset.UtcNow.AddSeconds(20); packageId is null && DateTimeOffset.UtcNow < deadline; await Task.Delay(20))
            packageId = run.Service.FindPackageOf(job.Id);
        Assert.NotNull(packageId);

        var saved = await run.Archives.SetPasswordAsync(new ArchiveSetPasswordRequest { PackageId = packageId, Password = "test-only password" });
        Assert.True(saved.Ok, saved.Error);
        Assert.Empty(run.Archives.List());
        Assert.Equal([packageId.Value], run.Archives.PackagesWithPassword());

        var cleared = await run.Archives.SetPasswordAsync(new ArchiveSetPasswordRequest { PackageId = packageId, Password = string.Empty });
        Assert.True(cleared.Ok, cleared.Error);
        Assert.Empty(run.Archives.PackagesWithPassword());
    }
}
