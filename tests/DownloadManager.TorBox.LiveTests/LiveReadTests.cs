using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using DownloadManager.Core.Providers;
using DownloadManager.Runtime.Remote;

namespace DownloadManager.TorBox.LiveTests;

/// <summary>
/// The real client against the real API, reading only. A test that has nothing to look at on this
/// account says so in its output and passes, because xUnit 2 can't skip once it has started.
/// </summary>
[Trait("Category", LiveAccount.ReadTrait)]
public sealed class LiveReadTests(ITestOutputHelper output)
{
    private static readonly RemoteItemKind[] Kinds = [RemoteItemKind.Torrent, RemoteItemKind.WebDownload, RemoteItemKind.Usenet];
    private const string UnknownHash40 = "ffffffffffffffffffffffffffffffffffffffff";
    private const string UnknownHash32 = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";

    private void Say(string text) => output.WriteLine(LiveAccount.Scrub(text));

    private static string DataKind(JsonDocument body) => body.RootElement.GetProperty("data").ValueKind.ToString();

    // ---- Account and key ------------------------------------------------------------------------

    [LiveFact]
    public async Task TheAccountReads()
    {
        var account = await LiveAccount.Client().GetAccountAsync();
        Assert.False(string.IsNullOrWhiteSpace(account.DisplayPlan), "user/me gave no plan.");
        Assert.True(account.PremiumUntil is not null || !account.Premium, "Premium with no end date.");
        Say($"premium: {account.Premium}; cooldown date present: {account.CooldownUntil is not null}");
    }

    /// <summary>Why the key is sent as entered: the API takes every form of it, so nothing needs its dashes removed.</summary>
    [LiveFact]
    public async Task TheApiTakesTheKeyWithDashesWithoutThemAndInCapitals()
    {
        var key = LiveAccount.Key!;
        foreach (var form in new[] { key, key.Replace("-", string.Empty, StringComparison.Ordinal), key.ToUpperInvariant() })
        {
            var account = await LiveAccount.Client(form).GetAccountAsync();
            Assert.False(string.IsNullOrWhiteSpace(account.DisplayPlan), "One form of the key gave no account.");
        }
    }

    [LiveFact]
    public async Task AWrongKeyIsAnAuthenticationFailureWithAReadableCode()
    {
        var failure = await Assert.ThrowsAsync<RemoteProviderException>(() =>
            LiveAccount.Client("00000000-0000-4000-8000-000000000000").GetAccountAsync());
        Assert.Equal(RemoteFailureKind.Authentication, failure.Kind);
        Say($"code: {failure.ProviderCode}");
    }

    [LiveFact]
    public async Task NoKeyAtAllIsAnsweredWithNoEnvelope()
    {
        var (status, body) = await LiveAccount.RawAsync("user/me", bearer: null);
        using (body)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, status);
            Assert.False(body!.RootElement.TryGetProperty("success", out _));
        }
    }

    // ---- Lists ----------------------------------------------------------------------------------

    /// <summary>A word this table doesn't know reads as generic text, and an item in it can wait forever, so a new one should be heard about.</summary>
    [LiveTheory]
    [InlineData(RemoteItemKind.Torrent)]
    [InlineData(RemoteItemKind.WebDownload)]
    [InlineData(RemoteItemKind.Usenet)]
    public async Task EachFamilyReadsAndEveryStateWordIsKnown(RemoteItemKind kind)
    {
        var client = LiveAccount.Client();
        var items = await client.ListAsync(kind, 0, 1000, fresh: true);

        Assert.All(items, item => Assert.NotNull(client.NormalizeItemId(item.Ref.ItemId)));
        var unknown = items.Where(item => item.Stage == RemoteStage.Unknown).Select(item => item.RawState ?? "(none)").Distinct().ToList();
        Assert.True(unknown.Count == 0, "State words the table doesn't know: " + LiveAccount.Scrub(string.Join(", ", unknown)));
        var words = items.GroupBy(item => $"{item.RawState} -> {item.Stage}").Select(group => $"{group.Key}: {group.Count()}");
        Say($"{kind}: {items.Count} items. {string.Join("; ", words)}");
    }

    [LiveTheory]
    [InlineData("torrents", RemoteItemKind.Torrent)]
    [InlineData("webdl", RemoteItemKind.WebDownload)]
    [InlineData("usenet", RemoteItemKind.Usenet)]
    public async Task APagePastTheEndIsAnEmptyListAndNeverNull(string family, RemoteItemKind kind)
    {
        Assert.Empty(await LiveAccount.Client().ListAsync(kind, 100_000, 5, fresh: true));
        var (status, body) = await LiveAccount.RawAsync($"{family}/mylist?offset=100000&limit=5&bypass_cache=true");
        using (body)
        {
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.True(body!.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("error").ValueKind);
            Assert.Equal("Array", DataKind(body));
            Assert.Equal(0, body.RootElement.GetProperty("data").GetArrayLength());
        }
    }

    [LiveTheory]
    [InlineData("torrents", RemoteItemKind.Torrent)]
    [InlineData("webdl", RemoteItemKind.WebDownload)]
    [InlineData("usenet", RemoteItemKind.Usenet)]
    public async Task AnItemNotOnTheAccountReadsAsAbsent(string family, RemoteItemKind kind)
    {
        Assert.Null(await LiveAccount.Client().GetAsync(new RemoteItemRef("torbox", kind, "1"), fresh: true));
        var (status, body) = await LiveAccount.RawAsync($"{family}/mylist?id=1");
        using (body)
        {
            Assert.Equal(HttpStatusCode.NotFound, status);
            Assert.Equal("ITEM_NOT_FOUND", body!.RootElement.GetProperty("error").GetString());
            Assert.True(body.RootElement.GetProperty("success").GetBoolean(), "The 404 no longer says success:true.");
            Say($"{family}: data is {DataKind(body)}");
        }
    }

    [LiveTheory]
    [InlineData("type=torrent", RemoteItemKind.Torrent)]
    [InlineData("type=webdl", RemoteItemKind.WebDownload)]
    [InlineData("type=usenet", RemoteItemKind.Usenet)]
    public async Task TheQueueReadsAndNothingInItIsAnEmptyList(string query, RemoteItemKind kind)
    {
        var queued = await LiveAccount.Client().ListQueuedAsync(kind, 0, 100, fresh: true);
        var (status, body) = await LiveAccount.RawAsync($"queued/getqueued?{query}&offset=0&limit=100&bypass_cache=true");
        using (body)
        {
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal("Array", DataKind(body!));
            Assert.Equal(queued.Count, body!.RootElement.GetProperty("data").GetArrayLength());
            Say($"{query}: {queued.Count} queued");
        }
    }

    [LiveFact]
    public async Task TheOldQueueAddressesAreGone()
    {
        var (torrents, torrentsBody) = await LiveAccount.RawAsync("torrents/getqueued");
        using (torrentsBody)
        {
            Assert.Equal(HttpStatusCode.Gone, torrents);
            Assert.Equal("DEPRECATED", torrentsBody!.RootElement.GetProperty("error").GetString());
        }

        foreach (var path in new[] { "webdl/getqueued", "usenet/getqueued", "nothing/here" })
        {
            var (status, body) = await LiveAccount.RawAsync(path);
            using (body)
            {
                Assert.Equal(HttpStatusCode.NotFound, status);
                Assert.False(body!.RootElement.TryGetProperty("error", out _), "It now has an envelope.");
            }
        }
    }

    [LiveFact]
    public async Task ListsPageByOffsetAndLimit()
    {
        var client = LiveAccount.Client();
        var first = await client.ListAsync(RemoteItemKind.Torrent, 0, 3, fresh: true);
        var second = await client.ListAsync(RemoteItemKind.Torrent, 1, 3, fresh: true);
        if (first.Count < 3)
        {
            Say("Fewer than three torrents, so paging wasn't looked at.");
            return;
        }

        Assert.Equal(3, first.Count);
        Assert.True(first[1].Ref.ItemId == second[0].Ref.ItemId, "An offset of one didn't move the page by one.");
    }

    /// <summary>What a local job's path and identity are built from.</summary>
    [LiveFact]
    public async Task EveryFileHasAUsablePathAndFileIdsRunFromZeroInEachItem()
    {
        var items = await LiveAccount.EverythingAsync();
        var files = 0;
        var refused = 0;
        var badIds = 0;
        foreach (var item in items)
        {
            files += item.Files.Count;
            refused += item.Files.Count(file => RemotePaths.ToSafeRelativePath(file.Path) is null);
            var ids = item.Files.Select(file => long.Parse(file.FileId, System.Globalization.CultureInfo.InvariantCulture)).Order().ToList();
            if (!ids.SequenceEqual(Enumerable.Range(0, ids.Count).Select(id => (long)id))) badIds++;
        }

        Assert.True(refused == 0, $"{refused} of {files} real paths were refused.");
        Assert.True(badIds == 0, $"{badIds} of {items.Count} items don't number their files from 0.");
        var withError = items.Count(item => item.Stage == RemoteStage.Ready && !string.IsNullOrWhiteSpace(item.Error));
        Say($"{items.Count} items, {files} files; ready items with error set: {withError}");
    }

    [LiveFact]
    public async Task AnExpiredItemIsAFailureAndGivesNoLink()
    {
        var items = await LiveAccount.Torrents;
        var expired = items.FirstOrDefault(item => string.Equals(item.RawState, "expired", StringComparison.OrdinalIgnoreCase));
        if (expired is null)
        {
            Say("No expired item on this account, so this wasn't looked at.");
            return;
        }

        Assert.Equal(RemoteStage.Failed, expired.Stage);
        Assert.NotEmpty(expired.Files);
        var failure = await Assert.ThrowsAsync<RemoteProviderException>(() =>
            LiveAccount.Client().ResolveLinkAsync(expired.Ref, expired.Files[0].FileId));
        Assert.Equal(RemoteFailureKind.ServiceError, failure.Kind);
    }

    // ---- Hosters --------------------------------------------------------------------------------

    private static async Task<IReadOnlyList<RemoteHoster>> HostersAsync()
    {
        var hosters = await LiveAccount.Client().GetHostersAsync();
        Assert.NotEmpty(hosters);
        return hosters;
    }

    [LiveFact]
    public async Task EveryRealPatternCompilesAndMatchesItsOwnDomainsThroughTheDomainGate()
    {
        var hosters = await HostersAsync();
        var problems = new List<string>();
        foreach (var hoster in hosters)
        {
            Assert.False(string.IsNullOrEmpty(hoster.Pattern), "A hoster has no pattern.");
            try
            {
                _ = new Regex(hoster.Pattern!, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RemoteHosterMatcher.MatchTimeout);
            }
            catch (ArgumentException)
            {
                problems.Add($"{hoster.Name}: pattern doesn't compile");
                continue;
            }

            var matcher = RemoteHosterMatcher.Create(hoster);
            Assert.NotNull(matcher);
            foreach (var domain in hoster.Domains)
            {
                if (!matcher.Matches($"https://{domain}/some/file", domain)) problems.Add($"{hoster.Name}: {domain}");
                if (!matcher.Matches($"https://www.{domain}/some/file", $"www.{domain}")) problems.Add($"{hoster.Name}: www.{domain}");

                // A hoster can list both twitch.tv and m.twitch.tv, and then not-m.twitch.tv is rightly its own.
                var underAnother = hoster.Domains.Any(other => domain.EndsWith("." + other, StringComparison.OrdinalIgnoreCase));
                if (!underAnother && matcher.Matches($"https://not-{domain}/some/file", $"not-{domain}")) problems.Add($"{hoster.Name}: not-{domain} matched");
            }
        }

        Assert.True(problems.Count == 0, string.Join("; ", problems));
        Say($"{hosters.Count} hosters, {hosters.Count(hoster => hoster.Available)} up");
    }

    [LiveTheory]
    [InlineData("https://mega.nz/file/INVENTED0#InventedFragment", true)]
    [InlineData("https://MEGA.NZ/folder/INVENTED0#InventedFragment", true)]
    [InlineData("https://www.mega.nz/file/INVENTED0", true)]
    [InlineData("https://1fichier.com/?inventedid", true)]
    [InlineData("https://example.com/?u=https://mega.nz/file/INVENTED0", false)]
    [InlineData("https://notmega.nz/file/INVENTED0", false)]
    [InlineData("https://mega.nz.example.com/file/INVENTED0", false)]
    public async Task TheClassifierRoutesRealPatternsTheWayItShould(string link, bool toProvider)
    {
        var client = LiveAccount.Client();
        var hosters = await HostersAsync();
        var provider = new RemoteClassifierProvider
        {
            Id = client.Id,
            DisplayName = client.DisplayName,
            Capabilities = client.Capabilities,
            FolderLinks = client,
            Submitter = client,
            Hosters = hosters.Select(RemoteHosterMatcher.Create).OfType<RemoteHosterMatcher>().ToList(),
        };

        var result = RemoteInputClassifier.Classify(link, [provider]);

        Assert.Equal(toProvider ? RemoteInputKind.HosterLink : RemoteInputKind.OrdinaryLink, result.Kind);
        if (toProvider) Assert.Equal(client.PredictHash(RemoteItemKind.WebDownload, link), result.Hash);
    }

    // ---- Cache checks ---------------------------------------------------------------------------

    [LiveFact]
    public async Task ACachedTorrentIsFoundByItsHashInEitherCaseAndAnUnknownOneIsNot()
    {
        var client = LiveAccount.Client();
        var cached = (await LiveAccount.Torrents).FirstOrDefault(item => item.Cached && item.Hash is not null);
        if (cached is null)
        {
            Say("No cached torrent on this account, so this wasn't looked at.");
            return;
        }

        var hits = await client.CheckCachedAsync(RemoteItemKind.Torrent, [cached.Hash!, UnknownHash40]);
        Assert.True(hits.ContainsKey(cached.Hash!), "A cached item wasn't found by its hash.");
        Assert.False(hits.ContainsKey(UnknownHash40), "An invented hash was called cached.");
        Assert.Equal(cached.Size, hits[cached.Hash!].Size);
        Assert.True(!string.IsNullOrEmpty(hits[cached.Hash!].Name), "A hit came with no name.");
        Assert.True((await client.CheckCachedAsync(RemoteItemKind.Torrent, [cached.Hash!.ToUpperInvariant()])).ContainsKey(cached.Hash!));
        Assert.Empty(await client.CheckCachedAsync(RemoteItemKind.Torrent, [UnknownHash40]));
    }

    /// <summary>The hash a web download goes by is the md5 of its exact link, and that is what a cache check takes.</summary>
    [LiveFact]
    public async Task AWebDownloadsHashIsTheMd5OfItsLinkAndACacheCheckFindsIt()
    {
        var client = LiveAccount.Client();
        var (status, body) = await LiveAccount.RawAsync("webdl/mylist?offset=0&limit=1000");
        using (body)
        {
            Assert.Equal(HttpStatusCode.OK, status);
            var checkedItems = 0;
            foreach (var item in body!.RootElement.GetProperty("data").EnumerateArray())
            {
                var link = item.GetProperty("original_url").GetString();
                var hash = item.GetProperty("hash").GetString();
                Assert.True(hash == client.PredictHash(RemoteItemKind.WebDownload, link!), "An item's hash isn't the md5 of its link.");
                checkedItems++;
            }

            Say($"{checkedItems} web downloads checked");
        }

        var cached = (await LiveAccount.WebDownloads).FirstOrDefault(item => item.Cached && item.Hash is not null);
        if (cached is null)
        {
            Say("No cached web download on this account, so the cache check wasn't looked at.");
            return;
        }

        Assert.True((await client.CheckCachedAsync(RemoteItemKind.WebDownload, [cached.Hash!])).ContainsKey(cached.Hash!), "A cached web download wasn't found.");
        Assert.Empty(await client.CheckCachedAsync(RemoteItemKind.WebDownload, [UnknownHash32]));
        Assert.Empty(await client.CheckCachedAsync(RemoteItemKind.Usenet, [UnknownHash32]));
    }

    [LiveFact]
    public async Task ACacheCheckByAnAlternativeHashAnswersThatHash()
    {
        var client = LiveAccount.Client();
        var item = (await LiveAccount.WebDownloads).FirstOrDefault(candidate => candidate.Cached && candidate.AlternativeHashes.Count > 0);
        if (item is null)
        {
            Say("No cached web download with an alternative hash, so this wasn't looked at.");
            return;
        }

        var other = item.AlternativeHashes[0];
        var hits = await client.CheckCachedAsync(RemoteItemKind.WebDownload, [other]);
        Assert.True(hits.ContainsKey(other), "Asked by an alternative hash, the answer can't be found under it.");
    }

    // ---- Links ----------------------------------------------------------------------------------

    [LiveFact]
    public async Task ADirectLinkReadsARangeThatAgreesWithTheReportedSize()
    {
        var client = LiveAccount.Client();
        var files = await LiveAccount.FetchableFilesAsync();
        var target = files.FirstOrDefault(pair => pair.File.Size >= 16);
        Assert.NotNull(target.Item);

        var link = (await client.ResolveLinkAsync(target.Item.Ref, target.File.FileId)).Url;
        Assert.Equal(Uri.UriSchemeHttps, link.Scheme);
        Assert.True(ProviderLinks.IsProviderLink(link.AbsoluteUri), "A real link isn't recognised as a provider's, so it would be treated as an ordinary address.");

        using (var answer = await LiveAccount.CdnAsync(link, "bytes=0-15"))
        {
            Assert.Equal(HttpStatusCode.PartialContent, answer.StatusCode);
            Assert.Equal(target.File.Size, answer.Content.Headers.ContentRange?.Length);
            Assert.Equal(16, (await answer.Content.ReadAsByteArrayAsync()).Length);
            Assert.True(answer.Headers.AcceptRanges.Contains("bytes"), "No Accept-Ranges.");
            Assert.NotNull(answer.Content.Headers.LastModified);
        }

        // Past the end: a 206 with no range and no body, not a 416. Resuming a finished part file arrives here.
        using (var past = await LiveAccount.CdnAsync(link, $"bytes={target.File.Size}-"))
        {
            Assert.Equal(HttpStatusCode.PartialContent, past.StatusCode);
            Assert.Equal(false, past.Content.Headers.ContentRange?.HasRange);
            Assert.Equal(target.File.Size, past.Content.Headers.ContentRange?.Length);
            Assert.Empty(await past.Content.ReadAsByteArrayAsync());
        }
    }

    /// <summary>The API takes any form of the key, but the link has only one, and the CDN takes only that.</summary>
    [LiveFact]
    public async Task ALinkCarriesTheKeyWithItsDashesHoweverItWasAskedFor()
    {
        var key = LiveAccount.Key!;
        var dashless = key.Replace("-", string.Empty, StringComparison.Ordinal);
        var target = (await LiveAccount.FetchableFilesAsync()).First();

        var askedWithout = (await LiveAccount.Client(dashless).ResolveLinkAsync(target.Item.Ref, target.File.FileId)).Url;
        Assert.True(askedWithout.Query == "?token=" + key, "The link's query isn't only the key with its dashes.");

        using (var good = await LiveAccount.CdnAsync(askedWithout, "bytes=0-0"))
        {
            Assert.Equal(HttpStatusCode.PartialContent, good.StatusCode);
        }

        foreach (var query in new[] { "token=" + dashless, "token=00000000-0000-4000-8000-000000000000", string.Empty })
        {
            using var refused = await LiveAccount.CdnAsync(LiveAccount.WithQuery(askedWithout, query), "bytes=0-0");
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Empty(await refused.Content.ReadAsByteArrayAsync());
        }
    }

    /// <summary>
    /// The policy is ours, from TorBox's support pages. This shows four really do work at once on one
    /// link, each answering its own range. The CDN wasn't seen refusing more, so this doesn't claim it would.
    /// </summary>
    [LiveFact]
    public async Task FourConnectionsWorkAtOnceOnOneLink()
    {
        var client = LiveAccount.Client();
        var connections = client.TransferPolicy.MaxConnectionsPerFile;
        Assert.Equal(4, connections);
        const long spacing = 4 * 1024 * 1024;
        var target = (await LiveAccount.FetchableFilesAsync()).FirstOrDefault(pair => pair.File.Size >= spacing * connections);
        if (target.Item is null)
        {
            Say("No file large enough, so this wasn't looked at.");
            return;
        }

        var link = (await client.ResolveLinkAsync(target.Item.Ref, target.File.FileId)).Url;
        using var separate = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.None, MaxConnectionsPerServer = connections });
        separate.DefaultRequestVersion = HttpVersion.Version11;
        separate.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact;
        var open = new List<HttpResponseMessage>();
        try
        {
            for (var index = 0; index < connections; index++)
            {
                var from = index * spacing;
                var answer = await LiveAccount.CdnAsync(link, $"bytes={from}-", separate);
                open.Add(answer);
                Assert.Equal(HttpStatusCode.PartialContent, answer.StatusCode);
                Assert.Equal(from, answer.Content.Headers.ContentRange?.From);
            }

            foreach (var answer in open)
            {
                await using var stream = await answer.Content.ReadAsStreamAsync();
                var chunk = new byte[4096];
                await stream.ReadExactlyAsync(chunk);
            }
        }
        finally
        {
            foreach (var answer in open) answer.Dispose();
        }
    }

    /// <summary>A provider's md5 decides whether a download is kept, so it had better be the md5 of the file.</summary>
    [LiveFact]
    public async Task AReportedMd5IsTheMd5OfTheBytes()
    {
        var client = LiveAccount.Client();
        var files = (await LiveAccount.FetchableFilesAsync())
            .Where(pair => !string.IsNullOrWhiteSpace(pair.File.Md5) && pair.File.Size <= 1024 * 1024)
            .Take(3)
            .ToList();
        if (files.Count == 0)
        {
            Say("No small file with an md5, so this wasn't looked at.");
            return;
        }

        foreach (var (item, file) in files)
        {
            var link = (await client.ResolveLinkAsync(item.Ref, file.FileId)).Url;
            using var answer = await LiveAccount.CdnAsync(link, "bytes=0-");
            var bytes = await answer.Content.ReadAsByteArrayAsync();
            Assert.Equal(file.Size, bytes.Length);
            Assert.True(Convert.ToHexString(MD5.HashData(bytes)).Equals(file.Md5, StringComparison.OrdinalIgnoreCase), "The reported md5 isn't the md5 of the file.");
        }

        Say($"{files.Count} files compared");
    }

    /// <summary>Why the engine asks a provider's file for a range from zero. Without one this type can arrive rewritten.</summary>
    [LiveFact]
    public async Task AnHtmlFileReadAsARangeFromZeroIsExact()
    {
        var client = LiveAccount.Client();
        var target = (await LiveAccount.FetchableFilesAsync()).FirstOrDefault(pair =>
            pair.File.MimeType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true);
        if (target.Item is null)
        {
            Say("No HTML file on this account, so this wasn't looked at.");
            return;
        }

        var link = (await client.ResolveLinkAsync(target.Item.Ref, target.File.FileId)).Url;
        using var range = await LiveAccount.CdnAsync(link, "bytes=0-");
        var exact = await range.Content.ReadAsByteArrayAsync();
        Assert.Equal(HttpStatusCode.PartialContent, range.StatusCode);
        Assert.Equal(target.File.Size, exact.Length);
        if (!string.IsNullOrWhiteSpace(target.File.Md5))
        {
            Assert.True(Convert.ToHexString(MD5.HashData(exact)).Equals(target.File.Md5, StringComparison.OrdinalIgnoreCase), "Read as a range, the file isn't what was reported.");
        }

        using var whole = await LiveAccount.CdnAsync(link);
        var declared = whole.Content.Headers.ContentLength; // Read first: buffering the body fills it in.
        var sent = await whole.Content.ReadAsByteArrayAsync();
        Say($"reported {target.File.Size}; as a range {exact.Length}; sent whole {sent.Length}, status {(int)whole.StatusCode}, Content-Length {declared?.ToString() ?? "none"}");
    }

    [LiveTheory]
    [InlineData(RemoteItemKind.Torrent)]
    [InlineData(RemoteItemKind.WebDownload)]
    [InlineData(RemoteItemKind.Usenet)]
    public async Task ALinkForAnItemNotOnTheAccountIsAServiceErrorNotAMissingItem(RemoteItemKind kind)
    {
        var failure = await Assert.ThrowsAsync<RemoteProviderException>(() =>
            LiveAccount.Client().ResolveLinkAsync(new RemoteItemRef("torbox", kind, "1"), "0"));
        Assert.Equal(RemoteFailureKind.ServiceError, failure.Kind);
        Assert.Equal("DATABASE_ERROR", failure.ProviderCode);
    }

    [LiveFact]
    public async Task ALinkForAFileNotInTheItemIsAServiceErrorToo()
    {
        var target = (await LiveAccount.FetchableFilesAsync()).First();
        var failure = await Assert.ThrowsAsync<RemoteProviderException>(() =>
            LiveAccount.Client().ResolveLinkAsync(target.Item.Ref, "99999"));
        Assert.Equal(RemoteFailureKind.ServiceError, failure.Kind);
    }
}
