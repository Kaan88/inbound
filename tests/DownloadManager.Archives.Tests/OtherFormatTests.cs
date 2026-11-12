using System.Diagnostics;
using System.Text;
using DownloadManager.Core.Archives;

namespace DownloadManager.Archives.Tests;

/// <summary>
/// Formats other than RAR, made here by the bundled 7-Zip itself. A password goes in on stdin, the way the extractor
/// sends it, so a quote or backslash in it means what it says.
/// </summary>
public sealed class OtherFormatTests : ExtractionTestBase
{
    private const string Quoted = "tail \"quoted\" end\\ ü";

    private async Task<string> BuildAsync(string name, string? password = null, params string[] switches)
    {
        var info = new ProcessStartInfo(RarTools.SevenZipPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Space.Source,
        };
        foreach (var argument in new[] { "a", "-sccUTF-8", "-bso0", "-bsp0" }.Concat(password is null ? [] : ["-p"]).Concat(switches))
            info.ArgumentList.Add(argument);
        info.ArgumentList.Add(Space.Archive(name));
        info.ArgumentList.Add("*");

        using var process = Process.Start(info)!;
        if (password is not null)
        {
            // Asked twice when making an archive.
            var line = Encoding.UTF8.GetBytes(password + (OperatingSystem.IsWindows() ? "\r\n" : "\n"));
            await process.StandardInput.BaseStream.WriteAsync(line);
            await process.StandardInput.BaseStream.WriteAsync(line);
        }

        process.StandardInput.Close();
        var errors = process.StandardError.ReadToEndAsync();
        await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, $"7-Zip exited {process.ExitCode}: {await errors}");
        return Space.Archive(name);
    }

    [RequiresSevenZipTheory]
    [InlineData("a.7z", "7z")]
    [InlineData("a.zip", "zip")]
    [InlineData("a.tar", "tar")]
    public async Task APlainArchiveExtracts(string name, string format)
    {
        Space.WriteStandardSource();
        var archive = await BuildAsync(name);

        var probe = await Extractor().ProbeAsync(archive, null, CancellationToken.None);
        Assert.Equal(format, probe.Version);
        Assert.False(probe.IsEncrypted);

        await Extractor().ExtractAsync(Request(archive), null, CancellationToken.None);
        Space.AssertSameAsSource();
    }

    // ----- Zips with backslashes in their names (ultrareview 0.6.0 #4) -----
    // Stored as bytes, because .NET's ZipArchive can't choose the host a zip names: Python's zipfile wrote them with the
    // names set after each entry was built (its constructor turns a backslash into "/" on Windows) and the host and
    // attributes the way Windows tools (FAT, archive bit) and Linux tools (Unix, file mode) set them. Windows' 7-Zip
    // treats a backslash as a folder separator in a zip made on Windows and replaces it in one made on Unix; Linux's
    // 7-Zip 26.04 keeps it in the name, whatever host the zip names.

    private const string WindowsFolders = "UEsDBBQAAAAAAABgaF0AAAAAAAAAAAAAAAAEAAAAZGlyXFBLAwQUAAAAAAAAYGhdAAAAAAAAAAAAAAAACgAAAGRpclxlbXB0eVxQSwMEFAAAAAAAAGBoXYamEDYFAAAABQAAAA0AAABkaXJcc3ViXGEudHh0aGVsbG9QSwMEFAAAAAAAAGBoXSP/GekFAAAABQAAAA8AAABkaXIvbWl4ZWRcYi50eHRtaXhlZFBLAQIUABQAAAAAAABgaF0AAAAAAAAAAAAAAAAEAAAAAAAAAAAAMAAAAAAAAABkaXJcUEsBAhQAFAAAAAAAAGBoXQAAAAAAAAAAAAAAAAoAAAAAAAAAAAAwAAAAIgAAAGRpclxlbXB0eVxQSwECFAAUAAAAAAAAYGhdhqYQNgUAAAAFAAAADQAAAAAAAAAAACAAAABKAAAAZGlyXHN1YlxhLnR4dFBLAQIUABQAAAAAAABgaF0j/xnpBQAAAAUAAAAPAAAAAAAAAAAAIAAAAHoAAABkaXIvbWl4ZWRcYi50eHRQSwUGAAAAAAQABADiAAAArAAAAAAA";
    private const string WindowsTraversal = "UEsDBBQAAAAAAABgaF1SMfuNBAAAAAQAAAALAAAALi5cZXZpbC50eHRldmlsUEsBAhQAFAAAAAAAAGBoXVIx+40EAAAABAAAAAsAAAAAAAAAAAAgAAAAAAAAAC4uXGV2aWwudHh0UEsFBgAAAAABAAEAOQAAAC0AAAAAAA==";
    private const string WindowsTwoNamesOneFile = "UEsDBBQAAAAAAABgaF3xhmx6AwAAAAMAAAAJAAAAZGlyXGEudHh0b25lUEsDBBQAAAAAAABgaF1misoRAwAAAAMAAAAJAAAAZGlyL2EudHh0dHdvUEsBAhQAFAAAAAAAAGBoXfGGbHoDAAAAAwAAAAkAAAAAAAAAAAAgAAAAAAAAAGRpclxhLnR4dFBLAQIUABQAAAAAAABgaF1misoRAwAAAAMAAAAJAAAAAAAAAAAAIAAAACoAAABkaXIvYS50eHRQSwUGAAAAAAIAAgBuAAAAVAAAAAAA";
    private const string UnixBackslashName = "UEsDBBQAAAAAAABgaF3oLbyRBwAAAAcAAAAJAAAAZGlyXGEudHh0bGl0ZXJhbFBLAQIUAxQAAAAAAABgaF3oLbyRBwAAAAcAAAAJAAAAAAAAAAAAAACkgQAAAABkaXJcYS50eHRQSwUGAAAAAAEAAQA3AAAALgAAAAAA";

    private static readonly char Backslash = (char)92;

    private async Task<string> WriteZipAsync(string name, string base64)
    {
        var archive = Space.Archive(name);
        await File.WriteAllBytesAsync(archive, Convert.FromBase64String(base64));
        return archive;
    }

    /// <summary>
    /// Some Windows tools (PowerShell 5.1's Compress-Archive among them) write a zip's folders with backslashes. On Linux
    /// those entries are listed by the folders they mean and put in them, including an empty folder and a name that
    /// mixes both separators.
    /// </summary>
    [RequiresSevenZipFact]
    public async Task AZipMadeOnWindowsWithBackslashesExtractsIntoItsFolders()
    {
        var archive = await WriteZipAsync("backslashes.zip", WindowsFolders);

        var probe = await Extractor().ProbeAsync(archive, null, CancellationToken.None);
        Assert.DoesNotContain(probe.Entries, entry => !OperatingSystem.IsWindows() && entry.Path.Contains(Backslash));

        await Extractor().ExtractAsync(Request(archive), null, CancellationToken.None);
        Assert.Equal("hello", await File.ReadAllTextAsync(Path.Combine(Space.Destination, "dir", "sub", "a.txt")));
        Assert.Equal("mixed", await File.ReadAllTextAsync(Path.Combine(Space.Destination, "dir", "mixed", "b.txt")));
        Assert.True(Directory.Exists(Path.Combine(Space.Destination, "dir", "empty")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Space.Destination).Where(entry => Path.GetFileName(entry).Contains(Backslash)));
    }

    /// <summary>A backslash that becomes a folder separator is checked as one: "..\evil.txt" climbs out, so nothing is written.</summary>
    [RequiresSevenZipFact]
    public async Task AWindowsZipThatClimbsOutWithBackslashesIsRefused()
    {
        var archive = await WriteZipAsync("climb.zip", WindowsTraversal);

        var failure = await Assert.ThrowsAsync<ExtractionException>(() => Extractor().ExtractAsync(Request(archive), null, CancellationToken.None));
        Assert.Equal(ExtractionFailureKind.UnsafePath, failure.Kind);
        // Named as a traversal, not as a backslash: the backslash was read as a separator before the check.
        Assert.Contains("traversal", failure.Message);
        Space.AssertDestinationUntouched();
        Space.AssertNoStagingLeft();
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(Space.Destination)!, "evil.txt")));
    }

    /// <summary>"dir\a.txt" and "dir/a.txt" are one file once the backslash separates folders, so one would overwrite the other.</summary>
    [RequiresSevenZipFact]
    public async Task AWindowsZipWhoseBackslashNameRepeatsAnotherIsRefused()
    {
        var archive = await WriteZipAsync("twice.zip", WindowsTwoNamesOneFile);

        var failure = await Assert.ThrowsAsync<ExtractionException>(() => Extractor().ExtractAsync(Request(archive), null, CancellationToken.None));
        Assert.Equal(ExtractionFailureKind.UnsafePath, failure.Kind);
        Assert.Contains("same name twice", failure.Message);
        Space.AssertDestinationUntouched();
        Space.AssertNoStagingLeft();
    }

    /// <summary>
    /// A zip made on Unix may hold a backslash as part of a name. Linux can't tell whether a folder was meant, so it refuses
    /// the archive. Windows can't keep such a name either; its 7-Zip writes it as one file with the backslash replaced.
    /// </summary>
    [RequiresSevenZipFact]
    public async Task AUnixZipWithABackslashInANameIsRefusedOffWindows()
    {
        var archive = await WriteZipAsync("unix.zip", UnixBackslashName);

        if (OperatingSystem.IsWindows())
        {
            await Extractor().ExtractAsync(Request(archive), null, CancellationToken.None);
            var written = Assert.Single(Directory.EnumerateFileSystemEntries(Space.Destination, "*", SearchOption.AllDirectories));
            Assert.Equal("literal", await File.ReadAllTextAsync(written));
            return;
        }

        Assert.Equal(ExtractionFailureKind.UnsafePath, await FailureOf(Request(archive)));
    }

    [RequiresSevenZipFact]
    public async Task A7zWithHiddenNamesNeedsItsPasswordAndTakesAnyCharacters()
    {
        Space.WriteStandardSource();
        var archive = await BuildAsync("locked.7z", Quoted, "-mhe=on");

        var probe = await Extractor().ProbeAsync(archive, null, CancellationToken.None);
        Assert.True(probe.HeadersEncrypted);
        Assert.Empty(probe.Entries);

        Assert.Equal(ExtractionFailureKind.PasswordRequired, await FailureOf(Request(archive)));
        Assert.Equal(ExtractionFailureKind.WrongPassword, await FailureOf(Request(archive, "not it")));

        await Extractor().ExtractAsync(Request(archive, Quoted), null, CancellationToken.None);
        Space.AssertSameAsSource();
    }

    [RequiresSevenZipTheory]
    [InlineData("Wrong password.zip")]
    [InlineData("Wrong.zip")]
    [InlineData("Wrong password.7z")]
    [InlineData("Data Error in encrypted file.7z")]
    public async Task AnArchiveNamedLikeAToolMessageDoesNotHideThatMessage(string name)
    {
        // Review of audit L22: a line that was a prefix of the archive's name was dropped as "a name made from the
        // archive", so "Wrong password.zip" turned 7-Zip's "ERROR: Wrong password : a.txt" into an unknown failure.
        Space.WriteStandardSource();
        var archive = await BuildAsync(name, "right");

        Assert.Equal(ExtractionFailureKind.WrongPassword, await FailureOf(Request(archive, "wrong")));
    }

    [RequiresSevenZipTheory]
    [InlineData("data.7z")]
    [InlineData("data.zip")]
    public async Task AWrongPasswordForEncryptedContentIsSaid(string name)
    {
        Space.WriteStandardSource();
        var archive = await BuildAsync(name, "right");

        var probe = await Extractor().ProbeAsync(archive, null, CancellationToken.None);
        Assert.True(probe.IsEncrypted);
        Assert.False(probe.HeadersEncrypted);

        Assert.Equal(ExtractionFailureKind.PasswordRequired, await FailureOf(Request(archive)));
        Assert.Equal(ExtractionFailureKind.WrongPassword, await FailureOf(Request(archive, "wrong")));

        await Extractor().ExtractAsync(Request(archive, "right"), null, CancellationToken.None);
        Space.AssertSameAsSource();
    }

    [RequiresSevenZipFact]
    public async Task ASplit7zSetExtractsAndNamesEveryPart()
    {
        Space.WriteSource("big.bin", Workspace.RandomBytes(300 * 1024, 11));
        var first = await BuildAsync("set.7z", null, "-v100k", "-mx0");
        first += ".001";

        var probe = await Extractor().ProbeAsync(first, null, CancellationToken.None);
        Assert.Equal(["set.7z.001", "set.7z.002", "set.7z.003", "set.7z.004"], probe.VolumesRead.Select(Path.GetFileName));

        var result = await Extractor().ExtractAsync(Request(first), null, CancellationToken.None);
        Assert.Equal(4, result.VolumesRead.Count);
        Space.AssertSameAsSource();
    }

    /// <summary>Wraps an archive already built into one compressed file, the way gzip and the rest do.</summary>
    private static async Task<string> CompressAsync(string file, string type, string target)
    {
        var info = new ProcessStartInfo(RarTools.SevenZipPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[] { "a", "-bso0", "-bsp0", "-t" + type, target, file }) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var errors = process.StandardError.ReadToEndAsync();
        await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, $"7-Zip exited {process.ExitCode}: {await errors}");
        return target;
    }

    [RequiresSevenZipTheory]
    [InlineData("a.tar.gz", "gzip")]
    [InlineData("a.tgz", "gzip")]
    [InlineData("a.tar.bz2", "bzip2")]
    [InlineData("a.tar.xz", "xz")]
    public async Task ACompressedTarExtractsBothLayers(string name, string type)
    {
        Space.WriteStandardSource();
        var tar = await BuildAsync("inner.tar");
        var archive = await CompressAsync(tar, type, Space.Archive(name));
        File.Delete(tar);

        var result = await Extractor().ExtractAsync(Request(archive), null, CancellationToken.None);
        Assert.DoesNotContain(result.Files, file => file.FinalPath.EndsWith(".tar", StringComparison.OrdinalIgnoreCase));
        Space.AssertSameAsSource();
    }

    [RequiresSevenZipFact]
    public async Task APlainGzipGivesItsOneFile()
    {
        var path = Space.WriteSource("log.txt", Encoding.ASCII.GetBytes("line one\nline two\n"));
        var archive = await CompressAsync(path, "gzip", Space.Archive("log.txt.gz"));

        var result = await Extractor().ExtractAsync(Request(archive), null, CancellationToken.None);
        var file = Assert.Single(result.Files);
        Assert.Equal("line one\nline two\n", File.ReadAllText(file.FinalPath));
    }

    [RequiresSevenZipTheory]
    [InlineData("big.bin.gz")]
    [InlineData("big.tar.gz")]
    public async Task AStreamThatRunsTheDiskDownIsStoppedAndNothingLands(string name)
    {
        // The first look (before anything is written) finds room; every later one finds the disk all but full, as a
        // small bomb would leave it. Before, the stream was unpacked in full and nothing measured the room it took.
        var path = Space.WriteSource("big.bin", new byte[4 * 1024 * 1024]);
        var archive = await CompressAsync(path, "gzip", Space.Archive(name));
        var looks = 0;
        var options = new SevenZipExtractorOptions
        {
            AvailableBytes = _ => Interlocked.Increment(ref looks) == 1 ? 100L << 30 : 1024,
        };

        Assert.Equal(ExtractionFailureKind.DiskSpace, await FailureOf(Request(archive), options));
        Assert.True(looks >= 2);
    }

    [RequiresSevenZipFact]
    public async Task ASplitSetThatEndsEarlyWaitsForItsNextPart()
    {
        Space.WriteSource("big.bin", Workspace.RandomBytes(300 * 1024, 12));
        var first = await BuildAsync("cut.7z", null, "-v100k", "-mx0") + ".001";
        var parts = Directory.GetFiles(Space.Archives, "cut.7z.*").Order().ToList();
        File.Delete(parts[^1]);

        var failure = await Assert.ThrowsAsync<ExtractionException>(() => Extractor().ExtractAsync(Request(first), null, CancellationToken.None));
        Assert.Equal(ExtractionFailureKind.MissingVolume, failure.Kind);
        Assert.Equal([Path.GetFileName(parts[^1])], failure.MissingVolumes);
    }

    [RequiresSevenZipFact]
    public async Task AFileThatIsNotAnArchiveIsSaidToBeOne()
    {
        var path = Space.WriteArchive("fake.zip", Encoding.ASCII.GetBytes("just some text"));
        Assert.Equal(ExtractionFailureKind.Corrupted, await FailureOf(Request(path)));
    }
}
