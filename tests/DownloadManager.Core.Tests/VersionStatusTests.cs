using DownloadManager.Contracts;
using DownloadManager.Desktop.WinUI;

namespace DownloadManager.Core.Tests;

/// <summary>
/// UI.R37: the status bar's version says whether it is the latest, as far as the Host's last update check knows; a
/// server shown in the window says when it isn't. Without a known latest it is the version alone.
/// </summary>
public sealed class VersionStatusTests
{
    [Fact]
    public void ThisPcSaysLatestOnlyWhenTheLastCheckSaysSo()
    {
        Assert.Equal(new VersionWords("0.7.0 (latest)", null), VersionStatus.ThisPc("0.7.0", "0.7.0"));
        Assert.Equal(new VersionWords("0.7.0", "update available (0.7.1)"), VersionStatus.ThisPc("0.7.0", "0.7.1"));
        Assert.Equal(new VersionWords("0.7.0", "update available (0.7.1)"), VersionStatus.ThisPc("0.7.0", "v0.7.1"));
        // Never checked, updates off, a portable build: nothing about latest.
        Assert.Equal(new VersionWords("0.7.0", null), VersionStatus.ThisPc("0.7.0", null));
        Assert.Equal(new VersionWords("0.7.0", null), VersionStatus.ThisPc("0.7.0", "not a version"));
        // A build newer than the manifest (a test build) is not told to go back.
        Assert.Equal(new VersionWords("0.7.2 (latest)", null), VersionStatus.ThisPc("0.7.2", "0.7.1"));
        Assert.Equal(new VersionWords(string.Empty, null), VersionStatus.ThisPc(null, "0.7.1"));
    }

    [Fact]
    public void AServerSaysNotLatestWhenOlder()
    {
        Assert.Equal("0.6.3 (not latest)", VersionStatus.Server("0.6.3", "0.7.0"));
        Assert.Equal("0.7.0 (latest)", VersionStatus.Server("0.7.0", "0.7.0"));
        Assert.Equal("0.7.0 (latest)", VersionStatus.Server("0.7.0", "0.7"));
        Assert.Equal("0.6.3", VersionStatus.Server("0.6.3", null));
        Assert.Equal(string.Empty, VersionStatus.Server(null, "0.7.0"));

        Assert.Equal("0.6.3 (not latest)", VersionStatus.ServerNote("0.6.3", "0.7.0"));
        Assert.Null(VersionStatus.ServerNote("0.7.0", "0.7.0"));
        Assert.Null(VersionStatus.ServerNote("0.6.3", null));
        Assert.Null(VersionStatus.ServerNote(null, "0.7.0"));
    }

    [Fact]
    public void AServerIsHeldToItsOwnCheckFirstThenThisPcs()
    {
        // Its own check reads its own build's manifest (the AppImage's Linux one), so it wins.
        Assert.Equal("0.7.0", VersionStatus.LatestFor("0.7.0", HostKinds.Linux, "0.7.1"));
        // Every Inbound build comes from the same version tag, so this PC's latest stands for the others.
        Assert.Equal("0.7.1", VersionStatus.LatestFor(null, HostKinds.Server, "0.7.1"));
        Assert.Equal("0.7.1", VersionStatus.LatestFor(null, HostKinds.Windows, "0.7.1"));
        Assert.Equal("0.7.1", VersionStatus.LatestFor(" ", null, "0.7.1"));
        // A kind this window doesn't know isn't claimed to be behind.
        Assert.Null(VersionStatus.LatestFor(null, "something-else", "0.7.1"));
        Assert.Null(VersionStatus.LatestFor(null, HostKinds.Server, null));
    }
}
