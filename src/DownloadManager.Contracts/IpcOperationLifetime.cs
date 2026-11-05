namespace DownloadManager.Contracts;

/// <summary>
/// Gives a long operation cancellation on a dedicated client's EOF, while retaining
/// a pipelined message for the caller's next dispatch instead of consuming it.
/// The caller remains responsible for awaiting nextMessage after this operation.
/// </summary>
public static class IpcOperationLifetime
{
    public static async Task RunAsync(Func<CancellationToken, Task> operation,
        Task<IpcMessage?> nextMessage, CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            var running = operation(lifetime.Token);
            if (await Task.WhenAny(running, nextMessage).ConfigureAwait(false) == nextMessage)
            {
                try
                {
                    if (await nextMessage.ConfigureAwait(false) is null) lifetime.Cancel();
                }
                catch
                {
                    lifetime.Cancel();
                    try { await running.ConfigureAwait(false); }
                    catch { /* Observe the cancelled operation before propagating the read failure. */ }
                    throw;
                }
            }
            await running.ConfigureAwait(false);
        }
        finally
        {
            // A failed operation can make the caller dispose the pipe before its
            // lookahead is awaited. Observe that eventual fault without blocking.
            _ = nextMessage.ContinueWith(completed => { _ = completed.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
}
