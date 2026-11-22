using System.Security.Cryptography;
using DownloadManager.Contracts;
using DownloadManager.Runtime;

namespace DownloadManager.Integration.Tests;

/// <summary>ANDROID.9: torrents only on Wi-Fi, and seeding only on Wi-Fi while charging, as the phone reports them.</summary>
public sealed partial class HostCommandDispatcherTests
{
    private bool SeedsOnAWorker() { lock (_torrentWorkers) return _torrentWorkers.Any(w => !w.Exited.IsCompleted && w.Torrents.Count == 1); }

    [Fact]
    public async Task OnAMeteredNetworkATorrentWaitsForWifiAndARunningOneGoesBackToItsQueue()
    {
        // The downloads' own metered rule is off, so only the torrent rule holds.
        var service = await TorrentServiceAsync(settings: s => s.DownloadOnlyOnUnmeteredNetwork = false);
        await service.SetNetworkMeteredAsync(true);
        var dispatcher = Dispatcher(service);
        var hash = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(20));
        var job = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Add, new TorrentAddRequest
        {
            Input = $"magnet:?xt=urn:btih:{hash}&dn=Waits", Destination = Path.Combine(_root, "down"),
        })).GetPayload<TorrentAddResult>().Job!;
        var waiting = await UntilAsync(() => service.GetSnapshot().Jobs.FirstOrDefault(j => j.Id == job.Id && j.Torrent!.WaitingFor is not null));
        Assert.Equal(("queued", DownloadManagerService.TorrentWaitingForWifi), (waiting.State, waiting.Torrent!.WaitingFor));
        await Task.Delay(200);
        lock (_torrentWorkers) Assert.DoesNotContain(_torrentWorkers, w => w.Torrents.Count > 0);

        await service.SetNetworkMeteredAsync(false);
        var running = await UntilAsync(() => service.GetSnapshot().Jobs.FirstOrDefault(j => j.Id == job.Id && j.State is "connecting" or "downloading"));
        Assert.Null(running.Torrent!.WaitingFor);

        await service.SetNetworkMeteredAsync(true);
        var back = await UntilAsync(() => service.GetSnapshot().Jobs.FirstOrDefault(j => j.Id == job.Id && j.State == "queued" && j.Torrent!.WaitingFor is not null));
        Assert.Equal(DownloadManagerService.TorrentWaitingForWifi, back.Torrent!.WaitingFor);

        // With the rule off, a metered network doesn't hold it.
        var settings = service.GetSettings();
        settings.TorrentOnlyOnUnmeteredNetwork = false;
        Assert.True((await service.UpdateSettingsAsync(settings)).Ok);
        var free = await UntilAsync(() => service.GetSnapshot().Jobs.FirstOrDefault(j => j.Id == job.Id && j.State is "connecting" or "downloading"));
        Assert.Null(free.Torrent!.WaitingFor);
    }

    [Fact]
    public async Task ATorrentThatHasDataAlsoGoesBackToItsQueueWhenWifiIsLost()
    {
        // Fable A9-1: only the HTTP engine says whether a download can resume, so a torrent with any bytes used to keep
        // running on mobile data. A torrent resumes from its pieces.
        var service = await TorrentServiceAsync(settings: s => s.DownloadOnlyOnUnmeteredNetwork = false);
        await service.SetNetworkMeteredAsync(false);
        var dispatcher = Dispatcher(service);
        var hash = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(20));
        var job = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Add, new TorrentAddRequest
        {
            Input = $"magnet:?xt=urn:btih:{hash}&dn=Partway", Destination = Path.Combine(_root, "down"),
        })).GetPayload<TorrentAddResult>().Job!;
        var worker = await UntilAsync(() => { lock (_torrentWorkers) return _torrentWorkers.FirstOrDefault(w => w.Torrents.Count == 1); });
        worker.Advance();
        var partway = await UntilAsync(() => service.GetSnapshot().Jobs.FirstOrDefault(j => j.Id == job.Id && j.ReceivedBytes > 0));
        Assert.Null(partway.Torrent!.WaitingFor);

        await service.SetNetworkMeteredAsync(true);
        var back = await UntilAsync(() => service.GetSnapshot().Jobs.FirstOrDefault(j => j.Id == job.Id && j.State == "queued" && j.Torrent!.WaitingFor is not null));
        Assert.Equal(DownloadManagerService.TorrentWaitingForWifi, back.Torrent!.WaitingFor);
        Assert.True(back.ReceivedBytes > 0);
    }

    [Fact]
    public async Task SeedingWaitsForChargingAndWifiStaysMarkedAndGoesOnByItself()
    {
        var (service, dispatcher, job) = await CompletedTorrentAsync();
        await UntilAsync(() => SeedsOnAWorker() ? job : null);

        await service.SetQueuePowerStateAsync(charging: false, batterySaver: false);
        var held = await UntilAsync(() => service.GetSnapshot().Jobs.FirstOrDefault(j => j.Id == job.Id && j.Torrent!.WaitingFor is not null));
        Assert.True(held.Torrent!.Seeding);
        Assert.Equal(DownloadManagerService.TorrentWaitingForCharging, held.Torrent.WaitingFor);
        await UntilAsync(() => SeedsOnAWorker() ? null : job);

        await service.SetQueuePowerStateAsync(charging: true, batterySaver: false);
        await UntilAsync(() => SeedsOnAWorker() ? job : null);
        var again = await UntilAsync(() => service.GetSnapshot().Jobs.FirstOrDefault(j => j.Id == job.Id && j.Torrent!.WaitingFor is null));
        Assert.True(again.Torrent!.Seeding);

        await service.SetNetworkMeteredAsync(true);
        var wifi = await UntilAsync(() => service.GetSnapshot().Jobs.FirstOrDefault(j => j.Id == job.Id && j.Torrent!.WaitingFor is not null));
        Assert.Equal(DownloadManagerService.TorrentWaitingForWifi, wifi.Torrent!.WaitingFor);
        Assert.True(wifi.Torrent.Seeding);
        await UntilAsync(() => SeedsOnAWorker() ? null : job);

        // A stop while held ends the seeding and the hold: Wi-Fi doesn't start it again.
        var off = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Seed, new TorrentSeedRequest { JobId = job.Id, On = false })).GetPayload<TorrentActionResult>();
        Assert.True(off.Ok, off.Error);
        var stopped = await UntilAsync(() => service.GetSnapshot().Jobs.FirstOrDefault(j => j.Id == job.Id && !j.Torrent!.Seeding));
        Assert.Null(stopped.Torrent!.WaitingFor);
        await service.SetNetworkMeteredAsync(false);
        await Task.Delay(300);
        Assert.False(SeedsOnAWorker());
        Assert.False(service.GetSnapshot().Jobs.Single(j => j.Id == job.Id).Torrent!.Seeding);

        // Started while the rule holds, it is marked seeding and waits.
        await service.SetNetworkMeteredAsync(true);
        var on = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Seed, new TorrentSeedRequest { JobId = job.Id, On = true })).GetPayload<TorrentActionResult>();
        Assert.True(on.Ok, on.Error);
        var waits = await UntilAsync(() => service.GetSnapshot().Jobs.FirstOrDefault(j => j.Id == job.Id && j.Torrent!.Seeding));
        Assert.Equal(DownloadManagerService.TorrentWaitingForWifi, waits.Torrent!.WaitingFor);
        await Task.Delay(200);
        Assert.False(SeedsOnAWorker());
        await service.SetNetworkMeteredAsync(false);
        await UntilAsync(() => SeedsOnAWorker() ? job : null);
    }

    [Fact]
    public async Task OnUnmeteredMobileDataATorrentStillWaitsForWifiAndSeedingIsHeld()
    {
        // Review A9-15 (2026-11-22): an unmetered 5G plan is not metered, but "Peers never use mobile data".
        var service = await TorrentServiceAsync(settings: s => s.DownloadOnlyOnUnmeteredNetwork = false);
        await service.SetNetworkMeteredAsync(false);
        Assert.True(await service.SetNetworkCellularAsync(true));
        Assert.False(await service.SetNetworkCellularAsync(true));
        var dispatcher = Dispatcher(service);
        var hash = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(20));
        var job = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Add, new TorrentAddRequest
        {
            Input = $"magnet:?xt=urn:btih:{hash}&dn=OnMobileData", Destination = Path.Combine(_root, "down"),
        })).GetPayload<TorrentAddResult>().Job!;
        var waiting = await UntilAsync(() => service.GetSnapshot().Jobs.FirstOrDefault(j => j.Id == job.Id && j.Torrent!.WaitingFor is not null));
        Assert.Equal(("queued", DownloadManagerService.TorrentWaitingForWifi), (waiting.State, waiting.Torrent!.WaitingFor));
        await Task.Delay(200);
        lock (_torrentWorkers) Assert.DoesNotContain(_torrentWorkers, w => w.Torrents.Count > 0);

        Assert.True(service.TorrentsWaitingForNetwork);

        // The phone sends both facts in one report (audit A9-15-1).
        await service.SetNetworkAsync(metered: false, mobileData: false);
        var running = await UntilAsync(() => service.GetSnapshot().Jobs.FirstOrDefault(j => j.Id == job.Id && j.State is "connecting" or "downloading"));
        Assert.Null(running.Torrent!.WaitingFor);
        Assert.False(service.TorrentsWaitingForNetwork);

        await service.SetNetworkCellularAsync(true);
        var back = await UntilAsync(() => service.GetSnapshot().Jobs.FirstOrDefault(j => j.Id == job.Id && j.State == "queued" && j.Torrent!.WaitingFor is not null));
        Assert.Equal(DownloadManagerService.TorrentWaitingForWifi, back.Torrent!.WaitingFor);
    }

    [Fact]
    public async Task SeedingOnUnmeteredMobileDataWaitsForWifi()
    {
        var (service, _, job) = await CompletedTorrentAsync();
        await service.SetQueuePowerStateAsync(charging: true, batterySaver: false);
        await UntilAsync(() => SeedsOnAWorker() ? job : null);

        await service.SetNetworkCellularAsync(true);
        var held = await UntilAsync(() => service.GetSnapshot().Jobs.FirstOrDefault(j => j.Id == job.Id && j.Torrent!.WaitingFor is not null));
        Assert.Equal(DownloadManagerService.TorrentWaitingForWifi, held.Torrent!.WaitingFor);
        Assert.True(held.Torrent.Seeding);
        await UntilAsync(() => SeedsOnAWorker() ? null : job);

        await service.SetNetworkCellularAsync(false);
        await UntilAsync(() => SeedsOnAWorker() ? job : null);
    }

    [Fact]
    public async Task WhereNoPowerIsReportedSeedingIsNeverHeld()
    {
        var (service, _, job) = await CompletedTorrentAsync();
        await UntilAsync(() => SeedsOnAWorker() ? job : null);
        await Task.Delay(200);
        var row = service.GetSnapshot().Jobs.Single(j => j.Id == job.Id);
        Assert.True(row.Torrent!.Seeding);
        Assert.Null(row.Torrent.WaitingFor);
        Assert.True(SeedsOnAWorker());
    }
}
