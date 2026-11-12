using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace DownloadManager.Torrents;

/// <summary>
/// The native worker as a child process. Its stdout carries frames only; its stderr is passed to <c>log</c> line by
/// line. Closing stdin asks it to save and exit; <see cref="Kill"/> ends it at once (used by crash tests and as the
/// last step of a shutdown that did not finish).
/// </summary>
public sealed class TorrentWorkerProcess : ITorrentEngine
{
    private readonly Process _process;
    private readonly Stream _stdin;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonObject>> _pending = new();
    private readonly Channel<JsonObject> _events = Channel.CreateUnbounded<JsonObject>(new UnboundedChannelOptions { SingleReader = true });
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _seq;

    private TorrentWorkerProcess(Process process, Action<string>? log)
    {
        _process = process;
        _stdin = process.StandardInput.BaseStream;
        _ = Task.Run(ReadLoopAsync);
        _ = Task.Run(async () =>
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                // Keep draining even if the logger fails: a full stderr pipe would block the worker.
                try { log?.Invoke(line); }
                catch (Exception) { }
            }
        });
    }

    public int ProcessId => _process.Id;

    public ChannelReader<JsonObject> Events => _events.Reader;

    public Task Exited => _exited.Task;

    public int? ExitCode => _process.HasExited ? _process.ExitCode : null;

    public static TorrentWorkerProcess Start(string executable, string? dhtStatePath = null,
        IReadOnlyDictionary<string, string>? environment = null, Action<string>? log = null)
    {
        var info = new ProcessStartInfo(executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (dhtStatePath is not null)
        {
            info.ArgumentList.Add("--dht-state");
            info.ArgumentList.Add(dhtStatePath);
        }
        if (environment is not null)
            foreach (var (key, value) in environment) info.Environment[key] = value;
        var process = Process.Start(info) ?? throw new InvalidOperationException("The torrent worker did not start.");
        return new TorrentWorkerProcess(process, log);
    }

    public async Task<JsonObject> SendAsync(string command, JsonObject? arguments = null, CancellationToken cancellationToken = default)
    {
        var seq = Interlocked.Increment(ref _seq);
        var message = arguments?.DeepClone().AsObject() ?? new JsonObject();
        message["cmd"] = command;
        message["seq"] = seq;
        var reply = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_exited.Task.IsCompleted) throw new TorrentEngineExitedException("The torrent worker has exited.");
            _pending[seq] = reply;
            // Never cancelled half way: a frame cut short would make the worker read the next one as garbage.
            await TorrentFrames.WriteAsync(_stdin, message, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            _pending.TryRemove(seq, out _);
            throw new TorrentEngineExitedException($"The torrent worker is gone: {e.Message}");
        }
        finally
        {
            _writeLock.Release();
        }
        using var registration = cancellationToken.Register(() =>
        {
            _pending.TryRemove(seq, out _);
            reply.TrySetCanceled(cancellationToken);
        });
        // The read loop fails every waiter it knows of when the worker exits; one added in that same moment is
        // caught here instead.
        if (await Task.WhenAny(reply.Task, _exited.Task).ConfigureAwait(false) != reply.Task && !reply.Task.IsCompleted)
        {
            _pending.TryRemove(seq, out _);
            throw new TorrentEngineExitedException("The torrent worker exited before it answered.");
        }
        var result = await reply.Task.ConfigureAwait(false);
        if (result["ok"]?.GetValue<bool>() != true)
            throw new TorrentCommandException(command, result["message"]?.GetValue<string>() ?? "no reason given");
        return result;
    }

    /// <summary>Closes stdin, which asks the worker to save resume data and exit, and waits for it.</summary>
    public async Task<bool> CloseAsync(TimeSpan timeout)
    {
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try { _stdin.Close(); }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { }
        finally { _writeLock.Release(); }
        var done = await Task.WhenAny(_exited.Task, Task.Delay(timeout)).ConfigureAwait(false) == _exited.Task;
        if (!done) Kill();
        return done;
    }

    public void Kill()
    {
        try { _process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_exited.Task.IsCompleted) await CloseAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        // The read loop still uses the process until it has finished.
        await _exited.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        _process.Dispose();
    }

    private async Task ReadLoopAsync()
    {
        var stdout = _process.StandardOutput.BaseStream;
        try
        {
            while (true)
            {
                var frame = await TorrentFrames.ReadAsync(stdout, CancellationToken.None).ConfigureAwait(false);
                if (frame is null) break;
                if (frame["event"] is JsonValue ev && ev.TryGetValue<string>(out var name) && name == "reply"
                    && frame["seq"] is JsonValue s && s.TryGetValue<long>(out var seq) && _pending.TryRemove(seq, out var waiter))
                {
                    waiter.TrySetResult(frame);
                    continue;
                }
                _events.Writer.TryWrite(frame);
            }
        }
        catch (Exception e)
        {
            // A worker that breaks the protocol can't be trusted to carry on; end it so nothing waits on it.
            _events.Writer.TryWrite(new JsonObject { ["event"] = "error", ["kind"] = "protocol", ["message"] = e.Message });
            Kill();
        }
        finally
        {
            foreach (var key in _pending.Keys)
                if (_pending.TryRemove(key, out var waiter))
                    waiter.TrySetException(new TorrentEngineExitedException("The torrent worker exited before it answered."));
            try { await _process.WaitForExitAsync().ConfigureAwait(false); }
            catch (Exception) { }
            foreach (var key in _pending.Keys)
                if (_pending.TryRemove(key, out var waiter))
                    waiter.TrySetException(new TorrentEngineExitedException("The torrent worker exited before it answered."));
            _events.Writer.TryComplete();
            _exited.TrySetResult();
        }
    }
}
