using DownloadManager.Contracts;
using DownloadManager.Core;
using DownloadManager.Persistence;
using DownloadManager.Runtime;
using DownloadManager.Runtime.Ipc;

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
        SupportsFolderBrowse = false,
        SupportsCompletionActions = false,
        SupportsReveal = false,
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
/// src/DownloadManager.Server/ServerApp.cs composes them, minus the HTTP front, Torrents, TorBox, Archives and Sites. A client is an
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
    public const string SharedDownloadFolder = "/storage/emulated/0/Download";

    private readonly HttpDownloadEngine _engine;
    private readonly Action<string> _say;

    private AndroidHost(DownloadManagerService service, HostCommandDispatcher dispatcher, HttpDownloadEngine engine, Action<string> say)
    {
        Service = service;
        Dispatcher = dispatcher;
        _engine = engine;
        _say = say;
    }

    public DownloadManagerService Service { get; }

    public HostCommandDispatcher Dispatcher { get; }

    /// <param name="stateRoot">The app's private folder for settings, job list and checkpoints (metadata only).</param>
    /// <param name="defaultDownloadFolder">Becomes the default download folder while that setting is still the built-in default (nobody set one).</param>
    public static async Task<AndroidHost> StartAsync(string stateRoot, string defaultDownloadFolder, Action<string> say, CancellationToken ct)
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
            service = new DownloadManagerService(paths, jobStore, checkpointStore, settingsStore, engine, log.Info);
        }
        catch
        {
            engine.Dispose();
            throw;
        }

        try
        {
            await service.InitializeAsync(ct).ConfigureAwait(false);
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
                var result = await service.UpdateSettingsAsync(settings, ct).ConfigureAwait(false);
                if (!result.Ok) say("[host WARN] default download folder not set: " + result.Error);
            }

            var dispatcher = new HostCommandDispatcher(service, new AndroidPlatform(), log);
            dispatcher.Start();
            return new AndroidHost(service, dispatcher, engine, say);
        }
        catch (Exception startError)
        {
            // Nothing half-started may keep writing to the state folder: a later start must be the only writer.
            try { await service.ShutdownAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex)
            {
                say("[host] shutdown after a failed start: " + ex.GetType().Name + ": " + ex.Message);
                engine.Dispose();
                // Something may still be running over the state folder: no second start in this process.
                throw new HostLeftRunningException(startError, ex);
            }

            engine.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Dispatcher.Stop();
        try { await Service.ShutdownAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { _say("[host] shutdown: " + ex.GetType().Name + ": " + ex.Message); }
        _engine.Dispose();
    }
}
