using DownloadManager.Core;
using DownloadManager.Persistence;

namespace DownloadManager.Integration.Tests;

/// <summary>T14: what a new install and an upgraded one get for the torrent folders, and how the torrent folder follows the download folder.</summary>
public sealed class TorrentFolderSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "test-work", "t14-" + Guid.NewGuid().ToString("N"));
    private readonly StoragePaths _paths;

    public TorrentFolderSettingsTests()
    {
        _paths = new StoragePaths(Path.Combine(_root, "state"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task ANewInstallGetsItsOwnTorrentFolderUnderTheDownloadFolder()
    {
        var settings = await new JsonSettingsStore(_paths).LoadAsync();
        Assert.True(settings.UseTorrentCategoryFolders);
        Assert.True(settings.UseCategoryFolders);
        Assert.Equal(Path.Combine(settings.DefaultDownloadDirectory, "Torrents"), settings.TorrentCategoryFoldersRoot);
        // Saved, so the next start reads the same.
        var again = await new JsonSettingsStore(_paths).LoadAsync();
        Assert.Equal(settings.TorrentCategoryFoldersRoot, again.TorrentCategoryFoldersRoot);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnUpgradeKeepsTorrentsWhereTheyWent(bool useCategoryFolders)
    {
        // Settings as a build from before T14 saved them: no torrent folder keys.
        var downloads = Path.Combine(_root, "dl");
        Directory.CreateDirectory(_paths.Root);
        await File.WriteAllTextAsync(_paths.SettingsFile, $$"""
            {
              "schemaVersion": {{AppSettings.CurrentSchemaVersion}},
              "defaultDownloadDirectory": {{System.Text.Json.JsonSerializer.Serialize(downloads)}},
              "useCategoryFolders": {{(useCategoryFolders ? "true" : "false")}},
              "categoryFoldersRoot": {{System.Text.Json.JsonSerializer.Serialize(downloads)}}
            }
            """);
        var store = new JsonSettingsStore(_paths);
        var settings = await store.LoadAsync();
        Assert.Null(settings.TorrentCategoryFoldersRoot);
        Assert.Equal(useCategoryFolders, settings.UseTorrentCategoryFolders);
        Assert.Equal(useCategoryFolders, settings.UseCategoryFolders);
        Assert.Contains("useTorrentCategoryFolders", await File.ReadAllTextAsync(_paths.SettingsFile));

        // Once stored, the two switches are apart: the normal one changing no longer moves the torrent one.
        settings.UseCategoryFolders = !useCategoryFolders;
        await store.SaveAsync(settings);
        var later = await new JsonSettingsStore(_paths).LoadAsync();
        Assert.Equal(useCategoryFolders, later.UseTorrentCategoryFolders);
        Assert.Equal(!useCategoryFolders, later.UseCategoryFolders);
        Assert.Null(later.TorrentCategoryFoldersRoot);
    }

    [Fact]
    public async Task TheTorrentFolderMovesWithTheDownloadFolderUnlessItWasChosen()
    {
        var store = new JsonSettingsStore(_paths);
        var first = Path.Combine(_root, "dl1");
        var settings = AppSettings.NewInstall();
        settings.DefaultDownloadDirectory = first;
        settings.TorrentCategoryFoldersRoot = TorrentFolderRules.DefaultRoot(first);
        await store.SaveAsync(settings);
        settings = await store.LoadAsync();

        var second = Path.Combine(_root, "dl2");
        settings.DefaultDownloadDirectory = second;
        await store.SaveAsync(settings);
        settings = await store.LoadAsync();
        Assert.Equal(Path.Combine(second, "Torrents"), settings.TorrentCategoryFoldersRoot);

        var chosen = Path.Combine(_root, "elsewhere");
        settings.TorrentCategoryFoldersRoot = chosen;
        settings.DefaultDownloadDirectory = Path.Combine(_root, "dl3");
        await store.SaveAsync(settings);
        Assert.Equal(chosen, (await store.LoadAsync()).TorrentCategoryFoldersRoot);
    }
}
