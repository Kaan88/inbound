namespace DownloadManager.Core.Tests;

/// <summary>CUT.1: FTP was cut in 0.7.2; these are the rules every add path and the Host use to refuse it.</summary>
public sealed class FtpAddressesTests
{
    [Theory]
    [InlineData("ftp://example.test/a.zip", true)]
    [InlineData("FTP://example.test/a.zip", true)]
    [InlineData(" ftps://example.test/a.zip ", true)]
    [InlineData("https://example.test/ftp/a.zip", false)]
    [InlineData("http://ftp.example.test/a.zip", false)]
    [InlineData("magnet:?xt=urn:btih:abc", false)]
    [InlineData("not an address", false)]
    [InlineData(null, false)]
    public void TellsAnFtpAddress(string? address, bool ftp)
        => Assert.Equal(ftp, FtpAddresses.IsFtp(address));

    /// <summary>Review 1 L11: the password an older version kept in an FTP address goes; the user name stays.</summary>
    [Theory]
    [InlineData("ftp://user:invented-password@example.test:2121/pub/a.bin", "ftp://user@example.test:2121/pub/a.bin")]
    [InlineData("ftp://user@example.test/pub/a.bin", "ftp://user@example.test/pub/a.bin")]
    [InlineData("ftp://example.test/pub/a.bin", "ftp://example.test/pub/a.bin")]
    [InlineData("https://user:invented-password@example.test/a.bin", "https://user:invented-password@example.test/a.bin")]
    [InlineData(null, null)]
    public void DropsOnlyAnFtpPassword(string? address, string? expected)
        => Assert.Equal(expected, FtpAddresses.WithoutPassword(address));
}
