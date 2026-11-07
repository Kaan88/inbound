using DownloadManager.Core.Providers;
using DownloadManager.Runtime.Remote;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// Files chosen from a .torrent file's own list, found again in the provider's once it has one. The rule is the one a probe of
/// the real service found: the provider's name for a file is the torrent's folder and the file's path, and a torrent of one
/// file gets a folder made up from its name, so the first folder is left out and the rest must agree exactly, with the size.
/// </summary>
public sealed class RemoteFileChoiceTests
{
    private static RemoteFile File(string id, string path, long size) => new() { FileId = id, Path = path, Size = size };

    [Theory]
    [InlineData("Pack/sub/a.bin", "sub/a.bin")]
    [InlineData("Pack/a.bin", "a.bin")]
    [InlineData("a.bin", "a.bin")]
    [InlineData(@"Pack\sub\a.bin", "sub/a.bin")]
    public void AFilesKeyLeavesOutItsFirstFolder(string path, string key)
        => Assert.Equal(new RemoteFileKey(key, 5), RemoteFileKey.Of(path, 5));

    [Theory]
    [InlineData("../outside.bin")]
    [InlineData("C:/rooted.bin")]
    [InlineData("")]
    public void APathThatIsntSafeHasNoKey(string path) => Assert.Null(RemoteFileKey.Of(path, 5));

    [Fact]
    public void TheProvidersFolderAndTheTorrentsNeedNotAgree()
    {
        // The provider can name the folder its own way. A single file's made-up folder is the name without its extension.
        Assert.Equal(RemoteFileKey.Of("Pack/sub/a.bin", 5), RemoteFileKey.Of("Pack (1)/sub/a.bin", 5));
        Assert.Equal(RemoteFileKey.Of("movie.mkv", 5), RemoteFileKey.Of("movie/movie.mkv", 5));
    }

    [Fact]
    public void CaseAndSizeBothCount()
    {
        Assert.NotEqual(RemoteFileKey.Of("Pack/A.bin", 5), RemoteFileKey.Of("Pack/a.bin", 5));
        Assert.NotEqual(RemoteFileKey.Of("Pack/a.bin", 5), RemoteFileKey.Of("Pack/a.bin", 6));
    }

    [Fact]
    public void PathsChosenBeforeTheListAreFoundByTheirKeys()
    {
        RemoteFile[] files = [File("7", "Pack/sub/a.bin", 10), File("8", "Pack/b.bin", 20), File("9", "Pack/c.bin", 30)];
        var (ids, unfound, missing) = RemoteChildPlanner.MatchChoice([], [RemoteFileKey.Of("pack/sub/a.bin", 10)!, RemoteFileKey.Of("pack/c.bin", 30)!], files);

        Assert.Equal(["7", "9"], ids);
        Assert.Empty(unfound);
        Assert.Equal(0, missing);
    }

    [Fact]
    public void WhatIsntFoundIsKeptAndCounted()
    {
        RemoteFile[] files = [File("7", "Pack/a.bin", 10), File("8", "Pack/b.bin", 20)];
        var renamed = RemoteFileKey.Of("pack/b2.bin", 20)!;
        var resized = RemoteFileKey.Of("pack/a.bin", 11)!;
        var (ids, unfound, missing) = RemoteChildPlanner.MatchChoice(["8"], [renamed, resized], files);

        Assert.Equal(["8"], ids);
        Assert.Equal([renamed, resized], unfound);
        Assert.Equal(2, missing);
    }

    [Fact]
    public void AnIdTheListDoesntHaveCountsAsMissingAndStays()
    {
        // Kept, so that a second look at the same list says the same.
        var (ids, unfound, missing) = RemoteChildPlanner.MatchChoice(["7", "gone"], [], [File("7", "Pack/a.bin", 10)]);

        Assert.Equal(["7", "gone"], ids);
        Assert.Empty(unfound);
        Assert.Equal(1, missing);
    }

    [Fact]
    public void AFileChosenTwiceIsTakenOnce()
    {
        var (ids, _, missing) = RemoteChildPlanner.MatchChoice(["7"], [RemoteFileKey.Of("pack/a.bin", 10)!], [File("7", "Pack/a.bin", 10)]);

        Assert.Equal(["7"], ids);
        Assert.Equal(0, missing);
    }

    [Theory]
    [InlineData(RemoteSelectionMode.AskIfCached, true, RemoteSelectionMode.AskWhenReady)]
    [InlineData(RemoteSelectionMode.AskIfCached, false, RemoteSelectionMode.All)]
    [InlineData(RemoteSelectionMode.AskWhenReady, false, RemoteSelectionMode.AskWhenReady)]
    [InlineData(RemoteSelectionMode.All, true, RemoteSelectionMode.All)]
    [InlineData(RemoteSelectionMode.Chosen, true, RemoteSelectionMode.Chosen)]
    public void OnlyAChoiceLeftToTheCacheIsDecidedByIt(RemoteSelectionMode mode, bool cached, RemoteSelectionMode decided)
        => Assert.Equal(decided, RemoteJobRules.DecideSelection(mode, cached));

    [Fact]
    public void TheWordsForMissingFilesCountThem()
    {
        Assert.Equal("1 of the chosen files isn't in TorBox's list. Choose again.", RemoteJobRules.ChosenFilesMissing(1, "TorBox"));
        Assert.Equal("3 of the chosen files aren't in TorBox's list. Choose again.", RemoteJobRules.ChosenFilesMissing(3, "TorBox"));
    }
}
