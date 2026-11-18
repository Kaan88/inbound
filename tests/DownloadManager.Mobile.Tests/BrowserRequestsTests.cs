using System.Text.Json;
using DownloadManager.Mobile.Links;

namespace DownloadManager.Mobile.Tests;

public sealed class BrowserRequestsTests
{
    private const string Page = "https://example.test/page";
    private const string File = "https://example.test/file.zip";
    private static string Wrapped(string inner) => JsonSerializer.Serialize(inner);

    [Fact] public void OneMainFrameGetRecordsCommittedPageAndMissingReferrer()
    {
        var requests = new BrowserRequests(); requests.CommitPage(Page);
        requests.Observe(File, "GET", true, null);
        var observation = requests.Take(File);
        Assert.NotNull(observation); Assert.Equal(Page, observation.PageUrl); Assert.Null(observation.Referrer);
        Assert.False(observation.Ambiguous); Assert.False(observation.KnownNonGet); Assert.Null(requests.Take(File));
    }
    [Fact] public void DuplicateUrlIsAmbiguousAndPreservesNonGetEvidence()
    {
        var requests = new BrowserRequests(); requests.CommitPage(Page);
        requests.Observe(File, "POST", true, Page); requests.Observe(File, "GET", true, Page);
        var observation = requests.Take(File); Assert.NotNull(observation);
        Assert.True(observation.Ambiguous); Assert.True(observation.KnownNonGet);
    }
    [Fact] public void NewDocumentDropsOldObservations()
    {
        var requests = new BrowserRequests(); requests.CommitPage(Page); requests.Observe(File, "GET", true, Page);
        requests.CommitPage("https://example.test/next"); Assert.Null(requests.Take(File));
    }
    [Fact] public void LoneSubframePostIsAmbiguousAndKnownNonGet()
    {
        var requests = new BrowserRequests(); requests.CommitPage(Page); requests.Observe(File, "POST", false, Page);
        var observation = requests.Take(File); Assert.NotNull(observation); Assert.True(observation.Ambiguous); Assert.True(observation.KnownNonGet);
    }
    [Fact] public void MainGetThenSubframePostIsAmbiguousAndKnownNonGet()
    {
        var requests = new BrowserRequests(); requests.CommitPage(Page); requests.Observe(File, "GET", true, Page); requests.Observe(File, "POST", false, Page);
        var observation = requests.Take(File); Assert.NotNull(observation); Assert.True(observation.Ambiguous); Assert.True(observation.KnownNonGet);
    }
    [Fact] public void SubframeGetThenMainGetIsAmbiguous()
    {
        var requests = new BrowserRequests(); requests.CommitPage(Page); requests.Observe(File, "GET", false, Page); requests.Observe(File, "GET", true, Page);
        var observation = requests.Take(File); Assert.NotNull(observation); Assert.True(observation.Ambiguous); Assert.False(observation.KnownNonGet);
    }
    [Fact] public void OneHundredTwentyNinthDistinctRequestIsUnknown()
    {
        var requests = new BrowserRequests(); requests.CommitPage(Page);
        for (var i = 0; i < 129; i++) requests.Observe($"https://example.test/{i}", "GET", true, null);
        Assert.Null(requests.Take("https://example.test/128"));
        Assert.NotNull(requests.Take("https://example.test/127"));
    }
    [Fact] public void DifferentFullDocumentReferrerIsAmbiguousAndPostIsFlagged()
    {
        var requests = new BrowserRequests(); requests.CommitPage(Page);
        requests.Observe(File, "POST", true, "https://example.test/other");
        var observation = requests.Take(File); Assert.NotNull(observation);
        Assert.True(observation.Ambiguous); Assert.True(observation.KnownNonGet);
    }
    [Theory]
    [InlineData(null)] [InlineData("{")] [InlineData("42")] [InlineData("{}")]
    [InlineData("\"{bad\"")] [InlineData("\"{}\"")]
    public void MalformedOrWrongRootProducesEmptyModel(string? value) => Assert.Empty(PageLinkResults.Parse(value).Visible);

    [Fact] public void MalformedItemsAreSkippedAndUnexpectedNameFallsBack()
    {
        var model = PageLinkResults.Parse(Wrapped("[null,{}, {\"url\":7}, {\"url\":\"ftp://example.test/a\"}, {\"url\":\"https://example.test/a?secret=needle\",\"name\":{}}, {\"url\":\"https://example.test/b\",\"name\":\"Valid\"}]"));
        Assert.Equal(2, model.TotalCount); Assert.Equal("", model.Visible[0].Name); Assert.Equal("Valid", model.Visible[1].Name);
        model.Query = "needle"; Assert.Empty(model.Visible);
    }
    [Fact] public void InspectsAtMostOneThousandItemsEvenIfEarlierOnesInvalid()
    {
        var items = Enumerable.Repeat("{}", 1000).Concat(["{\"url\":\"https://example.test/late\"}"]);
        var model = PageLinkResults.Parse(Wrapped("[" + string.Join(',', items) + "]"));
        Assert.Equal(0, model.TotalCount);
    }
}
