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
/// answer, and the broadcasts (<c>job.event</c>, <c>update.progress</c>, <c>groups.changed</c>, <c>settings.changed</c>, <c>remote.changed</c>,
/// <c>archive.changed</c>, <c>host.exiting</c>). The desktop Host's named pipe is one transport; a server's
/// WebSocket is another. What the machine itself can do comes from <see cref="IHostPlatform"/>; what one client may do
/// is its session's <see cref="SessionRights"/>, given when it opens: the platform's own, or a web session's
/// (<see cref="WebSessions"/>) when its <c>client.hello</c> asked for that role.
/// </summary>
public sealed partial class HostCommandDispatcher
{
    private const string UpdatesUnavailable = "Updates are not available here.";
    private const string ProgramSettingsUnavailable =
        "The post-download command, update source and system completion actions can't be changed from here.";
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
    private int _updateInstallWindowOpened;
    private int _exiting;
    private List<DownloadGroupDto>? _lastGroups;
    private int _groupsBroadcastPending;
    private int _settingsBroadcastPending;

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
        Torrents = _service.TorrentsAvailable,
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
        // archive.* is answered (extraction started) and an extractor is there (7-Zip found), as of the hello. A 7-Zip
        // installed later shows on the next connection, or at once in archive.settings.get's tool.
        if (rights.Archives is { } archives && archives.Coordinator.CanExtract) capabilities.Add("archives");
        // A .torrent file or a container can be uploaded (POST /api/upload/...) and then named by its reference.
        if (rights.Remote is { AcceptsUploads: true }) capabilities.Add("uploads");
        // T1: torrent.* is answered, whether or not torrents are turned on in Options (the commands then say so).
        if (rights.Torrents) capabilities.Add("torrents");
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
        _service.LatestVersionChanged += OnLatestVersionChanged;
        _service.SettingsChanged += OnSettingsChanged;
        if (_remote is not null) _remote.Changed += OnRemoteChanged;
    }

    /// <summary>Ends the broadcasts and a waiting package announcement. The transports close their own sessions.</summary>
    public void Stop()
    {
        _service.JobChanged -= OnJobChanged;
        _service.UpdateProgressChanged -= OnUpdateProgressChanged;
        _service.LatestVersionChanged -= OnLatestVersionChanged;
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
        // UI.R41: the native host names its browser; only a client at this PC may say an extension came.
        // A browser seen for the first time is pushed to the sessions at this PC, so an open window drops its hint now
        // rather than at its next snapshot; web sessions never get it (forScope gives them nothing).
        if (options?.Browser is { } browser && rights.Role == ClientRoles.Desktop && rights.Scope is null &&
            _service.ExtensionSightings is { } sightings && sightings.Note(browser))
            Broadcast(IpcMessage.Create(MessageTypes.ExtensionsSeen, new ExtensionsSeenDto { Seen = sightings.Snapshot() }), _ => null);
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
        // T3: a client that watched torrents watches no longer.
        _service.ForgetTorrentWatcher(client.Id);
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

            if (rights.Torrents && TorrentMessageTypes.IsRequest(message.Type))
            {
                await AnswerTorrentAsync(client, message, caller, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (WebUiMessageTypes.IsRequest(message.Type))
            {
                await AnswerWebUiAsync(client, rights, message, cancellationToken).ConfigureAwait(false);
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

                case MessageTypes.ChangelogGet:
                    // UPD.N1: this build's release notes; the same for every session, with nothing of the machine's in them.
                    await Reply(client, message, MessageTypes.ChangelogResult,
                        new ChangelogDto { Versions = Changelog.Embedded.ToList() }).ConfigureAwait(false);
                    break;

                case MessageTypes.StatisticsGet:
                    await Reply(client, message, MessageTypes.StatisticsResult, _service.GetStatistics()).ConfigureAwait(false);
                    break;

                case MessageTypes.SpeedHistoryGet:
                    var historyFor = message.Payload is { ValueKind: System.Text.Json.JsonValueKind.Object } ? message.GetPayload<SpeedHistoryRequest>()?.JobId : null;
                    await Reply(client, message, MessageTypes.SpeedHistoryResult,
                        historyFor is { } historyJob ? _service.GetSpeedHistory(historyJob) : _service.GetSpeedHistory()).ConfigureAwait(false);
                    break;

                case MessageTypes.JobsSnapshot:
                case MessageTypes.JobsQuery:
                    // Audit 3 N5: only the newest jobs asked for are read at all. A web session sees every job (those
                    // outside its folders without their addresses or paths), so its folders don't change which are newest.
                    await Reply(client, message, MessageTypes.JobsSnapshot,
                            LimitJobs(ForSession(_service.GetSnapshot(RequestedLimit(message)), rights), message))
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
                    if (caller is not null && _service.HeldBy(moveToQueue.JobId, caller) == false)
                    {
                        await Reply(client, message, MessageTypes.JobActionResult,
                            new JobActionResponse { Error = CallerScope.OutsideJobRefused, Job = _service.GetJob(moveToQueue.JobId) }).ConfigureAwait(false);
                        break;
                    }

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
                        caller is not null && _service.HeldBy(connectionsRequest.JobId, caller) == false
                            ? Array.Empty<ConnectionTelemetryDto>()
                            : _service.GetConnections(connectionsRequest.JobId)).ConfigureAwait(false);
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

                case MessageTypes.JobReveal:
                    await Reply(client, message, MessageTypes.HostActionResult,
                        await RevealAsync(message.GetPayload<JobActionRequest>().JobId, platform, caller, cancellationToken).ConfigureAwait(false))
                        .ConfigureAwait(false);
                    break;

                case MessageTypes.JobRedownload:
                    var redownload = message.GetPayload<JobActionRequest>();
                    await Reply(client, message, MessageTypes.JobActionResult,
                        await _service.RedownloadAsync(redownload.JobId, cancellationToken, caller).ConfigureAwait(false))
                        .ConfigureAwait(false);
                    break;

                case MessageTypes.JobSegments:
                    var segmentsRequest = message.GetPayload<JobActionRequest>();
                    await Reply(client, message, MessageTypes.JobSegmentsResult,
                        caller is not null && _service.HeldBy(segmentsRequest.JobId, caller) == false
                            ? Array.Empty<SegmentDto>()
                            : await _service.GetSegmentsAsync(segmentsRequest.JobId, cancellationToken)
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
                        new HostActionResult { Ok = true, Message = "All downloads stopped." }).ConfigureAwait(false);
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
                            // EXT.R10: only on this user's own machine (the folder dialog's Host), never for a scoped
                            // caller or a server, which answers folders.browse within its roots instead.
                            ExistingCandidates = caller is null && platform.SupportsFolderPicker
                                ? ExistingFolders(message)
                                : null,
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
                        await _service.UpdateSettingsAsync(update.Settings, cancellationToken, caller).ConfigureAwait(false))
                        .ConfigureAwait(false);
                    break;

                case MessageTypes.GroupAction:
                    var groupAction = message.GetPayload<GroupActionRequest>();
                    if (caller is not null && RefuseGroupActionFor(groupAction, caller) is { } groupRefused)
                    {
                        await Reply(client, message, MessageTypes.GroupActionResult, groupRefused).ConfigureAwait(false);
                        break;
                    }

                    await Reply(client, message, MessageTypes.GroupActionResult,
                        await _service.GroupActionAsync(groupAction, cancellationToken).ConfigureAwait(false))
                        .ConfigureAwait(false);
                    break;

                case MessageTypes.QueueAction:
                    var queueAction = message.GetPayload<QueueActionRequest>();
                    if (caller is not null && RefuseQueueActionFor(queueAction, caller) is { } queueRefused)
                    {
                        await Reply(client, message, MessageTypes.SettingsResult, queueRefused).ConfigureAwait(false);
                        break;
                    }

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
                    // EXT.R12: "Show in app" from the extension opens the main window at one download. Only a download on
                    // this list; its id is all that reaches the window's command line.
                    if (open.JobId is { } showJob && showJob != Guid.Empty)
                    {
                        if (_service.GetJob(showJob) is null)
                        {
                            await Reply(client, message, MessageTypes.HostActionResult,
                                new HostActionResult { Ok = false, Message = "That download is no longer in the app's list." }).ConfigureAwait(false);
                            break;
                        }

                        _platform.ShowJob(showJob);
                        await Reply(client, message, MessageTypes.HostActionResult,
                            new HostActionResult { Ok = true, Message = "Showing the download in the app." }).ConfigureAwait(false);
                        break;
                    }

                    // Only a page the window knows: the text goes on its command line (report 3, L2).
                    _platform.OpenUi(ClientPage(open.Action));
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
                    RememberPickedFolder(pickedFolder);
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
            // L16: an exception's words can hold local paths and the Windows user name; another device gets its kind only.
            await Reply(client, message, MessageTypes.HostActionResult,
                new HostActionResult { Ok = false, Message = caller is null ? reason : $"That could not be done ({ex.GetType().Name}). The app's log has the details." })
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// EXT.R10: which of a few folders the extension names exist, and nothing more: no names, sizes or contents. Only
    /// a local, fully qualified path is looked at. A network or device path (<c>\\server\share</c>, <c>\\?\</c>) or a
    /// drive letter mapped to a share is never touched, so a request can't make this machine reach out to another one.
    /// Null when none were asked about.
    /// </summary>
    public static List<string>? ExistingFolders(IpcMessage message)
    {
        DownloadFoldersRequest? request;
        try { request = message.Payload is { ValueKind: System.Text.Json.JsonValueKind.Object } ? message.GetPayload<DownloadFoldersRequest>() : null; }
        catch (Exception error) when (error is System.Text.Json.JsonException or InvalidDataException) { return null; }
        if (request?.Candidates is not { Count: > 0 } candidates) return null;
        var existing = new List<string>();
        foreach (var candidate in candidates.Take(DownloadFoldersRequest.MaxCandidates))
        {
            if (IsLocalFolderPath(candidate) && Directory.Exists(candidate) && !existing.Contains(candidate, StringComparer.Ordinal))
            {
                existing.Add(candidate);
            }
        }
        return existing;
    }

    private static bool IsLocalFolderPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && path.Length <= 4096 && path.IndexOf('\0') < 0 &&
        !path.StartsWith(@"\\", StringComparison.Ordinal) && !path.StartsWith("//", StringComparison.Ordinal) &&
        Path.IsPathFullyQualified(path) && !OnNetworkDrive(path);

    /// <summary>A drive letter mapped to a share (or a network mount) is another machine too; asking its type is local.</summary>
    public static bool OnNetworkDrive(string path, Func<string, DriveType>? driveType = null)
    {
        if (Path.GetPathRoot(path) is not { Length: > 0 } root) return true;
        try { return (driveType ?? (drive => new DriveInfo(drive).DriveType))(root) is DriveType.Network or DriveType.NoRootDirectory; }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException) { return true; }
    }

    private const string RevealUnavailable = "Showing a file in its folder is not available here.";

    /// <summary>How long <c>job.reveal</c> waits for a folder to answer (a share that is gone can hang a while).</summary>
    private static readonly TimeSpan RevealTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// EXT.R11: <c>job.reveal</c>. Only one download's own file or folder, never a path a client names: the file selected
    /// when it is there, else its folder, else a short refusal. The disk is asked off the dispatcher's thread, with a
    /// time limit, so a share that doesn't answer doesn't hold up the connection's other commands.
    /// </summary>
    private async Task<HostActionResult> RevealAsync(Guid jobId, PlatformCapabilities platform, CallerScope? caller, CancellationToken cancellationToken)
    {
        if (!platform.SupportsReveal || caller is not null) return new HostActionResult { Ok = false, Message = RevealUnavailable };
        if (_service.GetJob(jobId) is not { } job) return new HostActionResult { Ok = false, Message = "Job not found." };
        var path = job.FinalFilePath;
        if (string.IsNullOrWhiteSpace(path)) return new HostActionResult { Ok = false, Message = "This download has no file yet." };
        var reveal = Task.Run(() =>
        {
            if (File.Exists(path))
            {
                _platform.RevealFile(path);
                return new HostActionResult { Ok = true, Message = "Showing the file in its folder." };
            }

            var folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
            {
                _platform.RevealFile(path);
                return new HostActionResult { Ok = true, Message = "The file is gone, so its folder was opened." };
            }

            return new HostActionResult { Ok = false, Message = "The file and its folder are gone." };
        }, cancellationToken);
        try
        {
            return await reveal.WaitAsync(RevealTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The check goes on by itself; a late answer still opens the folder, which is what was asked.
            _ = reveal.ContinueWith(static task => _ = task.Exception, TaskScheduler.Default);
            return new HostActionResult { Ok = false, Message = "The download's folder didn't answer in time." };
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
        return await Task.Run(() => folders.Browse(browse.Path, browse.Files), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether <paramref name="requested"/> changes a setting that makes the Host start a program or trust an installer.
    /// A client that sends back the settings it was given, with other changes, is not refused for them.
    /// </summary>
    private static bool ChangesProgramSettings(SettingsDto? requested, SettingsDto current)
        => requested is not null && (
            !SameText(requested.PostDownloadCommand, current.PostDownloadCommand) ||
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

    /// <summary>
    /// What a web session is told about the Host: not where it keeps its state, nor leftovers outside its folders, nor which
    /// browsers this PC uses (review of 0.7.2, security-3; the push of it is withheld from web sessions too).
    /// </summary>
    private static HostStatusDto ForSession(HostStatusDto status, SessionRights rights)
    {
        if (rights.Scope is not { } caller) return status;
        status.StateDirectory = string.Empty;
        status.ExtensionsSeen = null;
        status.OrphanPartFiles = status.OrphanPartFiles.Where(path => caller.Roots.Allowed(path) is not null).ToList();
        return status;
    }

    /// <summary>
    /// Audit 3 M3: <c>jobs.query</c> with a limit lists that many jobs, newest first (the list's own order breaks
    /// ties). The extension asks for 20, so a long list can't make a frame over the browsers' 1 MB limit.
    /// </summary>
    public static SnapshotDto LimitJobs(SnapshotDto snapshot, IpcMessage message)
    {
        if (RequestedLimit(message) is not int limit || limit >= snapshot.Jobs.Count) return snapshot;
        snapshot.Jobs = snapshot.Jobs
            .Select((job, index) => (job, index))
            .OrderByDescending(entry => entry.job.CreatedAt)
            .ThenBy(entry => entry.index)
            .Take(limit)
            .Select(entry => entry.job)
            .ToList();
        return snapshot;
    }

    /// <summary>The limit a <c>jobs.query</c> asks for (above 0), or null for every job.</summary>
    public static int? RequestedLimit(IpcMessage message)
    {
        if (message.Type != MessageTypes.JobsQuery || message.Payload is not { ValueKind: System.Text.Json.JsonValueKind.Object })
            return null;
        try
        {
            return message.GetPayload<JobsQueryRequest>().Limit is int limit && limit > 0 ? limit : null;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException)
        {
            return null;
        }
    }

    private SnapshotDto ForSession(SnapshotDto snapshot, SessionRights rights)
    {
        snapshot.Status = ForSession(snapshot.Status, rights);
        if (rights.Scope is not { } caller) return snapshot;
        snapshot.Settings = WebSettingsRules.ForWeb(snapshot.Settings);
        return JobsForScope(snapshot, caller);
    }

    // The settings store trims the command and keeps an empty one as none, so " x" and "x", and
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

        // Report 3, M1: on this user's own PC (the Host with a folder dialog) the browser may only save into the app's
        // folders or one the dialog returned; anything else goes to the default folder. A web session is held to its
        // folders by the add itself, and a server to its roots.
        var destination = handoff.DestinationDirectory;
        string? note = null;
        if (caller is null && RightsOf(client).Capabilities.SupportsFolderPicker && !HandoffFolderAllowed(destination))
        {
            _log.Warn("A browser download asked for a folder outside the app's folders; it was saved in the default folder.");
            destination = null;
            note = HandoffFolderReplaced;
        }

        var request = new AddJobRequest
        {
            Url = handoff.Url,
            FinalUrl = handoff.FinalUrl,
            DestinationDirectory = destination,
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
            ContentType = handoff.MimeType,
            PackageKey = handoff.PackageKey,
            PackageName = handoff.PackageName,
            QuietWindows = handoff.QuietWindows,
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
            Note = result.Accepted ? note : null,
        }).ConfigureAwait(false);
    }

    /// <summary>A web session's copy of the settings, in whichever reply carries them, leaves out what it isn't shown.</summary>
    private Task Reply<T>(IClientSession client, IpcMessage request, string type, T payload)
    {
        if (RightsOf(client).Scope is { } caller)
        {
            if (payload is SettingsResult { Settings: { } settings } result) result.Settings = WebSettingsRules.ForWeb(settings);
            ReplyForScope(payload, caller);
        }

        client.Enqueue(IpcMessage.Create(type, payload, request.Id));
        return Task.CompletedTask;
    }

    /// <param name="forScope">
    /// What a session with a <see cref="CallerScope"/> gets instead (M4): built once per scope, and null sends it nothing.
    /// Without it every session gets <paramref name="message"/>.
    /// </param>
    private void Broadcast(IpcMessage message, Func<CallerScope, IpcMessage?>? forScope = null)
    {
        Dictionary<CallerScope, IpcMessage?>? scoped = null;
        foreach (var client in _sessions.Values)
        {
            if (forScope is not null && RightsOf(client).Scope is { } scope)
            {
                scoped ??= [];
                if (!scoped.TryGetValue(scope, out var own)) scoped[scope] = own = forScope(scope);
                if (own is not null) client.Enqueue(own);
                continue;
            }

            client.Enqueue(message);
        }
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
        => Broadcast(IpcMessage.Create(ArchiveMessageTypes.Changed, change),
            scope => IpcMessage.Create(ArchiveMessageTypes.Changed, ArchiveForScope(change, scope)));

    private void OnUpdateProgressChanged(object? sender, UpdateProgressDto progress) => BroadcastUpdateProgress(progress);

    private void OnLatestVersionChanged(object? sender, string? version) => BroadcastLatestVersion(version);

    /// <summary>
    /// UI.R37: every session, a web one too, hears the newest version the update check found, which snapshots also carry
    /// (<see cref="HostStatusDto.LatestVersion"/>).
    /// </summary>
    public void BroadcastLatestVersion(string? version)
        => Broadcast(IpcMessage.Create(MessageTypes.UpdateLatest, new UpdateLatestDto { LatestVersion = version }));

    /// <summary>
    /// An update's download opens its progress window once, where there is a window to open, and so does the start of
    /// its install: that window is what stays on screen while the install closes the main window, and the main window's
    /// update dialog keeps it shut while it shows the download itself (UI.R40). One already open is only shown again. A
    /// failure, a postponed install or "up to date" lets the next download and install open it again.
    /// </summary>
    public void BroadcastUpdateProgress(UpdateProgressDto progress)
    {
        if (progress.Stage == "downloading" && _platform.Capabilities.SupportsOpenUi &&
            Interlocked.Exchange(ref _updateWindowOpened, 1) == 0)
            _platform.OpenUi("update-progress");
        if (progress.Stage is "staging" or "installing" && _platform.Capabilities.SupportsOpenUi &&
            Interlocked.Exchange(ref _updateInstallWindowOpened, 1) == 0)
            _platform.OpenUi("update-progress");
        if (progress.Stage is "failed" or "postponed" or "upToDate")
        {
            Interlocked.Exchange(ref _updateWindowOpened, 0);
            Interlocked.Exchange(ref _updateInstallWindowOpened, 0);
        }
        Broadcast(IpcMessage.Create(MessageTypes.UpdateProgress, progress));
    }

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        // The app's folders may have changed, and with them which downloads a web session holds.
        ForgetHeldPaths();
        ScheduleGroupsBroadcast();
        ScheduleSettingsBroadcast();
    }

    /// <summary>
    /// Audit 4 H1: <c>settings.changed</c> carries the whole current settings after any change, so a client's copy
    /// (an open Options page, a web tab) follows changes made elsewhere instead of saving an old one back. A web session
    /// gets them as it is shown them (<see cref="WebSettingsRules.ForWeb"/>). A burst shares one message.
    /// </summary>
    public void ScheduleSettingsBroadcast()
    {
        if (Interlocked.Exchange(ref _settingsBroadcastPending, 1) == 0) _ = BroadcastSettingsAsync();
    }

    private async Task BroadcastSettingsAsync()
    {
        try
        {
            await Task.Delay(GroupsBroadcastDelay, _stopping.Token).ConfigureAwait(false);
            Interlocked.Exchange(ref _settingsBroadcastPending, 0);
            var settings = _service.GetSettings();
            IpcMessage? full = null;
            IpcMessage? web = null;
            foreach (var client in _sessions.Values)
            {
                client.Enqueue(RightsOf(client).Scope is null
                    ? full ??= IpcMessage.Create(MessageTypes.SettingsChanged, settings)
                    : web ??= IpcMessage.Create(MessageTypes.SettingsChanged, WebSettingsRules.ForWeb(settings)));
            }
        }
        catch (OperationCanceledException) { /* stopping */ }
        catch (Exception ex) { _log.Warn($"Could not announce a settings change: {ex.GetType().Name}"); }
    }

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
        }), scope => IpcMessage.Create(MessageTypes.JobEvent, new JobEventDto
        {
            Job = ForScope(e.Job, scope),
            EventType = e.ChangeType,
        }));
}
