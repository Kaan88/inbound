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
