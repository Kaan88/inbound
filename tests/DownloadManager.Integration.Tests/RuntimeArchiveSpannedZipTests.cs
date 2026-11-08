using System.Text;
using DownloadManager.Archives.Tests;
using DownloadManager.Contracts;

namespace DownloadManager.Integration.Tests;

/// <summary>A4.D: a spanned zip set (name.z01 ... name.zip) extracts as one set, and its parts go with it.</summary>
[Collection("runtime-archive")]
public sealed class RuntimeArchiveSpannedZipTests(ArchiveFileServer fixture)
{
    private static readonly byte[] First = Encoding.ASCII.GetBytes("in the first part");
    private static readonly byte[] Second = Encoding.ASCII.GetBytes("in the second part");
    private static readonly byte[] Last = Encoding.ASCII.GetBytes("in the zip");

    private readonly ArchiveFileServer _server = fixture;

    private static void WriteSet(ArchiveHarness run) =>
        SpannedZipBuilder.Write(Path.GetDirectoryName(run.PathOf("set.zip"))!, "set", ("a.txt", First), ("b.txt", Second), ("c.txt", Last));

    [SevenZipTheory]
    [InlineData("set.zip")]
    [InlineData("set.z01")]
    public async Task ASpannedSetExtractsOnceAndItsPartsAreDeletedWithIt(string asked)
    {
        await using var run = await ArchiveHarness.StartAsync(_server);
        await run.Archives.UpdateSettingsAsync(new ArchiveSettingsUpdate { DeleteArchives = true });
        run.WriteFile("unrelated.z01", First);
        WriteSet(run);

        await run.Archives.ExtractAsync(new ArchiveExtractRequest { FilePath = run.PathOf(asked) });
        await run.WaitForSetAsync(set => set is { State: "completed", ArchivesDeleted: true });

        Assert.Equal(["a.txt", "b.txt", "c.txt", "unrelated.z01"], run.AllFiles());
        Assert.Equal(Second, File.ReadAllBytes(run.PathOf("b.txt")));
    }
}
