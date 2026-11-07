using System.Diagnostics;

namespace DownloadManager.Core;

/// <summary>One budget per transfer, including all of its range workers, or one for all transfers together.</summary>
internal sealed class TransferRateLimiter(Func<long?> readLimit)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _previousLimit;
    private long _lastTimestamp;
    private double _creditBytes;
    private int _maximumBurstBytes;

    public async Task WaitAsync(int bytes, CancellationToken cancellationToken)
    {
        // No cap is the common case: skip the gate, so uncapped transfers never queue behind each other.
        if (readLimit().GetValueOrDefault() <= 0) return;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _maximumBurstBytes = Math.Max(_maximumBurstBytes, bytes);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var limit = readLimit().GetValueOrDefault();
                var now = Stopwatch.GetTimestamp();
                if (limit != _previousLimit || limit <= 0)
                {
                    _previousLimit = limit;
                    _creditBytes = 0;
                    _lastTimestamp = now;
                }
                if (limit <= 0) return;
                // A wait can take far longer than asked: Windows timers tick about every 15 ms. The credit has
                // to hold what that wait earned, or the cap falls well short with small reads, as over TLS.
                _creditBytes = Math.Min(Math.Max(_maximumBurstBytes, limit / 10),
                    _creditBytes + (now - _lastTimestamp) * (double)limit / Stopwatch.Frequency);
                _lastTimestamp = now;
                if (_creditBytes >= bytes)
                {
                    _creditBytes -= bytes;
                    return;
                }
                // Re-read the live setting frequently, so removing a cap does
                // not wait out the old budget. Cancellation also stays prompt.
                await Task.Delay(TimeSpan.FromSeconds(Math.Min((bytes - _creditBytes) / limit, 0.1)), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally { _gate.Release(); }
    }
}
