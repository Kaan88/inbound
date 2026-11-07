using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading.Channels;
using DownloadManager.Contracts;
using DownloadManager.Core;
using DownloadManager.Core.Providers;
using DownloadManager.Runtime;
using DownloadManager.Runtime.Archives;
using DownloadManager.Runtime.Remote;
using DownloadManager.Tray;

namespace DownloadManager.Host;

public interface IHostController
{
    void OpenUi(string? page = null);
    void RequestShutdown();
    Task<FolderPickResult> PickFolderAsync(string? initialPath, CancellationToken cancellationToken)
        => Task.FromResult(new FolderPickResult { Error = "Folder selection is unavailable in this Host." });
}

public sealed partial class HostPipeServer
{
    private readonly DownloadManagerService _service;
    private readonly IHostController _controller;
    private readonly FileLogger _log;
    private readonly RemoteCommands? _remote;
    private readonly ConcurrentDictionary<Guid, ClientConnection> _clients = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;
    private int _updateWindowOpened;

    public HostPipeServer(DownloadManagerService service, IHostController controller, FileLogger log, RemoteCommands? remote = null)
    {
        _service = service;
        _controller = controller;
        _log = log;
        _remote = remote;
    }

    /// <summary>Answers <c>archive.*</c>. Set before <see cref="Start"/>.</summary>
    public ArchiveIpcDispatcher? Archives { get; init; }

    /// <summary>Whole-state broadcast of one set. The Host subscribes this to the coordinator's change event.</summary>
    public void BroadcastArchiveChanged(ArchiveChangedDto change)
    {
        var message = IpcMessage.Create(ArchiveMessageTypes.Changed, change);
        foreach (var client in _clients.Values) client.Writer.TryWrite(message);
    }

    public void Start()
    {
        _service.JobChanged += OnJobChanged;
        _service.UpdateProgressChanged += OnUpdateProgressChanged;
        _service.SettingsChanged += OnSettingsChanged;
        if (_remote is not null) _remote.Changed += OnRemoteChanged;
        _acceptLoop = AcceptLoopAsync(_cts.Token);
        _log.Info($"IPC server listening on pipe '{PipeNames.HostPipe()}'.");
    }

    public async Task StopAsync()
    {
        _service.JobChanged -= OnJobChanged;
        _service.UpdateProgressChanged -= OnUpdateProgressChanged;
        _service.SettingsChanged -= OnSettingsChanged;
        if (_remote is not null) _remote.Changed -= OnRemoteChanged;
        _cts.Cancel();
        foreach (var client in _clients.Values)
        {
            client.Writer.TryComplete();
        }

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // expected
            }
        }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = CreatePipe();
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                var client = new ClientConnection(pipe, _log);
                _ = HandleClientAsync(client, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                pipe?.Dispose();
                break;
            }
            catch (Exception ex)
            {
                pipe?.Dispose();
                _log.Error($"Named-pipe accept failed: {ex.Message}");
                try
                {
                    await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private static NamedPipeServerStream CreatePipe()
    {
        var security = PipeSecurityFactory.CreateForCurrentUser();

        return NamedPipeServerStreamAcl.Create(
            PipeNames.HostPipe(),
            PipeDirection.InOut,
            8,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            64 * 1024,
            64 * 1024,
            security);
    }

    private async Task HandleClientAsync(ClientConnection client, CancellationToken serverToken)
    {
        // Registered first, so nothing that happens after the snapshot is missed. What is sent to it
        // meanwhile waits in its channel, because a client is promised the snapshot before any event.
        _clients[client.Id] = client;
        Task? writerTask = null;
        try
        {
            await client.SendDirectAsync(IpcMessage.Create(MessageTypes.ServerHello, new HostHelloDto
            {
                HostVersion = Protocol.HostVersion,
                ProtocolVersion = Protocol.Version,
                StartedAt = _service.StartedAt,
                Capabilities = _remote is null
                    ? new List<string> { "jobs", "settings", "handoff", "tray", "status" }
                    : new List<string> { "jobs", "settings", "handoff", "tray", "status", "remote" },
            }), serverToken).ConfigureAwait(false);

            await SendSnapshotAsync(client, serverToken).ConfigureAwait(false);
            writerTask = client.WriterLoopAsync(serverToken);

            Task<IpcMessage?>? pendingRead = null;
            while (!serverToken.IsCancellationRequested)
            {
                var message = await (pendingRead ?? IpcFraming.ReadAsync(client.Stream, serverToken)).ConfigureAwait(false);
                pendingRead = null;
                if (message is null)
                {
                    break;
                }

                if (message.V != Protocol.Version)
                {
                    await client.SendAsync(IpcMessage.Create(MessageTypes.ServerIncompatible, new IncompatibleDto
                    {
                        MinVersion = Protocol.Version,
                        MaxVersion = Protocol.Version,
                        Message = $"This host speaks protocol {Protocol.Version}.",
                    }, message.Id)).ConfigureAwait(false);
                    break;
                }

                if (message.Type == MessageTypes.FileMetadata || WaitsOnProvider(message.Type))
                {
                    pendingRead = IpcFraming.ReadAsync(client.Stream, serverToken);
                    await IpcOperationLifetime.RunAsync(token => DispatchAsync(client, message, token), pendingRead, serverToken).ConfigureAwait(false);
                }
                else await DispatchAsync(client, message, serverToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (IOException ex)
        {
            _log.Info($"IPC client disconnected: {ex.Message}");
        }
        catch (Exception ex)
        {
            _log.Error($"IPC client failed: {UrlRedactor.RedactText(ex.ToString())}");
        }
        finally
        {
            _clients.TryRemove(client.Id, out _);
            client.Writer.TryComplete();
            try
            {
                if (writerTask is not null) await writerTask.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // writer termination is best effort
            }

            await client.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A snapshot too large for a frame would refuse every client on every connect, for as long as
    /// the saved jobs stay as they are. Failure words go first, because the jobs are what a client needs.
    /// </summary>
    private async Task SendSnapshotAsync(ClientConnection client, CancellationToken cancellationToken)
    {
        var snapshot = _service.GetSnapshot();
        try
        {
            await client.SendDirectAsync(IpcMessage.Create(MessageTypes.JobsSnapshot, snapshot), cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            _log.Warn($"The snapshot of {snapshot.Jobs.Count} downloads was too large to send, so their failure messages were left out.");
            foreach (var job in snapshot.Jobs)
            {
                if (job.Failure is { } failure)
                {
                    failure.Message = "Too long to show.";
                }
            }

            await client.SendDirectAsync(IpcMessage.Create(MessageTypes.JobsSnapshot, snapshot), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task DispatchAsync(ClientConnection client, IpcMessage message, CancellationToken cancellationToken)
    {
        try
        {
            if (await TryDispatchRemoteAsync(client, message, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            if (Archives is { } archives && ArchiveIpcDispatcher.Handles(message.Type))
            {
                await client.SendAsync(await archives.DispatchAsync(message, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
                return;
            }

            switch (message.Type)
            {
                case MessageTypes.HostPing:
                    await Reply(client, message, MessageTypes.HostActionResult,
                        new HostActionResult { Ok = true, Message = "pong" }).ConfigureAwait(false);
                    break;

                case MessageTypes.HostStatus:
                    await Reply(client, message, MessageTypes.HostStatusResult,
                        _service.GetSnapshot().Status).ConfigureAwait(false);
                    break;

                case MessageTypes.HostCleanOrphans:
                    var cleanOrphans = message.GetPayload<CleanOrphansRequest>() ?? new CleanOrphansRequest();
                    await Reply(client, message, MessageTypes.HostCleanOrphansResult,
                        await _service.CleanOrphanPartFilesAsync(cleanOrphans.Delete, cancellationToken)
                            .ConfigureAwait(false))
                        .ConfigureAwait(false);
                    break;

                case MessageTypes.StatisticsGet:
                    await Reply(client, message, MessageTypes.StatisticsResult, _service.GetStatistics()).ConfigureAwait(false);
                    break;

                case MessageTypes.JobsSnapshot:
                case MessageTypes.JobsQuery:
                    await Reply(client, message, MessageTypes.JobsSnapshot, _service.GetSnapshot())
                        .ConfigureAwait(false);
                    break;

                case MessageTypes.JobAdd:
                    var add = message.GetPayload<AddJobRequest>();
                    var addResult = await _service.AddJobAsync(add, cancellationToken).ConfigureAwait(false);
                    await Reply(client, message, MessageTypes.JobAddResult, addResult).ConfigureAwait(false);
                    break;

                case MessageTypes.FileMetadata:
                    var metadata = await _service.ReadFileMetadataAsync(message.GetPayload<FileMetadataRequest>(), cancellationToken).ConfigureAwait(false);
                    await Reply(client, message, MessageTypes.FileMetadataResult, metadata).ConfigureAwait(false);
                    break;

                case MessageTypes.HandoffRequest:
                    await HandleHandoffAsync(client, message, cancellationToken).ConfigureAwait(false);
                    break;

                case MessageTypes.JobPause:
                    var pause = message.GetPayload<JobActionRequest>();
                    await Reply(client, message, MessageTypes.JobActionResult,
                        await _service.PauseAsync(pause.JobId, cancellationToken).ConfigureAwait(false))
                        .ConfigureAwait(false);
                    break;

                case MessageTypes.JobResume:
                case MessageTypes.JobRetry:
                    var resume = message.GetPayload<JobActionRequest>();
                    await Reply(client, message, MessageTypes.JobActionResult,
                        await _service.ResumeAsync(resume.JobId, cancellationToken).ConfigureAwait(false))
                        .ConfigureAwait(false);
                    break;

                case MessageTypes.JobCancel:
                    var cancel = message.GetPayload<JobActionRequest>();
                    await Reply(client, message, MessageTypes.JobActionResult,
                        await _service.CancelAsync(cancel.JobId, cancel.DeleteFile, cancellationToken,
                            onlyIfUnfinished: cancel.OnlyIfUnfinished).ConfigureAwait(false))
                        .ConfigureAwait(false);
                    break;

                case MessageTypes.JobRemove:
                    var remove = message.GetPayload<JobActionRequest>();
                    await Reply(client, message, MessageTypes.JobActionResult,
                        await (remove.JobIds is { Count: > 0 } removeIds
                            ? _service.RemoveManyAsync(removeIds, remove.DeleteFile, cancellationToken)
                            : _service.RemoveAsync(remove.JobId, remove.DeleteFile, cancellationToken)).ConfigureAwait(false))
                        .ConfigureAwait(false);
                    break;

                case MessageTypes.JobMoveToQueue:
                    var moveToQueue = message.GetPayload<JobQueueRequest>();
                    await Reply(client, message, MessageTypes.JobActionResult,
                        await (moveToQueue.Detach ? _service.DetachFromQueueAsync(moveToQueue.JobId, cancellationToken) :
                            _service.MoveToQueueAsync(moveToQueue.JobId, moveToQueue.QueueId, cancellationToken))
                            .ConfigureAwait(false))
                        .ConfigureAwait(false);
                    break;

                case MessageTypes.JobRefreshUrl:
                    var refreshUrl = message.GetPayload<JobRefreshUrlRequest>();
                    await Reply(client, message, MessageTypes.JobActionResult,
                        await _service.RefreshUrlAsync(refreshUrl.JobId, refreshUrl.Url, refreshUrl.ExplicitDirect, cancellationToken)
                            .ConfigureAwait(false))
                        .ConfigureAwait(false);
                    break;

                case MessageTypes.JobConnections:
                    var connectionsRequest = message.GetPayload<JobActionRequest>();
                    await Reply(client, message, MessageTypes.JobConnectionsResult,
                        _service.GetConnections(connectionsRequest.JobId)).ConfigureAwait(false);
                    break;

                case MessageTypes.JobUpdate:
                    var jobUpdate = message.GetPayload<JobUpdateRequest>();
                    await Reply(client, message, MessageTypes.JobActionResult,
                        await _service.UpdateJobAsync(jobUpdate, cancellationToken)
                            .ConfigureAwait(false))
                        .ConfigureAwait(false);
                    break;

                case MessageTypes.JobSegments:
                    var segmentsRequest = message.GetPayload<JobActionRequest>();
                    await Reply(client, message, MessageTypes.JobSegmentsResult,
                        await _service.GetSegmentsAsync(segmentsRequest.JobId, cancellationToken)
                            .ConfigureAwait(false))
                        .ConfigureAwait(false);
                    break;

                case MessageTypes.UpdateCheck:
                    await Reply(client, message, MessageTypes.UpdateCheckResult,
                        await _service.CheckForUpdatesAsync(cancellationToken).ConfigureAwait(false))
                        .ConfigureAwait(false);
                    break;

                case MessageTypes.UpdateDownload:
                    await Reply(client, message, MessageTypes.UpdateDownloadResult,
                        await _service.DownloadUpdateAsync(cancellationToken).ConfigureAwait(false))
                        .ConfigureAwait(false);
                    break;

                case MessageTypes.UpdateInstall:
                    var installRequest = message.GetPayload<UpdateInstallRequest>() ??
                                         new UpdateInstallRequest();
                    var installResult = await _service.InstallUpdateAsync(
                        installRequest, cancellationToken).ConfigureAwait(false);
                    try
                    {
                        await Reply(client, message, MessageTypes.UpdateInstallResult, installResult)
                            .ConfigureAwait(false);
                    }
                    finally
                    {
                        // The helper already waits for this Host. A client that
                        // disconnects before its reply must not strand staging.
                        if (installResult.Ok) _service.RequestUpdateShutdown();
                    }

                    break;

                case MessageTypes.UpdateDefer:
                    var deferRequest = message.GetPayload<UpdateDeferRequest>();
                    await Reply(client, message, MessageTypes.SettingsResult,
                        await _service.DeferUpdateAsync(deferRequest.Version, cancellationToken)
                            .ConfigureAwait(false))
                        .ConfigureAwait(false);
                    break;

                case MessageTypes.JobsPauseAll:
                    await _service.PauseAllAsync(cancellationToken).ConfigureAwait(false);
                    await Reply(client, message, MessageTypes.HostActionResult,
                        new HostActionResult { Ok = true, Message = "All downloads paused." }).ConfigureAwait(false);
                    break;

                case MessageTypes.JobsResumeAll:
                    await _service.ResumeAllAsync(cancellationToken).ConfigureAwait(false);
                    await Reply(client, message, MessageTypes.HostActionResult,
                        new HostActionResult { Ok = true, Message = "All downloads resumed." }).ConfigureAwait(false);
                    break;

                case MessageTypes.SettingsGet:
                    await Reply(client, message, MessageTypes.SettingsResult,
                        new SettingsResult
                        {
                            Ok = true,
                            Settings = _service.GetSnapshot().Settings,
                        }).ConfigureAwait(false);
                    break;

                case MessageTypes.FolderSpace:
                    await Reply(client, message, MessageTypes.FolderSpaceResult,
                        await _service.GetFolderSpaceAsync(message.GetPayload<FolderSpaceRequest>(), cancellationToken).ConfigureAwait(false))
                        .ConfigureAwait(false);
                    break;

                case MessageTypes.SettingsUpdate:
                    var update = message.GetPayload<SettingsUpdateRequest>();
                    await Reply(client, message, MessageTypes.SettingsResult,
                        await _service.UpdateSettingsAsync(update.Settings, cancellationToken).ConfigureAwait(false))
                        .ConfigureAwait(false);
                    break;

                case MessageTypes.GroupAction:
                    var groupAction = message.GetPayload<GroupActionRequest>();
                    await Reply(client, message, MessageTypes.GroupActionResult,
                        await _service.GroupActionAsync(groupAction, cancellationToken).ConfigureAwait(false))
                        .ConfigureAwait(false);
                    break;

                case MessageTypes.QueueAction:
                    var queueAction = message.GetPayload<QueueActionRequest>();
                    await Reply(client, message, MessageTypes.SettingsResult,
                        await _service.QueueActionAsync(queueAction, cancellationToken).ConfigureAwait(false))
                        .ConfigureAwait(false);
                    break;

                case MessageTypes.HostOpenUi:
                    var open = message.GetPayload<HostActionRequest>();
                    _controller.OpenUi(string.IsNullOrWhiteSpace(open.Action) ? null : open.Action);
                    await Reply(client, message, MessageTypes.HostActionResult,
                        new HostActionResult { Ok = true, Message = "Opening the desktop UI." }).ConfigureAwait(false);
                    break;

                case MessageTypes.HostPickFolder:
                    var pickFolder = message.GetPayload<FolderPickRequest>();
                    var pickedFolder = await _controller.PickFolderAsync(pickFolder.InitialPath, cancellationToken)
                        .ConfigureAwait(false);
                    await Reply(client, message, MessageTypes.HostFolderPicked, pickedFolder).ConfigureAwait(false);
                    break;

                case MessageTypes.HostShutdown:
                    // Written here and not queued: stopping ends the writer loop, and a reply still in its queue would be lost.
                    await client.SendDirectAsync(IpcMessage.Create(MessageTypes.HostActionResult,
                        new HostActionResult { Ok = true, Message = "Shutting down." }, message.Id), cancellationToken).ConfigureAwait(false);
                    await NotifyExitingAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                    _controller.RequestShutdown();
                    break;

                default:
                    await Reply(client, message, MessageTypes.HostActionResult,
                        new HostActionResult { Ok = false, Message = $"Unknown message type '{message.Type}'." })
                        .ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex)
        {
            var reason = UrlRedactor.RedactText(ex.Message);
            _log.Error($"IPC command '{message.Type}' failed: {reason}");
            await Reply(client, message, MessageTypes.HostActionResult,
                new HostActionResult { Ok = false, Message = reason }).ConfigureAwait(false);
        }
    }

    private async Task HandleHandoffAsync(ClientConnection client, IpcMessage message, CancellationToken cancellationToken)
    {
        var handoff = message.GetPayload<HandoffRequest>();
        if (string.IsNullOrWhiteSpace(handoff.RequestId) || string.IsNullOrWhiteSpace(handoff.Url))
        {
            await Reply(client, message, MessageTypes.HandoffAck, new HandoffAck
            {
                Accepted = false,
                Error = "requestId and url are required.",
                ErrorCode = "invalid_handoff",
            }).ConfigureAwait(false);
            return;
        }

        if (!BrowserBridge.IsTrustedExtensionId(handoff.ExtensionId))
        {
            await Reply(client, message, MessageTypes.HandoffAck, new HandoffAck
            {
                Accepted = false,
                Error = "The desktop app does not recognise this extension. Update the app.",
                ErrorCode = "untrusted_extension",
            }).ConfigureAwait(false);
            return;
        }

        var settings = _service.GetSnapshot().Settings;
        if (!settings.BrowserIntegrationEnabled)
        {
            await Reply(client, message, MessageTypes.HandoffAck, new HandoffAck
            {
                Accepted = false,
                Error = "Turn on browser downloads in the desktop app's Options.",
                ErrorCode = "integration_disabled",
            }).ConfigureAwait(false);
            return;
        }

        var request = new AddJobRequest
        {
            Url = handoff.Url,
            FinalUrl = handoff.FinalUrl,
            DestinationDirectory = handoff.DestinationDirectory,
            FileName = handoff.SuggestedFileName,
            StartMode = handoff.StartMode,
            Source = handoff.IsAutoIntercept ? "chromeAuto" : "chromeContextMenu",
            ClientRequestId = handoff.RequestId,
            OwnerExtensionId = handoff.ExtensionId,
            Headers = handoff.Headers ?? new Dictionary<string, string>(),
            Referrer = handoff.Referrer,
            UserAgent = handoff.UserAgent,
            PageUrl = handoff.PageUrl,
            Description = handoff.MimeType,
            PackageKey = handoff.PackageKey,
            PackageName = handoff.PackageName,
        };

        var result = await _service.AddJobAsync(request, cancellationToken).ConfigureAwait(false);
        await Reply(client, message, MessageTypes.HandoffAck, new HandoffAck
        {
            Accepted = result.Accepted,
            Duplicate = result.Duplicate,
            JobId = result.Job?.Id ?? Guid.Empty,
            Job = result.Job,
            Error = result.Error,
            ErrorCode = result.ErrorCode,
        }).ConfigureAwait(false);
    }

    private static Task Reply<T>(ClientConnection client, IpcMessage request, string type, T payload)
        => client.SendAsync(IpcMessage.Create(type, payload, request.Id));

    private void OnUpdateProgressChanged(object? sender, UpdateProgressDto progress)
    {
        if (progress.Stage == "downloading" && Interlocked.Exchange(ref _updateWindowOpened, 1) == 0)
            _controller.OpenUi("update-progress");
        if (progress.Stage is "failed" or "postponed" or "upToDate")
            Interlocked.Exchange(ref _updateWindowOpened, 0);
        var message = IpcMessage.Create(MessageTypes.UpdateProgress, progress);
        foreach (var client in _clients.Values) client.Writer.TryWrite(message);
    }

    /// <summary>
    /// Tells connected frontends that the user asked the whole application to exit, so they close
    /// instead of waiting offline for a Host that will not come back. Best effort, bounded by <paramref name="timeout"/>.
    /// </summary>
    public async Task NotifyExitingAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var message = IpcMessage.Create(MessageTypes.HostExiting, new { });
        await Task.WhenAll(_clients.Values.Select(async client =>
        {
            try { await client.SendDirectAsync(message, cts.Token).ConfigureAwait(false); }
            catch (Exception) { /* client already gone */ }
        })).ConfigureAwait(false);
    }

    private static readonly TimeSpan GroupsBroadcastDelay = TimeSpan.FromMilliseconds(100);
    private List<DownloadGroupDto>? _lastGroups;
    private int _groupsBroadcastPending;

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        // Packages are shown in the download list, so clients need membership changes live
        // (a browser batch or a split archive can form one without any UI involvement). A batch
        // changes them once per file and each message carries every package, so a burst shares one.
        if (Interlocked.Exchange(ref _groupsBroadcastPending, 1) == 0) _ = BroadcastGroupsAsync();
    }

    private async Task BroadcastGroupsAsync()
    {
        try
        {
            await Task.Delay(GroupsBroadcastDelay, _cts.Token).ConfigureAwait(false);
            Interlocked.Exchange(ref _groupsBroadcastPending, 0);
            var groups = _service.GetDownloadGroups();
            lock (_clients)
            {
                if (_lastGroups is not null && SameGroups(_lastGroups, groups)) return;
                _lastGroups = groups;
            }
            var message = IpcMessage.Create(MessageTypes.GroupsChanged, groups);
            foreach (var client in _clients.Values)
            {
                client.Writer.TryWrite(message);
            }
        }
        catch (OperationCanceledException) { /* stopping */ }
        catch (Exception ex) { _log.Warn($"Could not announce package changes: {ex.GetType().Name}"); }
    }

    private static bool SameGroups(List<DownloadGroupDto> left, List<DownloadGroupDto> right)
        => left.Count == right.Count && left.Zip(right).All(pair =>
            pair.First.Id == pair.Second.Id && pair.First.Name == pair.Second.Name && pair.First.Key == pair.Second.Key &&
            pair.First.JobIds.SequenceEqual(pair.Second.JobIds));

    private void OnJobChanged(object? sender, JobChangeEventArgs e)
    {
        var message = IpcMessage.Create(MessageTypes.JobEvent, new JobEventDto
        {
            Job = e.Job,
            EventType = e.ChangeType,
        });

        foreach (var client in _clients.Values)
        {
            client.Writer.TryWrite(message);
        }
    }

    private sealed class ClientConnection : IAsyncDisposable
    {
        private readonly NamedPipeServerStream _pipe;
        private readonly SemaphoreSlim _directWrite = new(1, 1);
        private readonly Channel<IpcMessage> _channel;
        private readonly FileLogger _log;

        public ClientConnection(NamedPipeServerStream pipe, FileLogger log)
        {
            _pipe = pipe;
            _log = log;
            _channel = Channel.CreateBounded<IpcMessage>(new BoundedChannelOptions(512)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });
        }

        public Guid Id { get; } = Guid.NewGuid();
        public Stream Stream => _pipe;
        public ChannelWriter<IpcMessage> Writer => _channel.Writer;

        public async Task SendDirectAsync(IpcMessage message, CancellationToken cancellationToken)
        {
            await _directWrite.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await IpcFraming.WriteAsync(_pipe, message, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _directWrite.Release();
            }
        }

        public Task SendAsync(IpcMessage message)
        {
            Writer.TryWrite(message);
            return Task.CompletedTask;
        }

        public async Task WriterLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                await foreach (var message in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    try
                    {
                        await SendDirectAsync(message, cancellationToken).ConfigureAwait(false);
                    }
                    catch (InvalidDataException)
                    {
                        // Too large for a frame, and nothing of it was sent. Everything after it still has to get through.
                        _log.Warn($"IPC message '{message.Type}' was too large to send.");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // expected
            }
            catch (IOException)
            {
                // client went away
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _pipe.DisposeAsync().ConfigureAwait(false);
            }
            catch (IOException)
            {
                // best effort
            }

            _directWrite.Dispose();
        }
    }
}
