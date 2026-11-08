using DownloadManager.Archives;

namespace DownloadManager.Archives.Tests;

public sealed class ArchiveVolumesTests
{
    private static ArchiveVolumeSet Only(params string[] names) => Assert.Single(ArchiveVolumes.Group(names));

    [Fact]
    public void NamesThatDifferOnlyByCaseAreOneFileOnWindowsAndTwoSetsOnLinux()
    {
        var sets = ArchiveVolumes.Group(["Movie.part1.rar", "Movie.part2.rar", "movie.part1.rar"]);
        if (OperatingSystem.IsWindows())
        {
            // NTFS keeps one of them, so the names are one file.
            var set = Assert.Single(sets);
            Assert.Equal(["Movie.part1.rar", "Movie.part2.rar"], set.Members);
            return;
        }

        // Linux keeps both files, so the lower-case one can't be taken for a volume of the other set, or its download
        // would be tied to that set's cleanup.
        Assert.Equal(2, sets.Count);
        Assert.Contains(sets, set => set.Members.SequenceEqual(["Movie.part1.rar", "Movie.part2.rar"]));
        Assert.Contains(sets, set => set.Members.SequenceEqual(["movie.part1.rar"]));
        Assert.Equal(["movie.part1.rar"], ArchiveVolumes.FindSet(["Movie.part1.rar", "Movie.part2.rar", "movie.part1.rar"], "movie.part1.rar")!.Members);
        // Without such a pair a set whose names differ in case stays one set there too (CaseDoesNotSplitASet).
        Assert.Single(ArchiveVolumes.Group(["Film.PART1.RAR", "film.part2.rar"]));
    }

    [Fact]
    public void PartNumberedSetIsInOrderWithFirstVolume()
    {
        var set = Only("example.part03.rar", "example.part01.rar", "example.part02.rar");

        Assert.Equal(ArchiveVolumeScheme.PartNumbered, set.Scheme);
        Assert.Equal("example", set.BaseName);
        Assert.Equal("example.part01.rar", set.FirstVolume);
        Assert.Equal(["example.part01.rar", "example.part02.rar", "example.part03.rar"], set.Members);
        Assert.Empty(set.MissingVolumes);
    }

    [Theory]
    [InlineData("x.part1.rar", "x.part2.rar", "x.part1.rar")]
    [InlineData("x.part01.rar", "x.part02.rar", "x.part01.rar")]
    [InlineData("x.part001.rar", "x.part002.rar", "x.part001.rar")]
    [InlineData("x.part0001.rar", "x.part0002.rar", "x.part0001.rar")]
    public void AnyPaddingIsAccepted(string first, string second, string expectedFirst)
    {
        var set = Only(second, first);
        Assert.Equal(expectedFirst, set.FirstVolume);
        Assert.Equal(2, set.Members.Count);
    }

    [Fact]
    public void PartGapIsReportedByNameWithTheSetsPadding()
    {
        var set = Only("x.part01.rar", "x.part04.rar");
        Assert.Equal(["x.part02.rar", "x.part03.rar"], set.MissingVolumes);
        Assert.Equal("x.part01.rar", set.FirstVolume);
    }

    [Fact]
    public void MissingFirstPartLeavesNoFirstVolume()
    {
        var set = Only("x.part2.rar", "x.part3.rar");
        Assert.Null(set.FirstVolume);
        Assert.Equal(["x.part1.rar"], set.MissingVolumes);
    }

    [Fact]
    public void UnpaddedSetCanGrowPastNine()
    {
        var names = Enumerable.Range(1, 11).Select(number => $"x.part{number}.rar").ToArray();
        var set = Only(names);
        Assert.Empty(set.MissingVolumes);
        Assert.Equal("x.part10.rar", set.Members[9]);
    }

    [Fact]
    public void PaddedSetCanHoldNumbersWithNoLeadingZero()
    {
        var names = Enumerable.Range(1, 12).Select(number => $"x.part{number:00}.rar").ToArray();
        var set = Only(names);
        Assert.Empty(set.MissingVolumes);
        Assert.Equal(12, set.Members.Count);
    }

    [Fact]
    public void DifferentPaddingIsDifferentSets()
    {
        var sets = ArchiveVolumes.Group(["a.part1.rar", "a.part01.rar"]);

        Assert.Equal(2, sets.Count);
        Assert.Contains(sets, set => set.FirstVolume == "a.part1.rar" && set.Members.Count == 1);
        Assert.Contains(sets, set => set.FirstVolume == "a.part01.rar" && set.Members.Count == 1);
    }

    [Fact]
    public void DifferentPaddingKeepsEachSetsOwnMembers()
    {
        var sets = ArchiveVolumes.Group(["a.part1.rar", "a.part2.rar", "a.part01.rar", "a.part02.rar", "a.part03.rar"]);
        Assert.Equal(2, sets.Count);
        Assert.Contains(sets, set => set.Members.SequenceEqual(["a.part1.rar", "a.part2.rar"]));
        Assert.Contains(sets, set => set.Members.SequenceEqual(["a.part01.rar", "a.part02.rar", "a.part03.rar"]));
    }

    [Fact]
    public void PartZeroIsNotAVolume()
    {
        var set = Only("x.part0.rar");
        Assert.Equal(ArchiveVolumeScheme.Single, set.Scheme);
    }

    [Fact]
    public void OldStyleSetStartsAtRarAndCountsR00Next()
    {
        var set = Only("x.r01", "x.rar", "x.r00");

        Assert.Equal(ArchiveVolumeScheme.OldStyle, set.Scheme);
        Assert.Equal("x.rar", set.FirstVolume);
        Assert.Equal(["x.rar", "x.r00", "x.r01"], set.Members);
        Assert.Empty(set.MissingVolumes);
    }

    [Fact]
    public void OldStyleGapIsNamed()
    {
        var set = Only("x.rar", "x.r00", "x.r02");
        Assert.Equal(["x.r01"], set.MissingVolumes);
    }

    [Fact]
    public void OldStyleWithoutRarNamesItAsMissing()
    {
        var set = Only("x.r00", "x.r01");
        Assert.Null(set.FirstVolume);
        Assert.Equal(["x.rar"], set.MissingVolumes);
    }

    [Fact]
    public void OldStyleContinuesWithS00AfterR99()
    {
        var names = new List<string> { "x.rar" };
        names.AddRange(Enumerable.Range(0, 100).Select(number => $"x.r{number:00}"));
        names.AddRange(["x.s00", "x.s02"]);

        var set = Only([.. names]);

        Assert.Equal("x.s00", set.Members[101]);
        Assert.Equal("x.s02", set.Members[^1]);
        Assert.Equal(["x.s01"], set.MissingVolumes);
    }

    [Fact]
    public void GapBeforeSNamesTheRVolumes()
    {
        var names = new List<string> { "x.rar" };
        names.AddRange(Enumerable.Range(0, 99).Select(number => $"x.r{number:00}"));
        names.Add("x.s00");

        var set = Only([.. names]);

        Assert.Equal(["x.r99"], set.MissingVolumes);
    }

    [Fact]
    public void SNumbersAloneAreNotVolumes()
    {
        Assert.Empty(ArchiveVolumes.Group(["show.s01", "show.s02"]));
        var set = Only("show.rar", "show.s01");
        Assert.Equal(ArchiveVolumeScheme.Single, set.Scheme);
        Assert.Equal(["show.rar"], set.Members);
    }

    [Fact]
    public void ThreeDigitSetStartsAtOne()
    {
        var set = Only("x.002.rar", "x.001.rar", "x.003.rar");
        Assert.Equal(ArchiveVolumeScheme.ThreeDigit, set.Scheme);
        Assert.Equal("x.001.rar", set.FirstVolume);
        Assert.Equal(["x.001.rar", "x.002.rar", "x.003.rar"], set.Members);
    }

    [Fact]
    public void ThreeDigitSetCanStartAtZero()
    {
        var set = Only("x.000.rar", "x.001.rar");
        Assert.Equal("x.000.rar", set.FirstVolume);
        Assert.Empty(set.MissingVolumes);
    }

    [Fact]
    public void ThreeDigitGapAndMissingFirstAreNamed()
    {
        var set = Only("x.002.rar", "x.004.rar");
        Assert.Null(set.FirstVolume);
        Assert.Equal(["x.001.rar", "x.003.rar"], set.MissingVolumes);
    }

    [Fact]
    public void FarApartThreeDigitNamesAreSeparateArchives()
    {
        var sets = ArchiveVolumes.Group(["report.123.rar", "report.124.rar"]);
        Assert.Equal(2, sets.Count);
        Assert.All(sets, set => Assert.Equal(ArchiveVolumeScheme.Single, set.Scheme));
        Assert.All(sets, set => Assert.Empty(set.MissingVolumes));
    }

    [Fact]
    public void LoneThreeDigitNameIsAPlainArchive()
    {
        var set = Only("movie.2019.rar");
        Assert.Equal(ArchiveVolumeScheme.Single, set.Scheme);
        set = Only("report.123.rar");
        Assert.Equal(ArchiveVolumeScheme.Single, set.Scheme);
    }

    [Fact]
    public void PlainRarWithoutSiblingsIsSingle()
    {
        var set = Only("name.rar");
        Assert.Equal(ArchiveVolumeScheme.Single, set.Scheme);
        Assert.Equal("name.rar", set.FirstVolume);
        Assert.Equal(["name.rar"], set.Members);
        Assert.Empty(set.MissingVolumes);
    }

    [Fact]
    public void DifferentNamesDoNotMix()
    {
        var sets = ArchiveVolumes.Group(["one.part1.rar", "one.part2.rar", "two.part1.rar", "two.part2.rar", "three.rar", "four.rar", "four.r00"]);

        Assert.Equal(4, sets.Count);
        Assert.Equal(["one.part1.rar", "one.part2.rar"], sets.Single(set => set.BaseName == "one").Members);
        Assert.Equal(["two.part1.rar", "two.part2.rar"], sets.Single(set => set.BaseName == "two").Members);
        Assert.Equal(ArchiveVolumeScheme.Single, sets.Single(set => set.BaseName == "three").Scheme);
        Assert.Equal(ArchiveVolumeScheme.OldStyle, sets.Single(set => set.BaseName == "four").Scheme);
    }

    [Fact]
    public void SameBaseWithDifferentSchemesStaysApart()
    {
        var sets = ArchiveVolumes.Group(["name.rar", "name.part1.rar", "name.part2.rar"]);
        Assert.Equal(2, sets.Count);
        Assert.Contains(sets, set => set.Scheme == ArchiveVolumeScheme.Single && set.FirstVolume == "name.rar");
        Assert.Contains(sets, set => set.Scheme == ArchiveVolumeScheme.PartNumbered && set.FirstVolume == "name.part1.rar");
    }

    [Theory]
    [InlineData("name.part1.rar.txt")]
    [InlineData("photo.r2d2")]
    [InlineData("photo.r1")]
    [InlineData("photo.r000")]
    [InlineData("notes.txt")]
    [InlineData("name.rar.part")]
    [InlineData("name.rar.0123456789abcdef.part")]
    [InlineData(".rar")]
    [InlineData(".zip")]
    [InlineData("rar")]
    public void UnrelatedFilesAreNotArchives(string name)
    {
        Assert.Empty(ArchiveVolumes.Group([name]));
    }

    [Fact]
    public void UnrelatedFilesDoNotJoinASet()
    {
        var set = Only("a.part1.rar", "a.part2.rar", "a.part1.rar.txt", "a.r2d2", "a.nfo");
        Assert.Equal(["a.part1.rar", "a.part2.rar"], set.Members);
    }

    [Fact]
    public void CaseDoesNotSplitASet()
    {
        var set = Only("Film.PART1.RAR", "film.part2.rar");
        Assert.Equal(2, set.Members.Count);
        Assert.Equal("Film.PART1.RAR", set.FirstVolume);
    }

    [Fact]
    public void AnOldStyleSetNamedLikeAPartStaysOldStyle()
    {
        var set = Only("x.part1.rar", "x.part1.r00");
        Assert.Equal(ArchiveVolumeScheme.OldStyle, set.Scheme);
        Assert.Equal("x.part1", set.BaseName);
        Assert.Equal("x.part1.rar", set.FirstVolume);
    }

    [Fact]
    public void DottedBaseNamesAreKeptWhole()
    {
        var set = Only("my.big.file.part1.rar", "my.big.file.part2.rar");
        Assert.Equal("my.big.file", set.BaseName);
    }

    [Fact]
    public void FindSetReturnsTheOneHoldingTheName()
    {
        var names = new[] { "a.part1.rar", "a.part2.rar", "b.rar" };
        Assert.Equal("a", ArchiveVolumes.FindSet(names, "a.part2.rar")?.BaseName);
        Assert.Null(ArchiveVolumes.FindSet(names, "c.rar"));
    }

    [Theory]
    [InlineData("a.7z", "a")]
    [InlineData("a.zip", "a")]
    [InlineData("a.tar", "a")]
    [InlineData("a.tar.gz", "a")]
    [InlineData("a.TGZ", "a")]
    [InlineData("a.tar.xz", "a")]
    [InlineData("a.tar.bz2", "a")]
    public void OtherSingleArchivesAreTheirOwnSet(string name, string stem)
    {
        var set = Only(name);
        Assert.Equal(ArchiveVolumeScheme.Single, set.Scheme);
        Assert.Equal(stem, set.BaseName);
        Assert.Equal(name, set.FirstVolume);
    }

    [Fact]
    public void ASplitSetIsInOrderAndNamesItsGaps()
    {
        var set = Only("b.7z.003", "b.7z.001", "b.7z.005");
        Assert.Equal(ArchiveVolumeScheme.Split, set.Scheme);
        Assert.Equal("b.7z", set.BaseName);
        Assert.Equal("b.7z.001", set.FirstVolume);
        Assert.Equal(["b.7z.001", "b.7z.003", "b.7z.005"], set.Members);
        Assert.Equal(["b.7z.002", "b.7z.004"], set.MissingVolumes);
    }

    [Fact]
    public void ASplitSetWithoutItsFirstWaitsForIt()
    {
        var set = Only("c.zip.002");
        Assert.Null(set.FirstVolume);
        Assert.Equal(["c.zip.001"], set.MissingVolumes);
    }

    [Theory]
    [InlineData("movie.mkv.001")]
    [InlineData("notes.gz")]
    [InlineData("film.mp4")]
    public void OtherNamesAreNotArchives(string name) => Assert.Empty(ArchiveVolumes.Group([name]));
}
