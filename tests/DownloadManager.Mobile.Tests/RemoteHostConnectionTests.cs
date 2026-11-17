using DownloadManager.Contracts;
using DownloadManager.Mobile.Remote;

namespace DownloadManager.Mobile.Tests;

/// <summary>ANDROID.7: the connection to another Inbound, over a fake socket.</summary>
public sealed class RemoteHostConnectionTests
{
    private static JobDto Job(string name, string state = "downloading", long received = 0, int minute = 0, Guid? id = null)
    {
        var job = Jobs.Make(state, name, total: 1000, received: received, minute: minute, id: id);
        job.UpdatedAt = job.CreatedAt.AddSeconds(received);
        return job;
    }

    [Fact]
    public async Task TheScreenGetsTheHelloThenTheSnapshotThenEventsAndTheServerIsAskedForBoth()
    {
        var a = Job("a.bin");
        var server = new FakeServer().Accepts(a);
        await using var connection = server.Connection();
        var frames = new FrameLog();

        await connection.OpenAsync(frames.Add, CancellationToken.None);

        Assert.Equal(RemoteConnectionStatus.Connected, connection.State.Status);
        Assert.True(server.Hellos[0].Snapshot);
        Assert.True(server.Hellos[0].Events);
        (await server.SocketAsync(1)).Event(Job("a.bin", received: 500, id: a.Id), "progress");
        await frames.WaitAsync(f => f.Length == 3, "the event");
        Assert.Equal([MessageTypes.ServerHello, MessageTypes.JobsSnapshot, MessageTypes.JobEvent], frames.All.Select(m => m.Type));
        Assert.Equal(HostKinds.Server, connection.Hello!.HostKind);
    }

    [Fact]
    public async Task ACommandGoesOverTheSocketAndGetsItsOwnReplyNotAnEvent()
    {
        var server = new FakeServer().Accepts();
        await using var connection = server.Connection();
        var frames = new FrameLog();
        await connection.OpenAsync(frames.Add, CancellationToken.None);
        var socket = await server.SocketAsync(1);
        socket.Answer = request =>
        {
            socket.Event(Job("new.bin"), "added"); // an event in between is not the reply
            return IpcMessage.Create(MessageTypes.JobAddResult, new AddJobResponse { Accepted = true }, request.Id);
        };

        var reply = await connection.RequestAsync(IpcMessage.Create(MessageTypes.JobAdd, new AddJobRequest { Url = "http://x.test/f" }), CancellationToken.None);

        Assert.Equal(MessageTypes.JobAddResult, reply.Type);
        Assert.True(reply.GetPayload<AddJobResponse>().Accepted);
        await frames.WaitAsync(f => f.Any(m => m.Type == MessageTypes.JobEvent), "the added event");
        Assert.DoesNotContain(frames.All, m => m.ReplyTo is not null);
    }

    [Fact]
    public async Task ACallThatWaitsOnTheNetworkGoesAsOnePostNotOverTheSocket()
    {
        var server = new FakeServer().Accepts();
        await using var connection = server.Connection();
        await connection.OpenAsync(_ => { }, CancellationToken.None);

        var request = IpcMessage.Create(MessageTypes.RemoteClassify, new { });
        var reply = await connection.RequestAsync(request, CancellationToken.None);

        Assert.Equal(request.Id, reply.ReplyTo);
        Assert.Single(server.OneShots);
        Assert.Empty((await server.SocketAsync(1)).Sent);
    }

    [Fact]
    public async Task AServerThatCantBeReachedIsAStateNotAFailureAndTheNextTryConnects()
    {
        var server = new FakeServer().Fails(new RemoteServerException("Couldn't reach the server.")).Accepts(Job("a.bin"));
        await using var connection = server.Connection();
        var states = new List<RemoteConnectionStatus>();
        connection.StateChanged += s => { lock (states) states.Add(s.Status); };
        var frames = new FrameLog();

        await connection.OpenAsync(frames.Add, CancellationToken.None); // does not throw

        await frames.WaitAsync(f => f.Any(m => m.Type == MessageTypes.JobsSnapshot), "the snapshot after the retry");
        Assert.Equal(2, server.Attempts);
        lock (states) Assert.Contains(RemoteConnectionStatus.Retrying, states);
        Assert.Equal(RemoteConnectionStatus.Connected, connection.State.Status);
    }

    [Fact]
    public async Task AfterAReconnectTheScreensGetOnlyWhatChangedAsEvents()
    {
        var kept = Job("kept.bin", received: 10, minute: 1);
        var changed = Job("changed.bin", received: 10, minute: 2);
        var gone = Job("gone.bin", minute: 3);
        var server = new FakeServer().Accepts(kept, changed, gone);
        await using var connection = server.Connection();
        var frames = new FrameLog();
        await connection.OpenAsync(frames.Add, CancellationToken.None);
        var first = await server.SocketAsync(1);

        var later = Job("changed.bin", state: "completed", received: 1000, minute: 2, id: changed.Id);
        var added = Job("added.bin", minute: 4);
        server.Accepts(kept, later, added);
        first.Close();

        await frames.WaitAsync(f => f.Count(m => m.Type == MessageTypes.ServerHello) == 2 && f.Count(m => m.Type == MessageTypes.JobEvent) == 3, "the difference");
        Assert.Single(frames.All, m => m.Type == MessageTypes.JobsSnapshot);
        var events = frames.All.Where(m => m.Type == MessageTypes.JobEvent).Select(m => m.GetPayload<JobEventDto>())
            .Select(e => (e.EventType, e.Job.FileName, e.Job.State)).ToArray();
        Assert.Equal([("removed", "gone.bin", "downloading"), (RemoteHostConnection.ResyncEvent, "changed.bin", "completed"), ("added", "added.bin", "downloading")], events);
    }

    [Fact]
    public async Task AJobRemovedByAnEventIsNotRemovedAgainAfterAReconnect()
    {
        var a = Job("a.bin");
        var server = new FakeServer().Accepts(a);
        await using var connection = server.Connection();
        var frames = new FrameLog();
        await connection.OpenAsync(frames.Add, CancellationToken.None);
        var first = await server.SocketAsync(1);
        first.Event(a, "removed");
        await frames.WaitAsync(f => f.Any(m => m.Type == MessageTypes.JobEvent), "the removal");

        server.Accepts();
        first.Close();
        await frames.WaitAsync(f => f.Count(m => m.Type == MessageTypes.ServerHello) == 2, "the second hello");
        await Task.Delay(50);

        Assert.Single(frames.All, m => m.Type == MessageTypes.JobEvent);
    }

    [Fact]
    public async Task ACommandWaitingForItsReplyFailsWhenTheConnectionIsLost()
    {
        var server = new FakeServer().Accepts();
        await using var connection = server.Connection();
        await connection.OpenAsync(_ => { }, CancellationToken.None);
        var socket = await server.SocketAsync(1);

        var pending = connection.RequestAsync(IpcMessage.Create(MessageTypes.JobPause, new JobActionRequest { JobId = Guid.NewGuid() }), CancellationToken.None);
        await Until.TrueAsync(() => socket.Sent.Count == 1, "the request was sent");
        socket.Close();

        var error = await Assert.ThrowsAsync<IOException>(() => pending);
        Assert.Contains("Home server", error.Message);
    }

    [Fact]
    public async Task ACommandSentBeforeTheConnectionIsUpWaitsForIt()
    {
        var server = new FakeServer().Fails(new RemoteServerException("Couldn't reach the server.")).Accepts();
        await using var connection = server.Connection();
        await connection.OpenAsync(_ => { }, CancellationToken.None);
        Assert.NotEqual(RemoteConnectionStatus.Connected, connection.State.Status);

        var send = connection.RequestAsync(IpcMessage.Create(MessageTypes.HostPing, new { }), CancellationToken.None);
        var socket = await server.SocketAsync(1);
        await Until.TrueAsync(() => { lock (socket.Sent) return socket.Sent.Count == 1; }, "the ping was sent on the new socket");
        socket.Push(IpcMessage.Create(MessageTypes.HostActionResult, new HostActionResult { Ok = true }, socket.Sent[0].Id));

        Assert.Equal(MessageTypes.HostActionResult, (await send).Type);
    }

    [Fact]
    public async Task ACommandWhileTheServerStaysAwaySaysSoInWords()
    {
        var server = new FakeServer();
        await using var connection = server.Connection(connectWait: TimeSpan.FromMilliseconds(100));
        await connection.OpenAsync(_ => { }, CancellationToken.None);

        var error = await Assert.ThrowsAsync<RemoteServerException>(() =>
            connection.RequestAsync(IpcMessage.Create(MessageTypes.HostPing, new { }), CancellationToken.None));
        Assert.Equal(RemoteWords.NotConnected("Home server"), error.Message);
    }

    [Fact]
    public async Task ARefusedTokenStopsTryingAndSaysToPairAgain()
    {
        var server = new FakeServer().Fails(new RemotePairingEndedException()).Accepts();
        await using var connection = server.Connection();
        await connection.OpenAsync(_ => { }, CancellationToken.None);

        Assert.Equal(RemoteConnectionStatus.Stopped, connection.State.Status);
        Assert.Contains("connect to it again", connection.State.Message);
        Assert.DoesNotContain("File >", connection.State.Message);
        connection.SetActive(false);
        connection.SetActive(true);
        connection.RetryNow(dropCurrent: true);
        await Task.Delay(150);
        Assert.Equal(1, server.Attempts);
        await Assert.ThrowsAsync<RemoteServerException>(() => connection.RequestAsync(IpcMessage.Create(MessageTypes.HostPing, new { }), CancellationToken.None));
    }

    [Fact]
    public async Task AServerOfAnotherProtocolVersionIsNotUsed()
    {
        var server = new FakeServer().Accepts(s => { s.Hello(version: Protocol.Version + 1); s.Snapshot(); });
        await using var connection = server.Connection();
        var frames = new FrameLog();
        await connection.OpenAsync(frames.Add, CancellationToken.None);

        Assert.Equal(RemoteConnectionStatus.Retrying, connection.State.Status);
        Assert.Contains("version", connection.State.Message);
        Assert.Empty(frames.All);
    }

    [Fact]
    public async Task NotShownMeansNoSocketAndNoTriesAndShownAgainConnectsAtOnce()
    {
        var server = new FakeServer().Accepts(Job("a.bin")).Accepts(Job("a.bin"));
        await using var connection = server.Connection();
        await connection.OpenAsync(_ => { }, CancellationToken.None);
        var first = await server.SocketAsync(1);

        connection.SetActive(false);
        await Until.TrueAsync(() => first.Disposed, "the socket was closed");
        await Until.TrueAsync(() => connection.State.Status == RemoteConnectionStatus.Paused, "paused");
        await Task.Delay(200); // several backoff waits: nothing is tried
        Assert.Equal(1, server.Attempts);

        connection.SetActive(true);
        await server.SocketAsync(2);
        await Until.TrueAsync(() => connection.State.Status == RemoteConnectionStatus.Connected, "connected again");
        Assert.Equal(2, server.Attempts);
    }

    [Fact]
    public async Task WhileConnectedAndIdleNothingIsSentAndNothingIsTried()
    {
        var server = new FakeServer().Accepts(Job("a.bin"));
        await using var connection = server.Connection();
        await connection.OpenAsync(_ => { }, CancellationToken.None);
        var socket = await server.SocketAsync(1);

        await Task.Delay(300);

        Assert.Empty(socket.Sent);
        Assert.Equal(1, server.Attempts);
    }

    [Fact]
    public async Task AfterDisposeNoFrameReachesTheScreenAndNothingIsTried()
    {
        var server = new FakeServer().Accepts(Job("a.bin"));
        var connection = server.Connection();
        var frames = new FrameLog();
        await connection.OpenAsync(frames.Add, CancellationToken.None);
        var socket = await server.SocketAsync(1);
        var before = frames.All.Length;

        await connection.DisposeAsync();
        socket.Event(Job("b.bin"), "added");
        await Task.Delay(100);

        Assert.Equal(before, frames.All.Length);
        Assert.True(socket.Disposed);
        Assert.Equal(1, server.Attempts);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => connection.RequestAsync(IpcMessage.Create(MessageTypes.HostPing, new { }), CancellationToken.None));
    }

    [Fact]
    public async Task AFrameHandlerThatThrowsSkipsThatFrameOnly()
    {
        var server = new FakeServer().Accepts(Job("a.bin"));
        var errors = new List<Exception>();
        await using var connection = server.Connection(onError: e => { lock (errors) errors.Add(e); });
        var frames = new FrameLog();
        await connection.OpenAsync(m =>
        {
            if (m.Type == MessageTypes.JobEvent && m.GetPayload<JobEventDto>().Job.FileName == "bad.bin") throw new InvalidOperationException("screen bug");
            frames.Add(m);
        }, CancellationToken.None);
        var socket = await server.SocketAsync(1);

        socket.Event(Job("bad.bin"), "added");
        socket.Event(Job("good.bin"), "added");

        await frames.WaitAsync(f => f.Any(m => m.Type == MessageTypes.JobEvent), "the good event");
        Assert.Equal(RemoteConnectionStatus.Connected, connection.State.Status);
        lock (errors) Assert.Single(errors);
    }

    [Fact]
    public async Task TheDownloadsModelFollowsTheServerAcrossAReconnect()
    {
        var a = Job("a.bin", received: 100, minute: 1);
        var b = Job("b.bin", minute: 2);
        var server = new FakeServer().Accepts(a, b);
        await using var connection = server.Connection();
        using var model = new DownloadsModel(connection, act => { lock (connection) act(); }, TimeSpan.Zero);
        await model.StartAsync(CancellationToken.None);
        await Until.TrueAsync(() => { lock (connection) return model.Rows.Count == 2; }, "two rows");
        Assert.Equal("/srv/dl", model.DefaultDownloadDirectory);

        server.Accepts(Job("a.bin", state: "completed", received: 1000, minute: 1, id: a.Id), Job("c.bin", minute: 3));
        (await server.SocketAsync(1)).Close();

        await Until.TrueAsync(() =>
        {
            lock (connection) return model.Rows.Select(r => r.Name).SequenceEqual(["c.bin", "a.bin"]) && model.Rows[1].Kind == StatusKind.Done;
        }, "the rows of the second snapshot");
    }

    [Fact]
    public void TheWordsNeverPointAtTheDesktopsMenus()
    {
        foreach (var error in new Exception[] { new RemotePairingEndedException(), new PlainHttpNotConfirmedException(), new RemoteServerException("The server is busy. It will be tried again shortly."), new TimeoutException(), new HttpRequestException() })
        {
            var words = RemoteWords.Failure("Home server", error);
            Assert.DoesNotContain("File >", words);
            Assert.DoesNotContain("again shortly. Trying again", words);
        }
    }
}
