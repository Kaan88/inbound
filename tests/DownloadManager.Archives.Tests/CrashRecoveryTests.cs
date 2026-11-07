using System.Text;
using DownloadManager.Core.Archives;

namespace DownloadManager.Archives.Tests;

/// <summary>
/// What a crash during finalizing leaves behind. A live run cannot be stopped between two moves
/// without its own rollback running, so each test copies the disk at the chosen moment, lets the
/// run finish, and puts the copy back. That is exactly what a kill at that moment would leave.
/// </summary>
public sealed class CrashRecoveryTests : ExtractionTestBase
{
    private static readonly byte[] Content = Encoding.ASCII.GetBytes("from the archive");
    private static readonly byte[] UserContent = Encoding.ASCII.GetBytes("the user's own");

    private string BuildThreeFiles() =>
        Space.WriteArchive("three.rar", RarBuilder.Rar5(("a.txt", Content), ("b.txt", Content), ("c.txt", Content)));

    private async Task RunAndRewindToMidFinalize(string archive, ExtractionConflictPolicy policy)
    {
        var saved = Path.Combine(Space.Root, "saved");
        var progress = new ImmediateProgress<ExtractionProgress>(report =>
        {
            if (report is { Phase: ExtractionPhase.Finalizing, FilesFinalized: 2 }) CopyWorld(Space.Root, saved);
        });

        await Extractor().ExtractAsync(Request(archive, policy: policy), progress, CancellationToken.None);

        Assert.True(Directory.Exists(saved));
        RestoreWorld(saved, Space.Root);
    }

    [RequiresSevenZipFact]
    public async Task ACrashMidFinalizeIsUndoneSoARetryUnderRenameWritesNoSecondCopy()
    {
        var archive = BuildThreeFiles();
        Directory.CreateDirectory(Space.Destination);
        File.WriteAllBytes(Space.Output("b.txt"), UserContent);

        await RunAndRewindToMidFinalize(archive, ExtractionConflictPolicy.Rename);

        // The moment of the crash: two of three are in place, one under a new name.
        Assert.True(File.Exists(Space.Output("a.txt")));
        Assert.True(File.Exists(Space.Output("b (1).txt")));
        Assert.False(File.Exists(Space.Output("c.txt")));

        Assert.Single(StagingArea.FindLeftovers(Space.Staging));
        Assert.True((await StagingArea.RecoverAndRemoveAsync(Space.Staging, archive, Space.Destination)).Removed);

        Assert.Equal(["b.txt"], Directory.EnumerateFiles(Space.Destination).Select(file => Path.GetFileName(file)!));
        Assert.Equal(UserContent, File.ReadAllBytes(Space.Output("b.txt")));
        Space.AssertNoStagingLeft();

        await Extractor().ExtractAsync(Request(archive, policy: ExtractionConflictPolicy.Rename), null, CancellationToken.None);
        Assert.Equal(
            ["a.txt", "b (1).txt", "b.txt", "c.txt"],
            Directory.EnumerateFiles(Space.Destination).Select(file => Path.GetFileName(file)!).Order(StringComparer.Ordinal));
    }

    [RequiresSevenZipFact]
    public async Task ACrashMidOverwritePutsTheOriginalBack()
    {
        var archive = BuildThreeFiles();
        Directory.CreateDirectory(Space.Destination);
        File.WriteAllBytes(Space.Output("b.txt"), UserContent);

        await RunAndRewindToMidFinalize(archive, ExtractionConflictPolicy.Overwrite);
        Assert.Equal(Content, File.ReadAllBytes(Space.Output("b.txt")));

        Assert.Single(StagingArea.FindLeftovers(Space.Staging));
        Assert.True((await StagingArea.RecoverAndRemoveAsync(Space.Staging, archive, Space.Destination)).Removed);

        Assert.Equal(UserContent, File.ReadAllBytes(Space.Output("b.txt")));
        Assert.False(File.Exists(Space.Output("a.txt")));
    }

    [RequiresSevenZipFact]
    public async Task ALineACrashCutShortDoesNotStopRecovery()
    {
        var archive = BuildThreeFiles();
        await RunAndRewindToMidFinalize(archive, ExtractionConflictPolicy.Rename);
        var leftover = Assert.Single(StagingArea.FindLeftovers(Space.Staging));
        File.AppendAllText(Path.Combine(leftover, "placed.log"), "{\"Source\":\"c.txt\",\"Tar");

        Assert.True((await StagingArea.RecoverAndRemoveAsync(Space.Staging, archive, Space.Destination)).Removed);

        Assert.Empty(Directory.EnumerateFiles(Space.Destination));
    }

    [RequiresSevenZipFact]
    public async Task AFailedCommitTakesThePlacedFilesBackOut()
    {
        var archive = BuildThreeFiles();
        var sawStaging = false;
        var request = new ExtractionRequest
        {
            FirstVolume = archive,
            Destination = Space.Destination,
            StagingRoot = Space.Staging,
            Committed = _ =>
            {
                sawStaging = StagingArea.FindLeftovers(Space.Staging).Count == 1;
                throw new IOException("the disk went away");
            },
        };

        var failure = await Assert.ThrowsAsync<ExtractionException>(() => Extractor().ExtractAsync(request, null, CancellationToken.None));

        Assert.True(sawStaging);
        Assert.NotEqual(ExtractionFailureKind.Cancelled, failure.Kind);
        Space.AssertDestinationUntouched();
        Space.AssertNoStagingLeft();
    }

    [RequiresSevenZipFact]
    public async Task ACommitRunsOnceTheFilesAreInPlaceAndThenStagingGoes()
    {
        var archive = BuildThreeFiles();
        var placed = 0;
        var request = new ExtractionRequest
        {
            FirstVolume = archive,
            Destination = Space.Destination,
            StagingRoot = Space.Staging,
            Committed = result =>
            {
                placed = result.Files.Count(file => File.Exists(file.FinalPath));
                return Task.CompletedTask;
            },
        };

        await Extractor().ExtractAsync(request, null, CancellationToken.None);

        Assert.Equal(3, placed);
        Space.AssertNoStagingLeft();
    }

    [RequiresSevenZipFact]
    public async Task DiscardingAFinishedAttemptLeavesItsFiles()
    {
        var archive = BuildThreeFiles();
        await RunAndRewindToMidFinalize(archive, ExtractionConflictPolicy.Rename);
        var leftover = Assert.Single(StagingArea.FindLeftovers(Space.Staging));

        Assert.True(await StagingArea.DiscardFinishedAsync(Space.Staging, archive, Space.Destination));

        Space.AssertNoStagingLeft();
        Assert.True(File.Exists(Space.Output("a.txt")));
    }

    [RequiresSevenZipFact]
    public async Task AnOverwriteDoesNotReplaceAFileItWasToldToKeep()
    {
        var bytes = RarBuilder.Rar5(("self.rar", Content), ("other.txt", Content));
        var archive = Space.WriteArchive("self.rar", bytes);
        var request = new ExtractionRequest
        {
            FirstVolume = archive,
            Destination = Space.Archives,
            StagingRoot = Space.Archives,
            ConflictPolicy = ExtractionConflictPolicy.Overwrite,
            KeepFiles = [archive],
        };

        var result = await Extractor().ExtractAsync(request, null, CancellationToken.None);

        Assert.Equal(bytes, File.ReadAllBytes(archive));
        Assert.Equal(Content, File.ReadAllBytes(Space.Archive("self (1).rar")));
        Assert.Contains(result.Files, file => file.Outcome == ExtractedFileOutcome.Renamed);
    }

    private static void CopyWorld(string root, string saved)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (file.StartsWith(saved, StringComparison.OrdinalIgnoreCase)) continue;
            var target = Path.Combine(saved, Path.GetRelativePath(root, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    private static void RestoreWorld(string saved, string root)
    {
        foreach (var folder in new[] { "out put", "staging" })
        {
            var live = Path.Combine(root, folder);
            if (Directory.Exists(live)) Directory.Delete(live, recursive: true);
        }

        foreach (var file in Directory.EnumerateFiles(saved, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(root, Path.GetRelativePath(saved, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }

        Directory.Delete(saved, recursive: true);
    }
}
