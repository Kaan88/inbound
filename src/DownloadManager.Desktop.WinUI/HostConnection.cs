using System.Collections.Concurrent;
using System.Diagnostics;
using DownloadManager.Contracts;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// The window's connection to one Host: this PC's over the named pipe, or another server's over its WebSocket
/// (<see cref="ServerTarget"/>, design D9 section 5). Replies come back to the call that asked; events go to the handlers.
/// <para>
/// To a server: reconnecting waits 2 s, then twice as long each time, up to about a minute
/// (<see cref="ReconnectBackoff"/>). A token the server refuses is first read again from the window's server list
/// (another window may have paired again meanwhile); if that is the same token, or an http address may not be used,
/// nothing more is tried until <see cref="Retarget"/>: showing or hiding the window doesn't try again. While
/// <see cref="SetEventsWanted"/> is false (the window is hidden) it asks the server for no snapshot and no events. Its
/// <c>host.exiting</c> means "disconnected": only this PC's Host quits the app, and only this PC's update progress is
/// shown.
/// </para>
/// </summary>
public sealed partial class HostConnection : IAsyncDisposable
{
    // Audit M11: with no Host running the window tried the pipe every 2 s for ever. Now 2, 4, 8, then every 10 s; a
    // connection that works, a retarget or showing the window starts from 2 s again.
    private static readonly TimeSpan LocalRetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan LocalRetryMaximum = TimeSpan.FromSeconds(10);
    private readonly ReconnectBackoff _localBackoff = new(LocalRetryDelay, LocalRetryMaximum);

    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<IpcMessage>> _pending = new();
    private readonly ReconnectBackoff _backoff;
    private readonly Func<ServerTarget, ClientHelloDto?, TimeSpan, CancellationToken, Task<IIpcConnection>> _connect;
    private readonly Func<string, ServerTarget?>? _reload;
    private readonly Lock _sync = new();
    private IIpcConnection? _client;
    private Task? _loop;
    private CancellationTokenSource? _session;
    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile ServerTarget _target;
    private volatile bool _eventsWanted = true;
    private volatile HostHelloDto? _hello;

    public HostConnection(ServerTarget? target = null)
        : this(target, connect: null, backoff: null, reload: id => ServerStore.ForThisUser().Target(id))
    {
    }

    /// <param name="connect">For tests: opens the connection a target names (<see cref="ServerConnector.ConnectAsync"/>).</param>
    /// <param name="backoff">For tests: the waits between a server's tries.</param>
    /// <param name="reload">A saved server's target as the window's server list has it now, by id; null when it isn't saved.</param>
    internal HostConnection(ServerTarget? target,
        Func<ServerTarget, ClientHelloDto?, TimeSpan, CancellationToken, Task<IIpcConnection>>? connect,
        ReconnectBackoff? backoff, Func<string, ServerTarget?>? reload)
    {
        _target = target ?? ServerTarget.Local;
        _connect = connect ?? ((server, hello, timeout, token) => ServerConnector.ConnectAsync(server, hello, timeout, token));
        _backoff = backoff ?? new ReconnectBackoff();
        _reload = reload;
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

    // A refused token or an http address that may not be used: no more tries until Retarget.
    private volatile bool _stopped;

    /// <summary>A refused token or an http address that may not be used: nothing more is tried until <see cref="Retarget"/>.</summary>
    public bool StoppedTrying => _stopped;

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
            _stopped = false;
            _hello = null;
            _backoff.Reset();
            _localBackoff.Reset();
            CancelSession();
            _wake.TrySetResult();
        }
    }

    /// <summary>
    /// False while the window is hidden: a server is then asked for no events (and no snapshot), so it sends nothing
    /// while nobody looks. A connection that is open, or being opened, starts again with the new choice; while waiting
    /// for the next try, showing the window tries at once and hiding it changes only what the next try asks for. After
    /// a refused token nothing is tried. This PC's pipe is left as it is.
    /// </summary>
    public void SetEventsWanted(bool wanted)
    {
        lock (_sync)
        {
            if (_eventsWanted == wanted) return;
            _eventsWanted = wanted;
            if (!IsRemote)
            {
                // This PC's pipe is left as it is; a window shown while waiting for the Host tries at once.
                if (wanted && _session is null)
                {
                    _localBackoff.Reset();
                    _wake.TrySetResult();
                }
                return;
            }
            if (_stopped) return;
            if (_session is not null) CancelSession();
            else if (wanted) _wake.TrySetResult();
        }
    }

    private void CancelSession()
    {
        try { _session?.Cancel(); }
        catch (ObjectDisposedException) { }
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

    /// <summary>F-12: one download's figures while it runs. An older Host answers with the total (no <c>jobId</c>).</summary>
    public Task<IpcMessage> GetSpeedHistoryAsync(Guid jobId, CancellationToken cancellationToken = default)
        => RequestAsync(MessageTypes.SpeedHistoryGet, new SpeedHistoryRequest { JobId = jobId }, TimeSpan.FromSeconds(8), cancellationToken);

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

    /// <summary>A torrent job's files (T1); a Host without torrents answers with an error.</summary>
    public async Task<TorrentFilesResult> GetTorrentFilesAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var reply = await RequestAsync(TorrentMessageTypes.Files, new TorrentFilesRequest { JobId = jobId }, TimeSpan.FromSeconds(15), cancellationToken)
            .ConfigureAwait(false);
        return reply.Type == TorrentMessageTypes.FilesResult
            ? reply.GetPayload<TorrentFilesResult>()
            : new TorrentFilesResult { Error = TorrentReplyError(reply) };
    }

    /// <summary>Which of a torrent job's files to download (T1).</summary>
    public async Task<TorrentActionResult> SetTorrentFilesAsync(Guid jobId, IEnumerable<int> indexes, CancellationToken cancellationToken = default)
    {
        var reply = await RequestAsync(TorrentMessageTypes.SetFiles, new TorrentSetFilesRequest { JobId = jobId, FileIndexes = indexes.ToList() },
            TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        return reply.Type == TorrentMessageTypes.ActionResult
            ? reply.GetPayload<TorrentActionResult>()
            : new TorrentActionResult { Error = TorrentReplyError(reply) };
    }

    /// <summary>T2: starts or stops seeding a finished torrent.</summary>
    public async Task<TorrentActionResult> SetTorrentSeedingAsync(Guid jobId, bool on, CancellationToken cancellationToken = default)
    {
        var reply = await RequestAsync(TorrentMessageTypes.Seed, new TorrentSeedRequest { JobId = jobId, On = on },
            TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        return reply.Type == TorrentMessageTypes.ActionResult
            ? reply.GetPayload<TorrentActionResult>()
            : new TorrentActionResult { Error = TorrentReplyError(reply) };
    }

    /// <summary>T2: a running torrent's peers. Null when the Host doesn't know the request (an older one).</summary>
    public Task<TorrentPeersResult?> GetTorrentPeersAsync(Guid jobId, CancellationToken cancellationToken = default)
        => TorrentQueryAsync(TorrentMessageTypes.Peers, TorrentMessageTypes.PeersResult, jobId,
            error => new TorrentPeersResult { Error = error }, cancellationToken);

    /// <summary>T2: a running torrent's trackers. Null when the Host doesn't know the request.</summary>
    public Task<TorrentTrackersResult?> GetTorrentTrackersAsync(Guid jobId, CancellationToken cancellationToken = default)
        => TorrentQueryAsync(TorrentMessageTypes.Trackers, TorrentMessageTypes.TrackersResult, jobId,
            error => new TorrentTrackersResult { Error = error }, cancellationToken);

    /// <summary>T2: the pieces a running torrent has. Null when the Host doesn't know the request.</summary>
    public Task<TorrentPiecesResult?> GetTorrentPiecesAsync(Guid jobId, CancellationToken cancellationToken = default)
        => TorrentQueryAsync(TorrentMessageTypes.Pieces, TorrentMessageTypes.PiecesResult, jobId,
            error => new TorrentPiecesResult { Error = error }, cancellationToken);

    private async Task<T?> TorrentQueryAsync<T>(string type, string resultType, Guid jobId, Func<string, T> failed, CancellationToken cancellationToken)
        where T : class
    {
        var reply = await RequestAsync(type, new TorrentJobRequest { JobId = jobId }, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        if (reply.Type == resultType) return reply.GetPayload<T>();
        return IsUnknownRequest(reply) ? null : failed(TorrentReplyError(reply));
    }

    // ---- T3: the torrent controls (options, recheck, announce, details, watch, priorities) ----

    private readonly Lock _probeSync = new();
    private HostHelloDto? _probedHello;
    private bool? _torrentControls;
    private Task<bool?>? _probe;

    /// <summary>
    /// T3: the connected Host answers the torrent controls; null until <see cref="ProbeTorrentControlsAsync"/> asked this
    /// connection's Host. Never true without <c>torrents</c> in its hello.
    /// </summary>
    public bool? TorrentControls
    {
        get
        {
            lock (_probeSync) return _probedHello is not null && ReferenceEquals(_probedHello, _hello) ? _torrentControls : null;
        }
    }

    /// <summary>
    /// Asks the connected Host once per connection whether it has the T3 torrent controls: <c>torrent.options</c> for no
    /// job, which a T3 Host answers with an options reply and an older one with "Unknown message type". Never throws.
    /// </summary>
    public Task<bool?> ProbeTorrentControlsAsync()
    {
        var hello = _hello;
        if (hello is null || _client is null) return Task.FromResult<bool?>(null);
        if (!hello.Capabilities.Contains("torrents", StringComparer.Ordinal)) return Task.FromResult<bool?>(false);
        lock (_probeSync)
        {
            // Asked once per connection, whatever the answer: a Host that didn't answer is asked again only after a reconnect.
            if (ReferenceEquals(_probedHello, hello)) return _probe ?? Task.FromResult(_torrentControls);
            _probedHello = hello;
            _torrentControls = null;
            return _probe = ProbeAsync(hello);
        }
    }

    private async Task<bool?> ProbeAsync(HostHelloDto hello)
    {
        bool? answer;
        try
        {
            var reply = await RequestAsync(TorrentMessageTypes.Options, new TorrentJobRequest { JobId = Guid.Empty }, TimeSpan.FromSeconds(8), CancellationToken.None)
                .ConfigureAwait(false);
            answer = TorrentControlRules.ProbeAnswer(reply.Type, reply.Type != TorrentMessageTypes.OptionsResult && IsUnknownRequest(reply));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            answer = null;
        }

        lock (_probeSync)
        {
            if (ReferenceEquals(_probedHello, hello))
            {
                _torrentControls = answer;
                _probe = null;
            }
        }

        return answer;
    }

    /// <summary>T3: a torrent's own options; null when the Host doesn't know the request.</summary>
    public Task<TorrentOptionsResult?> GetTorrentOptionsAsync(Guid jobId, CancellationToken cancellationToken = default)
        => TorrentQueryAsync(TorrentMessageTypes.Options, TorrentMessageTypes.OptionsResult, jobId,
            error => new TorrentOptionsResult { Error = error }, cancellationToken);

    /// <summary>T3: changes a torrent's own options.</summary>
    public async Task<TorrentOptionsResult> SetTorrentOptionsAsync(TorrentOptionsDto options, CancellationToken cancellationToken = default)
    {
        var reply = await RequestAsync(TorrentMessageTypes.SetOptions, options, TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        return reply.Type == TorrentMessageTypes.OptionsResult
            ? reply.GetPayload<TorrentOptionsResult>()
            : new TorrentOptionsResult { Error = IsUnknownRequest(reply) ? TorrentControlRules.OlderHost : TorrentReplyError(reply) };
    }

    /// <summary>T3: the Overview's facts; null when the Host doesn't know the request.</summary>
    public Task<TorrentDetailsResult?> GetTorrentDetailsAsync(Guid jobId, CancellationToken cancellationToken = default)
        => TorrentQueryAsync(TorrentMessageTypes.Details, TorrentMessageTypes.DetailsResult, jobId,
            error => new TorrentDetailsResult { Error = error }, cancellationToken);

    /// <summary>T3: Force recheck.</summary>
    public Task<TorrentActionResult> RecheckTorrentAsync(Guid jobId, CancellationToken cancellationToken = default)
        => TorrentActionAsync(TorrentMessageTypes.Recheck, new TorrentJobRequest { JobId = jobId }, cancellationToken);

    /// <summary>T3: Announce now.</summary>
    public Task<TorrentActionResult> AnnounceTorrentAsync(Guid jobId, CancellationToken cancellationToken = default)
        => TorrentActionAsync(TorrentMessageTypes.Announce, new TorrentJobRequest { JobId = jobId }, cancellationToken);

    /// <summary>T3: changes only these files' priorities (Skip leaves one out).</summary>
    public Task<TorrentActionResult> SetTorrentPrioritiesAsync(Guid jobId, IEnumerable<TorrentFilePriorityDto> priorities, CancellationToken cancellationToken = default)
        => TorrentActionAsync(TorrentMessageTypes.SetFiles, new TorrentSetFilesRequest { JobId = jobId, Priorities = priorities.ToList() }, cancellationToken);

    /// <summary>T3: the file choice that starts a waiting torrent, with the priorities chosen meanwhile.</summary>
    public Task<TorrentActionResult> SetTorrentFilesAsync(Guid jobId, IEnumerable<int> indexes, IReadOnlyList<TorrentFilePriorityDto>? priorities,
        CancellationToken cancellationToken = default)
        => priorities is not { Count: > 0 }
            ? SetTorrentFilesAsync(jobId, indexes, cancellationToken)
            : TorrentActionAsync(TorrentMessageTypes.SetFiles,
                new TorrentSetFilesRequest { JobId = jobId, FileIndexes = indexes.ToList(), Priorities = priorities.ToList() }, cancellationToken);

    /// <summary>T3: this client shows torrents now (<paramref name="on"/>), for <see cref="TorrentControlRules.LeaseSeconds"/>.
    /// False when the Host doesn't know the request; it never throws for a dropped connection.</summary>
    public async Task<bool> WatchTorrentsAsync(bool on, CancellationToken cancellationToken = default)
    {
        try
        {
            var reply = await RequestAsync(TorrentMessageTypes.Watch, new TorrentWatchRequest { On = on, LeaseSeconds = TorrentControlRules.LeaseSeconds },
                TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false);
            return reply.Type == TorrentMessageTypes.ActionResult || !IsUnknownRequest(reply);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException or OperationCanceledException or ObjectDisposedException)
        {
            return true;
        }
    }

    /// <summary>T3: the torrent engine's DHT nodes and listening; null when not answered.</summary>
    public async Task<TorrentNetworkResult?> GetTorrentNetworkAsync(CancellationToken cancellationToken = default)
    {
        var reply = await RequestAsync(TorrentMessageTypes.Network, new { }, TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false);
        return reply.Type == TorrentMessageTypes.NetworkResult ? reply.GetPayload<TorrentNetworkResult>() : null;
    }

    private async Task<TorrentActionResult> TorrentActionAsync<T>(string type, T payload, CancellationToken cancellationToken)
    {
        var reply = await RequestAsync(type, payload, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        if (reply.Type == TorrentMessageTypes.ActionResult) return reply.GetPayload<TorrentActionResult>();
        return new TorrentActionResult { Error = IsUnknownRequest(reply) && _hello?.Capabilities.Contains("torrents", StringComparer.Ordinal) == true
            ? TorrentControlRules.OlderHost
            : TorrentReplyError(reply) };
    }

    /// <summary>The Host answered that it doesn't know the request: an older Host, or one without torrents.</summary>
    internal static bool IsUnknownRequest(IpcMessage reply)
    {
        if (reply.Type != MessageTypes.HostActionResult) return true;
        try
        {
            return reply.GetPayload<HostActionResult>().Message?.Trim().StartsWith("Unknown message type", StringComparison.OrdinalIgnoreCase) == true;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>What Export list saves of each torrent job; null from a Host without torrents (its magnets are saved alone).
    /// Asked page by page, so a long list stays under the message size limit.</summary>
    public async Task<IReadOnlyDictionary<Guid, TorrentListEntry>?> GetTorrentListEntriesAsync(CancellationToken cancellationToken = default)
    {
        var all = new Dictionary<Guid, TorrentListEntry>();
        Guid? after = null;
        do
        {
            var reply = await RequestAsync(TorrentMessageTypes.ListEntries, new TorrentListEntriesRequest { After = after },
                TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            if (reply.Type != TorrentMessageTypes.ListEntriesResult) return all.Count > 0 ? all : null;
            var page = reply.GetPayload<TorrentListEntriesResult>();
            foreach (var (id, entry) in page.Entries) all[id] = entry;
            // A page that moves nothing on ends it, whatever it says.
            after = page.Next is { } next && next != after ? next : null;
        }
        while (after is not null);

        return all;
    }

    /// <summary>Adds an imported list's torrents, in requests that each fit one message; a Host without torrents skips
    /// them all and says why.</summary>
    public async Task<TorrentImportResult> ImportTorrentsAsync(IReadOnlyList<JobListEntry> entries, CancellationToken cancellationToken = default)
    {
        var total = new TorrentImportResult { Ok = true };
        var sent = 0;
        foreach (var chunk in TorrentListPaging.Chunk(entries))
        {
            var reply = await RequestAsync(TorrentMessageTypes.Import, new TorrentImportRequest { Entries = chunk },
                TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
            if (reply.Type != TorrentMessageTypes.ImportResult)
            {
                if (sent == 0) return new TorrentImportResult { Ok = true, Skipped = entries.Count, SkipReason = TorrentReplyError(reply) };
                total.Failed += entries.Count - sent;
                total.FirstError ??= TorrentReplyError(reply);
                break;
            }

            TorrentListPaging.AddTo(total, reply.GetPayload<TorrentImportResult>());
            sent += chunk.Count;
        }

        return total;
    }

    /// <summary>
    /// The reason in a reply that isn't the torrent answer. A Host without torrents (or an older one) answers
    /// "Unknown message type"; any other error reply carries the Host's own reason, which is shown as it is.
    /// </summary>
    internal static string TorrentReplyError(IpcMessage reply)
    {
        const string noTorrents = "This Host does not download torrents.";
        if (reply.Type != MessageTypes.HostActionResult) return noTorrents;
        string? message;
        try
        {
            message = reply.GetPayload<HostActionResult>().Message?.Trim();
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException or InvalidOperationException)
        {
            return "The Host could not do that.";
        }

        if (string.IsNullOrEmpty(message)) return "The Host could not do that.";
        return message.StartsWith("Unknown message type", StringComparison.OrdinalIgnoreCase) ? noTorrents : message;
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
            var retryNow = false;
            try
            {
                // A server is asked only for what the window shows: nothing while it is hidden.
                var hello = remote ? new ClientHelloDto { Snapshot = _eventsWanted, Events = _eventsWanted } : null;
                client = await _connect(target, hello, remote ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(2), session.Token)
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
                else _localBackoff.Reset();
                ConnectionChanged?.Invoke(true, null);

                if (snapshot?.Type == MessageTypes.JobsSnapshot)
                {
                    DispatchOne(snapshot.Type, () => SnapshotReceived?.Invoke(snapshot.GetPayload<SnapshotDto>()));
                }
                else if (snapshot is not null)
                {
                    DispatchOne(snapshot.Type, () => Dispatch(snapshot, snapshot: false));
                }

                while (!session.IsCancellationRequested)
                {
                    var message = await client.ReadAsync(session.Token).ConfigureAwait(false);
                    if (message is null)
                    {
                        break;
                    }

                    DispatchOne(message.Type, () => Dispatch(message, snapshot: false));
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
            catch (Exception ex) when (ex is RemotePairingEndedException && Reloaded(target) is { } paired)
            {
                // Paired again meanwhile (by the main window, while this one kept the old token): use the new one now.
                lock (_sync)
                {
                    if (_target == target) _target = paired;
                }

                retryNow = true;
            }
            catch (Exception ex) when (ex is RemotePairingEndedException or PlainHttpNotConfirmedException)
            {
                // Trying again can't help: the token is refused, or may not be sent. The user decides in File > Servers.
                stopTrying = true;
                lock (_sync)
                {
                    if (_target == target)
                    {
                        _stopped = true;
                        PairingEnded = ex is RemotePairingEndedException;
                    }
                }

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

            var askedFor = session.IsCancellationRequested || retryNow;
            session.Dispose();
            if (askedFor)
            {
                if (cancellationToken.IsCancellationRequested) break;
                continue;
            }

            try
            {
                // Stopped: only Retarget wakes it (SetEventsWanted leaves a stopped connection alone).
                var delay = stopTrying ? Timeout.InfiniteTimeSpan : remote ? _backoff.Next() : _localBackoff.Next();
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

    /// <summary>The saved server's target with a token other than the refused one, or null.</summary>
    private ServerTarget? Reloaded(ServerTarget refused)
    {
        if (_reload is null || refused.Id is not { } id) return null;
        try
        {
            return _reload(id) is { } now && now.Token != refused.Token ? now : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    /// <summary>
    /// Audit L8: one message this build can't read (a payload of a newer Host, a handler that throws) is skipped; it
    /// used to end the connection, and in a loop when it was the snapshot sent on every connect. The skip is written
    /// to <see cref="Trace"/> (the app's UI trace) and raised as <see cref="MessageSkipped"/>, so an empty list after an
    /// unreadable snapshot can be told apart from an empty Host.
    /// </summary>
    private void DispatchOne(string? type, Action dispatch)
    {
        try
        {
            dispatch();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var line = $"Skipped a Host message ({type ?? "no type"}): {ex.GetType().Name}: {ex.Message}";
            System.Diagnostics.Debug.WriteLine(line);
            try
            {
                Trace?.Invoke(line);
                MessageSkipped?.Invoke(line);
            }
            catch (Exception traceError) when (traceError is not OperationCanceledException)
            {
                System.Diagnostics.Debug.WriteLine($"Skip not traced: {traceError.Message}");
            }
        }
    }

    /// <summary>Where every connection writes a skipped message (audit L8); the app sets it to its UI trace.</summary>
    internal static Action<string>? Trace { get; set; }

    /// <summary>A message from the Host was skipped because this build could not read it; the words say which and why.</summary>
    public event Action<string>? MessageSkipped;

    private void Dispatch(IpcMessage message, bool snapshot)
    {
        if (message.Type == MessageTypes.HostExiting)
        {
            // Another server going away is a disconnection, which its closing socket reports; it never quits this app.
            if (RemoteDataRules.HostExitQuitsApp(IsRemote)) HostExiting?.Invoke();
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
        if (OperatingSystem.IsLinux())
        {
            EnsureStartedOnLinux();
            return;
        }

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

    /// <summary>
    /// Linux: the Host is running when its socket answers (it holds a lock file, not a named mutex). It is started apart
    /// from this process (setsid), so closing the window leaves it running, as on Windows.
    /// </summary>
    private static void EnsureStartedOnLinux()
    {
        if (HostPipeBusyException.PipeExists() && HostAnswers()) return;
        if (DateTimeOffset.UtcNow - _lastStartAttempt < TimeSpan.FromSeconds(5)) return;
        _lastStartAttempt = DateTimeOffset.UtcNow;
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "inbound-host"),
            // The AppImage keeps the window in desktop/ beside the Host.
            Path.Combine(AppContext.BaseDirectory, "..", "inbound-host"),
        };
        for (var i = 0; i < 7 && directory is not null; i++, directory = directory.Parent)
        {
            candidates.Add(Path.Combine(directory.FullName, "DownloadManager.Host.Linux", "bin", "Debug", "net10.0", "inbound-host"));
            candidates.Add(Path.Combine(directory.FullName, "DownloadManager.Host.Linux", "bin", "Release", "net10.0", "inbound-host"));
        }

        var fromEnv = Environment.GetEnvironmentVariable("DOWNLOADMANAGER_HOST");
        if (!string.IsNullOrWhiteSpace(fromEnv)) candidates.Insert(0, fromEnv);
        var executable = candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
        if (executable is null) return;
        try
        {
            var start = new ProcessStartInfo("setsid") { UseShellExecute = false };
            start.ArgumentList.Add("-f");
            start.ArgumentList.Add(executable);
            start.ArgumentList.Add("--background");
            Process.Start(start)?.Dispose();
        }
        catch (Exception)
        {
            // The connection loop will surface the failure.
        }
    }

    /// <summary>A socket file can outlive its Host; only an answer says one runs.</summary>
    private static bool HostAnswers()
    {
        try
        {
            using var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
            socket.Connect(new System.Net.Sockets.UnixDomainSocketEndPoint(UnixSockets.PathFor(PipeNames.HostPipe())));
            return true;
        }
        catch (System.Net.Sockets.SocketException)
        {
            return false;
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
