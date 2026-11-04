using DownloadManager.Contracts;

namespace DownloadManager.Core.Tests;

public sealed class ProxyAddressTests
{
    [Theory]
    [InlineData(null, 0, "")]
    [InlineData("", 0, "")]
    [InlineData("proxy.example:8080", 0, "proxy.example:8080")]
    [InlineData("http://proxy.example:8080", 0, "proxy.example:8080")]
    [InlineData("https://proxy.example:8443/", 0, "proxy.example:8443")]
    [InlineData("socks4://127.0.0.1:1080", 1, "127.0.0.1:1080")]
    [InlineData("SOCKS4A://127.0.0.1:1080", 2, "127.0.0.1:1080")]
    [InlineData("socks5://[::1]:1080", 3, "[::1]:1080")]
    [InlineData("ftp://odd.example:21", 0, "odd.example:21")]
    public void SplitRecoversProtocolAndHostPort(string? stored, int protocol, string hostPort)
    {
        var (index, text) = ProxyAddress.Split(stored);
        Assert.Equal(protocol, index);
        Assert.Equal(hostPort, text);
    }

    [Theory]
    [InlineData(0, "proxy.example:8080", "proxy.example:8080")]
    [InlineData(1, "127.0.0.1:1080", "socks4://127.0.0.1:1080")]
    [InlineData(2, "  127.0.0.1:1080  ", "socks4a://127.0.0.1:1080")]
    [InlineData(3, "proxy.example:1080", "socks5://proxy.example:1080")]
    [InlineData(3, "socks4://proxy.example:1080", "socks5://proxy.example:1080")]
    [InlineData(3, "[::1]:1080", "socks5://[::1]:1080")]
    [InlineData(99, "proxy.example:8080", "proxy.example:8080")]
    public void ComposeBuildsTheStoredAddress(int protocol, string input, string expected)
        => Assert.Equal(expected, ProxyAddress.Compose(protocol, input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("proxy.example")]
    [InlineData("proxy.example:0")]
    [InlineData("proxy.example:70000")]
    [InlineData("proxy.example:abc")]
    [InlineData("proxy.example:80/path")]
    [InlineData("pro xy:80")]
    [InlineData("::1:1080")]
    public void ComposeRejectsMissingHostOrBadPort(string? input)
        => Assert.Null(ProxyAddress.Compose(3, input));

    [Fact]
    public void SplitThenComposeRoundTripsEverySupportedScheme()
    {
        foreach (var stored in new[]
                 {
                     "proxy.example:8080",
                     "socks4://127.0.0.1:1080",
                     "socks4a://localhost:1080",
                     "socks5://proxy.example:1080",
                 })
        {
            var (index, hostPort) = ProxyAddress.Split(stored);
            Assert.Equal(stored, ProxyAddress.Compose(index, hostPort));
        }
    }
}
