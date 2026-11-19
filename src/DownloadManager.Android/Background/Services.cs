using Android.App;
using Android.App.Job;
using Android.Content;
using Android.OS;
using Android.Util;
using DownloadManager.Mobile;

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
        if (CrashGuardStore.Tripped)
        {
            // Repeated crashes during downloads: no session, no Host. The notification says so and offers Resume; the persisted job is dropped.
            Log.Warn(InboundApplication.Tag, "session job: crash guard tripped, not running; downloads stay paused until the user resumes");
            Notifications.Notify(this, Notifications.GuardId, Notifications.PausedAfterErrors(this));
            ((JobScheduler)GetSystemService(JobSchedulerService)!).Cancel(p.JobId);
            return false;
        }

        var keep = InboundBackground.KeepAlive as UidtKeepAlive;
        if (keep is null) return false;
        var execution = keep.Attach(this, p);
        // The notification first: a user-initiated job must have one when it starts.
        SetNotification(p, Notifications.OngoingId, Notifications.Resuming(this), JobEndNotificationPolicy.Remove);
        var app = ApplicationContext!;
        _ = Task.Run(async () =>
        {
            try
            {
                // May be a cold process (after a reboot): this starts the Host, which resumes what its reconcile resumes.
                await InboundApplication.GetHostAsync(app).ConfigureAwait(false);
                new Handler(Looper.MainLooper!).Post(() =>
                {
                    if (keep.IsCurrent(execution)) InboundBackground.SessionStarted();
                });
            }
            catch (System.Exception ex)
            {
                // The Host could not start: nothing to keep the process for.
                Log.Error(InboundApplication.Tag, "session job: Host did not start: " + ex.Message);
                keep.StopIfCurrent(execution);
            }
        });
        return true;
    }

    public override bool OnStopJob(JobParameters? p)
    {
        var reason = p?.StopReason;
        // This run is over whatever the reason: it is detached (a cancel that raced the start would otherwise leave a dead run attached and
        // Running true). Detach acts on this run only, so the new run of our own reschedule stays attached.
        (InboundBackground.KeepAlive as UidtKeepAlive)?.Detach(this, p);
        InboundBackground.RefreshTransferPermission();
        // Our own reschedule or cancel: nothing to reschedule (a reschedule's new run follows at once).
        if (reason == (int)StopReason.CancelledByApp)
        {
            Log.Info(InboundApplication.Tag, "session job stop: cancelled by app");
            InboundBackground.Evaluate();
            return false;
        }

        // Any other reason is the system's stop: nothing is marked, the Host keeps its state, and the job is rescheduled by the system.
        Log.Info(InboundApplication.Tag, $"session job stop: reason={reason} pid={Process.MyPid()}");
        return true;
    }
}

/// <summary>API 31 to 33 (and the API 30 test build): the dataSync foreground service.</summary>
[Service(Name = "app.inbound.android.KeepAliveService", Exported = false, ForegroundServiceType = Android.Content.PM.ForegroundService.TypeDataSync)]
internal sealed class KeepAliveService : Service
{
    private int _gen;

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        // Immediately: the system gives a few seconds to call StartForeground, even when this start is no longer wanted.
        StartForeground(Notifications.OngoingId, Notifications.Resuming(this), Android.Content.PM.ForegroundService.TypeDataSync);
        _gen = intent?.GetIntExtra(ForegroundKeepAlive.ExtraGeneration, 0) ?? 0;
        if (InboundBackground.KeepAlive is not ForegroundKeepAlive keep || !keep.OnServiceStarted(_gen))
        {
            // Stopped between the request and now, or an older start: this instance ends itself (only when no newer command is queued).
            StopForeground(StopForegroundFlags.Remove);
            StopSelf(startId);
            return StartCommandResult.NotSticky;
        }

        InboundBackground.SessionStarted();
        // Not sticky: a killed service is not brought back unseen; the app starts it again when it is opened.
        return StartCommandResult.NotSticky;
    }

    public override void OnDestroy()
    {
        (InboundBackground.KeepAlive as ForegroundKeepAlive)?.Gone(_gen);
        InboundBackground.RefreshTransferPermission();
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
                var host = await InboundApplication.GetHostAsync(app).ConfigureAwait(false);
                // Resume restarts these, and only these. Written before the pause, so a process killed right after it loses
                // nothing; what the pause did not pause after all is taken out again (Fable finding 15).
                var candidates = ResumeList.PauseCandidates(host.Service.GetSnapshot().Jobs);
                ResumeSet.Add(app, candidates, "pause all");
                var paused = await host.Service.PauseAllAsync().ConfigureAwait(false);
                ResumeSet.Add(app, paused, "pause all");
                ResumeSet.Remove(app, candidates.Except(paused));
                await host.PauseRemoteFilesAsync().ConfigureAwait(false);
                // After the pause: the news is "paused" only once it has happened.
                InboundBackground.MarkPauseAll();
                Log.Info(InboundApplication.Tag, "pause all (notification)");
            }
            catch (System.Exception ex) { Log.Error(InboundApplication.Tag, "pause all: " + ex); }
            finally { pending?.Finish(); }
        });
    }
}

/// <summary>"Use data" in the Waiting for Wi-Fi notification: lifts the rule until the network is next not metered, and starts the keep-alive.</summary>
[BroadcastReceiver(Name = "app.inbound.android.UseDataReceiver", Exported = false)]
internal sealed class UseDataReceiver : BroadcastReceiver
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
                var host = await InboundApplication.GetHostAsync(app).ConfigureAwait(false);
                host.Service.AllowMeteredUntilUnmetered();
                Log.Info(InboundApplication.Tag, "use data (notification)");
                // The tap is what allows a session to start with the app not visible (a notification action is exempt from the background rules).
                InboundBackground.StartSessionFromNotification();
            }
            catch (System.Exception ex) { Log.Error(InboundApplication.Tag, "use data: " + ex); }
            finally { pending?.Finish(); }
        });
    }
}

/// <summary>"Stop" in the ongoing notification's warning: stops the downloads that can't resume (they start over on a network that isn't metered).</summary>
[BroadcastReceiver(Name = "app.inbound.android.StopNoResumeReceiver", Exported = false)]
internal sealed class StopNoResumeReceiver : BroadcastReceiver
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
                var host = await InboundApplication.GetHostAsync(app).ConfigureAwait(false);
                var n = await host.Service.StopNonResumableOnMeteredAsync().ConfigureAwait(false);
                Log.Info(InboundApplication.Tag, "stop downloads that can't resume (notification): " + n);
            }
            catch (System.Exception ex) { Log.Error(InboundApplication.Tag, "stop no-resume: " + ex); }
            finally { pending?.Finish(); }
        });
    }
}
