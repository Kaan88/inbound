using System.Buffers;

namespace DownloadManager.Core;

/// <summary>
/// PERF.9: reads a response body for the transfer loops. One pooled buffer and one linked cancellation source per
/// response, re-armed for each read, replace the task, timer and 128 KB array that every read and every attempt used to
/// cost. A read that takes longer than the read timeout still fails with <see cref="TimeoutException"/>, as before, and
/// the read is cancelled (a stream that ignores the token is waited for only until the deadline). A cancel from outside stays an
/// <see cref="OperationCanceledException"/>.
/// </summary>
internal sealed class TransferReader : IDisposable
{
    private readonly Stream _stream;
    private readonly TimeSpan _timeout;
    private readonly CancellationToken _outer;
    private readonly byte[] _array;
    private CancellationTokenSource _deadline;
    private bool _abandoned;

    public TransferReader(Stream stream, int bufferSize, TimeSpan timeout, CancellationToken outer)
    {
        _stream = stream;
        _timeout = timeout;
        _outer = outer;
        _array = ArrayPool<byte>.Shared.Rent(bufferSize);
        Buffer = _array.AsMemory(0, bufferSize);
        _deadline = CancellationTokenSource.CreateLinkedTokenSource(outer);
    }

    /// <summary>The bytes the last <see cref="FillAsync"/> read go at the start of this.</summary>
    public Memory<byte> Buffer { get; }

    /// <summary>
    /// Reads until at least <paramref name="atLeast"/> bytes are in <see cref="Buffer"/> (never more than it holds) or
    /// the body ends; 0 means it ended. Each read has the whole read timeout. Asking for 1 reads once, as the loops did.
    /// </summary>
    public async ValueTask<int> FillAsync(int atLeast)
    {
        atLeast = Math.Clamp(atLeast, 1, Buffer.Length);
        var filled = 0;
        while (filled < atLeast)
        {
            var read = await ReadOnceAsync(Buffer[filled..]).ConfigureAwait(false);
            if (read == 0) break;
            filled += read;
        }

        return filled;
    }

    private async ValueTask<int> ReadOnceAsync(Memory<byte> into)
    {
        // A source whose timer fired between reads (it is disarmed after each read, so only a race) can't be reused.
        if (!_deadline.TryReset())
        {
            _deadline.Dispose();
            _deadline = CancellationTokenSource.CreateLinkedTokenSource(_outer);
        }

        _deadline.CancelAfter(_timeout);
        try
        {
            var pending = _stream.ReadAsync(into, _deadline.Token);
            // Most reads at speed are done at once: no task, no wait.
            if (pending.IsCompletedSuccessfully) return pending.Result;
            // A stream that ignores the token must still end at the deadline, so the wait itself is cancellable.
            return await pending.AsTask().WaitAsync(_deadline.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (!_outer.IsCancellationRequested && _deadline.IsCancellationRequested)
        {
            // Our own deadline, however the stream reports a cancelled read (a cancel or an I/O error). The read may
            // still be running and writing into the buffer, so the buffer is not given back to the pool.
            _abandoned = true;
            throw new TimeoutException($"No data arrived for {_timeout.TotalSeconds:0} seconds.", ex);
        }
        catch (OperationCanceledException) when (_outer.IsCancellationRequested)
        {
            // Stopped from outside while the read may still be running: same reason.
            _abandoned = true;
            throw;
        }
        finally
        {
            _deadline.CancelAfter(Timeout.InfiniteTimeSpan);
        }
    }

    public void Dispose()
    {
        _deadline.Dispose();
        // A read left running may still write into the array; it is left to the garbage collector then.
        if (!_abandoned) ArrayPool<byte>.Shared.Return(_array);
    }
}
