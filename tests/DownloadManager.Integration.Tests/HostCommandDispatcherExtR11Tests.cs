using DownloadManager.Contracts;
using DownloadManager.Core;
using DownloadManager.Core.Providers;
using DownloadManager.Runtime;
using DownloadManager.Runtime.Ipc;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// EXT.R11 (GitHub #4): what the popup's right-click menu asks the Host for, on one download each. Show in folder
/// (<c>job.reveal</c>) through a fake platform, Rename (<c>job.update</c> with <c>newFileName</c>) and Redownload
/// (<c>job.redownload</c>). Nothing here reaches the network: a redownload's address is a closed local port.
/// </summary>
public sealed partial class HostCommandDispatcherTests
{
    private string Folder => Path.Combine(_root, "files");

    private DownloadJob Finished(string name, string? url = null)
    {
        Directory.CreateDirectory(Folder);
        var path = Path.Combine(Folder, name);
        File.WriteAllText(path, "done");
        return new DownloadJob
        {
            Url = url ?? $"http://127.0.0.1:9/{name}",
            FileName = name,
            DestinationDirectory = Folder,
            FinalFilePath = path,
            PartFilePath = string.Empty,
            State = DownloadState.Completed,
            StartMode = StartMode.Later,
            CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            TotalBytes = 4,
            ReceivedBytes = 4,
        };
    }

    private async Task<(DownloadManagerService Service, DownloadJob[] Jobs)> SeededAsync(params DownloadJob[] jobs)
    {
        var service = await ServiceAsync(async store =>
        {
            foreach (var job in jobs) await store.SaveAsync(job);
        });
        return (service, jobs);
    }

    // ---- job.reveal ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task RevealShowsTheFileThenItsFolderWhenTheFileIsGoneAndSaysSoWhenBothAreGone()
    {
        var (service, jobs) = await SeededAsync(Finished("shown.bin"));
        var platform = new FakePlatform(PlatformCapabilities.Desktop, _timeline);
        var dispatcher = Dispatcher(service, platform);
        var session = Session();
        var path = jobs[0].FinalFilePath;

        var file = (await AskAsync(dispatcher, session, MessageTypes.JobReveal, new { jobId = jobs[0].Id })).GetPayload<HostActionResult>();
        Assert.True(file.Ok, file.Message);
        Assert.Equal([path], platform.Revealed);

        File.Delete(path);
        var folder = (await AskAsync(dispatcher, session, MessageTypes.JobReveal, new { jobId = jobs[0].Id })).GetPayload<HostActionResult>();
        Assert.True(folder.Ok, folder.Message);
        Assert.Contains("folder was opened", folder.Message);
        Assert.Equal(2, platform.Revealed.Count);

        Directory.Delete(Folder, recursive: true);
        var reply = await AskAsync(dispatcher, session, MessageTypes.JobReveal, new { jobId = jobs[0].Id });
        Assert.Equal(MessageTypes.HostActionResult, reply.Type);
        var gone = reply.GetPayload<HostActionResult>();
        Assert.False(gone.Ok);
        Assert.Equal("The file and its folder are gone.", gone.Message);
        Assert.Equal(2, platform.Revealed.Count);

        var unknown = (await AskAsync(dispatcher, session, MessageTypes.JobReveal, new { jobId = Guid.NewGuid() })).GetPayload<HostActionResult>();
        Assert.False(unknown.Ok);
        Assert.Equal("Job not found.", unknown.Message);
    }

    [Fact]
    public async Task RevealIsRefusedWhereThePlatformCannotShowFiles()
    {
        var (service, jobs) = await SeededAsync(Finished("server.bin"));
        // A server's platform, or a web session's rights: no reveal.
        var platform = new FakePlatform(PlatformCapabilities.Desktop with { SupportsReveal = false }, _timeline);
        var reply = (await AskAsync(Dispatcher(service, platform), Session(), MessageTypes.JobReveal, new { jobId = jobs[0].Id }))
            .GetPayload<HostActionResult>();

        Assert.False(reply.Ok);
        Assert.Contains("not available here", reply.Message);
        Assert.Empty(platform.Revealed);
        Assert.False(WebSessionProfile.WebCapabilities(PlatformCapabilities.Desktop).SupportsReveal);
    }

    // ---- job.update with newFileName ----------------------------------------------------------------------------

    [Fact]
    public async Task RenameMovesTheFinishedFileWithinItsFolder()
    {
        var (service, jobs) = await SeededAsync(Finished("before.bin"));
        var reply = (await AskAsync(Dispatcher(service), Session(), MessageTypes.JobUpdate,
            new { jobId = jobs[0].Id, newFileName = "  after.bin " })).GetPayload<JobActionResponse>();

        Assert.True(reply.Ok, reply.Error);
        Assert.Equal("after.bin", reply.Job!.FileName);
        Assert.Equal(Path.Combine(Folder, "after.bin"), reply.Job.FinalFilePath);
        Assert.True(File.Exists(Path.Combine(Folder, "after.bin")));
        Assert.False(File.Exists(Path.Combine(Folder, "before.bin")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("sub/after.bin")]
    [InlineData("..\\after.bin")]
    [InlineData("C:\\elsewhere\\after.bin")]
    [InlineData("a:b.bin")]
    [InlineData("what?.bin")]
    [InlineData("..")]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    [InlineData("trailing.")]
    public async Task RenameRefusesANameThatIsNotABareFileName(string name)
    {
        var (service, jobs) = await SeededAsync(Finished("kept.bin"));
        var reply = (await AskAsync(Dispatcher(service), Session(), MessageTypes.JobUpdate,
            new { jobId = jobs[0].Id, newFileName = name })).GetPayload<JobActionResponse>();

        Assert.False(reply.Ok);
        Assert.False(string.IsNullOrWhiteSpace(reply.Error));
        Assert.True(File.Exists(Path.Combine(Folder, "kept.bin")));
        Assert.Equal("kept.bin", service.GetJob(jobs[0].Id)!.FileName);
        Assert.Single(Directory.GetFiles(Folder));
    }

    [Fact]
    public async Task RenameNeverReplacesAnotherFile()
    {
        var (service, jobs) = await SeededAsync(Finished("mine.bin"));
        File.WriteAllText(Path.Combine(Folder, "theirs.bin"), "other");
        var reply = (await AskAsync(Dispatcher(service), Session(), MessageTypes.JobUpdate,
            new { jobId = jobs[0].Id, newFileName = "theirs.bin" })).GetPayload<JobActionResponse>();

        Assert.False(reply.Ok);
        Assert.Equal("The target file already exists.", reply.Error);
        Assert.Equal("other", File.ReadAllText(Path.Combine(Folder, "theirs.bin")));
        Assert.Equal("done", File.ReadAllText(Path.Combine(Folder, "mine.bin")));
    }

    [Fact]
    public async Task RenameIsOnlyForAFinishedDownloadWhoseFileIsThere()
    {
        var unfinished = Finished("running.bin");
        unfinished.State = DownloadState.Paused;
        unfinished.CompletedAt = null;
        var missing = Finished("missing.bin");
        var (service, _) = await SeededAsync(unfinished, missing);
        File.Delete(missing.FinalFilePath);
        var dispatcher = Dispatcher(service);
        var session = Session();

        var notDone = (await AskAsync(dispatcher, session, MessageTypes.JobUpdate,
            new { jobId = unfinished.Id, newFileName = "renamed.bin" })).GetPayload<JobActionResponse>();
        Assert.False(notDone.Ok);
        Assert.Equal("Rename is available after the download completes.", notDone.Error);

        var gone = (await AskAsync(dispatcher, session, MessageTypes.JobUpdate,
            new { jobId = missing.Id, newFileName = "renamed.bin" })).GetPayload<JobActionResponse>();
        Assert.False(gone.Ok);
        Assert.Equal("The completed file no longer exists.", gone.Error);

        var both = (await AskAsync(dispatcher, session, MessageTypes.JobUpdate,
            new { jobId = missing.Id, newFileName = "renamed.bin", newFilePath = Path.Combine(Folder, "x.bin") })).GetPayload<JobActionResponse>();
        Assert.False(both.Ok);
        Assert.Equal("Give a new name or a new path, not both.", both.Error);
        Assert.False(File.Exists(Path.Combine(Folder, "renamed.bin")));
    }

    [Fact]
    public async Task RenameThatOnlyChangesTheCaseRenamesTheFileAndTheDownload()
    {
        var (service, jobs) = await SeededAsync(Finished("report.pdf"));
        var reply = (await AskAsync(Dispatcher(service), Session(), MessageTypes.JobUpdate,
            new { jobId = jobs[0].Id, newFileName = "Report.pdf" })).GetPayload<JobActionResponse>();

        Assert.True(reply.Ok, reply.Error);
        Assert.Equal("Report.pdf", reply.Job!.FileName);
        Assert.Equal(Path.Combine(Folder, "Report.pdf"), reply.Job.FinalFilePath);
        Assert.Equal("Report.pdf", service.GetJob(jobs[0].Id)!.FileName);
        // The name on disk, as the folder lists it (Windows keeps one file whichever case is asked for).
        Assert.Equal(["Report.pdf"], Directory.GetFiles(Folder).Select(Path.GetFileName).ToArray());
        Assert.Equal("done", File.ReadAllText(Path.Combine(Folder, "Report.pdf")));
    }

    // ---- a web session (a browser on another device) --------------------------------------------------------------

    [Fact]
    public async Task AWebSessionCannotRevealAndCannotRenameOrRedownloadOutsideTheAppsFolders()
    {
        var outsideJob = Finished("outside.bin");
        Directory.CreateDirectory(Path.Combine(_root, "downloads"));
        var insideJob = Finished("inside.bin");
        var insidePath = Path.Combine(_root, "downloads", "inside.bin");
        File.Move(insideJob.FinalFilePath, insidePath);
        insideJob.DestinationDirectory = Path.GetDirectoryName(insidePath)!;
        insideJob.FinalFilePath = insidePath;
        var (service, _) = await SeededAsync(outsideJob, insideJob);
        var web = await WebAsync(service: service);

        // No file manager is opened for a session on another device, not even for a download inside the folders.
        var reveal = (await AskAsync(web.Dispatcher, web.Session, MessageTypes.JobReveal, new { jobId = insideJob.Id })).GetPayload<HostActionResult>();
        Assert.False(reveal.Ok);
        Assert.Contains("not available here", reveal.Message);
        Assert.Empty(web.Platform.Revealed);

        var rename = (await AskAsync(web.Dispatcher, web.Session, MessageTypes.JobUpdate,
            new { jobId = outsideJob.Id, newFileName = "renamed.bin" })).GetPayload<JobActionResponse>();
        Assert.False(rename.Ok);
        Assert.Equal(CallerScope.OutsideJobRefused, rename.Error);
        // Not even why a name would be bad is said for a download outside them.
        var badName = (await AskAsync(web.Dispatcher, web.Session, MessageTypes.JobUpdate,
            new { jobId = outsideJob.Id, newFileName = "a/b" })).GetPayload<JobActionResponse>();
        Assert.Equal(CallerScope.OutsideJobRefused, badName.Error);
        Assert.True(File.Exists(outsideJob.FinalFilePath));

        var redownload = (await AskAsync(web.Dispatcher, web.Session, MessageTypes.JobRedownload, new { jobId = outsideJob.Id })).GetPayload<JobActionResponse>();
        Assert.False(redownload.Ok);
        Assert.Equal(CallerScope.OutsideJobRefused, redownload.Error);
        Assert.Equal(2, service.GetSnapshot().Jobs.Count);

        // Inside the folders the web session may rename.
        var inside = (await AskAsync(web.Dispatcher, web.Session, MessageTypes.JobUpdate,
            new { jobId = insideJob.Id, newFileName = "kept-inside.bin" })).GetPayload<JobActionResponse>();
        Assert.True(inside.Ok, inside.Error);
        Assert.True(File.Exists(Path.Combine(_root, "downloads", "kept-inside.bin")));

        // The same desktop's own pipe session may show the file.
        var fromPipe = (await AskAsync(web.Dispatcher, web.Pipe, MessageTypes.JobReveal, new { jobId = outsideJob.Id })).GetPayload<HostActionResult>();
        Assert.True(fromPipe.Ok, fromPipe.Message);
        Assert.Equal([outsideJob.FinalFilePath], web.Platform.Revealed);
    }

    // ---- job.redownload ------------------------------------------------------------------------------------------

    [Fact]
    public async Task RedownloadAddsTheSameAddressAgainWithTheRenamePolicyAndKeepsTheFile()
    {
        var finished = Finished("again.bin");
        finished.CategoryId = "documents";
        var (service, _) = await SeededAsync(finished);
        var reply = (await AskAsync(Dispatcher(service), Session(), MessageTypes.JobRedownload, new { jobId = finished.Id }))
            .GetPayload<JobActionResponse>();

        Assert.True(reply.Ok, reply.Error);
        var added = reply.Job!;
        Assert.NotEqual(finished.Id, added.Id);
        Assert.Equal(finished.Url, added.Url);
        Assert.Equal(Folder, added.DestinationDirectory);
        Assert.Equal("rename", added.ConflictPolicy);
        Assert.Equal("documents", added.CategoryId);
        // Saved under a new name: the finished file is never the new download's target.
        Assert.NotEqual(finished.FinalFilePath, added.FinalFilePath);
        Assert.Equal("done", File.ReadAllText(finished.FinalFilePath));
        Assert.Equal(2, service.GetSnapshot().Jobs.Count);
    }

    [Fact]
    public async Task RedownloadIsRefusedWhileRunningForAProvidersFileAndForAHiddenAddress()
    {
        var paused = Finished("paused.bin");
        paused.State = DownloadState.Paused;
        paused.CompletedAt = null;
        var remote = Finished("remote.bin", "remote://torbox/torrents/1/2");
        var masked = Finished("masked.bin", $"https://api.torbox.app/v1/api/webdl/requestdl?token={UrlRedactor.Mask}&web_id=1");
        Assert.True(ProviderLinks.IsMasked(masked.Url));
        var (service, _) = await SeededAsync(paused, remote, masked);
        var dispatcher = Dispatcher(service);
        var session = Session();

        async Task<JobActionResponse> Redownload(DownloadJob job) =>
            (await AskAsync(dispatcher, session, MessageTypes.JobRedownload, new { jobId = job.Id })).GetPayload<JobActionResponse>();

        var running = await Redownload(paused);
        Assert.False(running.Ok);
        Assert.Equal("Stop the current download before redownloading.", running.Error);
        Assert.Contains("provider account", (await Redownload(remote)).Error);
        Assert.Contains("key hidden", (await Redownload(masked)).Error);
        Assert.False((await Redownload(new DownloadJob { Id = Guid.NewGuid() })).Ok);
        Assert.Equal(3, service.GetSnapshot().Jobs.Count);
    }
}
