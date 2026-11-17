using Android.App;
using Android.App.Job;
using Android.Content;
using Android.OS;
using Android.Util;

[assembly: UsesPermission("android.permission.POST_NOTIFICATIONS")]
[assembly: UsesPermission("android.permission.FOREGROUND_SERVICE")]
[assembly: UsesPermission("android.permission.FOREGROUND_SERVICE_DATA_SYNC")]
[assembly: UsesPermission("android.permission.RUN_USER_INITIATED_JOBS")]
[assembly: UsesPermission("android.permission.RECEIVE_BOOT_COMPLETED")] // required by a persisted job
[assembly: UsesPermission("android.permission.WAKE_LOCK")]

namespace Inbound.App.Background;

/// <summary>
/// API 34 and up: the persisted user-initiated data transfer job that holds the process while downloads can transfer. The system may run it
/// by itself after a reboot, so a cold process is expected here: it posts "Resuming downloads" at once, makes sure the Host is started,
/// and then keeps the job until <c>ActivitySummary.KeepAlive</c> turns false (InboundBackground ends it with JobFinished).
/// </summary>
[Service(Name = "app.inbound.android.SessionJobService", Permission = "android.permission.BIND_JOB_SERVICE", Exported = false)]
internal sealed class SessionJobService : JobService
{
    public override bool OnStartJob(JobParameters? p)
    {
        if (p is null) return false;
        Log.Info(InboundApplication.Tag, $"session job start id={p.JobId} pid={Process.MyPid()}");
        var keep = InboundBackground.KeepAlive as UidtKeepAlive;
        if (keep is null) return false;
        keep.Attach(this, p);
        // The notification first: a user-initiated job must have one when it starts.
        SetNotification(p, Notifications.OngoingId, Notifications.Resuming(this), JobEndNotificationPolicy.Remove);
        var app = ApplicationContext!;
        _ = Task.Run(async () =>
        {
            try
            {
                // May be a cold process (after a reboot): this starts the Host, which resumes what its reconcile resumes.
                await InboundApplication.GetHostAsync(app).ConfigureAwait(false);
                InboundBackground.SessionStarted();
            }
            catch (System.Exception ex)
            {
                // The Host could not start: nothing to keep the process for.
                Log.Error(InboundApplication.Tag, "session job: Host did not start: " + ex.Message);
                keep.Stop();
            }
        });
        return true;
    }

    public override bool OnStopJob(JobParameters? p)
    {
        var reason = p?.StopReason;
        // Our own reschedule (a new Schedule over a running job): the new run follows at once; nothing to do.
        if (reason == (int)StopReason.CancelledByApp)
        {
            Log.Info(InboundApplication.Tag, "session job stop: cancelled by app (own reschedule)");
            return false;
        }

        // Any other reason is the system's stop: nothing is marked, the Host keeps its state, and the job is rescheduled by the system.
        Log.Info(InboundApplication.Tag, $"session job stop: reason={reason} pid={Process.MyPid()}");
        (InboundBackground.KeepAlive as UidtKeepAlive)?.Detach(this);
        return true;
    }
}

/// <summary>API 31 to 33 (and the API 30 test build): the dataSync foreground service.</summary>
[Service(Name = "app.inbound.android.KeepAliveService", Exported = false, ForegroundServiceType = Android.Content.PM.ForegroundService.TypeDataSync)]
internal sealed class KeepAliveService : Service
{
    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        // Immediately: the system gives a few seconds to call StartForeground.
        StartForeground(Notifications.OngoingId, Notifications.Resuming(this), Android.Content.PM.ForegroundService.TypeDataSync);
        if (!InboundBackground.KeepAliveRunning)
        {
            // Stopped between the request and now.
            StopForeground(StopForegroundFlags.Remove);
            StopSelf();
            return StartCommandResult.NotSticky;
        }

        InboundBackground.SessionStarted();
        // Not sticky: a killed service is not brought back unseen; the app starts it again when it is opened.
        return StartCommandResult.NotSticky;
    }

    public override void OnDestroy()
    {
        (InboundBackground.KeepAlive as ForegroundKeepAlive)?.Gone();
        base.OnDestroy();
    }
}

/// <summary>"Pause all" in the ongoing notification: a broadcast, so it works with the app not visible.</summary>
[BroadcastReceiver(Name = "app.inbound.android.PauseAllReceiver", Exported = false)]
internal sealed class PauseAllReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null) return;
        var app = context.ApplicationContext!;
        var pending = GoAsync();
        _ = Task.Run(async () =>
        {
            try
            {
                InboundBackground.MarkPauseAll();
                var host = await InboundApplication.GetHostAsync(app).ConfigureAwait(false);
                await host.Service.PauseAllAsync().ConfigureAwait(false);
                Log.Info(InboundApplication.Tag, "pause all (notification)");
            }
            catch (System.Exception ex) { Log.Error(InboundApplication.Tag, "pause all: " + ex); }
            finally { pending?.Finish(); }
        });
    }
}
