using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace DownloadManager.Torrents.Tests;

/// <summary>Audit 2 L3: a worker that exited before its pump noticed is released when the next add restarts it.</summary>
public sealed class SupervisorReleaseTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task AWorkerThatExitedUnnoticedIsDisposedWhenTheNextAddStartsAnother()
    {
        var engines = new List<ITorrentEngine>();
        var first = new SilentExit();
        await using var supervisor = new TorrentWorkerSupervisor(() =>
        {
            lock (engines)
            {
                ITorrentEngine next = engines.Count == 0 ? first : new FakeTorrentEngine();
                engines.Add(next);
                return next;
            }
        }, new TorrentSupervisorOptions { DrainDelay = TimeSpan.FromSeconds(30), LoopbackOnly = true });

        await using var a = await supervisor.AttachAsync("a", _ => Task.FromResult(Add('a')), CancellationToken.None);
        Assert.Same(first, supervisor.Engine);

        // It exits, and its event stream stays open, so only the next add sees it.
        first.Exit();
        await using var b = await supervisor.AttachAsync("b", _ => Task.FromResult(Add('b')), CancellationToken.None);

        Assert.NotSame(first, supervisor.Engine);
        var until = DateTime.UtcNow + Wait;
        while (!first.Disposed)
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("The exited worker was never released.");
            await Task.Delay(20);
        }
    }

    private static JsonObject Add(char hash) => new() { ["savePath"] = Path.GetTempPath(), ["magnet"] = "magnet:?xt=urn:btih:" + new string(hash, 40) };

    /// <summary>The in-memory engine, but its exit is said only through <see cref="Exited"/>: its events never end.</summary>
    private sealed class SilentExit : ITorrentEngine
    {
        private readonly FakeTorrentEngine _inner = new();
        private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Channel<JsonObject> _events = Channel.CreateUnbounded<JsonObject>();
        private int _disposed;

        public bool Disposed => Volatile.Read(ref _disposed) == 1;

        public ChannelReader<JsonObject> Events => _events.Reader;

        public Task Exited => _exited.Task;

        public void Exit() => _exited.TrySetResult();

        public Task<JsonObject> SendAsync(string command, JsonObject? arguments = null, CancellationToken cancellationToken = default)
            => _exited.Task.IsCompleted
                ? Task.FromException<JsonObject>(new TorrentEngineExitedException("It has exited."))
                : _inner.SendAsync(command, arguments, cancellationToken);

        public async ValueTask DisposeAsync()
        {
            Volatile.Write(ref _disposed, 1);
            _exited.TrySetResult();
            _events.Writer.TryComplete();
            await _inner.DisposeAsync();
        }
    }
}
