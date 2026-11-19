namespace DownloadManager.Runtime;

public sealed partial class DownloadManagerService
{
    // Test synchronization only: Paused is published before the engine finishes its durable stop checkpoint.
    // Waiting for the real transfer to release its slot avoids guessing from two equal file reads.
    internal async Task WaitForTransferIdleForTestingAsync(Guid id, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
                if (!_jobs.TryGetValue(id, out var runtime) || !runtime.Starting && runtime.Runner is not { IsCompleted: false }) return;
            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        }
    }
}
