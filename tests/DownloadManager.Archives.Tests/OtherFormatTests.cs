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
            var line = Encoding.UTF8.GetBytes(password + "\r\n");
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
