using DownloadManager.Core;

namespace DownloadManager.Core.Tests;

/// <summary>An AppImage counts as an installed build that updates itself, only when this program runs from its mount.</summary>
public sealed class AppImageBuildTests
{
    [Fact]
    public void AProgramInsideTheMountIsAnAppImageBuildThatReplacesItsFile()
    {
        // Unix paths: an AppImage exists only on Linux.
        if (!OperatingSystem.IsLinux()) return;
        var build = InstalledBuild.AppImage("/home/u/Apps/Inbound.AppImage", "/tmp/.mount_InbouX1", "/tmp/.mount_InbouX1/usr/lib/inbound/", "0.6.5");
        Assert.NotNull(build);
        Assert.Equal("0.6.5", build.Version);
        Assert.Equal("stable", build.Channel);
        Assert.Equal("/home/u/Apps/Inbound.AppImage", build.AppImagePath);
        Assert.Equal("/home/u/Apps", build.InstallRoot);
    }

    [Theory]
    [InlineData(null, "/tmp/.mount_X", "/tmp/.mount_X/usr/lib/inbound/")]
    [InlineData("/home/u/Inbound.AppImage", null, "/tmp/.mount_X/usr/lib/inbound/")]
    [InlineData("/home/u/Inbound.AppImage", "/tmp/.mount_X", "/home/u/idm/bin/")]
    [InlineData("/home/u/Inbound.AppImage", "/tmp/.mount_X", "/tmp/.mount_XY/usr/lib/inbound/")]
    [InlineData("Inbound.AppImage", "/tmp/.mount_X", "/tmp/.mount_X/usr/lib/inbound/")]
    public void VariablesInheritedByAnotherProgramDontMakeItAnAppImage(string? appImage, string? appDir, string baseDirectory)
    {
        if (appImage is null && !OperatingSystem.IsLinux()) return;
        Assert.Null(InstalledBuild.AppImage(appImage ?? "", appDir ?? "", baseDirectory, "0.6.5"));
    }
}
