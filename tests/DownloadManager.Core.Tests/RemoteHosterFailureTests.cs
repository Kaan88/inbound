using DownloadManager.Core.Providers;

namespace DownloadManager.Core.Tests;

/// <summary>
/// TB.F18: a refused web link is explained only from TorBox's own answer (its code and words) and its
/// own list of sites. The strings here are the ones the user reads.
/// </summary>
public sealed class RemoteHosterFailureTests
{
    private const string ScanWords =
        "There was an error scanning this link. It may not be online or it is inaccessible. Please try again later.";

    private static RemoteHoster Down(string name = "Rapidgator", string? note = null) => new()
    {
        Name = name,
        Domains = ["rapidgator.net"],
        Available = false,
        Note = note,
    };

    private static RemoteHoster Up(string name = "Mega") => new()
    {
        Name = name,
        Domains = ["mega.nz"],
        Available = true,
    };

    [Fact]
    public void ASiteTheListSaysIsDownIsNamedAsDown()
    {
        var text = RemoteHosterFailure.Text("TorBox", "rapidgator.net", "Rapidgator", Down(), true, "DOWNLOAD_SERVER_ERROR", ScanWords);

        Assert.Equal("TorBox lists Rapidgator as down right now. Try again later.", text);
    }

    [Fact]
    public void ADownSiteKeepsTheServicesOwnNote()
    {
        var text = RemoteHosterFailure.Text("TorBox", "rapidgator.net", "Rapidgator", Down(note: "Down for maintenance"), true, null, null);

        Assert.Equal("TorBox lists Rapidgator as down right now: Down for maintenance. Try again later.", text);
    }

    [Fact]
    public void AHostNoSiteInTheListTakesIsSaidToBeUnlisted()
    {
        var text = RemoteHosterFailure.Text("TorBox", "files.example", null, null, false, "DOWNLOAD_SERVER_ERROR", ScanWords);

        Assert.Equal("TorBox doesn't list files.example among the hosters it supports.", text);
    }

    [Theory]
    [InlineData(null)]     // never read
    [InlineData(true)]     // the list has it: it is up, so the list says nothing against it
    public void NothingIsSaidWhenTheListCannotSayTheHostIsUnlisted(bool? listed)
    {
        var text = RemoteHosterFailure.Text("TorBox", "files.example", null, listed == true ? Up("Files") : null, listed, "DOWNLOAD_SERVER_ERROR", ScanWords);

        Assert.Null(text);
    }

    [Fact]
    public void ASiteTheListSaysIsUpAndListedKeepsTheWordsItHad()
    {
        var text = RemoteHosterFailure.Text("TorBox", "mega.nz", "Mega", Up(), true, "DOWNLOAD_SERVER_ERROR", ScanWords);

        Assert.Null(text);
    }

    [Fact]
    public void ACodesOwnReasonIsSaidInPlainWordsWithItsDetail()
    {
        var text = RemoteHosterFailure.Text("TorBox", "mega.nz", "Mega", Up(), true, "UNSUPPORTED_SITE",
            "The site you are trying to download from is not supported.");

        Assert.Equal("TorBox doesn't support Mega: The site you are trying to download from is not supported.", text);
    }

    [Fact]
    public void ASiteTurnedOffIsSaidPlainlyEvenWithNoList()
    {
        var text = RemoteHosterFailure.Text("TorBox", "mega.nz", "Mega", null, null, "TEMPORARILY_DISABLED",
            "The site you are trying to download from is temporarily disabled. You may view the status of supported sites at https://torbox.app/hosters.");

        Assert.Equal(
            "TorBox has Mega turned off right now: The site you are trying to download from is temporarily disabled. " +
            "You may view the status of supported sites at [link]. Try again later.",
            text);
    }

    [Fact]
    public void ACodeWithNoWordsStillSaysItsReason()
    {
        var text = RemoteHosterFailure.Text("TorBox", "mega.nz", null, null, null, "UNSUPPORTED_SITE", "UNSUPPORTED_SITE");

        Assert.Equal("TorBox doesn't support mega.nz.", text);
    }

    [Fact]
    public void ACodeThisAppDoesNotNameIsLeftToTheWordsItHad()
    {
        Assert.Null(RemoteHosterFailure.Text("TorBox", "mega.nz", "Mega", Up(), true, "LINK_OFFLINE", "The file is offline."));
        Assert.Null(RemoteHosterFailure.Text("TorBox", "mega.nz", "Mega", Up(), true, null, ScanWords));
    }

    [Fact]
    public void AnAddressInTheServicesWordsIsHidden()
    {
        var text = RemoteHosterFailure.Text("TorBox", "mega.nz", "Mega", null, null, "UNSUPPORTED_SITE",
            "Refused https://mega.nz/file/secret-key#fragment for you.");

        Assert.Equal("TorBox doesn't support Mega: Refused [link] for you.", text);
    }

    [Theory]
    [InlineData("https://mega.nz/file/abc#key", "mega.nz")]
    [InlineData("https://user:pass@files.example:8080/a/b?token=1#f", "files.example")]
    [InlineData("  https://MEGA.nz  ", "mega.nz")]
    [InlineData("magnet:?xt=urn:btih:abc", null)]
    [InlineData("not a link", null)]
    [InlineData(null, null)]
    public void OnlyTheHostOfALinkCanBeSaid(string? link, string? host)
    {
        Assert.Equal(host, RemoteHosterFailure.HostOf(link));
    }

    [Fact]
    public void AnUnlistedHostIsNotNamedWhenThereIsNoHost()
    {
        Assert.Null(RemoteHosterFailure.Text("TorBox", null, null, null, false, null, null));
    }
}
