using DownloadManager.Core;

namespace DownloadManager.Core.Tests;

/// <summary>SITES.4: downloads and site transfers share one power request; it is held while either asks.</summary>
public sealed class SharedSleepPreventionTests
{
    private sealed class Recording : ISleepPrevention
    {
        public List<bool> Calls { get; } = [];
        public bool Disposed { get; private set; }
        public void SetActive(bool active) => Calls.Add(active);
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void TheRequestIsHeldWhileAnyShareAsksAndOnlyChangesWhenNeeded()
    {
        var inner = new Recording();
        using var shared = new SharedSleepPrevention(inner);
        var downloads = shared.Share();
        var sites = shared.Share();

        downloads.SetActive(true);
        sites.SetActive(true);
        downloads.SetActive(true);
        downloads.SetActive(false);
        Assert.Equal([true], inner.Calls);

        sites.SetActive(false);
        Assert.Equal([true, false], inner.Calls);
        sites.SetActive(false);
        Assert.Equal([true, false], inner.Calls);
    }

    [Fact]
    public void DisposingAShareLetsGoOfItsAskAndDisposingTheOwnerReleasesTheRequest()
    {
        var inner = new Recording();
        var shared = new SharedSleepPrevention(inner);
        var downloads = shared.Share();
        var sites = shared.Share();
        downloads.SetActive(true);
        downloads.Dispose();
        Assert.Equal([true, false], inner.Calls);

        sites.SetActive(true);
        shared.Dispose();
        Assert.Equal([true, false, true, false], inner.Calls);
        Assert.True(inner.Disposed);
        sites.SetActive(true);
        Assert.Equal(4, inner.Calls.Count);
    }
}
