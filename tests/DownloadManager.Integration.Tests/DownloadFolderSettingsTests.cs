using DownloadManager.Contracts;

namespace DownloadManager.Integration.Tests;

public sealed partial class RuntimeServiceTests
{
    [Fact]
    public async Task FavoriteFoldersAreSavedNormalizedAndKeptAcrossRestart()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var settings = ctx.Service.GetSnapshot().Settings;
        var movies = Path.Combine(ctx.Destination, "Movies");
        settings.FavoriteFolders =
        [
            new FavoriteFolderDto { Name = " Films ", Path = movies },
            new FavoriteFolderDto { Name = "Duplicate", Path = movies + Path.DirectorySeparatorChar },
            new FavoriteFolderDto { Name = "", Path = Path.Combine(ctx.Destination, "Shows") },
        ];
        var saved = await ctx.Service.UpdateSettingsAsync(settings);
        Assert.True(saved.Ok);
        Assert.Equal(["Films", "Shows"], saved.Settings.FavoriteFolders.Select(favorite => favorite.Name));

        await ctx.Service.ShutdownAsync();
        await using var restarted = await ServiceContext.CreateAsync(_server, ctx.Paths, ctx.Destination);
        var reloaded = restarted.Service.GetSnapshot().Settings.FavoriteFolders;
        Assert.Equal(["Films", "Shows"], reloaded.Select(favorite => favorite.Name));
        Assert.Equal(movies, reloaded[0].Path);
    }

    [Fact]
    public async Task RecentFoldersFollowAddsAndAnOldClientCopyCannotUndoThem()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var stale = ctx.Service.GetSnapshot().Settings;
        var first = Path.Combine(ctx.Destination, "first");
        var second = Path.Combine(ctx.Destination, "second");
        foreach (var (folder, seed) in new[] { (first, 301), (second, 302) })
        {
            var added = await ctx.Service.AddJobAsync(new AddJobRequest
            {
                Url = $"{_server.BaseAddress}/file/r{seed}.bin?size=4096&seed={seed}",
                DestinationDirectory = folder,
                Source = "cli",
                StartMode = "queue",
            });
            Assert.NotNull(added.Job);
        }

        Assert.Equal([second, first], ctx.Service.GetSnapshot().Settings.RecentDestinationDirectories);

        // A client that read the settings before both adds saves something else.
        stale.RecentDestinationDirectories = [];
        stale.Theme = "Dark";
        Assert.True((await ctx.Service.UpdateSettingsAsync(stale)).Ok);
        Assert.Equal([second, first], ctx.Service.GetSnapshot().Settings.RecentDestinationDirectories);
    }

    [Fact]
    public async Task ARememberedLastFolderJoinsTheRecentFolders()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var settings = ctx.Service.GetSnapshot().Settings;
        var chosen = Path.Combine(ctx.Destination, "provider-choice");
        settings.LastDestinationDirectory = chosen;
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);
        Assert.Equal(chosen, ctx.Service.GetSnapshot().Settings.RecentDestinationDirectories.First());
    }

    [Fact]
    public async Task FolderSpaceIsMeasuredForEachFolderIncludingOnesNotMadeYet()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var later = Path.Combine(ctx.Destination, "not", "made", "yet");
        var result = await ctx.Service.GetFolderSpaceAsync(new FolderSpaceRequest { Paths = [ctx.Destination, later, " ", ctx.Destination] },
            CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(2, result.Folders.Count);
        Assert.All(result.Folders, folder =>
        {
            Assert.NotNull(folder.FreeBytes);
            Assert.True(folder.TotalBytes >= folder.FreeBytes);
            Assert.False(folder.InsideContainer);
        });
    }
}
