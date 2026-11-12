using System.Threading.Channels;
using DownloadManager.Contracts;

namespace DownloadManager.Runtime.Ipc;

/// <summary>
/// A session's outgoing queue and the one loop that writes it. A transport supplies <see cref="WriteFrameAsync"/>,
/// which must throw <see cref="InvalidDataException"/> before writing anything when a frame is over
/// <see cref="Protocol.MaxFrameBytes"/> (as <see cref="IpcFraming.WriteAsync"/> does).
/// </summary>
public abstract class QueuedClientSession : IClientSession, IAsyncDisposable
{
    public const int QueueCapacity = 4096;

    private readonly SemaphoreSlim _directWrite = new(1, 1);
    private readonly Channel<IpcMessage> _channel;
    private readonly IHostLog _log;
    private int _completed;

    protected QueuedClientSession(IHostLog log)
    {
        _log = log;
        // Full is never waited for: Enqueue uses TryWrite, and a full queue closes the session (L19).
        _channel = Channel.CreateBounded<IpcMessage>(new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>Whether the queue filled up and the session was closed for it.</summary>
    public bool Overflowed { get; private set; }

    /// <summary>
    /// Queues a frame. A client so far behind that the queue is full is closed instead of losing frames silently: a
    /// dropped reply would leave a request waiting for good and a dropped event a stale list, while a client that
    /// connects again gets a whole new snapshot.
    /// </summary>
    public bool Enqueue(IpcMessage message)
    {
        if (_channel.Writer.TryWrite(message)) return true;
        if (Volatile.Read(ref _completed) == 0)
        {
            Overflowed = true;
            _log.Warn($"A client fell {QueueCapacity} messages behind, so its connection was closed; it gets a new snapshot when it connects again.");
            Close();
        }

        return false;
    }

    public async Task SendDirectAsync(IpcMessage message, CancellationToken cancellationToken)
    {
        // One frame at a time on the wire: the writer loop and a direct send would otherwise interleave bytes.
        await _directWrite.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteFrameAsync(message, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _directWrite.Release();
        }
    }

    /// <summary>Writes one whole frame to the transport.</summary>
    protected abstract Task WriteFrameAsync(IpcMessage message, CancellationToken cancellationToken);

    /// <summary>No more frames are taken. The writer still sends what is already queued, then ends.</summary>
    public void Complete()
    {
        Volatile.Write(ref _completed, 1);
        _channel.Writer.TryComplete();
    }

    /// <summary>
    /// Takes no more frames. A transport that can also stop reading overrides this and calls the base; without that its
    /// reader ends when the client goes away.
    /// </summary>
    public virtual void Close() => Complete();

    /// <summary>Sends the queue in order until <see cref="Complete"/>, cancellation or the client going away.</summary>
    public async Task RunWriterAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var message in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await SendDirectAsync(message, cancellationToken).ConfigureAwait(false);
                }
                catch (InvalidDataException)
                {
                    // Too large for a frame, and nothing of it was sent. Everything after it still has to get through.
                    _log.Warn($"IPC message '{message.Type}' was too large to send.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // expected
        }
        catch (IOException)
        {
            // client went away
        }
    }

    public virtual ValueTask DisposeAsync()
    {
        _directWrite.Dispose();
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}
