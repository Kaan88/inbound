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
    // SEC.L1: a site without a scheme goes over https only, to exactly that host.
    [InlineData("example.test", "https://example.test/a", true)]
    [InlineData("example.test", "http://example.test/a", false)]
    [InlineData("example.test", "https://sub.example.test/a", false)]
    [InlineData("example.test", "https://example.test.other.test/a", false)]
    [InlineData("EXAMPLE.test", "https://example.TEST/a", true)]
    [InlineData("example.test:8080", "https://example.test:8080/a", true)]
    [InlineData("example.test:8080", "http://example.test:8080/a", false)]
    [InlineData("example.test:8080", "https://example.test/a", false)]
    [InlineData("example.test:8080/files", "https://example.test:8080/files/a", true)]
    [InlineData("example.test:8080/files", "https://example.test:8080/filesx/a", false)]
    [InlineData("example.test/files?x=1", "https://example.test/files/a", true)]
    // A "://" in the path or query of a scheme-less site is not a scheme.
    [InlineData("example.test/get?u=https://x.test", "https://example.test/get", true)]
    [InlineData("example.test/get?u=https://x.test", "http://example.test/get", false)]
    [InlineData("example.test/proxy/http://x.test", "https://example.test/proxy/http://x.test/a", true)]
    [InlineData("example.test:8443/p?u=http://x.test", "https://example.test:8443/p/a", true)]
    [InlineData("localhost:8080", "https://localhost:8080/a", true)]
    [InlineData("localhost:8080", "http://localhost:8080/a", false)]
    [InlineData("[::1]:8080", "https://[::1]:8080/a", true)]
    [InlineData("[::1]", "https://[::1]:8443/a", true)]
    // An explicit *. takes in the subdomains and the host itself; without a scheme still https only.
    [InlineData("*.example.test", "https://sub.example.test/a", true)]
    [InlineData("*.example.test", "https://a.b.example.test/a", true)]
    [InlineData("*.example.test", "https://example.test/a", true)]
    [InlineData("*.example.test", "http://sub.example.test/a", false)]
    [InlineData("*.example.test", "https://badexample.test/a", false)]
    [InlineData("http://*.example.test", "http://sub.example.test/a", true)]
    [InlineData("https://*.example.test", "https://sub.example.test/a", true)]
    [InlineData("ex*.test", "https://example.test/a", false)]
    // http:// is the user's choice: http (or https) to that host only.
    [InlineData("http://example.test", "http://example.test/a", true)]
    [InlineData("http://example.test", "https://example.test/a", true)]
    [InlineData("http://example.test", "http://sub.example.test/a", false)]
    [InlineData("HTTP://example.test", "http://example.test/a", true)]
    [InlineData("http://example.test:8080/files", "http://example.test:8080/files/a", true)]
    [InlineData("http://example.test:8080", "http://example.test:9090/a", false)]
    [InlineData("https://example.test", "http://example.test/a", false)]
    [InlineData("https://example.test", "https://example.test/a", true)]
    [InlineData("https://example.test", "https://sub.example.test/a", false)]
    [InlineData("ftp://example.test", "http://example.test/a", false)]
    [InlineData("ftp://example.test", "https://example.test/a", false)]
    [InlineData("", "https://example.test/a", false)]
    public void SiteLoginMatching(string site, string url, bool matches)
        => Assert.Equal(matches, SiteLoginMatcher.Matches(site, new Uri(url)));

    /// <summary>CUT.1: FTP was cut in 0.7.2, so a saved login is never used for an ftp address.</summary>
    [Fact]
    public void NoLoginIsUsedForAnFtpAddress()
    {
        Assert.False(SiteLoginMatcher.Matches("example.test:2121", new Uri("ftp://example.test:2121/f")));
        Assert.False(SiteLoginMatcher.Matches("example.test", new Uri("ftp://example.test/f")));
    }
}
