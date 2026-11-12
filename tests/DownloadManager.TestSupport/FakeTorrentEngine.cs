using System.Text.Json.Nodes;
using System.Threading.Channels;

using DownloadManager.Torrents;

namespace DownloadManager.TestSupport;

/// <summary>
/// An in-memory engine that speaks the same contract as the native worker, for Host tests that must not touch the
/// network. It is not a torrent engine: an added torrent gets one file of <see cref="FakeFileSize"/> bytes named after
/// its id, reports progress in four steps when <see cref="Advance"/> is called, then completes. It writes no files.
/// </summary>
public sealed class FakeTorrentEngine : ITorrentEngine
{
    public const long FakeFileSize = 4096;

    private readonly Channel<JsonObject> _events = Channel.CreateUnbounded<JsonObject>();
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<string, (long Done, bool Paused, bool Seed)> _torrents = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly List<JsonObject> _adds = new();

    public FakeTorrentEngine()
    {
        Emit(new JsonObject { ["event"] = "hello", ["protocol"] = 1, ["worker"] = "fake", ["libtorrent"] = "none" });
    }

    /// <summary>Files per added torrent: one (named after the id) by default; more sit in a folder named after the id.</summary>
    public int FilesPerTorrent { get; init; } = 1;

    /// <summary>The single file's name instead of the id's (tests that need a name such as an archive's).</summary>
    public string? SingleFileName { get; init; }

    /// <summary>Every <c>add</c> it took, in order (tests).</summary>
    public IReadOnlyList<JsonObject> Adds
    {
        get { lock (_gate) return _adds.ToArray(); }
    }

    private (long Download, long Upload)? _limits;

    /// <summary>The last <c>setLimits</c> it took (bytes/s, 0 = none); null before any.</summary>
    public (long Download, long Upload)? Limits
    {
        get { lock (_gate) return _limits; }
    }

    private readonly List<(string Command, JsonObject Arguments)> _sent = new();

    /// <summary>Every command it took after the add, with its fields, in order (tests).</summary>
    public IReadOnlyList<(string Command, JsonObject Arguments)> Sent
    {
        get { lock (_gate) return _sent.ToArray(); }
    }

    /// <summary>Commands it answers with "unknown command", as a worker older than them would (tests).</summary>
    public IReadOnlySet<string> Lacks { get; init; } = new HashSet<string>();

    /// <summary>Its <c>add</c> ignores <c>recheck</c> and does not answer <c>recheckQueued</c>, as a 0.3 worker (tests).</summary>
    public bool IgnoresAddRecheck { get; init; }

    /// <summary>DHT nodes its <c>stats</c> reports.</summary>
    public long DhtNodes { get; set; } = 42;

    /// <summary>Sends an event as the worker would (tests: errors and the like).</summary>
    public void Inject(JsonObject message) => Emit(message);

    public ChannelReader<JsonObject> Events => _events.Reader;

    public Task Exited => _exited.Task;

    public IReadOnlyCollection<string> Torrents
    {
        get { lock (_gate) return _torrents.Keys.ToArray(); }
    }

    /// <summary>Tests: while this task is not done, an <c>add</c> to seed waits for it before it is taken.</summary>
    public Task? HoldSeedAdds { get; init; }

    public Task<JsonObject> SendAsync(string command, JsonObject? arguments = null, CancellationToken cancellationToken = default)
    {
        if (command == "add" && HoldSeedAdds is { IsCompleted: false } hold && arguments?["seed"]?.GetValue<bool>() == true)
            return AfterHoldAsync(hold, command, arguments, cancellationToken);
        if (_exited.Task.IsCompleted) throw new TorrentEngineExitedException("The fake engine has stopped.");
        arguments ??= new JsonObject();
        string Id() => arguments["id"]?.GetValue<string>() ?? throw new TorrentCommandException(command, "missing id");
        lock (_gate)
        {
            if (Lacks.Contains(command)) throw new TorrentCommandException(command, "unknown command");
            if (command != "add") _sent.Add((command, arguments.DeepClone().AsObject()));
            switch (command)
            {
                case "recheck":
                case "announce":
                case "sequential":
                case "setTorrentLimits":
                    if (!_torrents.ContainsKey(Id())) throw new TorrentCommandException(command, "unknown torrent");
                    break;
                case "details":
                {
                    var id = Id();
                    if (!_torrents.ContainsKey(id)) throw new TorrentCommandException(command, "unknown torrent");
                    return Task.FromResult(new JsonObject
                    {
                        ["event"] = "reply", ["ok"] = true, ["infoHashV1"] = "", ["infoHashV2"] = new string('b', 64), ["private"] = false,
                        ["wasted"] = 16384L, ["failedBytes"] = 16384L, ["hashFailures"] = 1L, ["pieces"] = 4, ["pieceLength"] = FakeFileSize / 4,
                        ["connections"] = 1, ["connectionsLimit"] = 50, ["downloadLimit"] = 0, ["uploadLimit"] = 0, ["sequential"] = false,
                        ["pex"] = true, ["distributedCopies"] = 1.5, ["nextAnnounce"] = 1800L, ["addedAt"] = 0L, ["activeSeconds"] = 3L,
                        ["dhtNodes"] = DhtNodes,
                    });
                }
                case "stats":
                    return Task.FromResult(new JsonObject
                    {
                        ["event"] = "reply", ["ok"] = true, ["listenPort"] = 6881, ["torrents"] = _torrents.Count, ["dhtNodes"] = DhtNodes,
                    });
                case "setPeerLimits":
                case "setProxy":
                    break;
                case "add":
                {
                    var id = Id();
                    if (_torrents.ContainsKey(id)) throw new TorrentCommandException(command, "a torrent with this id is already added");
                    if (string.IsNullOrEmpty(arguments["savePath"]?.GetValue<string>())) throw new TorrentCommandException(command, "missing savePath");
                    // A finalized torrent (added back to seed) has all of its data.
                    var complete = arguments["finalized"]?.GetValue<bool>() == true;
                    _torrents[id] = (complete ? FakeFileSize : 0, arguments["paused"]?.GetValue<bool>() == true, arguments["seed"]?.GetValue<bool>() == true);
                    _adds.Add(arguments.DeepClone().AsObject());
                    // Several files sit in a folder named after the torrent, joined with '\' as libtorrent does on Windows.
                    var files = FilesPerTorrent <= 1
                        ? new JsonArray(new JsonObject { ["index"] = 0, ["path"] = SingleFileName ?? id + ".bin", ["size"] = FakeFileSize })
                        : new JsonArray(Enumerable.Range(0, FilesPerTorrent).Select(i => (JsonNode)new JsonObject
                        {
                            ["index"] = i, ["path"] = $"{id}\\file-{i}.bin", ["size"] = FakeFileSize / FilesPerTorrent,
                        }).ToArray());
                    var metadata = new JsonObject
                    {
                        ["event"] = "metadata", ["id"] = id, ["name"] = id, ["totalSize"] = FakeFileSize, ["pieceLength"] = 16384,
                        ["files"] = files,
                    };
                    // hold: the metadata says so and nothing moves until setFilePriorities, as in the worker.
                    if (arguments["hold"]?.GetValue<bool>() == true)
                    {
                        metadata["held"] = true;
                        _torrents[id] = _torrents[id] with { Paused = true };
                    }
                    Emit(metadata);
                    // A 0.4 worker says it queued the check asked for with the add; a 0.3 one ignored the field.
                    if (arguments["recheck"]?.GetValue<bool>() == true && !IgnoresAddRecheck)
                        return Task.FromResult(new JsonObject { ["event"] = "reply", ["ok"] = true, ["recheckQueued"] = true });
                    break;
                }
                case "pause":
                case "resume":
                {
                    var id = Id();
                    if (!_torrents.TryGetValue(id, out var t)) throw new TorrentCommandException(command, "unknown torrent");
                    _torrents[id] = t with { Paused = command == "pause" };
                    // A graceful pause hands over resume data, as the worker's does.
                    if (command == "pause") Emit(new JsonObject { ["event"] = "resumeBlob", ["id"] = id, ["data"] = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes("fake:" + t.Done)) });
                    break;
                }
                case "finalize":
                    if (!_torrents.ContainsKey(Id())) throw new TorrentCommandException(command, "unknown torrent");
                    break;
                case "setFilePriorities":
                {
                    var id = Id();
                    if (!_torrents.TryGetValue(id, out var t)) throw new TorrentCommandException(command, "unknown torrent");
                    _torrents[id] = t with { Paused = false };
                    break;
                }
                case "remove":
                    if (!_torrents.Remove(Id())) throw new TorrentCommandException(command, "unknown torrent");
                    break;
                case "shutdown":
                    Stop();
                    break;
                case "setListen":
                    Emit(new JsonObject { ["event"] = "stats", ["listenPort"] = 6881 });
                    break;
                case "seed":
                {
                    var id = Id();
                    if (!_torrents.TryGetValue(id, out var t)) throw new TorrentCommandException(command, "unknown torrent");
                    _torrents[id] = t with { Seed = arguments["on"]?.GetValue<bool>() != false };
                    break;
                }
                case "peers":
                    if (!_torrents.ContainsKey(Id())) throw new TorrentCommandException(command, "unknown torrent");
                    return Task.FromResult(new JsonObject
                    {
                        ["event"] = "reply", ["ok"] = true,
                        ["peers"] = new JsonArray(new JsonObject
                        {
                            ["address"] = "127.0.0.2", ["port"] = 6881, ["client"] = "Fake 1.0", ["flags"] = new JsonArray("seed", "outgoing"),
                            ["downRate"] = 10, ["upRate"] = 0, ["downloaded"] = 100L, ["uploaded"] = 0L, ["progress"] = 1.0,
                        }),
                    });
                case "trackers":
                    if (!_torrents.ContainsKey(Id())) throw new TorrentCommandException(command, "unknown torrent");
                    return Task.FromResult(new JsonObject
                    {
                        ["event"] = "reply", ["ok"] = true,
                        ["trackers"] = new JsonArray(new JsonObject
                        {
                            ["url"] = "http://tracker.invalid/announce", ["tier"] = 0, ["status"] = "working", ["message"] = "",
                            ["seeds"] = 3, ["peers"] = 4, ["nextAnnounce"] = 1800L,
                        }),
                    });
                case "pieces":
                {
                    var id = Id();
                    if (!_torrents.TryGetValue(id, out var t)) throw new TorrentCommandException(command, "unknown torrent");
                    var have = (int)(t.Done * 4 / FakeFileSize);
                    return Task.FromResult(new JsonObject
                    {
                        ["event"] = "reply", ["ok"] = true, ["count"] = 4, ["pieceLength"] = FakeFileSize / 4, ["have"] = have,
                        ["ranges"] = have == 0 ? new JsonArray() : new JsonArray(new JsonArray(0, have - 1)),
                    });
                }
                case "setLimits":
                    _limits = (arguments["download"]?.GetValue<long>() ?? 0, arguments["upload"]?.GetValue<long>() ?? 0);
                    break;
                case "setSeedPolicy":
                case "setProgressInterval":
                case "saveResume":
                    break;
                default:
                    throw new TorrentCommandException(command, "unknown command");
            }
        }
        return Task.FromResult(new JsonObject { ["event"] = "reply", ["ok"] = true });
    }

    private async Task<JsonObject> AfterHoldAsync(Task hold, string command, JsonObject arguments, CancellationToken cancellationToken)
    {
        await hold.WaitAsync(cancellationToken).ConfigureAwait(false);
        return await SendAsync(command, arguments, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Moves every running torrent a quarter of the way and reports progress; completes those that finish.</summary>
    public void Advance()
    {
        lock (_gate)
        {
            var rows = new JsonArray();
            var after = new List<JsonObject>();
            foreach (var id in _torrents.Keys.ToArray())
            {
                var t = _torrents[id];
                if (t.Paused || t.Done >= FakeFileSize) continue;
                var done = Math.Min(FakeFileSize, t.Done + FakeFileSize / 4);
                _torrents[id] = t with { Done = done };
                rows.Add(new JsonObject { ["id"] = id, ["state"] = done == FakeFileSize ? "seeding" : "downloading", ["done"] = done, ["wanted"] = FakeFileSize });
                if (done == FakeFileSize)
                {
                    after.Add(new JsonObject { ["event"] = "completed", ["id"] = id });
                    if (!t.Seed)
                    {
                        _torrents[id] = _torrents[id] with { Paused = true };
                        after.Add(new JsonObject { ["event"] = "state", ["id"] = id, ["state"] = "paused" });
                    }
                }
            }
            if (rows.Count > 0) Emit(new JsonObject { ["event"] = "progress", ["torrents"] = rows });
            foreach (var e in after) Emit(e);
        }
    }

    /// <summary>Reports that a torrent has sent <paramref name="uploaded"/> bytes in all (seeding tests).</summary>
    public void Upload(string id, long uploaded)
    {
        lock (_gate)
        {
            if (!_torrents.TryGetValue(id, out var t)) return;
            Emit(new JsonObject
            {
                ["event"] = "progress",
                ["torrents"] = new JsonArray(new JsonObject
                {
                    ["id"] = id, ["state"] = t.Paused ? "paused" : "seeding", ["done"] = t.Done, ["wanted"] = FakeFileSize,
                    ["upRate"] = 1000.0, ["peers"] = 1, ["seeds"] = 0, ["uploaded"] = uploaded,
                }),
            });
        }
    }

    /// <summary>Reports a torrent's state (tests: <c>checking</c>) without moving it.</summary>
    public void State(string id, string state)
    {
        lock (_gate)
        {
            if (!_torrents.TryGetValue(id, out var t)) return;
            Emit(new JsonObject
            {
                ["event"] = "progress",
                ["torrents"] = new JsonArray(new JsonObject { ["id"] = id, ["state"] = state, ["done"] = t.Done, ["wanted"] = FakeFileSize }),
            });
        }
    }

    /// <summary>Reports a torrent's download and upload rates (bytes/s) without moving it (speed limit tests).</summary>
    public void Rates(string id, double downRate, double upRate)
    {
        lock (_gate)
        {
            if (!_torrents.TryGetValue(id, out var t)) return;
            Emit(new JsonObject
            {
                ["event"] = "progress",
                ["torrents"] = new JsonArray(new JsonObject
                {
                    ["id"] = id, ["state"] = t.Paused ? "paused" : t.Done >= FakeFileSize ? "seeding" : "downloading",
                    ["done"] = t.Done, ["wanted"] = FakeFileSize, ["downRate"] = downRate, ["upRate"] = upRate,
                    ["peers"] = 1, ["seeds"] = 1, ["uploaded"] = 0L,
                }),
            });
        }
    }

    /// <summary>Whether the torrent is on the engine and not paused.</summary>
    public bool IsActive(string id)
    {
        lock (_gate) return _torrents.TryGetValue(id, out var t) && !t.Paused;
    }

    /// <summary>Ends the engine as a crash would: no resume data, no <c>bye</c>.</summary>
    public void Crash()
    {
        lock (_gate)
        {
            if (_exited.Task.IsCompleted) return;
            _events.Writer.TryComplete();
            _exited.TrySetResult();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate) Stop();
        return ValueTask.CompletedTask;
    }

    private void Stop()
    {
        if (_exited.Task.IsCompleted) return;
        Emit(new JsonObject { ["event"] = "bye" });
        _events.Writer.TryComplete();
        _exited.TrySetResult();
    }

    private void Emit(JsonObject message) => _events.Writer.TryWrite(message);
}
