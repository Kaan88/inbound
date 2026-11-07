using DownloadManager.Contracts;
using DownloadManager.Core.Providers;
using DownloadManager.Desktop.WinUI;

namespace DownloadManager.Core.Tests;

/// <summary>TB.F12: one rule for every place a download's address is shown or can be copied.</summary>
public sealed class ShownAddressTests
{
    private const string Key = "0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0";
    private const string FileId = "11112222-3333-4444-5555-666677778888";
    private const string Site = "store-9.weur.tb-cdn.st";
    private static readonly string Real = $"https://{Site}/dld/{FileId}?token={Key}";

    /// <summary>What the Host keeps and sends for a pasted direct link.</summary>
    private static JobDto Pasted(string? failure = null) => new()
    {
        Url = ProviderLinks.MaskToken(Real),
        AddressHidden = true,
        PageUrl = ProviderLinks.MaskToken(Real),
        FileName = "notes.txt",
        Failure = failure is null ? null : new FailureDto { Message = failure },
    };

    [Theory]
    [InlineData("https://store-9.weur.tb-cdn.st/dld/11112222-3333-4444-5555-666677778888?token=0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0")]
    [InlineData("https://store-9.weur.tb-cdn.st/dld/11112222-3333-4444-5555-666677778888?token=***")]
    [InlineData("https://store-9.weur.tb-cdn.st/dld/11112222-3333-4444-5555-666677778888")]
    public void ADirectProviderLinkShowsItsSiteAloneMaskedOrNot(string url)
    {
        Assert.Equal(Site, ShownAddress.For(url));
        Assert.Equal("Site", ShownAddress.Label(url));
        Assert.True(ShownAddress.IsHidden(url));
    }

    [Fact]
    public void TheApisOwnLinkShowsItsSiteAlone()
        => Assert.Equal("api.torbox.app", ShownAddress.For($"https://api.torbox.app/v1/api/torrents/requestdl?token={Key}&torrent_id=1&file_id=2"));

    [Fact]
    public void AnAddressTheHostSaysHeldAKeyShowsItsSiteAloneWhereverTheKeyWas()
    {
        var url = $"https://example.com/get/{Key}/file.zip";
        Assert.Equal("example.com", ShownAddress.For(url, hiddenByHost: true));
        Assert.Equal(url, ShownAddress.For(url));
    }

    [Theory]
    [InlineData("https://example.com/file.zip?id=1")]
    [InlineData("https://cdn.example.com/a.bin?X-Amz-Signature=abc")]
    public void AnOrdinaryAddressIsShownAndCopiedAsItIs(string url)
    {
        Assert.Equal(url, ShownAddress.For(url));
        Assert.Equal("Address", ShownAddress.Label(url));
    }

    [Fact]
    public void AHiddenAddressWithNoReadableSiteSaysSo()
        => Assert.Equal(ShownAddress.NoSite, ShownAddress.For("not an address", hiddenByHost: true));

    // The surfaces: the progress and completion windows and Properties (Shown and Label), the list's second line
    // and tooltip (Detail), the Description column (Own) and the refresh box (RefreshStart).
    [Fact]
    public void NoWindowOrListShowsTheTokenOrTheAccountsFileOfAPastedLink()
    {
        var job = Pasted();
        foreach (var shown in new[] { JobAddress.Shown(job), JobAddress.Detail(job), JobAddress.Own(job) ?? string.Empty, JobAddress.RefreshStart(job) })
        {
            Assert.DoesNotContain(Key, shown);
            Assert.DoesNotContain(FileId, shown);
            Assert.DoesNotContain("token", shown);
        }

        Assert.Equal(Site, JobAddress.Shown(job));
        Assert.Equal("Site", JobAddress.Label(job));
        Assert.Equal(Site, JobAddress.Detail(job));
        Assert.Equal(Site, JobAddress.Own(job));
        Assert.Equal(string.Empty, JobAddress.RefreshStart(job));
    }

    [Fact]
    public void EvenALinkTheHostDidntFlagIsShownAsItsSite()
    {
        var job = Pasted();
        job.AddressHidden = false;
        Assert.Equal(Site, JobAddress.Shown(job));
        Assert.Equal(string.Empty, JobAddress.RefreshStart(job));
    }

    [Fact]
    public void AFailureStillSaysWhatWentWrongOnTheRow()
        => Assert.Equal("The server said no.", JobAddress.Detail(Pasted("The server said no.")));

    [Fact]
    public void AProvidersFileHasNoAddressToShow()
    {
        var job = new JobDto { Url = "remote://torbox/torrents/1/2", IsRemote = true };
        Assert.False(JobAddress.HasAddress(job));
        Assert.Equal(string.Empty, JobAddress.Shown(job));
    }

    [Fact]
    public void AnOrdinaryJobKeepsItsAddressEverywhere()
    {
        var job = new JobDto { Url = "https://example.com/a.zip", PageUrl = "https://example.com/page" };
        Assert.Equal("https://example.com/a.zip", JobAddress.Shown(job));
        Assert.Equal("Address", JobAddress.Label(job));
        Assert.Equal("https://example.com/a.zip", JobAddress.RefreshStart(job));
        Assert.Equal("https://example.com/page", JobAddress.Own(job));
        Assert.Equal("My words", JobAddress.Own(new JobDto { Url = job.Url, Description = "My words", PageUrl = job.PageUrl }));
    }

    [Fact]
    public void AnExportLeavesOutAHiddenAddress()
    {
        var exported = JobListFile.Serialize([Pasted(), new JobDto { Url = "https://example.com/a.zip", FileName = "a.zip" }]);
        Assert.DoesNotContain(FileId, exported);
        Assert.DoesNotContain("tb-cdn", exported);
        Assert.Contains("https://example.com/a.zip", exported);
    }
}
