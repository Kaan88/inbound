using DownloadManager.Core;

namespace DownloadManager.Runtime;

public sealed partial class DownloadManagerService
{
    private async Task<bool> BeginCompletionActionsAsync(JobRuntime runtime, string changeType)
    {
        lock (_sync)
        {
            if (changeType != "completed" || runtime.Job.CompletionActionsStarted || runtime.CancelRequested ||
                runtime.CancelledForRemoval || !_jobs.ContainsKey(runtime.Job.Id)) return false;
            runtime.Job.CompletionActionsStarted = true;
        }
        try
        {
            // Mark before side effects. Restart never replays an executable or power action.
            await PersistAsync(runtime, CancellationToken.None, requireSuccess: true).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Completion actions skipped because their execution marker could not be saved: {ex.Message}");
            return false;
        }
    }

    private async Task RunJobCompletionActionsAsync(DownloadJob job)
    {
        var options = job.CompletionOptions.Clone();
        if (options.OpenFile && File.Exists(job.FinalFilePath))
        {
            try { await _completionActions.OpenFileAsync(job.FinalFilePath, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { _log?.Invoke($"Per-download open action failed: {ex.Message}"); }
        }
        if (options.SystemAction == "none") return;
        lock (_sync)
        {
            if (!_settings.AllowSystemCompletionActions || _jobs.Values.Any(runtime => runtime.Job.Id != job.Id &&
                (runtime.Job.QueueRetryAt is not null || runtime.Job.State is not (DownloadState.Completed or DownloadState.Failed or DownloadState.Cancelled))))
            {
                _log?.Invoke("Per-download system action skipped: actions are disabled or other downloads are unfinished.");
                return;
            }
        }
        try
        {
            if (options.SystemAction == "exit")
                await _completionActions.RequestExitAsync(CancellationToken.None).ConfigureAwait(false);
            else if (options.SystemAction is "shutdown" or "restart" or "logoff")
                await _completionActions.RequestShutdownAsync(options.SystemAction, options.ForceSystemAction, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) { _log?.Invoke($"Per-download system action failed: {ex.Message}"); }
    }
}
