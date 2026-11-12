using System.Threading.Channels;
using DownloadManager.Contracts;
using DownloadManager.Desktop.WinUI;

namespace DownloadManager.Core.Tests;

/// <summary>
/// Design D9, section 5: the window's connection to another server, with a fake connection in place of the WebSocket.
/// It waits longer between tries, stops after a refused token until it is retargeted (showing or hiding the window
/// doesn't try again), picks up a pairing made again meanwhile, asks for nothing while the window is hidden, and treats
/// the server's <c>host.exiting</c> as a disconnection.
/// </summary>
public sealed class HostConnectionTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private static ServerTarget Server(string token = "dmrc_invented-first") =>
        ServerTarget.Remote(new string('a', 32), "Home", new Uri("http://homeserver:8080/"), token, plainHttpConfirmed: true);

    [Fact]
    public async Task AServerThatIsDownIsTriedLessAndLessOften()
    {
        var fake = new FakeServers { Fail = () => new RemoteServerException("down") };
        await using var connection = fake.Connection(Server(), new ReconnectBackoff(TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(5)));

        connection.Start();
        await Task.Delay(500);

        // 0, 50, 150, 350 ms (then 750). Without the backoff it would be one every 2 s, or a tight loop.
        Assert.InRange(fake.Attempts, 2, 6);
        Assert.False(connection.PairingEnded);
    }

    [Fact]
    public async Task ARefusedTokenStopsTheTriesAndShowingTheWindowDoesntTryAgain()
    {
        var fake = new FakeServers { Fail = () => new RemotePairingEndedException() };
        var said = new List<string?>();
        await using var connection = fake.Connection(Server(), new ReconnectBackoff(TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10)));
        connection.ConnectionChanged += (_, error) => { lock (said) said.Add(error); };

        connection.Start();
        await Until(() => connection.PairingEnded);
        await Task.Delay(200);
        Assert.Equal(1, fake.Attempts);
        lock (said) Assert.Contains(said, error => error?.Contains("Pair it again") == true);

        connection.SetEventsWanted(false);
        connection.SetEventsWanted(true);
        await Task.Delay(200);
        Assert.Equal(1, fake.Attempts);
        Assert.True(connection.PairingEnded);

        // The user paired again (File > Servers): the window retargets, and it connects at once.
        fake.Fail = null;
        connection.Retarget(Server("dmrc_invented-second"));
        await Until(() => connection.IsConnected);
        Assert.False(connection.PairingEnded);
        Assert.Equal("dmrc_invented-second", fake.Tokens.Last());
    }

    [Fact]
    public async Task ATokenRefusedAfterAnotherWindowPairedAgainIsReplacedByTheSavedOne()
    {
        var fake = new FakeServers();
        fake.Fail = () => fake.Tokens.Last() == "dmrc_invented-first" ? new RemotePairingEndedException() : null;
        await using var connection = fake.Connection(Server(), reload: id => Server("dmrc_invented-second"));

        connection.Start();
        await Until(() => connection.IsConnected);

        Assert.Equal(["dmrc_invented-first", "dmrc_invented-second"], fake.Tokens);
        Assert.Equal("dmrc_invented-second", connection.Target.Token);
        Assert.False(connection.PairingEnded);
    }

    [Fact]
    public async Task AHiddenWindowAsksTheServerForNoSnapshotAndNoEvents()
    {
        var fake = new FakeServers();
        await using var connection = fake.Connection(Server());
        connection.SetEventsWanted(false);

        connection.Start();
        await Until(() => connection.IsConnected);
        Assert.False(fake.Hellos[0]!.Snapshot);
        Assert.False(fake.Hellos[0]!.Events);

        connection.SetEventsWanted(true);
        await Until(() => fake.Hellos.Count == 2 && connection.IsConnected);
        Assert.True(fake.Hellos[1]!.Snapshot);
        Assert.True(fake.Hellos[1]!.Events);
    }

    [Fact]
    public async Task AServerExitingIsADisconnectionAndOnlyThisPcsHostQuitsTheApp()
    {
        var fake = new FakeServers { ExitAfterHello = true };
        var exits = 0;
        await using (var remote = fake.Connection(Server()))
        {
            remote.HostExiting += () => Interlocked.Increment(ref exits);
            remote.Start();
            await Until(() => fake.Attempts >= 2);
        }

        Assert.Equal(0, Volatile.Read(ref exits));

        var local = new FakeServers { ExitAfterHello = true };
        await using var here = local.Connection(ServerTarget.Local);
        here.HostExiting += () => Interlocked.Increment(ref exits);
        here.Start();
        await Until(() => Volatile.Read(ref exits) > 0);
    }

    // Audit L8: an unreadable snapshot or update-progress payload used to end the connection (in a loop, as the
    // snapshot comes on every connect); now that one message is skipped and the next ones arrive.
    [Fact]
    public async Task AMessageThisBuildCantReadIsSkippedAndTheConnectionStays()
    {
        var fake = new FakeServers
        {
            Snapshot = IpcMessage.Create(MessageTypes.JobsSnapshot, "not a snapshot"),
            AfterHello = connection =>
            {
                connection.Push(IpcMessage.Create(MessageTypes.UpdateProgress, 42));
                connection.Push(IpcMessage.Create(MessageTypes.GroupsChanged, new List<DownloadGroupDto>()));
            },
        };
        await using var connection = fake.Connection(ServerTarget.Local);
        var groups = 0;
        connection.GroupsChanged += _ => Interlocked.Increment(ref groups);
        // Handlers must be there, or the payloads are never read and nothing is skipped.
        connection.SnapshotReceived += _ => { };
        connection.UpdateProgressChanged += _ => { };
        // Review: each skip is said (the app writes it to its UI trace), naming the message.
        var skipped = new System.Collections.Concurrent.ConcurrentQueue<string>();
        connection.MessageSkipped += skipped.Enqueue;

        connection.Start();
        await Until(() => Volatile.Read(ref groups) == 1);
        await Task.Delay(100);
        Assert.True(connection.IsConnected);
        Assert.Equal(1, fake.Attempts);
        Assert.Contains(skipped, line => line.Contains(MessageTypes.JobsSnapshot, StringComparison.Ordinal));
        Assert.Contains(skipped, line => line.Contains(MessageTypes.UpdateProgress, StringComparison.Ordinal));
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("The expected state never arrived.");
            await Task.Delay(5);
        }
    }

    /// <summary>Hands out fake connections, and records each try: its token and its hello.</summary>
    private sealed class FakeServers
    {
        private readonly Lock _sync = new();
        private int _attempts;

        public Func<Exception?>? Fail { get; set; }

        /// <summary>Each connection sends <c>host.exiting</c> right after the hello, then closes.</summary>
        public bool ExitAfterHello { get; init; }

        /// <summary>Sent in place of an empty snapshot.</summary>
        public IpcMessage? Snapshot { get; init; }

        /// <summary>Pushes more messages after the hello and the snapshot.</summary>
        public Action<FakeConnection>? AfterHello { get; init; }

        public int Attempts => Volatile.Read(ref _attempts);

        public List<string?> Tokens { get; } = [];

        public List<ClientHelloDto?> Hellos { get; } = [];

        public HostConnection Connection(ServerTarget target, ReconnectBackoff? backoff = null, Func<string, ServerTarget?>? reload = null) =>
            new(target, ConnectAsync, backoff ?? new ReconnectBackoff(TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(50)), reload);

        private Task<IIpcConnection> ConnectAsync(ServerTarget target, ClientHelloDto? hello, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _attempts);
            lock (_sync)
            {
                Tokens.Add(target.Token);
                Hellos.Add(hello);
            }

            if (Fail?.Invoke() is { } failure) throw failure;
            var connection = new FakeConnection(hello?.Snapshot ?? true);
            connection.Push(IpcMessage.Create(MessageTypes.ServerHello, new HostHelloDto { ProtocolVersion = Protocol.Version }));
            if (connection.SnapshotFollowsHello) connection.Push(Snapshot ?? IpcMessage.Create(MessageTypes.JobsSnapshot, new SnapshotDto()));
            AfterHello?.Invoke(connection);
            if (ExitAfterHello)
            {
                connection.Push(IpcMessage.Create(MessageTypes.HostExiting, new { }));
                connection.Close();
            }

            return Task.FromResult<IIpcConnection>(connection);
        }
    }

    private sealed class FakeConnection(bool snapshot) : IIpcConnection
    {
        private readonly Channel<IpcMessage> _toClient = Channel.CreateUnbounded<IpcMessage>();

        public bool IsConnected => true;

        public bool SnapshotFollowsHello { get; } = snapshot;

        public void Push(IpcMessage message) => _toClient.Writer.TryWrite(message);

        public void Close() => _toClient.Writer.TryComplete();

        public Task SendAsync(IpcMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async Task<IpcMessage?> ReadAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                return await _toClient.Reader.ReadAsync(cancellationToken);
            }
            catch (ChannelClosedException)
            {
                return null;
            }
        }

        public ValueTask DisposeAsync()
        {
            Close();
            return ValueTask.CompletedTask;
        }
    }
}
