using System.Text;
using DownloadManager.Archives;
using DownloadManager.Core.Archives;

namespace DownloadManager.Archives.Tests;

public sealed class UnrarListingTests
{
    private const string Plain = """

        Archive: C:\a b\plain.rar
        Details: RAR 5

                Name: a.txt
                Type: File
                Size: 5
         Packed size: 5
               Ratio: 100%
            Modified: 2026-11-06 05:58:54,698263700
          Attributes: ..A....
               CRC32: 3610A686
             Host OS: Windows
         Compression: RAR 5.0(v50) -m0 -md=512k

                Name: sub
                Type: Directory
            Modified: 2026-11-06 05:58:54,702303400
          Attributes: ...D...
             Host OS: Windows
         Compression: RAR 5.0(v50) -m0

        """;

    [Fact]
    public void ReadsEntriesAndDetails()
    {
        var listing = UnrarListing.Parse(Plain);

        Assert.True(listing.HasDetails);
        Assert.Equal("5", listing.Version);
        Assert.True(listing.IsRar5OrLater);
        Assert.False(listing.IsVolume);
        Assert.False(listing.HasUnknownLines);
        Assert.Equal(new ArchiveEntry("a.txt", 5, ArchiveEntryKind.File), listing.Entries[0]);
        Assert.Equal(ArchiveEntryKind.Directory, listing.Entries[1].Kind);
    }

    [Fact]
    public void NonArchiveHasNoDetails()
    {
        var listing = UnrarListing.Parse("\r\nnotrar.rar is not RAR archive\r\n");
        Assert.False(listing.HasDetails);
    }

    [Theory]
    [InlineData("RAR 5, volume 1", "5", true, 1)]
    [InlineData("RAR 5, volume 2", "5", true, 2)]
    [InlineData("RAR 1.5, volume", "1.5", true, null)]
    [InlineData("RAR 4, solid, recovery record", "4", false, null)]
    [InlineData("RAR 5, encrypted headers, volume 1", "5", true, 1)]
    public void ReadsDetailsTokens(string details, string version, bool isVolume, int? number)
    {
        var listing = UnrarListing.Parse($"Archive: x\r\nDetails: {details}\r\n");
        Assert.Equal(version, listing.Version);
        Assert.Equal(isVolume, listing.IsVolume);
        Assert.Equal(number, listing.VolumeNumber);
    }

    [Fact]
    public void FirstVolumeDetailsDescribeTheArchive()
    {
        var listing = UnrarListing.Parse("Archive: x.part1.rar\nDetails: RAR 5, volume 1\nArchive: x.part2.rar\nDetails: RAR 5, volume 2\n");
        Assert.Equal(1, listing.VolumeNumber);
    }

    [Fact]
    public void SplitFileIsOneEntry()
    {
        const string Output = """
            Archive: x.part1.rar
            Details: RAR 5, volume 1

                    Name: big.bin
                    Type: File
                    Size: 300
                   Ratio: -->
            Archive: x.part2.rar
            Details: RAR 5, volume 2

                    Name: big.bin
                    Type: File
                    Size: 300
                   Ratio: <--
            """;

        Assert.Equal(300, Assert.Single(UnrarListing.Parse(Output).Entries).Size);
    }

    [Theory]
    [InlineData("Unix symbolic link", ArchiveEntryKind.SymbolicLink)]
    [InlineData("Windows symbolic link", ArchiveEntryKind.SymbolicLink)]
    [InlineData("NTFS junction point", ArchiveEntryKind.Junction)]
    [InlineData("Hard link", ArchiveEntryKind.HardLink)]
    [InlineData("File reference", ArchiveEntryKind.HardLink)]
    [InlineData("NTFS alternate data stream", ArchiveEntryKind.Other)]
    [InlineData("Something new", ArchiveEntryKind.Other)]
    public void ReadsLinkKinds(string type, ArchiveEntryKind expected)
    {
        var listing = UnrarListing.Parse($"Archive: x\nDetails: RAR 5\n\n        Name: l\n        Type: {type}\n      Target: ..\\out\n");
        var entry = Assert.Single(listing.Entries);
        Assert.Equal(expected, entry.Kind);
        Assert.Equal("..\\out", entry.LinkTarget);
    }

    [Fact]
    public void EntryWithNoTypeCountsAsUnknown()
    {
        var listing = UnrarListing.Parse("Archive: x\nDetails: RAR 5\n\n        Name: l\n        Size: 1\n");
        Assert.Equal(ArchiveEntryKind.Other, Assert.Single(listing.Entries).Kind);
    }

    [Theory]
    [InlineData("        Name: a\nfake.txt\n        Type: File\n")]
    [InlineData("        Name: a\n        Type: File\n        Strange: 1\n")]
    [InlineData("        Type: File\n")]
    [InlineData("        Name: a\n        Type: File\n        Size: lots\n")]
    public void UnrecognisedLineIsFlagged(string body)
    {
        Assert.True(UnrarListing.Parse("Archive: x\nDetails: RAR 5\n\n" + body).HasUnknownLines);
    }

    [Fact]
    public void ColonInANameStaysInTheName()
    {
        var listing = UnrarListing.Parse("Archive: x\nDetails: RAR 5\n\n        Name: a: b.txt\n        Type: File\n");
        Assert.Equal("a: b.txt", Assert.Single(listing.Entries).Path);
    }
}

public sealed class UnrarPasswordTests
{
    private static readonly Encoding Cp1252;

    static UnrarPasswordTests()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Cp1252 = Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AbsentPasswordNeverPrompts(string? password)
    {
        var channel = UnrarPassword.From(password, Cp1252);
        Assert.Equal("-p-", channel.SwitchArgument);
        Assert.Null(channel.CopyStdinBytes());
    }

    [Fact]
    public void OrdinaryPasswordGoesThroughStdinOnly()
    {
        var channel = UnrarPassword.From("pä55ß with spaces ", Cp1252);

        Assert.Equal("-p", channel.SwitchArgument);
        Assert.Equal(Cp1252.GetBytes("pä55ß with spaces \r\n"), channel.CopyStdinBytes());
    }

    [Fact]
    public void ExactlyTheStdinLimitStaysOnStdin()
    {
        var channel = UnrarPassword.From(new string('x', 127), Cp1252);
        Assert.Equal("-p", channel.SwitchArgument);
    }

    [Fact]
    public void LongerPasswordFallsBackToTheCommandLine()
    {
        var password = new string('x', 128);
        var channel = UnrarPassword.From(password, Cp1252);
        Assert.Equal("-p" + password, channel.SwitchArgument);
        Assert.Null(channel.CopyStdinBytes());
    }

    [Fact]
    public void CharactersOutsideTheCodePageFallBackToTheCommandLine()
    {
        var channel = UnrarPassword.From("日本語", Cp1252);
        Assert.Equal("-p日本語", channel.SwitchArgument);
        Assert.Null(channel.CopyStdinBytes());
    }

    [Theory]
    [InlineData("a\nb")]
    [InlineData("a\rb")]
    [InlineData("a\0b")]
    public void LineBreakIsRefusedWithoutEchoingIt(string password)
    {
        var failure = Assert.Throws<ExtractionException>(() => UnrarPassword.From(password, Cp1252));
        Assert.Equal(ExtractionFailureKind.WrongPassword, failure.Kind);
        Assert.DoesNotContain(password, failure.Message);
    }

    [Fact]
    public void EachCopyIsSeparateSoClearingOneLeavesTheNext()
    {
        var channel = UnrarPassword.From("secret", Cp1252);
        var first = channel.CopyStdinBytes()!;
        Array.Clear(first);
        Assert.Equal(Cp1252.GetBytes("secret\r\n"), channel.CopyStdinBytes());
    }

    [Fact]
    public void RequestDoesNotPrintItsPassword()
    {
        var request = new ExtractionRequest { FirstVolume = "a", Destination = "b", StagingRoot = "c", Password = "hunter2" };
        Assert.DoesNotContain("hunter2", request.ToString()!);
    }
}

public sealed class UnrarToolLocatorTests : IDisposable
{
    private readonly Workspace _space = new();

    public void Dispose() => _space.Dispose();

    private string PlaceTool(string folder)
    {
        var directory = Directory.CreateDirectory(Path.Combine(_space.Root, folder)).FullName;
        var path = Path.Combine(directory, "UnRAR.exe");
        File.WriteAllBytes(path, []);
        return path;
    }

    [Fact]
    public void ConfiguredPathWins()
    {
        var configured = PlaceTool("custom");
        PlaceTool(@"pf\WinRAR");
        var path = UnrarToolLocator.Find(configured, [Path.Combine(_space.Root, "pf")], Path.GetDirectoryName(PlaceTool("onpath")));
        Assert.Equal(configured, path);
    }

    [Fact]
    public void ProgramFilesComesBeforePath()
    {
        var installed = PlaceTool(@"pf\WinRAR");
        var path = UnrarToolLocator.Find(null, [Path.Combine(_space.Root, "pf")], Path.GetDirectoryName(PlaceTool("onpath")));
        Assert.Equal(installed, path);
    }

    [Fact]
    public void MissingConfiguredPathFallsThrough()
    {
        var installed = PlaceTool(@"pf\WinRAR");
        Assert.Equal(installed, UnrarToolLocator.Find(Path.Combine(_space.Root, "nope.exe"), [Path.Combine(_space.Root, "pf")], string.Empty));
    }

    [Fact]
    public void PathIsTheLastResortAndQuotesAreHandled()
    {
        var tool = PlaceTool("on path");
        var folder = Path.GetDirectoryName(tool)!;
        var variable = string.Join(Path.PathSeparator, Path.Combine(_space.Root, "empty"), '"' + folder + '"');
        Assert.Equal(tool, UnrarToolLocator.Find(null, [], variable));
    }

    [Fact]
    public void RelativePathEntryIsIgnored()
    {
        PlaceTool("relative");
        var previous = Environment.CurrentDirectory;
        Assert.Null(UnrarToolLocator.Find(null, [], "relative;."));
        Assert.Equal(previous, Environment.CurrentDirectory);
    }

    [Fact]
    public void NothingFoundIsNull()
    {
        Assert.Null(UnrarToolLocator.Find(null, [], string.Empty));
    }

    [Fact]
    public async Task ExtractorReportsToolUnavailable()
    {
        var extractor = new UnrarExtractor(new UnrarExtractorOptions { ProgramFilesDirectories = [], PathVariable = string.Empty });
        var archive = _space.WriteArchive("a.rar", RarBuilder.Rar5(("a.txt", [1])));

        var failure = await Assert.ThrowsAsync<ExtractionException>(() => extractor.ExtractAsync(
            new ExtractionRequest { FirstVolume = archive, Destination = _space.Destination, StagingRoot = _space.Staging },
            null,
            CancellationToken.None));

        Assert.Equal(ExtractionFailureKind.ToolUnavailable, failure.Kind);
        _space.AssertDestinationUntouched();

        failure = await Assert.ThrowsAsync<ExtractionException>(() => extractor.ProbeAsync(archive, null, CancellationToken.None));
        Assert.Equal(ExtractionFailureKind.ToolUnavailable, failure.Kind);
    }

    [Fact]
    public async Task ToolThatCannotStartIsToolUnavailable()
    {
        var notAProgram = PlaceTool("broken");
        var extractor = new UnrarExtractor(new UnrarExtractorOptions { ToolPath = notAProgram, ProgramFilesDirectories = [], PathVariable = string.Empty });
        var archive = _space.WriteArchive("a.rar", RarBuilder.Rar5(("a.txt", [1])));

        var failure = await Assert.ThrowsAsync<ExtractionException>(() => extractor.ProbeAsync(archive, null, CancellationToken.None));

        Assert.Equal(ExtractionFailureKind.ToolUnavailable, failure.Kind);
    }
}

public sealed class UnrarProcessTests
{
    private static string Cmd => Path.Combine(Environment.SystemDirectory, "cmd.exe");

    private static ToolInvocation CmdRun(string script) => new(Cmd, ["/d", "/c", script]);

    [Fact]
    public async Task BoundedOutputSurvivesAFloodOnBothPipes()
    {
        var run = await UnrarProcess.RunAsync(
            CmdRun("for /l %i in (1,1,6000) do @(echo xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx& echo yyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyy 1>&2)"),
            CancellationToken.None);

        Assert.Equal(0, run.ExitCode);
        Assert.InRange(run.Output.Length, 1000, 32 * 1024);
        Assert.InRange(run.Errors.Length, 1000, 32 * 1024);
    }

    [Fact]
    public async Task OutputOverTheLimitStopsTheProcess()
    {
        var invocation = CmdRun("for /l %i in (1,1,200000) do @echo xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx") with { KeepOutputCharacters = 4096 };

        var failure = await Assert.ThrowsAsync<ExtractionException>(() => UnrarProcess.RunAsync(invocation, CancellationToken.None));

        Assert.Equal(ExtractionFailureKind.UnsafePath, failure.Kind);
    }

    [Fact]
    public async Task SilentProcessIsStopped()
    {
        var pid = 0;
        var invocation = CmdRun("ping -n 60 127.0.0.1 >nul") with
        {
            StallTimeout = TimeSpan.FromMilliseconds(400),
            Started = process => pid = process.Id,
        };

        var failure = await Assert.ThrowsAsync<ExtractionException>(() => UnrarProcess.RunAsync(invocation, CancellationToken.None));

        Assert.Equal(ExtractionFailureKind.Unknown, failure.Kind);
        Assert.Throws<ArgumentException>(() => System.Diagnostics.Process.GetProcessById(pid));
    }

    [Fact]
    public async Task CancellingStopsTheProcess()
    {
        using var cancellation = new CancellationTokenSource(200);
        var pid = 0;
        var invocation = CmdRun("ping -n 60 127.0.0.1 >nul") with { Started = process => pid = process.Id };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => UnrarProcess.RunAsync(invocation, cancellation.Token));

        Assert.Throws<ArgumentException>(() => System.Diagnostics.Process.GetProcessById(pid));
    }

    [Fact]
    public async Task AThrowingObserverDoesNotBlockThePipe()
    {
        var invocation = CmdRun("for /l %i in (1,1,3000) do @echo xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx") with
        {
            OnOutput = _ => throw new InvalidOperationException("observer failed"),
            StallTimeout = TimeSpan.FromSeconds(20),
        };

        var run = await UnrarProcess.RunAsync(invocation, CancellationToken.None);

        Assert.Equal(0, run.ExitCode);
    }

    [Fact]
    public async Task StdinIsClosedSoAReaderDoesNotHang()
    {
        var run = await UnrarProcess.RunAsync(CmdRun("set /p x=") with { StallTimeout = TimeSpan.FromSeconds(20) }, CancellationToken.None);
        Assert.NotNull(run);
    }

    [Fact]
    public async Task StdinBytesAreClearedAfterTheWrite()
    {
        var bytes = Encoding.ASCII.GetBytes("hello\r\n");
        var run = await UnrarProcess.RunAsync(CmdRun("findstr .") with { StandardInput = bytes }, CancellationToken.None);

        Assert.Contains("hello", run.Output);
        Assert.All(bytes, value => Assert.Equal(0, value));
    }
}
