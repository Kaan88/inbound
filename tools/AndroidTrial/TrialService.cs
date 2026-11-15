using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Util;

namespace AndroidTrial;

/// <summary>
/// mode=service: the in-process Host (AndroidHost) inside a foreground service of type dataSync, started by the Activity.
/// The host lives as long as the process; a start with phase/url runs <see cref="HostScenario"/> on it once.
/// </summary>
[Service(Name = "app.inbound.trial.TrialService", Exported = false, ForegroundServiceType = ForegroundService.TypeDataSync)]
public sealed class TrialService : Service
{
    private const string ChannelId = "inbound-trial";
    private static AndroidHost? _host;
    private static readonly object Gate = new();
    private static string? _logFile;

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var manager = (NotificationManager)GetSystemService(NotificationService)!;
        manager.CreateNotificationChannel(new NotificationChannel(ChannelId, "Downloads", NotificationImportance.Low));
        var notification = new Notification.Builder(this, ChannelId)
            .SetContentTitle("Inbound trial")
            .SetContentText("Host running")
            .SetSmallIcon(global::Android.Resource.Drawable.StatSysDownload)
            .SetOngoing(true)
            .Build();
        try
        {
            StartForeground(1, notification, ForegroundService.TypeDataSync);
        }
        catch (Exception ex)
        {
            Say("StartForeground FAILED " + ex);
            StopSelf();
            return StartCommandResult.NotSticky;
        }

        var ext = HostScenario.ExtDir(this);
        var runId = intent?.GetStringExtra("runId") ?? "svc";
        lock (Gate) _logFile = Path.Combine(ext, $"service-{runId}.log");
        Say($"OnStartCommand startId={startId} pid={Android.OS.Process.MyPid()} sinceProcessStart={SystemClock.ElapsedRealtime() - Android.OS.Process.StartElapsedRealtime} ms");
        var request = new HostRequest(runId, intent?.GetStringExtra("phase") ?? "add", intent?.GetStringExtra("url"),
            intent?.GetStringExtra("sha256"), intent?.GetIntExtra("segments", 1) ?? 1, intent?.GetBooleanExtra("resumeIfPaused", false) ?? false,
            intent?.GetBooleanExtra("idle", false) ?? false);
        if (_host is null)
        {
            _ = Task.Run(async () =>
            {
                try { _host = await HostScenario.RunAsync(this, request, Say, CancellationToken.None, keepHost: true).ConfigureAwait(false); }
                catch (Exception ex) { Say("scenario FATAL " + ex); }
            });
        }
        else Say("host already running in this process; ignoring the new request");

        return StartCommandResult.NotSticky;
    }

    // API 35: dataSync reached its 6 h in 24 h. The service must stop within a few seconds.
    public override void OnTimeout(int startId, [Android.Runtime.GeneratedEnum] ForegroundService fgsType)
    {
        Say($"OnTimeout startId={startId} type={fgsType}");
        base.OnTimeout(startId, fgsType);
        StopSelf();
    }

    public override void OnDestroy()
    {
        Say("OnDestroy");
        base.OnDestroy();
    }

    private static void Say(string line)
    {
        Log.Info("InboundTrial", line);
        string? file;
        lock (Gate) file = _logFile;
        if (file is null) return;
        try { lock (Gate) File.AppendAllText(file, $"{DateTimeOffset.UtcNow:HH:mm:ss.fff} {line}\n"); } catch (Exception) { }
    }
}
