#if INBOUND_TEST_BUILD
using DownloadManager.Core;
using DownloadManager.Mobile.Updates;
using Android.Util;

namespace Inbound.App.Updates;

// Isolated, signed host-loopback fixture transport for emulator end-to-end validation.
// No arbitrary URL, key, package name or trust override; absent from release builds.
internal static partial class AndroidUpdates
{
    private static readonly HttpClient FixtureHttp = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
    { Timeout = TimeSpan.FromMinutes(8), MaxResponseContentBufferSize = 65536 };

    public static async Task RunFixtureAsync(MainActivity activity)
    {
        try
        {
            var manifest = AndroidUpdateTrust.Parse(await FixtureHttp.GetByteArrayAsync("http://10.0.2.2:47611/manifest.json"));
            AndroidUpdateTrust.Validate(manifest, ReleaseUpdates.PublicKey);
            var installed = _app.PackageManager!.GetPackageInfo(_app.PackageName!, 0)!.LongVersionCode;
            Service = new AndroidUpdater(Path.Combine(_app.FilesDir!.AbsolutePath, "updates", "fixture"),
                ReleaseUpdates.PublicKey, installed, new FixtureFeed(manifest), ValidatePackageAsync);
            Service.Changed += status => { if (status.Stage == AndroidUpdateStage.Ready) NotifyReady(status); };
            await Service.CheckAsync();
            Log.Info(InboundApplication.Tag, "updates fixture: " + Service.Status.Stage);
            activity.RunOnUiThread(() => { if (!activity.IsDestroyed && !activity.IsFinishing) activity.Recreate(); });
        }
        catch (Exception error) { Log.Warn(InboundApplication.Tag, "updates fixture: " + error.GetType().Name + ": " + error.Message); }
    }

    private sealed class FixtureFeed(AndroidUpdateManifest manifest) : IAndroidUpdateFeed
    {
        public Task<AndroidUpdateManifest?> ReadAsync(CancellationToken ct) => Task.FromResult<AndroidUpdateManifest?>(manifest);
        public async Task DownloadAsync(AndroidUpdateManifest offer, string path, Action<long> progress, CancellationToken ct)
        {
            await using var input = await FixtureHttp.GetStreamAsync("http://10.0.2.2:47611/candidate-2.apk", ct);
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
            var buffer = new byte[65536];
            long total = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer, ct);
                if (read == 0) break;
                total = checked(total + read);
                if (total > offer.Size) throw new InvalidDataException();
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
            }
            await output.FlushAsync(ct);
            output.Flush(true);
            progress(output.Length);
        }
    }
}
#endif
