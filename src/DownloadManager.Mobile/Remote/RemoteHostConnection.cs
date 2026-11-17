using System.Collections.Concurrent;
using DownloadManager.Contracts;

namespace DownloadManager.Mobile.Remote;

/// <summary>Where a remote connection is, for the strip on the screens.</summary>
public enum RemoteConnectionStatus
{
    /// <summary>Opening the socket, or reading the hello and the snapshot.</summary>
    Connecting,

    /// <summary>The snapshot is in and events flow.</summary>
    Connected,

    /// <summary>The last try failed or the connection was lost; the next try is waiting (backoff).</summary>
    Retrying,

    /// <summary>The screen is not shown: the socket is closed on purpose and nothing is tried.</summary>
    Paused,

    /// <summary>Trying again can't help (the pairing ended, plain http may not be used, or the certificate is not the trusted one): nothing more is tried.</summary>
    Stopped,
}

/// <summary>A remote connection's state. <see cref="Message"/> is words for the user (never a token or a password).</summary>
public sealed record RemoteConnectionState(RemoteConnectionStatus Status, string? Message);

/// <summary>
/// <see cref="IHostConnection"/> to another Inbound (the Windows app with its web UI on, or a headless server), over the
/// server's existing API: the WebSocket <c>/api/ws?hello=1</c> with the paired <c>remoteClient</c> token
/// (<see cref="WebSocketIpcClient"/>, the desktop's D9 client), and one <c>POST /api/ipc</c> per call that waits on the
/// network (<see cref="SlowTypes"/>, the protocol doc's "One command at a time per connection").
/// <para>
/// The screens see one stream, as from the in-process Host: the hello, the snapshot, then events. After a reconnect the
/// new snapshot is not passed on again (a model takes one snapshot); it is compared with what the screens were shown and
/// the difference comes as job events: <c>removed</c> for a job that is gone, <c>added</c> for a new one and
/// <see cref="ResyncEvent"/> for one that changed meanwhile. The hello is passed on at every connect.
/// </para>
/// <para>
/// Reconnecting waits 2 s, then twice as long each time, up to about a minute (<see cref="ReconnectBackoff"/>). A token the
/// server refuses, or plain http the user didn't allow, stops it for good (<see cref="RemoteConnectionStatus.Stopped"/>).
/// No polling: while connected only the server's pushes and the socket's keep-alive move; while not shown
/// (<see cref="SetActive"/> false) the socket is closed and nothing runs at all.
/// </para>
/// </summary>
public sealed class RemoteHostConnection : IHostConnection, IRemoteStatus
{
    /// <summary>The event type of a job that changed while this phone was not connected (sent after a reconnect).</summary>
    public const string ResyncEvent = "resync";

    /// <summary>Calls that wait on the network: each goes as one <c>POST /api/ipc</c>, so the socket stays quick.</summary>
    public static readonly IReadOnlySet<string> SlowTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        MessageTypes.RemoteAccountGet, MessageTypes.RemoteAccountValidate, MessageTypes.RemoteClassify, MessageTypes.RemoteAdd,
        MessageTypes.RemoteItemGet, MessageTypes.RemoteFiles, MessageTypes.RemoteDeleteRemote, MessageTypes.RemoteStartQueued,
        MessageTypes.RemoteContainerPreview, MessageTypes.RemoteContainerAdd, MessageTypes.RemoteContainerSendToJd,
        MessageTypes.JobRefreshUrl,
    };

    /// <summary>Opens the socket with the hello sent (<see cref="ServerConnector.ConnectAsync"/>).</summary>
    public delegate Task<IIpcConnection> Connector(ServerTarget target, ClientHelloDto hello, CancellationToken cancellationToken);

    /// <summary>One request and its reply on a connection of its own (<see cref="RemoteServerHttp.SendIpcAsync"/>).</summary>
    public delegate Task<IpcMessage> OneShot(ServerTarget target, IpcMessage request, CancellationToken cancellationToken);

    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan SlowReplyTimeout = TimeSpan.FromMinutes(6);

    private readonly ServerTarget _target;
    private readonly Connector _connect;
    private readonly OneShot _sendOnce;
    private readonly ReconnectBackoff _backoff;
    private readonly TimeSpan _connectWait;
    private readonly Action<Exception>? _onError;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<IpcMessage>> _pending = new();
    private readonly TaskCompletionSource _firstAttempt = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // What the screens were shown, by job (the loop's thread only): the base of the comparison after a reconnect.
    private readonly Dictionary<Guid, JobDto> _shown = new();
    private TaskCompletionSource<IIpcConnection> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _session;
    private Action<IpcMessage>? _onFrame;
    private Task? _loop;
    private bool _active = true;
    private bool _stopped;
    private bool _disposed;
    private bool _gotSnapshot;
    private RemoteConnectionState _state = new(RemoteConnectionStatus.Connecting, null);

    /// <param name="target">The paired server (address and token).</param>
    /// <param name="connect">For tests: opens the socket. Default: <see cref="WebSocketIpcClient"/> with a 10 s connect deadline.</param>
    /// <param name="sendOnce">For tests: one request on its own connection. Default: <c>POST /api/ipc</c>.</param>
    /// <param name="backoff">For tests: the waits between tries.</param>
    /// <param name="connectWait">How long a command sent while not connected waits for the connection (default 10 s).</param>
    /// <param name="onError">Told when a frame handler throws; the frame is skipped and the connection goes on.</param>
    public RemoteHostConnection(ServerTarget target, Connector? connect = null, OneShot? sendOnce = null, ReconnectBackoff? backoff = null,
        TimeSpan? connectWait = null, Action<Exception>? onError = null)
    {
        if (target.IsLocal) throw new ArgumentException("A remote connection needs a server.", nameof(target));
        _target = target;
        _connect = connect ?? (async (t, hello, ct) => await ServerConnector.ConnectAsync(t, hello, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false));
        _sendOnce = sendOnce ?? RemoteServerHttp.SendIpcAsync;
        _backoff = backoff ?? new ReconnectBackoff();
        _connectWait = connectWait ?? TimeSpan.FromSeconds(10);
        _onError = onError;
    }

    /// <summary>The server's name as the user saved it.</summary>
    public string HostName => _target.Name;

    public RemoteConnectionState State
    {
        get { lock (_gate) return _state; }
    }

    /// <summary>Raised on the transport's thread whenever <see cref="State"/> changes.</summary>
    public event Action<RemoteConnectionState>? StateChanged;

    /// <summary>The last hello the server sent (its kind and capabilities); null before the first connect.</summary>
    public HostHelloDto? Hello { get; private set; }

    /// <summary>Raised after each connect that got the snapshot, with the server's hello (for "last seen" in the host list).</summary>
    public event Action<HostHelloDto>? Connected;

    /// <summary>
    /// Starts connecting and returns once the first try has ended, whether it worked or not: a server that can't be reached
    /// is a <see cref="State"/> to show, not a failure of the screen. Tries go on in the background.
    /// </summary>
    public async Task OpenAsync(Action<IpcMessage> onFrame, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_onFrame is not null) throw new InvalidOperationException("The connection was already opened.");
            _onFrame = onFrame;
            _loop = Task.Run(() => RunAsync(_stop.Token));
        }

        await _firstAttempt.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// False while the screen is not shown: the socket is closed and nothing is tried (no wake-ups). True again connects at
    /// once, from the first wait. A stopped connection stays stopped.
    /// </summary>
    public void SetActive(bool active)
    {
        lock (_gate)
        {
            if (_active == active) return;
            _active = active;
            _backoff.Reset();
            _session?.Cancel();
            _wake.TrySetResult();
        }
    }

    /// <summary>
    /// The phone's network changed: a connection that is waiting tries now; with <paramref name="dropCurrent"/> a working one is
    /// opened again too (its socket may be on a network that is gone and would only notice much later).
    /// </summary>
    public void RetryNow(bool dropCurrent)
    {
        lock (_gate)
        {
            if (_stopped || !_active) return;
            _backoff.Reset();
            if (dropCurrent || _state.Status != RemoteConnectionStatus.Connected) _session?.Cancel();
            _wake.TrySetResult();
        }
    }

    public async Task<IpcMessage> RequestAsync(IpcMessage request, CancellationToken cancellationToken)
    {
        if (SlowTypes.Contains(request.Type))
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
            deadline.CancelAfter(SlowReplyTimeout);
            return await _sendOnce(_target, request, deadline.Token).ConfigureAwait(false);
        }

        Task<IIpcConnection> ready;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_stopped) throw new RemoteServerException(_state.Message ?? RemoteWords.NotConnected(_target.Name));
            ready = _ready.Task;
        }

        IIpcConnection client;
        try
        {
            client = await ready.WaitAsync(_connectWait, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new RemoteServerException(RemoteWords.NotConnected(_target.Name));
        }

        var completion = new TaskCompletionSource<IpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[request.Id] = completion;
        try
        {
            await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return await completion.Task.WaitAsync(ReplyTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException) when (!Disposed)
        {
            // The socket was closed under the send (a reconnect): the command did not go out.
            throw new IOException(RemoteWords.Lost(_target.Name));
        }
        finally
        {
            _pending.TryRemove(request.Id, out _);
        }
    }

    private bool Disposed
    {
        get { lock (_gate) return _disposed; }
    }

    private enum Outcome { Retry, Asked, Stop }

    private async Task RunAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            Task wake;
            CancellationTokenSource? session = null;
            bool active, stopped;
            lock (_gate)
            {
                _wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                wake = _wake.Task;
                active = _active;
                stopped = _stopped;
                if (active && !stopped) _session = session = CancellationTokenSource.CreateLinkedTokenSource(stop);
            }

            if (stopped) break;
            if (session is null)
            {
                SetState(RemoteConnectionStatus.Paused, null);
                _firstAttempt.TrySetResult();
                try { await wake.WaitAsync(stop).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            Outcome outcome;
            try
            {
                outcome = await SessionAsync(session.Token).ConfigureAwait(false);
            }
            finally
            {
                lock (_gate)
                {
                    if (_session == session) _session = null;
                }

                session.Dispose();
            }

            if (outcome == Outcome.Stop) break;
            if (outcome == Outcome.Asked) continue;
            try
            {
                await wake.WaitAsync(_backoff.Next(), stop).ConfigureAwait(false);
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

    private async Task<Outcome> SessionAsync(CancellationToken token)
    {
        IIpcConnection? client = null;
        try
        {
            SetState(RemoteConnectionStatus.Connecting, null);
            client = await _connect(_target, new ClientHelloDto { Snapshot = true, Events = true }, token).ConfigureAwait(false);
            IpcMessage snapshot;
            using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                handshake.CancelAfter(HandshakeTimeout);
                var greeting = await client.ReadAsync(handshake.Token).ConfigureAwait(false);
                if (greeting?.Type != MessageTypes.ServerHello) throw new InvalidDataException("No hello.");
                if (greeting.V != Protocol.Version) throw new ProtocolMismatchException();
                Hello = TryPayload<HostHelloDto>(greeting) ?? new HostHelloDto();
                Deliver(greeting);
                snapshot = await client.ReadAsync(handshake.Token).ConfigureAwait(false)
                           ?? throw new IOException("Closed before the snapshot.");
                if (snapshot.Type != MessageTypes.JobsSnapshot) throw new InvalidDataException("No snapshot.");
            }

            ShowSnapshot(snapshot);
            lock (_gate)
            {
                _backoff.Reset();
                _ready.TrySetResult(client);
            }

            SetState(RemoteConnectionStatus.Connected, null);
            _firstAttempt.TrySetResult();
            if (Hello is { } hello)
            {
                try { Connected?.Invoke(hello); }
                catch (Exception e) { _onError?.Invoke(e); }
            }

            while (await client.ReadAsync(token).ConfigureAwait(false) is { } message) Handle(message);
            SetState(RemoteConnectionStatus.Retrying, RemoteWords.Lost(_target.Name));
            return Outcome.Retry;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return Outcome.Asked;
        }
        catch (Exception ex) when (ex is RemotePairingEndedException or PlainHttpNotConfirmedException or ServerCertificateRefusedException)
        {
            lock (_gate) _stopped = true;
            SetState(RemoteConnectionStatus.Stopped, RemoteWords.Failure(_target.Name, ex));
            return Outcome.Stop;
        }
        catch (Exception ex)
        {
            SetState(RemoteConnectionStatus.Retrying, RemoteWords.Failure(_target.Name, ex));
            return Outcome.Retry;
        }
        finally
        {
            lock (_gate)
            {
                if (_ready.Task.IsCompleted)
                    _ready = new TaskCompletionSource<IIpcConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (_disposed) _ready.TrySetException(new ObjectDisposedException(nameof(RemoteHostConnection)));
            }

            FailPending(new IOException(RemoteWords.Lost(_target.Name)));
            if (client is not null)
            {
                try { await client.DisposeAsync().ConfigureAwait(false); }
                catch (Exception e) when (e is IOException or ObjectDisposedException or System.Net.WebSockets.WebSocketException)
                {
                    // Closing is the point, and it is closed now.
                }
            }

            _firstAttempt.TrySetResult();
        }
    }

    /// <summary>The first snapshot goes to the screens as it is; a later one only as the difference to what they show.</summary>
    private void ShowSnapshot(IpcMessage snapshot)
    {
        var dto = TryPayload<SnapshotDto>(snapshot);
        if (!_gotSnapshot)
        {
            _gotSnapshot = true;
            if (dto is not null)
                foreach (var job in dto.Jobs) _shown[job.Id] = job;
            Deliver(snapshot);
            return;
        }

        if (dto is null) return;
        var fresh = new Dictionary<Guid, JobDto>();
        foreach (var job in dto.Jobs) fresh[job.Id] = job;
        foreach (var (id, gone) in _shown.ToArray())
        {
            if (fresh.ContainsKey(id)) continue;
            _shown.Remove(id);
            Deliver(JobEvent(gone, "removed"));
        }

        foreach (var job in dto.Jobs.OrderBy(j => j.CreatedAt))
        {
            if (_shown.TryGetValue(job.Id, out var known))
            {
                _shown[job.Id] = job;
                if (Same(known, job)) continue;
                Deliver(JobEvent(job, ResyncEvent));
            }
            else
            {
                _shown[job.Id] = job;
                Deliver(JobEvent(job, "added"));
            }
        }
    }

    private static bool Same(JobDto a, JobDto b) =>
        a.UpdatedAt == b.UpdatedAt && a.State == b.State && a.ReceivedBytes == b.ReceivedBytes && a.TotalBytes == b.TotalBytes
        && a.FileName == b.FileName && a.StatusNote == b.StatusNote;

    private static IpcMessage JobEvent(JobDto job, string type) =>
        IpcMessage.Create(MessageTypes.JobEvent, new JobEventDto { Job = job, EventType = type });

    private void Handle(IpcMessage message)
    {
        if (message.ReplyTo is { } to)
        {
            if (_pending.TryRemove(to, out var waiting)) waiting.TrySetResult(message);
            return;
        }

        if (message.Type == MessageTypes.JobEvent && TryPayload<JobEventDto>(message) is { Job: { } job } e)
        {
            if (e.EventType.Equals("removed", StringComparison.OrdinalIgnoreCase) || e.EventType.Equals("deleted", StringComparison.OrdinalIgnoreCase))
                _shown.Remove(job.Id);
            else _shown[job.Id] = job;
        }

        Deliver(message);
    }

    private void Deliver(IpcMessage message)
    {
        Action<IpcMessage>? onFrame;
        lock (_gate) onFrame = _disposed ? null : _onFrame;
        if (onFrame is null) return;
        try { onFrame(message); }
        catch (Exception e) { _onError?.Invoke(e); }
    }

    private T? TryPayload<T>(IpcMessage message) where T : class
    {
        try { return message.GetPayload<T>(); }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidDataException or NotSupportedException)
        {
            _onError?.Invoke(e);
            return null;
        }
    }

    private void SetState(RemoteConnectionStatus status, string? message)
    {
        RemoteConnectionState state;
        lock (_gate)
        {
            if (_disposed) return;
            if (_state.Status == status && _state.Message == message) return;
            _state = state = new RemoteConnectionState(status, message);
        }

        try { StateChanged?.Invoke(state); }
        catch (Exception e) { _onError?.Invoke(e); }
    }

    private void FailPending(Exception error)
    {
        foreach (var key in _pending.Keys)
            if (_pending.TryRemove(key, out var waiting)) waiting.TrySetException(error);
    }

    public async ValueTask DisposeAsync()
    {
        Task? loop;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            loop = _loop;
            _ready.TrySetException(new ObjectDisposedException(nameof(RemoteHostConnection)));
            _ = _ready.Task.Exception; // observed: nobody may be waiting
        }

        _stop.Cancel();
        _firstAttempt.TrySetResult();
        if (loop is not null)
        {
            try { await loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        FailPending(new ObjectDisposedException(nameof(RemoteHostConnection)));
        _stop.Dispose();
    }
}

/// <summary>The server speaks another protocol version than this app.</summary>
public sealed class ProtocolMismatchException() : IOException("Another protocol version.");

/// <summary>The words the phone shows for a remote connection. They never hold a token or a password.</summary>
public static class RemoteWords
{
    public static string NotConnected(string name) => $"Not connected to {name}. Try again when it is connected.";

    public static string Lost(string name) => $"Lost the connection to {name}. Trying again shortly.";

    /// <summary>What a failed try says. The desktop's own words point at its File menu, so the phone has its own.</summary>
    public static string Failure(string name, Exception ex) => ex switch
    {
        RemotePairingEndedException => $"{name} no longer accepts this phone. Remove it from the host list and connect to it again.",
        ServerCertificateRefusedException => $"{name} showed a certificate that isn't the one this phone trusts for it. If it has a new certificate, remove it from the host list and connect to it again.",
        PlainHttpNotConfirmedException => $"{name}'s address starts with http://, and sending its sign-in unencrypted wasn't allowed. Connect to it again to allow that.",
        ProtocolMismatchException => $"{name} runs a version of Inbound this app can't talk to. Update both to the same release.",
        RemoteServerException => ex.Message.Contains("again", StringComparison.Ordinal) ? ex.Message : ex.Message + " Trying again shortly.",
        InvalidDataException => $"{name} didn't answer like an Inbound server. Trying again shortly.",
        OperationCanceledException or TimeoutException => $"{name} didn't answer in time. Trying again shortly.",
        _ => $"Couldn't reach {name}. Trying again shortly.",
    };
}
