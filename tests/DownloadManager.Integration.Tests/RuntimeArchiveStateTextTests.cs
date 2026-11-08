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
    public async Task AWaitingThenPausedVolumeIsNamedAsSuchAndNotAsDownloading()
    {
        await using var run = await ArchiveHarness.StartAsync(_server);

        var job = await run.DownloadAsync("held.part1.rar", Bytes, startMode: "later");
        var waiting = await run.WaitForSetAsync(set => set.QueuedVolumes.Contains("held.part1.rar"));
        Assert.Equal("downloading", waiting.State);
        Assert.Equal("Waiting for download: held.part1.rar", waiting.StateText);
        Assert.Empty(waiting.PausedVolumes);

        var paused = await run.Service.PauseAsync(job.Id);
        Assert.True(paused.Ok, paused.Error);
        var held = await run.WaitForSetAsync(set => set.PausedVolumes.Contains("held.part1.rar"));
        Assert.Equal("Paused: held.part1.rar", held.StateText);
        Assert.DoesNotContain("Downloading", held.StateText);
        Assert.Empty(held.QueuedVolumes);
        Assert.Equal(["held.part1.rar"], held.DownloadingVolumes);
    }
}
