using DownloadManager.Contracts;

namespace DownloadManager.Integration.Tests;

/// <summary>SITES.4: how <c>dm sites transfers</c> writes one batch.</summary>
public sealed class SiteTransfersCliTests
{
    [Fact]
    public void ATransferLineSaysWhichWayHowFarAndWhy()
    {
        var line = DownloadManager.Cli.Program.SiteTransferLine(new SiteTransferDto
        {
            Id = "0123456789abcdef", SiteId = "s", SiteName = "nas", Direction = "download", RemoteFolder = "/pub",
            LocalFolder = @"C:\In", State = "failed", Files = 5, FilesDone = 2, FilesSkipped = 1, FilesFailed = 1,
            Message = "The server can't be reached.",
        });
        Assert.StartsWith("01234567  ↓ failed", line);
        Assert.Contains("3/5 files, 1 failed", line);
        Assert.Contains(@"nas:/pub -> C:\In", line);
        Assert.EndsWith("(The server can't be reached.)", line);

        var up = DownloadManager.Cli.Program.SiteTransferLine(new SiteTransferDto
        {
            Id = "ab", SiteName = "nas", Direction = "upload", RemoteFolder = "/in", LocalFolder = @"C:\Out", State = "running", Files = 1,
        });
        Assert.Contains(@"C:\Out -> nas:/in", up);
        Assert.StartsWith("ab        ↑ running", up);
    }
}
