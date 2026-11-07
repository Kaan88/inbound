using System.Diagnostics;
using System.Text;
using DownloadManager.Archives;
using DownloadManager.Core.Archives;

namespace DownloadManager.Archives.Tests;

/// <summary>Regressions from the review of extraction: names that mislead, archives that plant things, and what a crash or a power cut can cost.</summary>
public sealed class ReviewRegressionTests : ExtractionTestBase
{
    private static readonly byte[] Text = Encoding.ASCII.GetBytes("from the archive");
    private static readonly byte[] Other = Encoding.ASCII.GetBytes("something else!!");

    // ----- A name this app keeps for itself -----

    [Theory]
    [InlineData(".inbound-extract-evil")]
    [InlineData(".INBOUND-Extract-0123456789abcdef")]
    [InlineData(@"sub\.inbound-extract-x\attempt.json")]
    [InlineData(".inbound-extract-x/displaced/planted.cmd")]
    public void TheWorkingFolderNameCanNotComeFromOutside(string path)
    {
        Assert.Contains("working folders", ExtractionSafety.DescribeUnsafePath(path));
    }

    [RequiresSevenZipFact]
    public async Task AnArchiveHoldingAWorkingFolderIsRefusedBeforeAnythingLands()
    {
        var archive = Space.WriteArchive("evil.rar", RarBuilder.Rar5(
            (".inbound-extract-evil/attempt.json", Text),
            (".inbound-extract-evil/placed.log", Text),
            (".inbound-extract-evil/displaced/Startup/planted.cmd", Text)));
        var request = new ExtractionRequest { FirstVolume = archive, Destination = Space.Archives, StagingRoot = Space.Archives };

        var failure = await Assert.ThrowsAsync<ExtractionException>(() => Extractor().ExtractAsync(request, null, CancellationToken.None));

        Assert.Equal(ExtractionFailureKind.UnsafePath, failure.Kind);
        Assert.Empty(StagingArea.FindLeftovers(Space.Archives));
        Assert.Equal(["evil.rar"], Directory.EnumerateFileSystemEntries(Space.Archives).Select(item => Path.GetFileName(item)!));
    }

    [Fact]
    public void AWalkRefusesAWorkingFolderTheListingDidNotShow()
    {
        var payload = Directory.CreateDirectory(Path.Combine(Space.Root, "payload")).FullName;
        Directory.CreateDirectory(Path.Combine(payload, ".inbound-extract-x"));
        var probe = new ArchiveProbe
        {
            FirstVolume = Space.Archive("x.rar"),
            IsEncrypted = false,
            HeadersEncrypted = false,
            Entries = [],
            Version = "5",
            IsVolume = false,
            AllVolumesPresent = true,
        };

        var failure = Assert.Throws<ExtractionException>(() => ExtractionSafety.WalkStaging(payload, probe, new ExtractionLimits()));
        Assert.Equal(ExtractionFailureKind.UnsafePath, failure.Kind);
    }

    // ----- Names Windows can't keep apart -----

    [RequiresSevenZipFact]
    public async Task EntriesDifferingOnlyByCaseAreRefusedRatherThanOneOverwritingTheOther()
    {
        var archive = Space.WriteArchive("cases.rar", RarBuilder.Rar5(("a.txt", Text), ("A.TXT", Other)));
        Assert.Equal(ExtractionFailureKind.UnsafePath, await FailureOf(Request(archive)));
        Assert.True(File.Exists(archive));
    }

    [RequiresSevenZipFact]
    public async Task AFileAndAFolderThatCollapseToOneNameAreRefused()
    {
        var archive = Space.WriteArchive("mixed.rar", RarBuilder.Rar5(("thing", Text), ("THING/inner.txt", Other)));
        Assert.Equal(ExtractionFailureKind.UnsafePath, await FailureOf(Request(archive)));
    }

    [RequiresSevenZipFact]
    public async Task TheSameNameTwiceInOneVolumeIsRefusedRatherThanMerged()
    {
        var archive = Space.WriteArchive("twice.rar", RarBuilder.Rar5(("a.txt", Text), ("a.txt", Other)));
        Assert.Equal(ExtractionFailureKind.UnsafePath, await FailureOf(Request(archive)));
    }

    [RequiresSevenZipFact]
    public async Task FoldersDifferingOnlyByCaseAreOneFolderAndAreFine()
    {
        var archive = Space.WriteArchive("folders.rar", RarBuilder.Rar5(("Dir/a.txt", Text), ("DIR/b.txt", Other)));

        await Extractor().ExtractAsync(Request(archive), null, CancellationToken.None);

        Assert.Equal(Text, File.ReadAllBytes(Space.Output(@"Dir\a.txt")));
        Assert.Equal(Other, File.ReadAllBytes(Space.Output(@"Dir\b.txt")));
    }

    [Fact]
    public void OneFileAnsweringForTwoListedNamesIsAShortfall()
    {
        var payload = Directory.CreateDirectory(Path.Combine(Space.Root, "payload")).FullName;
        File.WriteAllBytes(Path.Combine(payload, "a.txt"), Text);
        var probe = new ArchiveProbe
        {
            FirstVolume = Space.Archive("x.rar"),
            IsEncrypted = false,
            HeadersEncrypted = false,
            Entries = [new ArchiveEntry("a.txt", Text.Length, ArchiveEntryKind.File), new ArchiveEntry("A.TXT", Text.Length, ArchiveEntryKind.File)],
            Version = "5",
            IsVolume = false,
            AllVolumesPresent = true,
        };

        var failure = Assert.Throws<ExtractionException>(() => ExtractionSafety.WalkStaging(payload, probe, new ExtractionLimits()));
        Assert.Equal(ExtractionFailureKind.Corrupted, failure.Kind);
    }

    // ----- What a header says against what a name suggests -----

    [Fact]
    public void AHeaderSaysWhetherAFileIsAVolume()
    {
        Assert.Equal(RarVolumeHeaderKind.NotAVolume, RarVolumeHeader.Parse(RarBuilder.Rar5(("a.txt", Text))));
        Assert.Equal(RarVolumeHeaderKind.NotAVolume, RarVolumeHeader.Parse(RarBuilder.Rar4(("a.txt", Text))));
        Assert.All(RarBuilder.Rar4Volumes("a.bin", new byte[5000], 2, newNumbering: true), volume => Assert.Equal(RarVolumeHeaderKind.Volume, RarVolumeHeader.Parse(volume)));
        // RAR4 leaves the main header readable even with the rest encrypted.
        Assert.Equal(RarVolumeHeaderKind.NotAVolume, RarVolumeHeader.Parse(RarBuilder.Rar4HeaderEncrypted("a.txt", Text, RarTools.Password)));
        Assert.Equal(RarVolumeHeaderKind.Unknown, RarVolumeHeader.Parse("not an archive"u8));
        Assert.Equal(RarVolumeHeaderKind.Unknown, RarVolumeHeader.Parse([]));
        Assert.Equal(RarVolumeHeaderKind.Unknown, RarVolumeHeader.Read(Space.Archive("not-there.rar")));
    }

    [RequiresRarFact]
    public async Task RealFilesFromRarReadTheSameWay()
    {
        Space.WriteSource("big.bin", Workspace.RandomBytes(300 * 1024, 5));
        var plain = await Space.BuildAsync("plain.rar");
        Assert.Equal(RarVolumeHeaderKind.NotAVolume, RarVolumeHeader.Read(plain));

        await Space.BuildAsync("set.rar", "-v100k");
        Assert.Equal(RarVolumeHeaderKind.Volume, RarVolumeHeader.Read(Space.Archive("set.part1.rar")));
        Assert.Equal(RarVolumeHeaderKind.Volume, RarVolumeHeader.Read(Space.Archive("set.part2.rar")));

        var hidden = await Space.BuildAsync("hidden.rar", "-hp" + RarTools.Password);
        Assert.Equal(RarVolumeHeaderKind.Unknown, RarVolumeHeader.Read(hidden));
    }

    [Fact]
    public void AWholeArchiveWithANumberedNameIsItsOwnSet()
    {
        var names = new[] { "report.001.rar", "report.002.rar", "lecture.part1.rar", "lecture.part2.rar", "real.part1.rar", "real.part2.rar" };
        var whole = new HashSet<string> { "report.001.rar", "report.002.rar", "lecture.part1.rar", "lecture.part2.rar" };

        var sets = ArchiveVolumes.Group(names, whole.Contains);

        Assert.Equal(5, sets.Count);
        Assert.Equal(["real.part1.rar", "real.part2.rar"], sets.Single(set => set.Scheme == ArchiveVolumeScheme.PartNumbered).Members);
        Assert.Equal(4, sets.Count(set => set.Scheme == ArchiveVolumeScheme.Single));

        // With no way to look, names decide as they always did.
        Assert.Equal(3, ArchiveVolumes.Group(names).Count);
    }

    [RequiresRarFact]
    public async Task TheResultNamesOnlyTheVolumesTheToolRead()
    {
        Space.WriteSource("big.bin", Workspace.RandomBytes(250 * 1024, 6));
        await Space.BuildAsync("set.rar", "-v100k");
        var parts = Directory.EnumerateFiles(Space.Archives, "set.part*.rar").Order(StringComparer.OrdinalIgnoreCase).ToList();
        Assert.True(parts.Count >= 2);

        // A whole archive and a volume of some other set, both with the next number.
        File.Copy(parts[1], Space.Archive($"set.part{parts.Count + 1}.rar"));
        File.WriteAllBytes(Space.Archive($"set.part{parts.Count + 2}.rar"), RarBuilder.Rar5(("other.txt", Other)));

        var result = await Extractor().ExtractAsync(Request(parts[0]), null, CancellationToken.None);

        Assert.Equal(parts, result.VolumesRead);
        Assert.False(File.Exists(Space.Output("other.txt")));
    }

    // ----- A rollback that meets a read-only file, or one somebody has open -----

    private byte[] TwoReadOnlyFiles() => RarBuilder.Rar5Raw(RarBuilder.Rar5File("a.txt", Text, attributes: 0x21), RarBuilder.Rar5File("b.txt", Text, attributes: 0x21));

    [RequiresSevenZipFact]
    public async Task ARollbackRemovesReadOnlyFilesToo()
    {
        var archive = Space.WriteArchive("ro.rar", TwoReadOnlyFiles());
        using var cancel = new CancellationTokenSource();
        var progress = new ImmediateProgress<ExtractionProgress>(report =>
        {
            if (report is { Phase: ExtractionPhase.Finalizing, FilesFinalized: 1 }) cancel.Cancel();
        });

        var failure = await Assert.ThrowsAsync<ExtractionException>(() => Extractor().ExtractAsync(Request(archive), progress, cancel.Token));

        Assert.Equal(ExtractionFailureKind.Cancelled, failure.Kind);
        Space.AssertDestinationUntouched();
        Space.AssertNoStagingLeft();
    }

    [RequiresSevenZipFact]
    public async Task AFileARollbackCouldNotRemoveIsFinishedByTheNextAttempt()
    {
        var archive = Space.WriteArchive("ro.rar", TwoReadOnlyFiles());
        using var cancel = new CancellationTokenSource();
        FileStream? scanner = null;
        var progress = new ImmediateProgress<ExtractionProgress>(report =>
        {
            if (report is { Phase: ExtractionPhase.Finalizing, FilesFinalized: 1 })
            {
                scanner = new FileStream(Space.Output("a.txt"), FileMode.Open, FileAccess.Read, FileShare.Read);
                cancel.Cancel();
            }
        });

        await Assert.ThrowsAsync<ExtractionException>(() => Extractor().ExtractAsync(Request(archive), progress, cancel.Token));

        // The contract's one exception: the file is held, so it stays, and the record of it stays with it.
        Assert.True(File.Exists(Space.Output("a.txt")));
        Assert.Single(StagingArea.FindLeftovers(Space.Staging));
        scanner!.Dispose();

        await Extractor().ExtractAsync(Request(archive), null, CancellationToken.None);

        Assert.Equal(["a.txt", "b.txt"], Directory.EnumerateFiles(Space.Destination).Select(file => Path.GetFileName(file)!).Order());
        Space.AssertNoStagingLeft();
    }

    // ----- A file somebody changed after a crash -----

    [RequiresSevenZipFact]
    public async Task AFileEditedAfterTheCrashSurvivesRecovery()
    {
        var archive = Space.WriteArchive("three.rar", RarBuilder.Rar5(("a.txt", Text), ("b.txt", Text), ("c.txt", Text)));
        var saved = Path.Combine(Space.Root, "saved");
        var progress = new ImmediateProgress<ExtractionProgress>(report =>
        {
            if (report is not { Phase: ExtractionPhase.Finalizing, FilesFinalized: 2 }) return;
            foreach (var file in Directory.EnumerateFiles(Space.Root, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(saved, Path.GetRelativePath(Space.Root, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
        });

        await Extractor().ExtractAsync(Request(archive), progress, CancellationToken.None);
        foreach (var folder in new[] { Space.Destination, Space.Staging }) Directory.Delete(folder, recursive: true);
        foreach (var file in Directory.EnumerateFiles(saved, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(Space.Root, Path.GetRelativePath(saved, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }

        File.WriteAllText(Space.Output("a.txt"), "edited by the user afterwards");

        var recovery = await StagingArea.RecoverAndRemoveAsync(Space.Staging, archive, Space.Destination);

        Assert.True(recovery.Removed);
        Assert.Equal(1, recovery.LeftInPlace);
        Assert.Equal("edited by the user afterwards", File.ReadAllText(Space.Output("a.txt")));
        Assert.False(File.Exists(Space.Output("b.txt")));
    }

    // ----- A destination at the root of a drive -----

    [Fact]
    public void APathInsideADriveRootCountsAsInside()
    {
        Assert.Equal(@"D:\", ExtractionSafety.WithSeparator(@"D:\"));
        Assert.Equal(@"D:\out\", ExtractionSafety.WithSeparator(@"D:\out"));
        Assert.StartsWith(ExtractionSafety.WithSeparator(@"D:\"), @"D:\file.txt", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A real drive root, by way of a substituted drive letter that points at this test's own folder.</summary>
    [RequiresSevenZipFact]
    public async Task ExtractionAndItsCrashUndoWorkAtADriveRoot()
    {
        var used = DriveInfo.GetDrives().Select(drive => char.ToUpperInvariant(drive.Name[0])).ToHashSet();
        var letter = Enumerable.Range('G', 20).Select(code => (char)code).Reverse().First(candidate => !used.Contains(candidate));
        var folder = Directory.CreateDirectory(Path.Combine(Space.Root, "drive")).FullName;
        var drive = $"{letter}:";
        if (!await SubstAsync(drive, folder) || !Directory.Exists(drive + @"\")) return;

        try
        {
            var root = drive + @"\";
            var archive = Space.WriteArchive("root.rar", RarBuilder.Rar5(("a.txt", Text), ("sub/b.txt", Other)));
            var request = new ExtractionRequest { FirstVolume = archive, Destination = root, StagingRoot = root };

            var result = await Extractor().ExtractAsync(request, null, CancellationToken.None);
            Assert.Equal(2, result.Files.Count);
            Assert.Equal(Text, File.ReadAllBytes(Path.Combine(folder, "a.txt")));
            Assert.Equal(Other, File.ReadAllBytes(Path.Combine(folder, "sub", "b.txt")));

            // An interrupted attempt's log is honoured at a drive root as well.
            var stage = StagingArea.FolderFor(root, archive, root);
            Directory.CreateDirectory(stage);
            var info = new FileInfo(Path.Combine(root, "a.txt"));
            var line = System.Text.Json.JsonSerializer.Serialize(new { Source = "a.txt", Target = info.FullName, DisplacedAs = (string?)null, info.Length, LastWriteTicks = info.LastWriteTimeUtc.Ticks });
            File.WriteAllText(Path.Combine(stage, "placed.log"), line + "\n");

            Assert.True((await StagingArea.RecoverAndRemoveAsync(root, archive, root)).Removed);
            Assert.False(File.Exists(Path.Combine(folder, "a.txt")));
        }
        finally
        {
            await SubstAsync(drive, null);
        }
    }

    private static async Task<bool> SubstAsync(string drive, string? folder)
    {
        var info = new ProcessStartInfo("subst.exe") { UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add(drive);
        if (folder is null) info.ArgumentList.Add("/D");
        else info.ArgumentList.Add(folder);
        using var process = Process.Start(info)!;
        await process.WaitForExitAsync();
        return process.ExitCode == 0;
    }

    // ----- Hidden data streams -----

    [RequiresRarFact]
    public async Task AHiddenStreamInAnArchiveStaysBehind()
    {
        var file = Space.WriteSource("s.txt", "visible");
        File.WriteAllText(file + ":hidden", "not in any listing");
        var archive = await Space.BuildAsync("streams.rar", "-os");

        await Extractor().ExtractAsync(Request(archive), null, CancellationToken.None);

        var extracted = Path.Combine(Space.Destination, "s.txt");
        Assert.Equal("visible", File.ReadAllText(extracted));
        Assert.False(File.Exists(extracted + ":hidden"));
        Space.AssertNoStagingLeft();
    }

    [RequiresRarFact]
    public async Task AZoneMarkStoredInAnUnmarkedArchiveStaysBehind()
    {
        var file = Space.WriteSource("s.txt", "visible");
        File.WriteAllText(file + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=2\r\n");
        var archive = await Space.BuildAsync("zone.rar", "-os");

        await Extractor().ExtractAsync(Request(archive), null, CancellationToken.None);

        Assert.False(File.Exists(Path.Combine(Space.Destination, "s.txt") + ":Zone.Identifier"));
    }

    [RequiresRarFact]
    public async Task AMarkedArchiveMarksItsFilesWithItsOwnZoneAndNotTheStoredOne()
    {
        var file = Space.WriteSource("s.txt", "visible");
        File.WriteAllText(file + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=2\r\n");
        var archive = await Space.BuildAsync("zone.rar", "-os");
        File.WriteAllText(archive + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=https://example.invalid/zone.rar\r\n");

        await Extractor().ExtractAsync(Request(archive), null, CancellationToken.None);

        var mark = File.ReadAllText(Path.Combine(Space.Destination, "s.txt") + ":Zone.Identifier");
        Assert.Contains("ZoneId=3", mark);
        Assert.DoesNotContain("example.invalid", mark);
    }

    [RequiresSevenZipFact]
    public async Task AnOrdinaryArchiveIsNotTakenForOneHoldingStreams()
    {
        var archive = Space.WriteArchive("plain.rar", RarBuilder.Rar5(("a.txt", Text)));
        Assert.False((await Extractor().ProbeAsync(archive, null, CancellationToken.None)).HoldsDataStreams);
    }

    private ArchiveProbe ProbeListing(string name) => new()
    {
        FirstVolume = Space.Archive("marked.rar"),
        IsEncrypted = false,
        HeadersEncrypted = false,
        Entries = [new ArchiveEntry(name, Text.Length, ArchiveEntryKind.File)],
        Version = "5",
        IsVolume = false,
        AllVolumesPresent = true,
    };

    [Fact]
    public void AWalkStillFindsAStreamTheListingDidNotAdmitTo()
    {
        var payload = Directory.CreateDirectory(Path.Combine(Space.Root, "payload")).FullName;
        var file = Path.Combine(payload, "a.txt");
        File.WriteAllBytes(file, Text);
        File.WriteAllText(file + ":hidden", "not in any listing");

        var failure = Assert.Throws<ExtractionException>(() => ExtractionSafety.WalkStaging(payload, ProbeListing("a.txt"), new ExtractionLimits()));

        Assert.Equal(ExtractionFailureKind.UnsafePath, failure.Kind);
        Assert.Contains("hidden data stream", failure.Message);
    }

    [Fact]
    public void AZoneMarkPassesAWalkOnlyWhenTheArchiveHasOneForTheToolToHaveCopied()
    {
        var payload = Directory.CreateDirectory(Path.Combine(Space.Root, "payload")).FullName;
        var file = Path.Combine(payload, "a.txt");
        File.WriteAllBytes(file, Text);
        File.WriteAllText(file + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
        File.WriteAllBytes(Space.Archive("marked.rar"), RarBuilder.Rar5(("a.txt", Text)));

        Assert.Throws<ExtractionException>(() => ExtractionSafety.WalkStaging(payload, ProbeListing("a.txt"), new ExtractionLimits()));

        File.WriteAllText(Space.Archive("marked.rar") + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
        Assert.Equal(["a.txt"], ExtractionSafety.WalkStaging(payload, ProbeListing("a.txt"), new ExtractionLimits()).Files);
    }

    // ----- Durable before the archives can go -----

    [RequiresSevenZipFact]
    public async Task AskingForDurabilityFlushesEveryFileAndKeepsReadOnlyFilesReadOnly()
    {
        var archive = Space.WriteArchive("ro.rar", TwoReadOnlyFiles());
        var request = new ExtractionRequest { FirstVolume = archive, Destination = Space.Destination, StagingRoot = Space.Staging, MakeDurable = true };

        var result = await Extractor().ExtractAsync(request, null, CancellationToken.None);

        Assert.True(result.FlushedToDisk);
        Assert.True(new FileInfo(Space.Output("a.txt")).IsReadOnly);
        Assert.Equal(Text, File.ReadAllBytes(Space.Output("a.txt")));
    }

    [RequiresSevenZipFact]
    public async Task AFlushThatFailsLeavesTheOutputButSaysSo()
    {
        var archive = Space.WriteArchive("one.rar", RarBuilder.Rar5(("a.txt", Text)));
        var request = new ExtractionRequest { FirstVolume = archive, Destination = Space.Destination, StagingRoot = Space.Staging, MakeDurable = true };
        var options = new SevenZipExtractorOptions { FlushOverride = _ => throw new IOException("the disk said no") };

        var result = await Extractor(options).ExtractAsync(request, null, CancellationToken.None);

        Assert.False(result.FlushedToDisk);
        Assert.Equal(Text, File.ReadAllBytes(Space.Output("a.txt")));
    }

    [RequiresSevenZipFact]
    public async Task NoFlushIsSpentWhenNobodyIsGoingToDeleteAnything()
    {
        var archive = Space.WriteArchive("one.rar", RarBuilder.Rar5(("a.txt", Text)));
        var flushes = 0;
        var options = new SevenZipExtractorOptions { FlushOverride = _ => Interlocked.Increment(ref flushes) };

        var result = await Extractor(options).ExtractAsync(Request(archive), null, CancellationToken.None);

        Assert.Equal(0, flushes);
        Assert.False(result.FlushedToDisk);
    }
}
