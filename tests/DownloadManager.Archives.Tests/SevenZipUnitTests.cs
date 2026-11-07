using System.Text;
using DownloadManager.Archives;
using DownloadManager.Core.Archives;

namespace DownloadManager.Archives.Tests;

public sealed class SevenZipListingTests
{
    private const string Banner = "\r\n7-Zip 26.04 (x64) : Copyright (c) 1999-2026 Igor Pavlov : 2026-10-05\r\n\r\nScanning the drive for archives:\r\n1 file, 462 bytes (1 KiB)\r\n\r\nListing archive: a.rar\r\n\r\n";

    private static string Listing(string archive, params string[] entries) =>
        Banner + "--\r\n" + archive + "\r\n----------\r\n" + string.Join("\r\n", entries.Select(entry => entry + "\r\n"));

    private static string Entry(string path, long size = 5, bool folder = false, bool encrypted = false, string extra = "") =>
        $"Path = {path}\r\nFolder = {(folder ? "+" : "-")}\r\nSize = {size}\r\nPacked Size = {size}\r\nAttributes = {(folder ? "D" : "A")}\r\n" +
        $"Alternate Stream = -\r\nEncrypted = {(encrypted ? "+" : "-")}\r\nSymbolic Link = \r\nHard Link = \r\nCopy Link = \r\n{extra}";

    [Fact]
    public void ReadsTheArchiveAndItsEntries()
    {
        var listing = SevenZipListing.Parse(Listing(
            "Path = a.rar\r\nType = Rar5\r\nPhysical Size = 462\r\nEncrypted = -\r\n",
            Entry(@"src\a.txt", 7), Entry(@"src\sub", 0, folder: true), Entry(@"src\sub\b.txt", 9, encrypted: true)));

        Assert.True(listing.HasEntrySection);
        Assert.Equal("Rar5", listing.Type);
        Assert.False(listing.IsVolume);
        Assert.False(listing.HasUnknownLines);
        Assert.Equal(3, listing.Entries.Count);
        Assert.Equal(new ArchiveEntry(@"src\a.txt", 7, ArchiveEntryKind.File), listing.Entries[0]);
        Assert.Equal(ArchiveEntryKind.Directory, listing.Entries[1].Kind);
        Assert.True(listing.Entries[2].IsEncrypted);
    }

    [Fact]
    public void ReadsAVolumeAndAMissingOne()
    {
        var listing = SevenZipListing.Parse(Listing(
            "Path = v.part1.rar\r\nType = Rar5\r\nERROR = Missing volume : v.part2.rar\r\nMultivolume = +\r\nVolume Index = 0\r\nVolumes = 1\r\n",
            Entry("big.bin", 300000)));

        Assert.True(listing.IsVolume);
        Assert.Equal(1, listing.VolumeNumber);
        Assert.Equal(["v.part2.rar"], listing.MissingVolumes);
    }

    [Fact]
    public void ALaterVolumeSaysWhichItIs()
    {
        var listing = SevenZipListing.Parse(Listing(
            "Path = v.part2.rar\r\nType = Rar5\r\nERRORS:\r\nHeaders Error\r\nMultivolume = +\r\nVolume Index = 1\r\nVolumes = 2\r\n",
            Entry("big.bin", 300000)));

        Assert.Equal(2, listing.VolumeNumber);
        Assert.Contains("Headers Error", listing.Problems);
    }

    [Fact]
    public void ASplitSetShowsBothTypes()
    {
        var listing = SevenZipListing.Parse(Listing(
            "Path = s.7z.001\r\nType = Split\r\nVolumes = 3\r\n----\r\nPath = s.7z\r\nSize = 300248\r\n--\r\nPath = s.7z\r\nType = 7z\r\n",
            Entry("a.bin", 300000)));

        Assert.Equal("Split", listing.OuterType);
        Assert.Equal("7z", listing.Type);
        Assert.Equal(3, listing.Volumes);
    }

    [Fact]
    public void NoEntrySectionMeansNothingWasListed()
    {
        var listing = SevenZipListing.Parse(Banner + "Errors: 1\r\n");
        Assert.False(listing.HasEntrySection);
        Assert.Empty(listing.Entries);
    }

    [Theory]
    [InlineData("Symbolic Link = ../../etc", ArchiveEntryKind.SymbolicLink)]
    [InlineData("Hard Link = other.txt", ArchiveEntryKind.HardLink)]
    [InlineData("Link = target", ArchiveEntryKind.SymbolicLink)]
    public void ReadsLinks(string line, ArchiveEntryKind kind)
    {
        var entry = $"Path = l\r\nFolder = -\r\nSize = 0\r\n{line}\r\n";
        var listing = SevenZipListing.Parse(Listing("Path = a.tar\r\nType = tar\r\n", entry));
        Assert.Equal(kind, Assert.Single(listing.Entries).Kind);
    }

    [Fact]
    public void AUnixLinkModeIsNotAFile()
    {
        var listing = SevenZipListing.Parse(Listing("Path = a.tar\r\nType = tar\r\n", "Path = l\r\nFolder = -\r\nSize = 0\r\nAttributes = A lrwxrwxrwx\r\n"));
        Assert.Equal(ArchiveEntryKind.Other, Assert.Single(listing.Entries).Kind);
    }

    [Theory]
    [InlineData("Path = a\r\nspilled line\r\nSize = 1\r\n")]
    [InlineData("Size = 1\r\nPath = a\r\n")]
    [InlineData("Path = a\r\nSize = 1\r\nSize = 0\r\n")]
    [InlineData("Path = a\r\nSize = lots\r\n")]
    public void ALineThatDoesNotFitMakesTheListingUntrusted(string block)
    {
        Assert.True(SevenZipListing.Parse(Listing("Path = a.zip\r\nType = zip\r\n", block)).HasUnknownLines);
    }

    [Fact]
    public void AnEqualsSignInANameStaysInTheName()
    {
        var listing = SevenZipListing.Parse(Listing("Path = a.zip\r\nType = zip\r\n", Entry("x = y.txt")));
        Assert.Equal("x = y.txt", Assert.Single(listing.Entries).Path);
    }

    [Fact]
    public void ANameTwiceIsFlaggedButAFolderTwiceIsNot()
    {
        Assert.True(SevenZipListing.Parse(Listing("Path = a.zip\r\nType = zip\r\n", Entry("a.txt"), Entry("a.txt"))).HasRepeatedNames);
        Assert.False(SevenZipListing.Parse(Listing("Path = a.zip\r\nType = zip\r\n", Entry("d", folder: true), Entry("d", folder: true))).HasRepeatedNames);
    }

    [Fact]
    public void AStreamEntryIsNoted()
    {
        var listing = SevenZipListing.Parse(Listing("Path = a.rar\r\nType = Rar5\r\n", "Path = a.txt:hidden\r\nFolder = -\r\nSize = 2\r\nAlternate Stream = +\r\n"));
        Assert.True(listing.HoldsDataStreams);
    }
}

public sealed class ToolPasswordTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NoPasswordSendsAnEmptyLine(string? password)
    {
        var channel = ToolPassword.From(password);
        Assert.Same(ToolPassword.None, channel);
        Assert.Equal("\r\n"u8.ToArray(), channel.CopyStdinBytes());
    }

    [Theory]
    [InlineData("pä55ß with spaces ")]
    [InlineData("日本語パスワード")]
    [InlineData(" leading and trailing ")]
    public void APasswordGoesThroughStdinAsUtf8(string password)
    {
        Assert.Equal(Encoding.UTF8.GetBytes(password + "\r\n"), ToolPassword.From(password).CopyStdinBytes());
    }

    [Theory]
    [InlineData("a\nb")]
    [InlineData("a\rb")]
    [InlineData("a\0b")]
    public void ALineBreakIsRefusedWithoutEchoingIt(string password)
    {
        var failure = Assert.Throws<ExtractionException>(() => ToolPassword.From(password));
        Assert.Equal(ExtractionFailureKind.WrongPassword, failure.Kind);
        Assert.DoesNotContain(password, failure.Message);
    }

    [Fact]
    public void EachCopyIsSeparateSoClearingOneLeavesTheNext()
    {
        var channel = ToolPassword.From("secret");
        Array.Clear(channel.CopyStdinBytes());
        Assert.Equal("secret\r\n"u8.ToArray(), channel.CopyStdinBytes());
    }

    [Fact]
    public void ARequestDoesNotPrintItsPassword()
    {
        var request = new ExtractionRequest { FirstVolume = "a", Destination = "b", StagingRoot = "c", Password = "hunter2" };
        Assert.DoesNotContain("hunter2", request.ToString()!);
    }
}

public sealed class SevenZipToolTests : IDisposable
{
    private readonly Workspace _space = new();

    public void Dispose() => _space.Dispose();

    [Fact]
    public void TheBundledCopyIsNextToTheProgramWithItsLibraryAndLicense()
    {
        var tool = SevenZipTool.Find();
        Assert.NotNull(tool);
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "tools", "7-Zip", "7z.exe"), tool);
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(tool)!, "License.txt")));
    }

    [Fact]
    public void AProgramWithoutItsLibraryIsNotFound()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_space.Root, "only exe")).FullName;
        var exe = Path.Combine(folder, "7z.exe");
        File.WriteAllBytes(exe, []);
        Assert.Null(SevenZipTool.Find(exe));
    }

    [Fact]
    public async Task AMissingToolIsToolUnavailableAndTouchesNothing()
    {
        var extractor = new SevenZipExtractor(new SevenZipExtractorOptions { ToolPath = Path.Combine(_space.Root, "none", "7z.exe") });
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
    public async Task AToolThatCannotStartIsToolUnavailable()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_space.Root, "broken")).FullName;
        File.WriteAllBytes(Path.Combine(folder, "7z.exe"), []);
        File.WriteAllBytes(Path.Combine(folder, "7z.dll"), []);
        var extractor = new SevenZipExtractor(new SevenZipExtractorOptions { ToolPath = Path.Combine(folder, "7z.exe") });
        var archive = _space.WriteArchive("a.rar", RarBuilder.Rar5(("a.txt", [1])));

        var failure = await Assert.ThrowsAsync<ExtractionException>(() => extractor.ProbeAsync(archive, null, CancellationToken.None));

        Assert.Equal(ExtractionFailureKind.ToolUnavailable, failure.Kind);
    }
}

public sealed class ToolProcessTests
{
    private static string Cmd => Path.Combine(Environment.SystemDirectory, "cmd.exe");

    private static ToolInvocation CmdRun(string script) => new(Cmd, ["/d", "/c", script]);

    [Fact]
    public async Task BoundedOutputSurvivesAFloodOnBothPipes()
    {
        var run = await ToolProcess.RunAsync(
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

        var failure = await Assert.ThrowsAsync<ExtractionException>(() => ToolProcess.RunAsync(invocation, CancellationToken.None));

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

        var failure = await Assert.ThrowsAsync<ExtractionException>(() => ToolProcess.RunAsync(invocation, CancellationToken.None));

        Assert.Equal(ExtractionFailureKind.Unknown, failure.Kind);
        Assert.Throws<ArgumentException>(() => System.Diagnostics.Process.GetProcessById(pid));
    }

    [Fact]
    public async Task CancellingStopsTheProcess()
    {
        using var cancellation = new CancellationTokenSource(200);
        var pid = 0;
        var invocation = CmdRun("ping -n 60 127.0.0.1 >nul") with { Started = process => pid = process.Id };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ToolProcess.RunAsync(invocation, cancellation.Token));

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

        var run = await ToolProcess.RunAsync(invocation, CancellationToken.None);

        Assert.Equal(0, run.ExitCode);
    }

    [Fact]
    public async Task StdinIsClosedSoAReaderDoesNotHang()
    {
        var run = await ToolProcess.RunAsync(CmdRun("set /p x=") with { StallTimeout = TimeSpan.FromSeconds(20) }, CancellationToken.None);
        Assert.NotNull(run);
    }

    [Fact]
    public async Task StdinBytesAreClearedAfterTheWrite()
    {
        var bytes = Encoding.ASCII.GetBytes("hello\r\n");
        var run = await ToolProcess.RunAsync(CmdRun("findstr .") with { StandardInput = bytes }, CancellationToken.None);

        Assert.Contains("hello", run.Output);
        Assert.All(bytes, value => Assert.Equal(0, value));
    }
}
