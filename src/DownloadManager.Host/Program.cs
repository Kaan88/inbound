using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using DownloadManager.Contracts;
using DownloadManager.Core;
using DownloadManager.Core.Providers;
using DownloadManager.Persistence;
using DownloadManager.Runtime;
using DownloadManager.Runtime.Archives;
using DownloadManager.Runtime.Remote;
using DownloadManager.Tray;

namespace DownloadManager.Host;

internal static class Program
{
    /// <summary>
    /// Every program the Host opens (a downloaded file, a completion program) inherits its current
    /// directory, and Windows will not rename or remove a folder that a running program has as
    /// its current directory. Starting from app\ therefore blocked updates and uninstall for as
    /// long as anything opened from here stayed open. Work from a folder the app does not own.
    /// </summary>
    private static void LeaveProgramFolder()
    {
        try
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (Directory.Exists(profile)) Environment.CurrentDirectory = profile;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        LeaveProgramFolder();
        var configureStartup = args.FirstOrDefault(arg =>
            arg.StartsWith("--configure-sign-in-launch=", StringComparison.OrdinalIgnoreCase));
        using var mutex = new Mutex(initiallyOwned: true, PipeNames.MutexName(), out var createdNew);
        if (!createdNew)
        {
            // Never rewrite settings behind a running service's single-writer authority.
            if (configureStartup is not null) return 1;
            NotifyExistingInstance();
            return 0;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        StoragePaths paths;
        try
        {
            paths = StoragePaths.CreateDefault();
            paths.EnsureCreated();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // There is nowhere to log this: a sandbox profile whose folder can't be used, or no folder at all.
            Console.Error.WriteLine($"Host could not use a data folder: {ex.GetType().Name}");
            return 5;
        }

        var log = new FileLogger(paths.LogsDirectory);
        log.Info($"Host starting; version {Protocol.HostVersion}, state '{paths.Root}'.");

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            log.Error($"Unhandled exception: {e.ExceptionObject}");
        Application.ThreadException += (_, e) => log.Error($"UI thread exception: {e.Exception}");

        try
        {
            // Setup must apply this even when the optional postinstall launch is skipped.
            // Do not initialize the runtime: configuration must not start queued downloads.
            if (configureStartup is not null)
            {
                // A fallback data folder (roaming or next to the exe) would configure the wrong settings.
                var overridden = Environment.GetEnvironmentVariable("DOWNLOADMANAGER_DATA");
                var expectedRoot = Path.GetFullPath(string.IsNullOrWhiteSpace(overridden) ? AppFolders.DataRoot() : overridden)
                    .TrimEnd(Path.DirectorySeparatorChar);
                if (!string.Equals(Path.GetFullPath(paths.Root).TrimEnd(Path.DirectorySeparatorChar), expectedRoot, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Setup could not use the data folder {expectedRoot}; the host resolved {paths.Root}.");
                var value = configureStartup.Split('=', 2)[1];
                if (value is not ("enabled" or "disabled")) return 2;
                var store = new JsonSettingsStore(paths);
                var settings = store.LoadAsync().GetAwaiter().GetResult();
                settings.LaunchAtStartup = value == "enabled";
                store.SaveAsync(settings).GetAwaiter().GetResult();
                StartupRegistration.Apply(settings.LaunchAtStartup, StartupRegistration.HostExecutablePath());
                return 0;
            }

            var jobStore = new JsonJobStore(paths, log.Info);
            var checkpoints = new JsonCheckpointStore(paths);
            var mediaCheckpoints = new JsonMediaCheckpointStore(paths);
            var settingsStore = new JsonSettingsStore(paths);
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
                checkpoints: checkpoints,
                mediaCheckpoints: mediaCheckpoints);
            var completionExecutor = new HostCompletionActionExecutor(log.Info);
            var sleepPrevention = new WindowsSleepPrevention(log.Info);
            var ftpEngine = new DownloadManager.Engine.FtpDownloadEngine();
            var postDownloadHook = new HostPostDownloadHook(log.Info);
            var remote = RemoteProviderSetup.Create(paths, engine, log.Info);
            log.Secrets = remote.Registry;
            var service = new DownloadManagerService(
                paths, jobStore, checkpoints, settingsStore, engine, log.Info,
                completionActions: completionExecutor,
                sleepPrevention: sleepPrevention,
                mediaCheckpoints: mediaCheckpoints,
                ftpEngine: ftpEngine,
                postDownloadHook: postDownloadHook,
                linkBroker: remote.Broker,
                linkSecrets: remote.Registry);
            foreach (var provider in remote.Registry.All)
            {
                // Addresses saved earlier may turn out to hold a key that was only just entered.
                if (provider.Account is { } account)
                {
                    account.Changed += (_, _) => FireAndForget(service.ProtectStoredSecretsAsync(), log);
                }
            }

            // Attaches to the service, so it must exist before anything can finish.
            var archives = new ArchiveCoordinator(service, new JsonArchiveStore(paths, log.Warn), log.Info);
            var remoteJobs = remote.CreateCoordinator(paths, service, archives, log.Info);
            var remoteCommands = new RemoteCommands(remote.Registry, remoteJobs, log: log.Info);
            service.DirectAddresses = remoteJobs;
            service.InitializeAsync().GetAwaiter().GetResult();

            // Ordinary downloads don't depend on this. Clients just don't get the archive commands this time.
            var archivesStarted = false;
            try
            {
                archives.InitializeAsync().GetAwaiter().GetResult();
                archivesStarted = true;
            }
            catch (Exception ex)
            {
                log.Error($"Archive extraction could not start: {ex.GetType().Name}");
                archives.ShutdownAsync().GetAwaiter().GetResult();
            }

            // After the service, because reconciling can create local jobs straight away. A job left
            // Submitting by a crash becomes SubmissionUnknown here, and the provider's list decides.
            var remoteStarted = false;
            try
            {
                remoteJobs.StartAsync().GetAwaiter().GetResult();
                remoteStarted = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Ordinary downloads don't depend on this. Clients just don't get the remote commands.
                log.Warn($"Remote jobs could not start: {ex.GetType().Name}");
            }

            service.NativeHostRegistered = NativeHostRegistration.IsRegistered;
            service.TrayAvailable = true;

            var controller = new HostController(service, log);
            // Update restarts keep frontends alive (they show restart progress); a user exit closes them too.
            service.UpdateShutdownRequested += (_, _) => controller.RequestShutdown();
            var pipe = new HostPipeServer(service, controller, log, remoteStarted ? remoteCommands : null) { Archives = archivesStarted ? new ArchiveIpcDispatcher(archives) : null };
            archives.Changed += (_, change) => pipe.BroadcastArchiveChanged(change);
            void ExitApplication() => _ = Task.Run(async () =>
            {
                try { await pipe.NotifyExitingAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
                finally { controller.RequestShutdown(); }
            });
            completionExecutor.ExitHost = ExitApplication;
            pipe.Start();

            var currentSettings = service.GetSnapshot().Settings;
            using var updateMonitor = new HostUpdateMonitor(service, log.Info);
            TryApplyStartup(currentSettings.LaunchAtStartup, log);
            service.SettingsChanged += (_, next) =>
            {
                currentSettings = service.GetSnapshot().Settings;
                updateMonitor.ApplySettings(currentSettings);
                TryApplyStartup(next.LaunchAtStartup, log);
                log.Info("Settings updated.");
            };

            var trayActions = new TrayActions(
                Open: () => controller.OpenUi(),
                OpenSettings: () => controller.OpenUi("settings"),
                PauseAll: () => FireAndForget(service.PauseAllAsync(), log),
                ResumeAll: () => FireAndForget(service.ResumeAllAsync(), log),
                Exit: ExitApplication,
                IntegrationStatus: () => BrowserIntegrationText(service),
                ActiveCount: () => service.GetSnapshot().Status.ActiveDownloads,
                UpdateText: () => updateMonitor.UpdateText,
                InstallUpdate: () => _ = updateMonitor.InstallAsync(),
                CheckForUpdates: updateMonitor.CheckNow);

            var icon = LoadIcon();
            var tray = new TrayApplicationContext(trayActions, icon);
            updateMonitor.Attach(tray.Notify);
            updateMonitor.Start();
            controller.UiContext = SynchronizationContext.Current;
            if (controller.UiContext is null)
            {
                var context = new WindowsFormsSynchronizationContext();
                SynchronizationContext.SetSynchronizationContext(context);
                controller.UiContext = context;
            }

            var copiedLinks = new CopiedLinks(service, remoteJobs);
            using var clipboardMonitor = new HostClipboardMonitor(
                () => currentSettings.ClipboardMonitoring,
                url => _ = AddClipboardDownloadAsync(copiedLinks, tray, url, log),
                copiedLinks.DescribeForLog,
                log.Info);
            clipboardMonitor.Start();

            var finishedQueueSounds = new System.Collections.Concurrent.ConcurrentDictionary<int, byte>();
            using var eventSounds = new CompletionSoundPlayer(log.Warn);
            void PlayEventSound(string key, SettingsDto settings)
            {
                _ = Task.Run(async () =>
                {
                    try { await eventSounds.PlayAsync(key, settings.EventSoundFiles).ConfigureAwait(false); }
                    catch (Exception ex) { log.Warn($"Event sound failed: {ex.GetType().Name}"); }
                });
            }
            using var downloadNotices = new DownloadNotices(service, archivesStarted ? archives : null, notice =>
            {
                var current = currentSettings;

                // The kind only: a notice's title and message carry file names.
                log.Info($"Download notice: {notice.Kind}");
                switch (notice.Kind)
                {
                    case DownloadNoticeKind.Completed:
                        if (current.NotifyOnCompletion) tray.Notify(notice.Title, notice.Message);
                        if (current.PlayCompletionSound) PlayEventSound("completed", current);
                        break;
                    case DownloadNoticeKind.Failed:
                        tray.Notify(notice.Title, notice.Message);
                        if (current.EventSoundFiles.ContainsKey("failed")) PlayEventSound("failed", current);
                        break;
                    case DownloadNoticeKind.Extraction:
                        tray.Notify(notice.Title, notice.Message);
                        break;
                    case DownloadNoticeKind.PackageExtractionCompleted:
                        if (current.NotifyOnCompletion) tray.Notify(notice.Title, notice.Message);
                        break;
                }
            }, log.Warn);

            // Remote items that come to need the user, by count. Ready to choose follows the completion setting, as
            // finished downloads do; a failure always shows, as a failed download's does.
            using var remoteNotices = remoteStarted
                ? RemoteNotices.Listen(remoteJobs, TimeProvider.System, () => currentSettings.NotifyOnCompletion, AppWindowInFront.Now, notice =>
                {
                    // Counts and the provider's name only, so it can be logged as it is.
                    log.Info($"Remote notice: {notice.Title}: {notice.Message}");
                    tray.Notify(notice.Title, notice.Message);
                }, log.Warn)
                : null;
            using var downloadWindows = new DownloadWindows(service, () => currentSettings.ShowCompletionDialog, window =>
            {
                // Process launch has no WinForms affinity. Keep it off the engine's event path
                // and independent of the tray synchronization context.
                _ = Task.Run(() =>
                {
                    if (window.IsPackage) controller.OpenPackage(window.Id);
                    else controller.OpenDownload(window.Id);
                });
            }, log.Warn);
            service.JobChanged += (_, e) =>
            {
                var current = currentSettings;
                var starting = (e.ChangeType == "added" && e.Job.StartMode == "startNow") ||
                    (e.ChangeType == "progress" && e.Job.State is "connecting" or "downloading");
                if (e.ChangeType is "added" or "queued" || starting) finishedQueueSounds.TryRemove(e.Job.QueueId, out var previousQueueSound);
                if (e.ChangeType is "completed" or "failed" && e.Job.IsQueued && current.EventSoundFiles.ContainsKey("queueFinished") &&
                    service.GetSnapshot().Jobs.Where(job => job.IsQueued && job.QueueId == e.Job.QueueId)
                        .All(job => job.QueueRetryAt is null && job.State is ("completed" or "failed" or "cancelled")) &&
                    finishedQueueSounds.TryAdd(e.Job.QueueId, 0))
                    PlayEventSound("queueFinished", current);
            };

            log.Info("Host ready.");
            // Start Menu / desktop shortcuts pass --open so a fresh start shows the window;
            // a second launch already opens it through NotifyExistingInstance.
            if (args.Contains("--open", StringComparer.OrdinalIgnoreCase))
            {
                controller.OpenUi();
            }

            // A client can ask for an exit as soon as the pipe is open, before there is a loop to end.
            if (!controller.ExitWasRequested) Application.Run(tray);

            log.Info("UI loop exited; shutting down.");

            // Shutting down stops what is running, which isn't news about any package.
            downloadNotices.Dispose();
            remoteNotices?.Dispose();
            downloadWindows.Dispose();
            try
            {
                pipe.StopAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                log.Warn($"Pipe shutdown: {ex.Message}");
            }

            try
            {
                archives.ShutdownAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                log.Error($"Archive shutdown: {ex.GetType().Name}");
            }

            try
            {
                remoteJobs.StopAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                log.Warn($"Remote jobs shutdown: {ex.GetType().Name}");
            }

            try
            {
                service.ShutdownAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                log.Error($"Service shutdown: {ex.Message}");
            }

            remoteCommands.Dispose();
            sleepPrevention.Dispose();
            remote.Dispose();
            engine.Dispose();
            tray.ExitNow();
            log.Info("Host stopped.");
            return 0;
        }
        catch (Exception ex)
        {
            log.Error($"Host failed to start: {ex}");
            if (!args.Contains("--background", StringComparer.OrdinalIgnoreCase))
            {
                MessageBox.Show($"Inbound Download Manager host failed to start:\r\n{ex.Message}",
                    "Inbound Download Manager", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            return 1;
        }
        finally
        {
            try
            {
                mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // not the owner
            }
        }
    }

    private static async Task AddClipboardDownloadAsync(
        CopiedLinks copiedLinks,
        TrayApplicationContext tray,
        string url,
        FileLogger log)
    {
        try
        {
            var outcome = await copiedLinks.AddAsync(url).ConfigureAwait(false);
            if (outcome.Title is { } title)
            {
                tray.Notify(title, outcome.Words ?? string.Empty);
            }

            if (outcome.Problem is { } problem)
            {
                log.Warn(problem);
            }
        }
        catch (Exception ex)
        {
            log.Error($"Clipboard download failed ({copiedLinks.DescribeForLog(url)}): {UrlRedactor.RedactText(ex.ToString())}");
        }
    }

    private static void NotifyExistingInstance()
    {
        IpcClient? client = null;
        try
        {
            client = IpcClient.ConnectAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            var message = IpcMessage.Create(MessageTypes.HostOpenUi,
                new HostActionRequest { Action = string.Empty });
            client.SendAsync(message).GetAwaiter().GetResult();
            client.ReadAsync().Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            // The existing process may still be starting; it will be found by the next attempt.
        }
        finally
        {
            if (client is not null)
            {
                client.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
    }

    private static void TryApplyStartup(bool enabled, FileLogger log)
    {
        // Isolated test/portable state must not replace or delete the installed user's Run entry.
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOWNLOADMANAGER_DATA")))
        {
            return;
        }

        try
        {
            StartupRegistration.Apply(enabled, StartupRegistration.HostExecutablePath());
        }
        catch (Exception ex)
        {
            log.Warn($"Could not update sign-in startup: {ex.Message}");
        }
    }

    private static string BrowserIntegrationText(DownloadManagerService service)
    {
        var settings = service.GetSnapshot().Settings;
        if (!settings.BrowserIntegrationEnabled)
        {
            return "disabled";
        }

        if (!NativeHostRegistration.IsRegistered())
        {
            return "native host not registered";
        }

        return "connected";
    }

    private static void FireAndForget(Task task, FileLogger log)
    {
        _ = task.ContinueWith(t =>
        {
            if (t.Exception is not null)
            {
                log.Error($"Tray action failed: {t.Exception.GetBaseException().Message}");
            }
        }, TaskScheduler.Default);
    }

    private static Icon? LoadIcon()
    {
        var candidate = Path.Combine(AppContext.BaseDirectory, "app.ico");
        if (File.Exists(candidate))
        {
            return new Icon(candidate);
        }

        return Environment.ProcessPath is { } process
            ? Icon.ExtractAssociatedIcon(process)
            : SystemIcons.Application;
    }
}
