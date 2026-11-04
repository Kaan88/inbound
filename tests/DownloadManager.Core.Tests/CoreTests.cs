using DownloadManager.Contracts;
using DownloadManager.Core;

namespace DownloadManager.Core.Tests;

public sealed class FileNameResolverTests
{
    [Theory]
    [InlineData("attachment; filename=\"report.pdf\"", "report.pdf")]
    [InlineData("attachment; filename=simple.zip", "simple.zip")]
    [InlineData("inline; filename*=UTF-8''na%C3%AFve%20file.txt", "naïve file.txt")]
    [InlineData("attachment; filename=\"../../evil.exe\"", "evil.exe")]
    [InlineData("attachment; filename=\"CON.txt\"", "_CON.txt")]
    public void ParsesAndSanitizes(string header, string expected)
    {
        Assert.Equal(expected, FileNameResolver.FromContentDisposition(header) is { } parsed
            ? FileNameResolver.Sanitize(parsed)
            : "MISSING");
    }

    [Fact]
    public void FallsBackToUrl()
    {
        var name = FileNameResolver.Resolve(null, new Uri("https://example.com/path/file%20name.bin?x=1"));
        Assert.Equal("file name.bin", name);
    }

    [Fact]
    public void RejectsTraversalAndInvalidCharacters()
    {
        Assert.Equal("file.bin", FileNameResolver.Sanitize("..\\..\\file.bin"));
        Assert.Equal("a_b_c_.txt", FileNameResolver.Sanitize("a:b*c?.txt"));
        Assert.Equal("download", FileNameResolver.Sanitize("   "));
    }

    [Fact]
    public void ResolveUniquePathNumbersConflicts()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "test-work", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var first = FileNameResolver.ResolveUniquePath(dir, "file.zip");
            File.WriteAllText(first, "x");
            var second = FileNameResolver.ResolveUniquePath(dir, "file.zip");
            Assert.EndsWith("file (1).zip", second);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

public sealed class SpeedEstimatorTests
{
    [Fact]
    public void EstimatesSpeedAndEta()
    {
        var estimator = new SpeedEstimator(TimeSpan.FromSeconds(10));
        var start = DateTimeOffset.UtcNow;
        estimator.Update(0, 1000, start);
        estimator.Update(500, 1000, start.AddSeconds(1));
        var (speed, eta) = estimator.Update(1000, 2000, start.AddSeconds(2));
        Assert.InRange(speed, 400, 600);
        Assert.NotNull(eta);
    }
}

public sealed class DownloadJobTests
{
    [Fact]
    public void CloneCopiesHeadersWithoutSharingDictionary()
    {
        var job = new DownloadJob { Url = "https://example.com/a" };
        job.RequestHeaders["Cookie"] = "a=b";
        var clone = job.Clone();
        clone.RequestHeaders["Cookie"] = "c=d";
        Assert.Equal("a=b", job.RequestHeaders["Cookie"]);
    }

    [Fact]
    public void ProgressFractionHandlesUnknownSize()
    {
        var job = new DownloadJob { ReceivedBytes = 10 };
        Assert.Null(job.ProgressFraction);
        job.TotalBytes = 100;
        Assert.Equal(0.1, job.ProgressFraction);
    }
}

public sealed class BatchUrlExpanderTests
{
    [Fact]
    public void ExpandsZeroPaddedNumericRange()
    {
        Assert.Equal(
            new[]
            {
                "https://example.com/file001.zip",
                "https://example.com/file002.zip",
                "https://example.com/file003.zip",
            },
            BatchUrlExpander.Expand("https://example.com/file[001-003].zip"));
    }

    [Fact]
    public void ExpandsDescendingLetters()
    {
        Assert.Equal(
            new[] { "https://example.com/c.txt", "https://example.com/b.txt", "https://example.com/a.txt" },
            BatchUrlExpander.Expand("https://example.com/[c-a].txt"));
    }

    [Fact]
    public void ExpandsCommaListAndCartesianProduct()
    {
        Assert.Equal(
            new[]
            {
                "https://example.com/1/a.bin",
                "https://example.com/1/b.bin",
                "https://example.com/3/a.bin",
                "https://example.com/3/b.bin",
            },
            BatchUrlExpander.Expand("https://example.com/[1,3]/[a-b].bin"));
    }

    [Fact]
    public void ReturnsSingleUrlWithoutRanges()
    {
        Assert.Equal(
            new[] { "https://example.com/file.zip" },
            BatchUrlExpander.Expand("https://example.com/file.zip"));
    }

    [Theory]
    [InlineData("https://example.com/[1-].zip")]
    [InlineData("https://example.com/[a-1].zip")]
    [InlineData("https://example.com/[1,].zip")]
    [InlineData("https://example.com/[1-2.zip")]
    public void RejectsInvalidPatterns(string pattern)
    {
        Assert.Throws<FormatException>(() => BatchUrlExpander.Expand(pattern));
    }

    [Fact]
    public void EnforcesExpansionLimit()
    {
        Assert.Throws<FormatException>(() => BatchUrlExpander.Expand("https://example.com/[1-100].zip", 10));
    }
}

public sealed class ClipboardUrlExtractorTests
{
    [Theory]
    [InlineData("https://example.com/file.zip", "https://example.com/file.zip")]
    [InlineData("Download this: https://example.com/a.bin now.", "https://example.com/a.bin")]
    [InlineData("(https://example.com/a?x=1)", "https://example.com/a?x=1")]
    [InlineData("one\nhttp://example.com/b\nthree", "http://example.com/b")]
    public void FindsFirstHttpUrl(string text, string expected)
    {
        Assert.Equal(expected, ClipboardUrlExtractor.FirstHttpUrl(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("no links here")]
    [InlineData("ftp://example.com/file.zip")]
    [InlineData("javascript:alert(1)")]
    public void RejectsNonHttpText(string text)
    {
        Assert.Null(ClipboardUrlExtractor.FirstHttpUrl(text));
    }
}

public sealed class ShutdownCommandTests
{
    [Theory]
    [InlineData("shutdown", false, "/s /t 30")]
    [InlineData("shutdown", true, "/s /t 30 /f")]
    [InlineData("restart", false, "/r /t 30")]
    [InlineData("restart", true, "/r /t 30 /f")]
    [InlineData("logoff", false, "/l /t 30")]
    [InlineData("bogus", false, "/s /t 30")]
    public void BuildsSafeWindowsArguments(string mode, bool force, string expected)
    {
        Assert.Equal(expected, ShutdownCommand.BuildArguments(mode, force));
    }
}
