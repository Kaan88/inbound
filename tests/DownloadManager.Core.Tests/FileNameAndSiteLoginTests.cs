using DownloadManager.Core;
using DownloadManager.Core.Providers;

namespace DownloadManager.Core.Tests;

/// <summary>Audit 1, L17 and M9/L12: file-name edge cases and how a saved site login is matched.</summary>
public sealed class FileNameAndSiteLoginTests
{
    [Theory]
    [InlineData("a .", "a")]
    [InlineData("report. . ", "report")]
    [InlineData("name .zip", "name .zip")]
    [InlineData("CON.tar.gz", "_CON.tar.gz")]
    [InlineData("con .txt", "_con .txt")]
    [InlineData("NUL", "_NUL")]
    public void SanitizeLeavesNoNameWindowsWouldChange(string input, string expected)
        => Assert.Equal(expected, FileNameResolver.Sanitize(input));

    [Fact]
    public void ALongNameWithoutExtensionDoesNotEndInADotOrSpace()
    {
        var name = new string('a', 178) + " .b";
        var cleaned = FileNameResolver.Sanitize(name.Replace(".b", ". "));
        Assert.False(cleaned.EndsWith('.') || cleaned.EndsWith(' '), cleaned);
        Assert.True(cleaned.Length <= 180);
    }

    [Theory]
    [InlineData(".txt", "_CON.txt")]
    [InlineData("", "_CON")]
    public void ACutThatLeavesADeviceNameIsPrefixedToo(string extension, string expected)
    {
        // The full name is not reserved ("CON   ...x"), but the cut keeps "CON" and spaces, and trimming them leaves "CON".
        var cleaned = FileNameResolver.Sanitize("CON" + new string(' ', 250) + "x" + extension);
        Assert.Equal(expected, cleaned);
        Assert.False(FileNameResolver.IsReservedDeviceName(cleaned));
    }

    [Fact]
    public void RemotePathsAndLocalNamesShareTheDeviceNameRule()
    {
        Assert.Equal("_CON.tar.gz", RemotePaths.CleanSegment("CON.tar.gz"));
        Assert.True(FileNameResolver.IsReservedDeviceName("lpt1 .log"));
        Assert.False(FileNameResolver.IsReservedDeviceName("console.log"));
    }

    [Theory]
    [InlineData("example.test", "http://example.test/a", true)]
    [InlineData("example.test", "http://sub.example.test/a", true)]
    [InlineData("example.test:8080", "http://example.test:8080/a", true)]
    [InlineData("example.test:8080", "http://example.test/a", false)]
    [InlineData("example.test:8080/files", "http://example.test:8080/files/a", true)]
    [InlineData("example.test:8080/files", "http://example.test:8080/filesx/a", false)]
    [InlineData("localhost:8080", "http://localhost:8080/a", true)]
    [InlineData("http://example.test", "https://example.test/a", true)]
    [InlineData("https://example.test", "http://example.test/a", false)]
    [InlineData("https://example.test", "https://example.test/a", true)]
    [InlineData("ftp://example.test", "http://example.test/a", false)]
    public void SiteLoginMatching(string site, string url, bool matches)
        => Assert.Equal(matches, SiteLoginMatcher.Matches(site, new Uri(url), allowSubdomains: true));

    [Fact]
    public void FtpLoginsDoNotSpreadToSubdomains()
    {
        Assert.True(SiteLoginMatcher.Matches("example.test:2121", new Uri("ftp://example.test:2121/f"), allowSubdomains: false));
        Assert.False(SiteLoginMatcher.Matches("example.test", new Uri("ftp://sub.example.test/f"), allowSubdomains: false));
    }
}
