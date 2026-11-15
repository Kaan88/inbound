using DownloadManager.Contracts;
using DownloadManager.Desktop.WinUI;

namespace DownloadManager.Core.Tests;

/// <summary>SITES.5: the words of New folder, Rename and Delete on server.</summary>
public sealed class SiteChangeRulesTests
{
    [Fact]
    public void ANewFolderGetsAFreeName()
    {
        Assert.Equal("New folder", SiteChangeRules.NewFolderName(["a", "b"]));
        Assert.Equal("New folder (2)", SiteChangeRules.NewFolderName(["new folder"]));
        Assert.Equal("New folder (3)", SiteChangeRules.NewFolderName(["New folder", "New folder (2)"]));
    }

    [Fact]
    public void RenameSelectsTheNameWithoutItsExtension()
    {
        Assert.Equal(12, SiteChangeRules.RenameSelectionLength("report.final.pdf", isFolder: false));
        Assert.Equal(".hidden".Length, SiteChangeRules.RenameSelectionLength(".hidden", isFolder: false));
        Assert.Equal("my.folder".Length, SiteChangeRules.RenameSelectionLength("my.folder", isFolder: true));
    }

    [Fact]
    public void TheDeleteQuestionSaysWhatGoesAndThatItStaysGone()
    {
        var one = SiteChangeRules.DeleteQuestion([new SiteTransferNameDto { Name = "music", IsFolder = true }],
            new SiteCountResultDto { Ok = true, Files = 12, Folders = 1, Bytes = 3 * 1024 * 1024 }, "NAS");
        Assert.StartsWith("Delete the folder \"music\" from the server? This removes 12 files (3.0 MB) on NAS, including everything inside the folder.", one);
        Assert.EndsWith("It does not go to a Recycle Bin and can't be undone.", one);

        var many = SiteChangeRules.DeleteQuestion([new SiteTransferNameDto { Name = "a" }, new SiteTransferNameDto { Name = "b" }],
            new SiteCountResultDto { Ok = true, Files = 1, Bytes = 10, SizeUnknown = true, Missing = 1 }, "NAS");
        Assert.Contains("Delete 2 items", many);
        Assert.Contains("1 file (at least 10 B) on NAS.", many);
        Assert.Contains("One of them is no longer there.", many);

        var empty = SiteChangeRules.DeleteQuestion([new SiteTransferNameDto { Name = "blank", IsFolder = true }],
            new SiteCountResultDto { Ok = true, Folders = 1 }, "NAS");
        Assert.Equal("Delete the folder \"blank\" from the server? There are no files in the folder. It does not go to a Recycle Bin and can't be undone.", empty);
    }

    [Fact]
    public void OnThisPcHasAWordForEachStateAndShowsOnlyForALinkedSite()
    {
        Assert.Equal("Same", SitesBrowseRules.HereText(SiteHereStates.Same));
        Assert.Equal("Older here", SitesBrowseRules.HereText(SiteHereStates.OlderHere));
        Assert.Equal("Partly here", SitesBrowseRules.HereText(SiteHereStates.SomeOfIt));
        Assert.Equal("Not here", SitesBrowseRules.HereText(SiteHereStates.NotHere));
        Assert.Equal("Different here", SitesBrowseRules.HereText(SiteHereStates.Different));
        Assert.Equal("Folder here", SitesBrowseRules.HereText(SiteHereStates.FolderHere));
        Assert.Equal("", SitesBrowseRules.HereText(null));
        Assert.True(SitesBrowseRules.ShowsHere(new SiteDto { LocalFolder = @"C:\Sites
as" }));
        Assert.False(SitesBrowseRules.ShowsHere(new SiteDto()));
        Assert.False(SitesBrowseRules.ShowsHere(null));
    }

    [Fact]
    public void OnlyTheFailedItemsAreSaid()
    {
        Assert.Null(SiteChangeRules.DeleteFailures(new SiteChangeResultDto { Ok = true, Items = [new() { Name = "a", Ok = true }] }));
        Assert.Equal("b: Permission denied.", SiteChangeRules.DeleteFailures(new SiteChangeResultDto
        {
            Ok = false,
            Items = [new() { Name = "a", Ok = true }, new() { Name = "b", Ok = false, Message = "Permission denied." }],
        }));
        Assert.Equal("A transfer is using it.", SiteChangeRules.DeleteFailures(new SiteChangeResultDto { Ok = false, Message = "A transfer is using it." }));
    }
}
