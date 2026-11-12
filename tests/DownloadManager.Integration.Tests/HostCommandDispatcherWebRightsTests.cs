using System.Reflection;
using DownloadManager.Contracts;
using DownloadManager.Core;
using DownloadManager.Engine;
using DownloadManager.Persistence;
using DownloadManager.Runtime;
using DownloadManager.Runtime.Archives;
using DownloadManager.Runtime.Ipc;
using DownloadManager.Runtime.Remote;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// D8: a <c>web</c> session (a browser on another device, through the web bridge) against a pipe session on the same
/// desktop Host. Nothing here makes a network call: every address a web session is refused is refused before any.
/// </summary>
public sealed partial class HostCommandDispatcherTests
{
    private string Inside => Path.Combine(_root, "downloads");
    private string Outside => Path.Combine(_root, "elsewhere");

    private sealed record Web(DownloadManagerService Service, HostCommandDispatcher Dispatcher, WebSessionProfile Profile,
        MemorySession Session, MemorySession Pipe, FakePlatform Platform, List<string> LinkLookups);

    /// <summary>A desktop Host with web sessions, its download folder <see cref="Inside"/>; one web and one pipe session open.</summary>
    private async Task<Web> WebAsync(Func<JsonJobStore, Task>? seed = null, RemoteProviderRegistry? providers = null,
        RemoteJobCoordinator? remoteJobs = null, ArchiveCoordinator? archives = null, DownloadManagerService? service = null,
        IReadOnlyList<string>? systemFolders = null)
    {
        Directory.CreateDirectory(Inside);
        Directory.CreateDirectory(Outside);
        service ??= await ServiceAsync(seed);
        var settings = service.GetSettings();
        settings.DefaultDownloadDirectory = Inside;
        Assert.True((await service.UpdateSettingsAsync(settings)).Ok);

        var lookups = new List<string>();
        var profile = new WebSessionProfile(service, PlatformCapabilities.Desktop, archives, providers, remoteJobs,
            linkTarget: path => { lock (lookups) lookups.Add(path); return null; }, systemFolders: systemFolders);
        var platform = new FakePlatform(PlatformCapabilities.Desktop, _timeline);
        var dispatcher = new HostCommandDispatcher(service, platform, new ListLog()) { WebSessions = profile.Rights };
        var web = Session();
        var pipe = Session();
        await dispatcher.OpenSessionAsync(web, new ClientHelloDto { Role = ClientRoles.Web }, CancellationToken.None);
        await dispatcher.OpenSessionAsync(pipe, new ClientHelloDto(), CancellationToken.None);
        await web.UntilAsync(frames => frames.Count >= 2);
        await pipe.UntilAsync(frames => frames.Count >= 2);
        _cleanup.Add(new Disposer(profile.Dispose));
        return new Web(service, dispatcher, profile, web, pipe, platform, lookups);
    }

    private sealed class Disposer(Action dispose) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            dispose();
            return ValueTask.CompletedTask;
        }
    }

    private DownloadJob SeededJob(string folder, string name = "seeded.bin") => new()
    {
        Url = "http://203.0.113.10/" + name,
        FileName = name,
        DestinationDirectory = folder,
        FinalFilePath = Path.Combine(folder, name),
        PartFilePath = Path.Combine(folder, name + ".part"),
        State = DownloadState.Paused,
        StartMode = StartMode.Later,
    };

    private async Task<DownloadJob> StoredAsync(Guid id)
    {
        var store = new JsonJobStore(_paths);
        await store.InitializeAsync();
        return (await store.LoadAsync(id))!;
    }

    private static AddJobRequest Later(string url, string? folder, string name = "file.bin") => new()
    {
        Url = url,
        DestinationDirectory = folder,
        FileName = name,
        StartMode = "later",
    };

    // ---- the hello and the role --------------------------------------------------------------------

    [Fact]
    public async Task AWebSessionsHelloListsOnlyWhatIsSafeFromAnotherDeviceWhileAPipeSessionKeepsEverything()
    {
        var web = await WebAsync();

        var webHello = web.Session.Frames[0].GetPayload<HostHelloDto>();
        Assert.Equal(["jobs", "settings", "handoff", "tray", "status", "folders"], webHello.Capabilities);
        Assert.Equal(HostKinds.Windows, webHello.HostKind);

        var pipeHello = web.Pipe.Frames[0].GetPayload<HostHelloDto>();
        Assert.Equal(["jobs", "settings", "handoff", "tray", "status", "updater", "programSettings", "folderPicker", "openUi", "shutdown", "completionActions"],
            pipeHello.Capabilities);
    }

    [Fact]
    public async Task OnlyARoleTheHostOffersIsAccepted()
    {
        var service = await ServiceAsync();
        var without = Dispatcher(service);
        Assert.True(without.OffersRole(null));
        Assert.True(without.OffersRole(ClientRoles.Desktop));
        Assert.False(without.OffersRole(ClientRoles.Web));
        Assert.False(without.OffersRole("admin"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            without.OpenSessionAsync(Session(), new ClientHelloDto { Role = ClientRoles.Web }, CancellationToken.None));

        var web = await WebAsync(service: service);
        Assert.True(web.Dispatcher.OffersRole(ClientRoles.Web));
        Assert.False(web.Dispatcher.OffersRole("admin"));
    }

    [Fact]
    public async Task ClosingTheWebSessionsEndsThemAndLeavesThePipeSessions()
    {
        var web = await WebAsync();

        Assert.Equal(1, web.Dispatcher.CloseSessions(ClientRoles.Web));
        web.Dispatcher.BroadcastJobChanged(new JobChangeEventArgs(new JobDto { Id = Guid.NewGuid(), FileName = "after.bin" }, "updated"));

        await web.Pipe.UntilAsync(frames => frames.Any(f => f.Type == MessageTypes.JobEvent));
        await web.Session.Writer!;
        Assert.DoesNotContain(web.Session.Frames, f => f.Type == MessageTypes.JobEvent);
        Assert.Equal(0, web.Dispatcher.CloseSessions(ClientRoles.Web));
    }

    // ---- what acts on this PC ----------------------------------------------------------------------

    [Fact]
    public async Task AWebSessionCantOpenTheWindowTheFolderDialogShutDownOrUpdate()
    {
        var web = await WebAsync();

        var open = await AskAsync(web.Dispatcher, web.Session, MessageTypes.HostOpenUi, new HostActionRequest());
        Assert.False(open.GetPayload<HostActionResult>().Ok);
        var pick = await AskAsync(web.Dispatcher, web.Session, MessageTypes.HostPickFolder, new FolderPickRequest());
        Assert.NotNull(pick.GetPayload<FolderPickResult>().Error);
        var shutdown = await AskAsync(web.Dispatcher, web.Session, MessageTypes.HostShutdown, new { });
        Assert.False(shutdown.GetPayload<HostActionResult>().Ok);
        // The updater's own refusal, each in its own reply type: not just any reply that mentions an error.
        const string updatesUnavailable = "Updates are not available here.";
        var check = await AskAsync(web.Dispatcher, web.Session, MessageTypes.UpdateCheck, new { });
        Assert.Equal(MessageTypes.UpdateCheckResult, check.Type);
        Assert.Equal(updatesUnavailable, check.GetPayload<UpdateCheckResult>().Error);
        var download = await AskAsync(web.Dispatcher, web.Session, MessageTypes.UpdateDownload, new { });
        Assert.Equal(MessageTypes.UpdateDownloadResult, download.Type);
        Assert.Equal(updatesUnavailable, download.GetPayload<UpdateDownloadResult>().Error);
        var install = await AskAsync(web.Dispatcher, web.Session, MessageTypes.UpdateInstall, new { });
        Assert.Equal(MessageTypes.UpdateInstallResult, install.Type);
        Assert.Equal(updatesUnavailable, install.GetPayload<UpdateInstallResult>().Error);
        Assert.False(install.GetPayload<UpdateInstallResult>().Ok);
        var defer = await AskAsync(web.Dispatcher, web.Session, MessageTypes.UpdateDefer, new { });
        Assert.Equal(MessageTypes.SettingsResult, defer.Type);
        Assert.Equal(updatesUnavailable, defer.GetPayload<SettingsResult>().Error);

        Assert.DoesNotContain(_timeline, line => line.StartsWith("platform:", StringComparison.Ordinal));

        // The same commands from the pipe reach the machine.
        Assert.True((await AskAsync(web.Dispatcher, web.Pipe, MessageTypes.HostOpenUi, new HostActionRequest())).GetPayload<HostActionResult>().Ok);
        await AskAsync(web.Dispatcher, web.Pipe, MessageTypes.HostPickFolder, new FolderPickRequest());
        Assert.Contains("platform:pick", _timeline);
        Assert.Single(web.Platform.Opened);
    }

    [Fact]
    public async Task OnlyThisUsersOwnSessionOnAPcWithAFolderDialogLearnsWhichStartFoldersExist()
    {
        // EXT.R10: the extension's start-folder check. A web session (or a server, which has no folder dialog) is
        // never told which folders on this PC exist.
        var web = await WebAsync();
        var ask = new DownloadFoldersRequest { Candidates = [Inside, Outside] };

        var pipe = (await AskAsync(web.Dispatcher, web.Pipe, MessageTypes.SettingsDownloadFolders, ask)).GetPayload<DownloadFoldersResult>();
        Assert.True(pipe.Ok);
        Assert.Equal([Inside, Outside], pipe.ExistingCandidates);

        var fromWeb = await AskAsync(web.Dispatcher, web.Session, MessageTypes.SettingsDownloadFolders, ask);
        if (fromWeb.Type == MessageTypes.SettingsDownloadFoldersResult) Assert.Null(fromWeb.GetPayload<DownloadFoldersResult>().ExistingCandidates);
        Assert.DoesNotContain(Outside.Replace(@"\", @"\\"), fromWeb.Payload!.Value.GetRawText());

        var noDialog = Dispatcher(web.Service, new FakePlatform(new PlatformCapabilities(), _timeline));
        var plain = (await AskAsync(noDialog, Session(), MessageTypes.SettingsDownloadFolders, ask)).GetPayload<DownloadFoldersResult>();
        Assert.True(plain.Ok);
        Assert.Null(plain.ExistingCandidates);
    }

    [Fact]
    public async Task AWebSessionIsNotToldWhereTheHostKeepsItsState()
    {
        var web = await WebAsync();

        Assert.Equal(string.Empty, web.Session.Frames[1].GetPayload<SnapshotDto>().Status.StateDirectory);
        Assert.NotEqual(string.Empty, web.Pipe.Frames[1].GetPayload<SnapshotDto>().Status.StateDirectory);
        var status = await AskAsync(web.Dispatcher, web.Session, MessageTypes.HostStatus, new { });
        Assert.Equal(string.Empty, status.GetPayload<HostStatusDto>().StateDirectory);
        var query = await AskAsync(web.Dispatcher, web.Session, MessageTypes.JobsQuery, new { });
        Assert.Equal(string.Empty, query.GetPayload<SnapshotDto>().Status.StateDirectory);
        Assert.NotEqual(string.Empty, (await AskAsync(web.Dispatcher, web.Pipe, MessageTypes.HostStatus, new { })).GetPayload<HostStatusDto>().StateDirectory);
    }

    [Fact]
    public async Task AWebSessionCantTurnOnWhatAFinishedDownloadDoesOnThisPc()
    {
        JobDto? job = null;
        var web = await WebAsync();
        job = (await web.Service.AddJobAsync(Later("http://203.0.113.10/a.bin", Inside))).Job!;

        foreach (var options in new[]
                 {
                     new JobCompletionOptionsDto { OpenFile = true },
                     new JobCompletionOptionsDto { SystemAction = "shutdown" },
                     new JobCompletionOptionsDto { ForceSystemAction = true },
                 })
        {
            var refused = await AskAsync(web.Dispatcher, web.Session, MessageTypes.JobUpdate, new JobUpdateRequest { JobId = job.Id, CompletionOptions = options });
            Assert.Contains("only be set in the app on this PC", refused.GetPayload<JobActionResponse>().Error);
        }

        var program = await AskAsync(web.Dispatcher, web.Session, MessageTypes.JobUpdate,
            new JobUpdateRequest { JobId = job.Id, CompletionOptions = new JobCompletionOptionsDto { ProgramPath = @"C:\Windows\notepad.exe" } });
        Assert.False(program.GetPayload<JobActionResponse>().Ok);

        // Sending back what it has (all off) is no change.
        var plain = await AskAsync(web.Dispatcher, web.Session, MessageTypes.JobUpdate,
            new JobUpdateRequest { JobId = job.Id, CompletionOptions = new JobCompletionOptionsDto(), Description = "kept" });
        Assert.True(plain.GetPayload<JobActionResponse>().Ok, plain.GetPayload<JobActionResponse>().Error);

        var fromPipe = await AskAsync(web.Dispatcher, web.Pipe, MessageTypes.JobUpdate,
            new JobUpdateRequest { JobId = job.Id, CompletionOptions = new JobCompletionOptionsDto { OpenFile = true } });
        Assert.True(fromPipe.GetPayload<JobActionResponse>().Ok, fromPipe.GetPayload<JobActionResponse>().Error);
        Assert.True(web.Service.GetJob(job.Id)!.CompletionOptions.OpenFile);
    }

    // ---- settings ----------------------------------------------------------------------------------

    [Fact]
    public void EverySettingIsDecidedForWebSessions()
    {
        var names = typeof(SettingsDto).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name).ToList();
        var lists = WebSettingsRules.Allowed.Concat(WebSettingsRules.PipeOnly).Concat(WebSettingsRules.KeptByHost).ToList();

        var undecided = names.Except(lists).ToList();
        Assert.True(undecided.Count == 0, "Decide in WebSettingsRules whether a web session may change: " + string.Join(", ", undecided));
        Assert.Equal(lists.Count, lists.Distinct().Count());
        Assert.Empty(lists.Except(names));
    }

    [Fact]
    public async Task AWebSessionSavesWhatItMayAndIsRefusedTheRestWithNothingSaved()
    {
        var web = await WebAsync();

        async Task<SettingsResult> Save(MemorySession session, Action<SettingsDto> change)
        {
            var settings = web.Service.GetSettings();
            change(settings);
            return (await AskAsync(web.Dispatcher, session, MessageTypes.SettingsUpdate, new SettingsUpdateRequest { Settings = settings })).GetPayload<SettingsResult>();
        }

        var allowed = await Save(web.Session, s => s.MaxConcurrentDownloads = 7);
        Assert.True(allowed.Ok, allowed.Error);
        Assert.Equal(7, web.Service.GetSettings().MaxConcurrentDownloads);

        // Where torrents go and whether files are chosen first are the browser's to change; turning them on is not.
        var torrentChoices = await Save(web.Session, s => { s.TorrentRoute = TorrentRoutes.Provider; s.TorrentChooseFiles = false; });
        Assert.True(torrentChoices.Ok, torrentChoices.Error);
        Assert.Equal(TorrentRoutes.Provider, web.Service.GetSettings().TorrentRoute);
        Assert.False(web.Service.GetSettings().TorrentChooseFiles);

        // T2 seeding is the browser's to change too, and the Host's checks still hold for it.
        var seeding = await Save(web.Session, s => { s.TorrentSeedAfterDownload = false; s.TorrentSeedRatio = 2.5; s.TorrentSeedHours = 0; s.TorrentUploadLimit = 512_000; });
        Assert.True(seeding.Ok, seeding.Error);
        var seeded = web.Service.GetSettings();
        Assert.Equal((false, 2.5, 0d, 512_000L), (seeded.TorrentSeedAfterDownload, seeded.TorrentSeedRatio, seeded.TorrentSeedHours, seeded.TorrentUploadLimit));
        var bothZero = await Save(web.Session, s => { s.TorrentSeedRatio = 0; s.TorrentSeedHours = 0; });
        Assert.False(bothZero.Ok);
        Assert.Equal(2.5, web.Service.GetSettings().TorrentSeedRatio);

        var refusals = new (string Name, Action<SettingsDto> Change)[]
        {
            ("defaultDownloadDirectory", s => s.DefaultDownloadDirectory = Outside),
            ("favoriteFolders", s => s.FavoriteFolders = [new FavoriteFolderDto { Name = "x", Path = Outside }]),
            ("categoryFolders", s => s.CategoryFolders["video"] = Outside),
            ("proxy", s => s.Proxy = new ProxyDto { Mode = "http", Address = "203.0.113.1:8080" }),
            ("allowLocalNetwork", s => s.AllowLocalNetwork = !s.AllowLocalNetwork),
            ("postDownloadCommand", s => s.PostDownloadCommand = "cmd.exe /c echo"),
            ("ffmpegPath", s => s.FfmpegPath = @"C:\Windows\System32\cmd.exe"),
            ("allowSystemCompletionActions", s => s.AllowSystemCompletionActions = true),
            ("updateManifestUrl", s => s.UpdateManifestUrl = "https://example.invalid/manifest.json"),
            ("launchAtStartup", s => s.LaunchAtStartup = !s.LaunchAtStartup),
            ("torrentsEnabled", s => s.TorrentsEnabled = !s.TorrentsEnabled),
            ("torrentHandler", s => s.TorrentHandler = !s.TorrentHandler),
            ("torrentListenPort", s => s.TorrentListenPort = 40404),
            ("torrentUpnp", s => s.TorrentUpnp = !s.TorrentUpnp),
        };
        foreach (var (name, change) in refusals)
        {
            var refused = await Save(web.Session, s => { s.MaxConcurrentDownloads = 2; change(s); });
            Assert.False(refused.Ok, name);
            Assert.Contains(name, refused.Error);
            Assert.Equal(7, web.Service.GetSettings().MaxConcurrentDownloads);
        }

        var schedule = await Save(web.Session, s => s.Schedules = [new ScheduleDto { Id = Guid.NewGuid(), ShutdownWhenDone = true }]);
        Assert.Equal(WebSettingsRules.ScheduleActionsRefused, schedule.Error);
        foreach (var change in new Action<ScheduleDto>[] { x => x.OpenFileWhenDone = true, x => x.ExitWhenDone = true, x => x.ShutdownMode = "hibernate", x => x.ForceShutdown = true })
        {
            var item = new ScheduleDto { Id = Guid.NewGuid() };
            change(item);
            Assert.Equal(WebSettingsRules.ScheduleActionsRefused, (await Save(web.Session, s => s.Schedules = [item])).Error);
        }

        var plainSchedule = await Save(web.Session, s => s.Schedules = [new ScheduleDto { Id = Guid.NewGuid(), StartTime = "03:00" }]);
        Assert.True(plainSchedule.Ok, plainSchedule.Error);

        var category = await Save(web.Session, s => s.CustomCategories = [new CategoryDefinitionDto { Id = "web-made", Name = "Web", DestinationDirectory = Outside, Extensions = ["abc"] }]);
        Assert.Equal(WebSettingsRules.CategoryFolderRefused, category.Error);

        // An older copy of what the Host keeps itself is replaced, not refused.
        var stale = await Save(web.Session, s => s.RecentUrls = ["http://203.0.113.10/old"]);
        Assert.True(stale.Ok, stale.Error);
        Assert.DoesNotContain("http://203.0.113.10/old", web.Service.GetSettings().RecentUrls);

        var fromPipe = await Save(web.Pipe, s => s.AllowLocalNetwork = !s.AllowLocalNetwork);
        Assert.True(fromPipe.Ok, fromPipe.Error);
    }

    // ---- folders -----------------------------------------------------------------------------------

    [Fact]
    public async Task AWebSessionAddsOnlyIntoTheAppsFolders()
    {
        var web = await WebAsync();

        var outside = await AskAsync(web.Dispatcher, web.Session, MessageTypes.JobAdd, Later("http://203.0.113.10/a.bin", Outside));
        var refused = outside.GetPayload<AddJobResponse>();
        Assert.False(refused.Accepted);
        Assert.Equal("That folder is outside the app's folders.", refused.Error);

        var inside = (await AskAsync(web.Dispatcher, web.Session, MessageTypes.JobAdd, Later("http://203.0.113.10/a.bin", Path.Combine(Inside, "sub")))).GetPayload<AddJobResponse>();
        Assert.True(inside.Accepted, inside.Error);
        Assert.True((await StoredAsync(inside.Job!.Id)).PublicNetworkOnly);

        var fromPipe = (await AskAsync(web.Dispatcher, web.Pipe, MessageTypes.JobAdd, Later("http://203.0.113.10/b.bin", Outside, "b.bin"))).GetPayload<AddJobResponse>();
        Assert.True(fromPipe.Accepted, fromPipe.Error);
        Assert.False((await StoredAsync(fromPipe.Job!.Id)).PublicNetworkOnly);
    }

    [Theory]
    [InlineData(@"\\fileserver\share\downloads")]
    [InlineData(@"//fileserver/share")]
    [InlineData(@"\\?\C:\downloads")]
    [InlineData(@"\\.\C:\downloads")]
    [InlineData(@"\??\C:\downloads")]
    [InlineData(@"C:\downloads\CON")]
    [InlineData(@"C:\downloads\nul.txt")]
    [InlineData(@"C:\downloads\COM1\x")]
    [InlineData(@"C:\downloads\file.bin:stream")]
    [InlineData(@"C:\downloads\folder.")]
    [InlineData(@"C:\downloads\folder \x")]
    [InlineData(@"C:downloads")]
    [InlineData(@"downloads\x")]
    public async Task AWebSessionsUnsafeFolderIsRefusedBeforeTheDiskIsAsked(string folder)
    {
        if (!OperatingSystem.IsWindows()) return;
        var web = await WebAsync();
        // The app's own folders are resolved once, when they are first needed; after that nothing is looked up.
        Assert.NotNull(web.Profile.Roots.Allowed(Inside));
        lock (web.LinkLookups) web.LinkLookups.Clear();

        var add = (await AskAsync(web.Dispatcher, web.Session, MessageTypes.JobAdd, Later("http://203.0.113.10/a.bin", folder))).GetPayload<AddJobResponse>();
        Assert.False(add.Accepted);
        var browse = (await AskAsync(web.Dispatcher, web.Session, MessageTypes.FoldersBrowse, new FolderBrowseRequest { Path = folder })).GetPayload<FolderBrowseResult>();
        Assert.False(browse.Ok);
        var space = (await AskAsync(web.Dispatcher, web.Session, MessageTypes.FolderSpace, new FolderSpaceRequest { Paths = [folder] })).GetPayload<FolderSpaceResult>();
        Assert.All(space.Folders, f => Assert.Null(f.TotalBytes));

        Assert.NotNull(PathRoots.DescribeUnsafeText(folder));
        Assert.Null(web.Profile.Roots.Allowed(folder));
        lock (web.LinkLookups) Assert.Empty(web.LinkLookups);
    }

    [Fact]
    public void ASafeDriveLetterPathPassesTheTextCheck()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Null(PathRoots.DescribeUnsafeText(Path.Combine(Inside, "sub", "file.bin")));
        Assert.Null(PathRoots.DescribeUnsafeText(Path.Combine(Inside, "a.b c")));
    }

    [Fact]
    public async Task TheAppsFoldersAreWorkedOutFromTheCurrentSettings()
    {
        var web = await WebAsync();
        var favorite = Path.Combine(_root, "favorite");
        Directory.CreateDirectory(favorite);
        Assert.Null(web.Profile.Roots.Allowed(favorite));

        var settings = web.Service.GetSettings();
        settings.FavoriteFolders = [new FavoriteFolderDto { Name = "Fav", Path = favorite }];
        settings.CustomCategories.Add(new CategoryDefinitionDto { Id = Guid.NewGuid().ToString("N"), Name = "Own files", DestinationDirectory = Outside, Extensions = ["ownfile"] });
        var saved = await web.Service.UpdateSettingsAsync(settings);
        Assert.True(saved.Ok, saved.Error);

        Assert.NotNull(web.Profile.Roots.Allowed(Path.Combine(favorite, "x")));
        Assert.NotNull(web.Profile.Roots.Allowed(Outside));
        Assert.NotNull(web.Profile.Roots.Allowed(Path.Combine(Inside, "deep", "er")));
        Assert.Null(web.Profile.Roots.Allowed(Path.Combine(_root, "state")));
    }

    [Fact]
    public async Task AWebSessionBrowsesMeasuresAndCreatesFoldersOnlyInsideTheAppsFolders()
    {
        var web = await WebAsync();

        var inside = (await AskAsync(web.Dispatcher, web.Session, MessageTypes.FoldersBrowse, new FolderBrowseRequest { Path = Inside })).GetPayload<FolderBrowseResult>();
        Assert.True(inside.Ok, inside.Error);
        var outside = (await AskAsync(web.Dispatcher, web.Session, MessageTypes.FoldersBrowse, new FolderBrowseRequest { Path = Outside })).GetPayload<FolderBrowseResult>();
        Assert.False(outside.Ok);
        Assert.Equal("That folder is outside the app's folders.", outside.Error);

        var created = (await AskAsync(web.Dispatcher, web.Session, MessageTypes.FoldersCreate, new FolderCreateRequest { Parent = Inside, Name = "made" })).GetPayload<FolderBrowseResult>();
        Assert.True(created.Ok, created.Error);
        Assert.True(Directory.Exists(Path.Combine(Inside, "made")));
        var createdOutside = (await AskAsync(web.Dispatcher, web.Session, MessageTypes.FoldersCreate, new FolderCreateRequest { Parent = Outside, Name = "made" })).GetPayload<FolderBrowseResult>();
        Assert.False(createdOutside.Ok);
        Assert.False(Directory.Exists(Path.Combine(Outside, "made")));

        var space = (await AskAsync(web.Dispatcher, web.Session, MessageTypes.FolderSpace, new FolderSpaceRequest { Paths = [Inside, Outside] })).GetPayload<FolderSpaceResult>();
        Assert.NotNull(space.Folders.Single(f => f.Path == Inside).TotalBytes);
        Assert.Null(space.Folders.Single(f => f.Path == Outside).TotalBytes);
        var pipeSpace = (await AskAsync(web.Dispatcher, web.Pipe, MessageTypes.FolderSpace, new FolderSpaceRequest { Paths = [Outside] })).GetPayload<FolderSpaceResult>();
        Assert.NotNull(pipeSpace.Folders.Single().TotalBytes);
    }

    // ---- downloads outside the folders -------------------------------------------------------------

    [Fact]
    public async Task ADownloadOutsideTheAppsFoldersIsReadOnlyForAWebSession()
    {
        var outsideJob = SeededJob(Outside, "outside.bin");
        var insideJob = SeededJob(Inside, "inside.bin");
        var web = await WebAsync(async store =>
        {
            await store.SaveAsync(outsideJob);
            await store.SaveAsync(insideJob);
        });
        File.WriteAllText(outsideJob.FinalFilePath, "keep");

        var update = (await AskAsync(web.Dispatcher, web.Session, MessageTypes.JobUpdate, new JobUpdateRequest { JobId = outsideJob.Id, Description = "x" })).GetPayload<JobActionResponse>();
        Assert.Equal(CallerScope.OutsideJobRefused, update.Error);
        var rename = (await AskAsync(web.Dispatcher, web.Session, MessageTypes.JobUpdate,
            new JobUpdateRequest { JobId = outsideJob.Id, NewFilePath = Path.Combine(Inside, "moved.bin") })).GetPayload<JobActionResponse>();
        Assert.Equal(CallerScope.OutsideJobRefused, rename.Error);
        var refresh = (await AskAsync(web.Dispatcher, web.Session, MessageTypes.JobRefreshUrl,
            new JobRefreshUrlRequest { JobId = outsideJob.Id, Url = "http://203.0.113.10/new.bin", ExplicitDirect = true })).GetPayload<JobActionResponse>();
        Assert.Equal(CallerScope.OutsideJobRefused, refresh.Error);
        var remove = (await AskAsync(web.Dispatcher, web.Session, MessageTypes.JobRemove, new JobActionRequest { JobId = outsideJob.Id, DeleteFile = true })).GetPayload<JobActionResponse>();
        Assert.Equal(CallerScope.OutsideJobRefused, remove.Error);
        var removeMany = (await AskAsync(web.Dispatcher, web.Session, MessageTypes.JobRemove,
            new JobActionRequest { JobIds = [insideJob.Id, outsideJob.Id], DeleteFile = true })).GetPayload<JobActionResponse>();
        Assert.Equal(CallerScope.OutsideJobRefused, removeMany.Error);
        Assert.NotNull(web.Service.GetJob(insideJob.Id));
        var cancel = (await AskAsync(web.Dispatcher, web.Session, MessageTypes.JobCancel, new JobActionRequest { JobId = outsideJob.Id, DeleteFile = true })).GetPayload<JobActionResponse>();
        Assert.Equal(CallerScope.OutsideJobRefused, cancel.Error);
        Assert.True(File.Exists(outsideJob.FinalFilePath));

        // Inside its folders the same changes go ahead, and from the pipe they go ahead everywhere.
        var insideUpdate = (await AskAsync(web.Dispatcher, web.Session, MessageTypes.JobUpdate, new JobUpdateRequest { JobId = insideJob.Id, Description = "x" })).GetPayload<JobActionResponse>();
        Assert.True(insideUpdate.Ok, insideUpdate.Error);
        var pipeUpdate = (await AskAsync(web.Dispatcher, web.Pipe, MessageTypes.JobUpdate, new JobUpdateRequest { JobId = outsideJob.Id, Description = "x" })).GetPayload<JobActionResponse>();
        Assert.True(pipeUpdate.Ok, pipeUpdate.Error);

        // Removing it without its file only takes it off the list.
        var keepFile = (await AskAsync(web.Dispatcher, web.Session, MessageTypes.JobRemove, new JobActionRequest { JobId = outsideJob.Id })).GetPayload<JobActionResponse>();
        Assert.True(keepFile.Ok, keepFile.Error);
        Assert.True(File.Exists(outsideJob.FinalFilePath));
    }

    [Fact]
    public async Task AWebSessionMovesOrRenamesADownloadOnlyToAPathInsideTheAppsFolders()
    {
        var job = SeededJob(Inside, "inside.bin");
        var web = await WebAsync(store => store.SaveAsync(job));

        foreach (var target in new[] { Path.Combine(Outside, "moved.bin"), @"\\fileserver\share\moved.bin", Path.Combine(Inside, "moved.bin:stream") })
        {
            var refused = (await AskAsync(web.Dispatcher, web.Session, MessageTypes.JobUpdate, new JobUpdateRequest { JobId = job.Id, NewFilePath = target })).GetPayload<JobActionResponse>();
            Assert.False(refused.Ok, target);
        }

        Assert.Equal(Path.Combine(Inside, "inside.bin"), web.Service.GetJob(job.Id)!.FinalFilePath);
    }

    [Fact]
    public async Task AWebSessionCantExtractADownloadOutsideTheAppsFoldersNorMoveTheExtractionFolder()
    {
        var outsideJob = SeededJob(Outside, "outside.rar");
        var service = await ServiceAsync(store => store.SaveAsync(outsideJob));
        await using var archives = new ArchiveCoordinator(service, new JsonArchiveStore(_paths));
        await archives.InitializeAsync();
        var web = await WebAsync(archives: archives, service: service);

        var extract = await AskAsync(web.Dispatcher, web.Session, ArchiveMessageTypes.Extract, new ArchiveExtractRequest { JobIds = [outsideJob.Id] });
        Assert.Equal(CallerScope.OutsideJobRefused, extract.GetPayload<ArchiveExtractResult>().Error);
        var byFile = await AskAsync(web.Dispatcher, web.Session, ArchiveMessageTypes.Extract, new ArchiveExtractRequest { FilePath = outsideJob.FinalFilePath });
        Assert.False(byFile.GetPayload<ArchiveExtractResult>().Ok);

        var folder = await AskAsync(web.Dispatcher, web.Session, ArchiveMessageTypes.SettingsSet, new ArchiveSettingsUpdate { Folder = Outside });
        Assert.False(folder.GetPayload<ArchiveSettingsResult>().Ok);
        Assert.NotEqual(Outside, archives.GetSettings().Settings.Folder);
        var autoExtract = await AskAsync(web.Dispatcher, web.Session, ArchiveMessageTypes.SettingsSet, new ArchiveSettingsUpdate { AutoExtract = true });
        Assert.True(autoExtract.GetPayload<ArchiveSettingsResult>().Ok, autoExtract.GetPayload<ArchiveSettingsResult>().Error);
    }

    // ---- addresses ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("http://127.0.0.1:9/a.bin")]
    [InlineData("http://localhost/a.bin")]
    [InlineData("http://printer.local/a.bin")]
    [InlineData("http://10.0.0.5/a.bin")]
    [InlineData("http://192.168.1.20/a.bin")]
    [InlineData("http://172.16.0.1/a.bin")]
    [InlineData("http://169.254.169.254/latest")]
    [InlineData("http://100.64.0.1/a.bin")]
    [InlineData("http://[::1]/a.bin")]
    [InlineData("http://[fe80::1]/a.bin")]
    [InlineData("http://[fd00::1]/a.bin")]
    [InlineData("http://[::ffff:192.168.1.1]/a.bin")]
    [InlineData("http://0.0.0.0/a.bin")]
    public async Task AWebSessionCantReachThisPcOrItsLocalNetwork(string url)
    {
        var web = await WebAsync();

        var add = (await AskAsync(web.Dispatcher, web.Session, MessageTypes.JobAdd, Later(url, Inside))).GetPayload<AddJobResponse>();
        Assert.False(add.Accepted);
        Assert.Equal("local_network", add.ErrorCode);
        var final = (await AskAsync(web.Dispatcher, web.Session, MessageTypes.JobAdd,
            new AddJobRequest { Url = "http://203.0.113.10/a.bin", FinalUrl = url, DestinationDirectory = Inside, FileName = "a.bin", StartMode = "later" })).GetPayload<AddJobResponse>();
        Assert.Equal("local_network", final.ErrorCode);
        var metadata = (await AskAsync(web.Dispatcher, web.Session, MessageTypes.FileMetadata, new FileMetadataRequest { Url = url })).GetPayload<FileMetadataResult>();
        Assert.Equal(CallerScope.LocalAddressRefused, metadata.Error);
        // U-WR-2: said before Start, so the dialog can turn Start off rather than fail after it.
        Assert.True(metadata.AddRefused);

        var job = (await web.Service.AddJobAsync(Later("http://203.0.113.10/b.bin", Inside, "b.bin"))).Job!;
        var refresh = (await AskAsync(web.Dispatcher, web.Session, MessageTypes.JobRefreshUrl, new JobRefreshUrlRequest { JobId = job.Id, Url = url, ExplicitDirect = true })).GetPayload<JobActionResponse>();
        Assert.Equal(CallerScope.LocalAddressRefused, refresh.Error);

        var fromPipe = (await AskAsync(web.Dispatcher, web.Pipe, MessageTypes.JobAdd, Later(url, Inside, "pipe.bin"))).GetPayload<AddJobResponse>();
        Assert.True(fromPipe.Accepted, fromPipe.Error);
    }

    [Fact]
    public async Task AWebSessionCantAddFtp()
    {
        var web = await WebAsync();
        var add = (await AskAsync(web.Dispatcher, web.Session, MessageTypes.JobAdd, Later("ftp://203.0.113.10/a.bin", Inside))).GetPayload<AddJobResponse>();
        Assert.False(add.Accepted);
        Assert.Equal(CallerScope.FtpRefused, add.Error);
    }

    [Fact]
    public async Task ABrowserHandOffThroughAWebSessionIsHeldToItsFoldersAndAddressesAndItsExtensionIdGrantsNothing()
    {
        var web = await WebAsync();
        HandoffRequest Handoff(string url, string? folder, string extension = "not-a-known-extension") => new()
        {
            RequestId = Guid.NewGuid().ToString("N"),
            ExtensionId = extension,
            Url = url,
            SuggestedFileName = "h.bin",
            DestinationDirectory = folder,
            StartMode = "later",
        };

        var local = (await AskAsync(web.Dispatcher, web.Session, MessageTypes.HandoffRequest, Handoff("http://127.0.0.1:9/h.bin", Inside, BrowserBridge.StoreExtensionId))).GetPayload<HandoffAck>();
        Assert.False(local.Accepted);
        Assert.Equal("local_network", local.ErrorCode);
        var outside = (await AskAsync(web.Dispatcher, web.Session, MessageTypes.HandoffRequest, Handoff("http://203.0.113.10/h.bin", Outside, BrowserBridge.StoreExtensionId))).GetPayload<HandoffAck>();
        Assert.False(outside.Accepted);

        var accepted = (await AskAsync(web.Dispatcher, web.Session, MessageTypes.HandoffRequest, Handoff("http://203.0.113.10/h.bin", Inside))).GetPayload<HandoffAck>();
        Assert.True(accepted.Accepted, accepted.Error);
        var job = await StoredAsync(accepted.JobId);
        Assert.True(job.PublicNetworkOnly);
        Assert.StartsWith(CallerScope.OwnerPrefix, job.OwnerExtensionId);

        // From the pipe the extension id is checked as before.
        var untrusted = (await AskAsync(web.Dispatcher, web.Pipe, MessageTypes.HandoffRequest, Handoff("http://203.0.113.10/h.bin", Inside))).GetPayload<HandoffAck>();
        Assert.Equal("untrusted_extension", untrusted.ErrorCode);
    }

    [Fact]
    public async Task APublicNetworkOnlyDownloadNeverConnectsToALocalAddressEvenWhereLocalAddressesAreAllowed()
    {
        using var engine = new HttpDownloadEngine(new EngineOptions { MaxAttempts = 1, AllowLocalNetwork = true });
        var folder = Path.Combine(_root, "engine");
        Directory.CreateDirectory(folder);
        var job = new DownloadJob
        {
            Url = "http://127.0.0.1:9/a.bin",
            FileName = "a.bin",
            DestinationDirectory = folder,
            FinalFilePath = Path.Combine(folder, "a.bin"),
            PartFilePath = Path.Combine(folder, "a.bin.part"),
            PublicNetworkOnly = true,
        };

        var result = await engine.ExecuteAsync(job, null, null, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal(FailureKind.Forbidden, result.Failure!.Kind);
        Assert.Equal("Local network destinations are disabled.", result.Failure.Message);
    }

    // ---- provider ----------------------------------------------------------------------------------

    [Fact]
    public async Task AWebSessionCantSetOrClearTheProvidersKey()
    {
        var service = await ServiceAsync();
        var registry = new RemoteProviderRegistry();
        await using var jobs = new RemoteJobCoordinator(registry, new JsonRemoteJobStore(_paths), service, new RemoteCoordinatorOptions { RunLoops = false });
        var web = await WebAsync(providers: registry, remoteJobs: jobs, service: service);

        Assert.Contains("remote", web.Session.Frames[0].GetPayload<HostHelloDto>().Capabilities);
        var set = await AskAsync(web.Dispatcher, web.Session, MessageTypes.RemoteAccountSetKey, new RemoteSetKeyRequest());
        Assert.Contains("only be changed in the app on this PC", set.GetPayload<RemoteAccountResult>().Error);
        var clear = await AskAsync(web.Dispatcher, web.Session, MessageTypes.RemoteAccountClearKey, new RemoteAccountRequest());
        Assert.Contains("only be changed in the app on this PC", clear.GetPayload<RemoteAccountResult>().Error);
    }
}
