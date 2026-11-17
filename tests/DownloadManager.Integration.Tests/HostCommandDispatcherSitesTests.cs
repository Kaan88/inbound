using System.Text.Json;
using DownloadManager.Contracts;
using DownloadManager.Core;
using DownloadManager.Persistence;
using DownloadManager.Runtime;
using DownloadManager.Runtime.Ipc;
using DownloadManager.Sites;
using DownloadManager.Tests.Shared;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// SITES.2, SITES.7: <c>sites.*</c> for the user at this PC and for a web session (a browser through the bridge, the
/// admin), whose folders on this computer are held to its own and which can't choose a program (Use my own rclone).
/// The Host's Sites changes reach both.
/// Mirrors the web UI tests of HostCommandDispatcherTests; nothing here makes a network call.
/// </summary>
public sealed class HostCommandDispatcherSitesTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "test-work", "dispatcher-sites-" + Guid.NewGuid().ToString("N"));
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

    private sealed class Disposer(Action dispose) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingSites : ISitesCommands
    {
        public List<string> Seen { get; } = [];
        public List<IpcMessage> Messages { get; } = [];
        public Dictionary<string, string> Folders { get; } = [];

        public string? TransferLocalFolder(string transferId) => Folders.GetValueOrDefault(transferId);

        public Task<IpcMessage> HandleAsync(IpcMessage request, CancellationToken cancellationToken)
        {
            lock (Seen)
            {
                Seen.Add(request.Type);
                Messages.Add(request);
            }

            return Task.FromResult(IpcMessage.Create(SitesMessageTypes.Status, new SitesStatusDto { Enabled = true }, request.Id));
        }
    }

    private const string SiteId = "0123456789abcdef0123456789abcdef";

    private static readonly (string Type, object Payload)[] SitesRequests =
    [
        (SitesMessageTypes.Get, new { }),
        (SitesMessageTypes.SetEnabled, new SitesSetEnabledRequest { Enabled = true, OwnRclonePath = "" }),
        (SitesMessageTypes.ListRequest, new { }),
        (SitesMessageTypes.Save, new SiteSaveRequest { Name = "Office box", Host = "srv.example", Password = "not sent by a browser" }),
        (SitesMessageTypes.Remove, new SiteIdRequest { Id = SiteId }),
        (SitesMessageTypes.KindsRequest, new { }),
        (SitesMessageTypes.Probe, new SiteProbeRequest { Host = "srv.example", Port = 22 }),
        (SitesMessageTypes.Trust, new SiteTrustRequest { Id = SiteId, Entries = ["ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIA"] }),
        (SitesMessageTypes.Test, new SiteIdRequest { Id = SiteId }),
        (SitesMessageTypes.RcloneInstall, new SitesRcloneInstallRequest()),
        (SitesMessageTypes.RcloneCheck, new { }),
        (SitesMessageTypes.Browse, new SiteBrowseRequest { Id = SiteId, Path = "/pub" }),
        (SitesMessageTypes.Star, new SiteStarRequest { Id = SiteId, Path = "/pub" }),
        (SitesMessageTypes.TransferStart, new SiteTransferStartRequest { SiteId = SiteId, LocalFolder = "/srv/in", Names = [new SiteTransferNameDto { Name = "a.txt" }] }),
        (SitesMessageTypes.TransferPause, new SiteTransferIdRequest { Id = SiteId }),
        (SitesMessageTypes.TransferResume, new SiteTransferIdRequest { Id = SiteId }),
        (SitesMessageTypes.TransferCancel, new SiteTransferIdRequest { Id = SiteId }),
        (SitesMessageTypes.TransferRetry, new SiteTransferIdRequest { Id = SiteId }),
        (SitesMessageTypes.TransferClear, new SiteTransferIdRequest()),
        (SitesMessageTypes.TransferList, new { }),
        (SitesMessageTypes.TransferGet, new SiteTransferIdRequest { Id = SiteId }),
        (SitesMessageTypes.TransferStopFile, new SiteTransferFileRequest { Id = SiteId, Path = "a.txt" }),
        (SitesMessageTypes.TransferStartFile, new SiteTransferFileRequest { Id = SiteId, Path = "a.txt" }),
        (SitesMessageTypes.OpenOnce, new SiteOpenOnceRequest { Address = "sftp://me@srv.example/pub", Password = "not logged" }),
        (SitesMessageTypes.Close, new SiteIdRequest { Id = SiteId }),
        (SitesMessageTypes.Keep, new SiteKeepRequest { Id = SiteId, Name = "Office box" }),
        (SitesMessageTypes.TestDraft, new SiteTestDraftRequest { Site = new SiteSaveRequest { Name = "Box", Host = "srv.example" } }),
        (SitesMessageTypes.Here, new SiteHereRequest { Id = SiteId, Path = "/pub", Names = ["docs"] }),
        (SitesMessageTypes.MakeFolder, new SiteMakeFolderRequest { Id = SiteId, Path = "/pub", Name = "new" }),
        (SitesMessageTypes.Rename, new SiteRenameRequest { Id = SiteId, Path = "/pub", Name = "a.txt", NewName = "b.txt" }),
        (SitesMessageTypes.Count, new SiteChangeRequest { Id = SiteId, Path = "/pub", Items = [new SiteChangeItemDto { Name = "a.txt" }] }),
        (SitesMessageTypes.Delete, new SiteChangeRequest { Id = SiteId, Path = "/pub", Items = [new SiteChangeItemDto { Name = "a.txt" }] }),
        (SitesMessageTypes.Certificate, new SiteIdRequest { Id = SiteId }),
        (SitesMessageTypes.SignIn, new SiteSignInRequest { Id = SiteId }),
        (SitesMessageTypes.SignInCancel, new SiteIdRequest { Id = SiteId }),
        (SitesMessageTypes.DeviceReadOpen, new SiteDeviceOpenRequest { Id = SiteId, Path = "/pub/a.txt" }),
        (SitesMessageTypes.DeviceWriteOpen, new SiteDeviceOpenRequest { Id = SiteId, Folder = "/pub", Name = "a.txt" }),
        (SitesMessageTypes.DeviceRead, new SiteDeviceChunkRequest { StreamId = "x" }),
        (SitesMessageTypes.DeviceWrite, new SiteDeviceChunkRequest { StreamId = "x" }),
        (SitesMessageTypes.DeviceClose, new SiteDeviceChunkRequest { StreamId = "x" }),
        (SitesMessageTypes.Remember, new SiteRememberRequest { Id = SiteId, LastFolder = "/pub" }),
        (SitesMessageTypes.Preview, new SitePreviewRequest { Id = SiteId, Path = "/pub/a.txt" }),
        (SitesMessageTypes.SyncPlan, new SiteSyncRequest { SiteId = SiteId, RemoteFolder = "/pub", LocalFolder = "/srv/in" }),
        (SitesMessageTypes.SyncRun, new SiteSyncRequest { SiteId = SiteId, RemoteFolder = "/pub", LocalFolder = "/srv/in" }),
        (SitesMessageTypes.SyncSchedule, new SiteSyncScheduleRequest { SiteId = SiteId, RemoteFolder = "/pub", LocalFolder = "/srv/in" }),
        (SitesMessageTypes.TransferSchedule, new SiteTransferScheduleRequest { Id = SiteId }),
    ];

    /// <summary>SITES.13: refused for a web session here, since /srv/in is outside its folders.</summary>
    private static bool OutsideForWeb(string type) =>
        type is SitesMessageTypes.SetEnabled or SitesMessageTypes.TransferStart or SitesMessageTypes.SyncPlan or SitesMessageTypes.SyncRun or SitesMessageTypes.SyncSchedule;

    /// <summary>The Host's own messages: replies and the push. Every other name in <see cref="SitesMessageTypes"/> is a request.</summary>
    private static readonly string[] SitesReplies =
    [
        SitesMessageTypes.Status, SitesMessageTypes.List, SitesMessageTypes.Result, SitesMessageTypes.Kinds,
        SitesMessageTypes.ProbeResult, SitesMessageTypes.BrowseResult, SitesMessageTypes.Changed,
        SitesMessageTypes.TransferResult, SitesMessageTypes.TransferListResult, SitesMessageTypes.TransferChanged,
        SitesMessageTypes.CountResult, SitesMessageTypes.ChangeResult, SitesMessageTypes.CertificateResult,
        SitesMessageTypes.SignInResult, SitesMessageTypes.DeviceStream, SitesMessageTypes.DeviceChunk, SitesMessageTypes.HereResult,
        SitesMessageTypes.PreviewResult, SitesMessageTypes.SyncPlanResult,
    ];

    [Fact]
    public void EveryRequestNameReachesSitesAndIsTestedHere()
    {
        // A request missing from IsRequest falls through to "unknown message type" (SITES.3 shipped that way once).
        var names = typeof(SitesMessageTypes).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .Except(SitesReplies)
            .ToList();
        Assert.All(names, name => Assert.True(SitesMessageTypes.IsRequest(name), name));
        Assert.All(SitesReplies, name => Assert.False(SitesMessageTypes.IsRequest(name), name));
        Assert.Equal(names.Order(), SitesRequests.Select(request => request.Type).Order());
    }

    [Fact]
    public async Task TheHelloOffersSitesOnlyToThePipeOfAHostWithSites()
    {
        var service = await ServiceAsync();
        var profile = new WebSessionProfile(service, PlatformCapabilities.Desktop);
        _cleanup.Add(new Disposer(profile.Dispose));
        var with = new HostCommandDispatcher(service, new FakePlatform(PlatformCapabilities.Desktop, _timeline), new ListLog())
        {
            WebSessions = profile.Rights,
            Sites = new RecordingSites(),
        };
        var without = Dispatcher(service);
        var web = Session();
        var pipe = Session();
        var plain = Session();
        await with.OpenSessionAsync(web, new ClientHelloDto { Role = ClientRoles.Web }, CancellationToken.None);
        await with.OpenSessionAsync(pipe, new ClientHelloDto(), CancellationToken.None);
        await without.OpenSessionAsync(plain, new ClientHelloDto(), CancellationToken.None);

        List<string> Offered(MemorySession session) => session.Frames.First(f => f.Type == MessageTypes.ServerHello).GetPayload<HostHelloDto>().Capabilities;
        Assert.Contains("sites", Offered(pipe));
        Assert.Equal("sites", Offered(pipe)[^1]);
        Assert.Contains("sites", Offered(web));
        Assert.DoesNotContain("sites", Offered(plain));
        Assert.Contains("sites", with.Capabilities);
    }

    [Fact]
    public async Task AWebSessionIsAnsweredHeldToItsFoldersAndThePipeIsAnswered()
    {
        var service = await ServiceAsync();
        var profile = new WebSessionProfile(service, PlatformCapabilities.Desktop);
        _cleanup.Add(new Disposer(profile.Dispose));
        var sites = new RecordingSites();
        var dispatcher = new HostCommandDispatcher(service, new FakePlatform(PlatformCapabilities.Desktop, _timeline), new ListLog())
        {
            WebSessions = profile.Rights,
            Sites = sites,
        };
        var web = Session();
        var pipe = Session();
        await dispatcher.OpenSessionAsync(web, new ClientHelloDto { Role = ClientRoles.Web }, CancellationToken.None);
        await dispatcher.OpenSessionAsync(pipe, new ClientHelloDto(), CancellationToken.None);

        foreach (var (type, payload) in SitesRequests)
        {
            var reply = await AskAsync(dispatcher, web, type, payload);
            if (OutsideForWeb(type))
            {
                // Use my own rclone names a program to run; /srv/in is outside the app's folders.
                Assert.Equal(MessageTypes.HostActionResult, reply.Type);
                Assert.False(reply.GetPayload<HostActionResult>().Ok);
            }
            else Assert.Equal(SitesMessageTypes.Status, reply.Type);
        }

        Assert.Equal(SitesRequests.Select(request => request.Type).Where(t => !OutsideForWeb(t)), sites.Seen);

        // The switch itself, and a folder inside the app's folders, go on; the folder as the Host resolved it.
        var inside = Path.Combine(profile.Roots.Roots[0], "from-site");
        Assert.Equal(SitesMessageTypes.Status, (await AskAsync(dispatcher, web, SitesMessageTypes.SetEnabled, new SitesSetEnabledRequest { Enabled = true })).Type);
        Assert.Equal(SitesMessageTypes.Status, (await AskAsync(dispatcher, web, SitesMessageTypes.TransferStart,
            new SiteTransferStartRequest { SiteId = SiteId, LocalFolder = inside, Names = [new SiteTransferNameDto { Name = "a.txt" }] })).Type);
        Assert.Equal(SitesMessageTypes.Status, (await AskAsync(dispatcher, web, SitesMessageTypes.Save,
            new SiteSaveRequest { Name = "Box", Host = "srv.example", LocalFolder = inside })).Type);
        var outside = await AskAsync(dispatcher, web, SitesMessageTypes.Save, new SiteSaveRequest { Name = "Box", Host = "srv.example", LocalFolder = "/srv/in" });
        Assert.Equal(MessageTypes.HostActionResult, outside.Type);
        lock (sites.Seen)
        {
            Assert.Equal(inside, sites.Messages[^1].GetPayload<SiteSaveRequest>().LocalFolder);
            // SITES.11: a web session's SFTP site must hold its own password or key, whatever the browser sent.
            Assert.True(sites.Messages[^1].GetPayload<SiteSaveRequest>().OwnSignInOnly);
            Assert.Equal(inside, sites.Messages[^2].GetPayload<SiteTransferStartRequest>().LocalFolder);
        }

        // SITES.11 R4: a draft test is held to the same rules as a save.
        Assert.Equal(SitesMessageTypes.Status, (await AskAsync(dispatcher, web, SitesMessageTypes.TestDraft,
            new SiteTestDraftRequest { Site = new SiteSaveRequest { Name = "Box", Host = "srv.example", LocalFolder = inside } })).Type);
        lock (sites.Seen)
        {
            Assert.True(sites.Messages[^1].GetPayload<SiteTestDraftRequest>().Site.OwnSignInOnly);
            Assert.Equal(inside, sites.Messages[^1].GetPayload<SiteTestDraftRequest>().Site.LocalFolder);
        }

        Assert.Equal(MessageTypes.HostActionResult, (await AskAsync(dispatcher, web, SitesMessageTypes.TestDraft,
            new SiteTestDraftRequest { Site = new SiteSaveRequest { Name = "Box", Host = "srv.example", LocalFolder = "/srv/in" } })).Type);
        Assert.True(HostCommandDispatcher.NeedsLifetime(SitesMessageTypes.TestDraft));

        // SITES.13: a sync names its folder here; one with none is refused for a web session, one inside goes on resolved.
        Assert.Equal(MessageTypes.HostActionResult, (await AskAsync(dispatcher, web, SitesMessageTypes.SyncPlan,
            new SiteSyncRequest { SiteId = SiteId, RemoteFolder = "/pub" })).Type);
        Assert.Equal(SitesMessageTypes.Status, (await AskAsync(dispatcher, web, SitesMessageTypes.SyncRun,
            new SiteSyncRequest { SiteId = SiteId, RemoteFolder = "/pub", LocalFolder = inside })).Type);
        lock (sites.Seen) Assert.Equal(inside, sites.Messages[^1].GetPayload<SiteSyncRequest>().LocalFolder);
        // A copy between remotes reads and writes nothing here, so it has no folder to check.
        Assert.Equal(SitesMessageTypes.Status, (await AskAsync(dispatcher, web, SitesMessageTypes.TransferStart,
            new SiteTransferStartRequest { SiteId = SiteId, Direction = "between", TargetSiteId = SiteId, TargetFolder = "/b", Names = [new SiteTransferNameDto { Name = "a.txt" }] })).Type);
        Assert.True(HostCommandDispatcher.NeedsLifetime(SitesMessageTypes.Preview));

        // A batch the user at this PC made outside the session's folders is not set moving again from the browser; one
        // inside them is, and pausing or cancelling either moves nothing.
        sites.Folders["outsidebatch"] = "/srv/in";
        sites.Folders["insidebatch"] = inside;
        foreach (var type in new[] { SitesMessageTypes.TransferResume, SitesMessageTypes.TransferRetry })
        {
            Assert.Equal(MessageTypes.HostActionResult, (await AskAsync(dispatcher, web, type, new SiteTransferIdRequest { Id = "outsidebatch" })).Type);
            Assert.Equal(SitesMessageTypes.Status, (await AskAsync(dispatcher, web, type, new SiteTransferIdRequest { Id = "insidebatch" })).Type);
            Assert.Equal(SitesMessageTypes.Status, (await AskAsync(dispatcher, pipe, type, new SiteTransferIdRequest { Id = "outsidebatch" })).Type);
        }

        Assert.Equal(SitesMessageTypes.Status, (await AskAsync(dispatcher, web, SitesMessageTypes.TransferPause, new SiteTransferIdRequest { Id = "outsidebatch" })).Type);
        sites.Seen.Clear();

        foreach (var (type, payload) in SitesRequests)
        {
            var answered = await AskAsync(dispatcher, pipe, type, payload);
            Assert.Equal(SitesMessageTypes.Status, answered.Type);
        }

        Assert.Equal(SitesRequests.Select(request => request.Type), sites.Seen);
        lock (sites.Seen)
            Assert.False(sites.Messages.Last(m => m.Type == SitesMessageTypes.Save).GetPayload<SiteSaveRequest>().OwnSignInOnly);
    }

    [Fact]
    public async Task SitesChangesReachThePipeAndWebSessions()
    {
        var service = await ServiceAsync();
        var profile = new WebSessionProfile(service, PlatformCapabilities.Desktop);
        _cleanup.Add(new Disposer(profile.Dispose));
        var dispatcher = new HostCommandDispatcher(service, new FakePlatform(PlatformCapabilities.Desktop, _timeline), new ListLog())
        {
            WebSessions = profile.Rights,
            Sites = new RecordingSites(),
        };
        var web = Session();
        var pipe = Session();
        var quiet = Session();
        await dispatcher.OpenSessionAsync(web, new ClientHelloDto { Role = ClientRoles.Web }, CancellationToken.None);
        await dispatcher.OpenSessionAsync(pipe, new ClientHelloDto(), CancellationToken.None);
        await dispatcher.OpenSessionAsync(quiet, new ClientHelloDto { Events = false }, CancellationToken.None);

        dispatcher.BroadcastSitesChanged(new SitesChangedDto
        {
            Status = new SitesStatusDto { Enabled = true, Rclone = SitesRcloneStates.Running },
            Sites = [new SiteDto { Id = SiteId, Name = "Office box", Kind = SiteKinds.Sftp, Host = "srv.example", Port = 22 }],
        });
        await pipe.UntilAsync(frames => frames.Any(frame => frame.Type == SitesMessageTypes.Changed));
        await web.UntilAsync(frames => frames.Any(frame => frame.Type == SitesMessageTypes.Changed));
        await Task.Delay(100);
        Assert.DoesNotContain(quiet.Frames, frame => frame.Type == SitesMessageTypes.Changed);
        var change = pipe.Frames.Single(frame => frame.Type == SitesMessageTypes.Changed).GetPayload<SitesChangedDto>();
        Assert.True(change.Status.Enabled);
        Assert.Equal("Office box", Assert.Single(change.Sites).Name);
    }

    [Fact]
    public async Task AHostWithoutSitesSaysSo()
    {
        var service = await ServiceAsync();
        var dispatcher = Dispatcher(service);
        var session = Session();
        await dispatcher.OpenSessionAsync(session, CancellationToken.None);
        var reply = await AskAsync(dispatcher, session, SitesMessageTypes.Get, new { });
        var result = reply.GetPayload<HostActionResult>();
        Assert.Equal(MessageTypes.HostActionResult, reply.Type);
        Assert.False(result.Ok);
        Assert.Contains("not available", result.Message);
    }

    [Theory]
    [InlineData(SitesMessageTypes.Probe, true)]
    [InlineData(SitesMessageTypes.Test, true)]
    [InlineData(SitesMessageTypes.ListRequest, false)]
    public void TheSlowSitesCommandsGetTheClientsLifetime(string type, bool expected)
        => Assert.Equal(expected, HostCommandDispatcher.NeedsLifetime(type));

    private async Task<DownloadManagerService> ServiceAsync()
    {
        var store = new JsonJobStore(_paths);
        var checkpoints = new JsonCheckpointStore(_paths);
        _engine = new HttpDownloadEngine(new EngineOptions { MaxAttempts = 1, AllowLocalNetwork = true }, checkpoints: checkpoints);
        _service = new DownloadManagerService(_paths, store, checkpoints, new JsonSettingsStore(_paths), _engine, enableSchedulerTimer: false);
        await _service.InitializeAsync();
        return _service;
    }

    private HostCommandDispatcher Dispatcher(DownloadManagerService service, FakePlatform? platform = null)
        => new(service, platform ?? new FakePlatform(PlatformCapabilities.Desktop, _timeline), new ListLog());

    private MemorySession Session()
    {
        var session = new MemorySession(_timeline);
        session.Writer = session.RunWriterAsync(CancellationToken.None);
        _cleanup.Add(session);
        return session;
    }

    private static async Task<IpcMessage> AskAsync(HostCommandDispatcher dispatcher, MemorySession session, string type, object payload)
    {
        var request = IpcMessage.Create(type, payload);
        await dispatcher.DispatchAsync(session, request, CancellationToken.None);
        return await session.ReplyToAsync(request.Id);
    }

    /// <summary>The real queue and writer, with frames kept in memory and the same size limit as a pipe frame.</summary>
    private sealed class MemorySession(List<string> timeline) : QueuedClientSession(new ListLog())
    {
        private readonly List<IpcMessage> _frames = [];

        public Task? Writer { get; set; }

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
                throw new InvalidDataException("Too large for a frame.");

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
            return Task.FromResult(Picks is { } path
                ? new FolderPickResult { Ok = true, Path = path }
                : new FolderPickResult { Ok = true, Cancelled = true });
        }

        /// <summary>The folder the dialog returns; null: the user cancels it.</summary>
        public string? Picks { get; set; }

        public List<string> Revealed { get; } = [];

        /// <summary>EXT.R12: downloads the main window was asked to open at.</summary>
        public List<Guid> Shown { get; } = [];

        public void ShowJob(Guid jobId)
        {
            Shown.Add(jobId);
            lock (timeline) timeline.Add("platform:show");
        }

        public void RevealFile(string path)
        {
            Revealed.Add(path);
            lock (timeline) timeline.Add("platform:reveal");
        }

        /// <summary>EXT.R13: files the popup's double-click opened.</summary>
        public List<string> OpenedFiles { get; } = [];

        public void OpenFile(string path)
        {
            lock (OpenedFiles) OpenedFiles.Add(path);
            lock (timeline) timeline.Add("platform:open");
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
