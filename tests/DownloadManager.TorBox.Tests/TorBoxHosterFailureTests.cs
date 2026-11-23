using System.Text.Json;
using DownloadManager.Core.Providers;
using DownloadManager.Providers.TorBox;
using TestServer;

namespace DownloadManager.TorBox.Tests;

/// <summary>
/// TB.F18: the plain reason is built from TorBox's own fields. The codes and words used here are the
/// ones captured from the service (see <see cref="MockFault"/>) and read through <see cref="TorBoxMapping"/>.
/// </summary>
public sealed class TorBoxHosterFailureTests
{
    private static RemoteHoster Hoster(string json)
    {
        using var document = JsonDocument.Parse(json);
        return TorBoxMapping.Hoster(document.RootElement);
    }

    [Fact]
    public void TheListsDownEntryExplainsADeadLink()
    {
        // The shape webdl/hosters answers with, including the note the service puts on a site it turned off.
        var hoster = Hoster("""{"name":"Rapidgator","domains":["rapidgator.net","rg.to"],"status":false,"note":"Down for maintenance"}""");

        var text = RemoteHosterFailure.Text("TorBox", "rapidgator.net", "Rapidgator", hoster, true, MockFault.DeadWebLink.Code, MockFault.DeadWebLink.Detail);

        Assert.Equal("TorBox lists Rapidgator as down right now: Down for maintenance. Try again later.", text);
    }

    [Fact]
    public void ACapturedDeadLinkRefusalSaysTheServiceRefusedIt()
    {
        // Captured: 500 DOWNLOAD_SERVER_ERROR with these words, and nothing is created.
        Assert.Equal("DOWNLOAD_SERVER_ERROR", MockFault.DeadWebLink.Code);
        var text = RemoteHosterFailure.Text("TorBox", "hoster.example", null, null, false, MockFault.DeadWebLink.Code, MockFault.DeadWebLink.Detail);

        Assert.Equal("TorBox doesn't list hoster.example among the hosters it supports.", text);
    }

    [Theory]
    [InlineData("UNSUPPORTED_SITE", "Mega")]
    [InlineData("TEMPORARILY_DISABLED", "Mega")]
    public void ACapturedRefusalThatNamesItsReasonIsSaidPlainly(string which, string site)
    {
        var fault = which == "UNSUPPORTED_SITE" ? MockFault.SiteNotSupported : MockFault.SiteTurnedOff;
        var text = RemoteHosterFailure.Text("TorBox", "mega.nz", site, Hoster("""{"name":"Mega","domains":["mega.nz"],"status":true}"""), true, fault.Code, fault.Detail);

        Assert.NotNull(text);
        Assert.StartsWith("TorBox ", text);
        Assert.Contains(site, text);
        Assert.Contains(fault.Detail!.Split(". ")[0], text);
    }
}
