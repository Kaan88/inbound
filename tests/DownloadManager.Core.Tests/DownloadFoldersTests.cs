using DownloadManager.Core;

namespace DownloadManager.Core.Tests;

public sealed class DownloadFoldersTests
{
    [Fact]
    public void FavoritesAreTrimmedNamedAndWithoutRepeats()
    {
        var result = DownloadFolders.Normalize(
        [
            new FavoriteFolder { Name = "  Movies ", Path = @" D:\Media\Movies " },
            new FavoriteFolder { Name = "Again", Path = @"d:\media\movies\" },
            new FavoriteFolder { Name = "", Path = @"E:\Shows" },
            new FavoriteFolder { Name = "Empty", Path = "  " },
            new FavoriteFolder { Name = "", Path = @"F:\" },
        ]);

        Assert.Equal(3, result.Count);
        Assert.Equal(("Movies", @"D:\Media\Movies"), (result[0].Name, result[0].Path));
        Assert.Equal(("Shows", @"E:\Shows"), (result[1].Name, result[1].Path));
        Assert.Equal((@"F:\", @"F:\"), (result[2].Name, result[2].Path));
    }

    [Fact]
    public void FavoritesKeepTheirOrderAndStopAtTheLimit()
    {
        var many = Enumerable.Range(0, 80).Select(i => new FavoriteFolder { Name = $"N{i}", Path = $@"D:\F{i}" });
        var result = DownloadFolders.Normalize(many);
        Assert.Equal(DownloadFolders.MaxFavorites, result.Count);
        Assert.Equal("N0", result[0].Name);
        Assert.Equal("N49", result[^1].Name);
    }

    [Fact]
    public void RecentPutsTheNewFolderFirstAndKeepsFive()
    {
        var recent = new List<string>();
        foreach (var folder in new[] { @"C:\A", @"C:\B", @"C:\C", @"C:\D", @"C:\E", @"C:\F", @"c:\b\" })
            recent = DownloadFolders.Remember(recent, folder);

        Assert.Equal([@"c:\b\", @"C:\F", @"C:\E", @"C:\D", @"C:\C"], recent);
    }

    [Theory]
    [InlineData("/downloads/movies/x", "/downloads")]
    [InlineData("/downloads", "/downloads")]
    [InlineData("/downloadsextra/x", "/")]
    [InlineData("/data/tv/show", "/data/tv")]
    [InlineData("/app/state", "/")]
    public void UnixFoldersUseTheirOwnMountNotTheRoot(string path, string mount)
        => Assert.Equal(mount, FolderSpaceMeter.MountFor(path, ["/", "/downloads/", "/data", "/data/tv", "/proc"]));

    [Fact]
    public void AWindowsFolderThatDoesNotExistYetIsMeasuredOnItsDrive()
    {
        var folder = Path.Combine(Path.GetTempPath(), "inbound-" + Guid.NewGuid().ToString("N"), "not-yet");
        var space = FolderSpaceMeter.Measure(folder);
        Assert.NotNull(space.FreeBytes);
        Assert.NotNull(space.TotalBytes);
        Assert.True(space.FreeBytes <= space.TotalBytes);
        Assert.False(space.InsideContainer);
    }
}
