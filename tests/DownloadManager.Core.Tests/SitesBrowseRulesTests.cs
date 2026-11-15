using DownloadManager.Desktop.WinUI;

namespace DownloadManager.Core.Tests;

/// <summary>SITES.3: paths, breadcrumbs, text and paging of the Sites browse view.</summary>
public sealed class SitesBrowseRulesTests
{
    [Theory]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("   ", "/")]
    [InlineData("/", "/")]
    [InlineData("home", "/home")]
    [InlineData("/home/", "/home")]
    [InlineData("//home///user//", "/home/user")]
    [InlineData("/backup\\2026", "/backup\\2026")]
    [InlineData("/a/./b", "/a/b")]
    [InlineData("/a/b/../c", "/a/c")]
    [InlineData("/..", "/")]
    [InlineData("/a/../../b", "/b")]
    public void Normalize(string? input, string expected) => Assert.Equal(expected, SitesBrowseRules.Normalize(input));

    [Theory]
    [InlineData("/", null)]
    [InlineData("/a", "/")]
    [InlineData("/a/b", "/a")]
    [InlineData("/a/b/c/", "/a/b")]
    [InlineData(null, null)]
    public void Parent(string? input, string? expected) => Assert.Equal(expected, SitesBrowseRules.Parent(input));

    [Theory]
    [InlineData("/", "a", "/a")]
    [InlineData("/a", "b", "/a/b")]
    [InlineData("/a/", "b", "/a/b")]
    [InlineData("/a", "", "/a")]
    [InlineData("/a", "/b/", "/a/b")]
    [InlineData(null, "x y", "/x y")]
    public void Join(string? folder, string name, string expected) => Assert.Equal(expected, SitesBrowseRules.Join(folder, name));

    [Fact]
    public void CrumbsStartAtTheRootAndEachOpensItsOwnPath()
    {
        var crumbs = SitesBrowseRules.Crumbs("/srv/data/2026");
        Assert.Equal(["/", "srv", "data", "2026"], crumbs.Select(crumb => crumb.Name));
        Assert.Equal(["/", "/srv", "/srv/data", "/srv/data/2026"], crumbs.Select(crumb => crumb.Path));
        Assert.Single(SitesBrowseRules.Crumbs("/"));
        Assert.Single(SitesBrowseRules.Crumbs(null));
    }

    [Theory]
    [InlineData("/", "/")]
    [InlineData("/a/b", "b")]
    public void LeafName(string path, string expected) => Assert.Equal(expected, SitesBrowseRules.LeafName(path));

    [Fact]
    public void SizeIsEmptyForFoldersAndUnknownSizes()
    {
        Assert.Equal(string.Empty, SitesBrowseRules.SizeText(4096, isFolder: true));
        Assert.Equal(string.Empty, SitesBrowseRules.SizeText(-1, isFolder: false));
        Assert.Equal("0 B", SitesBrowseRules.SizeText(0, isFolder: false));
        Assert.Equal(UiFormat.Bytes(5_000_000), SitesBrowseRules.SizeText(5_000_000, isFolder: false));
    }

    [Fact]
    public void ModifiedIsEmptyWithoutADate()
    {
        Assert.Equal(string.Empty, SitesBrowseRules.ModifiedText(null));
        Assert.Equal(string.Empty, SitesBrowseRules.ModifiedText(DateTimeOffset.MinValue));
        var moment = new DateTimeOffset(2026, 11, 9, 19, 19, 28, TimeSpan.Zero);
        Assert.Equal(UiFormat.Moment(moment), SitesBrowseRules.ModifiedText(moment));
    }

    [Theory]
    [InlineData(0, 500, 1200, true)]
    [InlineData(500, 500, 1200, true)]
    [InlineData(1000, 200, 1200, false)]
    [InlineData(0, 0, 0, false)]
    [InlineData(0, 3, 3, false)]
    public void MoreWaitsWhileLessThanTheTotalHasCome(int offset, int loaded, int total, bool expected)
        => Assert.Equal(expected, SitesBrowseRules.HasMore(offset, loaded, total));

    [Fact]
    public void NextOffsetIsWhatIsAlreadyHere() => Assert.Equal(700, SitesBrowseRules.NextOffset(500, 200));

    [Fact]
    public void CountText()
    {
        Assert.Equal("Empty folder", SitesBrowseRules.CountText(0, 0));
        Assert.Equal("1 item", SitesBrowseRules.CountText(1, 1));
        Assert.Contains("3", SitesBrowseRules.CountText(3, 3));
        Assert.StartsWith("Showing", SitesBrowseRules.CountText(500, 1200));
    }

    [Fact]
    public void StarsCompareByNormalisedPathAndKeepEachOnce()
    {
        string[] stars = ["/b/", "/a", "/a", "c"];
        Assert.True(SitesBrowseRules.IsStarred(stars, "/b"));
        Assert.True(SitesBrowseRules.IsStarred(stars, "/c/"));
        Assert.False(SitesBrowseRules.IsStarred(stars, "/d"));
        Assert.False(SitesBrowseRules.IsStarred(null, "/a"));
        Assert.Equal(["/a", "/b", "/c"], SitesBrowseRules.OrderedStars(stars));
    }

    [Fact]
    public void HostKeyProblemsPointToOptions()
    {
        var changed = SitesBrowseRules.ProblemText("hostKeyChanged", null, "SHA256:new", ["SHA256:old"]);
        Assert.Contains("Options > Sites", changed);
        Assert.Contains("SHA256:new", changed);
        Assert.Contains("SHA256:old", changed);
        Assert.Contains("Options > Sites", SitesBrowseRules.ProblemText("hostKeyNotAccepted", null, null, null));
        Assert.Contains("Options > Sites", SitesBrowseRules.ProblemText("off", null, null, null));
        Assert.True(SitesBrowseRules.PointsToOptions("hostKeyChanged"));
        Assert.False(SitesBrowseRules.PointsToOptions(null));
        Assert.False(SitesBrowseRules.PointsToOptions("other"));
    }

    [Fact]
    public void AnOrdinaryFailureShowsTheHostsWords()
    {
        Assert.Equal("Permission denied.", SitesBrowseRules.ProblemText(null, " Permission denied. ", null, null));
        Assert.Equal("The folder could not be read.", SitesBrowseRules.ProblemText(null, null, null, null));
    }
}
