using Android.App;
using Android.App.Job;
using Android.Content;
using Android.Util;
using DownloadManager.Mobile;
using Inbound.App.Host;

namespace Inbound.App.Background;

/// <summary>
/// The process's background keep-alive and notifications. It listens to <see cref="ActivityState.Changed"/> (already coalesced to one a
/// second, never a timer of its own) and on each change: starts the keep-alive session while the app is visible and
/// <see cref="ActivitySummary.KeepAlive"/> is true, ends it when that turns false, refreshes the ongoing notification, posts a
/// notification per finished file, and shows "N downloads paused" after Pause all.
/// </summary>
internal static class InboundBackground
{
    private static readonly object Gate = new();
    private static Context? _app;
    private static AndroidHost? _host;
    private static IKeepAlive? _keep;
    private static bool _visible;
    private static bool _pausedAll;
    private static bool _hiddenLogged;
    private static OngoingText? _shown;
    private static bool _pausedShown;
#if INBOUND_TEST_BUILD
    private static bool? _lastKeepLogged;
#endif

    /// <summary>The keep-alive of this API level.</summary>
    public static IKeepAlive KeepAlive => _keep ?? throw new InvalidOperationException("not initialised");

    public static bool KeepAliveRunning => _keep?.Running ?? false;

    public static ActivitySummary Summary => _host?.Activity.Summary ?? ActivitySummary.Idle;

    /// <summary>Application.OnCreate.</summary>
    public static void Init(Context app)
    {
        lock (Gate)
        {
            _app = app;
            // The persisted user-initiated data transfer job from API 34; a dataSync foreground service below that.
            _keep = OperatingSystem.IsAndroidVersionAtLeast(34) ? new UidtKeepAlive(app) : new ForegroundKeepAlive(app);
        }
    }

    /// <summary>The Host started (also from a job or the app): follow its activity.</summary>
    public static void OnHostStarted(Context app, AndroidHost host)
    {
        lock (Gate)
        {
            _app ??= app;
            if (ReferenceEquals(_host, host)) return;
            if (_host is not null) _host.Activity.Changed -= OnChanged;
            _host = host;
            host.Activity.Changed += OnChanged;
        }

        // A change between the snapshot and this subscription is covered by looking now.
        Evaluate();
    }

    /// <summary>MainActivity.OnStart and OnStop. Visible is what allows starting a keep-alive session from the background rules' point of view.</summary>
    public static void SetVisible(bool visible)
    {
        lock (Gate)
        {
            if (_visible == visible) return;
            _visible = visible;
            if (visible) _hiddenLogged = false;
        }

        if (visible) Evaluate();
    }

    /// <summary>The Pause all action ran: when only paused downloads remain, say so (and offer Resume).</summary>
    public static void MarkPauseAll()
    {
        lock (Gate) _pausedAll = true;
    }

    private static void OnChanged() => Evaluate();

    /// <summary>A session (job or service) has started and posted its plain notification: the next look replaces it with the real one.</summary>
    public static void SessionStarted()
    {
        lock (Gate) _shown = null;
        Evaluate();
    }

    /// <summary>Looks at the latest summary and brings the session and the notifications in line with it.</summary>
    public static void Evaluate()
    {
        try { EvaluateCore(); }
        catch (System.Exception ex) { Log.Error(InboundApplication.Tag, "background: " + ex); }
    }

    private static void EvaluateCore()
    {
        lock (Gate)
        {
            if (_host is null || _app is null || _keep is null) return;
            var app = _app;
            var s = _host.Activity.Summary;

            foreach (var f in _host.Activity.TakeFinished()) Notifications.PostFinished(app, f);

#if INBOUND_TEST_BUILD
            if (_lastKeepLogged != s.KeepAlive)
            {
                _lastKeepLogged = s.KeepAlive;
                LogState("flip");
            }
#endif

            if (s.KeepAlive)
            {
                _pausedAll = false;
                if (_pausedShown) { Notifications.Cancel(app, Notifications.PausedId); _pausedShown = false; }
                if (_keep.Running)
                {
                    var text = NotificationText.Ongoing(s);
                    // The same words and bar again: nothing to send.
                    if (text != _shown)
                    {
                        _shown = text;
                        _keep.Update(s, Notifications.Ongoing(app, text));
                    }
                    else _keep.Update(s, null);
                }
                else if (_visible)
                {
                    _shown = null;
                    _keep.Start(s);
                }
                else if (!_hiddenLogged)
                {
                    _hiddenLogged = true;
                    Log.Info(InboundApplication.Tag, "keep-alive needed, the app is not visible and no session runs: nothing started");
                }

                return;
            }

            _shown = null;
            if (_keep.Running) _keep.Stop();
            if (_pausedAll && s.ActiveCount == 0 && s.PausedCount > 0)
            {
                Notifications.Notify(app, Notifications.PausedId, Notifications.Paused(app, s.PausedCount));
                _pausedShown = true;
            }
            else
            {
                if (s.PausedCount == 0) _pausedAll = false;
                if (_pausedShown) { Notifications.Cancel(app, Notifications.PausedId); _pausedShown = false; }
            }
        }
    }

#if INBOUND_TEST_BUILD
    /// <summary>Test build: one "InboundTest" line with the keep-alive state, for a device script to wait on ("keepalive=true" / "keepalive=false").</summary>
    public static void LogState(string why)
    {
        var s = Summary;
        Log.Info("InboundTest",
            $"keepalive={(s.KeepAlive ? "true" : "false")} why={why} running={KeepAliveRunning} visible={_visible} active={s.ActiveCount} paused={s.PausedCount} " +
            $"waiting={s.WaitingCount} rate={s.BytesPerSecond:0} received={s.ActiveReceivedBytes} total={s.ActiveTotalBytes} first={s.First?.Name} api={Android.OS.Build.VERSION.SdkInt}");
    }
#endif
}

/// <summary>What keeps the process alive while downloads run: one interface, two implementations by API level.</summary>
internal interface IKeepAlive
{
    /// <summary>A session runs, or was just asked for and is expected to start.</summary>
    bool Running { get; }

    /// <summary>Asks for a session. Only called while the app is visible.</summary>
    void Start(ActivitySummary summary);

    /// <summary>The session's notification (null: unchanged) and byte figures, from the latest summary.</summary>
    void Update(ActivitySummary summary, Notification? notification);

    /// <summary><see cref="ActivitySummary.KeepAlive"/> turned false: end the session.</summary>
    void Stop();
}

/// <summary>
/// API 34 and up: one persisted user-initiated data transfer job (id 1, any network). Its estimate is what the summary says is left,
/// refreshed at the start of every run and when it changes notably; the bytes moved are reported as they arrive, throttled.
/// </summary>
internal sealed class UidtKeepAlive(Context app) : IKeepAlive
{
    public const int JobId = 1;
    private static readonly TimeSpan StartGrace = TimeSpan.FromSeconds(15);

    private readonly object _gate = new();
    private readonly TransferMeter _meter = new();
    private SessionJobService? _service;
    private JobParameters? _params;
    private DateTimeOffset _requestedAt = DateTimeOffset.MinValue;
    private long _lastEstimate;
    private bool _estimateDirty;

    public bool Running
    {
        get
        {
            lock (_gate) return _service is not null || DateTimeOffset.UtcNow - _requestedAt < StartGrace;
        }
    }

    public void Start(ActivitySummary summary)
    {
        lock (_gate)
        {
            var js = (JobScheduler)app.GetSystemService(Context.JobSchedulerService)!;
            var estimate = SessionBytes.Estimate(summary);
            var info = new JobInfo.Builder(JobId, new ComponentName(app, Java.Lang.Class.FromType(typeof(SessionJobService))))
                .SetUserInitiated(true)!
                .SetRequiredNetworkType(NetworkType.Any)!
                .SetPersisted(true)!
                .SetEstimatedNetworkBytes(estimate, 0)!
                .Build()!;
            var result = js.Schedule(info);
            var ok = result == JobScheduler.ResultSuccess;
            Log.Info(InboundApplication.Tag, "session job scheduled: " + (ok ? "success" : "failure") + " estimate=" + estimate);
            if (ok) { _requestedAt = DateTimeOffset.UtcNow; _lastEstimate = estimate; }
        }
    }

    /// <summary>The job started (SessionJobService.OnStartJob).</summary>
    public void Attach(SessionJobService service, JobParameters p)
    {
        lock (_gate)
        {
            _service = service;
            _params = p;
            _estimateDirty = true; // refreshed at every run start
            _meter.Reset();
        }
    }

    /// <summary>The job ended on the system's side; <paramref name="service"/> is the instance that was told.</summary>
    public void Detach(SessionJobService service)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_service, service)) return;
            _service = null;
            _params = null;
            _requestedAt = DateTimeOffset.MinValue;
        }
    }

    public void Update(ActivitySummary summary, Notification? notification)
    {
        lock (_gate)
        {
            if (_service is null || _params is null) return;
            try
            {
                if (notification is not null)
                    _service.SetNotification(_params, Notifications.OngoingId, notification, JobEndNotificationPolicy.Remove);
                var estimate = SessionBytes.Estimate(summary);
                if (_estimateDirty || SessionBytes.NotableChange(_lastEstimate, estimate))
                {
                    _service.UpdateEstimatedNetworkBytes(_params, estimate, 0);
                    _lastEstimate = estimate;
                    _estimateDirty = false;
                }

                if (_meter.Observe(summary.ActiveReceivedBytes, DateTimeOffset.UtcNow) is { } total)
                    _service.UpdateTransferredNetworkBytes(_params, total, 0);
            }
            catch (System.Exception ex) { Log.Warn(InboundApplication.Tag, "session update: " + ex.Message); }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            var s = _service;
            var p = _params;
            _service = null;
            _params = null;
            var wasRequested = _requestedAt != DateTimeOffset.MinValue;
            _requestedAt = DateTimeOffset.MinValue;
            try
            {
                if (s is not null && p is not null) s.JobFinished(p, false);
                else if (wasRequested) ((JobScheduler)app.GetSystemService(Context.JobSchedulerService)!).Cancel(JobId);
                Log.Info(InboundApplication.Tag, "session job finished");
            }
            catch (System.Exception ex) { Log.Warn(InboundApplication.Tag, "session stop: " + ex.Message); }
        }
    }
}

/// <summary>API 31 to 33 (and the API 30 test build): a dataSync foreground service, started while the app is visible.</summary>
internal sealed class ForegroundKeepAlive(Context app) : IKeepAlive
{
    private readonly object _gate = new();
    private bool _running;

    public bool Running
    {
        get
        {
            lock (_gate) return _running;
        }
    }

    public void Start(ActivitySummary summary)
    {
        lock (_gate)
        {
            try
            {
                app.StartForegroundService(new Intent(app, typeof(KeepAliveService)));
                _running = true;
                Log.Info(InboundApplication.Tag, "foreground service requested");
            }
            catch (System.Exception ex) { Log.Warn(InboundApplication.Tag, "foreground service: " + ex.Message); }
        }
    }

    public void Update(ActivitySummary summary, Notification? notification)
    {
        if (notification is not null) Notifications.Notify(app, Notifications.OngoingId, notification);
    }

    public void Stop()
    {
        lock (_gate)
        {
            _running = false;
            try { app.StopService(new Intent(app, typeof(KeepAliveService))); }
            catch (System.Exception ex) { Log.Warn(InboundApplication.Tag, "foreground service stop: " + ex.Message); }
        }
    }

    /// <summary>The service went away (stopped, or killed by the system).</summary>
    public void Gone()
    {
        lock (_gate) _running = false;
    }
}
