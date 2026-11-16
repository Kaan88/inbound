using System.Collections.Concurrent;
using System.Text.Json;
using DownloadManager.Contracts;
using DownloadManager.Core;
using DownloadManager.Persistence;
using DownloadManager.Runtime;
using DownloadManager.Runtime.Ipc;

namespace AndroidTrial;

/// <summary>Every capability off: nothing here can open a window, pick a folder, show a file or end the process.</summary>
internal sealed class AndroidTrialPlatform : IHostPlatform
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
        Task.FromResult(new FolderPickResult { Error = "No picker in the trial." });
}

internal sealed class DelegateLog(Action<string> say) : IHostLog
{
    public void Info(string message) => say("[host] " + message);

    public void Warn(string message) => say("[host WARN] " + message);

    public void Error(string message) => say("[host ERROR] " + message);
}

/// <summary>A client of the dispatcher in the same process: what a pipe or WebSocket session is, without the transport.</summary>
internal sealed class InProcessSession : IClientSession
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<IpcMessage>> _waiting = new();

    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>Called for every frame that is not the reply to a request (hello, snapshot, events).</summary>
    public Action<IpcMessage>? OnFrame { get; set; }

    public bool Enqueue(IpcMessage message)
    {
        if (message.ReplyTo is { } to && _waiting.TryRemove(to, out var tcs)) tcs.TrySetResult(message);
        else OnFrame?.Invoke(message);
        return true;
    }

    public Task SendDirectAsync(IpcMessage message, CancellationToken cancellationToken)
    {
        Enqueue(message);
        return Task.CompletedTask;
    }

    public Task<IpcMessage> ExpectReplyAsync(IpcMessage request)
    {
        var tcs = new TaskCompletionSource<IpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiting[request.Id] = tcs;
        return tcs.Task;
    }
}

/// <summary>
/// The Host core on Android: DownloadManagerService + HostCommandDispatcher composed as src/DownloadManager.Server/ServerApp.cs
/// composes them, minus the HTTP front, Torrents, TorBox, Archives and Sites (not referenced / not started). No pipe: a client is an
/// <see cref="InProcessSession"/> talking to the dispatcher with the same IpcMessage envelopes.
/// </summary>
internal sealed class AndroidHost : IAsyncDisposable
{
    private static int _protectorSet;
    private readonly HttpDownloadEngine _engine;
    private readonly Action<string> _say;

    private AndroidHost(StoragePaths paths, DownloadManagerService service, HostCommandDispatcher dispatcher, HttpDownloadEngine engine, Action<string> say)
    {
        Paths = paths;
        Service = service;
        Dispatcher = dispatcher;
        _engine = engine;
        _say = say;
    }

    public StoragePaths Paths { get; }

    public DownloadManagerService Service { get; }

    public HostCommandDispatcher Dispatcher { get; }

    public static async Task<AndroidHost> StartAsync(string stateRoot, Action<string> say, CancellationToken ct, bool chooseProtector = true)
    {
        StartupTrace.Mark("host.begin");
        var paths = new StoragePaths(stateRoot);
        paths.EnsureCreated();
        StartupTrace.Mark("host.paths");
        if (chooseProtector && Interlocked.Exchange(ref _protectorSet, 1) == 0)
        {
          if (WarmUp.Protector is { } early) SecretProtector.Use(early);   // trial: a protector that opens lazily or on a warm-up thread
          else
          {
            var keyDir = Path.Combine(stateRoot, "key");
            Directory.CreateDirectory(keyDir);
            SecretProtector.Use(AesGcmSecretProtector.OpenForDataFolder(
                Path.Combine(keyDir, AesGcmSecretProtector.DefaultFileName), keyDir, discardUnreadable: false));
          }
        }

        StartupTrace.Mark("host.protector");

        var log = new DelegateLog(say);
        IJobStore jobStore = new TimedJobStore(new JsonJobStore(paths, log.Info));
        var checkpointStore = new JsonCheckpointStore(paths);
        IDownloadCheckpointStore checkpoints = new TimedCheckpointStore(checkpointStore);
        ISettingsStore settingsStore = new TimedSettingsStore(new JsonSettingsStore(paths));
        StartupTrace.Mark("host.stores");
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
        StartupTrace.Mark("host.engine");
        var service = new DownloadManagerService(paths, jobStore, checkpoints, settingsStore, engine, log.Info);
        StartupTrace.Mark("host.service.constructed");
        await service.InitializeAsync(ct).ConfigureAwait(false);
        StartupTrace.Mark("host.service.initialized");
        service.TrayAvailable = false;
        var dispatcher = new HostCommandDispatcher(service, new AndroidTrialPlatform(), log);
        StartupTrace.Mark("host.dispatcher.constructed");
        dispatcher.Start();
        StartupTrace.Mark("host.dispatcher.started");
        return new AndroidHost(paths, service, dispatcher, engine, say);
    }

    /// <summary>Opens a session: the dispatcher sends the hello and the snapshot to <paramref name="session"/>.</summary>
    public Task OpenAsync(InProcessSession session, CancellationToken ct) => Dispatcher.OpenSessionAsync(session, ct);

    /// <summary>One command, the same way a transport does it (compatibility check, then dispatch); waits for its reply.</summary>
    public async Task<IpcMessage> RequestAsync(InProcessSession session, IpcMessage request, CancellationToken ct)
    {
        var reply = session.ExpectReplyAsync(request);
        if (!Dispatcher.RejectIfIncompatible(session, request))
            await Dispatcher.DispatchAsync(session, request, ct).ConfigureAwait(false);
        return await reply.WaitAsync(TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        Dispatcher.Stop();
        try { await Service.ShutdownAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { _say("[host] shutdown: " + ex.GetType().Name + ": " + ex.Message); }
        _engine.Dispose();
    }
}
