using Android.App;
using Android.Content;
using Android.Util;
using DownloadManager.Persistence;
using DownloadManager.Runtime;

namespace Inbound.App.Background;

/// <summary>
/// After a reboot on API 31 to 33 (and the API 30 test build): says how many downloads will resume once the app is opened, without starting
/// the Host and without writing anything (design section 8, "After a reboot"). It reads the job and settings files read-only
/// (<see cref="ReadOnlyState"/>; the stores write whole files by atomic rename and nothing else runs in this fresh process, so a read sees
/// complete files) and counts with the Runtime's pure start rule. On API 34 and up it does nothing: the persisted session job brings the
/// process back and the Host resumes silently. Best effort: some phones never send BOOT_COMPLETED to the app (seen on a Samsung).
/// Exported because the system's broadcast is delivered to the manifest receiver by its intent filter.
/// </summary>
[BroadcastReceiver(Name = "app.inbound.android.BootReceiver", Exported = true, Enabled = true)]
[IntentFilter([Intent.ActionBootCompleted])]
internal sealed class BootReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null || intent?.Action != Intent.ActionBootCompleted) return;
        if (OperatingSystem.IsAndroidVersionAtLeast(34))
        {
            Log.Info(InboundApplication.Tag, "boot: API 34+, nothing to do here (the persisted session job resumes downloads)");
            return;
        }

        var app = context.ApplicationContext!;
        var pending = GoAsync();
        _ = Task.Run(() =>
        {
            try
            {
                if (CrashGuardStore.Tripped)
                {
                    Log.Info(InboundApplication.Tag, "boot: crash guard tripped, posting the paused-after-errors notice");
                    Notifications.Notify(app, Notifications.GuardId, Notifications.PausedAfterErrors(app));
                    return;
                }

                var root = InboundApplication.StateRoot(app);
                var jobs = ReadOnlyState.ReadJobs(root, m => Log.Info(InboundApplication.Tag, "boot: " + m));
                var settings = ReadOnlyState.ReadSettings(root, m => Log.Info(InboundApplication.Tag, "boot: " + m));
                // The same decision the Host will make at its start; asked, not consumed (only the Host's start consumes the run ids).
                var userStopped = RunTracker.LastRunStoppedByUser(app);
                var count = StartReconcile.CountWouldResume(jobs, settings, userStopped);
                Log.Info(InboundApplication.Tag, $"boot: {jobs.Count} jobs, {count} would resume (user stopped={userStopped})");
                if (count > 0) Notifications.Notify(app, Notifications.BootId, Notifications.BootNotice(app, count));
            }
            catch (System.Exception ex) { Log.Warn(InboundApplication.Tag, "boot: " + ex.Message); }
            finally { pending?.Finish(); }
        });
    }
}
