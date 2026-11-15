using Android.App;
using Android.Content;
using Android.OS;
using Android.Util;
using Android.Views;

namespace AndroidTrial;

/// <summary>What the test script passes as intent extras (adb: --ei seed 200 --ez dynamic true ...).</summary>
public sealed record ScreenOptions(int Seed, bool Dynamic, int Real, string Server, int Downloads)
{
    public static ScreenOptions From(Intent? i) => new(
        i?.GetIntExtra("seed", 0) ?? 0,
        i?.GetBooleanExtra("dynamic", false) ?? false,
        i?.GetIntExtra("real", 3) ?? 3,
        i?.GetStringExtra("server") ?? "http://127.0.0.1:8765",
        i?.GetIntExtra("downloads", 0) ?? 0);
}

public static class TrialSupport
{
    public const string Tag = "InboundTk";

    public static string StateRoot(Context c) => System.IO.Path.Combine(c.FilesDir!.AbsolutePath, "host-state");

    public static string Destination(Context c) => c.GetExternalFilesDir(null)!.AbsolutePath;

    /// <summary>Starts the host, shows the snapshot through <paramref name="model"/>, then seeds if asked (after the list is shown).</summary>
    public static void Begin(Activity activity, DownloadsModel model, ScreenOptions options)
    {
        var reported = false;
        model.FirstSnapshotShown += () =>
        {
            if (reported) return;
            reported = true;
            ReportWhenDrawn(activity, model.Rows.Count);
        };
        _ = Task.Run(async () =>
        {
            try
            {
                await model.StartAsync(StateRoot(activity), CancellationToken.None).ConfigureAwait(false);
                // seed=N: N jobs, N-real of them idle, real (default 3) real throttled downloads. downloads=K (extra): only K real ones.
                if (options.Seed > 0)
                    await model.SeedAsync(Destination(activity), options.Server, options.Seed, Math.Min(options.Real, options.Seed), CancellationToken.None).ConfigureAwait(false);
                else if (options.Downloads > 0)
                    await model.SeedAsync(Destination(activity), options.Server, options.Downloads, options.Downloads, CancellationToken.None).ConfigureAwait(false);
                if (options.Seed > 0 || options.Downloads > 0) Log.Info(Tag, $"seeded={options.Seed}+{options.Downloads}");
            }
            catch (Exception ex) { Log.Error(Tag, "host: " + ex); }
        });
    }

    /// <summary>
    /// "Usable list": called when the snapshot's rows are in the list. Waits for two frames (rows are laid out in the first, drawn
    /// by the second), then ReportFullyDrawn and a log line with the time since process start.
    /// </summary>
    public static void ReportWhenDrawn(Activity activity, int rows)
    {
        var shownAt = SystemClock.ElapsedRealtime();
        Choreographer.Instance!.PostFrameCallback(new FrameCb(() => Choreographer.Instance!.PostFrameCallback(new FrameCb(() =>
        {
            activity.ReportFullyDrawn();
            var since = SystemClock.ElapsedRealtime() - Process.StartElapsedRealtime;
            Log.Info(Tag, $"fullyDrawn={since}ms rows={rows} rowsShownToDrawn={SystemClock.ElapsedRealtime() - shownAt}ms");
        }))));
    }

    private sealed class FrameCb(Action run) : Java.Lang.Object, Choreographer.IFrameCallback
    {
        public void DoFrame(long frameTimeNanos) => run();
    }
}
