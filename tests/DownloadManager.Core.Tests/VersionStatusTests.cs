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
    public void AServerIsHeldToItsOwnCheckFirstThenThisPcsOfTheSameKind()
    {
        // Its own check reads its own build's manifest (the AppImage's Linux one), so it wins, whatever this PC is.
        Assert.Equal("0.7.0", VersionStatus.LatestFor("0.7.0", HostKinds.Linux, "0.7.1", HostKinds.Windows));
        Assert.Equal("0.7.0", VersionStatus.LatestFor("0.7.0", HostKinds.Server, "0.7.1", HostKinds.Windows));
        // The same kind of install is released together, so this PC's latest stands for it.
        Assert.Equal("0.7.1", VersionStatus.LatestFor(null, HostKinds.Windows, "0.7.1", HostKinds.Windows));
        Assert.Equal("0.7.1", VersionStatus.LatestFor(" ", HostKinds.Linux, "0.7.1", HostKinds.Linux));
        // A Host from before hostKind is a Windows one.
        Assert.Equal("0.7.1", VersionStatus.LatestFor(null, null, "0.7.1", HostKinds.Windows));
        Assert.Equal("0.7.1", VersionStatus.LatestFor(null, HostKinds.Windows, "0.7.1", null));
    }

    [Fact]
    public void ADockerServerIsHeldOnlyToTheDockerLatestThisPcsHostRead()
    {
        // UPD.D1 addendum: the Docker latest stands for a Docker server with no check of its own, and for no other kind.
        Assert.Equal("0.7.4", VersionStatus.LatestFor(null, HostKinds.Server, "0.7.3", HostKinds.Windows, "0.7.4"));
        Assert.Null(VersionStatus.LatestFor(null, HostKinds.Server, "0.7.3", HostKinds.Windows, " "));
        Assert.Equal("0.7.0", VersionStatus.LatestFor("0.7.0", HostKinds.Server, "0.7.3", HostKinds.Windows, "0.7.4"));
        Assert.Equal("0.7.3", VersionStatus.LatestFor(null, HostKinds.Windows, "0.7.3", HostKinds.Windows, "0.7.4"));
    }

    [Theory]
    [InlineData(HostKinds.Server, "0.7.2", "0.7.4", "0.7.2 (update available)")]
    [InlineData(HostKinds.Server, "0.7.4", "0.7.4", null)]
    [InlineData(HostKinds.Server, "0.7.2", null, null)]
    [InlineData(HostKinds.Server, null, "0.7.4", null)]
    [InlineData(HostKinds.Windows, "0.7.2", "0.7.4", null)]
    [InlineData(HostKinds.Linux, "0.7.2", "0.7.4", null)]
    [InlineData(null, "0.7.2", "0.7.4", null)]
    public void ASavedDockerServerBehindTheDockerLatestIsMarked(string? kind, string? lastVersion, string? dockerLatest, string? expected)
        => Assert.Equal(expected, VersionStatus.SavedServerNote(kind, lastVersion, dockerLatest));

    [Fact]
    public void AServerIsNeverHeldToAnotherInstallKindsLatest()
    {
        // UI.B33: 0.7.3 was Windows only, so a Docker server on 0.7.2 is not behind it.
        Assert.Null(VersionStatus.LatestFor(null, HostKinds.Server, "0.7.3", HostKinds.Windows));
        Assert.Null(VersionStatus.LatestFor(null, HostKinds.Server, "0.7.3", HostKinds.Linux));
        Assert.Null(VersionStatus.LatestFor(null, HostKinds.Server, "0.7.3", HostKinds.Server));
        Assert.Null(VersionStatus.LatestFor(null, HostKinds.Linux, "0.7.3", HostKinds.Windows));
        Assert.Null(VersionStatus.LatestFor(null, HostKinds.Windows, "0.7.3", HostKinds.Linux));
        // A kind this window doesn't know isn't claimed to be behind.
        Assert.Null(VersionStatus.LatestFor(null, "something-else", "0.7.1", "something-else"));
        Assert.Null(VersionStatus.LatestFor(null, HostKinds.Windows, null, HostKinds.Windows));
        // With no latest known the version is shown alone.
        var latest = VersionStatus.LatestFor(null, HostKinds.Server, "0.7.3", HostKinds.Windows);
        Assert.Equal("0.7.2", VersionStatus.Server("0.7.2", latest));
        Assert.Null(VersionStatus.ServerNote("0.7.2", latest));
    }
}
