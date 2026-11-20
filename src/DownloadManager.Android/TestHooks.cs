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
///   am start -n app.inbound.android/.MainActivity --ez metered_override true|false
///       forces the network reported to the Host as metered or not (the phone's own network is untouched).
///   am start -n app.inbound.android/.MainActivity --ez wifi_only true|false
///       sets "Download only on Wi-Fi" (DownloadOnlyOnUnmeteredNetwork) until the app has a settings screen for it.
///   am start -n app.inbound.android/.MainActivity --ez crash_now true
///       2 s later an unhandled exception on a background thread ends the process (a crash with whatever runs); the crash-loop guard counts it.
///   am start -n app.inbound.android/.MainActivity --ez log_state true
///       logs one "InboundTest keepalive=true|false why=hook running=... active=... paused=... waiting=... noresume=... metered=... holds=..." line (the keep-alive and ActivitySummary
///       state, and crash=N, the crash-loop guard's count); the same line is also logged on every keep-alive flip (why=flip).
/// Logs "InboundTest" lines ("seeded=...", "added accepted=...") for the script to wait on.
/// </summary>
internal static class TestHooks
{
    public const string Tag = "InboundTest";

#if INBOUND_RCLONE_PROBE
    private static int _holdPublication;

    internal static Task HoldRemoteFilesPublicationAsync(CancellationToken token)
    {
        if (Interlocked.Exchange(ref _holdPublication, 0) == 0) return Task.CompletedTask;
        Log.Info(Tag, "Remote Files exclusive destination opened; waiting before copy");
        return Task.Delay(TimeSpan.FromSeconds(30), token);
    }
#endif

    public static void Run(DownloadsModel model, Intent? intent)
    {
        if (intent is null) return;
#if INBOUND_RCLONE_PROBE
        if (intent.GetBooleanExtra("hold_remote_files_publication", false))
        {
            intent.RemoveExtra("hold_remote_files_publication");
            Interlocked.Exchange(ref _holdPublication, 1);
        }
        if (intent.GetBooleanExtra("rclone_probe", false))
        {
            intent.RemoveExtra("rclone_probe");
            RcloneProbe.Run();
        }
        if (intent.GetBooleanExtra("media_upload_create", false))
        {
            intent.RemoveExtra("media_upload_create");
            MediaStoreUploadProbe.Create(Android.App.Application.Context, intent.GetBooleanExtra("media_upload_nested", false));
            intent.RemoveExtra("media_upload_nested");
        }
        if (intent.GetBooleanExtra("media_upload_cleanup", false))
        {
            intent.RemoveExtra("media_upload_cleanup");
            MediaStoreUploadProbe.Cleanup(Android.App.Application.Context);
        }
        if (intent.GetIntExtra("remote_files_fixture_port", 0) is > 0 and <= 65535)
        {
            var port = intent.GetIntExtra("remote_files_fixture_port", 0);
            intent.RemoveExtra("remote_files_fixture_port");
            RemoteFilesHostProbe.Run(port);
        }
        if (intent.GetIntExtra("remote_files_upload_port", 0) is > 0 and <= 65535)
        {
            var uploadPort = intent.GetIntExtra("remote_files_upload_port", 0);
            intent.RemoveExtra("remote_files_upload_port");
            RemoteFilesHostProbe.RunConditionalUpload(uploadPort);
        }
        if (intent.GetIntExtra("remote_files_s3_upload_port", 0) is > 0 and <= 65535)
        {
            var uploadPort = intent.GetIntExtra("remote_files_s3_upload_port", 0);
            intent.RemoveExtra("remote_files_s3_upload_port");
            RemoteFilesHostProbe.RunConditionalUpload(uploadPort, s3: true);
        }
#endif
        if (intent.HasExtra("metered_override"))
        {
            var metered = intent.GetBooleanExtra("metered_override", false);
            intent.RemoveExtra("metered_override");
            Inbound.App.Background.NetworkMonitor.SetOverride(metered);
        }

        if (intent.HasExtra("wifi_only"))
        {
            var on = intent.GetBooleanExtra("wifi_only", false);
            intent.RemoveExtra("wifi_only");
            _ = Task.Run(async () =>
            {
                try
                {
                    var host = await InboundApplication.GetHostAsync(Android.App.Application.Context).ConfigureAwait(false);
                    var settings = host.Service.GetSettings();
                    settings.DownloadOnlyOnUnmeteredNetwork = on;
                    var r = await host.Service.UpdateSettingsAsync(settings).ConfigureAwait(false);
                    Log.Info(Tag, $"wifi_only={on} ok={r.Ok} {r.Error}");
                }
                catch (System.Exception ex) { Log.Error(Tag, "wifi_only: " + ex.Message); }
            });
        }

        if (intent.GetBooleanExtra("log_state", false))
        {
            intent.RemoveExtra("log_state");
            Inbound.App.Background.InboundBackground.LogState("hook");
        }

        if (intent.GetBooleanExtra("crash_now", false))
        {
            intent.RemoveExtra("crash_now");
            Log.Warn(Tag, "crash_now: an unhandled exception on a background thread in 2 s");
            // A plain thread: an unhandled exception on it ends the process, which is what a device script needs to produce a crash with a download running.
            new System.Threading.Thread(() =>
            {
                System.Threading.Thread.Sleep(2000);
                throw new InvalidOperationException("crash_now test hook");
            }) { IsBackground = true }.Start();
        }

        // probe_url: one GET with the engine's kind of handler (SocketsHttpHandler) and one with the platform's, each logged with
        // the full exception chain, to tell a TLS fault of the build from one of the server.
        var probeUrl = intent.GetStringExtra("probe_url");
        if (!string.IsNullOrEmpty(probeUrl))
        {
            intent.RemoveExtra("probe_url");
            _ = Task.Run(async () =>
            {
                foreach (var (name, handler) in new (string, HttpMessageHandler)[]
                         { ("sockets", new SocketsHttpHandler()), ("android", new Xamarin.Android.Net.AndroidMessageHandler()) })
                {
                    try
                    {
                        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
                        using var r = await client.GetAsync(probeUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                        Log.Info(Tag, $"probe {name}: {(int)r.StatusCode}");
                    }
                    catch (System.Exception ex) { Log.Error(Tag, $"probe {name}: {ex}"); }
                }
            });
        }

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
