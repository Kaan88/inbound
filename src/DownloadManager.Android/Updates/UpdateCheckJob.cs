using Android.App;
using Android.App.Job;

namespace Inbound.App.Updates;

/// <summary>Ordinary network-constrained daily work. Android may defer it for Doze/battery; it never launches UI.</summary>
[Service(Name = "app.inbound.android.UpdateCheckJob", Permission = "android.permission.BIND_JOB_SERVICE", Exported = false)]
internal sealed class UpdateCheckJob : JobService
{
    private CancellationTokenSource? _run;
    private readonly object _gate = new();
    public override bool OnStartJob(JobParameters? parameters)
    {
        if (parameters is null) return false;
        var cancellation = new CancellationTokenSource();
        lock (_gate) { _run?.Cancel(); _run = cancellation; }
        _ = Task.Run(async () =>
        {
            try { await AndroidUpdates.Service.CheckAsync(cancellation.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            finally
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_run, cancellation))
                    {
                        _run = null;
                        if (!cancellation.IsCancellationRequested) JobFinished(parameters, false);
                    }
                    cancellation.Dispose();
                }
            }
        });
        return true;
    }

    public override bool OnStopJob(JobParameters? parameters)
    {
        lock (_gate) { _run?.Cancel(); _run = null; }
        return true;
    }
}
