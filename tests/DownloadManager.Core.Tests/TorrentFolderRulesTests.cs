using DownloadManager.Contracts;
using DownloadManager.Desktop.WinUI;

namespace DownloadManager.Core.Tests;

/// <summary>T14 (owner, 2026-11-12): torrent category folders apart from other downloads', both able to be turned off.</summary>
public sealed class TorrentFolderRulesTests
{
    private static readonly string Base = Path.Combine(Path.GetTempPath(), "t14-rules");
    private static readonly string Downloads = Path.Combine(Base, "Downloads");
    private static readonly string Torrents = Path.Combine(Base, "Downloads", "Torrents");
    private static readonly string NormalVideo = Path.Combine(Base, "My Video");

    private static AppSettings Settings(bool normalOn, bool torrentOn, string? torrentRoot)
    {
        var settings = new AppSettings
        {
            DefaultDownloadDirectory = Downloads,
            UseCategoryFolders = normalOn,
            UseTorrentCategoryFolders = torrentOn,
            TorrentCategoryFoldersRoot = torrentRoot,
        };
        settings.CategoryFolders["video"] = NormalVideo;
        settings.CustomCategories.Add(new DownloadCategory { Id = "c1", Name = "Books", DestinationDirectory = Path.Combine(Base, "Books") });
        return settings;
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void WithATorrentFolderTheNormalSwitchNeverCounts(bool normalOn, bool torrentOn)
    {
        var settings = Settings(normalOn, torrentOn, Torrents);
        Assert.Equal(torrentOn ? Path.Combine(Torrents, "Video") : Torrents, TorrentFolderRules.Resolve(settings, "video"));
        Assert.Equal(torrentOn ? Path.Combine(Torrents, "Books") : Torrents, TorrentFolderRules.Resolve(settings, "c1"));
        // No category: the torrent folder itself, on or off.
        Assert.Equal(Torrents, TorrentFolderRules.Resolve(settings, "other"));
        Assert.Equal(Torrents, TorrentFolderRules.Resolve(settings, null));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void WithNoTorrentFolderTorrentsShareTheOtherDownloadsFolders(bool normalOn, bool torrentOn)
    {
        var settings = Settings(normalOn, torrentOn, null);
        // "The same folders as other downloads": a category folder only while other downloads use them too.
        var shared = normalOn && torrentOn;
        Assert.Equal(shared ? NormalVideo : Downloads, TorrentFolderRules.Resolve(settings, "video"));
        Assert.Equal(shared ? Path.Combine(Base, "Books") : Downloads, TorrentFolderRules.Resolve(settings, "c1"));
        Assert.Equal(Downloads, TorrentFolderRules.Resolve(settings, "other"));
        settings.LastDestinationDirectory = Path.Combine(Base, "last");
        Assert.Equal(Path.Combine(Base, "last"), TorrentFolderRules.Resolve(settings, "other", useLastFolder: true));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void AProviderTorrentWithNoTorrentFolderGoesToTheDownloadFolderAsBefore(bool normalOn, bool torrentOn)
    {
        // An upgraded install: nothing moves for the provider's torrents, a categorised one included.
        var settings = Settings(normalOn, torrentOn, null);
        Assert.Equal(Downloads, TorrentFolderRules.ResolveForProvider(settings, "video"));
        Assert.Equal(Downloads, TorrentFolderRules.ResolveForProvider(settings, "c1"));
        // With a torrent folder, the torrent folders, as on this PC.
        settings.TorrentCategoryFoldersRoot = Torrents;
        Assert.Equal(torrentOn ? Path.Combine(Torrents, "Video") : Torrents, TorrentFolderRules.ResolveForProvider(settings, "video"));
    }

    [Fact]
    public void TheLastFolderIsNotUsedWhileTorrentsHaveTheirOwnFolder()
    {
        var settings = Settings(true, false, Torrents);
        settings.LastDestinationDirectory = Path.Combine(Base, "last");
        Assert.Equal(Torrents, TorrentFolderRules.Resolve(settings, "other", useLastFolder: true));
    }

    [Fact]
    public void ACategoryNameThatIsNoFolderNameIsMadeSafe()
    {
        var settings = Settings(true, true, Torrents);
        settings.CustomCategories.Add(new DownloadCategory { Id = "c2", Name = "a/b:c" });
        var folder = TorrentFolderRules.Resolve(settings, "c2");
        Assert.Equal(Torrents, Path.GetDirectoryName(folder));
        Assert.DoesNotContain(':', Path.GetFileName(folder));
    }

    [Fact]
    public void ANewInstallHasItsTorrentFolderUnderTheDownloadFolder()
    {
        var settings = AppSettings.NewInstall();
        Assert.Equal(Path.Combine(settings.DefaultDownloadDirectory, "Torrents"), settings.TorrentCategoryFoldersRoot);
        Assert.True(settings.UseTorrentCategoryFolders);
        // Plain new AppSettings (an upgraded file read without the keys) has none: the same folders as other downloads.
        Assert.Null(new AppSettings().TorrentCategoryFoldersRoot);
    }

    [Fact]
    public void TheTorrentDialogsSaveToSaysWhatEmptyMeans()
    {
        Assert.Equal("Each torrent's category folder", TorrentFolderText.SaveToPlaceholder(new SettingsDto { UseTorrentCategoryFolders = true, TorrentCategoryFoldersRoot = Torrents }));
        // An older Host says nothing: as before.
        Assert.Equal("Each torrent's category folder", TorrentFolderText.SaveToPlaceholder(new SettingsDto()));
        Assert.Equal("The torrent folder", TorrentFolderText.SaveToPlaceholder(new SettingsDto { UseTorrentCategoryFolders = false, TorrentCategoryFoldersRoot = Torrents }));
        // No torrent folder: where other downloads go, the last folder used or the download folder.
        Assert.Equal("Same folder as other downloads", TorrentFolderText.SaveToPlaceholder(new SettingsDto { UseTorrentCategoryFolders = false, TorrentCategoryFoldersRoot = "" }));
        Assert.Equal("Same folder as other downloads", TorrentFolderText.SaveToPlaceholder(new SettingsDto { UseTorrentCategoryFolders = true, UseCategoryFolders = false, TorrentCategoryFoldersRoot = "" }));
        Assert.Equal("Each torrent's category folder", TorrentFolderText.SaveToPlaceholder(new SettingsDto { UseTorrentCategoryFolders = true, UseCategoryFolders = true, TorrentCategoryFoldersRoot = "" }));
        // With a torrent folder the other downloads' switch doesn't count.
        Assert.Equal("Each torrent's category folder", TorrentFolderText.SaveToPlaceholder(new SettingsDto { UseTorrentCategoryFolders = true, UseCategoryFolders = false, TorrentCategoryFoldersRoot = Torrents }));
    }

    [Fact]
    public void OptionsSendsOnlyATorrentFolderChangeMadeThere()
    {
        var atOpen = new SettingsDto { UseTorrentCategoryFolders = true, TorrentCategoryFoldersRoot = Torrents };
        var edited = new SettingsDto { UseTorrentCategoryFolders = false, TorrentCategoryFoldersRoot = Torrents };
        // Meanwhile the CLI moved the torrent folder.
        var latest = new SettingsDto { UseTorrentCategoryFolders = true, TorrentCategoryFoldersRoot = Path.Combine(Base, "cli") };
        var sent = OptionsSettingsRebase.Rebase(atOpen, edited, latest);
        Assert.False(sent.UseTorrentCategoryFolders);
        Assert.Equal(Path.Combine(Base, "cli"), sent.TorrentCategoryFoldersRoot);
    }
}
