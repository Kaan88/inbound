using DownloadManager.Contracts;
using DownloadManager.Core;

namespace DownloadManager.Runtime;

/// <summary>
/// UI.B7 and UI.R21: limits that apply while downloads run. A lower simultaneous-downloads limit (overall or a
/// queue's) sends the excess back to the queue, newest first, keeping their data; the per-host connection share
/// is the engine's, and this side only shows which jobs are waiting for a connection.
/// </summary>
public sealed partial class DownloadManagerService
{
    public static string WaitingForConnectionText(string host) => $"Waiting for a connection to {host}";

    /// <summary>The waiting host is only worth showing while the job runs; a stale one must not outlive it.</summary>
    private static string? WaitingHostShown(DownloadJob job)
        => job.State is DownloadState.Connecting or DownloadState.Downloading or DownloadState.RetryWaiting
            ? job.WaitingForHost
            : null;

    /// <summary>
    /// Raised by the engine from inside its limiter's dispatch, perhaps while a cancel holds <c>_sync</c> on
    /// another thread, so the work moves to the pool. It reads the limiter's current answer, which makes notices
    /// that arrive late or out of order harmless: the last one to run sees the latest state.
    /// </summary>
    private void OnHostWaitChanged(Guid jobId)
        => ThreadPool.UnsafeQueueUserWorkItem(static state => state.service.RefreshWaitingForHost(state.jobId),
            (service: this, jobId), preferLocal: false);

    private void RefreshWaitingForHost(Guid jobId)
    {
        JobDto? dto;
        lock (_sync)
        {
            if (!_jobs.TryGetValue(jobId, out var runtime))
            {
                return;
            }

            var running = runtime.Starting || runtime.Runner is { IsCompleted: false };
            var host = running ? _engine.WaitingForHost(jobId) : null;
            if (string.Equals(host, runtime.Job.WaitingForHost, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            runtime.Job.WaitingForHost = host;
            runtime.Job.Touch();
            dto = Map(runtime.Job);
        }

        JobChanged?.Invoke(this, new JobChangeEventArgs(dto, "progress"));
    }

    /// <summary>Requeues what the current limits leave over, then saves and announces each one.</summary>
    private async Task RequeueExcessAsync(CancellationToken cancellationToken)
    {
        List<JobRuntime> requeued;
        lock (_sync)
        {
            requeued = EnforceConcurrencyLimitsLocked();
        }

        foreach (var runtime in requeued)
        {
            await PersistAsync(runtime, cancellationToken).ConfigureAwait(false);
            Raise(runtime, "queued");
        }
    }

    /// <summary>
    /// Picks the running downloads above the overall limit or their queue's, in the reverse of the order
    /// <see cref="Schedule"/> starts them (lowest priority, then latest in the queue's file order, then newest),
    /// and stops each as a pause would, except that it ends in Queued with its data and no completion actions.
    /// A job that can't stop without losing work is left to finish above the limit, and counts as part of the
    /// excess it is: the owner chose "newest first", so an older one is not stopped in its place.
    /// </summary>
    private List<JobRuntime> EnforceConcurrencyLimitsLocked()
    {
        var requeued = new List<JobRuntime>();
        if (!_initialized || _shuttingDown)
        {
            return requeued;
        }

        var running = _jobs.Values
            .Where(r => (r.Starting || r.Runner is { IsCompleted: false }) && !r.PauseRequested && !r.CancelRequested)
            .ToList();
        var queueDefaults = _settings.Queues.FirstOrDefault(q => q.Id == _settings.DefaultQueueId) ?? _settings.Queues[0];
        int QueueKey(JobRuntime r) => r.Job.IsQueued ? (_settings.Queues.FirstOrDefault(q => q.Id == r.Job.QueueId) ?? queueDefaults).Id : -1;
        int QueueMax(JobRuntime r) => r.Job.IsQueued
            ? (_settings.Queues.FirstOrDefault(q => q.Id == r.Job.QueueId) ?? queueDefaults).MaxConcurrentDownloads
            : _settings.MaxConcurrentDownloads;

        var overall = running.Count - _settings.MaxConcurrentDownloads;
        var perQueue = running.GroupBy(QueueKey).ToDictionary(group => group.Key, group => group.Count());
        var fileOrder = FileOrderLocked();
        foreach (var runtime in running
                     .OrderBy(r => r.Job.Priority)
                     .ThenByDescending(r => fileOrder.GetValueOrDefault((r.Job.QueueId, r.Job.Id), int.MaxValue))
                     .ThenByDescending(r => r.Job.CreatedAt))
        {
            var key = QueueKey(runtime);
            if (overall <= 0 && perQueue[key] <= QueueMax(runtime))
            {
                continue;
            }

            overall--;
            perQueue[key]--;
            if (WhyNotPreemptable(runtime) is { } reason)
            {
                _log?.Invoke($"Job {runtime.Job.Id} stays above the lowered limit: {reason}");
                continue;
            }

            runtime.Preempted = true;
            runtime.PauseRequested = true;
            runtime.ResumeWhenStopped = true;
            runtime.Job.State = DownloadState.Queued;
            runtime.Job.BytesPerSecond = null;
            runtime.Job.EstimatedRemaining = null;
            runtime.Job.ActiveConnections = 0;
            runtime.Job.WaitingForHost = null;
            runtime.Job.Touch();
            runtime.Cts?.Cancel();
            requeued.Add(runtime);
        }

        return requeued;
    }

    /// <summary>Null when stopping now keeps everything downloaded so far; otherwise why not.</summary>
    private static string? WhyNotPreemptable(JobRuntime runtime)
    {
        var job = runtime.Job;
        var phase = runtime.LastPhase ?? string.Empty;
        if (job.State == DownloadState.Verifying || phase.StartsWith("Verifying", StringComparison.Ordinal) ||
            phase.StartsWith("Finaliz", StringComparison.Ordinal))
        {
            return "it is verifying or finishing its file.";
        }

        if (phase.StartsWith("Muxing", StringComparison.Ordinal))
        {
            return "it is joining its audio and video.";
        }

        if (job.ReceivedBytes == 0)
        {
            return null;
        }

        // Media keeps a checkpoint per fragment, whatever the server says about ranges.
        if (IsStreamManifest(job, phase))
        {
            return null;
        }

        return job.ResumeSupported switch
        {
            true => null,
            false => "its server can't resume, so stopping would lose what it has.",
            // An FTP server shows whether it takes REST only when a transfer resumes; until then stopping might
            // lose what the job has, so a first FTP run is left alone like any other unknown.
            null => "its server hasn't said whether it can resume.",
        };
    }

    /// <summary>
    /// The engine's own tests for a playlist (the URL, or the hand-off's content type), plus the phase it reports,
    /// which also covers a playlist found by its content type behind a URL without the extension.
    /// </summary>
    private static bool IsStreamManifest(DownloadJob job, string phase)
    {
        if (phase.StartsWith("Downloading HLS", StringComparison.Ordinal) ||
            phase.StartsWith("Downloading DASH", StringComparison.Ordinal))
        {
            return true;
        }

        if (!Uri.TryCreate(job.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return false;
        }

        if (uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) ||
            uri.AbsolutePath.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return StreamManifestHint.HintsStream(job.HandoffContentType, job.Description);
    }

    private Dictionary<(int QueueId, Guid JobId), int> FileOrderLocked()
        => _settings.Queues
            .SelectMany(queue => (queue.JobOrder ?? new()).Select((id, index) => (QueueId: queue.Id, JobId: id, Index: index)))
            .GroupBy(item => (item.QueueId, item.JobId))
            .ToDictionary(group => group.Key, group => group.First().Index);
}
