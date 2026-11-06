using System.Diagnostics;
using System.Text;
using DownloadManager.Core.Providers;
using DownloadManager.Providers.TorBox;
using DownloadManager.Runtime.Remote;

namespace DownloadManager.Integration.Tests;

/// <summary>Classification alone: no network and no files. All links and hashes here are invented.</summary>
public sealed class RemoteInputClassifierTests : IDisposable
{
    private const string Hex = "0123456789abcdef0123456789abcdef01234567";
    private readonly HttpClient _http = new();

    public void Dispose() => _http.Dispose();

    private static RemoteHoster Hoster(string name, string pattern, bool available = true, string? note = null, params string[] domains)
        => new() { Name = name, Pattern = pattern, Available = available, Note = note, Domains = domains };

    private static RemoteHosterMatcher Matcher(string name, string pattern, bool available = true, string? note = null)
        => RemoteHosterMatcher.Create(Hoster(name, pattern, available, note))!;

    private RemoteClassifierProvider Provider(string? accountProblem = null, IReadOnlyList<RemoteHosterMatcher>? hosters = null, string? listProblem = null)
    {
        var client = new TorBoxClient(_http, () => "invented-key");
        return new RemoteClassifierProvider
        {
            Id = client.Id,
            DisplayName = client.DisplayName,
            Capabilities = client.Capabilities,
            AccountProblem = accountProblem,
            FolderLinks = client,
            Submitter = client,
            Hosters = hosters ?? [],
            HosterListProblem = listProblem,
        };
    }

    private RemoteInputClassification Classify(string? text, RemoteClassifierProvider? provider = null)
        => RemoteInputClassifier.Classify(text, [provider ?? Provider()]);

    [Fact]
    public void TheExampleFolderLink()
    {
        var result = Classify("https://torbox.app/download?id=133998885&type=torrents&name=Test");
        Assert.Equal(RemoteInputKind.FolderLink, result.Kind);
        Assert.Equal("133998885", result.ItemId);
        Assert.Equal(RemoteItemKind.Torrent, result.ItemKind);
        Assert.Equal("torbox", result.Provider);
        Assert.Null(result.Name);
    }

    [Fact]
    public void AFolderLinkWithATokenStaysAFolderLinkAndTheTokenIsDropped()
    {
        var result = Classify("https://torbox.app/download?id=5&type=web_downloads&token=invented");
        Assert.Equal(RemoteInputKind.FolderLink, result.Kind);
        Assert.DoesNotContain("invented", result.ToString());
    }

    [Fact]
    public void AMalformedFolderLinkGivesItsReason()
    {
        var result = Classify("https://torbox.app/download?id=abc&type=torrents");
        Assert.Equal(RemoteInputKind.Unsupported, result.Kind);
        Assert.Contains("item id", result.Reason);
    }

    [Theory]
    [InlineData("magnet:?xt=urn:btih:" + Hex)]
    [InlineData("MAGNET:?xt=urn:btih:" + Hex)]
    [InlineData("magnet:?dn=x&xt=urn:btih:" + Hex + "&tr=http%3A%2F%2Ft.invalid")]
    [InlineData("magnet:?xt=urn%3Abtih%3A" + Hex)]
    [InlineData("magnet:?xt=URN:BTIH:" + Hex)]
    [InlineData("  magnet:?xt=urn:btih:" + Hex + "  ")]
    public void AMagnetGivesItsHash(string text)
    {
        var result = Classify(text);
        Assert.Equal(RemoteInputKind.Magnet, result.Kind);
        Assert.Equal(Hex, result.Hash);
        Assert.Equal(RemoteItemKind.Torrent, result.ItemKind);
    }

    [Fact]
    public void AnUpperCaseHashIsLowered()
        => Assert.Equal(Hex, Classify("magnet:?xt=urn:btih:" + Hex.ToUpperInvariant()).Hash);

    [Fact]
    public void ABase32HashBecomesHex()
    {
        // The same twenty bytes, written the other way.
        Assert.Equal(Hex, Classify("magnet:?xt=urn:btih:" + ToBase32(Convert.FromHexString(Hex))).Hash);
    }

    private static string ToBase32(byte[] bytes)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var text = new StringBuilder();
        var buffer = 0;
        var bits = 0;
        foreach (var value in bytes)
        {
            buffer = (buffer << 8) | value;
            bits += 8;
            while (bits >= 5)
            {
                text.Append(alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }

            buffer &= (1 << bits) - 1;
        }

        return text.ToString();
    }

    [Theory]
    [InlineData("magnet:")]
    [InlineData("magnet:?dn=only-a-name")]
    [InlineData("magnet:?xt=urn:sha1:abc")]
    [InlineData("magnet:?xt=urn:btih:tooshort")]
    [InlineData("magnet:?xt=urn:btih:zzzz456789abcdef0123456789abcdef01234567")]
    public void AMagnetWithNoUsableHashIsRefused(string text)
    {
        var result = Classify(text);
        Assert.Equal(RemoteInputKind.Unsupported, result.Kind);
        Assert.Contains("hash", result.Reason);
    }

    [Fact]
    public void AVersion2OnlyMagnetSaysSo()
        => Assert.Contains("version 2", Classify("magnet:?xt=urn:btmh:1220" + new string('a', 64)).Reason);

    [Fact]
    public void AMagnetNameIsForDisplayAndHoldsNoAddress()
    {
        var result = Classify("magnet:?xt=urn:btih:" + Hex + "&dn=Nice%20Name%20http%3A%2F%2Fsecret.invalid%2Fx");
        Assert.Equal("Nice Name [link]", result.Name);
    }

    [Fact]
    public void AHosterLinkIsMatchedByTheProvidersPattern()
    {
        const string link = "https://mega.nz/file/INVENTED#invented-key";
        var result = Classify(link, Provider(hosters: [Matcher("Other", "^https://other\\.invalid/"), Matcher("Mega", @"^https?://(www\.)?mega\.nz/.+$", note: "100 links a day")]));
        Assert.Equal(RemoteInputKind.HosterLink, result.Kind);
        Assert.Equal("Mega", result.HosterName);
        Assert.True(result.HosterAvailable);
        Assert.Equal("100 links a day", result.HosterNote);
        Assert.Equal("mega.nz", result.DisplayHost);
        Assert.Equal(RemoteItemKind.WebDownload, result.ItemKind);
        // Md5 of exactly what was pasted, or the item couldn't be found again.
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(link))).ToLowerInvariant(), result.Hash);
        Assert.DoesNotContain("invented-key", result.ToString());
        Assert.DoesNotContain("INVENTED", result.ToString());
    }

    [Fact]
    public void AHosterThatIsDownIsStillAHosterLinkAndSaysSo()
    {
        var result = Classify("https://down.invalid/f/1", Provider(hosters: [Matcher("Down", @"^https://down\.invalid/", available: false, note: "Maintenance")]));
        Assert.Equal(RemoteInputKind.HosterLink, result.Kind);
        Assert.False(result.HosterAvailable);
        Assert.Equal("Maintenance", result.HosterNote);
    }

    [Fact]
    public void WithoutAKeyAHosterLinkStaysAnOrdinaryLink()
    {
        var result = Classify("https://mega.nz/file/x", Provider(accountProblem: "No key.", hosters: [Matcher("Mega", @"mega\.nz")]));
        Assert.Equal(RemoteInputKind.OrdinaryLink, result.Kind);
    }

    [Theory]
    [InlineData("magnet:?xt=urn:btih:" + Hex)]
    [InlineData("https://torbox.app/download?id=5&type=torrents")]
    [InlineData(@"C:\Users\someone\file.torrent")]
    public void WithoutAKeyWhatOnlyAProviderCanDoGivesThatReason(string text)
    {
        var result = Classify(text, Provider(accountProblem: "No key is set."));
        Assert.Equal(RemoteInputKind.Unsupported, result.Kind);
        Assert.Equal("No key is set.", result.Reason);
    }

    [Fact]
    public void WithNoProviderAtAllAMagnetSaysWhy()
    {
        var result = RemoteInputClassifier.Classify("magnet:?xt=urn:btih:" + Hex, []);
        Assert.Equal(RemoteInputKind.Unsupported, result.Kind);
        Assert.Contains("no provider", result.Reason);
    }

    [Fact]
    public void ABrokenPatternIsSkippedAndTheOthersStillWork()
    {
        Assert.Null(RemoteHosterMatcher.Create(Hoster("Broken", "((((")));
        Assert.Null(RemoteHosterMatcher.Create(Hoster("Huge", new string('a', 100_000))));
        Assert.Null(RemoteHosterMatcher.Create(Hoster("Nothing", "")));
    }

    [Fact]
    public void ASlowPatternIsSkippedThenLeftOut()
    {
        var slow = Matcher("Slow", @"^(a+)+$");
        var good = Matcher("Good", @"aaaaaaaaaa");
        var text = "https://x.invalid/" + new string('a', 40) + "!";
        var provider = Provider(hosters: [slow, good]);

        var clock = Stopwatch.StartNew();
        var result = Classify(text, provider);
        var first = clock.Elapsed;
        Assert.Equal(RemoteInputKind.HosterLink, result.Kind);
        Assert.Equal("Good", result.HosterName);

        clock.Restart();
        Classify(text, provider);
        Assert.True(clock.Elapsed < first, "The slow pattern should have been left out the second time.");
    }

    [Fact]
    public void WithNoPatternTheDomainsDecide()
    {
        var matcher = RemoteHosterMatcher.Create(Hoster("Plain", "", domains: "plain.invalid"))!;
        Assert.Equal(RemoteInputKind.HosterLink, Classify("https://www.plain.invalid/a", Provider(hosters: [matcher])).Kind);
        Assert.Equal(RemoteInputKind.OrdinaryLink, Classify("https://notplain.invalid/a", Provider(hosters: [matcher])).Kind);
    }

    [Theory]
    [InlineData("https://nexus.erth.tb-cdn.example/dld/abc?token=invented")]
    [InlineData("https://api.torbox.app/v1/api/torrents/requestdl?torrent_id=1&token=invented")]
    public void AProviderDirectLinkIsItsOwnKind(string text)
    {
        var result = Classify(text);
        Assert.Equal(RemoteInputKind.ProviderDirectLink, result.Kind);
        Assert.DoesNotContain("invented", result.ToString());
    }

    [Fact]
    public void AnAddressHoldingTheAccountKeyIsADirectLinkWhateverItsHost()
    {
        var provider = Provider() with { Secrets = new KeyMatcher("0a1b2c3d") };
        Assert.Equal(RemoteInputKind.ProviderDirectLink, Classify("https://elsewhere.invalid/x?k=0a1b2c3d", provider).Kind);
    }

    private sealed class KeyMatcher(string key) : IRemoteSecretMatcher
    {
        public bool Holds(string? text) => text?.Contains(key, StringComparison.Ordinal) == true;
        public string Mask(string text) => text.Replace(key, "***", StringComparison.Ordinal);
    }

    [Fact]
    public void AMaskedLinkIsRefusedWithAReason()
    {
        var result = Classify("https://nexus.erth.tb-cdn.example/dld/abc?token=***");
        Assert.Equal(RemoteInputKind.Unsupported, result.Kind);
        Assert.Contains("hidden", result.Reason);
    }

    [Theory]
    [InlineData("https://example.invalid/file.zip")]
    [InlineData("http://example.invalid/")]
    [InlineData("ftp://example.invalid/file.zip")]
    [InlineData("https://example.invalid/some.torrent")]
    public void EverythingElseOnTheWebIsAnOrdinaryLink(string text)
        => Assert.Equal(RemoteInputKind.OrdinaryLink, Classify(text).Kind);

    [Fact]
    public void WhenTheHosterListCouldNotBeFetchedTheLinkIsUndeterminedNotOrdinary()
    {
        var result = Classify("https://mega.nz/file/x", Provider(listProblem: "The list couldn't be fetched."));
        Assert.Equal(RemoteInputKind.Undetermined, result.Kind);
        Assert.Equal("The list couldn't be fetched.", result.Note);
        Assert.Contains("could not be reached", result.Reason);
        Assert.Equal("mega.nz", result.DisplayHost);
    }

    [Fact]
    public void ALocalTorrentFile()
        => Assert.Equal(RemoteInputKind.TorrentFile, Classify(@"C:\Users\someone\Pack.TORRENT").Kind);

    /// <summary>Opening one of these sends this Windows account's credentials to whatever host it names.</summary>
    [Theory]
    [InlineData(@"\\attacker.invalid\share\pack.torrent")]
    [InlineData(@"\\?\C:\pack.torrent")]
    [InlineData(@"\\.\C:\pack.torrent")]
    [InlineData("//attacker.invalid/share/pack.torrent")]
    [InlineData(@"\/attacker.invalid\share\pack.torrent")]
    public void ANetworkOrDevicePathIsRefusedWithoutBeingOpened(string text)
    {
        var result = Classify(text);
        Assert.Equal(RemoteInputKind.Unsupported, result.Kind);
        Assert.Contains("network path", result.Reason);
    }

    [Fact]
    public void AHosterIsOnlyAskedAboutAddressesOnItsOwnDomains()
    {
        var hoster = RemoteHosterMatcher.Create(Hoster("Files", @"files\.invalid/file/", domains: "files.invalid"))!;
        var provider = Provider(hosters: [hoster]);

        Assert.Equal(RemoteInputKind.HosterLink, Classify("https://files.invalid/file/x", provider).Kind);
        Assert.Equal(RemoteInputKind.HosterLink, Classify("https://www.files.invalid/file/x", provider).Kind);
        // Quoting one is not being one, and the whole of this link would have gone to the provider.
        Assert.Equal(RemoteInputKind.OrdinaryLink, Classify("https://intranet.invalid/go?u=https://files.invalid/file/x&sid=invented", provider).Kind);
        Assert.Equal(RemoteInputKind.OrdinaryLink, Classify("https://notfiles.invalid/file/x", provider).Kind);
        // A page on the site that isn't a file.
        Assert.Equal(RemoteInputKind.OrdinaryLink, Classify("https://files.invalid/help", provider).Kind);
    }

    [Fact]
    public void AnEntryThatWouldTakeEverythingOnlyTakesItsOwnDomains()
    {
        var greedy = RemoteHosterMatcher.Create(Hoster("Greedy", ".*", domains: "greedy.invalid"))!;
        Assert.Equal(RemoteInputKind.OrdinaryLink, Classify("https://example.invalid/file.zip", Provider(hosters: [greedy])).Kind);
    }

    [Fact]
    public void WithNoDomainsAPatternDoesntSeeTheQuery()
    {
        var hoster = Matcher("NoDomains", @"files\.invalid/file/");
        Assert.Equal(RemoteInputKind.HosterLink, Classify("https://files.invalid/file/x", Provider(hosters: [hoster])).Kind);
        Assert.Equal(RemoteInputKind.OrdinaryLink, Classify("https://intranet.invalid/go?u=files.invalid/file/x", Provider(hosters: [hoster])).Kind);
    }

    [Theory]
    [InlineData("((((")] // Won't compile.
    [InlineData(@"(?P<id>\w+)")] // Another language's way of writing it.
    public void APatternThatCantBeUsedLeavesTheDomainsToDecide(string pattern)
    {
        var matcher = RemoteHosterMatcher.Create(Hoster("Odd", pattern, domains: "odd.invalid"))!;
        Assert.Equal(RemoteInputKind.HosterLink, Classify("https://odd.invalid/a", Provider(hosters: [matcher])).Kind);
        Assert.Equal(RemoteInputKind.OrdinaryLink, Classify("https://other.invalid/a", Provider(hosters: [matcher])).Kind);
    }

    [Fact]
    public void APatternThatTimesOutLeavesTheDomainsToDecide()
    {
        var matcher = RemoteHosterMatcher.Create(Hoster("Slow", @"^(a+)+$", domains: "slow.invalid"))!;
        Assert.True(matcher.Matches(new string('a', 40) + "!", "slow.invalid"));
        Assert.Equal(RemoteInputKind.HosterLink, Classify("https://slow.invalid/" + new string('a', 40) + "!", Provider(hosters: [matcher])).Kind);
    }

    [Fact]
    public void AHosterThatCouldntBeCheckedIsSaidRatherThanCalledOrdinary()
    {
        var slow = Matcher("Slow", @"^(a+)+$");
        var text = new string('a', 40) + "!";
        Assert.False(slow.Matches(text, "x.invalid"));
        Assert.True(slow.CantBeChecked);

        var result = Classify("https://x.invalid/" + text, Provider(hosters: [slow]));
        Assert.Equal(RemoteInputKind.OrdinaryLink, result.Kind);
        Assert.Contains("couldn't check", result.Note);
    }

    [Fact]
    public void AListOfSlowPatternsCantHoldAPasteForLong()
    {
        var slow = Enumerable.Range(0, 200).Select(i => Matcher($"Slow{i}", @"(a+)+d$")).ToList();
        var clock = Stopwatch.StartNew();
        var result = Classify("https://x.invalid/" + new string('a', 40), Provider(hosters: slow));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(8), $"Took {clock.Elapsed}.");
        Assert.Contains("couldn't check", result.Note);
    }

    [Fact]
    public void AListThatCouldntBeRefreshedIsStillAGuide()
    {
        var known = RemoteHosterMatcher.Create(Hoster("Known", "", domains: "known.invalid"))!;
        var result = Classify("https://example.invalid/file.zip", Provider(hosters: [known], listProblem: "Couldn't refresh."));
        Assert.Equal(RemoteInputKind.OrdinaryLink, result.Kind);
        Assert.Equal("Couldn't refresh.", result.Note);
    }

    [Theory]
    [InlineData(null, "Nothing")]
    [InlineData("", "Nothing")]
    [InlineData("   \t ", "Nothing")]
    [InlineData("just some words", "isn't a link")]
    [InlineData(@"C:\Users\someone\archive.zip", "torrent")]
    [InlineData("mailto:someone@example.invalid", "http")]
    [InlineData("file:///C:/x.torrent", "http")]
    [InlineData("https://a.invalid/1\nhttps://b.invalid/2", "one link")]
    public void WhatCantBeUsedSaysWhy(string? text, string reason)
    {
        var result = Classify(text);
        Assert.Equal(RemoteInputKind.Unsupported, result.Kind);
        Assert.Contains(reason, result.Reason);
    }

    [Fact]
    public void AVeryLongInputIsRefusedBeforeAnyPatternSeesIt()
    {
        var result = Classify("https://x.invalid/" + new string('a', RemoteInputClassifier.LongestInput), Provider(hosters: [Matcher("Any", ".*")]));
        Assert.Equal(RemoteInputKind.Unsupported, result.Kind);
        Assert.Contains("too long", result.Reason);
    }

    [Fact]
    public void ATorrentFileIsParsedAndAMalformedOneGetsItsReason()
    {
        var info = "d6:lengthi1e4:name4:pack12:piece lengthi1e6:pieces20:aaaaaaaaaaaaaaaaaaaae";
        var result = RemoteInputClassifier.ClassifyTorrent(Encoding.Latin1.GetBytes($"d4:info{info}e"), [Provider()]);
        Assert.Equal(RemoteInputKind.TorrentFile, result.Kind);
        Assert.Equal("pack", result.Name);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.Latin1.GetBytes(info))).ToLowerInvariant(), result.Hash);

        var bad = RemoteInputClassifier.ClassifyTorrent("not a torrent"u8.ToArray(), [Provider()]);
        Assert.Equal(RemoteInputKind.Unsupported, bad.Kind);
        Assert.Contains("not a valid .torrent", bad.Reason);
    }
}
