using System.Security.Cryptography;
using DownloadManager.Contracts;
using DownloadManager.Core;
using DownloadManager.Torrents;

namespace DownloadManager.Integration.Tests;

/// <summary>T8: the torrents' upload on the job while it downloads, and in the Host's speed history while it seeds.</summary>
public sealed partial class HostCommandDispatcherTests
{
    [Fact]
    public async Task ADownloadingTorrentsUploadIsOnItsJobAndInTheUploadHistory()
    {
        var service = await TorrentServiceAsync();
        var dispatcher = Dispatcher(service);
        var hash = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(20));
        var job = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Add, new TorrentAddRequest
        {
            Input = $"magnet:?xt=urn:btih:{hash}&dn=Shared", Destination = Path.Combine(_root, "down"),
        })).GetPayload<TorrentAddResult>().Job!;
        await UntilAsync(() => _torrentWorkers.FirstOrDefault(w => w.Torrents.Count == 1));

        ((ITorrentJobSink)service).SwarmChanged(job.Id, 3, 1, 700);
        var shown = service.GetSnapshot().Jobs.Single(j => j.Id == job.Id);
        Assert.False(shown.Torrent!.Seeding);
        Assert.Equal(700, shown.Torrent.UploadRate);
        var history = service.GetSpeedHistory();
        Assert.Equal(history.BytesPerSecond.Count, history.UploadBytesPerSecond!.Count);
        Assert.Equal(700, history.UploadBytesPerSecond[^1]);

        // Paused, it sends nothing: once its run has ended, the job and the history say 0.
        await service.PauseAsync(job.Id);
        await UntilAsync(() => service.GetSnapshot().Jobs.FirstOrDefault(j => j.Id == job.Id && j.State == "paused" && j.Torrent!.UploadRate == 0));
        Assert.Equal(0, service.GetSpeedHistory().UploadBytesPerSecond![^1]);
    }

    [Fact]
    public async Task ASeedingTorrentsUploadIsInTheHistoryUntilSeedingStops()
    {
        var (service, _, job) = await CompletedTorrentAsync();
        await UntilAsync(() => service.GetSnapshot().Jobs.FirstOrDefault(j => j.Id == job.Id && j.Torrent!.Seeding));
        var worker = await UntilAsync(() => _torrentWorkers.FirstOrDefault(w => w.Torrents.Count == 1));
        worker.Upload(job.Id.ToString("N"), 10);
        await UntilAsync(() => service.GetSnapshot().Jobs.FirstOrDefault(j => j.Id == job.Id && j.Torrent!.UploadRate > 0));
        Assert.Equal(1000, service.GetSpeedHistory().UploadBytesPerSecond![^1]);

        await service.PauseAllAsync();
        await UntilAsync(() => service.GetSnapshot().Jobs.FirstOrDefault(j => j.Id == job.Id && !j.Torrent!.Seeding));
        Assert.Equal(0, service.GetSpeedHistory().UploadBytesPerSecond![^1]);
    }
}
