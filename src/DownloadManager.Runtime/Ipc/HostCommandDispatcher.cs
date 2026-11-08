using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
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
/// WebSocket is another. What the machine itself can do comes from <see cref="IHostPlatform"/>; what one client may do
/// is its session's <see cref="SessionRights"/>, given when it opens: the platform's own, or a web session's
/// (<see cref="WebSessions"/>) when its <c>client.hello</c> asked for that role.
/// </summary>
public sealed partial class HostCommandDispatcher
{
    private const string UpdatesUnavailable = "Updates are not available here.";
    private const string ProgramSettingsUnavailable =
        "The post-download command, FFmpeg path, update source and system completion actions can't be changed from here.";
    private const string CompletionProgramUnavailable = "A completion program can't be set from here.";
    private const string CompletionActionsUnavailable =
        "What happens when a download is done (open the file, shut down, exit) can only be set in the app on this PC.";
    private const string SendToOtherProgramUnavailable = "A container can only be handed to another download manager in the app on this PC.";
    private const string ProviderKeyUnavailable = "The provider's key can only be changed in the app on this PC.";
    private const string ExtractionFolderUnavailable = "The extraction folder can only be set in the app on this PC.";
    private const string ExtractionRemovalUnavailable =
        "Deleting archives after extraction and overwriting files can only be turned on in the app on this PC.";
    private static readonly TimeSpan GroupsBroadcastDelay = TimeSpan.FromMilliseconds(100);

    private readonly DownloadManagerService _service;
    private readonly IHostPlatform _platform;
    private readonly IHostLog _log;
    private readonly RemoteCommands? _remote;
    private readonly ConcurrentDictionary<Guid, IClientSession> _sessions = new();
    // Every open session with its rights, events or not, for CloseSessions. The rights also live on the session object
    // itself (_rights), so a command still being answered after its session closed keeps that session's rights.
    private readonly ConcurrentDictionary<Guid, (IClientSession Client, SessionRights Rights)> _open = new();
    private readonly ConditionalWeakTable<IClientSession, SessionRights> _rights = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _groupsGate = new();
    private int _updateWindowOpened;
    private int _exiting;
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
    /// Answers <c>folders.browse</c> and <c>folders.create</c> where the platform offers folder browsing
    /// (<see cref="PlatformCapabilities.SupportsFolderBrowse"/>). Null, or a platform without it, refuses them.
    /// </summary>
    public FolderBrowser? Folders { get; init; }

    /// <summary>
    /// The rights a <c>web</c> session gets (<see cref="WebSessionProfile"/>). Null: this Host has no such role, and a
    /// <c>client.hello</c> that asks for it is refused (<see cref="OffersRole"/>).
    /// </summary>
    public SessionRights? WebSessions { get; init; }

    /// <summary>The platform's own rights: the desktop window, the CLI, the browser's native host, or a server's client.</summary>
    public SessionRights PlatformRights => new()
    {
        Role = ClientRoles.Desktop,
        Capabilities = _platform.Capabilities,
        Archives = Archives,
        Folders = Folders,
        Remote = _remote,
    };

    /// <summary>What the hello offers a session with the platform's own rights. See <see cref="CapabilitiesOf"/>.</summary>
    public List<string> Capabilities => CapabilitiesOf(PlatformRights);

    /// <summary>
    /// What the hello offers a session with these rights. It is a set: clients look a name up (<c>Contains</c>) and never
    /// read a position. The desktop Host's list starts exactly as it has since protocol 1, in that order, and a new name
    /// is only ever appended. A platform without something leaves its name out, so on one without a tray the names after
    /// <c>tray</c> sit one place earlier; that is why a position can't be relied on.
    /// </summary>
    public static List<string> CapabilitiesOf(SessionRights rights)
    {
        var platform = rights.Capabilities;
        var capabilities = new List<string> { "jobs", "settings", "handoff" };
        if (platform.SupportsTray) capabilities.Add("tray");
        capabilities.Add("status");
        if (rights.Remote is not null) capabilities.Add("remote");
        if (FoldersOf(rights) is not null) capabilities.Add("folders");
        if (platform.SupportsUpdater) capabilities.Add("updater");
        if (platform.SupportsProgramSettings) capabilities.Add("programSettings");
        if (platform.SupportsFolderPicker) capabilities.Add("folderPicker");
        if (platform.SupportsOpenUi) capabilities.Add("openUi");
        if (platform.SupportsShutdown) capabilities.Add("shutdown");
        if (platform.SupportsCompletionActions) capabilities.Add("completionActions");
        return capabilities;
    }

    private static FolderBrowser? FoldersOf(SessionRights rights) => rights.Capabilities.SupportsFolderBrowse ? rights.Folders : null;

    /// <summary>Whether a <c>client.hello</c> may ask for this role. Null and <c>desktop</c> always; <c>web</c> where there is one.</summary>
    public bool OffersRole(string? role) => RightsFor(role) is not null;

    private SessionRights? RightsFor(string? role) => role switch
    {
        null or "" or ClientRoles.Desktop => PlatformRights,
        ClientRoles.Web => WebSessions,
        _ => null,
    };

    /// <summary>The session's rights. One that was never opened (a server's one-shot request) has the platform's.</summary>
    private SessionRights RightsOf(IClientSession client) => _rights.TryGetValue(client, out var rights) ? rights : PlatformRights;

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
    public Task OpenSessionAsync(IClientSession client, CancellationToken cancellationToken)
        => OpenSessionAsync(client, null, cancellationToken);

    /// <summary>
    /// As <see cref="OpenSessionAsync(IClientSession, CancellationToken)"/>, with what the client asked for in its
    /// <c>client.hello</c> (PERF.7); null is everything, as before. Without the snapshot only the hello is sent. Without
    /// events the session is never registered, so no broadcast reaches it; its replies still do. The role it asked for
    /// gives its rights for good; a role this Host doesn't offer throws, so check <see cref="OffersRole"/> first.
    /// </summary>
    public async Task OpenSessionAsync(IClientSession client, ClientHelloDto? options, CancellationToken cancellationToken)
    {
        var rights = RightsFor(options?.Role) ?? throw new ArgumentException("This Host doesn't offer that role.", nameof(options));
        _rights.AddOrUpdate(client, rights);
        _open[client.Id] = (client, rights);
        // PERF.7: a client that asked for no events is never registered, so no broadcast reaches it.
        if (options?.Events ?? true) _sessions[client.Id] = client;
        // UI.B12: once the user asked for an exit, a new client gets host.exiting instead of the hello, so a Host
        // starting meanwhile does not take this one for a running copy and quit. Registered first and read after,
        // and NotifyExitingAsync sets it before it reads the sessions: a registered session either sees it here or is
        // told there; one without events learns it from host.openUi's answer.
        if (Interlocked.CompareExchange(ref _exiting, 0, 0) == 1)
        {
            await client.SendDirectAsync(IpcMessage.Create(MessageTypes.HostExiting, new { }), cancellationToken).ConfigureAwait(false);
            return;
        }

        await client.SendDirectAsync(IpcMessage.Create(MessageTypes.ServerHello, new HostHelloDto
        {
            HostVersion = Protocol.HostVersion,
            ProtocolVersion = Protocol.Version,
            StartedAt = _service.StartedAt,
            Capabilities = CapabilitiesOf(rights),
            HostKind = _platform.Capabilities.HostKind,
        }), cancellationToken).ConfigureAwait(false);

        if (options?.Snapshot ?? true) await SendSnapshotAsync(client, rights, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>No more broadcasts go to this session.</summary>
    public void CloseSession(IClientSession client)
    {
        _sessions.TryRemove(client.Id, out _);
        _open.TryRemove(client.Id, out _);
    }

    /// <summary>
    /// Ends every open session of this role (<see cref="ClientRoles"/>), such as all web sessions when the web UI is
    /// turned off: no broadcast reaches them from now on, and their transports close them. Returns how many.
    /// </summary>
    public int CloseSessions(string role)
    {
        var closed = 0;
        foreach (var (id, (client, rights)) in _open)
        {
            if (!string.Equals(rights.Role, role, StringComparison.Ordinal)) continue;
            _sessions.TryRemove(id, out _);
            _open.TryRemove(id, out _);
            client.Close();
            closed++;
        }

        return closed;
    }

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
    private async Task SendSnapshotAsync(IClientSession client, SessionRights rights, CancellationToken cancellationToken)
    {
        var snapshot = ForSession(_service.GetSnapshot(), rights);
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
        var rights = RightsOf(client);
        var caller = rights.Scope;
        var platform = rights.Capabilities;
        try
        {
            if (await TryDispatchRemoteAsync(client, rights, message, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            if (rights.Archives is { } archives && ArchiveIpcDispatcher.Handles(message.Type))
            {
                client.Enqueue(caller is not null && RefuseArchiveFor(archives, caller, message) is { } refused
                    ? refused
                    : await archives.DispatchAsync(message, cancellationToken).ConfigureAwait(false));
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
                        ForSession(_service.GetStatus(), rights)).ConfigureAwait(false);
                    break;

                case MessageTypes.HostCleanOrphans:
                    var cleanOrphans = message.GetPayload<CleanOrphansRequest>() ?? new CleanOrphansRequest();
                    await Reply(client, message, MessageTypes.HostCleanOrphansResult,
                        await _service.CleanOrphanPartFilesAsync(cleanOrphans.Delete, cancellationToken, caller)
                            .ConfigureAwait(false))
                        .ConfigureAwait(false);
                    break;

                case MessageTypes.StatisticsGet:
                    await Reply(client, message, MessageTypes.StatisticsResult, _service.GetStatistics()).ConfigureAwait(false);
                    break;

                case MessageTypes.SpeedHistoryGet:
                    await Reply(client, message, MessageTypes.SpeedHistoryResult, _service.GetSpeedHistory()).ConfigureAwait(false);
                    break;

                case MessageTypes.JobsSnapshot:
                case MessageTypes.JobsQuery:
                    await Reply(client, message, MessageTypes.JobsSnapshot, ForSession(_service.GetSnapshot(), rights))
                        .ConfigureAwait(false);
                    break;

                case MessageTypes.JobAdd:
                    var add = message.GetPayload<AddJobRequest>();
                    var addResult = await _service.AddJobAsync(add, cancellationToken, caller).ConfigureAwait(false);
                    await Reply(client, message, MessageTypes.JobAddResult, addResult).ConfigureAwait(false);
                    break;

                case MessageTypes.FileMetadata:
                    var metadata = await _service.ReadFileMetadataAsync(message.GetPayload<FileMetadataRequest>(), cancellationToken, caller).ConfigureAwait(false);
                    await Reply(client, message, MessageTypes.FileMetadataResult, metadata).ConfigureAwait(false);
                    break;

                case MessageTypes.HandoffRequest:
                    await HandleHandoffAsync(client, message, caller, cancellationToken).ConfigureAwait(false);
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
                            onlyIfUnfinished: cancel.OnlyIfUnfinished, caller: caller).ConfigureAwait(false))
                        .ConfigureAwait(false);
                    break;

                case MessageTypes.JobRemove:
                    var remove = message.GetPayload<JobActionRequest>();
                    await Reply(client, message, MessageTypes.JobActionResult,
                        await (remove.JobIds is { Count: > 0 } removeIds
                            ? _service.RemoveManyAsync(removeIds, remove.DeleteFile, cancellationToken, caller)
                            : _service.RemoveAsync(remove.JobId, remove.DeleteFile, cancellationToken, caller)).ConfigureAwait(false))
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
                        await _service.RefreshUrlAsync(refreshUrl.JobId, refreshUrl.Url, refreshUrl.ExplicitDirect, cancellationToken, caller)
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
                    if (!platform.SupportsProgramSettings && ChangesCompletionProgram(jobUpdate))
                    {
                        await Reply(client, message, MessageTypes.JobActionResult,
                            new JobActionResponse { Error = CompletionProgramUnavailable, Job = _service.GetJob(jobUpdate.JobId) }).ConfigureAwait(false);
                        break;
                    }

                    // Only for a web session: the server runs no completion actions, so setting them there changes nothing.
                    if (caller is not null && !platform.SupportsCompletionActions && RaisesCompletionActions(jobUpdate))
                    {
                        await Reply(client, message, MessageTypes.JobActionResult,
                            new JobActionResponse { Error = CompletionActionsUnavailable, Job = _service.GetJob(jobUpdate.JobId) }).ConfigureAwait(false);
                        break;
                    }

                    await Reply(client, message, MessageTypes.JobActionResult,
                        await _service.UpdateJobAsync(jobUpdate, cancellationToken, caller)
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
                    if (!platform.SupportsUpdater)
                    {
                        await Reply(client, message, MessageTypes.UpdateCheckResult, new UpdateCheckResult { Error = UpdatesUnavailable }).ConfigureAwait(false);
                        break;
                    }

                    await Reply(client, message, MessageTypes.UpdateCheckResult,
                        await _service.CheckForUpdatesAsync(cancellationToken).ConfigureAwait(false))
                        .ConfigureAwait(false);
                    break;

                case MessageTypes.UpdateDownload:
                    if (!platform.SupportsUpdater)
                    {
                        await Reply(client, message, MessageTypes.UpdateDownloadResult, new UpdateDownloadResult { Error = UpdatesUnavailable }).ConfigureAwait(false);
                        break;
                    }

                    await Reply(client, message, MessageTypes.UpdateDownloadResult,
                        await _service.DownloadUpdateAsync(cancellationToken).ConfigureAwait(false))
                        .ConfigureAwait(false);
                    break;

                case MessageTypes.UpdateInstall:
                    if (!platform.SupportsUpdater)
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
                    if (!platform.SupportsUpdater)
                    {
                        await Reply(client, message, MessageTypes.SettingsResult, new SettingsResult { Error = UpdatesUnavailable, Settings = _service.GetSettings() }).ConfigureAwait(false);
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
                            Settings = _service.GetSettings(),
                        }).ConfigureAwait(false);
                    break;

                case MessageTypes.SettingsDownloadFolders:
                    var folderSettings = _service.GetSettings();
                    await Reply(client, message, MessageTypes.SettingsDownloadFoldersResult,
                        new DownloadFoldersResult
                        {
                            Ok = true,
                            DefaultFolder = folderSettings.DefaultDownloadDirectory,
                            Favorites = folderSettings.FavoriteFolders
                                .Select(favorite => new FavoriteFolderDto { Name = favorite.Name, Path = favorite.Path })
                                .ToList(),
                        }).ConfigureAwait(false);
                    break;

                case MessageTypes.FolderSpace:
                    await Reply(client, message, MessageTypes.FolderSpaceResult,
                        await _service.GetFolderSpaceAsync(message.GetPayload<FolderSpaceRequest>(), cancellationToken, caller).ConfigureAwait(false))
                        .ConfigureAwait(false);
                    break;

                case MessageTypes.FoldersBrowse:
                case MessageTypes.FoldersCreate:
                    await Reply(client, message, MessageTypes.FoldersBrowseResult,
                        await BrowseFoldersAsync(rights, message, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
                    break;

                case MessageTypes.SettingsUpdate:
                    var update = message.GetPayload<SettingsUpdateRequest>();
                    if (caller is not null && RefuseSettingsForCaller(update) is { } settingsRefusal)
                    {
                        await Reply(client, message, MessageTypes.SettingsResult, settingsRefusal).ConfigureAwait(false);
                        break;
                    }

                    if (!platform.SupportsProgramSettings &&
                        _service.GetSettings() is var currentSettings && ChangesProgramSettings(update.Settings, currentSettings))
                    {
                        await Reply(client, message, MessageTypes.SettingsResult,
                            new SettingsResult { Error = ProgramSettingsUnavailable, Settings = currentSettings }).ConfigureAwait(false);
                        break;
                    }

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
                    if (!platform.SupportsOpenUi)
                    {
                        await Reply(client, message, MessageTypes.HostActionResult,
                            new HostActionResult { Ok = false, Message = "Opening the desktop UI is not available here." }).ConfigureAwait(false);
                        break;
                    }

                    // UI.B12: a Host that is exiting opens no window and says so, so a starting copy that asked does not
                    // take it for a running one, whatever its client.hello asked for.
                    if (Interlocked.CompareExchange(ref _exiting, 0, 0) == 1)
                    {
                        await client.SendDirectAsync(IpcMessage.Create(MessageTypes.HostExiting, new { }), cancellationToken).ConfigureAwait(false);
                        break;
                    }

                    var open = message.GetPayload<HostActionRequest>();
                    _platform.OpenUi(string.IsNullOrWhiteSpace(open.Action) ? null : open.Action);
                    await Reply(client, message, MessageTypes.HostActionResult,
                        new HostActionResult { Ok = true, Message = "Opening the desktop UI." }).ConfigureAwait(false);
                    break;

                case MessageTypes.HostPickFolder:
                    if (!platform.SupportsFolderPicker)
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
                    if (!platform.SupportsShutdown)
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

    /// <summary>Off the dispatcher's thread: a folder on a share that doesn't answer can take a while to list.</summary>
    private static async Task<FolderBrowseResult> BrowseFoldersAsync(SessionRights rights, IpcMessage message, CancellationToken cancellationToken)
    {
        if (FoldersOf(rights) is not { } folders)
            return new FolderBrowseResult { Error = "Folder browsing is not available here." };
        if (message.Type == MessageTypes.FoldersCreate)
        {
            var create = message.GetPayload<FolderCreateRequest>();
            return await Task.Run(() => folders.Create(create.Parent, create.Name), cancellationToken).ConfigureAwait(false);
        }

        var browse = message.Payload is null ? new FolderBrowseRequest() : message.GetPayload<FolderBrowseRequest>();
        return await Task.Run(() => folders.Browse(browse.Path), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether <paramref name="requested"/> changes a setting that makes the Host start a program or trust an installer.
    /// A client that sends back the settings it was given, with other changes, is not refused for them.
    /// </summary>
    private static bool ChangesProgramSettings(SettingsDto? requested, SettingsDto current)
        => requested is not null && (
            !SameText(requested.PostDownloadCommand, current.PostDownloadCommand) ||
            !SameText(requested.FfmpegPath, current.FfmpegPath) ||
            !SameText(requested.UpdateManifestUrl, current.UpdateManifestUrl) ||
            !SameText(requested.UpdatePublicKey, current.UpdatePublicKey) ||
            requested.AllowSystemCompletionActions != current.AllowSystemCompletionActions);

    /// <summary>
    /// A completion program is a program the Host starts. No program ("") and the default one (null, the post-download
    /// command, which is guarded above) are always allowed, and so is sending back the one the download already has.
    /// </summary>
    private bool ChangesCompletionProgram(JobUpdateRequest request)
        => request.CompletionOptions?.ProgramPath is { } program && !string.IsNullOrWhiteSpace(program) &&
           !SameText(program, _service.GetJob(request.JobId)?.CompletionOptions.ProgramPath);

    /// <summary>
    /// Whether the request turns on something a finished download does on this machine: opening the file, a system
    /// action, forcing one. Turning them off, or sending back what the download already has, is no change.
    /// </summary>
    private bool RaisesCompletionActions(JobUpdateRequest request)
    {
        if (request.CompletionOptions is not { } requested) return false;
        var current = _service.GetJob(request.JobId)?.CompletionOptions ?? new JobCompletionOptionsDto();
        static bool None(string? action) => string.IsNullOrWhiteSpace(action) || string.Equals(action, "none", StringComparison.OrdinalIgnoreCase);
        return (requested.OpenFile && !current.OpenFile) ||
               (!None(requested.SystemAction) && !string.Equals(requested.SystemAction, current.SystemAction, StringComparison.OrdinalIgnoreCase)) ||
               (requested.ForceSystemAction && !current.ForceSystemAction);
    }

    /// <summary>A web session's save, held to <see cref="WebSettingsRules"/>. Null when it may go ahead.</summary>
    private SettingsResult? RefuseSettingsForCaller(SettingsUpdateRequest update)
    {
        var current = _service.GetSettings();
        if (update.Settings is null) return new SettingsResult { Error = "No settings were sent.", Settings = current };
        WebSettingsRules.KeepHostOwned(update.Settings, current);
        return WebSettingsRules.DescribeRefusal(update.Settings, current) is { } refusal
            ? new SettingsResult { Error = refusal, Settings = current }
            : null;
    }

    /// <summary>
    /// For a web session: extracting a download's or a package's archives, or giving a package its password (which
    /// starts its waiting sets), only where all their files are inside its folders (a path or set it names is held to
    /// them by the archive commands themselves). The extraction folder stays as it is, and extraction can't be made to
    /// delete archives or overwrite files: those apply to every archive the user downloads, wherever it is.
    /// </summary>
    private IpcMessage? RefuseArchiveFor(ArchiveIpcDispatcher archives, CallerScope caller, IpcMessage message)
    {
        switch (message.Type)
        {
            case ArchiveMessageTypes.Extract:
                var extract = message.GetPayload<ArchiveExtractRequest>();
                return HoldsAll(extract.JobIds, extract.PackageId, caller)
                    ? null
                    : IpcMessage.Create(ArchiveMessageTypes.ExtractResult, new ArchiveExtractResult { Error = CallerScope.OutsideJobRefused }, message.Id);
            case ArchiveMessageTypes.SetPassword:
                var password = message.GetPayload<ArchiveSetPasswordRequest>();
                return password.PackageId is not { } passwordPackage || HoldsAll(null, passwordPackage, caller)
                    ? null
                    : IpcMessage.Create(ArchiveMessageTypes.ActionResult, new ArchiveActionResult { Error = CallerScope.OutsideJobRefused }, message.Id);
            case ArchiveMessageTypes.SettingsSet:
                var settings = message.GetPayload<ArchiveSettingsUpdate>();
                var current = archives.Coordinator.GetSettings();
                var error = settings.Folder is not null && !SameText(settings.Folder, current.Settings.Folder) ? ExtractionFolderUnavailable
                    : (settings.DeleteArchives == true && !current.Settings.DeleteArchives) ||
                      (string.Equals(settings.ConflictPolicy?.Trim(), "overwrite", StringComparison.OrdinalIgnoreCase) &&
                       !string.Equals(current.Settings.ConflictPolicy, "overwrite", StringComparison.OrdinalIgnoreCase)) ? ExtractionRemovalUnavailable
                    : null;
                if (error is null) return null;
                current.Ok = false;
                current.Error = error;
                return IpcMessage.Create(ArchiveMessageTypes.SettingsResult, current, message.Id);
            default:
                return null;
        }
    }

    /// <summary>Whether every download named, and every one of the package, is inside the caller's folders. One that is gone counts as held.</summary>
    private bool HoldsAll(IEnumerable<Guid>? jobIds, Guid? packageId, CallerScope caller)
    {
        var jobs = (jobIds ?? []).ToList();
        if (packageId is { } package)
            jobs.AddRange(_service.GetDownloadGroups().FirstOrDefault(group => group.Id == package)?.JobIds ?? []);
        return !jobs.Distinct().Any(id => _service.HeldBy(id, caller) == false);
    }

    /// <summary>What a web session is told about the Host: not where it keeps its state, nor leftovers outside its folders.</summary>
    private static HostStatusDto ForSession(HostStatusDto status, SessionRights rights)
    {
        if (rights.Scope is not { } caller) return status;
        status.StateDirectory = string.Empty;
        status.OrphanPartFiles = status.OrphanPartFiles.Where(path => caller.Roots.Allowed(path) is not null).ToList();
        return status;
    }

    private static SnapshotDto ForSession(SnapshotDto snapshot, SessionRights rights)
    {
        snapshot.Status = ForSession(snapshot.Status, rights);
        if (rights.Scope is not null) snapshot.Settings = WebSettingsRules.ForWeb(snapshot.Settings);
        return snapshot;
    }

    // The settings store trims the command and the FFmpeg path and keeps an empty one as none, so " x" and "x", and
    // "" and null, are the same setting and sending one back is no change.
    private static bool SameText(string? left, string? right)
        => string.Equals(
            string.IsNullOrWhiteSpace(left) ? null : left.Trim(),
            string.IsNullOrWhiteSpace(right) ? null : right.Trim(),
            StringComparison.Ordinal);

    /// <param name="caller">
    /// Set for a web session: the extension id in the payload says nothing about who sent it there (the bridge's own
    /// sign-in decided that), so it is not checked and grants nothing; the download is held to the session's folders and
    /// addresses like any it adds.
    /// </param>
    private async Task HandleHandoffAsync(IClientSession client, IpcMessage message, CallerScope? caller, CancellationToken cancellationToken)
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

        if (caller is null && !BrowserBridge.IsTrustedExtensionId(handoff.ExtensionId))
        {
            await Reply(client, message, MessageTypes.HandoffAck, new HandoffAck
            {
                Accepted = false,
                Error = "The desktop app does not recognise this extension. Update the app.",
                ErrorCode = "untrusted_extension",
            }).ConfigureAwait(false);
            return;
        }

        var settings = _service.GetSettings();
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

        var result = await _service.AddJobAsync(request, cancellationToken, caller).ConfigureAwait(false);
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

    /// <summary>A web session's copy of the settings, in whichever reply carries them, leaves out what it isn't shown.</summary>
    private Task Reply<T>(IClientSession client, IpcMessage request, string type, T payload)
    {
        if (payload is SettingsResult { Settings: { } settings } result && RightsOf(client).Scope is not null)
            result.Settings = WebSettingsRules.ForWeb(settings);
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
        Interlocked.Exchange(ref _exiting, 1);
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
