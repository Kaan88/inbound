using DownloadManager.Core;

namespace DownloadManager.Torrents.Tests;

/// <summary>T5: the worker's errors in plain words, with whether they pause, fail for good or may be tried again.</summary>
public sealed class TorrentErrorsTests
{
    [Theory]
    [InlineData("file", "diskFull", "whatever the system says", FailureKind.DiskFull, false, true)]
    [InlineData("torrent", "diskFull", "x", FailureKind.DiskFull, false, true)]
    [InlineData("file", "permission", "x", FailureKind.Permission, false, false)]
    [InlineData("file", "pathTooLong", "x", FailureKind.DestinationUnavailable, false, false)]
    [InlineData("file", null, "There is not enough space on the disk.", FailureKind.DiskFull, false, true)]
    [InlineData("file", null, "No space left on device", FailureKind.DiskFull, false, true)]
    [InlineData("file", null, "Permission denied", FailureKind.Permission, false, false)]
    [InlineData("file", null, "Read-only file system", FailureKind.Permission, false, false)]
    [InlineData("file", null, "The filename or extension is too long.", FailureKind.DestinationUnavailable, false, false)]
    [InlineData("file", null, "File name too long", FailureKind.DestinationUnavailable, false, false)]
    [InlineData("file", null, "The device is not ready.", FailureKind.Permission, true, false)]
    [InlineData("torrent", null, "something odd", FailureKind.Unknown, true, false)]
    [InlineData("metadata", null, "unsafe file path", FailureKind.InvalidContent, false, false)]
    public void EachErrorHasItsMeaning(string kind, string? reason, string message, FailureKind expected, bool retryable, bool pauses)
    {
        var meaning = TorrentErrors.Classify(kind, reason, message)!;
        Assert.Equal(expected, meaning.Kind);
        Assert.Equal(retryable, meaning.Retryable);
        Assert.Equal(pauses, meaning.PausesJob);
    }

    [Fact]
    public void TheWordsArePlain()
    {
        Assert.Equal("The disk is full", TorrentErrors.Classify("file", "diskFull", "Espace insuffisant")!.Message);
        Assert.StartsWith("A file of the torrent can't be written here: permission denied.", TorrentErrors.Classify("file", "permission", "x")!.Message);
        Assert.StartsWith("A file path of the torrent is too long", TorrentErrors.Classify("file", "pathTooLong", "x")!.Message);
        Assert.Equal("The torrent's file list can't be used: bad", TorrentErrors.Classify("metadata", null, "bad")!.Message);
    }

    [Theory]
    [InlineData("listen")]
    [InlineData("protocol")]
    [InlineData("tracker")]
    [InlineData(null)]
    public void ErrorsThatAreNotAboutTheTorrentLeaveItRunning(string? kind)
        => Assert.Null(TorrentErrors.Classify(kind, null, "tracker unreachable"));
}
