using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using DownloadManager.Contracts;
using DownloadManager.Core;
using DownloadManager.Persistence;
using DownloadManager.Runtime;
using DownloadManager.Tray;

namespace DownloadManager.Host;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
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

        var paths = StoragePaths.CreateDefault();
        paths.EnsureCreated();
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
            var service = new DownloadManagerService(
                paths, jobStore, checkpoints, settingsStore, engine, log.Info,
                completionActions: completionExecutor,
                sleepPrevention: sleepPrevention,
                mediaCheckpoints: mediaCheckpoints,
                ftpEngine: ftpEngine,
                postDownloadHook: postDownloadHook);
            service.InitializeAsync().GetAwaiter().GetResult();
            service.NativeHostRegistered = NativeHostRegistration.IsRegistered;
            service.TrayAvailable = true;

            var controller = new HostController(service, log);
            // Update restarts keep frontends alive (they show restart progress); a user exit closes them too.
            service.UpdateShutdownRequested += (_, _) => controller.RequestShutdown();
            var pipe = new HostPipeServer(service, controller, log);
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

            using var clipboardMonitor = new HostClipboardMonitor(
                () => currentSettings.ClipboardMonitoring,
                url => _ = AddClipboardDownloadAsync(service, tray, url, log),
                log.Info);
            clipboardMonitor.Start();

            var shownDownloads = new System.Collections.Concurrent.ConcurrentDictionary<Guid, byte>();
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
            service.JobChanged += (_, e) =>
            {
                var current = currentSettings;
                // CLI callers remain headless. Interactive downloads get a separate
                // per-job frontend; launching again activates the same job window.
                var starting = (e.ChangeType == "added" && e.Job.StartMode == "startNow") ||
                    (e.ChangeType == "progress" && e.Job.State is "connecting" or "downloading");
                if (e.ChangeType is "added" or "queued" || starting) finishedQueueSounds.TryRemove(e.Job.QueueId, out var previousQueueSound);
                if (e.Job.Source != "cli" &&
                    ((starting && shownDownloads.TryAdd(e.Job.Id, 0)) ||
                     (e.ChangeType == "completed" && (e.Job.ShowCompletionDialog ?? current.ShowCompletionDialog))))
                {
                    // Process launch has no WinForms affinity. Keep it off the engine's
                    // event path and independent of the tray synchronization context.
                    _ = Task.Run(() => controller.OpenDownload(e.Job.Id));
                }
                if (e.ChangeType is "paused" or "cancelled" or "failed" or "completed" or "removed")
                    shownDownloads.TryRemove(e.Job.Id, out var ignored);
                if (e.ChangeType == "completed")
                {
                    if (current.NotifyOnCompletion)
                    {
                        tray.Notify("Download complete", e.Job.FileName);
                    }

                    if (current.PlayCompletionSound)
                    {
                        PlayEventSound("completed", current);
                    }
                }
                else if (e.ChangeType == "failed")
                {
                    tray.Notify("Download failed", $"{e.Job.FileName}: {e.Job.Failure?.Message}");
                    if (current.EventSoundFiles.ContainsKey("failed")) PlayEventSound("failed", current);
                }
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

            Application.Run(tray);

            log.Info("UI loop exited; shutting down.");
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
                service.ShutdownAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                log.Error($"Service shutdown: {ex.Message}");
            }

            sleepPrevention.Dispose();
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
        DownloadManagerService service,
        TrayApplicationContext tray,
        string url,
        FileLogger log)
    {
        try
        {
            var result = await service.AddJobAsync(new AddJobRequest
            {
                Url = url,
                Source = "clipboardMonitor",
                StartMode = "now",
            }).ConfigureAwait(false);

            if (result.Accepted && !result.Duplicate)
            {
                tray.Notify("Clipboard download added", result.Job?.FileName ?? url);
            }
            else if (!result.Accepted)
            {
                log.Warn($"Clipboard URL rejected ({url}): {result.Error}");
            }
        }
        catch (Exception ex)
        {
            log.Error($"Clipboard download failed ({url}): {ex}");
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
