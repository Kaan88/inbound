using System.Diagnostics;
using DownloadManager.Contracts;
using DownloadManager.Core;
using DownloadManager.Core.Providers;
using DownloadManager.Runtime;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// UI.B19: deleting an unfinished download left a folder behind. Every delete path (desktop, web UI, CLI,
/// extension popup, Delete Completed, package delete) ends in <see cref="DownloadManagerService.RemoveAsync"/>
/// or <see cref="DownloadManagerService.RemoveManyAsync"/>, so these tests drive those two.
/// </summary>
public sealed partial class RuntimeServiceTests
{
    private static string[] Entries(string folder)
        => Directory.Exists(folder)
            ? Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(folder, path)).Order(StringComparer.Ordinal).ToArray()
            : ["<missing>"];

    private string SlowLink(string name, int size = 4 * 1024 * 1024, int seed = 71)
        => $"{_server.BaseAddress}/slow/{name}?size={size}&seed={seed}&bps=131072";

    [Theory]
    [InlineData(true, 1, false)]
    [InlineData(false, 1, false)]
    [InlineData(true, 4, false)]
    [InlineData(false, 4, false)]
    [InlineData(true, 4, true)]
    [InlineData(false, 4, true)]
    public async Task DeletingAnUnfinishedDownloadInItsOwnFolderLeavesOnlyKeptData(bool deleteFile, int connections, bool pausedFirst)
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var added = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = SlowLink($"plain-{connections}.bin"),
            DestinationDirectory = ctx.Destination,
            FileName = $"plain-{connections}.bin",
            Connections = connections,
            Source = "cli",
        });
        Assert.True(added.Accepted, added.Error);
        await ctx.WaitFor(added.Job!.Id, j => j.ReceivedBytes > 32_768);
        if (pausedFirst)
        {
            Assert.True((await ctx.Service.PauseAsync(added.Job.Id)).Ok);
            await ctx.WaitFor(added.Job.Id, j => j.State == "paused");
            Assert.NotEmpty(Entries(ctx.Destination));
        }

        var removed = await ctx.Service.RemoveAsync(added.Job.Id, deleteFile);

        Assert.True(removed.Ok, removed.Error);
        if (!deleteFile)
            // Kept on purpose (CLI remove --keep-file, docs\chrome-extension-privacy.md), running or not.
            Assert.Equal([Path.GetFileName(added.Job.PartFilePath)], Entries(ctx.Destination));
        else
            Assert.Empty(Entries(ctx.Destination));
        Assert.True(Directory.Exists(ctx.Destination), "The folder the user chose stays.");
    }

    [Fact]
    public async Task DeletingAPausedProviderFileButKeepingItsDataKeepsItsFolderUntilEmpty()
    {
        var broker = new ScriptedBroker(job => RemoteLinkResolution.Link(SlowLink(job.FileName)));
        await using var ctx = await ServiceContext.CreateAsync(_server, linkBroker: broker);
        var result = await ctx.Service.AddRemoteJobsAsync(
            [RemoteFile(ctx, "1", "a.bin", relativePath: @"Pack\sub\a.bin")], "remote:job-1");
        var job = AddedJob(result);
        await ctx.WaitFor(job.Id, j => j.ReceivedBytes > 32_768);
        Assert.True((await ctx.Service.PauseAsync(job.Id)).Ok);
        var paused = await ctx.WaitFor(job.Id, j => j.State == "paused");
        Assert.Equal(["Pack", Path.Combine("Pack", "sub"), Path.Combine("Pack", "sub", Path.GetFileName(paused.PartFilePath))], Entries(ctx.Destination));

        Assert.True((await ctx.Service.RemoveAsync(job.Id, deleteFile: false)).Ok);

        var part = Path.GetFileName(paused.PartFilePath);
        Assert.Equal(["Pack", Path.Combine("Pack", "sub"), Path.Combine("Pack", "sub", part)], Entries(ctx.Destination));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DeletingAnUnfinishedProviderFileRemovesTheFoldersMadeForIt(bool deleteFile)
    {
        var broker = new ScriptedBroker(job => RemoteLinkResolution.Link(SlowLink(job.FileName)));
        await using var ctx = await ServiceContext.CreateAsync(_server, linkBroker: broker);
        var result = await ctx.Service.AddRemoteJobsAsync(
            [RemoteFile(ctx, "1", "a.bin", relativePath: @"Pack\sub\a.bin")], "remote:job-1");
        var job = AddedJob(result);
        Assert.Equal(Path.Combine(ctx.Destination, "Pack", "sub"), job.DestinationDirectory);
        await ctx.WaitFor(job.Id, j => j.ReceivedBytes > 32_768);

        var removed = await ctx.Service.RemoveAsync(job.Id, deleteFile);

        Assert.True(removed.Ok, removed.Error);
        if (deleteFile)
            Assert.Empty(Entries(ctx.Destination));
        else
            // Its kept data still needs the folders.
            Assert.Equal(["Pack", Path.Combine("Pack", "sub"), Path.Combine("Pack", "sub", Path.GetFileName(job.PartFilePath))],
                Entries(ctx.Destination));
        Assert.True(Directory.Exists(ctx.Destination), "The folder the package was put in stays.");
    }

    [Fact]
    public async Task DeletingAProviderPackageRemovesItsFolderOnlyWhenTheLastMemberGoes()
    {
        var broker = new ScriptedBroker(job => RemoteLinkResolution.Link(SlowLink(job.FileName)));
        await using var ctx = await ServiceContext.CreateAsync(_server, linkBroker: broker);
        var result = await ctx.Service.AddRemoteJobsAsync(
        [
            RemoteFile(ctx, "1", "a.bin", relativePath: @"Pack\a.bin"),
            RemoteFile(ctx, "2", "b.bin", relativePath: @"Pack\deep\b.bin") with { StartMode = "later" },
        ], "remote:job-1");
        var first = AddedJob(result);
        var second = AddedJob(result, 1);
        await ctx.WaitFor(first.Id, j => j.ReceivedBytes > 32_768);

        Assert.True((await ctx.Service.RemoveAsync(first.Id, deleteFile: true)).Ok);
        // The other member hasn't written anything yet, but its folder is still its own.
        Assert.True(Directory.Exists(Path.Combine(ctx.Destination, "Pack", "deep")));

        Assert.True((await ctx.Service.RemoveAsync(second.Id, deleteFile: true)).Ok);
        Assert.Empty(Entries(ctx.Destination));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DeletingAWholeProviderPackageAtOnceRemovesItsFolders(bool deleteFile)
    {
        var broker = new ScriptedBroker(job => RemoteLinkResolution.Link(SlowLink(job.FileName)));
        await using var ctx = await ServiceContext.CreateAsync(_server, linkBroker: broker);
        var result = await ctx.Service.AddRemoteJobsAsync(
        [
            RemoteFile(ctx, "1", "a.bin", relativePath: @"Pack\a.bin"),
            RemoteFile(ctx, "2", "b.bin", relativePath: @"Pack\deep\b.bin"),
        ], "remote:job-1");
        var first = AddedJob(result);
        var second = AddedJob(result, 1);
        await ctx.WaitFor(first.Id, j => j.ReceivedBytes > 32_768);
        await ctx.WaitFor(second.Id, j => j.ReceivedBytes > 32_768);

        var removed = await ctx.Service.RemoveManyAsync([first.Id, second.Id], deleteFile);

        Assert.True(removed.Ok, removed.Error);
        Assert.Equal(deleteFile ? [] :
            ["Pack", Path.Combine("Pack", Path.GetFileName(first.PartFilePath)), Path.Combine("Pack", "deep"),
             Path.Combine("Pack", "deep", Path.GetFileName(second.PartFilePath))],
            Entries(ctx.Destination));
    }

    [Fact]
    public async Task DeletingAProviderFileKeepsAFolderThatHoldsSomethingElse()
    {
        var broker = new ScriptedBroker(job => RemoteLinkResolution.Link(SlowLink(job.FileName)));
        await using var ctx = await ServiceContext.CreateAsync(_server, linkBroker: broker);
        var pack = Path.Combine(ctx.Destination, "Pack");
        Directory.CreateDirectory(pack);
        await File.WriteAllTextAsync(Path.Combine(pack, "mine.txt"), "the user's");
        var result = await ctx.Service.AddRemoteJobsAsync(
            [RemoteFile(ctx, "1", "a.bin", relativePath: @"Pack\sub\a.bin")], "remote:job-1");
        var job = AddedJob(result);
        await ctx.WaitFor(job.Id, j => j.ReceivedBytes > 32_768);

        Assert.True((await ctx.Service.RemoveAsync(job.Id, deleteFile: true)).Ok);

        Assert.Equal(["Pack", Path.Combine("Pack", "mine.txt")], Entries(ctx.Destination));
    }

    [Fact]
    public async Task DeletingAProviderFileNeverRemovesOrFollowsAJunction()
    {
        var broker = new ScriptedBroker(job => RemoteLinkResolution.Link(SlowLink(job.FileName)));
        await using var ctx = await ServiceContext.CreateAsync(_server, linkBroker: broker);
        var target = Path.Combine(Path.GetDirectoryName(ctx.Destination)!, "elsewhere");
        Directory.CreateDirectory(target);
        var link = Path.Combine(ctx.Destination, "Pack");
        CreateJunction(link, target);
        try
        {
            var result = await ctx.Service.AddRemoteJobsAsync(
                [RemoteFile(ctx, "1", "a.bin", relativePath: @"Pack\sub\a.bin")], "remote:job-1");
            var job = AddedJob(result);
            await ctx.WaitFor(job.Id, j => j.ReceivedBytes > 32_768);

            Assert.True((await ctx.Service.RemoveAsync(job.Id, deleteFile: true)).Ok);

            Assert.True(new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint));
            // Below a link the folder isn't ours to judge, so even the empty one the job made there stays.
            Assert.True(Directory.Exists(Path.Combine(target, "sub")));
        }
        finally
        {
            // Removes the link only; the fixture's recursive delete can't take a junction.
            if (Directory.Exists(link)) Directory.Delete(link);
        }
    }

    [Fact]
    public async Task DeletingAFinishedProviderFileWithoutItsFileKeepsFileAndFolders()
    {
        var broker = new ScriptedBroker(job => RemoteLinkResolution.Link($"{_server.BaseAddress}/file/{job.FileName}?size=4096&seed=3"));
        await using var ctx = await ServiceContext.CreateAsync(_server, linkBroker: broker);
        var result = await ctx.Service.AddRemoteJobsAsync(
            [RemoteFile(ctx, "1", "a.bin", relativePath: @"Pack\a.bin")], "remote:job-1");
        var job = await ctx.WaitFor(AddedJob(result).Id, j => j.State == "completed");

        Assert.True((await ctx.Service.RemoveAsync(job.Id, deleteFile: false)).Ok);
        Assert.Equal(["Pack", Path.Combine("Pack", "a.bin")], Entries(ctx.Destination));

        // A second download into the same folder, removed with its file, takes only what is its own.
        var again = await ctx.Service.AddRemoteJobsAsync(
            [RemoteFile(ctx, "2", "b.bin", relativePath: @"Pack\b.bin", remoteJobId: "job-2")], "remote:job-2");
        var other = await ctx.WaitFor(AddedJob(again).Id, j => j.State == "completed");
        Assert.True((await ctx.Service.RemoveAsync(other.Id, deleteFile: true)).Ok);
        Assert.Equal(["Pack", Path.Combine("Pack", "a.bin")], Entries(ctx.Destination));
    }

    [Fact]
    public async Task DeletingAFinishedProviderFileWithItsFileRemovesTheFolder()
    {
        var broker = new ScriptedBroker(job => RemoteLinkResolution.Link($"{_server.BaseAddress}/file/{job.FileName}?size=4096&seed=3"));
        await using var ctx = await ServiceContext.CreateAsync(_server, linkBroker: broker);
        var result = await ctx.Service.AddRemoteJobsAsync(
            [RemoteFile(ctx, "1", "a.bin", relativePath: @"Pack\sub\a.bin")], "remote:job-1");
        var job = await ctx.WaitFor(AddedJob(result).Id, j => j.State == "completed");

        Assert.True((await ctx.Service.RemoveAsync(job.Id, deleteFile: true)).Ok);

        Assert.Empty(Entries(ctx.Destination));
    }

    [Fact]
    public async Task DeletingADownloadNeverRemovesTheFolderTheUserChoseEvenWhenEmpty()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var chosen = Path.Combine(ctx.Destination, "typed", "new");
        var added = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = SlowLink("chosen.bin"),
            DestinationDirectory = chosen,
            FileName = "chosen.bin",
            Source = "cli",
        });
        Assert.True(added.Accepted, added.Error);
        await ctx.WaitFor(added.Job!.Id, j => j.ReceivedBytes > 32_768);

        Assert.True((await ctx.Service.RemoveAsync(added.Job.Id, deleteFile: true)).Ok);

        Assert.True(Directory.Exists(chosen));
        Assert.Empty(Entries(chosen));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DeletingAMediaDownloadLeavesNoPartialData(bool deleteFile)
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var added = await ctx.Service.AddJobAsync(new AddJobRequest
        {
            Url = $"{_server.BaseAddress}/hls/media.m3u8",
            DestinationDirectory = ctx.Destination,
            FileName = "media.ts",
            Source = "cli",
        });
        Assert.True(added.Accepted, added.Error);
        var job = await ctx.WaitFor(added.Job!.Id, j => j.ReceivedBytes > 0 || j.State is "completed" or "failed");

        var removed = await ctx.Service.RemoveAsync(job.Id, deleteFile);

        Assert.True(removed.Ok, removed.Error);
        var finished = removed.Job!.State == "completed";
        Assert.Equal(!deleteFile && finished ? [Path.GetFileName(removed.Job.FinalFilePath)] : [], Entries(ctx.Destination));
    }

    private static void CreateJunction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
    }
}
