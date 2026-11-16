using DownloadManager.Sites;

namespace DownloadManager.Sites.Tests;

/// <summary>SITES.4: which server names can land on this PC, Keep both names, and rclone's partial names.</summary>
public sealed class SiteTransferPathsTests
{
    [Theory]
    [InlineData("report.pdf")]
    [InlineData("noext")]
    [InlineData(".hidden")]
    [InlineData("ünïcode ✓.txt")]
    public void OrdinaryNamesAreFineEverywhere(string name)
    {
        Assert.Null(SiteTransferPaths.LocalNameProblem(name, windows: true));
        Assert.Null(SiteTransferPaths.LocalNameProblem(name, windows: false));
    }

    [Theory]
    [InlineData(@"..\evil.txt")]
    [InlineData(@"back\slash")]
    [InlineData("a:b")]
    [InlineData("what?")]
    [InlineData("star*")]
    [InlineData("pipe|")]
    [InlineData("trailing.")]
    [InlineData("trailing ")]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    [InlineData("com1.log")]
    [InlineData("COM¹")]
    [InlineData("conin$")]
    public void NamesWindowsCannotHoldAreRefusedOnWindowsOnly(string name)
    {
        Assert.NotNull(SiteTransferPaths.LocalNameProblem(name, windows: true));
        Assert.Null(SiteTransferPaths.LocalNameProblem(name, windows: false));
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a/b")]
    public void NamesNoSystemCanHoldAreRefused(string name)
    {
        Assert.NotNull(SiteTransferPaths.LocalNameProblem(name, windows: false));
        Assert.NotNull(SiteTransferPaths.LocalNameProblem(name, windows: true));
    }

    [Fact]
    public void AnItemPathIsCheckedSegmentBySegment()
    {
        Assert.Null(SiteTransferPaths.LocalPathProblem("docs/deep/b.txt"));
        Assert.NotNull(SiteTransferPaths.LocalPathProblem("docs/../b.txt"));
        Assert.True(SiteTransferPaths.IsRelative("docs/deep/b.txt"));
        Assert.False(SiteTransferPaths.IsRelative("/docs"));
        Assert.False(SiteTransferPaths.IsRelative("docs//b"));
        Assert.False(SiteTransferPaths.IsRelative("docs/../b"));
        Assert.False(SiteTransferPaths.IsRelative("a\nb"));
    }

    [Theory]
    [InlineData("report.pdf", 2, "report (2).pdf")]
    [InlineData("archive.tar.gz", 3, "archive.tar (3).gz")]
    [InlineData("noext", 2, "noext (2)")]
    [InlineData(".hidden", 2, ".hidden (2)")]
    [InlineData("docs/deep/b.txt", 2, "docs/deep/b (2).txt")]
    [InlineData("docs.v1/readme", 2, "docs.v1/readme (2)")]
    public void KeepBothNumbersTheName(string relative, int number, string expected) =>
        Assert.Equal(expected, SiteTransferPaths.Numbered(relative, number));

    [Theory]
    [InlineData("big.bin.63642737.partial", "big.bin", true)]
    [InlineData("c1.bin.b1dc66bb.partial", "c1.bin", true)]
    [InlineData("big.bin.0a1B2c3d.partial", "big.bin", false)]
    [InlineData("big.bin.0a1b2c.partial", "big.bin", false)]
    [InlineData("report.pdf.old.partial", "report.pdf", false)]
    [InlineData("big.bin.partial", "big.bin", false)]
    [InlineData("big.bin.6364.2737.partial", "big.bin", false)]
    [InlineData("other.bin.63642737.partial", "big.bin", false)]
    [InlineData("big.bin2.63642737.partial", "big.bin", false)]
    [InlineData("big.bin.63642737.partial.txt", "big.bin", false)]
    public void OnlyRclonesPartialOfThatFileMatches(string candidate, string target, bool expected) =>
        Assert.Equal(expected, SiteTransferPaths.IsPartialOf(candidate, target));

    [Fact]
    public void RemoteAndLocalPathsJoin()
    {
        Assert.Equal("/a.txt", SiteTransferPaths.Remote("/", "a.txt"));
        Assert.Equal("/srv/pub/d/a.txt", SiteTransferPaths.Remote("/srv/pub", "d/a.txt"));
        Assert.Equal(Path.Combine("root", "d", "a.txt"), SiteTransferPaths.Local("root", "d/a.txt"));
    }
}
