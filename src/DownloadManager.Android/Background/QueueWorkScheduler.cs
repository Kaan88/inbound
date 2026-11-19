using Android.App;
using Android.App.Job;
using Android.Content;
using Android.OS;
using Android.Util;
using DownloadManager.Contracts;
using Inbound.App.Host;

namespace Inbound.App.Background;

/// <summary>Ordinary persisted Android jobs: automatic queue starts are best effort under Doze and system quotas.</summary>
internal static class QueueWorkScheduler
{
    internal const int JobId = 4100;
    private static readonly object Gate = new();
    private static readonly Handler Main = new(Looper.MainLooper!);
    private static Context? _app;
    private static AndroidHost? _host;
    private static QueueWorkJob? _service;
    private static JobParameters? _parameters;
    private static JobRunIdentity? _runIdentity;
    private static object? _execution;
    private static bool _ready, _posted;
    private static string? _scheduledKey;
    private static DateTimeOffset _notBefore;

    internal static bool Running { get { lock (Gate) return _service is not null; } }

    internal static void OnHostStarted(Context app, AndroidHost host)
    {
        _app = app; _host = host;
        host.Service.SettingsChanged += (_, _) => RequestArm();
        host.Service.JobChanged += (_, change) => { if (change.ChangeType != "progress") RequestArm(); };
        host.Activity.Changed += () => Main.Post(UpdateRun);
        RequestArm();
    }

    internal static void RequestArm()
    {
        lock (Gate) { if (_posted || _app is null || _host is null) return; _posted = true; }
        Main.Post(() => { lock (Gate) _posted = false; Arm(); });
    }

    private static void Arm()
    {
        if (_app is not { } app || _host is not { } host || Running) return;
        try
        {
            var now = DateTimeOffset.UtcNow;
            var plan = DownloadManager.Mobile.QueueWakePlan.Next(host.Service.GetSnapshot(), host.Service.NextSchedulerWake,
                now, _notBefore, !InboundBackground.Visible && !InboundBackground.KeepAliveRunning, CrashGuardStore.Tripped);
            var scheduler = (JobScheduler)app.GetSystemService(Context.JobSchedulerService)!;
            if (plan is null) { scheduler.Cancel(JobId); _scheduledKey = null; return; }
            var wake = plan.At; var charging = plan.Charging; var unmetered = plan.Unmetered;
            // A key rounded to minutes prevents unchanged progress/state notices from rescheduling the same job continually.
            var key = $"{wake.ToUnixTimeSeconds() / 60}:{charging}:{unmetered}:{plan.OverrideConstraintsAt?.ToUnixTimeSeconds()}";
            if (key == _scheduledKey && scheduler.GetPendingJob(JobId) is not null) return;
            var builder = new JobInfo.Builder(JobId, new ComponentName(app, Java.Lang.Class.FromType(typeof(QueueWorkJob))))
                .SetExtras(JobRunIdentity.NewExtras())!.SetPersisted(true)!.SetMinimumLatency(Math.Max(1000, (long)(wake - now).TotalMilliseconds))!
                .SetRequiredNetworkType(unmetered ? NetworkType.Unmetered : NetworkType.Any)!
                .SetRequiresCharging(charging)!;
            if (plan.OverrideConstraintsAt is { } deadline)
                builder.SetOverrideDeadline(Math.Max(1000, (long)(deadline - now).TotalMilliseconds));
            var info = builder.Build()!;
            if (scheduler.Schedule(info) == JobScheduler.ResultSuccess) _scheduledKey = key;
            else Log.Warn(InboundApplication.Tag, "queue wake job was refused; opening Inbound retries it");
        }
        catch (Exception ex) { Log.Warn(InboundApplication.Tag, "queue wake scheduling: " + ex.GetType().Name); }
    }

    internal static bool Start(QueueWorkJob service, JobParameters parameters)
    {
        if (CrashGuardStore.Tripped) return false;
        var execution = new object();
        lock (Gate)
        {
            if (_service is not null) return false;
            _service = service; _parameters = parameters; _runIdentity = JobRunIdentity.From(parameters); _execution = execution; _ready = false; _scheduledKey = null;
            CrashGuardStore.SessionBegan(ExecutionLeaseKind.QueueWork);
        }
        Log.Info(InboundApplication.Tag, "queue job started");
        Notifications.Notify(service, Notifications.OngoingId, Notifications.Resuming(service));
        _ = Task.Run(async () =>
        {
            try
            {
                var host = await InboundApplication.GetHostAsync(service.ApplicationContext!).ConfigureAwait(false);
                // A stop while the Host initialized revokes this run; never restore its lease afterward.
                if (!IsCurrent(service, execution)) return;
                InboundBackground.RefreshTransferPermission();
                NetworkMonitor.AppVisible(); QueuePowerMonitor.AppVisible();
                await host.Service.EvaluateSchedulesAsync(DateTimeOffset.Now).ConfigureAwait(false);
                Main.Post(() => { if (!IsCurrent(service, execution)) return; _ready = true; UpdateRun(); });
            }
            catch (Exception ex)
            {
                Log.Warn(InboundApplication.Tag, "queue wake start: " + ex.GetType().Name);
                Main.Post(() => Finish(service, parameters, execution));
            }
        });
        return true;
    }

    private static bool IsCurrent(QueueWorkJob service, object execution)
    {
        lock (Gate) return _service?.Equals(service) == true && ReferenceEquals(_execution, execution);
    }

    private static void UpdateRun()
    {
        QueueWorkJob? service; JobParameters? parameters; object? execution;
        lock (Gate) { if (!_ready) return; service = _service; parameters = _parameters; execution = _execution; }
        if (service is null || parameters is null || execution is null || _host is not { } host) return;
        var summary = host.Activity.Summary;
        if (!summary.KeepAlive) { Finish(service, parameters, execution); return; }
        var notification = Notifications.Ongoing(service, DownloadManager.Mobile.NotificationText.Ongoing(summary));
        if (OperatingSystem.IsAndroidVersionAtLeast(34)) service.SetNotification(parameters, Notifications.OngoingId, notification, JobEndNotificationPolicy.Remove);
        else Notifications.Notify(service, Notifications.OngoingId, notification);
    }

    private static void Finish(QueueWorkJob service, JobParameters parameters, object execution)
    {
        if (!Detach(service, parameters, execution)) return;
        service.JobFinished(parameters, false);
        // A held power condition may still be false. Retry via Android at most once per 15 minutes, never a tight wake loop.
        _notBefore = DateTimeOffset.UtcNow.AddMinutes(15);
        if (!InboundBackground.KeepAliveRunning) Notifications.Cancel(service, Notifications.OngoingId);
        InboundBackground.RefreshTransferPermission(); RequestArm();
    }

    internal static bool Stop(QueueWorkJob service, JobParameters? parameters)
    {
        if (parameters is null) return false;
        var detached = Detach(service, parameters);
        var reason = OperatingSystem.IsAndroidVersionAtLeast(31) ? parameters.StopReason : -1;
        Log.Info(InboundApplication.Tag, $"queue job stopped: reason={reason} detached={detached}");
        if (!detached) return false;
        _notBefore = DateTimeOffset.UtcNow.AddMinutes(15);
        // The Runtime lease is revoked synchronously before its checkpoint writes await I/O.
        InboundBackground.RefreshTransferPermission();
        if (!InboundBackground.KeepAliveRunning && !Running) Notifications.Cancel(service, Notifications.OngoingId);
        return !CrashGuardStore.Tripped && reason != (int)StopReason.CancelledByApp;
    }

    private static bool Detach(QueueWorkJob service, JobParameters parameters, object? execution = null)
    {
        lock (Gate)
        {
            if (_service?.Equals(service) != true || _runIdentity != JobRunIdentity.From(parameters) ||
                (execution is not null && !ReferenceEquals(_execution, execution))) return false;
            _service = null; _parameters = null; _runIdentity = null; _execution = null; _ready = false;
            CrashGuardStore.SessionEnded(ExecutionLeaseKind.QueueWork);
        }
        return true;
    }
}

[Service(Name = "app.inbound.android.QueueWorkJob", Permission = "android.permission.BIND_JOB_SERVICE", Exported = false)]
internal sealed class QueueWorkJob : JobService
{
    public override bool OnStartJob(JobParameters? parameters) => parameters is not null && QueueWorkScheduler.Start(this, parameters);
    public override bool OnStopJob(JobParameters? parameters) => QueueWorkScheduler.Stop(this, parameters);
}
