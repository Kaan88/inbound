using DownloadManager.Contracts;
using DownloadManager.Persistence;
using DownloadManager.Runtime.Ipc;

namespace DownloadManager.Integration.Tests;

/// <summary>T2 network: the listen port and UPnP settings, and <c>torrent.network</c>.</summary>
public sealed partial class HostCommandDispatcherTests
{
    [Fact]
    public async Task TurningTorrentsOnChoosesAPortOnceAndKeepsIt()
    {
        var service = await TorrentServiceAsync(enabled: false);
        var settings = service.GetSettings();
        Assert.Equal(0, settings.TorrentListenPort);
        Assert.False(settings.TorrentUpnp);

        settings.TorrentsEnabled = true;
        var saved = await service.UpdateSettingsAsync(settings);
        Assert.True(saved.Ok, saved.Error);
        var chosen = service.GetSettings().TorrentListenPort;
        Assert.InRange(chosen, TorrentListenPorts.PickMin, TorrentListenPorts.PickMax);
        Assert.Equal(chosen, (await new JsonSettingsStore(_paths).LoadAsync(CancellationToken.None)).TorrentListenPort);

        // A client that leaves the field empty (0) keeps the saved port.
        settings = service.GetSettings();
        settings.TorrentListenPort = 0;
        settings.TorrentUpnp = true;
        Assert.True((await service.UpdateSettingsAsync(settings)).Ok);
        Assert.Equal(chosen, service.GetSettings().TorrentListenPort);
        Assert.True(service.GetSettings().TorrentUpnp);

        settings = service.GetSettings();
        settings.TorrentListenPort = 50000;
        Assert.True((await service.UpdateSettingsAsync(settings)).Ok);
        Assert.Equal(50000, service.GetSettings().TorrentListenPort);

        settings = service.GetSettings();
        settings.TorrentListenPort = 80;
        var refused = await service.UpdateSettingsAsync(settings);
        Assert.False(refused.Ok);
        Assert.Equal("The torrent port must be 1024 to 65535.", refused.Error);
        Assert.Equal(50000, service.GetSettings().TorrentListenPort);
    }

    [Fact]
    public void APortInUseIsNotPickedForTorrents()
    {
        using var taken = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork,
            System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp) { ExclusiveAddressUse = true };
        taken.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
        var port = ((System.Net.IPEndPoint)taken.LocalEndPoint!).Port;
        Assert.False(Runtime.DownloadManagerService.CanListenOn(port));
        taken.Dispose();
        Assert.True(Runtime.DownloadManagerService.CanListenOn(port));
    }

    [Fact]
    public async Task SettingsFromBeforeThePortWithTorrentsOnGetOneAtStart()
    {
        var service = await TorrentServiceAsync(enabled: true);
        var port = service.GetSettings().TorrentListenPort;
        Assert.InRange(port, TorrentListenPorts.PickMin, TorrentListenPorts.PickMax);
        Assert.Equal(port, (await new JsonSettingsStore(_paths).LoadAsync(CancellationToken.None)).TorrentListenPort);
    }

    [Fact]
    public async Task TorrentsOffLeaveThePortUnchosen()
    {
        var service = await TorrentServiceAsync(enabled: false);
        var settings = service.GetSettings();
        settings.TorrentUpnp = true;
        Assert.True((await service.UpdateSettingsAsync(settings)).Ok);
        Assert.Equal(0, service.GetSettings().TorrentListenPort);
    }

    [Fact]
    public async Task TheNetworkRequestAnswersBeforeAndAfterAWorkerRan()
    {
        var service = await TorrentServiceAsync();
        var dispatcher = Dispatcher(service);
        Assert.True(TorrentMessageTypes.IsRequest(TorrentMessageTypes.Network));

        var before = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Network, new { })).GetPayload<TorrentNetworkResult>();
        Assert.True(before.Ok);
        Assert.False(before.Ran);
        Assert.False(before.Running);

        var add = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Add,
            new TorrentAddRequest { Input = "magnet:?xt=urn:btih:" + new string('c', 40), Destination = _paths.Root })).GetPayload<TorrentAddResult>();
        Assert.True(add.Ok, add.Error);
        var until = DateTime.UtcNow.AddSeconds(10);
        TorrentNetworkResult after;
        do
        {
            after = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Network, new { })).GetPayload<TorrentNetworkResult>();
            if (after.Port > 0) break;
            await Task.Delay(20);
        } while (DateTime.UtcNow < until);
        Assert.True(after.Ran);
        Assert.Equal(6881, after.Port); // what the in-memory engine reports
        Assert.False(after.IncomingSeen);
    }

    [Fact]
    public void AWebSessionCannotChangeTheTorrentNetwork()
    {
        var current = new SettingsDto { TorrentListenPort = 50000 };
        Assert.Contains("torrentListenPort", WebSettingsRules.DescribeRefusal(new SettingsDto { TorrentListenPort = 50001 }, current));
        Assert.Contains("torrentUpnp", WebSettingsRules.DescribeRefusal(new SettingsDto { TorrentListenPort = 50000, TorrentUpnp = true }, current));
        Assert.Null(WebSettingsRules.DescribeRefusal(new SettingsDto { TorrentListenPort = 50000 }, current));
    }

    [Fact]
    public void AWebSessionMayChangeTheSeedingSettingsButNotTurnTorrentsOnOrOpenThePort()
    {
        var current = new SettingsDto { TorrentListenPort = 50000 };
        var seeding = new SettingsDto { TorrentListenPort = 50000, TorrentSeedAfterDownload = false, TorrentSeedRatio = 3, TorrentSeedHours = 0, TorrentUploadLimit = 1024 };
        Assert.Null(WebSettingsRules.DescribeRefusal(seeding, current));
        foreach (var name in new[] { nameof(SettingsDto.TorrentSeedAfterDownload), nameof(SettingsDto.TorrentSeedRatio), nameof(SettingsDto.TorrentSeedHours), nameof(SettingsDto.TorrentUploadLimit) })
        {
            Assert.Contains(name, WebSettingsRules.Allowed);
            Assert.DoesNotContain(name, WebSettingsRules.PipeOnly);
        }

        foreach (var name in new[] { nameof(SettingsDto.TorrentsEnabled), nameof(SettingsDto.TorrentHandler), nameof(SettingsDto.TorrentListenPort), nameof(SettingsDto.TorrentUpnp) })
            Assert.Contains(name, WebSettingsRules.PipeOnly);
        Assert.Contains("torrentsEnabled", WebSettingsRules.DescribeRefusal(new SettingsDto { TorrentListenPort = 50000, TorrentsEnabled = true }, current));
    }
}
