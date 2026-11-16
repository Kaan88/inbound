namespace DownloadManager.Runtime;

/// <summary>
/// MAC.6: Options' "Pause downloads on a phone hotspot, in Low Data Mode or in Low Power Mode". While the hold is on,
/// the overall limit counts as 0: running downloads go back to the queue with their data, as a lowered limit sends
/// them (a server that can't resume is left to finish rather than lose what it has), and nothing new starts, from a
/// queue, a schedule or a start at launch. When it ends they start again in their usual order. Nothing is marked
/// paused by the user and nothing is remembered on disk, so a restart or a lost helper can't leave them stopped.
/// </summary>
public sealed partial class DownloadManagerService
{
    public bool SavingHold
    {
        get { lock (_sync) return _savingHold; }
    }

    public async Task SetSavingHoldAsync(bool hold, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (_savingHold == hold) return;
            _savingHold = hold;
        }

        _log?.Invoke(hold ? "Saving data or power: downloads wait in the queue." : "Saving ended: downloads start again.");
        if (hold) await RequeueExcessAsync(cancellationToken).ConfigureAwait(false);
        else Schedule();
    }
}
