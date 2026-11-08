using DownloadManager.Core.Archives;

namespace DownloadManager.Archives.Tests;

/// <summary>
/// A4.D: spanned zip sets, name.z01, name.z02 ... with name.zip last. The sets are hand-built by
/// <see cref="SpannedZipBuilder"/>, since nothing on this PC writes them from a command line; the bundled 7-Zip 26.04
/// lists and extracts them from name.zip and names a missing part.
/// </summary>
public sealed class SpannedZipTests : ExtractionTestBase
{
    private static ArchiveVolumeSet Only(params string[] names) => Assert.Single(ArchiveVolumes.Group(names));

    private static readonly (string Name, byte[] Data)[] Entries =
    [
        ("one.txt", "first segment's file"u8.ToArray()),
        ("two.bin", Workspace.RandomBytes(5000, 3)),
        ("three.txt", "third"u8.ToArray()),
        ("four.txt", "last"u8.ToArray()),
    ];

    private IReadOnlyList<string> WriteSet()
    {
        foreach (var (name, data) in Entries) Space.WriteSource(name, data);
        return SpannedZipBuilder.Write(Space.Archives, "set", Entries);
    }

    [Fact]
    public void ASpannedSetGroupsWithItsZipLastAndOpenedFirst()
    {
        var set = Only("set.zip", "set.z02", "set.z01");
        Assert.Equal(ArchiveVolumeScheme.ZipSpanned, set.Scheme);
        Assert.Equal("set", set.BaseName);
        Assert.Equal("set.zip", set.FirstVolume);
        Assert.Equal(["set.z01", "set.z02", "set.zip"], set.Members);
        Assert.Empty(set.MissingVolumes);
    }

    [Fact]
    public void AMissingMiddlePartIsNamed()
    {
        var set = Only("set.z01", "set.z03", "set.zip");
        Assert.Equal(ArchiveVolumeScheme.ZipSpanned, set.Scheme);
        Assert.Equal(["set.z02"], set.MissingVolumes);
    }

    [Fact]
    public void WithoutItsZipTheSetWaitsForIt()
    {
        var set = Only("set.z02", "set.z01");
        Assert.Null(set.FirstVolume);
        Assert.Equal(["set.zip"], set.MissingVolumes);
    }

    [Fact]
    public void AZipAloneIsStillASingleArchive()
    {
        var set = Only("set.zip");
        Assert.Equal(ArchiveVolumeScheme.Single, set.Scheme);
    }

    [Fact]
    public void AnOldStyleRarSetPastR99KeepsItsZVolumes()
    {
        // name.rar, name.r00 ... name.z00 is an old-style RAR set, so its .zNN are RAR volumes, not zip parts.
        var set = Only("big.rar", "big.r00", "big.z01");
        Assert.Equal(ArchiveVolumeScheme.OldStyle, set.Scheme);
        Assert.Contains("big.z01", set.Members);
    }

    [Fact]
    public void ASpannedZipBesideOtherArchivesIsItsOwnSet()
    {
        var sets = ArchiveVolumes.Group(["photos.zip", "set.z01", "set.zip", "other.7z"]);
        Assert.Equal(3, sets.Count);
        Assert.Contains(sets, set => set is { Scheme: ArchiveVolumeScheme.ZipSpanned, FirstVolume: "set.zip" });
        Assert.Contains(sets, set => set is { Scheme: ArchiveVolumeScheme.Single, FirstVolume: "photos.zip" });
    }

    [RequiresSevenZipFact]
    public async Task ASpannedSetExtractsFromItsZipAndNamesEveryPart()
    {
        var paths = WriteSet();
        var zip = paths[^1];

        var probe = await Extractor().ProbeAsync(zip, null, CancellationToken.None);
        Assert.True(probe.IsVolume);
        Assert.Equal(1, probe.VolumeNumber);
        Assert.True(probe.AllVolumesPresent);
        Assert.Equal(["set.z01", "set.z02", "set.z03", "set.zip"], probe.VolumesRead.Select(Path.GetFileName));

        var result = await Extractor().ExtractAsync(Request(zip), null, CancellationToken.None);
        Assert.Equal(4, result.VolumesRead.Count);
        Space.AssertSameAsSource();
    }

    [RequiresSevenZipFact]
    public async Task AMissingMiddlePartStopsExtractionAndIsNamed()
    {
        var paths = WriteSet();
        File.Delete(paths[1]);

        var probe = await Extractor().ProbeAsync(paths[^1], null, CancellationToken.None);
        Assert.False(probe.AllVolumesPresent);
        Assert.Contains("set.z02", probe.MissingVolumes);
        Assert.DoesNotContain("set.z03", probe.MissingVolumes);

        Assert.Equal(ExtractionFailureKind.MissingVolume, await FailureOf(Request(paths[^1])));
        Space.AssertDestinationUntouched();
    }

    [RequiresSevenZipFact]
    public async Task AMissingLastPartIsNamedFromTheDiskCount()
    {
        // The names alone can't show set.z03 is gone when it was the highest part; name.zip's disk count does.
        var paths = WriteSet();
        File.Delete(paths[2]);
        Assert.Empty(Only("set.z01", "set.z02", "set.zip").MissingVolumes);

        var probe = await Extractor().ProbeAsync(paths[^1], null, CancellationToken.None);
        Assert.False(probe.AllVolumesPresent);
        Assert.Contains("set.z03", probe.MissingVolumes);
        Assert.Equal(ExtractionFailureKind.MissingVolume, await FailureOf(Request(paths[^1])));
    }

    [RequiresSevenZipFact]
    public async Task StartingFromAPartIsRefused()
    {
        var paths = WriteSet();
        Assert.Equal(ExtractionFailureKind.MissingVolume, await FailureOf(Request(paths[0])));
        Space.AssertDestinationUntouched();
    }
}
