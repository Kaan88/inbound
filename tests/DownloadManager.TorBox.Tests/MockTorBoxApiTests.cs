using System.Net;
using System.Text.Json;
using DownloadManager.Core;
using DownloadManager.Core.Providers;
using DownloadManager.Providers.TorBox;
using TestServer;

namespace DownloadManager.TorBox.Tests;

public sealed class MockServerFixture : IAsyncLifetime
{
    public TestServerHost Server { get; private set; } = null!;
    public async Task InitializeAsync() => Server = await TestServerHost.StartAsync();
    public async Task DisposeAsync() => await Server.DisposeAsync();
}

/// <summary>The real client over real HTTP against the mock. Every value in here is invented.</summary>
public sealed class MockTorBoxApiTests : IClassFixture<MockServerFixture>, IDisposable
{
    private readonly TestServerHost _server;
    private readonly MockTorBox _mock;
    private readonly HttpClient _http = new();
    private readonly TorBoxClient _client;
    private string? _key = MockTorBox.DefaultApiKey;

    public MockTorBoxApiTests(MockServerFixture fixture)
    {
        _server = fixture.Server;
        _mock = _server.TorBox;
        _mock.Reset();
        _client = new TorBoxClient(_http, () => _key, _mock.ApiBase);
    }

    public void Dispose() => _http.Dispose();

    private static RemoteItemRef Ref(RemoteItemKind kind, long id) => new("torbox", kind, id.ToString());
    private static RemoteItemRef Ref(MockTorBoxItem item) => Ref(item.Kind, item.Id);

    private MockTorBoxItem OneFile(RemoteItemKind kind = RemoteItemKind.Torrent, int size = 1024, params MockTorBoxState[] states) =>
        _mock.AddItem(kind, "Item", [MockTorBoxFile.Of(_mock.NextFileId(), "Item/a.bin", size)], states);

    private async Task<HttpResponseMessage> RawAsync(string relative, string? bearer = MockTorBox.DefaultApiKey, HttpMethod? method = null)
    {
        using var request = new HttpRequestMessage(method ?? HttpMethod.Get, new Uri(_mock.ApiBase, relative));
        if (bearer is not null) request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {bearer}");
        return await _http.SendAsync(request);
    }

    // ---- Envelope, status words, error classes ------------------------------------------------

    [Fact]
    public async Task AccountParsesMicrosecondDatesAndPlan()
    {
        var account = await _client.GetAccountAsync();
        Assert.Equal("Pro", account.DisplayPlan);
        Assert.True(account.Premium);
        Assert.Equal(2030, account.PremiumUntil!.Value.Year);
        Assert.Equal(1, _mock.Calls("GET user/me"));
        Assert.Equal($"Bearer {MockTorBox.DefaultApiKey}", _mock.AuthorizationHeaders.Single());
    }

    [Fact]
    public async Task MissingAuthAnswersWithOnlyADetail()
    {
        using var response = await RawAsync("user/me", bearer: null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(body.RootElement.TryGetProperty("success", out _));
        Assert.Equal("Not authenticated", body.RootElement.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task WrongKeyIsAnAuthenticationFailure()
    {
        _key = "ffffffff-ffff-4fff-8fff-ffffffffffff";
        var failure = await Assert.ThrowsAsync<RemoteProviderException>(() => _client.GetAccountAsync());
        Assert.Equal(RemoteFailureKind.Authentication, failure.Kind);
        Assert.Equal("AUTH_ERROR", failure.ProviderCode);
    }

    /// <summary>
    /// Read from the real API, and read again by the live project: it takes the key as it is, without
    /// dashes, or in capitals, so the key is sent as entered. The link it gives back always has the
    /// dashes, and the CDN takes only that form.
    /// </summary>
    [Fact]
    public async Task TheApiTakesAnyFormOfTheKeyAndTheLinkCarriesTheDashedOne()
    {
        var item = OneFile();
        var dashed = (await _client.ResolveLinkAsync(Ref(item), item.Files[0].Id.ToString())).Url;

        foreach (var form in new[] { MockTorBox.DefaultApiKey.Replace("-", ""), MockTorBox.DefaultApiKey.ToUpperInvariant() })
        {
            _key = form;
            Assert.True((await _client.GetAccountAsync()).Premium);
            var link = (await _client.ResolveLinkAsync(Ref(item), item.Files[0].Id.ToString())).Url;
            Assert.Equal(dashed.Query, link.Query);
        }

        using var asIssued = await _http.GetAsync(dashed);
        Assert.Equal(HttpStatusCode.OK, asIssued.StatusCode);
        using var withoutDashes = await _http.GetAsync(dashed.AbsoluteUri.Replace(MockTorBox.DefaultApiKey, MockTorBox.DefaultApiKey.Replace("-", "")));
        Assert.Equal(HttpStatusCode.Forbidden, withoutDashes.StatusCode);
    }

    [Theory]
    [InlineData("completed", true, true, false, null, true, RemoteStage.Ready)]
    [InlineData("cached", true, false, true, null, true, RemoteStage.Ready)]
    [InlineData("completed", false, true, false, null, true, RemoteStage.Processing)]
    [InlineData("completed", true, false, false, null, true, RemoteStage.Processing)]
    [InlineData("cached", false, false, true, null, true, RemoteStage.Processing)]
    [InlineData("cached", true, true, true, null, false, RemoteStage.MetadataPending)]
    [InlineData("queued", false, false, false, null, false, RemoteStage.Queued)]
    [InlineData("metaDL", false, false, false, null, false, RemoteStage.MetadataPending)]
    [InlineData("checkingResumeData", false, false, false, null, true, RemoteStage.MetadataPending)]
    [InlineData("downloading", false, false, false, null, true, RemoteStage.Processing)]
    [InlineData("paused", false, false, false, null, true, RemoteStage.Processing)]
    [InlineData("stalled (no seeds)", false, false, false, null, true, RemoteStage.Stalled)]
    [InlineData("failed", false, false, false, null, true, RemoteStage.Failed)]
    [InlineData("failed (hoster down)", false, false, false, null, true, RemoteStage.Failed)]
    [InlineData("downloading", false, false, false, "Hoster is down", true, RemoteStage.Failed)]
    // The real words these are about, with the flags a real account shows them with.
    [InlineData("expired", false, true, false, null, true, RemoteStage.Failed)]
    [InlineData("uploading", true, true, true, null, true, RemoteStage.Ready)]
    [InlineData("uploading (no peers)", true, true, true, null, true, RemoteStage.Ready)]
    [InlineData("downloading", true, true, true, null, true, RemoteStage.Ready)]
    [InlineData("uploading", false, false, false, null, true, RemoteStage.Processing)]
    [InlineData("uploading (no peers)", false, false, false, null, true, RemoteStage.Processing)]
    // Readiness comes first, as the reference client does. Invented input: none of 157 real items sent both.
    [InlineData("completed", true, true, false, "Hoster is down", true, RemoteStage.Ready)]
    [InlineData("completed", true, true, false, "Hoster is down", false, RemoteStage.Failed)]
    [InlineData("something new", false, false, false, null, true, RemoteStage.Unknown)]
    public async Task EveryStatusMapsOverHttp(string word, bool present, bool finished, bool cached, string? error, bool listsFiles, RemoteStage expected)
    {
        var state = new MockTorBoxState(word) { Present = present, Finished = finished, Cached = cached, Error = error, ListsFiles = listsFiles };
        foreach (var kind in new[] { RemoteItemKind.Torrent, RemoteItemKind.WebDownload, RemoteItemKind.Usenet })
        {
            var item = OneFile(kind, 1024, state);
            var read = await _client.GetAsync(Ref(item), fresh: true);
            Assert.NotNull(read);
            Assert.Equal(expected, read.Stage);
            Assert.Equal(word, read.RawState);
        }
    }

    [Theory]
    [InlineData("NO_AUTH", RemoteFailureKind.Authentication)]
    [InlineData("BAD_TOKEN", RemoteFailureKind.Authentication)]
    [InlineData("AUTH_ERROR", RemoteFailureKind.Authentication)]
    [InlineData("INVALID_DEVICE", RemoteFailureKind.Authentication)]
    [InlineData("PLAN_RESTRICTED_FEATURE", RemoteFailureKind.Rejected)]
    [InlineData("MONTHLY_LIMIT", RemoteFailureKind.AccountLimit)]
    [InlineData("DOWNLOAD_TOO_LARGE", RemoteFailureKind.Rejected)]
    [InlineData("TOO_MUCH_DATA", RemoteFailureKind.AccountLimit)]
    [InlineData("VENDOR_DISABLED", RemoteFailureKind.AccountLimit)]
    [InlineData("PAYMENT_REQUIRED", RemoteFailureKind.AccountLimit)]
    [InlineData("ACTIVE_LIMIT", RemoteFailureKind.AccountBusy)]
    [InlineData("COOLDOWN_LIMIT", RemoteFailureKind.AccountBusy)]
    [InlineData("DOWNLOAD_LIMIT_REACHED", RemoteFailureKind.AccountBusy)]
    [InlineData("ITEM_NOT_FOUND", RemoteFailureKind.NotFound)]
    [InlineData("ENDPOINT_NOT_FOUND", RemoteFailureKind.ServiceError)]
    [InlineData("BOZO_TORRENT", RemoteFailureKind.Rejected)]
    [InlineData("BOZO_NZB", RemoteFailureKind.Rejected)]
    [InlineData("LINK_OFFLINE", RemoteFailureKind.Rejected)]
    [InlineData("UNSUPPORTED_SITE", RemoteFailureKind.Rejected)]
    [InlineData("TEMPORARILY_DISABLED", RemoteFailureKind.Rejected)]
    [InlineData("INVALID_OPTION", RemoteFailureKind.Rejected)]
    [InlineData("MISSING_REQUIRED_OPTION", RemoteFailureKind.Rejected)]
    [InlineData("TOO_MANY_OPTIONS", RemoteFailureKind.Rejected)]
    [InlineData("DOWNLOAD_NOT_CACHED", RemoteFailureKind.NotCached)]
    [InlineData("DUPLICATE_ITEM", RemoteFailureKind.AlreadyExists)]
    [InlineData("DATABASE_ERROR", RemoteFailureKind.ServiceError)]
    [InlineData("UNKNOWN_ERROR", RemoteFailureKind.ServiceError)]
    [InlineData("DOWNLOAD_SERVER_ERROR", RemoteFailureKind.ServiceError)]
    [InlineData("NO_SERVERS_AVAILABLE_ERROR", RemoteFailureKind.ServiceError)]
    [InlineData("DIFF_ISSUE", RemoteFailureKind.ServiceError)]
    [InlineData("SLOW_DOWN", RemoteFailureKind.RateLimited)]
    [InlineData("RATE_LIMITED", RemoteFailureKind.RateLimited)]
    [InlineData("RATE_LIMIT_EXCEEDED", RemoteFailureKind.RateLimited)]
    public async Task EveryErrorCodeIsClassifiedOverHttp(string code, RemoteFailureKind expected)
    {
        _mock.Fail("GET user/me", MockFault.Error(code, "A scripted reason."), times: 1);
        var failure = await Assert.ThrowsAsync<RemoteProviderException>(() => _client.GetAccountAsync());
        Assert.Equal(expected, failure.Kind);
        Assert.Equal(code, failure.ProviderCode);
        Assert.Equal("A scripted reason.", failure.Message);
    }

    /// <summary>Captured from the real service: nothing is created, and the words are the only reason anyone gets.</summary>
    [Fact]
    public async Task ADeadWebLinkIsARefusalWithTheServicesOwnWords()
    {
        const string link = "https://hoster.example/dead";
        _mock.RefuseLink(link, MockFault.DeadWebLink);

        var failure = await Assert.ThrowsAsync<RemoteProviderException>(() =>
            _client.SubmitAsync(new RemoteSubmission { Kind = RemoteItemKind.WebDownload, Link = link }));

        Assert.Equal(RemoteFailureKind.Rejected, failure.Kind);
        Assert.Equal("DOWNLOAD_SERVER_ERROR", failure.ProviderCode);
        Assert.Contains("error scanning this link", failure.Message);
        Assert.Empty(await _client.ListAsync(RemoteItemKind.WebDownload, 0, 10, fresh: true));
    }

    [Fact]
    public async Task TheSameCodeWithoutWordsOrElsewhereIsStillAServiceError()
    {
        _mock.Fail("POST webdl/createwebdownload", MockFault.Empty(500) with { Code = "DOWNLOAD_SERVER_ERROR" }, times: 1);
        var noWords = await Assert.ThrowsAsync<RemoteProviderException>(() =>
            _client.SubmitAsync(new RemoteSubmission { Kind = RemoteItemKind.WebDownload, Link = "https://hoster.example/a" }));
        Assert.Equal(RemoteFailureKind.ServiceError, noWords.Kind);

        _mock.Fail("POST torrents/createtorrent", MockFault.DeadWebLink, times: 1);
        var torrent = await Assert.ThrowsAsync<RemoteProviderException>(() =>
            _client.SubmitAsync(new RemoteSubmission { Kind = RemoteItemKind.Torrent, Link = "magnet:?xt=urn:btih:" + new string('a', 40) }));
        Assert.Equal(RemoteFailureKind.ServiceError, torrent.Kind);

        _mock.Fail("GET user/me", MockFault.DeadWebLink, times: 1);
        var elsewhere = await Assert.ThrowsAsync<RemoteProviderException>(() => _client.GetAccountAsync());
        Assert.Equal(RemoteFailureKind.ServiceError, elsewhere.Kind);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(503)]
    public async Task ServerErrorsAreServiceErrors(int status)
    {
        _mock.Fail("GET user/me", MockFault.Status5xx(status), times: 1);
        var failure = await Assert.ThrowsAsync<RemoteProviderException>(() => _client.GetAccountAsync());
        Assert.Equal(RemoteFailureKind.ServiceError, failure.Kind);
        Assert.True((await _client.GetAccountAsync()).Premium);
    }

    [Fact]
    public async Task AGatewayPageIsAServiceError()
    {
        _mock.Fail("GET user/me", MockFault.NotJson(), times: 1);
        var failure = await Assert.ThrowsAsync<RemoteProviderException>(() => _client.GetAccountAsync());
        Assert.Equal(RemoteFailureKind.ServiceError, failure.Kind);
    }

    [Fact]
    public async Task AnUnreadable200IsABadResponse()
    {
        _mock.Fail("GET user/me", new MockFault(MockFaultKind.Respond) { Status = 200, RawBody = "<html>not json</html>" }, times: 1);
        var failure = await Assert.ThrowsAsync<RemoteProviderException>(() => _client.GetAccountAsync());
        Assert.Equal(RemoteFailureKind.BadResponse, failure.Kind);
    }

    [Fact]
    public async Task ALostConnectionIsANetworkFailure()
    {
        _mock.Fail("GET user/me", MockFault.Abort, times: 1);
        var failure = await Assert.ThrowsAsync<RemoteProviderException>(() => _client.GetAccountAsync());
        Assert.Equal(RemoteFailureKind.Network, failure.Kind);
        Assert.DoesNotContain("may have received", failure.Message);
    }

    [Fact]
    public async Task MissingItemIs404WithSuccessTrueAndReadsAsNull()
    {
        using var response = await RawAsync("torrents/mylist?id=1");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("ITEM_NOT_FOUND", body.RootElement.GetProperty("error").GetString());
        Assert.Equal(JsonValueKind.Object, body.RootElement.GetProperty("data").ValueKind);

        Assert.Null(await _client.GetAsync(Ref(RemoteItemKind.Torrent, 1), fresh: true));
    }

    [Fact]
    public async Task ItemsOfAnotherKindAreNotFound()
    {
        var torrent = OneFile(RemoteItemKind.Torrent);
        Assert.Null(await _client.GetAsync(Ref(RemoteItemKind.WebDownload, torrent.Id), fresh: true));
    }

    // ---- Rate limits --------------------------------------------------------------------------

    [Fact]
    public async Task RateHeadersAndRetryAfterAreOnEveryResponse()
    {
        foreach (var path in new[] { "user/me", "torrents/mylist?id=1", "torrents/getqueued" })
        {
            using var response = await RawAsync(path);
            Assert.True(response.Headers.Contains("X-RateLimit-Limit"), path);
            Assert.True(response.Headers.Contains("X-RateLimit-Remaining"), path);
            Assert.True(response.Headers.Contains("X-RateLimit-Reset"), path);
            Assert.NotNull(response.Headers.RetryAfter?.Delta);
        }
    }

    [Fact]
    public async Task TooManyRequestsCarriesItsWait()
    {
        _mock.Fail("GET user/me", MockFault.TooManyRequests(37), times: 1);
        var failure = await Assert.ThrowsAsync<RemoteProviderException>(() => _client.GetAccountAsync());
        Assert.Equal(RemoteFailureKind.RateLimited, failure.Kind);
        Assert.Equal(TimeSpan.FromSeconds(37), failure.RetryAfter);
    }

    [Fact]
    public async Task RetryAfterOnSuccessesIsIgnored()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 5; i++) await _client.GetAccountAsync();
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"Took {clock.Elapsed}");
    }

    [Fact]
    public async Task GoingOverTheBudgetIsA429()
    {
        _mock.RateLimit = 1;
        using (var first = await RawAsync("user/me")) Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var second = await RawAsync("user/me");
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
    }

    [Fact]
    public async Task TheClientWaitsOutAnUsedUpBudgetInsteadOfHittingTheLimit()
    {
        _mock.RateLimit = 2;
        _mock.RateWindow = TimeSpan.FromSeconds(1);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await _client.GetAccountAsync();
        await _client.GetAccountAsync();
        await _client.GetAccountAsync();
        Assert.Equal(3, _mock.Calls("GET user/me"));
        Assert.True(clock.Elapsed > TimeSpan.FromMilliseconds(100), $"Took {clock.Elapsed}");
    }

    // ---- Lists --------------------------------------------------------------------------------

    [Fact]
    public async Task ListsPageByOffsetAndLimit()
    {
        var a = OneFile();
        var b = OneFile();
        var c = OneFile();
        OneFile(RemoteItemKind.WebDownload);

        var all = await _client.ListAsync(RemoteItemKind.Torrent, 0, 50, fresh: true);
        Assert.Equal([c.Id.ToString(), b.Id.ToString(), a.Id.ToString()], all.Select(item => item.Ref.ItemId));
        var page = await _client.ListAsync(RemoteItemKind.Torrent, 1, 1, fresh: true);
        Assert.Equal(b.Id.ToString(), Assert.Single(page).Ref.ItemId);
        Assert.Empty(await _client.ListAsync(RemoteItemKind.Torrent, 10, 5, fresh: true));
    }

    [Fact]
    public async Task AnEmptyListThatAnswersNotFoundIsJustEmpty()
    {
        _mock.EmptyListIsNotFound = true;
        Assert.Empty(await _client.ListAsync(RemoteItemKind.Torrent, 0, 50, fresh: true));
    }

    [Theory]
    [InlineData("""{"success":true,"error":"ITEM_NOT_FOUND","detail":"No torrents found for this user.","data":{}}""", 404)]
    [InlineData("""{"detail":"No torrents found for this user."}""", 404)]
    [InlineData("""{"success":true,"error":null,"detail":"ok","data":[]}""", 200)]
    public async Task OnlyWhatSaysItIsEmptyIsEmpty(string body, int status)
    {
        _mock.Fail("GET torrents/mylist", new MockFault(MockFaultKind.Respond) { Status = status, RawBody = body });
        _mock.Fail("GET queued/getqueued", new MockFault(MockFaultKind.Respond) { Status = status, RawBody = body });
        Assert.Empty(await _client.ListAsync(RemoteItemKind.Torrent, 0, 50, fresh: true));
        Assert.Empty(await _client.ListQueuedAsync(RemoteItemKind.Torrent, 0, 50, fresh: true));
    }

    [Theory]
    [InlineData("""<html>Not here</html>""", 404, RemoteFailureKind.ServiceError)]
    [InlineData("", 404, RemoteFailureKind.ServiceError)]
    [InlineData("""{"success":false,"error":"ENDPOINT_NOT_FOUND","detail":"No such endpoint.","data":null}""", 404, RemoteFailureKind.ServiceError)]
    [InlineData("""{"success":false,"error":"DEPRECATED","detail":"Use the other one.","data":null}""", 410, RemoteFailureKind.ServiceError)]
    [InlineData("""{"detail":"Gone away."}""", 410, RemoteFailureKind.ServiceError)]
    [InlineData("""{"success":false,"error":null,"data":null}""", 200, RemoteFailureKind.BadResponse)]
    [InlineData("""{"success":true,"error":null,"data":{}}""", 200, RemoteFailureKind.BadResponse)]
    [InlineData("""{"success":true,"error":null,"data":"later"}""", 200, RemoteFailureKind.BadResponse)]
    [InlineData("""{"success":true,"error":null}""", 200, RemoteFailureKind.BadResponse)]
    // The real answer for nothing is data:[], read on all three families and the queue. Null says nothing, so nothing gets resent over it.
    [InlineData("""{"success":true,"error":null,"detail":"ok","data":null}""", 200, RemoteFailureKind.BadResponse)]
    public async Task WhatDoesntSayItIsEmptyIsAFailedLook(string body, int status, RemoteFailureKind kind)
    {
        _mock.Fail("GET torrents/mylist", new MockFault(MockFaultKind.Respond) { Status = status, RawBody = body });
        _mock.Fail("GET queued/getqueued", new MockFault(MockFaultKind.Respond) { Status = status, RawBody = body });
        Assert.Equal(kind, (await Assert.ThrowsAsync<RemoteProviderException>(() => _client.ListAsync(RemoteItemKind.Torrent, 0, 50, fresh: true))).Kind);
        Assert.Equal(kind, (await Assert.ThrowsAsync<RemoteProviderException>(() => _client.ListQueuedAsync(RemoteItemKind.Torrent, 0, 50, fresh: true))).Kind);
    }

    [Theory]
    [InlineData("""<html>Not here</html>""", 404)]
    [InlineData("""{"success":false,"error":"ENDPOINT_NOT_FOUND","detail":"No such endpoint.","data":null}""", 404)]
    [InlineData("""{"success":true,"error":null,"data":null}""", 200)]
    [InlineData("""{"success":true,"error":null,"data":{}}""", 200)]
    public async Task OneItemIsOnlyGoneWhenTheProviderSaysSo(string body, int status)
    {
        var item = OneFile();
        _mock.Fail("GET torrents/mylist", new MockFault(MockFaultKind.Respond) { Status = status, RawBody = body });
        await Assert.ThrowsAsync<RemoteProviderException>(() => _client.GetAsync(Ref(item), fresh: true));
    }

    [Fact]
    public async Task ASingleItemNotInAListIsAListOfOne()
    {
        var item = OneFile();
        _mock.Fail("GET torrents/mylist", new MockFault(MockFaultKind.Respond)
        {
            Status = 200,
            RawBody = $$$"""{"success":true,"error":null,"data":{"id":{{{item.Id}}},"name":"Alone","hash":"abc"}}""",
        });
        var listed = await _client.ListAsync(RemoteItemKind.Torrent, 0, 50, fresh: true);
        Assert.Equal(item.Id.ToString(), Assert.Single(listed).Ref.ItemId);
    }

    [Fact]
    public async Task AListAnsweringOneIdPicksThatIdOrFails()
    {
        var item = OneFile();
        _mock.Fail("GET torrents/mylist", new MockFault(MockFaultKind.Respond)
        {
            Status = 200,
            RawBody = $$"""{"success":true,"error":null,"data":[{"id":1},{"id":{{item.Id}},"name":"Mine"}]}""",
        }, times: 1);
        Assert.Equal(item.Id.ToString(), (await _client.GetAsync(Ref(item), fresh: true))!.Ref.ItemId);

        _mock.Fail("GET torrents/mylist", new MockFault(MockFaultKind.Respond) { Status = 200, RawBody = """{"success":true,"error":null,"data":[{"id":1}]}""" }, times: 1);
        await Assert.ThrowsAsync<RemoteProviderException>(() => _client.GetAsync(Ref(item), fresh: true));
    }

    [Fact]
    public async Task AHosterListThatIsntOneIsAFailureNotNoHosters()
    {
        _mock.Fail("GET webdl/hosters", new MockFault(MockFaultKind.Respond) { Status = 200, RawBody = """{"success":true,"error":null,"data":null}""" });
        Assert.Equal(RemoteFailureKind.BadResponse, (await Assert.ThrowsAsync<RemoteProviderException>(() => _client.GetHostersAsync())).Kind);
    }

    [Theory]
    [InlineData("00123", "123")]
    [InlineData(" 7 ", "7")]
    [InlineData("0", "0")]
    public void AnIdHasOneSpelling(string id, string expected)
        => Assert.Equal(expected, ((IRemoteCatalog)_client).NormalizeItemId(id));

    [Theory]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("99999999999999999999999")]
    [InlineData("")]
    public void WhatCantBeAnIdIsRefused(string id)
        => Assert.Null(((IRemoteCatalog)_client).NormalizeItemId(id));

    [Fact]
    public async Task OnlyAFreshListSeesChanges()
    {
        Assert.Empty(await _client.ListAsync(RemoteItemKind.Torrent, 0, 50, fresh: false));
        OneFile();
        Assert.Empty(await _client.ListAsync(RemoteItemKind.Torrent, 0, 50, fresh: false));
        Assert.Single(await _client.ListAsync(RemoteItemKind.Torrent, 0, 50, fresh: true));
        Assert.Single(await _client.ListAsync(RemoteItemKind.Torrent, 0, 50, fresh: false));
    }

    [Fact]
    public async Task ScriptedStatesAdvanceOnlyOnFreshReads()
    {
        var item = OneFile(RemoteItemKind.WebDownload, 1024, MockTorBoxState.Queued, MockTorBoxState.Downloading(0.5, 20), MockTorBoxState.Ready);

        Assert.Equal(RemoteStage.Queued, (await _client.GetAsync(Ref(item), fresh: false))!.Stage);
        Assert.Equal(RemoteStage.Queued, (await _client.GetAsync(Ref(item), fresh: false))!.Stage);
        Assert.Equal(RemoteStage.Queued, (await _client.GetAsync(Ref(item), fresh: true))!.Stage);
        var middle = await _client.GetAsync(Ref(item), fresh: true);
        Assert.Equal(RemoteStage.Processing, middle!.Stage);
        Assert.Equal(0.5, middle.Progress);
        Assert.Equal(20, middle.EtaSeconds);
        Assert.Equal(RemoteStage.Ready, (await _client.GetAsync(Ref(item), fresh: true))!.Stage);
        Assert.Equal(RemoteStage.Ready, (await _client.GetAsync(Ref(item), fresh: true))!.Stage);
    }

    [Fact]
    public async Task ZeroSizeInfectedNestedAndTraversalFilesComeThroughAsTheyAre()
    {
        var item = _mock.AddItem(RemoteItemKind.Torrent, "Pack", [
            MockTorBoxFile.Of(9, "Pack/deep/er/a.bin", 16),
            MockTorBoxFile.Of(2, "Pack/a.bin", 8),
            MockTorBoxFile.Of(7, "Pack/empty.txt", 0),
            MockTorBoxFile.Of(0, "Pack/virus.exe", 32, infected: true),
            MockTorBoxFile.Of(5, "../../escape.exe", 4),
            MockTorBoxFile.Of(6, "C:/Windows/evil.dll", 4),
            MockTorBoxFile.Of(3, "flat.bin", 4, withMd5: true),
        ]);

        var read = await _client.GetAsync(Ref(item), fresh: true);
        Assert.Equal(["9", "2", "7", "0", "5", "6", "3"], read!.Files.Select(file => file.FileId));
        Assert.Equal(0, read.Files[2].Size);
        Assert.True(read.Files[3].Infected);
        Assert.Equal(item.Files[6].Md5, read.Files[6].Md5);
        Assert.Null(read.Files[0].Md5);
        Assert.Equal(Path.Combine("Pack", "deep", "er", "a.bin"), RemotePaths.ToSafeRelativePath(read.Files[0].Path));
        Assert.Null(RemotePaths.ToSafeRelativePath(read.Files[4].Path));
        Assert.Null(RemotePaths.ToSafeRelativePath(read.Files[5].Path));
        Assert.Equal("flat.bin", RemotePaths.ToSafeRelativePath(read.Files[6].Path));
    }

    [Fact]
    public async Task AlternativeHashesAreReported()
    {
        var item = OneFile(RemoteItemKind.WebDownload);
        item.AlternativeHashes.Add("aabbcc");
        var read = await _client.GetAsync(Ref(item), fresh: true);
        Assert.Equal(item.Hash, read!.Hash);
        Assert.Equal(["aabbcc"], read.AlternativeHashes);
    }

    // ---- Cache checks and submissions ---------------------------------------------------------

    [Fact]
    public async Task ACacheCheckByAnAlternativeHashAnswersThatHash()
    {
        const string main = "1111222233334444555566667777888899990000";
        const string other = "0123456789abcdef0123456789abcdef";
        _mock.AddCached(main, "test_folder", 19);
        _mock.AddCachedAlternative(other, main);

        var hits = await _client.CheckCachedAsync(RemoteItemKind.WebDownload, [other]);

        Assert.True(hits.ContainsKey(other));
        Assert.Equal(19, hits[other].Size);
    }

    [Fact]
    public async Task CacheChecksReturnOnlyHits()
    {
        _mock.AddCached("1111222233334444555566667777888899990000", "test_folder", 19);
        var hits = await _client.CheckCachedAsync(RemoteItemKind.Torrent, ["1111222233334444555566667777888899990000", "ffffffffffffffffffffffffffffffffffffffff"]);
        var hit = Assert.Single(hits);
        Assert.Equal("test_folder", hit.Value.Name);
        Assert.Equal(19, hit.Value.Size);
        Assert.True(hits.ContainsKey("1111222233334444555566667777888899990000"));
        Assert.Equal(1, _mock.Calls("POST torrents/checkcached"));
        Assert.Empty(await _client.CheckCachedAsync(RemoteItemKind.WebDownload, ["abc"]));
    }

    [Fact]
    public async Task CacheCheckByQueryWorksInBothFormats()
    {
        _mock.AddCached("abc", "x", 1);
        using (var map = await RawAsync("webdl/checkcached?hash=abc,def&format=object"))
        {
            using var body = JsonDocument.Parse(await map.Content.ReadAsStringAsync());
            Assert.Equal("x", body.RootElement.GetProperty("data").GetProperty("abc").GetProperty("name").GetString());
        }

        using var list = await RawAsync("webdl/checkcached?hash=abc&hash=def&format=list");
        using var listed = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        Assert.Equal(1, listed.RootElement.GetProperty("data").GetArrayLength());
    }

    [Fact]
    public async Task AMagnetIsSubmittedAsAMultipartFormAndWalksItsStates()
    {
        const string hash = "aaaabbbbccccddddeeeeffff0000111122223333";
        var magnet = $"magnet:?xt=urn:btih:{hash}&dn=Name";
        _mock.ScriptSubmission(hash, [MockTorBoxFile.Of(_mock.NextFileId(), "Name/a.bin", 8)], [MockTorBoxState.MetaData, MockTorBoxState.Downloading(), MockTorBoxState.Ready]);

        Assert.Equal(hash, _client.PredictHash(RemoteItemKind.Torrent, magnet));
        var result = await _client.SubmitAsync(new RemoteSubmission { Kind = RemoteItemKind.Torrent, Link = magnet });
        Assert.Equal(hash, result.Hash);
        Assert.False(result.Queued);
        Assert.Equal(magnet, _mock.Submissions.Single().Link);
        Assert.True(string.IsNullOrEmpty(_mock.Submissions.Single().Name));

        Assert.Equal(RemoteStage.MetadataPending, (await _client.GetAsync(result.Ref!, fresh: true))!.Stage);
        Assert.Equal(RemoteStage.Processing, (await _client.GetAsync(result.Ref!, fresh: true))!.Stage);
        Assert.Equal(RemoteStage.Ready, (await _client.GetAsync(result.Ref!, fresh: true))!.Stage);
    }

    [Fact]
    public async Task ATorrentFileIsUploaded()
    {
        var result = await _client.SubmitAsync(new RemoteSubmission { Kind = RemoteItemKind.Torrent, TorrentFile = [1, 2, 3, 4] });
        Assert.NotNull(result.Ref);
        Assert.Equal(4, _mock.Submissions.Single().FileBytes);
        Assert.Null(_mock.Submissions.Single().Link);
    }

    [Fact]
    public async Task TheQueueIsListedByKindAndAnEmptyOneIsAnAnswer()
    {
        const string link = "https://hoster.invalid/queued-one";
        var hash = _client.PredictHash(RemoteItemKind.WebDownload, link)!;
        _mock.ScriptSubmission(hash, [], [MockTorBoxState.Queued], queued: true);
        var result = await _client.SubmitAsync(new RemoteSubmission { Kind = RemoteItemKind.WebDownload, Link = link });
        Assert.True(result.Queued);
        Assert.Null(result.Ref);

        var queued = Assert.Single(await _client.ListQueuedAsync(RemoteItemKind.WebDownload, 0, 50, fresh: true));
        Assert.Equal(hash, queued.Hash);
        Assert.Empty(await _client.ListQueuedAsync(RemoteItemKind.Torrent, 0, 50, fresh: true));

        // Past the end.
        Assert.Empty(await _client.ListQueuedAsync(RemoteItemKind.WebDownload, 50, 50, fresh: true));

        _mock.EmptyListIsNotFound = true;
        Assert.Empty(await _client.ListQueuedAsync(RemoteItemKind.Usenet, 0, 50, fresh: false));
    }

    [Fact]
    public async Task AskingForCachedOnlyIsRefusedWhenItIsNot()
    {
        const string hash = "1111222233334444555566667777888899990000";
        var magnet = $"magnet:?xt=urn:btih:{hash}";
        var refused = await Assert.ThrowsAsync<RemoteProviderException>(() =>
            _client.SubmitAsync(new RemoteSubmission { Kind = RemoteItemKind.Torrent, Link = magnet, OnlyIfCached = true }));
        Assert.Equal("DOWNLOAD_NOT_CACHED", refused.ProviderCode);
        Assert.Equal(RemoteFailureKind.NotCached, refused.Kind);
        Assert.Empty(await _client.ListAsync(RemoteItemKind.Torrent, 0, 50, fresh: true));

        _mock.AddCached(hash);
        var added = await _client.SubmitAsync(new RemoteSubmission { Kind = RemoteItemKind.Torrent, Link = magnet, OnlyIfCached = true });
        Assert.Equal(RemoteStage.Ready, (await _client.GetAsync(added.Ref!, fresh: true))!.Stage);
    }

    [Fact]
    public async Task AWebDownloadIsAFormWhoseHashIsTheMd5OfTheLink()
    {
        const string link = "https://hoster.invalid/file/abc#decryption-key";
        var predicted = _client.PredictHash(RemoteItemKind.WebDownload, link);
        var result = await _client.SubmitAsync(new RemoteSubmission { Kind = RemoteItemKind.WebDownload, Link = link });
        Assert.Equal(predicted, result.Hash);
        Assert.Equal(link, _mock.Submissions.Single().Link);
        Assert.Equal(1, _mock.Calls("POST webdl/createwebdownload"));

        var listed = Assert.Single(await _client.ListAsync(RemoteItemKind.WebDownload, 0, 50, fresh: true));
        Assert.Equal(predicted, listed.Hash);
    }

    [Fact]
    public async Task APasswordProtectedLinkAsksForItsPassword()
    {
        const string link = "https://hoster.invalid/protected";
        _mock.RequireLinkPassword(link, "invented-password");
        var missing = await Assert.ThrowsAsync<RemoteProviderException>(() =>
            _client.SubmitAsync(new RemoteSubmission { Kind = RemoteItemKind.WebDownload, Link = link }));
        Assert.Equal(RemoteFailureKind.LinkPasswordRequired, missing.Kind);
        var wrong = await Assert.ThrowsAsync<RemoteProviderException>(() =>
            _client.SubmitAsync(new RemoteSubmission { Kind = RemoteItemKind.WebDownload, Link = link, LinkPassword = "other" }));
        Assert.Equal(RemoteFailureKind.LinkPasswordRequired, wrong.Kind);

        var added = await _client.SubmitAsync(new RemoteSubmission { Kind = RemoteItemKind.WebDownload, Link = link, LinkPassword = "invented-password" });
        Assert.NotNull(added.Ref);
    }

    [Fact]
    public async Task AQueuedSubmissionHasNoItemYetAndShowsInTheQueueOnly()
    {
        const string link = "https://hoster.invalid/waiting";
        var hash = _client.PredictHash(RemoteItemKind.WebDownload, link)!;
        _mock.ScriptSubmission(hash, [MockTorBoxFile.Of(_mock.NextFileId(), "w/w.bin", 8)], [MockTorBoxState.Ready], queued: true);

        var result = await _client.SubmitAsync(new RemoteSubmission { Kind = RemoteItemKind.WebDownload, Link = link });
        Assert.True(result.Queued);
        Assert.Null(result.Ref);
        Assert.Empty(await _client.ListAsync(RemoteItemKind.WebDownload, 0, 50, fresh: true));

        using var queue = await RawAsync("queued/getqueued?type=webdl&bypass_cache=true");
        using var body = JsonDocument.Parse(await queue.Content.ReadAsStringAsync());
        Assert.Equal(hash, body.RootElement.GetProperty("data")[0].GetProperty("hash").GetString());
        using var other = await RawAsync("queued/getqueued?type=torrent");
        using var otherBody = JsonDocument.Parse(await other.Content.ReadAsStringAsync());
        Assert.Equal(0, otherBody.RootElement.GetProperty("data").GetArrayLength());
    }

    [Fact]
    public async Task TheDeprecatedQueuePathAnswers410()
    {
        using var response = await RawAsync("torrents/getqueued");
        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("DEPRECATED", body.RootElement.GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("webdl/getqueued")]
    [InlineData("usenet/getqueued")]
    [InlineData("nothing/here")]
    public async Task AnAddressThatIsNotThereAnswersWithNoEnvelope(string path)
    {
        using var response = await RawAsync(path);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Not Found", body.RootElement.GetProperty("detail").GetString());
        Assert.False(body.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task ABadParameterIsRefusedAndItsListOfDetailsIsNotTakenForWords()
    {
        var failure = await Assert.ThrowsAsync<RemoteProviderException>(() => _client.GetAsync(new RemoteItemRef("torbox", RemoteItemKind.Torrent, "abc"), fresh: true));
        Assert.Equal(RemoteFailureKind.Rejected, failure.Kind);
        Assert.Contains("422", failure.Message);
    }

    [Fact]
    public async Task ALostAnswerStillLeavesTheItemAndOneSubmit()
    {
        const string link = "https://hoster.invalid/lost-answer";
        _mock.Fail("POST webdl/createwebdownload", MockFault.AcceptThenAbort, times: 1);

        var failure = await Assert.ThrowsAsync<RemoteProviderException>(() =>
            _client.SubmitAsync(new RemoteSubmission { Kind = RemoteItemKind.WebDownload, Link = link }));
        Assert.Equal(RemoteFailureKind.Network, failure.Kind);
        Assert.Contains("may have received", failure.Message);

        var listed = Assert.Single(await _client.ListAsync(RemoteItemKind.WebDownload, 0, 50, fresh: true));
        Assert.Equal(_client.PredictHash(RemoteItemKind.WebDownload, link), listed.Hash);
        Assert.Equal(1, _mock.Calls("POST webdl/createwebdownload"));
    }

    [Fact]
    public async Task SubmittingAnExistingHashReturnsTheSameItemOrARefusal()
    {
        const string link = "https://hoster.invalid/twice";
        var first = await _client.SubmitAsync(new RemoteSubmission { Kind = RemoteItemKind.WebDownload, Link = link });
        var second = await _client.SubmitAsync(new RemoteSubmission { Kind = RemoteItemKind.WebDownload, Link = link });
        Assert.Equal(first.Ref, second.Ref);
        Assert.Single(await _client.ListAsync(RemoteItemKind.WebDownload, 0, 50, fresh: true));

        _mock.DuplicatesAreErrors = true;
        var refused = await Assert.ThrowsAsync<RemoteProviderException>(() =>
            _client.SubmitAsync(new RemoteSubmission { Kind = RemoteItemKind.WebDownload, Link = link }));
        Assert.Equal("DUPLICATE_ITEM", refused.ProviderCode);
    }

    [Fact]
    public async Task ASubmittedLinkInAnErrorDoesNotComeBackInTheMessage()
    {
        _mock.Fail("POST webdl/createwebdownload", MockFault.Error("LINK_OFFLINE", "Dead: https://hoster.invalid/secret-path#secret-fragment"), times: 1);
        var failure = await Assert.ThrowsAsync<RemoteProviderException>(() =>
            _client.SubmitAsync(new RemoteSubmission { Kind = RemoteItemKind.WebDownload, Link = "https://hoster.invalid/secret-path#secret-fragment" }));
        Assert.DoesNotContain("secret", failure.ToString());
    }

    // ---- Hosters and removal ------------------------------------------------------------------

    [Fact]
    public async Task HostersKeepTheirPatternAndAvailability()
    {
        var hosters = await _client.GetHostersAsync();
        var mega = hosters.Single(hoster => hoster.Name == "Mega");
        Assert.True(mega.Available);
        Assert.Matches(mega.Pattern!, "https://mega.nz/file/abc");
        Assert.Equal(100, mega.DailyLinkLimit);
        var down = hosters.Single(hoster => hoster.Name == "Rapidgator");
        Assert.False(down.Available);
        Assert.Equal("Down for maintenance", down.Note);
    }

    [Theory]
    [InlineData(RemoteItemKind.Torrent, "POST torrents/controltorrent")]
    [InlineData(RemoteItemKind.WebDownload, "POST webdl/controlwebdownload")]
    [InlineData(RemoteItemKind.Usenet, "POST usenet/controlusenetdownload")]
    public async Task RemovalUsesTheRightControlAndDeletes(RemoteItemKind kind, string route)
    {
        var item = OneFile(kind);
        await _client.RemoveAsync(Ref(item));
        Assert.Equal((route, item.Id), _mock.Deleted.Single());
        Assert.Null(await _client.GetAsync(Ref(item), fresh: true));

        var again = await Assert.ThrowsAsync<RemoteProviderException>(() => _client.RemoveAsync(Ref(item)));
        Assert.Equal(RemoteFailureKind.NotFound, again.Kind);
    }

    // ---- Direct links and the CDN -------------------------------------------------------------

    [Fact]
    public async Task ALinkRequestSendsBearerAndTokenAndNothingElseUsesTheKeyInItsAddress()
    {
        var item = OneFile();
        var link = await _client.ResolveLinkAsync(Ref(item), item.Files[0].Id.ToString());
        Assert.Contains("/torbox-cdn/", link.Url.AbsoluteUri);
        Assert.Contains(MockTorBox.DefaultApiKey, link.Url.Query);
        Assert.Equal($"Bearer {MockTorBox.DefaultApiKey}", _mock.AuthorizationHeaders.Single());
        Assert.Equal(1, _mock.Calls("GET torrents/requestdl"));
        Assert.DoesNotContain(MockTorBox.DefaultApiKey, link.ToString());
    }

    [Fact]
    public async Task AnHttpLinkIsOnlyAcceptedFromALocalApi()
    {
        var item = OneFile();
        Assert.StartsWith("http://127.0.0.1", (await _client.ResolveLinkAsync(Ref(item), item.Files[0].Id.ToString())).Url.AbsoluteUri);
        // An outside API sending a plain-text link is refused by the same check: TorBoxClientTests.UnusableLinkIsRefused.
    }

    [Theory]
    [InlineData(RemoteItemKind.Torrent, "torrent_id", "torrents")]
    [InlineData(RemoteItemKind.WebDownload, "web_id", "webdl")]
    [InlineData(RemoteItemKind.Usenet, "usenet_id", "usenet")]
    public async Task EachKindHasItsOwnLinkEndpoint(RemoteItemKind kind, string idName, string family)
    {
        var item = OneFile(kind);
        await _client.ResolveLinkAsync(Ref(item), item.Files[0].Id.ToString());
        Assert.Equal(1, _mock.Calls($"GET {family}/requestdl"));

        using var noToken = await RawAsync($"{family}/requestdl?{idName}={item.Id}&file_id={item.Files[0].Id}");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noToken.StatusCode);
    }

    [Fact]
    public async Task ATokenAloneIsEnoughAndAWrongOneIsNot()
    {
        var item = OneFile();
        var file = item.Files[0].Id;
        using var good = await RawAsync($"torrents/requestdl?torrent_id={item.Id}&file_id={file}&token={MockTorBox.DefaultApiKey}", bearer: null);
        Assert.Equal(HttpStatusCode.OK, good.StatusCode);
        using var bad = await RawAsync($"torrents/requestdl?torrent_id={item.Id}&file_id={file}&token=ffffffff", bearer: null);
        Assert.Equal(HttpStatusCode.Forbidden, bad.StatusCode);
    }

    [Fact]
    public async Task LinksForMissingThingsReadAsTheServiceErrorsTheRealOneAnswers()
    {
        var item = OneFile();
        var noFile = await Assert.ThrowsAsync<RemoteProviderException>(() => _client.ResolveLinkAsync(Ref(item), "99999"));
        Assert.Equal(RemoteFailureKind.ServiceError, noFile.Kind);
        Assert.Equal("DATABASE_ERROR", noFile.ProviderCode);
        var noItem = await Assert.ThrowsAsync<RemoteProviderException>(() => _client.ResolveLinkAsync(Ref(RemoteItemKind.Torrent, 1), "0"));
        Assert.Equal(RemoteFailureKind.ServiceError, noItem.Kind);
        var expired = OneFile(RemoteItemKind.Torrent, 1024, MockTorBoxState.Expired);
        var noLonger = await Assert.ThrowsAsync<RemoteProviderException>(() => _client.ResolveLinkAsync(Ref(expired), expired.Files[0].Id.ToString()));
        Assert.Equal(RemoteFailureKind.ServiceError, noLonger.Kind);
    }

    [Fact]
    public async Task ALinkForAnItemStillBeingFetchedIsRefused()
    {
        var item = OneFile(RemoteItemKind.WebDownload, 1024, MockTorBoxState.Downloading());
        var refused = await Assert.ThrowsAsync<RemoteProviderException>(() => _client.ResolveLinkAsync(Ref(item), item.Files[0].Id.ToString()));
        Assert.Equal(RemoteFailureKind.ServiceError, refused.Kind);
    }

    private async Task<(MockTorBoxFile File, Uri Link)> LinkAsync(int size = 100_000, int seed = 5)
    {
        var file = MockTorBoxFile.Of(_mock.NextFileId(), "Item/nested/a b.bin", size, seed);
        var item = _mock.AddItem(RemoteItemKind.Torrent, "Item", [file]);
        return (file, (await _client.ResolveLinkAsync(Ref(item), file.Id.ToString())).Url);
    }

    [Fact]
    public async Task TheCdnServesFilesLikeTheRealOne()
    {
        var (file, link) = await LinkAsync();

        using var whole = await _http.GetAsync(link);
        Assert.Equal(HttpStatusCode.OK, whole.StatusCode);
        Assert.Equal("bytes", Assert.Single(whole.Headers.AcceptRanges));
        Assert.Null(whole.Headers.ETag);
        Assert.Equal(file.LastModified, whole.Content.Headers.LastModified);
        Assert.Equal("a b.bin", whole.Content.Headers.ContentDisposition!.FileName!.Trim('"'));
        Assert.Equal(file.Content, await whole.Content.ReadAsByteArrayAsync());

        using var part = new HttpRequestMessage(HttpMethod.Get, link);
        part.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(10, 19);
        using var ranged = await _http.SendAsync(part);
        Assert.Equal(HttpStatusCode.PartialContent, ranged.StatusCode);
        Assert.Equal("bytes 10-19/100000", ranged.Content.Headers.ContentRange!.ToString());
        Assert.Equal(file.Content[10..20], await ranged.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task PastTheEndTheCdnAnswersAsTheRealOneDoes()
    {
        var item = OneFile(RemoteItemKind.Torrent, 423);
        var link = await _client.ResolveLinkAsync(Ref(item), item.Files[0].Id.ToString());
        using var plain = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, link.Url);
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(423, null);
        using var response = await plain.SendAsync(request);

        Assert.Equal(System.Net.HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("bytes */423", response.Content.Headers.ContentRange!.ToString());
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task AChangedFileIgnoresARangeWithAnOldValidator()
    {
        var (file, link) = await LinkAsync();
        using (var first = await _http.GetAsync(link)) Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var old = file.LastModified;
        file.Replace(100_000, 6);

        using var request = new HttpRequestMessage(HttpMethod.Get, link);
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(10, null);
        request.Headers.IfRange = new System.Net.Http.Headers.RangeConditionHeaderValue(old);
        using var answer = await _http.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Equal(file.Content, await answer.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData("wrong")]
    [InlineData(null)]
    public async Task AWrongOrMissingTokenIsAnEmpty403(string? token)
    {
        var (_, link) = await LinkAsync();
        var address = link.GetLeftPart(UriPartial.Path) + (token is null ? "" : $"?token={token}");
        using var answer = await _http.GetAsync(address);
        Assert.Equal(HttpStatusCode.Forbidden, answer.StatusCode);
        Assert.Empty(await answer.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task AnUnknownUuidIsAnEmpty404()
    {
        using var answer = await _http.GetAsync($"{_server.BaseAddress}/torbox-cdn/{Guid.NewGuid()}?token={MockTorBox.DefaultApiKey}");
        Assert.Equal(HttpStatusCode.NotFound, answer.StatusCode);
        Assert.Empty(await answer.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task ExpiredLinksStopWorkingAndANewOneWorks()
    {
        var (file, link) = await LinkAsync();
        _mock.ExpireLinks();
        using (var expired = await _http.GetAsync(link))
        {
            Assert.Equal(HttpStatusCode.Forbidden, expired.StatusCode);
            Assert.Empty(await expired.Content.ReadAsByteArrayAsync());
        }

        var item = _mock.AddItem(RemoteItemKind.Torrent, "Another", [file]);
        var fresh = await _client.ResolveLinkAsync(Ref(item), file.Id.ToString());
        Assert.NotEqual(link, fresh.Url);
        using var working = await _http.GetAsync(fresh.Url);
        Assert.Equal(HttpStatusCode.OK, working.StatusCode);
    }

    [Fact]
    public async Task LinksExpireByAge()
    {
        _mock.LinkLifetime = TimeSpan.FromMilliseconds(50);
        _mock.ExpiredLinkStatus = 410;
        var (_, link) = await LinkAsync();
        await Task.Delay(120);
        using var answer = await _http.GetAsync(link);
        Assert.Equal(HttpStatusCode.Gone, answer.StatusCode);
    }

    [Fact]
    public async Task ALinkCanDieMidTransfer()
    {
        _mock.NextLinkDiesAfter(64 * 1024);
        var (_, link) = await LinkAsync(1024 * 1024);
        var died = 0;
        _mock.LinkDied = () => Interlocked.Increment(ref died);

        await Assert.ThrowsAnyAsync<HttpRequestException>(() => _http.GetByteArrayAsync(link));
        Assert.Equal(1, died);
        using var after = await _http.GetAsync(link);
        Assert.Equal(HttpStatusCode.Forbidden, after.StatusCode);
    }

    [Fact]
    public async Task ARemovedItemsLinkIsAnEmpty404()
    {
        var file = MockTorBoxFile.Of(_mock.NextFileId(), "x/y.bin", 16);
        var item = _mock.AddItem(RemoteItemKind.Torrent, "x", [file]);
        var link = (await _client.ResolveLinkAsync(Ref(item), file.Id.ToString())).Url;
        _mock.RemoveItem(item);
        using var answer = await _http.GetAsync(link);
        Assert.Equal(HttpStatusCode.NotFound, answer.StatusCode);
    }

    [Fact]
    public async Task CdnFaultsCanBeScripted()
    {
        var (file, link) = await LinkAsync(4096);
        _mock.Fail(MockTorBox.CdnRoute, MockFault.DeadLink(), times: 1);
        using (var dead = await _http.GetAsync(link))
        {
            Assert.Equal(HttpStatusCode.OK, dead.StatusCode);
            Assert.Equal("This link has expired.", await dead.Content.ReadAsStringAsync());
        }

        _mock.Fail(MockTorBox.CdnRoute, MockFault.TooManyRequests(3), times: 1);
        using (var slow = await _http.GetAsync(link))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, slow.StatusCode);
            Assert.Equal(TimeSpan.FromSeconds(3), slow.Headers.RetryAfter!.Delta);
        }

        _mock.Fail(MockTorBox.CdnRoute, MockFault.Status5xx(503), times: 1);
        using (var down = await _http.GetAsync(link)) Assert.Equal(HttpStatusCode.ServiceUnavailable, down.StatusCode);

        using var good = await _http.GetAsync(link);
        Assert.Equal(file.Content, await good.Content.ReadAsByteArrayAsync());
        Assert.Equal(4, _mock.Calls(MockTorBox.CdnRoute));
    }

    [Fact]
    public void ResetLeavesNothingBehind()
    {
        OneFile();
        _mock.AddCached("abc");
        _mock.Fail("*", MockFault.Abort);
        _mock.Reset();
        Assert.Equal(0, _mock.TotalApiCalls);
        Assert.Empty(_mock.IssuedLinks);
    }

    [Fact]
    public async Task AWildcardFaultCoversEveryApiRoute()
    {
        _mock.Fail("*", MockFault.Status5xx(), times: 2);
        await Assert.ThrowsAsync<RemoteProviderException>(() => _client.GetAccountAsync());
        await Assert.ThrowsAsync<RemoteProviderException>(() => _client.ListAsync(RemoteItemKind.Torrent, 0, 1, true));
        Assert.Empty(await _client.ListAsync(RemoteItemKind.Torrent, 0, 1, true));
    }

    // ---- Proxy --------------------------------------------------------------------------------

    private static ProxySettings Manual(TestProxyServer proxy) =>
        new() { Mode = "manual", Address = proxy.Address, Username = "proxyuser", Password = "proxypass" };

    [Fact]
    public async Task ApiCallsTakeTheDownloadProxyAndFollowItsChanges()
    {
        await using var proxy = TestProxyServer.Start();
        using var engine = new HttpDownloadEngine(new EngineOptions { AllowLocalNetwork = true });
        using var http = new HttpClient(new ProxiedClientHandler(engine));
        var client = new TorBoxClient(http, () => _key, _mock.ApiBase);

        await client.GetAccountAsync();
        Assert.Equal(0, proxy.Authorized);

        engine.UpdateProxy(Manual(proxy));
        await client.GetAccountAsync();
        Assert.Equal(1, proxy.Authorized);
        Assert.Contains("/torbox/v1/api/user/me", proxy.LastTarget);
        Assert.Equal(2, _mock.Calls("GET user/me"));

        engine.UpdateProxy(new ProxySettings { Mode = "none" });
        await client.GetAccountAsync();
        Assert.Equal(1, proxy.Authorized);
        Assert.Equal(3, _mock.Calls("GET user/me"));
    }

    [Fact]
    public async Task ChangingTheProxyDoesNotDisturbARequestAlreadyRunning()
    {
        await using var proxy = TestProxyServer.Start();
        using var engine = new HttpDownloadEngine(new EngineOptions { AllowLocalNetwork = true });
        using var http = new HttpClient(new ProxiedClientHandler(engine));
        var client = new TorBoxClient(http, () => _key, _mock.ApiBase);
        _mock.Fail("GET user/me", MockFault.Delay(TimeSpan.FromMilliseconds(800)), times: 1);

        var running = client.GetAccountAsync();
        await Task.Delay(200);
        engine.UpdateProxy(Manual(proxy));
        await client.GetAccountAsync();
        Assert.Equal(1, proxy.Authorized);

        Assert.True((await running).Premium);
        Assert.Equal(1, proxy.Authorized);
    }

    [Fact]
    public async Task AProxyThatRefusesIsANetworkOrAuthFailureNotASilentBypass()
    {
        await using var proxy = TestProxyServer.Start();
        using var engine = new HttpDownloadEngine(new EngineOptions { AllowLocalNetwork = true });
        using var http = new HttpClient(new ProxiedClientHandler(engine));
        var client = new TorBoxClient(http, () => _key, _mock.ApiBase);
        engine.UpdateProxy(new ProxySettings { Mode = "manual", Address = proxy.Address, Username = "proxyuser", Password = "wrong" });

        await Assert.ThrowsAsync<RemoteProviderException>(() => client.GetAccountAsync());
        Assert.Equal(0, _mock.Calls("GET user/me"));
    }
}
