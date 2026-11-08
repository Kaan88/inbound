using DownloadManager.Archives;
using DownloadManager.Core.Archives;

namespace DownloadManager.Archives.Tests;

/// <summary>
/// <see cref="ExtractionSafety.ApplyNameFixes"/> moves what 7-Zip wrote under one name into the folders the listing
/// gave it. Off Windows the written names hold backslashes; the move itself doesn't care, so these use plain names and
/// run everywhere. The zips themselves are in <c>OtherFormatTests</c>.
/// </summary>
public sealed class NameFixTests : IDisposable
{
    private readonly Workspace _space = new();

    private string Payload => _space.Source;

    public void Dispose() => _space.Dispose();

    private string At(params string[] parts) => Path.Combine([Payload, .. parts]);

    private ExtractionException Refused(params ArchiveNameFix[] fixes) =>
        Assert.Throws<ExtractionException>(() => ExtractionSafety.ApplyNameFixes(Payload, fixes));

    [Fact]
    public void FilesMoveIntoTheirFoldersAndAWrittenFolderBecomesItsOwn()
    {
        File.WriteAllText(At("written-a.txt"), "a");
        Directory.CreateDirectory(At("written-empty"));

        ExtractionSafety.ApplyNameFixes(Payload,
        [
            new ArchiveNameFix("written-a.txt", "dir/sub/a.txt", false),
            new ArchiveNameFix("written-empty", "dir/empty", true),
        ]);

        Assert.Equal("a", File.ReadAllText(At("dir", "sub", "a.txt")));
        Assert.True(Directory.Exists(At("dir", "empty")));
        Assert.False(File.Exists(At("written-a.txt")));
        Assert.False(Directory.Exists(At("written-empty")));
    }

    [Fact]
    public void AFileThatDidntComeOutIsLeftToTheWalk()
    {
        ExtractionSafety.ApplyNameFixes(Payload, [new ArchiveNameFix("never-written.txt", "dir/a.txt", false)]);

        Assert.False(Directory.Exists(At("dir")));
    }

    [Fact]
    public void ATargetThatIsAlreadyThereIsRefusedAndNothingIsWrittenOver()
    {
        Directory.CreateDirectory(At("dir"));
        File.WriteAllText(At("dir", "a.txt"), "first");
        File.WriteAllText(At("written-a.txt"), "second");

        var failure = Refused(new ArchiveNameFix("written-a.txt", "dir/a.txt", false));

        Assert.Equal(ExtractionFailureKind.UnsafePath, failure.Kind);
        Assert.Equal("first", File.ReadAllText(At("dir", "a.txt")));
        Assert.Equal("second", File.ReadAllText(At("written-a.txt")));
    }

    [Fact]
    public void ATargetOutsideThePayloadFolderIsRefused()
    {
        File.WriteAllText(At("written-a.txt"), "a");

        var failure = Refused(new ArchiveNameFix("written-a.txt", "../outside.txt", false));

        Assert.Equal(ExtractionFailureKind.UnsafePath, failure.Kind);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(Payload)!, "outside.txt")));
        Assert.True(File.Exists(At("written-a.txt")));
    }

    [Fact]
    public void ALinkAnywhereInStagingStopsItBeforeAnythingMoves()
    {
        var elsewhere = Path.Combine(Path.GetDirectoryName(Payload)!, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        Directory.CreateDirectory(At("dir"));
        StagingWalkTests.MakeJunction(At("dir", "link"), elsewhere);
        File.WriteAllText(At("written-a.txt"), "a");

        var failure = Refused(new ArchiveNameFix("written-a.txt", "dir/link/a.txt", false));

        Assert.Equal(ExtractionFailureKind.UnsafePath, failure.Kind);
        Assert.Contains("link", failure.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(elsewhere));
        Assert.True(File.Exists(At("written-a.txt")));
    }

    /// <summary>
    /// "a\b/c.txt" makes 7-Zip create a folder "a\b" of its own, with no entry to fix it. Once c.txt has moved it is
    /// empty and goes; one that still holds something stays for the walk to refuse. Needs a backslash in a folder name, so
    /// Windows can't run it.
    /// </summary>
    [Fact]
    public void AnEmptyFolderWithABackslashThatNoEntryNamedIsRemovedAndAFullOneKept()
    {
        if (OperatingSystem.IsWindows()) return;
        var backslash = (char)92;
        var implicitFolder = "a" + backslash + "b";
        var fullFolder = "x" + backslash + "y";
        Directory.CreateDirectory(At(implicitFolder));
        File.WriteAllText(At(implicitFolder, "c.txt"), "c");
        Directory.CreateDirectory(At(fullFolder));
        File.WriteAllText(At(fullFolder, "unlisted.txt"), "u");

        ExtractionSafety.ApplyNameFixes(Payload, [new ArchiveNameFix(implicitFolder + "/c.txt", "a/b/c.txt", false)]);

        Assert.Equal("c", File.ReadAllText(At("a", "b", "c.txt")));
        Assert.False(Directory.Exists(At(implicitFolder)));
        Assert.True(File.Exists(At(fullFolder, "unlisted.txt")));
    }

    [Fact]
    public void AWrittenFolderThatStillHoldsSomethingIsRefused()
    {
        Directory.CreateDirectory(At("written-dir"));
        File.WriteAllText(At("written-dir", "inside.txt"), "x");

        var failure = Refused(new ArchiveNameFix("written-dir", "dir", true));

        Assert.Equal(ExtractionFailureKind.UnsafePath, failure.Kind);
        Assert.True(File.Exists(At("written-dir", "inside.txt")));
    }
}
