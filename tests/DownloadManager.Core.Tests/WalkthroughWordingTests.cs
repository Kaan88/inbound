using System.Globalization;
using DownloadManager.Desktop.WinUI;

namespace DownloadManager.Core.Tests;

/// <summary>UI.R23 (4) and (6): the Speed Limit menu's "Other" entry and when the package window shows its archive password.</summary>
public sealed class WalkthroughWordingTests
{
    private static readonly long[] Presets = [1L * SpeedLimitText.MegabytePerSecond, 5L * SpeedLimitText.MegabytePerSecond];

    [Fact]
    public void Other_carries_a_cap_that_is_no_preset()
    {
        var before = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            var cap = (long)(146.5 * 1024);
            Assert.False(SpeedLimitText.IsPreset(cap, Presets));
            Assert.Equal("Other (146.5 KB/s)…", SpeedLimitText.OtherLabel(cap, Presets));
            Assert.Equal("Other (2.5 MB/s)…", SpeedLimitText.OtherLabel(5 * SpeedLimitText.MegabytePerSecond / 2, Presets));
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Fact]
    public void Other_stays_plain_for_a_preset_and_for_no_cap()
    {
        Assert.True(SpeedLimitText.IsPreset(0, Presets));
        Assert.True(SpeedLimitText.IsPreset(5 * SpeedLimitText.MegabytePerSecond, Presets));
        Assert.Equal("Other…", SpeedLimitText.OtherLabel(0, Presets));
        Assert.Equal("Other…", SpeedLimitText.OtherLabel(SpeedLimitText.MegabytePerSecond, Presets));
    }

    [Theory]
    [InlineData("movie.rar")]
    [InlineData("movie.part2.rar")]
    [InlineData("movie.r00")]
    [InlineData("movie.7z")]
    [InlineData("movie.7z.001")]
    [InlineData("movie.zip")]
    [InlineData("movie.z01")]
    [InlineData("movie.tar.gz")]
    [InlineData("MOVIE.TGZ")]
    public void Archive_names_are_known(string name) => Assert.True(PackageArchiveSection.LooksLikeArchive(name));

    [Theory]
    [InlineData("movie.mkv")]
    [InlineData("setup.exe")]
    [InlineData("notes.txt")]
    [InlineData(".zip")]
    [InlineData("")]
    public void Other_names_are_not_archives(string name) => Assert.False(PackageArchiveSection.LooksLikeArchive(name));

    [Fact]
    public void The_password_section_shows_only_for_a_package_that_can_bring_archives()
    {
        Assert.False(PackageArchiveSection.Show(["movie.mkv", "subs.srt"], archiveSets: 0, hasPassword: false));
        Assert.True(PackageArchiveSection.Show(["movie.mkv", "extras.zip"], archiveSets: 0, hasPassword: false));
        Assert.True(PackageArchiveSection.Show(["movie.mkv"], archiveSets: 1, hasPassword: false));
        Assert.True(PackageArchiveSection.Show(["movie.mkv"], archiveSets: 0, hasPassword: true));
        // Not known yet: kept, as before.
        Assert.True(PackageArchiveSection.Show([], archiveSets: 0, hasPassword: false));
        Assert.True(PackageArchiveSection.Show(["movie.mkv", null], archiveSets: 0, hasPassword: false));
    }
}
