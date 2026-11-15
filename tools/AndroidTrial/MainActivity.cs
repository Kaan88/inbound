using Android.App;
using Android.Content;
using Android.OS;
using Android.Util;
using Android.Widget;

namespace AndroidTrial;

[Activity(Name = "app.inbound.trial.MainActivity", Label = "Inbound trial", MainLauncher = true, Exported = true, LaunchMode = Android.Content.PM.LaunchMode.SingleTop)]
public sealed class MainActivity : Activity
{
    private TextView? _log;
    private long _onCreateMs;
    private SafTrial? _saf;

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        _saf?.OnResult(requestCode, resultCode, data);
    }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        // Time from the process being forked to the first line of our code in the Activity.
        _onCreateMs = SystemClock.ElapsedRealtime() - Android.OS.Process.StartElapsedRealtime;
        var scroll = new ScrollView(this);
        _log = new TextView(this) { TextSize = 11 };
        _log.SetTextIsSelectable(true);
        scroll.AddView(_log);
        SetContentView(scroll);
        Show($"process start -> OnCreate: {_onCreateMs} ms");
        _ = StartAsync(Intent);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        _ = StartAsync(intent);
    }

    private async Task StartAsync(Intent? intent)
    {
        var mode = intent?.GetStringExtra("mode");
        if (mode is "host" or "service")
        {
            var hr = new HostRequest(
                intent!.GetStringExtra("runId") ?? "run", intent.GetStringExtra("phase") ?? "add", intent.GetStringExtra("url"),
                intent.GetStringExtra("sha256"), intent.GetIntExtra("segments", 1), intent.GetBooleanExtra("resumeIfPaused", false),
                intent.GetBooleanExtra("idle", false));
            if (mode == "service")
            {
                var svc = new Intent(this, typeof(TrialService));
                svc.PutExtra("runId", hr.RunId).PutExtra("phase", hr.Phase).PutExtra("segments", hr.Segments)
                    .PutExtra("resumeIfPaused", hr.ResumeIfPaused).PutExtra("idle", hr.Idle);
                if (hr.Url is not null) svc.PutExtra("url", hr.Url);
                if (hr.Sha256 is not null) svc.PutExtra("sha256", hr.Sha256);
                try
                {
                    StartForegroundService(svc);
                    Show("StartForegroundService ok");
                }
                catch (Exception ex) { Show("StartForegroundService FAILED " + ex); }
                if (intent.GetBooleanExtra("finish", false)) Finish();
                return;
            }

            try { await Task.Run(() => HostScenario.RunAsync(this, hr, Show, CancellationToken.None, keepHost: false)); }
            catch (Exception ex) { Log.Error("InboundTrial", ex.ToString()); Show("FATAL " + ex); }
            return;
        }

        if (mode == "saf")
        {
            _saf ??= new SafTrial(this, Show);
            _saf.Begin(intent!);
            return;
        }

        if (intent?.GetStringExtra("url") is not { Length: > 0 } url)
        {
            Show("No 'url' extra: idle. Start with: adb shell am start -n app.inbound.trial/.MainActivity --es url ... --ei segments 8 --es dest app --es runId r1");
            return;
        }

        var request = new TrialRequest(
            url,
            intent.GetIntExtra("segments", 1),
            intent.GetStringExtra("dest") ?? "app",
            intent.GetStringExtra("sha256"),
            intent.GetStringExtra("runId") ?? "run",
            _onCreateMs);
        try
        {
            await Task.Run(() => TrialRunner.RunAsync(this, request, Show, CancellationToken.None));
        }
        catch (Exception ex)
        {
            Log.Error("InboundTrial", ex.ToString());
            Show("FATAL " + ex);
        }
    }

    private void Show(string line)
    {
        Log.Info("InboundTrial", line);
        RunOnUiThread(() => _log?.Append(line + "\n"));
    }
}
