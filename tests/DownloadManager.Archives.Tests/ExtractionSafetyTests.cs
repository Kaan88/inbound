using System.Runtime.InteropServices;
using DownloadManager.Archives;
using DownloadManager.Core.Archives;

namespace DownloadManager.Archives.Tests;

public sealed class EntryPathTests
{
    [Theory]
    [InlineData("a.txt")]
    [InlineData(@"sub\deep\a.txt")]
    [InlineData("sub/deep/a.txt")]
    [InlineData("name with spaces.txt")]
    [InlineData("..hidden")]
    [InlineData("a..b.txt")]
    [InlineData("console.txt")]
    [InlineData("COM10.txt")]
    [InlineData("日本語\\ファイル.bin")]
    [InlineData("C_")]
    [InlineData("xy_\\a.txt")]
    public void AcceptsOrdinaryNames(string path)
    {
        // A backslash separates folders only on Windows. On Linux it would be part of one name, which is refused.
        if (!OperatingSystem.IsWindows() && path.Contains('\\'))
            Assert.Equal("backslash in name", ExtractionSafety.DescribeUnsafePath(path));
        else
            Assert.Null(ExtractionSafety.DescribeUnsafePath(path));
    }

    [Theory]
    [InlineData("sub/deep/a.txt")]
    [InlineData("日本語/ファイル.bin")]
    public void ForwardSlashFoldersAreFineEverywhere(string path)
    {
        Assert.Null(ExtractionSafety.DescribeUnsafePath(path));
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData(@"..\up.txt", "traversal")]
    [InlineData("../up.txt", "traversal")]
    [InlineData(@"sub\..\..\up.txt", "traversal")]
    [InlineData(@"sub\..", "traversal")]
    [InlineData(@".\a.txt", "traversal")]
    [InlineData(@"\rooted.txt", "rooted")]
    [InlineData("/rooted.txt", "rooted")]
    [InlineData(@"\\server\share\a.txt", "rooted")]
    [InlineData(@"\\?\C:\a.txt", "rooted")]
    [InlineData(@"\\.\PhysicalDrive0", "rooted")]
    [InlineData(@"C:\Windows\a.txt", "drive")]
    [InlineData("C:drive-relative.txt", "drive")]
    [InlineData(@"C_\drive-relative.txt", "drive")]
    [InlineData("a.txt:stream", "stream")]
    [InlineData("CON", "device")]
    [InlineData("con.txt", "device")]
    [InlineData(@"sub\NUL", "device")]
    [InlineData("AUX.tar.gz", "device")]
    [InlineData("PRN .txt", "device")]
    [InlineData("COM1", "device")]
    [InlineData("LPT9.log", "device")]
    [InlineData("COM¹", "device")]
    [InlineData("CONIN$", "device")]
    [InlineData("CONOUT$", "device")]
    [InlineData("trailing.", "dot or space")]
    [InlineData("trailing ", "dot or space")]
    [InlineData(@"folder.\a.txt", "dot or space")]
    [InlineData("a\tb.txt", "control")]
    [InlineData("a\nb.txt", "control")]
    [InlineData("a\u0001b.txt", "control")]
    [InlineData("a?b.txt", "allow")]
    [InlineData("a*b.txt", "allow")]
    [InlineData("a|b.txt", "allow")]
    [InlineData(@"a\\b.txt", "empty folder")]
    public void RefusesUnsafeNames(string path, string reasonPart)
    {
        var reason = ExtractionSafety.DescribeUnsafePath(path);
        Assert.NotNull(reason);
        Assert.Contains(reasonPart, reason);
    }

    [Fact]
    public void DisplayNeutralisesBidiAndControlCharacters()
    {
        Assert.Equal("a_b_c_d", ExtractionSafety.SafeForDisplay("a\u202Eb\nc\u2067d"));
    }
}

public sealed class ListingValidationTests
{
    private static ArchiveProbe Probe(params ArchiveEntry[] entries) => new()
    {
        FirstVolume = "x.rar",
        IsEncrypted = false,
        HeadersEncrypted = false,
        Entries = entries,
        Version = "5",
        IsVolume = false,
        AllVolumesPresent = true,
    };

    private static ExtractionFailureKind Refusal(ArchiveProbe probe, ExtractionLimits? limits = null) =>
        Assert.Throws<ExtractionException>(() => ExtractionSafety.ValidateListing(probe, limits ?? new ExtractionLimits())).Kind;

    [Fact]
    public void AcceptsFilesAndFolders()
    {
        ExtractionSafety.ValidateListing(Probe(new ArchiveEntry("a.txt", 1, ArchiveEntryKind.File), new("sub", 0, ArchiveEntryKind.Directory)), new ExtractionLimits());
    }

    [Theory]
    [InlineData(ArchiveEntryKind.SymbolicLink)]
    [InlineData(ArchiveEntryKind.Junction)]
    [InlineData(ArchiveEntryKind.HardLink)]
    [InlineData(ArchiveEntryKind.Other)]
    public void RefusesAnythingButFilesAndFolders(ArchiveEntryKind kind)
    {
        Assert.Equal(ExtractionFailureKind.UnsafePath, Refusal(Probe(new ArchiveEntry("link", 0, kind))));
    }

    [Fact]
    public void RefusesAnUnsafePath()
    {
        Assert.Equal(ExtractionFailureKind.UnsafePath, Refusal(Probe(new ArchiveEntry(@"..\a.txt", 1, ArchiveEntryKind.File))));
    }

    [Fact]
    public void RefusesAListingWithAnUnknownLine()
    {
        Assert.Equal(ExtractionFailureKind.UnsafePath, Refusal(Probe(new ArchiveEntry("a.txt", 1, ArchiveEntryKind.File)) with { ListingHasUnknownLines = true }));
    }

    [Fact]
    public void RefusesOverTheEntryCap()
    {
        var probe = Probe(new ArchiveEntry("a.txt", 1, ArchiveEntryKind.File), new("b.txt", 1, ArchiveEntryKind.File), new("c.txt", 1, ArchiveEntryKind.File));
        Assert.Equal(ExtractionFailureKind.UnsafePath, Refusal(probe, new ExtractionLimits { MaxEntries = 2 }));
        ExtractionSafety.ValidateListing(probe, new ExtractionLimits { MaxEntries = 3 });
    }

    [Fact]
    public void RefusesOverTheSizeCap()
    {
        var probe = Probe(new ArchiveEntry("a.txt", 600, ArchiveEntryKind.File), new("b.txt", 500, ArchiveEntryKind.File));
        Assert.Equal(ExtractionFailureKind.DiskSpace, Refusal(probe, new ExtractionLimits { MaxTotalBytes = 1000 }));
        ExtractionSafety.ValidateListing(probe, new ExtractionLimits { MaxTotalBytes = 1100 });
    }

    [Fact]
    public void FreeSpaceCheckIncludesTheMargin()
    {
        var limits = new ExtractionLimits { FreeSpaceMarginBytes = 100, FreeSpaceMarginFraction = 0.1 };
        Assert.Equal(1200, ExtractionSafety.RequiredFreeBytes(1000, limits));

        ExtractionSafety.CheckFreeSpace(1000, 1200, limits);
        ExtractionSafety.CheckFreeSpace(1000, null, limits);
        var failure = Assert.Throws<ExtractionException>(() => ExtractionSafety.CheckFreeSpace(1000, 1199, limits));
        Assert.Equal(ExtractionFailureKind.DiskSpace, failure.Kind);
    }
}

public sealed class StagingWalkTests : IDisposable
{
    private readonly Workspace _space = new();

    private string Payload => _space.Source;

    public void Dispose() => _space.Dispose();

    private static ArchiveProbe Probe(params ArchiveEntry[] entries) => new()
    {
        FirstVolume = "x.rar",
        IsEncrypted = false,
        HeadersEncrypted = false,
        Entries = entries,
        Version = "5",
        IsVolume = false,
        AllVolumesPresent = true,
    };

    private ExtractionException Refused(ArchiveProbe probe, ExtractionLimits? limits = null) =>
        Assert.Throws<ExtractionException>(() => ExtractionSafety.WalkStaging(Payload, probe, limits ?? new ExtractionLimits()));

    [Fact]
    public void ReturnsWhatWasListed()
    {
        _space.WriteSource(@"sub\a.txt", "aa");
        Directory.CreateDirectory(Path.Combine(Payload, "empty"));

        var content = ExtractionSafety.WalkStaging(
            Payload,
            Probe(new ArchiveEntry(@"sub\a.txt", 2, ArchiveEntryKind.File), new("empty", 0, ArchiveEntryKind.Directory)),
            new ExtractionLimits());

        Assert.Equal([@"sub\a.txt"], content.Files);
        Assert.Equal(["empty", "sub"], content.Directories);
    }

    [Fact]
    public void RefusesAFileTheListingDidNotName()
    {
        _space.WriteSource("a.txt", "a");
        _space.WriteSource("evil.txt", "e");
        var failure = Refused(Probe(new ArchiveEntry("a.txt", 1, ArchiveEntryKind.File)));
        Assert.Equal(ExtractionFailureKind.UnsafePath, failure.Kind);
    }

    [Fact]
    public void RefusesAFolderTheListingDidNotName()
    {
        _space.WriteSource("a.txt", "a");
        Directory.CreateDirectory(Path.Combine(Payload, "surprise"));
        Assert.Equal(ExtractionFailureKind.UnsafePath, Refused(Probe(new ArchiveEntry("a.txt", 1, ArchiveEntryKind.File))).Kind);
    }

    [Fact]
    public void RefusesMoreBytesThanListed()
    {
        _space.WriteSource("a.txt", "much more than one byte");
        Assert.Equal(ExtractionFailureKind.UnsafePath, Refused(Probe(new ArchiveEntry("a.txt", 1, ArchiveEntryKind.File))).Kind);
    }

    [Fact]
    public void AListedFileThatDidNotAppearIsCorrupted()
    {
        _space.WriteSource("a.txt", "a");
        var failure = Refused(Probe(new ArchiveEntry("a.txt", 1, ArchiveEntryKind.File), new("b.txt", 1, ArchiveEntryKind.File)));
        Assert.Equal(ExtractionFailureKind.Corrupted, failure.Kind);
    }

    [Fact]
    public void RefusesAHardLink()
    {
        var target = _space.WriteSource("a.txt", "a");
        Assert.True(CreateHardLink(Path.Combine(Payload, "b.txt"), target, IntPtr.Zero));
        var failure = Refused(Probe(new ArchiveEntry("a.txt", 1, ArchiveEntryKind.File), new("b.txt", 1, ArchiveEntryKind.File)));
        Assert.Equal(ExtractionFailureKind.UnsafePath, failure.Kind);
        Assert.Contains("hard link", failure.Message);
    }

    [Fact]
    public void RefusesAJunction()
    {
        _space.WriteSource("a.txt", "a");
        var outside = Directory.CreateDirectory(Path.Combine(_space.Root, "outside")).FullName;
        var link = Path.Combine(Payload, "sub");
        MakeJunction(link, outside);

        var failure = Refused(Probe(new ArchiveEntry("a.txt", 1, ArchiveEntryKind.File), new("sub", 0, ArchiveEntryKind.Directory)));

        Assert.Equal(ExtractionFailureKind.UnsafePath, failure.Kind);
        Assert.Contains("link", failure.Message);
    }

    internal static void MakeJunction(string link, string target)
    {
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var (code, output) = RarTools.RunAsync(cmd, Environment.SystemDirectory, "/d", "/c", "mklink", "/J", link, target).GetAwaiter().GetResult();
        Assert.True(code == 0, output);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string newFile, string existingFile, IntPtr security);
}

public sealed class FinalizerTests : IDisposable
{
    private readonly Workspace _space = new();

    public void Dispose() => _space.Dispose();

    private async Task<StagingArea> StagingWithAsync(params (string Relative, string Text)[] files)
    {
        var staging = await StagingArea.PrepareAsync(_space.Staging, _space.Archive("x.rar"), _space.Destination, CancellationToken.None);
        foreach (var (relative, text) in files)
        {
            var path = Path.Combine(staging.PayloadFolder, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }

        return staging;
    }

    private void Existing(string relative, string text)
    {
        var path = _space.Output(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public async Task WritesNewFilesAndEmptyFolders()
    {
        var staging = await StagingWithAsync((@"sub\a.txt", "new"));
        Directory.CreateDirectory(Path.Combine(staging.PayloadFolder, "empty"));

        var results = ExtractionFinalizer.MoveIntoDestination(
            staging, new StagedContent(["empty", "sub"], [@"sub\a.txt"]), _space.Destination, ExtractionConflictPolicy.Overwrite, true, null, CancellationToken.None);

        Assert.Equal(ExtractedFileOutcome.Written, Assert.Single(results).Outcome);
        Assert.Equal("new", File.ReadAllText(_space.Output(@"sub\a.txt")));
        Assert.True(Directory.Exists(_space.Output("empty")));
    }

    [Fact]
    public async Task OverwriteReplaces()
    {
        Existing("a.txt", "old");
        var staging = await StagingWithAsync(("a.txt", "new"));

        var results = ExtractionFinalizer.MoveIntoDestination(
            staging, new StagedContent([], ["a.txt"]), _space.Destination, ExtractionConflictPolicy.Overwrite, true, null, CancellationToken.None);

        Assert.Equal(ExtractedFileOutcome.Overwrote, Assert.Single(results).Outcome);
        Assert.Equal("new", File.ReadAllText(_space.Output("a.txt")));
    }

    [Fact]
    public async Task RenameKeepsBoth()
    {
        Existing("a.txt", "old");
        Existing("a (1).txt", "older");
        var staging = await StagingWithAsync(("a.txt", "new"));

        var results = ExtractionFinalizer.MoveIntoDestination(
            staging, new StagedContent([], ["a.txt"]), _space.Destination, ExtractionConflictPolicy.Rename, true, null, CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Equal(ExtractedFileOutcome.Renamed, result.Outcome);
        Assert.Equal(_space.Output("a (2).txt"), result.FinalPath);
        Assert.Equal("old", File.ReadAllText(_space.Output("a.txt")));
        Assert.Equal("new", File.ReadAllText(result.FinalPath));
    }

    [Fact]
    public async Task SkipLeavesTheExistingFile()
    {
        Existing("a.txt", "old");
        var staging = await StagingWithAsync(("a.txt", "new"));

        var results = ExtractionFinalizer.MoveIntoDestination(
            staging, new StagedContent([], ["a.txt"]), _space.Destination, ExtractionConflictPolicy.Skip, true, null, CancellationToken.None);

        Assert.Equal(ExtractedFileOutcome.Skipped, Assert.Single(results).Outcome);
        Assert.Equal("old", File.ReadAllText(_space.Output("a.txt")));
    }

    [Theory]
    [InlineData(ExtractionConflictPolicy.Overwrite)]
    [InlineData(ExtractionConflictPolicy.Skip)]
    [InlineData(ExtractionConflictPolicy.Rename)]
    public async Task FlatteningNeverLosesAFileFromThisArchive(ExtractionConflictPolicy policy)
    {
        var staging = await StagingWithAsync((@"one\a.txt", "1"), (@"two\a.txt", "2"));

        var results = ExtractionFinalizer.MoveIntoDestination(
            staging, new StagedContent(["one", "two"], [@"one\a.txt", @"two\a.txt"]), _space.Destination, policy, false, null, CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.Equal("1", File.ReadAllText(_space.Output("a.txt")));
        Assert.Equal("2", File.ReadAllText(_space.Output("a (1).txt")));
    }

    [Fact]
    public async Task FailurePartWayPutsEverythingBack()
    {
        Existing("1-first.txt", "old first");
        Existing("3-blocked", "ignored");
        File.Delete(_space.Output("3-blocked"));
        Directory.CreateDirectory(_space.Output("3-blocked"));
        var staging = await StagingWithAsync(("1-first.txt", "new first"), (@"2-sub\second.txt", "new second"), ("3-blocked", "cannot land"));

        var failure = Assert.Throws<ExtractionException>(() => ExtractionFinalizer.MoveIntoDestination(
            staging, new StagedContent(["2-sub"], ["1-first.txt", @"2-sub\second.txt", "3-blocked"]), _space.Destination, ExtractionConflictPolicy.Overwrite, true, null, CancellationToken.None));

        Assert.Equal(ExtractionFailureKind.Permission, failure.Kind);
        Assert.Equal("old first", File.ReadAllText(_space.Output("1-first.txt")));
        Assert.False(Directory.Exists(_space.Output("2-sub")));
        Assert.True(Directory.Exists(_space.Output("3-blocked")));
    }

    [Fact]
    public async Task CancelPartWayPutsEverythingBack()
    {
        Existing("a.txt", "old");
        var staging = await StagingWithAsync(("a.txt", "new"), ("b.txt", "new"));
        using var cancellation = new CancellationTokenSource();
        var progress = new ImmediateProgress<ExtractionProgress>(_ => cancellation.Cancel());

        Assert.Throws<OperationCanceledException>(() => ExtractionFinalizer.MoveIntoDestination(
            staging, new StagedContent([], ["a.txt", "b.txt"]), _space.Destination, ExtractionConflictPolicy.Overwrite, true, progress, cancellation.Token));

        Assert.Equal("old", File.ReadAllText(_space.Output("a.txt")));
        Assert.False(File.Exists(_space.Output("b.txt")));
    }

    [Fact]
    public async Task AJunctionInTheDestinationIsRefused()
    {
        var outside = Directory.CreateDirectory(Path.Combine(_space.Root, "outside")).FullName;
        Directory.CreateDirectory(_space.Destination);
        StagingWalkTests.MakeJunction(_space.Output("sub"), outside);
        var staging = await StagingWithAsync((@"sub\a.txt", "new"));

        var failure = Assert.Throws<ExtractionException>(() => ExtractionFinalizer.MoveIntoDestination(
            staging, new StagedContent(["sub"], [@"sub\a.txt"]), _space.Destination, ExtractionConflictPolicy.Overwrite, true, null, CancellationToken.None));

        Assert.Equal(ExtractionFailureKind.UnsafePath, failure.Kind);
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
    }
}

public sealed class StagingAreaTests : IDisposable
{
    private readonly Workspace _space = new();

    public void Dispose() => _space.Dispose();

    private Task<StagingArea> PrepareAsync() =>
        StagingArea.PrepareAsync(_space.Staging, _space.Archive("x.rar"), _space.Destination, CancellationToken.None);

    [Fact]
    public async Task PreparingWipesAnEarlierAttempt()
    {
        var first = await PrepareAsync();
        var leftover = Path.Combine(first.PayloadFolder, "partial.bin");
        File.WriteAllText(leftover, "x");
        File.SetAttributes(leftover, FileAttributes.ReadOnly);

        var second = await PrepareAsync();

        Assert.Equal(first.Folder, second.Folder);
        Assert.False(File.Exists(leftover));
        Assert.True(Directory.Exists(second.PayloadFolder));
    }

    [Fact]
    public async Task ALeftoverIsFoundByTheArchiveAndDestinationItIsFor()
    {
        var area = await PrepareAsync();

        var leftover = Assert.Single(StagingArea.FindLeftovers(_space.Staging));

        Assert.Equal(area.Folder, leftover);
        Assert.Equal(area.Folder, StagingArea.FolderFor(_space.Staging, _space.Archive("x.rar"), _space.Destination));
        Assert.True((await StagingArea.RecoverAndRemoveAsync(_space.Staging, _space.Archive("x.rar"), _space.Destination)).Removed);
        Assert.Empty(StagingArea.FindLeftovers(_space.Staging));
    }

    [Fact]
    public async Task ANextAttemptRestoresAnOriginalACrashLeftDisplaced()
    {
        var area = await PrepareAsync();
        var displaced = Path.Combine(area.DisplacedFolder, @"sub\a.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(displaced)!);
        File.WriteAllText(displaced, "the only copy");

        await PrepareAsync();

        Assert.Equal("the only copy", File.ReadAllText(_space.Output(@"sub\a.txt")));
    }

    [Fact]
    public async Task AnOriginalWhosePlaceIsTakenGoesInBesideItAndIsNeverLost()
    {
        var area = await PrepareAsync();
        var displaced = Path.Combine(area.DisplacedFolder, "a.txt");
        Directory.CreateDirectory(area.DisplacedFolder);
        File.WriteAllText(displaced, "old");
        Directory.CreateDirectory(_space.Destination);
        File.WriteAllText(_space.Output("a.txt"), "new");

        await PrepareAsync();

        Assert.Equal("new", File.ReadAllText(_space.Output("a.txt")));
        Assert.Equal("old", File.ReadAllText(_space.Output("a (1).txt")));
    }

    // ----- A staging folder sits among the user's files, so nothing in it is taken at its word -----

    private string PlantedFolder() => StagingArea.FolderFor(_space.Staging, _space.Archive("x.rar"), _space.Destination);

    private void Plant(string folder, params string[] logLines)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllLines(Path.Combine(folder, "placed.log"), logLines);
    }

    private static string LogLine(object entry) => System.Text.Json.JsonSerializer.Serialize(entry);

    [Fact]
    public async Task AFileChangedSinceItWasPlacedIsLeftAlone()
    {
        Directory.CreateDirectory(_space.Destination);
        var target = _space.Output("a.txt");
        File.WriteAllText(target, "the user re-extracted this and edited it");
        var info = new FileInfo(target);

        Plant(PlantedFolder(), LogLine(new { Source = "a.txt", Target = target, DisplacedAs = (string?)null, Length = info.Length + 1, LastWriteTicks = info.LastWriteTimeUtc.Ticks }));
        var recovery = await StagingArea.RecoverAndRemoveAsync(_space.Staging, _space.Archive("x.rar"), _space.Destination);

        Assert.True(recovery.Removed);
        Assert.Equal(1, recovery.LeftInPlace);
        Assert.Equal("the user re-extracted this and edited it", File.ReadAllText(target));
    }

    [Fact]
    public async Task ALogLineWithoutAnIdentityRemovesNothing()
    {
        Directory.CreateDirectory(_space.Destination);
        var target = _space.Output("precious.txt");
        File.WriteAllText(target, "precious");

        Plant(PlantedFolder(), LogLine(new { Source = "a.txt", Target = target }));
        var recovery = await StagingArea.RecoverAndRemoveAsync(_space.Staging, _space.Archive("x.rar"), _space.Destination);

        Assert.True(recovery.Removed);
        Assert.True(File.Exists(target));
    }

    [Fact]
    public async Task ALogLineOutsideTheDestinationRemovesNothing()
    {
        var outside = Path.Combine(_space.Root, "elsewhere.txt");
        File.WriteAllText(outside, "precious");
        var info = new FileInfo(outside);

        Plant(PlantedFolder(), LogLine(new { Source = "a.txt", Target = outside, DisplacedAs = (string?)null, info.Length, LastWriteTicks = info.LastWriteTimeUtc.Ticks }));
        await StagingArea.RecoverAndRemoveAsync(_space.Staging, _space.Archive("x.rar"), _space.Destination);

        Assert.True(File.Exists(outside));
    }

    [Theory]
    [InlineData("""{"Source":"a.txt","Target":"a\u0000b"}""")]
    [InlineData("""{"Target":"C:\\x.txt"}""")]
    [InlineData("""{"Source":"..\\..\\x","Target":"C:\\x.txt"}""")]
    [InlineData("""{"Source":"a.txt"}""")]
    [InlineData("""not json at all""")]
    public async Task ABadLogLineIsSkippedAndCanNotThrow(string line)
    {
        Plant(PlantedFolder(), line);

        var recovery = await StagingArea.RecoverAndRemoveAsync(_space.Staging, _space.Archive("x.rar"), _space.Destination);

        Assert.True(recovery.Removed);
    }

    [Fact]
    public async Task ADisplacedFileHasNowhereToGoButTheDestination()
    {
        // The destination comes from the caller, so nothing in the folder can point this anywhere else.
        var folder = PlantedFolder();
        Directory.CreateDirectory(Path.Combine(folder, "displaced", "Startup"));
        File.WriteAllText(Path.Combine(folder, "displaced", "Startup", "planted.cmd"), "x");
        File.WriteAllText(Path.Combine(folder, "attempt.json"), $$"""{"FirstVolume":"x","Destination":{{System.Text.Json.JsonSerializer.Serialize(_space.Root)}}}""");

        await StagingArea.RecoverAndRemoveAsync(_space.Staging, _space.Archive("x.rar"), _space.Destination);

        Assert.True(File.Exists(_space.Output(@"Startup\planted.cmd")));
        Assert.False(File.Exists(Path.Combine(_space.Root, "Startup", "planted.cmd")));
    }

    [Fact]
    public async Task AnEmptyOrUnreadableMarkerFromAnOlderBuildCostsNoOriginal()
    {
        var folder = PlantedFolder();
        Directory.CreateDirectory(Path.Combine(folder, "displaced"));
        File.WriteAllText(Path.Combine(folder, "attempt.json"), string.Empty);
        File.WriteAllText(Path.Combine(folder, "displaced", "users-file.txt"), "the only copy");

        var recovery = await StagingArea.RecoverAndRemoveAsync(_space.Staging, _space.Archive("x.rar"), _space.Destination);

        Assert.True(recovery.Removed);
        Assert.Equal("the only copy", File.ReadAllText(_space.Output("users-file.txt")));
    }

    [Fact]
    public async Task ALockedLogKeepsTheFolderForLater()
    {
        var folder = PlantedFolder();
        Plant(folder);
        var log = Path.Combine(folder, "placed.log");
        using (new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var recovery = await StagingArea.RecoverAndRemoveAsync(_space.Staging, _space.Archive("x.rar"), _space.Destination);
            Assert.False(recovery.Removed);
            Assert.True(Directory.Exists(folder));
        }
    }

    [Fact]
    public async Task AFolderThatIsALinkIsRemovedWithoutFollowingIt()
    {
        var outside = Path.Combine(_space.Root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "keep");
        File.SetAttributes(Path.Combine(outside, "keep.txt"), FileAttributes.ReadOnly);
        var folder = PlantedFolder();
        Directory.CreateDirectory(_space.Staging);
        try
        {
            Directory.CreateSymbolicLink(folder, outside);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Making a symbolic link needs a privilege this account may not have, which leaves nothing to test.
            return;
        }

        var recovery = await StagingArea.RecoverAndRemoveAsync(_space.Staging, _space.Archive("x.rar"), _space.Destination);

        Assert.True(recovery.Removed);
        Assert.True(File.Exists(Path.Combine(outside, "keep.txt")));
        Assert.True(new FileInfo(Path.Combine(outside, "keep.txt")).IsReadOnly);
        File.SetAttributes(Path.Combine(outside, "keep.txt"), FileAttributes.Normal);
    }

    [Fact]
    public async Task StagingOnAnotherVolumeIsRefused()
    {
        var failure = await Assert.ThrowsAsync<ExtractionException>(() =>
            StagingArea.PrepareAsync(@"Z:\staging", _space.Archive("x.rar"), @"Y:\out", CancellationToken.None));
        Assert.Equal(ExtractionFailureKind.Unknown, failure.Kind);
    }
}
