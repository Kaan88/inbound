using DownloadManager.Core;

namespace DownloadManager.Engine.Tests;

/// <summary>PERF.9: the reader the transfer loops read response bodies through.</summary>
public sealed class TransferReaderTests
{
    /// <summary>
    /// A connection that fails after some bytes came in (a reset): those bytes are handed over first, so they are written
    /// and count as progress, and the failure comes with the next read. Holding them back lost them, and a segment that
    /// only ever got a little before each reset never progressed (seen on Linux in
    /// EngineRecoveryTests.ASegmentThatGetsBytesOnItsLastAllowedAttemptStillRetries).
    /// </summary>
    [Fact]
    public async Task BytesBeforeAFailedReadAreHandedOverAndTheFailureComesNext()
    {
        var stream = new ScriptedStream([new byte[] { 1, 2, 3 }, new byte[] { 4, 5 }], new IOException("Connection reset by peer."));
        using var reader = new TransferReader(stream, 64, TimeSpan.FromSeconds(10), CancellationToken.None);

        var first = await reader.FillAsync(32);
        Assert.Equal(5, first);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, reader.Buffer[..first].ToArray());
        var failure = await Assert.ThrowsAsync<IOException>(async () => await reader.FillAsync(32));
        Assert.Contains("reset", failure.Message);
    }

    [Fact]
    public async Task AFailureWithNothingReadComesAtOnce()
    {
        var stream = new ScriptedStream([], new IOException("Connection reset by peer."));
        using var reader = new TransferReader(stream, 64, TimeSpan.FromSeconds(10), CancellationToken.None);
        await Assert.ThrowsAsync<IOException>(async () => await reader.FillAsync(32));
    }

    [Fact]
    public async Task ItFillsUpToWhatWasAskedAndStopsAtTheEnd()
    {
        var stream = new ScriptedStream([new byte[] { 1, 2 }, new byte[] { 3, 4 }, new byte[] { 5, 6 }], null);
        using var reader = new TransferReader(stream, 64, TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.Equal(4, await reader.FillAsync(3));
        Assert.Equal(2, await reader.FillAsync(3));
        Assert.Equal(0, await reader.FillAsync(3));
    }

    [Fact]
    public async Task AStalledReadIsATimeout()
    {
        var stream = new ScriptedStream([], null, stall: true);
        using var reader = new TransferReader(stream, 64, TimeSpan.FromMilliseconds(200), CancellationToken.None);
        await Assert.ThrowsAsync<TimeoutException>(async () => await reader.FillAsync(1));
    }

    /// <summary>Gives its pieces one read at a time, then fails, ends, or (stall) waits until the read is cancelled.</summary>
    private sealed class ScriptedStream(byte[][] pieces, Exception? failure, bool stall = false) : Stream
    {
        private int _next;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_next < pieces.Length)
            {
                var piece = pieces[_next++];
                piece.CopyTo(buffer);
                return piece.Length;
            }

            if (stall) await Task.Delay(Timeout.Infinite, cancellationToken);
            if (failure is not null) throw failure;
            return 0;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
