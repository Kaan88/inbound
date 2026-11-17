using DownloadManager.Runtime.Sites;
using DownloadManager.Sites;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// Review 2026-11-17: a file's ending marks a remote's dot only where the server was contacted, and between two remotes
/// the right one.
/// </summary>
public sealed class SiteTransfersReachTests
{
    private const string Source = "source";
    private const string Target = "target";

    private static List<(string SiteId, bool Ok)> Reach(string direction, string state, bool failed = false, bool folder = false) =>
        [.. SiteTransfers.ReachOf(
            new SiteTransferRecord { SiteId = Source, Direction = direction, TargetSiteId = direction == SiteTransferDirections.Between ? Target : null },
            new SiteTransferItem { Path = "a", Folder = folder }, state, failed)];

    [Fact]
    public void ADownloadSkippedOrAnEmptyFolderMadeHereSaysNothingAboutTheServer()
    {
        Assert.Empty(Reach(SiteTransferDirections.Download, SiteTransferItemStates.Skipped));
        Assert.Empty(Reach(SiteTransferDirections.Download, SiteTransferItemStates.Done, folder: true));
        Assert.Equal([(Source, true)], Reach(SiteTransferDirections.Download, SiteTransferItemStates.Done));
        Assert.Equal([(Source, false)], Reach(SiteTransferDirections.Download, SiteTransferItemStates.Failed, failed: true));
    }

    [Fact]
    public void AnUploadSkipWasLookedUpOnTheServer()
    {
        Assert.Equal([(Source, true)], Reach(SiteTransferDirections.Upload, SiteTransferItemStates.Skipped));
        Assert.Equal([(Source, true)], Reach(SiteTransferDirections.Upload, SiteTransferItemStates.Done, folder: true));
        Assert.Equal([(Source, false)], Reach(SiteTransferDirections.Upload, SiteTransferItemStates.Failed, failed: true));
        Assert.Empty(Reach(SiteTransferDirections.Upload, SiteTransferItemStates.Failed));
    }

    [Fact]
    public void BetweenRemotesACopyMarksBothASkipTheTargetAndAFailedConnectionNeither()
    {
        Assert.Equal([(Source, true), (Target, true)], Reach(SiteTransferDirections.Between, SiteTransferItemStates.Done));
        Assert.Equal([(Target, true)], Reach(SiteTransferDirections.Between, SiteTransferItemStates.Skipped));
        Assert.Equal([(Target, true)], Reach(SiteTransferDirections.Between, SiteTransferItemStates.Done, folder: true));
        Assert.Empty(Reach(SiteTransferDirections.Between, SiteTransferItemStates.Failed, failed: true));
    }
}
