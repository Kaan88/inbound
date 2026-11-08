using System.Collections.Concurrent;
using System.Diagnostics;
using DownloadManager.Contracts;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// The window's connection to one Host: this PC's over the named pipe, or another server's over its WebSocket
/// (<see cref="ServerTarget"/>, design D9 section 5). Replies come back to the call that asked; events go to the handlers.
/// <para>
/// To a server: reconnecting waits 2 s, then twice as long each time, up to about a minute
/// (<see cref="ReconnectBackoff"/>); a token the server refuses stops it until <see cref="Retarget"/> or
/// <see cref="ReconnectNow"/>. While <see cref="SetEventsWanted"/> is false (the window is hidden) it asks the server
/// for no snapshot and no events. Its <c>host.exiting</c> means "disconnected": only this PC's Host quits the app, and
/// only this PC's update progress is shown.
/// </para>
/// </summary>
public sealed partial class HostConnection : IAsyncDisposable
{
    private static readonly TimeSpan LocalRetryDelay = TimeSpan.FromSeconds(2);

    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<IpcMessage>> _pending = new();
    private readonly ReconnectBackoff _backoff = new();
    private readonly Lock _sync = new();
    private IIpcConnection? _client;
    private Task? _loop;
    private CancellationTokenSource? _session;
    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile ServerTarget _target;
    private volatile bool _eventsWanted = true;
    private volatile HostHelloDto? _hello;

    public HostConnection(ServerTarget? target = null)
    {
        _target = target ?? ServerTarget.Local;
    }

    public event Action<SnapshotDto>? SnapshotReceived;
    public event Action<UpdateProgressDto>? UpdateProgressChanged;
    public event Action<JobDto, string>? JobChanged;
    public event Action<List<DownloadGroupDto>>? GroupsChanged;
    public event Action<ArchiveChangedDto>? ArchiveChanged;
    public event Action<bool, string?>? ConnectionChanged;
    /// <summary>This PC's Host announced a user-requested application exit. Never raised for another server.</summary>
    public event Action? HostExiting;

    public bool IsConnected => _client is not null;

    /// <summary>The Host this connection goes to.</summary>
    public ServerTarget Target => _target;

    /// <summary>Another server, not this PC: its paths and files are not this PC's.</summary>
    public bool IsRemote => !_target.IsLocal;

    /// <summary>What the connected Host said in its hello (capabilities, hostKind); null until connected.</summary>
    public HostHelloDto? Hello => _hello;

    /// <summary>The server refused the token: nothing more is tried until the user pairs again.</summary>
    public bool PairingEnded { get; private set; }

    public void Start()
    {
        _loop ??= Task.Run(() => RunAsync(_cts.Token));
    }

    /// <summary>
    /// Goes to another Host. The current connection closes, every call waiting on it fails, and the new one is tried
    /// at once.
    /// </summary>
    public void Retarget(ServerTarget target)
    {
        lock (_sync)
        {
            _target = target;
            PairingEnded = false;
            _hello = null;
        }

        ReconnectNow();
    }

    /// <summary>Drops the current connection, or the wait before the next try, and connects again now.</summary>
    public void ReconnectNow()
    {
        lock (_sync)
        {
            _backoff.Reset();
            try { _session?.Cancel(); }
            catch (ObjectDisposedException) { }
            _wake.TrySetResult();
        }
    }

    /// <summary>
    /// False while the window is hidden: a server is then asked for no events (and no snapshot), so it sends nothing
    /// while nobody looks. This PC's pipe is left as it is.
    /// </summary>
    public void SetEventsWanted(bool wanted)
    {
        if (_eventsWanted == wanted) return;
        _eventsWanted = wanted;
        if (IsRemote) ReconnectNow();
    }

    public async Task<SnapshotDto> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var reply = await RequestAsync(MessageTypes.JobsQuery, new { }, TimeSpan.FromSeconds(8), cancellationToken)
            .ConfigureAwait(false);
        return reply.GetPayload<SnapshotDto>();
    }

    public Task<IpcMessage> ListArchivesAsync(CancellationToken cancellationToken = default)
        => RequestAsync(ArchiveMessageTypes.List, new { }, TimeSpan.FromSeconds(30), cancellationToken);

    /// <summary>The password goes only into this one message. An empty one clears what is on record.</summary>
    public Task<IpcMessage> SetArchivePasswordAsync(ArchiveSetPasswordRequest request, CancellationToken cancellationToken = default)
        => RequestAsync(ArchiveMessageTypes.SetPassword, request, TimeSpan.FromSeconds(30), cancellationToken);

    public Task<IpcMessage> GroupActionAsync(GroupActionRequest request, CancellationToken cancellationToken = default)
        => RequestAsync(MessageTypes.GroupAction, request, TimeSpan.FromMinutes(2), cancellationToken);

    public Task<IpcMessage> GetConnectionsAsync(Guid jobId, CancellationToken cancellationToken = default)
        => RequestAsync(MessageTypes.JobConnections, new JobActionRequest { JobId = jobId }, TimeSpan.FromSeconds(8), cancellationToken);

    public Task<IpcMessage> GetStatisticsAsync(CancellationToken cancellationToken = default)
        => RequestAsync(MessageTypes.StatisticsGet, new { }, TimeSpan.FromSeconds(8), cancellationToken);

    /// <summary>UI.R24: the Host's last hour of total download speed.</summary>
    public Task<IpcMessage> GetSpeedHistoryAsync(CancellationToken cancellationToken = default)
        => RequestAsync(MessageTypes.SpeedHistoryGet, new { }, TimeSpan.FromSeconds(8), cancellationToken);

    public Task<IpcMessage> AddAsync(AddJobRequest request, CancellationToken cancellationToken = default)
        => RequestAsync(MessageTypes.JobAdd, request, TimeSpan.FromSeconds(20), cancellationToken);

    public async Task<FileMetadataResult> ReadFileMetadataAsync(FileMetadataRequest payload, CancellationToken cancellationToken)
    {
        // Isolate a slow server preview from this UI's normal command/event connection.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(18));
        var request = IpcMessage.Create(MessageTypes.FileMetadata, payload);
        var reply = await SendOnOwnConnectionAsync(request, timeout.Token).ConfigureAwait(false)
            ?? throw new IOException("The Host disconnected while reading file information.");
        if (reply.V != Protocol.Version) throw new InvalidDataException("Host protocol mismatch.");
        if (reply.Type != MessageTypes.FileMetadataResult) return new FileMetadataResult { Error = "This Host does not support file information previews." };
        return reply.GetPayload<FileMetadataResult>();
    }

    public Task<IpcMessage> GetSegmentsAsync(Guid id, CancellationToken cancellationToken = default)
        => RequestAsync(MessageTypes.JobSegments, new JobActionRequest { JobId = id },
            TimeSpan.FromSeconds(8), cancellationToken);

    public Task<IpcMessage> PauseAsync(Guid id, CancellationToken cancellationToken = default)
        => RequestAsync(MessageTypes.JobPause, new JobActionRequest { JobId = id }, TimeSpan.FromSeconds(8), cancellationToken);

    public Task<IpcMessage> ResumeAsync(Guid id, CancellationToken cancellationToken = default)
        => RequestAsync(MessageTypes.JobResume, new JobActionRequest { JobId = id }, TimeSpan.FromSeconds(8), cancellationToken);

    public Task<IpcMessage> CancelAsync(Guid id, CancellationToken cancellationToken = default)
        => RequestAsync(MessageTypes.JobCancel, new JobActionRequest { JobId = id, DeleteFile = true, OnlyIfUnfinished = true },
            TimeSpan.FromSeconds(8), cancellationToken);

    public Task<IpcMessage> RemoveAsync(Guid id, bool deleteFile, CancellationToken cancellationToken = default)
        => RequestAsync(MessageTypes.JobRemove, new JobActionRequest { JobId = id, DeleteFile = deleteFile },
            TimeSpan.FromSeconds(10), cancellationToken);

    /// <summary>Removes several downloads in one Host operation; stopping them may take a while.</summary>
    public Task<IpcMessage> RemoveManyAsync(IReadOnlyList<Guid> ids, bool deleteFile, CancellationToken cancellationToken = default)
        => RequestAsync(MessageTypes.JobRemove, new JobActionRequest { JobIds = ids.ToList(), DeleteFile = deleteFile },
            TimeSpan.FromSeconds(30 + 2 * ids.Count), cancellationToken);

    /// <summary>Free space at these folders, measured by the Host. Each folder answers within about two seconds.</summary>
    public async Task<FolderSpaceResult?> GetFolderSpaceAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        var reply = await RequestAsync(MessageTypes.FolderSpace, new FolderSpaceRequest { Paths = paths.ToList() },
            TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        return reply.Type == MessageTypes.FolderSpaceResult ? reply.GetPayload<FolderSpaceResult>() : null;
    }

    public Task<IpcMessage> UpdateSettingsAsync(SettingsDto settings, CancellationToken cancellationToken = default)
        => RequestAsync(MessageTypes.SettingsUpdate, new SettingsUpdateRequest { Settings = settings },
            TimeSpan.FromSeconds(10), cancellationToken);

    public Task<IpcMessage> QueueActionAsync(QueueActionRequest request, CancellationToken cancellationToken = default)
        => RequestAsync(MessageTypes.QueueAction, request, request.MoveContentsToQueueId.HasValue ? TimeSpan.FromMinutes(2) : TimeSpan.FromSeconds(10), cancellationToken);

    public Task<IpcMessage> DetachFromQueueAsync(Guid jobId, CancellationToken cancellationToken = default)
        => RequestAsync(MessageTypes.JobMoveToQueue, new JobQueueRequest { JobId = jobId, Detach = true }, TimeSpan.FromSeconds(10), cancellationToken);

    public Task<IpcMessage> MoveToQueueAsync(Guid jobId, int queueId, CancellationToken cancellationToken = default)
        => RequestAsync(MessageTypes.JobMoveToQueue, new JobQueueRequest { JobId = jobId, QueueId = queueId },
            TimeSpan.FromSeconds(10), cancellationToken);

    public Task<IpcMessage> RefreshUrlAsync(Guid jobId, string url, CancellationToken cancellationToken = default)
        => RequestAsync(MessageTypes.JobRefreshUrl, new JobRefreshUrlRequest { JobId = jobId, Url = url },
            TimeSpan.FromSeconds(10), cancellationToken);

    /// <summary>
    /// For an address that differs from the job's. The Host has to find out whether the address belongs to a
    /// provider, which can wait on the network, so this gets a connection of its own like every such call.
    /// </summary>
    public async Task<IpcMessage> RefreshChangedUrlAsync(Guid jobId, string url, bool explicitDirect, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            var request = IpcMessage.Create(MessageTypes.JobRefreshUrl, new JobRefreshUrlRequest { JobId = jobId, Url = url, ExplicitDirect = explicitDirect });
            if (await SendOnOwnConnectionAsync(request, timeout.Token).ConfigureAwait(false) is { } reply)
            {
                if (reply.V != Protocol.Version) throw new InvalidDataException("Host protocol mismatch.");
                return reply;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return IpcMessage.Create(MessageTypes.JobActionResult, new JobActionResponse { Error = "Checking this address took too long, so it wasn't changed. Try again." });
        }

        throw new IOException("The Host disconnected while changing the address.");
    }

    /// <summary>
    /// One request on a connection of its own, so a slow answer can't hold up the main one: on this PC a pipe of its own
    /// (as before), to a server one <c>POST /api/ipc</c>. Null when the connection closed before the reply.
    /// </summary>
    private async Task<IpcMessage?> SendOnOwnConnectionAsync(IpcMessage request, CancellationToken cancellationToken)
    {
        var target = _target;
        if (!target.IsLocal) return await ServerConnector.SendOnceAsync(target, request, Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);

        await using var client = await ServerConnector.ConnectAsync(target, cancellationToken: cancellationToken).ConfigureAwait(false);
        await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        while (await client.ReadAsync(cancellationToken).ConfigureAwait(false) is { } reply)
        {
            if (reply.V != Protocol.Version) return reply;
            if (reply.ReplyTo == request.Id) return reply;
        }

        return null;
    }

    public Task<IpcMessage> CleanOrphansAsync(bool delete, CancellationToken cancellationToken = default)
        => RequestAsync(MessageTypes.HostCleanOrphans, new CleanOrphansRequest { Delete = delete },
            TimeSpan.FromSeconds(30), cancellationToken);

    public Task<IpcMessage> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
        => RequestAsync(MessageTypes.UpdateCheck, new { }, TimeSpan.FromSeconds(30), cancellationToken);

    public Task<IpcMessage> DownloadUpdateAsync(CancellationToken cancellationToken = default)
        => RequestAsync(MessageTypes.UpdateDownload, new { }, TimeSpan.FromMinutes(5), cancellationToken);

    public Task<IpcMessage> InstallUpdateAsync(bool force = false, CancellationToken cancellationToken = default)
        => RequestAsync(MessageTypes.UpdateInstall, new UpdateInstallRequest { Force = force },
            TimeSpan.FromMinutes(2), cancellationToken);

    public Task<IpcMessage> DeferUpdateAsync(string version, CancellationToken cancellationToken = default)
        => RequestAsync(MessageTypes.UpdateDefer, new UpdateDeferRequest { Version = version },
            TimeSpan.FromSeconds(10), cancellationToken);

    public Task<IpcMessage> UpdateJobAsync(Guid jobId, string description, string? categoryId = null, JobCompletionOptionsDto? completionOptions = null, CancellationToken cancellationToken = default)
        => RequestAsync(MessageTypes.JobUpdate, new JobUpdateRequest { JobId = jobId, Description = description, CategoryId = categoryId, CompletionOptions = completionOptions },
            TimeSpan.FromSeconds(10), cancellationToken);

    public Task<IpcMessage> SetCategoryAsync(Guid jobId, string categoryId, CancellationToken cancellationToken = default)
        => RequestAsync(MessageTypes.JobUpdate, new JobUpdateRequest { JobId = jobId, CategoryId = categoryId },
            TimeSpan.FromSeconds(10), cancellationToken);

    public Task<IpcMessage> UpdateTransferOptionsAsync(Guid id, long? speedLimitBytesPerSecond = null,
        bool? showCompletionDialog = null, CancellationToken cancellationToken = default)
        => RequestAsync(MessageTypes.JobUpdate, new JobUpdateRequest
        {
            JobId = id,
            SpeedLimitBytesPerSecond = speedLimitBytesPerSecond,
            ShowCompletionDialog = showCompletionDialog,
        }, TimeSpan.FromSeconds(10), cancellationToken);

    public Task<IpcMessage> SetPriorityAsync(Guid jobId, int priority, CancellationToken cancellationToken = default)
        => RequestAsync(MessageTypes.JobUpdate, new JobUpdateRequest { JobId = jobId, Priority = priority },
            TimeSpan.FromSeconds(10), cancellationToken);

    public Task<IpcMessage> MoveJobAsync(Guid jobId, string newFilePath, CancellationToken cancellationToken = default)
        => RequestAsync(MessageTypes.JobUpdate, new JobUpdateRequest { JobId = jobId, NewFilePath = newFilePath },
            TimeSpan.FromSeconds(30), cancellationToken);

    public Task<IpcMessage> PauseAllAsync(CancellationToken cancellationToken = default)
        => RequestAsync(MessageTypes.JobsPauseAll, new { }, TimeSpan.FromSeconds(8), cancellationToken);

    public Task<IpcMessage> ResumeAllAsync(CancellationToken cancellationToken = default)
        => RequestAsync(MessageTypes.JobsResumeAll, new { }, TimeSpan.FromSeconds(8), cancellationToken);

    public Task<IpcMessage> ShutdownAsync(CancellationToken cancellationToken = default)
        => RequestAsync(MessageTypes.HostShutdown, new { }, TimeSpan.FromSeconds(5), cancellationToken);

    private async Task<IpcMessage> RequestAsync<T>(
        string type,
        T payload,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var client = _client ?? throw new InvalidOperationException("The background host is not connected.");
        var request = IpcMessage.Create(type, payload);
        var completion = new TaskCompletionSource<IpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[request.Id] = completion;

        try
        {
            await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _sendGate.Release();
            }

            return await completion.Task
                .WaitAsync(timeout, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(request.Id, out _);
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        // App.OnLaunched owns Host startup. Reconnection only waits for it;
        // a hidden frontend must never undo an intentional tray Exit, even if
        // Exit happens before this frontend's first successful handshake.
        while (!cancellationToken.IsCancellationRequested)
        {
            ServerTarget target;
            CancellationTokenSource session;
            Task wake;
            lock (_sync)
            {
                target = _target;
                session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _session = session;
                _wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                wake = _wake.Task;
            }

            var remote = !target.IsLocal;
            IIpcConnection? client = null;
            var stopTrying = false;
            try
            {
                // A server is asked only for what the window shows: nothing while it is hidden.
                var hello = remote ? new ClientHelloDto { Snapshot = _eventsWanted, Events = _eventsWanted } : null;
                client = await ServerConnector.ConnectAsync(target, hello, remote ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(2), session.Token)
                    .ConfigureAwait(false);

                var greeting = await client.ReadAsync(session.Token).ConfigureAwait(false);
                if (greeting?.Type != MessageTypes.ServerHello)
                {
                    throw new InvalidDataException(remote ? $"{target.Name} did not answer like an Inbound server." : "The host did not send a greeting.");
                }

                if (greeting.V != Protocol.Version)
                {
                    throw new InvalidDataException(
                        $"Protocol mismatch: UI speaks {Protocol.Version}, host speaks {greeting.V}.");
                }

                try
                {
                    _hello = greeting.GetPayload<HostHelloDto>();
                }
                catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException)
                {
                    _hello = new HostHelloDto();
                }

                var snapshot = client.SnapshotFollowsHello ? await client.ReadAsync(session.Token).ConfigureAwait(false) : null;
                _client = client;
                if (remote) _backoff.Reset();
                ConnectionChanged?.Invoke(true, null);

                if (snapshot?.Type == MessageTypes.JobsSnapshot)
                {
                    SnapshotReceived?.Invoke(snapshot.GetPayload<SnapshotDto>());
                }
                else if (snapshot is not null)
                {
                    Dispatch(snapshot, snapshot: false);
                }

                while (!session.IsCancellationRequested)
                {
                    var message = await client.ReadAsync(session.Token).ConfigureAwait(false);
                    if (message is null)
                    {
                        break;
                    }

                    Dispatch(message, snapshot: false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException) when (session.IsCancellationRequested)
            {
                // Retargeted, shown or hidden: connect again at once, below.
            }
            catch (Exception ex) when (ex is RemotePairingEndedException or PlainHttpNotConfirmedException)
            {
                // Trying again can't help: the token is refused, or may not be sent. The user decides in File > Servers.
                stopTrying = true;
                PairingEnded = ex is RemotePairingEndedException;
                ConnectionChanged?.Invoke(false, ex.Message);
            }
            catch (Exception ex)
            {
                ConnectionChanged?.Invoke(false, remote
                    ? RemoteConnectionText.Failure(target, ex)
                    : ex is TimeoutException && HostPipeBusyException.PipeExists() ? RemoteText.HostBusy : ex.Message);
            }
            finally
            {
                if (client is not null && _client == client)
                {
                    _client = null;
                    if (!session.IsCancellationRequested || cancellationToken.IsCancellationRequested)
                    {
                        ConnectionChanged?.Invoke(false, remote
                            ? $"Disconnected from {target.Name}. Trying again shortly."
                            : "The background Host has stopped. Open Download Manager again to start it.");
                    }
                }

                FailPending(new IOException("The connection to the background host was lost."));
                if (client is not null)
                {
                    try
                    {
                        await client.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                    {
                        // Closing is the point, and it is closed now.
                    }
                }

                lock (_sync)
                {
                    if (_session == session) _session = null;
                }
            }

            var askedFor = session.IsCancellationRequested;
            session.Dispose();
            if (askedFor)
            {
                if (cancellationToken.IsCancellationRequested) break;
                continue;
            }

            try
            {
                var delay = stopTrying ? Timeout.InfiniteTimeSpan : remote ? _backoff.Next() : LocalRetryDelay;
                await wake.WaitAsync(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Time for the next try.
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void Dispatch(IpcMessage message, bool snapshot)
    {
        if (message.Type == MessageTypes.HostExiting)
        {
            // Another server going away is a disconnection, which its closing socket reports; it never quits this app.
            if (!IsRemote) HostExiting?.Invoke();
            return;
        }
        if (message.Type == MessageTypes.UpdateProgress)
        {
            // Updates are always this PC's.
            if (!IsRemote) UpdateProgressChanged?.Invoke(message.GetPayload<UpdateProgressDto>());
            return;
        }
        if (message.ReplyTo is not null && _pending.TryGetValue(message.ReplyTo, out var completion))
        {
            _pending.TryRemove(message.ReplyTo, out _);
            completion.TrySetResult(message);
            return;
        }

        if (message.Type == MessageTypes.GroupsChanged)
        {
            try { GroupsChanged?.Invoke(message.GetPayload<List<DownloadGroupDto>>() ?? new()); }
            catch { /* ignore malformed events */ }
            return;
        }

        if (message.Type == ArchiveMessageTypes.Changed)
        {
            try { ArchiveChanged?.Invoke(message.GetPayload<ArchiveChangedDto>()); }
            catch { /* ignore malformed events */ }
            return;
        }

        if (message.Type == MessageTypes.JobEvent)
        {
            try
            {
                var jobEvent = message.GetPayload<JobEventDto>();
                JobChanged?.Invoke(jobEvent.Job, jobEvent.EventType);
            }
            catch
            {
                // ignore malformed events
            }

            return;
        }

        if (!snapshot && message.Type == MessageTypes.JobsSnapshot)
        {
            SnapshotReceived?.Invoke(message.GetPayload<SnapshotDto>());
        }
    }

    private void FailPending(Exception error)
    {
        foreach (var pending in _pending.Values)
        {
            pending.TrySetException(error);
        }

        _pending.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // expected
            }
        }

        if (_client is not null)
        {
            await _client.DisposeAsync().ConfigureAwait(false);
            _client = null;
        }

        _cts.Dispose();
        _sendGate.Dispose();
    }
}

public static class HostLauncher
{
    private static DateTimeOffset _lastStartAttempt = DateTimeOffset.MinValue;

    public static void EnsureStarted()
    {
        // Use the same per-user/profile identity as IPC and the Host. A process
        // name can belong to another profile, while a duplicate scoped Host
        // can activate a frontend from an older build directory.
        try
        {
            if (Mutex.TryOpenExisting(PipeNames.MutexName(), out var existingHost))
            {
                existingHost.Dispose();
                return;
            }
        }
        catch (UnauthorizedAccessException)
        {
            // An existing Host owns this identity but is inaccessible. Starting
            // another cannot resolve that; let the connection report the error.
            return;
        }

        if (DateTimeOffset.UtcNow - _lastStartAttempt < TimeSpan.FromSeconds(5))
        {
            return;
        }

        _lastStartAttempt = DateTimeOffset.UtcNow;
        var executable = FindHostExecutable();
        if (executable is null)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                Arguments = "--background",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        catch (Exception)
        {
            // The connection loop will surface the failure.
        }
    }

    private static string? FindHostExecutable()
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "DownloadManager.Host.exe"),
            Path.Combine(AppContext.BaseDirectory, "..", "DownloadManager.Host.exe"),
        };

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 7 && directory is not null; i++)
        {
            candidates.Add(Path.Combine(directory.FullName, "DownloadManager.Host.exe"));
            candidates.Add(Path.Combine(directory.FullName, "DownloadManager.Host", "bin", "Debug", "net10.0-windows", "DownloadManager.Host.exe"));
            candidates.Add(Path.Combine(directory.FullName, "DownloadManager.Host", "bin", "Release", "net10.0-windows", "DownloadManager.Host.exe"));
            directory = directory.Parent;
        }

        var fromEnv = Environment.GetEnvironmentVariable("DOWNLOADMANAGER_HOST");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            candidates.Insert(0, fromEnv);
        }

        return candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
    }
}
