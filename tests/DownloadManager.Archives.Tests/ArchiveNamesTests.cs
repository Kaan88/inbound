using DownloadManager.Archives;

namespace DownloadManager.Archives.Tests;

/// <summary>Which files an extraction chooser lists: only names extraction starts from.</summary>
public sealed class ArchiveNamesTests
{
    [Theory]
    [InlineData("a.rar")]
    [InlineData("Movie.part1.rar")]
    [InlineData("Movie.part2.RAR")]
    [InlineData("a.7z")]
    [InlineData("a.zip")]
    [InlineData("a.tar")]
    [InlineData("a.tar.gz")]
    [InlineData("a.tgz")]
    [InlineData("a.tar.bz2")]
    [InlineData("a.tbz2")]
    [InlineData("a.tbz")]
    [InlineData("a.tar.xz")]
    [InlineData("a.txz")]
    [InlineData("a.tar.zst")]
    [InlineData("a.tzst")]
    [InlineData("a.7z.001")]
    [InlineData("a.zip.001")]
    [InlineData("a.rar.001")]
    [InlineData("a.TAR.001")]
    public void ListsWhatExtractionStartsFrom(string name) => Assert.True(ArchiveNames.IsChooserName(name), name);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(".zip")]
    [InlineData(".rar")]
    [InlineData(".tar.gz")]
    [InlineData(".7z.001")]
    [InlineData("backup.sql.gz")]
    [InlineData("log.bz2")]
    [InlineData("image.xz")]
    [InlineData("data.zst")]
    [InlineData("movie.mkv.001")]
    [InlineData("plain.001")]
    [InlineData("a.7z.002")]
    [InlineData("a.r00")]
    [InlineData("a.z01")]
    [InlineData("notes.txt")]
    [InlineData("a.zipx")]
    public void LeavesOutWhatExtractionWouldRefuse(string? name) => Assert.False(ArchiveNames.IsChooserName(name), name);
}
