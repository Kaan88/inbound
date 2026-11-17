using Android.App;
using Android.Runtime;
using Inbound.App.Host;

namespace Inbound.App;

/// <summary>
/// Owns the in-process Host: it is started once per process, on first need, and lives as long as the process (step 2 moves it into a
/// foreground service). Also sets the runtime environment of a release build.
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
            return _host ??= Task.Run(() => AndroidHost.StartAsync(root, AndroidHost.SharedDownloadFolder,
                m => Android.Util.Log.Info(Tag, m), CancellationToken.None));
        }
    }
}
