using DownloadManager.Core;
using DownloadManager.Runtime;

namespace DownloadManager.Integration.Tests;

public sealed class AppImageUpdateTests
{
    private static readonly InstallMetadata Windows = new("0.6.5", "stable", @"C:\Program Files\Inbound");
    private static readonly InstallMetadata AppImage = new("0.6.5", "stable", "/home/u", "/home/u/Inbound.AppImage");

    [Fact]
    public void AnAppImageReadsTheLinuxManifestBesideTheWindowsOne()
    {
        Assert.Equal("https://github.com/Kaan88/inbound/releases/latest/download/manifest-linux-x64.json",
            DownloadManagerService.ManifestUrlFor(AppImage, ReleaseUpdates.ManifestUrl));
        Assert.Equal(ReleaseUpdates.ManifestUrl, DownloadManagerService.ManifestUrlFor(Windows, ReleaseUpdates.ManifestUrl));
        // A manifest address of the owner's own, by another name, is used as it is.
        Assert.Equal("https://example.com/feed.json", DownloadManagerService.ManifestUrlFor(AppImage, "https://example.com/feed.json"));
    }

    [Fact]
    public void TheRestarterWaitsForTheHostAndOpensTheWindowOnlyWhenAsked()
    {
        var withWindow = AppImageRestarter.Arguments("/home/u/My Apps/Inbound.AppImage", 4242, reopenWindow: true);
        Assert.Equal(["-f", "/bin/sh", "-c", AppImageRestarter.Script, "inbound-restart", "4242", "/home/u/My Apps/Inbound.AppImage", "--open"], withWindow);
        Assert.Equal("", AppImageRestarter.Arguments("/a", 1, reopenWindow: false)[^1]);
        // The path is an argument of its own, never part of the script.
        Assert.DoesNotContain("My Apps", AppImageRestarter.Script);
    }

    [Theory]
    [InlineData("A=1\0DOWNLOADMANAGER_INSTANCE=t\0DOWNLOADMANAGER_DATA=/d\0", "t", "/d", true)]
    [InlineData("A=1\0", null, null, true)]
    [InlineData("DOWNLOADMANAGER_INSTANCE=\0", null, null, true)]
    [InlineData("DOWNLOADMANAGER_INSTANCE=uicheck\0", null, null, false)]
    [InlineData("A=1\0", "updtest", "/d", false)]
    [InlineData("DOWNLOADMANAGER_INSTANCE=t\0DOWNLOADMANAGER_DATA=/other\0", "t", "/d", false)]
    public void OnlyAWindowOfTheSameInstanceIsReopened(string environ, string? instance, string? data, bool same) =>
        Assert.Equal(same, AppImageRestarter.SameInstance(environ, instance, data));

    [Fact]
    public void AnUpdateTheHostAppliedItselfIsToldOnceAsInstalled()
    {
        var folder = Path.Combine(Path.GetTempPath(), "dm-appimage-journal-" + Guid.NewGuid().ToString("N"));
        try
        {
            var journal = new UpdateInstallJournal(folder);
            journal.RecordSucceeded("0.6.6");
            var outcome = journal.TakeUnreported(_ => false);
            Assert.NotNull(outcome);
            Assert.True(outcome.Succeeded);
            Assert.Equal("0.6.6", outcome.Version);
            Assert.Null(journal.TakeUnreported(_ => false));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }
}
