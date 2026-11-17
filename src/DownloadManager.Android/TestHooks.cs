#if INBOUND_TEST_BUILD
// TEST BUILD ONLY. This file is compiled only when the app is built with -p:InboundTestBuild=true (symbol INBOUND_TEST_BUILD). A release
// build has none of it: no extras are read and nothing here is in the APK.
using Android.Content;
using Android.Util;
using DownloadManager.Contracts;
using DownloadManager.Mobile;

namespace Inbound.App;

/// <summary>
/// Test-build-only hooks that let an adb script drive the app through the same Add command the screen uses.
///   am start -n app.inbound.android/.MainActivity --ei seed 200 [--ei real 3] [--es server http://127.0.0.1:8765]
///       seeds <c>seed</c> jobs: seed-real idle ones (added with "later", they never run) and <c>real</c> throttled downloads from the test server.
///   am start -n app.inbound.android/.MainActivity --es add_url http://127.0.0.1:8765/bench.bin?size=1048576
///       adds one download by URL and starts it now (the default folder, so it lands in Download/).
/// Logs "InboundTest" lines ("seeded=...", "added accepted=...") for the script to wait on.
/// </summary>
internal static class TestHooks
{
    public const string Tag = "InboundTest";

    public static void Run(DownloadsModel model, Intent? intent)
    {
        if (intent is null) return;
        var seed = intent.GetIntExtra("seed", 0);
        var real = intent.GetIntExtra("real", 3);
        var server = intent.GetStringExtra("server") ?? "http://127.0.0.1:8765";
        var addUrl = intent.GetStringExtra("add_url");
        if (seed <= 0 && string.IsNullOrEmpty(addUrl)) return;
        // Extras stay on the intent across a restart of the activity; clear them so they run once.
        intent.RemoveExtra("seed");
        intent.RemoveExtra("add_url");
        _ = Task.Run(async () =>
        {
            try
            {
                if (seed > 0) await SeedAsync(model, seed, Math.Min(real, seed), server).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(addUrl))
                {
                    var r = await model.AddAsync(addUrl).ConfigureAwait(false);
                    Log.Info(Tag, $"added accepted={r.Accepted} duplicate={r.Duplicate} id={r.Job?.Id} dest={r.Job?.DestinationDirectory} file={r.Job?.FileName} error={r.Error}");
                }
            }
            catch (System.Exception ex) { Log.Error(Tag, "hook: " + ex); }
        });
    }

    private static async Task SeedAsync(DownloadsModel model, int count, int real, string server)
    {
        for (var i = 0; i < count - real; i++)
            await model.AddAsync($"http://127.0.0.1:9/seed-{i}.bin", fileName: $"seed-{i}.bin", startMode: StartModes.Later).ConfigureAwait(false);
        long[] sizes = [700_000_000, 1_100_000_000, 1_600_000_000];
        int[] bps = [2_000_000, 3_000_000, 4_000_000];
        string[] names = ["ubuntu-26.10-desktop-amd64.iso", "field-recordings-vol3.zip", "conference-talk-1080p.mp4"];
        for (var i = 0; i < real; i++)
        {
            var k = i % 3;
            await model.AddAsync($"{server}/bench.bin?size={sizes[k]}&bps={bps[k]}&n={Guid.NewGuid():N}", fileName: names[k] + (i >= 3 ? $".{i}" : ""),
                startMode: StartModes.StartNow).ConfigureAwait(false);
        }

        Log.Info(Tag, $"seeded={count} real={real}");
    }
}
#endif
