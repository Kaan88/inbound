using System.Text.Json;
using DownloadManager.Contracts;
using DownloadManager.Core;
using DownloadManager.Engine;
using DownloadManager.Persistence;
using DownloadManager.Runtime;
using DownloadManager.Runtime.Ipc;
using DownloadManager.Runtime.Remote;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// The Host's protocol with no pipe under it: a real <see cref="DownloadManagerService"/> on its own state folder, and
/// sessions that keep their frames in memory. Nothing here makes a network call.
/// </summary>
public sealed class HostCommandDispatcherTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "test-work", "dispatcher-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _timeline = [];
    private StoragePaths _paths = null!;
    private HttpDownloadEngine? _engine;
    private DownloadManagerService? _service;
    private readonly List<IAsyncDisposable> _cleanup = [];

    public Task InitializeAsync()
    {
        _paths = new StoragePaths(Path.Combine(_root, "state"));
        _paths.EnsureCreated();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var item in _cleanup) await item.DisposeAsync();
        if (_service is not null) await _service.ShutdownAsync();
        _engine?.Dispose();
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
    }

    private async Task<DownloadManagerService> ServiceAsync(Func<JsonJobStore, Task>? seed = null)
    {
        var store = new JsonJobStore(_paths);
        if (seed is not null)
        {
            await store.InitializeAsync();
            await seed(store);
        }

        var checkpoints = new JsonCheckpointStore(_paths);
        _engine = new HttpDownloadEngine(new EngineOptions { MaxAttempts = 1, AllowLocalNetwork = true }, checkpoints: checkpoints);
        _service = new DownloadManagerService(_paths, store, checkpoints, new JsonSettingsStore(_paths), _engine, enableSchedulerTimer: false);
        await _service.InitializeAsync();
        return _service;
    }

    private HostCommandDispatcher Dispatcher(DownloadManagerService service, FakePlatform? platform = null, RemoteCommands? remote = null)
        => new(service, platform ?? new FakePlatform(PlatformCapabilities.Desktop, _timeline), new ListLog(), remote);

    private MemorySession Session(bool runWriter = true)
    {
        var session = new MemorySession(_timeline);
        if (runWriter) session.Writer = session.RunWriterAsync(CancellationToken.None);
        _cleanup.Add(session);
        return session;
    }

    private static async Task<IpcMessage> AskAsync(HostCommandDispatcher dispatcher, MemorySession session, string type, object payload)
    {
        var request = IpcMessage.Create(type, payload);
        await dispatcher.DispatchAsync(session, request, CancellationToken.None);
        return await session.ReplyToAsync(request.Id);
    }

    [Fact]
    public async Task TheHelloListsTheDesktopCapabilitiesInTheirOrderAndRemoteOnlyWhenThereIsOne()
    {
        var service = await ServiceAsync();
        var plain = Dispatcher(service);
        var session = Session();
        await plain.OpenSessionAsync(session, CancellationToken.None);
        var hello = session.Frames[0];
        Assert.Equal(MessageTypes.ServerHello, hello.Type);
        Assert.Equal(["jobs", "settings", "handoff", "tray", "status"], hello.GetPayload<HostHelloDto>().Capabilities);
        Assert.Equal(Protocol.Version, hello.GetPayload<HostHelloDto>().ProtocolVersion);

        var registry = new RemoteProviderRegistry();
        await using var jobs = new RemoteJobCoordinator(registry, new JsonRemoteJobStore(_paths), service, new RemoteCoordinatorOptions { RunLoops = false });
        using var remote = new RemoteCommands(registry, jobs);
        Assert.Equal(["jobs", "settings", "handoff", "tray", "status", "remote"], Dispatcher(service, remote: remote).Capabilities);

        // Somewhere without a tray says so by leaving it out; the rest keep their places.
        Assert.Equal(["jobs", "settings", "handoff", "status"], Dispatcher(service, new FakePlatform(new PlatformCapabilities(), _timeline)).Capabilities);
    }

    [Fact]
    public async Task TheSnapshotComesBeforeAnyEventEvenOneRaisedWhileTheHelloIsBeingWritten()
    {
        var service = await ServiceAsync();
        var dispatcher = Dispatcher(service);
        var session = Session(runWriter: false);
        var job = new JobDto { Id = Guid.NewGuid(), FileName = "early.bin" };
        session.BeforeWrite = message =>
        {
            // Registered before the hello, so this one is not missed; queued, so it can't go ahead of the snapshot.
            if (message.Type == MessageTypes.ServerHello) dispatcher.BroadcastJobChanged(new JobChangeEventArgs(job, "updated"));
        };

        await dispatcher.OpenSessionAsync(session, CancellationToken.None);
        session.Writer = session.RunWriterAsync(CancellationToken.None);
        await session.UntilAsync(frames => frames.Count >= 3);

        Assert.Equal([MessageTypes.ServerHello, MessageTypes.JobsSnapshot, MessageTypes.JobEvent], session.Frames.Select(f => f.Type).Take(3));
        Assert.Equal(job.Id, session.Frames[2].GetPayload<JobEventDto>().Job.Id);
    }

    [Fact]
    public async Task ASnapshotTooLargeForAFrameIsSentWithoutTheFailureWords()
    {
        var service = await ServiceAsync(store => store.SaveAsync(new DownloadJob
        {
            Url = "http://127.0.0.1:9/huge.bin",
            FileName = "huge.bin",
            DestinationDirectory = _root,
            FinalFilePath = Path.Combine(_root, "huge.bin"),
            PartFilePath = Path.Combine(_root, "huge.bin.part"),
            State = DownloadState.Failed,
            StartMode = StartMode.Later,
            Failure = new JobFailure(FailureKind.Unknown, new string('x', Protocol.MaxFrameBytes + 1024), false, false, DateTimeOffset.UtcNow),
        }));
        var dispatcher = Dispatcher(service);
        var session = Session();

        await dispatcher.OpenSessionAsync(session, CancellationToken.None);

        var snapshot = Assert.Single(session.Frames, f => f.Type == MessageTypes.JobsSnapshot).GetPayload<SnapshotDto>();
        var job = Assert.Single(snapshot.Jobs);
        Assert.Equal("huge.bin", job.FileName);
        Assert.Equal("Too long to show.", job.Failure!.Message);
        Assert.Equal(1, session.Refused);
    }

    [Fact]
    public async Task ShutdownRepliesDirectlyThenSaysExitingThenShutsDown()
    {
        var service = await ServiceAsync();
        var dispatcher = Dispatcher(service);
        var asking = Session(runWriter: false);
        var other = Session(runWriter: false);
        await dispatcher.OpenSessionAsync(asking, CancellationToken.None);
        await dispatcher.OpenSessionAsync(other, CancellationToken.None);
        _timeline.Clear();

        // No writer runs, so only what is written directly reaches the timeline.
        await dispatcher.DispatchAsync(asking, IpcMessage.Create(MessageTypes.HostShutdown, new { }), CancellationToken.None);

        var ordered = _timeline.ToList();
        Assert.Equal($"{asking.Id}:{MessageTypes.HostActionResult}", ordered[0]);
        Assert.Equal("platform:shutdown", ordered[^1]);
        Assert.Equal(2, ordered.Count(entry => entry.EndsWith(":" + MessageTypes.HostExiting, StringComparison.Ordinal)));
        Assert.Equal(4, ordered.Count);
    }

    [Fact]
    public async Task ABurstOfSettingsChangesAnnouncesThePackagesOnceAndNotAgainWhenNothingChanged()
    {
        var service = await ServiceAsync();
        var dispatcher = Dispatcher(service);
        var session = Session();
        await dispatcher.OpenSessionAsync(session, CancellationToken.None);

        for (var i = 0; i < 25; i++) dispatcher.ScheduleGroupsBroadcast();
        await session.UntilAsync(frames => frames.Any(f => f.Type == MessageTypes.GroupsChanged));
        await Task.Delay(400);
        Assert.Single(session.Frames, f => f.Type == MessageTypes.GroupsChanged);

        for (var i = 0; i < 25; i++) dispatcher.ScheduleGroupsBroadcast();
        await Task.Delay(400);
        Assert.Single(session.Frames, f => f.Type == MessageTypes.GroupsChanged);
    }

    [Fact]
    public async Task AnUnknownTypeIsAnsweredWithAnError()
    {
        var service = await ServiceAsync();
        var dispatcher = Dispatcher(service);
        var session = Session();

        var reply = await AskAsync(dispatcher, session, "nope.nothing", new { });

        Assert.Equal(MessageTypes.HostActionResult, reply.Type);
        var result = reply.GetPayload<HostActionResult>();
        Assert.False(result.Ok);
        Assert.Equal("Unknown message type 'nope.nothing'.", result.Message);
    }

    [Fact]
    public async Task WithoutAWindowDialogExitOrUpdaterThoseCommandsSayTheyAreUnavailable()
    {
        var service = await ServiceAsync();
        var platform = new FakePlatform(new PlatformCapabilities(), _timeline);
        var dispatcher = Dispatcher(service, platform);
        var session = Session();

        var picked = await AskAsync(dispatcher, session, MessageTypes.HostPickFolder, new FolderPickRequest());
        Assert.Equal(MessageTypes.HostFolderPicked, picked.Type);
        var pick = picked.GetPayload<FolderPickResult>();
        Assert.False(pick.Ok);
        Assert.Contains("unavailable", pick.Error);

        var opened = (await AskAsync(dispatcher, session, MessageTypes.HostOpenUi, new HostActionRequest())).GetPayload<HostActionResult>();
        Assert.False(opened.Ok);
        Assert.Contains("not available here", opened.Message);

        var shutdown = (await AskAsync(dispatcher, session, MessageTypes.HostShutdown, new { })).GetPayload<HostActionResult>();
        Assert.False(shutdown.Ok);

        var check = await AskAsync(dispatcher, session, MessageTypes.UpdateCheck, new { });
        Assert.Equal(MessageTypes.UpdateCheckResult, check.Type);
        Assert.False(check.GetPayload<UpdateCheckResult>().Ok);
        Assert.Equal("Updates are not available here.", check.GetPayload<UpdateCheckResult>().Error);
        Assert.False((await AskAsync(dispatcher, session, MessageTypes.UpdateDownload, new { })).GetPayload<UpdateDownloadResult>().Ok);
        Assert.False((await AskAsync(dispatcher, session, MessageTypes.UpdateInstall, new UpdateInstallRequest())).GetPayload<UpdateInstallResult>().Ok);
        var defer = await AskAsync(dispatcher, session, MessageTypes.UpdateDefer, new UpdateDeferRequest { Version = "9.9.9" });
        Assert.Equal(MessageTypes.SettingsResult, defer.Type);
        Assert.False(defer.GetPayload<SettingsResult>().Ok);

        // None of them reached the machine.
        Assert.DoesNotContain(_timeline, entry => entry.StartsWith("platform:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnUpdatesDownloadOpensItsWindowOnceAndAgainOnlyAfterItEnded()
    {
        var service = await ServiceAsync();
        var platform = new FakePlatform(PlatformCapabilities.Desktop, _timeline);
        var dispatcher = Dispatcher(service, platform);
        var session = Session();
        await dispatcher.OpenSessionAsync(session, CancellationToken.None);

        for (var i = 0; i < 3; i++) dispatcher.BroadcastUpdateProgress(new UpdateProgressDto { Stage = "downloading", ReceivedBytes = i });
        Assert.Equal(["update-progress"], platform.Opened);

        dispatcher.BroadcastUpdateProgress(new UpdateProgressDto { Stage = "failed" });
        dispatcher.BroadcastUpdateProgress(new UpdateProgressDto { Stage = "downloading" });
        Assert.Equal(["update-progress", "update-progress"], platform.Opened);

        // Every one of them still went to the client.
        await session.UntilAsync(frames => frames.Count(f => f.Type == MessageTypes.UpdateProgress) == 5);

        var headless = new FakePlatform(new PlatformCapabilities(), _timeline);
        Dispatcher(service, headless).BroadcastUpdateProgress(new UpdateProgressDto { Stage = "downloading" });
        Assert.Empty(headless.Opened);
    }

    [Fact]
    public async Task AnotherProtocolVersionIsRefusedWithItsReplyQueued()
    {
        var service = await ServiceAsync();
        var dispatcher = Dispatcher(service);
        var session = Session();
        var request = IpcMessage.Create(MessageTypes.HostPing, new { }) with { V = Protocol.Version + 1 };

        Assert.False(dispatcher.RejectIfIncompatible(session, IpcMessage.Create(MessageTypes.HostPing, new { })));
        Assert.True(dispatcher.RejectIfIncompatible(session, request));

        var reply = await session.ReplyToAsync(request.Id);
        Assert.Equal(MessageTypes.ServerIncompatible, reply.Type);
    }

    [Theory]
    [InlineData(MessageTypes.FileMetadata, true)]
    [InlineData(MessageTypes.JobRefreshUrl, true)]
    [InlineData(MessageTypes.RemoteAdd, true)]
    [InlineData(MessageTypes.RemoteList, false)]
    [InlineData(MessageTypes.HostPing, false)]
    [InlineData(MessageTypes.JobAdd, false)]
    public void OnlyCommandsThatWaitGetTheClientsLifetime(string type, bool expected)
        => Assert.Equal(expected, HostCommandDispatcher.NeedsLifetime(type));

    /// <summary>The real queue and writer, with frames kept in memory and the same size limit as a pipe frame.</summary>
    private sealed class MemorySession(List<string> timeline) : QueuedClientSession(new ListLog())
    {
        private readonly List<IpcMessage> _frames = [];

        public Task? Writer { get; set; }
        public Action<IpcMessage>? BeforeWrite { get; set; }
        public int Refused { get; private set; }

        public IReadOnlyList<IpcMessage> Frames
        {
            get
            {
                lock (_frames) return _frames.ToList();
            }
        }

        protected override Task WriteFrameAsync(IpcMessage message, CancellationToken cancellationToken)
        {
            if (JsonSerializer.SerializeToUtf8Bytes(message, IpcJson.Options).Length > Protocol.MaxFrameBytes)
            {
                Refused++;
                throw new InvalidDataException("Too large for a frame.");
            }

            BeforeWrite?.Invoke(message);
            lock (_frames) _frames.Add(message);
            lock (timeline) timeline.Add($"{Id}:{message.Type}");
            return Task.CompletedTask;
        }

        public async Task UntilAsync(Func<IReadOnlyList<IpcMessage>, bool> done)
        {
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
            while (!done(Frames))
            {
                if (DateTimeOffset.UtcNow > deadline) throw new TimeoutException($"Frames so far: {string.Join(", ", Frames.Select(f => f.Type))}");
                await Task.Delay(10);
            }
        }

        public async Task<IpcMessage> ReplyToAsync(string id)
        {
            await UntilAsync(frames => frames.Any(f => f.ReplyTo == id));
            return Frames.First(f => f.ReplyTo == id);
        }

        public override async ValueTask DisposeAsync()
        {
            Complete();
            if (Writer is not null) await Writer;
            await base.DisposeAsync();
        }
    }

    private sealed class FakePlatform(PlatformCapabilities capabilities, List<string> timeline) : IHostPlatform
    {
        public PlatformCapabilities Capabilities { get; } = capabilities;
        public List<string?> Opened { get; } = [];

        public void OpenUi(string? page = null)
        {
            Opened.Add(page);
            lock (timeline) timeline.Add($"platform:open:{page}");
        }

        public void RequestShutdown()
        {
            lock (timeline) timeline.Add("platform:shutdown");
        }

        public Task<FolderPickResult> PickFolderAsync(string? initialPath, CancellationToken cancellationToken)
        {
            lock (timeline) timeline.Add("platform:pick");
            return Task.FromResult(new FolderPickResult { Ok = true, Cancelled = true });
        }
    }

    private sealed class ListLog : IHostLog
    {
        public List<string> Lines { get; } = [];
        public void Info(string message) { lock (Lines) Lines.Add("INFO " + message); }
        public void Warn(string message) { lock (Lines) Lines.Add("WARN " + message); }
        public void Error(string message) { lock (Lines) Lines.Add("ERROR " + message); }
    }
}
