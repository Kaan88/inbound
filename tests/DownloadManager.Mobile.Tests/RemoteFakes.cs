using System.Threading.Channels;
using DownloadManager.Contracts;
using DownloadManager.Mobile.Remote;

namespace DownloadManager.Mobile.Tests;

/// <summary>A fake WebSocket to a server: the test pushes what the server sends; what the client sends is kept and may be answered.</summary>
internal sealed class FakeSocket : IIpcConnection
{
    private readonly Channel<IpcMessage?> _incoming = Channel.CreateUnbounded<IpcMessage?>();

    public List<IpcMessage> Sent { get; } = new();

    /// <summary>Answers a request (the reply is pushed as the server would); null sends nothing back.</summary>
    public Func<IpcMessage, IpcMessage?>? Answer { get; set; }

    public bool Disposed { get; private set; }

    public bool IsConnected => !Disposed;

    public bool SnapshotFollowsHello => true;

    public Task SendAsync(IpcMessage message, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        lock (Sent) Sent.Add(message);
        if (Answer?.Invoke(message) is { } reply) Push(reply);
        return Task.CompletedTask;
    }

    public async Task<IpcMessage?> ReadAsync(CancellationToken cancellationToken = default)
    {
        try { return await _incoming.Reader.ReadAsync(cancellationToken); }
        catch (ChannelClosedException) { return null; }
    }

    public void Push(IpcMessage message) => _incoming.Writer.TryWrite(message);

    public void Hello(int version = Protocol.Version, string kind = HostKinds.Server) =>
        Push(IpcMessage.Create(MessageTypes.ServerHello, new HostHelloDto { ProtocolVersion = version, HostKind = kind }) with { V = version });

    public void Snapshot(params JobDto[] jobs) =>
        Push(IpcMessage.Create(MessageTypes.JobsSnapshot, new SnapshotDto { Jobs = jobs.ToList(), Settings = new SettingsDto { DefaultDownloadDirectory = "/srv/dl" } }));

    public void Event(JobDto job, string type) =>
        Push(IpcMessage.Create(MessageTypes.JobEvent, new JobEventDto { Job = job, EventType = type }));

    /// <summary>The server closes the connection.</summary>
    public void Close() => _incoming.Writer.TryComplete();

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        _incoming.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}

/// <summary>A server that hands out fake sockets in order; each connect takes the next plan (a socket, or an exception).</summary>
internal sealed class FakeServer
{
    private readonly Queue<Func<FakeSocket>> _plans = new();
    private readonly SemaphoreSlim _connected = new(0);

    public List<FakeSocket> Sockets { get; } = new();

    public List<ClientHelloDto> Hellos { get; } = new();

    public int Attempts { get; private set; }

    public List<IpcMessage> OneShots { get; } = new();

    /// <summary>The next connect gets a socket that already holds the hello and a snapshot of <paramref name="jobs"/>.</summary>
    public FakeServer Accepts(params JobDto[] jobs)
    {
        _plans.Enqueue(() =>
        {
            var socket = new FakeSocket();
            socket.Hello();
            socket.Snapshot(jobs);
            return socket;
        });
        return this;
    }

    public FakeServer Accepts(Action<FakeSocket> prepare)
    {
        _plans.Enqueue(() =>
        {
            var socket = new FakeSocket();
            prepare(socket);
            return socket;
        });
        return this;
    }

    public FakeServer Fails(Exception error)
    {
        _plans.Enqueue(() => throw error);
        return this;
    }

    public Task<IIpcConnection> ConnectAsync(ServerTarget target, ClientHelloDto hello, CancellationToken cancellationToken)
    {
        lock (_plans)
        {
            Attempts++;
            Hellos.Add(hello);
            if (!_plans.TryDequeue(out var plan)) return Task.FromException<IIpcConnection>(new RemoteServerException("Couldn't reach the server."));
            try
            {
                var socket = plan();
                Sockets.Add(socket);
                _connected.Release();
                return Task.FromResult<IIpcConnection>(socket);
            }
            catch (Exception e)
            {
                return Task.FromException<IIpcConnection>(e);
            }
        }
    }

    public Task<IpcMessage> SendOnceAsync(ServerTarget target, IpcMessage request, CancellationToken cancellationToken)
    {
        lock (OneShots) OneShots.Add(request);
        return Task.FromResult(IpcMessage.Create(MessageTypes.RemoteClassifyResult, new { }, request.Id));
    }

    /// <summary>Waits until the <paramref name="count"/>-th socket was handed out.</summary>
    public async Task<FakeSocket> SocketAsync(int count)
    {
        while (true)
        {
            lock (_plans)
                if (Sockets.Count >= count) return Sockets[count - 1];
            if (!await _connected.WaitAsync(TimeSpan.FromSeconds(10))) throw new TimeoutException($"Socket {count} never connected.");
        }
    }

    public static ServerTarget Target(string token = "ibt_test-token-not-real") =>
        ServerTarget.Remote("0123456789abcdef0123456789abcdef", "Home server", new Uri("http://127.0.0.1:8080/"), token, plainHttpConfirmed: false);

    public RemoteHostConnection Connection(TimeSpan? connectWait = null, Action<Exception>? onError = null) =>
        new(Target(), ConnectAsync, SendOnceAsync, new ReconnectBackoff(TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(80)), connectWait, onError);
}

/// <summary>Frames as a screen receives them, thread-safe, with a way to wait for one.</summary>
internal sealed class FrameLog
{
    private readonly List<IpcMessage> _frames = new();

    public void Add(IpcMessage m)
    {
        lock (_frames) _frames.Add(m);
    }

    public IpcMessage[] All
    {
        get { lock (_frames) return _frames.ToArray(); }
    }

    public (string Type, string? Event, string? Name)[] Summary => All.Select(m => m.Type == MessageTypes.JobEvent
        ? (m.Type, (string?)m.GetPayload<JobEventDto>().EventType, (string?)m.GetPayload<JobEventDto>().Job.FileName)
        : (m.Type, null, null)).ToArray();

    public async Task WaitAsync(Func<IpcMessage[], bool> done, string what)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!done(All))
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("Never got: " + what + ". Got: " + string.Join(", ", Summary));
            await Task.Delay(10);
        }
    }
}

internal static class Until
{
    public static async Task TrueAsync(Func<bool> condition, string what)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("Never: " + what);
            await Task.Delay(10);
        }
    }
}

internal sealed class MemoryHostList : IHostListStore
{
    public HostListData Saved { get; private set; } = new();
    public int Saves { get; private set; }
    public bool FailSaves { get; set; }

    public HostListData Load() => Clone(Saved);

    public void Save(HostListData data)
    {
        if (FailSaves) throw new IOException("disk full");
        Saves++;
        Saved = Clone(data);
    }

    private static HostListData Clone(HostListData d) => new()
    {
        Version = d.Version,
        CurrentId = d.CurrentId,
        Servers = d.Servers.Select(s => new HostEntry
        {
            Id = s.Id, Name = s.Name, Address = s.Address, PlainHttpConfirmed = s.PlainHttpConfirmed, HostKind = s.HostKind,
            TokenId = s.TokenId, PairedAt = s.PairedAt, LastSeenAt = s.LastSeenAt,
        }).ToList(),
    };
}

internal sealed class MemorySecrets : ISecretStore
{
    public Dictionary<string, string> Values { get; } = new();
    public bool FailWrites { get; set; }

    public string? Read(string name) => Values.GetValueOrDefault(name);

    public void Write(string name, string secret)
    {
        if (FailWrites) throw new InvalidOperationException("keystore unavailable");
        Values[name] = secret;
    }

    public void Delete(string name) => Values.Remove(name);
}
