using DownloadManager.Contracts;
using DownloadManager.Mobile;
using Xunit;

namespace DownloadManager.Mobile.Tests;

public sealed class RemoteTransferTextTests
{
    private static SiteTransferDto Download() => new()
    {
        Id = "abc", SiteName = "Home NAS", Direction = "download", State = "running", RemoteFolder = "/media",
        LocalFolder = "/storage/emulated/0/Download/Inbound/Music", Files = 1, Bytes = 455_000_000, BytesDone = 212_000_000, Speed = 9_800_000,
    };

    [Fact]
    public void RunningSingleFileLineHasSizesRateAndTimeLeft() =>
        Assert.Equal("212 of 455 MB · 9.8 MB/s · 24 s left", RemoteTransferText.Status(Download(), DateTimeOffset.Now));

    [Fact]
    public void RouteDropsThePhoneRoot()
    {
        var (source, destination) = RemoteTransferText.Route(Download(), phoneHost: true);
        Assert.Equal("Home NAS", source);
        Assert.Equal("Download/Inbound/Music", destination);
    }

    [Fact]
    public void FailedBatchSaysHowManyWereNotCopied()
    {
        var t = new SiteTransferDto { State = "failed", Files = 24, FilesDone = 21, FilesFailed = 3 };
        Assert.Equal("Failed · 3 of 24 not copied", RemoteTransferText.Status(t, DateTimeOffset.Now));
    }

    [Fact]
    public void FinishedBatchMentionsFilesAlreadyThere()
    {
        var t = new SiteTransferDto { State = "done", Files = 14, FilesDone = 12, FilesSkipped = 2 };
        Assert.Equal("Finished · 14 files, 2 already there", RemoteTransferText.Status(t, DateTimeOffset.Now));
    }

    [Fact]
    public void TitleIsTheFileOrFolderName()
    {
        var one = new SiteTransferDto { Files = 1, State = "done" };
        Assert.Equal("setlist.pdf", RemoteTransferText.Title(one, ["setlist.pdf"]));
        var folder = new SiteTransferDto { Files = 3, State = "running" };
        Assert.Equal("Wedding", RemoteTransferText.Title(folder, ["Wedding/a.jpg", "Wedding/b.jpg", "Wedding/x/c.jpg"]));
        var ended = new SiteTransferDto { Files = 3, State = "done" };
        Assert.Equal("Wedding · 3 files", RemoteTransferText.Title(ended, ["Wedding/a.jpg", "Wedding/b.jpg", "Wedding/x/c.jpg"]));
        Assert.Equal("2 files", RemoteTransferText.Title(new SiteTransferDto { Files = 2 }, ["a.jpg", "b.jpg"]));
    }

    [Fact]
    public void FiltersCountActiveStates()
    {
        Assert.True(RemoteTransferText.InFilter(new SiteTransferDto { State = "paused" }, "active"));
        Assert.False(RemoteTransferText.InFilter(new SiteTransferDto { State = "done" }, "active"));
        Assert.True(RemoteTransferText.InFilter(new SiteTransferDto { State = "canceled" }, "canceled"));
    }

    [Fact]
    public void RunningBeforeTheSizeIsKnownSaysStarting()
    {
        var t = Download(); t.Bytes = 0; t.BytesDone = 0; t.Speed = 0;
        Assert.Equal("Starting", RemoteTransferText.Status(t, DateTimeOffset.Now));
    }

    [Fact]
    public void PausedBannerHasTitleAndDetail()
    {
        var reason = RemoteTransferText.Reason(new SiteTransferDto { State = "paused" });
        Assert.Equal("Paused", reason?.Title);
        Assert.Equal("Unfinished files restart from the beginning on resume.", reason?.Detail);
    }

    [Fact]
    public void FileRowsShowTheNameOnlyAndSignInFailuresNeedAttention()
    {
        Assert.Equal("IMG_2291.jpg", RemoteTransferText.FileLine(new SiteTransferItemDto { Path = "Photos 2025/IMG_2291.jpg", State = "done" }).Name);
        Assert.True(RemoteTransferText.NeedsAttention(new SiteTransferDto { State = "failed", Problem = SiteProblems.SignInNeeded, SiteName = "Drive" }));
        Assert.False(RemoteTransferText.NeedsAttention(new SiteTransferDto { State = "failed" }));
    }

    [Theory]
    [InlineData("Connection lost", "Not copied · connection lost")]
    [InlineData("SFTP refused it", "Not copied · SFTP refused it")]
    public void ANotCopiedReasonReadsInLowercaseAfterTheDot(string error, string expected) =>
        Assert.Equal(expected, RemoteTransferText.FileLine(new SiteTransferItemDto { Path = "a/b.jpg", State = "failed", Error = error }).Line);
}
