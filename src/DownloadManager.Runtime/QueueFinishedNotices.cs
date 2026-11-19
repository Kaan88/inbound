using DownloadManager.Contracts;
using DownloadManager.Core;

namespace DownloadManager.Runtime;

/// <summary>A transient queue summary. No file or system completion action is requested.</summary>
public sealed class QueueFinishedEventArgs(int queueId, string name, int completed, int failed, int cancelled) : EventArgs
{
    public int QueueId { get; } = queueId;
    public string Name { get; } = name;
    public int Completed { get; } = completed;
    public int Failed { get; } = failed;
    public int Cancelled { get; } = cancelled;
}

public sealed partial class DownloadManagerService
{
    public event EventHandler<QueueFinishedEventArgs>? QueueFinished;
    private readonly HashSet<int> _queueFinishNotified = [];

    private void AnnounceQueueFinished(JobDto changed, string changeType)
    {
        QueueFinishedEventArgs? notice = null;
        lock (_sync)
        {
            if (!changed.IsQueued) return;
            if (changed.State is not ("completed" or "failed" or "cancelled"))
            {
                _queueFinishNotified.Remove(changed.QueueId);
                return;
            }
            // Snapshots/recreation do not replay old notices. Only a real terminal transition can finish a run.
            if (changeType is not ("completed" or "failed" or "cancelled") || _shuttingDown ||
                _queueFinishNotified.Contains(changed.QueueId) ||
                _settings.Queues.FirstOrDefault(q => q.Id == changed.QueueId) is not { IsRunning: true, NotifyWhenFinished: true } queue)
                return;
            var jobs = _jobs.Values.Where(r => r.Job.IsQueued && r.Job.QueueId == queue.Id).Select(r => r.Job).ToArray();
            if (jobs.Length == 0 || jobs.Any(j => j.QueueRetryAt is not null ||
                j.State is not (DownloadState.Completed or DownloadState.Failed or DownloadState.Cancelled))) return;
            _queueFinishNotified.Add(queue.Id);
            notice = new(queue.Id, queue.Name, jobs.Count(j => j.State == DownloadState.Completed),
                jobs.Count(j => j.State == DownloadState.Failed), jobs.Count(j => j.State == DownloadState.Cancelled));
        }
        if (QueueFinished is not { } listeners) return;
        foreach (EventHandler<QueueFinishedEventArgs> listener in listeners.GetInvocationList())
        {
            try { listener(this, notice); }
            catch (Exception ex) { _log?.Invoke($"A queue completion listener failed: {ex.GetType().Name}"); }
        }
    }
}
