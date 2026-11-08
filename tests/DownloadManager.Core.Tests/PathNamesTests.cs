using DownloadManager.Core;

namespace DownloadManager.Core.Tests;

public sealed class PathNamesTests
{
    [Fact]
    public void CapitalLettersMatterOnLinuxOnly()
    {
        Assert.Equal(OperatingSystem.IsWindows(), PathNames.Same("/downloads/A", "/downloads/a"));
        Assert.True(PathNames.Same("/downloads/a", "/downloads/a"));
        var set = new HashSet<string>(PathNames.Comparer) { "a.txt", "A.TXT" };
        Assert.Equal(OperatingSystem.IsWindows() ? 1 : 2, set.Count);
    }

    [Fact]
    public void SeparatorsBecomeTheMachinesOwnAndABackslashStaysPartOfANameOnLinux()
    {
        Assert.Equal(OperatingSystem.IsWindows() ? @"sub\a.txt" : "sub/a.txt", PathNames.ToNativeSeparators("sub/a.txt"));
        Assert.Equal(@"sub\a.txt", PathNames.ToNativeSeparators(@"sub\a.txt"));
    }

    [Fact]
    public void AFolderIsOnTheVolumeThatHoldsIt()
    {
        var here = AppContext.BaseDirectory;
        var root = Volumes.RootOf(here);
        Assert.NotNull(root);
        Assert.StartsWith(root!, Path.GetFullPath(here), PathNames.Comparison);
        Assert.True(Volumes.Same(here, Path.Combine(here, "not-there-yet", "x")));
        Assert.NotNull(Volumes.AvailableBytes(here));
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(Path.GetPathRoot(Path.GetFullPath(here)), root);
        }
    }

    [Fact]
    public void OnLinuxTheVolumeIsTheLongestMountNotTheFirstPart()
    {
        if (OperatingSystem.IsWindows()) return;
        // /proc is its own mount on every Linux; "/" would be the wrong answer for a path inside it.
        Assert.Equal("/proc", Volumes.RootOf("/proc/self"));
        Assert.False(Volumes.Same("/proc/self", "/"));
        Assert.Equal("/", Volumes.RootOf("/"));
    }
}
