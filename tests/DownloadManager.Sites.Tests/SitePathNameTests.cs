using DownloadManager.Sites;

namespace DownloadManager.Sites.Tests;

/// <summary>SITES.5: which names a new folder or a rename may give on the server.</summary>
public sealed class SitePathNameTests
{
    [Theory]
    [InlineData("new folder")]
    [InlineData(".hidden")]
    [InlineData("a\\b")] // a backslash is part of a name on an SFTP server
    [InlineData("ünïcode ✓")]
    [InlineData("...")]
    public void UsableNames(string name) => Assert.Null(SitePaths.NameProblem(name));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a\tb")]
    [InlineData("line\nbreak")]
    public void RefusedNames(string? name) => Assert.NotNull(SitePaths.NameProblem(name));

    [Fact]
    public void AtMost255Characters()
    {
        Assert.Null(SitePaths.NameProblem(new string('x', 255)));
        Assert.NotNull(SitePaths.NameProblem(new string('x', 256)));
    }
}
