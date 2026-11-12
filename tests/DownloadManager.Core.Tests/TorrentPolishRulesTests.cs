using DownloadManager.Contracts;
using DownloadManager.Desktop.WinUI;

namespace DownloadManager.Core.Tests;

/// <summary>T5: a full disk on a torrent's row, and torrents in a saved download list.</summary>
public sealed class TorrentPolishRulesTests
{
    private const string Hash = "0123456789abcdef0123456789abcdef01234567";

    private static JobDto Torrent(string state = "paused", FailureDto? failure = null, int wanted = 3) => new()
    {
        Id = Guid.NewGuid(),
        Url = $"magnet:?xt=urn:btih:{Hash}&dn=Album",
        FileName = "Album",
        State = state,
        Failure = failure,
        Torrent = new TorrentDto { InfoHash = Hash, Name = "Album", HasMetadata = true, FileCount = 3, WantedCount = wanted },
    };

    [Fact]
    public void ATorrentPausedOnAFullDiskSaysSo()
    {
        Assert.Equal("Stopped · The disk is full", TorrentRules.StatusText(Torrent(failure: new FailureDto { Kind = "diskFull", Message = "The disk is full" })));
        Assert.Null(TorrentRules.StatusText(Torrent()));
    }

    [Fact]
    public void ASavedListKeepsATorrentsPartAndAnOlderHostsTorrentKeepsItsMagnet()
    {
        var withEntry = Torrent();
        var alone = Torrent();
        var plain = new JobDto { Id = Guid.NewGuid(), Url = "https://example.test/a.zip", FileName = "a.zip" };
        var entries = new Dictionary<Guid, TorrentListEntry>
        {
            [withEntry.Id] = new() { InfoHash = Hash, Name = "Album", Magnet = withEntry.Url, Data = "ZGF0YQ==", FileIndexes = [0, 2] },
        };

        var text = JobListFile.Serialize([withEntry, alone, plain], entries);
        var read = JobListFile.Deserialize(text);
        Assert.Equal("ZGF0YQ==", read[0].Torrent!.Data);
        Assert.Equal([0, 2], read[0].Torrent!.FileIndexes!);
        Assert.Equal((Hash, alone.Url, (string?)null), (read[1].Torrent!.InfoHash, read[1].Torrent!.Magnet, read[1].Torrent!.Data));
        Assert.Null(read[2].Torrent);
        Assert.DoesNotContain("\"torrent\": null", text);
        Assert.Equal([true, true, false], read.Select(JobListFile.IsTorrent));
        // A bare magnet in a hand-made list is a torrent too.
        Assert.True(JobListFile.IsTorrent(new JobListEntry { Url = " MAGNET:?xt=urn:btih:" + Hash }));
    }

    [Fact]
    public void TheImportSummarySaysHowManyTorrentsWereSkippedAndWhy()
    {
        Assert.Equal("Added 1 of 3 downloads. 2 torrents skipped: Already in the list.",
            RemoteText.ImportSummary(1, 3, 0, null, 0, 2, "Already in the list."));
        Assert.Equal("Added 0 of 2 downloads; 1 failed. No. 1 torrent skipped: Torrents are turned off.",
            RemoteText.ImportSummary(0, 2, 1, "No.", 0, 1, "Torrents are turned off."));
        Assert.Equal("Added 3 of 3 downloads.", RemoteText.ImportSummary(3, 3, 0, null, 0));
    }

    /// <summary>N6 follow-up: an entry's hand-off content type is sent in the import request, so its size counts too
    /// (the web UI's listEntrySize counts it already).</summary>
    [Fact]
    public void AnEntrysContentTypeCountsTowardsItsRequestSize()
    {
        var plain = new JobListEntry { Url = "https://example.test/a.bin" };
        var typed = new JobListEntry { Url = "https://example.test/a.bin", ContentType = new string('x', 100) };
        Assert.Equal(TorrentListPaging.SizeOf(plain) + 600, TorrentListPaging.SizeOf(typed));
    }

    [Fact]
    public void AnImportIsSentInRequestsUnderTheSizeLimitAndATooLargeTorrentFileGoesAsItsMagnet()
    {
        JobListEntry Entry(int dataChars) => new()
        {
            Url = $"magnet:?xt=urn:btih:{Hash}",
            Torrent = new TorrentListEntry { InfoHash = Hash, Magnet = $"magnet:?xt=urn:btih:{Hash}", Data = new string('A', dataChars) },
        };
        var half = TorrentListPaging.PageChars / 2;
        var huge = Entry(TorrentListPaging.PageChars + 1);
        var chunks = TorrentListPaging.Chunk([Entry(half), Entry(half), Entry(10), huge]);

        Assert.Equal([1, 3], chunks.Select(chunk => chunk.Count));
        Assert.All(chunks, chunk => Assert.True(chunk.Sum(TorrentListPaging.SizeOf) <= TorrentListPaging.PageChars));
        var sent = chunks[^1][^1];
        Assert.Null(sent.Torrent!.Data);
        Assert.Equal(huge.Torrent!.Magnet, sent.Torrent.Magnet);
        Assert.NotNull(huge.Torrent.Data); // the list read from the file is left as it was

        var total = new TorrentImportResult();
        TorrentListPaging.AddTo(total, new TorrentImportResult { Added = 1, Skipped = 1, SkipReason = "Already in the list." });
        TorrentListPaging.AddTo(total, new TorrentImportResult { Added = 2, Failed = 1, FirstError = "No." });
        Assert.Equal((3, 1, 1, "Already in the list.", "No."), (total.Added, total.Skipped, total.Failed, total.SkipReason, total.FirstError));
    }
}
