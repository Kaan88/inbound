using System.Diagnostics;
using DownloadManager.Contracts;

namespace DownloadManager.Core.Tests;

/// <summary>Audit 1, C-2 to C-4: batch patterns stop at the limit, start modes are a closed set, no dead status field.</summary>
public sealed class ContractsAuditTests
{
    [Fact]
    public void AHugeRangeStopsAtTheLimitInsteadOfExpandingFirst()
    {
        var stopwatch = Stopwatch.StartNew();
        var before = GC.GetTotalAllocatedBytes(precise: false);

        var error = Assert.Throws<FormatException>(() => BatchUrlExpander.Expand("http://x.test/[1-2000000000].bin"));

        var allocated = GC.GetTotalAllocatedBytes(precise: false) - before;
        Assert.Contains("beyond 1000", error.Message);
        // Two billion strings would take minutes and gigabytes; stopping at the limit takes a moment.
        Assert.True(allocated < 50L * 1024 * 1024, $"allocated {allocated:N0} bytes");
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"took {stopwatch.Elapsed}");
    }

    [Fact]
    public void APatternThatFitsTheLimitStillExpandsInFull()
    {
        var urls = BatchUrlExpander.Expand("http://x.test/[01-10]/[a-c].bin", limit: 30);
        Assert.Equal(30, urls.Count);
        Assert.Equal("http://x.test/01/a.bin", urls[0]);
        Assert.Equal("http://x.test/10/c.bin", urls[^1]);
        Assert.Throws<FormatException>(() => BatchUrlExpander.Expand("http://x.test/[1-31].bin", limit: 30));
    }

    [Theory]
    [InlineData(null, "startNow")]
    [InlineData("", "startNow")]
    [InlineData("now", "startNow")]
    [InlineData("StartNow", "startNow")]
    [InlineData("start", "startNow")]
    [InlineData("queue", "queue")]
    [InlineData("later", "later")]
    [InlineData(" Later ", "later")]
    [InlineData("latr", null)]
    [InlineData("1", null)]
    [InlineData("2", null)]
    public void StartModeWordsAreAClosedSet(string? value, string? expected)
        => Assert.Equal(expected, StartModes.Normalize(value));

    [Fact]
    public void HostStatusHasNoFieldNothingSets()
        => Assert.Null(typeof(HostStatusDto).GetProperty("LastError"));
}
