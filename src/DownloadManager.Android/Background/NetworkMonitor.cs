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
    private static bool _pumping;
    private static bool? _sent;
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
            if (active is not null && cm.GetNetworkCapabilities(active) is { } caps) lock (Gate) _metered = IsMetered(caps);
            cm.RegisterDefaultNetworkCallback(new Callback());
            Log.Info(InboundApplication.Tag, "network: metered=" + _metered);
        }
        catch (System.Exception ex) { Log.Warn(InboundApplication.Tag, "network monitor: " + ex.Message); }
    }

    private static bool IsMetered(NetworkCapabilities caps) =>
        MeteredPolicy.IsMetered(caps.HasCapability(NetCapability.NotMetered),
            OperatingSystem.IsAndroidVersionAtLeast(30) && caps.HasCapability(NetCapability.TemporarilyNotMetered));

    private sealed class Callback : ConnectivityManager.NetworkCallback
    {
        // Capabilities arrive after OnAvailable and on every change (Wi-Fi to mobile data included, as the default network switches).
        public override void OnCapabilitiesChanged(Network network, NetworkCapabilities networkCapabilities)
        {
            bool metered;
            lock (Gate)
            {
                metered = IsMetered(networkCapabilities);
                if (_metered == metered) return;
                _metered = metered;
            }

            Log.Info(InboundApplication.Tag, "network changed: metered=" + metered);
            Deliver();
        }

        // Losing the network changes nothing: the next network reports itself.
        public override void OnLost(Network network) => Log.Info(InboundApplication.Tag, "network lost");
    }

    /// <summary>
    /// The Host has started with <paramref name="reportedAtStart"/> as its first report: from now on changes reach it. A change that came
    /// while it started is delivered here.
    /// </summary>
    public static void OnHostStarted(AndroidHost host, bool reportedAtStart)
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
                    bool metered;
                    lock (Gate)
                    {
                        if (_host is null) { _pumping = false; return; }
                        host = _host;
                        metered = Current;
                        if (_sent == metered) { _pumping = false; return; }
                        _sent = metered;
                    }

                    await host.Service.SetNetworkMeteredAsync(metered).ConfigureAwait(false);
                    Log.Info(InboundApplication.Tag, "network reported to the Host: metered=" + metered);
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
                var locked = ((KeyguardManager)app.GetSystemService(Context.KeyguardService)!).IsDeviceLocked;
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
