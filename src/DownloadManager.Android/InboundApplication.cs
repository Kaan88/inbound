using Android.App;
using Android.Runtime;
using Inbound.App.Background;
using Inbound.App.Host;

namespace Inbound.App;

/// <summary>
/// Owns the in-process Host: it is started once per process, on first need, and lives as long as the process (the keep-alive in
/// Background/ holds the process while downloads can transfer). Also sets the runtime environment of a release build.
/// </summary>
[Application(Name = "app.inbound.android.InboundApplication")]
public sealed class InboundApplication : Application
{
    public const string Tag = "Inbound";

    private static Task<AndroidHost>? _host;
    private static readonly object Gate = new();

    public InboundApplication(IntPtr handle, JniHandleOwnership transfer) : base(handle, transfer) { }

    public override void OnCreate()
    {
        base.OnCreate();
        // Appearance first: the theme choice is the app night mode, and dynamic colour is applied to every screen made after this.
        var appearance = new DownloadManager.Mobile.Appearance(new Screens.PrefsStore(this));
        Screens.ThemeSupport.ApplyNightMode(appearance);
        // The switch is read each time a screen is made, so turning it off takes effect when the screen is rebuilt.
        Google.Android.Material.Color.DynamicColors.ApplyToActivitiesIfAvailable(this,
            new Google.Android.Material.Color.DynamicColorsOptions.Builder().SetPrecondition(new DynamicPrecondition(appearance)).Build());
        // Every process start, whatever started it (the app, a job, a notification action): this run's id, and the previous run's kept for the Host start.
        RunTracker.Begin(this);
        // Right after: accounts for a run that ended in a crash or ANR with a session open (the crash-loop guard).
        CrashGuardStore.Begin(this);
        InboundBackground.Init(this);
        // Before any Host: its first report of the network comes from here.
        NetworkMonitor.Init(this);
        QueuePowerMonitor.Init(this);
#if INBOUND_RELEASE_ENV
        // The sockets read this switch once, at first use (before the Host starts). The project also lists it as an AndroidEnvironment
        // file; this makes sure it is set when that file is not applied (NativeAOT).
        if (System.Environment.GetEnvironmentVariable("DOTNET_SYSTEM_NET_SOCKETS_INLINE_COMPLETIONS") is null)
            System.Environment.SetEnvironmentVariable("DOTNET_SYSTEM_NET_SOCKETS_INLINE_COMPLETIONS", "1");
        Android.Util.Log.Info(Tag, "DOTNET_SYSTEM_NET_SOCKETS_INLINE_COMPLETIONS=" + System.Environment.GetEnvironmentVariable("DOTNET_SYSTEM_NET_SOCKETS_INLINE_COMPLETIONS"));
#endif
        Updates.AndroidUpdates.Initialize(this);
    }

    /// <summary>The state folder: the app's private files directory (metadata only; downloads go to the shared Download folder).</summary>
    public static string StateRoot(Android.Content.Context c) => System.IO.Path.Combine(c.FilesDir!.AbsolutePath, "host-state");

    /// <summary>
    /// The Host of this process, started on the first call. A start that failed is tried again on the next call (the next
    /// screen); AndroidHost.StartAsync stops whatever it started before it fails, so there is never a second writer. When
    /// it could not stop it (<see cref="HostLeftRunningException"/>), the failure stays: only a new process starts again.
    /// </summary>
    internal static Task<AndroidHost> GetHostAsync(Android.Content.Context c)
    {
        lock (Gate)
        {
            if (_host is { IsCanceled: true } ||
                (_host is { IsFaulted: true } && _host.Exception?.InnerException is not HostLeftRunningException)) _host = null;
            var root = StateRoot(c);
            var app = c.ApplicationContext!;
            return _host ??= Task.Run(async () =>
            {
                // The network is read here and reported before the Host's InitializeAsync (its first Schedule is already held).
                var metered = NetworkMonitor.Current;
                // Both are asked (no short-circuit): a tripped crash guard posts its notice even when the last run was also a user stop.
                var guardPaused = CrashGuardStore.HostStartsPaused(app);
                var userStopped = RunTracker.LastRunStoppedByUser(app);
                var host = await AndroidHost.StartAsync(root, AndroidHost.SharedDownloadFolder, new AndroidSleepPrevention(app),
                    userStopped || guardPaused, metered, m => Android.Util.Log.Info(Tag, m), CancellationToken.None).ConfigureAwait(false);
                // The Host used the "stopped by the user" decision in its start: the older runs are explained.
                RunTracker.HostConsumed();
                // The crash guard's Resume restarts what this start paused (not downloads the user had paused before).
                if (guardPaused) ResumeSet.Add(app, host.Service.PausedAtStart, "crash guard start");
                NetworkMonitor.OnHostStarted(host, metered);
                QueuePowerMonitor.OnHostStarted(host);
                InboundBackground.OnHostStarted(app, host);
                QueueWorkScheduler.OnHostStarted(app, host);
                return host;
            });
        }
    }
}

internal sealed class DynamicPrecondition(DownloadManager.Mobile.Appearance appearance) : Java.Lang.Object, Google.Android.Material.Color.DynamicColors.IPrecondition
{
    public bool ShouldApplyDynamicColors(Android.App.Activity activity, int theme) => appearance.DynamicColour;
}
