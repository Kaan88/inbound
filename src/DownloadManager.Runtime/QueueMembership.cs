using DownloadManager.Contracts;
using DownloadManager.Core;

namespace DownloadManager.Runtime;

public sealed partial class DownloadManagerService
{
    public async Task<JobActionResponse> DetachFromQueueAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _settingsUpdateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _persistGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                JobRuntime? runtime;
                DownloadJob before;
                lock (_sync)
                {
                    if (!_jobs.TryGetValue(id, out runtime)) return new JobActionResponse { Error = "Job not found." };
                    before = runtime.Job.Clone();
                    runtime.AdmissionSuspended = true;
                    runtime.Job.IsQueued = false;
                    runtime.Job.QueueId = _settings.DefaultQueueId;
                    runtime.Job.QueueName = "None";
                    runtime.Job.QueueProcessing = false;
                    runtime.Job.QueueRetryAt = null;
                    if (runtime.Job.State == DownloadState.Queued) runtime.Job.StartMode = StartMode.Later;
                    runtime.Job.Touch();
                }
                try { await PersistAsync(runtime, cancellationToken, requireSuccess: true, gateHeld: true).ConfigureAwait(false); }
                catch
                {
                    lock (_sync)
                    {
                        runtime.Job.IsQueued = before.IsQueued; runtime.Job.QueueId = before.QueueId;
                        runtime.Job.QueueName = before.QueueName; runtime.Job.QueueProcessing = before.QueueProcessing;
                        runtime.Job.QueueRetryAt = before.QueueRetryAt; runtime.Job.StartMode = before.StartMode;
                    }
                    throw;
                }
                finally { lock (_sync) runtime.AdmissionSuspended = false; }
                Raise(runtime, "updated");
                Schedule();
                return new JobActionResponse { Ok = true, Job = Map(runtime.Job) };
            }
            finally { _persistGate.Release(); }
        }
        finally { _settingsUpdateGate.Release(); }
    }
}
