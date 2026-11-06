using DownloadManager.Contracts;
using DownloadManager.Core;
using DownloadManager.Core.Providers;

namespace DownloadManager.Core.Tests;

public sealed class ProviderLinksTests
{
    [Theory]
    [InlineData("https://store-1.weur.tb-cdn.st/dl/file.zip?token=abc123", true)]
    [InlineData("https://x.tb-cdn.io/file?a=1&token=abc123&b=2", true)]
    [InlineData("http://x.TB-CDN.example/file?TOKEN=abc123", true)]
    [InlineData("https://x.tb-cdn.st/file", false)]
    [InlineData("https://x.tb-cdn.st/file?token=", false)]
    [InlineData("https://x.tb-cdn.st/file?other=abc123", false)]
    [InlineData("https://x.tb-cdn/file?token=abc123", false)]
    [InlineData("https://tb-cdn-fake.st/file?token=abc123", false)]
    [InlineData("https://example.com/file?token=abc123", false)]
    [InlineData("ftp://x.tb-cdn.st/file?token=abc123", false)]
    [InlineData("not a url", false)]
    [InlineData(null, false)]
    [InlineData("https://api.torbox.app/v1/api/torrents/requestdl?token=abc123&torrent_id=1&file_id=2", true)]
    [InlineData("https://torbox.app/x?token=abc123", true)]
    [InlineData("https://API.TORBOX.APP/x?token=abc123", true)]
    [InlineData("https://x.tb-cdn.st/f?a=1;token=abc123", true)]
    [InlineData("https://api.torbox.app/v1/api/user/me", false)]
    [InlineData("https://torbox.app.evil.test/f?token=abc123", false)]
    [InlineData("https://nottorbox.app/f?token=abc123", false)]
    public void RecognisesProviderLinks(string? url, bool expected)
        => Assert.Equal(expected, ProviderLinks.IsProviderLink(url));

    [Fact]
    public void MaskingKeepsTheAddressAndRemovesOnlyTheSecret()
    {
        const string url = "https://x.tb-cdn.st/dl/file.zip?a=1&token=abc123&b=2#frag";
        var masked = ProviderLinks.MaskToken(url);
        Assert.Equal("https://x.tb-cdn.st/dl/file.zip?a=1&token=***&b=2", masked);
        Assert.DoesNotContain("abc123", masked);
    }

    [Fact]
    public void MaskingRemovesEverySecretAndAnyLogin()
    {
        var masked = ProviderLinks.MaskToken("https://user:pw9@x.tb-cdn.st/f?token=K1&sig=S2&key=K3&keep=1&u=https%3A%2F%2Fb.test%2F%3Ftoken%3DK4");
        Assert.Equal("https://x.tb-cdn.st/f?token=***&sig=***&key=***&keep=1&u=***", masked);
        Assert.True(ProviderLinks.IsMasked(masked));
    }

    [Fact]
    public void AMaskedLinkIsNotARealLink()
    {
        var masked = ProviderLinks.MaskToken("https://x.tb-cdn.st/file?token=abc123");
        Assert.False(ProviderLinks.IsProviderLink(masked));
        Assert.True(ProviderLinks.IsMasked(masked));
        Assert.False(ProviderLinks.IsMasked("https://x.tb-cdn.st/file?token=abc123"));
        Assert.False(ProviderLinks.IsMasked("https://example.com/file?token=***"));
    }

    [Fact]
    public void AllowsTheProvidersConnectionLimit() => Assert.Equal(4, ProviderLinks.MaxConnections);
}

public sealed class UrlRedactorTests
{
    [Theory]
    [InlineData("https://a.test/f?token=s3", "https://a.test/f?token=***")]
    [InlineData("https://a.test/f?KEY=s3&x=1", "https://a.test/f?KEY=***&x=1")]
    [InlineData("https://a.test/f?apikey=s3", "https://a.test/f?apikey=***")]
    [InlineData("https://a.test/f?api_key=s3", "https://a.test/f?api_key=***")]
    [InlineData("https://a.test/f?auth=s3", "https://a.test/f?auth=***")]
    [InlineData("https://a.test/f?signature=s3", "https://a.test/f?signature=***")]
    [InlineData("https://a.test/f?x=1&sig=s3", "https://a.test/f?x=1&sig=***")]
    [InlineData("https://a.test/f?%74oken=s3", "https://a.test/f?%74oken=***")]
    [InlineData("https://mega.test/file/id#decryption-key", "https://mega.test/file/id#***")]
    [InlineData("https://a.test/f?token=s3#frag", "https://a.test/f?token=***#***")]
    [InlineData("https://user:s3@a.test/f", "https://***@a.test/f")]
    [InlineData("https://a.test/f?name=keep&page=2", "https://a.test/f?name=keep&page=2")]
    [InlineData("https://a.test/at@sign/f", "https://a.test/at@sign/f")]
    public void RedactsAnAddress(string url, string expected)
        => Assert.Equal(expected, UrlRedactor.Redact(url));

    [Fact]
    public void RedactsAddressesInsideText()
    {
        var text = "System.Net.HttpRequestException: GET https://x.tb-cdn.st/f?token=s3 failed\n" +
                   "  and 'ftp://u:p4ss@h.test/a#b' too, at C:\\path\\file.cs:line 12";
        var redacted = UrlRedactor.RedactText(text);
        Assert.DoesNotContain("s3", redacted);
        Assert.DoesNotContain("p4ss", redacted);
        Assert.Contains("https://x.tb-cdn.st/f?token=***", redacted);
        Assert.Contains("C:\\path\\file.cs:line 12", redacted);
    }

    [Theory]
    [InlineData("https://go.test/?u=https://x.tb-cdn.st/f?token=s3cret")]
    [InlineData("https://go.test/?u=https%3A%2F%2Fx.tb-cdn.st%2Ff%3Ftoken%3Ds3cret")]
    [InlineData("https://go.test/?u=https%253A%252F%252Fx.tb-cdn.st%252Ff%253Ftoken%253Ds3cret")]
    [InlineData("https://go.test/?u=x.tb-cdn.st/f?token=s3cret")]
    [InlineData("https://a.test/?x=1,https://b.test/?token=s3cret")]
    [InlineData("https://a.test/f,https://user:s3cret@b.test/f")]
    [InlineData("https://a.test/f?access_token=s3cret")]
    [InlineData("https://a.test/f?refresh_token=s3cret")]
    [InlineData("https://a.test/f?password=s3cret")]
    [InlineData("https://a.test/f?pwd=s3cret")]
    [InlineData("https://a.test/f?client_secret=s3cret")]
    [InlineData("https://a.test/f?X-Amz-Signature=s3cret")]
    [InlineData("https://a.test/f?X-Goog-Credential=s3cret")]
    [InlineData("https://a.test/f?a=1;token=s3cret")]
    [InlineData("https://a.test/?token=ab'cds3cret")]
    [InlineData("GET /v1/api/torrents/requestdl?token=s3cret failed")]
    [InlineData("the request had token=s3cret in it")]
    [InlineData("magnet:?xt=urn:btih:s3cret&tr=udp://tracker.test:80")]
    [InlineData("could not add magnet:?xt=urn:btih:s3cret&dn=name now")]
    public void TextNeverKeepsTheSecret(string text)
        => Assert.DoesNotContain("s3cret", UrlRedactor.RedactText(text));

    [Theory]
    [InlineData("https://go.test/?u=https://x.tb-cdn.st/f?token=s3cret")]
    [InlineData("https://go.test/?u=https%3A%2F%2Fx.tb-cdn.st%2Ff%3Ftoken%3Ds3cret")]
    [InlineData("https://a.test/?x=1,https://b.test/?token=s3cret")]
    [InlineData("https://a.test/f?X-Amz-Signature=s3cret")]
    [InlineData("magnet:?xt=urn:btih:s3cret")]
    public void AddressNeverKeepsTheSecret(string url)
        => Assert.DoesNotContain("s3cret", UrlRedactor.Redact(url));

    [Fact]
    public void OrdinaryTextIsLeftAlone()
    {
        const string text = "Failed at C:\\path\\file.cs:line 12 with page=2 for monkey=3 at https://a.test/f?page=2&name=x";
        Assert.Equal(text, UrlRedactor.RedactText(text));
    }

    [Fact]
    public void HidingAddressesLeavesOnlyTheWords()
    {
        var hidden = UrlRedactor.HideAddresses("Offline: https://rapidgator.test/file/s3cret and magnet:?xt=urn:btih:s3cret, token=s3cret");
        Assert.DoesNotContain("s3cret", hidden);
        Assert.DoesNotContain("rapidgator", hidden);
        Assert.StartsWith("Offline: [link] and [link]", hidden);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void EmptyStaysEmpty(string? value)
    {
        Assert.Equal(string.Empty, UrlRedactor.Redact(value));
        Assert.Equal(string.Empty, UrlRedactor.RedactText(value));
    }
}

public sealed class RetryPolicyTests
{
    private static readonly EngineOptions Options = new()
    {
        InitialRetryDelay = TimeSpan.FromSeconds(1),
        RetryBackoffFactor = 2,
        MaxRetryDelay = TimeSpan.FromSeconds(10),
        MaxRetryAfterDelay = TimeSpan.FromSeconds(60),
    };

    [Fact]
    public void BackoffGrowsAndJitterOnlyShortensIt()
    {
        Assert.Equal(TimeSpan.FromSeconds(4), RetryPolicy.Delay(3, Options, jitterSample: 0));
        Assert.Equal(TimeSpan.FromSeconds(3.2), RetryPolicy.Delay(3, Options, jitterSample: 1));
        Assert.Equal(TimeSpan.FromSeconds(10), RetryPolicy.Delay(30, Options, jitterSample: 0));
        Assert.Equal(TimeSpan.FromSeconds(8), RetryPolicy.Delay(30, Options, jitterSample: 1));
    }

    [Fact]
    public void RandomJitterStaysInItsRangeAndVaries()
    {
        var delays = Enumerable.Range(0, 200).Select(_ => RetryPolicy.Delay(3, Options)).ToList();
        Assert.All(delays, delay => Assert.InRange(delay, TimeSpan.FromSeconds(3.2), TimeSpan.FromSeconds(4)));
        Assert.True(delays.Distinct().Count() > 1);
    }

    [Fact]
    public void RetryAfterWaitsAtLeastWhatWasAsked()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), RetryPolicy.Delay(1, Options, TimeSpan.FromSeconds(30), jitterSample: 0));
        Assert.Equal(TimeSpan.FromSeconds(36), RetryPolicy.Delay(1, Options, TimeSpan.FromSeconds(30), jitterSample: 1));
    }

    [Fact]
    public void RetryAfterIsBounded()
        => Assert.Equal(TimeSpan.FromSeconds(60), RetryPolicy.Delay(1, Options, TimeSpan.FromHours(5), jitterSample: 0));

    [Fact]
    public void ShortOrMissingRetryAfterFallsBackToBackoff()
    {
        Assert.Equal(TimeSpan.FromSeconds(4), RetryPolicy.Delay(3, Options, TimeSpan.FromSeconds(1), jitterSample: 0));
        Assert.Equal(TimeSpan.FromSeconds(4), RetryPolicy.Delay(3, Options, (TimeSpan?)null, jitterSample: 0));
        Assert.Equal(TimeSpan.FromSeconds(4), RetryPolicy.Delay(3, Options, TimeSpan.FromSeconds(-5), jitterSample: 0));
    }
}

public sealed class HashingTests
{
    [Fact]
    public async Task Md5MatchesAKnownValue()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "test-work", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "abc");
        try { Assert.Equal("900150983cd24fb0d6963f7d28e17f72", await Hashing.Md5FileAsync(path)); }
        finally { File.Delete(path); }
    }
}

public sealed class NativeBridgePolicyTests
{
    [Theory]
    [InlineData("handoff.request")]
    [InlineData("host.ping")]
    [InlineData("host.openUi")]
    [InlineData("host.pickFolder")]
    [InlineData("jobs.query")]
    public void ForwardsWhatTheExtensionUses(string type) => Assert.True(NativeBridgePolicy.AllowFromExtension(type));

    [Theory]
    [InlineData("job.add")]
    [InlineData("job.remove")]
    [InlineData("settings.update")]
    [InlineData("settings.get")]
    [InlineData("host.shutdown")]
    [InlineData("update.install")]
    [InlineData("remote.add")]
    [InlineData("archive.extract")]
    [InlineData("HOST.PING")]
    [InlineData("")]
    [InlineData(null)]
    public void RefusesEverythingElseFromTheExtension(string? type) => Assert.False(NativeBridgePolicy.AllowFromExtension(type));

    [Theory]
    [InlineData("remote.changed", false)]
    [InlineData("Remote.Changed", false)]
    [InlineData("archive.progress", false)]
    [InlineData("job.event", true)]
    [InlineData("handoff.ack", true)]
    [InlineData("jobs.snapshot", true)]
    [InlineData("host.folderPicked", true)]
    [InlineData("remotely.unrelated", true)]
    [InlineData("groups.changed", false)]
    public void KeepsProviderAndArchiveTrafficFromTheBrowser(string type, bool expected)
        => Assert.Equal(expected, NativeBridgePolicy.AllowToExtension(type));

    [Fact]
    public void ASnapshotReachesTheExtensionWithoutItsSettings()
    {
        var snapshot = new SnapshotDto
        {
            Jobs = [new JobDto { FileName = "kept.bin" }],
            Settings = new SettingsDto { RecentUrls = ["https://a.test/private-history"] },
        };
        var forwarded = NativeBridgePolicy.ToExtension(IpcMessage.Create(MessageTypes.JobsSnapshot, snapshot, "request-1"));

        Assert.NotNull(forwarded);
        Assert.Equal("request-1", forwarded!.ReplyTo);
        Assert.DoesNotContain("private-history", forwarded.Payload!.Value.GetRawText());
        Assert.False(forwarded.Payload.Value.TryGetProperty("settings", out _));
        Assert.Equal("kept.bin", Assert.Single(forwarded.GetPayload<SnapshotDto>().Jobs).FileName);
    }

    [Fact]
    public void EveryProviderMessageStaysOnTheHostsSideInBothDirections()
    {
        var types = typeof(MessageTypes).GetFields()
            .Select(field => (string)field.GetRawConstantValue()!)
            .Where(type => type.StartsWith("remote.", StringComparison.Ordinal) || type.StartsWith("archive.", StringComparison.Ordinal))
            .ToList();
        Assert.Contains(MessageTypes.RemoteAccountSetKey, types);
        Assert.Contains(MessageTypes.RemoteChanged, types);
        Assert.Contains(MessageTypes.RemoteAdd, types);
        Assert.All(types, type =>
        {
            Assert.False(NativeBridgePolicy.AllowFromExtension(type), type);
            Assert.False(NativeBridgePolicy.AllowToExtension(type), type);
            Assert.Null(NativeBridgePolicy.ToExtension(IpcMessage.Create(type, new { })));
        });
    }

    [Fact]
    public void OtherMessagesPassUnchangedOrNotAtAll()
    {
        var job = IpcMessage.Create(MessageTypes.JobEvent, new JobDto { FileName = "a.bin" });
        Assert.Same(job, NativeBridgePolicy.ToExtension(job));
        Assert.Null(NativeBridgePolicy.ToExtension(IpcMessage.Create("remote.changed")));
    }
}

public sealed class JobListFileRemoteTests
{
    [Fact]
    public void ExportLeavesOutProviderJobs()
    {
        var json = JobListFile.Serialize(
        [
            new JobDto { Url = "https://example.com/a.bin", FileName = "a.bin" },
            new JobDto { Url = "remote://torbox/torrents/1/2", FileName = "b.bin", IsRemote = true },
        ]);
        var entry = Assert.Single(JobListFile.Deserialize(json));
        Assert.Equal("a.bin", entry.FileName);
    }
}

public sealed class RemoteSourceTests
{
    [Fact]
    public void ReferenceHoldsOnlyStableIds()
        => Assert.Equal("remote://torbox/webdl/12%2F3/a%20b",
            new RemoteSource("torbox", "webdl", "12/3", "a b", "dir/file.bin", "job").ToReferenceUrl());

    [Fact]
    public void AResolutionDoesNotPrintItsLink()
        => Assert.DoesNotContain("secret", RemoteLinkResolution.Link("https://x.test/f?token=secret").ToString());
}
