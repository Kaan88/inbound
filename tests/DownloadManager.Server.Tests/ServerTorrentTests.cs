using DownloadManager.Contracts;

namespace DownloadManager.Server.Tests;

/// <summary>T3: the headless server's torrents: the capability follows the worker beside it, and its fixed listen port.</summary>
public sealed class ServerTorrentTests
{
    [Fact]
    public async Task TheHelloOffersTorrentsWhenTheWorkerIsBesideTheServer()
    {
        // Any file stands for the worker: nothing starts it until a torrent runs.
        var worker = Path.Combine(ServerHarness.NewWork(), "inbound-torrent");
        await File.WriteAllBytesAsync(worker, []);
        await using var server = await ServerHarness.StartAsync(options => options with { FindTorrentWorker = () => worker });

        Assert.Contains("torrents", await CapabilitiesAsync(server));
    }

    [Fact]
    public async Task TheServersBrowserTurnsTorrentsOnInOptions()
    {
        var worker = Path.Combine(ServerHarness.NewWork(), "inbound-torrent");
        await File.WriteAllBytesAsync(worker, []);
        await using var server = await ServerHarness.StartAsync(options => options with { FindTorrentWorker = () => worker });

        var settings = (await server.AskAsync<SettingsResult>(MessageTypes.SettingsGet, new { })).Settings!;
        Assert.False(settings.TorrentsEnabled);
        settings.TorrentsEnabled = true;
        settings.TorrentRoute = "thisPc";
        settings.TorrentChooseFiles = false;
        var saved = await server.AskAsync<SettingsResult>(MessageTypes.SettingsUpdate, new SettingsUpdateRequest { Settings = settings });

        Assert.True(saved.Ok, saved.Error);
        Assert.True(saved.Settings!.TorrentsEnabled);
        Assert.Equal("thisPc", saved.Settings.TorrentRoute);
        Assert.False(saved.Settings.TorrentChooseFiles);
    }

    [Fact]
    public async Task TheHelloOffersNoTorrentsWithoutTheWorker()
    {
        await using var server = await ServerHarness.StartAsync(options => options with { FindTorrentWorker = () => null });

        Assert.DoesNotContain("torrents", await CapabilitiesAsync(server));
    }

    [Fact]
    public void TheWorkerIsLookedForNextToTheServerProgram()
    {
        var folder = ServerHarness.NewWork();
        var before = Environment.GetEnvironmentVariable(Torrents.TorrentSetup.WorkerVariable);
        Environment.SetEnvironmentVariable(Torrents.TorrentSetup.WorkerVariable, null);
        try
        {
            Assert.Null(Torrents.TorrentSetup.FindWorker(folder));
            var worker = Path.Combine(folder, Torrents.TorrentSetup.WorkerFileName);
            File.WriteAllBytes(worker, []);
            Assert.Equal(worker, Torrents.TorrentSetup.FindWorker(folder));
        }
        finally
        {
            Environment.SetEnvironmentVariable(Torrents.TorrentSetup.WorkerVariable, before);
        }
    }

    [Fact]
    public void TheTorrentPortIsFixedAndPortMappingIsOffByDefault()
    {
        var options = ServerOptions.FromEnvironment(_ => null);
        Assert.Equal(ServerOptions.DefaultTorrentPort, options.TorrentPort);
        Assert.False(options.TorrentPortMapping);
        Assert.Null(options.TorrentPortProblem);

        var listen = options.TorrentListen();
        Assert.Equal(6881, (int)listen["port"]!);
        Assert.False((bool)listen["upnp"]!);
        Assert.False((bool)listen["natpmp"]!);
        Assert.True((bool)listen["dht"]!);
    }

    [Fact]
    public void TheTorrentPortAndPortMappingComeFromTheEnvironment()
    {
        var options = ServerOptions.FromEnvironment(name => name switch
        {
            ServerOptions.TorrentPortVariable => " 51413 ",
            ServerOptions.TorrentUpnpVariable => "on",
            _ => null,
        });
        Assert.Equal(51413, options.TorrentPort);
        Assert.True(options.TorrentPortMapping);
        Assert.True((bool)options.TorrentListen()["upnp"]!);

        foreach (var bad in new[] { "0", "65536", "-1", "port", "6881/udp" })
        {
            var refused = ServerOptions.FromEnvironment(name => name == ServerOptions.TorrentPortVariable ? bad : null);
            Assert.Contains(ServerOptions.TorrentPortVariable, refused.TorrentPortProblem);
        }
    }

    [Fact]
    public async Task ABadTorrentPortStopsTheServerFromStarting()
    {
        await Assert.ThrowsAsync<ServerStartException>(() =>
            ServerHarness.StartAsync(options => options with { TorrentPortProblem = "DOWNLOADMANAGER_TORRENT_PORT takes a port number" }));
    }

    /// <summary>A server with a stand-in worker (nothing starts it: no torrent here runs) and torrents on.</summary>
    private static async Task<ServerHarness> TorrentServerAsync()
    {
        var worker = Path.Combine(ServerHarness.NewWork(), "inbound-torrent");
        await File.WriteAllBytesAsync(worker, []);
        var server = await ServerHarness.StartAsync(options => options with { FindTorrentWorker = () => worker });
        var settings = (await server.AskAsync<SettingsResult>(MessageTypes.SettingsGet, new { })).Settings!;
        settings.TorrentsEnabled = true;
        settings.TorrentChooseFiles = false;
        var saved = await server.AskAsync<SettingsResult>(MessageTypes.SettingsUpdate, new SettingsUpdateRequest { Settings = settings });
        Assert.True(saved.Ok, saved.Error);
        return server;
    }

    private static async Task<JobDto> AddLaterAsync(ServerHarness server, string destination)
    {
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(20));
        var add = await server.AskAsync<TorrentAddResult>(TorrentMessageTypes.Add,
            new TorrentAddRequest { Input = $"magnet:?xt=urn:btih:{hash}&dn=Later", Destination = destination, StartMode = "later" });
        Assert.True(add.Ok, add.Error);
        return add.Job!;
    }

    [Fact]
    public async Task TheWebUisTorrentDetailsAreAnsweredOverTheSocketAndSayNotRunningForAStoppedTorrent()
    {
        await using var server = await TorrentServerAsync();
        var job = await AddLaterAsync(server, server.Root);
        await using var client = await server.ConnectAsync();
        Assert.Equal(MessageTypes.ServerHello, (await client.ReadAsync())!.Type);

        var peers = await client.AskAsync<TorrentPeersResult>(TorrentMessageTypes.Peers, new TorrentJobRequest { JobId = job.Id });
        Assert.True(peers.Ok, peers.Error);
        Assert.False(peers.Running);
        Assert.Empty(peers.Peers);
        var trackers = await client.AskAsync<TorrentTrackersResult>(TorrentMessageTypes.Trackers, new TorrentJobRequest { JobId = job.Id });
        Assert.True(trackers is { Ok: true, Running: false }, trackers.Error);
        var pieces = await client.AskAsync<TorrentPiecesResult>(TorrentMessageTypes.Pieces, new TorrentJobRequest { JobId = job.Id });
        Assert.True(pieces is { Ok: true, Running: false, Count: 0 }, pieces.Error);

        // Nothing has run, so nothing listens.
        var network = await client.AskAsync<TorrentNetworkResult>(TorrentMessageTypes.Network, new { });
        Assert.True(network is { Ok: true, Ran: false, Running: false }, network.Error);
    }

    [Fact]
    public async Task SeedingIsOnlyForAFinishedTorrentAndAnUnknownJobIsRefused()
    {
        await using var server = await TorrentServerAsync();
        var job = await AddLaterAsync(server, server.Root);

        var on = await server.AskAsync<TorrentActionResult>(TorrentMessageTypes.Seed, new TorrentSeedRequest { JobId = job.Id, On = true });
        Assert.False(on.Ok);
        Assert.Equal("Only a finished torrent can be seeded.", on.Error);
        var off = await server.AskAsync<TorrentActionResult>(TorrentMessageTypes.Seed, new TorrentSeedRequest { JobId = job.Id, On = false });
        Assert.True(off.Ok, off.Error);

        var unknown = Guid.NewGuid();
        Assert.Equal("That torrent is not in the list.", (await server.AskAsync<TorrentActionResult>(TorrentMessageTypes.Seed, new TorrentSeedRequest { JobId = unknown, On = true })).Error);
        foreach (var type in new[] { TorrentMessageTypes.Peers, TorrentMessageTypes.Trackers, TorrentMessageTypes.Pieces })
        {
            var refused = await server.AskAsync<TorrentActionResult>(type, new TorrentJobRequest { JobId = unknown });
            Assert.False(refused.Ok, type);
            Assert.Equal("That torrent is not in the list.", refused.Error);
        }
    }

    [Fact]
    public async Task TheSeedingSettingsAreSavedAndCheckedOnTheServer()
    {
        await using var server = await TorrentServerAsync();
        var settings = (await server.AskAsync<SettingsResult>(MessageTypes.SettingsGet, new { })).Settings!;
        Assert.Equal((true, 1.0, 48d, 0L), (settings.TorrentSeedAfterDownload, settings.TorrentSeedRatio, settings.TorrentSeedHours, settings.TorrentUploadLimit));

        settings.TorrentSeedAfterDownload = false;
        settings.TorrentSeedRatio = 2;
        settings.TorrentSeedHours = 0;
        settings.TorrentUploadLimit = 100 * 1024;
        var saved = await server.AskAsync<SettingsResult>(MessageTypes.SettingsUpdate, new SettingsUpdateRequest { Settings = settings });
        Assert.True(saved.Ok, saved.Error);
        Assert.Equal((false, 2.0, 0d, 100L * 1024), (saved.Settings!.TorrentSeedAfterDownload, saved.Settings.TorrentSeedRatio, saved.Settings.TorrentSeedHours, saved.Settings.TorrentUploadLimit));

        settings.TorrentSeedRatio = 0;
        var refused = await server.AskAsync<SettingsResult>(MessageTypes.SettingsUpdate, new SettingsUpdateRequest { Settings = settings });
        Assert.False(refused.Ok);
        Assert.Equal("Seeding needs a ratio or a time limit; both can't be 0.", refused.Error);
    }

    [Fact]
    public async Task ATorrentIsAddedOnlyIntoTheServersFoldersAndItsListEntryIsGivenForExport()
    {
        await using var server = await TorrentServerAsync();
        var outside = Path.Combine(server.Work, "elsewhere");
        Directory.CreateDirectory(outside);
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(20));
        var refused = await server.AskAsync<TorrentAddResult>(TorrentMessageTypes.Add,
            new TorrentAddRequest { Input = $"magnet:?xt=urn:btih:{hash}", Destination = outside, StartMode = "later" });
        Assert.False(refused.Ok);
        Assert.False(string.IsNullOrEmpty(refused.Error));

        var job = await AddLaterAsync(server, server.Root);
        var entries = await server.AskAsync<TorrentListEntriesResult>(TorrentMessageTypes.ListEntries, new TorrentListEntriesRequest());
        Assert.True(entries.Ok, entries.Error);
        Assert.True(entries.Entries.ContainsKey(job.Id));
        Assert.Null(entries.Next);

        var imported = await server.AskAsync<TorrentImportResult>(TorrentMessageTypes.Import, new TorrentImportRequest());
        Assert.True(imported.Ok, imported.Error);
        Assert.Equal(0, imported.Added);
    }

    [Fact]
    public async Task TheWebUisT3TorrentCommandsAreAnsweredOverTheSocket()
    {
        // Options, recheck, announce, details and watch from the web UI's own socket, for a torrent that does not run.
        await using var server = await TorrentServerAsync();
        var job = await AddLaterAsync(server, server.Root);
        await using var client = await server.ConnectAsync();
        Assert.Equal(MessageTypes.ServerHello, (await client.ReadAsync())!.Type);
        var request = new TorrentJobRequest { JobId = job.Id };

        var options = await client.AskAsync<TorrentOptionsResult>(TorrentMessageTypes.Options, request);
        Assert.True(options is { Ok: true, Running: false }, options.Error);
        Assert.Equal("global", options.Options!.SeedGoal);
        Assert.Null(options.Options.DownloadLimit);

        var set = await client.AskAsync<TorrentOptionsResult>(TorrentMessageTypes.SetOptions,
            new TorrentOptionsDto { JobId = job.Id, DownloadLimit = 500 * 1024, UploadLimit = 0, SeedGoal = "ratio", SeedGoalValue = 2, Sequential = true });
        Assert.True(set is { Ok: true, Running: false }, set.Error);
        Assert.Equal((500L * 1024, (long?)null, "ratio", 2d, true),
            (set.Options!.DownloadLimit, set.Options.UploadLimit, set.Options.SeedGoal, set.Options.SeedGoalValue, set.Options.Sequential));
        var reread = await client.AskAsync<TorrentOptionsResult>(TorrentMessageTypes.Options, request);
        Assert.Equal(500L * 1024, reread.Options!.DownloadLimit);

        var badRatio = await client.AskAsync<TorrentOptionsResult>(TorrentMessageTypes.SetOptions, new TorrentOptionsDto { JobId = job.Id, SeedGoal = "ratio", SeedGoalValue = 0 });
        Assert.False(badRatio.Ok);
        Assert.Equal("Enter a seeding ratio from 0.01 to 100.", badRatio.Error);

        // A magnet without its file list has nothing to check, and a torrent that doesn't run can't ask for peers.
        var recheck = await client.AskAsync<TorrentActionResult>(TorrentMessageTypes.Recheck, request);
        Assert.False(recheck.Ok);
        Assert.Equal("The file list is not in yet, so there is nothing to check.", recheck.Error);
        var announce = await client.AskAsync<TorrentActionResult>(TorrentMessageTypes.Announce, request);
        Assert.False(announce.Ok);
        Assert.Equal("The torrent isn't running, so it can't ask for peers now.", announce.Error);

        var details = await client.AskAsync<TorrentDetailsResult>(TorrentMessageTypes.Details, request);
        Assert.True(details is { Ok: true, Running: false, Source: "magnet" }, details.Error);
        Assert.Equal(job.Torrent!.InfoHash, details.InfoHash);
        Assert.Equal("ratio", details.Options.SeedGoal);

        // The watch is held for this socket only: on, off, and on again until the socket closes.
        var service = server.App.Service;
        Assert.Equal(0, service.TorrentWatcherCount);
        var watch = await client.AskAsync<TorrentActionResult>(TorrentMessageTypes.Watch, new TorrentWatchRequest { On = true, LeaseSeconds = 60 });
        Assert.True(watch.Ok, watch.Error);
        Assert.Equal(1, service.TorrentWatcherCount);
        var unwatch = await client.AskAsync<TorrentActionResult>(TorrentMessageTypes.Watch, new TorrentWatchRequest { On = false });
        Assert.True(unwatch.Ok, unwatch.Error);
        Assert.Equal(0, service.TorrentWatcherCount);

        await using (var other = await server.ConnectAsync())
        {
            Assert.Equal(MessageTypes.ServerHello, (await other.ReadAsync())!.Type);
            Assert.True((await other.AskAsync<TorrentActionResult>(TorrentMessageTypes.Watch, new TorrentWatchRequest { On = true, LeaseSeconds = 60 })).Ok);
            Assert.Equal(1, service.TorrentWatcherCount);
        }

        // A socket that closes while it watches ends its watch.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (service.TorrentWatcherCount != 0 && DateTime.UtcNow < deadline) await Task.Delay(25);
        Assert.Equal(0, service.TorrentWatcherCount);
    }

    [Fact]
    public async Task TheServersBrowserChangesHowPeersAreFoundAndTheConnectionLimits()
    {
        // The server's own web UI has no PC beside it: Options › Torrents' peer settings are its to change.
        await using var server = await TorrentServerAsync();
        var settings = (await server.AskAsync<SettingsResult>(MessageTypes.SettingsGet, new { })).Settings!;
        Assert.Equal((true, true, false, "prefer", false), (settings.TorrentDht, settings.TorrentPex, settings.TorrentLsd, settings.TorrentEncryption, settings.TorrentUseProxy));

        settings.TorrentDht = false;
        settings.TorrentLsd = true;
        settings.TorrentEncryption = "require";
        settings.TorrentSeedsAtOnce = 3;
        settings.TorrentConnectionsPerTorrent = 50;
        settings.TorrentMaxConnections = 500;
        var saved = await server.AskAsync<SettingsResult>(MessageTypes.SettingsUpdate, new SettingsUpdateRequest { Settings = settings });
        Assert.True(saved.Ok, saved.Error);
        Assert.Equal((false, true, "require", 3, 50, 500),
            (saved.Settings!.TorrentDht, saved.Settings.TorrentLsd, saved.Settings.TorrentEncryption, saved.Settings.TorrentSeedsAtOnce,
                saved.Settings.TorrentConnectionsPerTorrent, saved.Settings.TorrentMaxConnections));

        // The web UI checks these with the same words before it sends them.
        settings.TorrentConnectionsPerTorrent = 1;
        var refused = await server.AskAsync<SettingsResult>(MessageTypes.SettingsUpdate, new SettingsUpdateRequest { Settings = settings });
        Assert.False(refused.Ok);
        Assert.Equal("Enter connections per torrent from 2 to 2000, or 0 for no limit of its own.", refused.Error);
    }

    [Fact]
    public async Task TheT3TorrentCommandsRefuseAJobThatIsNotATorrentInTheList()
    {
        await using var server = await TorrentServerAsync();
        var unknown = new TorrentJobRequest { JobId = Guid.NewGuid() };
        foreach (var type in new[] { TorrentMessageTypes.Options, TorrentMessageTypes.Recheck, TorrentMessageTypes.Announce, TorrentMessageTypes.Details })
        {
            var refused = await server.AskAsync<TorrentActionResult>(type, unknown);
            Assert.False(refused.Ok, type);
            Assert.Equal("That torrent is not in the list.", refused.Error);
        }

        var set = await server.AskAsync<TorrentOptionsResult>(TorrentMessageTypes.SetOptions, new TorrentOptionsDto { JobId = unknown.JobId, Sequential = true });
        Assert.Equal("That torrent is not in the list.", set.Error);

        // A download in the list that is not a torrent is refused the same way.
        var http = await server.AskAsync<AddJobResponse>(MessageTypes.JobAdd,
            new AddJobRequest { Url = "http://127.0.0.1:1/plain.bin", DestinationDirectory = server.Root, StartMode = "later" });
        Assert.True(http.Accepted, http.Error);
        Assert.Null(http.Job!.Torrent);
        var plain = new TorrentJobRequest { JobId = http.Job.Id };
        foreach (var type in new[] { TorrentMessageTypes.Options, TorrentMessageTypes.Recheck, TorrentMessageTypes.Announce, TorrentMessageTypes.Details })
        {
            var refused = await server.AskAsync<TorrentActionResult>(type, plain);
            Assert.False(refused.Ok, type);
            Assert.Equal("That torrent is not in the list.", refused.Error);
        }

        var setPlain = await server.AskAsync<TorrentOptionsResult>(TorrentMessageTypes.SetOptions, new TorrentOptionsDto { JobId = http.Job.Id, Sequential = true });
        Assert.False(setPlain.Ok);
        Assert.Equal("That torrent is not in the list.", setPlain.Error);
    }

    [Fact]
    public async Task InspectSaysTheFreeSpaceInTheServersFolderOnly()
    {
        await using var server = await TorrentServerAsync();
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(20));
        var magnet = $"magnet:?xt=urn:btih:{hash}&dn=Space";

        var inside = await server.AskAsync<TorrentInspectResult>(TorrentMessageTypes.Inspect, new TorrentInspectRequest { Input = magnet, Destination = server.Root });
        Assert.True(inside.Ok, inside.Error);
        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(server.Root)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(inside.Destination!)));
        Assert.True(inside.FreeBytes is > 0);
        // A magnet's size isn't known yet, so it can't be said not to fit.
        Assert.Null(inside.SpaceProblem);

        // Outside the server's folders nothing is measured.
        var elsewhere = Path.Combine(server.Work, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        var outside = await server.AskAsync<TorrentInspectResult>(TorrentMessageTypes.Inspect, new TorrentInspectRequest { Input = magnet, Destination = elsewhere });
        Assert.Null(outside.FreeBytes);
    }

    private static async Task<IReadOnlyList<string>> CapabilitiesAsync(ServerHarness server)
    {
        await using var client = await server.ConnectAsync();
        var hello = await client.ReadAsync();
        Assert.Equal(MessageTypes.ServerHello, hello!.Type);
        return hello.GetPayload<HostHelloDto>().Capabilities;
    }
}
