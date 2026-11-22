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
    [Fact] public void A_download_at_the_end_of_a_redirect_takes_the_one_open_main_frame_get()
    {
        var requests = new BrowserRequests(); requests.CommitPage(Page);
        requests.Observe(File, "GET", true, Page);
        requests.Observe("https://example.test/style.css", "GET", false, Page);
        var observation = requests.Take("https://cdn.example.test/signed/file.zip?t=1");
        Assert.NotNull(observation); Assert.True(observation.Redirected); Assert.False(observation.Ambiguous);
        Assert.Equal(Page, observation.PageUrl);
        // Cross-origin hop: only the origin goes on as the referrer.
        Assert.Equal("https://example.test/", observation.Referrer);
        Assert.Null(requests.Take("https://cdn.example.test/other.zip"));
    }
    [Fact] public void A_same_origin_redirect_keeps_the_full_referrer()
    {
        var requests = new BrowserRequests(); requests.CommitPage(Page);
        requests.Observe(File, "GET", true, Page);
        var observation = requests.Take("https://example.test/files/real.zip");
        Assert.NotNull(observation); Assert.Equal(Page, observation.Referrer);
    }
    [Fact] public void An_unobserved_download_with_two_open_main_frame_requests_stays_unknown()
    {
        var requests = new BrowserRequests(); requests.CommitPage(Page);
        requests.Observe(File, "GET", true, Page); requests.Observe("https://example.test/other.zip", "GET", true, Page);
        Assert.Null(requests.Take("https://cdn.example.test/file.zip"));
    }
    [Fact] public void An_unobserved_download_after_a_main_frame_post_stays_unknown()
    {
        var requests = new BrowserRequests(); requests.CommitPage(Page);
        requests.Observe(File, "POST", true, Page);
        Assert.Null(requests.Take("https://cdn.example.test/file.zip"));
    }
    [Fact] public void An_unobserved_download_from_an_older_document_stays_unknown()
    {
        var requests = new BrowserRequests(); requests.CommitPage(Page);
        requests.Observe(File, "GET", true, Page); requests.CommitPage("https://example.test/next");
        Assert.Null(requests.Take("https://cdn.example.test/file.zip"));
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
