using System.Collections.Concurrent;
using DownloadManager.Contracts;
using DownloadManager.Mobile;
using DownloadManager.Runtime.Ipc;

namespace Inbound.App.Host;

/// <summary>
/// A client of the dispatcher in the same process: what a pipe or WebSocket session is, without the transport.
/// The dispatcher registers a session before it sends the snapshot and promises the snapshot before any event (see
/// <see cref="HostCommandDispatcher.OpenSessionAsync(IClientSession, CancellationToken)"/>), so broadcasts that come in
/// before <see cref="GoLive"/> are held and delivered after the snapshot, in order, as a transport's queue would.
/// </summary>
internal sealed class InProcessSession : IClientSession
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<IpcMessage>> _waiting = new();
    private readonly object _gate = new();
    private readonly List<IpcMessage> _held = new();
    private bool _live;
    private bool _closed;
    private Action<IpcMessage>? _onFrame;

    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>Told when the frame callback throws on an event. The exception never reaches the Host's announcing thread.</summary>
    public Action<Exception>? OnError { get; set; }

    /// <summary>
    /// Sets the callback for every frame that is not the reply to a request (hello, snapshot, events). False once the session is
    /// closed: a close that ran before the open must not be undone by it.
    /// </summary>
    public bool Attach(Action<IpcMessage> onFrame)
    {
        lock (_gate)
        {
            if (_closed) return false;
            _onFrame = onFrame;
            return true;
        }
    }

    /// <summary>No frame is delivered after this returns, except one whose delivery had already begun on another thread.</summary>
    public void Close()
    {
        lock (_gate)
        {
            _closed = true;
            _onFrame = null;
            _held.Clear();
        }
    }

    public bool Closed
    {
        get { lock (_gate) return _closed; }
    }

    private Action<IpcMessage>? Callback()
    {
        lock (_gate) return _closed ? null : _onFrame;
    }

    private void Deliver(IpcMessage message)
    {
        try { Callback()?.Invoke(message); }
        catch (Exception e) { OnError?.Invoke(e); }
    }

    /// <summary>Broadcasts (events). Held until <see cref="GoLive"/>; replies are never held.</summary>
    public bool Enqueue(IpcMessage message)
    {
        // A reply goes to the request that waits for it, and nowhere else: one whose request already gave up is dropped.
        if (message.ReplyTo is { } to)
        {
            if (_waiting.TryRemove(to, out var tcs)) tcs.TrySetResult(message);
            return true;
        }

        lock (_gate)
        {
            if (_closed) return true;
            if (!_live)
            {
                _held.Add(message);
                return true;
            }
        }

        Deliver(message);
        return true;
    }

    /// <summary>
    /// The hello and the snapshot (and replies sent directly): delivered at once, on the opening thread. A callback that
    /// throws here is not caught: the open fails, so the screen shows that the Host could not be reached rather than an
    /// empty list that never fills.
    /// </summary>
    public Task SendDirectAsync(IpcMessage message, CancellationToken cancellationToken)
    {
        if (message.ReplyTo is { } to)
        {
            if (_waiting.TryRemove(to, out var tcs)) tcs.TrySetResult(message);
        }
        else Callback()?.Invoke(message);
        return Task.CompletedTask;
    }

    /// <summary>After the snapshot: delivers what was held, in order, then lets events through.</summary>
    public void GoLive()
    {
        while (true)
        {
            IpcMessage[] batch;
            lock (_gate)
            {
                if (_held.Count == 0 || _closed)
                {
                    _held.Clear();
                    _live = true;
                    return;
                }

                batch = [.. _held];
                _held.Clear();
            }

            foreach (var m in batch) Deliver(m);
        }
    }

    public Task<IpcMessage> ExpectReplyAsync(IpcMessage request)
    {
        var tcs = new TaskCompletionSource<IpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiting[request.Id] = tcs;
        return tcs.Task;
    }

    /// <summary>A request that timed out, was cancelled or failed to dispatch no longer waits for its reply.</summary>
    public void Forget(IpcMessage request) => _waiting.TryRemove(request.Id, out _);
}

/// <summary><see cref="IHostConnection"/> over an <see cref="InProcessSession"/> and the in-process dispatcher.</summary>
/// <param name="onUserStart">Told before a command of the user that starts downloads (<see cref="CrashGuard.IsUserStart"/>): it lifts a tripped crash guard.</param>
internal sealed class InProcessHostConnection(HostCommandDispatcher dispatcher, Action<Exception>? onError = null, Action? onUserStart = null) : IHostConnection
{
    private readonly InProcessSession _session = new() { OnError = onError };

    /// <summary>Opening a connection that was already disposed does nothing: no session is registered and no frame is delivered.</summary>
    public async Task OpenAsync(Action<IpcMessage> onFrame, CancellationToken cancellationToken)
    {
        if (!_session.Attach(onFrame)) return;
        try { await dispatcher.OpenSessionAsync(_session, cancellationToken).ConfigureAwait(false); }
        catch
        {
            // Registered before the hello and snapshot were sent: a failure there must not leave it registered.
            _session.Close();
            dispatcher.CloseSession(_session);
            throw;
        }

        // Disposed while the dispatcher registered it: that close may have run first, so close it again.
        if (_session.Closed) dispatcher.CloseSession(_session);
        else _session.GoLive();
    }

    /// <summary>One command, the same way a transport does it (compatibility check, then dispatch); waits for its reply.</summary>
    public async Task<IpcMessage> RequestAsync(IpcMessage request, CancellationToken cancellationToken)
    {
        // A closed screen sends nothing to the Host (a command after dispose ran there before).
        ObjectDisposedException.ThrowIf(_session.Closed, this);
        // The user starting downloads is what lifts a tripped crash guard (never a queue start of its own).
        if (onUserStart is not null && CrashGuard.IsUserStart(request.Type)) onUserStart();
        var reply = _session.ExpectReplyAsync(request);
        try
        {
            if (!dispatcher.RejectIfIncompatible(_session, request))
                await dispatcher.DispatchAsync(_session, request, cancellationToken).ConfigureAwait(false);
            return await reply.WaitAsync(TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _session.Forget(request);
            throw;
        }
    }

    /// <summary>The Host is owned by the app, not by one session; closing a session only unregisters it.</summary>
    public ValueTask DisposeAsync()
    {
        _session.Close();
        dispatcher.CloseSession(_session);
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// The screen is built before the Host has started; this opens the in-process session once it has. A command sent before
/// then waits for the session (and fails if the Host could not start). Disposing at any point, even while the Host is
/// still starting, leaves no session registered.
/// </summary>
internal sealed class LazyInProcessConnection(Task<HostCommandDispatcher> host, Action<Exception>? onError = null, Action? onUserStart = null) : IHostConnection
{
    private readonly object _gate = new();
    private readonly TaskCompletionSource<InProcessHostConnection> _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private InProcessHostConnection? _inner;
    private bool _disposed;

    public async Task OpenAsync(Action<IpcMessage> onFrame, CancellationToken cancellationToken)
    {
        InProcessHostConnection? inner = null;
        try
        {
            var started = await host.ConfigureAwait(false);
            lock (_gate)
            {
                if (_disposed) return;
                _inner = inner = new InProcessHostConnection(started, onError, onUserStart);
            }

            await inner.OpenAsync(onFrame, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // A session that failed while opening stays registered until it is closed.
            if (inner is not null) await inner.DisposeAsync().ConfigureAwait(false);
            _opened.TrySetException(e);
            _ = _opened.Task.Exception; // observed here; a waiting RequestAsync still gets it
            throw;
        }

        bool closeNow;
        lock (_gate) closeNow = _disposed;
        // Disposed while the session was being opened: that close may have run before the dispatcher registered it.
        if (closeNow) await inner.DisposeAsync().ConfigureAwait(false);
        else _opened.TrySetResult(inner);
    }

    public async Task<IpcMessage> RequestAsync(IpcMessage request, CancellationToken cancellationToken)
    {
        var inner = await _opened.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        return await inner.RequestAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        InProcessHostConnection? inner;
        lock (_gate)
        {
            _disposed = true;
            inner = _inner;
        }

        _opened.TrySetException(new ObjectDisposedException(nameof(LazyInProcessConnection)));
        _ = _opened.Task.Exception; // observed here: nobody may be waiting
        return inner?.DisposeAsync() ?? ValueTask.CompletedTask;
    }
}
