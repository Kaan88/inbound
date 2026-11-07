using System.Net;
using System.Text;
using DownloadManager.Core.Providers;
using DownloadManager.Providers.TorBox;

namespace DownloadManager.TorBox.Tests;

public sealed class TorBoxClientTests
{
    private const string Key = "test-key-00000000";

    /// <summary>Answers from a function and remembers what was asked. Field shapes copy real responses.</summary>
    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url, string? Auth, string? Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Calls.Add((request.Method, request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(), body));
            return answer(request);
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static (TorBoxClient Client, Stub Stub) Make(Func<HttpRequestMessage, HttpResponseMessage> answer, string? key = Key)
    {
        var stub = new Stub(answer);
        return (new TorBoxClient(new HttpClient(stub), () => key), stub);
    }

    private const string TorrentItem = """
        {"id":137,"hash":"d0545d7900ad4def92bbbb5fe8d8ee7109136424","name":"Package","size":100,"download_state":"cached",
         "progress":1,"download_speed":0,"eta":0,"expires_at":null,"download_present":true,"download_finished":true,"cached":true,
         "created_at":"2026-11-05T19:38:10Z","alternative_hashes":["aa"],
         "files":[
           {"id":2,"md5":null,"hash":"d054","name":"Package/sub/a.mkv","size":90,"infected":false,"mimetype":"video/x-matroska","short_name":"a.mkv"},
           {"id":0,"md5":"abc","hash":"d054","name":"Package/a.mkv","size":10,"infected":true,"short_name":"a.mkv"}]}
        """;

    [Fact]
    public async Task ReadyItemKeepsFilesPathsAndIds()
    {
        var (client, stub) = Make(_ => Json($$"""{"success":true,"error":null,"detail":"ok","data":[{{TorrentItem}}]}"""));
        var items = await client.ListAsync(RemoteItemKind.Torrent, 0, 50, fresh: true);

        var item = Assert.Single(items);
        Assert.Equal(RemoteStage.Ready, item.Stage);
        Assert.Equal("137", item.Ref.ItemId);
        Assert.Equal(RemoteItemKind.Torrent, item.Ref.Kind);
        Assert.Equal(["2", "0"], item.Files.Select(file => file.FileId));
        Assert.Equal("Package/sub/a.mkv", item.Files[0].Path);
        Assert.True(item.Files[1].Infected);
        Assert.Equal("abc", item.Files[1].Md5);
        Assert.Contains("bypass_cache=true", stub.Calls[0].Url);
        Assert.Equal($"Bearer {Key}", stub.Calls[0].Auth);
    }

    [Theory]
    [InlineData("downloading", false, false, null, RemoteStage.Processing)]
    [InlineData("metaDL", false, false, null, RemoteStage.MetadataPending)]
    [InlineData("checking", false, false, null, RemoteStage.MetadataPending)]
    [InlineData("checkingResumeData", false, false, null, RemoteStage.MetadataPending)]
    [InlineData("stalled (no seeds)", false, false, null, RemoteStage.Stalled)]
    [InlineData("completed", true, false, null, RemoteStage.Processing)]
    [InlineData("completed", false, true, null, RemoteStage.Processing)]
    [InlineData("failed", false, false, null, RemoteStage.Failed)]
    [InlineData("downloading", false, false, "Hoster is down", RemoteStage.Failed)]
    [InlineData("expired", true, false, null, RemoteStage.Failed)]
    [InlineData("uploading (no peers)", false, false, null, RemoteStage.Processing)]
    [InlineData("something new", false, false, null, RemoteStage.Unknown)]
    public void StageNeedsBothFlagsToBeReady(string state, bool finished, bool present, string? error, RemoteStage expected)
    {
        var json = $$"""
            {"id":1,"download_state":"{{state}}","download_finished":{{(finished ? "true" : "false")}},"download_present":{{(present ? "true" : "false")}},
             "error":{{(error is null ? "null" : $"\"{error}\"")}},"files":[{"id":0,"name":"a/b","size":1}]}
            """;
        using var document = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(expected, TorBoxMapping.Item(RemoteItemKind.WebDownload, document.RootElement).Stage);
    }

    /// <summary>Real items that don't know yet: 100 days and a size of -1, which the row read as "about 2400h 0m left".</summary>
    [Theory]
    [InlineData(8_640_000, -1, null, 0)]
    [InlineData(8_639_999, 5, 8_639_999L, 5)]
    [InlineData(1967, 32_330_558, 1967L, 32_330_558)]
    public void NoEstimateAndNoSizeAreNotFigures(long eta, long size, long? expectedEta, long expectedSize)
    {
        using var document = System.Text.Json.JsonDocument.Parse($$"""{"id":1,"download_state":"checking","eta":{{eta}},"size":{{size}},"files":[]}""");
        var item = TorBoxMapping.Item(RemoteItemKind.Torrent, document.RootElement);
        Assert.Equal(expectedEta, item.EtaSeconds);
        Assert.Equal(expectedSize, item.Size);
    }

    [Fact]
    public void FlagsWithoutFilesAreNotReady()
    {
        using var document = System.Text.Json.JsonDocument.Parse("""{"id":1,"download_state":"cached","download_finished":true,"download_present":true,"files":[]}""");
        Assert.Equal(RemoteStage.MetadataPending, TorBoxMapping.Item(RemoteItemKind.Torrent, document.RootElement).Stage);
    }

    [Fact]
    public async Task MissingItemIsNullEvenWithSuccessTrue()
    {
        var (client, _) = Make(_ => Json("""{"success":true,"error":"ITEM_NOT_FOUND","detail":"No torrents found for this user.","data":{}}""", HttpStatusCode.NotFound));
        Assert.Null(await client.GetAsync(new RemoteItemRef("torbox", RemoteItemKind.Torrent, "1"), fresh: true));
    }

    [Theory]
    [InlineData("""{"detail":"Not authenticated"}""", HttpStatusCode.Unauthorized, RemoteFailureKind.Authentication)]
    [InlineData("""{"success":false,"error":"BAD_TOKEN","detail":"Bad","data":null}""", HttpStatusCode.Forbidden, RemoteFailureKind.Authentication)]
    [InlineData("""{"success":false,"error":"ACTIVE_LIMIT","detail":"Too many active","data":null}""", HttpStatusCode.OK, RemoteFailureKind.AccountBusy)]
    [InlineData("""{"success":false,"error":"RATE_LIMIT_EXCEEDED","detail":"60 per 1 hour","data":null}""", HttpStatusCode.OK, RemoteFailureKind.RateLimited)]
    [InlineData("""{"success":false,"error":"UNKNOWN_ERROR","detail":"This link is password protected.","data":null}""", HttpStatusCode.OK, RemoteFailureKind.LinkPasswordRequired)]
    [InlineData("""{"success":false,"error":"UNSUPPORTED_SITE","detail":"No","data":null}""", HttpStatusCode.OK, RemoteFailureKind.Rejected)]
    [InlineData("""{"success":false,"error":"PLAN_RESTRICTED_FEATURE","detail":"Plan","data":null}""", HttpStatusCode.Forbidden, RemoteFailureKind.Rejected)]
    [InlineData("""{"success":false,"error":"MONTHLY_LIMIT","detail":"Plan","data":null}""", HttpStatusCode.Forbidden, RemoteFailureKind.AccountLimit)]
    [InlineData("""{"success":false,"error":"DATABASE_ERROR","detail":"Oops","data":null}""", HttpStatusCode.InternalServerError, RemoteFailureKind.ServiceError)]
    [InlineData("""{"success":false,"error":"LINK_OFFLINE","detail":"Gone","data":null}""", HttpStatusCode.BadRequest, RemoteFailureKind.Rejected)]
    [InlineData("""{"success":false,"error":"A_NEW_CODE","detail":"Who knows","data":null}""", HttpStatusCode.BadGateway, RemoteFailureKind.ServiceError)]
    [InlineData("""not json""", HttpStatusCode.BadGateway, RemoteFailureKind.ServiceError)]
    public async Task FailuresAreClassified(string body, HttpStatusCode status, RemoteFailureKind expected)
    {
        var (client, _) = Make(_ => Json(body, status));
        var failure = await Assert.ThrowsAsync<RemoteProviderException>(() => client.GetAccountAsync());
        Assert.Equal(expected, failure.Kind);
    }

    /// <summary>Doesn't say how long it is, so only reading it finds out.</summary>
    private sealed class UnsizedContent(int bytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
            => await stream.WriteAsync(new byte[bytes]);

        protected override Task<Stream> CreateContentReadStreamAsync()
            => Task.FromResult<Stream>(new MemoryStream(new byte[bytes]));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnAnswerTooLargeIsABadResponseAndNeverWholeInMemory(bool saysSoUpFront)
    {
        var stub = new Stub(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = saysSoUpFront ? new StringContent(new string(' ', 70_000)) : new UnsizedContent(200_000),
        });
        var client = new TorBoxClient(new HttpClient(stub), () => Key, mostResponseBytes: 64 * 1024);

        var failure = await Assert.ThrowsAsync<RemoteProviderException>(() => client.ListAsync(RemoteItemKind.Torrent, 0, 50, fresh: true));
        Assert.Equal(RemoteFailureKind.BadResponse, failure.Kind);
        Assert.Contains("too large", failure.Message);
    }

    [Fact]
    public async Task AnAnswerWithinTheLimitIsRead()
    {
        var stub = new Stub(_ => Json($$"""{"success":true,"error":null,"data":[{{TorrentItem}}]}"""));
        var client = new TorBoxClient(new HttpClient(stub), () => Key, mostResponseBytes: 64 * 1024);
        Assert.Single(await client.ListAsync(RemoteItemKind.Torrent, 0, 50, fresh: true));
    }

    private static HttpResponseMessage WithBudget(string remaining, string reset)
    {
        var response = Json("""{"success":true,"error":null,"data":[]}""");
        response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", remaining);
        response.Headers.TryAddWithoutValidation("X-RateLimit-Reset", reset);
        return response;
    }

    [Theory]
    [InlineData("4102444800")] // The year 2100.
    [InlineData("1e30")]       // Past anything a date can be.
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-5")]
    public async Task AnAbsurdBudgetResetIsIgnored(string reset)
    {
        var (client, _) = Make(_ => WithBudget("0", reset));
        await client.ListAsync(RemoteItemKind.Torrent, 0, 50, fresh: true);
        // Would wait 90 s if it counted, and the first call would have thrown on the worst of them.
        await client.ListAsync(RemoteItemKind.Torrent, 0, 50, fresh: true).WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task AWaitForBudgetEndsWhenTheProviderSaysThereIsSome()
    {
        var soon = DateTimeOffset.UtcNow.AddSeconds(1).ToUnixTimeMilliseconds() / 1000.0;
        var later = DateTimeOffset.UtcNow.AddSeconds(240).ToUnixTimeSeconds();
        var calls = 0;
        var (client, _) = Make(_ =>
        {
            calls++;
            return calls switch
            {
                1 => WithBudget("0", soon.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                2 => WithBudget("100", later.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                _ => WithBudget("100", later.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            };
        });

        await client.ListAsync(RemoteItemKind.Torrent, 0, 50, fresh: true);
        // Waits out the short window, and that answer says there is budget again, so the next doesn't wait at all.
        await client.ListAsync(RemoteItemKind.Torrent, 0, 50, fresh: true).WaitAsync(TimeSpan.FromSeconds(10));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await client.ListAsync(RemoteItemKind.Torrent, 0, 50, fresh: true).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"Took {clock.Elapsed}.");
    }

    [Fact]
    public async Task ACallWaitingForBudgetCanBeCancelled()
    {
        var later = DateTimeOffset.UtcNow.AddSeconds(240).ToUnixTimeSeconds();
        var (client, stub) = Make(_ => WithBudget("0", later.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        await client.ListAsync(RemoteItemKind.Torrent, 0, 50, fresh: true);

        using var stop = new CancellationTokenSource();
        var waiting = client.ListAsync(RemoteItemKind.Torrent, 0, 50, fresh: true, stop.Token);
        await Task.Delay(100);
        Assert.False(waiting.IsCompleted);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Single(stub.Calls);
    }

    [Fact]
    public async Task OneCallWaitingForBudgetDoesNotHoldUpAnother()
    {
        var soon = DateTimeOffset.UtcNow.AddSeconds(1).ToUnixTimeMilliseconds() / 1000.0;
        var (client, _) = Make(_ => WithBudget("0", soon.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        await client.ListAsync(RemoteItemKind.Torrent, 0, 50, fresh: true);

        // Queued behind a gate, these would each wait a turn. Side by side they all end with the one window.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await Task.WhenAll(
            client.ListAsync(RemoteItemKind.Torrent, 0, 50, fresh: true),
            client.ListAsync(RemoteItemKind.Torrent, 0, 50, fresh: true),
            client.ListAsync(RemoteItemKind.Torrent, 0, 50, fresh: true)).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"Took {clock.Elapsed}.");
    }

    [Theory]
    [InlineData("60 per 1 hour", 3600)]
    [InlineData("Too many requests", 60)]
    public async Task ATooManyRequestsWithNoRetryAfterGoesByItsWords(string detail, int seconds)
    {
        var (client, _) = Make(_ => Json($$"""{"detail":"{{detail}}"}""", HttpStatusCode.TooManyRequests));
        var failure = await Assert.ThrowsAsync<RemoteProviderException>(() => client.GetAccountAsync());
        Assert.Equal(RemoteFailureKind.RateLimited, failure.Kind);
        Assert.Equal(TimeSpan.FromSeconds(seconds), failure.RetryAfter);
    }

    [Theory]
    [InlineData("RATE_LIMIT_EXCEEDED", "60 per 1 hour", 3600)]
    [InlineData("SLOW_DOWN", "Too fast", 60)]
    public async Task ARateLimitSaidWithAnyOtherStatusCarriesAWaitToo(string code, string detail, int seconds)
    {
        var (client, _) = Make(_ => Json($$"""{"success":false,"error":"{{code}}","detail":"{{detail}}","data":null}"""));
        var failure = await Assert.ThrowsAsync<RemoteProviderException>(() => client.GetAccountAsync());
        Assert.Equal(RemoteFailureKind.RateLimited, failure.Kind);
        Assert.Equal(TimeSpan.FromSeconds(seconds), failure.RetryAfter);
    }

    [Fact]
    public void AHosterIsReadTheWayTheReferenceClientReadsIt()
    {
        using var nothing = System.Text.Json.JsonDocument.Parse("""{"name":"mega.nz","domains":null}""");
        var hoster = TorBoxMapping.Hoster(nothing.RootElement);
        Assert.Equal(["mega.nz"], hoster.Domains);
        Assert.True(hoster.Available);

        using var down = System.Text.Json.JsonDocument.Parse("""{"name":"x","domains":["x.invalid"],"status":false}""");
        Assert.False(TorBoxMapping.Hoster(down.RootElement).Available);
        using var up = System.Text.Json.JsonDocument.Parse("""{"name":"x","domains":["x.invalid"],"status":true}""");
        Assert.True(TorBoxMapping.Hoster(up.RootElement).Available);
    }

    [Fact]
    public async Task TorBoxsOwnWordsReachTheUser()
    {
        var (client, _) = Make(_ => Json("""{"success":false,"error":"ACTIVE_LIMIT","detail":"You have reached your limit.","data":null}""", HttpStatusCode.Forbidden));
        var failure = await Assert.ThrowsAsync<RemoteProviderException>(() => client.GetAccountAsync());
        Assert.Equal("You have reached your limit.", failure.Message);
        Assert.Equal("ACTIVE_LIMIT", failure.ProviderCode);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, RemoteFailureKind.Rejected)]
    [InlineData(HttpStatusCode.TooManyRequests, RemoteFailureKind.RateLimited)]
    public async Task WhatWasSubmittedDoesNotComeBackInTheMessage(HttpStatusCode status, RemoteFailureKind kind)
    {
        var (client, _) = Make(_ => Json("""{"success":false,"error":"LINK_OFFLINE","detail":"Dead: https://hoster.test/file/s3cret and magnet:?xt=urn:btih:s3cret","data":null}""", status));
        var failure = await Assert.ThrowsAsync<RemoteProviderException>(() => client.GetAccountAsync());
        Assert.Equal(kind, failure.Kind);
        Assert.DoesNotContain("s3cret", failure.Message);
        Assert.DoesNotContain("hoster.test", failure.Message);
        Assert.StartsWith("Dead:", failure.Message);
    }

    [Fact]
    public async Task TooManyRequestsCarriesTheWait()
    {
        var (client, _) = Make(_ =>
        {
            var response = Json("""{"detail":"slow"}""", HttpStatusCode.TooManyRequests);
            response.Headers.TryAddWithoutValidation("Retry-After", "37");
            return response;
        });
        var failure = await Assert.ThrowsAsync<RemoteProviderException>(() => client.GetAccountAsync());
        Assert.Equal(RemoteFailureKind.RateLimited, failure.Kind);
        Assert.Equal(TimeSpan.FromSeconds(37), failure.RetryAfter);
    }

    [Fact]
    public async Task RetryAfterOnASuccessDoesNotSlowTheNextCall()
    {
        // TorBox sends Retry-After on every response.
        var (client, stub) = Make(_ =>
        {
            var response = Json("""{"success":true,"error":null,"data":{"plan":1}}""");
            response.Headers.TryAddWithoutValidation("Retry-After", "60");
            response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "299");
            return response;
        });
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await client.GetAccountAsync();
        await client.GetAccountAsync();
        Assert.Equal(2, stub.Calls.Count);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task NoKeyFailsWithoutCallingAnyone()
    {
        var (client, stub) = Make(_ => Json("{}"), key: null);
        var failure = await Assert.ThrowsAsync<RemoteProviderException>(() => client.GetAccountAsync());
        Assert.Equal(RemoteFailureKind.Authentication, failure.Kind);
        Assert.Empty(stub.Calls);
    }

    [Fact]
    public async Task NetworkFailureDoesNotQuoteTheAddress()
    {
        var (client, _) = Make(request => throw new HttpRequestException($"Could not reach {request.RequestUri}"));
        var failure = await Assert.ThrowsAsync<RemoteProviderException>(() =>
            client.ResolveLinkAsync(new RemoteItemRef("torbox", RemoteItemKind.Torrent, "1"), "2"));
        Assert.Equal(RemoteFailureKind.Network, failure.Kind);
        Assert.DoesNotContain(Key, failure.ToString());
    }

    [Fact]
    public async Task SubmissionNetworkFailureSaysItMayHaveArrived()
    {
        var (client, _) = Make(_ => throw new HttpRequestException("reset"));
        var failure = await Assert.ThrowsAsync<RemoteProviderException>(() =>
            client.SubmitAsync(new RemoteSubmission { Kind = RemoteItemKind.WebDownload, Link = "https://mega.nz/file/x#y" }));
        Assert.Equal(RemoteFailureKind.Network, failure.Kind);
        Assert.Contains("may have received", failure.Message);
    }

    [Fact]
    public async Task DirectLinkIsHiddenFromToString()
    {
        var (client, stub) = Make(_ => Json("""{"success":true,"error":null,"data":"https://cdn.example/dld/abc?token=secret"}"""));
        var link = await client.ResolveLinkAsync(new RemoteItemRef("torbox", RemoteItemKind.WebDownload, "5"), "0");
        Assert.Equal("cdn.example", link.Url.Host);
        Assert.DoesNotContain("secret", link.ToString());
        Assert.Contains("web_id=5", stub.Calls[0].Url);
        Assert.Contains("file_id=0", stub.Calls[0].Url);
    }

    [Theory]
    [InlineData("""{"success":true,"error":null,"data":"http://cdn.example/plain"}""")]
    [InlineData("""{"success":true,"error":null,"data":"not a link"}""")]
    [InlineData("""{"success":true,"error":null,"data":null}""")]
    public async Task UnusableLinkIsRefused(string body)
    {
        var (client, _) = Make(_ => Json(body));
        var failure = await Assert.ThrowsAsync<RemoteProviderException>(() =>
            client.ResolveLinkAsync(new RemoteItemRef("torbox", RemoteItemKind.Torrent, "5"), "0"));
        Assert.Equal(RemoteFailureKind.BadResponse, failure.Kind);
    }

    [Fact]
    public async Task CacheCheckReturnsOnlyHits()
    {
        var (client, stub) = Make(_ => Json("""{"success":true,"error":null,"data":{"d984":{"name":"test_folder","size":19,"hash":"d984"}}}"""));
        var hits = await client.CheckCachedAsync(RemoteItemKind.Torrent, ["d984", "ffff"]);
        Assert.True(hits.ContainsKey("D984"));
        Assert.False(hits.ContainsKey("ffff"));
        Assert.Equal(19, hits["d984"].Size);
        Assert.Contains("hashes", stub.Calls[0].Body);
    }

    [Fact]
    public async Task EmptyCacheAnswerMeansNothingCached()
    {
        var (client, _) = Make(_ => Json("""{"success":true,"error":null,"data":{}}"""));
        Assert.Empty(await client.CheckCachedAsync(RemoteItemKind.WebDownload, ["abc"]));
    }

    [Fact]
    public void WebHashIsTheMd5OfTheExactLink()
    {
        var (client, _) = Make(_ => Json("{}"));
        // md5("https://example.com/f") computed independently.
        Assert.Equal(
            Convert.ToHexString(System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes("https://example.com/f"))).ToLowerInvariant(),
            client.PredictHash(RemoteItemKind.WebDownload, "https://example.com/f"));
    }

    [Theory]
    [InlineData("magnet:?xt=urn:btih:1111222233334444555566667777888899990000&tr=x", "1111222233334444555566667777888899990000")]
    [InlineData("magnet:?dn=a&xt=urn:btih:1111222233334444555566667777888899990000", "1111222233334444555566667777888899990000")]
    [InlineData("magnet:?xt=urn:btih:zzz", null)]
    [InlineData("magnet:?dn=only", null)]
    [InlineData("", null)]
    public void InfoHashFromMagnet(string magnet, string? expected) =>
        Assert.Equal(expected, TorBoxMapping.InfoHashFromMagnet(magnet));

    [Fact]
    public void Base32InfoHashBecomesHex() =>
        Assert.Equal(40, TorBoxMapping.InfoHashFromMagnet("magnet:?xt=urn:btih:3GCPM6XZTEXSC7I4QUA2WXZSTZGFVIF3")!.Length);

    [Fact]
    public async Task MagnetSubmissionUsesTheTorrentEndpoint()
    {
        var (client, stub) = Make(_ => Json("""{"success":true,"error":null,"detail":"ok","data":{"hash":"abc","torrent_id":77,"auth_id":"x"}}"""));
        var result = await client.SubmitAsync(new RemoteSubmission { Kind = RemoteItemKind.Torrent, Link = "magnet:?xt=urn:btih:abc", OnlyIfCached = true });
        Assert.Equal("77", result.Ref!.ItemId);
        Assert.Equal("abc", result.Hash);
        Assert.Contains("torrents/createtorrent", stub.Calls[0].Url);
        Assert.Contains("add_only_if_cached", stub.Calls[0].Body);
    }

    [Fact]
    public async Task WebSubmissionSendsTheLinkInTheBody()
    {
        var (client, stub) = Make(_ => Json("""{"success":true,"error":null,"detail":"ok","data":{"hash":"abc","webdownload_id":9,"auth_id":"x"}}"""));
        var result = await client.SubmitAsync(new RemoteSubmission { Kind = RemoteItemKind.WebDownload, Link = "https://mega.nz/file/x#key" });
        Assert.Equal("9", result.Ref!.ItemId);
        Assert.DoesNotContain("mega", stub.Calls[0].Url);
        Assert.Contains("mega", stub.Calls[0].Body);
    }

    [Fact]
    public async Task HostersKeepRoutingFacts()
    {
        var (client, _) = Make(_ => Json("""{"success":true,"error":null,"data":[{"name":"Mega","domains":["mega.nz"],"status":true,"regex":"^x$","daily_link_limit":100,"daily_link_used":3,"per_link_size_limit":5},{"name":"Down","status":false}]}"""));
        var hosters = await client.GetHostersAsync();
        Assert.True(hosters[0].Available);
        Assert.Equal(["mega.nz"], hosters[0].Domains);
        Assert.Equal(3, hosters[0].DailyLinksUsed);
        Assert.False(hosters[1].Available);
    }

    [Theory]
    [InlineData("Package/sub/a.mkv", true)]
    [InlineData("../evil.exe", false)]
    [InlineData("a/../../evil", false)]
    [InlineData("/etc/passwd", false)]
    [InlineData("C:/Windows/x", false)]
    [InlineData("\\\\server\\share\\x", false)]
    [InlineData("..\\evil", false)]
    [InlineData("   ", false)]
    public void RemotePathsRefuseEscapes(string path, bool allowed) =>
        Assert.Equal(allowed, RemotePaths.ToSafeRelativePath(path) is not null);

    [Fact]
    public void RemotePathsRepairIllegalNames()
    {
        Assert.Equal(Path.Combine("ab_b", "c_d.txt"), RemotePaths.ToSafeRelativePath("ab:b/c?d.txt"));
        Assert.Equal(Path.Combine("_CON.txt"), RemotePaths.ToSafeRelativePath("CON.txt"));
        Assert.Equal(Path.Combine("dir", "file"), RemotePaths.ToSafeRelativePath("dir./file. "));
    }
}
