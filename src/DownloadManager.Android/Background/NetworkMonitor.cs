using Android.App;
using Android.Content;
using Android.Net;
using Android.OS;
using Android.Util;
using DownloadManager.Mobile;
using Inbound.App.Host;

namespace Inbound.App.Background;

/// <summary>
/// Tells the Host whether the default network is metered (the Wi-Fi-only rule). Registered in Application.OnCreate, before any Host exists:
/// the value is kept here, <see cref="InboundApplication.GetHostAsync"/> hands <see cref="Current"/> to the Host before its
/// InitializeAsync, and from <see cref="OnHostStarted"/> on every change is delivered, newest value last, one call at a time.
/// Also decides, once each time the rule starts to hold, what happens to downloads that can't resume (locked: stop; unlocked: go on).
/// </summary>
internal static class NetworkMonitor
{
    private static readonly object Gate = new();
    private static Context? _app;
    private static bool _metered;
    private static bool _cellular;
    private static bool _pumping;
    private static NetworkReport? _sent;
    private static AndroidHost? _host;
#if INBOUND_TEST_BUILD
    private static bool? _override;
#endif

    /// <summary>What the Host is told: the network now (or the test override).</summary>
    public static bool Current
    {
        get
        {
            lock (Gate)
            {
#if INBOUND_TEST_BUILD
                if (_override is { } forced) return forced;
#endif
                return _metered;
            }
        }
    }

    /// <summary>
    /// What the Host is told, both facts read together: metered (the Wi-Fi-only rules) and mobile data (the torrent rules hold on it
    /// even unmetered, review A9-15). One report carries both so the Host never schedules between them (audit A9-15-1).
    /// </summary>
    public static NetworkReport CurrentReport
    {
        get
        {
            lock (Gate)
            {
#if INBOUND_TEST_BUILD
                if (_override is { } forced) return new(forced, _cellular);
#endif
                return new(_metered, _cellular);
            }
        }
    }

    /// <summary>Application.OnCreate: read the active network now (synchronously), then follow it.</summary>
    public static void Init(Context app)
    {
        lock (Gate) _app = app;
        try
        {
            var cm = (ConnectivityManager)app.GetSystemService(Context.ConnectivityService)!;
            // The Host's first report: the network as it is now. No network at all counts as not metered (nothing transfers anyway,
            // and the callback reports the network when one comes).
            var active = cm.ActiveNetwork;
            if (active is not null && cm.GetNetworkCapabilities(active) is { } caps) lock (Gate) { _metered = IsMetered(caps); _cellular = IsMobileData(caps); }
            cm.RegisterDefaultNetworkCallback(new Callback());
            Log.Info(InboundApplication.Tag, "network: " + CurrentReport);
        }
        catch (System.Exception ex) { Log.Warn(InboundApplication.Tag, "network monitor: " + ex.Message); }
    }

    private static bool IsMetered(NetworkCapabilities caps) =>
        MeteredPolicy.IsMetered(caps.HasCapability(NetCapability.NotMetered),
            OperatingSystem.IsAndroidVersionAtLeast(30) && caps.HasCapability(NetCapability.TemporarilyNotMetered));

    private static bool IsMobileData(NetworkCapabilities caps) =>
        MeteredPolicy.IsMobileData(caps.HasTransport(TransportType.Cellular), caps.HasTransport(TransportType.Wifi), caps.HasTransport(TransportType.Ethernet));

    private sealed class Callback : ConnectivityManager.NetworkCallback
    {
        // Capabilities arrive after OnAvailable and on every change (Wi-Fi to mobile data included, as the default network switches).
        public override void OnCapabilitiesChanged(Network network, NetworkCapabilities networkCapabilities)
        {
            NetworkReport now;
            lock (Gate)
            {
                now = new(IsMetered(networkCapabilities), IsMobileData(networkCapabilities));
                if (_metered == now.Metered && _cellular == now.MobileData) return;
                _metered = now.Metered;
                _cellular = now.MobileData;
            }

            Log.Info(InboundApplication.Tag, "network changed: " + now);
            Deliver();
        }

        // Losing the network changes nothing: the next network reports itself.
        public override void OnLost(Network network) => Log.Info(InboundApplication.Tag, "network lost");
    }

    /// <summary>
    /// The Host has started with <paramref name="reportedAtStart"/> as its first report: from now on changes reach it. A change that came
    /// while it started is delivered here.
    /// </summary>
    public static void OnHostStarted(AndroidHost host, NetworkReport reportedAtStart)
    {
        lock (Gate)
        {
            if (ReferenceEquals(_host, host)) return;
            _host = host;
            _sent = reportedAtStart;
        }

        host.Service.MeteredWaitChanged += OnWaitChanged;
        Deliver();
    }

#if INBOUND_TEST_BUILD
    /// <summary>Test build: <c>--ez metered_override true|false</c> forces the reported network (the phone's own settings stay as they are).</summary>
    public static void SetOverride(bool metered)
    {
        lock (Gate) _override = metered;
        Log.Info(InboundApplication.Tag, "network override: metered=" + metered);
        Deliver();
    }
#endif

    private static bool _deferred;

    /// <summary>
    /// Below API 34 a report that would let held work go is held back while nothing could keep it alive: the app is not visible and no
    /// foreground service runs. The service can only start while the app is visible (review of step 2: the Host resumed the transfers in
    /// the background with no service). That is "not metered" while downloads wait for Wi-Fi, and "not metered and not mobile data" while
    /// torrents wait for it (audit A9-15-1). API 34+ has the hold job, which the system starts on such a network.
    /// </summary>
    private static bool HoldBackRelease(AndroidHost host, NetworkReport report, bool visible, bool keepAlive) =>
        !report.Metered && !OperatingSystem.IsAndroidVersionAtLeast(34) && !visible && !keepAlive &&
        (host.Service.WaitingForMeteredNetwork || !report.MobileData && host.Service.TorrentsWaitingForNetwork);

    /// <summary>The app became visible: a report held back is delivered now (the Host resumes, and the keep-alive can start).</summary>
    public static void AppVisible()
    {
        bool deferred;
        lock (Gate) deferred = _deferred;
        if (deferred) Deliver();
    }

    // One delivery loop at a time, always the latest value: two callbacks can't reach the Host out of order.
    private static void Deliver()
    {
        lock (Gate)
        {
            if (_pumping || _host is null) return;
            _pumping = true;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    AndroidHost host;
                    NetworkReport report;
                    bool defer = false, wasDeferred, done = false;
                    // Read before Gate: InboundBackground reads this class under its own lock (lock order).
                    var visible = InboundBackground.Visible;
                    var keepAlive = InboundBackground.KeepAliveRunning || QueueWorkScheduler.Running;
                    lock (Gate)
                    {
                        if (_host is null) { _pumping = false; return; }
                        host = _host;
                        report = CurrentReport;
                        wasDeferred = _deferred;
                        if (_sent == report) { _pumping = false; _deferred = false; done = true; }
                        else if (HoldBackRelease(host, report, visible, keepAlive))
                        {
                            // API 31 to 33, app not visible: the Host would resume the held downloads with no foreground service (one can't
                            // start from the background). They stay held; the waiting notification says "Wi-Fi is back · Tap to resume", and
                            // the report goes when the app is visible (AppVisible).
                            _pumping = false;
                            _deferred = defer = true;
                        }
                        else { _sent = report; _deferred = false; }
                    }

                    if (defer)
                    {
                        if (!wasDeferred) Log.Info(InboundApplication.Tag, "network free again with the app hidden (API < 34): held until the app is opened (" + report + ")");
                        InboundBackground.SetWifiBack(true);
                        return;
                    }

                    if (wasDeferred) InboundBackground.SetWifiBack(false);
                    if (done) return;

                    await host.Service.SetNetworkAsync(report.Metered, report.MobileData).ConfigureAwait(false);
                    Log.Info(InboundApplication.Tag, "network reported to the Host: " + report);
                }
            }
            catch (System.Exception ex)
            {
                lock (Gate) { _pumping = false; _sent = null; }
                Log.Error(InboundApplication.Tag, "network report: " + ex.Message);
            }
        });
    }

    // MeteredWaitChanged: raised once per change of "the rule holds", never from inside the Host's lock.
    private static void OnWaitChanged(object? sender, bool holds)
    {
        if (!holds) return;
        _ = Task.Run(async () =>
        {
            try
            {
                AndroidHost? host;
                Context? app;
                lock (Gate) { host = _host; app = _app; }
                if (host is null || app is null) return;
                // The lock screen is showing (also without a secure lock, or one Smart Lock keeps open): nobody is using the phone.
                var locked = ((KeyguardManager)app.GetSystemService(Context.KeyguardService)!).IsKeyguardLocked;
                var interactive = ((PowerManager)app.GetSystemService(Context.PowerService)!).IsInteractive;
                var stop = MeteredPolicy.StopNonResumableAtOnce(locked, interactive);
                Log.Info(InboundApplication.Tag, $"wi-fi only now holds: locked={locked} interactive={interactive} -> " +
                    (stop ? "stop downloads that can't resume" : "let them go on, with a warning"));
                if (!stop) return;
                var n = await host.Service.StopNonResumableOnMeteredAsync().ConfigureAwait(false);
                Log.Info(InboundApplication.Tag, "stopped " + n + " download(s) that can't resume");
            }
            catch (System.Exception ex) { Log.Error(InboundApplication.Tag, "metered hold: " + ex.Message); }
        });
    }
}

/// <summary>The network as the Host is told it: metered, and mobile data (cellular with no Wi-Fi or Ethernet).</summary>
internal readonly record struct NetworkReport(bool Metered, bool MobileData)
{
    public override string ToString() => $"metered={Metered} mobile data={MobileData}";
}
