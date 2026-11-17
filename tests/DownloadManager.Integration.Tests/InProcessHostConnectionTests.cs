using System.Reflection;
using System.Collections.Concurrent;
using DownloadManager.Contracts;
using DownloadManager.Core;
using DownloadManager.Persistence;
using DownloadManager.Runtime;
using DownloadManager.Runtime.Ipc;
using Inbound.App.Host;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// The phone app's in-process Host connection (linked from DownloadManager.Android, which has no Android types in this file)
/// against a real dispatcher over a real service. Nothing here touches the network: downloads are added with startMode "later".
/// </summary>
public sealed class InProcessHostConnectionTests : IAsyncLifetime
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(20);

    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "test-work", "inproc-" + Guid.NewGuid().ToString("N"));
    private readonly List<IAsyncDisposable> _cleanup = [];
    private StoragePaths _paths = null!;
    private HttpDownloadEngine? _engine;
    private DownloadManagerService _service = null!;
    private HostCommandDispatcher _dispatcher = null!;

    public async Task InitializeAsync()
    {
        _paths = new StoragePaths(Path.Combine(_root, "state"));
        _paths.EnsureCreated();
        var store = new JsonJobStore(_paths);
        var checkpoints = new JsonCheckpointStore(_paths);
        _engine = new HttpDownloadEngine(new EngineOptions { MaxAttempts = 1, AllowLocalNetwork = true }, checkpoints: checkpoints);
        _service = new DownloadManagerService(_paths, store, checkpoints, new JsonSettingsStore(_paths), _engine, enableSchedulerTimer: false);
        await _service.InitializeAsync();
        _dispatcher = new HostCommandDispatcher(_service, new TestPlatform(), new ListLog());
        _dispatcher.Start();
    }

    public async Task DisposeAsync()
    {
        foreach (var item in _cleanup) await item.DisposeAsync();
        _dispatcher.Stop();
        await _service.ShutdownAsync();
        _engine?.Dispose();
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
    }

    private T Track<T>(T item) where T : IAsyncDisposable
    {
        _cleanup.Add(item);
        return item;
    }

    private void Announce(string name, string eventType = "updated")
        => _dispatcher.BroadcastJobChanged(new JobChangeEventArgs(new JobDto { Id = Guid.NewGuid(), FileName = name }, eventType));

    private static string NameOf(IpcMessage frame) => frame.GetPayload<JobEventDto>().Job.FileName;

    private static IpcMessage AddLater(string name = "x.bin")
        => IpcMessage.Create(MessageTypes.JobAdd, new AddJobRequest { Url = "http://127.0.0.1:9/" + name, StartMode = "later" });

    // ---- 1. hello, then snapshot, then events ----------------------------------------------------

    [Fact]
    public async Task Open_delivers_the_hello_then_the_snapshot_then_an_event_announced_while_it_was_being_opened()
    {
        var frames = new Frames();
        var connection = Track(new InProcessHostConnection(_dispatcher));

        // The hello is sent after the session is registered, so an event announced from the hello's callback is one the
        // dispatcher has already queued for this session: it must wait for the snapshot.
        await connection.OpenAsync(frame =>
        {
            frames.Add(frame);
            if (frame.Type == MessageTypes.ServerHello) Announce("during-open");
        }, CancellationToken.None);

        await frames.UntilAsync(f => f.Count >= 3);
        Assert.Equal([MessageTypes.ServerHello, MessageTypes.JobsSnapshot, MessageTypes.JobEvent], frames.Types.Take(3));
        Assert.Equal("during-open", NameOf(frames.Snapshot()[2]));
    }

    [Fact]
    public async Task Events_broadcast_from_another_thread_while_opening_come_after_the_snapshot_in_order_and_none_is_lost_after_registration()
    {
        for (var round = 0; round < 40; round++)
        {
            var frames = new Frames();
            var connection = new InProcessHostConnection(_dispatcher);
            var announced = 0;
            using var stop = new CancellationTokenSource();
            // The announcer runs until it is stopped, and the open starts only once it has announced, so every round
            // registers the session in the middle of a stream of events.
            var announcer = Task.Run(() =>
            {
                while (!stop.IsCancellationRequested) Announce("n" + Interlocked.Increment(ref announced));
            });
            SpinWait.SpinUntil(() => Volatile.Read(ref announced) >= 5);
            await connection.OpenAsync(frames.Add, CancellationToken.None);
            var afterOpen = Volatile.Read(ref announced);
            SpinWait.SpinUntil(() => Volatile.Read(ref announced) >= afterOpen + 20);
            stop.Cancel();
            await announcer;
            var last = Volatile.Read(ref announced);
            await frames.UntilAsync(f => f.Count > 2 && NameOf(f[^1]) == "n" + last);

            // Delivered: hello, snapshot, then a run of consecutive numbers that starts after 1 (the session joined late)
            // and ends at the last one announced: nothing lost after registration, nothing before the snapshot.
            var all = frames.Snapshot();
            Assert.Equal(MessageTypes.ServerHello, all[0].Type);
            Assert.Equal(MessageTypes.JobsSnapshot, all[1].Type);
            Assert.All(all.Skip(2), f => Assert.Equal(MessageTypes.JobEvent, f.Type));
            var numbers = all.Skip(2).Select(f => int.Parse(NameOf(f)[1..])).ToList();
            Assert.True(numbers[0] > 1, "the session was registered before the first event");
            Assert.Equal(last, numbers[^1]);
            Assert.Equal(Enumerable.Range(numbers[0], numbers.Count), numbers);

            await connection.DisposeAsync();
        }
    }

    // ---- 2. a command, its reply, its event -------------------------------------------------------

    [Fact]
    public async Task A_request_gets_its_reply_and_the_job_event_it_caused_reaches_the_frame_callback()
    {
        var frames = new Frames();
        var connection = Track(new InProcessHostConnection(_dispatcher));
        await connection.OpenAsync(frames.Add, CancellationToken.None);

        var request = AddLater("a.bin");
        var reply = await connection.RequestAsync(request, CancellationToken.None).WaitAsync(Bound);

        Assert.Equal(MessageTypes.JobAddResult, reply.Type);
        Assert.Equal(request.Id, reply.ReplyTo);
        var added = reply.GetPayload<AddJobResponse>();
        Assert.True(added.Accepted, added.Error);

        await frames.UntilAsync(f => f.Any(m => m.Type == MessageTypes.JobEvent && m.GetPayload<JobEventDto>().Job.Id == added.Job!.Id));
        // The reply went to the waiting request, not to the frame callback.
        Assert.DoesNotContain(frames.Snapshot(), f => f.Type == MessageTypes.JobAddResult);
    }

    [Fact]
    public async Task A_request_on_another_protocol_version_is_answered_server_incompatible_without_being_dispatched()
    {
        var connection = Track(new InProcessHostConnection(_dispatcher));
        await connection.OpenAsync(_ => { }, CancellationToken.None);

        var reply = await connection.RequestAsync(AddLater() with { V = Protocol.Version + 1 }, CancellationToken.None).WaitAsync(Bound);

        Assert.Equal(MessageTypes.ServerIncompatible, reply.Type);
        Assert.Empty(_service.GetSnapshot().Jobs);
    }

    // ---- 3-5. LazyInProcessConnection -------------------------------------------------------------

    [Fact]
    public async Task A_lazy_request_sent_before_the_host_is_up_waits_and_then_succeeds()
    {
        var host = new TaskCompletionSource<HostCommandDispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var frames = new Frames();
        var connection = Track(new LazyInProcessConnection(host.Task));

        var pending = connection.RequestAsync(AddLater("lazy.bin"), CancellationToken.None);
        var opening = connection.OpenAsync(frames.Add, CancellationToken.None);
        // The Host is not up: neither has finished.
        await Task.Delay(150);
        Assert.False(pending.IsCompleted);
        Assert.False(opening.IsCompleted);
        Assert.Empty(frames.Snapshot());

        host.SetResult(_dispatcher);
        await opening.WaitAsync(Bound);
        var reply = await pending.WaitAsync(Bound);

        Assert.Equal(MessageTypes.JobAddResult, reply.Type);
        Assert.True(reply.GetPayload<AddJobResponse>().Accepted);
        Assert.Equal([MessageTypes.ServerHello, MessageTypes.JobsSnapshot], frames.Types.Take(2));
    }

    [Fact]
    public async Task A_lazy_connection_whose_host_faults_throws_from_open_and_fails_pending_and_later_requests_with_that_exception()
    {
        var host = new TaskCompletionSource<HostCommandDispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = Track(new LazyInProcessConnection(host.Task));
        var failure = new InvalidOperationException("the Host could not start");

        var pending = connection.RequestAsync(AddLater(), CancellationToken.None);
        var opening = connection.OpenAsync(_ => { }, CancellationToken.None);
        host.SetException(failure);

        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => opening.WaitAsync(Bound)));
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => pending.WaitAsync(Bound)));
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => connection.RequestAsync(AddLater(), CancellationToken.None).WaitAsync(Bound)));
    }

    [Fact]
    public async Task A_lazy_connection_disposed_before_the_host_is_up_opens_nothing_when_the_host_arrives()
    {
        var host = new TaskCompletionSource<HostCommandDispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var frames = new Frames();
        var connection = new LazyInProcessConnection(host.Task);

        var opening = connection.OpenAsync(frames.Add, CancellationToken.None);
        var pending = connection.RequestAsync(AddLater(), CancellationToken.None);
        await connection.DisposeAsync();
        // A request waiting for the session is told it is gone, not left hanging.
        await Assert.ThrowsAsync<ObjectDisposedException>(() => pending.WaitAsync(Bound));

        host.SetResult(_dispatcher);
        await opening.WaitAsync(Bound);

        Announce("after");
        Announce("after-2");
        Assert.Empty(frames.Snapshot());
        // Nothing was registered, so the service has no work from it either.
        await Assert.ThrowsAsync<ObjectDisposedException>(() => connection.RequestAsync(AddLater(), CancellationToken.None).WaitAsync(Bound));
        Assert.Empty(_service.GetSnapshot().Jobs);
    }

    // ---- 6. disposed while opening or right after ----------------------------------------------------

    [Fact]
    public async Task A_lazy_connection_disposed_from_the_hello_delivers_no_later_frames()
    {
        var frames = new Frames();
        var connection = new LazyInProcessConnection(Task.FromResult(_dispatcher));
        var disposing = Task.CompletedTask;

        await connection.OpenAsync(frame =>
        {
            frames.Add(frame);
            // Dispose from the hello's callback: the session is registered, the snapshot has not been sent.
            if (frame.Type == MessageTypes.ServerHello) disposing = connection.DisposeAsync().AsTask();
        }, CancellationToken.None);
        await disposing;

        Announce("late");
        Announce("later");
        Assert.Equal([MessageTypes.ServerHello], frames.Types);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => connection.RequestAsync(AddLater(), CancellationToken.None).WaitAsync(Bound));
    }

    // The window the lazy connection can hit (review, 2026-11-17): its DisposeAsync runs after the inner connection exists but
    // before the inner OpenAsync attaches the callback. The close must win: no frame, and no session left registered.
    [Fact]
    public async Task A_connection_disposed_before_it_is_opened_stays_closed()
    {
        var frames = new Frames();
        var connection = new InProcessHostConnection(_dispatcher);
        await connection.DisposeAsync();
        await connection.OpenAsync(frames.Add, CancellationToken.None).WaitAsync(Bound);
        Announce("after");
        Assert.Empty(frames.Types);
        Assert.Equal(0, RegisteredSessions());
    }

    private int RegisteredSessions() =>
        ((ConcurrentDictionary<Guid, IClientSession>)typeof(HostCommandDispatcher)
            .GetField("_sessions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_dispatcher)!).Count;

    [Fact]
    public async Task A_connection_disposed_with_events_still_held_for_the_snapshot_delivers_none_of_them()
    {
        var frames = new Frames();
        var connection = new InProcessHostConnection(_dispatcher);
        await connection.OpenAsync(frame =>
        {
            frames.Add(frame);
            if (frame.Type == MessageTypes.ServerHello)
            {
                Announce("held");
                _ = connection.DisposeAsync();
            }
        }, CancellationToken.None);

        Announce("after");
        Assert.Equal([MessageTypes.ServerHello], frames.Types);
    }

    [Fact]
    public async Task No_event_reaches_the_callback_once_an_open_connection_is_disposed()
    {
        var frames = new Frames();
        var connection = new InProcessHostConnection(_dispatcher);
        await connection.OpenAsync(frames.Add, CancellationToken.None);
        Announce("before");
        await frames.UntilAsync(f => f.Any(m => m.Type == MessageTypes.JobEvent));

        await connection.DisposeAsync();
        var count = frames.Snapshot().Count;
        Announce("after");
        Announce("after-2");
        Assert.Equal(count, frames.Snapshot().Count);
    }

    // ---- 7. a throwing callback -----------------------------------------------------------------------

    [Fact]
    public async Task A_frame_callback_that_throws_goes_to_onError_and_neither_stops_later_frames_nor_reaches_the_announcer()
    {
        var errors = new ConcurrentQueue<Exception>();
        var frames = new Frames();
        var connection = Track(new InProcessHostConnection(_dispatcher, errors.Enqueue));
        await connection.OpenAsync(frame =>
        {
            frames.Add(frame);
            if (frame.Type == MessageTypes.JobEvent && NameOf(frame) == "bad") throw new InvalidOperationException("bad event");
        }, CancellationToken.None);

        Assert.Equal([MessageTypes.ServerHello, MessageTypes.JobsSnapshot], frames.Types.Take(2));

        var thrown = Record.Exception(() =>
        {
            Announce("bad");
            Announce("good");
        });
        Assert.Null(thrown);

        await frames.UntilAsync(f => f.Any(m => m.Type == MessageTypes.JobEvent && NameOf(m) == "good"));
        Assert.Equal(["bad event"], errors.Select(e => e.Message));
    }

    [Fact]
    public async Task A_callback_that_throws_on_the_snapshot_fails_the_open_instead_of_leaving_an_empty_list()
    {
        var errors = new ConcurrentQueue<Exception>();
        var connection = Track(new InProcessHostConnection(_dispatcher, errors.Enqueue));
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => connection.OpenAsync(frame =>
        {
            if (frame.Type == MessageTypes.JobsSnapshot) throw new InvalidOperationException("snapshot");
        }, CancellationToken.None));
        Assert.Equal("snapshot", thrown.Message);
        Assert.Empty(errors);   // not swallowed into onError
    }

    [Fact]
    public async Task A_lazy_connection_whose_snapshot_callback_throws_fails_open_and_later_requests()
    {
        var host = new TaskCompletionSource<HostCommandDispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = new LazyInProcessConnection(host.Task);
        host.SetResult(_dispatcher);
        await Assert.ThrowsAsync<InvalidOperationException>(() => connection.OpenAsync(frame =>
        {
            if (frame.Type == MessageTypes.JobsSnapshot) throw new InvalidOperationException("snapshot");
        }, CancellationToken.None));
        var request = IpcMessage.Create(MessageTypes.JobAdd, new AddJobRequest { Url = "http://127.0.0.1:9/x.bin", StartMode = StartModes.Later });
        await Assert.ThrowsAsync<InvalidOperationException>(() => connection.RequestAsync(request, CancellationToken.None));
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task A_callback_that_throws_on_a_held_event_does_not_lose_the_held_events_after_it()
    {
        var errors = new ConcurrentQueue<Exception>();
        var frames = new Frames();
        var connection = Track(new InProcessHostConnection(_dispatcher, errors.Enqueue));
        await connection.OpenAsync(frame =>
        {
            frames.Add(frame);
            if (frame.Type == MessageTypes.ServerHello)
            {
                Announce("bad");
                Announce("one");
                Announce("two");
            }
            else if (frame.Type == MessageTypes.JobEvent && NameOf(frame) == "bad") throw new InvalidOperationException("bad event");
        }, CancellationToken.None);

        Assert.Equal(["bad", "one", "two"], frames.Snapshot().Where(f => f.Type == MessageTypes.JobEvent).Select(NameOf));
        Assert.Single(errors);
    }

    // ---- 8. a cancelled request leaves nothing behind -----------------------------------------------

    [Fact]
    public async Task A_request_whose_wait_is_cancelled_leaves_no_waiting_entry_behind()
    {
        var connection = Track(new InProcessHostConnection(_dispatcher));
        await connection.OpenAsync(_ => { }, CancellationToken.None);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        // Either the cancellation or the reply wins the race; both must leave the table empty.
        try { await connection.RequestAsync(AddLater(), cancelled.Token).WaitAsync(Bound); }
        catch (OperationCanceledException) { /* expected */ }

        Assert.Empty(WaitingEntries(connection));
    }

    [Fact]
    public async Task A_session_forgets_a_request_it_no_longer_waits_for()
    {
        // A request that never gets its reply (the dispatcher is not asked): the entry is there until forgotten, which RequestAsync does on cancel.
        var connection = Track(new InProcessHostConnection(_dispatcher));
        await connection.OpenAsync(_ => { }, CancellationToken.None);
        var session = (InProcessSession)typeof(InProcessHostConnection).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(connection)!;
        var request = AddLater();
        _ = session.ExpectReplyAsync(request);
        Assert.Single(WaitingEntries(connection));

        session.Forget(request);

        Assert.Empty(WaitingEntries(connection));
    }

    private static System.Collections.ICollection WaitingEntries(InProcessHostConnection connection)
    {
        var session = typeof(InProcessHostConnection).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(connection)!;
        return (System.Collections.ICollection)typeof(InProcessSession).GetField("_waiting", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
    }

    // ---- helpers --------------------------------------------------------------------------------------

    private sealed class Frames
    {
        private readonly object _gate = new();
        private readonly List<IpcMessage> _frames = [];
        private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Add(IpcMessage frame)
        {
            TaskCompletionSource signal;
            lock (_gate)
            {
                _frames.Add(frame);
                signal = _changed;
                _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            signal.TrySetResult();
        }

        public List<IpcMessage> Snapshot() { lock (_gate) return [.. _frames]; }

        public List<string> Types => Snapshot().Select(f => f.Type).ToList();

        public async Task UntilAsync(Func<List<IpcMessage>, bool> condition)
        {
            using var deadline = new CancellationTokenSource(Bound);
            while (true)
            {
                Task changed;
                lock (_gate)
                {
                    if (condition(_frames)) return;
                    changed = _changed.Task;
                }

                try { await changed.WaitAsync(deadline.Token); }
                catch (OperationCanceledException) { throw new TimeoutException("The expected frames did not arrive: " + string.Join(", ", Types)); }
            }
        }
    }

    private sealed class TestPlatform : IHostPlatform
    {
        public PlatformCapabilities Capabilities { get; } = PlatformCapabilities.Desktop;
        public void OpenUi(string? page = null) { }
        public void RequestShutdown() { }
        public Task<FolderPickResult> PickFolderAsync(string? initialPath, CancellationToken cancellationToken)
            => Task.FromResult(new FolderPickResult { Ok = true, Cancelled = true });
    }

    private sealed class ListLog : IHostLog
    {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message) { }
    }
}
