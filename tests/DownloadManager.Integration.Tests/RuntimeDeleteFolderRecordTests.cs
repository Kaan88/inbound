using System.Runtime.InteropServices;
using System.Text;
using DownloadManager.Contracts;
using DownloadManager.Core;
using DownloadManager.Core.Providers;
using DownloadManager.Persistence;
using DownloadManager.Runtime;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// UI.B19, second pass: only the folders the app recorded as made for a download are removed, user and named folders
/// stay whatever their path looks like, a limited caller removes nothing outside its folders, an unfinished download
/// never deletes a final file it didn't write, a cancel takes its folders too, the quarantine folder goes once empty,
/// and a removal a crash interrupted takes the folders at the next start.
/// </summary>
public sealed partial class RuntimeServiceTests
{
    private ScriptedBroker SlowBroker() => new(job => RemoteLinkResolution.Link(SlowLink(job.FileName)));

    private ScriptedBroker QuickBroker() =>
        new(job => RemoteLinkResolution.Link($"{_server.BaseAddress}/file/{job.FileName}?size=4096&seed=3"));

    [Fact]
    public async Task AnEmptyFolderTheUserMadeBeforeTheDownloadStays()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server, linkBroker: SlowBroker());
        var media = Path.Combine(ctx.Destination, "Media");
        var show = Path.Combine(media, "Show");
        Directory.CreateDirectory(show);
        var result = await ctx.Service.AddRemoteJobsAsync(
            [RemoteFile(ctx, "1", "ep1.mkv", relativePath: @"Show\ep1.mkv") with { DestinationRoot = media }], "remote:job-1");
        var job = AddedJob(result);
        await ctx.WaitFor(job.Id, j => j.ReceivedBytes > 32_768);

        Assert.True((await ctx.Service.RemoveAsync(job.Id, deleteFile: true)).Ok);

        Assert.True(Directory.Exists(show), "The user's own folder isn't one the app made.");
        Assert.Empty(Entries(show));
    }

    [Fact]
    public async Task AFinishedDownloadMovedIntoAFolderOfTheSameNameLeavesThatFolder()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server, linkBroker: QuickBroker());
        var result = await ctx.Service.AddRemoteJobsAsync(
            [RemoteFile(ctx, "1", "a.bin", relativePath: @"Pack\a.bin")], "remote:job-1");
        var job = await ctx.WaitFor(AddedJob(result).Id, j => j.State == "completed");
        var keep = Path.Combine(ctx.Destination, "Keep", "Pack");
        Directory.CreateDirectory(keep);
        var moved = await ctx.Service.UpdateJobAsync(new JobUpdateRequest { JobId = job.Id, NewFilePath = Path.Combine(keep, "a.bin") });
        Assert.True(moved.Ok, moved.Error);

        Assert.True((await ctx.Service.RemoveAsync(job.Id, deleteFile: true)).Ok);

        // The folder it was moved into is the user's; the one the app made for it, now empty, goes.
        Assert.Equal(["Keep", Path.Combine("Keep", "Pack")], Entries(ctx.Destination));
    }

    [Theory]
    [InlineData("extraction")]
    [InlineData("recent")]
    [InlineData("long-path-form")]
    [InlineData("short-name")]
    [InlineData("share")]
    public async Task AFolderNamedInTheSettingsInAnyFormStays(string how)
    {
        await using var ctx = await ServiceContext.CreateAsync(_server, linkBroker: QuickBroker());
        var result = await ctx.Service.AddRemoteJobsAsync(
            [RemoteFile(ctx, "1", "a.bin", relativePath: @"Pack Folder Name\sub\a.bin")], "remote:job-1");
        var job = await ctx.WaitFor(AddedJob(result).Id, j => j.State == "completed");
        var pack = Path.Combine(ctx.Destination, "Pack Folder Name");
        var sub = Path.Combine(pack, "sub");
        var named = how switch
        {
            "long-path-form" => @"\\?\" + pack,
            "short-name" => ShortName(pack),
            "share" => AdminShareForm(pack),
            _ => pack,
        };
        if (named is null || (how != "extraction" && how != "recent" && !Directory.Exists(named)))
            return; // This machine has no such form (8.3 names off, or no administrative share): nothing to test.

        if (how == "extraction")
        {
            ctx.Service.ExtractionFolder = () => named;
        }
        else
        {
            var settings = ctx.Service.GetSnapshot().Settings;
            if (how == "recent")
            {
                // Recent, but no longer the last folder.
                settings.LastDestinationDirectory = named;
                Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);
                settings = ctx.Service.GetSnapshot().Settings;
                settings.LastDestinationDirectory = ctx.Destination;
                Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);
                Assert.Contains(named, ctx.Service.GetSnapshot().Settings.RecentDestinationDirectories, StringComparer.OrdinalIgnoreCase);
            }
            else
            {
                settings.FavoriteFolders = [new FavoriteFolderDto { Name = "Kept", Path = named }];
                Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);
            }
        }

        Assert.True((await ctx.Service.RemoveAsync(job.Id, deleteFile: true)).Ok);

        Assert.True(Directory.Exists(pack), $"The folder named as {how} stays.");
        // The folder below it is the download's own, and isn't named anywhere.
        Assert.False(Directory.Exists(sub));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ALimitedCallerRemovesNoFolderOutsideItsFolders(bool callerHoldsTheFolders)
    {
        await using var ctx = await ServiceContext.CreateAsync(_server, linkBroker: QuickBroker());
        var result = await ctx.Service.AddRemoteJobsAsync(
            [RemoteFile(ctx, "1", "a.bin", relativePath: @"Pack\sub\a.bin")], "remote:job-1");
        var job = await ctx.WaitFor(AddedJob(result).Id, j => j.State == "completed");
        File.Delete(job.FinalFilePath);
        var elsewhere = Path.Combine(Path.GetDirectoryName(ctx.Destination)!, "web-root");
        Directory.CreateDirectory(elsewhere);
        var caller = new CallerScope(new PathRoots([callerHoldsTheFolders ? ctx.Destination : elsewhere]));

        // Without the file, so the caller may remove it from the list even though its folders aren't the caller's.
        Assert.True((await ctx.Service.RemoveAsync(job.Id, deleteFile: false, caller: caller)).Ok);

        Assert.Equal(callerHoldsTheFolders ? [] : ["Pack", Path.Combine("Pack", "sub")], Entries(ctx.Destination));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DeletingAnUnfinishedDownloadNeverDeletesAFinalFileItDidNotWrite(bool viaCancel)
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var mine = Path.Combine(ctx.Destination, "mine.bin");
        await File.WriteAllTextAsync(mine, "the user's own file");
        var added = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = SlowLink("mine.bin"),
            DestinationDirectory = ctx.Destination,
            FileName = "mine.bin",
            ConflictPolicy = "overwrite",
            Source = "cli",
        });
        Assert.True(added.Accepted, added.Error);
        Assert.Equal(mine, added.Job!.FinalFilePath, ignoreCase: true);
        await ctx.WaitFor(added.Job.Id, j => j.ReceivedBytes > 32_768);
        Assert.True((await ctx.Service.PauseAsync(added.Job.Id)).Ok);
        await ctx.WaitFor(added.Job.Id, j => j.State == "paused");

        var result = viaCancel
            ? await ctx.Service.CancelAsync(added.Job.Id, deleteFile: true)
            : await ctx.Service.RemoveAsync(added.Job.Id, deleteFile: true);

        Assert.True(result.Ok, result.Error);
        Assert.Equal("the user's own file", await File.ReadAllTextAsync(mine));
        Assert.Equal(["mine.bin"], Entries(ctx.Destination));
    }

    [Fact]
    public async Task ACancelThatDeletesTheDataTakesTheFoldersAndAStartMakesThemAgain()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server, linkBroker: SlowBroker());
        var result = await ctx.Service.AddRemoteJobsAsync(
            [RemoteFile(ctx, "1", "a.bin", relativePath: @"Pack\sub\a.bin")], "remote:job-1");
        var job = AddedJob(result);
        await ctx.WaitFor(job.Id, j => j.ReceivedBytes > 32_768);

        Assert.True((await ctx.Service.CancelAsync(job.Id, deleteFile: true)).Ok);
        // A running one takes them away once it has stopped.
        await WaitUntil(() => Entries(ctx.Destination).Length == 0);
        Assert.NotNull(ctx.Service.GetJob(job.Id));

        Assert.True((await ctx.Service.ResumeAsync(job.Id)).Ok);
        await ctx.WaitFor(job.Id, j => j.ReceivedBytes > 32_768);
        Assert.True(Directory.Exists(Path.Combine(ctx.Destination, "Pack", "sub")));

        Assert.True((await ctx.Service.RemoveAsync(job.Id, deleteFile: true)).Ok);
        Assert.Empty(Entries(ctx.Destination));
    }

    [Fact]
    public async Task ACancelOfAStoppedDownloadTakesItsFoldersAtOnce()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server, linkBroker: SlowBroker());
        var result = await ctx.Service.AddRemoteJobsAsync(
            [RemoteFile(ctx, "1", "a.bin", relativePath: @"Pack\a.bin")], "remote:job-1");
        var job = AddedJob(result);
        await ctx.WaitFor(job.Id, j => j.ReceivedBytes > 32_768);
        Assert.True((await ctx.Service.PauseAsync(job.Id)).Ok);
        await ctx.WaitFor(job.Id, j => j.State == "paused");

        Assert.True((await ctx.Service.CancelAsync(job.Id, deleteFile: true)).Ok);

        Assert.Empty(Entries(ctx.Destination));
    }

    [Fact]
    public async Task TheQuarantineFolderGoesOnceItIsEmpty()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var orphan = Path.Combine(ctx.Destination, "left.bin.0123456789abcdef0123456789abcdef.part");
        await File.WriteAllTextAsync(orphan, "partial");
        var quarantine = Path.Combine(ctx.Destination, DownloadManagerService.OrphanQuarantineFolderName);

        var moved = await ctx.Service.CleanOrphanPartFilesAsync(delete: false, CancellationToken.None);
        Assert.True(moved.Ok, moved.Error);
        var kept = Path.Combine(quarantine, "left.bin.0123456789abcdef0123456789abcdef.part");
        Assert.True(File.Exists(kept));

        // What a quarantine holds is kept for the user (P5.9): a Delete clean leaves it and its folder.
        Assert.True((await ctx.Service.CleanOrphanPartFilesAsync(delete: true, CancellationToken.None)).Ok);
        Assert.True(File.Exists(kept));

        // Once the user has emptied it, the next clean removes it.
        File.Delete(kept);
        Assert.True((await ctx.Service.CleanOrphanPartFilesAsync(delete: true, CancellationToken.None)).Ok);
        Assert.Empty(Entries(ctx.Destination));
    }

    [Fact]
    public async Task AnEmptyQuarantineFolderGoesAtTheNextStart()
    {
        var destination = TestFolder(out var paths);
        try
        {
            await using (var ctx = await ServiceContext.CreateAsync(_server, paths, destination)) { }
            var quarantine = Path.Combine(destination, DownloadManagerService.OrphanQuarantineFolderName);
            Directory.CreateDirectory(quarantine);

            await using var restarted = await ServiceContext.CreateAsync(_server, paths, destination);

            Assert.False(Directory.Exists(quarantine));
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(destination)!, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task AnEmptyQuarantineFolderInAFolderMadeForADownloadDoesNotKeepIt()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server, linkBroker: QuickBroker());
        var result = await ctx.Service.AddRemoteJobsAsync(
            [RemoteFile(ctx, "1", "a.bin", relativePath: @"Pack\a.bin")], "remote:job-1");
        var job = await ctx.WaitFor(AddedJob(result).Id, j => j.State == "completed");
        Directory.CreateDirectory(Path.Combine(ctx.Destination, "Pack", DownloadManagerService.OrphanQuarantineFolderName));

        Assert.True((await ctx.Service.RemoveAsync(job.Id, deleteFile: true)).Ok);

        Assert.Empty(Entries(ctx.Destination));
    }

    [Fact]
    public async Task ARemovalACrashInterruptedTakesTheFoldersAtTheNextStart()
    {
        var destination = TestFolder(out var paths);
        try
        {
            Guid id;
            string part;
            await using (var ctx = await ServiceContext.CreateAsync(_server, paths, destination, linkBroker: SlowBroker()))
            {
                var result = await ctx.Service.AddRemoteJobsAsync(
                    [RemoteFile(ctx, "1", "a.bin", relativePath: @"Pack\sub\a.bin")], "remote:job-1");
                var job = AddedJob(result);
                await ctx.WaitFor(job.Id, j => j.ReceivedBytes > 32_768);
                Assert.True((await ctx.Service.PauseAsync(job.Id)).Ok);
                await ctx.WaitFor(job.Id, j => j.State == "paused");
                (id, part) = (job.Id, job.PartFilePath);
            }

            // As left by a kill right after the removal marker was saved.
            var jobs = new JsonJobStore(paths);
            var stored = (await jobs.LoadAsync(id))!;
            Assert.Equal([Path.Combine(destination, "Pack", "sub"), Path.Combine(destination, "Pack")], stored.MadeFolders);
            stored.RemovalPending = true;
            stored.DeleteFileOnCancel = true;
            await jobs.SaveAsync(stored);
            Assert.True(File.Exists(part));

            await using var restarted = await ServiceContext.CreateAsync(_server, paths, destination, linkBroker: SlowBroker());
            Assert.Null(restarted.Service.GetJob(id));
            Assert.Empty(Entries(destination));
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(destination)!, recursive: true); } catch (IOException) { }
        }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow > deadline) throw new TimeoutException("The condition was not met in time.");
            await Task.Delay(50);
        }
    }

    /// <summary>The folder's 8.3 form, or null when this volume makes none.</summary>
    private static string? ShortName(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var buffer = new StringBuilder(1024);
        var length = GetShortPathNameW(path, buffer, buffer.Capacity);
        if (length == 0 || length >= buffer.Capacity) return null;
        var shortPath = buffer.ToString();
        return string.Equals(shortPath, path, StringComparison.OrdinalIgnoreCase) ? null : shortPath;
    }

    /// <summary>The folder through this PC's administrative share, as a mapped drive would show a share.</summary>
    private static string? AdminShareForm(string path)
        => OperatingSystem.IsWindows() && Path.GetPathRoot(path) is { Length: 3 } root && root[1] == ':'
            ? $@"\\localhost\{root[0]}$\{path[3..]}"
            : null;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetShortPathNameW(string longPath, StringBuilder shortPath, int length);
}
