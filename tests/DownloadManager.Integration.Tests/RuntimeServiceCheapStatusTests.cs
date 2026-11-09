using DownloadManager.Contracts;

namespace DownloadManager.Integration.Tests;

public sealed partial class RuntimeServiceTests
{
    /// <summary>
    /// PERF.2: the tray asks for the active count at most once a second while downloads run. Through
    /// GetSnapshot that copied and sorted every job and read both browsers' registry keys and the manifest each
    /// time (about 2.5 MB a second with 5,000 jobs). The narrow calls give the same answers without either.
    /// </summary>
    [Fact]
    public async Task TheTraysCountAndTheSettingsNeedNoSnapshotOrRegistryRead()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server, autoResume: false);
        for (var i = 0; i < 3; i++)
        {
            var added = await ctx.Service.AddJobAsync(new AddJobRequest
            {
                Url = $"{_server.BaseAddress}/slow/cheap-status-{i}.bin?size=4194304&bps=65536",
                DestinationDirectory = ctx.Destination,
                StartMode = i == 0 ? "startNow" : "later",
            });
            Assert.True(added.Accepted, added.Error);
        }

        await WaitUntilAsync(() => ctx.Service.GetSnapshot().Jobs.Any(job => job.State is "connecting" or "downloading"));
        var reads = 0;
        ctx.Service.NativeHostState = () =>
        {
            Interlocked.Increment(ref reads);
            return new NativeHostRegistrationState(NativeHostKeyState.Usable, NativeHostKeyState.Absent);
        };

        var count = ctx.Service.ActiveDownloadCount();
        var settings = ctx.Service.GetSettings();
        Assert.Equal(0, Volatile.Read(ref reads));

        var snapshot = ctx.Service.GetSnapshot();
        Assert.Equal(snapshot.Status.ActiveDownloads, count);
        Assert.Equal(1, count);
        Assert.Equal(snapshot.Settings.DefaultDownloadDirectory, settings.DefaultDownloadDirectory);
        Assert.Equal(snapshot.Settings.MaxConcurrentDownloads, settings.MaxConcurrentDownloads);

        var status = ctx.Service.GetStatus();
        Assert.Equal(snapshot.Status.TotalJobs, status.TotalJobs);
        Assert.Equal(snapshot.Status.QueuedDownloads, status.QueuedDownloads);
        Assert.Equal("on", status.BrowserIntegration);

        await ctx.Service.PauseAllAsync();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the download to start.");
            await Task.Delay(50);
        }
    }
}
