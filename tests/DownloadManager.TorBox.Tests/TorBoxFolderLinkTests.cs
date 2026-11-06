using DownloadManager.Core.Providers;
using DownloadManager.Providers.TorBox;

namespace DownloadManager.TorBox.Tests;

/// <summary>The cases in the brief's list for Copy JDownloader Folder Link, with invented ids.</summary>
public sealed class TorBoxFolderLinkTests
{
    private static RemoteFolderReference Accepted(string text)
    {
        var parsed = TorBoxFolderLink.Parse(text);
        Assert.NotNull(parsed);
        Assert.Null(parsed.Problem);
        return parsed.Reference!;
    }

    private static string Refused(string text)
    {
        var parsed = TorBoxFolderLink.Parse(text);
        Assert.NotNull(parsed);
        Assert.Null(parsed.Reference);
        return parsed.Problem!;
    }

    [Fact]
    public void TheExactExampleShape()
    {
        var reference = Accepted("https://torbox.app/download?id=133998885&type=torrents&name=Test");
        Assert.Equal(RemoteItemKind.Torrent, reference.Kind);
        Assert.Equal("133998885", reference.ItemId);
    }

    [Theory]
    [InlineData("torrents", RemoteItemKind.Torrent)]
    [InlineData("web_downloads", RemoteItemKind.WebDownload)]
    [InlineData("usenet_downloads", RemoteItemKind.Usenet)]
    [InlineData("TORRENTS", RemoteItemKind.Torrent)]
    [InlineData("Web_Downloads", RemoteItemKind.WebDownload)]
    public void EachTypeInAnyCase(string type, RemoteItemKind kind)
        => Assert.Equal(kind, Accepted($"https://torbox.app/download?id=5&type={type}").Kind);

    [Theory]
    // Name missing, empty, encoded, or saying something else altogether.
    [InlineData("https://torbox.app/download?id=42&type=torrents")]
    [InlineData("https://torbox.app/download?id=42&type=torrents&name=")]
    [InlineData("https://torbox.app/download?id=42&type=torrents&name")]
    [InlineData("https://torbox.app/download?id=42&type=torrents&name=My%20Pack%20%26%20Co%2F..%2F..%2Fwindows")]
    [InlineData("https://torbox.app/download?id=42&type=torrents&name=a+b")]
    // Order.
    [InlineData("https://torbox.app/download?type=torrents&id=42")]
    [InlineData("https://torbox.app/download?name=x&type=torrents&id=42")]
    // Extra parameters are ignored, and never sent anywhere.
    [InlineData("https://torbox.app/download?id=42&type=torrents&name=x&utm_source=y&token=z")]
    [InlineData("https://torbox.app/download?foo&id=42&bar=&type=torrents")]
    // Case of everything that doesn't care about case.
    [InlineData("HTTPS://TORBOX.APP/DOWNLOAD?ID=42&TYPE=TORRENTS")]
    [InlineData("https://www.torbox.app/download?id=42&type=torrents")]
    // Whitespace around the link and around the values.
    [InlineData("   https://torbox.app/download?id=42&type=torrents\r\n")]
    [InlineData("\thttps://torbox.app/download?id=%2042&type=torrents%20")]
    // Shape variations a browser would treat the same.
    [InlineData("http://torbox.app/download?id=42&type=torrents")]
    [InlineData("https://torbox.app/download/?id=42&type=torrents")]
    [InlineData("https://torbox.app/download?id=42&type=torrents#files")]
    [InlineData("https://torbox.app/download?id=42&id=42&type=torrents")]
    [InlineData("https://torbox.app/download?id=0042&type=torrents")]
    public void TheseAllMeanItem42(string text)
    {
        var reference = Accepted(text);
        Assert.Equal("42", reference.ItemId);
        Assert.Equal(RemoteItemKind.Torrent, reference.Kind);
    }

    [Theory]
    [InlineData("https://torbox.app/download")]
    [InlineData("https://torbox.app/download?type=torrents")]
    [InlineData("https://torbox.app/download?id=&type=torrents")]
    [InlineData("https://torbox.app/download?id=abc&type=torrents")]
    [InlineData("https://torbox.app/download?id=-5&type=torrents")]
    [InlineData("https://torbox.app/download?id=1.5&type=torrents")]
    [InlineData("https://torbox.app/download?id=0&type=torrents")]
    [InlineData("https://torbox.app/download?id=1%2B1&type=torrents")]
    [InlineData("https://torbox.app/download?id=99999999999999999999999&type=torrents")]
    [InlineData("https://torbox.app/download?id=١٢٣&type=torrents")]
    [InlineData("https://torbox.app/download?id=1&id=2&type=torrents")]
    public void AnUnusableIdIsRefusedWithAReason(string text)
        => Assert.Contains("item id", Refused(text));

    [Theory]
    [InlineData("https://torbox.app/download?id=1")]
    [InlineData("https://torbox.app/download?id=1&type=")]
    [InlineData("https://torbox.app/download?id=1&type=movies")]
    [InlineData("https://torbox.app/download?id=1&type=torrent")]
    [InlineData("https://torbox.app/download?id=1&type=web_downloads%2Fx")]
    [InlineData("https://torbox.app/download?id=1&type=torrents&type=usenet_downloads")]
    public void AnUnusableTypeIsRefusedWithAReason(string text)
        => Assert.Contains("type", Refused(text));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a link")]
    [InlineData("torbox.app/download?id=1&type=torrents")]
    [InlineData("ftp://torbox.app/download?id=1&type=torrents")]
    [InlineData("https://torbox.app/")]
    [InlineData("https://torbox.app/downloads?id=1&type=torrents")]
    [InlineData("https://torbox.app/download/extra?id=1&type=torrents")]
    [InlineData("https://api.torbox.app/download?id=1&type=torrents")]
    // Only the real host counts, however much the address looks like it.
    [InlineData("https://torbox.app.example.test/download?id=1&type=torrents")]
    [InlineData("https://nottorbox.app/download?id=1&type=torrents")]
    [InlineData("https://example.test/torbox.app/download?id=1&type=torrents")]
    [InlineData("https://torbox.app@example.test/download?id=1&type=torrents")]
    [InlineData("https://user:secret@torbox.app/download?id=1&type=torrents")]
    [InlineData("https://torbox.app:8443/download?id=1&type=torrents")]
    public void OtherAddressesAreNotFolderLinks(string text)
        => Assert.Null(TorBoxFolderLink.Parse(text));

    [Fact]
    public void NoProblemTextQuotesWhatWasPasted()
    {
        var problem = Refused("https://torbox.app/download?id=secret-value&type=other-secret");
        Assert.DoesNotContain("secret", problem);
    }

    [Fact]
    public void TheClientExposesTheParser()
    {
        using var http = new HttpClient();
        IRemoteFolderLinkParser client = new TorBoxClient(http, () => "unused");
        Assert.Equal("7", client.ParseFolderLink("https://torbox.app/download?id=7&type=usenet_downloads")!.Reference!.ItemId);
    }
}
