using System.Collections.Concurrent;
using System.Diagnostics;
using DownloadManager.Contracts;

namespace DownloadManager.Desktop.WinUI;

public sealed class HostConnection : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<IpcMessage>> _pending = new();
    private IpcClient? _client;
    private Task? _loop;

    public event Action<SnapshotDto>? SnapshotReceived;
    public event Action<UpdateProgressDto>? UpdateProgressChanged;
    public event Action<JobDto, string>? JobChanged;
    public event Action<List<DownloadGroupDto>>? GroupsChanged;
    public event Action<bool, string?>? ConnectionChanged;
    /// <summary>The Host announced a user-requested application exit.</summary>
    public event Action? HostExiting;

    public bool IsConnected => _client is not null;

    public void Start()
    {
        _loop ??= Task.Run(() => RunAsync(_cts.Token));
    }

    public async Task<SnapshotDto> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var reply = await RequestAsync(MessageTypes.JobsQuery, new { }, TimeSpan.FromSeconds(8), cancellationToken)
            .ConfigureAwait(false);
        return reply.GetPayload<SnapshotDto>();
    }

    public Task<IpcMessage> GroupActionAsync(GroupActionRequest request, CancellationToken cancellationToken = default)
        => RequestAsync(MessageTypes.GroupAction, request, TimeSpan.FromMinutes(2), cancellationToken);

    public Task<IpcMessage> GetConnectionsAsync(Guid jobId, CancellationToken cancellationToken = default)
        => RequestAsync(MessageTypes.JobConnections, new JobActionRequest { JobId = jobId }, TimeSpan.FromSeconds(8), cancellationToken);

    public Task<IpcMessage> GetStatisticsAsync(CancellationToken cancellationToken = default)
        => RequestAsync(MessageTypes.StatisticsGet, new { }, TimeSpan.FromSeconds(8), cancellationToken);

    public Task<IpcMessage> AddAsync(AddJobRequest request, CancellationToken cancellationToken = default)
        => RequestAsync(MessageTypes.JobAdd, request, TimeSpan.FromSeconds(20), cancellationToken);

    public async Task<FileMetadataResult> ReadFileMetadataAsync(FileMetadataRequest payload, CancellationToken cancellationToken)
    {
        // Isolate a slow server preview from this UI's normal command/event connection.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(18));
        await using var client = await IpcClient.ConnectAsync(cancellationToken: timeout.Token).ConfigureAwait(false);
        var request = IpcMessage.Create(MessageTypes.FileMetadata, payload);
        await client.SendAsync(request, timeout.Token).ConfigureAwait(false);
        while (await client.ReadAsync(timeout.Token).ConfigureAwait(false) is { } reply)
        {
            if (reply.V != Protocol.Version) throw new InvalidDataException("Host protocol mismatch.");
            if (reply.ReplyTo != request.Id) continue;
            if (reply.Type != MessageTypes.FileMetadataResult) return new FileMetadataResult { Error = "This Host does not support file information previews." };
            return reply.GetPayload<FileMetadataResult>();
        }
        throw new IOException("The Host disconnected while reading file information.");
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
            IpcClient? client = null;
            try
            {
                client = await IpcClient.ConnectAsync(TimeSpan.FromSeconds(2), cancellationToken)
                    .ConfigureAwait(false);

                var hello = await client.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (hello?.Type != MessageTypes.ServerHello)
                {
                    throw new InvalidDataException("The host did not send a greeting.");
                }

                if (hello.V != Protocol.Version)
                {
                    throw new InvalidDataException(
                        $"Protocol mismatch: UI speaks {Protocol.Version}, host speaks {hello.V}.");
                }

                var snapshot = await client.ReadAsync(cancellationToken).ConfigureAwait(false);
                _client = client;
                ConnectionChanged?.Invoke(true, null);

                if (snapshot?.Type == MessageTypes.JobsSnapshot)
                {
                    SnapshotReceived?.Invoke(snapshot.GetPayload<SnapshotDto>());
                }

                while (!cancellationToken.IsCancellationRequested)
                {
                    var message = await client.ReadAsync(cancellationToken).ConfigureAwait(false);
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
            catch (Exception ex)
            {
                ConnectionChanged?.Invoke(false, ex.Message);
            }
            finally
            {
                if (_client == client)
                {
                    _client = null;
                    ConnectionChanged?.Invoke(false, "The background Host has stopped. Open Download Manager again to start it.");
                }

                FailPending(new IOException("The connection to the background host was lost."));
                if (client is not null)
                {
                    await client.DisposeAsync().ConfigureAwait(false);
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
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
            HostExiting?.Invoke();
            return;
        }
        if (message.Type == MessageTypes.UpdateProgress)
        {
            UpdateProgressChanged?.Invoke(message.GetPayload<UpdateProgressDto>());
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
