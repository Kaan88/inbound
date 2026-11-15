using DownloadManager.Core;

namespace DownloadManager.Core.Tests;

/// <summary>MAC.1: a program inside Inbound.app counts as an installed build that updates the whole bundle.</summary>
public sealed class MacAppBuildTests
{
    private static bool HasPlist(string path) => path.Replace('\\', '/').EndsWith("/Inbound.app/Contents/Info.plist", StringComparison.Ordinal);

    [Theory]
    [InlineData("/Applications/Inbound.app/Contents/Resources/host/")]
    [InlineData("/Applications/Inbound.app/Contents/Resources/window")]
    public void TheHostOrTheWindowInsideTheBundleIsAMacBuildThatReplacesTheBundle(string baseDirectory)
    {
        // Unix paths: a bundle exists only on macOS, and Windows reads them as relative to a drive.
        if (OperatingSystem.IsWindows()) return;
        var build = InstalledBuild.MacApp(baseDirectory, "0.8.0", HasPlist);
        Assert.NotNull(build);
        Assert.Equal("0.8.0", build.Version);
        Assert.Equal("stable", build.Channel);
        Assert.Equal("/Applications/Inbound.app", build.MacAppPath);
        Assert.Equal("/Applications", build.InstallRoot);
        Assert.Null(build.AppImagePath);
    }

    [Theory]
    [InlineData("/home/u/idm/src/DownloadManager.Host.Linux/bin/Release/net10.0/")]
    [InlineData("/Applications/Inbound.app/Contents/MacOS/")]
    [InlineData("/Applications/Inbound/Contents/Resources/host/")]
    [InlineData("/Applications/Other.app/Contents/Resources/host/")]
    public void AProgramOutsideAnInboundBundleIsNot(string baseDirectory)
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.Null(InstalledBuild.MacApp(baseDirectory, "0.8.0", HasPlist));
    }
}
