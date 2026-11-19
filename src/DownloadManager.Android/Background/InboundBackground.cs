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
    private static bool _guardLogged;
    private static OngoingText? _shown;
    private static bool _pausedShown;
    private static bool _waitingShown;
    private static int _waitingCount;
    private static bool _waitingWifiBack;
    // API 31 to 33: the network is not metered again but the app is not visible, so the downloads stay held (NetworkMonitor defers the report).
    private static bool _wifiBack;
    private static int _pausedCount;
    private static bool _holdDone;
    // A hold job the system started (it sees a network that isn't metered) while the Host still holds: the platform's own network
    // report normally lets go within moments; if it hasn't after this long, the two disagree and the job ends without a reschedule.
    private static readonly TimeSpan HoldGrace = TimeSpan.FromSeconds(30);
    private static bool _stopIssued;
    private static DateTimeOffset _backgroundStartUntil = DateTimeOffset.MinValue;
#if INBOUND_TEST_BUILD
    private static bool? _lastKeepLogged;
#endif

    /// <summary>The keep-alive of this API level.</summary>
    public static IKeepAlive KeepAlive => _keep ?? throw new InvalidOperationException("not initialised");

    public static bool KeepAliveRunning => _keep?.Running ?? false;
    internal static bool TransferExecutionAllowed => Visible || KeepAliveRunning || QueueWorkScheduler.Running;
    private static readonly object PermissionGate = new();
    private static DownloadManager.Runtime.DownloadManagerService? _permissionService;
    internal static void RegisterStartingService(DownloadManager.Runtime.DownloadManagerService service)
    {
        lock (PermissionGate) _permissionService = service;
        RefreshTransferPermission();
    }
    internal static void ForgetStartingService(DownloadManager.Runtime.DownloadManagerService service)
    {
        lock (PermissionGate) { if (ReferenceEquals(_permissionService, service)) _permissionService = null; }
    }
    internal static void RefreshTransferPermission()
    {
        lock (PermissionGate)
        {
            if ((_permissionService ?? _host?.Service) is not { } service) return;
            var update = service.SetPlatformTransfersAllowedAsync(TransferExecutionAllowed);
            _ = update.ContinueWith(t => Log.Warn(InboundApplication.Tag, "transfer permission: " + t.Exception?.GetBaseException().GetType().Name),
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
        QueueWorkScheduler.RequestArm();
    }

    /// <summary>The downloads screen is visible (between MainActivity's OnStart and OnStop).</summary>
    public static bool Visible
    {
        get { lock (Gate) return _visible; }
    }

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
            if (_host is not null) { _host.Activity.Changed -= OnChanged; _host.Service.QueueFinished -= OnQueueFinished; }
            _host = host;
            host.Activity.Changed += OnChanged;
            host.Service.QueueFinished += OnQueueFinished;
        }

        // The boot notice (API 31 to 33) said what would resume: the Host is up now, which resumes it.
        Notifications.Cancel(app, Notifications.BootId);
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

        RefreshTransferPermission();
        if (visible)
        {
            QueuePowerMonitor.AppVisible();
            // API 31 to 33: a "not metered" report held back while the app was hidden goes to the Host now.
            NetworkMonitor.AppVisible();
            Evaluate();
        }
    }

    /// <summary>API 31 to 33: Wi-Fi is back but the report is held until the app is visible; the waiting notification says "Tap to resume".</summary>
    public static void SetWifiBack(bool back)
    {
        lock (Gate)
        {
            if (_wifiBack == back) return;
            _wifiBack = back;
        }

        Evaluate();
    }

    /// <summary>"Resume" of a notification ran: the paused notification goes (also one a previous process posted).</summary>
    public static void ResumedFromNotification()
    {
        lock (Gate)
        {
            _pausedAll = false;
            _pausedShown = false;
            if (_app is { } app) Notifications.Cancel(app, Notifications.PausedId);
        }
    }

    /// <summary>The Pause all action ran: when only paused downloads remain, say so (and offer Resume).</summary>
    public static void MarkPauseAll()
    {
        lock (Gate) _pausedAll = true;
        Evaluate();
    }

    /// <summary>
    /// "Use data" was tapped: for a short while a session may be started with the app not visible. The tap is exempt from the background
    /// rules (a notification action; a broadcast action scheduled the UIDT job from the background on API 37). The next look that finds
    /// something to transfer starts it; a refusal is logged (opening the app from a broadcast is blocked from API 31: no trampoline). The
    /// window stays within the system's allowance after a notification action (about 10 s).
    /// </summary>
    public static void StartSessionFromNotification()
    {
        lock (Gate) _backgroundStartUntil = DateTimeOffset.UtcNow.AddSeconds(10);
        Evaluate();
    }

    private static void OnQueueFinished(object? sender, DownloadManager.Runtime.QueueFinishedEventArgs notice)
    {
        Context? app;
        lock (Gate) app = ReferenceEquals(sender, _host?.Service) ? _app : null;
        if (app is not null) Notifications.PostQueueFinished(app, notice);
        // Lifecycle already releases an idle session; another queue's active work keeps its own lease.
        Evaluate();
    }

    private static void OnChanged() => Evaluate();

    /// <summary>The user sent a command that starts downloads (through the app's own connection): a tripped guard starts over.</summary>
    public static void UserStartedDownloads()
    {
        if (!CrashGuardStore.Tripped) return;
        CrashGuardStore.Reset("the user started or resumed a download in the app");
        lock (Gate) _guardLogged = false;
        Evaluate();
    }

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
        finally { RefreshTransferPermission(); }
    }

    private static void EvaluateCore()
    {
        lock (Gate)
        {
            if (_host is null || _app is null || _keep is null) return;
            var app = _app;
            var s = _host.Activity.Summary;

            var finished = _host.Activity.TakeFinished();
            if (_host.Service.GetSettings().NotifyOnCompletion)
                foreach (var f in finished) Notifications.PostFinished(app, f);

#if INBOUND_TEST_BUILD
            if (_lastKeepLogged != s.KeepAlive)
            {
                _lastKeepLogged = s.KeepAlive;
                LogState("flip");
            }
#endif

            if (s.KeepAlive)
            {
                if (CrashGuardStore.Tripped)
                {
                    // Repeated crashes during downloads: no session starts on its own. Only a command of the user (resume, add, retry
                    // in the app, or Resume in the notification) starts the guard over (UserStartedDownloads); a download the queue
                    // started while the app was visible does not (Fable finding 9).
                    if (!_guardLogged) { _guardLogged = true; Log.Warn(InboundApplication.Tag, "crash guard tripped: no session started"); }
                    return;
                }

                // Something runs again (a resume, a new start): "paused" is no longer the news.
                if (s.ActiveCount > 0) _pausedAll = false;
                _holdDone = false;
                _stopIssued = false;
                if (_waitingShown) { Notifications.Cancel(app, Notifications.WaitingId); _waitingShown = false; }
                if (_pausedShown) { Notifications.Cancel(app, Notifications.PausedId); _pausedShown = false; }
                if (_keep.Running)
                {
                    var text = NotificationText.Ongoing(s);
                    // The same words and bar again: nothing to send.
                    if (text != _shown)
                    {
                        // Remembered only once delivered (no session yet, or the system refused): the next look tries again.
                        if (_keep.Update(s, Notifications.Ongoing(app, text))) _shown = text;
                    }
                    else _keep.Update(s, null);
                }
                else if (_visible || DateTimeOffset.UtcNow < _backgroundStartUntil)
                {
                    _shown = null;
                    var fromBackground = !_visible;
                    _backgroundStartUntil = DateTimeOffset.MinValue;
                    if (!_keep.Start(s) && fromBackground)
                        Log.Warn(InboundApplication.Tag, "keep-alive refused after a notification action: downloads run while the process lives; opening the app starts it");
                }
                else if (!_hiddenLogged)
                {
                    _hiddenLogged = true;
                    Log.Info(InboundApplication.Tag, "keep-alive needed, the app is not visible and no session runs: nothing started");
                }

                return;
            }

            _shown = null;
            if (s.WaitingForNetwork)
            {
                // Held for a network that isn't metered: the session ends, and the system is asked to start it again when one comes (once per hold).
                if (!_holdDone) { _holdDone = true; _stopIssued = false; _keep.Hold(s); }
                else if (_keep.AttachedFor is { } attached && !_stopIssued)
                {
                    if (attached >= HoldGrace)
                    {
                        _stopIssued = true;
                        Log.Warn(InboundApplication.Tag, "hold job started but the network still counts as metered after " +
                            (int)attached.TotalSeconds + " s: ending it; downloads resume when the app is opened or the network changes");
                        _keep.Stop();
                    }
                    else
                    {
                        var wait = HoldGrace - attached + TimeSpan.FromSeconds(1);
                        _ = Task.Delay(wait).ContinueWith(_ => Evaluate(), TaskScheduler.Default);
                    }
                }
                if (s.ActiveCount == 0)
                {
                    if (!_waitingShown || _waitingCount != s.WaitingCount || _waitingWifiBack != _wifiBack)
                    {
                        _waitingShown = true;
                        _waitingCount = s.WaitingCount;
                        _waitingWifiBack = _wifiBack;
                        Notifications.Notify(app, Notifications.WaitingId,
                            _wifiBack ? Notifications.WifiBack(app, s.WaitingCount) : Notifications.Waiting(app, s.WaitingCount));
                    }
                }
            }
            else
            {
                _holdDone = false;
                _wifiBack = false;
                // Once per flip, whatever the session's state: it also cancels a job the system scheduled again after a stop of its own.
                if (!_stopIssued) { _stopIssued = true; _keep.Stop(); }
            }

            if (!s.WaitingForNetwork || s.ActiveCount > 0)
            {
                if (_waitingShown) { Notifications.Cancel(app, Notifications.WaitingId); _waitingShown = false; }
            }

            // Only what Pause all paused counts (owner, S24: "201 downloads paused" for one download).
            var paused = _pausedAll && s.ActiveCount == 0 ? ResumeSet.StillPaused(app, _host) : 0;
            if (paused > 0)
            {
                if (!_pausedShown || _pausedCount != paused)
                    Notifications.Notify(app, Notifications.PausedId, Notifications.Paused(app, paused));
                _pausedShown = true;
                _pausedCount = paused;
            }
            else
            {
                if (_pausedAll && s.ActiveCount == 0) _pausedAll = false;
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
            $"waiting={s.WaitingCount} noresume={s.NoResumeOnMeteredCount} metered={NetworkMonitor.Current} holds={_host?.Service.WaitingForMeteredNetwork} rate={s.BytesPerSecond:0} received={s.ActiveReceivedBytes} total={s.ActiveTotalBytes} first={s.First?.Name} crash={CrashGuardStore.Count} api={Android.OS.Build.VERSION.SdkInt}");
    }
#endif
}

/// <summary>What keeps the process alive while downloads run: one interface, two implementations by API level.</summary>
internal interface IKeepAlive
{
    /// <summary>A session runs, or was just asked for and is expected to start.</summary>
    bool Running { get; }

    /// <summary>Asks for a session. Called while the app is visible (or just after "Use data"). False when the system refused.</summary>
    bool Start(ActivitySummary summary);

    /// <summary>The session's notification (null: unchanged) and byte figures, from the latest summary. False when a notification could not be delivered.</summary>
    bool Update(ActivitySummary summary, Notification? notification);

    /// <summary><see cref="ActivitySummary.KeepAlive"/> turned false: end the session (and whatever was scheduled for it). Safe to call with nothing running.</summary>
    void Stop();

    /// <summary>
    /// Only waiting for a network that isn't metered remains (<see cref="ActivitySummary.WaitingForNetwork"/>): end the session and arrange,
    /// where the platform allows it, that the system starts one again when such a network comes.
    /// </summary>
    void Hold(ActivitySummary summary);

    /// <summary>How long a started session has been attached (API 34+ job), or null when none is attached or the mechanism has no such notion.</summary>
    TimeSpan? AttachedFor { get; }
}

/// <summary>
/// API 34 and up: one persisted user-initiated data transfer job (id 1). Its estimate is what the summary says is left, refreshed at the
/// start of every run and when it changes notably; the bytes moved are reported as they arrive, throttled. Any network while downloads can
/// transfer; not metered only while they wait for such a network (<see cref="Hold"/>).
/// </summary>
internal sealed class UidtKeepAlive(Context app) : IKeepAlive
{
    public const int JobId = 1;
    private static readonly TimeSpan StartGrace = TimeSpan.FromSeconds(15);

    private readonly object _gate = new();
    private readonly TransferMeter _meter = new();
    private SessionJobService? _service;
    private JobParameters? _params;
    private JobRunIdentity? _runIdentity;
    private object? _execution;
    private DateTimeOffset _requestedAt = DateTimeOffset.MinValue;
    private long _lastEstimate;
    private bool _estimateDirty;
    // The network the job was last scheduled with, and the one the attached run started under: a run the hold job started needs a network
    // that isn't metered, so the system would stop it on mobile data even while downloads go on there (Use data, or the warning).
    private NetworkType _scheduledNetwork = NetworkType.Any;
    private NetworkType _attachedNetwork = NetworkType.Any;
    private bool _anyTried;

    public bool Running
    {
        get
        {
            lock (_gate) return _service is not null || DateTimeOffset.UtcNow - _requestedAt < StartGrace;
        }
    }

    private JobScheduler Scheduler => (JobScheduler)app.GetSystemService(Context.JobSchedulerService)!;

    private bool Schedule(NetworkType network, long estimate, string what)
    {
        var info = new JobInfo.Builder(JobId, new ComponentName(app, Java.Lang.Class.FromType(typeof(SessionJobService))))
            .SetUserInitiated(true)!
            .SetExtras(JobRunIdentity.NewExtras())!
            .SetRequiredNetworkType(network)!
            .SetPersisted(true)!
            .SetEstimatedNetworkBytes(estimate, 0)!
            .Build()!;
        var ok = Scheduler.Schedule(info) == JobScheduler.ResultSuccess;
        if (ok) _scheduledNetwork = network;
        Log.Info(InboundApplication.Tag, $"{what} scheduled ({network}): " + (ok ? "success" : "failure") + " estimate=" + estimate);
        return ok;
    }

    public bool Start(ActivitySummary summary)
    {
        lock (_gate)
        {
            // A request younger than the grace window is still on its way: the window only decides whether to schedule again.
            if (_service is not null || DateTimeOffset.UtcNow - _requestedAt < StartGrace) return true;
            var estimate = SessionBytes.Estimate(summary);
            var ok = Schedule(NetworkType.Any, estimate, "session job");
            if (ok) { _requestedAt = DateTimeOffset.UtcNow; _lastEstimate = estimate; CrashGuardStore.SessionBegan(ExecutionLeaseKind.KeepAlive); }
            return ok;
        }
    }

    /// <summary>The job started (SessionJobService.OnStartJob).</summary>
    private DateTimeOffset _attachedAt;

    public TimeSpan? AttachedFor
    {
        get { lock (_gate) return _service is null ? null : DateTimeOffset.UtcNow - _attachedAt; }
    }

    public object Attach(SessionJobService service, JobParameters p)
    {
        lock (_gate)
        {
            var execution = _execution = new object();
            _service = service;
            _params = p;
            _runIdentity = JobRunIdentity.From(p);
            CrashGuardStore.SessionBegan(ExecutionLeaseKind.KeepAlive);
            _attachedNetwork = _scheduledNetwork;
            _anyTried = false;
            _attachedAt = DateTimeOffset.UtcNow;
            _estimateDirty = true; // refreshed at every run start
            _meter.Reset();
            return execution;
        }
    }

    /// <summary>The job's run <paramref name="p"/> ended on the system's side (any stop reason). A stop of an older run (our own reschedule over
    /// a running job) leaves a newer attached run alone.</summary>
    public void Detach(SessionJobService service, JobParameters? p)
    {
        lock (_gate)
        {
            if (_service?.Equals(service) != true || _params is null || p is null || _runIdentity != JobRunIdentity.From(p)) return;
            _service = null;
            _params = null;
            _runIdentity = null;
            _execution = null;
            _requestedAt = DateTimeOffset.MinValue;
            CrashGuardStore.SessionEnded(ExecutionLeaseKind.KeepAlive);
        }
    }

    public bool IsCurrent(object execution)
    {
        lock (_gate) return ReferenceEquals(_execution, execution);
    }

    public void StopIfCurrent(object execution)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_execution, execution)) return;
            Stop();
        }
        InboundBackground.RefreshTransferPermission();
    }

    public bool Update(ActivitySummary summary, Notification? notification)
    {
        lock (_gate)
        {
            if (_service is null || _params is null) return false;
            if (_attachedNetwork == NetworkType.Unmetered && !_anyTried)
            {
                // Transferring again in a run the hold job started: the same job again with any network, over the running one (the system
                // stops the old run as "cancelled by app" and starts the new one at once), so mobile data later does not end it.
                _anyTried = true;
                Schedule(NetworkType.Any, SessionBytes.Estimate(summary), "session job (any network, after the hold)");
            }

            var delivered = true;
            try
            {
                if (notification is not null)
                    _service.SetNotification(_params, Notifications.OngoingId, notification, JobEndNotificationPolicy.Remove);
            }
            catch (System.Exception ex)
            {
                delivered = false;
                Log.Warn(InboundApplication.Tag, "session notification: " + ex.Message);
            }

            try
            {
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

            return delivered;
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
            _runIdentity = null;
            _execution = null;
            _requestedAt = DateTimeOffset.MinValue;
            try
            {
                // Always: a running session is finished; with none attached (never started, ended by the system, or a window that ran
                // out) whatever is still scheduled is cancelled, so the system does not start a session for nothing.
                if (s is not null && p is not null) s.JobFinished(p, false);
                else Scheduler.Cancel(JobId);
                Log.Info(InboundApplication.Tag, "session job finished");
                CrashGuardStore.SessionEnded(ExecutionLeaseKind.KeepAlive);
            }
            catch (System.Exception ex) { Log.Warn(InboundApplication.Tag, "session stop: " + ex.Message); }
        }
    }

    /// <summary>
    /// Schedules the same job again requiring a network that isn't metered, so the system starts it when Wi-Fi comes back. Tried first over
    /// the running job (its own process, maybe not visible: the system answers "cancelled by app" to the old run and the new one waits for its
    /// network); when that is refused the run is finished and the schedule is tried once more. When both fail it is logged and the downloads
    /// wait until the app is opened or "Use data" is tapped.
    /// </summary>
    public void Hold(ActivitySummary summary)
    {
        lock (_gate)
        {
            var estimate = SessionBytes.Estimate(summary);
            var s = _service;
            var p = _params;
            var ok = Schedule(NetworkType.Unmetered, estimate, "hold job" + (s is not null ? " over the running session" : ""));
            if (!ok && s is not null && p is not null)
            {
                try { s.JobFinished(p, false); }
                catch (System.Exception ex) { Log.Warn(InboundApplication.Tag, "hold: session finish: " + ex.Message); }
                ok = Schedule(NetworkType.Unmetered, estimate, "hold job after the session ended");
            }

            _service = null;
            _params = null;
            _runIdentity = null;
            _execution = null;
            _requestedAt = DateTimeOffset.MinValue;
            CrashGuardStore.SessionEnded(ExecutionLeaseKind.KeepAlive);
            if (ok) _lastEstimate = estimate;
            else Log.Warn(InboundApplication.Tag, "hold job refused: downloads resume when the app is opened or Use data is tapped");
        }
    }
}

/// <summary>
/// API 31 to 33 (and the API 30 test build): a dataSync foreground service, started while the app is visible (or by a notification action).
/// Every start has a generation: the service and the start command act only on their own, so an old instance going away cannot clear the
/// state of a newer start, and a stop that comes before the start command ran leaves the stopping to the service (a service stopped before
/// it called StartForeground crashes the app).
/// </summary>
internal sealed class ForegroundKeepAlive(Context app) : IKeepAlive
{
    // The service path has no hold job (it ends while held and is started again by the app).
    public TimeSpan? AttachedFor => null;

    public const string ExtraGeneration = "keepalive_gen";

    private readonly object _gate = new();
    private bool _running;
    private bool _started;
    private int _gen;

    public bool Running
    {
        get
        {
            lock (_gate) return _running;
        }
    }

    public bool Start(ActivitySummary summary)
    {
        lock (_gate)
        {
            try
            {
                var gen = _gen + 1;
                var intent = new Intent(app, typeof(KeepAliveService));
                intent.PutExtra(ExtraGeneration, gen);
                app.StartForegroundService(intent);
                _gen = gen;
                _running = true;
                _started = false;
                CrashGuardStore.SessionBegan(ExecutionLeaseKind.KeepAlive);
                Log.Info(InboundApplication.Tag, "foreground service requested gen=" + gen);
                return true;
            }
            catch (System.Exception ex)
            {
                Log.Warn(InboundApplication.Tag, "foreground service: " + ex.Message);
                return false;
            }
        }
    }

    public bool Update(ActivitySummary summary, Notification? notification)
    {
        if (notification is not null) Notifications.Notify(app, Notifications.OngoingId, notification);
        return true;
    }

    public void Stop()
    {
        lock (_gate)
        {
            _running = false;
            CrashGuardStore.SessionEnded(ExecutionLeaseKind.KeepAlive);
            // Before the start command ran there is nothing to stop yet: the service sees it is no longer wanted and stops itself.
            if (!_started)
            {
                Log.Info(InboundApplication.Tag, "foreground service: stop before it started, it stops itself");
                return;
            }

            _started = false;
            try { app.StopService(new Intent(app, typeof(KeepAliveService))); }
            catch (System.Exception ex) { Log.Warn(InboundApplication.Tag, "foreground service stop: " + ex.Message); }
        }
    }

    /// <summary>No scheduling is possible here: the service ends, and the app starts it again when it is visible and a not metered network is back.</summary>
    public void Hold(ActivitySummary summary)
    {
        Stop();
        Log.Info(InboundApplication.Tag, "held for Wi-Fi: no foreground service; downloads resume when the app is visible and the network is not metered");
    }

    /// <summary>The start command of generation <paramref name="gen"/> ran (StartForeground is already called). True while that start is still wanted.</summary>
    public bool OnServiceStarted(int gen)
    {
        lock (_gate)
        {
            if (gen != _gen || !_running) return false;
            _started = true;
            return true;
        }
    }

    /// <summary>The service of generation <paramref name="gen"/> went away (stopped, or killed by the system).</summary>
    public void Gone(int gen)
    {
        lock (_gate)
        {
            if (gen != _gen) return;
            _running = false;
            CrashGuardStore.SessionEnded(ExecutionLeaseKind.KeepAlive);
            _started = false;
        }
    }
}
