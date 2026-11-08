using System.Text;
using DownloadManager.Archives.Tests;
using DownloadManager.Contracts;

namespace DownloadManager.Integration.Tests;

/// <summary>TB.S3: a set's RAR recovery volumes go with its archives, and only when archives are deleted.</summary>
[Collection("runtime-archive")]
public sealed class RuntimeArchiveRecoveryVolumeTests(ArchiveFileServer fixture)
{
    private static readonly byte[] Text = Encoding.ASCII.GetBytes("inside the recoverable archive");
    private static readonly byte[] NotAnArchive = Encoding.ASCII.GetBytes("stand-in for a recovery volume");

    private readonly ArchiveFileServer _server = fixture;

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(20);
        }

        Assert.Fail($"This never happened: {what}.");
    }

    private static void WriteSetWithRecovery(ArchiveHarness run)
    {
        run.WriteFile("movie.rar", RarBuilder.Rar5(("inside.txt", Text)));
        run.WriteFile("movie.rev", NotAnArchive);
        run.WriteFile("movie.part1.rev", NotAnArchive);
        run.WriteFile("movie1_2_1.rev", NotAnArchive);
        // Not this set's: another name, and a name RAR wouldn't give this set's recovery volumes.
        run.WriteFile("movie2.rev", NotAnArchive);
        run.WriteFile("other.rev", NotAnArchive);
    }

    [SevenZipFact]
    public async Task WithDeletionOnTheSetsRecoveryVolumesGoWithItAndNoOneElsesDo()
    {
        await using var run = await ArchiveHarness.StartAsync(_server);
        await run.Archives.UpdateSettingsAsync(new ArchiveSettingsUpdate { DeleteArchives = true });
        WriteSetWithRecovery(run);

        await run.Archives.ExtractAsync(new ArchiveExtractRequest { FilePath = run.PathOf("movie.rar") });
        await run.WaitForSetAsync(set => set is { State: "completed", ArchivesDeleted: true });
        await WaitUntilAsync(() => !File.Exists(run.PathOf("movie.rev")), "the recovery volumes deleted");

        Assert.Equal(["inside.txt", "movie2.rev", "other.rev"], run.AllFiles());
        Assert.Equal(Text, File.ReadAllBytes(run.PathOf("inside.txt")));
    }

    [SevenZipFact]
    public async Task WithDeletionOffTheRecoveryVolumesStay()
    {
        await using var run = await ArchiveHarness.StartAsync(_server);
        WriteSetWithRecovery(run);

        await run.Archives.ExtractAsync(new ArchiveExtractRequest { FilePath = run.PathOf("movie.rar") });
        var done = await run.WaitForStateAsync("completed");

        Assert.False(done.ArchivesDeleted);
        Assert.Equal(["inside.txt", "movie.part1.rev", "movie.rar", "movie.rev", "movie1_2_1.rev", "movie2.rev", "other.rev"], run.AllFiles());
    }
}
