using System.Runtime.InteropServices;
using DownloadManager.Contracts;
using DownloadManager.Core;
using DownloadManager.Persistence;
using DownloadManager.Runtime;
using DownloadManager.Runtime.Archives;
using DownloadManager.Runtime.Ipc;
using DownloadManager.Runtime.Remote;

namespace DownloadManager.Host;

/// <summary>
/// The Linux desktop Host: one per user, the single writer of the data folder, serving the window, the CLI and the
/// browser bridge over Unix sockets. Composed as the Windows Host is (<c>DownloadManager.Host/Program.cs</c>), in the
/// same start and stop order, without what has no Linux part yet.
/// <list type="bullet">
/// <item><c>--open</c>: open the window once ready (or, when a Host is already running, ask it to).</item>
/// <item><c>--background</c>: start without opening anything (sign-in start).</item>
/// </list>
/// Exit codes: 0 stopped normally, 1 failed, 3 another Host has the data folder and couldn't be reached, 5 no data folder.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsLinux())
        {
            Console.Error.WriteLine("This is the Linux Host; on Windows, run DownloadManager.Host.");
            return 1;
        }

        // What the Host opens inherits this; the program's own folder (an AppImage mount) isn't one to hold.
        Environment.CurrentDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var open = args.Contains("--open", StringComparer.Ordinal);

        StoragePaths paths;
        try
        {
            paths = StoragePaths.CreateDefault();
            paths.EnsureCreated();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Console.Error.WriteLine($"The Host could not use a data folder: {ex.GetType().Name}");
            return 5;
        }

        // One Host per data folder, as the Windows Host's mutex gives it. .NET takes an exclusive flock() here.
        using var instanceLock = InstanceLock.TryAcquire(paths.Root);
        if (instanceLock is null)
        {
            return await AskRunningHostAsync(open).ConfigureAwait(false) ? 0 : 3;
        }

        var log = new FileLogger(paths.LogsDirectory);
        log.Info($"Linux Host starting; version {Protocol.HostVersion}, state '{paths.Root}'.");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => log.Error($"Unhandled exception: {e.ExceptionObject}");

        try
        {
            return await RunAsync(paths, log, open).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.Error($"Host failed to start: {ex}");
            Console.Error.WriteLine($"Inbound's Host failed to start: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> RunAsync(StoragePaths paths, FileLogger log, bool open)
    {
        // There is no DPAPI here: saved keys and passwords are sealed with a key file in the data folder, as on the
        // server. [TODO D7] Keep that key in the Secret Service (libsecret) when the session has one.
        var keyFile = Path.Combine(paths.Root, AesGcmSecretProtector.DefaultFileName);
        SecretProtector.Use(AesGcmSecretProtector.OpenForDataFolder(keyFile, paths.Root, discardUnreadable: false, log.Warn));

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
        // PERF.6: one budget for the shared speed cap, so it covers HTTP and FTP downloads together.
        var ftpEngine = new DownloadManager.Engine.FtpDownloadEngine(engine.SpeedBudget);
        var remote = RemoteProviderSetup.Create(paths, engine, log.Info);
        log.Secrets = remote.Registry;
        using var notifications = new DesktopNotifications(log.Warn);
        var completionExecutor = new LinuxCompletionActionExecutor(log.Info) { Notice = notifications.Notify };
        using var sleepPrevention = new LogindSleepPrevention(log.Info);
        var service = new DownloadManagerService(
            paths, jobStore, checkpoints, settingsStore, engine, log.Info,
            completionActions: completionExecutor,
            sleepPrevention: sleepPrevention,
            postDownloadHook: new HostPostDownloadHook(log.Info),
            mediaCheckpoints: mediaCheckpoints,
            ftpEngine: ftpEngine,
            linkBroker: remote.Broker,
            linkSecrets: remote.Registry);
        foreach (var provider in remote.Registry.All)
        {
            // Addresses saved earlier may turn out to hold a key that was only just entered.
            if (provider.Account is { } account)
                account.Changed += (_, _) => Observe(service.ProtectStoredSecretsAsync(), log, "Protecting saved addresses");
        }

        // Attaches to the service, so it must exist before anything can finish.
        var archives = new ArchiveCoordinator(service, new JsonArchiveStore(paths, log.Warn), log.Info);
        var remoteJobs = remote.CreateCoordinator(paths, service, archives, log.Info);
        var remoteCommands = new RemoteCommands(remote.Registry, remoteJobs, log: log.Info);
        service.DirectAddresses = remoteJobs;
        await service.InitializeAsync().ConfigureAwait(false);

        // Ordinary downloads don't depend on this. Clients just don't get the archive commands this time.
        var archivesStarted = false;
        try
        {
            await archives.InitializeAsync().ConfigureAwait(false);
            archivesStarted = true;
        }
        catch (Exception ex)
        {
            log.Error($"Archive extraction could not start: {ex.GetType().Name}");
            await archives.ShutdownAsync().ConfigureAwait(false);
        }

        // After the service, because reconciling can create local jobs straight away.
        var remoteStarted = false;
        try
        {
            await remoteJobs.StartAsync().ConfigureAwait(false);
            remoteStarted = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warn($"Remote jobs could not start: {ex.GetType().Name}");
        }

        service.TrayAvailable = false;
        var platform = new LinuxHostPlatform(service, log);
        var dispatcher = new HostCommandDispatcher(service, platform, log, remoteStarted ? remoteCommands : null)
        {
            Archives = archivesStarted ? new ArchiveIpcDispatcher(archives) : null,
        };
        archives.Changed += (_, change) => dispatcher.BroadcastArchiveChanged(change);
        completionExecutor.ExitHost = () => _ = Task.Run(async () =>
        {
            try { await dispatcher.NotifyExitingAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
            finally { platform.RequestShutdown(); }
        });
        service.SettingsChanged += (_, next) =>
        {
            TryApplyStartup(next.LaunchAtStartup, log);
            log.Info("Settings updated.");
        };
        await using var sockets = new UnixSocketIpcServer(dispatcher, log);
        dispatcher.Start();
        sockets.Start();

        var currentSettings = service.GetSettings();
        service.SettingsChanged += (_, _) => currentSettings = service.GetSettings();
        TryApplyStartup(currentSettings.LaunchAtStartup, log);
        // As the Windows Host's tray balloons: the same notices on the same settings.
        using var downloadNotices = new DownloadNotices(service, archivesStarted ? archives : null, notice =>
        {
            var current = currentSettings;
            // The kind only: a notice's title and message carry file names.
            log.Info($"Download notice: {notice.Kind}");
            var show = notice.Kind switch
            {
                DownloadNoticeKind.Completed or DownloadNoticeKind.PackageExtractionCompleted => current.NotifyOnCompletion,
                DownloadNoticeKind.Failed or DownloadNoticeKind.Extraction => true,
                _ => false,
            };
            if (show) notifications.Notify(notice.Title, notice.Message);
        }, log.Warn);
        // [TODO D7] Whether the window is in front, so a notice it already shows isn't repeated.
        using var remoteNotices = remoteStarted
            ? RemoteNotices.Listen(remoteJobs, TimeProvider.System, () => currentSettings.NotifyOnCompletion, () => false, notice =>
            {
                log.Info($"Remote notice: {notice.Title}: {notice.Message}");
                notifications.Notify(notice.Title, notice.Message);
            }, log.Warn)
            : null;
        using var downloadWindows = new DownloadWindows(service, () => currentSettings.ShowCompletionDialog, window =>
        {
            _ = Task.Run(() =>
            {
                if (window.IsPackage) platform.OpenPackage(window.Id);
                else platform.OpenDownload(window.Id);
            });
        }, log.Warn);

        // Ctrl+C in a terminal, SIGTERM from the session ending or systemd: stop as an exit from the window would.
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, Stop);
        using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, Stop);
        void Stop(PosixSignalContext context)
        {
            context.Cancel = true;
            platform.RequestShutdown();
        }

        log.Info("Host ready.");
        if (open) platform.OpenUi();

        try
        {
            await Task.Delay(Timeout.Infinite, platform.ExitRequested).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // asked to exit
        }

        log.Info("Shutting down.");
        try
        {
            await dispatcher.NotifyExitingAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.Warn($"Exit notice: {ex.GetType().Name}");
        }

        // Shutting down stops what is running, which isn't news about any package.
        downloadNotices.Dispose();
        remoteNotices?.Dispose();
        downloadWindows.Dispose();
        await Step(async () =>
        {
            // Broadcasts end before the sessions close, as on Windows.
            dispatcher.Stop();
            await sockets.StopAsync().ConfigureAwait(false);
        }, "Socket shutdown", log).ConfigureAwait(false);
        await Step(() => archives.ShutdownAsync(), "Archive shutdown", log).ConfigureAwait(false);
        await Step(() => remoteJobs.StopAsync(), "Remote jobs shutdown", log).ConfigureAwait(false);
        await Step(() => service.ShutdownAsync(), "Service shutdown", log).ConfigureAwait(false);
        remoteCommands.Dispose();
        remote.Dispose();
        engine.Dispose();
        log.Info("Host stopped.");
        return 0;
    }

    /// <summary>
    /// Another Host has the data folder: a second start asks it to open the window, as on Windows. False when it
    /// doesn't answer (it may be starting or stopping).
    /// </summary>
    private static async Task<bool> AskRunningHostAsync(bool open)
    {
        try
        {
            await using var client = await IpcClient.ConnectAsync(new ClientHelloDto { Snapshot = false, Events = false },
                TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var hello = await client.ReadAsync(deadline.Token).ConfigureAwait(false);
            if (hello?.Type != MessageTypes.ServerHello) return false;
            if (open)
            {
                await client.SendAsync(IpcMessage.Create(MessageTypes.HostOpenUi, new HostActionRequest { Action = string.Empty }),
                    deadline.Token).ConfigureAwait(false);
                await client.ReadAsync(deadline.Token).ConfigureAwait(false);
            }

            Console.Error.WriteLine("Inbound's Host is already running.");
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException
                                       or System.Net.Sockets.SocketException)
        {
            Console.Error.WriteLine($"Another Inbound Host has the data folder but doesn't answer: {ex.Message}");
            return false;
        }
    }

    /// <summary>A sandbox data folder (tests, a second profile) must not change the real sign-in start.</summary>
    private static void TryApplyStartup(bool enabled, FileLogger log)
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOWNLOADMANAGER_DATA"))) return;
        try
        {
            AutostartEntry.Apply(enabled);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warn($"Could not update sign-in startup: {ex.Message}");
        }
    }

    private static async Task Step(Func<Task> step, string name, FileLogger log)
    {
        try
        {
            await step().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.Error($"{name}: {ex.GetType().Name}");
        }
    }

    private static void Observe(Task task, FileLogger log, string what) => _ = task.ContinueWith(
        t => log.Error($"{what} failed: {t.Exception?.GetBaseException().GetType().Name}"),
        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
}

/// <summary>The data folder's single-instance lock: an exclusive open of <c>host.lock</c>, which .NET makes a flock().</summary>
internal sealed class InstanceLock : IDisposable
{
    public const string FileName = "host.lock";

    private readonly FileStream _stream;

    private InstanceLock(FileStream stream) => _stream = stream;

    /// <summary>Null when another process holds it.</summary>
    public static InstanceLock? TryAcquire(string dataFolder)
    {
        try
        {
            var stream = new FileStream(Path.Combine(dataFolder, FileName), new FileStreamOptions
            {
                Mode = FileMode.OpenOrCreate,
                Access = FileAccess.ReadWrite,
                Share = FileShare.None,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            });
            return new InstanceLock(stream);
        }
        catch (IOException ex) when (ex is not FileNotFoundException and not DirectoryNotFoundException and not PathTooLongException)
        {
            return null;
        }
    }

    public void Dispose() => _stream.Dispose();
}
