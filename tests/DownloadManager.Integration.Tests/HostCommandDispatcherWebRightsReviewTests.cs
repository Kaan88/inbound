using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using DownloadManager.Containers.Tests;
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
/// D8 review: what the first round of web-session rights left open. Archive sets outside the app's folders, the
/// completion actions of a download whose address changes, what a web session is shown of the settings, the folders
/// no root may be, link targets and every path entry point. Nothing here makes a network call.
/// </summary>
public sealed partial class HostCommandDispatcherTests
{
    private const string ArchivePassword = "invented-archive-password";

    private async Task<(Web Web, ArchiveCoordinator Archives)> WebWithArchivesAsync(Func<JsonJobStore, Task>? seed = null)
    {
        var service = await ServiceAsync(seed);
        var archives = new ArchiveCoordinator(service, new JsonArchiveStore(_paths));
        _cleanup.Add(archives);
        await archives.InitializeAsync();
        return (await WebAsync(archives: archives, service: service), archives);
    }

    // ---- archive sets ------------------------------------------------------------------------------

    [Fact]
    public async Task AWebSessionLeavesArchiveSetsOutsideTheAppsFoldersAloneAndDoesntSeeThem()
    {
        var (web, archives) = await WebWithArchivesAsync();
        var outside = (await archives.SetPasswordAsync(new ArchiveSetPasswordRequest { FilePath = Path.Combine(Outside, "owner.rar"), Password = ArchivePassword })).Set!;
        var inside = (await archives.SetPasswordAsync(new ArchiveSetPasswordRequest { FilePath = Path.Combine(Inside, "web.rar"), Password = ArchivePassword })).Set!;
        var refusal = ArchiveIpcDispatcher.OutsideSetText(web.Profile.Roots);

        foreach (var type in new[] { ArchiveMessageTypes.Retry, ArchiveMessageTypes.Cancel, ArchiveMessageTypes.Remove })
        {
            var refused = (await AskAsync(web.Dispatcher, web.Session, type, new ArchiveActionRequest { SetId = outside.Id })).GetPayload<ArchiveActionResult>();
            Assert.False(refused.Ok, type);
            Assert.Equal(refusal, refused.Error);
        }

        var password = (await AskAsync(web.Dispatcher, web.Session, ArchiveMessageTypes.SetPassword,
            new ArchiveSetPasswordRequest { SetId = outside.Id, Password = ArchivePassword + "-new" })).GetPayload<ArchiveActionResult>();
        Assert.Equal(refusal, password.Error);
        Assert.NotNull(archives.Find(outside.Id));

        var list = (await AskAsync(web.Dispatcher, web.Session, ArchiveMessageTypes.List, new { })).GetPayload<ArchiveListResult>();
        Assert.Equal([inside.Id], list.Sets.Select(set => set.Id));
        Assert.Equal(2, archives.List().Count);

        // Inside its folders the same commands go ahead.
        var retry = (await AskAsync(web.Dispatcher, web.Session, ArchiveMessageTypes.Retry, new ArchiveActionRequest { SetId = inside.Id })).GetPayload<ArchiveActionResult>();
        Assert.True(retry.Ok, retry.Error);
        var remove = (await AskAsync(web.Dispatcher, web.Session, ArchiveMessageTypes.Remove, new ArchiveActionRequest { SetId = inside.Id })).GetPayload<ArchiveActionResult>();
        Assert.True(remove.Ok, remove.Error);
    }

    [Fact]
    public async Task AWebSessionCantGiveAPasswordToAPackageWithADownloadOutsideTheAppsFolders()
    {
        var outsideJob = SeededJob(Outside, "outside.part1.rar");
        var insideJob = SeededJob(Inside, "inside.part1.rar");
        var (web, _) = await WebWithArchivesAsync(async store =>
        {
            await store.SaveAsync(outsideJob);
            await store.SaveAsync(insideJob);
        });
        var mixed = await web.Service.GroupActionAsync(new GroupActionRequest { Action = "create", Name = "mixed", JobIds = [outsideJob.Id, insideJob.Id] });
        Assert.True(mixed.Ok, mixed.Error);

        var refused = (await AskAsync(web.Dispatcher, web.Session, ArchiveMessageTypes.SetPassword,
            new ArchiveSetPasswordRequest { PackageId = mixed.GroupId, Password = ArchivePassword })).GetPayload<ArchiveActionResult>();
        Assert.False(refused.Ok);
        Assert.Equal(CallerScope.OutsideJobRefused, refused.Error);
        var extract = (await AskAsync(web.Dispatcher, web.Session, ArchiveMessageTypes.Extract, new ArchiveExtractRequest { PackageId = mixed.GroupId })).GetPayload<ArchiveExtractResult>();
        Assert.Equal(CallerScope.OutsideJobRefused, extract.Error);
    }

    [Fact]
    public async Task AWebSessionCantMakeExtractionDeleteArchivesOrOverwriteFiles()
    {
        var (web, archives) = await WebWithArchivesAsync();

        foreach (var update in new[] { new ArchiveSettingsUpdate { DeleteArchives = true }, new ArchiveSettingsUpdate { ConflictPolicy = "overwrite" } })
        {
            var refused = (await AskAsync(web.Dispatcher, web.Session, ArchiveMessageTypes.SettingsSet, update)).GetPayload<ArchiveSettingsResult>();
            Assert.False(refused.Ok);
            Assert.Contains("only be turned on in the app on this PC", refused.Error);
        }

        Assert.False(archives.GetSettings().Settings.DeleteArchives);
        Assert.NotEqual("overwrite", archives.GetSettings().Settings.ConflictPolicy);

        // Safer choices, and the rest of the extraction settings, go ahead.
        var skip = (await AskAsync(web.Dispatcher, web.Session, ArchiveMessageTypes.SettingsSet, new ArchiveSettingsUpdate { ConflictPolicy = "skip", DeleteArchives = false, PreserveFolders = false })).GetPayload<ArchiveSettingsResult>();
        Assert.True(skip.Ok, skip.Error);
        Assert.Equal("skip", archives.GetSettings().Settings.ConflictPolicy);

        // Set in the app, it stays, and sending it back is no change.
        Assert.True((await archives.UpdateSettingsAsync(new ArchiveSettingsUpdate { DeleteArchives = true })).Ok);
        var same = (await AskAsync(web.Dispatcher, web.Session, ArchiveMessageTypes.SettingsSet, new ArchiveSettingsUpdate { DeleteArchives = true, AutoExtract = true })).GetPayload<ArchiveSettingsResult>();
        Assert.True(same.Ok, same.Error);
    }

    [Fact]
    public async Task AWebSessionCanGiveOrClearAPasswordOnlyForAnArchiveFileInsideTheAppsFolders()
    {
        var (web, archives) = await WebWithArchivesAsync();

        var outside = (await AskAsync(web.Dispatcher, web.Session, ArchiveMessageTypes.SetPassword,
            new ArchiveSetPasswordRequest { FilePath = Path.Combine(Outside, "x.rar"), Password = ArchivePassword })).GetPayload<ArchiveActionResult>();
        Assert.False(outside.Ok);
        Assert.Empty(archives.List());
        var inside = (await AskAsync(web.Dispatcher, web.Session, ArchiveMessageTypes.SetPassword,
            new ArchiveSetPasswordRequest { FilePath = Path.Combine(Inside, "x.rar"), Password = ArchivePassword })).GetPayload<ArchiveActionResult>();
        Assert.True(inside.Ok, inside.Error);
    }

    // ---- a download's completion actions -----------------------------------------------------------

    [Fact]
    public async Task AWebSessionCantGiveANewAddressToADownloadThatOpensItsFileOrRunsAProgramWhenDone()
    {
        var web = await WebAsync();

        foreach (var options in new[] { new JobCompletionOptionsDto { OpenFile = true }, new JobCompletionOptionsDto { ProgramPath = @"C:\Tools\scan.exe" } })
        {
            var job = (await web.Service.AddJobAsync(Later("http://203.0.113.10/a.bin", Inside))).Job!;
            var set = await web.Service.UpdateJobAsync(new JobUpdateRequest { JobId = job.Id, CompletionOptions = options });
            Assert.True(set.Ok, set.Error);

            var refused = (await AskAsync(web.Dispatcher, web.Session, MessageTypes.JobRefreshUrl,
                new JobRefreshUrlRequest { JobId = job.Id, Url = "http://203.0.113.20/other.bin", ExplicitDirect = true })).GetPayload<JobActionResponse>();
            Assert.False(refused.Ok);
            Assert.Equal(CallerScope.CompletionActionsKeepAddress, refused.Error);
            Assert.Equal("http://203.0.113.10/a.bin", web.Service.GetJob(job.Id)!.Url);

            var fromPipe = (await AskAsync(web.Dispatcher, web.Pipe, MessageTypes.JobRefreshUrl,
                new JobRefreshUrlRequest { JobId = job.Id, Url = "http://203.0.113.20/other.bin", ExplicitDirect = true })).GetPayload<JobActionResponse>();
            Assert.True(fromPipe.Ok, fromPipe.Error);
        }

        var plain = (await web.Service.AddJobAsync(Later("http://203.0.113.10/b.bin", Inside, "b.bin"))).Job!;
        var changed = (await AskAsync(web.Dispatcher, web.Session, MessageTypes.JobRefreshUrl,
            new JobRefreshUrlRequest { JobId = plain.Id, Url = "http://203.0.113.20/b.bin", ExplicitDirect = true })).GetPayload<JobActionResponse>();
        Assert.True(changed.Ok, changed.Error);
    }

    // ---- what a web session is shown ---------------------------------------------------------------

    [Fact]
    public async Task AWebSessionIsNotShownTheProgramsProxyLoginsSoundsOrRecentAddressesAndCanStillSaveWhatItWasShown()
    {
        var web = await WebAsync();
        var settings = web.Service.GetSettings();
        settings.PostDownloadCommand = @"C:\Tools\scan.exe";
        settings.FfmpegPath = @"C:\Tools\ffmpeg.exe";
        settings.UpdateManifestUrl = "https://updates.example.invalid/manifest.json";
        settings.Proxy = new ProxyDto { Mode = "manual", Address = "203.0.113.5:3128", Username = "proxy-user", Password = "invented-proxy-password" };
        settings.SiteLogins = [new SiteLoginDto { Id = Guid.NewGuid(), Site = "files.example.invalid", Username = "site-user", Password = "invented-site-password" }];
        settings.EventSoundFiles = new(StringComparer.OrdinalIgnoreCase) { ["completed"] = @"C:\Sounds\done.wav" };
        Assert.True((await web.Service.UpdateSettingsAsync(settings)).Ok);
        await web.Service.AddJobAsync(Later("http://203.0.113.10/signed.bin?token=invented-token", Inside, "signed.bin"));
        Assert.NotEmpty(web.Service.GetSettings().RecentUrls);

        void AssertHidden(SettingsDto shown)
        {
            Assert.Null(shown.PostDownloadCommand);
            Assert.Null(shown.FfmpegPath);
            Assert.Null(shown.UpdateManifestUrl);
            Assert.Equal("manual", shown.Proxy.Mode);
            Assert.Null(shown.Proxy.Address);
            Assert.Null(shown.Proxy.Username);
            Assert.Empty(shown.SiteLogins);
            Assert.Empty(shown.EventSoundFiles);
            Assert.Empty(shown.RecentUrls);
            Assert.Empty(shown.RecentDestinationDirectories);
            Assert.Equal(Inside, shown.DefaultDownloadDirectory);
        }

        var get = (await AskAsync(web.Dispatcher, web.Session, MessageTypes.SettingsGet, new { })).GetPayload<SettingsResult>();
        AssertHidden(get.Settings);
        AssertHidden((await AskAsync(web.Dispatcher, web.Session, MessageTypes.JobsQuery, new { })).GetPayload<SnapshotDto>().Settings);
        var pipe = (await AskAsync(web.Dispatcher, web.Pipe, MessageTypes.SettingsGet, new { })).GetPayload<SettingsResult>();
        Assert.Equal("203.0.113.5:3128", pipe.Settings.Proxy.Address);
        Assert.NotEmpty(pipe.Settings.RecentUrls);

        // What it was shown, sent back with a change of its own, saves that change and keeps what it wasn't shown.
        var shown = get.Settings;
        shown.MaxConcurrentDownloads = 6;
        var saved = (await AskAsync(web.Dispatcher, web.Session, MessageTypes.SettingsUpdate, new SettingsUpdateRequest { Settings = shown })).GetPayload<SettingsResult>();
        Assert.True(saved.Ok, saved.Error);
        AssertHidden(saved.Settings);
        var kept = web.Service.GetSettings();
        Assert.Equal(6, kept.MaxConcurrentDownloads);
        Assert.Equal(settings.PostDownloadCommand, kept.PostDownloadCommand);
        Assert.Equal(settings.FfmpegPath, kept.FfmpegPath);
        Assert.Equal("203.0.113.5:3128", kept.Proxy.Address);
        Assert.True(kept.Proxy.HasPassword);
        Assert.Equal("site-user", Assert.Single(kept.SiteLogins).Username);
        Assert.Equal(@"C:\Sounds\done.wav", kept.EventSoundFiles["completed"]);

        // A hidden one changed is still refused.
        shown.PostDownloadCommand = "cmd.exe /c echo";
        var refused = (await AskAsync(web.Dispatcher, web.Session, MessageTypes.SettingsUpdate, new SettingsUpdateRequest { Settings = shown })).GetPayload<SettingsResult>();
        Assert.False(refused.Ok);
        Assert.Contains("postDownloadCommand", refused.Error);
        AssertHidden(refused.Settings);
    }

    // ---- other programs on this PC -----------------------------------------------------------------

    [Fact]
    public async Task AWebSessionCantHandAContainerToAnotherProgramOnThisPc()
    {
        var service = await ServiceAsync();
        var registry = new RemoteProviderRegistry();
        await using var jobs = new RemoteJobCoordinator(registry, new JsonRemoteJobStore(_paths), service, new RemoteCoordinatorOptions { RunLoops = false });
        var web = await WebAsync(providers: registry, remoteJobs: jobs, service: service);
        var container = Path.Combine(Inside, "links.dlc");
        File.WriteAllText(container, "not a real container");

        var send = (await AskAsync(web.Dispatcher, web.Session, MessageTypes.RemoteContainerSendToJd, new RemoteContainerSendRequest { Path = container })).GetPayload<RemoteContainerSendResult>();
        Assert.False(send.Ok);
        Assert.False(send.JdAnswered);
        Assert.Contains("only be handed to another download manager in the app on this PC", send.Message);

        var preview = (await AskAsync(web.Dispatcher, web.Session, MessageTypes.RemoteContainerPreview, new RemoteContainerPreviewRequest { Path = container })).GetPayload<RemoteContainerPreviewResult>();
        Assert.False(preview.CanSendToJd);
    }

    // ---- the provider and container paths ----------------------------------------------------------

    [Fact]
    public async Task AWebSessionsProviderAndContainerPathsAreHeldToTheAppsFolders()
    {
        var service = await ServiceAsync();
        var registry = new RemoteProviderRegistry();
        await using var jobs = new RemoteJobCoordinator(registry, new JsonRemoteJobStore(_paths), service, new RemoteCoordinatorOptions { RunLoops = false });
        var web = await WebAsync(providers: registry, remoteJobs: jobs, service: service);
        var container = Path.Combine(Outside, "owner.rsdf");
        File.WriteAllBytes(container, ContainerFixtures.Rsdf("https://203.0.113.10/a.bin"));

        var outsideDestination = (await AskAsync(web.Dispatcher, web.Session, MessageTypes.RemoteAdd,
            new RemoteAddRequestDto { Input = "magnet:?xt=urn:btih:" + new string('a', 40), Destination = Outside })).GetPayload<RemoteAddResultDto>();
        Assert.False(outsideDestination.Ok);
        Assert.Equal("That folder is outside the app's folders.", outsideDestination.Error);

        // A .torrent file needs a provider before it is read: RuntimeServiceTests.AWebSessionsTorrentFileAndProviderJobsAreHeldToItsFolders.

        var preview = (await AskAsync(web.Dispatcher, web.Session, MessageTypes.RemoteContainerPreview, new RemoteContainerPreviewRequest { Path = container })).GetPayload<RemoteContainerPreviewResult>();
        Assert.Equal("That file is outside the app's folders.", preview.Error);
        Assert.Null(preview.PreviewId);
        // Without a preview there is nothing to add from.
        var add = (await AskAsync(web.Dispatcher, web.Session, MessageTypes.RemoteContainerAdd, new RemoteContainerAddRequest { PreviewId = Guid.NewGuid(), EntryIds = [0] })).GetPayload<RemoteContainerAddResult>();
        Assert.False(add.Ok);
        Assert.Empty(jobs.List());
    }

    // ---- leftovers ---------------------------------------------------------------------------------

    [Fact]
    public async Task AWebSessionSeesAndCleansUpOnlyLeftoversInsideTheAppsFolders()
    {
        var outsideJob = SeededJob(Outside, "kept.bin");
        Directory.CreateDirectory(Outside);
        Directory.CreateDirectory(Inside);
        var outsideLeftover = Path.Combine(Outside, "leftover.bin.0123456789abcdef0123456789abcdef.part");
        var insideLeftover = Path.Combine(Inside, "leftover.bin.0123456789abcdef0123456789abcdef.part");
        File.WriteAllText(outsideLeftover, "x");
        File.WriteAllText(insideLeftover, "x");
        var web = await WebAsync(store => store.SaveAsync(outsideJob));

        var webStatus = (await AskAsync(web.Dispatcher, web.Session, MessageTypes.HostStatus, new { })).GetPayload<HostStatusDto>();
        Assert.DoesNotContain(outsideLeftover, webStatus.OrphanPartFiles);
        Assert.DoesNotContain(outsideLeftover, web.Session.Frames[1].GetPayload<SnapshotDto>().Status.OrphanPartFiles);
        Assert.Contains(outsideLeftover, (await AskAsync(web.Dispatcher, web.Pipe, MessageTypes.HostStatus, new { })).GetPayload<HostStatusDto>().OrphanPartFiles);

        var cleaned = (await AskAsync(web.Dispatcher, web.Session, MessageTypes.HostCleanOrphans, new CleanOrphansRequest { Delete = true })).GetPayload<CleanOrphansResponse>();
        Assert.Equal([insideLeftover], cleaned.Processed);
        Assert.True(File.Exists(outsideLeftover));
        Assert.False(File.Exists(insideLeftover));
    }

    // ---- removing several --------------------------------------------------------------------------

    [Fact]
    public async Task AWebSessionsRemovalOfSeveralSkipsOneAlreadyGoneAsThePipesDoes()
    {
        var insideJob = SeededJob(Inside, "inside.bin");
        var web = await WebAsync(store => store.SaveAsync(insideJob));
        // Paused, so its data is the partial file. A file at its final path it never wrote isn't its own (UI.B19).
        File.WriteAllText(insideJob.PartFilePath, "x");

        var removed = (await AskAsync(web.Dispatcher, web.Session, MessageTypes.JobRemove,
            new JobActionRequest { JobIds = [Guid.NewGuid(), insideJob.Id], DeleteFile = true })).GetPayload<JobActionResponse>();
        Assert.True(removed.Ok, removed.Error);
        Assert.Null(web.Service.GetJob(insideJob.Id));
        Assert.False(File.Exists(insideJob.PartFilePath));
    }

    // ---- which folders can be roots ----------------------------------------------------------------

    [Fact]
    public async Task AFolderThatHoldsOrIsInsideWindowsOrProgramFoldersIsNoRoot()
    {
        if (!OperatingSystem.IsWindows()) return;
        var web = await WebAsync();
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var drive = Path.GetPathRoot(Inside)!;
        var settings = web.Service.GetSettings();
        settings.FavoriteFolders = [new FavoriteFolderDto { Name = "Drive", Path = drive }, new FavoriteFolderDto { Name = "Startup", Path = Path.Combine(appData, "Microsoft") }];
        Assert.True((await web.Service.UpdateSettingsAsync(settings)).Ok);

        Assert.Null(web.Profile.Roots.Allowed(Path.Combine(appData, "Microsoft", "Windows", "Start Menu")));
        Assert.Null(web.Profile.Roots.Allowed(Path.Combine(drive, "Windows", "System32")));
        Assert.Null(web.Profile.Roots.Allowed(Outside));
        Assert.NotNull(web.Profile.Roots.Allowed(Path.Combine(Inside, "x")));

        var add = (await AskAsync(web.Dispatcher, web.Session, MessageTypes.JobAdd, Later("http://203.0.113.10/a.bin", Path.Combine(appData, "Microsoft")))).GetPayload<AddJobResponse>();
        Assert.False(add.Accepted);
        Assert.Equal("That folder can't be used from another device. " + WebSessionProfile.SystemFolderRefused, add.Error);
    }

    [Fact]
    public async Task AnAppFolderThatCantBeUsedFromAnotherDeviceIsNamedSoInTheRefusal()
    {
        var blocked = Path.Combine(_root, "blocked");
        Directory.CreateDirectory(blocked);
        var web = await WebAsync(systemFolders: [blocked]);
        var settings = web.Service.GetSettings();
        settings.FavoriteFolders = [new FavoriteFolderDto { Name = "Blocked", Path = blocked }];
        Assert.True((await web.Service.UpdateSettingsAsync(settings)).Ok);

        var add = (await AskAsync(web.Dispatcher, web.Session, MessageTypes.JobAdd, Later("http://203.0.113.10/a.bin", Path.Combine(blocked, "sub")))).GetPayload<AddJobResponse>();
        Assert.Equal("That folder can't be used from another device. " + WebSessionProfile.SystemFolderRefused, add.Error);
        var browse = (await AskAsync(web.Dispatcher, web.Session, MessageTypes.FoldersBrowse, new FolderBrowseRequest { Path = blocked })).GetPayload<FolderBrowseResult>();
        Assert.Equal("That folder can't be used from another device. " + WebSessionProfile.SystemFolderRefused, browse.Error);
        var elsewhere = (await AskAsync(web.Dispatcher, web.Session, MessageTypes.JobAdd, Later("http://203.0.113.10/a.bin", Outside))).GetPayload<AddJobResponse>();
        Assert.Equal("That folder is outside the app's folders.", elsewhere.Error);
    }

    // ---- links and Windows path forms --------------------------------------------------------------

    [Fact]
    public void ALinkToADriveThatIsNotAFixedOneIsNotFollowed()
    {
        if (!OperatingSystem.IsWindows()) return;
        var letter = "QRSTUVWXYZ".FirstOrDefault(candidate => new DriveInfo(candidate.ToString()).DriveType != DriveType.Fixed);
        if (letter == default) return;
        var asked = new List<string>();
        var link = Path.Combine(Inside, "share");
        var roots = new PathRoots([Inside], path =>
        {
            asked.Add(path);
            return PathNames.Same(path, link) ? $@"{letter}:\share" : null;
        });

        Assert.Null(roots.Allowed(Path.Combine(link, "file.bin")));
        Assert.DoesNotContain(asked, path => path.StartsWith($"{letter}:", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AJunctionTargetInTheNtFormIsFollowedLikeAnyOther()
    {
        if (!OperatingSystem.IsWindows()) return;
        var outLink = Path.Combine(Inside, "out");
        var inLink = Path.Combine(Inside, "in");
        var roots = new PathRoots([Inside], path =>
            PathNames.Same(path, outLink) ? @"\??\" + Outside : PathNames.Same(path, inLink) ? @"\??\" + Path.Combine(Inside, "sub") : null);

        Assert.Null(roots.Allowed(Path.Combine(outLink, "file.bin")));
        Assert.Equal(Path.Combine(Inside, "sub", "file.bin"), roots.Allowed(Path.Combine(inLink, "file.bin")));
    }

    [Fact]
    public void ARealJunctionInsideARootThatLeadsOutsideItLeadsNowhere()
    {
        if (!OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(Inside);
        Directory.CreateDirectory(Outside);
        Directory.CreateDirectory(Path.Combine(Inside, "sub"));
        var outLink = Path.Combine(Inside, "out-junction");
        var inLink = Path.Combine(Inside, "in-junction");
        if (!Junction(outLink, Outside) || !Junction(inLink, Path.Combine(Inside, "sub"))) return;
        try
        {
            var roots = new PathRoots([Inside]);
            Assert.Null(roots.Allowed(outLink));
            Assert.Null(roots.Allowed(Path.Combine(outLink, "file.bin")));
            Assert.Equal(Path.Combine(Inside, "sub", "file.bin"), roots.Allowed(Path.Combine(inLink, "file.bin")));
        }
        finally
        {
            Directory.Delete(outLink);
            Directory.Delete(inLink);
        }
    }

    [Fact]
    public void AShortNameOrAnotherCaseIsTheSameFolder()
    {
        if (!OperatingSystem.IsWindows()) return;
        var longInside = Path.Combine(Inside, "A folder with a long name");
        var longOutside = Path.Combine(Outside, "Another folder with a long name");
        Directory.CreateDirectory(longInside);
        Directory.CreateDirectory(longOutside);
        var roots = new PathRoots([Inside]);

        Assert.NotNull(roots.Allowed(Path.Combine(Inside.ToUpperInvariant(), "x.bin")));
        Assert.NotNull(roots.Allowed(Path.Combine(Inside.ToLowerInvariant(), "x.bin")));

        var shortInside = ShortName(longInside);
        var shortOutside = ShortName(longOutside);
        if (shortInside is null || shortOutside is null || PathNames.Same(shortInside, longInside)) return; // no short names on this volume
        Assert.NotNull(roots.Allowed(Path.Combine(shortInside, "x.bin")));
        Assert.Null(roots.Allowed(Path.Combine(shortOutside, "x.bin")));
    }

    private static bool Junction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        process.WaitForExit(10_000);
        return process.ExitCode == 0 && Directory.Exists(link);
    }

    private static string? ShortName(string path)
    {
        var buffer = new StringBuilder(1024);
        return GetShortPathNameW(path, buffer, buffer.Capacity) is > 0 and < 1024 ? buffer.ToString() : null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetShortPathNameW(string longPath, StringBuilder shortPath, int bufferLength);

    // ---- the mark of the web -----------------------------------------------------------------------

    [Fact]
    public async Task EveryDownloadFromTheNetworkIsMarkedAsFromTheInternetWithOnlyItsSite()
    {
        if (!OperatingSystem.IsWindows()) return;
        var handler = new FixedResponse("payload");
        var store = new JsonJobStore(_paths);
        var checkpoints = new JsonCheckpointStore(_paths);
        _engine = new HttpDownloadEngine(new EngineOptions { MaxAttempts = 1, AllowLocalNetwork = true }, handler, checkpoints);
        _service = new DownloadManagerService(_paths, store, checkpoints, new JsonSettingsStore(_paths), _engine, enableSchedulerTimer: false);
        await _service.InitializeAsync();
        var web = await WebAsync(service: _service);

        var fromWeb = (await AskAsync(web.Dispatcher, web.Session, MessageTypes.JobAdd,
            new AddJobRequest { Url = "https://203.0.113.10/web.bin?token=invented", DestinationDirectory = Inside, FileName = "web.bin", StartMode = "startNow" })).GetPayload<AddJobResponse>();
        var fromPipe = (await AskAsync(web.Dispatcher, web.Pipe, MessageTypes.JobAdd,
            new AddJobRequest { Url = "https://203.0.113.10/pipe.bin", DestinationDirectory = Inside, FileName = "pipe.bin", StartMode = "startNow" })).GetPayload<AddJobResponse>();
        Assert.True(fromWeb.Accepted, fromWeb.Error);
        Assert.True(fromPipe.Accepted, fromPipe.Error);
        await UntilCompletedAsync(web.Service, fromWeb.Job!.Id);
        await UntilCompletedAsync(web.Service, fromPipe.Job!.Id);

        var mark = File.ReadAllText(web.Service.GetJob(fromWeb.Job.Id)!.FinalFilePath + ":Zone.Identifier");
        Assert.Contains("ZoneId=3", mark);
        Assert.Contains("HostUrl=https://203.0.113.10/", mark);
        Assert.DoesNotContain("invented", mark);
        // REV.4: the owner's own downloads are marked too.
        var pipeMark = File.ReadAllText(web.Service.GetJob(fromPipe.Job.Id)!.FinalFilePath + ":Zone.Identifier");
        Assert.Contains("ZoneId=3", pipeMark);
        Assert.Contains("HostUrl=https://203.0.113.10/", pipeMark);
    }

    [Theory]
    [InlineData("https://example.com/a.zip", true)]
    [InlineData("http://203.0.113.10/a.zip", true)]
    [InlineData("ftp://files.example.com/a.zip", true)]
    [InlineData("http://127.0.0.1:8080/a.zip", false)]
    [InlineData("http://localhost/a.zip", false)]
    [InlineData("http://[::1]/a.zip", false)]
    [InlineData("file:///C:/a.zip", false)]
    [InlineData("magnet:?xt=urn:btih:00", false)]
    [InlineData(null, false)]
    public void OnlyADownloadFromTheNetworkIsMarked(string? address, bool marked) =>
        Assert.Equal(marked, MarkOfTheWeb.Applies(address));

    private static async Task UntilCompletedAsync(DownloadManagerService service, Guid id)
    {
        var until = DateTime.UtcNow.AddSeconds(20);
        while (service.GetJob(id) is { } job && job.State != "completed")
        {
            Assert.True(job.State != "failed", job.Failure?.Message);
            Assert.True(DateTime.UtcNow < until, $"still {job.State}");
            await Task.Delay(50);
        }
    }

    private sealed class FixedResponse(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)), RequestMessage = request };
            response.Content.Headers.ContentLength = body.Length;
            return Task.FromResult(response);
        }
    }

    // ---- the connection itself ---------------------------------------------------------------------

    [Fact]
    public async Task APublicNetworkOnlyDownloadToANameThatResolvesToThisPcIsRefusedWhenItConnects()
    {
        var result = await PublicOnlyAsync(new EngineOptions { MaxAttempts = 1, AllowLocalNetwork = true });
        Assert.False(result.Success);
        Assert.Contains("Local network destinations are disabled.", result.Failure!.Message);
    }

    [Fact]
    public async Task APublicNetworkOnlyDownloadThroughAProxyIsRefusedWhenItsNameResolvesToThisPc()
    {
        // Nothing listens on the proxy's port: had the name not been checked first, the failure would be the proxy's.
        var options = new EngineOptions { MaxAttempts = 1, AllowLocalNetwork = true };
        options.Proxy.Mode = "manual";
        options.Proxy.Address = "http://127.0.0.1:9";
        var result = await PublicOnlyAsync(options);
        Assert.False(result.Success);
        Assert.Contains("Local network destinations are disabled.", result.Failure!.Message);
    }

    /// <summary>A download to this PC's own name, which passes the text check and resolves to its own addresses without any network.</summary>
    private async Task<DownloadResult> PublicOnlyAsync(EngineOptions options)
    {
        var name = Dns.GetHostName();
        Assert.False(NetworkAddresses.IsLocalTarget(new Uri($"http://{name}:9/a.bin")));
        Assert.Contains((await Dns.GetHostAddressesAsync(name)), NetworkAddresses.IsLocal);
        using var engine = new HttpDownloadEngine(options);
        var folder = Path.Combine(_root, "engine-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var job = new DownloadJob
        {
            Url = $"http://{name}:9/a.bin",
            FileName = "a.bin",
            DestinationDirectory = folder,
            FinalFilePath = Path.Combine(folder, "a.bin"),
            PartFilePath = Path.Combine(folder, "a.bin.part"),
            PublicNetworkOnly = true,
        };
        return await engine.ExecuteAsync(job, null, null, CancellationToken.None);
    }
}
