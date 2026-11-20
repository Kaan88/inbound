using DownloadManager.Mobile.Links;

namespace DownloadManager.Mobile.Tests;

public sealed class PageLinksTests
{
    [Theory]
    [InlineData("https://example.com/download?token=keep!")]
    [InlineData("https://example.com/file.")]
    [InlineData("https://example.com/download?token=keep;")]
    public void ASharedSingleUrlDoesNotChangeSignedOrLiteralPunctuation(string url) => Assert.Equal(new[] { url }, SharedLinks.Extract(url));
    [Fact] public void SharedProseExtractsAndNormalizesDistinctUrls()
    {
        var links = SharedLinks.Extract("See https://Example.com/a(1).zip#top, (https://example.com/b). Again https://example.com/a(1).zip#other!");
        Assert.Equal(new[] { "https://example.com/a(1).zip", "https://example.com/b" }, links);
    }
    [Fact] public void RejectsCredentialsAndOtherSchemesAndMalformedAddresses()
    {
        var links = SharedLinks.Extract("ftp://example.com/a https://user:secret@example.com/b https:// bad http://?x=1 https://example.com/ok");
        Assert.Equal(new[] { "https://example.com/ok" }, links);
        var model = new PageLinks([new("https://example.com/a\u0001b", "bad"), new("file:///tmp/a", "file"), new("https://u:p@example.com/a", "credentials")]);
        Assert.Empty(model.Visible);
    }
    [Fact] public void SharedTextAndUrlsAreBounded()
    {
        var urls = string.Join(' ', Enumerable.Range(0, 110).Select(i => $"https://example.com/{i}"));
        Assert.Equal(100, SharedLinks.Extract(urls).Count);
        Assert.Empty(SharedLinks.Extract("https://example.com/" + new string('x', 8200)));
        Assert.Empty(SharedLinks.Extract(new string('x', 65536) + " https://example.com/late"));
    }
    [Fact] public void DistinctPathsRemainCaseSensitiveAndFragmentsDoNotCreateDuplicates()
    {
        var model = new PageLinks([new("https://EXAMPLE.com/A#one", " A\u0001 "), new("https://example.com/A#two", "duplicate"), new("https://example.com/a", new string('n', 250))]);
        Assert.Equal(2, model.Visible.Count); Assert.Equal("A", model.Visible[0].Name); Assert.Equal(200, model.Visible[1].Name.Length);
    }
    [Fact] public void FilterAndSelectionPersistAcrossKindsAndSearchNeverReadsQuery()
    {
        var model = new PageLinks([new("https://files.example/a.zip?secret=needle", "Archive"), new("https://other.example/movie.MP4?x=.zip", "Clip"), new("https://files.example/p.pdf", "Paper"), new("https://files.example/data", "Data")]);
        Assert.Equal(0, model.SelectedCount); model.Kind = PageLinkKind.Archives; Assert.Single(model.Visible); model.SetVisibleSelected(true);
        model.Kind = PageLinkKind.Video; Assert.Single(model.Visible); model.Toggle(model.Visible[0].Url);
        model.Kind = PageLinkKind.Documents; Assert.Single(model.Visible); model.SetVisibleSelected(true);
        model.Kind = PageLinkKind.Other; Assert.Single(model.Visible); model.Query = "needle"; Assert.Empty(model.Visible);
        model.Query = "FILES.EXAMPLE"; Assert.Single(model.Visible); model.Kind = PageLinkKind.All;
        Assert.Equal(3, model.SelectedCount); Assert.Equal(new[] { "Archive", "Clip", "Paper" }, model.Selected.Select(x => x.Name));
        model.SetVisibleSelected(false); Assert.Single(model.Selected); Assert.Equal("Clip", model.Selected[0].Name);
    }
    [Fact] public void ACategoryGroupsItsLinksFirstAndSelectsOnlyThoseLinks()
    {
        var links = new PageLinks([new("https://example.com/cover.jpg", "Cover"),
            new("https://example.com/a.zip", "Archive"), new("https://example.com/readme.txt", "Readme")]);
        links.Kind = PageLinkKind.Archives;
        links.SetVisibleSelected(true);
        Assert.Equal(new[] { "Archive", "Cover", "Readme" }, links.DisplayRows.Select(link => link.Name));
        Assert.Equal("Archive", Assert.Single(links.Selected).Name);
        links.Query = "missing";
        Assert.Empty(links.DisplayRows);
    }

    [Fact] public void ByteTotalsRequireKnownSizesForEverySelectedLinkAndSafeAddition()
    {
        var links = new PageLinks([new("https://example.com/a.zip", "A", long.MaxValue),
            new("https://example.com/b.zip", "B", 1), new("https://example.com/c.zip", "C")]);
        links.Toggle(links.Visible[0].Url);
        Assert.Equal(long.MaxValue, links.SelectedBytes);
        links.Toggle(links.Visible[1].Url);
        Assert.Null(links.SelectedBytes);
        links.Toggle(links.Visible[0].Url);
        Assert.Equal(1L, links.SelectedBytes);
        links.Toggle(links.Visible[2].Url);
        Assert.Null(links.SelectedBytes);
    }

    [Fact] public void VisibleCategoryTotalExcludesOtherSelectedLinksAndRequiresSafeKnownSizes()
    {
        var links = new PageLinks([new("https://example.com/a.zip", "A", 10),
            new("https://example.com/b.zip", "B", 20), new("https://example.com/cover.jpg", "Cover", 7)]);
        links.Toggle(links.Visible[2].Url);
        links.Kind = PageLinkKind.Archives;
        links.SetVisibleSelected(true);
        Assert.Equal(30, links.VisibleBytes);
        Assert.Equal(37, links.SelectedBytes);
        links.Query = "missing";
        Assert.Null(links.VisibleBytes);
        var overflow = new PageLinks([new("https://example.com/a.zip", "A", long.MaxValue),
            new("https://example.com/b.zip", "B", 1)]);
        Assert.Null(overflow.VisibleBytes);
        var unknown = new PageLinks([new("https://example.com/a.zip", "A")]);
        Assert.Null(unknown.VisibleBytes);
    }

    [Fact] public void InputCapCountsOnlyDistinctValidLinks()
    {
        var input = new[] { new PageLink("javascript:bad", "bad"), new PageLink("https://example.com/0#duplicate", "first") }
            .Concat(Enumerable.Range(0, 2100).Select(i => new PageLink($"https://example.com/{i}", i.ToString())));
        var model = new PageLinks(input); Assert.Equal(2000, model.Visible.Count); Assert.Equal("1999", model.Visible[^1].Name);
    }
}
