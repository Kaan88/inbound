using System.Threading.Channels;
using DownloadManager.Contracts;
using DownloadManager.Desktop.WinUI;

namespace DownloadManager.Core.Tests;

public sealed class RemoteClientTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task AFullPipeIsNotReportedAsAHostThatIsDown()
    {
        var host = new FakeHost { Busy = true };
        await using var client = host.NewClient(new UiQueue());

        var slow = await client.GetItemsAsync(new RemoteItemRequest { Provider = "torbox", Kind = "torrents" });
        var quick = await client.ListAsync();

        Assert.Equal(RemoteText.HostBusy, slow.Error);
        Assert.Equal(RemoteText.HostBusy, quick.Error);
    }

    [Fact]
    [Trait("Platform", "Windows")] // desktop code: Windows paths, Explorer or named pipes
    public async Task APipeWithEveryInstanceTakenStillExistsAndOneNobodyMadeDoesNot()
    {
        var name = "inbound-test-" + Guid.NewGuid().ToString("N");
        Assert.False(HostPipeBusyException.PipeExists(name));

        await using var server = new System.IO.Pipes.NamedPipeServerStream(name, System.IO.Pipes.PipeDirection.InOut, 1, System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous);
        var accepted = server.WaitForConnectionAsync();
        await using var client = new System.IO.Pipes.NamedPipeClientStream(".", name, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
        await client.ConnectAsync(5000);
        await accepted;

        Assert.True(HostPipeBusyException.PipeExists(name));
    }

    [Fact]
    public async Task ShowingAnotherServerEndsThisHostsConnectionAndStartsTheStateOver()
    {
        var here = new FakeHost();
        var there = new FakeHost { SnapshotWithHello = false, Capabilities = ["jobs", "settings"] };
        var ui = new UiQueue();
        await using var client = here.NewClient(ui);
        client.Start();
        await ui.UntilAsync(() => client.State.IsReady && client.State.RemoteSupported);

        await client.RetargetAsync(there.Connector, there.Connector, remote: true);
        ui.Flush();
        Assert.False(client.State.RemoteSupported);
        await ui.UntilAsync(() => client.State.IsReady);

        // The server's socket sent no snapshot, and the client didn't wait for one: it is ready, and talks to the server.
        Assert.All(here.Connections, pipe => Assert.True(pipe.Disposed));
        Assert.False(client.State.RemoteSupported);
        Assert.NotEmpty(there.Connections);
        Assert.False(there.Connections[0].Disposed);
    }

    [Fact]
    public async Task OnlyAnotherServerWhoseHelloListsUploadsIsOfferedTheUploads()
    {
        // This PC's Host reads this PC's files by path, whatever its hello says.
        var here = new FakeHost { Capabilities = ["jobs", "settings", "remote", "uploads"] };
        var offering = new FakeHost { SnapshotWithHello = false, Capabilities = ["jobs", "settings", "remote", "uploads"] };
        var older = new FakeHost { SnapshotWithHello = false, Capabilities = ["jobs", "settings", "remote"] };
        var ui = new UiQueue();
        await using var client = here.NewClient(ui);
        client.Start();
        await ui.UntilAsync(() => client.State.IsReady);
        Assert.False(client.State.UploadsOffered);

        await client.RetargetAsync(offering.Connector, offering.Connector, remote: true);
        await ui.UntilAsync(() => client.State.IsReady && client.State.UploadsOffered);

        await client.RetargetAsync(older.Connector, older.Connector, remote: true);
        ui.Flush();
        Assert.False(client.State.UploadsOffered);
        await ui.UntilAsync(() => client.State.IsReady);
        Assert.False(client.State.UploadsOffered);
    }

    [Fact]
    public async Task AContainerFileThatCantBeSentToAServerIsRefusedHereWithoutAskingAnything()
    {
        var here = new FakeHost();
        // A documentation address (RFC 5737): nothing listens there, and nothing is sent to it.
        var server = ServerTarget.Remote("s1", "Home", new Uri("http://192.0.2.10:8080/"), "ibt_" + new string('a', 43), plainHttpConfirmed: true);
        var there = new FakeHost { SnapshotWithHello = false, Capabilities = ["jobs", "settings", "remote", "uploads"] };
        var ui = new UiQueue();
        await using var client = here.NewClient(ui);
        await client.RetargetAsync(there.Connector, there.Connector, remote: true, server);

        Assert.Equal("Copy the container file to this PC first; a file on a network share isn't sent from here.",
            (await client.PreviewContainerAsync(@"\\nas\share\links.dlc")).Error);
        Assert.Equal("The container file needs its full path on this PC.", (await client.PreviewContainerAsync("links.dlc")).Error);
        var missing = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.rsdf");
        Assert.Equal("That container file isn't on this PC.", (await client.PreviewContainerAsync(missing)).Error);
        var send = await client.SendContainerToJdAsync(missing);
        Assert.Equal("failed", send.Status);
        Assert.Equal("That container file isn't on this PC.", send.Message);
        Assert.Equal(0, there.Received(MessageTypes.RemoteContainerPreview) + there.Received(MessageTypes.RemoteContainerSendToJd));
    }

    // ---- container uploads to another server: retry and a reference that has gone ----------------------

    private const string TokenForTests = "ibt_" + "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    /// <summary>Nothing listens on port 1 of this PC, so an upload there is refused at once.</summary>
    private static ServerTarget NobodyThere() => ServerTarget.Remote("s1", "Home", new Uri("http://127.0.0.1:1/"), TokenForTests, plainHttpConfirmed: true);

    private static string WriteContainer()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "test-work");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"invented-{Guid.NewGuid():N}.rsdf");
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        return path;
    }

    private static string KeyOf(string path)
    {
        var info = new FileInfo(path);
        return RemoteClient.ContainerUploadKey(info.FullName, info.Length, info.LastWriteTimeUtc);
    }

    [Fact]
    public async Task AnUploadThatCouldNotReachTheServerOffersToReadTheFileAgain()
    {
        var path = WriteContainer();
        try
        {
            var there = new FakeHost { SnapshotWithHello = false, Capabilities = ["jobs", "settings", "remote", "uploads"] };
            await using var client = new FakeHost().NewClient(new UiQueue());
            await client.RetargetAsync(there.Connector, there.Connector, remote: true, NobodyThere());

            var reply = await client.ReadContainerAsync(path).WaitAsync(Patience);

            Assert.Equal("Couldn't reach Home.", reply.Result.Error);
            Assert.True(reply.CanReadAgain);
            Assert.True(RemoteText.ContainerRead(reply.Result, "invented.rsdf", canReadAgain: reply.CanReadAgain).CanReadAgain);
            Assert.False(RemoteText.ContainerRead(reply.Result, "invented.rsdf").CanReadAgain);
            Assert.Equal(0, there.Received(MessageTypes.RemoteContainerPreview));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AFileThatCantBeReadHereDoesNotOfferToReadItAgain()
    {
        var there = new FakeHost { SnapshotWithHello = false, Capabilities = ["jobs", "settings", "remote", "uploads"] };
        await using var client = new FakeHost().NewClient(new UiQueue());
        await client.RetargetAsync(there.Connector, there.Connector, remote: true, NobodyThere());

        var missing = await client.ReadContainerAsync(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.rsdf")).WaitAsync(Patience);
        var network = await client.ReadContainerAsync(@"\\nas\share\links.dlc").WaitAsync(Patience);

        Assert.False(missing.CanReadAgain);
        Assert.False(network.CanReadAgain);
    }

    [Fact]
    public void OnlyAnUploadThatTimedOutOrCouldNotReachTheServerCanBeTriedAgain()
    {
        var target = ServerTarget.Remote("s1", "Home", new Uri("http://127.0.0.1:1/"), TokenForTests, plainHttpConfirmed: true);

        Assert.True(RemoteClient.UploadCanBeRetried("Couldn't reach Home.", target));
        Assert.True(RemoteClient.UploadCanBeRetried("Home didn't take the container file in time. Try again.", target));
        Assert.False(RemoteClient.UploadCanBeRetried("Home doesn't take container files from this app. Import it in the server's web UI instead.", target));
        Assert.False(RemoteClient.UploadCanBeRetried("Home couldn't take the file.", target));
        Assert.False(RemoteClient.UploadCanBeRetried("That container file couldn't be read on this PC.", target));
        Assert.False(RemoteClient.UploadCanBeRetried("Couldn't reach Elsewhere.", target));
        Assert.False(RemoteClient.UploadCanBeRetried(null, target));
    }

    [Fact]
    public async Task AKeptReferenceIsSentAsItIsAndKeptWhileTheServerStillHasTheFile()
    {
        var path = WriteContainer();
        try
        {
            var there = new FakeHost { SnapshotWithHello = false, Capabilities = ["jobs", "settings", "remote", "uploads"] };
            there.Answer(MessageTypes.RemoteContainerPreview, MessageTypes.RemoteContainerPreviewResult, new RemoteContainerPreviewResult { Ok = true });
            await using var client = new FakeHost().NewClient(new UiQueue());
            await client.RetargetAsync(there.Connector, there.Connector, remote: true, NobodyThere());
            client.RememberUpload(KeyOf(path), "upload:kept");

            var reply = await client.ReadContainerAsync(path).WaitAsync(Patience);

            Assert.True(reply.Result.Ok);
            Assert.Equal("upload:kept", there.LastPayload(MessageTypes.RemoteContainerPreview)!.Value.GetProperty("path").GetString());
            Assert.True(client.HasUpload(KeyOf(path)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AReferenceTheServerSaysIsGoneIsDroppedAndTheFileIsUploadedOnceMore()
    {
        var path = WriteContainer();
        using var server = new UploadServer();
        try
        {
            var there = new FakeHost { SnapshotWithHello = false, Capabilities = ["jobs", "settings", "remote", "uploads"] };
            var paths = new List<string?>();
            there.AnswerRaw(MessageTypes.RemoteContainerPreview, request =>
            {
                paths.Add(request.Payload!.Value.GetProperty("path").GetString());
                return IpcMessage.Create(MessageTypes.RemoteContainerPreviewResult,
                    paths.Count == 1 ? new RemoteContainerPreviewResult { Error = UploadReferenceText.Gone } : new RemoteContainerPreviewResult { Ok = true },
                    request.Id);
            });
            var target = ServerTarget.Remote("s1", "Home", server.Address, TokenForTests, plainHttpConfirmed: true);
            await using var client = new FakeHost().NewClient(new UiQueue());
            await client.RetargetAsync(there.Connector, there.Connector, remote: true, target);
            client.RememberUpload(KeyOf(path), "upload:stale");

            var reply = await client.ReadContainerAsync(path).WaitAsync(Patience);

            Assert.True(reply.Result.Ok);
            Assert.Equal(["upload:stale", UploadServer.Reference], paths);
            Assert.Equal(1, server.Uploads);
            Assert.True(client.HasUpload(KeyOf(path)));

            // The new reference is the one kept from now on.
            await client.ReadContainerAsync(path).WaitAsync(Patience);
            Assert.Equal(UploadServer.Reference, paths[^1]);
            Assert.Equal(1, server.Uploads);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AGoneReferenceIsDroppedEvenWhenUploadingAgainFails()
    {
        var path = WriteContainer();
        try
        {
            var there = new FakeHost { SnapshotWithHello = false, Capabilities = ["jobs", "settings", "remote", "uploads"] };
            there.Answer(MessageTypes.RemoteContainerPreview, MessageTypes.RemoteContainerPreviewResult, new RemoteContainerPreviewResult { Error = UploadReferenceText.Gone });
            await using var client = new FakeHost().NewClient(new UiQueue());
            await client.RetargetAsync(there.Connector, there.Connector, remote: true, NobodyThere());
            client.RememberUpload(KeyOf(path), "upload:stale");

            var reply = await client.ReadContainerAsync(path).WaitAsync(Patience);

            Assert.Equal(1, there.Received(MessageTypes.RemoteContainerPreview));
            Assert.False(client.HasUpload(KeyOf(path)));
            Assert.Equal("Couldn't reach Home.", reply.Result.Error);
            Assert.True(reply.CanReadAgain);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AFreshUploadTheServerSaysIsGoneIsNotUploadedAgainInALoop()
    {
        var path = WriteContainer();
        using var server = new UploadServer();
        try
        {
            var there = new FakeHost { SnapshotWithHello = false, Capabilities = ["jobs", "settings", "remote", "uploads"] };
            there.Answer(MessageTypes.RemoteContainerPreview, MessageTypes.RemoteContainerPreviewResult, new RemoteContainerPreviewResult { Error = UploadReferenceText.Gone });
            var target = ServerTarget.Remote("s1", "Home", server.Address, TokenForTests, plainHttpConfirmed: true);
            await using var client = new FakeHost().NewClient(new UiQueue());
            await client.RetargetAsync(there.Connector, there.Connector, remote: true, target);

            var reply = await client.ReadContainerAsync(path).WaitAsync(Patience);

            Assert.Equal(UploadReferenceText.Gone, reply.Result.Error);
            Assert.Equal(1, server.Uploads);
            Assert.Equal(1, there.Received(MessageTypes.RemoteContainerPreview));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AReferenceTheServerSaysIsGoneIsUploadedAgainForTheHandOverToJdToo()
    {
        var path = WriteContainer();
        using var server = new UploadServer();
        try
        {
            var there = new FakeHost { SnapshotWithHello = false, Capabilities = ["jobs", "settings", "remote", "uploads"] };
            var paths = new List<string?>();
            there.AnswerRaw(MessageTypes.RemoteContainerSendToJd, request =>
            {
                paths.Add(request.Payload!.Value.GetProperty("path").GetString());
                return IpcMessage.Create(MessageTypes.RemoteContainerSendResult,
                    paths.Count == 1
                        ? new RemoteContainerSendResult { Status = "failed", Message = UploadReferenceText.Gone }
                        : new RemoteContainerSendResult { Ok = true, Status = "sent", Message = "Sent to JDownloader." },
                    request.Id);
            });
            var target = ServerTarget.Remote("s1", "Home", server.Address, TokenForTests, plainHttpConfirmed: true);
            await using var client = new FakeHost().NewClient(new UiQueue());
            await client.RetargetAsync(there.Connector, there.Connector, remote: true, target);
            client.RememberUpload(KeyOf(path), "upload:stale");

            var sent = await client.SendContainerToJdAsync(path).WaitAsync(Patience);

            Assert.True(sent.Ok);
            Assert.Equal(["upload:stale", UploadServer.Reference], paths);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>A stand-in server on this PC that takes a container upload and answers with one reference.</summary>
    private sealed class UploadServer : IDisposable
    {
        public const string Reference = "upload:00000000000000000000000000000001";
        private readonly System.Net.Sockets.TcpListener _listener = new(System.Net.IPAddress.Loopback, 0);
        private int _uploads;

        public UploadServer()
        {
            _listener.Start();
            Address = new Uri($"http://127.0.0.1:{((System.Net.IPEndPoint)_listener.LocalEndpoint).Port}/");
            _ = Task.Run(ServeAsync);
        }

        public Uri Address { get; }

        public int Uploads => Volatile.Read(ref _uploads);

        private async Task ServeAsync()
        {
            while (true)
            {
                System.Net.Sockets.TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync();
                }
                catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ObjectDisposedException or InvalidOperationException)
                {
                    return;
                }

                _ = Task.Run(() => AnswerAsync(client));
            }
        }

        private async Task AnswerAsync(System.Net.Sockets.TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    // Reads the request's head and its whole body, so closing afterwards doesn't reset the connection.
                    var head = new System.Text.StringBuilder();
                    var one = new byte[1];
                    while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal) && await stream.ReadAsync(one) == 1) head.Append((char)one[0]);
                    var length = 0;
                    foreach (var line in head.ToString().Split("\r\n"))
                    {
                        if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(line[15..].Trim());
                    }

                    var body = new byte[length];
                    await stream.ReadExactlyAsync(body);
                    Interlocked.Increment(ref _uploads);
                    var json = System.Text.Encoding.UTF8.GetBytes($"{{\"upload\":\"{Reference}\"}}");
                    var reply = System.Text.Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {json.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(reply);
                    await stream.WriteAsync(json);
                }
                catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or ObjectDisposedException)
                {
                    // The caller gave up; there is nobody to answer.
                }
            }
        }

        public void Dispose() => _listener.Stop();
    }

    [Fact]
    public async Task AHiddenWindowClosesAServersEventConnectionButNotThisHosts()
    {
        var here = new FakeHost();
        var ui = new UiQueue();
        await using var client = here.NewClient(ui);
        client.Start();
        await ui.UntilAsync(() => client.State.IsReady);

        await client.SetEventsWantedAsync(false);
        Assert.False(here.Connections[0].Disposed);
        await client.SetEventsWantedAsync(true);

        var there = new FakeHost { SnapshotWithHello = false };
        await client.RetargetAsync(there.Connector, there.Connector, remote: true);
        await ui.UntilAsync(() => client.State.IsReady);
        await client.SetEventsWantedAsync(false);
        Assert.True(there.Connections[0].Disposed);

        await client.SetEventsWantedAsync(true);
        await there.UntilAsync(() => there.Connections.Count == 2);
    }

    [Fact]
    public async Task AServerThatRefusesTheTokenIsNotTriedAgainUntilRetargetedAndCallsSayWhy()
    {
        var here = new FakeHost();
        var there = new FakeHost { Fail = () => new RemotePairingEndedException() };
        var ui = new UiQueue();
        // With waits this short, a loop that kept trying would try dozens of times below.
        await using var client = here.NewClient(ui, remoteBackoff: new ReconnectBackoff(TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10)));

        await client.RetargetAsync(there.Connector, there.Connector, remote: true);
        await ui.UntilAsync(() => client.State.IsReady);
        await Task.Delay(300);
        Assert.Equal(1, there.Attempts);

        var list = await client.ListAsync();
        Assert.Contains("Pair it again", list.Error);
        var items = await client.GetItemsAsync(new RemoteItemRequest { Provider = "torbox", Kind = "torrents" });
        Assert.Contains("Pair it again", items.Error);
        await client.SetEventsWantedAsync(false);
        await client.SetEventsWantedAsync(true);
        await Task.Delay(100);
        Assert.Equal(1, there.Attempts);

        // Paired again: the new target is tried at once.
        there.Fail = null;
        await client.RetargetAsync(there.Connector, there.Connector, remote: true);
        await there.UntilAsync(() => there.Connections.Count == 1);
        await ui.UntilAsync(() => client.State.IsConnected);
    }

    [Fact]
    public async Task AServerThatIsDownIsTriedLessAndLessOften()
    {
        var here = new FakeHost();
        var there = new FakeHost { Fail = () => new IOException("down") };
        var ui = new UiQueue();
        await using var client = here.NewClient(ui, remoteBackoff: new ReconnectBackoff(TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(5)));

        await client.RetargetAsync(there.Connector, there.Connector, remote: true);
        await Task.Delay(500);

        // 0, 50, 150, 350 ms (then 750): the fixed 10 ms wait this client has for this PC would have made about fifty.
        Assert.InRange(there.Attempts, 2, 6);
    }

    // Audit M11: this PC's Host not running was tried at the fixed wait for ever; now the wait doubles up to five times it.
    [Fact]
    public async Task ThisPcsHostNotRunningIsTriedLessAndLessOften()
    {
        var here = new FakeHost { Fail = () => new IOException("no pipe") };
        var ui = new UiQueue();
        await using var client = new RemoteClient(ui.Post, here.Connector, reconnectDelay: TimeSpan.FromMilliseconds(50));

        client.Start();
        await Task.Delay(500);

        // 0, 50, 150, 350 ms (then 600): the fixed 50 ms wait would have made about ten.
        Assert.InRange(here.Attempts, 2, 6);
    }

    [Fact]
    public async Task AHiddenWindowsCallToAServerGoesAsOneRequestAndLeavesTheEventSocketClosed()
    {
        var here = new FakeHost();
        var there = new FakeHost { SnapshotWithHello = false };
        var ui = new UiQueue();
        await using var client = here.NewClient(ui);
        await client.RetargetAsync(there.Connector, there.Connector, remote: true);
        await ui.UntilAsync(() => client.State.IsReady);
        await client.SetEventsWantedAsync(false);
        var before = there.Connections.Count;

        var list = await client.ListAsync();

        Assert.True(list.Ok, list.Error);
        Assert.Equal(before + 1, there.Connections.Count);
        Assert.All(there.Connections, pipe => Assert.True(pipe.Disposed));
    }

    [Fact]
    public void ATorrentFileForAServerIsReadOnlyFromAFullLocalPathAndUploadedAgainWhenItChanges()
    {
        Assert.Contains("network share", RemoteClient.TorrentPathProblem(@"\\nas\share\a.torrent"));
        Assert.Contains("network share", RemoteClient.TorrentPathProblem("//nas/share/a.torrent"));
        Assert.Contains("full path", RemoteClient.TorrentPathProblem("a.torrent"));
        Assert.Null(RemoteClient.TorrentPathProblem(@"C:\Downloads\a.torrent"));

        var written = new DateTime(2026, 11, 9, 10, 0, 0, DateTimeKind.Utc);
        var key = RemoteClient.UploadKey(@"C:\Downloads\a.torrent", 1200, written);
        Assert.Equal(key, RemoteClient.UploadKey(@"C:\Downloads\a.torrent", 1200, written));
        Assert.NotEqual(key, RemoteClient.UploadKey(@"C:\Downloads\a.torrent", 1201, written));
        Assert.NotEqual(key, RemoteClient.UploadKey(@"C:\Downloads\a.torrent", 1200, written.AddSeconds(1)));
    }

    [Fact]
    public void ATimeoutAfterTheHostWasReachedIsNotReportedAsAHostThatIsDown()
    {
        Assert.Equal(RemoteText.HostTooSlow, RemoteClient.DescribeFailure(new TimeoutException(), reachedHost: true));
        Assert.Equal(RemoteText.HostNotRunning, RemoteClient.DescribeFailure(new TimeoutException(), reachedHost: false));
    }

    [Fact]
    public void ArchiveRepliesReadOnAnotherConnectionAnswerInWords()
    {
        var set = new ArchiveSetDto { Id = Guid.NewGuid(), Name = "archive", State = "downloaded" };

        var package = Guid.NewGuid();
        var listed = RemoteClient.ReadArchiveListReply(IpcMessage.Create(ArchiveMessageTypes.ListResult, new ArchiveListResult { Sets = [set], PackagesWithPassword = [package] }));
        var unknown = RemoteClient.ReadArchiveListReply(IpcMessage.Create(MessageTypes.HostActionResult, new HostActionResult { Ok = false, Message = "Unknown message type." }));
        var saved = RemoteClient.ReadArchiveActionReply(IpcMessage.Create(ArchiveMessageTypes.ActionResult, new ArchiveActionResult { Ok = true, Set = set }));
        var refused = RemoteClient.ReadArchiveActionReply(IpcMessage.Create(MessageTypes.HostActionResult, new HostActionResult { Ok = false, Message = "Unknown message type." }));

        Assert.True(listed.Ok);
        Assert.Equal(set.Id, Assert.Single(listed.Sets).Id);
        Assert.Equal([package], listed.PackagesWithPassword);
        Assert.Equal(RemoteText.HostDoesNotOffer, unknown.Error);
        Assert.Empty(unknown.PackagesWithPassword);
        Assert.True(saved.Ok);
        Assert.Equal(set.Id, saved.Set?.Id);
        Assert.False(refused.Ok);
        Assert.Equal(RemoteText.HostDoesNotOffer, refused.Error);
    }

    [Fact]
    public async Task SavingAKeySaysSoAfterTheAccountIsApplied()
    {
        var host = new FakeHost();
        var ui = new UiQueue();
        await using var client = host.NewClient(ui);
        var heard = new List<string?>();
        var accountsWhenHeard = -1;
        client.State.KeySaved += provider =>
        {
            heard.Add(provider);
            accountsWhenHeard = client.State.Accounts.Count;
        };

        client.Start();
        await ui.UntilAsync(() => client.State.IsReady);
        await client.GetAccountsAsync("torbox");
        await client.ValidateAsync("torbox");
        ui.Flush();
        Assert.Empty(heard);

        await client.SetKeyAsync("typed", "torbox");
        await ui.UntilAsync(() => heard.Count == 1);

        Assert.Equal("torbox", heard[0]);
        Assert.Equal(1, accountsWhenHeard);
    }

    [Fact]
    public async Task AKeyTheHostRefusesToSaveSaysNothingOfTheKind()
    {
        var host = new FakeHost();
        host.Answer(MessageTypes.RemoteAccountSetKey, MessageTypes.RemoteAccountResult, new RemoteAccountResult { Ok = false, Error = "No." });
        var ui = new UiQueue();
        await using var client = host.NewClient(ui);
        var heard = 0;
        client.State.KeySaved += _ => heard++;
        client.Start();
        await ui.UntilAsync(() => client.State.IsReady);

        var result = await client.SetKeyAsync("typed", "torbox");
        ui.Flush();

        Assert.False(result.Ok);
        Assert.Equal(0, heard);
    }

    [Fact]
    public async Task ConnectingReadsAccountsJobsAndArchiveSets()
    {
        var host = new FakeHost();
        var ui = new UiQueue();
        await using var client = host.NewClient(ui);

        client.Start();

        await ui.UntilAsync(() => client.State.IsReady);
        Assert.True(client.State.IsConnected);
        Assert.True(client.State.RemoteSupported);
        Assert.True(client.State.ArchivesSupported);
        Assert.Equal("torbox", Assert.Single(client.State.Accounts).Provider);
        Assert.Equal(host.Job.Id, Assert.Single(client.State.Jobs).Id);
        Assert.Equal(host.Set.Id, Assert.Single(client.State.ArchiveSets).Id);
    }

    [Fact]
    public async Task StateOnlyChangesOnTheUiThread()
    {
        var host = new FakeHost();
        var ui = new UiQueue();
        await using var client = host.NewClient(ui);

        client.Start();
        await host.UntilAsync(() => host.Received(MessageTypes.RemoteList) > 0);
        await Task.Delay(50);

        // Replies have been read, but nothing ran what they posted.
        Assert.False(client.State.IsConnected);
        Assert.Empty(client.State.Accounts);

        await ui.UntilAsync(() => client.State.Accounts.Count == 1);
    }

    [Fact]
    public async Task AHostWithoutRemoteIsNeverAskedAboutIt()
    {
        var host = new FakeHost { Capabilities = ["jobs", "settings"] };
        var ui = new UiQueue();
        await using var client = host.NewClient(ui);

        client.Start();

        await ui.UntilAsync(() => client.State.IsReady);
        Assert.False(client.State.RemoteSupported);
        Assert.Empty(client.State.Accounts);
        Assert.Equal(0, host.Received(MessageTypes.RemoteAccountGet));
        Assert.True(client.State.ArchivesSupported);
    }

    [Fact]
    public async Task AccountsThatCouldNotBeReadKeepEveryLineGoingToTheHost()
    {
        var host = new FakeHost();
        host.Answer(MessageTypes.RemoteAccountGet, MessageTypes.RemoteAccountResult, new RemoteAccountResult { Ok = false, Error = "No." });
        var ui = new UiQueue();
        await using var client = host.NewClient(ui);

        Assert.True(RemoteRules.ProviderAddApplies(client.State));
        client.Start();

        await ui.UntilAsync(() => client.State.IsReady);
        Assert.False(client.State.AccountsKnown);
        Assert.Empty(client.State.Accounts);
        Assert.True(RemoteRules.ProviderAddApplies(client.State));
    }

    [Fact]
    public async Task AHostWithNoRemoteSupportLetsAddingStayOrdinary()
    {
        var host = new FakeHost { Capabilities = ["jobs", "settings"] };
        var ui = new UiQueue();
        await using var client = host.NewClient(ui);

        client.Start();

        await ui.UntilAsync(() => client.State.IsReady);
        Assert.True(client.State.AccountsKnown);
        Assert.False(RemoteRules.ProviderAddApplies(client.State));
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    public async Task AnAccountReadThatWorkedDecidesByTheKey(bool hasKey, bool unreadable, bool expected)
    {
        var host = new FakeHost();
        host.Account.HasKey = hasKey;
        host.Account.KeyUnreadable = unreadable;
        var ui = new UiQueue();
        await using var client = host.NewClient(ui);

        client.Start();

        await ui.UntilAsync(() => client.State.IsReady);
        Assert.True(client.State.AccountsKnown);
        Assert.Equal(expected, RemoteRules.ProviderAddApplies(client.State));
    }

    [Fact]
    public async Task AHostThatDoesNotKnowArchiveMessagesHidesThem()
    {
        var host = new FakeHost { KnowsArchives = false };
        var ui = new UiQueue();
        await using var client = host.NewClient(ui);

        client.Start();

        await ui.UntilAsync(() => client.State.IsReady);
        Assert.False(client.State.ArchivesSupported);
        Assert.True(client.State.RemoteSupported);

        var result = await client.GetArchiveSettingsAsync();

        Assert.False(result.Ok);
        Assert.Equal(RemoteText.HostDoesNotOffer, result.Error);
    }

    [Fact]
    public async Task ASlowCallLeavesTheEventConnectionFree()
    {
        var host = new FakeHost();
        var ui = new UiQueue();
        await using var client = host.NewClient(ui);
        client.Start();
        await ui.UntilAsync(() => client.State.IsReady);
        var answerValidate = host.Hold(MessageTypes.RemoteAccountValidate);

        var validating = client.ValidateAsync();
        await host.UntilAsync(() => host.Received(MessageTypes.RemoteAccountValidate) == 1);

        // A quick call still gets its answer while the slow one is out.
        var settings = await client.GetArchiveSettingsAsync().WaitAsync(Patience);
        Assert.True(settings.Ok);
        Assert.False(validating.IsCompleted);

        Assert.NotSame(host.ConnectionOf(MessageTypes.RemoteAccountValidate), host.ConnectionOf(ArchiveMessageTypes.SettingsGet));
        Assert.Same(host.Connections[0], host.ConnectionOf(ArchiveMessageTypes.SettingsGet));

        answerValidate();
        Assert.True((await validating.WaitAsync(Patience)).Ok);
        await host.UntilAsync(() => host.ConnectionOf(MessageTypes.RemoteAccountValidate).Disposed);
        Assert.False(host.Connections[0].Disposed);
    }

    [Fact]
    public async Task AClassifierAsksManyThingsOnOneConnectionOfItsOwn()
    {
        var host = new FakeHost();
        var ui = new UiQueue();
        await using var client = host.NewClient(ui);
        client.Start();
        await ui.UntilAsync(() => client.State.IsReady);
        var before = host.Connections.Count;

        await using (var classifier = client.OpenClassifier())
        {
            Assert.Equal(before, host.Connections.Count);
            var first = await classifier.ClassifyAsync("one", CancellationToken.None).WaitAsync(Patience);
            var second = await classifier.ClassifyAsync("two", CancellationToken.None).WaitAsync(Patience);

            Assert.True(first.Ok);
            Assert.True(second.Ok);
            Assert.False(classifier.Broken);
            Assert.Equal(before + 1, host.Connections.Count);
            Assert.Equal(2, host.Received(MessageTypes.RemoteClassify));
            Assert.NotSame(host.Connections[0], host.ConnectionOf(MessageTypes.RemoteClassify));
        }

        Assert.True(host.Connections[^1].Disposed);
    }

    [Fact]
    public async Task AClassifierForAddingAsksForFilesOnlyWhereTheyCanBeKnown()
    {
        // For a magnet that is a call to the provider, so nothing else asks for it.
        var host = new FakeHost();
        var ui = new UiQueue();
        await using var client = host.NewClient(ui);
        client.Start();
        await ui.UntilAsync(() => client.State.IsReady);
        const string Magnet = "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567";

        async Task<bool> AskedForFiles(Func<string, Task> classify, string text)
        {
            await classify(text).WaitAsync(Patience);
            return host.LastPayload(MessageTypes.RemoteClassify) is { } payload &&
                   payload.TryGetProperty("withFiles", out var withFiles) && withFiles.GetBoolean();
        }

        await using (var adding = client.OpenClassifier(withFiles: true))
        {
            Assert.True(await AskedForFiles(text => adding.ClassifyAsync(text, CancellationToken.None), Magnet));
            Assert.True(await AskedForFiles(text => adding.ClassifyAsync(text, CancellationToken.None), @"C:\Torrents\pack.torrent"));
            Assert.False(await AskedForFiles(text => adding.ClassifyAsync(text, CancellationToken.None), "https://example.com/file.zip"));
        }

        await using (var plain = client.OpenClassifier())
        {
            Assert.False(await AskedForFiles(text => plain.ClassifyAsync(text, CancellationToken.None), Magnet));
        }

        Assert.True(await AskedForFiles(text => client.ClassifyAsync(text, withFiles: true), Magnet));
        Assert.False(await AskedForFiles(text => client.ClassifyAsync(text), Magnet));
    }

    [Fact]
    public async Task ClosingAClassifierEndsItsCallAndGivesItsTurnBack()
    {
        var host = new FakeHost();
        var ui = new UiQueue();
        await using var client = host.NewClient(ui);
        client.Start();
        await ui.UntilAsync(() => client.State.IsReady);
        host.Hold(MessageTypes.RemoteClassify);
        using var cancel = new CancellationTokenSource();

        var classifier = client.OpenClassifier();
        var asking = classifier.ClassifyAsync("one", cancel.Token);
        await host.UntilAsync(() => host.Received(MessageTypes.RemoteClassify) == 1);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => asking.WaitAsync(Patience));
        Assert.True(classifier.Broken);
        await classifier.DisposeAsync();
        Assert.True(host.ConnectionOf(MessageTypes.RemoteClassify).Disposed);

        // Three more slow calls can run, so the turn it held came back.
        host.Answer(MessageTypes.RemoteFiles, MessageTypes.RemoteFilesResult, new RemoteFilesResultDto { Ok = true });
        var calls = Enumerable.Range(0, 4).Select(_ => client.GetFilesAsync(Guid.NewGuid())).ToArray();
        await Task.WhenAll(calls).WaitAsync(Patience);
        Assert.All(calls, call => Assert.True(call.Result.Ok));
    }

    [Fact]
    public async Task AClassifierWhoseHostGoesAwaySaysSoInWordsAndStops()
    {
        var host = new FakeHost();
        var ui = new UiQueue();
        await using var client = host.NewClient(ui);
        client.Start();
        await ui.UntilAsync(() => client.State.IsReady);
        host.Hold(MessageTypes.RemoteClassify);

        await using var classifier = client.OpenClassifier();
        var asking = classifier.ClassifyAsync("one", CancellationToken.None);
        await host.UntilAsync(() => host.Received(MessageTypes.RemoteClassify) == 1);
        host.ConnectionOf(MessageTypes.RemoteClassify).Drop();
        var result = await asking.WaitAsync(Patience);

        Assert.False(result.Ok);
        Assert.Equal(RemoteText.ClassifyLost, result.Error);
        Assert.True(classifier.Broken);
        Assert.Equal(RemoteText.ClassifyLost, (await classifier.ClassifyAsync("two", CancellationToken.None).WaitAsync(Patience)).Error);
        Assert.Equal(1, host.Received(MessageTypes.RemoteClassify));
    }

    [Fact]
    public async Task AClassifierOnAHostThatDoesntKnowTheMessageStops()
    {
        var host = new FakeHost();
        host.AnswerRaw(MessageTypes.RemoteClassify, request => IpcMessage.Create(MessageTypes.HostActionResult, new HostActionResult { Ok = false, Message = "Unknown message type." }, request.Id));
        var ui = new UiQueue();
        await using var client = host.NewClient(ui);
        client.Start();
        await ui.UntilAsync(() => client.State.IsReady);

        await using var classifier = client.OpenClassifier();
        var result = await classifier.ClassifyAsync("one", CancellationToken.None).WaitAsync(Patience);

        Assert.False(result.Ok);
        Assert.Equal(RemoteText.HostDoesNotOffer, result.Error);
        Assert.True(classifier.Broken);
    }

    [Fact]
    public async Task AClassifierThatCantConnectSaysSo()
    {
        var host = new FakeHost { Refuse = true };
        var ui = new UiQueue();
        await using var client = host.NewClient(ui);

        await using var classifier = client.OpenClassifier();
        var result = await classifier.ClassifyAsync("one", CancellationToken.None).WaitAsync(Patience);

        Assert.False(result.Ok);
        Assert.Equal(RemoteText.HostNotRunning, result.Error);
        Assert.True(classifier.Broken);
    }

    [Theory]
    [InlineData(MessageTypes.RemoteClassify)]
    [InlineData(MessageTypes.RemoteAdd)]
    [InlineData(MessageTypes.RemoteItemGet)]
    [InlineData(MessageTypes.RemoteFiles)]
    [InlineData(MessageTypes.RemoteSelect)]
    [InlineData(MessageTypes.RemoteDeleteRemote)]
    [InlineData(MessageTypes.RemoteAccountValidate)]
    [InlineData(MessageTypes.RemoteAccountGet + "?refresh")]
    public async Task EveryCallThatMayWaitOnTheNetworkGetsAConnectionOfItsOwn(string call)
    {
        var host = new FakeHost();
        var ui = new UiQueue();
        await using var client = host.NewClient(ui);
        client.Start();
        await ui.UntilAsync(() => client.State.IsReady);
        var before = host.Connections.Count;

        switch (call)
        {
            case MessageTypes.RemoteClassify: await client.ClassifyAsync("text"); break;
            case MessageTypes.RemoteAdd: await client.AddAsync(new RemoteAddRequestDto { Input = "input" }); break;
            case MessageTypes.RemoteItemGet: await client.GetItemsAsync(new RemoteItemRequest { Kind = "torrents" }); break;
            case MessageTypes.RemoteFiles: await client.GetFilesAsync(Guid.NewGuid()); break;
            case MessageTypes.RemoteSelect: await client.SelectAsync(new RemoteSelectRequest { JobId = Guid.NewGuid(), All = true }); break;
            case MessageTypes.RemoteDeleteRemote: await client.DeleteRemoteAsync(Guid.NewGuid(), confirmed: true); break;
            case MessageTypes.RemoteAccountValidate: await client.ValidateAsync(); break;
            default: await client.GetAccountsAsync(refresh: true); break;
        }

        Assert.Equal(before + 1, host.Connections.Count);
    }

    [Fact]
    public async Task CallsThatOnlyTouchLocalStateStayOnTheEventConnection()
    {
        var host = new FakeHost();
        var ui = new UiQueue();
        await using var client = host.NewClient(ui);
        client.Start();
        await ui.UntilAsync(() => client.State.IsReady);
        var before = host.Connections.Count;

        await client.GetAccountsAsync();
        await client.SetKeyAsync("typed-key");
        await client.ClearKeyAsync();
        await client.ListAsync();
        await client.RetryAsync(new RemoteRetryRequest { JobId = host.Job.Id });
        await client.CheckNowAsync();
        await client.RemoveAsync(host.Job.Id);
        await client.GetArchiveSettingsAsync();
        await client.SetArchiveSettingsAsync(new ArchiveSettingsUpdate { AutoExtract = true });
        await client.ListArchivesAsync();
        await client.ExtractAsync(new ArchiveExtractRequest { FilePath = "C:\\a.rar" });
        await client.SetArchivePasswordAsync(new ArchiveSetPasswordRequest { SetId = host.Set.Id, Password = "typed" });
        await client.RetryArchiveAsync(host.Set.Id);
        await client.CancelArchiveAsync(host.Set.Id);
        await client.RemoveArchiveAsync(host.Set.Id);

        Assert.Equal(before, host.Connections.Count);
    }

    [Fact]
    public async Task CancellingASlowCallClosesOnlyItsConnection()
    {
        var host = new FakeHost();
        var ui = new UiQueue();
        await using var client = host.NewClient(ui);
        client.Start();
        await ui.UntilAsync(() => client.State.IsReady);
        host.Hold(MessageTypes.RemoteItemGet);
        using var cancel = new CancellationTokenSource();

        var reading = client.GetItemsAsync(new RemoteItemRequest { Kind = "torrents" }, cancel.Token);
        await host.UntilAsync(() => host.Received(MessageTypes.RemoteItemGet) == 1);
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading.WaitAsync(Patience));
        Assert.True(host.ConnectionOf(MessageTypes.RemoteItemGet).Disposed);
        Assert.False(host.Connections[0].Disposed);
        Assert.True((await client.GetArchiveSettingsAsync().WaitAsync(Patience)).Ok);
    }

    [Fact]
    public async Task SlowCallsWaitTheirTurnSoTheHostKeepsPipesForOthers()
    {
        var host = new FakeHost();
        var ui = new UiQueue();
        await using var client = host.NewClient(ui);
        client.Start();
        await ui.UntilAsync(() => client.State.IsReady);
        var answer = host.Hold(MessageTypes.RemoteClassify);

        var calls = Enumerable.Range(0, 6).Select(_ => client.ClassifyAsync("text")).ToList();
        await host.UntilAsync(() => host.Received(MessageTypes.RemoteClassify) == 3);
        await Task.Delay(100);
        Assert.Equal(3, host.Received(MessageTypes.RemoteClassify));

        answer();
        await Task.WhenAll(calls).WaitAsync(Patience);
        Assert.Equal(6, host.Received(MessageTypes.RemoteClassify));
    }

    [Fact]
    public async Task AfterAHostRestartItConnectsAgainAndReadsEverythingAnew()
    {
        var host = new FakeHost();
        var ui = new UiQueue();
        await using var client = host.NewClient(ui);
        client.Start();
        await ui.UntilAsync(() => client.State.IsReady);
        await ui.UntilAsync(() => client.State.Jobs.Count == 1);
        var replaced = new RemoteJobDto { Id = Guid.NewGuid(), Provider = "torbox", State = "waiting", Status = "Waiting" };
        host.Job = replaced;

        host.Connections[0].Drop();

        await ui.UntilAsync(() => host.Connections.Count == 2 && client.State.IsConnected &&
            client.State.Jobs.Count == 1 && client.State.Jobs[0].Id == replaced.Id);
    }

    [Fact]
    public async Task EventsReachTheStateInWhole()
    {
        var host = new FakeHost();
        var ui = new UiQueue();
        await using var client = host.NewClient(ui);
        client.Start();
        await ui.UntilAsync(() => client.State.IsReady && client.State.ArchiveSets.Count == 1);

        var job = new RemoteJobDto { Id = Guid.NewGuid(), Provider = "torbox", State = "waiting", Status = "Processing" };
        host.Connections[0].Push(IpcMessage.Create(MessageTypes.RemoteChanged, new RemoteChangedDto { Change = "job", Job = job }));
        await ui.UntilAsync(() => client.State.Jobs.Any(item => item.Id == job.Id));

        host.Connections[0].Push(IpcMessage.Create(MessageTypes.RemoteChanged, new RemoteChangedDto { Change = "removed", JobId = job.Id }));
        await ui.UntilAsync(() => client.State.Jobs.All(item => item.Id != job.Id));

        host.Connections[0].Push(IpcMessage.Create(MessageTypes.RemoteChanged, new RemoteChangedDto
        {
            Change = "account",
            Account = new RemoteAccountDto { Provider = "torbox", ProviderName = "TorBox", HasKey = true },
        }));
        await ui.UntilAsync(() => client.State.FindAccount("torbox")!.HasKey);

        var set = new ArchiveSetDto { Id = Guid.NewGuid(), Name = "new", State = "waiting" };
        host.Connections[0].Push(IpcMessage.Create(ArchiveMessageTypes.Changed, new ArchiveChangedDto { Set = set }));
        await ui.UntilAsync(() => client.State.ArchiveSets.Any(item => item.Id == set.Id));

        host.Connections[0].Push(IpcMessage.Create(ArchiveMessageTypes.Changed, new ArchiveChangedDto { RemovedId = set.Id }));
        await ui.UntilAsync(() => client.State.ArchiveSets.All(item => item.Id != set.Id));
    }

    [Fact]
    public async Task AMalformedEventDoesNotCostTheConnection()
    {
        var host = new FakeHost();
        var ui = new UiQueue();
        await using var client = host.NewClient(ui);
        client.Start();
        await ui.UntilAsync(() => client.State.IsReady);

        host.Connections[0].Push(new IpcMessage(Protocol.Version, MessageTypes.RemoteChanged, "x", null, null));
        var settings = await client.GetArchiveSettingsAsync().WaitAsync(Patience);

        Assert.True(settings.Ok);
        Assert.Single(host.Connections);
    }

    [Fact]
    public async Task AValidateAnswerFeedsTheStateBecauseTheHostSendsNoEventForIt()
    {
        var host = new FakeHost();
        var ui = new UiQueue();
        await using var client = host.NewClient(ui);
        client.Start();
        await ui.UntilAsync(() => client.State.IsReady);
        host.Account.Plan = "Pro";

        var result = await client.ValidateAsync();

        Assert.True(result.Ok);
        await ui.UntilAsync(() => client.State.FindAccount("torbox")?.Plan == "Pro");
    }

    [Fact]
    public async Task AnOkFalseReplyKeepsItsWordsAndFailureKind()
    {
        var host = new FakeHost();
        host.Answer(MessageTypes.RemoteAccountValidate, MessageTypes.RemoteAccountResult,
            new RemoteAccountResult { Ok = false, Error = "TorBox refused the API key.", FailureKind = "authentication" });
        var ui = new UiQueue();
        await using var client = host.NewClient(ui);

        var result = await client.ValidateAsync();

        Assert.False(result.Ok);
        Assert.Equal("TorBox refused the API key.", result.Error);
        Assert.Equal("authentication", result.FailureKind);
    }

    [Fact]
    public async Task AFailedArchiveSaveReadsAsAFailureEvenInAnotherResultType()
    {
        var host = new FakeHost();
        host.Answer(ArchiveMessageTypes.SettingsSet, ArchiveMessageTypes.ActionResult,
            new ArchiveActionResult { Ok = false, Error = "The change could not be saved: IOException." });
        var ui = new UiQueue();
        await using var client = host.NewClient(ui);

        var result = await client.SetArchiveSettingsAsync(new ArchiveSettingsUpdate { AutoExtract = true });

        Assert.False(result.Ok);
        Assert.Equal("The change could not be saved: IOException.", result.Error);
    }

    [Fact]
    public async Task AnUnknownMessageReplyBecomesPlainWords()
    {
        var host = new FakeHost();
        host.Answer(MessageTypes.RemoteClassify, MessageTypes.HostActionResult,
            new HostActionResult { Ok = false, Message = "Unknown message type 'remote.classify'." });
        var ui = new UiQueue();
        await using var client = host.NewClient(ui);

        var result = await client.ClassifyAsync("text");

        Assert.False(result.Ok);
        Assert.Equal(RemoteText.HostDoesNotOffer, result.Error);
    }

    [Fact]
    public async Task AHostThatIsNotRunningIsAPlainFailureAndNoException()
    {
        var host = new FakeHost { Refuse = true };
        var ui = new UiQueue();
        await using var client = host.NewClient(ui, quickTimeout: TimeSpan.FromMilliseconds(200));

        var slow = await client.ValidateAsync().WaitAsync(Patience);
        var quick = await client.GetArchiveSettingsAsync().WaitAsync(Patience);

        Assert.False(slow.Ok);
        Assert.Equal(RemoteText.HostNotRunning, slow.Error);
        Assert.False(quick.Ok);
        Assert.Equal(RemoteText.HostNotRunning, quick.Error);
    }

    [Fact]
    public async Task LosingTheConnectionDuringACallSaysItMayHaveGoneThrough()
    {
        var host = new FakeHost();
        var ui = new UiQueue();
        await using var client = host.NewClient(ui);
        client.Start();
        await ui.UntilAsync(() => client.State.IsReady);
        host.Hold(MessageTypes.RemoteAdd);
        host.Hold(MessageTypes.RemoteRetry);

        var adding = client.AddAsync(new RemoteAddRequestDto { Input = "input" });
        var retrying = client.RetryAsync(new RemoteRetryRequest { JobId = host.Job.Id });
        await host.UntilAsync(() => host.Received(MessageTypes.RemoteAdd) == 1 && host.Received(MessageTypes.RemoteRetry) == 1);
        host.ConnectionOf(MessageTypes.RemoteAdd).Drop();
        host.Connections[0].Drop();

        Assert.Equal(RemoteText.HostLost, (await adding.WaitAsync(Patience)).Error);
        Assert.Equal(RemoteText.HostLost, (await retrying.WaitAsync(Patience)).Error);
        Assert.Equal(1, host.Received(MessageTypes.RemoteAdd));
    }

    [Fact]
    public async Task AHostThatNeverAnswersTimesOutWithoutHoldingACallForever()
    {
        var host = new FakeHost();
        var ui = new UiQueue();
        await using var client = host.NewClient(ui, slowTimeout: TimeSpan.FromMilliseconds(100));
        host.Hold(MessageTypes.RemoteFiles);

        var result = await client.GetFilesAsync(Guid.NewGuid()).WaitAsync(Patience);

        Assert.False(result.Ok);
        Assert.Equal(RemoteText.HostTooSlow, result.Error);
        Assert.True(host.ConnectionOf(MessageTypes.RemoteFiles).Disposed);
    }

    [Fact]
    public async Task AnUnreadableAnswerIsAPlainFailure()
    {
        var host = new FakeHost();
        host.AnswerRaw(MessageTypes.RemoteClassify, request => new IpcMessage(Protocol.Version, MessageTypes.RemoteClassifyResult, "y", request.Id, null));
        var ui = new UiQueue();
        await using var client = host.NewClient(ui);

        var result = await client.ClassifyAsync("text");

        Assert.False(result.Ok);
        Assert.Equal(RemoteText.HostUnreadable, result.Error);
    }

    [Fact]
    public async Task NoExceptionWordsEverReachAMessage()
    {
        const string key = "the-key-being-saved";
        var host = new FakeHost();
        var ui = new UiQueue();
        await using var client = host.NewClient(ui);
        client.Start();
        await ui.UntilAsync(() => client.State.IsReady);
        host.Connections[0].FailNextSend = new IOException($"could not send {key}");

        var result = await client.SetKeyAsync(key).WaitAsync(Patience);

        Assert.False(result.Ok);
        Assert.DoesNotContain(key, result.Error);
    }

    [Fact]
    public async Task TheKeyGoesToTheHostAndComesBackNowhere()
    {
        const string key = "the-key-being-saved";
        var host = new FakeHost();
        var ui = new UiQueue();
        await using var client = host.NewClient(ui);
        client.Start();
        await ui.UntilAsync(() => client.State.IsReady);

        var result = await client.SetKeyAsync(key).WaitAsync(Patience);

        Assert.True(result.Ok);
        Assert.Equal(key, host.LastPayload(MessageTypes.RemoteAccountSetKey)!.Value.GetProperty("key").GetString());
        await ui.UntilAsync(() => client.State.FindAccount("torbox") is not null);
        var seen = System.Text.Json.JsonSerializer.Serialize(client.State.Accounts);
        Assert.DoesNotContain(key, seen);
    }

    // ---- remote.container.* ----------------------------------------------------------------------

    [Fact]
    public async Task AContainerIsReadOnAConnectionOfItsOwnWhichClosingEnds()
    {
        var host = new FakeHost();
        var ui = new UiQueue();
        await using var client = host.NewClient(ui);
        client.Start();
        await ui.UntilAsync(() => client.State.IsReady);
        host.Hold(MessageTypes.RemoteContainerPreview);
        using var closing = new CancellationTokenSource();

        var read = client.PreviewContainerAsync(@"D:\files\invented.ccf", closing.Token);
        await host.UntilAsync(() => host.Received(MessageTypes.RemoteContainerPreview) == 1);

        Assert.NotSame(host.Connections[0], host.ConnectionOf(MessageTypes.RemoteContainerPreview));
        Assert.False(host.ConnectionOf(MessageTypes.RemoteContainerPreview).Disposed);

        // The main connection isn't held up by it.
        Assert.True((await client.ListAsync().WaitAsync(Patience)).Ok);

        closing.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(Patience));
        await host.UntilAsync(() => host.ConnectionOf(MessageTypes.RemoteContainerPreview).Disposed);
        Assert.False(host.Connections[0].Disposed);
    }

    [Fact]
    public async Task ANetworkPathGoesToTheHostUntouchedAndItsRefusalComesBackInWords()
    {
        const string Path = @"\\files.invalid\share\invented.ccf";
        const string Refusal = "Only files on a drive of this computer can be opened, not ones on a network path.";
        var host = new FakeHost();
        host.Answer(MessageTypes.RemoteContainerPreview, MessageTypes.RemoteContainerPreviewResult, new RemoteContainerPreviewResult { Error = Refusal });
        var ui = new UiQueue();
        await using var client = host.NewClient(ui);

        var result = await client.PreviewContainerAsync(Path).WaitAsync(Patience);
        var view = RemoteText.ContainerRead(result, "invented.ccf");

        Assert.Equal(Path, host.LastPayload(MessageTypes.RemoteContainerPreview)!.Value.GetProperty("path").GetString());
        Assert.False(result.Ok);
        Assert.Equal(Refusal, view.Status);
        Assert.True(view.IsProblem);
        Assert.False(view.CanSendToJd);
        Assert.False(view.CanReadAgain);
    }

    [Fact]
    public async Task ALostReadSaysNothingWasAddedAndOffersToTryAgain()
    {
        var host = new FakeHost();
        var ui = new UiQueue();
        await using var client = host.NewClient(ui, slowTimeout: TimeSpan.FromMilliseconds(100));
        host.Hold(MessageTypes.RemoteContainerPreview);

        var result = await client.PreviewContainerAsync(@"D:\invented.rsdf").WaitAsync(Patience);

        Assert.Equal(RemoteText.ContainerReadLost, result.Error);
        Assert.True(RemoteText.ContainerRead(result, "invented.rsdf").CanReadAgain);
    }

    [Fact]
    public void AReadThatNeverReachedTheHostOffersToTryAgainInItsOwnWords()
    {
        foreach (var words in new[] { RemoteText.HostBusy, RemoteText.HostNotRunning })
        {
            var view = RemoteText.ContainerRead(new RemoteContainerPreviewResult { Error = words }, "invented.rsdf");
            Assert.True(view.CanReadAgain);
            Assert.Equal(words, view.Status);
        }
    }

    [Fact]
    public void OnlyAnUncheckedSiteStopsAnOrdinaryLinkGoingStraightToADirectRequest()
    {
        const string Note = "Fetcher lists a site this app couldn't check this link against, so this link may be one it can handle.";
        var uncheckedLine = new AddLine("https://site.invalid/a") { Result = new RemoteClassifyResult { Ok = true, Kind = "ordinaryLink", Note = Note, CheckIncomplete = true } };
        var staleLine = new AddLine("https://site.invalid/a") { Result = new RemoteClassifyResult { Ok = true, Kind = "ordinaryLink", Note = "The list is old." } };

        Assert.True(uncheckedLine.CheckIncomplete);
        Assert.Equal(AddRoute.Direct, uncheckedLine.Route);
        Assert.False(staleLine.CheckIncomplete);
        Assert.StartsWith(Note, RemoteText.CheckIncompleteWords(uncheckedLine.Result!, "Fetcher"), StringComparison.Ordinal);
        Assert.Contains("Fetcher", RemoteText.CheckIncompleteWords(new RemoteClassifyResult(), "Fetcher"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddingAContainerOutlastsTheOrdinarySlowLimitBecauseRunningOutWouldCancelTheRest()
    {
        var job = new RemoteJobDto { Id = Guid.NewGuid(), Provider = "torbox", State = "intended", ApprovalNeeded = true };
        var host = new FakeHost();
        host.Answer(MessageTypes.RemoteContainerAdd, MessageTypes.RemoteContainerAddResult, new RemoteContainerAddResult
        {
            Ok = true,
            Outcomes = [new RemoteContainerAddOutcomeDto { EntryId = 1, Ok = true, JobId = job.Id, Job = job }],
        });
        var ui = new UiQueue();
        await using var client = host.NewClient(ui, slowTimeout: TimeSpan.FromMilliseconds(50));
        var answer = host.Hold(MessageTypes.RemoteContainerAdd);

        var adding = client.AddFromContainerAsync(new RemoteContainerAddRequest { PreviewId = Guid.NewGuid(), EntryIds = [1] });
        await Task.Delay(300);
        Assert.False(adding.IsCompleted);
        answer();
        var result = await adding.WaitAsync(Patience);

        Assert.True(result.Ok);
        await ui.UntilAsync(() => client.State.Jobs.Any(known => known.Id == job.Id));
    }

    [Fact]
    public async Task JobsLeftOutOfAnAddAreReadFromTheListAndNotTreatedAsFailures()
    {
        var host = new FakeHost();
        var id = host.Job.Id;
        host.Answer(MessageTypes.RemoteContainerAdd, MessageTypes.RemoteContainerAddResult, new RemoteContainerAddResult
        {
            Ok = true,
            JobsLeftOut = true,
            Outcomes = [new RemoteContainerAddOutcomeDto { EntryId = 1, Ok = true, JobId = id }],
        });
        var ui = new UiQueue();
        await using var client = host.NewClient(ui);
        client.Start();
        await ui.UntilAsync(() => client.State.IsReady);
        var listsBefore = host.Received(MessageTypes.RemoteList);

        var result = await client.AddFromContainerAsync(new RemoteContainerAddRequest { PreviewId = Guid.NewGuid(), EntryIds = [1] }).WaitAsync(Patience);

        Assert.True(result.Ok);
        Assert.True(result.JobsLeftOut);
        Assert.Equal(listsBefore + 1, host.Received(MessageTypes.RemoteList));
        ui.Flush();
        Assert.Contains(client.State.Jobs, job => job.Id == id);
    }

    [Fact]
    public async Task AWholeAnswerOfJobsDoesNotReadTheListAgain()
    {
        var host = new FakeHost();
        host.Answer(MessageTypes.RemoteContainerAdd, MessageTypes.RemoteContainerAddResult, new RemoteContainerAddResult { Ok = true, Outcomes = [new RemoteContainerAddOutcomeDto { EntryId = 1, Ok = true, JobId = host.Job.Id, Job = host.Job }] });
        var ui = new UiQueue();
        await using var client = host.NewClient(ui);
        client.Start();
        await ui.UntilAsync(() => client.State.IsReady);
        var listsBefore = host.Received(MessageTypes.RemoteList);

        await client.AddFromContainerAsync(new RemoteContainerAddRequest { PreviewId = Guid.NewGuid(), EntryIds = [1] }).WaitAsync(Patience);

        Assert.Equal(listsBefore, host.Received(MessageTypes.RemoteList));
    }

    [Fact]
    public async Task TheHandOverWaitsLongerThanJDownloadersFiveMinutes()
    {
        Assert.True(RemoteClient.SendToJdTimeout > TimeSpan.FromMinutes(5));

        var host = new FakeHost();
        host.Answer(MessageTypes.RemoteContainerSendToJd, MessageTypes.RemoteContainerSendResult, new RemoteContainerSendResult { Ok = true, Status = "sent", JdAnswered = true, Message = "Sent to JDownloader." });
        var ui = new UiQueue();
        await using var client = host.NewClient(ui, slowTimeout: TimeSpan.FromMilliseconds(50));
        var answer = host.Hold(MessageTypes.RemoteContainerSendToJd);

        var sending = client.SendContainerToJdAsync(@"D:\invented.dlc");
        await Task.Delay(300);
        Assert.False(sending.IsCompleted);
        answer();

        Assert.Equal("sent", (await sending.WaitAsync(Patience)).Status);
    }

    [Fact]
    public async Task CancellingTheHandOverClosesItsConnection()
    {
        var host = new FakeHost();
        var ui = new UiQueue();
        await using var client = host.NewClient(ui);
        host.Hold(MessageTypes.RemoteContainerSendToJd);
        using var stop = new CancellationTokenSource();

        var sending = client.SendContainerToJdAsync(@"D:\invented.dlc", stop.Token);
        await host.UntilAsync(() => host.Received(MessageTypes.RemoteContainerSendToJd) == 1);
        stop.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending.WaitAsync(Patience));
        await host.UntilAsync(() => host.ConnectionOf(MessageTypes.RemoteContainerSendToJd).Disposed);
    }

    [Fact]
    public async Task AHostThatDoesntKnowContainersGetsSaidInWords()
    {
        var host = new FakeHost();
        host.AnswerRaw(MessageTypes.RemoteContainerPreview, request => IpcMessage.Create(MessageTypes.HostActionResult, new HostActionResult { Ok = false, Message = "Unknown message type." }, request.Id));
        host.AnswerRaw(MessageTypes.RemoteContainerSendToJd, request => IpcMessage.Create(MessageTypes.HostActionResult, new HostActionResult { Ok = false, Message = "Unknown message type." }, request.Id));
        var ui = new UiQueue();
        await using var client = host.NewClient(ui);

        Assert.Equal(RemoteText.HostDoesNotOffer, (await client.PreviewContainerAsync(@"D:\invented.ccf").WaitAsync(Patience)).Error);
        var sent = await client.SendContainerToJdAsync(@"D:\invented.ccf").WaitAsync(Patience);
        Assert.False(sent.Ok);
        Assert.Equal("failed", sent.Status);
        Assert.Equal(RemoteText.HostDoesNotOffer, sent.Message);
    }

    /// <summary>What a window's dispatcher queue does: nothing runs until the UI thread gets to it.</summary>
    private sealed class UiQueue
    {
        private readonly Lock _sync = new();
        private readonly Queue<Action> _actions = new();

        public void Post(Action action)
        {
            lock (_sync) _actions.Enqueue(action);
        }

        public void Flush()
        {
            while (true)
            {
                Action action;
                lock (_sync)
                {
                    if (_actions.Count == 0) return;
                    action = _actions.Dequeue();
                }

                action();
            }
        }

        public async Task UntilAsync(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow + Patience;
            while (true)
            {
                Flush();
                if (condition()) return;
                if (DateTime.UtcNow > deadline) Assert.Fail("The expected state never arrived.");
                await Task.Delay(5);
            }
        }
    }

    private sealed class FakeHost
    {
        private readonly Lock _sync = new();
        private readonly List<(string Type, FakePipe Pipe, IpcMessage Message)> _received = [];
        private readonly Dictionary<string, TaskCompletionSource> _held = [];
        private readonly Dictionary<string, Func<IpcMessage, IpcMessage>> _answers = [];

        public List<string> Capabilities { get; set; } = ["jobs", "settings", "remote"];
        public bool KnowsArchives { get; set; } = true;
        public bool Refuse { get; set; }
        public bool Busy { get; set; }

        /// <summary>Each connect throws this, as a server that refuses the token or is down.</summary>
        public Func<Exception>? Fail { get; set; }

        private int _attempts;

        /// <summary>Every connect asked for, including the ones that failed.</summary>
        public int Attempts => Volatile.Read(ref _attempts);

        /// <summary>False: as another server's socket that asked for no snapshot, the hello comes alone.</summary>
        public bool SnapshotWithHello { get; set; } = true;

        public Func<TimeSpan, CancellationToken, Task<IRemotePipe>> Connector => ConnectAsync;
        public List<FakePipe> Connections { get; } = [];
        public RemoteAccountDto Account { get; } = new() { Provider = "torbox", ProviderName = "TorBox", Capabilities = ["account"] };
        public RemoteJobDto Job { get; set; } = new() { Id = Guid.NewGuid(), Provider = "torbox", State = "waiting", Status = "Waiting for TorBox" };
        public ArchiveSetDto Set { get; } = new() { Id = Guid.NewGuid(), Name = "archive", State = "downloaded" };

        public RemoteClient NewClient(UiQueue ui, TimeSpan? quickTimeout = null, TimeSpan? slowTimeout = null, ReconnectBackoff? remoteBackoff = null)
            => new(ui.Post, ConnectAsync, TimeSpan.FromMilliseconds(10), quickTimeout, slowTimeout, remoteBackoff);

        public int Received(string type)
        {
            lock (_sync) return _received.Count(entry => entry.Type == type);
        }

        public FakePipe ConnectionOf(string type)
        {
            lock (_sync) return _received.Last(entry => entry.Type == type).Pipe;
        }

        public System.Text.Json.JsonElement? LastPayload(string type)
        {
            lock (_sync) return _received.Last(entry => entry.Type == type).Message.Payload;
        }

        public async Task UntilAsync(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow + Patience;
            while (!condition())
            {
                if (DateTime.UtcNow > deadline) Assert.Fail("The Host never saw what was expected.");
                await Task.Delay(5);
            }
        }

        /// <summary>Calls of this type go unanswered until the returned action runs.</summary>
        public Action Hold(string type)
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_sync) _held[type] = gate;
            return () => gate.TrySetResult();
        }

        public void Answer<T>(string type, string replyType, T reply)
            => AnswerRaw(type, request => IpcMessage.Create(replyType, reply, request.Id));

        public void AnswerRaw(string type, Func<IpcMessage, IpcMessage> answer)
        {
            lock (_sync) _answers[type] = answer;
        }

        private Task<IRemotePipe> ConnectAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _attempts);
            if (Fail?.Invoke() is { } failure) throw failure;
            if (Refuse) throw new TimeoutException();
            if (Busy) throw new HostPipeBusyException();
            var pipe = new FakePipe(this);
            lock (_sync) Connections.Add(pipe);
            pipe.Push(IpcMessage.Create(MessageTypes.ServerHello, new HostHelloDto { ProtocolVersion = Protocol.Version, Capabilities = Capabilities }));
            if (SnapshotWithHello) pipe.Push(IpcMessage.Create(MessageTypes.JobsSnapshot, new SnapshotDto()));
            return Task.FromResult<IRemotePipe>(pipe);
        }

        public void Receive(FakePipe pipe, IpcMessage request)
        {
            TaskCompletionSource? held;
            lock (_sync)
            {
                _received.Add((request.Type, pipe, request));
                _held.TryGetValue(request.Type, out held);
            }

            _ = Task.Run(async () =>
            {
                if (held is not null) await held.Task;
                pipe.Push(Reply(request));
            });
        }

        private IpcMessage Reply(IpcMessage request)
        {
            lock (_sync)
            {
                if (_answers.TryGetValue(request.Type, out var answer)) return answer(request);
            }

            if (!KnowsArchives && request.Type.StartsWith(ArchiveMessageTypes.Prefix, StringComparison.Ordinal))
            {
                return IpcMessage.Create(MessageTypes.HostActionResult, new HostActionResult { Ok = false, Message = "Unknown message type." }, request.Id);
            }

            return request.Type switch
            {
                MessageTypes.RemoteAccountGet or MessageTypes.RemoteAccountSetKey or MessageTypes.RemoteAccountClearKey or MessageTypes.RemoteAccountValidate
                    => IpcMessage.Create(MessageTypes.RemoteAccountResult, new RemoteAccountResult { Ok = true, Accounts = [Account] }, request.Id),
                MessageTypes.RemoteList => IpcMessage.Create(MessageTypes.RemoteListResult, new RemoteListResult { Ok = true, Jobs = [Job] }, request.Id),
                MessageTypes.RemoteClassify => IpcMessage.Create(MessageTypes.RemoteClassifyResult, new RemoteClassifyResult { Ok = true, Kind = "magnet" }, request.Id),
                MessageTypes.RemoteAdd => IpcMessage.Create(MessageTypes.RemoteAddResult, new RemoteAddResultDto { Ok = true }, request.Id),
                MessageTypes.RemoteItemGet => IpcMessage.Create(MessageTypes.RemoteItemResult, new RemoteItemResult { Ok = true }, request.Id),
                MessageTypes.RemoteFiles => IpcMessage.Create(MessageTypes.RemoteFilesResult, new RemoteFilesResultDto { Ok = true }, request.Id),
                MessageTypes.RemoteSelect or MessageTypes.RemoteRetry or MessageTypes.RemoteCheckNow or MessageTypes.RemoteRemove or MessageTypes.RemoteDeleteRemote
                    => IpcMessage.Create(MessageTypes.RemoteActionResult, new RemoteActionResultDto { Ok = true }, request.Id),
                ArchiveMessageTypes.SettingsGet or ArchiveMessageTypes.SettingsSet
                    => IpcMessage.Create(ArchiveMessageTypes.SettingsResult, new ArchiveSettingsResult { Ok = true }, request.Id),
                ArchiveMessageTypes.List => IpcMessage.Create(ArchiveMessageTypes.ListResult, new ArchiveListResult { Sets = [Set] }, request.Id),
                ArchiveMessageTypes.Extract => IpcMessage.Create(ArchiveMessageTypes.ExtractResult, new ArchiveExtractResult { Ok = true }, request.Id),
                _ => IpcMessage.Create(ArchiveMessageTypes.ActionResult, new ArchiveActionResult { Ok = true }, request.Id),
            };
        }
    }

    private sealed class FakePipe(FakeHost host) : IRemotePipe
    {
        private readonly Channel<IpcMessage?> _toClient = Channel.CreateUnbounded<IpcMessage?>();

        public bool Disposed { get; private set; }

        public bool SnapshotFollowsHello => host.SnapshotWithHello;

        public Exception? FailNextSend { get; set; }

        public void Push(IpcMessage message) => _toClient.Writer.TryWrite(message);

        /// <summary>The far end goes away: the reader sees the end of the stream.</summary>
        public void Drop() => _toClient.Writer.TryWrite(null);

        public Task SendAsync(IpcMessage message, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailNextSend is { } failure)
            {
                FailNextSend = null;
                throw failure;
            }

            host.Receive(this, message);
            return Task.CompletedTask;
        }

        public async Task<IpcMessage?> ReadAsync(CancellationToken cancellationToken)
            => await _toClient.Reader.ReadAsync(cancellationToken);

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
