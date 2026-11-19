using DownloadManager.Contracts;
using DownloadManager.Runtime;

namespace DownloadManager.Integration.Tests;

public sealed partial class HostCommandDispatcherTests
{
    [Fact]
    public async Task TorrentClassificationFinishesBeforeCompletedSnapshotIsPublished()
    {
        var service = await TorrentServiceAsync();
        var (bytes, _) = SingleFileTorrent("ubuntu.iso", 40_000);
        var server = await TorrentFileServerAsync(bytes);
        var entered = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource<JobDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.JobChanged += (_, e) =>
        {
            if (e.ChangeType is "completed" or "failed") finished.TrySetResult(e.Job);
        };
        service.TorrentClassificationForTesting = async path =>
        {
            entered.TrySetResult(path);
            await release.Task.ConfigureAwait(false);
            return TorrentFileRule.IsTorrentFile(path);
        };

        try
        {
            var folder = Path.Combine(_root, "torrent-completion-snapshot");
            var added = await service.AddJobAsync(new AddJobRequest
            {
                Url = $"{server.BaseAddress}/auth/torrent/get",
                DestinationDirectory = folder,
                FileName = "ubuntu.torrent",
                ContentType = TorrentFileRule.ContentType,
                Source = "chromeAuto",
                StartMode = "startNow",
                QuietWindows = true,
                Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Cookie"] = Cookie },
            });
            Assert.True(added.Accepted, added.Error);
            var id = added.Job!.Id;
            var path = await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(Path.Combine(folder, "ubuntu.torrent"), path);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));

            var held = service.GetJob(id);
            Assert.NotNull(held);
            Assert.NotEqual("completed", held!.State);
            Assert.False(held.TorrentFileReady);
            var snapshot = Assert.Single(service.GetSnapshot().Jobs, j => j.Id == id);
            Assert.NotEqual("completed", snapshot.State);
            Assert.False(snapshot.TorrentFileReady);

            release.TrySetResult();
            var done = await finished.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal("completed", done.State);
            Assert.True(done.TorrentFileReady);
            Assert.True(done.QuietWindows);
            Assert.Equal(path, done.FinalFilePath);
            Assert.Equal("ubuntu.torrent", done.FileName);
            Assert.Equal(TorrentFileRule.ContentType, done.ContentType);
            Assert.Equal(TorrentFileRule.ContentType, done.HandoffContentType);
            var published = service.GetJob(id);
            Assert.NotNull(published);
            Assert.Equal("completed", published!.State);
            Assert.True(published.TorrentFileReady);
            Assert.Equal(path, published.FinalFilePath);
        }
        finally
        {
            // A failed assertion must not strand the Host worker in classification during fixture shutdown.
            release.TrySetResult();
        }
    }
}
