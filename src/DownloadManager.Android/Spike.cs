#if INBOUND_TEST_BUILD
// TEST BUILD ONLY (step 2 spike S-1, android-step2-design.md section 6). Answers what Android's pages leave open, on a
// real phone, before the background design is built: what a swipe from Recents, a reboot (persisted job), a Task Manager
// Stop and a notification action do to a user-initiated data transfer (UIDT) job. Nothing here downloads; the job only
// runs and logs a heartbeat. Driven by adb (see SpikeHooks.Run); every line is logged under the tag "InboundSpike".
using Android.App;
using Android.App.Job;
using Android.Content;
using Android.OS;
using Android.Util;

[assembly: UsesPermission("android.permission.RUN_USER_INITIATED_JOBS")]
[assembly: UsesPermission("android.permission.RECEIVE_BOOT_COMPLETED")]
[assembly: UsesPermission("android.permission.POST_NOTIFICATIONS")]

namespace Inbound.App;

internal static class SpikeHooks
{
    public const string Tag = "InboundSpike";
    public const string Channel = "spike";

    /// <summary>
    ///   am start -n app.inbound.android/.MainActivity --es spike schedule      schedules UIDT job 7001 (persisted, any network)
    ///   am start -n app.inbound.android/.MainActivity --es spike notify        posts a notification with two actions:
    ///                                                                          "By broadcast" and "By activity", each scheduling job 7002
    ///   am start -n app.inbound.android/.MainActivity --es spike cancel        cancels both jobs
    ///   am start -n app.inbound.android/.MainActivity --es spike exitinfo      logs the last exit reasons of the process
    /// </summary>
    public static void Run(Context context, Intent? intent)
    {
        var what = intent?.GetStringExtra("spike");
        if (string.IsNullOrEmpty(what)) return;
        intent!.RemoveExtra("spike");
        try
        {
            switch (what)
            {
                case "schedule": Schedule(context, 7001, "activity visible"); break;
                case "schedule2": Schedule(context, 7002, "notification action, activity"); break;
                case "notify": Notify(context); break;
                case "cancel":
                    var js = (JobScheduler)context.GetSystemService(Context.JobSchedulerService)!;
                    js.Cancel(7001); js.Cancel(7002);
                    Log.Info(Tag, "cancelled 7001 7002");
                    break;
                case "exitinfo": ExitInfo(context); break;
            }
        }
        catch (Exception ex) { Log.Error(Tag, what + ": " + ex); }
    }

    public static void Schedule(Context context, int id, string from)
    {
        var js = (JobScheduler)context.GetSystemService(Context.JobSchedulerService)!;
        var info = new JobInfo.Builder(id, new ComponentName(context, Java.Lang.Class.FromType(typeof(SpikeJobService))))
            .SetUserInitiated(true)!
            .SetRequiredNetworkType(NetworkType.Any)!
            .SetPersisted(true)!
            .SetEstimatedNetworkBytes(1_000_000_000, 0)!
            .Build()!;
        var result = js.Schedule(info);
        Log.Info(Tag, $"schedule id={id} from={from} result={(result == JobScheduler.ResultSuccess ? "success" : "failure")}");
    }

    public static void EnsureChannel(Context context)
    {
        var nm = (NotificationManager)context.GetSystemService(Context.NotificationService)!;
        nm.CreateNotificationChannel(new NotificationChannel(Channel, "Spike", NotificationImportance.Low));
    }

    private static void Notify(Context context)
    {
        EnsureChannel(context);
        var byBroadcast = PendingIntent.GetBroadcast(context, 1,
            new Intent(context, typeof(SpikeReceiver)).SetAction("app.inbound.android.SPIKE_SCHEDULE"),
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        var byActivity = PendingIntent.GetActivity(context, 2,
            new Intent(context, typeof(MainActivity)).PutExtra("spike", "schedule2").AddFlags(ActivityFlags.NewTask),
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        var n = new Notification.Builder(context, Channel)
            .SetSmallIcon(Android.Resource.Drawable.StatSysDownload)
            .SetContentTitle("Spike: schedule from a notification")
            .AddAction(new Notification.Action.Builder(null, "By broadcast", byBroadcast).Build())
            .AddAction(new Notification.Action.Builder(null, "By activity", byActivity).Build())
            .Build();
        ((NotificationManager)context.GetSystemService(Context.NotificationService)!).Notify(7100, n);
        Log.Info(Tag, "notify posted");
    }

    private static void ExitInfo(Context context)
    {
        var am = (ActivityManager)context.GetSystemService(Context.ActivityService)!;
        foreach (var e in am.GetHistoricalProcessExitReasons(context.PackageName, 0, 5) ?? [])
            Log.Info(Tag, $"exit reason={e.Reason} sub={e.Description} time={e.Timestamp}");
    }
}

[BroadcastReceiver(Name = "app.inbound.android.SpikeReceiver", Exported = false)]
internal sealed class SpikeReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null) return;
        try { SpikeHooks.Schedule(context, 7002, "notification action, broadcast"); }
        catch (Exception ex) { Log.Error(SpikeHooks.Tag, "receiver: " + ex); }
    }
}

[BroadcastReceiver(Name = "app.inbound.android.SpikeBootReceiver", Exported = true)]
[IntentFilter([Intent.ActionBootCompleted])]
internal sealed class SpikeBootReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null) return;
        var js = (JobScheduler)context.GetSystemService(Context.JobSchedulerService)!;
        var pending = string.Join(",", (js.AllPendingJobs ?? []).Select(j => j.Id));
        Log.Info(SpikeHooks.Tag, "boot received; pending jobs=" + pending);
        try { SpikeHooks.Schedule(context, 7003, "boot receiver"); }
        catch (Exception ex) { Log.Error(SpikeHooks.Tag, "boot schedule: " + ex); }
    }
}

[Service(Name = "app.inbound.android.SpikeJobService", Permission = "android.permission.BIND_JOB_SERVICE", Exported = false)]
internal sealed class SpikeJobService : JobService
{
    private CancellationTokenSource? _beat;

    public override bool OnStartJob(JobParameters? p)
    {
        if (p is null) return false;
        Log.Info(SpikeHooks.Tag, $"start id={p.JobId} pid={Process.MyPid()}");
        SpikeHooks.EnsureChannel(this);
        var n = new Notification.Builder(this, SpikeHooks.Channel)
            .SetSmallIcon(Android.Resource.Drawable.StatSysDownload)
            .SetContentTitle("Spike job " + p.JobId)
            .SetOngoing(true)
            .Build();
        SetNotification(p, 7000 + p.JobId % 100, n, JobEndNotificationPolicy.Remove);
        var cts = _beat = new CancellationTokenSource();
        var id = p.JobId;
        _ = Task.Run(async () =>
        {
            for (var i = 1; !cts.IsCancellationRequested; i++)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(15), cts.Token); } catch (System.OperationCanceledException) { break; }
                Log.Info(SpikeHooks.Tag, $"beat id={id} n={i} pid={Process.MyPid()}");
            }
        });
        return true;
    }

    public override bool OnStopJob(JobParameters? p)
    {
        Log.Info(SpikeHooks.Tag, $"stop id={p?.JobId} reason={p?.StopReason} pid={Process.MyPid()}");
        _beat?.Cancel();
        return true;
    }
}
#endif
