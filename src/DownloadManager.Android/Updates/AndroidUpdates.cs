using Android.App;
using Android.App.Job;
using Android.Content;
using Android.Content.PM;
using Android.Util;
using AndroidX.Core.Content;
using DownloadManager.Core;
using DownloadManager.Mobile.Updates;
using Google.Android.Material.Dialog;
using System.Net;
using System.Security.Cryptography;
using System.IO.Compression;

namespace Inbound.App.Updates;

/// <summary>The app updates itself, regardless of which download Host is shown. No downloader job or user file is involved.</summary>
internal static partial class AndroidUpdates
{
    internal const int DailyJobId = 4001;
    private const int NotificationId = 4002;
    private const string Channel = "app_updates";
    private static Context _app = null!;
    private static int _launchChecked;
    private static readonly SemaphoreSlim InstallGate = new(1, 1);
    private static WeakReference<MainActivity>? _resumed;
    private static CancellationTokenSource? _foreground;
    private static Android.App.Dialog? _permissionDialog;
    public const string ShowUpdatesExtra = "show_app_updates";
    public static AndroidUpdater Service { get; private set; } = null!;

    public static void Initialize(Context context)
    {
        _app = context.ApplicationContext!;
        var package = _app.PackageManager!.GetPackageInfo(_app.PackageName!, PackageInfoFlags.SigningCertificates)!;
        var http = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        }) { Timeout = Timeout.InfiniteTimeSpan };
        Service = new AndroidUpdater(Path.Combine(_app.FilesDir!.AbsolutePath, "updates"), ReleaseUpdates.PublicKey,
            package.LongVersionCode, new GithubAndroidFeed(http), ValidatePackageAsync);
        Service.Changed += status =>
        {
            if (status.Stage == AndroidUpdateStage.Ready) NotifyReady(status);
            else if (status.Stage == AndroidUpdateStage.Current)
                ((NotificationManager)_app.GetSystemService(Context.NotificationService)!).Cancel(NotificationId);
        };
        Service.Log = message => Android.Util.Log.Info("InboundUpdates", message);
        Service.Withdrawn += _ => ((NotificationManager)_app.GetSystemService(Context.NotificationService)!).Cancel(NotificationId);
        var scheduler = (JobScheduler)_app.GetSystemService(Context.JobSchedulerService)!;
        // A pending job from an older version with another definition is replaced (review A7).
        var pending = scheduler.GetPendingJob(DailyJobId);
        if (pending is null || pending.IntervalMillis != (long)TimeSpan.FromDays(1).TotalMilliseconds ||
            pending.FlexMillis != (long)TimeSpan.FromHours(2).TotalMilliseconds || !pending.IsPersisted ||
            pending.NetworkType != NetworkType.Any ||
            pending.Service?.ClassName != Java.Lang.Class.FromType(typeof(UpdateCheckJob)).Name)
        {
            var info = new JobInfo.Builder(DailyJobId, new ComponentName(_app, Java.Lang.Class.FromType(typeof(UpdateCheckJob))))
                .SetPeriodic((long)TimeSpan.FromDays(1).TotalMilliseconds, (long)TimeSpan.FromHours(2).TotalMilliseconds)!
                .SetRequiredNetworkType(NetworkType.Any)!.SetPersisted(true)!.Build()!;
            if (scheduler.Schedule(info) != JobScheduler.ResultSuccess)
                Log.Warn(InboundApplication.Tag, "updates: daily check could not be scheduled");
        }
    }

    public static void OnAppVisible()
    {
        // Exactly once per actual user-opened process; activity recreation/warm resume does not recheck.
        // A process started only by a transfer job does not consume the user's cold-launch check.
        if (Interlocked.Exchange(ref _launchChecked, 1) == 0)
        {
            Log.Info(InboundApplication.Tag, "updates: cold app launch check");
            _ = CheckNowAsync();
        }
    }

    public static Task CheckNowAsync() => Service.CheckAsync();

    public static bool TryClaimPrompt(string version)
    {
        var preferences = _app.GetSharedPreferences("app-updates", FileCreationMode.Private)!;
        var same = preferences.GetString("prompt-version", "") == version;
        var previous = preferences.GetLong("prompt-time", 0);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (same && now >= previous && now - previous < 86400) return false;
        preferences.Edit()!.PutString("prompt-version", version)!.PutLong("prompt-time", now)!.Apply();
        return true;
    }

    public static Task InstallAsync(MainActivity activity)
    {
        var selected = Service.ReadyOffer;
        return InstallSelectedAsync(activity, selected?.Manifest.VersionCode, selected?.Manifest.Sha256);
    }

    public static Task InstallAsync(MainActivity activity, long versionCode, string hash) => InstallSelectedAsync(activity, versionCode, hash);

    private static async Task InstallSelectedAsync(MainActivity activity, long? versionCode, string? hash)
    {
        if (!IsResumed(activity) || _foreground is null) return;
        var ct = _foreground.Token;
        if (!await InstallGate.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            var ready = await Service.ReadyForInstallAsync(versionCode, hash, ct).ConfigureAwait(false);
            if (ready is null) { activity.ShowMessage("Check for updates again before installing."); return; }
            await DispatchAsync(activity, () =>
            {
                ct.ThrowIfCancellationRequested();
                if (!IsResumed(activity)) return;
                if (!activity.PackageManager!.CanRequestPackageInstalls())
                {
                    _permissionDialog?.Dismiss();
                    _permissionDialog = new MaterialAlertDialogBuilder(activity).SetTitle("Allow Inbound to install its update")!
                        .SetMessage("Android needs your permission to install apps from Inbound. After allowing it, return here to install the verified update.")!
                        .SetPositiveButton("Open settings", (_, _) =>
                        {
                            try
                            {
                                if (!IsResumed(activity)) return;
                                _app.GetSharedPreferences("app-updates", FileCreationMode.Private)!.Edit()!
                                    .PutLong("install-code", ready.Manifest.VersionCode)!
                                    .PutString("install-hash", ready.Manifest.Sha256)!
                                    .PutLong("install-time", DateTimeOffset.UtcNow.ToUnixTimeSeconds())!.Apply();
                                var settings = new Intent(Android.Provider.Settings.ActionManageUnknownAppSources,
                                    Android.Net.Uri.Parse("package:" + activity.PackageName));
                                activity.StartActivity(settings);
                            }
                            catch (Exception)
                            {
                                ClearPendingInstall();
                                activity.ShowMessage("Open Android Settings to allow installation from Inbound.");
                            }
                        })!.SetNegativeButton("Later", (IDialogInterfaceOnClickListener?)null)!.Show();
                    return;
                }
                var uri = FileProvider.GetUriForFile(activity, activity.PackageName + ".fileprovider", new Java.IO.File(ready.Path));
                var install = new Intent(Intent.ActionView).SetDataAndType(uri, "application/vnd.android.package-archive");
                install!.AddFlags(ActivityFlags.GrantReadUriPermission);
                try { activity.StartActivity(install); }
                catch (ActivityNotFoundException) { activity.ShowMessage("Android's app installer could not be opened."); }
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { activity.ShowMessage("The update could not be opened. Check for updates again."); }
        finally { InstallGate.Release(); }
    }

    public static void OnResumed(MainActivity activity)
    {
        _resumed = new(activity);
        _foreground?.Dispose();
        _foreground = new();
        var preferences = _app.GetSharedPreferences("app-updates", FileCreationMode.Private)!;
        var code = preferences.GetLong("install-code", 0);
        var hash = preferences.GetString("install-hash", null);
        var time = preferences.GetLong("install-time", 0);
        if (code == 0) return;
        ClearPendingInstall();
        var age = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - time;
        if (age is >= 0 and <= 600 && hash is not null && activity.PackageManager!.CanRequestPackageInstalls())
            _ = InstallSelectedAsync(activity, code, hash);
    }

    public static void OnPaused(MainActivity activity)
    {
        if (!IsResumed(activity)) return;
        _resumed = null;
        _foreground?.Cancel();
        _foreground?.Dispose();
        _foreground = null;
        _permissionDialog?.Dismiss();
        _permissionDialog = null;
    }

    private static bool IsResumed(MainActivity activity) => !activity.IsDestroyed && !activity.IsFinishing &&
        _resumed?.TryGetTarget(out var current) == true && ReferenceEquals(current, activity);

    private static void ClearPendingInstall() => _app.GetSharedPreferences("app-updates", FileCreationMode.Private)!.Edit()!
        .Remove("install-code")!.Remove("install-hash")!.Remove("install-time")!.Apply();

    private static Task DispatchAsync(MainActivity activity, Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        activity.RunOnUiThread(() =>
        {
            try { action(); completion.TrySetResult(); }
            catch (Exception error) { completion.TrySetException(error); }
        });
        return completion.Task;
    }

    private static Task ValidatePackageAsync(string path, AndroidUpdateManifest manifest, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        const string releaseAbi = "arm64-v8a";
#if INBOUND_TEST_BUILD
        var expectedAbi = Android.OS.Build.SupportedAbis?[0] ?? releaseAbi;
#else
        var expectedAbi = releaseAbi;
#endif
        using (var zip = ZipFile.OpenRead(path))
        {
            var native = zip.Entries.Where(e => e.FullName.StartsWith("lib/", StringComparison.Ordinal) &&
                e.FullName.EndsWith(".so", StringComparison.Ordinal)).Select(e => e.FullName.Split('/')[1]).ToArray();
            if (native.Length == 0 || native.Any(abi => abi != expectedAbi))
                throw new InvalidDataException("Update targets the wrong CPU architecture.");
        }
        var pm = _app.PackageManager!;
        var archive = pm.GetPackageArchiveInfo(path, PackageInfoFlags.SigningCertificates)
            ?? throw new InvalidDataException("Update is not an Android package.");
        var signers = archive.SigningInfo?.GetApkContentsSigners();
        if (archive.PackageName != AndroidUpdateTrust.PackageName || archive.LongVersionCode != manifest.VersionCode ||
            archive.VersionName != manifest.VersionName || archive.ApplicationInfo is not { } app ||
            (app.Flags & ApplicationInfoFlags.Debuggable) != 0 || (int)app.MinSdkVersion < 31 ||
            (int)app.MinSdkVersion > (int)Android.OS.Build.VERSION.SdkInt || signers is not { Length: 1 } ||
            !Convert.ToHexString(SHA256.HashData(signers[0].ToByteArray()!)).Equals(AndroidUpdateTrust.CertificateSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Update package identity, version or signer is incorrect.");
        var installed = pm.GetPackageInfo(_app.PackageName!, PackageInfoFlags.SigningCertificates)!.SigningInfo?.GetApkContentsSigners();
        if (installed is not { Length: 1 } || !Convert.ToHexString(SHA256.HashData(installed[0].ToByteArray()!))
                .Equals(AndroidUpdateTrust.CertificateSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("This test/debug installation cannot use permanent-key updates.");
        return Task.CompletedTask;
    }

    private static void NotifyReady(AndroidUpdateStatus status)
    {
        try
        {
            var manager = (NotificationManager)_app.GetSystemService(Context.NotificationService)!;
            manager.CreateNotificationChannel(new NotificationChannel(Channel, "App updates", NotificationImportance.Default));
            var intent = new Intent(_app, typeof(MainActivity)).AddFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop)
                .PutExtra(ShowUpdatesExtra, true);
            var pending = PendingIntent.GetActivity(_app, NotificationId, intent, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable)!;
            var notification = new Notification.Builder(_app, Channel).SetSmallIcon(Android.Resource.Drawable.StatSysDownloadDone)!
                .SetContentTitle("Inbound update ready")!.SetContentText(status.Text)!.SetContentIntent(pending)!
                .SetAutoCancel(true)!.SetOnlyAlertOnce(true)!.Build();
            manager.Notify(NotificationId, notification);
        }
        catch (Exception) { /* Notification permission is optional; the in-app prompt and Settings still work. */ }
    }
}
