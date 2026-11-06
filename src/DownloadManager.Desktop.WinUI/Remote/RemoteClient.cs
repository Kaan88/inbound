using System.Collections.Concurrent;
using System.Text.Json;
using DownloadManager.Contracts;

namespace DownloadManager.Desktop.WinUI;

/// <summary>The little this client needs from a connection, so it can be tested without a real pipe.</summary>
internal interface IRemotePipe : IAsyncDisposable
{
    Task SendAsync(IpcMessage message, CancellationToken cancellationToken);
    Task<IpcMessage?> ReadAsync(CancellationToken cancellationToken);
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
internal sealed class RemoteClient : IAsyncDisposable
{
    private const int MostSlowCalls = 3;

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan EventConnectTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DefaultQuickTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultSlowTimeout = TimeSpan.FromSeconds(90);

    private readonly Action<Action> _post;
    private readonly Func<TimeSpan, CancellationToken, Task<IRemotePipe>> _connect;
    private readonly TimeSpan _reconnectDelay;
    private readonly TimeSpan _quickTimeout;
    private readonly TimeSpan _slowTimeout;
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<IpcMessage>> _pending = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    // The Host has eight pipe instances for every client on the machine, so these wait their turn.
    private readonly SemaphoreSlim _slowCalls = new(MostSlowCalls, MostSlowCalls);
    private readonly TaskCompletionSource _firstRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _sync = new();

    private TaskCompletionSource<IRemotePipe> _control = NewControlSource();
    private Task? _loop;

    /// <param name="post">Runs an action on the UI thread. State only ever changes through this.</param>
    public RemoteClient(
        Action<Action> post,
        Func<TimeSpan, CancellationToken, Task<IRemotePipe>>? connect = null,
        TimeSpan? reconnectDelay = null,
        TimeSpan? quickTimeout = null,
        TimeSpan? slowTimeout = null)
    {
        _post = post;
        _connect = connect ?? ConnectToHostAsync;
        _reconnectDelay = reconnectDelay ?? TimeSpan.FromSeconds(2);
        _quickTimeout = quickTimeout ?? DefaultQuickTimeout;
        _slowTimeout = slowTimeout ?? DefaultSlowTimeout;
    }

    public RemoteState State { get; } = new();

    /// <summary>Starts the event connection. A call does this by itself if it comes first.</summary>
    public void Start()
    {
        lock (_sync) _loop ??= Task.Run(() => RunAsync(_stopping.Token));
    }

    /// <summary>Waits for what the first connection says, or the time given, whichever is first.</summary>
    public async Task WaitUntilReadyAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        Start();
        try
        {
            await _firstRead.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
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
        => Feed(await QuickAsync(MessageTypes.RemoteAccountSetKey, new RemoteSetKeyRequest { Provider = provider, Key = key },
            MessageTypes.RemoteAccountResult, FailedAccount, cancellationToken).ConfigureAwait(false));

    public async Task<RemoteAccountResult> ClearKeyAsync(string? provider = null, CancellationToken cancellationToken = default)
        => Feed(await QuickAsync(MessageTypes.RemoteAccountClearKey, new RemoteAccountRequest { Provider = provider },
            MessageTypes.RemoteAccountResult, FailedAccount, cancellationToken).ConfigureAwait(false));

    public async Task<RemoteAccountResult> ValidateAsync(string? provider = null, CancellationToken cancellationToken = default)
        => Feed(await SlowAsync(MessageTypes.RemoteAccountValidate, new RemoteAccountRequest { Provider = provider },
            MessageTypes.RemoteAccountResult, FailedAccount, cancellationToken).ConfigureAwait(false));

    // ---- remote.classify, add, list, item, files, select, retry, checkNow, remove, deleteRemote ----

    public Task<RemoteClassifyResult> ClassifyAsync(string text, CancellationToken cancellationToken = default)
        => SlowAsync(MessageTypes.RemoteClassify, new RemoteClassifyRequest { Text = text }, MessageTypes.RemoteClassifyResult,
            error => new RemoteClassifyResult { Error = error }, cancellationToken);

    public async Task<RemoteAddResultDto> AddAsync(RemoteAddRequestDto request, CancellationToken cancellationToken = default)
    {
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

    /// <summary>Deletes the item from the provider for good. Ask the user first.</summary>
    public async Task<RemoteActionResultDto> DeleteRemoteAsync(Guid jobId, bool confirmed, CancellationToken cancellationToken = default)
        => Feed(await SlowAsync(MessageTypes.RemoteDeleteRemote, new RemoteDeleteRequest { JobId = jobId, Confirmed = confirmed },
            MessageTypes.RemoteActionResult, FailedAction, cancellationToken).ConfigureAwait(false));

    // ---- archive.* ----------------------------------------------------------------------------

    public Task<ArchiveSettingsResult> GetArchiveSettingsAsync(CancellationToken cancellationToken = default)
        => QuickAsync(ArchiveMessageTypes.SettingsGet, new { }, ArchiveMessageTypes.SettingsResult, FailedSettings, cancellationToken);

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

    private static ArchiveListReply ReadArchiveList(IpcMessage reply) => reply.Type == ArchiveMessageTypes.ListResult
        ? new ArchiveListReply(null, reply.GetPayload<ArchiveListResult>().Sets)
        : new ArchiveListReply(RemoteText.Failure(reply.GetPayload<ArchiveActionResult>().Error));

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
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);
        deadline.CancelAfter(_quickTimeout);
        try
        {
            Start();
            var reply = await RequestOnControlAsync(type, request, deadline.Token).ConfigureAwait(false);
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

    private async Task<TResult> SlowAsync<TRequest, TResult>(string type, TRequest request, string replyType, Func<string, TResult> failed, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);
        deadline.CancelAfter(_slowTimeout);
        var connected = false;
        var holdsTurn = false;
        try
        {
            await _slowCalls.WaitAsync(deadline.Token).ConfigureAwait(false);
            holdsTurn = true;
            await using var pipe = await _connect(ConnectTimeout, deadline.Token).ConfigureAwait(false);
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

    private static bool IsCommunicationFailure(Exception ex) => ex is
        OperationCanceledException or TimeoutException or IOException or ObjectDisposedException or
        UnauthorizedAccessException or InvalidOperationException or JsonException or InvalidDataException;

    private static string DescribeFailure(Exception ex, bool reachedHost) => ex switch
    {
        OperationCanceledException when reachedHost => RemoteText.HostTooSlow,
        OperationCanceledException or TimeoutException => RemoteText.HostNotRunning,
        InvalidDataException or JsonException => RemoteText.HostUnreadable,
        _ => reachedHost ? RemoteText.HostLost : RemoteText.HostNotRunning,
    };

    private async Task<IpcMessage> RequestOnControlAsync<T>(string type, T payload, CancellationToken cancellationToken)
    {
        // A Host that is down gets said so soon, rather than after the whole time a reply may take.
        var pipe = await ControlSource().Task.WaitAsync(ConnectTimeout, cancellationToken).ConfigureAwait(false);
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

    private TaskCompletionSource<IRemotePipe> ControlSource()
    {
        lock (_sync) return _control;
    }

    private static TaskCompletionSource<IRemotePipe> NewControlSource() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    // ---- The event connection -------------------------------------------------------------------

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            IRemotePipe? pipe = null;
            var published = false;
            try
            {
                pipe = await _connect(EventConnectTimeout, cancellationToken).ConfigureAwait(false);
                var hello = await pipe.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (hello?.Type != MessageTypes.ServerHello || hello.V != Protocol.Version)
                {
                    throw new InvalidDataException("The Host did not send a usable greeting.");
                }

                var remote = hello.GetPayload<HostHelloDto>().Capabilities.Contains("remote", StringComparer.Ordinal);

                // The snapshot comes before any event, and this client has no use for it.
                _ = await pipe.ReadAsync(cancellationToken).ConfigureAwait(false);

                lock (_sync) _control.TrySetResult(pipe);
                published = true;
                _post(() => State.IsConnected = true);

                // Not awaited: its replies arrive through the loop below.
                _ = SeedAsync(remote, cancellationToken);

                while (await pipe.ReadAsync(cancellationToken).ConfigureAwait(false) is { } message)
                {
                    Dispatch(message);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (IsCommunicationFailure(ex))
            {
                // Comes back around. Whoever is waiting on a call hears about it below.
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

            try
            {
                await Task.Delay(_reconnectDelay, cancellationToken).ConfigureAwait(false);
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
            _firstRead.TrySetResult();
        }
    }

    private async Task SeedRemoteAsync(bool remote, CancellationToken cancellationToken)
    {
        if (!remote)
        {
            _post(() =>
            {
                State.RemoteSupported = false;
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
            });
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
        => new HostPipe(await IpcClient.ConnectAsync(timeout, cancellationToken).ConfigureAwait(false));

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

    private sealed class HostPipe(IpcClient client) : IRemotePipe
    {
        public Task SendAsync(IpcMessage message, CancellationToken cancellationToken) => client.SendAsync(message, cancellationToken);

        public Task<IpcMessage?> ReadAsync(CancellationToken cancellationToken) => client.ReadAsync(cancellationToken);

        public ValueTask DisposeAsync() => client.DisposeAsync();
    }
}

/// <summary><c>archive.listResult</c> has no ok or error of its own, and a call can still fail on its way there.</summary>
internal sealed record ArchiveListReply(string? Error, List<ArchiveSetDto> Sets)
{
    public ArchiveListReply(string? error) : this(error, []) { }

    public bool Ok => Error is null;
}
