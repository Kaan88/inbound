using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;
using DownloadManager.Contracts;

namespace DownloadManager.Desktop.WinUI;

/// <summary>The little this client needs from a connection, so it can be tested without a real pipe.</summary>
internal interface IRemotePipe : IAsyncDisposable
{
    Task SendAsync(IpcMessage message, CancellationToken cancellationToken);
    Task<IpcMessage?> ReadAsync(CancellationToken cancellationToken);

    /// <summary>Whether <c>jobs.snapshot</c> follows the hello. A server's event socket asks for none.</summary>
    bool SnapshotFollowsHello => true;
}

/// <summary>
/// Talks to the Host about remote providers and archives, on connections of its own, so nothing here
/// can hold up <see cref="HostConnection"/>.
/// <list type="bullet">
/// <item>One long-lived connection carries the events and the calls that answer from local state. It
/// comes back by itself if the Host restarts, and never starts the Host: an intentional tray Exit stays.</item>
/// <item>Each call that may wait on the network gets a connection for itself. The Host runs one command
/// per connection at a time, so sharing one would have a slow list hold up a key check, and closing
/// the connection is how a call gets cancelled.</item>
/// </list>
/// Every call answers with its reply type. A failure of any kind, including a lost connection, comes
/// back as <c>Ok = false</c> with words for the user; only the caller's own cancellation throws. A call
/// that fails is never sent again by itself, because an add or a delete may have reached the Host.
/// Nothing here writes a log, and no exception's words reach a message: a key or password passes through.
/// </summary>
internal sealed partial class RemoteClient : IAsyncDisposable
{
    private const int MostSlowCalls = 3;

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan EventConnectTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DefaultQuickTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultSlowTimeout = TimeSpan.FromSeconds(90);

    /// <summary>Entries are saved one at a time, up to 5000 of them, and running out of time here would cancel the rest.</summary>
    internal static readonly TimeSpan ContainerAddTimeout = TimeSpan.FromMinutes(15);

    /// <summary>JDownloader's user gets five minutes to answer, and this has to outlast that, or the Host's own answer is never heard.</summary>
    internal static readonly TimeSpan SendToJdTimeout = TimeSpan.FromMinutes(5.5);

    private readonly Action<Action> _post;
    private Func<TimeSpan, CancellationToken, Task<IRemotePipe>> _connect;

    /// <summary>For the calls that get a connection each: the same as <see cref="_connect"/> on this PC, one POST each to a server.</summary>
    private Func<TimeSpan, CancellationToken, Task<IRemotePipe>> _connectCall;
    private readonly TimeSpan _reconnectDelay;
    private readonly TimeSpan _quickTimeout;
    private readonly TimeSpan _slowTimeout;
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<IpcMessage>> _pending = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    // The Host has eight pipe instances for every client on the machine, so these wait their turn.
    private readonly SemaphoreSlim _slowCalls = new(MostSlowCalls, MostSlowCalls);
    private TaskCompletionSource _firstRead = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // One per Host this client goes to (RetargetAsync): ending it ends the event connection and the calls to that Host.
    private CancellationTokenSource _round;
    private bool _eventsWanted = true;
    private bool _remoteTarget;

    // A server's event socket: tries wait longer each time (design D9, section 5), and a refused token, or a plain-http
    // address the user hasn't allowed, stops them until the next RetargetAsync. _parked holds what to say meanwhile.
    private readonly ReconnectBackoff _remoteBackoff;
    private string? _parked;

    // Audit M11: this PC's pipe was tried every 2 s for ever while no Host ran. Now the wait doubles from the first one
    // (2 s) up to five times it (10 s), and starts again once a connection works.
    private readonly ReconnectBackoff _localBackoff;

    // A server's .torrent uploads: the server the files go to, and the reference each file got there, kept for a short
    // while and only for the same file (path, size and time written), so the check before an add and the add share one.
    private ServerTarget? _uploadTarget;
    private readonly ConcurrentDictionary<string, (string Reference, DateTimeOffset At)> _uploads = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan UploadKeptFor = TimeSpan.FromMinutes(30);
    private readonly Lock _sync = new();

    private TaskCompletionSource<IRemotePipe> _control = NewControlSource();
    private Task? _loop;
    private volatile bool _pipeBusy;

    /// <param name="post">Runs an action on the UI thread. State only ever changes through this.</param>
    public RemoteClient(
        Action<Action> post,
        Func<TimeSpan, CancellationToken, Task<IRemotePipe>>? connect = null,
        TimeSpan? reconnectDelay = null,
        TimeSpan? quickTimeout = null,
        TimeSpan? slowTimeout = null,
        ReconnectBackoff? remoteBackoff = null)
    {
        _post = post;
        _connect = connect ?? ConnectToHostAsync;
        _connectCall = _connect;
        _round = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
        _reconnectDelay = reconnectDelay ?? TimeSpan.FromSeconds(2);
        _quickTimeout = quickTimeout ?? DefaultQuickTimeout;
        _slowTimeout = slowTimeout ?? DefaultSlowTimeout;
        _remoteBackoff = remoteBackoff ?? new ReconnectBackoff();
        _localBackoff = new ReconnectBackoff(_reconnectDelay, _reconnectDelay * 5);
    }

    public RemoteState State { get; } = new();

    /// <summary>Starts the event connection. A call does this by itself if it comes first.</summary>
    public void Start()
    {
        lock (_sync)
        {
            if (_round.IsCancellationRequested || _parked is not null) return;
            var round = _round.Token;
            _loop ??= Task.Run(() => RunAsync(round));
        }
    }

    private CancellationToken RoundToken
    {
        get
        {
            lock (_sync) return _round.Token;
        }
    }

    /// <summary>
    /// Goes to another Host (design D9, section 5): the event connection closes, calls waiting on the old one fail, what
    /// is known is cleared, and the new one is read from the start. A server gets a WebSocket for events and one
    /// <c>POST /api/ipc</c> for each call that would have had a connection of its own.
    /// </summary>
    public Task RetargetAsync(ServerTarget target)
    {
        Func<TimeSpan, CancellationToken, Task<IRemotePipe>> events = target.IsLocal
            ? ConnectToHostAsync
            : (timeout, token) => ConnectToServerAsync(target, timeout, token);
        Func<TimeSpan, CancellationToken, Task<IRemotePipe>> calls = target.IsLocal
            ? ConnectToHostAsync
            : (_, _) => Task.FromResult<IRemotePipe>(new OneShotPipe(target));
        return RetargetAsync(events, calls, !target.IsLocal, target.IsLocal ? null : target);
    }

    internal async Task RetargetAsync(Func<TimeSpan, CancellationToken, Task<IRemotePipe>> events,
        Func<TimeSpan, CancellationToken, Task<IRemotePipe>> calls, bool remote, ServerTarget? uploadTarget = null)
    {
        await EndRoundAsync().ConfigureAwait(false);
        bool start;
        lock (_sync)
        {
            _connect = events;
            _connectCall = calls;
            _remoteTarget = remote;
            _uploadTarget = uploadTarget;
            _uploads.Clear();
            _parked = null;
            _remoteBackoff.Reset();
            _localBackoff.Reset();
            _firstRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            start = !remote || _eventsWanted;
        }

        _post(() =>
        {
            State.IsReady = false;
            State.IsConnected = false;
            State.RemoteSupported = false;
            State.ArchivesSupported = false;
            State.UploadsOffered = false;
            State.TorrentsOffered = false;
            State.AccountsKnown = false;
            State.ClearRemote();
            State.ApplyArchiveList([]);
        });
        if (start) Start();
    }

    /// <summary>
    /// False while the window is hidden: a server's event connection closes, so it sends nothing while nobody looks, and
    /// a call made meanwhile goes as one <c>POST /api/ipc</c> of its own without opening it again. This PC's pipe is left
    /// as it is.
    /// </summary>
    public async Task SetEventsWantedAsync(bool wanted)
    {
        bool remote;
        lock (_sync)
        {
            if (_eventsWanted == wanted) return;
            _eventsWanted = wanted;
            remote = _remoteTarget;
        }

        if (!remote) return;
        if (wanted) Start();
        else await EndRoundAsync().ConfigureAwait(false);
    }

    private async Task EndRoundAsync()
    {
        CancellationTokenSource old;
        Task? loop;
        lock (_sync)
        {
            old = _round;
            loop = _loop;
            _loop = null;
            _round = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
        }

        await old.CancelAsync().ConfigureAwait(false);
        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }
        }

        old.Dispose();
        FailPending();
    }

    /// <summary>Waits for what the first connection says, or the time given, whichever is first.</summary>
    public async Task WaitUntilReadyAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        Start();
        try
        {
            Task first;
            lock (_sync) first = _firstRead.Task;
            await first.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The Host isn't up yet. What is known so far is what gets shown.
        }
    }

    // ---- remote.account.* ---------------------------------------------------------------------

    /// <summary>The last known answer, with no network call. With <paramref name="refresh"/> it asks the provider.</summary>
    public async Task<RemoteAccountResult> GetAccountsAsync(string? provider = null, bool refresh = false, CancellationToken cancellationToken = default)
    {
        var request = new RemoteAccountRequest { Provider = provider, Refresh = refresh };
        var result = refresh
            ? await SlowAsync(MessageTypes.RemoteAccountGet, request, MessageTypes.RemoteAccountResult, FailedAccount, cancellationToken).ConfigureAwait(false)
            : await QuickAsync(MessageTypes.RemoteAccountGet, request, MessageTypes.RemoteAccountResult, FailedAccount, cancellationToken).ConfigureAwait(false);
        return Feed(result);
    }

    /// <summary>The key goes only into this one message. Nothing keeps it.</summary>
    public async Task<RemoteAccountResult> SetKeyAsync(string key, string? provider = null, CancellationToken cancellationToken = default)
    {
        var result = Feed(await QuickAsync(MessageTypes.RemoteAccountSetKey, new RemoteSetKeyRequest { Provider = provider, Key = key },
            MessageTypes.RemoteAccountResult, FailedAccount, cancellationToken).ConfigureAwait(false));
        if (result.Ok) _post(() => State.RaiseKeySaved(provider));
        return result;
    }

    public async Task<RemoteAccountResult> ClearKeyAsync(string? provider = null, CancellationToken cancellationToken = default)
        => Feed(await QuickAsync(MessageTypes.RemoteAccountClearKey, new RemoteAccountRequest { Provider = provider },
            MessageTypes.RemoteAccountResult, FailedAccount, cancellationToken).ConfigureAwait(false));

    public async Task<RemoteAccountResult> ValidateAsync(string? provider = null, CancellationToken cancellationToken = default)
        => Feed(await SlowAsync(MessageTypes.RemoteAccountValidate, new RemoteAccountRequest { Provider = provider },
            MessageTypes.RemoteAccountResult, FailedAccount, cancellationToken).ConfigureAwait(false));

    /// <summary>The provider's list of sites, from the Host's hourly cache.</summary>
    public Task<RemoteHostersResult> GetHostersAsync(CancellationToken cancellationToken = default)
        => SlowAsync(MessageTypes.RemoteHosters, new { }, MessageTypes.RemoteHostersResult,
            error => new RemoteHostersResult { Error = RemoteText.Failure(error) }, cancellationToken);

    // ---- remote.classify, add, list, item, files, select, retry, checkNow, remove, deleteRemote ----

    /// <param name="withFiles">For adding: a magnet or a .torrent file also says which files it holds, when that can be known.</param>
    public async Task<RemoteClassifyResult> ClassifyAsync(string text, CancellationToken cancellationToken = default, bool withFiles = false)
    {
        var (sent, uploadError) = await ForServerAsync(text, cancellationToken).ConfigureAwait(false);
        if (sent is null) return new RemoteClassifyResult { Error = uploadError };
        return await SlowAsync(MessageTypes.RemoteClassify, ClassifyRequest(text, withFiles, sent), MessageTypes.RemoteClassifyResult,
            error => new RemoteClassifyResult { Error = RemoteText.ClassifyFailure(error) }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// For many lines in a row: one connection, one command at a time, so a long paste doesn't open a
    /// connection and read a whole snapshot per line. It connects when first used, and holds one of the
    /// slow-call turns until it is disposed, so dispose it as soon as there is nothing left to ask.
    /// </summary>
    /// <param name="withFiles">As for <see cref="ClassifyAsync"/>, for every line asked about.</param>
    public IRemoteClassifier OpenClassifier(bool withFiles = false) => new ClassifierChannel(this, withFiles);

    /// <summary>Files are asked for only where they can be known, because for a magnet that is a call to the provider.</summary>
    private static RemoteClassifyRequest ClassifyRequest(string text, bool withFiles, string? sent = null)
        => new() { Text = sent ?? text, WithFiles = withFiles && AddChoices.WantsFiles(text) };

    /// <summary>
    /// A .torrent file on this PC, for another server: the server can read only its own files, so the file is uploaded
    /// (<c>POST /api/upload/torrent</c>) and its <c>upload:</c> reference sent instead of the path. Anything else, and
    /// everything for this PC's Host, goes as it is. Null with the reason when the file can't be sent.
    /// </summary>
    private async Task<(string? Sent, string? Error)> ForServerAsync(string text, CancellationToken cancellationToken)
    {
        ServerTarget? target;
        lock (_sync) target = _uploadTarget;
        if (!LinkExtractor.IsTorrentPath(text)) return (text, null);
        var path = text.Trim();
        // A network or device path is refused for this PC's Host too, before anything asks about it.
        if (TorrentRules.IsNetworkOrDevicePath(path))
            return (null, target is null ? TorrentRules.OnlyLocalTorrentFiles : TorrentPathProblem(path) ?? TorrentRules.OnlyLocalTorrentFiles);
        if (target is null) return (text, null);
        if (TorrentPathProblem(path) is { } problem) return (null, problem);
        byte[] bytes;
        string key;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return (null, "That .torrent file isn't on this PC.");
            if (info.Length is 0 or > MostTorrentBytes) return (null, "That file is too large to be a .torrent file.");
            key = UploadKey(info.FullName, info.Length, info.LastWriteTimeUtc);
            if (_uploads.TryGetValue(key, out var known) && DateTimeOffset.UtcNow - known.At < UploadKeptFor) return (known.Reference, null);
            bytes = await File.ReadAllBytesAsync(info.FullName, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return (null, "That .torrent file couldn't be read on this PC.");
        }

        var (reference, error) = await RemoteServerHttp.UploadTorrentAsync(target, bytes, cancellationToken).ConfigureAwait(false);
        if (reference is null) return (null, error);
        _uploads[key] = (reference, DateTimeOffset.UtcNow);
        return (reference, null);
    }

    /// <summary>
    /// Why a typed .torrent path isn't opened here, or null. A network path is never opened from here: even asking about
    /// it sends this Windows account's credentials there.
    /// </summary>
    internal static string? TorrentPathProblem(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
            return "Copy the .torrent file to this PC first; a file on a network share isn't sent from here.";
        return Path.IsPathFullyQualified(path) ? null : @"Type the .torrent file's full path, such as C:\Downloads\file.torrent.";
    }

    /// <summary>The same file: its path, size and time written. A changed file is uploaded again.</summary>
    internal static string UploadKey(string fullPath, long length, DateTime lastWriteUtc) => $"{fullPath}|{length}|{lastWriteUtc.Ticks}";

    /// <summary>The server's limit for an uploaded .torrent file.</summary>
    private const long MostTorrentBytes = 16 * 1024 * 1024;

    public async Task<RemoteAddResultDto> AddAsync(RemoteAddRequestDto request, CancellationToken cancellationToken = default)
    {
        if (request.Input is { } input)
        {
            var (sent, uploadError) = await ForServerAsync(input, cancellationToken).ConfigureAwait(false);
            if (sent is null) return new RemoteAddResultDto { Error = uploadError };
            request.Input = sent;
        }

        var result = await SlowAsync(MessageTypes.RemoteAdd, request, MessageTypes.RemoteAddResult,
            error => new RemoteAddResultDto { Error = error }, cancellationToken).ConfigureAwait(false);
        Feed(result.Job);
        return result;
    }

    public async Task<RemoteListResult> ListAsync(CancellationToken cancellationToken = default)
    {
        _post(State.BeginJobList);
        var result = await QuickAsync(MessageTypes.RemoteList, new { }, MessageTypes.RemoteListResult,
            error => new RemoteListResult { Error = error }, cancellationToken).ConfigureAwait(false);
        if (result.Ok) _post(() => State.ApplyJobList(result.Jobs));
        return result;
    }

    public Task<RemoteItemResult> GetItemsAsync(RemoteItemRequest request, CancellationToken cancellationToken = default)
        => SlowAsync(MessageTypes.RemoteItemGet, request, MessageTypes.RemoteItemResult,
            error => new RemoteItemResult { Error = error }, cancellationToken);

    public Task<RemoteFilesResultDto> GetFilesAsync(Guid jobId, CancellationToken cancellationToken = default)
        => SlowAsync(MessageTypes.RemoteFiles, new RemoteJobIdRequest { JobId = jobId }, MessageTypes.RemoteFilesResult,
            error => new RemoteFilesResultDto { Error = error }, cancellationToken);

    /// <summary>The Host may have to read the file list for this, so it counts as a slow call.</summary>
    public async Task<RemoteActionResultDto> SelectAsync(RemoteSelectRequest request, CancellationToken cancellationToken = default)
        => Feed(await SlowAsync(MessageTypes.RemoteSelect, request, MessageTypes.RemoteActionResult, FailedAction, cancellationToken).ConfigureAwait(false));

    public async Task<RemoteActionResultDto> RetryAsync(RemoteRetryRequest request, CancellationToken cancellationToken = default)
        => Feed(await QuickAsync(MessageTypes.RemoteRetry, request, MessageTypes.RemoteActionResult, FailedAction, cancellationToken).ConfigureAwait(false));

    public async Task<RemoteActionResultDto> CheckNowAsync(Guid? jobId = null, string? provider = null, CancellationToken cancellationToken = default)
        => Feed(await QuickAsync(MessageTypes.RemoteCheckNow, new RemoteCheckNowRequest { JobId = jobId, Provider = provider },
            MessageTypes.RemoteActionResult, FailedAction, cancellationToken).ConfigureAwait(false));

    /// <summary>Takes the job off this app's list. The provider's item and any local downloads stay.</summary>
    public async Task<RemoteActionResultDto> RemoveAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var result = await QuickAsync(MessageTypes.RemoteRemove, new RemoteJobIdRequest { JobId = jobId },
            MessageTypes.RemoteActionResult, FailedAction, cancellationToken).ConfigureAwait(false);
        if (result.Ok) _post(() => State.RemoveJob(jobId));
        return result;
    }

    /// <summary>TB.S1: starts the job's entry in the provider's queue now.</summary>
    public async Task<RemoteActionResultDto> StartQueuedAsync(Guid jobId, CancellationToken cancellationToken = default)
        => Feed(await SlowAsync(MessageTypes.RemoteStartQueued, new RemoteJobIdRequest { JobId = jobId },
            MessageTypes.RemoteActionResult, FailedAction, cancellationToken).ConfigureAwait(false));

    /// <summary>Deletes the item from the provider for good, or, for a job waiting in its queue, takes that entry out. Ask the user first.</summary>
    public async Task<RemoteActionResultDto> DeleteRemoteAsync(Guid jobId, bool confirmed, CancellationToken cancellationToken = default)
        => Feed(await SlowAsync(MessageTypes.RemoteDeleteRemote, new RemoteDeleteRequest { JobId = jobId, Confirmed = confirmed },
            MessageTypes.RemoteActionResult, FailedAction, cancellationToken).ConfigureAwait(false));

    // ---- remote.container.* --------------------------------------------------------------------

    /// <summary>
    /// For this PC's Host the path goes untouched. Nothing here looks at it: even asking whether a file on a network path
    /// exists sends this Windows account's credentials there, and the Host is the one that refuses those. For another
    /// server the file is uploaded first (<see cref="ContainerForServerAsync"/>) and its reference sent instead.
    /// </summary>
    public async Task<RemoteContainerPreviewResult> PreviewContainerAsync(string path, CancellationToken cancellationToken = default)
        => (await ReadContainerAsync(path, cancellationToken).ConfigureAwait(false)).Result;

    /// <summary>
    /// As <see cref="PreviewContainerAsync"/>, and says whether asking again could help when the file wasn't read because
    /// uploading it failed on the way (no answer in time, or the server out of reach). A kept reference the server says
    /// is gone is dropped, and the file is uploaded once more.
    /// </summary>
    public async Task<ContainerReadReply> ReadContainerAsync(string path, CancellationToken cancellationToken = default)
    {
        var upload = await ContainerForServerAsync(path, cancellationToken).ConfigureAwait(false);
        var reply = await PreviewUploadedAsync(upload, cancellationToken).ConfigureAwait(false);
        if (!ReferenceGone(upload, reply.Result.Error)) return reply;

        upload = await ContainerForServerAsync(path, cancellationToken, forceUpload: true).ConfigureAwait(false);
        return await PreviewUploadedAsync(upload, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ContainerReadReply> PreviewUploadedAsync(ContainerUpload upload, CancellationToken cancellationToken)
    {
        if (upload.Sent is null) return new ContainerReadReply(new RemoteContainerPreviewResult { Error = upload.Error }, upload.CanRetry);
        var result = await SlowAsync(MessageTypes.RemoteContainerPreview, new RemoteContainerPreviewRequest { Path = upload.Sent, FileName = upload.FileName }, MessageTypes.RemoteContainerPreviewResult,
            failure => new RemoteContainerPreviewResult { Error = RemoteText.ContainerReadFailure(failure) }, cancellationToken).ConfigureAwait(false);
        return new ContainerReadReply(result, false);
    }

    /// <summary>
    /// A reply that used a kept reference and says its file is gone: the reference is dropped from the cache, so the next
    /// <see cref="ContainerForServerAsync"/> uploads the file again. False when no kept reference was involved.
    /// </summary>
    private bool ReferenceGone(ContainerUpload upload, string? said)
    {
        if (upload.KeptKey is not { } key || !UploadReferenceText.IsGone(said)) return false;
        _uploads.TryRemove(key, out _);
        return true;
    }

    /// <summary>What <see cref="ContainerForServerAsync"/> found out.</summary>
    /// <param name="Sent">What goes to the Host or server as the path. Null when the file can't be sent: <paramref name="Error"/> says why.</param>
    /// <param name="CanRetry">The upload failed on the way, not because of the file, so asking again could help.</param>
    /// <param name="KeptKey">The cache entry the reference came from, when it wasn't uploaded just now.</param>
    private sealed record ContainerUpload(string? Sent, string? FileName, string? Error, bool CanRetry = false, string? KeptKey = null);

    /// <summary>
    /// A container file on this PC, for another server whose hello lists <c>uploads</c>: uploaded
    /// (<c>POST /api/upload/container</c>, the name in a header for its kind), and its <c>upload:</c> reference sent with the
    /// file's name. For this PC's Host, the path as it is. Not sent, with the reason, when the file can't be sent.
    /// </summary>
    /// <param name="forceUpload">Ignores a kept reference, because the server said its file is gone.</param>
    private async Task<ContainerUpload> ContainerForServerAsync(string path, CancellationToken cancellationToken, bool forceUpload = false)
    {
        ServerTarget? target;
        lock (_sync) target = _uploadTarget;
        if (target is null) return new ContainerUpload(path, null, null);
        var text = path.Trim();
        if (ContainerPathProblem(text) is { } problem) return new ContainerUpload(null, null, problem);
        var name = Path.GetFileName(text);
        byte[] bytes;
        string key;
        try
        {
            var info = new FileInfo(text);
            if (!info.Exists) return new ContainerUpload(null, null, "That container file isn't on this PC.");
            if (info.Length is 0 or > MostContainerBytes) return new ContainerUpload(null, null, "That file is too large to be a container.");
            key = ContainerUploadKey(info.FullName, info.Length, info.LastWriteTimeUtc);
            if (forceUpload) _uploads.TryRemove(key, out _);
            else if (_uploads.TryGetValue(key, out var known) && DateTimeOffset.UtcNow - known.At < UploadKeptFor) return new ContainerUpload(known.Reference, name, null, KeptKey: key);
            bytes = await File.ReadAllBytesAsync(info.FullName, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return new ContainerUpload(null, null, "That container file couldn't be read on this PC.");
        }

        var (reference, error) = await RemoteServerHttp.UploadContainerAsync(target, bytes, name, cancellationToken).ConfigureAwait(false);
        if (reference is null) return new ContainerUpload(null, null, error, UploadCanBeRetried(error, target));
        _uploads[key] = (reference, DateTimeOffset.UtcNow);
        return new ContainerUpload(reference, name, null);
    }

    /// <summary>The cache key for a container file: <see cref="UploadKey"/> with its own prefix, so it never meets a .torrent file's.</summary>
    internal static string ContainerUploadKey(string fullPath, long length, DateTime lastWriteUtc) => "container|" + UploadKey(fullPath, length, lastWriteUtc);

    /// <summary>Keeps a reference as if the file had just been uploaded. For tests.</summary>
    internal void RememberUpload(string key, string reference) => _uploads[key] = (reference, DateTimeOffset.UtcNow);

    /// <summary>Whether a reference is kept for the key. For tests.</summary>
    internal bool HasUpload(string key) => _uploads.ContainsKey(key);

    /// <summary>
    /// Whether an upload failed because the server didn't answer in time or couldn't be reached, so that uploading again
    /// could work. A server that refuses the file, doesn't take uploads or has ended the pairing won't change its mind.
    /// The upload reports only words, so these are told by the two sentences it uses (RemoteServerHttp.UploadAsync).
    /// </summary>
    internal static bool UploadCanBeRetried(string? error, ServerTarget target)
        => error is not null &&
           (error == $"Couldn't reach {target.Name}." ||
            (error.StartsWith($"{target.Name} didn't take the ", StringComparison.Ordinal) && error.EndsWith(" in time. Try again.", StringComparison.Ordinal)));

    /// <summary>Why a container path isn't opened here for a server, or null: as <see cref="TorrentPathProblem"/>.</summary>
    internal static string? ContainerPathProblem(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
            return "Copy the container file to this PC first; a file on a network share isn't sent from here.";
        return Path.IsPathFullyQualified(path) ? null : "The container file needs its full path on this PC.";
    }

    /// <summary>The server's limit for an uploaded container (ContainerLimits.Default.MaxInputBytes).</summary>
    private const long MostContainerBytes = 16 * 1024 * 1024;

    /// <summary>
    /// Adds the chosen entries. A reply with <see cref="RemoteContainerAddResult.JobsLeftOut"/> has the entries saved and the
    /// full jobs left out, so those are read from <c>remote.list</c>, and that read happens whatever became of the caller's wait.
    /// </summary>
    public async Task<RemoteContainerAddResult> AddFromContainerAsync(RemoteContainerAddRequest request, CancellationToken cancellationToken = default)
    {
        var result = await SlowAsync(MessageTypes.RemoteContainerAdd, request, MessageTypes.RemoteContainerAddResult,
            error => new RemoteContainerAddResult { Error = error }, cancellationToken, ContainerAddTimeout).ConfigureAwait(false);
        foreach (var outcome in result.Outcomes) Feed(outcome.Job);
        if (result.JobsLeftOut) await ListAsync(CancellationToken.None).ConfigureAwait(false);
        return result;
    }

    /// <summary>Waits for a person to answer in JDownloader. Cancelling stops waiting here, and JDownloader may still be asking.</summary>
    public async Task<RemoteContainerSendResult> SendContainerToJdAsync(string path, CancellationToken cancellationToken = default)
    {
        var upload = await ContainerForServerAsync(path, cancellationToken).ConfigureAwait(false);
        var result = await SendUploadedAsync(upload, cancellationToken).ConfigureAwait(false);
        if (!ReferenceGone(upload, result.Message)) return result;

        upload = await ContainerForServerAsync(path, cancellationToken, forceUpload: true).ConfigureAwait(false);
        return await SendUploadedAsync(upload, cancellationToken).ConfigureAwait(false);
    }

    private async Task<RemoteContainerSendResult> SendUploadedAsync(ContainerUpload upload, CancellationToken cancellationToken)
    {
        if (upload.Sent is null) return new RemoteContainerSendResult { Status = "failed", Message = upload.Error ?? string.Empty };
        return await SlowAsync(MessageTypes.RemoteContainerSendToJd, new RemoteContainerSendRequest { Path = upload.Sent }, MessageTypes.RemoteContainerSendResult,
            failure => new RemoteContainerSendResult { Status = "failed", Message = RemoteText.ContainerSendFailure(failure) }, cancellationToken, SendToJdTimeout).ConfigureAwait(false);
    }

    // ---- archive.* ----------------------------------------------------------------------------

    public Task<ArchiveSettingsResult> GetArchiveSettingsAsync(CancellationToken cancellationToken = default)
        => QuickAsync(ArchiveMessageTypes.SettingsGet, new { }, ArchiveMessageTypes.SettingsResult, FailedSettings, cancellationToken);

    /// <summary>A magnet, or a .torrent file on this PC (uploaded first when the target is another server).</summary>
    /// <param name="destination">T3: the folder whose free space the Host measures; null is the category folder.</param>
    /// <param name="fromJobId">T10: the Host's finished .torrent download this line is the file of. The Host reads that
    /// file itself, so nothing is uploaded and the path never has to be on this PC (another server, a network share).</param>
    public async Task<TorrentInspectResult> InspectTorrentAsync(string input, CancellationToken cancellationToken = default, string? destination = null,
        Guid? fromJobId = null)
    {
        var request = new TorrentInspectRequest { Destination = destination, FromJobId = fromJobId };
        if (fromJobId is null)
        {
            var (sent, problem) = await ForServerAsync(input, cancellationToken).ConfigureAwait(false);
            if (sent is null) return new TorrentInspectResult { Error = problem };
            request.Input = sent;
        }

        return await QuickAsync(TorrentMessageTypes.Inspect, request, TorrentMessageTypes.InspectResult,
            error => new TorrentInspectResult { Error = error }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TorrentAddResult> AddTorrentAsync(TorrentAddRequest request, CancellationToken cancellationToken = default)
    {
        string sent = string.Empty;
        // T10: a finished .torrent download goes by its id; the Host reads its own file, so nothing is uploaded.
        if (request.FromJobId is null)
        {
            var (forServer, problem) = await ForServerAsync(request.Input, cancellationToken).ConfigureAwait(false);
            if (forServer is null) return new TorrentAddResult { Error = problem };
            sent = forServer;
        }

        var payload = TorrentAddPayload(request, sent);
        return await QuickAsync(TorrentMessageTypes.Add, payload, TorrentMessageTypes.AddResult, error => new TorrentAddResult { Error = error },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The request as it goes to the Host, with <paramref name="sent"/> as its input (empty with a FromJobId).</summary>
    internal static TorrentAddRequest TorrentAddPayload(TorrentAddRequest request, string sent) => new()
    {
        Input = sent, Destination = request.Destination, QueueId = request.QueueId, StartMode = request.StartMode,
        Files = request.Files, FileIndexes = request.FileIndexes, Description = request.Description,
        Priorities = request.Priorities, Options = request.Options, FromJobId = request.FromJobId, OnlyIfListed = request.OnlyIfListed,
    };

    public Task<TorrentActionResult> SetTorrentFilesAsync(Guid jobId, IEnumerable<int> indexes, CancellationToken cancellationToken = default)
        => QuickAsync(TorrentMessageTypes.SetFiles, new TorrentSetFilesRequest { JobId = jobId, FileIndexes = indexes.ToList() },
            TorrentMessageTypes.ActionResult, error => new TorrentActionResult { Error = error }, cancellationToken);

    public Task<TorrentFilesResult> GetTorrentFilesAsync(Guid jobId, CancellationToken cancellationToken = default)
        => QuickAsync(TorrentMessageTypes.Files, new TorrentFilesRequest { JobId = jobId }, TorrentMessageTypes.FilesResult,
            error => new TorrentFilesResult { Error = error }, cancellationToken);

    /// <summary>T2: starts (<paramref name="on"/>) or stops seeding a finished torrent.</summary>
    public Task<TorrentActionResult> SetTorrentSeedingAsync(Guid jobId, bool on, CancellationToken cancellationToken = default)
        => QuickAsync(TorrentMessageTypes.Seed, new TorrentSeedRequest { JobId = jobId, On = on }, TorrentMessageTypes.ActionResult,
            error => new TorrentActionResult { Error = error }, cancellationToken);

    /// <summary>T3: checks a torrent's files on disk again.</summary>
    public Task<TorrentActionResult> RecheckTorrentAsync(Guid jobId, CancellationToken cancellationToken = default)
        => QuickAsync(TorrentMessageTypes.Recheck, new TorrentJobRequest { JobId = jobId }, TorrentMessageTypes.ActionResult,
            error => new TorrentActionResult { Error = error }, cancellationToken);

    /// <summary>T3: asks a running torrent's trackers (and the DHT) for peers now.</summary>
    public Task<TorrentActionResult> AnnounceTorrentAsync(Guid jobId, CancellationToken cancellationToken = default)
        => QuickAsync(TorrentMessageTypes.Announce, new TorrentJobRequest { JobId = jobId }, TorrentMessageTypes.ActionResult,
            error => new TorrentActionResult { Error = error }, cancellationToken);

    /// <summary>T2: the port hint of Options › Torrents.</summary>
    public Task<TorrentNetworkResult> GetTorrentNetworkAsync(CancellationToken cancellationToken = default)
        => QuickAsync(TorrentMessageTypes.Network, new { }, TorrentMessageTypes.NetworkResult,
            error => new TorrentNetworkResult { Error = error }, cancellationToken);

    public Task<ArchiveSettingsResult> SetArchiveSettingsAsync(ArchiveSettingsUpdate update, CancellationToken cancellationToken = default)
        => QuickAsync(ArchiveMessageTypes.SettingsSet, update, ArchiveMessageTypes.SettingsResult, FailedSettings, cancellationToken);

    public async Task<ArchiveListReply> ListArchivesAsync(CancellationToken cancellationToken = default)
    {
        _post(State.BeginArchiveList);
        var reply = await QuickAsync(ArchiveMessageTypes.List, new { }, ArchiveMessageTypes.ListResult,
            error => new ArchiveListReply(error), ReadArchiveList, cancellationToken).ConfigureAwait(false);
        if (reply.Ok) _post(() => State.ApplyArchiveList(reply.Sets));
        return reply;
    }

    public async Task<ArchiveExtractResult> ExtractAsync(ArchiveExtractRequest request, CancellationToken cancellationToken = default)
    {
        var result = await QuickAsync(ArchiveMessageTypes.Extract, request, ArchiveMessageTypes.ExtractResult,
            error => new ArchiveExtractResult { Error = error }, cancellationToken).ConfigureAwait(false);
        foreach (var set in result.Sets) _post(() => State.ApplyArchiveReply(set));
        return result;
    }

    /// <summary>For a window that sends <c>archive.list</c> on a connection it already has.</summary>
    internal static ArchiveListReply ReadArchiveListReply(IpcMessage reply)
        => Read(reply, ArchiveMessageTypes.ListResult, error => new ArchiveListReply(error), ReadArchiveList);

    /// <summary>For a window that sends an archive action on a connection it already has.</summary>
    internal static ArchiveActionResult ReadArchiveActionReply(IpcMessage reply)
        => Read(reply, ArchiveMessageTypes.ActionResult, error => new ArchiveActionResult { Error = error }, raw => raw.GetPayload<ArchiveActionResult>());

    private static ArchiveListReply ReadArchiveList(IpcMessage reply)
    {
        if (reply.Type != ArchiveMessageTypes.ListResult) return new ArchiveListReply(RemoteText.Failure(reply.GetPayload<ArchiveActionResult>().Error));
        var list = reply.GetPayload<ArchiveListResult>();
        return new ArchiveListReply(null, list.Sets) { PackagesWithPassword = list.PackagesWithPassword ?? [] };
    }

    /// <summary>The password goes only into this one message. An empty one clears what is on record.</summary>
    public Task<ArchiveActionResult> SetArchivePasswordAsync(ArchiveSetPasswordRequest request, CancellationToken cancellationToken = default)
        => ArchiveActionAsync(ArchiveMessageTypes.SetPassword, request, cancellationToken);

    public Task<ArchiveActionResult> RetryArchiveAsync(Guid setId, CancellationToken cancellationToken = default)
        => ArchiveActionAsync(ArchiveMessageTypes.Retry, new ArchiveActionRequest { SetId = setId }, cancellationToken);

    public Task<ArchiveActionResult> CancelArchiveAsync(Guid setId, CancellationToken cancellationToken = default)
        => ArchiveActionAsync(ArchiveMessageTypes.Cancel, new ArchiveActionRequest { SetId = setId }, cancellationToken);

    /// <summary>Forgets the record. No file is touched.</summary>
    public async Task<ArchiveActionResult> RemoveArchiveAsync(Guid setId, CancellationToken cancellationToken = default)
    {
        var result = await QuickAsync(ArchiveMessageTypes.Remove, new ArchiveActionRequest { SetId = setId }, ArchiveMessageTypes.ActionResult,
            error => new ArchiveActionResult { Error = error }, cancellationToken).ConfigureAwait(false);
        if (result.Ok) _post(() => State.RemoveArchiveSet(setId));
        return result;
    }

    private async Task<ArchiveActionResult> ArchiveActionAsync<T>(string type, T request, CancellationToken cancellationToken)
    {
        var result = await QuickAsync(type, request, ArchiveMessageTypes.ActionResult,
            error => new ArchiveActionResult { Error = error }, cancellationToken).ConfigureAwait(false);
        if (result.Set is { } set) _post(() => State.ApplyArchiveReply(set));
        return result;
    }

    // ---- Feeding the state from replies ---------------------------------------------------------

    private RemoteAccountResult Feed(RemoteAccountResult result)
    {
        if (result.Accounts.Count > 0) _post(() => State.ApplyAccounts(result.Accounts));
        return result;
    }

    private RemoteActionResultDto Feed(RemoteActionResultDto result)
    {
        Feed(result.Job);
        return result;
    }

    private void Feed(RemoteJobDto? job)
    {
        if (job is not null) _post(() => State.ApplyJobReply(job));
    }

    private static RemoteAccountResult FailedAccount(string error) => new() { Error = error };

    private static RemoteActionResultDto FailedAction(string error) => new() { Error = error };

    private static ArchiveSettingsResult FailedSettings(string error) => new() { Error = error };

    // ---- Calls ----------------------------------------------------------------------------------

    private Task<TResult> QuickAsync<TRequest, TResult>(string type, TRequest request, string replyType, Func<string, TResult> failed, CancellationToken cancellationToken)
        => QuickAsync(type, request, replyType, failed, raw => raw.GetPayload<TResult>(), cancellationToken);

    private async Task<TResult> QuickAsync<TRequest, TResult>(
        string type, TRequest request, string replyType, Func<string, TResult> failed, Func<IpcMessage, TResult> read, CancellationToken cancellationToken)
    {
        bool remote, eventsWanted, controlReady;
        string? parked;
        lock (_sync)
        {
            remote = _remoteTarget;
            eventsWanted = _eventsWanted;
            controlReady = _control.Task.IsCompletedSuccessfully;
            parked = _parked;
        }

        if (remote && parked is not null) return failed(parked);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, RoundToken);
        deadline.CancelAfter(_quickTimeout);
        try
        {
            // A server's call goes as one POST while its event socket is closed (the window is hidden) or not back yet:
            // that says at once what is wrong, and a hidden window doesn't open the socket again.
            IpcMessage reply;
            if (remote && (!eventsWanted || !controlReady))
            {
                if (eventsWanted) Start();
                reply = await RequestOnceAsync(type, request, deadline.Token).ConfigureAwait(false);
            }
            else
            {
                Start();
                reply = await RequestOnControlAsync(type, request, deadline.Token).ConfigureAwait(false);
            }

            return Read(reply, replyType, failed, read);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (IsCommunicationFailure(ex))
        {
            return failed(DescribeFailure(ex, reachedHost: ex is IOException || ControlSource().Task.IsCompletedSuccessfully));
        }
    }

    private async Task<TResult> SlowAsync<TRequest, TResult>(string type, TRequest request, string replyType, Func<string, TResult> failed, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        string? parked;
        lock (_sync) parked = _remoteTarget ? _parked : null;
        if (parked is not null) return failed(parked);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, RoundToken);
        deadline.CancelAfter(timeout ?? _slowTimeout);
        var connected = false;
        var holdsTurn = false;
        try
        {
            await _slowCalls.WaitAsync(deadline.Token).ConfigureAwait(false);
            holdsTurn = true;
            await using var pipe = await _connectCall(ConnectTimeout, deadline.Token).ConfigureAwait(false);
            connected = true;
            var message = IpcMessage.Create(type, request);
            await pipe.SendAsync(message, deadline.Token).ConfigureAwait(false);

            // Hello, the snapshot and events go by on the way to the reply.
            while (await pipe.ReadAsync(deadline.Token).ConfigureAwait(false) is { } reply)
            {
                if (reply.ReplyTo == message.Id) return Read(reply, replyType, failed, raw => raw.GetPayload<TResult>());
            }

            return failed(RemoteText.HostLost);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (IsCommunicationFailure(ex))
        {
            return failed(DescribeFailure(ex, connected));
        }
        finally
        {
            if (holdsTurn) _slowCalls.Release();
        }
    }

    private static TResult Read<TResult>(IpcMessage reply, string replyType, Func<string, TResult> failed, Func<IpcMessage, TResult> read)
    {
        // A Host that doesn't know the message answers with something else. An archive request that
        // fails to save answers with an action result, which has the same ok and error.
        var understood = reply.Type == replyType ||
            (reply.Type == ArchiveMessageTypes.ActionResult && replyType.StartsWith(ArchiveMessageTypes.Prefix, StringComparison.Ordinal));
        if (!understood)
        {
            return failed(reply.Type == MessageTypes.ServerIncompatible ? RemoteText.HostIncompatible : RemoteText.HostDoesNotOffer);
        }

        try
        {
            return read(reply);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or InvalidOperationException)
        {
            return failed(RemoteText.HostUnreadable);
        }
    }

    internal static bool IsCommunicationFailure(Exception ex) => ex is
        OperationCanceledException or TimeoutException or IOException or ObjectDisposedException or
        UnauthorizedAccessException or InvalidOperationException or JsonException or InvalidDataException;

    internal static string DescribeFailure(Exception ex, bool reachedHost) => ex switch
    {
        RemoteServerException => ex.Message,
        HostPipeBusyException => RemoteText.HostBusy,
        OperationCanceledException or TimeoutException when reachedHost => RemoteText.HostTooSlow,
        OperationCanceledException or TimeoutException => RemoteText.HostNotRunning,
        InvalidDataException or JsonException => RemoteText.HostUnreadable,
        _ => reachedHost ? RemoteText.HostLost : RemoteText.HostNotRunning,
    };

    private async Task<IpcMessage> RequestOnControlAsync<T>(string type, T payload, CancellationToken cancellationToken)
    {
        // A Host that is down gets said so soon, rather than after the whole time a reply may take.
        IRemotePipe pipe;
        try
        {
            pipe = await ControlSource().Task.WaitAsync(ConnectTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException) when (_pipeBusy)
        {
            throw new HostPipeBusyException();
        }

        var request = IpcMessage.Create(type, payload);
        var completion = new TaskCompletionSource<IpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[request.Id] = completion;
        try
        {
            await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await pipe.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // A frame cut off halfway would leave the rest of this connection unreadable to the Host.
                _ = pipe.DisposeAsync();
                throw;
            }
            finally
            {
                _sendGate.Release();
            }

            return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(request.Id, out _);
        }
    }

    /// <summary>One request on a connection of its own (to a server: one <c>POST /api/ipc</c>), and its reply.</summary>
    private async Task<IpcMessage> RequestOnceAsync<T>(string type, T payload, CancellationToken cancellationToken)
    {
        await using var pipe = await _connectCall(ConnectTimeout, cancellationToken).ConfigureAwait(false);
        var message = IpcMessage.Create(type, payload);
        await pipe.SendAsync(message, cancellationToken).ConfigureAwait(false);
        while (await pipe.ReadAsync(cancellationToken).ConfigureAwait(false) is { } reply)
        {
            if (reply.ReplyTo == message.Id) return reply;
        }

        throw new IOException("The connection closed before the answer came.");
    }

    private TaskCompletionSource<IRemotePipe> ControlSource()
    {
        lock (_sync) return _control;
    }

    private static TaskCompletionSource<IRemotePipe> NewControlSource() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    // ---- The event connection -------------------------------------------------------------------

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        bool remote;
        lock (_sync) remote = _remoteTarget;
        while (!cancellationToken.IsCancellationRequested)
        {
            IRemotePipe? pipe = null;
            var published = false;
            try
            {
                pipe = await _connect(EventConnectTimeout, cancellationToken).ConfigureAwait(false);
                _pipeBusy = false;
                var hello = await pipe.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (hello?.Type != MessageTypes.ServerHello || hello.V != Protocol.Version)
                {
                    throw new InvalidDataException("The Host did not send a usable greeting.");
                }

                var capabilities = hello.GetPayload<HostHelloDto>().Capabilities;
                var offersRemote = capabilities.Contains("remote", StringComparer.Ordinal);
                var offersUploads = remote && capabilities.Contains("uploads", StringComparer.Ordinal);
                var offersTorrents = capabilities.Contains("torrents", StringComparer.Ordinal);

                // The snapshot comes before any event, and this client has no use for it. A server's socket asks for none.
                if (pipe.SnapshotFollowsHello) _ = await pipe.ReadAsync(cancellationToken).ConfigureAwait(false);

                lock (_sync) _control.TrySetResult(pipe);
                published = true;
                if (remote) _remoteBackoff.Reset();
                else _localBackoff.Reset();
                _post(() =>
                {
                    State.IsConnected = true;
                    State.UploadsOffered = offersUploads;
                    State.TorrentsOffered = offersTorrents;
                });

                // Not awaited: its replies arrive through the loop below.
                _ = SeedAsync(offersRemote, cancellationToken);

                while (await pipe.ReadAsync(cancellationToken).ConfigureAwait(false) is { } message)
                {
                    Dispatch(message);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (RemoteServerException ex) when (remote && ex is RemotePairingEndedException or PlainHttpNotConfirmedException)
            {
                // Trying again can't help until the user pairs again or allows the address (File > Servers), which
                // retargets this client. Calls meanwhile say why at once.
                lock (_sync)
                {
                    _parked = ex.Message;
                    _firstRead.TrySetResult();
                }

                _post(() => State.IsReady = true);
            }
            catch (Exception ex) when (IsCommunicationFailure(ex))
            {
                // Comes back around. Whoever is waiting on a call hears about it below.
                _pipeBusy = ex is HostPipeBusyException;
            }
            finally
            {
                if (published)
                {
                    lock (_sync) _control = NewControlSource();
                    FailPending();
                    _post(() => State.IsConnected = false);
                }

                if (pipe is not null)
                {
                    try { await pipe.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception ex) when (IsCommunicationFailure(ex)) { }
                }
            }

            lock (_sync)
            {
                if (_parked is not null) break;
            }

            try
            {
                await Task.Delay(remote ? _remoteBackoff.Next() : _localBackoff.Next(), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>A client that connects reads these for itself: they aren't part of the snapshot.</summary>
    private async Task SeedAsync(bool remote, CancellationToken cancellationToken)
    {
        try
        {
            await SeedRemoteAsync(remote, cancellationToken).ConfigureAwait(false);
            await SeedArchivesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsCommunicationFailure(ex))
        {
            // The connection went away meanwhile. The next one starts over.
        }
        finally
        {
            _post(() => State.IsReady = true);
            lock (_sync) _firstRead.TrySetResult();
        }
    }

    private async Task SeedRemoteAsync(bool remote, CancellationToken cancellationToken)
    {
        if (!remote)
        {
            _post(() =>
            {
                State.RemoteSupported = false;
                State.AccountsKnown = true;
                State.ClearRemote();
            });
            return;
        }

        var accountsReply = await QuickAsync(MessageTypes.RemoteAccountGet, new RemoteAccountRequest(), MessageTypes.RemoteAccountResult, FailedAccount, cancellationToken).ConfigureAwait(false);
        if (accountsReply.Ok)
        {
            _post(() =>
            {
                State.ApplyAccounts(accountsReply.Accounts, wholeList: true);
                State.RemoteSupported = accountsReply.Accounts.Count > 0;
                State.AccountsKnown = true;
            });
        }
        else
        {
            _post(() => State.AccountsKnown = false);
        }

        await ListAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task SeedArchivesAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_quickTimeout);
        _post(State.BeginArchiveList);
        var reply = await RequestOnControlAsync(ArchiveMessageTypes.List, new { }, deadline.Token).ConfigureAwait(false);
        var supported = reply.Type == ArchiveMessageTypes.ListResult;
        var sets = supported ? reply.GetPayload<ArchiveListResult>().Sets : [];
        _post(() =>
        {
            State.ArchivesSupported = supported;
            State.ApplyArchiveList(sets);
        });
    }

    private void Dispatch(IpcMessage message)
    {
        if (message.ReplyTo is { } id)
        {
            if (_pending.TryRemove(id, out var completion)) completion.TrySetResult(message);
            return;
        }

        try
        {
            if (message.Type == MessageTypes.RemoteChanged)
            {
                var change = message.GetPayload<RemoteChangedDto>();
                _post(() => State.Apply(change));
            }
            else if (message.Type == ArchiveMessageTypes.Changed)
            {
                var change = message.GetPayload<ArchiveChangedDto>();
                _post(() => State.Apply(change));
            }
            else if (message.Type == WebUiMessageTypes.Changed)
            {
                RaiseWebUiChanged(message);
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            // One bad event must not cost the connection. The next whole state replaces it.
        }
    }

    private void FailPending()
    {
        foreach (var pending in _pending.Values)
        {
            pending.TrySetException(new IOException("The connection to the Host was lost."));
        }

        _pending.Clear();
    }

    private static async Task<IRemotePipe> ConnectToHostAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            return new HostPipe(await IpcClient.ConnectAsync(timeout, cancellationToken).ConfigureAwait(false));
        }
        catch (TimeoutException) when (HostPipeBusyException.PipeExists())
        {
            throw new HostPipeBusyException();
        }
    }

    private static async Task<IRemotePipe> ConnectToServerAsync(ServerTarget target, TimeSpan timeout, CancellationToken cancellationToken)
        => new HostPipe(await WebSocketIpcClient.ConnectAsync(target, new ClientHelloDto { Snapshot = false, Events = true },
            timeout + TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false));

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        Task? loop;
        lock (_sync) loop = _loop;
        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }
        }

        FailPending();
    }

    private sealed class ClassifierChannel(RemoteClient client, bool withFiles) : IRemoteClassifier
    {
        private IRemotePipe? _pipe;
        private bool _holdsTurn;

        public bool Broken { get; private set; }

        public async Task<RemoteClassifyResult> ClassifyAsync(string text, CancellationToken cancellationToken)
        {
            if (Broken) return new RemoteClassifyResult { Error = RemoteText.ClassifyLost };
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, client.RoundToken);
            deadline.CancelAfter(client._slowTimeout);
            var connected = _pipe is not null;
            try
            {
                if (_pipe is null)
                {
                    await client._slowCalls.WaitAsync(deadline.Token).ConfigureAwait(false);
                    _holdsTurn = true;
                    _pipe = await client._connectCall(ConnectTimeout, deadline.Token).ConfigureAwait(false);
                    connected = true;
                }

                var (sent, uploadError) = await client.ForServerAsync(text, deadline.Token).ConfigureAwait(false);
                if (sent is null) return new RemoteClassifyResult { Error = uploadError };
                var message = IpcMessage.Create(MessageTypes.RemoteClassify, ClassifyRequest(text, withFiles, sent));
                await _pipe.SendAsync(message, deadline.Token).ConfigureAwait(false);
                while (await _pipe.ReadAsync(deadline.Token).ConfigureAwait(false) is { } reply)
                {
                    if (reply.ReplyTo != message.Id) continue;
                    var result = Read(reply, MessageTypes.RemoteClassifyResult, Failed, raw => raw.GetPayload<RemoteClassifyResult>());
                    if (reply.Type != MessageTypes.RemoteClassifyResult) Broken = true;
                    return result;
                }

                Broken = true;
                return Failed(RemoteText.HostLost);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Broken = true;
                throw;
            }
            catch (Exception ex) when (IsCommunicationFailure(ex))
            {
                Broken = true;
                return Failed(DescribeFailure(ex, connected));
            }
        }

        private static RemoteClassifyResult Failed(string error) => new() { Error = RemoteText.ClassifyFailure(error) };

        public async ValueTask DisposeAsync()
        {
            Broken = true;
            var pipe = _pipe;
            _pipe = null;
            try
            {
                if (pipe is not null) await pipe.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (IsCommunicationFailure(ex))
            {
                // Closing is the point, and it is closed now.
            }
            finally
            {
                if (_holdsTurn)
                {
                    _holdsTurn = false;
                    client._slowCalls.Release();
                }
            }
        }
    }

    private sealed class HostPipe(IIpcConnection client) : IRemotePipe
    {
        public Task SendAsync(IpcMessage message, CancellationToken cancellationToken) => client.SendAsync(message, cancellationToken);

        public Task<IpcMessage?> ReadAsync(CancellationToken cancellationToken) => client.ReadAsync(cancellationToken);

        public bool SnapshotFollowsHello => client.SnapshotFollowsHello;

        public ValueTask DisposeAsync() => client.DisposeAsync();
    }

    /// <summary>
    /// A server's call that would have had a pipe of its own: each message sent is one <c>POST /api/ipc</c>, and the
    /// next read is its reply. Ending the request (cancelling) ends the call on the server, as closing a pipe does.
    /// </summary>
    internal sealed class OneShotPipe(ServerTarget target) : IRemotePipe
    {
        private Task<IpcMessage>? _reply;

        public Task SendAsync(IpcMessage message, CancellationToken cancellationToken)
        {
            _reply = RemoteServerHttp.SendIpcAsync(target, message, cancellationToken);
            return Task.CompletedTask;
        }

        public async Task<IpcMessage?> ReadAsync(CancellationToken cancellationToken)
        {
            var reply = _reply;
            _reply = null;
            return reply is null ? null : await reply.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        public bool SnapshotFollowsHello => false;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>
/// The Host is there, but every one of its pipe instances is taken. A connect times out the same way
/// whether the Host is busy or gone, so what tells them apart is whether the pipe exists at all.
/// </summary>
internal sealed class HostPipeBusyException : TimeoutException
{
    private const int FileNotFound = 2;

    /// <summary>Ends a wait at once, and takes no instance, so asking can't make the shortage any worse.</summary>
    public static bool PipeExists() => PipeExists(PipeNames.HostPipe());

    internal static bool PipeExists(string name)
    {
        // The Linux Host's sockets are files; one that is left behind is found stale on connecting.
        if (OperatingSystem.IsLinux()) return File.Exists(UnixSockets.PathFor(name));
        if (WaitNamedPipe($@"\\.\pipe\{name}", 1)) return true;
        return Marshal.GetLastPInvokeError() != FileNotFound;
    }

    [DllImport("kernel32.dll", EntryPoint = "WaitNamedPipeW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WaitNamedPipe(string name, uint timeoutMilliseconds);
}

/// <summary>Sorts one input after another on one connection of its own. Closing it cancels whatever it was asking.</summary>
internal interface IRemoteClassifier : IAsyncDisposable
{
    /// <summary>The connection failed or the Host doesn't offer this, so nothing more can be asked here.</summary>
    bool Broken { get; }

    /// <summary>A failure comes back as <c>Ok = false</c> with words for the user. Only the caller's own cancellation throws.</summary>
    Task<RemoteClassifyResult> ClassifyAsync(string text, CancellationToken cancellationToken);
}

/// <summary><c>archive.listResult</c> has no ok or error of its own, and a call can still fail on its way there.</summary>
internal sealed record ArchiveListReply(string? Error, List<ArchiveSetDto> Sets)
{
    public ArchiveListReply(string? error) : this(error, []) { }

    /// <summary>Packages the Host holds an archive password for. Empty from a Host that doesn't say.</summary>
    public List<Guid> PackagesWithPassword { get; init; } = [];

    public bool Ok => Error is null;
}
