using DownloadManager.Desktop.WinUI;

namespace DownloadManager.Core.Tests;

/// <summary>
/// UI.B28: Progress Details opened by a double-click fell behind the main window. It is brought forward again once the
/// click is over, and only when the window it was opened from took the front.
/// </summary>
public sealed class WindowFrontRulesTests
{
    [Fact]
    public void WaitsWhileTheButtonIsHeld()
    {
        Assert.True(WindowFrontRules.KeepWaiting(inputHeld: true, TimeSpan.FromMilliseconds(100)));
        Assert.False(WindowFrontRules.KeepWaiting(inputHeld: false, TimeSpan.Zero));
    }

    [Fact]
    public void StopsWaitingForAButtonHeldTooLong()
        => Assert.False(WindowFrontRules.KeepWaiting(inputHeld: true, WindowFrontRules.LongestWait));

    [Fact]
    public void BringsBackOnlyWhenTheOpenerTookTheFront()
    {
        Assert.True(WindowFrontRules.BringBack(windowShown: true, frontIsWindow: false, frontIsOpener: true));
        // Already in front.
        Assert.False(WindowFrontRules.BringBack(windowShown: true, frontIsWindow: true, frontIsOpener: false));
        // The user went to another app, or another window of this app is in front.
        Assert.False(WindowFrontRules.BringBack(windowShown: true, frontIsWindow: false, frontIsOpener: false));
        // Closed or hidden meanwhile.
        Assert.False(WindowFrontRules.BringBack(windowShown: false, frontIsWindow: false, frontIsOpener: true));
    }

    [Fact]
    public void KeepsWatchingForAMomentAfterTheInputEnded()
    {
        Assert.True(WindowFrontRules.KeepWatching(windowShown: true, newInput: false, TimeSpan.FromMilliseconds(100), bringBacks: 1));
        // The settle time is over.
        Assert.False(WindowFrontRules.KeepWatching(windowShown: true, newInput: false, WindowFrontRules.Settle, bringBacks: 1));
        // The user clicked or pressed a key again: whatever window they go to keeps the front.
        Assert.False(WindowFrontRules.KeepWatching(windowShown: true, newInput: true, TimeSpan.FromMilliseconds(100), bringBacks: 0));
        // Closed or hidden.
        Assert.False(WindowFrontRules.KeepWatching(windowShown: false, newInput: false, TimeSpan.FromMilliseconds(100), bringBacks: 0));
        // Never a tug of war.
        Assert.False(WindowFrontRules.KeepWatching(windowShown: true, newInput: false, TimeSpan.FromMilliseconds(100), WindowFrontRules.MostBringBacks));
    }
}
