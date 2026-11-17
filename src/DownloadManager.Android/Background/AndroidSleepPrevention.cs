using Android.Content;
using Android.Net;
using Android.Net.Wifi;
using Android.OS;
using Android.Util;
using DownloadManager.Core;

namespace Inbound.App.Background;

/// <summary>
/// The Runtime's sleep prevention on Android: a partial wake lock (the CPU keeps running with the screen off) and a Wi-Fi lock (the radio is
/// not put to sleep). The Runtime calls <see cref="SetActive"/> only while a download runs and the setting is on. Neither lock has a timeout:
/// the Runtime releases them when the work ends or the Host shuts down (<see cref="Dispose"/>).
/// </summary>
internal sealed class AndroidSleepPrevention(Context app) : ISleepPrevention
{
    // WifiManager.WIFI_MODE_FULL_HIGH_PERF (3) is deprecated from API 34 in favour of WIFI_MODE_FULL_LOW_LATENCY (4). The number is used so
    // a deprecated constant is not named in the build; the low-latency mode holds only while the app is visible, the high-performance
    // one is what keeps the radio up with the screen off where it is still honoured.
    private const int FullHighPerf = 3;
    private const int FullLowLatency = 4;

    private readonly object _gate = new();
    private PowerManager.WakeLock? _cpu;
    private WifiManager.WifiLock? _wifi;
    private bool _active;

    public void SetActive(bool active)
    {
        lock (_gate)
        {
            if (active == _active) return;
            _active = active;
            try
            {
                if (active)
                {
                    _cpu ??= CreateCpu();
                    _wifi ??= CreateWifi();
                    if (_cpu is { IsHeld: false }) _cpu.Acquire();
                    if (_wifi is { IsHeld: false }) _wifi.Acquire();
                }
                else Release();
                Log.Info(InboundApplication.Tag, "sleep prevention " + (active ? "on" : "off"));
            }
            catch (System.Exception ex) { Log.Warn(InboundApplication.Tag, "sleep prevention " + active + ": " + ex.Message); }
        }
    }

    private PowerManager.WakeLock? CreateCpu()
    {
        var pm = (PowerManager?)app.GetSystemService(Context.PowerService);
        var l = pm?.NewWakeLock(WakeLockFlags.Partial, "Inbound:downloading");
        l?.SetReferenceCounted(false);
        return l;
    }

    private WifiManager.WifiLock? CreateWifi()
    {
        var wm = (WifiManager?)app.GetSystemService(Context.WifiService);
        var mode = (WifiMode)(OperatingSystem.IsAndroidVersionAtLeast(34) ? FullLowLatency : FullHighPerf);
        var l = wm?.CreateWifiLock(mode, "Inbound:downloading");
        l?.SetReferenceCounted(false);
        return l;
    }

    private void Release()
    {
        if (_cpu is { IsHeld: true }) _cpu.Release();
        if (_wifi is { IsHeld: true }) _wifi.Release();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _active = false;
            try { Release(); } catch (System.Exception ex) { Log.Warn(InboundApplication.Tag, "sleep prevention release: " + ex.Message); }
        }
    }
}
