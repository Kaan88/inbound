using System.Diagnostics;
using DownloadManager.Core;
using DownloadManager.Core.Providers;

namespace DownloadManager.Core.Tests;

/// <summary>Text and names a provider chose, which can be anything at all. All values are invented.</summary>
public sealed class RemoteTextSafetyTests
{
    [Theory]
    [InlineData("a‮txt.exe")]
    [InlineData("photo‮gpj.exe")]
    [InlineData("a‪txt")]
    [InlineData("a⁦txt")]
    [InlineData("a⁩txt")]
    [InlineData("a‎txt")]
    [InlineData("a‏txt")]
    [InlineData("a؜txt")]
    [InlineData("x\u0085y")]
    [InlineData("x\u009By")]
    [InlineData("x\u007Fy")]
    [InlineData("x\u001By")]
    public void NothingThatReordersOrControlsTextSurvivesInAName(string name)
    {
        var segment = RemotePaths.CleanSegment(name)!;
        Assert.DoesNotContain(segment, FileNameResolver.IsDeceptiveOrControl);
        Assert.Contains('_', segment);

        var sanitized = FileNameResolver.Sanitize(name);
        Assert.DoesNotContain(sanitized, FileNameResolver.IsDeceptiveOrControl);
        Assert.Contains('_', sanitized);
    }

    [Theory]
    [InlineData("café.txt")]
    [InlineData("فيلم.mkv")]
    [InlineData("a‍b.txt")] // A joiner is part of how some scripts and emoji are written.
    public void OrdinaryNamesAreKept(string name)
    {
        Assert.Equal(name, RemotePaths.CleanSegment(name));
        Assert.Equal(name, FileNameResolver.Sanitize(name));
    }

    [Fact]
    public void ASegmentAtTheLimitIsKeptAndOnePastItIsRefused()
    {
        Assert.NotNull(RemotePaths.ToSafeRelativePath(new string('a', RemotePaths.LongestSegment)));
        Assert.Null(RemotePaths.ToSafeRelativePath(new string('a', RemotePaths.LongestSegment + 1)));
        Assert.Null(RemotePaths.ToSafeRelativePath($"folder/{new string('a', 300)}/file.bin"));
    }

    [Fact]
    public void ATooLongPathIsRefusedHoweverDeepItIs()
    {
        var parts = Enumerable.Repeat(new string('a', 50), 20);
        Assert.NotNull(RemotePaths.ToSafeRelativePath(string.Join('/', parts.Take(5))));
        Assert.Null(RemotePaths.ToSafeRelativePath(string.Join('/', parts)));
        Assert.Null(RemotePaths.ToSafeRelativePath(string.Join('/', Enumerable.Repeat("a", 100_000))));
    }

    /// <summary>The patterns took about 8.9 s on this length, and four times that for each doubling.</summary>
    [Theory]
    [InlineData("a.")]
    [InlineData("x-")]
    public void TextBuiltToSlowThePatternsDoesntSlowThem(string unit)
    {
        var text = string.Concat(Enumerable.Repeat(unit, 640_000)) + "=";
        var clock = Stopwatch.StartNew();
        var hidden = UrlRedactor.HideAddresses(text);
        var redacted = UrlRedactor.RedactText(text);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"Took {clock.Elapsed}.");
        Assert.True(hidden.Length <= UrlRedactor.LongestText);
        Assert.True(redacted.Length <= UrlRedactor.LongestText);
    }

    [Theory]
    [InlineData("magnet:xt=urn:btih:abc&tr=http%3A%2F%2Ftracker%2Finvented-passkey")]
    [InlineData("Could not fetch magnet:?xt=urn:btih:abc&tr=invented-passkey today")]
    public void EveryMagnetIsHidden(string text)
    {
        Assert.DoesNotContain("invented-passkey", UrlRedactor.HideAddresses(text));
        Assert.DoesNotContain("invented-passkey", UrlRedactor.RedactText(text));
    }

    [Fact]
    public void MagnetAsAPlainWordIsNotHidden() => Assert.Equal("A magnet is fine", UrlRedactor.HideAddresses("A magnet is fine"));
}
