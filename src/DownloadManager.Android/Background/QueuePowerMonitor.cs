using Android.Content;
using Android.OS;
using Android.Util;
using Inbound.App.Host;

namespace Inbound.App.Background;

/// <summary>System power broadcasts only; no polling or durable platform state.</summary>
internal static class QueuePowerMonitor
{
    private static readonly SemaphoreSlim Reports = new(1, 1);
    private static Context? _app;
    private static AndroidHost? _host;
    private static Receiver? _receiver;

    internal static (bool Charging, bool BatterySaver) Read(Context context)
    {
        using var battery = context.RegisterReceiver(null, new IntentFilter(Intent.ActionBatteryChanged));
        var plugged = battery?.GetIntExtra("plugged", 0) > 0;
        var manager = (PowerManager)context.GetSystemService(Context.PowerService)!;
        return (plugged, manager.IsPowerSaveMode);
    }

    internal static void Init(Context context)
    {
        _app = context.ApplicationContext!;
        _receiver = new Receiver();
        var filter = new IntentFilter(Intent.ActionBatteryChanged);
        filter.AddAction(PowerManager.ActionPowerSaveModeChanged);
        if (OperatingSystem.IsAndroidVersionAtLeast(33)) _app.RegisterReceiver(_receiver, filter, ReceiverFlags.NotExported);
        else _app.RegisterReceiver(_receiver, filter);
    }

    internal static void AppVisible() => Report();

    internal static void OnHostStarted(AndroidHost host) { _host = host; Report(); }

    private static void Report()
    {
        _ = Task.Run(async () =>
        {
            await Reports.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_host is null || _app is null) return;
                // Read only after taking the report gate: a delayed broadcast can never restore an older value.
                var power = Read(_app);
                await _host.Service.SetQueuePowerStateAsync(power.Charging, power.BatterySaver).ConfigureAwait(false);
                InboundBackground.Evaluate();
            }
            catch (Exception ex) { Log.Warn(InboundApplication.Tag, "queue power report: " + ex.GetType().Name); }
            finally { Reports.Release(); }
        });
    }

    private sealed class Receiver : BroadcastReceiver
    {
        public override void OnReceive(Context? context, Intent? intent) => Report();
    }
}
