using DownloadManager.Contracts;
using DownloadManager.Core;
using DownloadManager.Mobile;
using DownloadManager.Persistence;
using DownloadManager.Runtime;
using DownloadManager.Runtime.Ipc;
using DownloadManager.Runtime.Sites;

namespace Inbound.App.Host;

/// <summary>What the phone can do for the Host: nothing that needs a window, a tray, a picker or a shell (the app is the UI).</summary>
internal sealed class AndroidPlatform : IHostPlatform
{
    public PlatformCapabilities Capabilities { get; } = new()
    {
        SupportsOpenUi = false,
        SupportsFolderPicker = false,
        SupportsTray = false,
        SupportsShutdown = false,
        SupportsUpdater = false,
        SupportsProgramSettings = false,
        SupportsFolderBrowse = true,
        SupportsCompletionActions = false,
        SupportsReveal = false,
        SupportsMeteredNetworkRule = true,
        SupportsQueueConditions = true,
        SupportsQueueCompletionNotifications = true,
        HostKind = HostKinds.Server,
    };

    public void OpenUi(string? page = null) => throw new NotSupportedException();

    public void RequestShutdown() => throw new NotSupportedException();

    public Task<FolderPickResult> PickFolderAsync(string? initialPath, CancellationToken cancellationToken) =>
        Task.FromResult(new FolderPickResult { Error = "Picking a folder is not built yet." });
}

internal sealed class DelegateLog(Action<string> say) : IHostLog
{
    public void Info(string message) => say("[host] " + message);

    public void Warn(string message) => say("[host WARN] " + message);

    public void Error(string message) => say("[host ERROR] " + message);
}

/// <summary>
/// The Host core on Android, in this process: DownloadManagerService + HostCommandDispatcher composed as
/// src/DownloadManager.Server/ServerApp.cs composes them, minus the HTTP front, Torrents, TorBox and Archives. A client is an
/// <see cref="InProcessSession"/> talking to the dispatcher with the same IpcMessage envelopes a pipe or WebSocket carries.
/// For now it lives and dies with the process (no foreground service yet).
/// </summary>
/// <summary>A start failed and what it had started could not be stopped: starting again in this process could make two writers.</summary>
internal sealed class HostLeftRunningException(Exception startError, Exception shutdownError)
    : Exception("The downloader could not start, and could not be stopped cleanly; close the app and open it again. (" + startError.Message + ")",
        new AggregateException(startError, shutdownError));

internal sealed class AndroidHost : IAsyncDisposable
{
    /// <summary>The shared Download folder, by path (owner's decision for the default destination).</summary>
    public static string SharedDownloadFolder { get; } =
        Android.OS.Environment.GetExternalStoragePublicDirectory(Android.OS.Environment.DirectoryDownloads)?.AbsolutePath ?? "/storage/emulated/0/Download";

    private readonly HttpDownloadEngine _engine;
    private readonly SitesCommands? _sites;
    private readonly ISleepPrevention? _siteSleep;
    private readonly Action<string> _say;

    private AndroidHost(DownloadManagerService service, HostCommandDispatcher dispatcher, HttpDownloadEngine engine, SitesCommands? sites, ISleepPrevention? siteSleep, ActivityState activity,
        ISleepPrevention sleep, EventHandler<JobChangeEventArgs> onJob, EventHandler<bool> onWait, EventHandler<AppSettings> onSettings, Action<string> say)
    {
        Service = service;
        Dispatcher = dispatcher;
        _engine = engine;
        _sites = sites;
        _siteSleep = siteSleep;
        Activity = activity;
        _sleep = sleep;
        _onJob = onJob;
        _onWait = onWait;
        _onSettings = onSettings;
        _say = say;
    }

    private readonly ISleepPrevention _sleep;
    private readonly EventHandler<JobChangeEventArgs> _onJob;
    private readonly EventHandler<bool> _onWait;
    private readonly EventHandler<AppSettings> _onSettings;

    /// <summary>What the keep-alive and the notifications read: fed by the service's job events, snapshot first.</summary>
    public ActivityState Activity { get; }

    public DownloadManagerService Service { get; }

    public HostCommandDispatcher Dispatcher { get; }

    /// <param name="stateRoot">The app's private folder for settings, job list and checkpoints (metadata only).</param>
    /// <param name="defaultDownloadFolder">Becomes the default download folder while that setting is still the built-in default (nobody set one).</param>
    /// <param name="sleep">Held by the Runtime while downloads run and the setting is on (wake lock, Wi-Fi lock).</param>
    /// <param name="lastRunStoppedByUser">The previous run was stopped by the user: downloads that were running start Paused.</param>
    /// <param name="networkMetered">The network as it is now, reported to the Runtime before InitializeAsync.</param>
    public static async Task<AndroidHost> StartAsync(string stateRoot, string defaultDownloadFolder, ISleepPrevention sleep,
        bool lastRunStoppedByUser, bool networkMetered, Action<string> say, CancellationToken ct)
    {
        var paths = new StoragePaths(stateRoot);
        paths.EnsureCreated();
        var keyDir = Path.Combine(stateRoot, "key");
        Directory.CreateDirectory(keyDir);
        SecretProtector.Use(AesGcmSecretProtector.OpenForDataFolder(
            Path.Combine(keyDir, AesGcmSecretProtector.DefaultFileName), keyDir, discardUnreadable: false));

        var log = new DelegateLog(say);
        IJobStore jobStore = new JsonJobStore(paths, log.Info);
        var checkpointStore = new JsonCheckpointStore(paths);
        ISettingsStore settingsStore = new JsonSettingsStore(paths);
        var engine = new HttpDownloadEngine(
            new EngineOptions
            {
                MaxAttempts = 5,
                InitialRetryDelay = TimeSpan.FromSeconds(2),
                MaxRetryDelay = TimeSpan.FromSeconds(60),
                ConnectTimeout = TimeSpan.FromSeconds(20),
                ReadTimeout = TimeSpan.FromSeconds(90),
                AllowLocalNetwork = true,
            },
            checkpoints: checkpointStore);
        DownloadManagerService service;
        try
        {
            service = new DownloadManagerService(paths, jobStore, checkpointStore, settingsStore, engine, log.Info, sleepPrevention: sleep);
        }
        catch
        {
            engine.Dispose();
            throw;
        }

        // Subscribe first, then take the snapshot (ActivityState ignores events that come before its snapshot).
        var activity = new ActivityState();
        EventHandler<JobChangeEventArgs> onJob = (_, e) => activity.OnJobChanged(e.Job, e.ChangeType);
        EventHandler<bool> onWait = (_, waiting) => activity.SetNetworkWait(waiting);
        // A queue started or stopped (or the queues changed): its queued jobs do or don't keep the process alive (Fable finding 8).
        SitesCommands? sites = null;
        EventHandler<AppSettings> onSettings = (_, settings) =>
        {
            activity.SetQueues(DownloadManagerService.MapSettings(settings).Queues);
            sites?.Transfers.RefreshSleep();
        };
        service.JobChanged += onJob;
        service.MeteredWaitChanged += onWait;
        service.SettingsChanged += onSettings;
        ISleepPrevention? siteSleep = null;
        try
        {
            // Before InitializeAsync: only stored there, and the first Schedule after reconcile is already held.
            Background.InboundBackground.RegisterStartingService(service);
            await service.SetNetworkMeteredAsync(networkMetered, ct).ConfigureAwait(false);
            var power = Background.QueuePowerMonitor.Read(Android.App.Application.Context);
            await service.SetQueuePowerStateAsync(power.Charging, power.BatterySaver, ct).ConfigureAwait(false);
            await service.InitializeAsync(lastRunStoppedByUser, ct).ConfigureAwait(false);
            service.TrayAvailable = false;

            // Default destination while none was ever set: the shared Download folder, directly (owner, 2026-11-16: no
            // subfolder, so no Video/ or Compressed/ category folders either). Decided by the setting itself, not by whether a
            // settings file exists, so a first start that wrote settings but died before this (or whose update was refused)
            // is put right at the next start. The built-in default is the app's private folder, which nobody can pick or see.
            if (service.GetSettings().DefaultDownloadDirectory == AppSettings.GetDefaultDownloadDirectory())
            {
                var settings = service.GetSettings();
                settings.DefaultDownloadDirectory = defaultDownloadFolder;
                settings.UseCategoryFolders = false;
                // First run: keep the CPU and the radio awake while downloading (the model default is already on; this makes
                // the first-run choice explicit, and a later change by the user is never overwritten, since it only runs here).
                settings.PreventSleepWhileDownloading = true;
                var result = await service.UpdateSettingsAsync(settings, ct).ConfigureAwait(false);
                if (!result.Ok) say("[host WARN] default download folder not set: " + result.Error);
            }

            var snapshot = service.GetSnapshot();
            activity.ApplySnapshot(snapshot.Jobs, snapshot.Status, snapshot.Settings.Queues);
            // Never fetch or run an executable from writable app storage on Android.
            // Test/other ABI builds without the bundled binary do not advertise this capability.
            var nativeLibraries = Android.App.Application.Context.ApplicationInfo?.NativeLibraryDir;
            var bundledRclone = nativeLibraries is null ? null : Path.Combine(nativeLibraries, "librclone.so");
            if (File.Exists(bundledRclone))
            {
                siteSleep = new Background.AndroidSleepPrevention(Android.App.Application.Context);
                sites = new SitesCommands(paths.Root, log.Info, findRclone: _ => bundledRclone,
                    sleepPrevention: siteSleep, preventSleep: () => service.GetSettings().PreventSleepWhileDownloading,
                    transferExecutionAllowed: false
#if INBOUND_TEST_BUILD && INBOUND_RCLONE_PROBE
                    , afterTransferExclusiveOpen: TestHooks.HoldRemoteFilesPublicationAsync, allowWebDavUploadProbe: true
#endif
                    );
            }
            var dispatcher = new HostCommandDispatcher(service, new AndroidPlatform(), log)
            {
                Folders = new FolderBrowser(new PathRoots([SharedDownloadFolder]) { Called = "this phone's Download folder" }),
                Sites = sites,
                SitesRoots = sites is null ? null : new PathRoots([SharedDownloadFolder]) { Called = "this phone's Download folder" },
            };
            if (sites is not null)
            {
                sites.Changed += (_, change) => dispatcher.BroadcastSitesChanged(change);
                sites.TransfersChanged += (_, change) => dispatcher.BroadcastSiteTransfersChanged(change);
                sites.Transfers.ActivityChanged += service.OnSiteTransfersActivityChanged;
                void RefreshSiteActivity() => activity.SetRemoteFilesActive(sites.Transfers.HasRunningTransfers);
                sites.Transfers.ActivityChanged += RefreshSiteActivity;
                RefreshSiteActivity();
                sites.Transfers.Measured += service.OnSiteTransfersMeasured;
                service.AttachSiteTransfers(sites.Transfers.Demand, sites.Transfers.SetSpeedLimit);
            }
            dispatcher.Start();
            return new AndroidHost(service, dispatcher, engine, sites, siteSleep, activity, sleep, onJob, onWait, onSettings, say);
        }
        catch (Exception startError)
        {
            Background.InboundBackground.ForgetStartingService(service);
            service.JobChanged -= onJob;
            service.MeteredWaitChanged -= onWait;
            service.SettingsChanged -= onSettings;
            activity.Dispose();
            // Nothing half-started may keep writing to the state folder: a later start must be the only writer.
            Exception? sitesShutdownError = null;
            try { if (sites is not null) await sites.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { sitesShutdownError = ex; }
            finally { siteSleep?.Dispose(); }
            try { await service.ShutdownAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex)
            {
                say("[host] shutdown after a failed start: " + ex.GetType().Name + ": " + ex.Message);
                engine.Dispose();
                // Something may still be running over the state folder: no second start in this process.
                throw new HostLeftRunningException(startError, sitesShutdownError is null ? ex : new AggregateException(sitesShutdownError, ex));
            }

            engine.Dispose();
            if (sitesShutdownError is not null) throw new HostLeftRunningException(startError, sitesShutdownError);
            throw;
        }
    }

    internal Task PauseRemoteFilesAsync() => _sites?.Transfers.PauseAllAsync("Paused from the notification.") ?? Task.CompletedTask;

    internal void SetRemoteFilesExecutionAllowed(bool allowed) => _sites?.Transfers.SetExecutionAllowed(allowed);
    internal bool RemoteFilesExecutionIntent => _sites?.Transfers.HasExecutionIntent == true;

    public async ValueTask DisposeAsync()
    {
        Dispatcher.Stop();
        Service.JobChanged -= _onJob;
        Service.MeteredWaitChanged -= _onWait;
        Service.SettingsChanged -= _onSettings;
        Activity.Dispose();
        try { if (_sites is not null) await _sites.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { _say("[host] remote files shutdown: " + ex.GetType().Name); }
        finally { _siteSleep?.Dispose(); }
        try { await Service.ShutdownAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { _say("[host] shutdown: " + ex.GetType().Name + ": " + ex.Message); }
        _engine.Dispose();
        _sleep.Dispose();
    }
}
