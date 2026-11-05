using DownloadManager.Core;
using Xunit;

namespace DownloadManager.Core.Tests;

public sealed class MultipartArchiveNameTests
{
    [Theory]
    [InlineData("movie.part1.rar", "movie")]
    [InlineData("movie.part01.rar", "movie")]
    [InlineData("Movie.PART012.RAR", "Movie")]
    [InlineData("data.r00", "data")]
    [InlineData("data.r15", "data")]
    [InlineData("backup.7z.001", "backup.7z")]
    [InlineData("backup.zip.002", "backup.zip")]
    [InlineData("site.z01", "site")]
    [InlineData("disk.001", "disk")]
    [InlineData(@"C:\dl\movie.part2.rar", "movie")]
    public void Recognises_split_archive_parts(string fileName, string expected)
    {
        Assert.True(MultipartArchiveName.TryGetBaseName(fileName, out var baseName));
        Assert.Equal(expected, baseName);
    }

    [Theory]
    [InlineData("v1.2.3.zip")]
    [InlineData("movie.rar")]
    [InlineData("setup.exe")]
    [InlineData("report.2024")]
    [InlineData("readme")]
    [InlineData("")]
    [InlineData(null)]
    public void Ignores_single_files(string? fileName) =>
        Assert.False(MultipartArchiveName.TryGetBaseName(fileName, out _));

    [Fact]
    public void Parts_in_the_same_folder_share_a_key_case_insensitively()
    {
        var one = MultipartArchiveName.KeyFor("Movie.part1.rar", @"C:\Downloads");
        var two = MultipartArchiveName.KeyFor("movie.PART02.rar", @"c:\downloads\");
        Assert.NotNull(one);
        Assert.Equal(one, two);
        Assert.NotEqual(one, MultipartArchiveName.KeyFor("movie.part3.rar", @"C:\Other"));
    }

    [Fact]
    public void Unnumbered_first_part_matches_old_style_sets()
    {
        Assert.Equal(MultipartArchiveName.KeyFor("data.r00", @"C:\d"), MultipartArchiveName.FirstPartKeyFor("data.rar", @"C:\d"));
        Assert.Equal(MultipartArchiveName.KeyFor("site.z01", @"C:\d"), MultipartArchiveName.FirstPartKeyFor("site.zip", @"C:\d"));
        Assert.Null(MultipartArchiveName.FirstPartKeyFor("movie.part1.rar", @"C:\d"));
        Assert.Null(MultipartArchiveName.FirstPartKeyFor("movie.mkv", @"C:\d"));
    }
}
