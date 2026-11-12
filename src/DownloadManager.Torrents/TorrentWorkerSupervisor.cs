using DownloadManager.Core;
using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace DownloadManager.Torrents;

public enum TorrentWorkerState
{
    Off,
    Starting,
    Active,
    /// <summary>No torrent is attached; the worker closes when the drain delay passes without a new one.</summary>
    Draining,
}

public sealed class TorrentSupervisorOptions
{
    /// <summary>How long the worker stays up after the last torrent left (a quick resume reuses it).</summary>
    public TimeSpan DrainDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>A worker that dies is started again at most once in this window; a second death fails the torrents.</summary>
    public TimeSpan RestartWindow { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>How long a closing worker gets to save before it is killed.</summary>
    public TimeSpan CloseTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public int ProgressIntervalMs { get; init; } = 1000;

    /// <summary>How long a start waits for the worker to report its listen port.</summary>
    public TimeSpan ListenWait { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Fixed <c>setListen</c> arguments that win over <see cref="TorrentWorkerSupervisor.SetListenAsync"/> (the loopback
    /// test listen). Null: <see cref="TorrentWorkerSupervisor.ListenArguments"/> of the port and UPnP choice from Options.
    /// </summary>
    public Func<JsonObject>? Listen { get; init; }

    /// <summary>
    /// Test runs only: Options' port on 127.0.0.1 with DHT, UPnP and NAT-PMP off. Defaults to
    /// <see cref="TorrentSetup.LoopbackOnlyVariable"/> = 1, which the test projects set; unset in every installed build.
    /// </summary>
    public bool LoopbackOnly { get; init; } = Environment.GetEnvironmentVariable(TorrentSetup.LoopbackOnlyVariable) == "1";
}

/// <summary>
/// The only owner of the torrent worker process (docs/plans/torrent-t1-design.md, section 3). It starts the worker on
/// the first attach and closes it <see cref="TorrentSupervisorOptions.DrainDelay"/> after the last detach, so with no
/// torrent running there is no process at all. Events are routed to the attached torrent they name. A worker that dies
/// is started again (once per <see cref="TorrentSupervisorOptions.RestartWindow"/>) and every attached torrent is added
/// back from what its add callback returns, normally its last resume blob.
/// </summary>
public sealed class TorrentWorkerSupervisor : IAsyncDisposable
{
    /// <summary>Sent to every attached torrent when the worker died and was not started again.</summary>
    public const string WorkerLostEvent = "workerLost";

    private readonly Func<ITorrentEngine> _start;
    private readonly TorrentSupervisorOptions _options;
    private readonly Action<string>? _log;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, TorrentSession> _sessions = new(StringComparer.Ordinal);
    private ITorrentEngine? _engine;
    private CancellationTokenSource? _drain;
    private DateTimeOffset? _lastRestart;
    private TorrentWorkerState _state;
    private readonly object _limitsSync = new();
    private long? _downloadLimit;
    private long? _uploadLimit;
    private TaskCompletionSource<int> _listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _listenFailed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;
    private int _listenPort;
    private bool _upnp;
    private readonly object _networkSync = new();
    private TorrentNetworkStatus _network = TorrentNetworkStatus.None;
    private bool _listenSucceeded;
    /// <summary>T3: Options › Torrents' peer, connection and proxy choices; sent at every start and when they change.</summary>
    private TorrentPeerSettings _peerSettings = TorrentPeerSettings.Default;
    /// <summary>T3: the progress interval wanted now (1 s while someone watches torrents, else 5 s). The latest wins.</summary>
    private int _progressIntervalMs;
    /// <summary>The interval the running worker was last sent (0 = none yet); read and written under the gate.</summary>
    private int _sentProgressIntervalMs;

    public TorrentWorkerSupervisor(Func<ITorrentEngine> start, TorrentSupervisorOptions? options = null,
        Action<string>? log = null, TimeProvider? time = null)
    {
        _start = start;
        _options = options ?? new TorrentSupervisorOptions();
        _log = log;
        _time = time ?? TimeProvider.System;
        _progressIntervalMs = _options.ProgressIntervalMs;
    }

    /// <summary>The progress interval sent to the worker now (tests).</summary>
    public int ProgressIntervalMs => Volatile.Read(ref _progressIntervalMs);

    /// <summary>The proxy the worker was told to use; null for none (tests: never the password).</summary>
    public string? ProxyInUse { get; private set; }

    public TorrentWorkerState State => _state;

    public event Action<TorrentWorkerState>? StateChanged;

    /// <summary>The worker's process, while it runs (tests).</summary>
    public ITorrentEngine? Engine => _engine;

    /// <summary>What the worker reported of its listening on its current or last run (the port hint).</summary>
    public TorrentNetworkStatus Network
    {
        get
        {
            lock (_networkSync) return _network;
        }
    }

    /// <summary>
    /// The <c>setListen</c> arguments for Options' choice: every interface, the port (0: any), DHT on, LSD off, and
    /// UPnP and NAT-PMP together on or off.
    /// </summary>
    public static JsonObject ListenArguments(int port, bool upnp) => ListenArguments(port, upnp, TorrentPeerSettings.Default);

    /// <summary>T3: the same with Options' DHT, PEX, LSD and encryption choices.</summary>
    public static JsonObject ListenArguments(int port, bool upnp, TorrentPeerSettings peers) => new()
    {
        ["port"] = port is >= 0 and <= 65535 ? port : 0,
        ["dht"] = peers.Dht,
        ["lsd"] = peers.Lsd,
        ["pex"] = peers.Pex,
        ["encryption"] = peers.Encryption,
        ["upnp"] = upnp,
        ["natpmp"] = upnp,
    };

    /// <summary>
    /// T3: Options › Torrents' peer, connection and proxy choices. Kept for the next start, and sent now when the worker
    /// runs: <c>setListen</c> for DHT, PEX, LSD and encryption, <c>setPeerLimits</c> and <c>setProxy</c>.
    /// </summary>
    public async Task SetPeerSettingsAsync(TorrentPeerSettings settings)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var before = _peerSettings;
            if (before == settings) return;
            _peerSettings = settings;
            if (_engine is not { } engine) return;
            if (before.Proxy != settings.Proxy) await SendProxyAsync(engine, CancellationToken.None).ConfigureAwait(false);
            if (_options.Listen is null && (before.Dht, before.Pex, before.Lsd, before.Encryption) != (settings.Dht, settings.Pex, settings.Lsd, settings.Encryption))
                await engine.SendAsync("setListen", CurrentListenArguments(), CancellationToken.None).ConfigureAwait(false);
            if ((before.SeedsAtOnce, before.ConnectionsPerTorrent, before.MaxConnections) != (settings.SeedsAtOnce, settings.ConnectionsPerTorrent, settings.MaxConnections))
                await SendPeerLimitsAsync(engine, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e) when (e is TorrentEngineExitedException or TorrentCommandException or InvalidOperationException)
        {
            _log?.Invoke($"Torrent worker: the peer settings were not applied: {e.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>T3: how often the worker reports progress (100 to 60000 ms); sent now when it runs.</summary>
    public Task SetProgressIntervalAsync(int milliseconds)
    {
        WantProgressInterval(milliseconds);
        return ApplyProgressIntervalAsync();
    }

    /// <summary>
    /// Records the wanted progress interval at once and sends it in the background. Whatever order the sends take,
    /// each sends the latest wanted value, so the worker ends at the last one asked for.
    /// </summary>
    public void SetProgressInterval(int milliseconds)
    {
        WantProgressInterval(milliseconds);
        _ = Task.Run(ApplyProgressIntervalAsync);
    }

    private void WantProgressInterval(int milliseconds)
        => Volatile.Write(ref _progressIntervalMs, Math.Clamp(milliseconds, 100, 60000));

    private async Task ApplyProgressIntervalAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var wanted = Volatile.Read(ref _progressIntervalMs);
            if (_engine is not { } engine || _sentProgressIntervalMs == wanted) return;
            await engine.SendAsync("setProgressInterval", new JsonObject { ["ms"] = wanted }, CancellationToken.None).ConfigureAwait(false);
            _sentProgressIntervalMs = wanted;
        }
        catch (Exception e) when (e is TorrentEngineExitedException or TorrentCommandException)
        {
            _log?.Invoke($"Torrent worker: the progress interval was not changed: {e.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>T3: the running worker's <c>stats</c> reply; null while it does not run.</summary>
    public async Task<JsonObject?> StatsAsync(CancellationToken cancellationToken)
    {
        if (_engine is not { } engine) return null;
        try
        {
            return await engine.SendAsync("stats", new JsonObject(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is TorrentEngineExitedException or TorrentCommandException)
        {
            return null;
        }
    }

    /// <summary>
    /// T3: tells the worker its proxy. An older worker answers "unknown command": without a proxy that changes nothing,
    /// but with one it would connect around it, so the start fails instead (<see cref="InvalidOperationException"/>).
    /// </summary>
    private async Task SendProxyAsync(ITorrentEngine engine, CancellationToken cancellationToken)
    {
        var proxy = _peerSettings.Proxy;
        var arguments = proxy is null
            ? new JsonObject { ["type"] = "none" }
            : new JsonObject
            {
                ["type"] = proxy.Type, ["host"] = proxy.Host, ["port"] = proxy.Port,
                ["username"] = proxy.Username ?? string.Empty, ["password"] = proxy.Password ?? string.Empty,
            };
        try
        {
            await engine.SendAsync("setProxy", arguments, cancellationToken).ConfigureAwait(false);
            ProxyInUse = proxy?.ToString();
        }
        catch (TorrentCommandException e) when (e.Reason == "unknown command")
        {
            ProxyInUse = null;
            if (proxy is not null)
                throw new InvalidOperationException("this version of the torrent program can't use a proxy. Turn off \"Use the proxy\" in Options > Torrents, or update the app.");
        }
    }

    /// <summary>T3: connections and seeds at once; an older worker without the command keeps libtorrent's defaults.</summary>
    private async Task SendPeerLimitsAsync(ITorrentEngine engine, CancellationToken cancellationToken)
    {
        var peers = _peerSettings;
        try
        {
            await engine.SendAsync("setPeerLimits", new JsonObject
            {
                ["connections"] = peers.MaxConnections,
                ["connectionsPerTorrent"] = peers.ConnectionsPerTorrent,
                ["activeSeeds"] = peers.SeedsAtOnce,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (TorrentCommandException e) when (e.Reason == "unknown command")
        {
            _log?.Invoke("Torrent worker: it can't limit connections or seeds at once (an older worker).");
        }
    }

    /// <summary>
    /// The port and UPnP choice from Options. Sent now when the worker runs (it moves to the new port), else used when it
    /// starts. <see cref="TorrentSupervisorOptions.Listen"/>, when set, wins.
    /// </summary>
    public async Task SetListenAsync(int port, bool upnp)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_listenPort == port && _upnp == upnp) return;
            _listenPort = port;
            _upnp = upnp;
            if (_options.Listen is null && _engine is { } engine)
            {
                lock (_networkSync)
                {
                    _listenSucceeded = false;
                    _network = _network with { Port = 0, ListenError = null, MappedPort = null, UnusablePort = null };
                }

                await ListenAsync(engine, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is TorrentEngineExitedException or TorrentCommandException)
        {
            _log?.Invoke($"Torrent worker: the listen port was not changed: {e.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    private JsonObject CurrentListenArguments() => _options.Listen?.Invoke() ?? Contained(ListenArguments(_listenPort, _upnp, _peerSettings));

    /// <summary>
    /// Test runs only (<see cref="TorrentSupervisorOptions.LoopbackOnly"/>): the same port, but on 127.0.0.1 with DHT, UPnP
    /// and NAT-PMP off, so a worker started from a test's own folder never listens on the network and Windows shows no
    /// firewall prompt for it.
    /// </summary>
    private JsonObject Contained(JsonObject arguments)
    {
        if (!_options.LoopbackOnly) return arguments;
        arguments["interfaces"] = new JsonArray("127.0.0.1");
        arguments["dht"] = false;
        arguments["lsd"] = false;
        arguments["upnp"] = false;
        arguments["natpmp"] = false;
        return arguments;
    }

    /// <summary>The port the running worker listens on; null before it reported one.</summary>
    public int? ListenPort => _listening.Task.IsCompletedSuccessfully ? _listening.Task.Result : null;

    /// <summary>
    /// Adds a torrent to the worker, starting it when needed. <paramref name="addArguments"/> builds the <c>add</c>
    /// command's fields (without <c>id</c>) and is called again for a restart, so it should read the latest resume blob.
    /// </summary>
    public async Task<TorrentSession> AttachAsync(string id, Func<CancellationToken, Task<JsonObject>> addArguments,
        CancellationToken cancellationToken)
    {
        var arguments = await addArguments(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_sessions.ContainsKey(id)) throw new InvalidOperationException("This torrent is already running.");
            _drain?.Cancel();
            _drain = null;
            // A worker that exited before its pump noticed is handled here first, so its torrents are added back.
            if (_engine is { Exited.IsCompleted: true } exited)
            {
                await RestartLockedAsync(exited).ConfigureAwait(false);
                // Its pump then finds it is no longer the current worker and leaves it alone, so it is released here (L3).
                _ = ReleaseExitedAsync(exited);
            }

            var engine = _engine ?? await StartLockedAsync(cancellationToken).ConfigureAwait(false);
            var session = new TorrentSession(this, id, addArguments);
            // Registered before the add, so the events that follow its reply at once reach it.
            _sessions[id] = session;
            try
            {
                arguments["id"] = id;
                var added = await engine.SendAsync("add", arguments, cancellationToken).ConfigureAwait(false);
                // T3: a worker from 0.4.0-t3 says it queued the file check asked for with the add (a 0.3 one ignores it).
                session.RecheckQueued = added["recheckQueued"] is JsonValue queued && queued.TryGetValue<bool>(out var yes) && yes;
            }
            catch (Exception e)
            {
                _sessions.TryRemove(id, out _);
                session.Complete();
                // A cancelled or broken add may still have reached the worker; it must not keep the torrent.
                if (e is not TorrentCommandException) _ = RemoveQuietlyAsync(engine, id);
                if (_sessions.IsEmpty) BeginDrainLocked();
                throw;
            }

            SetState(TorrentWorkerState.Active);
            return session;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The download cap for every torrent (null or 0: none). Sent now when the worker runs, else when it starts.</summary>
    public Task SetDownloadLimitAsync(long? bytesPerSecond) => StoreAndSendLimitsAsync(bytesPerSecond, keepUpload: true, upload: null);

    /// <summary>The upload cap for every torrent (null or 0: none), sent like <see cref="SetDownloadLimitAsync"/>.</summary>
    public Task SetUploadLimitAsync(long? bytesPerSecond) => StoreAndSendLimitsAsync(null, keepUpload: false, upload: bytesPerSecond, keepDownload: true);

    /// <summary>Both caps at once (T2: their shares of the overall speed limit), in one <c>setLimits</c>.</summary>
    public Task SetLimitsAsync(long? download, long? upload) => StoreAndSendLimitsAsync(download, keepUpload: false, upload);

    private async Task StoreAndSendLimitsAsync(long? download, bool keepUpload, long? upload, bool keepDownload = false)
    {
        // Kept before the gate: a worker that is starting now (it holds the gate) sends these as it starts.
        lock (_limitsSync)
        {
            if (!keepDownload) _downloadLimit = download is > 0 ? download : null;
            if (!keepUpload) _uploadLimit = upload is > 0 ? upload : null;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_engine is { } engine) await SendLimitsAsync(engine, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e) when (e is TorrentEngineExitedException or TorrentCommandException)
        {
            _log?.Invoke($"Torrent worker: the speed limits were not applied: {e.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    internal Task<JsonObject> SendAsync(string command, JsonObject arguments, CancellationToken cancellationToken)
    {
        if (command == "remove" && arguments["id"]?.GetValue<string>() is { } removing && _sessions.TryGetValue(removing, out var session))
            session.Removing = true;
        var engine = _engine ?? throw new TorrentEngineExitedException("The torrent worker is not running.");
        return engine.SendAsync(command, arguments, cancellationToken);
    }

    internal async Task DetachAsync(TorrentSession session)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_sessions.TryRemove(new KeyValuePair<string, TorrentSession>(session.Id, session))) return;
            session.Complete();
            // A run that ended without removing its torrent (it threw) must not leave it on the worker.
            if (!session.Removing && _engine is { } engine) _ = RemoveQuietlyAsync(engine, session.Id);
            if (_sessions.IsEmpty && _engine is not null) BeginDrainLocked();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RemoveQuietlyAsync(ITorrentEngine engine, string id)
    {
        try
        {
            using var timeout = new CancellationTokenSource(_options.CloseTimeout);
            await engine.SendAsync("remove", new JsonObject { ["id"] = id }, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is TorrentEngineExitedException or TorrentCommandException or OperationCanceledException)
        {
            // not there, or the worker is gone
        }
    }

    private async Task<ITorrentEngine> StartLockedAsync(CancellationToken cancellationToken)
    {
        // T3: torrents are set to use a proxy that can't be used; no process starts, so nothing goes around it.
        if (_peerSettings.ProxyProblem is { } proxyProblem) throw new InvalidOperationException(proxyProblem);
        SetState(TorrentWorkerState.Starting);
        ITorrentEngine engine;
        try
        {
            engine = _start();
        }
        catch
        {
            SetState(TorrentWorkerState.Off);
            throw;
        }

        _engine = engine;
        lock (_networkSync)
        {
            _listenSucceeded = false;
            _network = new TorrentNetworkStatus(true, 0, null, null, false);
        }

        _ = Task.Run(() => PumpAsync(engine));
        try
        {
            // The proxy first: listening starts the DHT, which must not go around it.
            await SendProxyAsync(engine, cancellationToken).ConfigureAwait(false);
            await ListenAsync(engine, cancellationToken).ConfigureAwait(false);
            var interval = Volatile.Read(ref _progressIntervalMs);
            await engine.SendAsync("setProgressInterval", new JsonObject { ["ms"] = interval }, cancellationToken).ConfigureAwait(false);
            _sentProgressIntervalMs = interval;
            await SendLimitsAsync(engine, cancellationToken).ConfigureAwait(false);
            await SendPeerLimitsAsync(engine, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _engine = null;
            (engine as TorrentWorkerProcess)?.Kill();
            await engine.DisposeAsync().ConfigureAwait(false);
            SetState(TorrentWorkerState.Off);
            throw;
        }

        _log?.Invoke("Torrent worker started.");
        return engine;
    }

    /// <summary>
    /// Sends <c>setListen</c> and waits up to <see cref="TorrentSupervisorOptions.ListenWait"/> for a port. Outgoing
    /// connections need the sockets it opens (a magnet's first peer would be lost before them), so when the port from
    /// Options can't be opened on any address (Windows reserves some ports for Hyper-V, WSL or Docker; another program
    /// may hold it) this run listens on any free port instead, and the port hint says so. Called under the gate.
    /// </summary>
    private async Task ListenAsync(ITorrentEngine engine, CancellationToken cancellationToken)
    {
        var listening = _listening = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failed = _listenFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            await engine.SendAsync("setListen", CurrentListenArguments(), cancellationToken).ConfigureAwait(false);
            var timeout = Task.Delay(_options.ListenWait, _time, wait.Token);
            var first = await Task.WhenAny(listening.Task, failed.Task, timeout).ConfigureAwait(false);
            if (first == listening.Task) return;
            cancellationToken.ThrowIfCancellationRequested();
            var port = _listenPort;
            if (first == failed.Task && _options.Listen is null && port != 0)
            {
                // The worker answers after libtorrent tried every address, so this says whether any of them opened.
                var reply = await engine.SendAsync("stats", new JsonObject(), cancellationToken).ConfigureAwait(false);
                if (reply["listenPort"] is JsonValue open && open.TryGetValue<int>(out var openPort) && openPort > 0) return;
                lock (_networkSync)
                {
                    if (_listenSucceeded) return;
                    _network = _network with { UnusablePort = port };
                }

                _log?.Invoke($"Torrent worker: port {port} couldn't be opened; this run listens on any free port.");
                await engine.SendAsync("setListen", Contained(ListenArguments(0, _upnp, _peerSettings)), cancellationToken).ConfigureAwait(false);
                if (await Task.WhenAny(listening.Task, timeout).ConfigureAwait(false) == listening.Task) return;
                cancellationToken.ThrowIfCancellationRequested();
            }

            _log?.Invoke("Torrent worker: no listen port reported yet; carrying on.");
        }
        finally
        {
            wait.Cancel(); // no timer left behind
        }
    }

    private Task SendLimitsAsync(ITorrentEngine engine, CancellationToken cancellationToken)
    {
        JsonObject limits;
        lock (_limitsSync) limits = new JsonObject { ["download"] = _downloadLimit ?? 0, ["upload"] = _uploadLimit ?? 0 };
        return engine.SendAsync("setLimits", limits, cancellationToken);
    }

    private void BeginDrainLocked()
    {
        _drain?.Cancel();
        var drain = new CancellationTokenSource();
        _drain = drain;
        SetState(TorrentWorkerState.Draining);
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_options.DrainDelay, _time, drain.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await _gate.WaitAsync().ConfigureAwait(false);
            ITorrentEngine? closing = null;
            try
            {
                if (drain.IsCancellationRequested || !_sessions.IsEmpty || _engine is null) return;
                closing = _engine;
                _engine = null;
                _drain = null;
                SetState(TorrentWorkerState.Off);
            }
            finally
            {
                _gate.Release();
            }

            await CloseAsync(closing).ConfigureAwait(false);
            _log?.Invoke("Torrent worker closed: no torrent is running.");
        });
    }

    private async Task CloseAsync(ITorrentEngine engine)
    {
        try
        {
            if (engine is TorrentWorkerProcess process) await process.CloseAsync(_options.CloseTimeout).ConfigureAwait(false);
            await engine.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _log?.Invoke($"Torrent worker: closing failed: {e.Message}");
        }
    }

    private async Task PumpAsync(ITorrentEngine engine)
    {
        try
        {
            await foreach (var message in engine.Events.ReadAllAsync().ConfigureAwait(false))
            {
                try
                {
                    Route(engine, message);
                }
                catch (Exception e) when (e is InvalidOperationException or FormatException)
                {
                    // A field of the wrong type: this one event is dropped. Taking it for the worker's exit would start a
                    // second worker on the same files while the first still runs (L2).
                    _log?.Invoke($"Torrent worker: an event it sent could not be read and was skipped: {e.Message}");
                }
            }
        }
        catch (Exception e)
        {
            _log?.Invoke($"Torrent worker: reading its events failed: {e.Message}");
        }

        await OnExitedAsync(engine).ConfigureAwait(false);
    }

    /// <summary>Hands one event from the worker to the session it is about, or notes it here.</summary>
    private void Route(ITorrentEngine engine, JsonObject message)
    {
        var name = message["event"]?.GetValue<string>();
        if (name == "progress")
        {
            if (message["torrents"] is not JsonArray rows) return;
            foreach (var row in rows)
            {
                if (row is JsonObject torrent && torrent["id"]?.GetValue<string>() is { } rowId && _sessions.TryGetValue(rowId, out var target))
                {
                    var copy = torrent.DeepClone().AsObject();
                    copy["event"] = "progress";
                    target.Post(copy);
                }
            }

            return;
        }

        if (name == "stats")
        {
            var current = ReferenceEquals(_engine, engine);
            if ((message["listenPort"] ?? message["mappedPort"]) is JsonValue portValue && portValue.TryGetValue<int>(out var port) && current)
                _listening.TrySetResult(port);
            if (current) NoteStats(message);
            return;
        }

        if (name == "error" && message["id"] is null && message["kind"]?.GetValue<string>() == "listen" && ReferenceEquals(_engine, engine))
        {
            var why = message["message"]?.GetValue<string>();
            lock (_networkSync)
            {
                // Each address reports on its own; the port counts as open when any of them could use it.
                if (!_listenSucceeded) _network = _network with { ListenError = string.IsNullOrWhiteSpace(why) ? "unknown error" : why };
            }

            _listenFailed.TrySetResult();

            _log?.Invoke($"Torrent worker: listen: {why}");
            return;
        }

        if (message["id"] is JsonValue idValue && idValue.TryGetValue<string>(out var id))
        {
            if (_sessions.TryGetValue(id, out var session)) session.Post(message);
            return;
        }

        if (name == "error") _log?.Invoke($"Torrent worker: {message["kind"]}: {message["message"]}");
    }

    /// <summary>
    /// A <c>stats</c> event: <c>listenPort</c> (an address opened), <c>mappedPort</c> (the router forwarded it) and, from
    /// the T2 worker on, <c>incoming</c> (a peer connected in). An older worker never sends <c>incoming</c>.
    /// </summary>
    private void NoteStats(JsonObject message)
    {
        lock (_networkSync)
        {
            if (message["listenPort"] is JsonValue listen && listen.TryGetValue<int>(out var port) && port > 0)
            {
                _listenSucceeded = true;
                _network = _network with { Port = port, ListenError = null };
            }

            if (message["mappedPort"] is JsonValue mapped && mapped.TryGetValue<int>(out var external) && external > 0)
                _network = _network with { MappedPort = external };
            if (message["incoming"] is JsonValue incoming && incoming.TryGetValue<bool>(out var seen) && seen)
                _network = _network with { IncomingSeen = true };
        }
    }

    private async Task OnExitedAsync(ITorrentEngine engine)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            // A worker closed on purpose is no longer the current one, and its closer disposes it.
            if (!ReferenceEquals(_engine, engine)) return;
            await RestartLockedAsync(engine).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        await ReleaseExitedAsync(engine).ConfigureAwait(false);
    }

    private async Task ReleaseExitedAsync(ITorrentEngine engine)
    {
        try { await engine.DisposeAsync().ConfigureAwait(false); }
        catch (Exception e) { _log?.Invoke($"Torrent worker: releasing the exited process failed: {e.Message}"); }
    }

    private async Task RestartLockedAsync(ITorrentEngine engine)
    {
        {
            _engine = null;
            var exitCode = (engine as TorrentWorkerProcess)?.ExitCode;
            _log?.Invoke($"Torrent worker exited unexpectedly (code {exitCode?.ToString() ?? "unknown"}).");
            if (_sessions.IsEmpty || _disposed)
            {
                SetState(TorrentWorkerState.Off);
                return;
            }

            var now = _time.GetUtcNow();
            if (_lastRestart is { } last && now - last < _options.RestartWindow)
            {
                FailAllLocked("The torrent engine stopped twice in two minutes.");
                return;
            }

            _lastRestart = now;
            ITorrentEngine restarted;
            try
            {
                restarted = await StartLockedAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                FailAllLocked($"The torrent engine could not be started again: {e.Message}");
                return;
            }

            foreach (var session in _sessions.Values.ToList())
            {
                // A torrent that was being removed is gone with the old worker; it is not added back.
                if (session.Removing)
                {
                    _sessions.TryRemove(session.Id, out _);
                    session.Complete();
                    continue;
                }

                try
                {
                    var arguments = await session.AddArguments(CancellationToken.None).ConfigureAwait(false);
                    arguments["id"] = session.Id;
                    await restarted.SendAsync("add", arguments, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    _sessions.TryRemove(session.Id, out _);
                    session.Post(new JsonObject { ["event"] = WorkerLostEvent, ["message"] = $"The torrent could not be added again: {e.Message}" });
                    session.Complete();
                }
            }

            SetState(_sessions.IsEmpty ? TorrentWorkerState.Draining : TorrentWorkerState.Active);
            if (_sessions.IsEmpty) BeginDrainLocked();
        }
    }

    private void FailAllLocked(string message)
    {
        foreach (var session in _sessions.Values.ToList())
        {
            _sessions.TryRemove(session.Id, out _);
            session.Post(new JsonObject { ["event"] = WorkerLostEvent, ["message"] = message });
            session.Complete();
        }

        SetState(TorrentWorkerState.Off);
    }

    private void SetState(TorrentWorkerState state)
    {
        if (_state == state) return;
        _state = state;
        try { StateChanged?.Invoke(state); }
        catch (Exception e) { _log?.Invoke($"Torrent worker: a state listener failed: {e.Message}"); }
    }

    public async ValueTask DisposeAsync()
    {
        ITorrentEngine? engine;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            _drain?.Cancel();
            engine = _engine;
            _engine = null;
            foreach (var session in _sessions.Values) session.Complete();
            _sessions.Clear();
            SetState(TorrentWorkerState.Off);
        }
        finally
        {
            _gate.Release();
        }

        if (engine is not null) await CloseAsync(engine).ConfigureAwait(false);
    }
}

/// <summary>One torrent attached to the worker: its own events, and commands that name it.</summary>
public sealed class TorrentSession : IAsyncDisposable
{
    private readonly TorrentWorkerSupervisor _owner;
    private readonly Channel<JsonObject> _events = Channel.CreateUnbounded<JsonObject>(new UnboundedChannelOptions { SingleReader = true });

    internal TorrentSession(TorrentWorkerSupervisor owner, string id, Func<CancellationToken, Task<JsonObject>> addArguments)
    {
        _owner = owner;
        Id = id;
        AddArguments = addArguments;
    }

    public string Id { get; }

    internal Func<CancellationToken, Task<JsonObject>> AddArguments { get; }

    /// <summary>A <c>remove</c> was sent: a restart does not add it back and detaching sends none.</summary>
    internal volatile bool Removing;

    /// <summary>T3: the worker said its add queued the file check that went with it (<c>recheckQueued</c>).</summary>
    public bool RecheckQueued { get; internal set; }

    /// <summary>Events naming this torrent (<c>progress</c> carries one row), and <see cref="TorrentWorkerSupervisor.WorkerLostEvent"/>.
    /// Completes when the session is detached.</summary>
    public ChannelReader<JsonObject> Events => _events.Reader;

    public Task<JsonObject> SendAsync(string command, JsonObject? arguments = null, CancellationToken cancellationToken = default)
    {
        var message = arguments ?? new JsonObject();
        message["id"] = Id;
        return _owner.SendAsync(command, message, cancellationToken);
    }

    internal void Post(JsonObject message) => _events.Writer.TryWrite(message);

    internal void Complete() => _events.Writer.TryComplete();

    public ValueTask DisposeAsync() => new(_owner.DetachAsync(this));
}
