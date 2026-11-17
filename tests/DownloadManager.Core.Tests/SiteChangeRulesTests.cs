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


    private static string Joined(List<SiteChangeRules.TextPart> parts) => string.Concat(parts.Select(p => p.Bold ? $"**{p.Text}**" : p.Text));

    [Fact]
    public void TheServerDeleteLeadNamesWhatGoesAndWhere()
    {
        var folder = new[] { new SiteTransferNameDto { Name = "backups", IsFolder = true } };
        var counted = SiteChangeRules.CountOf(new SiteCountResultDto { Ok = true, Files = 1842, Folders = 1, Bytes = 92_800_000_000 });
        Assert.Equal("**backups** and the 1,842 files inside it (86.4 GB) will be deleted from **nas.home.lan**.", Joined(SiteChangeRules.DeleteLead(folder, counted, "nas.home.lan")));

        var file = new[] { new SiteTransferNameDto { Name = "notes.txt" } };
        Assert.Equal("**notes.txt** (2.0 KB) will be deleted from **nas**.",
            Joined(SiteChangeRules.DeleteLead(file, SiteChangeRules.CountOf(new SiteCountResultDto { Ok = true, Files = 1, Bytes = 2048 }), "nas")));

        var several = new[] { new SiteTransferNameDto { Name = "a" }, new SiteTransferNameDto { Name = "b", IsFolder = true }, new SiteTransferNameDto { Name = "c" } };
        Assert.Equal("**3 items** and the 5 files inside them (at least 10 B) will be deleted from **nas**. One of them is no longer there.",
            Joined(SiteChangeRules.DeleteLead(several, SiteChangeRules.CountOf(new SiteCountResultDto { Ok = true, Files = 5, Bytes = 10, SizeUnknown = true, Missing = 1 }), "nas")));

        // Counting, or a count that failed: no numbers, still "everything inside".
        Assert.Equal("**backups** and everything inside it will be deleted from **nas**.", Joined(SiteChangeRules.DeleteLead(folder, null, "nas")));
        Assert.Equal("**backups** and everything inside it will be deleted from **nas**.",
            Joined(SiteChangeRules.DeleteLead(folder, SiteChangeRules.CountOf(new SiteCountResultDto { Ok = false }), "nas")));
        Assert.Equal("**blank** (no files inside) will be deleted from **nas**.",
            Joined(SiteChangeRules.DeleteLead([new SiteTransferNameDto { Name = "blank", IsFolder = true }], SiteChangeRules.CountOf(new SiteCountResultDto { Ok = true, Folders = 1 }), "nas")));
        Assert.Equal("**notes.txt** will be deleted from **nas**.", Joined(SiteChangeRules.DeleteLead(file, null, "nas")));
    }

    [Fact]
    public void DeletePermanentlyHasTheServersShapeAndSaysForGood()
    {
        var file = new List<SiteTransferNameDto> { new() { Name = "notes.txt" } };
        var folder = new List<SiteTransferNameDto> { new() { Name = "archive", IsFolder = true } };
        var forGood = SiteChangeRules.DeletePlace.ForGood;
        Assert.Equal("Delete permanently?", SiteChangeRules.DeleteTitle(forGood));
        Assert.Equal("Delete permanently", SiteChangeRules.DeleteButton(forGood));
        Assert.Equal("**notes.txt** (2.0 KB) will be deleted from this PC for good.",
            Joined(SiteChangeRules.DeleteLead(file, SiteChangeRules.CountOf(new SiteCountResultDto { Ok = true, Files = 1, Bytes = 2048 }), forGood, null)));
        Assert.Equal("**archive** and everything inside it will be deleted from this PC for good.", Joined(SiteChangeRules.DeleteLead(folder, null, forGood, null)));
        Assert.Equal("It does not go to the Recycle Bin and can't be brought back.", SiteChangeRules.DeleteWarning(forGood, 1));
        Assert.Equal("They do not go to the Recycle Bin and can't be brought back.", SiteChangeRules.DeleteWarning(forGood, 2));
        // Counted, and a folder needs the tick, as on the server; the Recycle Bin needs neither.
        Assert.True(SiteChangeRules.DeleteCounts(forGood));
        Assert.True(SiteChangeRules.DeleteCounts(SiteChangeRules.DeletePlace.Server));
        Assert.False(SiteChangeRules.DeleteCounts(SiteChangeRules.DeletePlace.RecycleBin));
        Assert.False(SiteChangeRules.DeleteCounts(SiteChangeRules.DeletePlace.NetworkFolder));
        Assert.Equal("I understand that 12 files will be deleted", SiteChangeRules.DeleteTick(folder, SiteChangeRules.CountOf(new SiteCountResultDto { Ok = true, Files = 12 })));
    }

    [Fact]
    public void ANetworkFolderDoesNotPromiseTheRecycleBin()
    {
        var file = new List<SiteTransferNameDto> { new() { Name = "notes.txt" } };
        var network = SiteChangeRules.DeletePlace.NetworkFolder;
        Assert.Equal(network, SiteChangeRules.LocalDeletePlace(@"\\nas\share\docs", networkDrive: false));
        Assert.Equal(network, SiteChangeRules.LocalDeletePlace(@"\\?\UNC\nas\share", networkDrive: false));
        Assert.Equal(network, SiteChangeRules.LocalDeletePlace(@"Z:\docs", networkDrive: true));
        Assert.Equal(SiteChangeRules.DeletePlace.RecycleBin, SiteChangeRules.LocalDeletePlace(@"C:\docs", networkDrive: false));
        Assert.Equal(SiteChangeRules.DeletePlace.RecycleBin, SiteChangeRules.LocalDeletePlace(@"\\?\C:\docs", networkDrive: false));
        Assert.Equal("**notes.txt** will be deleted.", Joined(SiteChangeRules.DeleteLead(file, null, network, null)));
        Assert.DoesNotContain("moved to the Recycle Bin", SiteChangeRules.DeleteWarning(network, 1));
        Assert.Contains("Windows asks", SiteChangeRules.DeleteWarning(network, 1));
        Assert.Equal("Delete", SiteChangeRules.DeleteButton(network));
        Assert.Equal("Move to Recycle Bin", SiteChangeRules.DeleteButton(SiteChangeRules.DeletePlace.RecycleBin));
    }

    [Fact]
    public void OnlyAFullPathThatIsNotADriveIsHandedToADelete()
    {
        Assert.NotNull(SiteChangeRules.LocalDeletePathProblem(""));
        Assert.NotNull(SiteChangeRules.LocalDeletePathProblem("notes.txt"));
        Assert.NotNull(SiteChangeRules.LocalDeletePathProblem(@"docs\notes.txt"));
        if (OperatingSystem.IsWindows())
        {
            Assert.Null(SiteChangeRules.LocalDeletePathProblem(@"C:\Users\me\notes.txt"));
            Assert.Null(SiteChangeRules.LocalDeletePathProblem(@"\\nas\share\docs"));
            Assert.NotNull(SiteChangeRules.LocalDeletePathProblem(@"C:notes.txt"));
            Assert.NotNull(SiteChangeRules.LocalDeletePathProblem(@"\notes.txt"));
            Assert.NotNull(SiteChangeRules.LocalDeletePathProblem(@"C:\"));
            Assert.NotNull(SiteChangeRules.LocalDeletePathProblem(@"D:"));
            Assert.NotNull(SiteChangeRules.LocalDeletePathProblem(@"\\nas\share\"));
            Assert.NotNull(SiteChangeRules.LocalDeletePathProblem(@"C:\docs\*.txt"));
            Assert.NotNull(SiteChangeRules.LocalDeletePathProblem(@"C:\docs\a?.txt"));
            // "." and ".." segments: Win32 folds them away, so the spelling is not the item.
            Assert.NotNull(SiteChangeRules.LocalDeletePathProblem(@"D:\x\.."));
            Assert.NotNull(SiteChangeRules.LocalDeletePathProblem(@"D:\x\..\"));
            Assert.NotNull(SiteChangeRules.LocalDeletePathProblem(@"D:\x\.\y"));
            Assert.NotNull(SiteChangeRules.LocalDeletePathProblem(@"\\nas\share\a\..\b"));
            // A trailing dot or space: "foo." would delete "foo".
            Assert.Equal(SiteChangeRules.UnaddressableName, SiteChangeRules.LocalDeletePathProblem(@"D:\x\foo."));
            Assert.Equal(SiteChangeRules.UnaddressableName, SiteChangeRules.LocalDeletePathProblem(@"D:\x\foo "));
            Assert.Equal(SiteChangeRules.UnaddressableName, SiteChangeRules.LocalDeletePathProblem(@"D:\x.\foo"));
            Assert.Equal("left alone; Windows can't address this name", SiteChangeRules.UnaddressableName);
            // Dots and spaces elsewhere in a name are fine.
            Assert.Null(SiteChangeRules.LocalDeletePathProblem(@"D:\x\.env"));
            Assert.Null(SiteChangeRules.LocalDeletePathProblem(@"D:\x\my file.tar.gz"));
            Assert.Null(SiteChangeRules.LocalDeletePathProblem(@"D:\x\...x"));
            Assert.Null(SiteChangeRules.LocalDeletePathProblem(@"D:\x\docs\"));
        }
        else
        {
            Assert.Null(SiteChangeRules.LocalDeletePathProblem("/home/me/notes.txt"));
            Assert.NotNull(SiteChangeRules.LocalDeletePathProblem("/"));
            Assert.NotNull(SiteChangeRules.LocalDeletePathProblem("/home/*.txt"));
        }
    }

    [Fact]
    public void TheShellsDeleteCodesAreSaidInWords()
    {
        Assert.Equal("Access was denied.", SiteChangeRules.ShellDeleteMessage(0x78));
        foreach (var code in new[] { 0x79, 0x7C, 0x402 }) Assert.Equal("The path is too long or not valid.", SiteChangeRules.ShellDeleteMessage(code));
        Assert.Equal("Cancelled.", SiteChangeRules.ShellDeleteMessage(1223));
        Assert.Equal("Windows could not delete it (0x75).", SiteChangeRules.ShellDeleteMessage(0x75));
        Assert.Equal("Windows could not delete it (0x2).", SiteChangeRules.ShellDeleteMessage(2));
    }

    [Fact]
    public void WhatADeleteOnThisPcLeftIsNamed()
    {
        Assert.Equal("notes.txt is in use", SiteChangeRules.LocalLeftText("notes.txt", new IOException("busy", unchecked((int)0x80070020))));
        Assert.Equal(@"docs\a.bin is in use", SiteChangeRules.LocalLeftText(@"docs\a.bin", new IOException("locked", unchecked((int)0x80070021))));
        Assert.Equal("a is read-only or access was denied", SiteChangeRules.LocalLeftText("a", new UnauthorizedAccessException("no")));
        Assert.Equal("a: odd", SiteChangeRules.LocalLeftText("a", new IOException("odd")));

        Assert.Null(SiteChangeRules.LocalDeleteNote(SiteChangeRules.DeletePlace.ForGood, []));
        Assert.Equal("Not deleted: a is in use", SiteChangeRules.LocalDeleteNote(SiteChangeRules.DeletePlace.ForGood, ["a is in use"]));
        Assert.Equal("Not moved to the Recycle Bin: a; b; c; and 2 more", SiteChangeRules.LocalDeleteNote(SiteChangeRules.DeletePlace.RecycleBin, ["a", "b", "c", "d", "e"]));
        Assert.Equal("Not deleted: a", SiteChangeRules.LocalDeleteNote(SiteChangeRules.DeletePlace.NetworkFolder, ["a"]));
        // Stopped after too many were left: how many more is not known.
        Assert.Equal("Not deleted: a; b; c; and more", SiteChangeRules.LocalDeleteNote(SiteChangeRules.DeletePlace.ForGood, ["a", "b", "c", "d"], cutShort: true));
    }

    [Fact]
    public void TheLocalDeleteLeadSaysRecycleBin()
    {
        var folder = new[] { new SiteTransferNameDto { Name = "archive", IsFolder = true } };
        Assert.Equal("**archive** and everything inside it will be moved to the Recycle Bin.", Joined(SiteChangeRules.DeleteLead(folder, null, null)));
        Assert.Contains("Recycle Bin", SiteChangeRules.DeleteLocalWarning);
        Assert.Contains("cannot bring them back", SiteChangeRules.DeleteServerWarning);
        Assert.Equal("Delete on This PC?", SiteChangeRules.DeleteLocalTitle);
        Assert.Equal("Delete on server?", SiteChangeRules.DeleteServerTitle);
    }

    [Fact]
    public void OnlyAFolderAsksForTheTickAndItsWordsFollowTheCount()
    {
        var folder = new[] { new SiteTransferNameDto { Name = "backups", IsFolder = true } };
        Assert.Null(SiteChangeRules.DeleteTick([new SiteTransferNameDto { Name = "a" }], null));
        Assert.Equal("I understand that 1,842 files will be deleted", SiteChangeRules.DeleteTick(folder, SiteChangeRules.CountOf(new SiteCountResultDto { Ok = true, Files = 1842 })));
        Assert.Equal("I understand that at least 7 files will be deleted", SiteChangeRules.DeleteTick(folder, SiteChangeRules.CountOf(new SiteCountResultDto { Ok = true, Files = 7, SizeUnknown = true })));
        Assert.Equal("I understand that everything inside will be deleted", SiteChangeRules.DeleteTick(folder, null));
        Assert.Equal("I understand that everything inside will be deleted", SiteChangeRules.DeleteTick(folder, SiteChangeRules.CountOf(new SiteCountResultDto { Ok = false })));
        Assert.Equal(SiteChangeRules.CountingLine, SiteChangeRules.CountNote(folder, null));
        Assert.Null(SiteChangeRules.CountNote(folder, SiteChangeRules.CountOf(new SiteCountResultDto { Ok = true, Files = 1 })));
        Assert.NotNull(SiteChangeRules.CountNote(folder, SiteChangeRules.CountOf(new SiteCountResultDto { Ok = false })));
        Assert.Null(SiteChangeRules.CountNote([new SiteTransferNameDto { Name = "a" }], null));
    }
    [Fact]
    public void OnThisPcHasAWordForEachStateAndShowsOnlyForALinkedSite()
    {
        Assert.Equal("Same", SitesBrowseRules.HereText(SiteHereStates.Same));
        Assert.Equal("Older here", SitesBrowseRules.HereText(SiteHereStates.OlderHere));
        Assert.Equal("Some of it", SitesBrowseRules.HereText(SiteHereStates.SomeOfIt));
        Assert.Equal("Not here", SitesBrowseRules.HereText(SiteHereStates.NotHere));
        Assert.Equal("Different here", SitesBrowseRules.HereText(SiteHereStates.Different));
        Assert.Equal("", SitesBrowseRules.HereText(SiteHereStates.FolderHere));
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
