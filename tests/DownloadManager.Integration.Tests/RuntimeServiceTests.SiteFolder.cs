using DownloadManager.Contracts;

namespace DownloadManager.Integration.Tests;

public sealed partial class RuntimeServiceTests
{
    /// <summary>SITES.11 (decisions 1 and 2): a site download with no folder named and no linked folder follows the category rules.</summary>
    [Fact]
    public async Task ASiteDownloadWithNoFolderGoesByTheCategoryRulesOneFolderPerBatch()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server);
        var settings = ctx.Service.GetSnapshot().Settings;
        var video = Path.Combine(ctx.Destination, "Video");
        var other = Path.Combine(ctx.Destination, "Other");
        settings.UseCategoryFolders = true;
        settings.CategoryFolders["video"] = video;
        settings.CategoryFolders["other"] = other;
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);
        var downloads = Path.GetFullPath(ctx.Service.GetSnapshot().Settings.DefaultDownloadDirectory);

        static SiteTransferNameDto File(string name) => new() { Name = name };
        static SiteTransferNameDto Folder(string name) => new() { Name = name, IsFolder = true };

        // One category with a folder: there.
        Assert.Equal(Path.GetFullPath(video), ctx.Service.SiteDownloadFolder([File("a.mkv"), File("b.mp4")]));
        // Mixed: the download folder, never one file's category folder for all of them.
        Assert.Equal(downloads, ctx.Service.SiteDownloadFolder([File("a.mkv"), File("notes.pdf")]));
        // A folder is "other", and so is its company of other "other" things.
        Assert.Equal(Path.GetFullPath(other), ctx.Service.SiteDownloadFolder([Folder("isos")]));
        Assert.Equal(downloads, ctx.Service.SiteDownloadFolder([Folder("isos"), File("a.mkv")]));

        // Category folders off: the download folder, and the last folder used is not looked at.
        settings = ctx.Service.GetSnapshot().Settings;
        settings.UseCategoryFolders = false;
        Assert.True((await ctx.Service.UpdateSettingsAsync(settings)).Ok);
        Assert.Equal(downloads, ctx.Service.SiteDownloadFolder([File("a.mkv")]));
    }
}
