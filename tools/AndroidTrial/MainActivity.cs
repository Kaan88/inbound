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
