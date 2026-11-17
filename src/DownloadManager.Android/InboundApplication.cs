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
        // Every process start, whatever started it (the app, a job, a notification action): this run's id, and the previous run's kept for the Host start.
        RunTracker.Begin(this);
        InboundBackground.Init(this);
        // Before any Host: its first report of the network comes from here.
        NetworkMonitor.Init(this);
#if INBOUND_RELEASE_ENV
        // The sockets read this switch once, at first use (before the Host starts). The project also lists it as an AndroidEnvironment
        // file; this makes sure it is set when that file is not applied (NativeAOT).
        if (System.Environment.GetEnvironmentVariable("DOTNET_SYSTEM_NET_SOCKETS_INLINE_COMPLETIONS") is null)
            System.Environment.SetEnvironmentVariable("DOTNET_SYSTEM_NET_SOCKETS_INLINE_COMPLETIONS", "1");
        Android.Util.Log.Info(Tag, "DOTNET_SYSTEM_NET_SOCKETS_INLINE_COMPLETIONS=" + System.Environment.GetEnvironmentVariable("DOTNET_SYSTEM_NET_SOCKETS_INLINE_COMPLETIONS"));
#endif
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
                var host = await AndroidHost.StartAsync(root, AndroidHost.SharedDownloadFolder, new AndroidSleepPrevention(app),
                    RunTracker.LastRunStoppedByUser(app), metered, m => Android.Util.Log.Info(Tag, m), CancellationToken.None).ConfigureAwait(false);
                // The Host used the "stopped by the user" decision in its start: the older runs are explained.
                RunTracker.HostConsumed();
                NetworkMonitor.OnHostStarted(host, metered);
                InboundBackground.OnHostStarted(app, host);
                return host;
            });
        }
    }
}
