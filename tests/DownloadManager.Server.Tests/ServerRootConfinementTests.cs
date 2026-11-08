using DownloadManager.Contracts;
using TestServer;

namespace DownloadManager.Server.Tests;

/// <summary>
/// Review finding (D1b): the roots gated folders.browse and folders.create only. A client could still have the server
/// write a download, move a finished one, save a folder setting, or read a container or archive anywhere on its disk.
/// </summary>
public sealed class ServerRootConfinementTests
{
    [Fact]
    public async Task AnAddIsRefusedOutsideTheRootsIncludingThroughALinkAndAcceptedInsideThem()
    {
        await using var files = await TestServerHost.StartAsync();
        await using var server = await ServerHarness.StartAsync();
        var outside = Directory.CreateDirectory(Path.Combine(server.Work, "outside")).FullName;
        var url = $"{files.BaseAddress}/file/confined.bin?size=1024&seed=3";

        var refused = await server.AskAsync<AddJobResponse>(MessageTypes.JobAdd, new AddJobRequest { Url = url, DestinationDirectory = outside, StartMode = "later" });
        Assert.False(refused.Accepted);
        Assert.Equal("invalid_destination", refused.ErrorCode);
        var escape = await server.AskAsync<AddJobResponse>(MessageTypes.JobAdd, new AddJobRequest
        {
            Url = url, DestinationDirectory = Path.Combine(server.Root, "..", "outside"), StartMode = "later",
        });
        Assert.False(escape.Accepted);

        var link = Path.Combine(server.Root, "elsewhere");
        if (TryLink(link, outside))
        {
            var throughLink = await server.AskAsync<AddJobResponse>(MessageTypes.JobAdd, new AddJobRequest { Url = url, DestinationDirectory = link, StartMode = "later" });
            Assert.False(throughLink.Accepted);
        }

        Assert.Empty(Directory.EnumerateFileSystemEntries(outside));

        var inside = Path.Combine(server.Root, "Movies");
        var accepted = await server.AskAsync<AddJobResponse>(MessageTypes.JobAdd, new AddJobRequest { Url = url, DestinationDirectory = inside, StartMode = "later" });
        Assert.True(accepted.Accepted, accepted.Error);
        Assert.Equal(Path.GetFullPath(inside), accepted.Job!.DestinationDirectory);
    }

    [Fact]
    public async Task FolderSettingsOutsideTheRootsAreRefusedAndAddsDontFallBackOnThem()
    {
        await using var server = await ServerHarness.StartAsync();
        var outside = Path.Combine(server.Work, "outside");

        var settings = (await server.AskAsync<SettingsResult>(MessageTypes.SettingsGet, new { })).Settings!;
        settings.DefaultDownloadDirectory = outside;
        var refused = await server.AskAsync<SettingsResult>(MessageTypes.SettingsUpdate, new SettingsUpdateRequest { Settings = settings });
        Assert.False(refused.Ok);
        Assert.Equal(Path.GetFullPath(server.Root), refused.Settings!.DefaultDownloadDirectory);
        // Saving would have created it.
        Assert.False(Directory.Exists(outside));

        settings = (await server.AskAsync<SettingsResult>(MessageTypes.SettingsGet, new { })).Settings!;
        settings.LastDestinationDirectory = outside;
        Assert.False((await server.AskAsync<SettingsResult>(MessageTypes.SettingsUpdate, new SettingsUpdateRequest { Settings = settings })).Ok);

        settings = (await server.AskAsync<SettingsResult>(MessageTypes.SettingsGet, new { })).Settings!;
        settings.CategoryFolders["video"] = outside;
        Assert.False((await server.AskAsync<SettingsResult>(MessageTypes.SettingsUpdate, new SettingsUpdateRequest { Settings = settings })).Ok);

        // A folder inside the roots is saved as before.
        settings = (await server.AskAsync<SettingsResult>(MessageTypes.SettingsGet, new { })).Settings!;
        settings.CategoryFolders["video"] = Path.Combine(server.Root, "Video");
        var saved = await server.AskAsync<SettingsResult>(MessageTypes.SettingsUpdate, new SettingsUpdateRequest { Settings = settings });
        Assert.True(saved.Ok, saved.Error);
    }

    [Fact]
    public async Task AFinishedDownloadCantBeMovedOutsideTheRoots()
    {
        await using var files = await TestServerHost.StartAsync();
        await using var server = await ServerHarness.StartAsync();
        await using var client = await server.ConnectAsync();
        await client.ReadUntilAsync(message => message.Type == MessageTypes.JobsSnapshot);
        var added = await client.AskAsync<AddJobResponse>(MessageTypes.JobAdd, new AddJobRequest
        {
            Url = $"{files.BaseAddress}/file/move-me.bin?size=4096&seed=5",
            StartMode = "startNow",
        });
        Assert.True(added.Accepted, added.Error);
        await client.ReadUntilAsync(message =>
            message.Type == MessageTypes.JobEvent && message.GetPayload<JobEventDto>() is { Job.State: "completed" } done && done.Job.Id == added.Job!.Id,
            TimeSpan.FromSeconds(60));

        var outside = Directory.CreateDirectory(Path.Combine(server.Work, "outside")).FullName;
        var refused = await client.AskAsync<JobActionResponse>(MessageTypes.JobUpdate, new JobUpdateRequest
        {
            JobId = added.Job!.Id, NewFilePath = Path.Combine(outside, "move-me.bin"),
        });
        Assert.False(refused.Ok);
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside));

        var moved = await client.AskAsync<JobActionResponse>(MessageTypes.JobUpdate, new JobUpdateRequest
        {
            JobId = added.Job!.Id, NewFilePath = Path.Combine(server.Root, "kept", "moved.bin"),
        });
        Assert.True(moved.Ok, moved.Error);
        Assert.True(File.Exists(Path.Combine(server.Root, "kept", "moved.bin")));
    }

    [Fact]
    public async Task AContainerOrArchiveOutsideTheRootsIsNotRead()
    {
        await using var server = await ServerHarness.StartAsync();
        var outside = Directory.CreateDirectory(Path.Combine(server.Work, "outside")).FullName;
        var container = Path.Combine(outside, "links.rsdf");
        await File.WriteAllTextAsync(container, "not a real container");

        var preview = await server.AskAsync<RemoteContainerPreviewResult>(MessageTypes.RemoteContainerPreview, new RemoteContainerPreviewRequest { Path = container });
        Assert.False(preview.Ok);
        Assert.Contains("outside", preview.Error);
        // Inside a root the same file is read, and what it holds is the answer.
        var insideContainer = Path.Combine(server.Root, "links.rsdf");
        File.Copy(container, insideContainer);
        var read = await server.AskAsync<RemoteContainerPreviewResult>(MessageTypes.RemoteContainerPreview, new RemoteContainerPreviewRequest { Path = insideContainer });
        Assert.DoesNotContain("outside", read.Error ?? string.Empty);

        var archive = Path.Combine(outside, "set.zip");
        await File.WriteAllBytesAsync(archive, [0x50, 0x4B, 0x05, 0x06, .. new byte[18]]);
        var extract = await server.AskAsync<ArchiveExtractResult>(ArchiveMessageTypes.Extract, new ArchiveExtractRequest { FilePath = archive });
        Assert.False(extract.Ok);
        Assert.Contains("outside", extract.Error);

        var folder = await server.AskAsync<ArchiveSettingsResult>(ArchiveMessageTypes.SettingsSet, new ArchiveSettingsUpdate { Folder = outside, Destination = "folder" });
        Assert.False(folder.Ok);
        Assert.NotEqual(outside, folder.Settings.Folder);
    }

    private static bool TryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Windows without Developer Mode can't make one; Linux always can.
            Assert.True(OperatingSystem.IsWindows(), $"Could not make a link: {ex.Message}");
            return false;
        }
    }
}
