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
        using var mutex = new Mutex(initiallyOwned: true, PipeNames.MutexName(), out var createdNew);
        if (!createdNew)
        {
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
            var jobStore = new JsonJobStore(paths, log.Info);
            var checkpoints = new JsonCheckpointStore(paths);
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
                checkpoints: checkpoints);
            var completionExecutor = new HostCompletionActionExecutor(log.Info);
            var service = new DownloadManagerService(
                paths, jobStore, checkpoints, settingsStore, engine, log.Info,
                completionActions: completionExecutor);
            service.InitializeAsync().GetAwaiter().GetResult();
            service.NativeHostRegistered = NativeHostRegistration.IsRegistered;
            service.TrayAvailable = true;

            var controller = new HostController(service, log);
            completionExecutor.ExitHost = controller.RequestShutdown;
            var pipe = new HostPipeServer(service, controller, log);
            pipe.Start();

            var settings = service.GetSnapshot().Settings;
            TryApplyStartup(settings.LaunchAtStartup, log);
            service.SettingsChanged += (_, next) =>
            {
                TryApplyStartup(next.LaunchAtStartup, log);
                log.Info("Settings updated.");
            };

            var trayActions = new TrayActions(
                Open: () => controller.OpenUi(),
                OpenSettings: () => controller.OpenUi("settings"),
                PauseAll: () => FireAndForget(service.PauseAllAsync(), log),
                ResumeAll: () => FireAndForget(service.ResumeAllAsync(), log),
                Exit: controller.RequestShutdown,
                IntegrationStatus: () => BrowserIntegrationText(service),
                ActiveCount: () => service.GetSnapshot().Status.ActiveDownloads);

            var icon = LoadIcon();
            var tray = new TrayApplicationContext(trayActions, icon);
            controller.UiContext = SynchronizationContext.Current;
            if (controller.UiContext is null)
            {
                var context = new WindowsFormsSynchronizationContext();
                SynchronizationContext.SetSynchronizationContext(context);
                controller.UiContext = context;
            }

            service.JobChanged += (_, e) =>
            {
                if (e.ChangeType == "completed")
                {
                    tray.Notify("Download complete", e.Job.FileName);
                }
                else if (e.ChangeType == "failed")
                {
                    tray.Notify("Download failed", $"{e.Job.FileName}: {e.Job.Failure?.Message}");
                }
            };

            log.Info("Host ready.");
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
                MessageBox.Show($"Download Manager host failed to start:\r\n{ex.Message}",
                    "Download Manager", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
