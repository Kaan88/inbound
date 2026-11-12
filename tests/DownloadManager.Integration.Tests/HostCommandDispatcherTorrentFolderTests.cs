using DownloadManager.Contracts;
using DownloadManager.Core;
using DownloadManager.Persistence;

namespace DownloadManager.Integration.Tests;

/// <summary>T14 (owner, 2026-11-12): torrents have their own category folders, apart from other downloads', and both can be turned off.</summary>
public sealed partial class HostCommandDispatcherTests
{
    /// <summary>A video torrent inspected and added with Save to empty, as the torrent dialog does.</summary>
    private async Task<(string Inspected, string Added)> TorrentFolderOfAsync(Action<AppSettings> settings)
    {
        var service = await TorrentServiceAsync(settings: settings);
        var dispatcher = Dispatcher(service);
        var file = Path.Combine(_root, "film.torrent");
        var (bytes, _) = SingleFileTorrent("Film.mkv", 4096);
        await File.WriteAllBytesAsync(file, bytes);
        var inspect = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Inspect, new TorrentInspectRequest { Input = file }))
            .GetPayload<TorrentInspectResult>();
        Assert.True(inspect.Ok, inspect.Error);
        var added = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Add, new TorrentAddRequest { Input = file, StartMode = "later" }))
            .GetPayload<TorrentAddResult>();
        Assert.True(added.Ok, added.Error);
        return (inspect.Destination!, added.Job!.DestinationDirectory);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task T14_TorrentsUseTheTorrentFoldersWhateverFoldersByCategorySays(bool normalOn, bool torrentOn)
    {
        var downloads = Path.Combine(_root, "dl");
        var torrents = Path.Combine(_root, "torrents");
        var (inspected, added) = await TorrentFolderOfAsync(s =>
        {
            s.DefaultDownloadDirectory = downloads;
            s.UseCategoryFolders = normalOn;
            s.UseTorrentCategoryFolders = torrentOn;
            s.TorrentCategoryFoldersRoot = torrents;
        });
        var expected = torrentOn ? Path.Combine(torrents, "Video") : torrents;
        Assert.Equal(expected, inspected);
        Assert.Equal(expected, added);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task T14_WithNoTorrentFolderTorrentsShareTheOtherDownloadsFolders(bool normalOn, bool torrentOn)
    {
        var downloads = Path.Combine(_root, "dl");
        var video = Path.Combine(_root, "my video");
        var (inspected, added) = await TorrentFolderOfAsync(s =>
        {
            s.DefaultDownloadDirectory = downloads;
            s.CategoryFolders["video"] = video;
            s.UseCategoryFolders = normalOn;
            s.UseTorrentCategoryFolders = torrentOn;
            s.TorrentCategoryFoldersRoot = null;
        });
        // "The same folders as other downloads": a category folder only while both switches are on.
        var expected = normalOn && torrentOn ? video : downloads;
        Assert.Equal(expected, inspected);
        Assert.Equal(expected, added);
    }

    [Fact]
    public async Task T14_ATypedSaveToStillWins()
    {
        var service = await TorrentServiceAsync(settings: s => s.TorrentCategoryFoldersRoot = Path.Combine(_root, "torrents"));
        var dispatcher = Dispatcher(service);
        var file = Path.Combine(_root, "film.torrent");
        await File.WriteAllBytesAsync(file, SingleFileTorrent("Film.mkv", 4096).Item1);
        var typed = Path.Combine(_root, "typed");
        var inspect = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Inspect, new TorrentInspectRequest { Input = file, Destination = typed }))
            .GetPayload<TorrentInspectResult>();
        Assert.Equal(typed, inspect.Destination);
    }

    [Fact]
    public async Task T14_TheTorrentFolderSettingsAreSavedAndAnOlderClientKeepsThem()
    {
        var service = await TorrentServiceAsync();
        var settings = service.GetSnapshot().Settings;
        var torrents = Path.Combine(_root, "torrents");
        settings.UseTorrentCategoryFolders = false;
        settings.TorrentCategoryFoldersRoot = torrents;
        Assert.True((await service.UpdateSettingsAsync(settings)).Ok);
        Assert.Equal((false, torrents), (service.GetSettings().UseTorrentCategoryFolders, service.GetSettings().TorrentCategoryFoldersRoot));

        // A client from before T14 sends neither: both stay.
        var older = service.GetSnapshot().Settings;
        older.UseTorrentCategoryFolders = null;
        older.TorrentCategoryFoldersRoot = null;
        Assert.True((await service.UpdateSettingsAsync(older)).Ok);
        Assert.Equal((false, torrents), (service.GetSettings().UseTorrentCategoryFolders, service.GetSettings().TorrentCategoryFoldersRoot));

        // Empty is "the same folders as other downloads".
        var shared = service.GetSnapshot().Settings;
        shared.TorrentCategoryFoldersRoot = "";
        Assert.True((await service.UpdateSettingsAsync(shared)).Ok);
        Assert.Equal(string.Empty, service.GetSettings().TorrentCategoryFoldersRoot);
        var reloaded = await new JsonSettingsStore(_paths).LoadAsync();
        Assert.Null(reloaded.TorrentCategoryFoldersRoot);
        Assert.False(reloaded.UseTorrentCategoryFolders);

        var relative = service.GetSnapshot().Settings;
        relative.TorrentCategoryFoldersRoot = "torrents";
        var refused = await service.UpdateSettingsAsync(relative);
        Assert.False(refused.Ok);
        Assert.Contains("torrent folder", refused.Error);
    }
}
