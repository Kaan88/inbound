using DownloadManager.Core;
using DownloadManager.Desktop.WinUI;

namespace DownloadManager.Core.Tests;

/// <summary>UI.R38: what Add URL's one dialog makes of the text in its address box.</summary>
public sealed class AddUrlRulesTests
{
    private const string Magnet = "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&dn=Sample";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NothingTypedIsEmpty(string? text)
        => Assert.Equal(AddUrlKind.Empty, AddUrlRules.Read(text, withProvider: false).Kind);

    [Theory]
    [InlineData("https://example.com/file.zip")]
    [InlineData("  http://192.168.1.20/share/file.iso  ")]
    [InlineData("https://example.com/report[final-v2].zip")]
    [InlineData("https://example.com/some.torrent")]
    public void OneAddressIsAddedHere(string text)
    {
        var input = AddUrlRules.Read(text, withProvider: false);
        Assert.Equal(AddUrlKind.OneLink, input.Kind);
        Assert.Equal(text.Trim(), input.Address);
    }

    [Fact]
    public void OneLinkInASentenceIsThatLink()
    {
        var input = AddUrlRules.Read("Here it is: https://example.com/file.zip. Thanks!", withProvider: false);
        Assert.Equal(AddUrlKind.OneLink, input.Kind);
        Assert.Equal("https://example.com/file.zip", input.Address);
    }

    [Fact]
    public void ANumberedAddressGoesToAddLinksAsEachOfItsNumbers()
    {
        var input = AddUrlRules.Read("https://example.com/part[1-3].rar", withProvider: false);
        Assert.Equal(AddUrlKind.SeveralLinks, input.Kind);
        Assert.Equal(["https://example.com/part1.rar", "https://example.com/part2.rar", "https://example.com/part3.rar"], input.Links);
        Assert.StartsWith("3 links. Continue opens Add links", AddUrlRules.HandoffWords(input, torrentDialog: false));
    }

    [Fact]
    public void PastedTextWithSeveralLinksGoesToAddLinks()
    {
        var text = "https://example.com/a.zip\nhttps://example.com/b.zip and " + Magnet;
        var plain = AddUrlRules.Read(text, withProvider: false);
        Assert.Equal(AddUrlKind.SeveralLinks, plain.Kind);
        Assert.Equal(["https://example.com/a.zip", "https://example.com/b.zip"], plain.Links);

        // With a provider the magnet is one of them too.
        var withProvider = AddUrlRules.Read(text, withProvider: true);
        Assert.Equal(3, withProvider.Links.Count);
        Assert.Contains(Magnet, withProvider.Links);
    }

    [Theory]
    [InlineData(Magnet)]
    [InlineData(@"C:\Users\someone\Downloads\pack.torrent")]
    public void ATorrentGoesToTheTorrentDialog(string text)
    {
        var input = AddUrlRules.Read(text, withProvider: false);
        Assert.Equal(AddUrlKind.Torrent, input.Kind);
        Assert.Equal(text, input.Address);
        Assert.Equal("A torrent. Continue opens the torrent dialog.", AddUrlRules.HandoffWords(input, torrentDialog: true));
        Assert.Equal("A torrent. Continue opens the add dialog for it.", AddUrlRules.HandoffWords(input, torrentDialog: false));
    }

    [Theory]
    [InlineData("ftp://example.com/file.zip")]
    [InlineData("see ftps://example.com/file.zip")]
    public void AnFtpAddressSaysFtpIsNotSupported(string text)
        => Assert.Equal(AddUrlKind.Ftp, AddUrlRules.Read(text, withProvider: false).Kind);

    [Theory]
    [InlineData("example.com/file.zip")]
    [InlineData("just some words")]
    [InlineData("mailto:someone@example.com")]
    public void AnythingElseIsNotALink(string text)
        => Assert.Equal(AddUrlKind.NotALink, AddUrlRules.Read(text, withProvider: false).Kind);

    [Fact]
    public void TheWordsSayWhereAProvidersLinkGoes()
    {
        Assert.Equal("Rapid Site goes through TorBox. Continue opens the add dialog for it.", AddUrlRules.ProviderWords("hosterLink", " Rapid Site ", "TorBox"));
        Assert.Equal("This site goes through TorBox. Continue opens the add dialog for it.", AddUrlRules.ProviderWords("hosterLink", null, "TorBox"));
        Assert.Equal("A link to an item on your TorBox account. Continue opens the add dialog for it.", AddUrlRules.ProviderWords("folderLink", null, "TorBox"));
        Assert.Equal("Enter an absolute http or https URL.", AddUrlRules.NotALinkWords(torrents: false));
        Assert.Contains("magnet", AddUrlRules.NotALinkWords(torrents: true));
    }

    [Theory]
    [InlineData("https://example.com/a.zip\r\nhttps://example.com/b.zip\r\n")]
    [InlineData("https://example.com/a.zip\nhttps://example.com/b.zip")]
    [InlineData("https://example.com/a.zip \n\n\thttps://example.com/b.zip")]
    public void APastedListStaysSeveralLinksOnTheOneLineBox(string pasted)
    {
        // The box can't hold line breaks: the lines go in side by side, and both links are still found.
        var line = AddUrlRules.OneLine(pasted);
        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
        Assert.Contains("a.zip https://", line);
        var input = AddUrlRules.Read(line, withProvider: false);
        Assert.Equal(AddUrlKind.SeveralLinks, input.Kind);
        Assert.Equal(["https://example.com/a.zip", "https://example.com/b.zip"], input.Links);
    }

    [Fact]
    public void OneLinePastedIsLeftAsItIs()
        => Assert.Equal("https://example.com/a b.zip", AddUrlRules.OneLine("https://example.com/a b.zip"));

    [Fact]
    public void SeveralMagnetsOnOneLineAreSeveralLinks()
    {
        const string other = "magnet:?xt=urn:btih:89abcdef0123456789abcdef0123456789abcdef&dn=Other";
        var input = AddUrlRules.Read(AddUrlRules.OneLine(Magnet + "\n" + other), withProvider: true);
        Assert.Equal(AddUrlKind.SeveralLinks, input.Kind);
        Assert.Equal([Magnet, other], input.Links);
    }

    /// <summary>Review of 0.7.2, security-1: a user name and password typed for one site don't go to another.</summary>
    [Theory]
    [InlineData("https://files.corp.example/a", "https://files.corp.example/b?x=1", false)]
    [InlineData("https://files.corp.example/a", "https://FILES.corp.example:443/b", false)]
    [InlineData("https://files.corp.example/a", "http://files.corp.example/a", true)]
    [InlineData("https://files.corp.example/a", "https://files.corp.example:8443/a", true)]
    [InlineData("https://files.corp.example/a", "https://files.co", true)]
    [InlineData("https://files.corp.example/a", "http://mirror.other.example/b", true)]
    public void CredentialsStayWithTheSiteTheyWereTypedFor(string typedFor, string now, bool leaves)
        => Assert.Equal(leaves, AddUrlRules.LeavesSite(AddUrlRules.SiteOf(typedFor), now));

    [Fact]
    public void CredentialsTypedBeforeAnyAddressAreTiedToTheFirstOneTheyAreUsedFor()
    {
        Assert.False(AddUrlRules.LeavesSite(null, "https://example.com/a"));
        Assert.Null(AddUrlRules.SiteOf("not an address"));
    }

    [Fact]
    public void ItWaitsForTheAddressToStopChangingBeforeChecking()
        => Assert.InRange(AddUrlRules.CheckDelay, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(800));
}
