using Android.App;
using Android.Content;
using Android.Net;
using Android.OS;
using Android.Util;
using DownloadManager.Mobile;
using DownloadManager.Mobile.Remote;

namespace Inbound.App.Hosts;

/// <summary>
/// ANDROID.7: which host the screens show (this phone or a paired Inbound), for the whole process. A screen asks
/// <see cref="Connect"/> for its connection instead of building the in-process one itself; switching hosts recreates the
/// screens (<see cref="HostBar"/>), so every screen shows the host chosen. The phone's own Host keeps running either way.
/// </summary>
internal static class HostChoice
{
    private static readonly object Gate = new();
    private static HostDirectory? _hosts;

    /// <summary>The host list, loaded once per process. Tokens are read from the Keystore-backed store only when connecting.</summary>
    public static HostDirectory Hosts(Context context)
    {
        lock (Gate)
        {
            if (_hosts is not null) return _hosts;
            var app = context.ApplicationContext!;
            var file = new HostListFile(System.IO.Path.Combine(app.FilesDir!.AbsolutePath, "hosts.json"));
            return _hosts = new HostDirectory(file, new KeystoreSecretStore(app), "Inbound on " + (Build.Model ?? "Android"));
        }
    }

    /// <summary>
    /// The connection for the host shown now. For a server it is closed while the activity is not started (no socket, no
    /// tries while the app is in the background) and tries again at once when the phone's network changes.
    /// </summary>
    public static IHostConnection Connect(Activity activity, HostEntry selected, Func<IHostConnection> phone)
    {
        // Only the exception's type is logged: a message from the network layer is not ours to vouch for.
        var connection = HostConnections.ForEntry(Hosts(activity), selected, phone,
            e => Log.Warn(InboundApplication.Tag, "remote: a frame was skipped (" + e.GetType().Name + ")"));
        if (connection is IRemoteStatus remote) Follow(activity, remote);
        return connection;
    }

    private static void Follow(Activity activity, IRemoteStatus remote)
    {
        var app = (Application)activity.ApplicationContext!;
        var callbacks = new Lifecycle(activity, remote);
        app.RegisterActivityLifecycleCallbacks(callbacks);
    }

    private sealed class Lifecycle(Activity activity, IRemoteStatus remote) : Java.Lang.Object, Application.IActivityLifecycleCallbacks
    {
        private NetworkWatch? _network;

        public void OnActivityStarted(Activity a)
        {
            if (a != activity) return;
            remote.SetActive(true);
            _network ??= NetworkWatch.Start(a, remote);
        }

        public void OnActivityStopped(Activity a)
        {
            if (a != activity) return;
            remote.SetActive(false);
            _network?.Stop();
            _network = null;
        }

        public void OnActivityDestroyed(Activity a)
        {
            if (a != activity) return;
            _network?.Stop();
            _network = null;
            ((Application)a.ApplicationContext!).UnregisterActivityLifecycleCallbacks(this);
        }

        public void OnActivityCreated(Activity a, Bundle? savedInstanceState) { }
        public void OnActivityPaused(Activity a) { }
        public void OnActivityResumed(Activity a) { }
        public void OnActivitySaveInstanceState(Activity a, Bundle outState) { }
    }

    /// <summary>The default network, while a remote host is on screen: a new one makes the connection try again now.</summary>
    private sealed class NetworkWatch : ConnectivityManager.NetworkCallback
    {
        private readonly ConnectivityManager _manager;
        private readonly IRemoteStatus _remote;
        private Network? _current;

        private NetworkWatch(ConnectivityManager manager, IRemoteStatus remote)
        {
            _manager = manager;
            _remote = remote;
        }

        public static NetworkWatch? Start(Context context, IRemoteStatus remote)
        {
            if (context.GetSystemService(Context.ConnectivityService) is not ConnectivityManager manager) return null;
            // The baseline is the network now (none while offline), so the first callback after coming back online is a new
            // network and the connection tries at once instead of waiting out its backoff (Fable finding 7).
            var watch = new NetworkWatch(manager, remote) { _current = manager.ActiveNetwork };
            try { manager.RegisterDefaultNetworkCallback(watch); }
            catch (Java.Lang.Exception) { return null; }
            return watch;
        }

        public void Stop()
        {
            try { _manager.UnregisterNetworkCallback(this); }
            catch (Java.Lang.Exception) { }
        }

        public override void OnAvailable(Network network)
        {
            // The first call is the network at registration, the same as the baseline then. A different one means the socket
            // may be on a network that is gone (or there was none): open it again.
            var previous = _current;
            _current = network;
            if (!network.Equals(previous)) _remote.RetryNow(dropCurrent: true);
        }
    }
}
