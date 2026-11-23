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

    private static SettingsDto Dto(bool normalOn, bool torrentOn, string? torrentRoot, string? last = null) => new()
    {
        DefaultDownloadDirectory = Downloads,
        UseCategoryFolders = normalOn,
        UseTorrentCategoryFolders = torrentOn,
        TorrentCategoryFoldersRoot = torrentRoot,
        LastDestinationDirectory = last,
        CategoryFolders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["video"] = NormalVideo },
        CustomCategories = [new CategoryDefinitionDto { Id = "c1", Name = "Books", DestinationDirectory = Path.Combine(Base, "Books") }],
    };

    /// <summary>A placeholder without the words that say why it is that folder ("E:\ (the last folder used)").</summary>
    private static string PathOnly(string placeholder)
    {
        var note = placeholder.LastIndexOf(" (", StringComparison.Ordinal);
        return note < 0 ? placeholder : placeholder[..note];
    }

    [Fact]
    public void TheTorrentDialogsSaveToNamesTheLastFolderUsed()
    {
        // T14.B1 (owner, 2026-11-23): no torrent folder and kinds off, so torrents share other downloads' folder: the last
        // folder used, which was E: with no setting pointing there.
        var settings = Dto(normalOn: true, torrentOn: false, torrentRoot: "", last: @"E:\");
        Assert.Equal(@"E:\ (the last folder used)", TorrentFolderText.SaveToPlaceholder(settings, "video"));
        Assert.Equal(@"Torrents go to E:\ (the last folder used).", TorrentFolderText.WhereTorrentsGo(settings));
        // A browser session doesn't count the last folder: the download folder.
        Assert.Equal($"{Downloads} (the download folder)", TorrentFolderText.SaveToPlaceholder(settings, "video", useLastFolder: false));
    }

    [Fact]
    public void TheTorrentDialogsSaveToNamesTheKindFolderUnderTheTorrentFolder()
    {
        var settings = Dto(normalOn: false, torrentOn: true, torrentRoot: Torrents);
        Assert.Equal(Path.Combine(Torrents, "Video"), TorrentFolderText.SaveToPlaceholder(settings, "video"));
        Assert.Equal($"Torrents go to {Path.Combine(Torrents, "Video")}.", TorrentFolderText.WhereTorrentsGo(settings, "video"));
        // A kind without a folder of its own, an unknown one and a torrent with no kind go to the torrent folder.
        Assert.Equal(Torrents, TorrentFolderText.SaveToPlaceholder(settings, "other"));
        Assert.Equal($"Torrents go to {Torrents}.", TorrentFolderText.WhereTorrentsGo(settings, "other"));
        // Kind not known yet: where its folder is, and what the folders are.
        Assert.Equal(Path.Combine(Torrents, "<kind: Video, Music, …>"), TorrentFolderText.SaveToPlaceholder(settings));
        Assert.Equal($"Torrents go to {Torrents}, in a folder for its kind: Video, Music, Programs, Documents or Compressed.",
            TorrentFolderText.WhereTorrentsGo(settings));
    }

    [Fact]
    public void WithKindsOffTheTorrentFolderIsTheFolder()
    {
        var settings = Dto(normalOn: true, torrentOn: false, torrentRoot: Torrents);
        Assert.Equal(Torrents, TorrentFolderText.SaveToPlaceholder(settings, "video"));
        Assert.Equal($"Torrents go to {Torrents}.", TorrentFolderText.WhereTorrentsGo(settings, "video"));
        Assert.Equal($"Torrents go to {Torrents}.", TorrentFolderText.WhereTorrentsGo(settings));
    }

    [Fact]
    public void WithNoTorrentFolderTheKindFolderIsTheOtherDownloadsOne()
    {
        var settings = Dto(normalOn: true, torrentOn: true, torrentRoot: "");
        Assert.Equal(NormalVideo, TorrentFolderText.SaveToPlaceholder(settings, "video"));
        Assert.Equal($"Torrents go to {NormalVideo}.", TorrentFolderText.WhereTorrentsGo(settings, "video"));
        // A kind with no folder of its own has none to share: the last folder used, or the download folder.
        settings.LastDestinationDirectory = @"E:\";
        Assert.Equal(@"E:\ (the last folder used)", TorrentFolderText.SaveToPlaceholder(settings, "music"));
        Assert.Equal($"{Downloads} (the download folder)", TorrentFolderText.SaveToPlaceholder(settings, "music", useLastFolder: false));
        // Kind not known yet: where other downloads go, and that a kind's folder may take it instead.
        settings.LastDestinationDirectory = null;
        Assert.Equal($"Torrents go to {Downloads} (the download folder), unless a folder is set for their kind of download.",
            TorrentFolderText.WhereTorrentsGo(settings));
    }

    [Fact]
    public void AnOlderHostKeepsTheOldWords()
    {
        var older = new SettingsDto { DefaultDownloadDirectory = Downloads, TorrentCategoryFoldersRoot = Torrents };
        Assert.Null(older.UseTorrentCategoryFolders);
        Assert.Equal("Each torrent's category folder", TorrentFolderText.SaveToPlaceholder(older));
        Assert.Equal("Each torrent's category folder", TorrentFolderText.WhereTorrentsGo(older));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void WhatTheDialogShowsIsTheFolderTheHostUses(bool normalOn, bool torrentOn)
    {
        // T14.B1: the words follow TorrentFolderRules.Resolve, so a torrent goes where the dialog said it would.
        foreach (var root in new[] { null, Torrents })
        {
            foreach (var category in new[] { "video", "c1", "other" })
            {
                var app = Settings(normalOn, torrentOn, root);
                app.LastDestinationDirectory = Path.Combine(Base, "last");
                var dto = Dto(normalOn, torrentOn, root, Path.Combine(Base, "last"));
                Assert.Equal(Path.GetFullPath(TorrentFolderRules.Resolve(app, category, useLastFolder: true)),
                    Path.GetFullPath(PathOnly(TorrentFolderText.SaveToPlaceholder(dto, category))));
            }
        }
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
