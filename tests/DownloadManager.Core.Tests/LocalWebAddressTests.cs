using DownloadManager.Core;

namespace DownloadManager.Core.Tests;

/// <summary>UI.B27: which addresses Add URL treats as ordinary downloads without asking a provider.</summary>
public sealed class LocalWebAddressTests
{
    [Theory]
    [InlineData("http://127.0.0.1:8080/file.bin")]
    [InlineData("http://127.5.6.7/file.bin")]
    [InlineData("https://localhost/file.bin")]
    [InlineData("http://LOCALHOST./file.bin")]
    [InlineData("http://dev.localhost/file.bin")]
    [InlineData("http://[::1]:9000/file.bin")]
    [InlineData("http://10.0.0.5/file.bin")]
    [InlineData("http://10.255.255.255/file.bin")]
    [InlineData("http://172.16.0.1/file.bin")]
    [InlineData("http://172.31.255.254/file.bin")]
    [InlineData("http://192.168.1.20/share/file.iso")]
    [InlineData("http://169.254.10.10/file.bin")]
    [InlineData("http://[fe80::1]/file.bin")]
    [InlineData("http://[fd12:3456:789a::1]/file.bin")]
    [InlineData("http://[fc00::1]/file.bin")]
    [InlineData("http://[::ffff:192.168.0.1]/file.bin")]
    [InlineData("http://nas/file.bin")]
    [InlineData("http://nas:5000/file.bin")]
    [InlineData("http://printer.local/file.bin")]
    [InlineData("http://router.lan/file.bin")]
    [InlineData("http://media.home.arpa/file.bin")]
    [InlineData("http://home.arpa/file.bin")]
    [InlineData("  http://192.168.0.2/a.zip  ")]
    public void AnAddressOnThisPcOrItsNetworkIsLocal(string address)
        => Assert.True(NetworkAddresses.IsLocalWebAddress(address));

    [Theory]
    [InlineData("https://example.com/file.bin")]
    [InlineData("https://files.example.org/a/b.zip")]
    [InlineData("http://8.8.8.8/file.bin")]
    [InlineData("http://172.15.0.1/file.bin")]
    [InlineData("http://172.32.0.1/file.bin")]
    [InlineData("http://192.169.0.1/file.bin")]
    [InlineData("http://11.0.0.1/file.bin")]
    [InlineData("http://[2001:db8::1]/file.bin")]
    [InlineData("https://mylan.com/file.bin")]
    [InlineData("https://plan.example/file.bin")]
    [InlineData("https://local.example.com/file.bin")]
    [InlineData("https://arpa.example.com/home.arpa")]
    public void APublicAddressIsNot(string address)
        => Assert.False(NetworkAddresses.IsLocalWebAddress(address));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nas")]
    [InlineData("magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567")]
    [InlineData("ftp://192.168.1.2/file.bin")]
    [InlineData("C:\\files\\a.torrent")]
    [InlineData("file:///C:/files/a.bin")]
    public void AnythingButAnHttpAddressIsNot(string? text)
        => Assert.False(NetworkAddresses.IsLocalWebAddress(text));
}
