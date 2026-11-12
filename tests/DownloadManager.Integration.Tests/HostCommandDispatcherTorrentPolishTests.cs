using System.Security.Cryptography;
using System.Text.Json.Nodes;
using DownloadManager.Contracts;
using DownloadManager.Runtime;

namespace DownloadManager.Integration.Tests;

/// <summary>T5: a full disk pauses a torrent, and a saved list carries torrents out and back in.</summary>
public sealed partial class HostCommandDispatcherTests
{
    [Fact]
    public async Task AFullDiskPausesTheTorrentWithItsReasonAndResumeGoesOn()
    {
        var service = await TorrentServiceAsync();
        var dispatcher = Dispatcher(service);
        var notices = new List<DownloadNotice>();
        using var told = new DownloadNotices(service, null, notice => { lock (notices) notices.Add(notice); });
        var hash = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(20));
        var job = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Add, new TorrentAddRequest
        {
            Input = $"magnet:?xt=urn:btih:{hash}&dn=big.bin", Destination = Path.Combine(_root, "down"),
        })).GetPayload<TorrentAddResult>().Job!;
        var worker = await UntilAsync(() => _torrentWorkers.FirstOrDefault(w => w.Torrents.Count == 1));
        worker.Advance();
        worker.Inject(new JsonObject { ["event"] = "error", ["id"] = job.Id.ToString("N"), ["kind"] = "file", ["reason"] = "diskFull", ["message"] = "x" });

        var paused = await UntilAsync(() => service.GetSnapshot().Jobs.FirstOrDefault(j => j.Id == job.Id && j.State == "paused"));
        Assert.Equal(("diskFull", "The disk is full"), (paused.Failure!.Kind, paused.Failure.Message));
        Assert.True(paused.PausedByUser); // a queue start leaves it for the user
        // The notice follows the change event, so it may come a moment after the row shows the pause.
        await UntilAsync(() =>
        {
            lock (notices) return notices.FirstOrDefault(notice => notice is { Kind: DownloadNoticeKind.Failed, Title: "Download stopped" } && notice.Message.EndsWith("The disk is full"));
        });

        Assert.True((await service.ResumeAsync(job.Id)).Ok);
        var again = await UntilAsync(() => _torrentWorkers.LastOrDefault(w => w.Torrents.Count == 1));
        for (var i = 0; i < 4; i++) again.Advance();
        var done = await UntilAsync(() => service.GetSnapshot().Jobs.FirstOrDefault(j => j.Id == job.Id && j.State == "completed"));
        Assert.Null(done.Failure);
    }

    [Fact]
    public async Task ASavedListCarriesTorrentsOutAndBackInSkippingThoseAlreadyListed()
    {
        var service = await TorrentServiceAsync(filesPerTorrent: 3);
        var dispatcher = Dispatcher(service);
        var destination = Path.Combine(_root, "down");

        // A .torrent file's job, not started, and a magnet whose files were chosen.
        var file = Path.Combine(_root, "film.torrent");
        var (bytes, fileHash) = SingleFileTorrent("film.mkv", 100_000);
        await File.WriteAllBytesAsync(file, bytes);
        var fromFile = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Add, new TorrentAddRequest
        {
            Input = file, Destination = destination, StartMode = "later",
        })).GetPayload<TorrentAddResult>().Job!;
        var magnetHash = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(20));
        var magnet = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Add, new TorrentAddRequest
        {
            Input = $"magnet:?xt=urn:btih:{magnetHash}&dn=Album", Destination = destination, Files = "askWhenReady",
        })).GetPayload<TorrentAddResult>().Job!;
        await UntilAsync(() => service.GetSnapshot().Jobs.FirstOrDefault(j => j.Id == magnet.Id && j.Torrent!.AwaitingFileChoice && j.State == "paused"));
        Assert.True((await AskAsync(dispatcher, Session(), TorrentMessageTypes.SetFiles, new TorrentSetFilesRequest { JobId = magnet.Id, FileIndexes = [0, 2] }))
            .GetPayload<TorrentActionResult>().Ok);
        await service.PauseAsync(magnet.Id);

        var listed = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.ListEntries, new TorrentListEntriesRequest()))
            .GetPayload<TorrentListEntriesResult>();
        Assert.True(listed.Ok);
        var fileEntry = listed.Entries[fromFile.Id];
        Assert.Equal((fileHash, Convert.ToBase64String(bytes), (List<int>?)null), (fileEntry.InfoHash, fileEntry.Data, fileEntry.FileIndexes));
        var magnetEntry = listed.Entries[magnet.Id];
        Assert.Null(magnetEntry.Data);
        Assert.StartsWith($"magnet:?xt=urn:btih:{magnetHash}", magnetEntry.Magnet);
        Assert.Equal([0, 2], magnetEntry.FileIndexes!);

        var text = JobListFile.Serialize(service.GetSnapshot().Jobs, listed.Entries);
        var entries = JobListFile.Deserialize(text);
        Assert.All(entries, entry => Assert.True(JobListFile.IsTorrent(entry)));

        // Imported while both are listed: both skipped.
        var skipped = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Import, new TorrentImportRequest { Entries = entries.ToList() }))
            .GetPayload<TorrentImportResult>();
        Assert.Equal((0, 2, 0, DownloadManagerService.TorrentAlreadyListedText), (skipped.Added, skipped.Skipped, skipped.Failed, skipped.SkipReason));

        // Removed, then imported: back, the file's with its own file list and the magnet asking for its choice again.
        await service.RemoveAsync(fromFile.Id, deleteFile: false);
        await service.RemoveAsync(magnet.Id, deleteFile: false);
        await UntilAsync(() => service.GetSnapshot().Jobs.Count == 0 ? service : null);
        var imported = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Import, new TorrentImportRequest { Entries = entries.ToList(), StartMode = "later" }))
            .GetPayload<TorrentImportResult>();
        Assert.Equal((2, 0, 0), (imported.Added, imported.Skipped, imported.Failed));
        var jobs = service.GetSnapshot().Jobs;
        var back = Assert.Single(jobs, j => j.Torrent!.InfoHash == fileHash);
        Assert.True(back.Torrent!.HasMetadata);
        Assert.Equal(destination, back.DestinationDirectory);
        Assert.Single(jobs, j => j.Torrent!.InfoHash == magnetHash);
    }

    [Fact]
    public async Task AQueuedTorrentThatCannotWriteItsFilesStaysFailed()
    {
        var service = await TorrentServiceAsync();
        var dispatcher = Dispatcher(service);
        var hash = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(20));
        var job = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Add, new TorrentAddRequest
        {
            Input = $"magnet:?xt=urn:btih:{hash}&dn=locked.bin", Destination = Path.Combine(_root, "down"), StartMode = "queue", QueueId = 0,
        })).GetPayload<TorrentAddResult>().Job!;
        Assert.True((await service.QueueActionAsync(new QueueActionRequest { QueueId = 0, Action = "start" })).Ok);
        var worker = await UntilAsync(() => _torrentWorkers.FirstOrDefault(w => w.Torrents.Count == 1));
        worker.Inject(new JsonObject { ["event"] = "error", ["id"] = job.Id.ToString("N"), ["kind"] = "file", ["reason"] = "permission", ["message"] = "x" });

        var failed = await UntilAsync(() => service.GetSnapshot().Jobs.FirstOrDefault(j => j.Id == job.Id && j.State == "failed"));
        Assert.Equal("permission", failed.Failure!.Kind);
        Assert.Null(failed.QueueRetryAt); // the running queue (no retry limit) does not try it again
        var workers = _torrentWorkers.Count;
        Assert.True((await service.QueueActionAsync(new QueueActionRequest { QueueId = 0, Action = "start" })).Ok);
        await Task.Delay(300);
        Assert.Equal(failed.UpdatedAt, service.GetJob(job.Id)!.UpdatedAt);
        Assert.Equal(workers, _torrentWorkers.Count);
    }

    [Fact]
    public async Task AnImportedMagnetGetsItsSavedChoiceWhenItsFileListComes()
    {
        var service = await TorrentServiceAsync(filesPerTorrent: 3);
        var dispatcher = Dispatcher(service);
        string Magnet() => $"magnet:?xt=urn:btih:{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(20))}";
        JobListEntry Entry(string magnet, List<int> chosen) => new()
        {
            Url = magnet, DestinationDirectory = Path.Combine(_root, "down"), StartMode = "startNow",
            Torrent = new TorrentListEntry { InfoHash = magnet[^40..], Magnet = magnet, FileIndexes = chosen },
        };
        var (fits, misfit) = (Magnet(), Magnet());

        var imported = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.Import, new TorrentImportRequest
        {
            Entries = [Entry(fits, [0, 2]), Entry(misfit, [1, 7])], StartMode = "later",
        })).GetPayload<TorrentImportResult>();
        Assert.Equal((2, 0, 0), (imported.Added, imported.Skipped, imported.Failed));

        // Before its file list comes, a new export still has the choice.
        var early = (await AskAsync(dispatcher, Session(), TorrentMessageTypes.ListEntries, new TorrentListEntriesRequest()))
            .GetPayload<TorrentListEntriesResult>();
        Assert.Equal([0, 2], Assert.Single(early.Entries.Values, entry => entry.InfoHash == fits[^40..]).FileIndexes!);

        foreach (var job in service.GetSnapshot().Jobs) Assert.True((await service.ResumeAsync(job.Id)).Ok);
        var chosen = await UntilAsync(() => service.GetSnapshot().Jobs.FirstOrDefault(j => j.Torrent!.InfoHash == fits[^40..] && j.Torrent.HasMetadata));
        Assert.False(chosen.Torrent!.AwaitingFileChoice);
        Assert.Equal(2, chosen.Torrent.WantedCount);
        await UntilAsync(() => service.GetSnapshot().Jobs.FirstOrDefault(j => j.Torrent!.InfoHash == misfit[^40..] && j.Torrent.AwaitingFileChoice && j.State == "paused"));
    }

    [Fact]
    public async Task ALongListGoesOutAndBackInMessagesUnderTheSizeLimit()
    {
        var service = await TorrentServiceAsync();
        var dispatcher = Dispatcher(service);
        var destination = Path.Combine(_root, "down");
        // Three .torrent files of about 2 MB each: together over a page, and their base64 over half a frame.
        var hashes = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var (bytes, hash) = PaddedTorrent($"big-{i}.bin", 2 * 1024 * 1024);
            hashes.Add(hash);
            var file = Path.Combine(_root, $"big-{i}.torrent");
            await File.WriteAllBytesAsync(file, bytes);
            Assert.True((await AskAsync(dispatcher, Session(), TorrentMessageTypes.Add, new TorrentAddRequest
            {
                Input = file, Destination = destination, StartMode = "later",
            })).GetPayload<TorrentAddResult>().Ok);
        }

        var all = new Dictionary<Guid, TorrentListEntry>();
        Guid? after = null;
        var pages = 0;
        do
        {
            var reply = await AskAsync(dispatcher, Session(), TorrentMessageTypes.ListEntries, new TorrentListEntriesRequest { After = after });
            Assert.Equal(TorrentMessageTypes.ListEntriesResult, reply.Type);
            var page = reply.GetPayload<TorrentListEntriesResult>();
            Assert.True(page.Entries.Values.Sum(TorrentListPaging.SizeOf) <= TorrentListPaging.PageChars);
            foreach (var (id, entry) in page.Entries) all[id] = entry;
            after = page.Next;
            pages++;
        }
        while (after is not null);

        Assert.True(pages > 1);
        Assert.Equal(3, all.Count);
        Assert.All(all.Values, entry => Assert.NotNull(entry.Data));

        var entries = JobListFile.Deserialize(JobListFile.Serialize(service.GetSnapshot().Jobs, all));
        foreach (var job in service.GetSnapshot().Jobs) await service.RemoveAsync(job.Id, deleteFile: false);
        await UntilAsync(() => service.GetSnapshot().Jobs.Count == 0 ? service : null);
        var chunks = TorrentListPaging.Chunk(entries);
        Assert.True(chunks.Count > 1);
        var total = new TorrentImportResult();
        foreach (var chunk in chunks)
        {
            var reply = await AskAsync(dispatcher, Session(), TorrentMessageTypes.Import, new TorrentImportRequest { Entries = chunk, StartMode = "later" });
            Assert.Equal(TorrentMessageTypes.ImportResult, reply.Type);
            TorrentListPaging.AddTo(total, reply.GetPayload<TorrentImportResult>());
        }

        Assert.Equal((3, 0, 0), (total.Added, total.Skipped, total.Failed));
        Assert.Equal(hashes.Order(), service.GetSnapshot().Jobs.Select(j => j.Torrent!.InfoHash).Order());
        Assert.All(service.GetSnapshot().Jobs, j => Assert.True(j.Torrent!.HasMetadata));
    }

    /// <summary>A single-file .torrent made large by a comment (outside the info dictionary, so the hash is the plain one's).</summary>
    private static (byte[] Bytes, string Hash) PaddedTorrent(string name, int padding)
    {
        var (bytes, hash) = SingleFileTorrent(name, 16384);
        var comment = System.Text.Encoding.ASCII.GetBytes($"7:comment{padding}:" + new string('x', padding));
        return ([(byte)'d', .. comment, .. bytes.AsSpan(1)], hash);
    }

    [Fact]
    public async Task ImportedTorrentsAreSkippedWithAReasonWhileTorrentsAreOff()
    {
        var service = await TorrentServiceAsync(enabled: false);
        var entries = new List<JobListEntry>
        {
            new() { Url = "magnet:?xt=urn:btih:" + new string('b', 40) },
            new() { Url = "magnet:?xt=urn:btih:" + new string('c', 40), Torrent = new TorrentListEntry { InfoHash = new string('c', 40) } },
        };
        var result = (await AskAsync(Dispatcher(service), Session(), TorrentMessageTypes.Import, new TorrentImportRequest { Entries = entries }))
            .GetPayload<TorrentImportResult>();
        Assert.Equal((0, 2, 0), (result.Added, result.Skipped, result.Failed));
        Assert.Contains("turned off", result.SkipReason);
        Assert.Empty(service.GetSnapshot().Jobs);
    }
}
