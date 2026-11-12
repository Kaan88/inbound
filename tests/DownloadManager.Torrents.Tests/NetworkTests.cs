using System.Net.Sockets;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using DownloadManager.Core;

namespace DownloadManager.Torrents.Tests;

/// <summary>T2 network: the listen arguments from Options, the test override, the port hint, and the worker's incoming report.</summary>
public sealed class NetworkTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Fact]
    public void ListenArgumentsCarryThePortAndOneRouterChoice()
    {
        var on = TorrentWorkerSupervisor.ListenArguments(51413, upnp: true);
        Assert.Equal(51413, on["port"]!.GetValue<int>());
        Assert.True(on["upnp"]!.GetValue<bool>());
        Assert.True(on["natpmp"]!.GetValue<bool>());
        Assert.True(on["dht"]!.GetValue<bool>());
        Assert.False(on["lsd"]!.GetValue<bool>());
        Assert.Null(on["interfaces"]);

        var off = TorrentWorkerSupervisor.ListenArguments(0, upnp: false);
        Assert.Equal(0, off["port"]!.GetValue<int>());
        Assert.False(off["upnp"]!.GetValue<bool>());
        Assert.False(off["natpmp"]!.GetValue<bool>());
    }

    [Fact]
    public async Task OptionsPortGoesToTheWorkerAtStartAndAgainWhileItRuns()
    {
        Recording? worker = null;
        await using var supervisor = new TorrentWorkerSupervisor(() => worker = new Recording(),
            new TorrentSupervisorOptions { DrainDelay = TimeSpan.FromSeconds(30), LoopbackOnly = false });
        await supervisor.SetListenAsync(50123, upnp: true);
        Assert.Null(worker); // nothing starts for a setting

        await using var session = await supervisor.AttachAsync("a", _ => Task.FromResult(Add()), CancellationToken.None);
        var first = Assert.Single(worker!.Listens);
        Assert.Equal(50123, first["port"]!.GetValue<int>());
        Assert.True(first["upnp"]!.GetValue<bool>());

        await supervisor.SetListenAsync(50124, upnp: false);
        Assert.Equal(2, worker.Listens.Count);
        Assert.Equal(50124, worker.Listens[1]["port"]!.GetValue<int>());
        Assert.False(worker.Listens[1]["natpmp"]!.GetValue<bool>());

        await supervisor.SetListenAsync(50124, upnp: false);
        Assert.Equal(2, worker.Listens.Count); // the same choice again sends nothing
    }

    [Fact]
    public async Task TheTestListenWinsOverOptions()
    {
        Recording? worker = null;
        await using var supervisor = new TorrentWorkerSupervisor(() => worker = new Recording(), new TorrentSupervisorOptions
        {
            Listen = () => new JsonObject { ["port"] = 6899, ["interfaces"] = new JsonArray("127.0.0.1"), ["dht"] = false },
        });
        await supervisor.SetListenAsync(50123, upnp: true);
        await using var session = await supervisor.AttachAsync("a", _ => Task.FromResult(Add()), CancellationToken.None);
        await supervisor.SetListenAsync(50124, upnp: true);
        var only = Assert.Single(worker!.Listens);
        Assert.Equal(6899, only["port"]!.GetValue<int>());
        Assert.Equal("127.0.0.1", only["interfaces"]![0]!.GetValue<string>());
    }

    [Fact]
    public async Task ThePortHintFollowsWhatTheWorkerReports()
    {
        Recording? worker = null;
        await using var supervisor = new TorrentWorkerSupervisor(() => worker = new Recording(reportListen: false),
            new TorrentSupervisorOptions { ListenWait = TimeSpan.FromMilliseconds(50) });
        Assert.Equal(TorrentNetworkStatus.None, supervisor.Network);
        await using var session = await supervisor.AttachAsync("a", _ => Task.FromResult(Add()), CancellationToken.None);
        Assert.True(supervisor.Network.Ran);

        // IPv6 failed but IPv4 opened: the port counts as open.
        worker!.Inject(new JsonObject { ["event"] = "error", ["kind"] = "listen", ["message"] = "address family not supported" });
        await WaitForAsync(() => supervisor.Network.ListenError is not null);
        worker.Inject(new JsonObject { ["event"] = "stats", ["listenPort"] = 50123 });
        worker.Inject(new JsonObject { ["event"] = "error", ["kind"] = "listen", ["message"] = "late failure" });
        worker.Inject(new JsonObject { ["event"] = "stats", ["mappedPort"] = 50123 });
        worker.Inject(new JsonObject { ["event"] = "stats", ["incoming"] = true });
        await WaitForAsync(() => supervisor.Network.IncomingSeen);
        Assert.Equal(new TorrentNetworkStatus(true, 50123, null, 50123, true), supervisor.Network);
    }

    [Fact]
    public async Task AFailedListenIsReported()
    {
        Recording? worker = null;
        await using var supervisor = new TorrentWorkerSupervisor(() => worker = new Recording(reportListen: false),
            new TorrentSupervisorOptions { ListenWait = TimeSpan.FromMilliseconds(50) });
        await using var session = await supervisor.AttachAsync("a", _ => Task.FromResult(Add()), CancellationToken.None);
        worker!.Inject(new JsonObject { ["event"] = "error", ["kind"] = "listen", ["message"] = "address in use" });
        await WaitForAsync(() => supervisor.Network.ListenError is not null);
        Assert.Equal(new TorrentNetworkStatus(true, 0, "address in use", null, false), supervisor.Network);
    }

    [Fact]
    public async Task LoopbackOnlyKeepsOptionsPortOn127AndTurnsTheRestOff()
    {
        Recording? worker = null;
        await using var supervisor = new TorrentWorkerSupervisor(() => worker = new Recording(),
            new TorrentSupervisorOptions { DrainDelay = TimeSpan.FromSeconds(30), LoopbackOnly = true });
        await supervisor.SetListenAsync(50123, upnp: true);
        await using var session = await supervisor.AttachAsync("a", _ => Task.FromResult(Add()), CancellationToken.None);
        var sent = Assert.Single(worker!.Listens);
        Assert.Equal(50123, sent["port"]!.GetValue<int>());
        Assert.Equal("127.0.0.1", Assert.Single(sent["interfaces"]!.AsArray())!.GetValue<string>());
        Assert.False(sent["dht"]!.GetValue<bool>());
        Assert.False(sent["upnp"]!.GetValue<bool>());
        Assert.False(sent["natpmp"]!.GetValue<bool>());
    }

    [Fact]
    public async Task APortThatCantBeOpenedFallsBackToAnyFreePortForThatRun()
    {
        Recording? worker = null;
        await using var supervisor = new TorrentWorkerSupervisor(() => worker = new Recording(refuse: 50123),
            new TorrentSupervisorOptions { ListenWait = TimeSpan.FromSeconds(30), LoopbackOnly = false });
        await supervisor.SetListenAsync(50123, upnp: true);
        var started = DateTime.UtcNow;
        await using var session = await supervisor.AttachAsync("a", _ => Task.FromResult(Add()), CancellationToken.None);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10)); // the failure ends the wait, not the timeout

        Assert.Equal(2, worker!.Listens.Count);
        Assert.Equal(50123, worker.Listens[0]["port"]!.GetValue<int>());
        Assert.Equal(0, worker.Listens[1]["port"]!.GetValue<int>());
        Assert.True(worker.Listens[1]["upnp"]!.GetValue<bool>());
        await WaitForAsync(() => supervisor.Network.Port == 6881);
        Assert.Equal(50123, supervisor.Network.UnusablePort);
        Assert.Null(supervisor.Network.ListenError);

        // A port that opens, chosen while it runs: no fallback, and the note goes.
        await supervisor.SetListenAsync(50124, upnp: true);
        Assert.Equal(3, worker.Listens.Count);
        Assert.Equal(50124, worker.Listens[2]["port"]!.GetValue<int>());
        await WaitForAsync(() => supervisor.Network.Port == 6881);
        Assert.Null(supervisor.Network.UnusablePort);
    }

    [Fact]
    public async Task OneAddressFailingKeepsTheChosenPort()
    {
        Recording? worker = null;
        await using var supervisor = new TorrentWorkerSupervisor(() => worker = new Recording(refuse: 50123, otherAddressOpens: true),
            new TorrentSupervisorOptions { ListenWait = TimeSpan.FromSeconds(30) });
        await supervisor.SetListenAsync(50123, upnp: false);
        await using var session = await supervisor.AttachAsync("a", _ => Task.FromResult(Add()), CancellationToken.None);
        Assert.Single(worker!.Listens);
        Assert.Null(supervisor.Network.UnusablePort);
    }

    [WorkerFact]
    public async Task TheWorkerReportsTheFirstIncomingConnectionOnce()
    {
        var folder = Directory.CreateTempSubdirectory("inbound-incoming-").FullName;
        await using var w = Worker.Start(output, "listener");
        var port = await w.ListenOnLoopbackAsync();
        // libtorrent turns connections away while no torrent is active, so one magnet waits (DHT is off: it finds nobody).
        await w.Process.SendAsync("add", new JsonObject { ["id"] = "m", ["savePath"] = folder, ["magnet"] = "magnet:?xt=urn:btih:" + new string('b', 40) });
        var mark = w.Mark();
        using (var first = new TcpClient()) await first.ConnectAsync("127.0.0.1", port);
        await w.WaitForAsync(e => e["event"]?.GetValue<string>() == "stats" && e["incoming"]?.GetValue<bool>() == true, Wait, "incoming", mark);
        using (var second = new TcpClient()) await second.ConnectAsync("127.0.0.1", port);
        await w.Process.SendAsync("stats", new JsonObject()); // a round trip after the second connection
        await Task.Delay(500);
        Assert.Single(w.Seen.Skip(mark), e => e["incoming"] is not null);
        await w.Process.CloseAsync(TimeSpan.FromSeconds(15));
        try { Directory.Delete(folder, recursive: true); }
        catch (IOException) { }
    }

    private static JsonObject Add() => new() { ["savePath"] = Path.GetTempPath(), ["magnet"] = "magnet:?xt=urn:btih:" + new string('a', 40) };

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow + Wait;
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("The condition was not met.");
            await Task.Delay(20);
        }
    }

    /// <summary>The in-memory engine, recording each <c>setListen</c>; it can stay silent about its port.</summary>
    private sealed class Recording(bool reportListen = true, int? refuse = null, bool otherAddressOpens = false) : ITorrentEngine
    {
        private int _open;

        private readonly FakeTorrentEngine _inner = new();
        private readonly List<JsonObject> _listens = [];

        public IReadOnlyList<JsonObject> Listens
        {
            get { lock (_listens) return _listens.ToArray(); }
        }

        public ChannelReader<JsonObject> Events => _inner.Events;

        public Task Exited => _inner.Exited;

        public void Inject(JsonObject message) => _inner.Inject(message);

        public Task<JsonObject> SendAsync(string command, JsonObject? arguments = null, CancellationToken cancellationToken = default)
        {
            if (command == "stats") return Task.FromResult(new JsonObject { ["ok"] = true, ["listenPort"] = Volatile.Read(ref _open) });
            if (command != "setListen") return _inner.SendAsync(command, arguments, cancellationToken);
            lock (_listens) _listens.Add(arguments!.DeepClone().AsObject());
            if (refuse is { } bad && arguments["port"]!.GetValue<int>() == bad)
            {
                // libtorrent tries each address before it answers the next command; one of them may still open.
                Volatile.Write(ref _open, otherAddressOpens ? bad : 0);
                _inner.Inject(new JsonObject { ["event"] = "error", ["kind"] = "listen", ["message"] = "access denied" });
                return Task.FromResult(new JsonObject { ["ok"] = true });
            }

            Volatile.Write(ref _open, 6881);
            return reportListen ? _inner.SendAsync(command, arguments, cancellationToken) : Task.FromResult(new JsonObject { ["ok"] = true });
        }

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}
