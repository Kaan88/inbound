using System.Collections.Concurrent;
using DownloadManager.Contracts;
using DownloadManager.Core;
using DownloadManager.Core.Providers;
using DownloadManager.Runtime.Archives;
using DownloadManager.Runtime.Remote;

namespace DownloadManager.Runtime.Ipc;

/// <summary>
/// The Host's whole IPC protocol without a transport: the hello and snapshot a client gets first, every command's
/// answer, and the broadcasts (<c>job.event</c>, <c>update.progress</c>, <c>groups.changed</c>, <c>remote.changed</c>,
/// <c>archive.changed</c>, <c>host.exiting</c>). The desktop Host's named pipe is one transport; a server's
/// WebSocket is another. What the machine itself can do comes from <see cref="IHostPlatform"/>.
/// </summary>
public sealed partial class HostCommandDispatcher
{
    private const string UpdatesUnavailable = "Updates are not available here.";
    private static readonly TimeSpan GroupsBroadcastDelay = TimeSpan.FromMilliseconds(100);

    private readonly DownloadManagerService _service;
    private readonly IHostPlatform _platform;
    private readonly IHostLog _log;
    private readonly RemoteCommands? _remote;
    private readonly ConcurrentDictionary<Guid, IClientSession> _sessions = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _groupsGate = new();
    private int _updateWindowOpened;
    private List<DownloadGroupDto>? _lastGroups;
    private int _groupsBroadcastPending;

    public HostCommandDispatcher(DownloadManagerService service, IHostPlatform platform, IHostLog log, RemoteCommands? remote = null)
    {
        _service = service;
        _platform = platform;
        _log = log;
        _remote = remote;
    }

    /// <summary>Answers <c>archive.*</c>. Set before <see cref="Start"/>.</summary>
    public ArchiveIpcDispatcher? Archives { get; init; }

    /// <summary>
    /// What the hello offers. The desktop Host's list keeps this order, because frontends have read it since protocol 1;
    /// anything new is only ever appended.
    /// </summary>
    public List<string> Capabilities
    {
        get
        {
            var capabilities = new List<string> { "jobs", "settings", "handoff" };
            if (_platform.Capabilities.SupportsTray) capabilities.Add("tray");
            capabilities.Add("status");
            if (_remote is not null) capabilities.Add("remote");
            return capabilities;
        }
    }

    /// <summary>Starts the broadcasts. Before any transport accepts a client.</summary>
    public void Start()
    {
        _service.JobChanged += OnJobChanged;
        _service.UpdateProgressChanged += OnUpdateProgressChanged;
        _service.SettingsChanged += OnSettingsChanged;
        if (_remote is not null) _remote.Changed += OnRemoteChanged;
    }

    /// <summary>Ends the broadcasts and a waiting package announcement. The transports close their own sessions.</summary>
    public void Stop()
    {
        _service.JobChanged -= OnJobChanged;
        _service.UpdateProgressChanged -= OnUpdateProgressChanged;
        _service.SettingsChanged -= OnSettingsChanged;
        if (_remote is not null) _remote.Changed -= OnRemoteChanged;
        _stopping.Cancel();
    }

    /// <summary>
    /// These wait on something that can take long (a server's headers, the provider), so a transport runs them under a
    /// lifetime that ends when their client goes away (<see cref="IpcOperationLifetime"/>). The rest are quick.
    /// </summary>
    public static bool NeedsLifetime(string type) => type == MessageTypes.FileMetadata || WaitsOnProvider(type);

    /// <summary>
    /// Registers the session and sends it the hello and the snapshot, directly. Registered first, so nothing that
    /// happens after the snapshot is missed: what is broadcast meanwhile waits in its queue, because a client is
    /// promised the snapshot before any event. Start the session's writer after this returns. A session that fails
    /// here stays registered until <see cref="CloseSession"/>.
    /// </summary>
    public async Task OpenSessionAsync(IClientSession client, CancellationToken cancellationToken)
    {
        _sessions[client.Id] = client;
        await client.SendDirectAsync(IpcMessage.Create(MessageTypes.ServerHello, new HostHelloDto
        {
            HostVersion = Protocol.HostVersion,
            ProtocolVersion = Protocol.Version,
            StartedAt = _service.StartedAt,
            Capabilities = Capabilities,
        }), cancellationToken).ConfigureAwait(false);

        await SendSnapshotAsync(client, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>No more broadcasts go to this session.</summary>
    public void CloseSession(IClientSession client) => _sessions.TryRemove(client.Id, out _);

    /// <summary>
    /// A frame on another protocol version is answered with <c>server.incompatible</c>; the transport then ends the
    /// connection, once its queue has gone out.
    /// </summary>
    /// <returns>Whether it was refused.</returns>
    public bool RejectIfIncompatible(IClientSession client, IpcMessage message)
    {
        if (message.V == Protocol.Version) return false;
        client.Enqueue(IpcMessage.Create(MessageTypes.ServerIncompatible, new IncompatibleDto
        {
            MinVersion = Protocol.Version,
            MaxVersion = Protocol.Version,
            Message = $"This host speaks protocol {Protocol.Version}.",
        }, message.Id));
        return true;
    }

    /// <summary>
    /// A snapshot too large for a frame would refuse every client on every connect, for as long as
    /// the saved jobs stay as they are. Failure words go first, because the jobs are what a client needs.
    /// </summary>
    private async Task SendSnapshotAsync(IClientSession client, CancellationToken cancellationToken)
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

    /// <summary>
    /// Answers one command. Replies go to <paramref name="client"/>'s queue, behind what it was already sent. A command
    /// that <see cref="NeedsLifetime"/> should get a token that ends when its client goes away.
    /// </summary>
    public async Task DispatchAsync(IClientSession client, IpcMessage message, CancellationToken cancellationToken)
    {
        try
        {
            if (await TryDispatchRemoteAsync(client, message, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            if (Archives is { } archives && ArchiveIpcDispatcher.Handles(message.Type))
            {
                client.Enqueue(await archives.DispatchAsync(message, cancellationToken).ConfigureAwait(false));
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
                    if (!_platform.Capabilities.SupportsUpdater)
                    {
                        await Reply(client, message, MessageTypes.UpdateCheckResult, new UpdateCheckResult { Error = UpdatesUnavailable }).ConfigureAwait(false);
                        break;
                    }

                    await Reply(client, message, MessageTypes.UpdateCheckResult,
                        await _service.CheckForUpdatesAsync(cancellationToken).ConfigureAwait(false))
                        .ConfigureAwait(false);
                    break;

                case MessageTypes.UpdateDownload:
                    if (!_platform.Capabilities.SupportsUpdater)
                    {
                        await Reply(client, message, MessageTypes.UpdateDownloadResult, new UpdateDownloadResult { Error = UpdatesUnavailable }).ConfigureAwait(false);
                        break;
                    }

                    await Reply(client, message, MessageTypes.UpdateDownloadResult,
                        await _service.DownloadUpdateAsync(cancellationToken).ConfigureAwait(false))
                        .ConfigureAwait(false);
                    break;

                case MessageTypes.UpdateInstall:
                    if (!_platform.Capabilities.SupportsUpdater)
                    {
                        await Reply(client, message, MessageTypes.UpdateInstallResult, new UpdateInstallResult { Error = UpdatesUnavailable }).ConfigureAwait(false);
                        break;
                    }

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
                    if (!_platform.Capabilities.SupportsUpdater)
                    {
                        await Reply(client, message, MessageTypes.SettingsResult, new SettingsResult { Error = UpdatesUnavailable, Settings = _service.GetSnapshot().Settings }).ConfigureAwait(false);
                        break;
                    }

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
                    if (!_platform.Capabilities.SupportsOpenUi)
                    {
                        await Reply(client, message, MessageTypes.HostActionResult,
                            new HostActionResult { Ok = false, Message = "Opening the desktop UI is not available here." }).ConfigureAwait(false);
                        break;
                    }

                    var open = message.GetPayload<HostActionRequest>();
                    _platform.OpenUi(string.IsNullOrWhiteSpace(open.Action) ? null : open.Action);
                    await Reply(client, message, MessageTypes.HostActionResult,
                        new HostActionResult { Ok = true, Message = "Opening the desktop UI." }).ConfigureAwait(false);
                    break;

                case MessageTypes.HostPickFolder:
                    if (!_platform.Capabilities.SupportsFolderPicker)
                    {
                        await Reply(client, message, MessageTypes.HostFolderPicked,
                            new FolderPickResult { Error = "Folder selection is unavailable here." }).ConfigureAwait(false);
                        break;
                    }

                    var pickFolder = message.GetPayload<FolderPickRequest>();
                    var pickedFolder = await _platform.PickFolderAsync(pickFolder.InitialPath, cancellationToken)
                        .ConfigureAwait(false);
                    await Reply(client, message, MessageTypes.HostFolderPicked, pickedFolder).ConfigureAwait(false);
                    break;

                case MessageTypes.HostShutdown:
                    if (!_platform.Capabilities.SupportsShutdown)
                    {
                        await Reply(client, message, MessageTypes.HostActionResult,
                            new HostActionResult { Ok = false, Message = "Shutting down is not available here." }).ConfigureAwait(false);
                        break;
                    }

                    // Written here and not queued: stopping ends the writer loop, and a reply still in its queue would be lost.
                    await client.SendDirectAsync(IpcMessage.Create(MessageTypes.HostActionResult,
                        new HostActionResult { Ok = true, Message = "Shutting down." }, message.Id), cancellationToken).ConfigureAwait(false);
                    await NotifyExitingAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                    _platform.RequestShutdown();
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

    private async Task HandleHandoffAsync(IClientSession client, IpcMessage message, CancellationToken cancellationToken)
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

    private static Task Reply<T>(IClientSession client, IpcMessage request, string type, T payload)
    {
        client.Enqueue(IpcMessage.Create(type, payload, request.Id));
        return Task.CompletedTask;
    }

    private void Broadcast(IpcMessage message)
    {
        foreach (var client in _sessions.Values) client.Enqueue(message);
    }

    /// <summary>
    /// Tells connected frontends that the user asked the whole application to exit, so they close
    /// instead of waiting offline for a Host that will not come back. Best effort, bounded by <paramref name="timeout"/>.
    /// </summary>
    public async Task NotifyExitingAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var message = IpcMessage.Create(MessageTypes.HostExiting, new { });
        await Task.WhenAll(_sessions.Values.Select(async client =>
        {
            try { await client.SendDirectAsync(message, cts.Token).ConfigureAwait(false); }
            catch (Exception) { /* client already gone */ }
        })).ConfigureAwait(false);
    }

    /// <summary>Whole-state broadcast of one set. The Host subscribes this to the coordinator's change event.</summary>
    public void BroadcastArchiveChanged(ArchiveChangedDto change)
        => Broadcast(IpcMessage.Create(ArchiveMessageTypes.Changed, change));

    private void OnUpdateProgressChanged(object? sender, UpdateProgressDto progress) => BroadcastUpdateProgress(progress);

    /// <summary>
    /// An update's download opens its progress window once, where there is a window to open. A failure, a postponed
    /// install or "up to date" lets the next download open it again.
    /// </summary>
    public void BroadcastUpdateProgress(UpdateProgressDto progress)
    {
        if (progress.Stage == "downloading" && _platform.Capabilities.SupportsOpenUi &&
            Interlocked.Exchange(ref _updateWindowOpened, 1) == 0)
            _platform.OpenUi("update-progress");
        if (progress.Stage is "failed" or "postponed" or "upToDate")
            Interlocked.Exchange(ref _updateWindowOpened, 0);
        Broadcast(IpcMessage.Create(MessageTypes.UpdateProgress, progress));
    }

    private void OnSettingsChanged(object? sender, AppSettings settings) => ScheduleGroupsBroadcast();

    /// <summary>
    /// Packages are shown in the download list, so clients need membership changes live
    /// (a browser batch or a split archive can form one without any UI involvement). A batch
    /// changes them once per file and each message carries every package, so a burst shares one,
    /// and nothing is sent when the packages are as they were last announced.
    /// </summary>
    public void ScheduleGroupsBroadcast()
    {
        if (Interlocked.Exchange(ref _groupsBroadcastPending, 1) == 0) _ = BroadcastGroupsAsync();
    }

    private async Task BroadcastGroupsAsync()
    {
        try
        {
            await Task.Delay(GroupsBroadcastDelay, _stopping.Token).ConfigureAwait(false);
            Interlocked.Exchange(ref _groupsBroadcastPending, 0);
            var groups = _service.GetDownloadGroups();
            lock (_groupsGate)
            {
                if (_lastGroups is not null && SameGroups(_lastGroups, groups)) return;
                _lastGroups = groups;
            }

            Broadcast(IpcMessage.Create(MessageTypes.GroupsChanged, groups));
        }
        catch (OperationCanceledException) { /* stopping */ }
        catch (Exception ex) { _log.Warn($"Could not announce package changes: {ex.GetType().Name}"); }
    }

    private static bool SameGroups(List<DownloadGroupDto> left, List<DownloadGroupDto> right)
        => left.Count == right.Count && left.Zip(right).All(pair =>
            pair.First.Id == pair.Second.Id && pair.First.Name == pair.Second.Name && pair.First.Key == pair.Second.Key &&
            pair.First.JobIds.SequenceEqual(pair.Second.JobIds));

    private void OnJobChanged(object? sender, JobChangeEventArgs e) => BroadcastJobChanged(e);

    public void BroadcastJobChanged(JobChangeEventArgs e)
        => Broadcast(IpcMessage.Create(MessageTypes.JobEvent, new JobEventDto
        {
            Job = e.Job,
            EventType = e.ChangeType,
        }));
}
