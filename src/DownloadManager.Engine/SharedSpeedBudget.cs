

namespace DownloadManager.Core;

/// <summary>
/// The speed cap all transfers share (UI.B5). One instance given to several engines makes the cap cover them together,
/// so HTTP and FTP downloads draw on one budget (PERF.6). Zero means no cap.
/// </summary>
public sealed class SharedSpeedBudget
{
    private readonly TransferRateLimiter _limiter;
    private long _bytesPerSecond;

    public SharedSpeedBudget() => _limiter = new TransferRateLimiter(() => Volatile.Read(ref _bytesPerSecond));

    public long BytesPerSecond => Volatile.Read(ref _bytesPerSecond);

    /// <summary>Null or zero takes the cap away. A change reaches transfers already running.</summary>
    public void Update(long? bytesPerSecond) => Volatile.Write(ref _bytesPerSecond, bytesPerSecond is > 0 ? bytesPerSecond.Value : 0);

    internal Task WaitAsync(int bytes, CancellationToken cancellationToken) => _limiter.WaitAsync(bytes, cancellationToken);
}
