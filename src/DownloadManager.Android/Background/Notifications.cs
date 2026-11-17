using Android.App;
using Android.Content;
using Android.Util;
using Android.Webkit;
using AndroidX.Core.Content;
using DownloadManager.Mobile;

namespace Inbound.App.Background;

/// <summary>The phone's notifications (mockup A7): the ongoing one, "N downloads paused", and one per finished file.</summary>
internal static class Notifications
{
    public const string OngoingChannel = "downloads";
    public const string FinishedChannel = "finished";
    public const int OngoingId = 1001;
    public const int PausedId = 1002;
    public const int WaitingId = 1003;
    public const int BootId = 1004;
    public const int GuardId = 1005;

    /// <summary>MainActivity extra of the "Resume" action: resume everything, then keep the process alive (the app is visible then).</summary>
    public const string ExtraResumeAll = "resume_all";

    /// <summary>The FileProvider declared in the manifest.</summary>
    public const string FileProviderAuthority = "app.inbound.android.fileprovider";

    public const string PauseAllAction = "app.inbound.android.PAUSE_ALL";
    public const string UseDataAction = "app.inbound.android.USE_DATA";
    public const string StopNoResumeAction = "app.inbound.android.STOP_NO_RESUME";

    private static bool _channels;

    private static NotificationManager Manager(Context c) => (NotificationManager)c.GetSystemService(Context.NotificationService)!;

    public static void EnsureChannels(Context c)
    {
        if (_channels) return;
        var nm = Manager(c);
        nm.CreateNotificationChannel(new NotificationChannel(OngoingChannel, "Downloads in progress", NotificationImportance.Low));
        nm.CreateNotificationChannel(new NotificationChannel(FinishedChannel, "Finished downloads", NotificationImportance.Default));
        _channels = true;
    }

    public static void Notify(Context c, int id, Notification n)
    {
        try { Manager(c).Notify(id, n); }
        catch (System.Exception ex) { Log.Warn(InboundApplication.Tag, "notify " + id + ": " + ex.Message); }
    }

    public static void Cancel(Context c, int id)
    {
        try { Manager(c).Cancel(id); }
        catch (System.Exception ex) { Log.Warn(InboundApplication.Tag, "cancel " + id + ": " + ex.Message); }
    }

    private static PendingIntent OpenApp(Context c, bool resumeAll)
    {
        var i = new Intent(c, typeof(MainActivity));
        i.AddFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop);
        if (resumeAll) i.PutExtra(ExtraResumeAll, true);
        // Different request codes: a PendingIntent is keyed by its intent without extras, and the two must stay apart.
        return PendingIntent.GetActivity(c, resumeAll ? 11 : 10, i, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;
    }

    private static Notification.Builder Builder(Context c, string channel, string title, string? body)
    {
        EnsureChannels(c);
        var b = new Notification.Builder(c, channel);
        // No icon of our own yet: the system's download icon.
        b.SetSmallIcon(Android.Resource.Drawable.StatSysDownload);
        b.SetContentTitle(title);
        if (body is not null) b.SetContentText(body);
        b.SetShowWhen(false);
        return b;
    }

    /// <summary>The ongoing notification: Pause all, and the body opens the app. Used as the foreground notification too.</summary>
    public static Notification Ongoing(Context c, OngoingText t)
    {
        var b = Builder(c, OngoingChannel, t.Title, t.Body);
        b.SetOngoing(true);
        b.SetOnlyAlertOnce(true);
        b.SetCategory(Notification.CategoryProgress);
        if (OperatingSystem.IsAndroidVersionAtLeast(31)) b.SetForegroundServiceBehavior((int)NotificationForegroundService.Immediate);
        b.SetProgress(t.Max, t.Progress, t.Indeterminate);
        b.SetContentIntent(OpenApp(c, false));
        var pauseIntent = new Intent(c, typeof(PauseAllReceiver));
        pauseIntent.SetAction(PauseAllAction);
        var pause = PendingIntent.GetBroadcast(c, 12, pauseIntent, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        b.AddAction(new Notification.Action.Builder(null, "Pause all", pause).Build());
        if (t.Warning is not null)
        {
            // Downloads that can't resume are running on mobile data: say so (the line replaces the file line when collapsed) and offer Stop.
            b.SetContentText(t.Warning);
            b.SetStyle(new Notification.BigTextStyle().BigText(string.IsNullOrEmpty(t.Body) ? t.Warning : t.Body + "\n" + t.Warning));
            b.AddAction(new Notification.Action.Builder(null, "Stop", Broadcast(c, typeof(StopNoResumeReceiver), StopNoResumeAction, 14)).Build());
        }

        return b.Build()!;
    }

    private static PendingIntent Broadcast(Context c, Type receiver, string action, int requestCode)
    {
        var i = new Intent(c, receiver);
        i.SetAction(action);
        return PendingIntent.GetBroadcast(c, requestCode, i, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;
    }

    /// <summary>"Waiting for Wi-Fi" / "N downloads will resume", with Use data (a broadcast, so it works with the app not visible).</summary>
    public static Notification Waiting(Context c, int count)
    {
        var b = Builder(c, OngoingChannel, NotificationText.WaitingTitle, NotificationText.WaitingBody(count));
        b.SetOnlyAlertOnce(true);
        b.SetCategory(Notification.CategoryStatus);
        b.SetContentIntent(OpenApp(c, false));
        b.AddAction(new Notification.Action.Builder(null, "Use data", Broadcast(c, typeof(UseDataReceiver), UseDataAction, 13)).Build());
        return b.Build()!;
    }

    /// <summary>The app's own screen, from a notification action whose receiver could not start a session (best effort: the system may refuse).</summary>
    public static void OpenAppFromBackground(Context c)
    {
        try
        {
            var i = new Intent(c, typeof(MainActivity));
            i.AddFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop);
            c.StartActivity(i);
            Log.Info(InboundApplication.Tag, "session could not be started from the background: opening the app");
        }
        catch (System.Exception ex) { Log.Warn(InboundApplication.Tag, "open app from background: " + ex.Message); }
    }

    /// <summary>The plain notification a session starts with, before the Host has said what is running.</summary>
    public static Notification Resuming(Context c) =>
        Ongoing(c, new OngoingText(NotificationText.Resuming, null, NotificationText.BarMax, 0, true));

    /// <summary>"N downloads paused" with Resume, which opens the app (a direct activity intent) and resumes there.</summary>
    public static Notification Paused(Context c, int count)
    {
        var b = Builder(c, OngoingChannel, NotificationText.Paused(count), null);
        b.SetOnlyAlertOnce(true);
        b.SetAutoCancel(true);
        b.SetContentIntent(OpenApp(c, false));
        b.AddAction(new Notification.Action.Builder(null, "Resume", OpenApp(c, true)).Build());
        return b.Build()!;
    }

    /// <summary>API 31 to 33 after a reboot: "N downloads paused" / "Tap to resume". The tap opens the app (a direct activity intent), which starts the Host; its start resumes them.</summary>
    public static Notification BootNotice(Context c, int count)
    {
        var b = Builder(c, OngoingChannel, NotificationText.Paused(count), NotificationText.BootBody);
        b.SetAutoCancel(true);
        b.SetContentIntent(OpenApp(c, false));
        return b.Build()!;
    }

    /// <summary>The crash-loop guard: downloads stay paused until the user resumes. "Resume" (and the tap) open the app with the resume-all extra.</summary>
    public static Notification PausedAfterErrors(Context c)
    {
        var b = Builder(c, OngoingChannel, NotificationText.PausedAfterErrors, null);
        b.SetOnlyAlertOnce(true);
        b.SetAutoCancel(true);
        b.SetContentIntent(OpenApp(c, true));
        b.AddAction(new Notification.Action.Builder(null, "Resume", OpenApp(c, true)).Build());
        return b.Build()!;
    }

    /// <summary>One notification per finished file; a tap opens the file through the FileProvider.</summary>
    public static void PostFinished(Context c, FinishedJob job)
    {
        var b = Builder(c, FinishedChannel, string.IsNullOrEmpty(job.FileName) ? "Download" : job.FileName, NotificationText.FinishedBody(job));
        b.SetSmallIcon(Android.Resource.Drawable.StatSysDownloadDone);
        b.SetAutoCancel(true);
        b.SetCategory(Notification.CategoryStatus);
        var id = 20000 + (job.Id.GetHashCode() & 0x0FFFFFFF);
        var open = OpenFile(c, job, id);
        if (open is not null) b.SetContentIntent(open);
        Notify(c, id, b.Build()!);
    }

    private static PendingIntent? OpenFile(Context c, FinishedJob job, int requestCode)
    {
        try
        {
            if (string.IsNullOrEmpty(job.FinalPath)) return null;
            var uri = FileProvider.GetUriForFile(c, FileProviderAuthority, new Java.IO.File(job.FinalPath));
            var ext = System.IO.Path.GetExtension(job.FinalPath).TrimStart('.');
            var mime = MimeTypeMap.Singleton?.GetMimeTypeFromExtension(ext.ToLowerInvariant()) ?? "*/*";
            var view = new Intent(Intent.ActionView);
            view.SetDataAndType(uri, mime);
            view.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.NewTask);
            return PendingIntent.GetActivity(c, requestCode, view, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        }
        catch (System.Exception ex)
        {
            // A file outside the folders the provider serves (a folder the user chose): the notification still shows, without a tap target.
            Log.Warn(InboundApplication.Tag, "no open action for " + job.FileName + ": " + ex.Message);
            return null;
        }
    }
}
